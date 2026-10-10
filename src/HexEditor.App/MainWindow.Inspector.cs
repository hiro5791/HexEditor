using System.Collections.Specialized;
using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Files;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Notifications;
using HexEditor.Core.Panels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// データインスペクタ (INSP-01〜INSP-19) とブックマーク (INSP-23〜INSP-26) のつなぎ込み。どちらもパネルの枠 (UI-05) に登録する
/// (インスペクタは右、ブックマーク一覧は左。INSP-01・INSP-26 の「画面」)。表示・配置はパネルの配置 (セッション・state.json) が持つ。
/// 状態 (<see cref="InspectorViewModel"/>、<see cref="BookmarkListViewModel"/>) はウィンドウごとに 1 つで、パネルの中身 (浮動パネルとの間を
/// 移るたびに作り直す) はそれを共有する。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>データインスペクタのパネル ID (表示切り替えのコマンドは <c>view.panel.inspector</c>)。</summary>
    public const string InspectorPanelId = "inspector";

    /// <summary>ブックマーク一覧のパネル ID (表示切り替えのコマンドは <c>view.panel.bookmarks</c>)。</summary>
    public const string BookmarksPanelId = "bookmarks";

    private readonly Dictionary<DocumentViewModel, DocumentAnnotations> _annotations = [];
    private readonly HashSet<HexView> _annotatedViews = [];
    private DocumentDataStore _documentData = null!;
    private InspectorViewModel _inspectorVm = null!;
    private BookmarkListViewModel _bookmarksVm = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _annotationSaveTimer = null!;
    private DocumentViewModel? _annotatedDocument;
    private bool _inspectorRefreshQueued;
    private Flyout? _bookmarkFlyout;
    private BookmarkEditor? _bookmarkEditor;

    /// <summary>インスペクタのパネルが表示されているか。</summary>
    public bool InspectorVisible => IsPanelShown(InspectorPanelId);

    public bool BookmarksVisible => IsPanelShown(BookmarksPanelId);

    /// <summary>表示中のインスペクタの中身 (表示されていなければ null)。</summary>
    private InspectorPanel? InspectorView => ShownPanelContent(InspectorPanelId) as InspectorPanel;

    private BookmarkListPanel? BookmarkListView => ShownPanelContent(BookmarksPanelId) as BookmarkListPanel;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterAnnotationPanels()
    {
        if (PanelRegistry.Find(InspectorPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(InspectorPanelId, "Inspector_PanelName", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateInspectorPanel()));
        }

        if (PanelRegistry.Find(BookmarksPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(BookmarksPanelId, "Bookmarks_PanelName", PanelDock.Left,
                ctx => ((MainWindow)ctx.Window).CreateBookmarkListPanel()));
        }

        RegisterPhase2AnnotationPanels();
    }

    /// <summary>コンストラクターから呼ぶ (パネルとコマンドより先)。</summary>
    private void InitializeAnnotations()
    {
        // 前回の位置 (ENG-16) と同じ付随データの置き場所を使う。
        _documentData = Vm.Files?.Documents ?? new DocumentDataStore(Program.Environment.Locations.Documents);
        _inspectorVm = new InspectorViewModel(App.Settings);
        // 「すべてのドキュメント」(INSP-26 の仕様 8) は、すべてのウィンドウで開いているドキュメント (UI-14)。
        _bookmarksVm = new BookmarkListViewModel { AllAnnotations = AllWindowsAnnotations };
        _bookmarksVm.Rows.Prepare = PrepareBookmarkRow;
        _bookmarksVm.Rows.PrepareGroup = PrepareGroupRow;
        InitializeAnnotationLayers();
#if HEX_TEST_HOOKS
        if (TestHooks.Settings.TimeZone is { Length: > 0 } zone)
        {
            InspectorViewModel.LocalTimeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        }

        if (TestHooks.Time is { } time)
        {
            InspectorViewModel.TestableTime = time;
        }
#endif
        _inspectorVm.HighlightChanged += (_, _) => SelectedView()?.RefreshHighlights();

        // 付随データの保存は、変更から 1 秒待ってまとめて書く。
        _annotationSaveTimer = DispatcherQueue.CreateTimer();
        _annotationSaveTimer.Interval = TimeSpan.FromSeconds(1);
        _annotationSaveTimer.IsRepeating = false;
        _annotationSaveTimer.Tick += (_, _) => SaveAnnotations();

        Vm.Documents.CollectionChanged += Documents_CollectionChanged;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                AttachAnnotationsToSelected();
            }
        };
        // 設定はアプリ全体のもの。ウィンドウを閉じたら外す (閉じたウィンドウのインスペクタを更新し続けないように)。
        Action<IReadOnlyCollection<string>> settingsChanged = _ => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closingConfirmed)
            {
                return;
            }

            _inspectorVm.ReloadSettings();
            GlobalColoringRules.Reload();
            InspectorView?.SyncOptions();
            SelectedView()?.RefreshHighlights();
        });
        App.Settings.Changed += settingsChanged;
        BuildNumberedBookmarkMenus();
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
                _bookmarksVm.Dispose();
                _positionVm?.Detach();
            }

            SaveAnnotations(force: true);

            // 付随データの書き出し (別のスレッド) を待ってから終わる。
            if (!DocumentAnnotations.WaitForWrites(TimeSpan.FromSeconds(30)))
            {
                AppLog.Warning("Document data: writing did not finish before closing");
            }

            App.Settings.Flush();
        };
    }

    private InspectorPanel CreateInspectorPanel() => new(_inspectorVm);

    private BookmarkListPanel CreateBookmarkListPanel()
    {
        var list = new BookmarkListPanel(_bookmarksVm);
        list.GoToRequested += (_, b) => GoToBookmark(b);
        list.OpenInNewTabRequested += (_, b) =>
        {
            if (Vm.Selected is { } doc)
            {
                OpenRangeInNewTab(doc, b.Start, b.Length, b.Name, copy: false);
            }
        };
        list.PreviewRequested += (_, b) => PreviewBookmark(b);
        list.EditRequested += (_, e) => EditBookmark(e.Bookmark, e.Anchor, e.Rename);
        list.Deleted += (_, items) => ShowBookmarksDeleted(items);
        list.AddRequested += (_, _) => AddBookmarkAtCursor();
        list.UndoRequested += (_, _) => CurrentAnnotations()?.Bookmarks.UndoDelete();
        HookBookmarkListPhase2(list);
        return list;
    }

    private bool IsHighContrast => SelectedView()?.IsHighContrast ?? new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;

    /// <summary>
    /// パネルの表示の変化をインスペクタ・一覧の状態に反映する (配置を反映するたびに呼ぶ)。表示していない間は更新しない
    /// (INSP-01 の受け入れ基準 1、INSP-26)。
    /// </summary>
    private void SyncAnnotationPanels()
    {
        if (_inspectorVm is null)
        {
            return;
        }

        bool inspector = InspectorVisible && Vm.Selected is not null;
        if (_inspectorVm.IsActive != inspector)
        {
            _inspectorVm.IsActive = inspector;
            _inspectorVm.Refresh();
            SelectedView()?.RefreshHighlights();
        }

        bool bookmarks = BookmarksVisible && Vm.Selected is not null;
        if (_bookmarksVm.IsActive != bookmarks)
        {
            _bookmarksVm.IsActive = bookmarks;
            _bookmarksVm.Rebuild();
        }

        SyncPhase2Panels();
    }

    /// <summary>ブックマークのコマンド (INSP-23、INSP-25、INSP-26。キーは 00-overview.md 8.2)。</summary>
    private void RegisterBookmarkCommands()
    {
        CommandState NeedsEditor() => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument")) : CommandState.Available;
        Commands.Register("go.bookmark.toggle", ToggleBookmark, NeedsEditor);
        Commands.Register("go.bookmark.next", () => JumpToBookmark(forward: true), NeedsEditor);
        Commands.Register("go.bookmark.previous", () => JumpToBookmark(forward: false), NeedsEditor);
        Commands.Register("go.bookmark.edit", () =>
        {
            if (BookmarkAtCursor() is { } b && SelectedView() is { } view)
            {
                EditBookmark(b, view, rename: false);
            }
        }, () => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
            : BookmarkAtCursor() is null ? CommandState.Unavailable(Loc.Get("Command_NoBookmarkAtCursor")) : CommandState.Available);
        Commands.Register("go.bookmark.list", () =>
        {
            ShowPanel(BookmarksPanelId);
            DispatcherQueue.TryEnqueue(() => BookmarkListView?.FocusList());
        }, NeedsEditor);
        for (int n = 1; n <= 9; n++)
        {
            int number = n;
            Commands.Register($"go.bookmark.set{n}", () => SetNumberedBookmark(number), NeedsEditor);
            Commands.Register($"go.bookmark.goto{n}", () => GoToNumberedBookmark(number), NeedsEditor);
        }

        // インスペクタ: エンディアンの切り替え (INSP-02 の「呼び出し」)。インスペクタのエンディアンはドキュメントごと。
        Commands.Register("inspector.toggleEndian", () =>
        {
            _inspectorVm.ToggleEndian();
            InspectorView?.SyncOptions();
        }, NeedsEditor);

        RegisterAnnotationCommands();

        // コマンドパレットの「@」(UI-17 の仕様 2): 作業中の文書のブックマーク。
        PaletteBookmarks = doc => AnnotationsFor(doc).Bookmarks.All.Select(b => new PaletteBookmark(b.Name, b.Start));
    }

    // ---- ドキュメントの付随データ ----

    /// <summary>すべてのウィンドウで開いているドキュメントの付随データ (ウィンドウの作った順、タブの順)。</summary>
    private IReadOnlyList<DocumentAnnotations> AllWindowsAnnotations()
    {
        IEnumerable<MainWindow> windows = WindowManager.Windows.Contains(this) ? WindowManager.Windows : [this];
        return [.. windows.SelectMany(w => w.Vm.Documents.Select(w.AnnotationsFor))];
    }

    private void Documents_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 「すべてのドキュメント」の一覧は他のウィンドウのドキュメントも出すので、すべてのウィンドウに知らせる。
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (MainWindow w in WindowManager.Windows.Contains(this) ? WindowManager.Windows : [this])
            {
                w._bookmarksVm.DocumentsChanged();
            }
        });
        foreach (DocumentViewModel doc in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            AnnotationsFor(doc);
        }

        foreach (DocumentViewModel doc in e.Action == NotifyCollectionChangedAction.Move ? [] : e.OldItems?.OfType<DocumentViewModel>() ?? [])
        {
            if (!_annotations.Remove(doc, out DocumentAnnotations? closed))
            {
                continue;
            }

            UnhookAnnotations(doc);
            if (Vm.IsRearranging && !doc.IsPending)
            {
                // 別のウィンドウへの移動 (UI-11): 付随データ (保存していないブックマークを含む) をそのまま移す。
                s_movingAnnotations[doc] = closed;
            }
            else
            {
                // 閉じたら付随データを書く (ファイルに保存されている内容での位置)。
                closed.SaveBookmarks();
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (DocumentViewModel doc in _annotations.Keys.Where(d => !Vm.Documents.Contains(d)).ToList())
            {
                _annotations.Remove(doc, out DocumentAnnotations? closed);
                UnhookAnnotations(doc);
                closed?.SaveBookmarks();
            }
        }
    }

    /// <summary>別のウィンドウに移している途中のタブの付随データ (外したウィンドウから、受け取るウィンドウへ)。</summary>
    private static readonly Dictionary<DocumentViewModel, DocumentAnnotations> s_movingAnnotations = new(ReferenceEqualityComparer.Instance);

    /// <summary>付随データにつないだこのウィンドウの処理を外す処理 (タブを別のウィンドウに移すとき)。</summary>
    private readonly Dictionary<DocumentViewModel, Action> _annotationHooks = new(ReferenceEqualityComparer.Instance);

    private void UnhookAnnotations(DocumentViewModel doc)
    {
        if (_annotationHooks.Remove(doc, out Action? unhook))
        {
            unhook();
        }
    }

    private DocumentAnnotations AnnotationsFor(DocumentViewModel doc)
    {
        if (_annotations.TryGetValue(doc, out DocumentAnnotations? existing))
        {
            return existing;
        }

        if (s_movingAnnotations.Remove(doc, out DocumentAnnotations? moved))
        {
            _annotations[doc] = moved;
            HookAnnotations(doc, moved);
            return moved;
        }

        var annotations = new DocumentAnnotations(doc, _documentData);
        _annotations[doc] = annotations;
        _ = LoadAnnotationsAsync(doc, annotations);
        HookAnnotations(doc, annotations);
        return annotations;
    }

    private void HookAnnotations(DocumentViewModel doc, DocumentAnnotations annotations)
    {
        EventHandler<BookmarksChangedEventArgs> bookmarksChanged = (_, e) => Bookmarks_Changed(annotations, e);
        EventHandler<DocumentChangedEventArgs> documentChanged = (_, e) =>
        {
            if (e.Kind == DocumentChangeKind.Saved)
            {
                // 保存したら、保存した内容での位置を書く (ファイルの更新日時も変わる)。
                annotations.SaveBookmarks(force: true);
            }

            if (doc == Vm.Selected)
            {
                QueueInspectorRefresh();
            }
        };
        EventHandler dataLoaded = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (doc == Vm.Selected)
            {
                QueueInspectorRefresh();
            }
        });
        EventHandler editorChanged = (_, _) =>
        {
            if (doc == Vm.Selected)
            {
                QueueInspectorRefresh();
                QueueAnnotationPanelsRefresh();
            }
        };
        annotations.Bookmarks.Changed += bookmarksChanged;
        doc.Document.Changed += documentChanged;
        doc.Document.DataLoaded += dataLoaded;
        doc.EditorChanged += editorChanged;
        _annotationHooks[doc] = () =>
        {
            annotations.Bookmarks.Changed -= bookmarksChanged;
            doc.Document.Changed -= documentChanged;
            doc.Document.DataLoaded -= dataLoaded;
            doc.EditorChanged -= editorChanged;
        };
    }

    /// <summary>付随データを (別のスレッドで) 読み、読み終わったら適用する。ファイルが変わっていれば確かめる。</summary>
    private async Task LoadAnnotationsAsync(DocumentViewModel doc, DocumentAnnotations annotations)
    {
        await annotations.LoadAsync();
        if (annotations.Pending is { } pending)
        {
            // ファイルが記録したときから変わっている: 適用する前に確かめる (00-overview 10 章)。
            ShowNotice(Loc.Format("Bookmarks_FileChanged", pending.Items.Count.ToString("N0", CultureInfo.CurrentCulture)),
                InfoBarSeverity.Warning, doc, actions:
                [
                    new NotificationAction(Loc.Get("Bookmarks_ApplyAnyway"), () =>
                    {
                        annotations.ApplyPending();
                        QueueAnnotationSave();
                    }),
                    new NotificationAction(Loc.Get("Bookmarks_Discard"), () => annotations.DiscardPending()),
                ]);
        }

        if (doc == Vm.Selected)
        {
            SelectedView()?.RefreshHighlights();
            QueueInspectorRefresh();
        }
    }

    private DocumentAnnotations? CurrentAnnotations() => Vm.Selected is { } doc ? AnnotationsFor(doc) : null;

    private void AttachAnnotationsToSelected()
    {
        DocumentViewModel? doc = Vm.Selected;
        if (doc == _annotatedDocument)
        {
            return;
        }

        _annotatedDocument = doc;
        DocumentAnnotations? annotations = doc is null ? null : AnnotationsFor(doc);
        _inspectorVm.Attach(doc, annotations);
        _bookmarksVm.Attach(annotations);
        AttachPhase2Panels(annotations);
        foreach (HexView view in _views)
        {
            AttachAnnotations(view);
        }
    }

    private void Bookmarks_Changed(DocumentAnnotations annotations, BookmarksChangedEventArgs e)
    {
        if (e.Kind != BookmarkChangeKind.Positions)
        {
            annotations.Dirty = true;
            QueueAnnotationSave();
        }

        if (annotations.Document == Vm.Selected)
        {
            SelectedView()?.RefreshHighlights();
            QueueAnnotationPanelsRefresh();
        }
    }

    private void QueueAnnotationSave()
    {
        if (!_annotationSaveTimer.IsRunning)
        {
            _annotationSaveTimer.Start();
        }
    }

    private void SaveAnnotations(bool force = false)
    {
        foreach (DocumentAnnotations a in _annotations.Values)
        {
            a.SaveBookmarks(force && a.Dirty);
        }
    }

    private void QueueInspectorRefresh()
    {
        // 起点が変わってから 50 ms 以内に更新する (INSP-01 の仕様 3)。同じフレームの中の変更はまとめる。
        if (_inspectorRefreshQueued || !_inspectorVm.IsActive)
        {
            return;
        }

        _inspectorRefreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _inspectorRefreshQueued = false;
            _inspectorVm.Refresh();
        });
    }

    // ---- Hex ビューの強調 (INSP-18、INSP-23 の仕様 8) ----

    /// <summary>Hex ビューにインスペクタの対象とブックマークの強調・目印・右クリックメニューの項目を付ける。</summary>
    private void AttachAnnotations(HexView view)
    {
        if (!_annotatedViews.Add(view))
        {
            view.RefreshHighlights();
            return;
        }

        view.SetHighlightSource("inspector", (start, end) => InspectorHighlights(view, start, end));
        view.SetHighlightSource("bookmarks", (start, end) => BookmarkHighlights(view, start, end));
        view.SetOffsetMarkerSource("bookmarks", (start, end) => BookmarkMarks(view, start, end));

        // スクロールバーの印 (VIEW-02 の仕様 9。設定でオン): その範囲から始まる最初のブックマークの色。木を引くので件数によらない。
        view.BookmarkMarkerSource = (start, end) =>
        {
            if (DocumentOf(view) is not { } d || !_annotations.TryGetValue(d, out DocumentAnnotations? a))
            {
                return null;
            }

            Bookmark? first = start <= 0 ? a.Bookmarks.First : a.Bookmarks.After(start - 1);
            return first is not null && first.Start < end ? AnnotationBrushes.Mark(first.Color, view, view.IsHighContrast) : null;
        };

        // ブックマークの名前はツールチップ (VIEW-07) と位置の読み上げ (「ブックマーク 名前」。INSP-23 の仕様 9、UI-51) に出す。
        view.AnnotationNames = at => DocumentOf(view) is { } d && _annotations.TryGetValue(d, out DocumentAnnotations? a)
            ? [.. a.Layer.At(at).Select(x => x.Annotation.Label)]
            : [];

        // ツールチップには名前とコメントの冒頭を出す (INSP-23 の仕様 8。Markdown の描画と範囲の表示は INSP-31 (フェーズ 2))。
        view.AnnotationToolTips = at => DocumentOf(view) is { } d && _annotations.TryGetValue(d, out DocumentAnnotations? a)
            ? [.. a.Bookmarks.Overlapping(at, at + 1).Where(a.Bookmarks.IsVisible).Select(BookmarkToolTip)]
            : [];
        AttachAnnotationLayers(view);
        view.SetContextMenuExtension(menu =>
        {
            ExtendHexViewEditMenu(menu);
            ExtendHexViewDataMenu(menu);
            ExtendHexViewMenu(view, menu);
            ExtendHexViewStatisticsMenu(menu);
            ExtendHexViewReferenceMenu(menu);
            ExtendHexViewFilesMenu(menu);
        });
    }

    private IEnumerable<HexHighlight> InspectorHighlights(HexView view, long start, long end)
    {
        if (view.Editor != Vm.Selected?.Editor || _inspectorVm.HighlightRange is not { } range || range.Offset >= end || range.Offset + range.Length <= start)
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        yield return new HexHighlight(range.Offset, range.Length, CellLayer.Focus,
            hc ? null : AnnotationBrushes.Get("InspectorTargetBrush", view, hc),
            AnnotationBrushes.Get("InspectorTargetBorderBrush", view, hc), null, "inspector");
    }

    private IEnumerable<HexHighlight> BookmarkHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } doc || !_annotations.TryGetValue(doc, out DocumentAnnotations? annotations))
        {
            yield break;
        }

        // ハイコントラストでは背景を塗らず、システム色の枠線と線の形で示す (INSP-23 の仕様 8、INSP-24 の仕様 4)。
        // 非表示のグループ (INSP-27 の仕様 3) と、出どころ「ブックマーク」を非表示にしたとき (INSP-32 の仕様 2) は出さない。
        // 色はグループの色を使う (色を個別に設定していなければ。INSP-27 の仕様 2)。
        if (!annotations.Layer.Display.IsVisible(Core.Annotations.AnnotationOrigin.Bookmark))
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        Core.Annotations.AnnotationStyle style = annotations.Layer.Display.StyleOf(Core.Annotations.AnnotationOrigin.Bookmark);
        BookmarkCollection bookmarks = annotations.Bookmarks;
        foreach (Bookmark b in bookmarks.Overlapping(start, end))
        {
            if (!bookmarks.IsVisible(b))
            {
                continue;
            }

            BookmarkColor color = bookmarks.EffectiveColor(b);
            yield return hc || style != Core.Annotations.AnnotationStyle.Background
                ? new HexHighlight(b.Start, b.Length, CellLayer.Bookmark, null, AnnotationBrushes.Mark(color, view, hc),
                    hc ? AnnotationBrushes.Dash(color) : null, "bookmark:" + b.Name, Underline: style == Core.Annotations.AnnotationStyle.Underline)
                : new HexHighlight(b.Start, b.Length, CellLayer.Bookmark, AnnotationBrushes.Background(color, view, false), null,
                    null, "bookmark:" + b.Name);
        }
    }

    private IEnumerable<HexOffsetMarker> BookmarkMarks(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } doc || !_annotations.TryGetValue(doc, out DocumentAnnotations? annotations))
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        if (!annotations.Layer.Display.IsVisible(Core.Annotations.AnnotationOrigin.Bookmark))
        {
            yield break;
        }

        foreach (Bookmark b in annotations.Bookmarks.Overlapping(start, end).Where(b => b.Start >= start && b.Start < end && annotations.Bookmarks.IsVisible(b)))
        {
            yield return new HexOffsetMarker(b.Start, AnnotationBrushes.Mark(annotations.Bookmarks.EffectiveColor(b), view, hc), null,
                b.Number > 0 ? b.Number.ToString(CultureInfo.InvariantCulture) : string.Empty,
                AnnotationBrushes.Get("BookmarkMarkTextBrush", view, hc), "bookmark:" + b.Name);
        }
    }

    /// <summary>コメントの冒頭として出す文字数。</summary>
    private const int CommentExcerptLength = 80;

    /// <summary>ツールチップの 1 項目: 名前と、コメントの最初の空でない行の冒頭 (長ければ「…」で切る)。</summary>
    internal static string BookmarkToolTip(Bookmark b)
    {
        string? line = b.Comment.Split(['\r', '\n']).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return b.Name;
        }

        if (line.Length > CommentExcerptLength)
        {
            line = line[..CommentExcerptLength].TrimEnd() + "…";
        }

        return Loc.Format("Bookmarks_ToolTip", b.Name, line);
    }

    private DocumentViewModel? DocumentOf(HexView view) => Vm.Documents.FirstOrDefault(d => d.Editor == view.Editor);

    // ---- Hex ビューの右クリックメニュー (INSP-23、INSP-24) ----

    private void ExtendHexViewMenu(HexView view, MenuFlyout menu)
    {
        const string ToggleId = "HexViewMenu_ToggleBookmark";
        const string EditId = "HexViewMenu_EditBookmark";
        const string HashId = "HexViewMenu_ComputeHash";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == ToggleId))
        {
            // 選択範囲の「ハッシュを計算」(ANA-18 の「呼び出し」): ハッシュパネルを開き、選択範囲を対象に計算する。
            var hash = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_ComputeHash"), Tag = "ComputeHash" };
            AutomationProperties.SetAutomationId(hash, HashId);
            hash.Click += (_, _) => _ = Commands.ExecuteAsync("analysis.hash");
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(hash);

            menu.Items.Add(new MenuFlyoutSeparator());
            var toggle = new MenuFlyoutItem { Tag = "Bookmark" };
            AutomationProperties.SetAutomationId(toggle, ToggleId);
            toggle.Click += (_, _) => _ = Commands.ExecuteAsync("go.bookmark.toggle");
            var edit = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_EditBookmark"), Tag = "EditBookmark" };
            AutomationProperties.SetAutomationId(edit, EditId);
            edit.Click += (_, _) =>
            {
                if (BookmarkAtCursor() is { } b)
                {
                    EditBookmark(b, view, rename: false);
                }
            };
            menu.Items.Add(toggle);
            menu.Items.Add(edit);
        }

        Bookmark? atCursor = BookmarkAtCursor();
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            switch (AutomationProperties.GetAutomationId(item))
            {
                case ToggleId:
                    ((MenuFlyoutItem)item).Text = Loc.Get(atCursor is null ? "HexView_Menu_SetBookmark" : "HexView_Menu_RemoveBookmark");
                    ((MenuFlyoutItem)item).KeyboardAcceleratorTextOverride = CommandService.ShortcutText("go.bookmark.toggle");
                    break;
                case EditId:
                    ((MenuFlyoutItem)item).IsEnabled = atCursor is not null;
                    break;
                case HashId:
                    ((MenuFlyoutItem)item).IsEnabled = view.Editor?.HasSelection == true;
                    ((MenuFlyoutItem)item).KeyboardAcceleratorTextOverride = CommandService.ShortcutText("analysis.hash");
                    break;
            }
        }
    }

    /// <summary>カーソル位置 (選択範囲があれば先頭) から始まるブックマーク。なければ、カーソルを含むもの。</summary>
    private Bookmark? BookmarkAtCursor()
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return null;
        }

        long at = BookmarkActions.Anchor(editor);
        return a.Bookmarks.StartingAt(at) ?? a.Bookmarks.Overlapping(editor.Cursor, editor.Cursor + 1).FirstOrDefault();
    }

    // ---- ブックマークの操作 (INSP-23、INSP-25、INSP-26) ----

    /// <summary>Ctrl+F2: カーソル位置にブックマークを付ける・外す (INSP-23 の仕様 2)。</summary>
    private void ToggleBookmark()
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return;
        }

        (BookmarkToggleOutcome outcome, Bookmark? bookmark) = BookmarkActions.Toggle(a.Bookmarks, editor, n => Loc.Format("Bookmarks_DefaultName", n),
            DefaultBookmarkColor);
        switch (outcome)
        {
            case BookmarkToggleOutcome.LimitReached:
                ShowNotice(Loc.Format("Bookmarks_Limit", BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Error, Vm.Selected);
                break;
            case BookmarkToggleOutcome.RemovedCustomized when bookmark is not null:
                ShowBookmarksDeleted([bookmark]);
                break;
        }
    }

    /// <summary>設定「ブックマークの既定の色」(INSP-23 の仕様 2。既定は色の一覧の 1 番目)。</summary>
    private static BookmarkColor DefaultBookmarkColor => BookmarkColor.FromSetting(App.Settings.GetString(BookmarkColor.DefaultColorKey, "1"));

    /// <summary>ツールバーの「追加」: その位置にブックマークがなければ付ける (外さない)。</summary>
    private void AddBookmarkAtCursor()
    {
        if (CurrentAnnotations() is { } a && Editor is { } editor && a.Bookmarks.StartingAt(BookmarkActions.Anchor(editor)) is null)
        {
            ToggleBookmark();
        }
    }

    /// <summary>削除したことと「元に戻す」を InfoBar で知らせる (INSP-23 の仕様 2、INSP-26 の仕様 6)。</summary>
    private void ShowBookmarksDeleted(IReadOnlyList<Bookmark> items)
    {
        if (CurrentAnnotations() is not { } a || items.Count == 0)
        {
            return;
        }

        string message = items.Count == 1
            ? Loc.Format("Bookmarks_Deleted", items[0].Name)
            : Loc.Format("Bookmarks_DeletedMany", items.Count.ToString("N0", CultureInfo.CurrentCulture));
        ShowNotice(message, InfoBarSeverity.Informational, Vm.Selected,
            undo: new NotificationAction(Loc.Get("Common_Undo"), () => a.Bookmarks.UndoDelete()));
    }

    /// <summary>F2 / Shift+F2 (INSP-26 の仕様 7)。末尾 / 先頭まで行ったら反対側に戻り、そのことを知らせる。</summary>
    private void JumpToBookmark(bool forward)
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return;
        }

        BookmarkJump jump = forward ? BookmarkActions.Next(a.Bookmarks, editor.Cursor) : BookmarkActions.Previous(a.Bookmarks, editor.Cursor);
        if (jump.Target is not { } target)
        {
            ShowNotice(Loc.Get("Bookmarks_None"), InfoBarSeverity.Informational, Vm.Selected);
            return;
        }

        BookmarkActions.GoTo(editor, target);
        if (jump.Wrapped)
        {
            // 折り返しはステータスバーの一時的な文で知らせる (INSP-26 の仕様 7、UI-06 の仕様 7)。
            ShowStatusMessage(Loc.Get(forward ? "Bookmarks_WrappedToStart" : "Bookmarks_WrappedToEnd"));
        }
    }

    private void GoToBookmark(Bookmark b)
    {
        // 「すべてのドキュメント」では、別のドキュメントのブックマークならそのタブに切り替えてから移動する (INSP-26 の仕様 8)。
        // 別のウィンドウのドキュメントなら、そのウィンドウを前に出してタブを切り替える (UI-14)。
        if (_bookmarksVm.OwnerOf(b)?.Document is { } owner && owner != Vm.Selected)
        {
            if (Vm.Documents.Contains(owner))
            {
                Vm.Selected = owner;
            }
            else if (WindowManager.OwnerOf(owner) is { } other)
            {
                other.Vm.Selected = owner;
                WindowManager.MarkActive(other);
                WindowManager.BringToFront(other);
                if (other.Editor is { } otherEditor)
                {
                    BookmarkActions.GoTo(otherEditor, b);
                }

                return;
            }
        }

        if (Editor is { } editor)
        {
            BookmarkActions.GoTo(editor, b);
        }
    }

    /// <summary>一覧で ↑ / ↓ で行を選んだ: カーソルは動かさずに、ブックマークの位置が見えるようにスクロールする (INSP-26 の仕様 4)。</summary>
    private void PreviewBookmark(Bookmark b)
    {
        if (Editor is { } editor && _bookmarksVm.OwnerOf(b)?.Document == Vm.Selected)
        {
            long row = editor.Layout.RowOf(Math.Min(b.Start, editor.Layout.MaxCursor));
            if (row < editor.TopRow || row >= editor.TopRow + editor.VisibleRows)
            {
                editor.ScrollToRow(Math.Max(0, row - editor.VisibleRows / 3));
            }
        }
    }

    /// <summary>
    /// 番号付きブックマークのメニュー (設定 Ctrl+Shift+1〜9、移動 Ctrl+1〜9)。項目はコマンド <c>go.bookmark.set&lt;N&gt;</c> /
    /// <c>go.bookmark.goto&lt;N&gt;</c> を参照する (キーは KeyDispatcher が受ける。テンキーの数字も同じ。00-overview.md 8.7)。
    /// </summary>
    private void BuildNumberedBookmarkMenus()
    {
        for (int n = 1; n <= 9; n++)
        {
            // アクセスキーは番号 (キーボードだけで選べるように。UI-52 の受け入れ基準 1)。
            string digit = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var set = new MenuFlyoutItem { Text = Loc.Format("Menu_Go_SetNumbered", n), AccessKey = digit };
            AutomationProperties.SetAutomationId(set, $"Command_SetNumberedBookmark{n}");
            CommandUi.SetId(set, $"go.bookmark.set{n}");
            var go = new MenuFlyoutItem { Text = Loc.Format("Menu_Go_GoNumbered", n), AccessKey = digit };
            AutomationProperties.SetAutomationId(go, $"Command_GoNumberedBookmark{n}");
            CommandUi.SetId(go, $"go.bookmark.goto{n}");
            NumberedSetMenu.Items.Add(set);
            NumberedGoMenu.Items.Add(go);
        }
    }

    private void SetNumberedBookmark(int number)
    {
        if (CurrentAnnotations() is { } a && Editor is { } editor && BookmarkActions.SetNumber(a.Bookmarks, editor, number, DefaultBookmarkColor) is null)
        {
            ShowNotice(Loc.Format("Bookmarks_Limit", BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Error, Vm.Selected);
        }
    }

    /// <summary>Ctrl+N: 番号 N のブックマークへ移動する (ジャンプ履歴に記録する。INSP-25 の仕様 2)。</summary>
    private void GoToNumberedBookmark(int number)
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return;
        }

        if (a.Bookmarks.WithNumber(number) is { } b)
        {
            editor.GoTo(Math.Min(b.Start, editor.Layout.MaxCursor));
        }
        else
        {
            // 未設定の番号はステータスバーの一時的な文で知らせる (INSP-25 の仕様 2、UI-06 の仕様 7)。
            ShowStatusMessage(Loc.Format("Bookmarks_NumberNotSet", number));
        }
    }

    // ---- 編集のフライアウト (INSP-24) ----

    private void EditBookmark(Bookmark bookmark, FrameworkElement anchor, bool rename)
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return;
        }

        // 開くたびに作る (閉じかけのフライアウトを開き直すと表示されないことがあるため)。
        _bookmarkFlyout?.Hide();
        _bookmarkEditor = new BookmarkEditor();
        _bookmarkEditor.LinkClicked += (_, url) => OpenCommentLink(url);
        _bookmarkFlyout = new Flyout { Content = _bookmarkEditor, Placement = FlyoutPlacementMode.Bottom };
        _bookmarkEditor.HighContrast = IsHighContrast;
        _bookmarkEditor.Load(a.Bookmarks, bookmark, editor);
        _bookmarkFlyout!.ShowAt(anchor);
        if (rename)
        {
            _bookmarkEditor.FocusName();
        }
    }

    /// <summary>
    /// コメントのリンク (INSP-24 の仕様 5): <c>#0x1F00</c>・<c>#bm.header</c> はドキュメントの中の位置へ移動する。http / https は確認なしで
    /// 既定のブラウザで開く。それ以外のスキームは開かない。
    /// </summary>
    private void OpenCommentLink(string url)
    {
        switch (MarkdownLite.Classify(url))
        {
            case MarkdownLite.LinkKind.Document when Editor is { } editor:
                if (ExpressionEvaluator.TryEvaluate(url[1..], new EditorExpressionContext(editor), out long target, out _)
                    && target >= 0 && target <= editor.Layout.MaxCursor)
                {
                    _bookmarkFlyout?.Hide();
                    editor.GoTo(target);
                }

                break;
            case MarkdownLite.LinkKind.Web:
                var uri = new Uri(url);
                if (!TestHooks.InterceptLaunch(uri))
                {
                    _ = Launcher.LaunchUriAsync(uri);
                }

                break;
            default:
                AppLog.Info($"Comment link not opened: {url}");
                break;
        }
    }
}
