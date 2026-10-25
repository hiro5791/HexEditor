using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.App.Views;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Templates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 構造を理解した比較 (ANA-31・ANA-32、F6-01) のつなぎ込み。構造の比較は比較タブ (<see cref="CompareSessionViewModel"/>) が
/// バイトの比較と並行して行い、結果は下のパネル「構造の差分」(ウィンドウごとに 1 つ。最後に使った比較タブの結果) に出す。
/// </summary>
public sealed partial class MainWindow : IStructDiffHost
{
    /// <summary>構造の差分のパネル ID (表示切り替えのコマンドは <c>view.panel.structDiffs</c>)。</summary>
    public const string StructDiffsPanelId = "structDiffs";

    private StructDiffPanel? _structPanel;

    internal StructDiffPanel StructPanel => _structPanel ??= new StructDiffPanel { Host = this };

    /// <summary>パネルの一覧に登録する (<see cref="RegisterComparePanel"/> から呼ぶ)。</summary>
    private static void RegisterStructDiffPanel()
    {
        if (PanelRegistry.Find(StructDiffsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(StructDiffsPanelId, "Compare_StructPanel_Title", PanelDock.Bottom, ctx => ((MainWindow)ctx.Window).StructPanel)
            {
                CanFloat = false,
                RequiresDocument = false,
            });
        }
    }

    private void RegisterStructCompareCommands()
    {
        Commands.Register("compare.structure", () =>
        {
            if (CurrentCompare is { } c)
            {
                c.StructureEnabled = !c.StructureEnabled;
                ShowPanel(StructDiffsPanelId, focus: false);
            }
        }, () => CurrentCompare is not { } c ? CompareUnavailable(Loc.Get("Compare_NoTab"))
            : c.StructUnavailableReason is { } reason ? CompareUnavailable(reason) : Toggle(c.StructureEnabled));
        Commands.Register("compare.structureTemplate", async () =>
        {
            if (CurrentCompare is { } c)
            {
                await ChooseStructTemplateAsync(c);
            }
        }, () => CurrentCompare is not { } c ? CompareUnavailable(Loc.Get("Compare_NoTab"))
            : c.StructUnavailableReason is { } reason ? CompareUnavailable(reason) : CommandState.Available);
        Commands.Register("compare.navigateStructure", () =>
        {
            if (CurrentCompare is { } c)
            {
                c.NavigateStructure = !c.NavigateStructure;
            }
        }, () => CurrentCompare is { } c ? Toggle(c.NavigateStructure) : CompareUnavailable(Loc.Get("Compare_NoTab")));
        Commands.Register("compare.showStructDiffs", () => ShowPanel(StructDiffsPanelId));
        Commands.Register("compare.nextStructDiff", () => MoveStructFromCommand(next: true), StructNavigationState);
        Commands.Register("compare.previousStructDiff", () => MoveStructFromCommand(next: false), StructNavigationState);
        Commands.Register("compare.exportStructDiffs", async () =>
        {
            if (CurrentCompare is { } c)
            {
                await ExportStructDiffsAsync(c, StructPanel.View);
            }
        }, () => CurrentCompare?.StructResult is { Choice.IsNone: false } ? CommandState.Available : CompareUnavailable(Loc.Get("StructDiff_NoResult")));
    }

    private CommandState StructNavigationState() =>
        CurrentCompare?.StructResult is { Choice.IsNone: false } ? CommandState.Available : CompareUnavailable(Loc.Get("StructDiff_NoResult"));

    /// <summary>
    /// Alt+F5 / Shift+Alt+F5 (ANA-05) を構造の差分の移動にするか: 構造の差分パネルにフォーカスがあるとき、または比較タブの
    /// 「移動の対象」が「構造」のとき (ANA-32 の「呼び出し」・仕様 5)。
    /// </summary>
    private bool NavigatesStructure(CompareSessionViewModel session) =>
        session.StructResult is { Choice.IsNone: false } && ((_structPanel is { } p && p.HasTreeFocus) || session.NavigateStructure);

    private void MoveStructFromCommand(bool next)
    {
        if (CurrentCompare is { } c)
        {
            MoveStruct(c, next);
        }
    }

    /// <summary>次 / 前の構造の差分へ移動する (ANA-32 の仕様 5)。末尾から先頭に戻ったらステータスバーに知らせる。</summary>
    public void MoveStruct(CompareSessionViewModel session, bool next)
    {
        StructDiffPanel panel = StructPanel;
        if (!ReferenceEquals(panel.Session, session))
        {
            panel.Session = session;
        }

        bool? wrapped = session.MoveStruct(panel.View, next);
        _lastCompare = session;
        if (wrapped is null)
        {
            ShowStatusMessage(Loc.Get("StructDiff_NoStructDiffs"));
            return;
        }

        if (session.StructCurrent is { } current)
        {
            panel.SelectRow(current);
        }

        if (wrapped == true)
        {
            ShowStatusMessage(Loc.Get(next ? "StructDiff_WrappedToFirst" : "StructDiff_WrappedToLast"), TimeSpan.FromSeconds(3));
        }

        UpdateCompareStatus();
    }

    /// <summary>左右のドキュメントのテンプレートの適用 (構造の比較のテンプレートの決め方の 2)。</summary>
    private static IReadOnlyList<TemplateApplicationInfo> StructApplicationsOf(CompareSideViewModel side) =>
        side.Owner is { } doc && DocumentTemplates.Find(doc.Document) is { } t ? [.. t.Entries.Select(e => e.Info)] : [];

    /// <summary>テンプレートのファイルを保存した: そのテンプレートで構造の比較をしている比較タブをやり直す (ANA-31 の仕様 9)。</summary>
    internal void StructTemplateSaved(string path)
    {
        foreach (CompareSessionViewModel c in _compares)
        {
            c.TemplateSaved(path);
        }
    }

    // ---- IStructDiffHost ----

    void IStructDiffHost.ShowStatusMessage(string message) => ShowStatusMessage(message);


    public void SelectStructDiff(CompareSessionViewModel session, StructDiffNode node)
    {
        if (!ReferenceEquals(ActiveCompare, session))
        {
            ShowCompare(session);
        }

        session.SelectStructDiff(node);
        _lastCompare = session;
        UpdateCompareStatus();
    }

    /// <summary>「構造の比較に使うテンプレートを選ぶ」(TPL-20 の選択画面に左右の開始オフセットの欄を加えたもの)。</summary>
    public async Task ChooseStructTemplateAsync(CompareSessionViewModel session)
    {
        StructTemplateChoice? current = session.StructResult?.Choice is { IsNone: false } c ? c : session.StructUserChoice;
        long left = current?.LeftStart ?? session.Result?.Left.Start ?? 0;
        long right = current?.RightStart ?? session.Result?.Right.Start ?? 0;
        var dialog = new TemplateApplyDialog(Root.XamlRoot, left, session.Left.Document.Length, session.Left.Editor.View.BigEndian,
            compareRightOffset: right, compareRightLength: session.Right.Document.Length);
        _lastStructTemplateDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() is { } info)
            {
                TemplateService.AddRecent(info.TemplatePath);
                await session.SetStructTemplateAsync(new StructTemplateChoice(info.TemplatePath, info.StartOffset, dialog.RightStart, StructTemplateReason.UserChoice));
                ShowPanel(StructDiffsPanelId, focus: false);
            }
        }
        finally
        {
            _lastStructTemplateDialog = null;
        }
    }

    private TemplateApplyDialog? _lastStructTemplateDialog;

    public void EditStructTemplate(CompareSessionViewModel session)
    {
        if (session.StructResult?.Choice is { IsNone: false } c)
        {
            OpenTemplateEditor(c.TemplatePath);
        }
    }

    /// <summary>エラー・警告の件数のクリック: その側のテンプレートの出力 (TPL-27) を一覧にする。</summary>
    public async Task ShowStructDiagnosticsAsync(CompareSessionViewModel session, bool right)
    {
        if ((right ? session.StructResult?.RightResult : session.StructResult?.LeftResult) is not { } result)
        {
            return;
        }

        var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, "StructDiff_DiagnosticsList");
        foreach (TemplateOutputEntry e in result.Output)
        {
            list.Items.Add(e.Diagnostic is { } d ? TemplateDiagnosticText.Line(d) : e.Text);
        }

        if (list.Items.Count == 0)
        {
            list.Items.Add(Loc.Get("StructDiff_NoDiagnostics"));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = Loc.Get(right ? "StructDiff_RightOutput" : "StructDiff_LeftOutput"),
            Content = list,
            CloseButtonText = Loc.Get("Common_Close"),
        };
        await dialog.ShowQueuedAsync();
    }

    public void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    /// <summary>「左にブックマークを追加」「右にブックマークを追加」: パスと種類をコメントにしたブックマーク (ANA-32 の仕様 6)。</summary>
    public void AddStructBookmark(CompareSessionViewModel session, StructDiffNode node, bool right)
    {
        CompareSideViewModel side = right ? session.Right : session.Left;
        if (side.Owner is not { } doc || !(right ? node.HasRight : node.HasLeft))
        {
            return;
        }

        DocumentAnnotations annotations = AnnotationsFor(doc);
        long offset = right ? node.RightOffset : node.LeftOffset;
        long size = right ? node.RightSize : node.LeftSize;
        Bookmark b = annotations.Bookmarks.Add(offset, Math.Max(1, size), node.Name);
        annotations.Bookmarks.SetComment(b, Loc.Format("StructDiff_BookmarkComment", StructDiffRowViewModel.KindName(node.Kind), node.DisplayPath));
        annotations.Dirty = true;
        QueueAnnotationSave();
        ShowStatusMessage(Loc.Format("Compare_BookmarksAdded", 1.ToString("N0", CultureInfo.CurrentCulture)));
    }

    /// <summary>
    /// 「左の結果ツリーで表示」(ANA-32 の仕様 6): 左のドキュメントに同じテンプレート・同じ開始オフセットの適用があれば、その結果ツリーで対応する
    /// ノードを選ぶ。なければ固定の適用として加えてから選ぶ。適用の数が上限なら加えずに知らせる。
    /// </summary>
    public async Task ShowInResultTreeAsync(CompareSessionViewModel session, StructDiffNode node, bool right)
    {
        CompareSideViewModel side = right ? session.Right : session.Left;
        if (side.Owner is not { } doc || session.StructResult?.Choice is not { IsNone: false } choice)
        {
            return;
        }

        long start = right ? choice.RightStart : choice.LeftStart;
        string path = right ? node.RightPath : node.LeftPath;
        DocumentTemplates templates = DocumentTemplates.For(doc);
        TemplateApplicationEntry? entry = templates.Entries.FirstOrDefault(e => e.Info.Kind == TemplateApplyKind.Fixed && e.Info.TypeName is null
            && e.Info.StartOffset == start && string.Equals(e.Info.TemplatePath, choice.TemplatePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null && templates.Entries.Count >= TemplateApplicationSet.MaxApplications)
        {
            ShowNotice(Loc.Format("Tpl_TooManyApplications", TemplateApplicationSet.MaxApplications), InfoBarSeverity.Warning, doc);
            return;
        }

        // 結果ツリーは選んでいるドキュメントの適用を出すので、そのドキュメントのタブに切り替える
        Vm.Selected = doc;
        HideToolPages();
        entry ??= AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = choice.TemplatePath, StartOffset = start });
        ShowPanel(TemplatesPanelId, focus: false);
        if (entry is null)
        {
            return;
        }

        // 評価が終わり、結果ツリーに適用の行ができるまで待ってから選ぶ
        for (int i = 0; i < 200; i++)
        {
            if (entry.Result is { } result && result.Completion.IsCompleted && await TemplateVm.GoToPathAsync(path))
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>「対応付けの方法...」(ANA-31 の仕様 5 の 3): 配列ごとに方法とキーを選び、テンプレートと配列のパスごとに記録して比べ直す。</summary>
    public async Task ChangeArrayRuleAsync(CompareSessionViewModel session, StructDiffNode node)
    {
        if (node.Array is not { } info || session.StructResult?.Choice is not { IsNone: false } choice)
        {
            return;
        }

        var methods = new RadioButtons { Header = Loc.Get("StructRule_Method") };
        foreach (string key in new[] { "StructRule_Auto", "StructRule_Key", "StructRule_Content", "StructRule_Index" })
        {
            methods.Items.Add(Loc.Get(key));
        }

        AutomationProperties.SetAutomationId(methods, "StructRule_Method");
        var keys = new ComboBox { Header = Loc.Get("StructRule_KeyField"), MinWidth = 200 };
        AutomationProperties.SetAutomationId(keys, "StructRule_KeyField");
        foreach (string k in info.KeyCandidates)
        {
            keys.Items.Add(k);
        }

        StructArrayRules rules = StructArrayRules.FromJson(App.Settings.GetNode(StructArrayRules.SettingKey));
        StructArrayRule? rule = rules.Find(choice.TemplatePath, info.RulePath);
        methods.SelectedIndex = rule?.Method switch
        {
            StructMatchMethod.Key => 1,
            StructMatchMethod.Content => 2,
            StructMatchMethod.Index => 3,
            _ => 0,
        };
        keys.SelectedItem = rule?.Key ?? info.KeyField ?? info.KeyCandidates.FirstOrDefault();
        keys.IsEnabled = methods.SelectedIndex == 1;
        methods.SelectionChanged += (_, _) => keys.IsEnabled = methods.SelectedIndex == 1;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = Loc.Format("StructRule_Array", info.RulePath), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(methods);
        content.Children.Add(keys);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = Loc.Get("StructRule_Title"),
            Content = content,
            PrimaryButtonText = Loc.Get("Common_Ok"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "StructRuleDialog");
        _structRuleDialog = (dialog, methods, keys);
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            _structRuleDialog = null;
        }

        if (answer != ContentDialogResult.Primary)
        {
            return;
        }

        StructMatchMethod method = methods.SelectedIndex switch
        {
            1 => StructMatchMethod.Key,
            2 => StructMatchMethod.Content,
            3 => StructMatchMethod.Index,
            _ => StructMatchMethod.Auto,
        };
        if (method == StructMatchMethod.Key && keys.SelectedItem is not string)
        {
            return;
        }

        rules.Set(choice.TemplatePath, info.RulePath, method, keys.SelectedItem as string);
        App.Settings.SetNode(StructArrayRules.SettingKey, rules.ToJson());
        await session.RestartStructureAsync();
    }

    private (ContentDialog Dialog, RadioButtons Methods, ComboBox Keys)? _structRuleDialog;

    /// <summary>
    /// 「エクスポート...」(ANA-32 の仕様 7): 形式 (JSON・CSV・テキスト)、対象 (表示中の行・すべての差分)、オフセットの書式を選び、長時間処理として
    /// ストリームで書き出す。キャンセル・書き込みの失敗では書きかけのファイルを消す。
    /// </summary>
    public async Task ExportStructDiffsAsync(CompareSessionViewModel session, StructDiffView view)
    {
        if (session.StructResult is not { Choice.IsNone: false } result)
        {
            ShowStatusMessage(Loc.Get("StructDiff_NoResult"));
            return;
        }

        RadioButtons Radio(string header, string id, params string[] keys)
        {
            var r = new RadioButtons { Header = Loc.Get(header), SelectedIndex = 0 };
            foreach (string k in keys)
            {
                r.Items.Add(Loc.Get(k));
            }

            AutomationProperties.SetAutomationId(r, id);
            return r;
        }

        RadioButtons format = Radio("StructExport_Format", "StructExport_Format", "StructExport_Json", "StructExport_Csv", "StructExport_Text");
        RadioButtons scope = Radio("StructExport_Scope", "StructExport_Scope", "StructExport_Shown", "StructExport_All");
        RadioButtons offsets = Radio("StructExport_Offsets", "StructExport_Offsets", "StructExport_Decimal", "StructExport_Hex");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(format);
        content.Children.Add(scope);
        content.Children.Add(offsets);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = Loc.Get("StructExport_Title"),
            Content = content,
            PrimaryButtonText = Loc.Get("StructExport_Save"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "StructExportDialog");
        _structExportDialog = dialog;
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            _structExportDialog = null;
        }

        if (answer != ContentDialogResult.Primary)
        {
            return;
        }

        string kind = format.SelectedIndex switch { 1 => "csv", 2 => "text", _ => "json" };
        string extension = kind switch { "csv" => ".csv", "text" => ".txt", _ => ".json" };
        string suggested = "struct-diffs" + extension;
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.StructExport" };
            picker.FileTypeChoices.Add(Loc.Get(kind switch { "csv" => "StructExport_Csv", "text" => "StructExport_Text", _ => "StructExport_Json" }), [extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        IReadOnlyList<StructDiffNode> rows = scope.SelectedIndex == 1 ? result.Diffs() : [.. view.FilteredDiffs()];
        await ExportStructDiffsToAsync(session, result, rows, kind, offsets.SelectedIndex == 1, path);
    }

    private ContentDialog? _structExportDialog;

    /// <summary>書き出しの本体 (テスト用の命令からも呼ぶ)。</summary>
    internal async Task ExportStructDiffsToAsync(CompareSessionViewModel session, StructCompareResult result, IReadOnlyList<StructDiffNode> rows, string kind,
        bool hex, string path)
    {
        IReadOnlyList<string> summary = StructSummaryLines(result);
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("StructExport_OperationName"), OperationKind.WritesExternal, session, rows.Count, op =>
            {
                try
                {
                    using FileStream stream = File.Create(path);
                    switch (kind)
                    {
                        case "csv":
                            StructDiffExport.WriteCsv(result, rows, stream, hex, op.CancellationToken, op.Report);
                            break;
                        case "text":
                            StructDiffExport.WriteReport(result, summary, rows, StructDiffRowViewModel.KindName, Loc.Get("StructExport_OffsetsFormat"), stream,
                                op.CancellationToken, op.Report);
                            break;
                        default:
                            StructDiffExport.WriteJson(result, rows, stream, hex, op.CancellationToken, op.Report);
                            break;
                    }
                }
                catch (Exception)
                {
                    TryDelete(path);
                    throw;
                }

                return Task.CompletedTask;
            });
            ShowStatusMessage(Loc.Format("Compare_Exported", Path.GetFileName(path)));
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            ShowNotice(Loc.Format("Compare_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }

        static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>テキストのレポートの要約の行 (ANA-31 の仕様 10。表示言語)。</summary>
    private static IReadOnlyList<string> StructSummaryLines(StructCompareResult r)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            Loc.Format("StructExport_Template", r.TemplateName, r.Choice.TemplatePath),
            Loc.Format("StructExport_Starts", "0x" + r.Choice.LeftStart.ToString("X", CultureInfo.InvariantCulture), "0x" + r.Choice.RightStart.ToString("X", CultureInfo.InvariantCulture)),
            Loc.Format("StructExport_Fields", r.LeftFields.ToString("N0", c), r.RightFields.ToString("N0", c)),
        };
        foreach (StructDiffKind k in new[] { StructDiffKind.Changed, StructDiffKind.Added, StructDiffKind.Removed, StructDiffKind.Moved, StructDiffKind.MovedChanged, StructDiffKind.TypeChanged, StructDiffKind.Unparsed })
        {
            lines.Add(StructDiffRowViewModel.KindName(k) + ": " + r.CountOf(k).ToString("N0", c));
        }

        StructOutsideDiffs o = r.Outside ?? default;
        lines.Add(Loc.Format("StructExport_Outside", o.Count.ToString("N0", c), o.Bytes.ToString("N0", c)));
        lines.Add(Loc.Format("StructExport_Diagnostics", r.LeftErrors.ToString(c), r.LeftWarnings.ToString(c), r.RightErrors.ToString(c), r.RightWarnings.ToString(c)));
        lines.Add(Loc.Format("StructExport_Elapsed", r.Elapsed.TotalSeconds.ToString("N2", c)));
        if (r.State is StructCompareState.Cancelled or StructCompareState.FieldLimit)
        {
            lines.Add(Loc.Get(r.State == StructCompareState.Cancelled ? "StructDiff_Cancelled" : "StructDiff_StoppedAtLimit"));
        }

        return lines;
    }
}
