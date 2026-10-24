using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.App.Views;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Panels;
using HexEditor.Core.Selection;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using HexEditor.Core.Templates.Syntax;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 構造体テンプレート (07) のウィンドウ側: 結果ツリーパネル (TPL-21、既定は右)、出力パネル (TPL-27 の仕様 4、既定は下)、適用 (TPL-20)、
/// Hex ビューとの連動 (TPL-23)、右クリックメニュー (TPL-24)、エクスポート (TPL-25)、自動適用 (TPL-30)。
/// 適用の一覧はドキュメントごと (<see cref="DocumentTemplates"/>)、パネルの状態はウィンドウごと (<see cref="TemplatePanelViewModel"/>)。
/// </summary>
public sealed partial class MainWindow : ITemplatePanelHost, ITemplateOutputHost
{
    public const string TemplatesPanelId = "templates";
    public const string TemplateOutputPanelId = "templateOutput";

    private TemplatePanelViewModel? _templateVm;
    private TemplatePanel? _templatePanel;
    private TemplateOutputPanel? _templateOutput;
    private DocumentViewModel? _templateDocument;
    private bool _templateFollowSuppressed;
    private readonly HashSet<DocumentViewModel> _templateEditorHooked = [];

    /// <summary>パネルの一覧に登録し、コマンドライン・スクリプトのつなぎ目を差し込む (アプリの起動時、ウィンドウを作る前に 1 度)。</summary>
    public static void RegisterTemplatePanels()
    {
        if (PanelRegistry.Find(TemplatesPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(TemplatesPanelId, "Panel_Templates_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateTemplatePanel()));
            PanelRegistry.Register(new PanelRegistration(TemplateOutputPanelId, "Panel_TemplateOutput_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).CreateTemplateOutputPanel()));
        }

        Views.TemplateSettingsSection.Register();
        RegisterTemplateVisualizerPanel();
        RegisterTemplateInferencePanel();
        Views.OnlineTemplateSection.Register();

        // コマンドラインの --template (AUTO-37)
        CommandLineActions.ApplyTemplate = (window, doc, nameOrPath) => window.ApplyTemplateFromCommandLineAsync(doc, nameOrPath);

        // スクリプトの templates.apply(…, { show: true }) (AUTO-13): 適用したドキュメントのウィンドウの結果ツリーに出す
        Services.Scripting.ScriptingService.Templates.Show = (host, entry, result) =>
        {
            long offset = result.Options.StartOffset;
            App.DispatcherQueue?.TryEnqueue(() =>
            {
                if (host is Services.Scripting.AppDocumentHost appHost && WindowManager.OwnerOf(appHost.ViewModel) is { } owner)
                {
                    owner.AddTemplateApplication(appHost.ViewModel, new TemplateApplicationInfo { TemplatePath = entry.Path, StartOffset = offset });
                }
            });
        };
    }

    private TemplatePanelViewModel TemplateVm => _templateVm ??= CreateTemplateVm();

    private TemplatePanelViewModel CreateTemplateVm()
    {
        var vm = new TemplatePanelViewModel(DispatcherQueue);
        vm.ResultsChanged += (_, _) => TemplateResultsChanged();
        HookTemplateVisualizer(vm);
#if HEX_TEST_HOOKS
        // テスト用のタイムゾーン (OS の設定を変えない): ツールチップの地域の時刻 (TPL-03 の仕様 5)
        if (TestHooks.Settings.TimeZone is { Length: > 0 } zone)
        {
            vm.Model.DisplayContext = vm.Model.DisplayContext with { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(zone) };
        }
#endif
        return vm;
    }

    private TemplatePanel CreateTemplatePanel()
    {
        var panel = new TemplatePanel(TemplateVm, this);
        _templatePanel = panel;
        UpdateTemplateRadix();
        return panel;
    }

    private TemplateOutputPanel CreateTemplateOutputPanel()
    {
        var panel = new TemplateOutputPanel(this);
        _templateOutput = panel;
        panel.Refresh(_templateDocument is null ? null : DocumentTemplates.For(_templateDocument));
        return panel;
    }

    private TemplatePanel? TemplatePanelView => ShownPanelContent(TemplatesPanelId) as TemplatePanel ?? _templatePanel;

    /// <summary>作業中のドキュメントの適用 (なければ作る)。</summary>
    private DocumentTemplates? CurrentTemplates => Vm.Selected is { } doc ? DocumentTemplates.For(doc) : null;

    // ───────────── 初期化・ドキュメント ─────────────

    private void RegisterTemplateCommands()
    {
        RegisterTemplateVisualizerCommands();
        Commands.Register("templates.apply", () => ApplyTemplateAsync(Editor is null ? 0 : 0), NeedsDocument);
        Commands.Register("templates.applyHere", () => ApplyTemplateAsync(Editor?.Cursor ?? 0), NeedsDocument);
        Commands.Register("templates.followType", () => ApplyTemplateAsync(Editor?.Cursor ?? 0, TemplateApplyKind.Follow), NeedsDocument);
        Commands.Register("templates.applyKaitai", () => ApplyTemplateAsync(0, TemplateApplyKind.Fixed, kaitaiOnly: true), NeedsDocument);
        Commands.Register("templates.rerun", RerunTemplates, () => NeedsDocument(_ => CurrentTemplates?.Entries.Count > 0 ? null : Loc.Get("Tpl_NoApplications")));
        Commands.Register("templates.remove", RemoveSelectedApplication,
            () => NeedsDocument(_ => CurrentTemplates?.Entries.Count > 0 ? null : Loc.Get("Tpl_NoApplications")));
        Commands.Register("templates.stop", StopTemplateEvaluation, () => NeedsDocument(_ => TemplateVm.IsEvaluating ? null : Loc.Get("Tpl_NotEvaluating")));
        Commands.Register("templates.showResults", () => ShowPanel(TemplatesPanelId));
        Commands.Register("templates.showOutput", ShowTemplateOutput);
        Commands.Register("templates.editValue", async () =>
        {
            if (TemplateVm.SelectedItem is { } item && TemplatePanelView is { } panel)
            {
                await panel.EditAsync(item);
            }
        }, () => NeedsDocument(_ => TemplateEditBlockReason(TemplateVm.SelectedItem)));
        Commands.Register("templates.goToDefinition", GoToTemplateDefinitionAsync);
        Commands.Register("templates.goToTarget", () =>
        {
            if (TemplateVm.SelectedItem is { } item)
            {
                GoToPointerTarget(item);
            }
        }, () => NeedsDocument(_ => TemplateVm.SelectedItem?.Data?.PointerTarget is null ? Loc.Get("Tpl_NotAPointer") : null));
        Commands.Register("templates.copyValue", () => CopyTemplateValues(SelectedTemplateItems()), () => NeedsDocument(_ => TemplateVm.SelectedItem is null ? Loc.Get("Tpl_NoNode") : null));
        Commands.Register("templates.addBookmark", () => AddTemplateBookmarks(SelectedTemplateItems()), () => NeedsDocument(_ => TemplateVm.SelectedItem is null ? Loc.Get("Tpl_NoNode") : null));
        Commands.Register("templates.export", () => ExportTemplateResultAsync(null),
            () => NeedsDocument(_ => CurrentTemplates?.Entries.Count > 0 ? null : Loc.Get("Tpl_NoApplications")));
        Commands.Register("templates.toggleColoring", () =>
        {
            bool on = !TemplateService.ColoringEnabled;
            App.Settings.SetBool(TemplateService.ColoringKey, on, true);
            AnnotationLayer.SharedDisplay.SetVisible(AnnotationOrigin.Template, on);
            RefreshTemplateHighlights();
        }, () => Toggle(TemplateService.ColoringEnabled));
        Commands.Register("templates.findCandidates", FindTemplateCandidatesAsync, NeedsDocument);
        Commands.Register("templates.findValues", FindTemplateValuesCommand, NeedsDocument);
        Commands.Register("view.panel.templates", () => TogglePanel(TemplatesPanelId), () => Toggle(IsPanelShown(TemplatesPanelId)));
        Commands.Register("view.panel.templateOutput", () => TogglePanel(TemplateOutputPanelId), () => Toggle(IsPanelShown(TemplateOutputPanelId)));
        RegisterTemplateEditorCommands();
        RegisterTemplateInferenceCommands();

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                AttachTemplatesToSelected();
            }
        };
        Vm.Documents.CollectionChanged += Documents_CollectionChangedForTemplates;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Documents.CollectionChanged -= Documents_CollectionChangedForTemplates;
                _templateVm?.Detach();
            }
        };
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            OnTemplateDocumentOpened(doc);
        }

        AttachTemplatesToSelected();
    }

    private void Documents_CollectionChangedForTemplates(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel doc in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            OnTemplateDocumentOpened(doc);
        }

        foreach (DocumentViewModel doc in e.OldItems?.OfType<DocumentViewModel>() ?? [])
        {
            doc.EditorChanged -= TemplateDocument_EditorChanged;
            _templateEditorHooked.Remove(doc);

            // 閉じた (別のウィンドウに移したのではない) ドキュメントの評価を止める
            DispatcherQueue.TryEnqueue(() =>
            {
                if (WindowManager.OwnerOf(doc) is null && DocumentTemplates.Find(doc.Document) is { } t)
                {
                    t.Close();
                }
            });
        }
    }

    /// <summary>開いたドキュメント: 適用の記録を復元し、なければ自動適用の判定をする (TPL-20 の仕様 7、TPL-30)。</summary>
    private void OnTemplateDocumentOpened(DocumentViewModel doc)
    {
        if (_templateEditorHooked.Add(doc))
        {
            doc.EditorChanged += TemplateDocument_EditorChanged;
        }

        DocumentTemplates templates = DocumentTemplates.For(doc);
        if (templates.Loaded)
        {
            return;
        }

        bool restored = templates.Restore();
        if (!restored)
        {
            _ = AutoApplyTemplatesAsync(doc, templates);
        }
    }

    private void AttachTemplatesToSelected()
    {
        DocumentViewModel? doc = Vm.Selected;
        if (doc == _templateDocument)
        {
            return;
        }

        if (_templateDocument is not null && DocumentTemplates.Find(_templateDocument.Document) is { } old)
        {
            old.ResultChanged -= Templates_ResultChangedForWindow;
        }

        _templateDocument = doc;
        DocumentTemplates? templates = doc is null ? null : DocumentTemplates.For(doc);
        if (templates is not null)
        {
            templates.ResultChanged += Templates_ResultChangedForWindow;
        }

        TemplateVm.Attach(templates);
        UpdateTemplateRadix();
        _templateOutput?.Refresh(templates);
    }

    private void UpdateTemplateRadix()
    {
        if (_templateVm is not null)
        {
            _templateVm.Rows.Radix = Editor?.View.Radix ?? OffsetRadix.Hex;
        }
    }

    /// <summary>カーソルが動いた: 追従の適用を評価し直し、結果ツリーで対応するノードを選ぶ (TPL-23 の仕様 5、TPL-26)。</summary>
    private void TemplateDocument_EditorChanged(object? sender, EventArgs e)
    {
        if (sender is not DocumentViewModel doc || DocumentTemplates.Find(doc.Document) is not { } templates)
        {
            return;
        }

        long cursor = doc.Editor.Cursor;
        if (templates.Entries.Count == 0 || cursor == _lastTemplateCursor && ReferenceEquals(doc, _lastTemplateCursorDoc))
        {
            return;
        }

        _lastTemplateCursor = cursor;
        _lastTemplateCursorDoc = doc;
        templates.CursorMoved(cursor);
        if (doc == Vm.Selected && !_templateFollowSuppressed && _templateVm is not null && IsPanelShown(TemplatesPanelId))
        {
            QueueTemplateFollow(cursor);
        }
    }

    private long _lastTemplateCursor = -1;
    private DocumentViewModel? _lastTemplateCursorDoc;
    private long _followTarget = -1;
    private bool _followQueued;

    private void QueueTemplateFollow(long cursor)
    {
        _followTarget = cursor;
        if (_followQueued)
        {
            return;
        }

        _followQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            _followQueued = false;
            _templateFollowSuppressed = true;
            try
            {
                await TemplateVm.FollowAsync(_followTarget);
            }
            finally
            {
                _templateFollowSuppressed = false;
            }
        });
    }

    private void Templates_ResultChangedForWindow(object? sender, TemplateApplicationEntry entry)
    {
        _templateOutput?.Refresh(sender as DocumentTemplates);
        RefreshTemplateHighlights();
        if (entry.Result is { Completion.IsCompleted: true } result)
        {
            ReportTemplateResult(entry, result);
        }
        else if (entry.Result is { } running)
        {
            TrackTemplateOperation(entry, running);
        }
    }

    private void TemplateResultsChanged()
    {
        RefreshTemplateHighlights();
        UpdateCommandStates();
    }

    private void RefreshTemplateHighlights()
    {
        foreach (HexView view in _views)
        {
            view.RefreshHighlights();
        }
    }

    // ───────────── 評価の結果の知らせ (TPL-27・TPL-28・TPL-30) ─────────────

    private readonly HashSet<TemplateResult> _reportedResults = new(ReferenceEqualityComparer.Instance);

    private void ReportTemplateResult(TemplateApplicationEntry entry, TemplateResult result)
    {
        entry.Operation = null;
        if (!_reportedResults.Add(result))
        {
            return;
        }

        DocumentViewModel doc = entry.Owner.Owner;

        // パターン数の上限: 部分結果と「上限を上げて再実行」(TPL-28 の仕様 3)
        if (result.State == TemplateRunState.LimitReached && result.LimitReached == TemplateLimitKind.Patterns)
        {
            long limit = result.Limits.Patterns;
            ShowNotice(Loc.Format("Tpl_PatternLimit", limit.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, doc,
                actions: [new Core.Notifications.NotificationAction(Loc.Get("Tpl_RaiseLimit"), () => RaiseTemplateLimit(entry))]);
        }

        // 自動適用で付けた適用のエラー (TPL-30 の「エラー」)、require を満たさない「常に適用する」の適用は取り消す (仕様 3)
        if (entry.IsAuto)
        {
            if (TemplateAutoApply.FailedRequire(result))
            {
                entry.Owner.Remove(entry);
                return;
            }

            if (result.ErrorCount > 0)
            {
                ShowNotice(Loc.Format("Tpl_AutoApplyError", entry.DisplayName), InfoBarSeverity.Error, doc,
                    actions: [new Core.Notifications.NotificationAction(Loc.Get("Tpl_Details"), ShowTemplateOutput)]);
            }
        }
    }

    /// <summary>「上限を上げて再実行」: パターン数の上限を 10 倍 (設定の最大値まで) にして評価し直す。</summary>
    private void RaiseTemplateLimit(TemplateApplicationEntry entry)
    {
        TemplateLimits current = entry.Result?.Limits ?? TemplateLimits.Default;
        TemplateLimits max = TemplateLimits.DefaultMaximum;
        entry.Application.ReevaluateWithLimits(current with { Patterns = Math.Min(max.Patterns, current.Patterns * 10) });
    }

    /// <summary>評価に 0.5 秒以上かかったら処理センターに出す (評価済みのパターン数と位置。TPL-28 の仕様 5)。</summary>
    private void TrackTemplateOperation(TemplateApplicationEntry entry, TemplateResult result)
    {
        if (entry.Operation is not null || entry.Debugger is not null)
        {
            return;
        }

        var started = new TaskCompletionSource<Core.Operations.LongRunningOperation>();
        _ = Vm.Operations.RunAsync(Loc.Format("Tpl_OperationName", entry.DisplayName), Core.Operations.OperationKind.ReadOnly, entry.Owner.Document,
            result.DataLength, async op =>
            {
                started.TrySetResult(op);
                using CancellationTokenRegistration reg = op.CancellationToken.Register(result.Cancel);
                while (!result.Completion.IsCompleted)
                {
                    op.Report(Math.Clamp(result.CurrentOffset, 0, result.DataLength));
                    op.ReportDetail(Loc.Format("Tpl_Evaluated", result.PatternCount.ToString("N0", CultureInfo.CurrentCulture)));
                    await Task.WhenAny(result.Completion, Task.Delay(250));
                }
            });
        entry.Operation = started.Task.IsCompleted ? started.Task.Result : null;
    }

    // ───────────── 適用 (TPL-20) ─────────────

    /// <summary>「テンプレートを適用...」: 選択画面を出して適用する。</summary>
    private async Task ApplyTemplateAsync(long startOffset, TemplateApplyKind kind = TemplateApplyKind.Fixed, bool kaitaiOnly = false)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var dialog = new TemplateApplyDialog(Root.XamlRoot, startOffset, doc.Document.Length, doc.Editor.View.BigEndian, kind, kaitaiOnly);
        _lastApplyDialog = dialog;
        if (await dialog.ShowAsync() is { } info)
        {
            AddTemplateApplication(doc, info);
        }

        _lastApplyDialog = null;
    }

    private TemplateApplyDialog? _lastApplyDialog;

    public Task ApplyTemplateAsync() => ApplyTemplateAsync(0);

    /// <summary>適用を加えて結果ツリーを開く (TPL-21 の「呼び出し」: 適用時に自動で開く)。</summary>
    internal TemplateApplicationEntry? AddTemplateApplication(DocumentViewModel doc, TemplateApplicationInfo info, bool isAuto = false,
        Core.Templates.Loading.TemplateProgram? unsaved = null, Core.Templates.Debugging.TemplateDebugger? debugger = null)
    {
        DocumentTemplates templates = DocumentTemplates.For(doc);
        templates.MarkLoaded();
        if (templates.Add(info, unsaved, isAuto, debugger) is not { } entry)
        {
            ShowNotice(Loc.Format("Tpl_TooManyApplications", TemplateApplicationSet.MaxApplications), InfoBarSeverity.Warning, doc);
            return null;
        }

        if (!isAuto || true)
        {
            ShowPanel(TemplatesPanelId, focus: false);
        }

        return entry;
    }

    /// <summary>コマンドラインの <c>--template</c> (名前またはパス)。見つからなければ知らせる。</summary>
    private Task ApplyTemplateFromCommandLineAsync(DocumentViewModel doc, string nameOrPath)
    {
        if (TemplateService.Catalog.Find(nameOrPath) is not { } entry)
        {
            ShowNotice(Loc.Format("Tpl_NotFound", nameOrPath), InfoBarSeverity.Error, doc);
            return Task.CompletedTask;
        }

        DocumentTemplates templates = DocumentTemplates.For(doc);
        templates.MarkLoaded();
        if (templates.Entries.Any(e => string.Equals(e.Info.TemplatePath, entry.Path, StringComparison.OrdinalIgnoreCase) && e.Info.StartOffset == 0))
        {
            return Task.CompletedTask; // 同じ起動の引数を 2 度受け取った (既存のインスタンスへの転送など)
        }

        AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = entry.Path });
        return Task.CompletedTask;
    }

    public void RerunTemplates()
    {
        foreach (TemplateApplicationEntry e in CurrentTemplates?.Entries ?? [])
        {
            e.Application.Reevaluate();
        }
    }

    public void StopTemplateEvaluation()
    {
        foreach (TemplateApplicationEntry e in CurrentTemplates?.Entries ?? [])
        {
            e.Result?.Cancel();
            e.Debugger?.Resume(Core.Templates.Debugging.TemplateDebugCommand.Stop);
        }
    }

    private void RemoveSelectedApplication()
    {
        if (CurrentTemplates is not { } templates)
        {
            return;
        }

        TemplateApplicationEntry? entry = TemplateVm.SelectedItem is { } item ? TemplateVm.EntryOf(item) : templates.Entries.LastOrDefault();
        if (entry is not null)
        {
            templates.Remove(entry);
        }
    }

    // ───────────── 自動適用 (TPL-30) ─────────────

    private async Task AutoApplyTemplatesAsync(DocumentViewModel doc, DocumentTemplates templates)
    {
        TemplateAutoApplyMode mode = TemplateService.AutoApplyMode;
        if (mode == TemplateAutoApplyMode.None || doc.Document.IsDisposed)
        {
            templates.MarkLoaded();
            return;
        }

        TemplateCatalog catalog = TemplateService.Catalog;
        IReadOnlySet<string> suppressed = TemplateService.Preferences.Suppressed;
        string? path = doc.FilePath;
        Core.Engine.DocumentSnapshot snapshot = doc.Document.Current;
        TemplateAutoApplyDecision decision;
        try
        {
            decision = await Task.Run(() => TemplateAutoApply.Decide(catalog, snapshot, path, mode, suppressed, hasRecordedApplications: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            AppLog.Warning($"Template auto-apply: {ex.GetType().Name}");
            templates.MarkLoaded();
            return;
        }

        templates.MarkLoaded();
        AppLog.Info($"Template auto-apply decided in {decision.Elapsed.TotalMilliseconds:F1} ms ({decision.Candidates.Count} candidates, {decision.Action}).");
        if (doc.Document.IsDisposed || templates.Entries.Count > 0 || decision.Top is not { } top)
        {
            return;
        }

        LastAutoApplyDecision = decision;
        if (decision.Action == TemplateAutoApplyAction.Apply)
        {
            AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = top.Entry.Path }, isAuto: true);
            return;
        }

        if (decision.Action == TemplateAutoApplyAction.Suggest)
        {
            SuggestTemplate(doc, decision);
        }
    }

    /// <summary>最後の自動適用の判定 (テスト用の命令)。</summary>
    internal TemplateAutoApplyDecision? LastAutoApplyDecision { get; private set; }

    /// <summary>
    /// 提案の InfoBar (TPL-30 の仕様 3): 「このファイルは … のようです。テンプレート "…" を適用しますか?」と「適用」「ほかの候補...」
    /// 「この形式では今後表示しない」。編集を妨げない。拡張関数を使うテンプレートには「スクリプトを使用」と付ける (TPL-19 の仕様 3)。
    /// </summary>
    private void SuggestTemplate(DocumentViewModel doc, TemplateAutoApplyDecision decision)
    {
        TemplateCatalogEntry entry = decision.Top!.Entry;
        string name = entry.DisplayName(CultureInfo.CurrentUICulture);
        string message = Loc.Format("Tpl_Suggest", name) + (entry.Metadata.UsesExtensions ? " " + Loc.Get("Tpl_UsesScriptNote") : string.Empty);
        Core.Notifications.Notification? notice = null;
        notice = ShowNotice(message, InfoBarSeverity.Informational, doc, actions:
        [
            new Core.Notifications.NotificationAction(Loc.Get("Tpl_SuggestApply"), () =>
            {
                AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = entry.Path });
                DismissNotice(notice);
            }),
            new Core.Notifications.NotificationAction(Loc.Get("Tpl_SuggestOthers"), () => _ = ShowTemplateCandidatesAsync(doc, decision.Candidates)),
            new Core.Notifications.NotificationAction(Loc.Get("Tpl_SuggestNever"), () =>
            {
                TemplateService.Preferences.Suppressed.Add(entry.Path);
                TemplateService.SavePreferences();
                DismissNotice(notice);
            }),
        ]);
    }

    private void DismissNotice(Core.Notifications.Notification? notice)
    {
        if (notice is not null)
        {
            Vm.Notifications.Dismiss(notice);
        }
    }

    /// <summary>「ほかの候補...」(TPL-30 の仕様 4): 候補の一覧 (表示名、一致した条件、点数) を出し、選んで適用する。</summary>
    private async Task ShowTemplateCandidatesAsync(DocumentViewModel doc, IReadOnlyList<TemplateCandidate> candidates)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        AutomationProperties.SetAutomationId(list, "TplCandidates_List");
        foreach (TemplateCandidate c in candidates)
        {
            list.Items.Add(new ListViewItem { Content = CandidateText(c), Tag = c });
        }

        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.Get("TplCandidates_Title"),
            Content = list,
            PrimaryButtonText = Loc.Get("TplApply_Apply"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "TemplateCandidatesDialog");
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && (list.SelectedItem as ListViewItem)?.Tag is TemplateCandidate chosen)
        {
            AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = chosen.Entry.Path });
        }
    }

    /// <summary>候補の行 (表示名、一致した条件、点数)。</summary>
    internal static string CandidateText(TemplateCandidate c)
    {
        var conditions = new List<string>();
        if (c.Match.Magic)
        {
            conditions.Add(Loc.Get("Tpl_MatchMagic"));
        }

        if (c.Match.Mime)
        {
            conditions.Add(Loc.Get("Tpl_MatchMime"));
        }

        if (c.Match.Extension)
        {
            conditions.Add(Loc.Get("Tpl_MatchExtension"));
        }

        return Loc.Format("Tpl_CandidateLine", c.Entry.DisplayName(CultureInfo.CurrentUICulture), string.Join(", ", conditions), c.Match.Score);
    }

    /// <summary>「テンプレート: 合うテンプレートを探す」(TPL-30 の「呼び出し」)。</summary>
    private async Task FindTemplateCandidatesAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        TemplateCatalog catalog = TemplateService.Catalog;
        Core.Engine.DocumentSnapshot snapshot = doc.Document.Current;
        string? path = doc.FilePath;
        IReadOnlyList<TemplateCandidate> candidates = await Task.Run(() => catalog.Detect(snapshot, path));
        if (candidates.Count == 0)
        {
            ShowNotice(Loc.Get("Tpl_NoCandidates"), InfoBarSeverity.Informational, doc);
            return;
        }

        await ShowTemplateCandidatesAsync(doc, candidates);
    }

    // ───────────── 結果ツリーの操作 (ITemplatePanelHost) ─────────────

    private IReadOnlyList<TemplateTreeItem> SelectedTemplateItems() =>
        TemplatePanelView?.SelectedItems is { Count: > 0 } items ? items : TemplateVm.SelectedItem is { } one ? [one] : [];

    /// <summary>Hex ビューでノードの範囲を選んで表示する (TPL-23 の仕様 6、TPL-24 の「データへ移動」)。</summary>
    public async void GoToTemplateData(TemplateTreeItem item, bool focusEditor)
    {
        if (item.Data is null)
        {
            await TemplateVm.Model.LoadRowDataAsync([item]);
        }

        if (Vm.Selected is not { } doc || item.Data is not { } data || item.Kind == TemplateTreeItemKind.Application && data.Size == 0)
        {
            return;
        }

        if (data.SectionName is not null)
        {
            return; // セクションの中のパターンはドキュメントの範囲ではない
        }

        _templateFollowSuppressed = true;
        try
        {
            long start = data.Offset;
            long length = data.BitSize is not null ? Math.Max(1, data.Size) : data.Size;
            doc.Editor.SelectMatch(start, Math.Max(0, Math.Min(length, doc.Document.Length - start)));
            _lastTemplateCursor = doc.Editor.Cursor;
            _lastTemplateCursorDoc = doc;
        }
        finally
        {
            _templateFollowSuppressed = false;
        }

        if (focusEditor)
        {
            FocusEditor();
        }
    }

    /// <summary>出力パネルの「データへ」: その位置へ移動する。</summary>
    public void GoToTemplateDataOffset(long offset, string? sectionName)
    {
        if (sectionName is null && Editor is { } editor)
        {
            editor.GoTo(offset);
            FocusEditor();
        }
    }

    /// <summary>「指す先へ移動」(ジャンプ履歴に記録する。TPL-24 の仕様 1)。</summary>
    private async void GoToPointerTarget(TemplateTreeItem item)
    {
        if (Vm.Selected is not { } doc || item.Node is not { } node)
        {
            return;
        }

        TemplateResult result = node.Result;
        TemplateNode? target = await result.InvokeAsync(() => node.PointerTarget);
        if (target is null || target.Section is not null)
        {
            ShowNotice(Loc.Get("Tpl_NoTarget"), InfoBarSeverity.Warning, doc);
            return;
        }

        _templateFollowSuppressed = true;
        try
        {
            // 移動前の位置を記録する (Alt+← で戻れる)
            doc.Editor.RecordJump();
            doc.Editor.SelectMatch(target.Offset, Math.Max(0, Math.Min(target.Size, doc.Document.Length - target.Offset)));
        }
        finally
        {
            _templateFollowSuppressed = false;
        }

        FocusEditor();
    }

    public void ShowTemplateOutput() => ShowPanel(TemplateOutputPanelId);

    /// <summary>「値をコピー」(複数のノードは改行区切り。TPL-24 の仕様 2)。</summary>
    public void CopyTemplateValues(IReadOnlyList<TemplateTreeItem> items) =>
        CopyTemplateText(items, i => i.Data?.Value ?? string.Empty);

    private void CopyTemplateText(IReadOnlyList<TemplateTreeItem> items, Func<TemplateTreeItem, string> text)
    {
        if (items.Count == 0)
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(string.Join("\r\n", items.Select(text)));
        SystemClipboard.SetContent(package);
    }

    /// <summary>「ブックマークに追加」: 範囲を、パターンの名前とパスをコメントにしたブックマークにする (TPL-24 の仕様 1)。</summary>
    private void AddTemplateBookmarks(IReadOnlyList<TemplateTreeItem> items)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        DocumentAnnotations annotations = AnnotationsFor(doc);
        foreach (TemplateTreeItem item in items)
        {
            if (item.Data is not { } d || item.Kind != TemplateTreeItemKind.Node || d.SectionName is not null)
            {
                continue;
            }

            string name = item.Node!.DisplayName;
            Bookmark b = annotations.Bookmarks.Add(d.Offset, Math.Max(1, d.Size), name);
            annotations.Bookmarks.SetComment(b, Loc.Format("Tpl_BookmarkComment", name, d.Path));
        }

        annotations.Dirty = true;
        QueueAnnotationSave();
        SelectedView()?.RefreshHighlights();
    }

    /// <summary>右クリックメニュー (TPL-24 の仕様 1)。</summary>
    public void FillTemplateMenu(MenuFlyout menu, IReadOnlyList<TemplateTreeItem> items)
    {
        TemplateTreeItem? item = items.FirstOrDefault() ?? TemplateVm.SelectedItem;
        if (item is null)
        {
            return;
        }

        IReadOnlyList<TemplateTreeItem> all = items.Count > 0 ? items : [item];
        TemplateRowData? data = item.Data;
        void Add(string id, string textKey, Action action, bool enabled = true, string? reason = null)
        {
            var mi = new MenuFlyoutItem { Text = Loc.Get(textKey), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(mi, "TplMenu_" + id);
            if (!enabled && reason is not null)
            {
                ToolTipService.SetToolTip(mi, reason);
                AutomationProperties.SetHelpText(mi, reason);
            }

            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        bool node = item.Kind == TemplateTreeItemKind.Node;
        Add("GoToData", "TplMenu_GoToData", () => GoToTemplateData(item, true), data is not null && data.SectionName is null);
        Add("GoToTarget", "TplMenu_GoToTarget", () => GoToPointerTarget(item), data?.PointerTarget is not null);
        Add("GoToDefinition", "TplMenu_GoToDefinition", () => _ = GoToNodeDefinitionAsync(item), node);
        string? block = TemplateEditBlockReason(item);
        Add("EditValue", "TplMenu_EditValue", () => _ = TemplatePanelView?.EditAsync(item), block is null, block);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("CopyValue", "TplMenu_CopyValue", () => CopyTemplateValues(all));
        Add("CopyNameValue", "TplMenu_CopyNameValue", () => CopyTemplateText(all, i => (i.Node?.DisplayName ?? i.Data?.Name) + " = " + i.Data?.Value));
        Add("CopyPath", "TplMenu_CopyPath", () => CopyTemplateText(all, i => i.Data?.Path ?? string.Empty), node);
        Add("CopyBytes", "TplMenu_CopyBytes", () => CopyTemplateBytes(all), node && data?.SectionName is null);
        Add("CopyOffset", "TplMenu_CopyOffset", () => CopyTemplateText(all, i => "0x" + (i.Data?.Offset ?? 0).ToString("X", CultureInfo.InvariantCulture)));
        Add("CopyJson", "TplMenu_CopyJson", () => _ = CopyTemplateJsonAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("AddBookmark", "TplMenu_AddBookmark", () => AddTemplateBookmarks(all), node && data?.SectionName is null);
        Add("AddToSelection", "TplMenu_AddToSelection", () => AddTemplateRangesToSelection(all), node);
        Add("OpenSection", "TplMenu_OpenSection", () => OpenTemplateSection(item), data?.Kind == TemplateNodeKind.Section);
        Add("ApplyHere", "TplMenu_ApplyHere", () => _ = ApplyTemplateAsync(data?.Offset ?? 0), data is not null && data.SectionName is null);
        Add("BreakOnRead", "TplMenu_BreakOnRead", () => BreakOnRead(data?.Offset ?? 0), node && TemplateEditorDebugging);
        AddTemplateVisualizerMenuItem(menu, item);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (TemplateVm.EntryOf(item) is { } entry)
        {
            Add("ToggleKind", "TplMenu_ToggleKind", () => ToggleTemplateKind(entry));
            Add("ToFixed", "TplMenu_ToFixed", () => ConvertTemplateToFixed(entry), entry.IsFollow);
            Add("Settings", "TplMenu_Settings", () => _ = ShowTemplateApplicationSettingsAsync(entry));
            Add("Export", "TplMenu_Export", () => _ = ExportTemplateResultAsync(item));
            Add("Rerun", "TplMenu_Rerun", () => entry.Application.Reevaluate());
            Add("Remove", "TplMenu_Remove", () => entry.Owner.Remove(entry));
        }
    }

    /// <summary>編集できない理由 (TPL-22 の仕様 6。null は編集できる)。</summary>
    private string? TemplateEditBlockReason(TemplateTreeItem? item)
    {
        if (item?.Data is not { } d || item.Kind != TemplateTreeItemKind.Node)
        {
            return Loc.Get("Tpl_NoNode");
        }

        if (Vm.Selected?.Document.IsReadOnly == true)
        {
            return Loc.Get("Command_ReadOnly");
        }

        return d.EditBlock switch
        {
            TemplateEditBlock.None => null,
            TemplateEditBlock.InSection => Loc.Get("Tpl_EditBlock_InSection"),
            TemplateEditBlock.ReadOnlyAttribute => Loc.Get("Tpl_EditBlock_ReadOnly"),
            TemplateEditBlock.Computed => Loc.Get("Tpl_EditBlock_Computed"),
            TemplateEditBlock.Error => Loc.Get("Tpl_EditBlock_Error"),
            _ => Loc.Get("Tpl_EditBlock_NotAValue"),
        };
    }

    private void CopyTemplateBytes(IReadOnlyList<TemplateTreeItem> items)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        CopyTemplateText(items, i =>
        {
            if (i.Data is not { } d || d.Size <= 0 || d.Size > 1 << 20)
            {
                return string.Empty;
            }

            byte[] buffer = new byte[(int)Math.Min(d.Size, doc.Document.Length - d.Offset)];
            doc.Document.Current.Read(d.Offset, buffer);
            return Convert.ToHexString(buffer);
        });
    }

    /// <summary>「部分木を JSON でコピー」(64 MB を超える場合はファイルへの保存を勧める。TPL-24 の「巨大ファイル」)。</summary>
    private async Task CopyTemplateJsonAsync(TemplateTreeItem item)
    {
        TemplateNode? node = item.Node;
        if (node is null)
        {
            return;
        }

        const long Limit = 64L * 1024 * 1024;
        string? text = await Task.Run(() =>
        {
            var ms = new LimitedStream(Limit);
            try
            {
                TemplateJsonExporter.Write(node, ms, new TemplateExportOptions { Display = TemplateDisplayContext.Current });
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (IOException)
            {
                return null;
            }
        });
        if (text is null)
        {
            ShowNotice(Loc.Get("Tpl_JsonTooLarge"), InfoBarSeverity.Warning, Vm.Selected,
                actions: [new Core.Notifications.NotificationAction(Loc.Get("Tpl_SaveToFile"), () => _ = ExportTemplateResultAsync(item))]);
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    /// <summary>上限を超えたら書き込みに失敗するメモリのストリーム。</summary>
    private sealed class LimitedStream(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit)
            {
                throw new IOException("too large");
            }

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit)
            {
                throw new IOException("too large");
            }

            base.Write(buffer);
        }
    }

    /// <summary>「選択範囲をマルチ選択に追加」(TPL-24 の仕様 1)。</summary>
    private void AddTemplateRangesToSelection(IReadOnlyList<TemplateTreeItem> items)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var ranges = items.Select(i => i.Data).Where(d => d is { SectionName: null, Size: > 0 }).Select(d => new ByteRange(d!.Offset, d.Size)).ToList();
        if (ranges.Count > 0)
        {
            doc.Editor.SetSelections(ranges, add: true);
        }
    }

    /// <summary>「セクションを新しいタブで開く」(読み取り専用。TPL-15 の仕様 5)。</summary>
    private void OpenTemplateSection(TemplateTreeItem item)
    {
        if (Vm.Selected is not { } doc || item.Node is not { } node)
        {
            return;
        }

        TemplateSection? section = node.Result.Sections.FirstOrDefault(s => s.Name == node.Name) ?? node.Section;
        if (section is null)
        {
            return;
        }

        DocumentViewModel tab = Vm.AddTemplateSection(section.CreateByteSource(), $"{doc.DisplayName} ({section.Name})", doc);
        Vm.Selected = tab;
    }

    /// <summary>「固定 / 追従を切り替え」(TPL-26)。追従にするときは、型を選んでいなければ最初の型。</summary>
    private void ToggleTemplateKind(TemplateApplicationEntry entry)
    {
        TemplateApplicationInfo info = entry.Info;
        if (info.Kind == TemplateApplyKind.Follow)
        {
            entry.Owner.Update(entry, info with { Kind = TemplateApplyKind.Fixed, StartOffset = entry.Application.FollowOffset });
            return;
        }

        string? type = info.TypeName ?? entry.Application.Program?.TypeNames.FirstOrDefault();
        if (type is null)
        {
            return;
        }

        entry.Owner.Update(entry, info with { Kind = TemplateApplyKind.Follow, TypeName = type, StartOffset = Editor?.Cursor ?? 0 });
    }

    /// <summary>「固定に変換」: そのときのカーソル位置で固定の適用を作る (TPL-26 の仕様 6)。</summary>
    private void ConvertTemplateToFixed(TemplateApplicationEntry entry)
    {
        if (Vm.Selected is { } doc)
        {
            AddTemplateApplication(doc, entry.Application.ToFixed());
        }
    }

    /// <summary>適用の設定 (テンプレートのパス、開始オフセット、種類、エンディアン) を表示する。</summary>
    private async Task ShowTemplateApplicationSettingsAsync(TemplateApplicationEntry entry)
    {
        TemplateApplicationInfo info = entry.Info;
        string endian = info.BigEndian switch
        {
            true => Loc.Get("TplApply_EndianBig"),
            false => Loc.Get("TplApply_EndianLittle"),
            null => Loc.Get("TplApply_EndianDocument"),
        };
        var text = new TextBlock
        {
            Text = Loc.Format("Tpl_ApplicationSettings", info.TemplatePath, "0x" + info.StartOffset.ToString("X", CultureInfo.InvariantCulture),
                Loc.Get(info.Kind == TemplateApplyKind.Follow ? "TplApply_KindFollow" : "TplApply_KindFixed"), endian, info.TypeName ?? "—"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AutomationProperties.SetAutomationId(text, "TplSettings_Text");
        LastTemplateSettingsText = text.Text;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = entry.DisplayName,
            Content = text,
            CloseButtonText = Loc.Get("Common_Close"),
        };
        AutomationProperties.SetAutomationId(dialog, "TemplateSettingsDialog");
        await dialog.ShowQueuedAsync();
    }

    /// <summary>最後に表示した適用の設定 (テスト用の命令)。</summary>
    internal string? LastTemplateSettingsText { get; private set; }

    // ───────────── 値の編集 (TPL-22) ─────────────

    public async Task EditTemplateValueAsync(TemplateTreeItem item, FrameworkElement anchor)
    {
        if (Vm.Selected is not { } doc || item.Node is not { } node || item.Kind != TemplateTreeItemKind.Node)
        {
            return;
        }

        if (TemplateEditBlockReason(item) is { } reason)
        {
            ShowStatusMessage(reason);
            return;
        }

        TemplateValueEditSession? session = await node.Result.InvokeAsync(() => TemplateValueEditSession.Create(node, doc.Document.IsReadOnly, out _));
        if (session is null)
        {
            return;
        }

        var editor = new TemplateValueEditorFlyout(session);
        ActiveValueEditor = editor;
        TemplateEditPlan? plan = await editor.ShowAsync(anchor);
        ActiveValueEditor = null;
        if (plan is not { Success: true } p)
        {
            return;
        }

        WriteTemplatePlan(doc, p);
    }

    /// <summary>開いている値の編集欄 (テスト用の命令)。</summary>
    internal TemplateValueEditorFlyout? ActiveValueEditor { get; private set; }

    /// <summary>書き込む (パターンの範囲の上書き。Undo 1 回分。TPL-22 の仕様 2・5)。</summary>
    private void WriteTemplatePlan(DocumentViewModel doc, TemplateEditPlan plan)
    {
        if (doc.Document.IsReadOnly)
        {
            ShowNotice(Loc.Get("Command_ReadOnly"), InfoBarSeverity.Warning, doc);
            return;
        }

        if (plan.Offset < 0 || plan.Offset + plan.Bytes!.Length > doc.Document.Length)
        {
            return;
        }

        doc.Document.Overwrite(plan.Offset, plan.Bytes, Loc.Get("Tpl_UndoEdit"));
    }

    // ───────────── エクスポート (TPL-25) ─────────────

    public async Task ExportTemplateResultAsync(TemplateTreeItem? subtree)
    {
        if (Vm.Selected is not { } doc || CurrentTemplates is not { Entries.Count: > 0 } templates)
        {
            return;
        }

        TemplateTreeItem? selected = subtree ?? TemplateVm.SelectedItem;
        TemplateApplicationEntry? entry = selected is not null ? TemplateVm.EntryOf(selected) : templates.Entries[0];
        entry ??= templates.Entries[0];
        if (entry.Result is not { } result)
        {
            return;
        }

        bool canSubtree = selected is { Kind: TemplateTreeItemKind.Node };
        TemplateExportRequest? request = await new TemplateExportDialog(Root.XamlRoot, canSubtree, selected?.Node?.Path).ShowAsync();
        if (request is null)
        {
            return;
        }

        TemplateNode root = request.Subtree && selected?.Node is { } n ? n : result.Root;
        string extension = request.Format == TemplateExportFormat.Json ? ".json" : ".csv";
        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + "-" + Path.GetFileNameWithoutExtension(entry.Info.TemplatePath) + extension;
        string? path = await PickTemplateSavePathAsync(suggested, request.Format);
        if (path is null)
        {
            return;
        }

        await ExportTemplateToFileAsync(doc, root, path, request.Format, request.Options);
    }

    /// <summary>ストリームで書き出す (長時間処理。キャンセル・失敗したら書きかけのファイルを消す。TPL-25 の「巨大ファイル」「エラー」)。</summary>
    internal async Task ExportTemplateToFileAsync(DocumentViewModel doc, TemplateNode root, string path, TemplateExportFormat format, TemplateExportOptions options)
    {
        Func<string, Stream>? open = TemplateExportFailAt is { } failAt ? temp => new FailingWriteStream(temp, failAt) : null;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("Tpl_ExportOperation"), Core.Operations.OperationKind.ReadOnly, doc.Document, null, op =>
            {
                TemplateExportOptions o = options with { Progress = n => op.ReportDetail(Loc.Format("Tpl_ExportProgress", n.ToString("N0", CultureInfo.CurrentCulture))) };
                TemplateResultExporter.ExportToFile(root, path, format, o, op.CancellationToken, open);
                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("Tpl_Exported", Path.GetFileName(path)), InfoBarSeverity.Success, doc);
        }
        catch (OperationCanceledException)
        {
            // キャンセル: 書きかけのファイルは消してある
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Tpl_ExportError", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>テスト用: 書き込みを決めた位置で失敗させるストリーム (空き容量不足の再現。TC-TPL-25-05)。</summary>
    private sealed class FailingWriteStream(string path, long failAt) : FileStream(path, FileMode.CreateNew, FileAccess.Write)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > failAt)
            {
                throw new IOException(Loc.Get("Tpl_DiskFull"));
            }

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > failAt)
            {
                throw new IOException(Loc.Get("Tpl_DiskFull"));
            }

            base.Write(buffer);
        }
    }

    private async Task<string?> PickTemplateSavePathAsync(string suggestedName, TemplateExportFormat format)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.TemplateExport" };
        if (format == TemplateExportFormat.Json)
        {
            picker.FileTypeChoices.Add("JSON", [".json"]);
        }
        else
        {
            picker.FileTypeChoices.Add(Loc.Get("FileType_Csv"), [".csv"]);
        }

        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ───────────── Hex ビュー (TPL-23・TPL-26・TPL-32) ─────────────

    /// <summary>Hex ビューに追従の適用の枠線とデバッガの <c>$</c> の印を付ける (VIEW-17 の層 3)。</summary>
    private void AttachTemplates(HexView view)
    {
        view.SetHighlightSource("templateFocus", (start, end) => TemplateFocusHighlights(view, start, end));
        AttachTemplateInference(view);
    }

    private static readonly double[] FollowDash = [4, 2];

    private IEnumerable<HexHighlight> TemplateFocusHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } doc || DocumentTemplates.Find(doc.Document) is not { } templates)
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        foreach (TemplateApplicationEntry entry in templates.Entries)
        {
            if (entry.IsFollow && entry.Result is { } r && r.Root.Size > 0 && r.Root.Offset < end && r.Root.Offset + r.Root.Size > start)
            {
                // 追従の結果は色分けせず、範囲を枠線だけで示す (TPL-26 の仕様 5)
                yield return new HexHighlight(r.Root.Offset, r.Root.Size, CellLayer.Focus, null, TemplateBrushes.Line("TemplateFollowBorderBrush", view, hc),
                    FollowDash, "templateFollow", Thickness: 2);
            }

            if (entry.Debugger is { IsPaused: true, State: { } state } && state.Position >= start && state.Position < end)
            {
                // デバッガの現在位置 $ (カーソルとは別の印。TPL-32 の仕様 3)
                yield return new HexHighlight(state.Position, 0, CellLayer.Focus, null, TemplateBrushes.Line("TemplateDebugMarkerBrush", view, hc),
                    null, "templateDebug", Thickness: 2);
            }
        }
    }

    /// <summary>Hex ビューの右クリックメニュー: 「ここからテンプレートを適用...」「ここを読むときに止める」(TPL-20、TPL-32)。</summary>
    private void ExtendHexViewTemplateMenu(MenuFlyout menu)
    {
        const string ApplyId = "HexViewMenu_TemplateApplyHere";
        const string BreakId = "HexViewMenu_TemplateBreakOnRead";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == ApplyId))
        {
            var apply = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_TemplateApplyHere"), Tag = "TemplateApplyHere" };
            AutomationProperties.SetAutomationId(apply, ApplyId);
            apply.Click += (_, _) => _ = Commands.ExecuteAsync("templates.applyHere");
            var brk = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_TemplateBreakOnRead"), Tag = "TemplateBreakOnRead" };
            AutomationProperties.SetAutomationId(brk, BreakId);
            brk.Click += (_, _) => _ = Commands.ExecuteAsync("templates.breakOnRead");
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(apply);
            menu.Items.Add(brk);
        }

        ExtendHexViewInferenceMenu(menu);

        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            if (AutomationProperties.GetAutomationId(item) is BreakId)
            {
                item.Visibility = TemplateEditorOpen ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ───────────── 定義へ移動 (TPL-24 の仕様 3) ─────────────

    /// <summary>
    /// F12: 結果ツリーにフォーカスがあれば選んだノードの定義、Hex ビューならカーソル位置を含む最も深いパターンの定義、テンプレートエディタ
    /// なら名前の定義 (TPL-31) を開く。
    /// </summary>
    private async Task GoToTemplateDefinitionAsync()
    {
        DependencyObject? focused = Root.XamlRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        for (DependencyObject? n = focused; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (n is TemplatePanel && TemplateVm.SelectedItem is { } item)
            {
                await GoToNodeDefinitionAsync(item);
                return;
            }

            if (n is TemplateEditorPage page)
            {
                await page.GoToDefinitionAsync();
                return;
            }

            // 逆アセンブルパネルにフォーカスがあれば、選んだ行の命令の分岐先へ移動する (06 の ANA-25 の仕様 4)。
            if (n is DisassemblyPanel && DisasmVm.SelectedRow is { } disasmRow)
            {
                GoToDisasmTarget(disasmRow);
                return;
            }
        }

        // テンプレートの適用がなく、逆アセンブルパネルが開いていれば、カーソル位置を含む命令の分岐先へ移動する (ANA-25 の仕様 4)。
        if (Vm.Selected is { } d && DocumentTemplates.Find(d.Document) is not { Entries.Count: > 0 })
        {
            DisassemblyF12();
            return;
        }

        if (Vm.Selected is { } doc && Editor is { } editor && DocumentTemplates.Find(doc.Document) is { Entries.Count: > 0 } templates)
        {
            foreach (TemplateApplicationEntry entry in templates.Entries)
            {
                if (entry.Result is not { } result)
                {
                    continue;
                }

                long cursor = editor.Cursor;
                TemplateNode? node = await result.InvokeAsync(() => result.DeepestAt(cursor));
                if (node?.Declaration is { } at)
                {
                    await OpenTemplateSourceAsync(at);
                    return;
                }
            }
        }
    }

    private async Task GoToNodeDefinitionAsync(TemplateTreeItem item)
    {
        if (item.Node?.Declaration is { } at)
        {
            await OpenTemplateSourceAsync(at);
        }
    }

    /// <summary>マニュアルのエラーのコードのページの、そのコードの節を開く (TPL-27 の仕様 7、UI-59)。</summary>
    public async Task OpenTemplateHelpAsync(string code) => await OpenManualAsync(Core.Help.ManualTopic.TemplateError(code));

    // ───────────── 出力パネル (ITemplateOutputHost) ─────────────

    public Task OpenTemplateSourceAsync(TemplateSourceLocation location) => OpenTemplateEditorAtAsync(location.File, location.Line, location.Column);
}
