using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索バー (FIND-04〜FIND-14、FIND-20、FIND-22〜FIND-24、FIND-27、FIND-28)。検索はバックグラウンドで実行し、UI は止めない。
/// 状態 (種類・検索語・オプション・直前の一致) はウィンドウごとに持ち、タブを切り替えても変わらない (FIND-04 の仕様 10)。
/// 置換は FindBar.Replace.cs、検索履歴は FindBar.History.cs、インクリメンタルサーチは FindBar.Incremental.cs。
/// </summary>
public sealed partial class FindBar : UserControl
{
    /// <summary>件数の表示の上限 (FIND-12 の仕様 3)。</summary>
    private const long CountLimit = SearchEngine.CountLimit;

    /// <summary>「一致の最大長」の設定 (FIND-01 の仕様 3)。</summary>
    public const string MaxMatchLengthKey = "search.maxMatchLength";

    /// <summary>すべて検索の件数の上限の設定 (FIND-20 の仕様 6。1,000〜100,000,000)。</summary>
    public const string FindAllLimitKey = "search.findAll.limit";

    private readonly FindNavigator _navigator = new();
    private CancellationTokenSource? _running;
    private CancellationTokenSource? _counting;
    private SearchPattern? _pattern;
    private SearchScope _scope = SearchScope.WholeDocument;
    private SearchScope? _rangeScope;
    private SearchResults? _count;
    private bool _kindChosen;

    /// <summary>実行中の検索・数え上げの長時間処理 (進捗バーの表示に使う)。</summary>
    private volatile LongRunningOperation? _activeOperation;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;

    public FindBar()
    {
        InitializeComponent();
        foreach (TextEncodingId id in TextEncodings.All)
        {
            EncodingChoice.Items.Add(new ComboBoxItem { Content = EncodingName(id), Tag = id });
        }

        foreach (int bits in NumericSearch.IntegerSizes)
        {
            IntBitsChoice.Items.Add(new ComboBoxItem { Content = Loc.Format("Find_IntBits_Item", bits), Tag = bits });
        }

        IntBitsChoice.SelectedIndex = NumericSearch.IntegerSizes.ToList().IndexOf(32);
        EncodingChoice.SelectedIndex = 0;
        AutomationProperties.SetName(KindChoice, Loc.Get("Find_Kind_Name"));
        AutomationProperties.SetName(EncodingChoice, Loc.Get("Find_Encoding_Name"));
        AutomationProperties.SetName(DirectionChoice, Loc.Get("Find_Direction_Name"));
        AutomationProperties.SetName(ScopeChoice, Loc.Get("Find_Scope_Name"));
        AutomationProperties.SetName(IntBitsChoice, Loc.Get("Find_IntBits_Name"));
        AutomationProperties.SetName(SignChoice, Loc.Get("Find_Sign_Name"));
        AutomationProperties.SetName(EndianChoice, Loc.Get("Find_Endian_Name"));
        AutomationProperties.SetName(FloatChoice, Loc.Get("Find_FloatFormat_Name"));
        AutomationProperties.SetName(ToleranceChoice, Loc.Get("Find_Tolerance_Name"));
        AutomationProperties.SetName(LengthPolicyChoice, Loc.Get("Find_LengthPolicy_Name"));
        AutomationProperties.SetName(FillerChoice, Loc.Get("Find_Filler_Name"));
        AutomationProperties.SetName(PreviousButton, Loc.Get("Find_Previous_Name"));
        AutomationProperties.SetName(NextButton, Loc.Get("Find_Next_Name"));
        AutomationProperties.SetName(OptionsToggle, Loc.Get("Find_Options_Name"));
        AutomationProperties.SetName(CloseButton, Loc.Get("Find_Close_Name"));
        AutomationProperties.SetName(HistoryButton, Loc.Get("Find_History_Name"));
        ToolTipService.SetToolTip(PreviousButton, Loc.Get("Find_Previous_Name") + " (Shift+F3)");
        ToolTipService.SetToolTip(NextButton, Loc.Get("Find_Next_Name") + " (F3)");
        ToolTipService.SetToolTip(FindAllButton, Loc.Get("Find_FindAll_Name") + " (Alt+Enter)");
        ToolTipService.SetToolTip(OptionsToggle, Loc.Get("Find_Options_Name"));
        ToolTipService.SetToolTip(CloseButton, Loc.Get("Find_Close_Name"));
        ToolTipService.SetToolTip(HistoryButton, Loc.Get("Find_History_Name"));
        InitializeIncremental();
        _ready = true;
    }

    /// <summary>コンストラクターが終わったか (XAML の初期値の設定で出る変更のイベントでは、まだ検証しない)。</summary>
    private readonly bool _ready;

    // ---- 進捗とキャンセル (FIND-02) ----

    /// <summary>検索・数え上げの実行中は、0.5 秒を過ぎたら進捗バーとキャンセルボタンを出す (FIND-02 の仕様 1)。</summary>
    private void StartProgress()
    {
        if (_progressTimer is null)
        {
            _progressTimer = DispatcherQueue.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
            _progressTimer.Tick += (_, _) => UpdateProgress();
        }

        _progressTimer.Start();
    }

    private void UpdateProgress()
    {
        bool running = IsBusy;
        LongRunningOperation? op = _activeOperation;
        bool show = running && op is not null && op.Elapsed > LongRunningOperation.ShowDelay;
        BusyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            Progress.IsIndeterminate = op!.Fraction is null;
            Progress.Value = op.Fraction ?? 0;
        }

        if (!running)
        {
            _progressTimer?.Stop();
        }
    }

    /// <summary>検索・数え上げ・すべて検索・すべて置換のどれかが実行中か。</summary>
    private bool IsBusy => _running is not null || _counting is not null || _replacing is not null;

    /// <summary>実行中の検索と数え上げを取り消す (キャンセルボタン、検索欄の Esc。FIND-02 の仕様 3)。</summary>
    private void CancelRunning()
    {
        _running?.Cancel();
        _counting?.Cancel();
        _replacing?.Cancel();
        _incremental?.Cancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRunning();

    private EditorState? _editor;

    /// <summary>検索の対象のビュー。変わったら (タブの切り替え) 直前の一致と件数を忘れる。検索語と条件は残す。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (_editor != value)
            {
                _editor = value;
                UpdateReplaceAvailability();
                Validate();
            }
        }
    }

    /// <summary>長時間処理の管理 (ENG-09)。</summary>
    public OperationCenter? Operations { get; set; }

    /// <summary>すべて検索の結果一覧 (FIND-20)。null ならすべて検索はできない。</summary>
    public SearchResultsPanel? ResultsPanel { get; set; }

    /// <summary>一度でも有効な検索語があったか (F3 で検索バーを開くかどうかの判定。FIND-09 の仕様 7)。</summary>
    public bool HasPattern => _pattern is not null;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>進捗バーとキャンセルボタンを表示している (FIND-02 の仕様 1)。</summary>
    public bool IsProgressVisible => IsOpen && BusyPanel.Visibility == Visibility.Visible;

    /// <summary>今の検索語のパターン (無効なら null)。</summary>
    public SearchPattern? Pattern => _pattern;

    /// <summary>今の検索の種類。</summary>
    public SearchKind Kind => (SearchKind)Math.Max(0, KindChoice.SelectedIndex);

    public event EventHandler? Closed;

    /// <summary>検索語・条件・件数が変わった (表示中の一致の強調とスクロールバーの印を更新する)。</summary>
    public event EventHandler? MatchesChanged;

    /// <summary>
    /// 表示中の範囲と重なる一致 (FIND-04 の仕様 9)。検索バーが開いていて検索語が有効なときだけ返す。
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> MatchesInView(DocumentSnapshot snapshot, long offset, long length)
    {
        if (!IsOpen || _pattern is not { } pattern)
        {
            return [];
        }

        return SearchEngine.FindInView(snapshot, pattern, offset, length, CurrentScope, out _)
            .Select(m => (m.Offset, m.Length)).ToList();
    }

    /// <summary>スクロールバーの印にする一致の位置 (数え上げが終わっていれば。多すぎる場合は間引く)。</summary>
    public IReadOnlyList<long>? MarkerOffsets()
    {
        if (!IsOpen || _count is not { } count)
        {
            return null;
        }

        IReadOnlyList<SearchMatch> matches = count.Matches;
        int step = Math.Max(1, matches.Count / 10_000);
        return matches.Where((_, i) => i % step == 0).Select(m => m.Offset).ToList();
    }

    /// <summary>
    /// 検索バーを開く (FIND-04 の仕様 1・2)。選択範囲が 1〜256 バイトならその内容を検索欄に入れ、257 バイト以上なら
    /// 検索範囲を「選択範囲」にする。<paramref name="replace"/> なら置換欄を加える (FIND-22 の仕様 1)。
    /// </summary>
    public void Open(bool replace = false)
    {
        if (Editor is { } editor)
        {
            // 種類の既定: 前回使った種類。初回はカーソルのある列 (FIND-04 の仕様 3)。
            if (!_kindChosen)
            {
                KindChoice.SelectedIndex = editor.ActiveColumn == ActiveColumn.Text ? 1 : 0;
            }

            if (editor.HasSelection && editor.SelectionLength <= 256 && Kind is SearchKind.Hex or SearchKind.Text)
            {
                byte[] bytes = new byte[editor.SelectionLength];
                editor.Document.Current.Read(editor.SelectionStart, bytes);
                _suppressIncremental = true;
                Query.Text = Kind == SearchKind.Text && TryDecode(bytes, out string? text) ? text : ToHex(bytes);
                if (Kind == SearchKind.Text && Query.Text.Length > 0 && !TryDecode(bytes, out _))
                {
                    KindChoice.SelectedIndex = 0;
                }

                _suppressIncremental = false;
            }

            // 選択範囲の検索は、開いたときの選択範囲に固定する (FIND-11 の仕様 1)。
            ScopeSelectionItem.IsEnabled = editor.HasSelection;
            if (editor.HasSelection)
            {
                _scope = SearchScope.Of(editor.SelectionStart, editor.SelectionLength);
                if (editor.SelectionLength > 256)
                {
                    ScopeChoice.SelectedIndex = 1;
                }
            }
            else if (ScopeChoice.SelectedIndex == 1)
            {
                ScopeChoice.SelectedIndex = 0;
            }

            // インクリメンタルサーチの起点は、検索バーを開いたときのカーソル位置 (FIND-27 の仕様 3)。
            _origin = editor.Cursor;
        }

        SetReplaceMode(replace);
        Visibility = Visibility.Visible;
        Query.Focus(FocusState.Programmatic);
        Query.SelectAll();
        _historyCursor.Reset();
        Validate();
    }

    public void Close()
    {
        CancelRunning();
        _incrementalTimer?.Stop();

        // 「Esc で元の位置に戻る」がオンなら起点に戻る (FIND-27 の仕様 5)。
        if (EscReturnChoice.IsChecked == true && _incrementalMoved && Editor is { } editor)
        {
            editor.GoTo(_origin);
        }

        _incrementalMoved = false;
        Visibility = Visibility.Collapsed;
        MatchesChanged?.Invoke(this, EventArgs.Empty);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 次 (F3) または前 (Shift+F3) を検索する (FIND-09)。開始位置は、選択範囲が直前の一致と同じならその次 (前)、
    /// そうでなければカーソル位置。検索語がなければ何もしない。
    /// </summary>
    public async Task FindAsync(bool forward)
    {
        if (Editor is not { } editor || _pattern is not { } pattern || !ScopeIsValid)
        {
            return;
        }

        AddToHistory();
        StopIncremental();
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        DocumentSnapshot snapshot = editor.Document.Current;
        bool wrap = WrapChoice.IsChecked == true;
        var options = new SearchOptions { Scope = CurrentScope };
        long cursor = editor.Cursor;
        long selStart = editor.SelectionStart;
        long selLength = editor.SelectionLength;
        Status.Text = Loc.Get("Find_Searching");
        StartProgress();
        try
        {
            SearchHit? hit = Operations is null
                ? await Task.Run(() => _navigator.FindNext(snapshot, pattern, forward, wrap, cursor, selStart, selLength, options, null, cts.Token), cts.Token)
                : await Operations.RunAsync(
                    Loc.Get("Operation_Find"),
                    OperationKind.ReadOnly,
                    editor.Document,
                    snapshot.Length,
                    op =>
                    {
                        cts.Token.Register(op.Cancel);
                        op.ReportMatches(0);
                        _activeOperation = op;
                        return Task.FromResult(_navigator.FindNext(snapshot, pattern, forward, wrap, cursor, selStart, selLength, options, op, op.CancellationToken));
                    });
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (hit is { } h)
            {
                editor.SelectMatch(h.Offset, h.Length);
                Status.Text = h.Wrapped ? Loc.Get(forward ? "Find_WrappedToStart" : "Find_WrappedToEnd") : string.Empty;
                MarkQuery(QueryState.Normal);
                UpdateCountText();
                Announce(h.Wrapped ? Status.Text : Loc.Format("Find_FoundAt", StatusFormat.Hex(h.Offset)));
            }
            else
            {
                Status.Text = wrap ? Loc.Get("Find_NotFound") : Loc.Get(forward ? "Find_NotFoundToEnd" : "Find_NotFoundToStart");
                MarkQuery(QueryState.NotFound);
                Announce(Status.Text);
            }
        }
        catch (OperationCanceledException)
        {
            Status.Text = Loc.Get("Find_Cancelled");
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
                UpdateProgress();
            }
        }
    }

    // ---- すべて検索 (FIND-20) ----

    /// <summary>
    /// すべて検索 (Alt+Enter、「すべて検索」ボタン)。検索バーの今の条件ですべての一致を探し、結果一覧に出す (FIND-20 の仕様 1・2)。
    /// </summary>
    public async Task FindAllAsync()
    {
        if (Editor is not { } editor || _pattern is not { } pattern || ResultsPanel is not { } panel || !ScopeIsValid)
        {
            return;
        }

        AddToHistory();
        StopIncremental();
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        int limit = Math.Clamp(App.Settings?.GetInt(FindAllLimitKey, 1_000_000) ?? 1_000_000, 1_000, 100_000_000);
        var results = new SearchResults(editor.Document.Current, pattern, new SearchOptions
        {
            Scope = CurrentScope,
            IncludeOverlapping = OverlapChoice.IsChecked == true,
            MaxMatches = limit,
        });
        panel.Operations = Operations;
        Status.Text = Loc.Get("Find_Searching");
        StartProgress();
        try
        {
            await panel.RunAsync(editor, results, KindName(Kind), Query.Text, ResultsEncoding(editor), cts, op => _activeOperation = op);
            string message = results.LongCount == 0
                ? Loc.Get("Find_NotFound")
                : Loc.Format("Find_FoundCount", results.LongCount.ToString("N0", CultureInfo.CurrentCulture));
            Status.Text = cts.IsCancellationRequested ? Loc.Get("Find_Cancelled") : message;
            MarkQuery(results.LongCount == 0 && !cts.IsCancellationRequested ? QueryState.NotFound : QueryState.Normal);
            Announce(Status.Text);
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
                UpdateProgress();
            }
        }
    }

    /// <summary>結果一覧のテキストの列の文字コード: テキストの検索では検索の文字コード、それ以外は表示中の文字コード。</summary>
    private Encoding ResultsEncoding(EditorState editor) => Kind == SearchKind.Text
        ? TextEncodings.Get(SelectedEncoding)
        : Encoding.GetEncoding(editor.TextEncoding.CodePage);

    /// <summary>種類の表示名 (結果一覧の見出しの「Hex: AB CD」の「Hex」)。</summary>
    private static string KindName(SearchKind kind) => Loc.Get(kind switch
    {
        SearchKind.Text => "Find_KindName_Text",
        SearchKind.Integer => "Find_KindName_Integer",
        SearchKind.Float => "Find_KindName_Float",
        _ => "Find_KindName_Hex",
    });

    private async void FindAll_Click(object sender, RoutedEventArgs e) => await FindAllAsync();

    // ---- 件数 (FIND-12) ----

    /// <summary>検索語を確定したら件数を数える。範囲が 1 GiB を超える場合はボタンを出し、押したときだけ数える。</summary>
    private void StartCountIfAutomatic()
    {
        if (Editor is not { } editor || _pattern is null)
        {
            return;
        }

        // 同じ条件ですでに数えた (数えている) ときは数え直さない。
        if (_count is not null || _counting is not null)
        {
            UpdateCountText();
            return;
        }

        long scopeBytes = CurrentScope.TotalLength(editor.Document.Length);
        if (SearchEngine.CountsAutomatically(scopeBytes))
        {
            _ = CountAsync();
        }
        else
        {
            CountButton.Visibility = Visibility.Visible;
            CountText.Visibility = Visibility.Collapsed;
        }
    }

    private async void Count_Click(object sender, RoutedEventArgs e) => await CountAsync();

    private async Task CountAsync()
    {
        if (Editor is not { } editor || _pattern is not { } pattern)
        {
            return;
        }

        _counting?.Cancel();
        var cts = new CancellationTokenSource();
        _counting = cts;
        _count = null;
        CountButton.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Visible;
        UpdateCountText();
        DocumentSnapshot snapshot = editor.Document.Current;
        var options = new SearchOptions { Scope = CurrentScope };
        StartProgress();
        try
        {
            SearchResults results = Operations is null
                ? await Task.Run(() => SearchEngine.Count(snapshot, pattern, options, null, cts.Token), cts.Token)
                : await Operations.RunAsync(
                    Loc.Get("Operation_Count"),
                    OperationKind.ReadOnly,
                    editor.Document,
                    CurrentScope.TotalLength(snapshot.Length),
                    op =>
                    {
                        cts.Token.Register(op.Cancel);
                        _activeOperation = op;

                        // 数え上げ (SearchEngine.Count と同じ条件) の途中の件数を処理センターに出す (FIND-02 の仕様 2)。
                        var counted = new SearchResults(snapshot, pattern, options with { IncludeOverlapping = true, MaxMatches = SearchEngine.CountLimit });
                        op.ReportMatches(0);
                        counted.MatchesAdded += (_, _) => op.ReportMatches(counted.Count);
                        try
                        {
                            SearchEngine.FindAll(counted, op, op.CancellationToken);
                        }
                        finally
                        {
                            op.ReportMatches(counted.Count);
                        }

                        return Task.FromResult(counted);
                    });
            if (_counting == cts)
            {
                _count?.Dispose();
                _count = results;
                UpdateCountText();
                MatchesChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                results.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_counting == cts)
            {
                _counting = null;
                UpdateCountText();
                UpdateProgress();
            }
        }
    }

    /// <summary>「3 / 125」。数え上げ中は「3 / 数えています…」、100 万件を超えたら「100 万件以上」(FIND-12 の仕様 1〜3)。</summary>
    private void UpdateCountText()
    {
        if (Editor is not { } editor || _pattern is null)
        {
            CountText.Text = string.Empty;
            return;
        }

        string current = _navigator.IsLastMatch(editor.SelectionStart, editor.SelectionLength) && _navigator.LastMatch is { } m
            ? _count is { } c ? (c.CountBefore(m.Offset) + 1).ToString("N0") : "?"
            : "-";
        CountText.Text = _counting is not null
            ? Loc.Format("Find_Counting", current)
            : _count is { LimitReached: true }
                ? Loc.Format("Find_CountOverLimit", current, CountLimit.ToString("N0"))
                : _count is { } done ? Loc.Format("Find_CountResult", current, done.Count.ToString("N0")) : string.Empty;
    }

    // ---- 入力の検証 (FIND-04 の仕様 6・7) ----

    private enum QueryState
    {
        Normal,
        Invalid,
        NotFound,
    }

    /// <summary>今の検索範囲 (FIND-11)。</summary>
    private SearchScope CurrentScope => ScopeChoice.SelectedIndex switch
    {
        1 => _scope,
        2 => _rangeScope ?? SearchScope.WholeDocument,
        _ => SearchScope.WholeDocument,
    };

    /// <summary>検索範囲が正しいか (「オフセット範囲」の入力が正しくなければ検索できない。FIND-11 の「エラー」)。</summary>
    private bool ScopeIsValid => ScopeChoice.SelectedIndex != 2 || _rangeScope is not null;

    /// <summary>入力中の検索語を検証し、変換後のバイト列を表示する。条件が変わったら直前の一致と件数を忘れる。</summary>
    private void Validate()
    {
        if (!_ready)
        {
            return;
        }

        SearchKind kind = Kind;
        bool text = kind == SearchKind.Text;
        bool integer = kind == SearchKind.Integer;
        bool floating = kind == SearchKind.Float;
        EncodingChoice.Visibility = CaseChoice.Visibility = WordChoice.Visibility = EscapeChoice.Visibility = Show(text);
        AlignChoice.Visibility = Show(text && TextEncodings.SupportsAlignment(SelectedEncoding));
        IntBitsChoice.Visibility = SignChoice.Visibility = Show(integer);
        FloatChoice.Visibility = ToleranceChoice.Visibility = Show(floating);
        ToleranceValue.Visibility = Show(floating && ToleranceChoice.SelectedIndex > 0);
        EndianChoice.Visibility = Show(integer || floating);
        IncrementalChoice.Visibility = EscReturnChoice.Visibility = Show(kind is SearchKind.Hex or SearchKind.Text);
        bool range = ScopeChoice.SelectedIndex == 2;
        RangeStart.Visibility = RangeEnd.Visibility = RangeInfo.Visibility = Show(range);
        ValidateRange();

        _navigator.Reset();
        _counting?.Cancel();
        _counting = null;
        _count?.Dispose();
        _count = null;
        CountButton.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Visible;
        try
        {
            _pattern = BuildPattern(kind);
            Status.Text = PreviewText(_pattern, kind);
            MarkQuery(QueryState.Normal);
        }
        catch (PatternException ex)
        {
            _pattern = null;
            string message = Query.Text.Length == 0 ? string.Empty : ErrorText(ex);
            Status.Text = ex.Position is int position && message.Length > 0 ? Loc.Format("Find_ErrorAt", message, position) : message;
            MarkQuery(Query.Text.Length == 0 ? QueryState.Normal : QueryState.Invalid);
        }

        bool can = _pattern is not null && ScopeIsValid;
        NextButton.IsEnabled = PreviousButton.IsEnabled = can;
        FindAllButton.IsEnabled = can && ResultsPanel is not null;
        UpdateCountText();
        ValidateReplacement();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>今の種類と条件で検索語のパターンを作る。誤りは <see cref="PatternException"/>。</summary>
    private SearchPattern BuildPattern(SearchKind kind) => kind switch
    {
        SearchKind.Text => SearchPattern.FromText(Query.Text, TextEncodings.Get(SelectedEncoding), new TextSearchOptions
        {
            CaseSensitive = CaseChoice.IsChecked == true,
            UseEscapes = EscapeChoice.IsChecked == true,
            AlignToCharacters = AlignChoice.IsChecked == true,
            WholeWord = WordChoice.IsChecked == true,
        }),
        SearchKind.Integer => NumericSearch.Integer(Query.Text, new IntegerSearchOptions
        {
            Bits = SelectedBits,
            Sign = (IntegerSign)Math.Max(0, SignChoice.SelectedIndex),
            Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
        }, Editor is { } editor ? new EditorExpressionContext(editor) : null),
        SearchKind.Float => NumericSearch.Float(Query.Text, new FloatSearchOptions
        {
            Format = (FloatFormat)Math.Max(0, FloatChoice.SelectedIndex),
            Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
            Tolerance = (ToleranceKind)Math.Max(0, ToleranceChoice.SelectedIndex),
            ToleranceValue = ParseTolerance(),
        }),
        _ => SearchPattern.FromHex(Query.Text, new HexSearchOptions
        {
            MaxWildcardLength = Math.Clamp(App.Settings?.GetInt(MaxMatchLengthKey, SearchPattern.DefaultMaxMatchLength) ?? SearchPattern.DefaultMaxMatchLength,
                1, SearchPattern.MaxMaxMatchLength),
        }),
    };

    private int SelectedBits => IntBitsChoice.SelectedItem is ComboBoxItem { Tag: int bits } ? bits : 32;

    /// <summary>許容誤差の入力 (小数点は `.`。空なら 0)。読めない値は誤りにする。</summary>
    private double ParseTolerance()
    {
        if (ToleranceChoice.SelectedIndex <= 0)
        {
            return 0;
        }

        string t = ToleranceValue.Text.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        if (!double.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double v))
        {
            throw new PatternException(PatternError.InvalidTolerance, t);
        }

        return v;
    }

    /// <summary>
    /// 変換結果の表示 (FIND-04 の仕様 7)。Hex・テキストは先頭 32 バイト、整数はエンディアンごとのバイト列 (「34 12 (LE), 12 34 (BE)」)、
    /// 浮動小数点は格納される値 (FIND-14 の画面)。
    /// </summary>
    private static string PreviewText(SearchPattern pattern, SearchKind kind)
    {
        string bytes = pattern.Length > 32 ? Loc.Format("Find_PreviewMore", pattern.Preview(32), pattern.Length.ToString("N0")) : pattern.Preview(32);
        if (kind is SearchKind.Integer or SearchKind.Float && pattern.Numeric is { } numeric)
        {
            string variants;
            if (pattern.Variants.Count > 1 && !numeric.IsFloat)
            {
                variants = string.Join(", ", pattern.Variants.Select((v, i) =>
                    $"{Hex(NumericSearch.EncodeInteger(NumericSearch.DecodeUnsigned(pattern.Bytes, numeric.IsBigEndian(0)), numeric.Bits, numeric.IsBigEndian(i)))} ({v})"));
            }
            else
            {
                variants = $"{bytes} ({string.Join("/", pattern.Variants)})";
            }

            if (numeric.IsFloat)
            {
                double stored = NumericSearch.DecodeFloat(pattern.Bytes, numeric.Format, numeric.IsBigEndian(0));
                return Loc.Format("Find_FloatStored", NumericSearch.FormatName(numeric.Format), stored.ToString("G15", CultureInfo.InvariantCulture), variants);
            }

            return "= " + variants;
        }

        return pattern.Warnings.HasFlag(PatternWarnings.EdgeWildcardsIgnored) ? Loc.Format("Find_Warning_EdgeWildcards", bytes) : bytes;
    }

    private static string Hex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>誤りの説明文。範囲などの値があればそれを、なければ誤りのある語を入れる。</summary>
    private static string ErrorText(PatternException ex) =>
        ex.Arguments.Count > 0 ? Loc.Format("Find_Error_" + ex.Error, [.. ex.Arguments]) : Loc.Format("Find_Error_" + ex.Error, ex.Detail);

    /// <summary>「オフセット範囲」の開始と終了 (このバイトを含む) を入力式で読む (FIND-11 の仕様 1、エラー)。</summary>
    private void ValidateRange()
    {
        _rangeScope = null;
        if (ScopeChoice.SelectedIndex != 2 || Editor is not { } editor)
        {
            RangeStart.ClearValue(Control.BorderBrushProperty);
            RangeEnd.ClearValue(Control.BorderBrushProperty);
            return;
        }

        var context = new EditorExpressionContext(editor);
        bool startOk = ExpressionEvaluator.TryEvaluate(RangeStart.Text, context, out long start, out _);
        bool endOk = ExpressionEvaluator.TryEvaluate(RangeEnd.Text, context, out long end, out _);
        long length = editor.Document.Length;
        startOk &= start >= 0 && start < length;
        endOk &= end >= 0 && end < length;
        bool ordered = startOk && endOk && start <= end;
        Brush critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        SetBorder(RangeStart, startOk && (ordered || !endOk) ? null : critical);
        SetBorder(RangeEnd, endOk && (ordered || !startOk) ? null : critical);
        if (ordered)
        {
            _rangeScope = SearchScope.Of([SearchRange.FromInclusive(start, end)]);
            RangeInfo.Text = Loc.Format("Find_RangeInfo",
                start.ToString("N0", CultureInfo.CurrentCulture), StatusFormat.Hex(start),
                end.ToString("N0", CultureInfo.CurrentCulture), StatusFormat.Hex(end));
        }
        else
        {
            RangeInfo.Text = RangeStart.Text.Length == 0 && RangeEnd.Text.Length == 0 ? string.Empty : Loc.Get("Find_RangeInvalid");
        }
    }

    private static void SetBorder(Control control, Brush? brush)
    {
        if (brush is null)
        {
            control.ClearValue(Control.BorderBrushProperty);
        }
        else
        {
            control.BorderBrush = brush;
        }
    }

    /// <summary>不正な入力は赤枠、見つからないときは警告色の枠 (FIND-04 の仕様 6、エラー)。</summary>
    private void MarkQuery(QueryState state)
    {
        if (state == QueryState.Normal)
        {
            Query.ClearValue(Control.BorderBrushProperty);
            return;
        }

        Query.BorderBrush = (Brush)Application.Current.Resources[state == QueryState.Invalid ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];
    }

    private void Announce(string message)
    {
        if (message.Length > 0)
        {
            Microsoft.UI.Xaml.Automation.Peers.AutomationPeer? peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(Status)
                ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(Status);
            peer?.RaiseNotificationEvent(
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "FindResult");
        }
    }

    private TextEncodingId SelectedEncoding =>
        EncodingChoice.SelectedItem is ComboBoxItem { Tag: TextEncodingId id } ? id : TextEncodingId.Ascii;

    private bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            Encoding strict = Encoding.GetEncoding(TextEncodings.Get(SelectedEncoding).CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = strict.GetString(bytes);
            return !text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'));
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string ToHex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));

    private static string EncodingName(TextEncodingId id) => id switch
    {
        TextEncodingId.Ascii => "ASCII",
        TextEncodingId.Ansi => $"ANSI ({TextEncodings.CodePage(id)})",
        TextEncodingId.Oem => $"OEM ({TextEncodings.CodePage(id)})",
        TextEncodingId.Ebcdic => "EBCDIC (37)",
        TextEncodingId.Utf8 => "UTF-8",
        TextEncodingId.Utf16LE => "UTF-16 LE",
        TextEncodingId.Utf16BE => "UTF-16 BE",
        TextEncodingId.Utf32LE => "UTF-32 LE",
        TextEncodingId.Utf32BE => "UTF-32 BE",
        TextEncodingId.ShiftJis => "Shift_JIS",
        TextEncodingId.EucJp => "EUC-JP",
        TextEncodingId.Gb18030 => "GB18030",
        TextEncodingId.Big5 => "Big5",
        TextEncodingId.EucKr => "EUC-KR",
        _ => id.ToString(),
    };

    // ---- イベント ----

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applyingHistory)
        {
            _historyCursor.Reset();
        }

        Validate();
        ScheduleIncremental();
    }

    private void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, KindChoice) && IsLoaded)
        {
            _kindChosen = true;
        }

        // Hex・数値の入力は左から右に固定する。テキストの検索は表示言語の向きに従う (UI-44 の仕様 2)。
        if (Query is not null && KindChoice is not null)
        {
            Query.FlowDirection = Kind == SearchKind.Text ? FlowDirection : FlowDirection.LeftToRight;
        }

        Validate();
    }

    private void Check_Changed(object sender, RoutedEventArgs e) => Validate();

    private void Range_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OptionsToggle_Click(object sender, RoutedEventArgs e) =>
        OptionsPanel.Visibility = OptionsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 検索欄のキー (00-overview 8.4): Enter / Shift+Enter で検索 (方向のオプションに従う)、Alt+Enter ですべて検索、
    /// ↑ / ↓ で検索履歴、Esc で実行中の検索・数え上げの取り消し、なければ閉じる。
    /// </summary>
    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key is VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Up or VirtualKey.Down)
        {
            e.Handled = true;
            await HandleQueryKeyAsync(e.Key, shift, alt);
        }
    }

    /// <summary>検索欄の Enter / Shift+Enter / Alt+Enter / ↑ / ↓ / Esc の処理 (テスト用の命令の通り道からも呼ぶ)。</summary>
    internal async Task HandleQueryKeyAsync(VirtualKey key, bool shift, bool alt = false)
    {
        switch (key)
        {
            case VirtualKey.Enter when alt:
                await FindAllAsync();
                break;
            case VirtualKey.Enter:
                bool forward = DirectionChoice.SelectedIndex == 0;
                await FindAsync(shift ? !forward : forward);
                StartCountIfAutomatic();
                break;
            case VirtualKey.Up:
                RecallHistory(HistoryList.Find, older: true);
                break;
            case VirtualKey.Down:
                RecallHistory(HistoryList.Find, older: false);
                break;
            case VirtualKey.Escape:
                if (IsBusy || _incremental is not null)
                {
                    CancelRunning();
                }
                else
                {
                    Close();
                }

                break;
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(forward: true);
        StartCountIfAutomatic();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(forward: false);
        StartCountIfAutomatic();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
