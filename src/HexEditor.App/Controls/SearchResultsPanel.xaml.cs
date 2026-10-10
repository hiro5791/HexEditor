using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>すべて検索の対象の 1 ドキュメント (「開いているすべてのドキュメント」では複数。FIND-11 の仕様 1)。</summary>
public sealed record SearchTarget(EditorState Editor, string Name, SearchResults Results);

/// <summary>
/// 「検索結果」の一覧 (FIND-20、FIND-21)。すべて検索の結果を開始オフセットの昇順に表示し、検索の完了を待たずに操作できる。
/// 「開いているすべてのドキュメント」の検索では、ドキュメントごとにまとめて並べ、「ドキュメント」列を加える (FIND-11 の仕様 1)。
/// 行は見えている分だけ作って並べ (仮想化)、行の内容 (今の状態での位置・データ・状態) はキャッシュにあればその場で、なければ
/// バックグラウンドで読む。UI-05 のパネルの仕組み (下のパネル) に置くことを想定した部品で、それまではメインウィンドウの検索バーの下に置く。
/// </summary>
public sealed partial class SearchResultsPanel : UserControl
{
    private const double RowHeight = 24;

    /// <summary>行の内容のキャッシュの上限。</summary>
    private const int CacheLimit = 1024;

    /// <summary>状態の列の位置 (アイコンのフォントを使う)。</summary>
    private const string StatusColumn = "SearchResults_Column_Status";

    /// <summary>列 (見出しのキー、幅、等幅フォント、出す条件、並べ替えの鍵。並べ替えられない列は null)。</summary>
    private static readonly (string Key, double Width, bool Mono, ColumnKind Kind, SearchResultSortKey? Sort)[] Columns =
    [
        ("SearchResults_Column_Number", 64, false, ColumnKind.Always, SearchResultSortKey.Number),
        ("SearchResults_Column_Document", 160, false, ColumnKind.Documents, null),
        ("SearchResults_Column_Offset", 120, true, ColumnKind.Always, SearchResultSortKey.Offset),
        ("SearchResults_Column_Length", 64, false, ColumnKind.Always, SearchResultSortKey.Length),
        ("SearchResults_Column_Hex", 300, true, ColumnKind.Always, SearchResultSortKey.Hex),
        ("SearchResults_Column_Text", 200, true, ColumnKind.Always, SearchResultSortKey.Text),
        ("SearchResults_Column_Context", 300, true, ColumnKind.Always, null),
        (StatusColumn, 120, false, ColumnKind.Always, SearchResultSortKey.Status),
        (VariantColumnKey, 140, false, ColumnKind.Variant, null),
        ("SearchResults_Column_Value", 180, true, ColumnKind.Numeric, null),
        ("SearchResults_Column_Chars", 72, false, ColumnKind.Strings, null),
    ];

    /// <summary>種類の列 (エンディアン・文字コード・検索語・値の解釈) の位置の鍵。見出しは結果の種類で決める。</summary>
    private const string VariantColumnKey = "SearchResults_Column_Variant";

    // ---- 並べ替えと絞り込み (00-overview 9 章の「結果一覧」、FIND-20 の仕様 9) ----

    /// <summary>一覧の行の番号 → 結果の番号 (見つかった順)。並べ替え・絞り込みをしていなければ null (同じ番号)。</summary>
    private long[]? _view;
    private SearchResultSortKey _sortKey = SearchResultSortKey.Number;
    private bool _sortDescending;
    private string _filter = string.Empty;
    private CancellationTokenSource? _viewCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewTimer;
    private Task _viewTask = Task.CompletedTask;

    private readonly Dictionary<long, SearchResultRow> _cache = [];
    private readonly HashSet<long> _fetching = [];
    private readonly List<RowVisual> _rows = [];
    private readonly List<Group> _groups = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private Encoding _encoding = Encoding.ASCII;
    private string _kindName = string.Empty;
    private string _query = string.Empty;
    private CancellationTokenSource? _running;
    private long _top;
    private long _selected = -1;
    private long _anchor = -1;
    private int _generation;
    private bool _dirty;
    private bool _suppressScroll;

    public SearchResultsPanel()
    {
        InitializeComponent();
        AutomationProperties.SetName(ListHost, Loc.Get("SearchResults_List_Name"));
        AutomationProperties.SetName(FilterBox, Loc.Get("SearchResults_Filter_Name"));
        ActualThemeChanged += (_, _) => Render();
        AutomationProperties.SetName(PinButton, Loc.Get("SearchResults_Pin_Name"));
        ToolTipService.SetToolTip(PinButton, Loc.Get("SearchResults_Pin_Name"));
        InitializeListMenu();
        BuildHeaders();
    }

    private enum ColumnKind
    {
        Always,
        Documents,
        Variant,
        Numeric,
        Strings,
    }

    /// <summary>長時間処理の管理 (ENG-09)。</summary>
    public OperationCenter? Operations { get; set; }

    /// <summary>保存のダイアログの親ウィンドウ。</summary>
    public Microsoft.UI.WindowId WindowId { get; set; }

    /// <summary>今の結果 (複数のドキュメントのときは最初のもの。なければ null)。</summary>
    public SearchResults? Results => _groups.Count > 0 ? _groups[0].Results : null;

    /// <summary>結果を出しているビュー (複数のドキュメントのときは最初のもの)。</summary>
    public EditorState? Editor => _groups.Count > 0 ? _groups[0].Editor : null;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>F3 / Shift+F3 で一覧の次 / 前の結果に移動するか (一覧に結果があるとき。FIND-20 の仕様 11)。</summary>
    public bool HasResults => IsOpen && RowCount > 0;

    /// <summary>すべて検索が実行中か。</summary>
    public bool IsRunning => _running is not null;

    /// <summary>選んでいる行 (0 から。なければ −1)。</summary>
    public long SelectedIndex => _selected;

    /// <summary>一覧を閉じた (強調表示を消す)。</summary>
    public event EventHandler? Closed;

    /// <summary>結果を出した (パネルの枠に表示させる。UI-05)。</summary>
    public event EventHandler? Shown;

    /// <summary>一致の強調表示を更新する (結果が増えた、一覧を開いた・閉じた)。</summary>
    public event EventHandler? HighlightsChanged;

    /// <summary>通知を出す (エクスポートの失敗など)。</summary>
    public event EventHandler<(string Message, InfoBarSeverity Severity)>? NoticeRequested;

    /// <summary>別のタブのドキュメントの結果に移動する前に、そのタブに切り替える (「開いているすべてのドキュメント」)。</summary>
    public event EventHandler<EditorState>? ActivateRequested;

    /// <summary>
    /// ブックマークへの変換 (FIND-21 の仕様 3)。購読がなければ「ブックマークに」は無効。引数は各一致の範囲と名前、グループ名。
    /// メインウィンドウがこれを購読してブックマークを作る (INSP-23)。
    /// </summary>
    public event EventHandler<(EditorState Editor, IReadOnlyList<(long Offset, long Length, string Name)> Items, string Group)>? BookmarksRequested;

    /// <summary>
    /// すべて検索を始めて一覧に出す (FIND-20)。前の結果は置き換える (結果のタブの固定は UI-05 のパネルの仕組みの後)。
    /// 結果は見つかった順に一覧に加わる。キャンセルされた場合は、それまでの結果を残して見出しに「中断」と出す。
    /// </summary>
    public Task RunAsync(EditorState editor, SearchResults results, string kindName, string query, Encoding encoding,
        CancellationTokenSource cts, Action<LongRunningOperation>? started = null) =>
        RunAsync([new SearchTarget(editor, string.Empty, results)], kindName, query, encoding, cts, started);

    /// <summary>複数のドキュメントのすべて検索 (順に探し、ドキュメントごとにまとめて出す。FIND-11 の仕様 1)。</summary>
    public async Task RunAsync(IReadOnlyList<SearchTarget> targets, string kindName, string query, Encoding encoding,
        CancellationTokenSource cts, Action<LongRunningOperation>? started = null)
    {
        Attach(targets, kindName, query, encoding);
        await RunSearchAsync(cts, started, continued: false);
    }

    /// <summary>このビューの結果を出しているか (一致の強調と F3 の移動)。</summary>
    public bool Shows(EditorState? editor) => editor is not null && _groups.Any(g => g.Editor == editor);

    /// <summary>F3 / Shift+F3: 一覧の次 / 前の結果に移動する (FIND-20 の仕様 11)。移動したら true。</summary>
    public bool MoveNext(bool forward, EditorState? current)
    {
        long count = RowCount;
        if (count == 0)
        {
            return false;
        }

        long index;
        if (_selected >= 0)
        {
            index = Math.Clamp(_selected + (forward ? 1 : -1), 0, count - 1);
        }
        else if (_view is not null)
        {
            // 並べ替え・絞り込みをしているときは、一覧の先頭 (末尾) から。
            index = forward ? 0 : count - 1;
        }
        else
        {
            // まだ行を選んでいなければ、今のビューのカーソルの次 (前) の結果。
            Group g = _groups.FirstOrDefault(x => x.Editor == current) ?? _groups[0];
            long before = StartOf(g) + g.Results.CountBefore(g.Editor.Cursor);
            index = forward ? Math.Min(before, count - 1) : Math.Max(0, before - 1);
        }

        Select(index, extend: false);
        Jump(index);
        return true;
    }

    /// <summary>
    /// 表示中の範囲と重なる一致 (FIND-20 の仕様 10)。今の状態で検索語を照合し直すので、検索の後の編集にも合う。
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> MatchesInView(DocumentSnapshot snapshot, long offset, long length)
    {
        if (!IsOpen || _groups.Count == 0)
        {
            return [];
        }

        SearchResults results = _groups[0].Results;
        if (results.HighlightFromResults)
        {
            // 一致しない箇所・文字列の抽出: 結果の範囲そのものを強調する (検索の後に編集していなければ)。
            return ReferenceEquals(results.Snapshot.Tree, snapshot.Tree)
                ? [.. results.Overlapping(offset, length).Select(m => (m.Offset, m.Length))]
                : [];
        }

        return SearchEngine.FindInView(snapshot, results.Pattern, offset, length, results.Options.Scope, out _)
            .Select(m => (m.Offset, m.Length)).ToList();
    }

    /// <summary>一覧を閉じる (すべてのタブを閉じ、実行中のすべて検索も止める)。</summary>
    public void Close()
    {
        _running?.Cancel();
        Detach();
        DiscardOtherTabs();
        Visibility = Visibility.Collapsed;
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>エクスポート (FIND-21 の仕様 4)。保存先を選んで書き出す。長時間処理として進捗とキャンセルを扱う。</summary>
    public async Task ExportAsync(ExportFormat format)
    {
        if (_groups.Count == 0)
        {
            return;
        }

        string extension = format switch { ExportFormat.Csv => ".csv", ExportFormat.Text => ".txt", _ => ".json" };
        if (!TestHooks.TrySavePicker("results" + extension, out string? path))
        {
            var picker = new FileSavePicker(WindowId)
            {
                SuggestedFileName = "results" + extension,
                SettingsIdentifier = "HexEditor.ExportSearchResults",
            };
            picker.FileTypeChoices.Add(Loc.Get(format switch
            {
                ExportFormat.Csv => "SearchResults_FileType_Csv",
                ExportFormat.Text => "SearchResults_FileType_Text",
                _ => "SearchResults_FileType_Json",
            }), [extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await ExportToAsync(format, path);
    }

    /// <summary>指定したファイルに書き出す (テスト用の命令の通り道からも呼ぶ)。</summary>
    internal async Task ExportToAsync(ExportFormat format, string path)
    {
        if (_groups.Count == 0)
        {
            return;
        }

        bool multi = _groups.Count > 1;
        var groups = _groups.Select(g => (multi ? g.Name : null, new SearchResultRowFactory(g.Results, g.Editor.Document.Current, _encoding))).ToList();
        // 対象は選んだ行か、一覧のすべての行 (並べ替え・絞り込みをしていればその順と行。FIND-21 の仕様 1)。
        IReadOnlyList<long>? indices = TargetIndices();
        ExportLabels labels = Labels();
        string temp = path + ".tmp";
        try
        {
            async Task Work(LongRunningOperation? op)
            {
                await Task.Run(() =>
                {
                    using (FileStream stream = File.Create(temp))
                    {
                        SearchResultsExporter.Export(groups, stream, format, labels, indices, op, op?.CancellationToken ?? default);
                    }

                    File.Move(temp, path, overwrite: true);
                });
            }

            if (Operations is null)
            {
                await Work(null);
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_ExportResults"), OperationKind.WritesExternal, null, null, Work);
            }

            AppLog.Info($"Search results exported: {path}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            NoticeRequested?.Invoke(this, (Loc.Format("SearchResults_ExportFailed", ex.Message), InfoBarSeverity.Error));
        }
    }

    // ---- 結果の付け替え ----

    /// <summary>すべての結果の件数。</summary>
    private long TotalCount => _groups.Sum(g => g.Results.LongCount);

    /// <summary>一覧の行の数 (絞り込みをしていれば、残った行の数)。</summary>
    private long RowCount => _view?.LongLength ?? TotalCount;

    /// <summary>一覧の行の番号から結果の番号 (見つかった順) を求める。</summary>
    private long Map(long index) => _view is null ? index : index >= 0 && index < _view.LongLength ? _view[index] : -1;

    /// <summary>まとまりの最初の行の番号。</summary>
    private long StartOf(Group group)
    {
        long start = 0;
        foreach (Group g in _groups)
        {
            if (g == group)
            {
                return start;
            }

            start += g.Results.LongCount;
        }

        return start;
    }

    /// <summary>一覧の行の番号から、まとまりとその中の番号を求める。範囲外なら null。</summary>
    private (Group Group, long Local)? Locate(long index) => LocateResult(Map(index));

    /// <summary>結果の番号 (見つかった順) から、まとまりとその中の番号を求める。範囲外なら null。</summary>
    private (Group Group, long Local)? LocateResult(long index)
    {
        if (index < 0)
        {
            return null;
        }

        foreach (Group g in _groups)
        {
            long count = g.Results.LongCount;
            if (index < count)
            {
                return (g, index);
            }

            index -= count;
        }

        return null;
    }

    private void Attach(IReadOnlyList<SearchTarget> targets, string kindName, string query, Encoding encoding)
    {
        // 前の結果を置き換えるか、新しいタブに出す (FIND-20 の仕様 3)。
        PrepareTabForNewResults();
        foreach (SearchTarget t in targets)
        {
            var group = new Group(t.Editor, t.Name, t.Results);
            t.Editor.Document.Changed += Document_Changed;
            t.Results.MatchesAdded += Results_Changed;
            t.Results.StateChanged += Results_Changed;
            _groups.Add(group);
        }

        _kindName = kindName;
        _query = query;
        _encoding = encoding;
        _selected = _anchor = -1;
        _top = 0;
        _view = null;
        if (IsOrdered)
        {
            ScheduleView(immediately: true);
        }

        ToBookmarksItem.IsEnabled = BookmarksRequested is not null;
        BuildHeaders();
        Visibility = Visibility.Visible;
        Shown?.Invoke(this, EventArgs.Empty);
        UpdateTabStrip();
        Render();
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Detach()
    {
        foreach (Group g in _groups)
        {
            g.Results.MatchesAdded -= Results_Changed;
            g.Results.StateChanged -= Results_Changed;
            g.Editor.Document.Changed -= Document_Changed;
            if (_running is null)
            {
                g.Results.Dispose();
            }
        }

        _groups.Clear();
        _generation++;
        _cache.Clear();
        _fetching.Clear();
    }

    private async Task RunSearchAsync(CancellationTokenSource cts, Action<LongRunningOperation>? started, bool continued)
    {
        if (_groups.Count == 0)
        {
            return;
        }

        List<Group> groups = [.. _groups];
        _running?.Cancel();
        _running = cts;
        UpdateHeader();
        AppLog.Info($"Find all: start ({_kindName}: {_query}){(continued ? " continued" : string.Empty)}");
        try
        {
            void Work(LongRunningOperation? op)
            {
                long found = groups.Sum(g => g.Results.LongCount);
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                    op.ReportMatches(found);
                }

                CancellationToken token = op?.CancellationToken ?? cts.Token;
                foreach (Group g in groups)
                {
                    long before = found;
                    EventHandler<int>? report = op is null ? null : (_, n) => op.ReportMatches(before + n);
                    g.Results.MatchesAdded += report;
                    try
                    {
                        if (continued)
                        {
                            SearchEngine.ContinueFindAll(g.Results, op, token);
                        }
                        else
                        {
                            SearchEngine.FindAll(g.Results, op, token);
                        }
                    }
                    finally
                    {
                        g.Results.MatchesAdded -= report;
                    }

                    found += g.Results.LongCount;
                }
            }

            if (Operations is null)
            {
                await Task.Run(() => Work(null), cts.Token);
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_FindAll"), OperationKind.ReadOnly, groups[0].Editor.Document,
                    groups.Sum(g => g.Results.TotalBytes), op =>
                    {
                        started?.Invoke(op);
                        Work(op);
                        return Task.CompletedTask;
                    });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SearchAbortedException)
        {
        }
        finally
        {
            // タブを別のウィンドウに移していれば、移した先の一覧で後始末をする (UI-11 の仕様 3)。
            SearchResultsPanel owner = OwnerOfRun(cts);
            if (owner._running == cts)
            {
                owner._running = null;
            }

            owner.ForgetRun(cts);

            // キャンセルされた場合、まだ探していないドキュメントの結果は「中断」にする。
            foreach (Group g in groups.Where(g => g.Results.State == SearchResultsState.Running))
            {
                g.Results.SetCancelled();
            }

            AppLog.Info($"Find all: end ({string.Join(", ", groups.Select(g => g.Results.State).Distinct())}, {groups.Sum(g => g.Results.LongCount)} matches)");
            if (owner._groups.SequenceEqual(groups))
            {
                owner._dirty = true;
                owner.Refresh();
                owner.UpdateTabStrip();
            }
            else if (owner.HoldsInOtherTab(groups))
            {
                // 裏のタブの結果: 表示はそのタブに切り替えたときに作り直す。
                owner.UpdateTabStrip();
            }
            else
            {
                // 置き換えられた結果 (実行中だったため Detach で破棄しなかったもの) を破棄する。
                foreach (Group g in groups)
                {
                    g.Results.Dispose();
                }
            }
        }
    }

    private void Results_Changed(object? sender, EventArgs e) => QueueRefresh();

    private void Results_Changed(object? sender, int e) => QueueRefresh();

    /// <summary>結果が増えたら 100 ms ごとにまとめて表示を更新する (ストリーミング)。</summary>
    private void QueueRefresh()
    {
        _dirty = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_refreshTimer is null)
            {
                _refreshTimer = DispatcherQueue.CreateTimer();
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(100);
                _refreshTimer.IsRepeating = false;
                _refreshTimer.Tick += (_, _) => Refresh();
            }

            if (!_refreshTimer.IsRunning)
            {
                _refreshTimer.Start();
            }
        });
    }

    private void Refresh()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        if (IsOrdered)
        {
            // 結果が増えたら、並べ替え・絞り込みをし直す (新しい結果は並び順の正しい位置に入る。FIND-20 の仕様 9)。
            ScheduleView(immediately: false);
        }

        Render();
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>今の状態に合わせた行の部品 (ドキュメントが変わっていたら作り直し、行のキャッシュを捨てる)。</summary>
    private SearchResultRowFactory Factory(Group g)
    {
        if (g.Factory is null || !ReferenceEquals(g.Factory.Current.Tree, g.Editor.Document.Current.Tree))
        {
            g.Factory = new SearchResultRowFactory(g.Results, g.Editor.Document.Current, _encoding);
            _generation++;
            _cache.Clear();
            _fetching.Clear();
        }

        return g.Factory;
    }

    /// <summary>ドキュメントが変わったら、位置の補正と状態の印を作り直す (FIND-03 の仕様 3・4)。</summary>
    private void Document_Changed(object? sender, DocumentChangedEventArgs e)
    {
        // 再読み込み (ENG-18、ENG-19) の後の結果は古い結果 (FIND-03 の「エラー」)。
        bool reloaded = e.Kind == DocumentChangeKind.Reloaded;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (reloaded)
            {
                foreach (Group g in _groups.Where(g => ReferenceEquals(g.Editor.Document, sender)))
                {
                    g.Reloaded = true;
                }
            }

            if (_groups.Count > 0)
            {
                Render();
            }
        });
    }

    // ---- 表示 ----

    /// <summary>値の列を出すか (数値の検索)。</summary>
    private bool IsNumeric => _groups.Count > 0 && _groups[0].Results.Pattern.Numeric is not null;

    /// <summary>種類の列を出すか (エンディアン「両方」、複数の文字コード、複数語、範囲の解釈、文字列の抽出の文字コード)。</summary>
    private bool HasVariant => _groups.Count > 0 && _groups[0].Results is { } r
        && (r.VariantNames is not null || r.Pattern.Numeric is not null || r.Pattern.Variants.Count > 1);

    /// <summary>文字列の抽出の結果か (文字数の列を出す)。</summary>
    private bool IsStrings => _groups.Count > 0 && StringExtractor.IsStrings(_groups[0].Results);

    /// <summary>種類の列の見出し。</summary>
    private string VariantHeader => (_groups.Count > 0 ? _groups[0].Results.VariantColumnOverride ?? _groups[0].Results.Pattern.VariantColumn : VariantColumn.Endian) switch
    {
        VariantColumn.Encoding => Loc.Get("SearchResults_Column_Encoding"),
        VariantColumn.Term => Loc.Get("SearchResults_Column_Term"),
        VariantColumn.Interpretation => Loc.Get("SearchResults_Column_Interpretation"),
        _ => Loc.Get("SearchResults_Column_Endian"),
    };

    private bool ShowsDocuments => _groups.Count > 1;

    /// <summary>
    /// 列で並べ替える (同じ列をもう一度選ぶと逆順)。テスト用の命令の通り道からも呼ぶ。並べ替えはバックグラウンドで行い、
    /// 終わったら一覧を作り直す。
    /// </summary>
    internal void SortBy(SearchResultSortKey key)
    {
        _sortDescending = key == _sortKey && !_sortDescending;
        _sortKey = key;
        BuildHeaders();
        ScheduleView(immediately: true);
    }

    /// <summary>絞り込みの語 (Hex は空白を無視、テキストは大文字・小文字を区別しない)。</summary>
    internal void SetFilter(string text, bool immediately = false)
    {
        _filter = text.Trim();
        if (FilterBox.Text != text)
        {
            FilterBox.Text = text;
        }

        ScheduleView(immediately);
    }

    /// <summary>並べ替え・絞り込みをしているか。</summary>
    private bool IsOrdered => _sortKey != SearchResultSortKey.Number || _sortDescending || _filter.Length > 0;

    /// <summary>並べ替え・絞り込みの終わりを待つ (テスト用)。</summary>
    internal Task WhenViewReadyAsync() => _viewTask;

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingTab)
        {
            SetFilter(FilterBox.Text);
        }
    }

    /// <summary>一覧の並びを作り直す。<paramref name="immediately"/> でなければ 300 ms 待ってまとめる (入力中・結果が増えている間)。</summary>
    private void ScheduleView(bool immediately)
    {
        if (_viewTimer is null)
        {
            _viewTimer = DispatcherQueue.CreateTimer();
            _viewTimer.Interval = TimeSpan.FromMilliseconds(300);
            _viewTimer.IsRepeating = false;
            _viewTimer.Tick += (_, _) => RebuildView();
        }

        _viewTimer.Stop();
        if (immediately)
        {
            RebuildView();
        }
        else
        {
            _viewTimer.Start();
        }
    }

    private void RebuildView()
    {
        _viewCts?.Cancel();
        if (_groups.Count == 0 || !IsOrdered)
        {
            SetView(null);
            _viewTask = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _viewCts = cts;
        List<SearchResultRowFactory> factories = [.. _groups.Select(Factory)];
        (SearchResultSortKey key, bool descending, string filter) = (_sortKey, _sortDescending, _filter);
        var done = new TaskCompletionSource();
        _viewTask = done.Task;
        _ = Task.Run(() =>
        {
            long[]? order = null;
            bool tooMany = false;
            try
            {
                order = SearchResultsOrdering.Build(factories, key, descending, filter, cts.Token);
                tooMany = order is null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentOutOfRangeException)
            {
                AppLog.Warning($"Search results: cannot sort ({ex.GetType().Name}: {ex.Message})");
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_viewCts == cts && !cts.IsCancellationRequested)
                {
                    if (tooMany)
                    {
                        NoticeRequested?.Invoke(this, (Loc.Format("SearchResults_TooManyToSort",
                            SearchResultsOrdering.MaxResults.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning));
                    }
                    else if (order is not null)
                    {
                        SetView(order);
                    }
                }

                done.TrySetResult();
            });
        });
    }

    /// <summary>一覧の並びを付け替える (選んでいた結果は新しい並びでも選んだままにし、行のキャッシュを捨てる)。</summary>
    private void SetView(long[]? order)
    {
        if (ReferenceEquals(order, _view))
        {
            return;
        }

        long selectedResult = _selected >= 0 ? Map(_selected) : -1;
        _view = order;
        _generation++;
        _cache.Clear();
        _fetching.Clear();
        _anchor = -1;
        _selected = selectedResult < 0 ? -1 : order is null ? selectedResult : Array.IndexOf(order, selectedResult);
        if (_selected < 0)
        {
            _top = 0;
            Render();
        }
        else
        {
            // 選んでいる行が見えるようにする。
            Select(_selected, extend: false);
        }
    }

    private IEnumerable<(string Key, double Width, bool Mono)> VisibleColumns() =>
        Columns.Where(c => c.Kind == ColumnKind.Always || (c.Kind == ColumnKind.Numeric && IsNumeric) || (c.Kind == ColumnKind.Documents && ShowsDocuments)
                || (c.Kind == ColumnKind.Variant && HasVariant) || (c.Kind == ColumnKind.Strings && IsStrings))
            .Select(c => (c.Key, c.Kind == ColumnKind.Always && c.Key == "SearchResults_Column_Text" && IsStrings ? 400 : c.Width, c.Mono));

    private void BuildHeaders()
    {
        ColumnHeaders.ColumnDefinitions.Clear();
        ColumnHeaders.Children.Clear();
        int col = 0;
        foreach ((string key, double width, bool _) in VisibleColumns())
        {
            ColumnHeaders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            string text = key == VariantColumnKey ? VariantHeader
                : key == "SearchResults_Column_Text" && IsStrings ? Loc.Get("SearchResults_Column_String") : Loc.Get(key);
            FrameworkElement header;
            if (Columns.First(c => c.Key == key).Sort is { } sort)
            {
                // 押すとその列で並べ替える。もう一度押すと逆順 (00-overview 9 章の「結果一覧」)。
                string arrow = sort == _sortKey && IsOrdered ? (_sortDescending ? " \u25BC" : " \u25B2") : string.Empty;
                var button = new HyperlinkButton
                {
                    Content = new TextBlock
                    {
                        Text = text + arrow,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Padding = new Thickness(0),
                    Tag = sort,
                };
                AutomationProperties.SetAutomationId(button, "SearchResults_Sort" + sort);
                AutomationProperties.SetName(button, Loc.Format("SearchResults_SortBy", text));
                button.Click += (_, _) => SortBy(sort);
                header = button;
            }
            else
            {
                header = new TextBlock
                {
                    Text = text,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            Grid.SetColumn(header, col++);
            ColumnHeaders.Children.Add(header);
        }

        _rows.Clear();
        RowsPanel.Children.Clear();
        EnsureRows();
    }

    private int VisibleRows => Math.Max(1, (int)Math.Floor(Math.Max(0, ListHost.ActualHeight) / RowHeight));

    /// <summary>見えている行の数だけ行の部品を作る。</summary>
    private void EnsureRows()
    {
        int needed = VisibleRows;
        var columns = VisibleColumns().ToList();
        while (_rows.Count < needed)
        {
            var row = new RowVisual([.. columns.Select(c => c.Width)], [.. columns.Select(c => c.Mono)],
                columns.FindIndex(c => c.Key == StatusColumn), RowHeight);
            int index = _rows.Count;
            row.Root.PointerPressed += (_, e) => Row_PointerPressed(index, e);
            _rows.Add(row);
            RowsPanel.Children.Add(row.Root);
        }

        while (_rows.Count > needed)
        {
            RowsPanel.Children.RemoveAt(_rows.Count - 1);
            _rows.RemoveAt(_rows.Count - 1);
        }
    }

    private void Render()
    {
        UpdateHeader();
        if (_groups.Count == 0)
        {
            foreach (RowVisual row in _rows)
            {
                row.Clear();
            }

            return;
        }

        foreach (Group g in _groups)
        {
            Factory(g);
        }

        EnsureRows();
        long count = RowCount;
        int visible = _rows.Count;
        long maxTop = Math.Max(0, count - visible);
        _top = Math.Clamp(_top, 0, maxTop);
        Scroll.Maximum = maxTop;
        Scroll.ViewportSize = visible;
        Scroll.LargeChange = Math.Max(1, visible - 1);
        if (Math.Abs(Scroll.Value - _top) > 0.5)
        {
            _suppressScroll = true;
            Scroll.Value = _top;
            _suppressScroll = false;
        }

        Scroll.Visibility = count > visible ? Visibility.Visible : Visibility.Collapsed;
        Brush selectedBack = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush selectedFore = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        Brush normalFore = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        Brush transparent = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        var missing = new List<long>();
        (long lo, long hi) = SelectionRange;
        for (int i = 0; i < visible; i++)
        {
            long index = _top + i;
            RowVisual row = _rows[i];
            if (Locate(index) is not (Group g, long local))
            {
                row.Clear();
                continue;
            }

            bool selected = index >= lo && index <= hi && _selected >= 0;
            row.SetColors(selected ? selectedBack : transparent, selected ? selectedFore : normalFore);
            if (!_cache.TryGetValue(index, out SearchResultRow? data) && Factory(g).TryRowForDisplay(local, g.Results[local]) is { } quick)
            {
                // キャッシュに載っているデータなら、その場で作る (表示を 2 回に分けない)。
                data = quick;
                Remember(index, quick);
            }

            // 番号の列は結果の番号 (見つかった順) を出す (並べ替えても一致と番号の対応は変わらない)。
            if (data is not null)
            {
                row.Set(Cells(Map(index), g, data));
            }
            else
            {
                row.Set([(Map(index) + 1).ToString("N0", CultureInfo.CurrentCulture), "…"]);
                missing.Add(index);
            }
        }

        if (missing.Count > 0)
        {
            Fetch(missing);
        }

        UpdateAccessibleName();
    }

    private string[] Cells(long index, Group g, SearchResultRow r)
    {
        var cells = new List<string> { (index + 1).ToString("N0", CultureInfo.CurrentCulture) };
        if (ShowsDocuments)
        {
            cells.Add(g.Name);
        }

        cells.AddRange(
        [
            StatusFormat.Hex(r.Offset),
            r.Length.ToString("N0", CultureInfo.CurrentCulture),
            r.Hex,
            r.Text,
            r.Before + " | " + r.After,
            StatusText(r.Status),
        ]);
        if (HasVariant)
        {
            cells.Add(r.Variant ?? string.Empty);
        }

        if (IsNumeric)
        {
            cells.Add(r.Value ?? string.Empty);
        }

        if (IsStrings)
        {
            cells.Add(r.Chars?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty);
        }

        return [.. cells];
    }

    /// <summary>状態の列: 文字とアイコンで示す (色だけで示さない。FIND-03 の画面)。</summary>
    internal static string StatusText(MatchStatus status) => status switch
    {
        MatchStatus.Modified => " " + Loc.Get("SearchResults_Status_Modified"),
        MatchStatus.Deleted => " " + Loc.Get("SearchResults_Status_Deleted"),
        MatchStatus.Stale => " " + Loc.Get("SearchResults_Status_Stale"),
        _ => string.Empty,
    };

    /// <summary>行の内容をキャッシュに入れる (上限を超えたら捨てて作り直す)。</summary>
    private void Remember(long index, SearchResultRow row)
    {
        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[index] = row;
    }

    /// <summary>見えていない行の内容をバックグラウンドで読み、読み終えたら表示し直す。</summary>
    private void Fetch(List<long> indices)
    {
        var work = new List<(long Index, SearchResultRowFactory Factory, long Local)>();
        foreach (long index in indices)
        {
            if (_fetching.Add(index) && Locate(index) is (Group g, long local))
            {
                work.Add((index, Factory(g), local));
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        int generation = _generation;
        _ = Task.Run(() =>
        {
            var rows = new List<(long, SearchResultRow?)>(work.Count);
            foreach ((long index, SearchResultRowFactory factory, long local) in work)
            {
                try
                {
                    rows.Add((index, factory.Row(local)));
                }
                catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or ObjectDisposedException)
                {
                    rows.Add((index, null));
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                foreach ((long index, SearchResultRow? row) in rows)
                {
                    _fetching.Remove(index);
                    if (row is not null)
                    {
                        Remember(index, row);
                    }
                }

                Render();
            });
        });
    }

    /// <summary>見出し: 「Hex: AB CD — 100 件 (完了)」。上限で止めたら「続ける」を出す (FIND-20 の画面、仕様 6)。</summary>
    private void UpdateHeader()
    {
        if (_groups.Count == 0)
        {
            Summary.Text = string.Empty;
            ContinueButton.Visibility = CancelButton.Visibility = ResearchButton.Visibility = SkippedButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool running = _running is not null;
        bool stale = !running && _groups.Any(g => g.Reloaded || (!g.Editor.Document.IsDisposed && g.Results.IsStale(g.Editor.Document.Current)));
        SearchResults? limited = _groups.Select(g => g.Results).FirstOrDefault(r => r.State == SearchResultsState.LimitReached);
        string state;
        if (running)
        {
            state = Loc.Get("SearchResults_State_Running");
        }
        else if (stale)
        {
            // 元ファイルを読み直した後の結果は「古い結果」(FIND-03 の「エラー」)。
            state = Loc.Get("SearchResults_State_Stale");
        }
        else if (_groups.Any(g => g.Results.State == SearchResultsState.Cancelled))
        {
            state = Loc.Get("SearchResults_State_Cancelled");
        }
        else if (limited is not null)
        {
            state = limited.SpillFailed
                ? Loc.Format("SearchResults_State_SpillFailed", limited.SpillError ?? string.Empty)
                : Loc.Format("SearchResults_State_Limit", limited.Limit.ToString("N0", CultureInfo.CurrentCulture));
        }
        else if (_groups.Any(g => g.Results.State == SearchResultsState.Failed))
        {
            state = Loc.Get("SearchResults_State_Failed");
        }
        else if (_groups.All(g => g.Results.State == SearchResultsState.Completed))
        {
            state = Loc.Get("SearchResults_State_Completed");
        }
        else
        {
            state = Loc.Get("SearchResults_State_Running");
        }

        Summary.Text = Loc.Format("SearchResults_Summary", _kindName, _query, TotalCount, state);
        ContinueButton.Visibility = !running && _groups.Count == 1 && limited is { SpillFailed: false } ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ResearchButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        PinButton.IsChecked = IsPinned;
        UpdateSkipped();
        UpdateVariantCounts(running);
    }

    private void UpdateAccessibleName()
    {
        if (_selected >= 0 && _cache.TryGetValue(_selected, out SearchResultRow? row))
        {
            AutomationProperties.SetName(ListHost, Loc.Format("SearchResults_List_Selected",
                (_selected + 1).ToString("N0", CultureInfo.CurrentCulture), RowCount.ToString("N0", CultureInfo.CurrentCulture),
                StatusFormat.Hex(row.Offset), row.Hex));
        }
        else
        {
            AutomationProperties.SetName(ListHost, Loc.Get("SearchResults_List_Name"));
        }
    }

    // ---- 選択と移動 ----

    private (long Lo, long Hi) SelectionRange =>
        _selected < 0 ? (-1, -2) : (Math.Min(_anchor < 0 ? _selected : _anchor, _selected), Math.Max(_anchor < 0 ? _selected : _anchor, _selected));

    /// <summary>選んでいる行の数。</summary>
    internal long SelectionCount => _selected < 0 ? 0 : SelectionRange.Hi - SelectionRange.Lo + 1;

    private IReadOnlyList<long> SelectedIndices()
    {
        (long lo, long hi) = SelectionRange;
        var list = new List<long>();
        for (long i = lo; i <= hi && list.Count < SearchResultsConversion.MaxSelectionRanges; i++)
        {
            list.Add(i);
        }

        return list;
    }

    /// <summary>行を選ぶ。<paramref name="extend"/> なら起点から範囲を選ぶ (Shift)。見えるようにスクロールする。</summary>
    internal void Select(long index, bool extend)
    {
        long count = RowCount;
        if (count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, count - 1);
        if (!extend || _anchor < 0)
        {
            _anchor = index;
        }

        _selected = index;
        int visible = Math.Max(1, _rows.Count);
        if (index < _top)
        {
            _top = index;
        }
        else if (index >= _top + visible)
        {
            _top = index - visible + 1;
        }

        Render();
    }

    /// <summary>
    /// その一致に移動して選択する (FIND-20 の仕様 8)。削除済みの一致は削除された位置に移動する (FIND-03 の仕様 3)。
    /// 別のタブのドキュメントなら、先にそのタブに切り替える。
    /// </summary>
    internal void Jump(long index)
    {
        if (Locate(index) is not (Group g, long local))
        {
            return;
        }

        ActivateRequested?.Invoke(this, g.Editor);
        TrackedMatch t = Factory(g).Track(g.Results[local]);
        if (t.Status == MatchStatus.Deleted || t.Length == 0)
        {
            g.Editor.GoTo(t.Offset);
        }
        else
        {
            g.Editor.SelectMatch(t.Offset, t.Length);
        }
    }

    /// <summary>↑ / ↓ で行を移したときのプレビュー: エディタをその位置にスクロールする (カーソルは動かさない。FIND-20 の仕様 8)。</summary>
    private void Preview(long index)
    {
        if (Locate(index) is not (Group g, long local))
        {
            return;
        }

        TrackedMatch t = Factory(g).Track(g.Results[local]);
        EditorState editor = g.Editor;
        long row = editor.Layout.RowOf(t.Offset);
        if (row < editor.TopRow || row >= editor.TopRow + editor.VisibleRows)
        {
            editor.ScrollToRow(Math.Max(0, row - (editor.VisibleRows / 2)));
        }
    }

    /// <summary>一覧のキー (↑ / ↓ / PageUp / PageDown / Home / End、Shift で範囲、Enter で移動)。テスト用の命令からも呼ぶ。</summary>
    internal bool HandleKey(VirtualKey key, bool shift, bool ctrl)
    {
        long count = RowCount;
        if (count == 0)
        {
            return false;
        }

        int page = Math.Max(1, _rows.Count - 1);
        long current = _selected < 0 ? -1 : _selected;
        long? target = key switch
        {
            VirtualKey.Down => current + 1,
            VirtualKey.Up => Math.Max(0, current - 1),
            VirtualKey.PageDown => Math.Max(0, current) + page,
            VirtualKey.PageUp => Math.Max(0, current) - page,
            VirtualKey.Home => 0,
            VirtualKey.End => count - 1,
            _ => null,
        };
        if (target is long t)
        {
            Select(t, shift);
            if (key is VirtualKey.Up or VirtualKey.Down && App.Settings?.GetBool(PreviewKey, true) != false)
            {
                Preview(_selected);
            }

            return true;
        }

        if (key == VirtualKey.Enter && _selected >= 0)
        {
            Jump(_selected);
            return true;
        }

        return false;
    }

    /// <summary>↑ / ↓ のプレビューを有効にする設定 (FIND-20 の仕様 8)。</summary>
    public const string PreviewKey = "search.results.preview";

    private void ListHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (HandleKey(e.Key, shift, ctrl))
        {
            e.Handled = true;
        }
    }

    private void Row_PointerPressed(int visualIndex, PointerRoutedEventArgs e)
    {
        long index = _top + visualIndex;
        if (index >= RowCount)
        {
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        ListHost.Focus(FocusState.Pointer);
        if (e.GetCurrentPoint(ListHost).Properties.IsRightButtonPressed)
        {
            // 右クリック: 選んだ行の中ならそのまま、外ならその行を選ぶ (移動はしない)。メニューは ContextFlyout が開く。
            (long lo, long hi) = SelectionRange;
            if (_selected < 0 || index < lo || index > hi)
            {
                Select(index, extend: false);
            }

            return;
        }

        Click(index, shift);
        e.Handled = true;
    }

    /// <summary>行のクリック: 選んで移動する (Shift なら範囲を選ぶだけ)。テスト用の命令からも呼ぶ。</summary>
    internal void Click(long index, bool shift)
    {
        Select(index, shift);
        if (!shift)
        {
            Jump(index);
        }
    }

    private void ListHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(ListHost).Properties.MouseWheelDelta;
        _top -= delta / 120 * 3;
        Render();
        e.Handled = true;
    }

    private void Scroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressScroll)
        {
            return;
        }

        _top = (long)Math.Round(e.NewValue);
        Render();
    }

    /// <summary>テスト用: スクロールバーのつまみの位置 (0〜1) に表示位置を合わせる (つまみのドラッグと同じ)。</summary>
    internal void ScrollToFraction(double fraction)
    {
        Scroll.Value = Math.Clamp(fraction, 0, 1) * Scroll.Maximum;
    }

    /// <summary>テスト用: 先頭に表示している行。</summary>
    internal long TopIndex => _top;

    /// <summary>テスト用: 見えている行の数。</summary>
    internal int VisibleRowCount => _rows.Count;

    /// <summary>テスト用: すべての結果の件数。</summary>
    internal long Count => TotalCount;

    /// <summary>テスト用: 一覧の行の数 (絞り込みの後)。</summary>
    internal long ViewCount => RowCount;

    /// <summary>テスト用: 一覧の行の、結果の番号 (1 から。見つかった順)。</summary>
    internal long ResultNumberAt(long index) => Map(index) + 1;

    /// <summary>テスト用: 行の内容とドキュメントの名前 (今の状態で作り直す)。</summary>
    internal (SearchResultRow Row, string Document)? RowAt(long index) =>
        Locate(index) is (Group g, long local)
            ? (new SearchResultRowFactory(g.Results, g.Editor.Document.Current, _encoding).Row(local), g.Name)
            : null;

    /// <summary>テスト用: [from, from + count) の行の開始オフセット (検索したときの位置)。</summary>
    internal IReadOnlyList<long> OffsetsAt(long from, int count)
    {
        var result = new List<long>(Math.Max(0, count));
        long index = from;
        while (result.Count < count && Locate(index) is (Group g, long local))
        {
            // 並べ替え・絞り込みをしていれば 1 行ずつ、していなければ続く行をまとめて読む。
            int n = _view is not null ? 1 : (int)Math.Min(count - result.Count, g.Results.LongCount - local);
            result.AddRange(g.Results.GetRange(local, n).Select(m => m.Offset));
            index += n;
        }

        return result;
    }

    /// <summary>テスト用: 見出しの文字列。</summary>
    internal string SummaryText => Summary.Text;

    /// <summary>テスト用: 「続ける」を出しているか。</summary>
    internal bool CanContinue => ContinueButton.Visibility == Visibility.Visible;

    /// <summary>テスト用: 全体の状態 (実行中・キャンセル・上限・完了)。</summary>
    internal string? StateText => _groups.Count == 0 ? null
        : _groups.Any(g => g.Results.State == SearchResultsState.Cancelled) ? nameof(SearchResultsState.Cancelled)
        : _groups.Any(g => g.Results.State == SearchResultsState.LimitReached) ? nameof(SearchResultsState.LimitReached)
        : _groups.Any(g => g.Results.State == SearchResultsState.Running) ? nameof(SearchResultsState.Running)
        : _groups.Any(g => g.Results.State == SearchResultsState.Failed) ? nameof(SearchResultsState.Failed)
        : nameof(SearchResultsState.Completed);

    /// <summary>「続ける」: 上限を 2 倍にして続きから探す (FIND-20 の仕様 6)。</summary>
    internal async Task ContinueAsync()
    {
        if (_groups.Count != 1 || _running is not null)
        {
            return;
        }

        await RunSearchAsync(new CancellationTokenSource(), null, continued: true);
    }

    /// <summary>実行中のすべて検索を止める。</summary>
    public void CancelRunning() => _running?.Cancel();

    private void ListHost_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void ListHost_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_selected < 0 && RowCount > 0)
        {
            Select(0, extend: false);
        }
    }

    private void ListHost_LostFocus(object sender, RoutedEventArgs e)
    {
    }

    private async void Continue_Click(object sender, RoutedEventArgs e) => await ContinueAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRunning();

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Csv);

    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Json);

    private void CopyHex_Click(object sender, RoutedEventArgs e) => CopyRows(hex: true);

    private void CopyText_Click(object sender, RoutedEventArgs e) => CopyRows(hex: false);

    /// <summary>選んだ行の一致したデータを Hex 文字列 (1 行 1 件) またはテキストでコピーする (FIND-21 の仕様 5)。</summary>
    internal void CopyRows(bool hex)
    {
        if (SelectionCount == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (long index in SelectedIndices())
        {
            if (Locate(index) is (Group g, long local))
            {
                SearchResultRow row = Factory(g).Row(local);
                sb.Append(hex ? row.Hex : row.Text).Append("\r\n");
            }
        }

        var package = new DataPackage();
        package.SetText(sb.ToString());
        SystemClipboard.SetContent(package);
    }

    /// <summary>ブックマークに変換 (FIND-21 の仕様 3)。</summary>
    private async void ToBookmarks_Click(object sender, RoutedEventArgs e) => await ToBookmarksAsync();

    /// <summary>今の時刻 (グループの名前に使う。テストでは固定した時刻)。</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    private static ExportLabels Labels() => new()
    {
        Number = Loc.Get("SearchResults_Column_Number"),
        Document = Loc.Get("SearchResults_Column_Document"),
        Offset = Loc.Get("SearchResults_Export_Offset"),
        OffsetHex = Loc.Get("SearchResults_Export_OffsetHex"),
        Length = Loc.Get("SearchResults_Column_Length"),
        Hex = Loc.Get("SearchResults_Column_Hex"),
        Text = Loc.Get("SearchResults_Column_Text"),
        Before = Loc.Get("SearchResults_Export_Before"),
        After = Loc.Get("SearchResults_Export_After"),
        Status = Loc.Get("SearchResults_Column_Status"),
        Endian = Loc.Get("SearchResults_Column_Endian"),
        Value = Loc.Get("SearchResults_Column_Value"),
        StatusText = s => s switch
        {
            MatchStatus.Modified => Loc.Get("SearchResults_Status_Modified"),
            MatchStatus.Deleted => Loc.Get("SearchResults_Status_Deleted"),
            MatchStatus.Stale => Loc.Get("SearchResults_Status_Stale"),
            _ => string.Empty,
        },
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>1 つのドキュメントの結果のまとまり。</summary>
    private sealed class Group(EditorState editor, string name, SearchResults results)
    {
        public EditorState Editor { get; } = editor;

        public string Name { get; } = name;

        public SearchResults Results { get; } = results;

        public SearchResultRowFactory? Factory { get; set; }

        /// <summary>検索の後に元データを読み直した (古い結果)。</summary>
        public bool Reloaded { get; set; }
    }

    /// <summary>一覧の 1 行の部品 (列ごとの TextBlock)。</summary>
    private sealed class RowVisual
    {
        private readonly TextBlock[] _cells;
        private Brush? _background;
        private Brush? _foreground;

        public RowVisual(double[] widths, bool[] mono, int statusColumn, double height)
        {
            Root = new Grid { Height = height };
            _cells = new TextBlock[widths.Length];
            for (int i = 0; i < widths.Length; i++)
            {
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
                var cell = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Padding = new Thickness(0, 0, 8, 0),
                };
                if (mono[i])
                {
                    cell.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    cell.FlowDirection = FlowDirection.LeftToRight;
                }

                if (i == statusColumn)
                {
                    // 状態の列はアイコン (Segoe Fluent Icons) と文字。
                    cell.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI");
                }

                Grid.SetColumn(cell, i);
                Root.Children.Add(cell);
                _cells[i] = cell;
            }
        }

        public Grid Root { get; }

        public void Set(IReadOnlyList<string> values)
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                string text = i < values.Count ? values[i] : string.Empty;
                if (_cells[i].Text != text)
                {
                    _cells[i].Text = text;
                }
            }
        }

        /// <summary>背景と文字の色 (変わったときだけ設定する。描画の手間を減らす)。</summary>
        public void SetColors(Brush background, Brush foreground)
        {
            if (!ReferenceEquals(_background, background))
            {
                _background = background;
                Root.Background = background;
            }

            if (!ReferenceEquals(_foreground, foreground))
            {
                _foreground = foreground;
                foreach (TextBlock cell in _cells)
                {
                    cell.Foreground = foreground;
                }
            }
        }

        public void Clear()
        {
            _background = null;
            Root.Background = null;
            Set([]);
        }
    }
}
