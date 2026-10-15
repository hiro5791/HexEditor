using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Commands;
using HexEditor.Core.Notifications;
using HexEditor.Core.Panels;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using HexEditor.Core.Scripting.Hooks;
using HexEditor.Core.Scripting.Library;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// スクリプトの作成と管理の画面 (08 の AUTO-19〜AUTO-25): スクリプト一覧・出力・コンソールのパネル、スクリプトエディタのタブ、
/// 実行・デバッグのコマンド、フックの通知。パネルの状態 (ViewModel) はウィンドウごとに 1 つ。
/// </summary>
public sealed partial class MainWindow : IScriptListHost, IScriptEditorHost
{
    public const string ScriptsPanelId = "scripts";
    public const string OutputPanelId = "output";
    public const string ConsolePanelId = "console";

    private ScriptListViewModel? _scriptListVm;
    private OutputPanelViewModel? _outputVm;
    private ConsolePanelViewModel? _consoleVm;
    private readonly Dictionary<string, ScriptEditorPage> _scriptEditors = [];
    private static bool s_scriptStartupDone;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度)。</summary>
    public static void RegisterScriptPanels()
    {
        if (PanelRegistry.Find(ScriptsPanelId) is not null)
        {
            return;
        }

        PanelRegistry.Register(new PanelRegistration(ScriptsPanelId, "Panel_Scripts_Title", PanelDock.Left,
            ctx => ((MainWindow)ctx.Window).CreateScriptListPanel())
        { RequiresDocument = false });
        PanelRegistry.Register(new PanelRegistration(OutputPanelId, "Panel_Output_Title", PanelDock.Bottom,
            ctx => ((MainWindow)ctx.Window).CreateOutputPanel())
        { RequiresDocument = false });
        PanelRegistry.Register(new PanelRegistration(ConsolePanelId, "Panel_Console_Title", PanelDock.Bottom,
            ctx => ((MainWindow)ctx.Window).CreateConsolePanel())
        { RequiresDocument = false });
    }

    internal ScriptListViewModel ScriptListVm => _scriptListVm ??= new ScriptListViewModel(DispatcherQueue);

    internal OutputPanelViewModel OutputVm => _outputVm ??= CreateOutputViewModel();

    internal ConsolePanelViewModel ConsoleVm => _consoleVm ??= new ConsolePanelViewModel(DispatcherQueue);

    private OutputPanelViewModel CreateOutputViewModel()
    {
        var vm = new OutputPanelViewModel(ScriptingService.Output, DispatcherQueue);
        vm.Capacity = App.Settings.GetInt("scripts.output.maxLines", ScriptOutputLog.DefaultCapacity);
        return vm;
    }

    private FrameworkElement CreateScriptListPanel()
    {
        ScriptWorkspace.Start(DispatcherQueue);
        return new ScriptListPanel(ScriptListVm, this);
    }

    private FrameworkElement CreateOutputPanel() => new OutputPanel(OutputVm, this);

    private FrameworkElement CreateConsolePanel() => new ConsolePanel(ConsoleVm, this);

    // ---- コマンド ----

    /// <summary>スクリプトの作成と管理のコマンド (UI-16)。<see cref="RegisterCommandHandlers"/> から呼ぶ。</summary>
    private void RegisterScriptUiCommands()
    {
        Commands.Register("scripting.showList", () => ShowPanel(ScriptsPanelId));
        Commands.Register("scripting.showConsole", () => ShowPanel(ConsolePanelId));
        Commands.Register("scripting.new", () => NewScript(ScriptLanguage.JavaScript));
        Commands.Register("scripting.newCSharp", () => NewScript(ScriptLanguage.CSharp));
        Commands.Register("scripting.openSample", OpenSampleFromArgumentAsync);
        Commands.Register("scripting.openFolder", () =>
        {
            Directory.CreateDirectory(ScriptingService.Folders.Scripts);
            ShowInFolder(ScriptingService.Folders.Scripts);
        });
        Commands.Register("scripting.clearOutput", () => OutputVm.Clear());
        Commands.Register("scripting.debug", () => ActiveScriptEditor?.DebugRun(), EditorCommandState);
        Commands.Register("scripting.pause", () => ActiveScriptEditor?.PauseRun(), EditorCommandState);
        Commands.Register("scripting.editor.run", () => ActiveScriptEditor?.RunOrContinue(), EditorCommandState);
        Commands.Register("scripting.editor.stop", () => ActiveScriptEditor?.StopRun(), EditorCommandState);
        Commands.Register("scripting.editor.toggleBreakpoint", () => ActiveScriptEditor?.ToggleBreakpoint(), EditorCommandState);
        Commands.Register("scripting.editor.stepOver", () => ActiveScriptEditor?.StepOver(), EditorCommandState);
        Commands.Register("scripting.editor.stepIn", () => ActiveScriptEditor?.StepIn(), EditorCommandState);
        Commands.Register("scripting.editor.stepOut", () => ActiveScriptEditor?.StepOut(), EditorCommandState);
        RegisterMacroCommands();
        BuildSamplesMenu();

        // ユーザーのスクリプトのコマンド (AUTO-19 の仕様 4): 登録されたものにこのウィンドウの処理をつなぐ。
        ScriptWorkspace.AttachWindow(Commands);
        EventHandler attach = (_, _) =>
        {
            ScriptWorkspace.AttachWindow(Commands);
            RebuildScriptMenuItems();
        };
        ScriptWorkspace.CommandsChanged += attach;
        App.Settings.Changed += OnScriptSettingsChanged;

        // 閉じる操作が取り消されたときも Closed は来る。実際に閉じるときだけ外す。
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                ScriptWorkspace.CommandsChanged -= attach;
                App.Settings.Changed -= OnScriptSettingsChanged;
            }
        };
    }

    private void OnScriptSettingsChanged(IReadOnlyCollection<string> keys)
    {
        if (keys.Contains("scripts.folders"))
        {
            DispatcherQueue.TryEnqueue(ScriptWorkspace.RefreshFoldersIfChanged);
        }

        if (keys.Contains("scripts.output.maxLines") && _outputVm is { } vm)
        {
            DispatcherQueue.TryEnqueue(() => vm.Capacity = App.Settings.GetInt("scripts.output.maxLines", ScriptOutputLog.DefaultCapacity));
        }
    }

    private CommandState EditorCommandState() =>
        ActiveScriptEditor is not null ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoScriptEditor"));

    /// <summary>最初のウィンドウが表示されたあとに 1 度だけ: スクリプト一覧の監視と起動時のフック (AUTO-25 の仕様 1: 起動を遅らせない)。</summary>
    private void StartScriptWorkspace()
    {
        if (s_scriptStartupDone)
        {
            return;
        }

        s_scriptStartupDone = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ScriptWorkspace.Start(DispatcherQueue);
            if (!Hosting.Program.CommandLine.SafeMode && ScriptWorkspace.HookStore.Load().Entries(HookKind.Startup).Any(e => e.Enabled))
            {
                _ = Task.Run(() => ScriptWorkspace.Hooks.RunStartupAsync());
            }
        });
    }

    /// <summary>メニュー「ツール &gt; スクリプト &gt; サンプル」(AUTO-46): 同梱のサンプルの一覧。選ぶと利用者のフォルダにコピーしてエディタで開く。</summary>
    private void BuildSamplesMenu()
    {
        ScriptSamplesMenu.Items.Clear();
        if (!Directory.Exists(ScriptWorkspace.SamplesFolder))
        {
            ScriptSamplesMenu.IsEnabled = false;
            return;
        }

        string language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        foreach (string file in Directory.EnumerateFiles(ScriptWorkspace.SamplesFolder).Where(f => ScriptHost.LanguageOf(f) is not null).Order(StringComparer.OrdinalIgnoreCase))
        {
            ScriptMetadata meta;
            try
            {
                meta = ScriptMetadata.Parse(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bool csharp = ScriptHost.LanguageOf(file) == ScriptLanguage.CSharp;
            var item = new MenuFlyoutItem { Text = $"{meta.DisplayName(language, Path.GetFileNameWithoutExtension(file))} ({(csharp ? "C#" : "JavaScript")})" };
            AutomationProperties.SetAutomationId(item, "Sample_" + Path.GetFileName(file));
            ToolTipService.SetToolTip(item, meta.DisplayDescription(language) ?? string.Empty);
            string fileName = Path.GetFileName(file);
            item.Click += (_, _) => OpenScriptEditor(CopySample(fileName));
            ScriptSamplesMenu.Items.Add(item);
        }
    }

    // ---- スクリプトエディタのタブ (AUTO-20 の仕様 1) ----

    /// <summary>表示中のスクリプトエディタ (なければ null)。</summary>
    internal ScriptEditorPage? ActiveScriptEditor =>
        ActiveToolPage is { } id && _scriptEditors.TryGetValue(id, out ScriptEditorPage? editor) ? editor : null;

    /// <summary>開いているスクリプトエディタ (テスト用)。</summary>
    internal IReadOnlyDictionary<string, ScriptEditorPage> ScriptEditors => _scriptEditors;

    private static string EditorPageId(string path) => "script:" + Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>スクリプトをエディタのタブで開く。開いていればそのタブを前に出し、行を指定すればそこへ移る。</summary>
    public void OpenScriptEditor(string path, int line = 0, int column = 0)
    {
        string id = EditorPageId(path);
        if (!_scriptEditors.TryGetValue(id, out ScriptEditorPage? page))
        {
            page = new ScriptEditorPage(path, this);
            _scriptEditors[id] = page;
            ShowToolPage(id, page, Path.GetFileName(path), "");
        }
        else
        {
            ShowToolPageTab(id);
        }

        if (line > 0)
        {
            DispatcherQueue.TryEnqueue(() => page.GoToLine(line, Math.Max(1, column)));
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => ((Panels.IPanelContent)page).FocusContent());
        }

        UpdateCommandStates();
    }

    /// <summary>ページのタブを閉じる直前: エディタの後始末 (比較タブの後始末と同じ口。MainWindow.Compare.cs)。</summary>
    private void OnScriptEditorClosing(string id)
    {
        if (_scriptEditors.Remove(id, out ScriptEditorPage? page))
        {
            page.Close();
        }
    }

    /// <summary>
    /// スクリプトエディタのタブを閉じる要求: 未保存の変更があれば「保存する」「保存しない」「キャンセル」を尋ねる。
    /// 閉じた (または閉じなくてよい) なら true。
    /// </summary>
    internal async Task<bool> ConfirmCloseScriptEditorAsync(string id)
    {
        if (!_scriptEditors.TryGetValue(id, out ScriptEditorPage? page) || !page.IsDirty)
        {
            return true;
        }

        ContentDialog dialog = NewDialog(Path.GetFileName(page.Path), Loc.Format("ScriptEditor_UnsavedBody", Path.GetFileName(page.Path)));
        dialog.PrimaryButtonText = Loc.Get("ScriptEditor_UnsavedSave");
        dialog.SecondaryButtonText = Loc.Get("ScriptEditor_UnsavedDiscard");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "ScriptEditorUnsavedDialog");
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        return result switch
        {
            ContentDialogResult.Primary => page.Save(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    /// <summary>未保存の変更があるスクリプトエディタ (ウィンドウを閉じる前の確認用)。</summary>
    internal IReadOnlyList<ScriptEditorPage> DirtyScriptEditors => [.. _scriptEditors.Values.Where(p => p.IsDirty)];

    private void NewScript(ScriptLanguage language)
    {
        try
        {
            string path = ScriptLibrary.CreateScript(ScriptingService.Folders.Scripts, language, Loc.Get("ScriptList_NewName"));
            OpenScriptEditor(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("ScriptList_CreateFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>「サンプル」を開く (AUTO-46 の仕様 3): 利用者のスクリプトフォルダにコピーしてからエディタで開く。引数はサンプルのファイル名。</summary>
    private Task OpenSampleFromArgumentAsync()
    {
        OpenScriptEditor(CopySample(CommandArgs.Argument ?? string.Empty));
        return Task.CompletedTask;
    }

    /// <summary>同梱のサンプルを利用者のスクリプトフォルダにコピーしてパスを返す (同梱のファイルは変えない)。</summary>
    internal static string CopySample(string fileName) =>
        ScriptLibrary.CopySample(Path.Combine(ScriptWorkspace.SamplesFolder, Path.GetFileName(fileName)), ScriptingService.Folders.Scripts);

    // ---- IScriptPanelHost ----

    void IScriptPanelHost.OpenScriptEditor(string path, int line, int column) => OpenScriptEditor(path, line, column);

    async Task<string?> IScriptPanelHost.PickSavePathAsync(string suggestedName, string extension) =>
        (await ScriptPickAsync("save", new Sdk.PickOptions { SuggestedName = suggestedName, Filters = ["*" + extension] }))?.FirstOrDefault();

    void IScriptPanelHost.SetClipboardText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    void IScriptPanelHost.ShowStatus(string text) => ShowStatusMessage(text, TimeSpan.FromSeconds(5));

    void IScriptPanelHost.FocusEditor() => FocusEditor();

    // ---- IScriptListHost ----

    void IScriptListHost.ShowNotice(string text, bool error) => ShowNotice(text, error ? InfoBarSeverity.Error : InfoBarSeverity.Informational);

    async Task<string?> IScriptListHost.PromptNameAsync(string title, string label, string initial)
    {
        Microsoft.UI.Xaml.Controls.TextBox box = Controls.DialogParts.Field("ScriptList_NameInput", label, initial, monospace: false);
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "ScriptListNameDialog", title, box, Loc.Get("Common_Ok"));
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>ショートカットの割り当て: 設定のキーボードの画面を、そのコマンドで絞り込んで開く (AUTO-19 の仕様 3)。</summary>
    void IScriptListHost.AssignShortcut(string commandId)
    {
        OpenSettingsPage(SettingCategories.Keyboard);
        string name = CommandService.DisplayName(commandId);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Views.SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Search") is Microsoft.UI.Xaml.Controls.TextBox search)
            {
                search.Text = name;
                search.Focus(FocusState.Programmatic);
            }
        });
    }

    void IScriptListHost.OpenHookSettings(string? scriptPath) => OpenHookSettings(scriptPath);

    void IScriptListHost.ShowInFolder(string path) => ShowInFolder(path);

    IReadOnlyList<string> IScriptListHost.ResidentScriptPaths =>
        [.. ScriptingService.Host.Running.Where(r => r.IsResident && r.Path is not null).Select(r => r.Path!)];

    private void ShowInFolder(string path)
    {
        if (TestHooks.Active)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowNotice(Loc.Format("ScriptList_ShowInFolderFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---- IScriptEditorHost ----

    IReadOnlyList<(string Name, object Document)> IScriptEditorHost.OpenDocuments =>
        [.. Vm.Documents.Where(d => !d.IsPending).Select(d => (d.DisplayName, (object)d))];

    object? IScriptEditorHost.ActiveDocument => Vm.Selected;

    void IScriptEditorHost.ActivateDocument(object document)
    {
        if (document is DocumentViewModel doc && Vm.Documents.Contains(doc))
        {
            Vm.Selected = doc;
        }
    }

    /// <summary>
    /// 「デバッガのアタッチを待っています」(AUTO-22 の仕様 2): プロセス ID と、アプリ内ではステップ実行できず外部のデバッガでアタッチする旨を
    /// 出す。アタッチされるか「アタッチせずに実行」を押すと true、「キャンセル」で false。
    /// </summary>
    async Task<bool> IScriptEditorHost.ConfirmAttachDebuggerAsync(int processId)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = Loc.Format("CSharpDebug_Waiting", processId), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = Loc.Get("CSharpDebug_Limitation"), TextWrapping = TextWrapping.Wrap });
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "CSharpDebugDialog", Loc.Get("CSharpDebug_Title"), body, Loc.Get("CSharpDebug_RunWithout"));
        // デバッガがアタッチされたら自動で閉じて実行を始める。
        using var cts = new CancellationTokenSource();
        bool attached = false;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    attached = true;
                    DispatcherQueue.TryEnqueue(dialog.Hide);
                    return;
                }

                try
                {
                    await Task.Delay(200, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        cts.Cancel();
        return attached || result == ContentDialogResult.Primary;
    }

    void IScriptEditorHost.ShowNotice(string text, bool error) => ShowNotice(text, error ? InfoBarSeverity.Error : InfoBarSeverity.Informational);

    void IScriptEditorHost.RefreshCommandStates() => UpdateCommandStates();

    void IScriptEditorHost.SetDebugSession(JsDebugSession? session) => ConsoleVm.DebugSession = session;

    // ---- 出力パネルの自動表示 (AUTO-24 の呼び出し) ----

    /// <summary>スクリプトの実行時に出力パネルを自動で表示する (設定 <c>scripts.output.autoShow</c>)。フック・コンソールの実行では表示しない。</summary>
    private void AutoShowOutput(ScriptRun run)
    {
        if (run.Origin is ScriptOrigin.Hook or ScriptOrigin.Console || !App.Settings.GetBool("scripts.output.autoShow", true))
        {
            return;
        }

        if (!IsPanelShown(OutputPanelId))
        {
            ShowPanel(OutputPanelId);
        }
    }

    // ---- フックの通知 (AUTO-25 の仕様 5) ----

    /// <summary>フックのエラー・上限時間の超過・自動で無効にしたことを InfoBar で知らせる。</summary>
    internal void ShowHookNotice(HookNotice notice)
    {
        string kind = Loc.Get("Hook_Kind_" + HookKinds.Name(notice.Kind));
        switch (notice.Type)
        {
            case HookNoticeKind.Failed:
                ShowNotice(Loc.Format("Hook_Failed", notice.ScriptName, kind, notice.Message), InfoBarSeverity.Error);
                break;
            case HookNoticeKind.TimedOut:
                ShowNotice(Loc.Format("Hook_TimedOut", notice.ScriptName, kind, notice.Message), InfoBarSeverity.Error);
                break;
            case HookNoticeKind.AutoDisabled:
                ShowNotice(Loc.Format("Hook_AutoDisabled", notice.ScriptName, kind), InfoBarSeverity.Warning,
                    actions: [new NotificationAction(Loc.Get("Hook_OpenSettings"), () => OpenHookSettings(notice.Script))]);
                break;
            case HookNoticeKind.SaveCanceled:
                ShowNotice(Loc.Format("Hook_SaveCanceled", notice.ScriptName, notice.Message), InfoBarSeverity.Warning);
                break;
        }
    }

    /// <summary>保存の前のフックが失敗した: 保存を続けるか尋ねる (ボタン: 「保存する」「保存しない」。AUTO-25 の仕様 5)。</summary>
    internal async Task<bool> ConfirmSaveAfterHookFailureAsync(HookNotice notice)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Hook_SaveFailedBody", notice.ScriptName, notice.Message), TextWrapping = TextWrapping.Wrap });
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "HookSaveDialog", Loc.Get("Hook_SaveFailedTitle"), body, Loc.Get("Hook_SaveAnyway"));
        dialog.CloseButtonText = Loc.Get("Hook_DontSave");
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>設定の「自動実行」を開く (スクリプトのパスを渡すと、そのスクリプトを追加する欄を示す)。</summary>
    internal void OpenHookSettings(string? scriptPath)
    {
        PendingHookScript = scriptPath;
        OpenSettingsPage(SettingCategories.Automation);
    }

    /// <summary>「自動実行の設定」で追加しようとしているスクリプト (設定の区画が読む)。</summary>
    internal string? PendingHookScript { get; set; }
}
