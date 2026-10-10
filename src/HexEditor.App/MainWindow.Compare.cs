using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// ファイル比較 (ANA-01〜ANA-08、F2-01) のつなぎ込み。比較タブはページのタブ (設定画面と同じ仕組み。MainWindow.TabItems.cs) で、中身は
/// <see cref="CompareView"/>。比較タブの表示中は、コマンド (元に戻す・ジャンプ・コピーなど) の対象とステータスバーを、フォーカスのある側の
/// 表示にする。差分の一覧はパネル「差分」(下) に出す (ウィンドウごとに 1 つの部品。最後に使った比較タブの結果を出す)。
/// </summary>
/// <remarks>
/// 並列表示 (VIEW-39) との関係: 比較タブは自分の Hex ビューを 2 つ持ち、<see cref="ICompareViewHost.AttachHexView"/> で通常のタブと同じ設定を
/// する。並列表示の部品ができたら、<see cref="CompareView"/> の左右の領域 (SidePanel) をその部品に置き換えられる (同期は
/// <see cref="CompareSessionViewModel"/> が EditorState だけで行うため、表示の部品に依存しない)。
/// </remarks>
public sealed partial class MainWindow : ICompareViewHost, IDiffListHost
{
    /// <summary>差分の一覧のパネル ID (表示切り替えのコマンドは <c>view.panel.diffs</c>)。</summary>
    public const string DiffsPanelId = "diffs";

    /// <summary>前回の比較方式とオプション (ANA-01 の仕様 4。state.json)。</summary>
    public const string CompareOptionsKey = "compare.options";

    /// <summary>比較タブの番号 (アプリ全体で一意。ページのタブの ID に使う)。</summary>
    private static int s_compareNumber;

    private readonly List<CompareSessionViewModel> _compares = [];
    private readonly Dictionary<CompareSessionViewModel, CompareView> _compareViews = [];
    private DiffListPanel? _diffPanel;
    private CompareSessionViewModel? _lastCompare;
    private CompareDialog? _compareDialog;

    /// <summary>タブの右クリックメニュー「比較の左側に選択」で選んだタブ (ANA-01 の「呼び出し」)。</summary>
    private DocumentViewModel? _compareLeftCandidate;

    /// <summary>パネルの一覧に登録し、外部変更の通知の「比較」(ENG-19 の仕様 5) をつなぐ (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterComparePanel()
    {
        if (PanelRegistry.Find(DiffsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(DiffsPanelId, "Compare_Panel_Title", PanelDock.Bottom, ctx => ((MainWindow)ctx.Window).DiffPanel)
            {
                CanFloat = false,
                RequiresDocument = false,
            });
        }

        CompareWithDisk = (window, doc) => window.CompareWithSavedAsync(doc, external: true);

        // 履歴パネルの「2 つの時点を比較」(EDIT-20 の仕様 5): 2 つの時点の内容を読み取り専用のコピーとして比較タブで比べる。
        HistoryPanelViewModel.CompareSnapshots = (doc, first, second, firstName, secondName) =>
            WindowManager.OwnerOf(doc) is { } window ? window.CompareSnapshotsAsync(doc, first, second, firstName, secondName) : Task.CompletedTask;
    }

    /// <summary>
    /// 同じドキュメントの 2 つの時点 (編集履歴のスナップショット) を比べる。内容はピースの参照で作るので、ファイルの大きさによらずすぐに開く。
    /// </summary>
    internal async Task CompareSnapshotsAsync(DocumentViewModel doc, DocumentSnapshot first, DocumentSnapshot second, string firstName,
        string secondName)
    {
        CompareTargetSpec Spec(DocumentSnapshot snapshot, string name) => new(CompareSourceKind.Content, doc, null, 0, null)
        {
            Open = () => Document.CreateCopy(snapshot, 0, snapshot.Length, $"{doc.DisplayName} ({name})", Vm.DocumentOptions),
            Name = $"{doc.DisplayName} ({name})",
        };

        CompareOptions options = CompareOptions.FromJson(CommandService.State?.Get(CompareOptionsKey));
        await OpenCompareAsync(Spec(first, firstName), Spec(second, secondName), options);
    }

    private DiffListPanel DiffPanel => _diffPanel ??= new DiffListPanel { Host = this };

    /// <summary>表示している比較タブ (なければ null)。</summary>
    internal CompareSessionViewModel? ActiveCompare => ActiveToolPage is { } id ? _compares.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>比較タブの表示中のコマンドの対象 (フォーカスのある側)。</summary>
    private EditorState? CompareEditor => ActiveCompare?.Focused is { IsClosed: false } side ? side.Editor : null;

    /// <summary>開いている比較タブ (テスト用)。</summary>
    internal IReadOnlyList<CompareSessionViewModel> Compares => _compares;

    // ---- コマンド (ANA-01・ANA-05・ANA-07・ANA-08) ----

    private void RegisterCompareCommands()
    {
        Commands.Register("analysis.compare", () => ShowCompareDialogAsync(null, null));
        Commands.Register("analysis.compareSaved", async () =>
        {
            if (Vm.Selected is { } doc)
            {
                await CompareWithSavedAsync(doc, external: doc.HasExternalChange);
            }
        }, () => NeedsDocument(d => d.FilePath is null ? Loc.Get("Compare_Untitled") : null));
        Commands.Register("compare.nextDiff", () => MoveDiff(next: true), DiffNavigationState);
        Commands.Register("compare.previousDiff", () => MoveDiff(next: false), DiffNavigationState);
        Commands.Register("compare.recompare", async () =>
        {
            if (ActiveCompare is { } c)
            {
                await c.RunAsync();
            }
        }, () => ActiveCompare is null ? CompareUnavailable(Loc.Get("Compare_NoTab")) : CommandState.Available);
        Commands.Register("compare.syncScroll", () =>
        {
            if (ActiveCompare is { } c)
            {
                c.SyncScroll = !c.SyncScroll;
            }
        }, () => ActiveCompare is { } c ? Toggle(c.SyncScroll) : CompareUnavailable(Loc.Get("Compare_NoTab")));
        Commands.Register("compare.layout", () =>
        {
            if (ActiveCompare is { } c)
            {
                c.Stacked = !c.Stacked;
            }
        }, () => ActiveCompare is { } c ? Toggle(c.Stacked) : CompareUnavailable(Loc.Get("Compare_NoTab")));
        foreach ((string id, MergeDirection direction, bool all) in new[]
        {
            ("compare.copyRight", MergeDirection.ToRight, false), ("compare.copyLeft", MergeDirection.ToLeft, false),
            ("compare.copyAllRight", MergeDirection.ToRight, true), ("compare.copyAllLeft", MergeDirection.ToLeft, true),
        })
        {
            Commands.Register(id, async () =>
            {
                if (CurrentCompare is { } c)
                {
                    await CopyDiffsAsync(c, direction, null, all);
                }
            }, () => CurrentCompare is not { } c ? CompareUnavailable(Loc.Get("Compare_NoTab"))
                : c.CopyBlockedReason(direction) is { } reason ? CompareUnavailable(reason) : CommandState.Available);
        }

        Commands.Register("compare.showDiffList", () => ShowPanel(DiffsPanelId));
        Commands.Register("compare.saveReport", async () =>
        {
            if (CurrentCompare is { } c)
            {
                await ExportDiffsAsync(c, "report", null);
            }
        }, () => CurrentCompare?.Result is null ? CompareUnavailable(Loc.Get("Compare_NoResult")) : CommandState.Available);

        Vm.Documents.CollectionChanged += Compare_DocumentsChanged;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                Vm.RaiseStatusDocumentChanged();
            }
        };
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                foreach (CompareSessionViewModel c in _compares.ToList())
                {
                    c.Dispose();
                }

                _compares.Clear();
            }
        };
    }

    private static CommandState CompareUnavailable(string reason) => CommandState.Unavailable(reason);

    /// <summary>マージ・レポートの対象の比較 (表示中の比較タブ、なければ最後に使った比較タブ)。</summary>
    private CompareSessionViewModel? CurrentCompare => ActiveCompare ?? (_lastCompare is { } last && _compares.Contains(last) ? last : null);

    /// <summary>
    /// 次 / 前の差分に使う比較 (ANA-05 の仕様 6): 比較タブならそれ、通常のタブなら、そのドキュメントを含む比較タブのうち最後に使ったもの。
    /// </summary>
    private (CompareSessionViewModel Session, EditorState Editor, bool Right)? DiffNavigationTarget()
    {
        if (ActiveCompare is { } active)
        {
            return (active, active.Focused.Editor, active.FocusedRight);
        }

        if (Vm.Selected is not { } doc)
        {
            return null;
        }

        CompareSessionViewModel? session = (_lastCompare is { } last && Includes(last, doc) ? last : null)
            ?? _compares.LastOrDefault(c => Includes(c, doc));
        if (session is null)
        {
            return null;
        }

        return (session, doc.Editor, session.Right.Owner == doc && !session.Right.IsClosed);

        static bool Includes(CompareSessionViewModel c, DocumentViewModel d) => c.Left.Owner == d || c.Right.Owner == d;
    }

    private CommandState DiffNavigationState() =>
        DiffNavigationTarget() is { Session.Result: not null } ? CommandState.Available : CompareUnavailable(Loc.Get("Compare_NoResult"));

    /// <summary>次 / 前の差分へ移動する (ANA-05)。</summary>
    private void MoveDiff(bool next)
    {
        if (DiffNavigationTarget() is not { } target || target.Session.Result is not { } result)
        {
            ShowStatusMessage(Loc.Get("Compare_NoResult"));
            return;
        }

        DiffStep? step;
        if (ReferenceEquals(target.Session, ActiveCompare))
        {
            step = target.Session.Move(next);
        }
        else
        {
            // 通常のタブ: そのタブのカーソルで探し、そのタブで選ぶ (ジャンプ履歴に記録する)。
            step = next ? DiffNavigation.Next(result.Diffs, target.Right, target.Editor.Cursor, target.Session.CurrentIndex)
                : DiffNavigation.Previous(result.Diffs, target.Right, target.Editor.Cursor);
            if (step is { } s)
            {
                DiffRange d = result.Diffs[s.Index];
                target.Editor.SelectMatch(d.Start(target.Right), d.Length(target.Right));

                // 比較タブの「差分 n / N」と次の移動の基準も、この差分にする。
                target.Session.SetCurrentIndex(s.Index);
            }
        }

        _lastCompare = target.Session;
        if (step is null)
        {
            ShowStatusMessage(Loc.Get("Compare_NoDiffs"));
        }
        else if (step.Value.Wrapped)
        {
            // 末尾から先頭 (先頭から末尾) に戻った: ステータスバーに 3 秒出す (仕様 3)。
            ShowStatusMessage(Loc.Get(next ? "Compare_WrappedToFirst" : "Compare_WrappedToLast"), TimeSpan.FromSeconds(3));
        }

        UpdateCompareStatus();
    }

    // ---- 比較を始める (ANA-01) ----

    /// <summary>「ファイルを比較」ダイアログを開く。<paramref name="left"/>・<paramref name="right"/> は初めに選んでおく対象。</summary>
    private async Task ShowCompareDialogAsync(DocumentViewModel? left, DocumentViewModel? right)
    {
        var candidates = new List<CompareCandidate>();
        foreach (DocumentViewModel doc in Vm.Documents.Where(d => !d.IsPending && !d.IsMissing))
        {
            candidates.Add(new CompareCandidate(doc.DisplayName, CompareSourceKind.Document, doc));
        }

        foreach (DocumentViewModel doc in Vm.Documents.Where(d => d.FilePath is not null && !d.IsPending && !d.IsMissing))
        {
            candidates.Add(new CompareCandidate(Loc.Format("Compare_SavedName", doc.DisplayName), CompareSourceKind.Saved, doc));
        }

        candidates.Add(new CompareCandidate(Loc.Get("Compare_Dialog_ChooseFile"), CompareSourceKind.File, null));
        int fileIndex = candidates.Count - 1;

        // 保存した .hexsnap (ANA-09 の仕様 3)。開いているスナップショットのタブはドキュメントの一覧に名前で出る (仕様 4)。
        candidates.Add(new CompareCandidate(Loc.Get("Compare_Dialog_ChooseSnapshot"), CompareSourceKind.Snapshot, null));
        int IndexOf(DocumentViewModel? d) => d is null ? -1 : candidates.FindIndex(c => c.Kind == CompareSourceKind.Document && c.Document == d);
        int leftIndex = IndexOf(left ?? Vm.Documents.ElementAtOrDefault(0));
        int rightIndex = IndexOf(right ?? Vm.Documents.FirstOrDefault(d => d != (left ?? Vm.Documents.ElementAtOrDefault(0))));

        // 片側だけを選んで実行した (開いているドキュメントが 1 つ): 右は「ファイルを選択...」にする (ANA-01 の仕様 9)。
        if (rightIndex < 0)
        {
            rightIndex = fileIndex;
        }

        if (leftIndex < 0)
        {
            leftIndex = fileIndex;
        }

        var content = new CompareDialog(candidates, CompareOptions.FromJson(CommandService.State?.Get(CompareOptionsKey)), leftIndex, rightIndex)
        {
            BrowseFile = PickCompareFileAsync,
            BrowseSnapshot = PickCompareSnapshotAsync,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("Compare_Dialog_Title"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = Loc.Get("Compare_Dialog_Compare"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = content.IsValid,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "CompareDialog");
        content.ValidityChanged += (_, _) => dialog.IsPrimaryButtonEnabled = content.IsValid;
        _compareDialog = content;
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            _compareDialog = null;
        }

        if (answer != ContentDialogResult.Primary || !content.IsValid)
        {
            return;
        }

        CommandService.State?.Set(CompareOptionsKey, content.Options.ToJson());
        (CompareTargetSpec l, CompareTargetSpec r) = content.Targets;
        await OpenCompareAsync(l, r, content.Options);
    }

    private async Task<string?> PickCompareFileAsync()
    {
        const string settingsIdentifier = "HexEditor.Compare";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { Count: > 0 } paths)
        {
            return paths[0];
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<string?> PickCompareSnapshotAsync()
    {
        const string settingsIdentifier = "HexEditor.CompareSnapshot";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { Count: > 0 } paths)
        {
            return paths[0];
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add(Core.Processes.HexSnapshot.Extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>
    /// 比較タブを開いて比較を始める (ANA-01 の仕様 5〜8)。同じ組み合わせの比較タブがあれば、そのタブに切り替えて再比較する (仕様 6)。
    /// 開けなかった場合 (ファイルが開けない) は理由を知らせて null。
    /// </summary>
    public async Task<CompareSessionViewModel?> OpenCompareAsync(CompareTargetSpec left, CompareTargetSpec right, CompareOptions options,
        bool fromPieces = false)
    {
        if (_compares.FirstOrDefault(c => Same(c.LeftSpec, left) && Same(c.RightSpec, right) && !c.Left.IsClosed && !c.Right.IsClosed) is { } existing)
        {
            existing.Options = options;
            ShowCompare(existing);
            await existing.RunAsync();
            return existing;
        }

        CompareSideViewModel? leftSide = null;
        CompareSideViewModel? rightSide = null;
        try
        {
            leftSide = CreateCompareSide(left, right: false);
            rightSide = CreateCompareSide(right, right: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftSide?.Dispose();
            ShowNotice(Loc.Format("Compare_CannotOpen", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        var session = new CompareSessionViewModel(Interlocked.Increment(ref s_compareNumber), leftSide, rightSide, left, right, options,
            Vm.Operations, DispatcherQueue)
        {
            FromPieces = fromPieces,
        };
        var view = new CompareView(this, session);
        view.Focused += (_, side) => OnCompareSideFocused(session, side);

        // ツールバーの「オプション」で変えた値も次回の既定にする (ANA-01 の仕様 4)。
        view.OptionsApplied += (_, applied) => CommandService.State?.Set(CompareOptionsKey, applied.ToJson());
        _compares.Add(session);
        _compareViews[session] = view;
        session.PropertyChanged += (_, _) => UpdateCompareStatus();
        session.ResultChanged += (_, _) => UpdateCompareStatus();
        session.ViewChanged += (_, _) => UpdateCompareStatus();

        // 比較範囲の先頭を一番上に表示する (左の開始 0x200・右の 0x0 が同じ高さに並ぶ。TC-ANA-01-02)。
        foreach ((CompareSideViewModel side, CompareTargetSpec spec) in new[] { (leftSide, left), (rightSide, right) })
        {
            long start = Math.Clamp(spec.Start, 0, side.Editor.Layout.MaxCursor);
            side.Editor.FollowTo(start, side.Editor.Layout.RowOf(start));
        }

        _lastCompare = session;
        ShowToolPage(session.Id, view, session.Title, "");
        ShowPanel(DiffsPanelId, focus: false);
        DiffPanel.Session = session;
        DispatcherQueue.TryEnqueue(() => view.LeftView.Focus(FocusState.Programmatic));
        await session.RunAsync();
        return session;

        static bool Same(CompareTargetSpec a, CompareTargetSpec b) => a.Kind == b.Kind && ReferenceEquals(a.Document, b.Document)
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Start == b.Start && a.Length == b.Length;
    }

    /// <summary>
    /// 片側を作る。開いているドキュメントは同じドキュメントを別のカーソルで表示し、ディスク上のファイル・保存済みの内容は読み取り専用で開く。
    /// </summary>
    private CompareSideViewModel CreateCompareSide(CompareTargetSpec spec, bool right)
    {
        switch (spec.Kind)
        {
            case CompareSourceKind.Document:
                DocumentViewModel owner = spec.Document!;
                var view = new DocumentViewModel(owner.Document, owner.FilePath, owner.DisplayName) { Notifications = Vm.Notifications };
                view.Editor.ApplyView(owner.Editor.View);
                view.Editor.TextEncoding = owner.Editor.TextEncoding;
                EditorSettings.Apply(App.Settings, view);
                return new CompareSideViewModel(right, view, owner, ownsDocument: false, owner.DisplayName);
            case CompareSourceKind.Content:
                Document content = spec.Open!();
                content.SetReadOnly(ReadOnlyReason.OpenedReadOnly);
                string contentName = spec.Name ?? content.Source.DisplayName;
                var contentView = new DocumentViewModel(content, null, contentName) { Notifications = Vm.Notifications };
                if (spec.Document is { } from)
                {
                    contentView.Editor.ApplyView(from.Editor.View);
                }

                EditorSettings.Apply(App.Settings, contentView);
                return new CompareSideViewModel(right, contentView, null, ownsDocument: true, contentName);
            default:
                string path = spec.Path ?? spec.Document?.FilePath ?? throw new IOException(Loc.Get("Compare_Dialog_NoPath"));

                // .hexsnap はスナップショットとして開き、領域ごとに比べる (ANA-09 の仕様 3・5。「ファイルを選択...」で選んだ場合も)。
                if (spec.Kind == CompareSourceKind.Snapshot || (spec.Kind == CompareSourceKind.File && Core.Processes.HexSnapshot.IsSnapshotFile(path)))
                {
                    Core.Processes.SnapshotByteSource snapshot;
                    try
                    {
                        snapshot = Core.Processes.SnapshotByteSource.Open(path);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or EndOfStreamException)
                    {
                        throw new IOException(Loc.Get("Compare_Dialog_NotSnapshot"), ex);
                    }

                    var snapshotDocument = new Document(snapshot, Vm.DocumentOptions);
                    snapshotDocument.SetReadOnly(ReadOnlyReason.NoWriteTarget);
                    var snapshotView = new DocumentViewModel(snapshotDocument, null, snapshot.DisplayName) { Notifications = Vm.Notifications };
                    EditorSettings.Apply(App.Settings, snapshotView);
                    return new CompareSideViewModel(right, snapshotView, null, ownsDocument: true, snapshot.DisplayName);
                }

                var fileSource = FileByteSource.Open(path);
                var document = new Document(fileSource, Vm.DocumentOptions);
                document.SetReadOnly(ReadOnlyReason.OpenedReadOnly);
                string name = spec.Kind == CompareSourceKind.Saved ? Loc.Format("Compare_SavedName", spec.Document!.DisplayName) : Path.GetFileName(path);
                var fileView = new DocumentViewModel(document, path, name) { Notifications = Vm.Notifications };
                if (spec.Document is { } source)
                {
                    fileView.Editor.ApplyView(source.Editor.View);
                }

                EditorSettings.Apply(App.Settings, fileView);
                var side = new CompareSideViewModel(right, fileView, null, ownsDocument: true, name) { FilePath = path };
                WatchCompareSide(side, fileSource);
                return side;
        }
    }

    /// <summary>
    /// 比較タブが自分で開いたファイルの外部変更 (ENG-19) を監視する。検知したら比較タブに「再比較」を出し、再比較で開き直す
    /// (ANA-04 の「エラー」)。監視は通常のタブと同じ仕組み (<see cref="ExternalChangeMonitor"/>) を使う。
    /// </summary>
    private void WatchCompareSide(CompareSideViewModel side, FileByteSource source)
    {
        if (Vm.ExternalChanges is not { } monitor || side.FilePath is not { } path)
        {
            return;
        }

        WatchedFile watch = monitor.Track(side, path, source.Stamp, source.ReadCurrentStamp);
        side.StopWatching = () => monitor.Untrack(watch);
        side.RebaseWatch = () =>
        {
            if (side.View.Document.Source is FileByteSource reopened)
            {
                monitor.Rebase(watch, reopened.ReadCurrentStamp() ?? reopened.Stamp, reopened.ReadCurrentStamp);
            }
        };
    }

    /// <summary>
    /// 通常のタブのファイルが外部で変更された (ENG-19): そのドキュメントを比べている比較タブにも「再比較」を出す (ANA-04 の「エラー」)。
    /// </summary>
    partial void OnDocumentChangedOnDisk(DocumentViewModel doc)
    {
        foreach (CompareSessionViewModel session in _compares)
        {
            foreach (CompareSideViewModel side in new[] { session.Left, session.Right })
            {
                if (side.Owner == doc && !side.IsClosed)
                {
                    session.MarkExternalChange(side);
                }
            }
        }
    }

    /// <summary>
    /// 保存済みの内容と比較する (ANA-08)。外部で変更されていなければピースツリーから差分を求め (仕様 2)、変更されていれば挿入・削除を考慮した
    /// 比較を行う (仕様 3。外部変更の通知の「比較」から呼ばれる場合を含む)。未保存の編集も外部変更もなければ比較タブを開かない (仕様 5)。
    /// </summary>
    public async Task CompareWithSavedAsync(DocumentViewModel doc, bool external)
    {
        if (doc.FilePath is not { } path)
        {
            ShowStatusMessage(Loc.Get("Compare_Untitled"));
            return;
        }

        bool changedOnDisk = external || doc.HasExternalChange;
        if (!changedOnDisk && !doc.Document.IsModified)
        {
            ShowStatusMessage(Loc.Get("Compare_SameAsSaved"));
            return;
        }

        if (!File.Exists(path))
        {
            ShowNotice(Loc.Format("Compare_SavedMissing", Loc.Get("Compare_Dialog_NotFound")), InfoBarSeverity.Error, doc);
            return;
        }

        CompareOptions remembered = CompareOptions.FromJson(CommandService.State?.Get(CompareOptionsKey));
        CompareOptions options = changedOnDisk ? remembered with { Method = CompareMethod.InsertDelete } : remembered;
        try
        {
            await OpenCompareAsync(new CompareTargetSpec(CompareSourceKind.Saved, doc, path, 0, null),
                new CompareTargetSpec(CompareSourceKind.Document, doc, null, 0, null), options, fromPieces: !changedOnDisk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Compare_SavedMissing", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>
    /// Shift を押しながら 2 つのファイルをドロップした (ANA-01 の仕様 10): 2 つを開き、その 2 つを左右に選んだ比較ダイアログを開く。
    /// 2 つのファイルでなければ false (通常のドロップとして扱う)。
    /// </summary>
    internal async Task<bool> TryDropCompareAsync(IReadOnlyList<Windows.Storage.IStorageItem> items)
    {
        var files = items.OfType<Windows.Storage.StorageFile>().ToList();
        if (files.Count != 2 || items.Count != 2)
        {
            return false;
        }

        DocumentViewModel? Open(string path) => Vm.Documents.FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            ?? TryOpen(path);
        DocumentViewModel? left = Open(files[0].Path);
        DocumentViewModel? right = Open(files[1].Path);
        if (left is null || right is null)
        {
            return true;
        }

        await ShowCompareDialogAsync(left, right);
        return true;
    }

    // ---- 比較タブの表示・閉じる ----

    public void ShowCompare(CompareSessionViewModel session)
    {
        if (_compareViews.TryGetValue(session, out CompareView? view))
        {
            ShowToolPage(session.Id, view, session.Title, "");
        }
    }

    partial void OnToolPageClosing(string id)
    {
        if (_compares.FirstOrDefault(c => c.Id == id) is not { } session)
        {
            return;
        }

        _compares.Remove(session);
        if (_compareViews.Remove(session, out CompareView? view))
        {
            foreach (HexView hex in new[] { view.LeftView, view.RightView })
            {
                _views.Remove(hex);
                hex.Editor = null;
            }
        }

        if (ReferenceEquals(_lastCompare, session))
        {
            _lastCompare = _compares.LastOrDefault();
        }

        if (_diffPanel is not null && ReferenceEquals(_diffPanel.Session, session))
        {
            _diffPanel.Session = _lastCompare;
        }

        session.Dispose();
    }

    partial void OnActiveToolPageChanged()
    {
        CompareSessionViewModel? active = ActiveCompare;
        Vm.StatusOverride = active?.Focused is { IsClosed: false } side ? side.View : null;
        if (active is not null)
        {
            _lastCompare = active;
            DiffPanel.Session = active;
        }

        UpdateCompareStatus();
        UpdateCommandStates();
    }

    /// <summary>比較タブの片側の Hex ビューにフォーカスが移った: コマンドの対象とステータスバーをその側にする。</summary>
    private void OnCompareSideFocused(CompareSessionViewModel session, CompareSideViewModel side)
    {
        session.FocusedRight = side.IsRight;
        if (!ReferenceEquals(ActiveCompare, session))
        {
            return;
        }

        Vm.StatusOverride = side.View;
        if (side.Owner is { } owner && Vm.Documents.Contains(owner) && Vm.Selected != owner)
        {
            // 保存などドキュメントに対するコマンドの対象もその側のドキュメントにする (ページのタブは選んだまま)。
            Vm.Selected = owner;
        }

        UpdateCommandStates();
    }

    /// <summary>ドキュメントのタブが閉じられた: そのドキュメントを含む比較を中止する (ANA-04 の「エラー」)。</summary>
    private void Compare_DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null)
        {
            return;
        }

        foreach (DocumentViewModel doc in e.OldItems.OfType<DocumentViewModel>())
        {
            foreach (CompareSessionViewModel session in _compares)
            {
                foreach (CompareSideViewModel side in new[] { session.Left, session.Right })
                {
                    if (side.Owner == doc && !side.IsClosed)
                    {
                        session.SideClosed(side);
                        if (_compareViews.TryGetValue(session, out CompareView? view))
                        {
                            view.ViewOf(side.IsRight).Editor = null;
                        }
                    }
                }
            }
        }
    }

    // ---- ステータスバー (ANA-04 の仕様 6) ----

    private void UpdateCompareStatus()
    {
        CompareSessionViewModel? active = ActiveCompare;
        StatusCompare.Content = active?.StatusText ?? string.Empty;
        StatusCompare.Visibility = active is not null && IsStatusItemVisible("compare") ? Visibility.Visible : Visibility.Collapsed;
        if (active is not null && _compareViews.TryGetValue(active, out CompareView? view))
        {
            view.RefreshToolbarStates();
        }

        QueueStatusBarLayout();
    }

    /// <summary>ステータスバーの比較の項目: 差分の一覧を出す。</summary>
    private void StatusCompare_Click(object sender, RoutedEventArgs e) => ShowPanel(DiffsPanelId);

    // ---- タブの右クリックメニュー (ANA-01・ANA-08 の「呼び出し」) ----

    private IEnumerable<TabMenuEntry?> CompareTabMenuEntries(DocumentViewModel doc)
    {
        if (doc.IsPending || doc.IsMissing)
        {
            yield break;
        }

        yield return null;
        yield return new TabMenuEntry("TabMenu_CompareSelectLeft", Loc.Get("Compare_Tab_SelectLeft"), true, () => _compareLeftCandidate = doc);
        DocumentViewModel? left = _compareLeftCandidate is { } l && l != doc && Vm.Documents.Contains(l) ? l : null;
        yield return new TabMenuEntry("TabMenu_CompareWithLeft", left is null ? Loc.Get("Compare_Tab_WithLeftNone") : Loc.Format("Compare_Tab_WithLeft", left.DisplayName),
            left is not null, () => _ = OpenCompareAsync(new CompareTargetSpec(CompareSourceKind.Document, left!, null, 0, null),
                new CompareTargetSpec(CompareSourceKind.Document, doc, null, 0, null), CompareOptions.FromJson(CommandService.State?.Get(CompareOptionsKey))));
        yield return new TabMenuEntry("TabMenu_CompareSaved", Loc.Get("Compare_Tab_Saved"), doc.FilePath is not null,
            () => _ = CompareWithSavedAsync(doc, external: doc.HasExternalChange));
    }

    // ---- ICompareViewHost ----

    CommandState ICompareViewHost.StateOf(string commandId) => Commands.StateOf(commandId);

    Task ICompareViewHost.ExecuteAsync(string commandId) => Commands.ExecuteAsync(commandId);

    void ICompareViewHost.AttachHexView(HexView view, CompareSessionViewModel session, CompareSideViewModel side)
    {
        // 通常のタブの Hex ビュー (HexView_Loaded) と同じ設定。フォーカスは比較タブが決める。
        if (!_views.Contains(view))
        {
            _views.Add(view);
        }

        view.Loaded += (_, _) =>
        {
            TrackDocumentName(view);
            ConfigureHexView(view);
            AttachZoom(view);
        };
        view.FocusRegionRequested += (_, args) =>
        {
            MoveToRegion(args.Forward);
            args.Handled = true;
        };
        view.StatusMessageRequested += (_, args) => ShowNotice(args.Message, InfoBarSeverity.Informational, side.Owner);
        view.CommandRequested += HexView_CommandRequested;
        view.EditRejected += HexView_EditRejected;
        view.MinimapDifferences = MinimapDifferencesOf(session, side.IsRight);
    }

    /// <summary>
    /// ミニマップの差分の印 (VIEW-35 の仕様 6) の提供元。印は描くたびに求めるため、結果・件数・状態が変わったときだけ作り直す
    /// (差分が数百万件でも、カーソルを動かすたびに全件を読まない)。
    /// </summary>
    private static Func<IEnumerable<(long Offset, long Length, DiffKind Kind)>> MinimapDifferencesOf(CompareSessionViewModel session, bool right)
    {
        (CompareResult? Result, long Count, CompareState State) key = default;
        IReadOnlyList<(long Offset, long Length, DiffKind Kind)> marks = [];
        long builtAt = 0;
        return () =>
        {
            if (session.Result is not { } result)
            {
                return [];
            }

            // 比較中は 1 秒に 1 回まで作り直す (見つかった差分を順に示しつつ、全件を読み直し続けない)。
            (CompareResult?, long, CompareState) now = (result, result.Diffs.Count, result.State);
            bool throttled = result.State == CompareState.Running && ReferenceEquals(key.Result, result) && Environment.TickCount64 - builtAt < 1000;
            if (now != key && !throttled)
            {
                builtAt = Environment.TickCount64;
                CompareRange range = right ? result.Right : result.Left;
                marks = DiffMarks.Build(result.Diffs.Enumerate(), right, range.Length);
                key = now;
            }

            return marks;
        };
    }

    // ---- IDiffListHost (ANA-06・ANA-07) ----

    /// <summary>差分をコピーし、飛ばした差分の件数などを InfoBar で知らせる (ANA-07 の仕様 3・「エラー」)。</summary>
    public async Task CopyDiffsAsync(CompareSessionViewModel session, MergeDirection direction, IReadOnlyCollection<long>? indices, bool all)
    {
        if (session.CopyBlockedReason(direction) is { } reason)
        {
            ShowStatusMessage(reason);
            return;
        }

        MergeOutcome? outcome = await session.CopyAsync(direction, indices, all);
        if (outcome is { Skipped: > 0 } o)
        {
            ShowNotice(Loc.Format("Compare_SkippedLengthChange", o.Skipped.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Informational);
        }

        UpdateCompareStatus();
        UpdateCommandStates();
    }

    /// <summary>選んだ差分を、左右どちらかのドキュメントのマルチ選択にする (ANA-06 の仕様 5)。</summary>
    public void DiffsToMultiSelection(CompareSessionViewModel session, bool right, IReadOnlyList<long> indices)
    {
        CompareSideViewModel side = right ? session.Right : session.Left;
        if (session.Result is not { } r || side.Owner is not { } doc || indices.Count == 0)
        {
            return;
        }

        IReadOnlyList<(long Offset, long Length)> ranges = [.. indices.Select(i => r.Diffs[i]).Select(d => (d.Start(right), d.Length(right))).Where(x => x.Item2 > 0)];
#if HEX_TEST_HOOKS
        _lastMultiSelection = (doc, ranges);
#endif
        if (ranges.Count == 1)
        {
            doc.Editor.Select(ranges[0].Offset, ranges[0].Length);
        }
        else if (ranges.Count > 1
            && doc.Editor.SetSelections(ranges.Select(x => new Core.Selection.ByteRange(x.Offset, x.Length))) == SelectionResult.Truncated)
        {
            // 要素の上限 (EDIT-07) を超えた分は選ばない。
            ShowNotice(Loc.Format("Notice_SelectionTruncated", doc.Editor.MaxSelectionElements.ToString("N0", CultureInfo.CurrentCulture)),
                InfoBarSeverity.Informational);
        }
    }

    /// <summary>選んだ差分を、左右どちらかのドキュメントのブックマークにする (ANA-06 の仕様 5)。</summary>
    public void DiffsToBookmarks(CompareSessionViewModel session, bool right, IReadOnlyList<long> indices)
    {
        CompareSideViewModel side = right ? session.Right : session.Left;
        if (session.Result is not { } r || side.Owner is not { } doc)
        {
            return;
        }

        DocumentAnnotations annotations = AnnotationsFor(doc);
        foreach (long index in indices)
        {
            DiffRange d = r.Diffs[index];
            annotations.Bookmarks.Add(d.Start(right), Math.Max(0, d.Length(right)),
                Loc.Format("Compare_BookmarkName", (index + 1).ToString(CultureInfo.CurrentCulture), CompareSessionViewModel.KindName(d.Kind)));
        }

        ShowStatusMessage(Loc.Format("Compare_BookmarksAdded", indices.Count.ToString("N0", CultureInfo.CurrentCulture)));
    }

    /// <summary>
    /// 差分の一覧を CSV / JSON / テキストのレポートに書き出す (ANA-06 の仕様 5・8)。長時間処理として扱い、書き込めなければ InfoBar で知らせる。
    /// </summary>
    public async Task ExportDiffsAsync(CompareSessionViewModel session, string format, IReadOnlyList<long>? indices)
    {
        if (session.Result is not { } r)
        {
            return;
        }

        string extension = format switch { "json" => ".json", "report" => ".txt", _ => ".csv" };
        string suggested = (format == "report" ? "compare-report" : "diffs") + extension;
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.CompareExport" };
            picker.FileTypeChoices.Add(Loc.Get(format switch { "json" => "Compare_Export_Json", "report" => "Compare_Export_Report", _ => "Compare_Export_Csv" }), [extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await ExportDiffsToAsync(session, format, indices, path);
    }

    /// <summary>書き出しの本体 (テスト用の命令からも呼ぶ)。</summary>
    internal async Task ExportDiffsToAsync(CompareSessionViewModel session, string format, IReadOnlyList<long>? indices, string path)
    {
        if (session.Result is not { } r)
        {
            return;
        }

        IEnumerable<long> rows = indices ?? ListRows(session);
        IReadOnlyList<string> summary = [.. session.Summary.Select(s => Loc.Format("Compare_Summary_Row", s.Label, s.Value))];
        Func<DiffKind, string> kindName = CompareSessionViewModel.KindName;
        long total = indices?.Count ?? session.ListCount;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("Compare_ExportOperationName"), OperationKind.WritesExternal, session, total, op =>
            {
                using FileStream stream = File.Create(path);
                switch (format)
                {
                    case "json":
                        DiffExport.WriteJson(r, rows, stream, op.CancellationToken, op.Report);
                        break;
                    case "report":
                        DiffExport.WriteReport(r, summary, kindName, stream, op.CancellationToken, op.Report);
                        break;
                    default:
                        DiffExport.WriteCsv(r, rows, stream, op.CancellationToken, op.Report);
                        break;
                }

                return Task.CompletedTask;
            });
            ShowStatusMessage(Loc.Format("Compare_Exported", Path.GetFileName(path)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Compare_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>一覧の順の差分の番号 (絞り込み・並べ替えを反映)。</summary>
    private static IEnumerable<long> ListRows(CompareSessionViewModel session)
    {
        long count = session.ListCount;
        for (long row = 0; row < count; row++)
        {
            yield return session.ListIndex(row);
        }
    }

#if HEX_TEST_HOOKS
    /// <summary>最後にマルチ選択に変換した範囲 (テスト用。マルチ選択の担当の処理がなくても、渡した範囲を確かめられるように)。</summary>
    private (DocumentViewModel Document, IReadOnlyList<(long Offset, long Length)> Ranges)? _lastMultiSelection;
#endif
}
