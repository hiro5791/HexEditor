using HexEditor.Core.Scripting.Hooks;
using System.Globalization;
using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Devices;
using HexEditor.Core.Notifications;
using HexEditor.Core.Processes;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Sdk;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// スクリプトの実行 (08 の AUTO-01、AUTO-03、AUTO-16、AUTO-34) のウィンドウ側。コマンド、信頼・許可・パラメータのダイアログ、
/// 書き込み中の InfoBar、スクリプトの環境 (<see cref="AppScriptEnvironment"/>) が使うウィンドウの操作。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterScriptCommands()
    {
        Commands.Register("scripting.run", RunScriptAsync, ScriptCommandState);
        Commands.Register("scripting.runFile", RunScriptFileAsync, ScriptCommandState);
        Commands.Register("scripting.stopAll", ScriptingService.StopAll,
            () => ScriptingService.RunningCount > 0 ? CommandState.Available : CommandState.Unavailable(Loc.Get("Script_NoneRunning")));
        Commands.Register("scripting.addCSharpComponent", async () => await InstallCSharpComponentAsync(),
            () => ScriptingService.Components.IsInstalled ? CommandState.Unavailable(Loc.Get("Script_ComponentInstalled")) : CommandState.Available);
        HookScriptEvents();
    }

    /// <summary>組織の設定で無効なら使えない (AUTO-34 の仕様 9。理由をツールチップと InfoBar に出す)。</summary>
    private static CommandState ScriptCommandState() =>
        ScriptingService.PolicyDisabled ? CommandState.Unavailable(Loc.Get("Script_DisabledByPolicy")) : CommandState.Available;

    /// <summary>F5 (AUTO-01 の仕様 2 の 3): 直前に実行したスクリプトを再実行する。まだなければファイル選択ダイアログ。</summary>
    private async Task RunScriptAsync()
    {
        if (ScriptingService.Host.LastRequest is { } last)
        {
            ScriptingService.Start(last);
            return;
        }

        await RunScriptFileAsync();
    }

    private async Task RunScriptFileAsync()
    {
        string? path = (await ScriptPickAsync("open", new PickOptions { Filters = ["*.js", "*.mjs", "*.csx"] }))?.FirstOrDefault();
        if (path is not null)
        {
            ScriptingService.Start(new ScriptRunRequest { Path = path });
        }
    }

    // ---- イベント (AUTO-06 の仕様 4) ----

    private void HookScriptEvents()
    {
        ScriptEventSource events = ScriptingService.Environment.EventSource;
        Vm.Documents.CollectionChanged += (_, e) =>
        {
            foreach (DocumentViewModel d in e.NewItems?.OfType<DocumentViewModel>() ?? [])
            {
                events.RaiseDocumentOpened(ScriptingService.Environment.HostFor(d));
                d.EditorChanged += OnScriptSelectionChanged;
                _ = RunOpenHooksAsync(d);
            }

            foreach (DocumentViewModel d in e.OldItems?.OfType<DocumentViewModel>() ?? [])
            {
                d.EditorChanged -= OnScriptSelectionChanged;
                events.RaiseDocumentClosed(ScriptingService.Environment.HostFor(d));
                ScriptingService.Environment.Forget(d);
            }
        };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                events.RaiseActiveDocumentChanged();
            }
        };
    }

    /// <summary>ファイルを開いたときの自動実行 (AUTO-25)。先頭 64 バイトを条件の判定に使う。</summary>
    private async Task RunOpenHooksAsync(DocumentViewModel d)
    {
        try
        {
            var head = new byte[(int)Math.Min(64, d.Document.Length)];
            d.Document.Current.Read(0, head);
            string name = d.FilePath is { } p ? System.IO.Path.GetFileName(p) : d.DisplayName;
            await ScriptWorkspace.Hooks.RunOpenAsync(ScriptingService.Environment.HostFor(d), new HookTarget(d.FilePath, name, d.ScriptKind, head));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Debug($"open hook failed: {ex.Message}");
        }
    }

    private void OnScriptSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is DocumentViewModel d)
        {
            ScriptingService.Environment.EventSource.RaiseSelectionChanged(ScriptingService.Environment.HostFor(d));
        }
    }

    /// <summary>保存 (保存の直前・直後のイベントを出す。AUTO-06 の <c>beforeSave</c> / <c>afterSave</c>)。</summary>
    private async Task<bool> SaveAsync(DocumentViewModel doc, bool saveAs)
    {
        AppDocumentHost host = ScriptingService.Environment.HostFor(doc);
        if (await ScriptingService.Environment.EventSource.RaiseBeforeSaveAsync(host, ScriptEventSource.BeforeSaveTimeout) is string reason)
        {
            ShowNotice(Loc.Format("Script_SaveCanceled", reason), InfoBarSeverity.Warning, doc);
            return false;
        }

        // 保存前の自動実行 (AUTO-25)。スクリプトが中止を求めたら保存しない (通知はフック側が出す)。
        if (!(await ScriptWorkspace.Hooks.RunBeforeSaveAsync(host)).Proceed)
        {
            return false;
        }

        bool saved = await SaveCoreAsync(doc, saveAs);
        if (saved)
        {
            ScriptingService.Environment.EventSource.RaiseAfterSave(host);
            Services.Scripting.MacroHooks.Saved(saveAs ? doc.FilePath : null);
            if (!Hosting.Program.CommandLine.SafeMode)
            {
                _ = ScriptWorkspace.Hooks.RunAfterSaveAsync(host);
            }
        }

        return saved;
    }

    // ---- スクリプトの環境が使う操作 ----

    internal async Task SaveForScriptAsync(DocumentViewModel doc, string? path)
    {
        if (path is not null)
        {
            _binarySaveAsPath = path;
        }

        if (!await SaveAsync(doc, saveAs: path is not null))
        {
            _binarySaveAsPath = null;
            throw new IoError(Msg.T($"ドキュメント「{doc.DisplayName}」を保存できませんでした。", $"The document \"{doc.DisplayName}\" was not saved."));
        }
    }

    internal async Task<bool> CloseForScriptAsync(DocumentViewModel doc, bool discard)
    {
        if (discard)
        {
            Vm.Close(doc);
            UpdateTitle();
            return true;
        }

        return await CloseAsync([doc]);
    }

    internal Task<IScriptDocumentHost> OpenRangeForScriptAsync(DocumentViewModel doc, long start, long length)
    {
        DocumentViewModel view = Vm.OpenLinkedView(doc, start, length);
        view.Document.SetReadOnly(Core.Engine.ReadOnlyReason.User);
        return Task.FromResult<IScriptDocumentHost>(ScriptingService.Environment.HostFor(view));
    }

    internal Task<DocumentViewModel> OpenForScriptAsync(string path, bool readOnly, bool background)
    {
        DocumentViewModel? previous = Vm.Selected;
        DocumentViewModel opened = TryOpen(path, readOnly: readOnly)
            ?? throw new IoError(Msg.T($"開けません: {path}", $"Cannot open: {path}"));
        if (background && previous is not null)
        {
            Vm.Selected = previous;
        }

        return Task.FromResult(opened);
    }

    internal async Task<DocumentViewModel> OpenDiskForScriptAsync(string id, bool readOnly)
    {
        string path = id.StartsWith(@"\\.\", StringComparison.Ordinal) ? id : @"\\.\" + id;
        var item = new DeviceListItem(id, string.Empty, needsAdmin: false, locked: false, () => new DeviceOpenInfo { Path = path, DisplayName = id }, false);
        return await OpenDeviceItemAsync(item, readOnly) ?? throw new IoError(Msg.T($"ディスクを開けません: {id}", $"Cannot open the disk: {id}"));
    }

    internal async Task<DocumentViewModel> OpenProcessForScriptAsync(int pid, bool readOnly)
    {
        var entry = new ProcessEntry { Pid = pid, Name = "pid " + pid.ToString(CultureInfo.InvariantCulture) };
        return await OpenProcessEntryAsync(entry, readOnly) ?? throw new IoError(Msg.T($"プロセスを開けません: {pid}", $"Cannot open the process: {pid}"));
    }

    internal DocumentViewModel NewDocumentForScript(long length, byte[] fill) => Vm.NewDocument(length, fill);

    internal CopyOptions CopyOptionsForScript() => Vm.Selected is { } doc ? OptionsOf(LoadCopyAsState(), doc) : new CopyOptions();

    internal Core.Inspector.InspectorOptions InspectorOptionsForScript() => _inspectorVm.Options;

    internal void SetInspectorRowForScript(string typeId, bool visible) => _inspectorVm.SetLayout(_inspectorVm.Layout.WithVisible(typeId, visible));

    internal void SetClipboardBytesForScript(byte[] data, string hexText)
    {
        var package = new DataPackage();
        package.SetText(hexText);
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(data);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }

        package.SetData(ClipboardPlan.BinaryFormat, stream);
        SystemClipboard.SetContent(package);
    }

    internal byte[]? GetClipboardBytesForScript()
    {
        DataPackageView view = SystemClipboard.GetContent();
        if (!view.Contains(ClipboardPlan.BinaryFormat))
        {
            return null;
        }

        var stream = (Windows.Storage.Streams.IRandomAccessStream)view.GetDataAsync(ClipboardPlan.BinaryFormat).AsTask().GetAwaiter().GetResult();
        using Stream s = stream.AsStreamForRead();
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    // ---- 実行の表示 (AUTO-01 の仕様 6・10、「エラー」) ----

    /// <summary>書き込み中の InfoBar「スクリプト「名前」を実行中です」と停止ボタン。</summary>
    internal Notification ShowScriptWritingNotice(DocumentViewModel doc, ScriptRun run) => run.Origin == ScriptOrigin.External
        // 外部からの自動操作 (AUTO-45 の仕様 7): 「外部から操作されています: プログラム名」。「切断」で接続を切る。
        ? ShowNotice(Loc.Format("Rpc_Writing", run.Name), InfoBarSeverity.Warning, doc,
            actions: [new NotificationAction(Loc.Get("Rpc_Disconnect"), run.Stop)])
        : ShowNotice(Loc.Format("Script_Writing", run.Name), InfoBarSeverity.Warning, doc,
            actions: [new NotificationAction(Loc.Get("Script_Stop"), run.Stop)]);

    internal void OnScriptStarted(ScriptRun run)
    {
        AutoShowOutput(run);
        _ = run.Completion.ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
        {
            ScriptRunResult r = t.Result;
            if (r.Status == ScriptRunStatus.Failed)
            {
                ShowNotice(Loc.Format("Script_Failed", run.Name), InfoBarSeverity.Error, null,
                    actions: [new NotificationAction(Loc.Get("Script_Details"), () => _ = ShowScriptErrorAsync(run, r))]);
            }
            else if (r.Status == ScriptRunStatus.NotRun && r.Failure == ScriptFailure.Permission)
            {
                ShowNotice(Loc.Format("Script_NotRun", run.Name, r.Message ?? string.Empty), InfoBarSeverity.Warning);
            }
        }), TaskScheduler.Default);
    }

    /// <summary>エラーの詳細 (出力パネル (AUTO-24) ができるまでの代わり。メッセージ・位置・スタックトレース)。</summary>
    private async Task ShowScriptErrorAsync(ScriptRun run, ScriptRunResult r)
    {
        var text = new StringBuilder();
        text.AppendLine(r.Message);
        foreach (ScriptDiagnostic d in r.Diagnostics)
        {
            text.AppendLine(d.Path is not null && d.Line > 0 ? $"{Path.GetFileName(d.Path)}({d.Line},{d.Column}): {d.Message}" : d.Message);
        }

        if (!string.IsNullOrEmpty(r.StackTrace))
        {
            text.AppendLine().AppendLine(r.StackTrace);
        }

        var box = new TextBox
        {
            Text = text.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = Controls.DialogParts.Mono,
            FlowDirection = FlowDirection.LeftToRight,
            MaxHeight = 360,
        };
        AutomationProperties.SetName(box, Loc.Get("Script_Details"));
        ContentDialog dialog = NewDialog(Loc.Format("Script_DialogTitle", run.Name), box);
        dialog.CloseButtonText = Loc.Get("Common_Close");
        AutomationProperties.SetAutomationId(dialog, "ScriptErrorDialog");
        await dialog.ShowQueuedAsync();
    }

    /// <summary>ステータスバーに実行中のスクリプトの数を出す (AUTO-01 の画面)。</summary>
    internal void UpdateScriptStatus(int count)
    {
        if (count > 0)
        {
            ShowStatusMessage(Loc.Format("Script_RunningCount", count), TimeSpan.FromSeconds(5));
        }
    }

    // ---- UI (AUTO-16) ----

    internal async Task ScriptMessageAsync(string scriptName, string text, string kind, bool modal)
    {
        InfoBarSeverity severity = kind switch { "error" => InfoBarSeverity.Error, "warning" => InfoBarSeverity.Warning, _ => InfoBarSeverity.Informational };
        if (!modal)
        {
            ShowNotice(Loc.Format("Script_Message", scriptName, text), severity);
            return;
        }

        ContentDialog dialog = NewDialog(Loc.Format("Script_DialogTitle", scriptName), text);
        dialog.CloseButtonText = Loc.Get("Common_Ok");
        AutomationProperties.SetAutomationId(dialog, "ScriptMessageDialog");
        await dialog.ShowQueuedAsync();
    }

    /// <summary>ボタンを選ばせる。ボタンは内容の中に縦に並べ (3 つを超えても選べるように)、Esc / キャンセルは -1。</summary>
    internal async Task<int> ScriptConfirmAsync(string scriptName, string text, IReadOnlyList<string> buttons)
    {
        int chosen = -1;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        ContentDialog dialog = NewDialog(Loc.Format("Script_DialogTitle", scriptName), panel);
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, "ScriptConfirm_" + i.ToString(CultureInfo.InvariantCulture));
            button.Click += (_, _) =>
            {
                chosen = index;
                dialog.Hide();
            };
            panel.Children.Add(button);
        }

        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        AutomationProperties.SetAutomationId(dialog, "ScriptConfirmDialog");
        await dialog.ShowQueuedAsync();
        return chosen;
    }

    /// <summary>
    /// 入力ダイアログ (AUTO-16 の <c>ui.prompt</c> / <c>ui.form</c>、AUTO-26 のパラメータ)。タイトルにスクリプト名を付け、各欄の
    /// スクリーンリーダー用の名前はラベルにする。Tab で欄を移り、Enter で確定する。誤りがある間は確定しない。
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>?> ScriptFormAsync(string scriptName, string? title, IReadOnlyList<ScriptFormField> fields,
        Func<IReadOnlyDictionary<string, string>, (string Field, string Message)?> validate)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var readers = new List<(string Name, Control Control, Func<string> Read)>();
        foreach (ScriptFormField f in fields)
        {
            string id = "ScriptField_" + f.Name;
            switch (f.Type)
            {
                case "checkbox":
                {
                    CheckBox box = Controls.DialogParts.Check(id, f.Label, string.Equals(f.Default as string, "true", StringComparison.OrdinalIgnoreCase));
                    panel.Children.Add(box);
                    readers.Add((f.Name, box, () => box.IsChecked == true ? "true" : "false"));
                    break;
                }

                case "choice":
                {
                    IReadOnlyList<string> choices = f.Choices ?? [];
                    int selected = Math.Max(0, choices.ToList().IndexOf(f.Default as string ?? string.Empty));
                    ComboBox combo = Controls.DialogParts.Combo(id, f.Label, choices, selected);
                    panel.Children.Add(combo);
                    readers.Add((f.Name, combo, () => combo.SelectedItem as string ?? string.Empty));
                    break;
                }

                default:
                {
                    TextBox box = Controls.DialogParts.Field(id, f.Label, f.Default as string ?? string.Empty, monospace: f.Type != "text");
                    if (f.Type != "text")
                    {
                        box.FlowDirection = FlowDirection.LeftToRight;
                    }

                    panel.Children.Add(box);
                    readers.Add((f.Name, box, () => box.Text));
                    break;
                }
            }
        }

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.SetValue(AutomationProperties.LiveSettingProperty, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(error, "ScriptField_Error");
        panel.Children.Add(error);
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "ScriptFormDialog", Loc.Format("Script_DialogTitle", title ?? scriptName),
            new ScrollViewer { Content = panel, MaxHeight = 480 }, Loc.Get("Common_Ok"));
        Dictionary<string, string> Values() => readers.ToDictionary(r => r.Name, r => r.Read(), StringComparer.Ordinal);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            foreach (var reader in readers)
            {
                Controls.DialogParts.MarkInvalid(reader.Control, false);
            }

            if (validate(Values()) is { } problem)
            {
                args.Cancel = true;
                error.Text = problem.Message;
                error.Visibility = Visibility.Visible;
                if (readers.FirstOrDefault(r => r.Name == problem.Field).Control is { } bad)
                {
                    Controls.DialogParts.MarkInvalid(bad, true);
                    bad.Focus(FocusState.Programmatic);
                }
            }
        };
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? Values() : null;
    }

    internal async Task<IReadOnlyList<string>?> ScriptPickAsync(string kind, PickOptions options)
    {
        string identifier = "HexEditor.Script." + kind;
        if (kind is "open" or "openMany" && TestHooks.OpenPickerResult(identifier) is { } testPaths)
        {
            return testPaths.Count == 0 ? null : testPaths;
        }

        if (kind == "save" && TestHooks.TrySavePicker(options.SuggestedName ?? "output.bin", out string? testPath))
        {
            return testPath is null ? null : [testPath];
        }

        switch (kind)
        {
            case "open":
            case "openMany":
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = identifier };
                foreach (string filter in options.Filters ?? ["*"])
                {
                    picker.FileTypeFilter.Add(filter.TrimStart('*'));
                }

                if (kind == "open")
                {
                    return (await picker.PickSingleFileAsync())?.Path is { } p ? [p] : null;
                }

                var many = await picker.PickMultipleFilesAsync();
                return many is { Count: > 0 } ? [.. many.Select(f => f.Path)] : null;
            }

            case "save":
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId)
                {
                    SettingsIdentifier = identifier,
                    SuggestedFileName = options.SuggestedName ?? "output.bin",
                };
                picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(picker.SuggestedFileName) is { Length: > 0 } ext ? ext : ".bin"]);
                return (await picker.PickSaveFileAsync())?.Path is { } p ? [p] : null;
            }

            default:
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId) { SettingsIdentifier = identifier };
                return (await picker.PickSingleFolderAsync())?.Path is { } p ? [p] : null;
            }
        }
    }

    /// <summary>
    /// 結果の一覧 (AUTO-16 の <c>ui.showResults</c>)。共通の結果一覧パネルへの表示は出力パネルと同じ時期に行う。それまでは出力に行を書く。
    /// </summary>
    internal void ShowScriptResults(string scriptName, string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns,
        DocumentViewModel? document)
    {
        string channel = ScriptRun.ChannelPrefix + scriptName;
        ScriptingService.Output.Write(channel, ScriptOutputKind.Info, title);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            ScriptingService.Output.Write(channel, ScriptOutputKind.Info,
                string.Join("\t", columns.Select(c => Convert.ToString(row.TryGetValue(c, out object? v) ? v : null, CultureInfo.CurrentCulture))));
        }
    }

    // ---- 信頼と許可 (AUTO-34) ----

    internal async Task<TrustAnswer> ConfirmScriptTrustAsync(TrustPrompt prompt)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        void Line(string text, string id, bool ltr = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (ltr)
            {
                block.FlowDirection = FlowDirection.LeftToRight;
            }

            AutomationProperties.SetAutomationId(block, id);
            panel.Children.Add(block);
        }

        if (prompt.Elevated)
        {
            Line(Loc.Get("ScriptTrust_Elevated"), "ScriptTrust_Elevated");
        }

        Line(Loc.Format(prompt.External ? "RpcTrust_Program" : "ScriptTrust_Name", prompt.Name), "ScriptTrust_Name");
        if (prompt.Path is not null)
        {
            Line(prompt.Path, "ScriptTrust_Path", ltr: true);
        }

        if (prompt.Author is not null)
        {
            Line(Loc.Format(prompt.External ? "RpcTrust_Signer" : "ScriptTrust_Author", prompt.Author), "ScriptTrust_Author");
        }

        if (!prompt.ElevationOnly)
        {
            if (prompt.External)
            {
                // 外部のプログラムからの接続 (AUTO-45 の仕様 4)。署名がなければその旨も出す。
                Line(Loc.Get("RpcTrust_Description"), "ScriptTrust_Description");
                if (prompt.Author is null)
                {
                    Line(Loc.Get("RpcTrust_Unsigned"), "RpcTrust_Unsigned");
                }
            }
            else if (prompt.FullTrust)
            {
                Line(Loc.Get("ScriptTrust_FullTrust"), "ScriptTrust_Description");
            }
            else
            {
                Line(Loc.Get(prompt.Permissions.Count > 0 ? "ScriptTrust_JavaScript" : "ScriptTrust_JavaScriptNoPermissions"), "ScriptTrust_Description");
                foreach (string p in prompt.Permissions)
                {
                    Line("• " + p + " — " + (Loc.TryGet(ScriptPermissions.ResourceKey(p)) ?? p), "ScriptTrust_Permission_" + p);
                }
            }
        }

        if (prompt.FromInternet)
        {
            var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Message = Loc.Get("ScriptTrust_FromInternet") };
            AutomationProperties.SetAutomationId(warning, "ScriptTrust_FromInternet");
            panel.Children.Add(warning);
        }

        ContentDialog dialog = NewDialog(Loc.Format(prompt.External ? "RpcTrust_Title" : "ScriptTrust_Title", prompt.Name), new ScrollViewer { Content = panel, MaxHeight = 480 });
        AutomationProperties.SetAutomationId(dialog, prompt.External ? "RpcTrustDialog" : "ScriptTrustDialog");
        if (prompt.ElevationOnly)
        {
            dialog.PrimaryButtonText = Loc.Get("ScriptTrust_Run");
            dialog.CloseButtonText = Loc.Get("ScriptTrust_DontRun");
            dialog.DefaultButton = ContentDialogButton.Close;
            return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? TrustAnswer.RunOnce : TrustAnswer.Deny;
        }

        dialog.PrimaryButtonText = Loc.Get(prompt.External ? "RpcTrust_AllowOnce" : "ScriptTrust_RunOnce");
        dialog.SecondaryButtonText = Loc.Get("ScriptTrust_AlwaysAllow");
        dialog.CloseButtonText = Loc.Get(prompt.External ? "RpcTrust_Deny" : "ScriptTrust_DontRun");
        dialog.DefaultButton = prompt.Elevated ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return await dialog.ShowQueuedAsync() switch
        {
            ContentDialogResult.Primary => TrustAnswer.RunOnce,
            ContentDialogResult.Secondary => TrustAnswer.AlwaysAllow,
            _ => TrustAnswer.Deny,
        };
    }

    internal async Task<bool> ConfirmScriptPermissionAsync(string scriptName, string permission, string detail)
    {
        string description = Loc.TryGet(ScriptPermissions.ResourceKey(permission)) ?? permission;
        ContentDialog dialog = NewDialog(Loc.Format("Script_DialogTitle", scriptName), Loc.Format("ScriptPermission_Body", permission, description, detail));
        dialog.PrimaryButtonText = Loc.Get("ScriptPermission_Allow");
        dialog.CloseButtonText = Loc.Get("ScriptPermission_Deny");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "ScriptPermissionDialog");
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>実行時間の上限 (AUTO-02 の仕様 5): InfoBar「スクリプト「名前」が 60 秒を超えました」と「続ける」「停止」。</summary>
    internal Task<bool> ConfirmScriptTimeoutAsync(ScriptRun run, TimeSpan limit)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string seconds = limit.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        Notification? notice = null;
        notice = ShowNotice(Loc.Format("Script_TimeLimit", run.Name, seconds), InfoBarSeverity.Warning, null, actions:
        [
            new NotificationAction(Loc.Format("Script_Continue", seconds), () => answer.TrySetResult(true)),
            new NotificationAction(Loc.Get("Script_Stop"), () => answer.TrySetResult(false)),
        ]);
        _ = run.Completion.ContinueWith(_ => answer.TrySetResult(false), TaskScheduler.Default);
        return answer.Task.ContinueWith(t =>
        {
            DispatcherQueue.TryEnqueue(() => Vm.Notifications.Dismiss(notice));
            return t.Result;
        }, TaskScheduler.Default);
    }

    // ---- C# スクリプト コンポーネント (AUTO-03) ----

    /// <summary>コンポーネントがないときの案内 (仕様 8)。「追加する」なら追加まで行う。追加できたら true。</summary>
    internal async Task<bool> OfferCSharpComponentAsync()
    {
        ContentDialog dialog = NewDialog(Loc.Get("CSharpComponent_Title"), Loc.Get("CSharpComponent_Offer"));
        dialog.PrimaryButtonText = Loc.Get("CSharpComponent_Add");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "CSharpComponentOfferDialog");
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && await InstallCSharpComponentAsync();
    }

    /// <summary>
    /// 追加する (仕様 3): サイズ・保存先・通信先を示して確認し、処理センターに進捗を出してダウンロード・検証する。オフラインや失敗の
    /// 場合は理由と手動で入れる手順を出す。
    /// </summary>
    private async Task<bool> InstallCSharpComponentAsync()
    {
        if (await ShowOfflineComponentInstructionsAsync())
        {
            return false;
        }

        CSharpComponentManager components = ScriptingService.Components;
        (long size, string destination, string host) = components.DownloadInfo;
        ContentDialog confirm = NewDialog(Loc.Get("CSharpComponent_Title"),
            Loc.Format("CSharpComponent_Confirm", Core.View.StatusFormat.ShortSize(size, CultureInfo.CurrentCulture) ?? Core.View.StatusFormat.Number(size, CultureInfo.CurrentCulture), destination, host));
        confirm.PrimaryButtonText = Loc.Get("CSharpComponent_Download");
        confirm.CloseButtonText = Loc.Get("Common_Cancel");
        confirm.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(confirm, "CSharpComponentConfirmDialog");
        if (await confirm.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Get("CSharpComponent_Operation"), Core.Operations.OperationKind.ReadOnly, null, 1000, async op =>
            {
                var progress = new Progress<double>(f => op.Report((long)(Math.Clamp(f, 0, 1) * 1000)));
                await components.InstallAsync(progress, op.CancellationToken);
            });
            ShowNotice(Loc.Get("CSharpComponent_Installed"), InfoBarSeverity.Success);
            return true;
        }
        catch (CSharpComponentException ex)
        {
            ShowNotice(Loc.Format(ex.ManualInstructions ? "CSharpComponent_FailedManual" : "CSharpComponent_Failed", ex.Message), InfoBarSeverity.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
