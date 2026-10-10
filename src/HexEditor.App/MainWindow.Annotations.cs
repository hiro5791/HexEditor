using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Files;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// フェーズ 2 の注釈のつなぎ込み: ブックマークのグループ (INSP-27)、選択範囲との相互変換 (INSP-28)、インポート / エクスポート (INSP-30)、
/// 位置マネージャとツールチップ (INSP-31)、共通の注釈レイヤーの表示 (INSP-32)、色付けルールと凡例 (INSP-33、INSP-34)。
/// </summary>
public sealed partial class MainWindow
{
    public const string PositionManagerPanelId = "positionManager";
    public const string ColoringRulesPanelId = "coloringRules";
    public const string LegendPanelId = "legend";

    /// <summary>設定「カーソルに追従する」(INSP-31 の仕様 2。既定オン)。</summary>
    public const string FollowCursorKey = "positionManager.followCursor";

    /// <summary>設定「ブックマークの説明をツールチップに出す」(INSP-31 の仕様 4。既定オン)。</summary>
    public const string RichToolTipKey = "bookmarks.richToolTip";

    /// <summary>設定: 注釈の表示 (出どころの非表示・注釈の列。INSP-32)。</summary>
    public const string AnnotationsHiddenKey = "annotations.hidden";
    public const string AnnotationsColumnKey = "annotations.column";

    private static readonly Dictionary<uint, SolidColorBrush> s_ruleBrushes = [];
    private static bool s_annotationDisplayLoaded;
    private PositionManagerViewModel? _positionVm;
    private ColoringRulesViewModel? _coloringVm;
    private LegendViewModel? _legendVm;
    private readonly HashSet<DocumentAnnotations> _coloringHooked = [];

    private PositionManagerPanel? PositionManagerView => ShownPanelContent(PositionManagerPanelId) as PositionManagerPanel;

    private LegendPanel? LegendView => ShownPanelContent(LegendPanelId) as LegendPanel;

    /// <summary>位置マネージャ・色付けルール・凡例のパネルを登録する (RegisterAnnotationPanels から呼ぶ)。</summary>
    private static void RegisterPhase2AnnotationPanels()
    {
        if (PanelRegistry.Find(PositionManagerPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(PositionManagerPanelId, "PositionManager_PanelName", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreatePositionManagerPanel()));
        }

        if (PanelRegistry.Find(ColoringRulesPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(ColoringRulesPanelId, "Coloring_PanelName", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateColoringRulesPanel()));
        }

        if (PanelRegistry.Find(LegendPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(LegendPanelId, "Legend_PanelName", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateLegendPanel()));
        }
    }

    /// <summary>InitializeAnnotations から呼ぶ。</summary>
    private void InitializeAnnotationLayers()
    {
        if (!s_annotationDisplayLoaded)
        {
            s_annotationDisplayLoaded = true;
            LoadAnnotationDisplay();
        }

        _positionVm = new PositionManagerViewModel(App.Settings);
        _coloringVm = new ColoringRulesViewModel();
        _legendVm = new LegendViewModel();
        EventHandler globalChanged = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (DocumentAnnotations a in _annotations.Values)
            {
                CompileColoring(a);
            }

            _coloringVm.Reload();
            RefreshAnnotationViews();
        });
        EventHandler displayChanged = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            SaveAnnotationDisplay();
            foreach (HexView view in _views)
            {
                view.AnnotationColumnVisible = AnnotationLayer.SharedDisplay.ShowColumn;
            }

            RefreshAnnotationViews();
        });
        GlobalColoringRules.Changed += globalChanged;
        AnnotationLayer.SharedDisplay.Changed += displayChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                GlobalColoringRules.Changed -= globalChanged;
                AnnotationLayer.SharedDisplay.Changed -= displayChanged;
            }
        };
    }

    private void RefreshAnnotationViews()
    {
        foreach (HexView view in _views)
        {
            view.RefreshHighlights();
        }

        _legendVm?.Refresh();
    }

    private static void LoadAnnotationDisplay()
    {
        AnnotationDisplay display = AnnotationLayer.SharedDisplay;

        // 出どころごとの描き方 (設定 annotations.style.*。設定の画面で変えたらすぐ反映する。INSP-32 の仕様 4)。
        display.ApplyStyleSettings(key => App.Settings.GetString(key, string.Empty));
        App.Settings.Changed += keys =>
        {
            if (keys.Any(k => k.StartsWith("annotations.style.", StringComparison.Ordinal)))
            {
                display.ApplyStyleSettings(key => App.Settings.GetString(key, string.Empty));
            }
        };

        foreach (string name in App.Settings.GetString(AnnotationsHiddenKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse(name.Trim(), ignoreCase: true, out AnnotationOrigin origin))
            {
                display.SetVisible(origin, false);
            }
        }

        display.SetShowColumn(App.Settings.GetBool(AnnotationsColumnKey, false));
    }

    private static void SaveAnnotationDisplay()
    {
        AnnotationDisplay display = AnnotationLayer.SharedDisplay;
        App.Settings.SetString(AnnotationsHiddenKey, string.Join(",", display.Hidden.Select(o => o.ToString())), string.Empty);
        App.Settings.SetBool(AnnotationsColumnKey, display.ShowColumn, false);
    }

    // ---- コマンド ----

    /// <summary>フェーズ 2 の注釈のコマンド (RegisterBookmarkCommands から呼ぶ)。</summary>
    private void RegisterAnnotationCommands()
    {
        CommandState NeedsEditor() => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument")) : CommandState.Available;
        Commands.Register("edit.selectionToBookmarks", SelectionToBookmarksAsync, () => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
            : MultiSelectionBridge.RangesOf(Editor).Count == 0 ? CommandState.Unavailable(Loc.Get("Command_NoSelection")) : CommandState.Available);
        Commands.Register("go.bookmark.toSelection", () =>
        {
            IReadOnlyList<Bookmark> selected = BookmarkListView?.SelectedBookmarks ?? [];
            return BookmarksToSelectionAsync(selected.Count > 0 ? selected : CurrentAnnotations()?.Bookmarks.Ordered ?? []);
        }, NeedsEditor);
        Commands.Register("go.bookmark.showDescription", ShowDescriptionAtCursor, NeedsEditor);
        Commands.Register("file.importBookmarks", () => ImportBookmarksAsync(null), NeedsEditor);
        Commands.Register("file.exportBookmarks", () => ExportBookmarksAsync([], null), NeedsEditor);
        Commands.Register("view.annotations.toggle", () =>
        {
            AnnotationDisplay display = AnnotationLayer.SharedDisplay;
            bool anyShown = Enum.GetValues<AnnotationOrigin>().Any(display.IsVisible);
            foreach (AnnotationOrigin origin in Enum.GetValues<AnnotationOrigin>())
            {
                display.SetVisible(origin, !anyShown);
            }
        }, () => new CommandState(true, null, Enum.GetValues<AnnotationOrigin>().Any(AnnotationLayer.SharedDisplay.IsVisible)));
        Commands.Register("view.annotations.column", () => AnnotationLayer.SharedDisplay.SetShowColumn(!AnnotationLayer.SharedDisplay.ShowColumn),
            () => new CommandState(true, null, AnnotationLayer.SharedDisplay.ShowColumn));
        foreach (AnnotationOrigin origin in Enum.GetValues<AnnotationOrigin>())
        {
            AnnotationOrigin o = origin;
            Commands.Register(AnnotationOriginCommand(o), () => AnnotationLayer.SharedDisplay.SetVisible(o, !AnnotationLayer.SharedDisplay.IsVisible(o)),
                () => new CommandState(true, null, AnnotationLayer.SharedDisplay.IsVisible(o)));
        }

        Commands.Register("view.coloring.fromSelection", ColoringRuleFromSelection, () => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
            : Editor.SelectionLength is < 1 or > 256 ? CommandState.Unavailable(Loc.Get("Coloring_FromSelectionUnavailable")) : CommandState.Available);
    }

    /// <summary>出どころの表示切り替えのコマンド ID (<c>view.annotations.yara</c> など)。</summary>
    public static string AnnotationOriginCommand(AnnotationOrigin origin) => "view.annotations." + char.ToLowerInvariant(origin.ToString()[0]) + origin.ToString()[1..];

    // ---- ブックマーク一覧のイベント (INSP-27、INSP-28、INSP-30) ----

    private void HookBookmarkListPhase2(BookmarkListPanel list)
    {
        list.GroupDeleted += (_, e) => ShowNotice(Loc.Format("Bookmarks_GroupDeleted", BookmarkGroups.LastSegment(e.Deletion.Path)), InfoBarSeverity.Informational,
            Vm.Selected, undo: new NotificationAction(Loc.Get("Common_Undo"), () => e.Owner.UndoGroupDeletion(e.Deletion)));
        list.GroupTooDeep += (_, _) => ShowNotice(Loc.Format("Bookmarks_GroupTooDeep", BookmarkGroups.MaxDepth), InfoBarSeverity.Warning, Vm.Selected);
        list.ImportRequested += (_, _) => _ = ImportBookmarksAsync(null);
        list.ExportRequested += (_, e) => _ = ExportBookmarksAsync(e.Bookmarks, e.Group);
        list.ToSelectionRequested += (_, items) => _ = BookmarksToSelectionAsync(items);
    }

    /// <summary>ブックマーク一覧の色見本 (グループの色を使っているときはその色。INSP-27 の仕様 2)。</summary>
    private void PrepareBookmarkRow(BookmarkRowViewModel row)
    {
        BookmarkCollection? owner = _bookmarksVm.OwnerOf(row.Bookmark)?.Bookmarks;
        BookmarkColor color = owner?.EffectiveColor(row.Bookmark) ?? row.Bookmark.Color;
        row.ShownColor = color;
        row.Swatch = AnnotationBrushes.Mark(color, Root, IsHighContrast);
    }

    private void PrepareGroupRow(BookmarkGroupRowViewModel row) =>
        row.Swatch = row.Group.Color is { } color ? AnnotationBrushes.Mark(color, Root, IsHighContrast) : null;

    // ---- 選択範囲との相互変換 (INSP-28) ----

    /// <summary>反映の 1 回分の件数 (UI スレッドを一度に長く止めない)。</summary>
    private const int BookmarkCommitChunk = 20_000;

    /// <summary>
    /// ブックマークの変換・インポート・エクスポートを行う (INSP-28・INSP-30 の「巨大ファイル・長時間処理」)。<paramref name="count"/> が 10 万件を
    /// 超えるときは長時間処理として進捗とキャンセルを出す: 準備 (<paramref name="prepare"/>) は別のスレッドで行い (キャンセルできる)、
    /// ブックマークへの反映 (<paramref name="commit"/>) は UI スレッドで少しずつ行う。反映を始めたら最後まで行う (一部だけを加えないため)。
    /// それ以下の件数はその場で行う。キャンセルしたら null。
    /// </summary>
    private async Task<T?> RunBookmarkWorkAsync<T>(string nameKey, object? target, long count, Func<CancellationToken, Action<long>, T> prepare,
        Func<T, IEnumerable<int>>? commit = null)
        where T : class
    {
        if (count <= BookmarkConversions.LongRunningThreshold)
        {
            T small = prepare(CancellationToken.None, _ => { });
            if (commit is not null)
            {
                foreach (int _ in commit(small))
                {
                }
            }

            return small;
        }

        long total = commit is null ? count : count * 2;
        try
        {
            return await Vm.Operations.RunAsync(Loc.Get(nameKey), OperationKind.ReadOnly, target, total, async op =>
            {
                T prepared = prepare(op.CancellationToken, n => op.Report(n));
                if (commit is not null)
                {
                    op.Report(count);
                    using IEnumerator<int> steps = commit(prepared).GetEnumerator();
                    while (await OnUiAsync(steps.MoveNext))
                    {
                        // 反映中はキャンセルを受け付けない (進捗だけ出す)。
                        if (!op.CancellationToken.IsCancellationRequested)
                        {
                            op.Report(count + steps.Current);
                        }
                    }
                }

                return prepared;
            });
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>UI スレッドで実行する。</summary>
    private Task<TResult> OnUiAsync<TResult>(Func<TResult> action)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }

    /// <summary>「選択範囲をブックマークに」: 各選択範囲を 1 件のブックマークにする。2 つ以上ならグループ「選択範囲 &lt;日時&gt;」を作る。</summary>
    private async Task SelectionToBookmarksAsync()
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor)
        {
            return;
        }

        IReadOnlyList<SelectedRange> ranges = MultiSelectionBridge.RangesOf(editor);
        if (ranges.Count == 0)
        {
            return;
        }

        DateTime now = (TestHooks.Time ?? TimeProvider.System).GetLocalNow().DateTime;
        string group = Loc.Format("Bookmarks_SelectionGroup", now.ToString("G", CultureInfo.CurrentCulture));

        // 名前は別のスレッドで作るので、書式の文字列と書式の言語は先に取る (リソースは UI スレッドで読む)。
        string pattern = Loc.Get("Bookmarks_SelectionName");
        CultureInfo culture = CultureInfo.CurrentCulture;
        string language = CultureInfo.CurrentUICulture.Name;
        BookmarkColor color = DefaultBookmarkColor;
        try
        {
            PreparedSelectionBookmarks? prepared = await RunBookmarkWorkAsync("Bookmarks_OperationConvert", a.Document.Document, ranges.Count,
                (token, progress) => BookmarkConversions.PrepareFromRanges(ranges, n => Core.Text.MessageFormat.Format(pattern, culture, language, n), group,
                    color, token, progress),
                p => p.CommitInSteps(a.Bookmarks, BookmarkCommitChunk));
            if (prepared?.Result is { Truncated: true })
            {
                ShowNotice(Loc.Format("Bookmarks_Limit", BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, Vm.Selected);
            }
        }
        catch (BookmarkLimitException)
        {
            ShowNotice(Loc.Format("Bookmarks_Limit", BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Error, Vm.Selected);
        }
    }

    /// <summary>「選択範囲にする」: ブックマークの範囲を選択する (2 件以上ならマルチ選択。長さ 0 のものは除く)。</summary>
    private async Task BookmarksToSelectionAsync(IReadOnlyList<Bookmark> bookmarks)
    {
        if (Editor is not { } editor || bookmarks.Count == 0)
        {
            return;
        }

        // ブックマークの位置は UI スレッドで写す (並べ替えなどは別のスレッドで行う)。
        SelectedRange[] positions = new SelectedRange[bookmarks.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = new SelectedRange(bookmarks[i].Start, bookmarks[i].Length);
        }

        long length = editor.Document.Length;
        BookmarkSelectionResult? result = await RunBookmarkWorkAsync("Bookmarks_OperationConvert", editor.Document, positions.Length,
            (token, progress) => BookmarkConversions.ToRanges(positions, length, token, progress));
        if (result is null || Editor != editor)
        {
            return;
        }

        if (result.Ranges.Count > 0)
        {
            MultiSelectionBridge.Select(editor, result.Ranges);
        }

        if (result.Truncated)
        {
            ShowNotice(Loc.Format("Bookmarks_SelectionLimit", BookmarkConversions.MaxRanges.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, Vm.Selected);
        }
    }

    // ---- インポート / エクスポート (INSP-30) ----

    private async Task ImportBookmarksAsync(string? path)
    {
        if (CurrentAnnotations() is null)
        {
            return;
        }

        path ??= await PickBookmarkFileAsync();
        if (path is null)
        {
            return;
        }

        // 選択肢 (追加する / 置き換える、オフセットのずれ) はファイルを選んだ後のフライアウトで選ぶ (INSP-30 の「画面」)。
        ShowImportOptions(path);
    }

    private async Task<string?> PickBookmarkFileAsync()
    {
        const string identifier = "HexEditor.BookmarksImport";
        if (TestHooks.OpenPickerResult(identifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = identifier };
        foreach (string ext in new[] { ".json", ".csv", ".tags", ".xml", ".hexproj", "*" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    private Flyout? _importFlyout;

    /// <summary>インポートの選択肢のフライアウト。</summary>
    private void ShowImportOptions(string path)
    {
        var append = new RadioButton { Content = Loc.Get("Bookmarks_ImportAppend"), IsChecked = true, GroupName = "BookmarkImportMode" };
        AutomationProperties.SetAutomationId(append, "Bookmarks_ImportAppend");
        var replace = new RadioButton { Content = Loc.Get("Bookmarks_ImportReplace"), GroupName = "BookmarkImportMode" };
        AutomationProperties.SetAutomationId(replace, "Bookmarks_ImportReplace");
        var shift = new TextBox { Header = Loc.Get("Bookmarks_ImportShift"), PlaceholderText = "+0x200", FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(shift, "Bookmarks_ImportShift");
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = Loc.Get("Bookmarks_ImportOk"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(ok, "Bookmarks_ImportOk");
        var panel = new StackPanel
        {
            Spacing = 8,
            Width = 300,
            Children = { new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis }, append, replace, shift, error, ok },
        };
        AutomationProperties.SetAutomationId(panel, "Bookmarks_ImportOptions");
        _importFlyout?.Hide();
        _importFlyout = new Flyout { Content = panel };
        ok.Click += async (_, _) =>
        {
            if (!TryParseShift(shift.Text, out long delta))
            {
                error.Text = Loc.Get("Bookmarks_ImportShiftError");
                return;
            }

            _importFlyout?.Hide();
            await ImportBookmarksNowAsync(path, replace.IsChecked == true ? BookmarkImportMode.Replace : BookmarkImportMode.Append, delta);
        };
        FrameworkElement anchor = BookmarkListView as FrameworkElement ?? (FrameworkElement?)SelectedView() ?? Root;
        _importFlyout.ShowAt(anchor);
    }

    /// <summary>オフセットのずれ (入力式。空なら 0。先頭の + / − も受け付ける)。</summary>
    private bool TryParseShift(string text, out long delta)
    {
        delta = 0;
        text = text.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        bool negative = text.StartsWith('-');
        string body = text.TrimStart('+', '-');
        IExpressionContext context = Editor is { } editor ? new EditorExpressionContext(editor) : new EditorExpressionContext(Vm.Selected!.Editor);
        if (!ExpressionEvaluator.TryEvaluate(body, context, out long value, out _))
        {
            return false;
        }

        delta = negative ? -value : value;
        return true;
    }

    /// <summary>読み込む (10 万件を超えるファイルも UI を止めないよう、解析は別のスレッドで行う)。完了後に件数を InfoBar で知らせる。</summary>
    internal async Task ImportBookmarksNowAsync(string path, BookmarkImportMode mode, long shift)
    {
        if (CurrentAnnotations() is not { } a)
        {
            return;
        }

        BookmarkFileFormat format = BookmarkExchange.FormatFromPath(path) ?? BookmarkFileFormat.Json;
        BookmarkImportData data;
        try
        {
            data = await Task.Run(() => BookmarkExchange.Parse(format, File.ReadAllBytes(path)));
        }
        catch (BookmarkFormatException ex)
        {
            string where = ex.Line is { } line
                ? ex.Position is { } pos ? Loc.Format("Bookmarks_ImportErrorAt", line, pos) : Loc.Format("Bookmarks_ImportErrorLine", line)
                : string.Empty;
            LastImportError = Loc.Format("Bookmarks_ImportError", Path.GetFileName(path), where, FormatErrorText(ex));
            ShowNotice(LastImportError, InfoBarSeverity.Error, Vm.Selected);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastImportError = Loc.Format("Bookmarks_ImportError", Path.GetFileName(path), string.Empty, ex.Message);
            ShowNotice(LastImportError, InfoBarSeverity.Error, Vm.Selected);
            return;
        }

        LastImportError = null;

        // 10 万件を超える読み込みは長時間処理 (準備は別のスレッド、反映は UI スレッドで少しずつ。INSP-30)。
        long documentLength = a.Document.Document.Length;
        int existing = mode == BookmarkImportMode.Replace ? 0 : a.Bookmarks.Count;
        PreparedBookmarkImport? prepared = await RunBookmarkWorkAsync("Bookmarks_OperationImport", a.Document.Document, data.Items.Count,
            (token, progress) => BookmarkExchange.Prepare(data, mode, shift, documentLength, existing, token, progress),
            p => p.CommitInSteps(a.Bookmarks, BookmarkCommitChunk));
        if (prepared?.Report is not { } report)
        {
            return;
        }

        string message = Loc.Format("Bookmarks_Imported", report.Imported, report.SkippedOutOfRange + report.SkippedLimit);
        if (report.NumbersMoved.Count > 0 || report.NumbersDropped > 0)
        {
            message += " " + Loc.Format("Bookmarks_ImportedNumbers", string.Join(", ", report.NumbersMoved), report.NumbersDropped);
        }

        LastImportReport = message;
        ShowNotice(message, InfoBarSeverity.Success, Vm.Selected);
    }

    /// <summary>
    /// プロジェクトファイル (<c>.hexproj</c>) を開く: ファイル本体を開き (開いていればそのタブ)、プロジェクトのブックマークで置き換え、
    /// 色付けルールとインスペクタのエンディアンを戻す。本体が見つからなければ理由を知らせて null。
    /// </summary>
    private DocumentViewModel? OpenProject(string path, int? insertAt)
    {
        HexProject project;
        try
        {
            project = HexProject.Read(path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is BookmarkFormatException or IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Bookmarks_ImportError", Path.GetFileName(path), string.Empty,
                ex is BookmarkFormatException format ? FormatErrorText(format) : ex.Message), InfoBarSeverity.Error);
            return null;
        }

        if (project.FilePath is not { } file || !File.Exists(file))
        {
            ShowNotice(Loc.Format("Project_FileMissing", Path.GetFileName(path), project.FilePath ?? string.Empty), InfoBarSeverity.Error);
            return null;
        }

        if (TryOpen(file, insertAt) is not { } doc)
        {
            return null;
        }

        DocumentAnnotations a = AnnotationsFor(doc);
        BookmarkExchange.Apply(a.Bookmarks, project.Bookmarks, BookmarkImportMode.Replace, 0, doc.Document.Length);
        a.SetColoringRules(project.ColoringRules);
        if (Enum.TryParse(project.InspectorEndian, ignoreCase: true, out Core.Inspector.InspectorEndianMode endian))
        {
            a.InspectorEndian = endian;
        }

        return doc;
    }

    /// <summary>形式の誤りの理由の文 (Core の誤りの種類を表示言語の文にする。INSP-30 の「エラー」)。</summary>
    private static string FormatErrorText(BookmarkFormatException ex) => Loc.Format("Bookmarks_FormatError_" + ex.Error, ex.Detail);

    /// <summary>最後のインポートの結果・誤りの文 (テスト用)。</summary>
    internal string? LastImportReport { get; private set; }

    internal string? LastImportError { get; private set; }

    /// <summary>
    /// エクスポート (INSP-30 の仕様 5): 選んだグループ、一覧で選んだもの、どちらもなければすべて。形式は保存先の拡張子で決める
    /// (.hexbm.json、.csv、.tags (wxHexEditor)、.hexproj)。
    /// </summary>
    private async Task ExportBookmarksAsync(IReadOnlyList<Bookmark> selected, BookmarkGroup? group, string? path = null)
    {
        if (CurrentAnnotations() is not { } a)
        {
            return;
        }

        BookmarkCollection bookmarks = a.Bookmarks;
        IReadOnlyList<Bookmark> items = group is not null ? bookmarks.InGroup(group.Path) : selected.Count > 0 ? [.. selected.OrderBy(b => b.Start)] : bookmarks.Ordered;
        string baseName = a.Document.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : "bookmarks";
        path ??= await PickBookmarkSavePathAsync(baseName + ".hexbm.json");
        if (path is null)
        {
            return;
        }

        try
        {
            BookmarkFileFormat format = BookmarkExchange.FormatFromPath(path) ?? BookmarkFileFormat.Json;
            byte[]? content;
            if (format == BookmarkFileFormat.Project)
            {
                content = HexProject.Write(path, a.Document.FilePath, bookmarks, a.ColoringRules, a.InspectorEndian == Core.Inspector.InspectorEndianMode.Document ? null
                    : a.InspectorEndian.ToString().ToLowerInvariant());
            }
            else
            {
                // 値は UI スレッドで写し、ファイルの中身は別のスレッドで作る (10 万件を超える書き出しは長時間処理。INSP-30)。
                IReadOnlyList<BookmarkExportItem> captured = BookmarkExchange.Capture(bookmarks, items);
                IReadOnlyList<BookmarkGroupRecord> groups = BookmarkExchange.GroupsOf(bookmarks, items);
                string? fileName = a.Document.FilePath is { } f ? Path.GetFileName(f) : null;
                content = items.Count <= BookmarkConversions.LongRunningThreshold
                    ? BookmarkExchange.Export(format, captured, groups, fileName)
                    : await RunBookmarkWorkAsync("Bookmarks_OperationExport", null, captured.Count,
                        (token, progress) => BookmarkExchange.Export(format, captured, groups, fileName, token, progress));
            }

            if (content is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, content);
            ShowNotice(Loc.Format("Bookmarks_Exported", items.Count, Path.GetFileName(path)), InfoBarSeverity.Success, Vm.Selected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Bookmarks_ExportError", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, Vm.Selected);
        }
    }

    private async Task<string?> PickBookmarkSavePathAsync(string suggestedName)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.BookmarksExport" };
        picker.FileTypeChoices.Add(Loc.Get("FileType_BookmarksJson"), [".json"]);
        picker.FileTypeChoices.Add(Loc.Get("FileType_Csv"), [".csv"]);
        picker.FileTypeChoices.Add(Loc.Get("FileType_WxTags"), [".tags"]);
        picker.FileTypeChoices.Add(Loc.Get("FileType_HexProject"), [".hexproj"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ---- 注釈の表示 (INSP-32) ----

    /// <summary>Hex ビューに注釈・色付けルール・注釈の列・ツールチップの説明を付ける (AttachAnnotations から呼ぶ)。</summary>
    private void AttachAnnotationLayers(HexView view)
    {
        view.SetHighlightSource("annotations", (start, end) => AnnotationHighlights(view, start, end));
        view.SetHighlightSource("coloring", (start, end) => ColoringHighlights(view, start, end));
        view.CellForegroundSource = (start, count, hex, text) => ColoringForegrounds(view, start, count, hex, text);
        view.AnnotationColumnVisible = AnnotationLayer.SharedDisplay.ShowColumn;
        view.AnnotationColumnText = (start, end) => DocumentOf(view) is { } d && _annotations.TryGetValue(d, out DocumentAnnotations? a)
            && a.Layer.RowLabel(start, end) is { } label
                ? label.More > 0 ? Loc.Format("Annotations_ColumnMore", label.Label, label.More) : label.Label
                : null;
        view.RichToolTipContent = at => AnnotationToolTip(view, at);
    }

    private IEnumerable<HexHighlight> AnnotationHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } doc || !_annotations.TryGetValue(doc, out DocumentAnnotations? a))
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        AnnotationDisplay display = a.Layer.Display;
        foreach (PlacedAnnotation p in a.Layer.QueryVisible(start, end, o => o != AnnotationOrigin.Bookmark))
        {
            AnnotationOrigin origin = p.Source.Origin;
            AnnotationStyle style = display.StyleOf(origin);
            CellLayer layer = origin == AnnotationOrigin.Template ? CellLayer.Template : CellLayer.Annotation;
            string name = origin switch
            {
                AnnotationOrigin.Yara => "Yara",
                AnnotationOrigin.SearchResults => "Search",
                AnnotationOrigin.Template => "Template",
                AnnotationOrigin.Analysis => "Analysis",
                _ => "Script",
            };
            Brush mark = p.Annotation.Rgb is { } rgb && !hc ? AnnotationBrushes.Mark(BookmarkColor.Custom(rgb), view, false)
                : AnnotationBrushes.Get($"Annotation{name}Brush", view, hc);
            string tag = $"annotation:{origin}:{p.Annotation.Label}";
            yield return style switch
            {
                // ハイコントラストでは背景を塗らず、システム色の枠線で示す。
                AnnotationStyle.Background when !hc => new HexHighlight(p.Annotation.Start, p.Annotation.Length, layer,
                    p.Annotation.Rgb is { } back ? AnnotationBrushes.Background(BookmarkColor.Custom(back), view, false) : AnnotationBrushes.Get($"Annotation{name}BackgroundBrush", view, false),
                    null, null, tag, Level: p.Level),
                AnnotationStyle.Underline => new HexHighlight(p.Annotation.Start, p.Annotation.Length, layer, null, mark, null, tag, Level: p.Level, Underline: true),
                _ => new HexHighlight(p.Annotation.Start, p.Annotation.Length, layer, null, mark, null, tag, Level: p.Level),
            };
        }
    }

    /// <summary>注釈の出どころ「すべて検索の結果」の ID。</summary>
    private const string SearchResultsSourceId = "searchResults";

    /// <summary>
    /// すべて検索の結果 (FIND-20) を注釈の出どころ「すべて検索の結果」として載せる (INSP-32 の仕様 2・6)。結果は 100 万件を超えることがあるので、
    /// 注釈の配列にはせず、表示範囲の一致だけを結果一覧に問い合わせる。表示 > 注釈 > すべて検索の結果 で表示 / 非表示を切り替える。
    /// 結果一覧はウィンドウごとなので、ドキュメントを別のウィンドウに移したら、移した先のウィンドウで登録し直す (同じ ID は置き換え)。
    /// </summary>
    private void RegisterSearchResultAnnotations(DocumentAnnotations annotations)
    {
        Core.Engine.Document document = annotations.Document.Document;
        annotations.Layer.Register(new RangeAnnotationSource(SearchResultsSourceId, AnnotationOrigin.SearchResults,
            (start, end) => !MatchHighlightEnabled || document.IsDisposed ? []
                : SearchResults.MatchesInDocument(document, document.Current, start, end - start),
            () => SearchResults.Query.Length > 0 ? SearchResults.Query : Loc.Get("Annotations_Origin_SearchResults")));
    }

    /// <summary>結果一覧の一致が変わった (検索・一覧を閉じた・設定): 注釈を描き直す。</summary>
    private void RefreshSearchResultAnnotations()
    {
        foreach (DocumentAnnotations a in _annotations.Values)
        {
            (a.Layer.Find(SearchResultsSourceId) as RangeAnnotationSource)?.RaiseChanged();
        }
    }

    /// <summary>
    /// ツールチップの注釈の部分 (INSP-31 の仕様 4、INSP-32 の仕様 5): そのバイトを含むすべての注釈の出どころ・ラベル・範囲・説明
    /// (Markdown を描いたもの。先頭 1,000 文字)。設定でブックマークの説明を出さないときはブックマーク以外だけ。
    /// </summary>
    private FrameworkElement? AnnotationToolTip(HexView view, long at)
    {
        if (DocumentOf(view) is not { } doc || !_annotations.TryGetValue(doc, out DocumentAnnotations? a))
        {
            return null;
        }

        bool bookmarks = App.Settings.GetBool(RichToolTipKey, true);
        var items = a.Layer.At(at).Where(x => bookmarks || x.Source.Origin != AnnotationOrigin.Bookmark).ToList();
        return items.Count == 0 ? null : DescriptionPanel(items, maxLength: 1000);
    }

    /// <summary>注釈の説明の一覧 (ツールチップと「カーソル位置の説明を表示」で共通)。</summary>
    private StackPanel DescriptionPanel(IReadOnlyList<(Annotation Annotation, IAnnotationSource Source)> items, int maxLength)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach ((Annotation annotation, IAnnotationSource source) in items)
        {
            string origin = Loc.Get("Annotations_Origin_" + source.Origin);
            if (source.DisplayName.Length > 0)
            {
                origin += " (" + source.DisplayName + ")";
            }

            long last = annotation.Start + Math.Max(1, annotation.Length) - 1;
            var header = new TextBlock
            {
                Text = annotation.Label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            var range = new TextBlock
            {
                Text = origin + " · " + Loc.Format("Annotations_Range", "0x" + annotation.Start.ToString("X", CultureInfo.InvariantCulture),
                    "0x" + last.ToString("X", CultureInfo.InvariantCulture), annotation.Length.ToString("N0", CultureInfo.CurrentCulture)),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            };
            var item = new StackPanel { Spacing = 2, Children = { header, range } };
            if (annotation.Description.Length > 0)
            {
                var body = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
                string text = annotation.Description.Length > maxLength ? annotation.Description[..maxLength] + "…" : annotation.Description;
                MarkdownRenderer.Render(body, text, OpenCommentLink);
                item.Children.Add(body);
            }

            panel.Children.Add(item);
        }

        return panel;
    }

    /// <summary>
    /// 「カーソル位置の説明を表示」(INSP-31 の仕様 5): カーソル位置のブックマーク・注釈の説明をフライアウトで表示する (全文をスクロールで読める)。
    /// Esc で閉じてフォーカスを Hex ビューに戻す。
    /// </summary>
    private void ShowDescriptionAtCursor()
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor || SelectedView() is not { } view)
        {
            return;
        }

        var items = a.Layer.At(editor.Cursor).ToList();
        FrameworkElement content = items.Count == 0
            ? new TextBlock { Text = Loc.Get("Annotations_NoDescription"), TextWrapping = TextWrapping.Wrap }
            : DescriptionPanel(items, int.MaxValue);
        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 360,
            Width = 420,
            IsTabStop = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetAutomationId(scroll, "Annotations_DescriptionFlyout");
        AutomationProperties.SetName(scroll, Loc.Get("Annotations_DescriptionName"));
        var flyout = new Flyout { Content = scroll, Placement = FlyoutPlacementMode.Bottom };
        // フライアウトは開いた直後に自分の Popup にフォーカスを置くことがある (遅い環境で Opened の後)。説明の領域に置けるまで、
        // 読み込み・次の描画の後にも置き直す (Esc・PageDown をすぐに受けられるように)。
        void FocusScroll()
        {
            if (!flyout.IsOpen)
            {
                return;
            }

            scroll.Focus(FocusState.Keyboard);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (flyout.IsOpen && scroll.FocusState == FocusState.Unfocused)
                {
                    scroll.Focus(FocusState.Keyboard);
                }
            });
        }

        flyout.Opened += (_, _) => FocusScroll();
        scroll.Loaded += (_, _) => FocusScroll();
        scroll.GotFocus += (_, _) => _descriptionFocused = true;
        scroll.KeyDown += (_, e) => e.Handled = HandleDescriptionKey(e.Key);
        flyout.Closed += (_, _) => view.Focus(FocusState.Keyboard);
        _descriptionFlyout = flyout;
        _descriptionFocused = false;
        if (view.TryGetCellRect(editor.Cursor, out Windows.Foundation.Rect rect, editor.ActiveColumn))
        {
            flyout.ShowAt(view, new FlyoutShowOptions { Position = new Windows.Foundation.Point(rect.X, rect.Y + rect.Height), ShowMode = FlyoutShowMode.Standard });
        }
        else
        {
            flyout.ShowAt(view);
        }
    }

    private Flyout? _descriptionFlyout;

    /// <summary>説明のフライアウトの領域にフォーカスが入った (テスト用の状態)。</summary>
    private bool _descriptionFocused;

    /// <summary>説明のフライアウトのキー: PageDown / PageUp でスクロール、Esc で閉じる (実際のキー入力とテスト用の命令の通り道から呼ぶ)。</summary>
    internal bool HandleDescriptionKey(Windows.System.VirtualKey key)
    {
        if (_descriptionFlyout?.Content is not ScrollViewer scroll)
        {
            return false;
        }

        switch (key)
        {
            case Windows.System.VirtualKey.PageDown:
            case Windows.System.VirtualKey.PageUp:
                double page = Math.Max(16, scroll.ViewportHeight - 16);
                scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + (key == Windows.System.VirtualKey.PageDown ? page : -page), 0, scroll.ScrollableHeight), null, true);
                return true;
            case Windows.System.VirtualKey.Escape:
                _descriptionFlyout.Hide();
                return true;
            default:
                return false;
        }
    }

    // ---- 色付けルール (INSP-33、INSP-34) ----

    /// <summary>ドキュメントの色付けルールを (全体のルールと合わせて) 解釈し直す。</summary>
    private void CompileColoring(DocumentAnnotations a)
    {
        if (_coloringHooked.Add(a))
        {
            a.ColoringRulesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                CompileColoring(a);
                _coloringVm?.Reload();
                RefreshAnnotationViews();
            });
            a.Coloring.Updated += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (a.Document == Vm.Selected)
                {
                    SelectedView()?.RefreshHighlights();
                    _legendVm?.Refresh();
                }
            });
            // 出どころの登録・注釈の変更 (YARA・テンプレート・スクリプトなど) で描き直す (INSP-32)。
            a.Layer.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (a.Document == Vm.Selected)
                {
                    RefreshAnnotationViews();
                }
            });
            a.Document.Editor.Changed += (_, _) =>
            {
                // sel.start などの名前を使う式のルールは、カーソル・選択範囲が変わったら解釈し直す。
                if (UsesEditorNames(a))
                {
                    CompileColoring(a);
                }
            };
        }

        ColoringRuleSet set = ColoringRuleSet.Compile(a.ColoringRules, GlobalColoringRules.Rules, new EditorExpressionContext(a.Document.Editor));
        if (!SameRules(a.Coloring.Rules, set))
        {
            a.Coloring.Rules = set;
        }
    }

    private static bool UsesEditorNames(DocumentAnnotations a) =>
        a.ColoringRules.Concat(GlobalColoringRules.Rules).Any(r => r.Enabled
            && (r.Kind == ColoringConditionKind.OffsetRange && (Mentions(r.Pattern) || Mentions(r.EndExpression)) || Mentions(r.RangeStart) || Mentions(r.RangeEnd)));

    private static bool Mentions(string text) => text.Contains("cur", StringComparison.OrdinalIgnoreCase) || text.Contains("sel", StringComparison.OrdinalIgnoreCase);

    /// <summary>同じルールを同じ値で解釈したものか (解釈し直しても結果が同じなら、キャッシュを捨てない)。</summary>
    private static bool SameRules(ColoringRuleSet a, ColoringRuleSet b) =>
        a.Rules.Count == b.Rules.Count && a.Errors.Count == b.Errors.Count
        && a.Rules.Zip(b.Rules).All(p => p.First.Rule == p.Second.Rule && p.First.Scope == p.Second.Scope && SameOffsets(p.First, p.Second));

    private static bool SameOffsets(CompiledColoringRule a, CompiledColoringRule b) => a.ResolvedOffsets == b.ResolvedOffsets;

    /// <summary>ハイコントラストでも色付けルールの色を使うか (INSP-34 の仕様 4)。</summary>
    private static bool UseRuleColors(HexView view) => !view.IsHighContrast || App.Settings.GetBool(GlobalColoringRules.HighContrastColorsKey, false);

    /// <summary>ルールの色のブラシ (同じ色は同じブラシ。描画のたびに作らない)。</summary>
    internal static SolidColorBrush RuleBrush(uint rgb)
    {
        if (!s_ruleBrushes.TryGetValue(rgb, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(AnnotationBrushes.FromRgb(rgb));
            s_ruleBrushes[rgb] = brush;
        }

        return brush;
    }

    /// <summary>枠線の形の破線の模様 (実線・二重線は null)。同じ配列を返す (描画で、変わったときだけ設定し直すため)。</summary>
    internal static IReadOnlyList<double>? RuleDash(ColoringShape shape) => shape switch
    {
        ColoringShape.Dashed => DashedPattern,
        ColoringShape.Dotted => DottedPattern,
        _ => null,
    };

    private static readonly double[] DashedPattern = [4, 2];
    private static readonly double[] DottedPattern = [1, 1.5];

    /// <summary>1 フレームの評価の結果 (背景・枠線と文字色の 2 つの問い合わせで使い回す)。</summary>
    private readonly Dictionary<HexView, (DocumentSnapshot Snapshot, long Start, int Count, int Version, ColoringCell[] Hex, ColoringCell[] Text, bool Complete)> _coloringFrames = [];

    private (ColoringCell[] Hex, ColoringCell[] Text)? ColoringCells(HexView view, long start, int count, out ColoringRuleSet rules)
    {
        rules = ColoringRuleSet.Empty;
        if (DocumentOf(view) is not { } doc || !_annotations.TryGetValue(doc, out DocumentAnnotations? a) || a.Coloring.Rules.IsEmpty || count <= 0)
        {
            return null;
        }

        rules = a.Coloring.Rules;
        DocumentSnapshot snapshot = doc.Document.Current;
        start = Math.Max(0, start);
        if (_coloringFrames.TryGetValue(view, out var frame) && ReferenceEquals(frame.Snapshot, snapshot) && frame.Start == start && frame.Count == count
            && frame.Version == rules.Version && frame.Complete)
        {
            return (frame.Hex, frame.Text);
        }

        var hex = new ColoringCell[count];
        var text = new ColoringCell[count];
        bool complete = a.Coloring.TryGetCells(snapshot, start, hex, text);
        _coloringFrames[view] = (snapshot, start, count, rules.Version, hex, text, complete);
        return (hex, text);
    }

    private IEnumerable<HexHighlight> ColoringHighlights(HexView view, long start, long end)
    {
        if (ColoringCells(view, start, (int)Math.Min(int.MaxValue, end - Math.Max(0, start)), out ColoringRuleSet rules) is not { } cells)
        {
            yield break;
        }

        start = Math.Max(0, start);
        bool colors = UseRuleColors(view);
        Brush systemBorder = AnnotationBrushes.Get("AnnotationScriptBrush", view, true);
        for (int column = 0; column < 2; column++)
        {
            ColoringCell[] list = column == 0 ? cells.Hex : cells.Text;
            int i = 0;
            while (i < list.Length)
            {
                ColoringCell cell = list[i];
                if (cell.IsEmpty)
                {
                    i++;
                    continue;
                }

                // 同じ結果のバイトをまとめて 1 つの強調にする。
                int j = i + 1;
                while (j < list.Length && list[j] == cell)
                {
                    j++;
                }

                // ハイコントラストでは色を使わず、ルールの順に枠線の形を割り当てる (INSP-34 の仕様 4)。
                int ruleIndex = cell.Background >= 0 ? cell.Background : cell.Border >= 0 ? cell.Border : cell.Foreground;
                ColoringRule rule = rules.Rules[ruleIndex].Rule;
                Brush? background = colors && cell.Background >= 0 ? RuleBrush(rules.Rules[cell.Background].Rule.Background!.Value) : null;
                Brush? border = !colors ? systemBorder
                    : cell.Border >= 0 ? RuleBrush(rules.Rules[cell.Border].Rule.Foreground ?? rules.Rules[cell.Border].Rule.Background ?? 0x808080) : null;
                ColoringShape shape = !colors ? ColoringShapes.HighContrast(ruleIndex)
                    : cell.Border >= 0 ? ColoringShapes.Of(rules.Rules[cell.Border].Rule.Border) : ColoringShape.Solid;
                // 背景は合成の図形で塗る (乱数のデータではバイトごとに強調になり、1 画面に数千になる。INSP-33 の仕様 5)。
                yield return new HexHighlight(start + i, j - i, CellLayer.ColoringRule, background, border, border is null ? null : RuleDash(shape),
                    (column == 0 ? "coloring-hex:" : "coloring-text:") + rule.Name, LightBackground: true, DoubleLine: border is not null && shape == ColoringShape.Double);
                i = j;
            }
        }
    }

    private bool ColoringForegrounds(HexView view, long start, int count, Brush?[] hex, Brush?[] text)
    {
        if (!UseRuleColors(view))
        {
            return false;
        }

        long from = Math.Max(0, start);
        int skip = (int)(from - start);
        if (ColoringCells(view, from, count - skip, out ColoringRuleSet rules) is not { } cells)
        {
            return false;
        }

        bool any = false;
        for (int i = 0; i < cells.Hex.Length; i++)
        {
            int h = cells.Hex[i].Foreground;
            if (h >= 0)
            {
                hex[i + skip] = RuleBrush(ColorOf(rules.Rules[h].Rule));
                any = true;
            }

            int t = cells.Text[i].Foreground;
            if (t >= 0)
            {
                text[i + skip] = RuleBrush(ColorOf(rules.Rules[t].Rule));
                any = true;
            }
        }

        static uint ColorOf(ColoringRule rule) => rule.Foreground!.Value;

        return any;
    }

    /// <summary>「選択範囲の値で色付けルールを作成」(INSP-33 の仕様 6): 選択範囲の内容を Hex パターンにしたルールを作り、編集画面を開く。</summary>
    private void ColoringRuleFromSelection()
    {
        if (CurrentAnnotations() is not { } a || Editor is not { } editor || editor.SelectionLength is < 1 or > 256)
        {
            return;
        }

        byte[] bytes = new byte[editor.SelectionLength];
        editor.Document.Current.Read(editor.SelectionStart, bytes);
        var rule = new ColoringRule
        {
            Name = Loc.Get("Coloring_NewRuleName"),
            Kind = ColoringConditionKind.HexPattern,
            Pattern = string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
            Background = 0xFFD700,
        };
        a.SetColoringRules([rule, .. a.ColoringRules]);
        ShowPanel(ColoringRulesPanelId);
        _coloringVm?.Show(ColoringScope.Document, rule.Id);
    }
}
