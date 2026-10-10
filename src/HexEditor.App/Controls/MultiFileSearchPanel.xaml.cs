using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>開いているドキュメント (複数ファイル検索で、ファイルの代わりに未保存の状態を検索・置換する)。</summary>
public sealed record OpenFileDocument(string Path, EditorState Editor);

/// <summary>
/// 「複数ファイル検索」パネル (FIND-30、FIND-31)。フォルダのファイルを並列に検索し、結果をファイルごとにまとめて出す。置換モードでは
/// チェックした一致だけを、確認のあとファイルごとの安全な保存で置換する (開いているドキュメントはエディタ上で置換し、Undo で戻せる)。
/// 一致は Core の結果 (メモリ上は 1,000,000 件まで、超える分は一時ファイル) に置き、一覧の行は見えている分だけ作る。
/// </summary>
public sealed partial class MultiFileSearchPanel : UserControl
{
    /// <summary>名前を付けて保存した対象の指定 (アプリの状態。FIND-30 の仕様 9)。</summary>
    public const string TargetSetsKey = "search.multiFile.targets";

    /// <summary>検索中に一覧を更新する間隔 (一致ごとに更新しない)。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _running;
    private MultiFileSearchResults? _results;
    private MultiFileResultView? _view;
    private SearchQuery? _query;
    private BuiltSearchPattern? _built;
    private ReplacementTemplate? _template;
    private bool _ready;

    /// <summary>検索バーから取り込んだ、パネルの欄にない条件 (複数の文字コード・複数の語)。</summary>
    private SearchQuery? _extras;

    /// <summary>検索の条件 (「続ける」で同じものを使う)。</summary>
    private SearchOptions _searchOptions = new();
    private Dictionary<string, OpenDocumentSnapshot> _openSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private int _parallelism = MultiFileSearch.DefaultParallelism;
    private LongRunningOperation? _operation;

    /// <summary>置換の結果の表示 (ファイルの番号ごと)。</summary>
    private readonly Dictionary<int, string> _outcomeTexts = [];
    private int _refreshPending;
    private readonly ConcurrentQueue<MultiFileRow> _previewQueue = new();
    private int _previewRunning;

    public MultiFileSearchPanel()
    {
        Rows = new MultiFileRowList(CreateRow);
        InitializeComponent();
        foreach (EncodingEntry entry in TermRow.Encodings)
        {
            EncodingChoice.Items.Add(new ComboBoxItem { Content = MainWindow.EncodingDisplayText(entry), Tag = entry.Id });
        }

        EncodingChoice.SelectedIndex = Math.Max(0, TermRow.Encodings.ToList().FindIndex(e => e.Id == "ascii"));
        foreach (int bits in NumericSearch.IntegerSizes)
        {
            IntBitsChoice.Items.Add(new ComboBoxItem { Content = Loc.Format("Find_IntBits_Item", bits), Tag = bits });
        }

        IntBitsChoice.SelectedIndex = NumericSearch.IntegerSizes.ToList().IndexOf(32);
        AutomationProperties.SetName(ModeChoice, Loc.Get("MultiFile_Mode_Name"));
        AutomationProperties.SetName(KindChoice, Loc.Get("Find_Kind_Name"));
        AutomationProperties.SetName(EncodingChoice, Loc.Get("Find_Encoding_Name"));
        AutomationProperties.SetName(IntBitsChoice, Loc.Get("Find_IntBits_Name"));
        AutomationProperties.SetName(SignChoice, Loc.Get("Find_Sign_Name"));
        AutomationProperties.SetName(FloatChoice, Loc.Get("Find_FloatFormat_Name"));
        AutomationProperties.SetName(EndianChoice, Loc.Get("Find_Endian_Name"));
        AutomationProperties.SetName(ToleranceChoice, Loc.Get("Find_Tolerance_Name"));
        AutomationProperties.SetName(ToleranceValue, Loc.Get("Find_ToleranceValue_Name"));
        AutomationProperties.SetName(MaskModeChoice, Loc.Get("Find_MaskMode_Name"));
        AutomationProperties.SetName(LengthPolicyChoice, Loc.Get("Find_LengthPolicy_Name"));
        AutomationProperties.SetName(FillerChoice, Loc.Get("Find_Filler_Name"));
        AutomationProperties.SetName(FillerCustom, Loc.Get("Find_FillerCustom_Name"));
        AutomationProperties.SetName(FolderList, Loc.Get("MultiFile_Folders_Name"));
        AutomationProperties.SetName(ResultList, Loc.Get("MultiFile_Results_Name"));
        AutomationProperties.SetName(TargetSetChoice, Loc.Get("MultiFile_TargetSet_Name"));
        FillTargetSets();
        _ready = true;
        Validate();
    }

    /// <summary>検索するフォルダ。</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>結果の行 (ファイルの行と、その下の一致の行)。見えている行だけを作る。</summary>
    public MultiFileRowList Rows { get; }

    public OperationCenter? Operations { get; set; }

    public Microsoft.UI.WindowId WindowId { get; set; }

    /// <summary>確認ダイアログ (置換の前。FIND-31 の仕様 3)。</summary>
    public Func<ConfirmRequest, Task<ConfirmChoice>>? Confirm { get; set; }

    /// <summary>開いているドキュメント (UI のスレッドで呼ぶ)。</summary>
    public Func<IReadOnlyList<OpenFileDocument>>? OpenDocuments { get; set; }

    /// <summary>既定のフォルダ (現在のドキュメントのフォルダ。FIND-30 の仕様 2)。</summary>
    public Func<string?>? DefaultFolder { get; set; }

    /// <summary>検索バーの今の条件 (「検索バーの条件を使う」。FIND-30 の仕様 1)。</summary>
    public Func<SearchQuery?>? FindBarQuery { get; set; }

    /// <summary>結果を開く: そのファイルをタブで開き (開いていれば切り替え)、一致を選択する (FIND-30 の仕様 6)。</summary>
    public event EventHandler<(string Path, long Offset, long Length)>? OpenRequested;

    /// <summary>通知を出す (失敗など)。</summary>
    public event EventHandler<(string Message, InfoBarSeverity Severity)>? NoticeRequested;

    public bool IsRunning => _running is not null;

    /// <summary>置換モードか。</summary>
    public bool IsReplaceMode => ModeChoice.SelectedIndex == 1;

    /// <summary>テスト用: 結果。</summary>
    internal MultiFileSearchResults? Results => _results;

    /// <summary>テスト用: 行の並び。</summary>
    internal MultiFileResultView? View => _view;

    /// <summary>テスト用: 置換の結果 (ファイルごと)。</summary>
    internal IReadOnlyList<FileReplaceOutcome> Outcomes { get; private set; } = [];

    internal string SummaryText => Summary.Text;

    internal string ErrorText => Error.Text;

    internal bool ContinueVisible => ContinueButton.Visibility == Visibility.Visible;

    internal string ExtrasDescription => ExtrasText.Text;

    /// <summary>テスト用: 1 ファイルあたりの件数の上限 (既定 10,000)。</summary>
    internal int PerFileLimit { get; set; } = MultiFileSearch.DefaultPerFileLimit;

    /// <summary>テスト用: メモリ上に置く一致の件数の上限 (null なら既定の 1,000,000)。</summary>
    internal int? MemoryLimit { get; set; }

    /// <summary>パネルを表示したとき: フォルダがなければ現在のドキュメントのフォルダを入れる。</summary>
    public void PrepareToShow(bool replace)
    {
        if (Folders.Count == 0 && DefaultFolder?.Invoke() is { Length: > 0 } folder)
        {
            Folders.Add(folder);
        }

        if (replace)
        {
            ModeChoice.SelectedIndex = 1;
        }

        Query.Focus(FocusState.Programmatic);
    }

    /// <summary>フォルダを対象にする (Explorer からのフォルダのドロップ。FIND-30 の「呼び出し」、UI-34 の仕様 4)。</summary>
    public void SetFolders(IEnumerable<string> folders)
    {
        Folders.Clear();
        foreach (string f in folders)
        {
            if (!Folders.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                Folders.Add(f);
            }
        }

        Validate();
    }

    /// <summary>テスト用: 条件と対象を入れる。</summary>
    internal void Configure(JsonObject request)
    {
        if (request["mode"]?.GetValue<string>() is { } mode)
        {
            ModeChoice.SelectedIndex = mode == "replace" ? 1 : 0;
        }

        if (request["kind"]?.GetValue<string>() is { } kind)
        {
            KindChoice.SelectedIndex = (int)Enum.Parse<SearchKind>(kind, ignoreCase: true);
        }

        if (request["query"]?.GetValue<string>() is { } query)
        {
            Query.Text = query;
        }

        if (request["replace"]?.GetValue<string>() is { } replace)
        {
            ReplaceQuery.Text = replace;
        }

        if (request["folders"] is JsonArray folders)
        {
            SetFolders([.. folders.Select(f => f!.GetValue<string>())]);
        }

        if (request["exclude"]?.GetValue<string>() is { } exclude)
        {
            ExcludeInput.Text = exclude;
        }

        if (request["include"]?.GetValue<string>() is { } include)
        {
            IncludeInput.Text = include;
        }

        if (request["backup"] is { } backup)
        {
            BackupChoice.IsChecked = backup.GetValue<bool>();
        }

        if (request["options"] is JsonObject o)
        {
            static void Set(CheckBox box, JsonNode? node)
            {
                if (node is not null)
                {
                    box.IsChecked = node.GetValue<bool>();
                }
            }

            static void Index(ComboBox box, JsonNode? node)
            {
                if (node is not null)
                {
                    box.SelectedIndex = node.GetValue<int>();
                }
            }

            Set(CaseChoice, o["caseSensitive"]);
            Set(WordChoice, o["wholeWord"]);
            Set(EscapeChoice, o["escapes"]);
            Set(RangeExcludeChoice, o["rangeExclude"]);
            Set(RegexIgnoreCase, o["regexIgnoreCase"]);
            Set(RegexMultiline, o["regexMultiline"]);
            Set(RegexSingleline, o["regexSingleline"]);
            Set(MaskChoice, o["mask"]);
            Set(MismatchChoice, o["mismatch"]);
            Index(SignChoice, o["sign"]);
            Index(EndianChoice, o["endian"]);
            Index(FloatChoice, o["floatFormat"]);
            Index(MaskModeChoice, o["maskMode"]);
            Index(LengthPolicyChoice, o["lengthPolicy"]);
            Index(FillerChoice, o["filler"]);
            if (o["intBits"] is { } bits)
            {
                IntBitsChoice.SelectedIndex = NumericSearch.IntegerSizes.ToList().IndexOf(bits.GetValue<int>());
            }

            if (o["encoding"]?.GetValue<string>() is { } encoding)
            {
                EncodingChoice.SelectedItem = EncodingChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == encoding);
            }

            MaskQuery.Text = o["maskText"]?.GetValue<string>() ?? MaskQuery.Text;
            FillerCustom.Text = o["fillerCustom"]?.GetValue<string>() ?? FillerCustom.Text;
            PositionModulus.Text = o["positionModulus"]?.GetValue<string>() ?? PositionModulus.Text;
            PositionRemainder.Text = o["positionRemainder"]?.GetValue<string>() ?? PositionRemainder.Text;
            ToleranceChoice.SelectedIndex = o["tolerance"]?.GetValue<int>() ?? ToleranceChoice.SelectedIndex;
            ToleranceValue.Text = o["toleranceValue"]?.GetValue<string>() ?? ToleranceValue.Text;
        }

        if (request["perFileLimit"] is { } perFile)
        {
            PerFileLimit = perFile.GetValue<int>();
        }

        if (request["memoryLimit"] is { } memory)
        {
            MemoryLimit = memory.GetValue<int>();
        }

        Validate();
    }

    /// <summary>テスト用: 行のチェックを切り替える (ファイルのパスの末尾と、一致の番号。番号がなければファイルの行)。</summary>
    internal bool SetChecked(string fileSuffix, int? match, bool value)
    {
        if (FindFile(fileSuffix) is not int f)
        {
            return false;
        }

        _view!.SetChecked(f, match ?? -1, value);
        ShowChecks(f);
        Validate();
        return true;
    }

    /// <summary>テスト用: 行を開く (ダブルクリック・Enter と同じ)。</summary>
    internal bool OpenRow(string fileSuffix, int match)
    {
        if (FindFile(fileSuffix) is not int f || match >= _view!.MatchCount(f))
        {
            return false;
        }

        int row = (int)_view.RowOf(f, match);
        ResultList.SelectedIndex = row;
        Open(Rows[row]);
        return true;
    }

    private int? FindFile(string fileSuffix)
    {
        if (_view is not { } view)
        {
            return null;
        }

        for (int i = 0; i < view.FileCount; i++)
        {
            if (view.File(i).Path.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>テスト用: 行の内容 (行のオブジェクトを作らずに。一致のデータは含まない)。</summary>
    internal (FileSearchResult File, SearchMatch? Match, string Title, bool? Checked) DescribeRow(int row)
    {
        (int f, int m) = _view!.RowAt(row);
        FileSearchResult file = _view.File(f);
        return m < 0
            ? (file, null, FileTitle(f), _view.IsChecked(f, -1))
            : (file, file.MatchAt(m), MatchTitle(file.MatchAt(m), null, 0), _view.IsChecked(f, m));
    }

    // ---- 条件 (FIND-30 の仕様 1: 検索バーと同じ種類・オプション) ----

    private SearchKind Kind => (SearchKind)Math.Max(0, KindChoice.SelectedIndex);

    private string EncodingId => EncodingChoice.SelectedItem is ComboBoxItem { Tag: string id } ? id : "ascii";

    private int SelectedBits => IntBitsChoice.SelectedItem is ComboBoxItem { Tag: int bits } ? bits : 32;

    private bool HasTerms => _extras?.Terms is not null;

    private bool HasEncodings => _extras is { Encodings.Count: > 0 } && Kind == SearchKind.Text && !HasTerms;

    /// <summary>パネルの欄と、検索バーから取り込んだ条件をまとめた検索条件。位置の条件の入力が不正なら <see cref="PatternException"/>。</summary>
    private SearchQuery CurrentQuery() => new()
    {
        Kind = Kind,
        Text = Query.Text,
        EncodingId = EncodingId,
        Encodings = HasEncodings ? _extras!.Encodings : [],
        CaseSensitive = CaseChoice.IsChecked == true,
        WholeWord = WordChoice.IsChecked == true,
        UseEscapes = EscapeChoice.IsChecked == true,
        AlignToCharacters = AlignChoice.IsChecked == true,
        IntegerBits = SelectedBits,
        Sign = (IntegerSign)Math.Max(0, SignChoice.SelectedIndex),
        Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
        FloatFormat = (FloatFormat)Math.Max(0, FloatChoice.SelectedIndex),
        Tolerance = (ToleranceKind)Math.Max(0, ToleranceChoice.SelectedIndex),
        ToleranceText = ToleranceValue.Text,
        RangeExclude = RangeExcludeChoice.IsChecked == true,
        RegexIgnoreCase = RegexIgnoreCase.IsChecked == true,
        RegexMultiline = RegexMultiline.IsChecked == true,
        RegexSingleline = RegexSingleline.IsChecked == true,
        Mask = MaskChoice.IsChecked == true && Kind == SearchKind.Hex,
        MaskBits = MaskModeChoice.SelectedIndex == 1,
        MaskText = MaskQuery.Text,
        Mismatch = MismatchChoice.IsChecked == true && Kind == SearchKind.Hex,
        MismatchAligned = MismatchAlignChoice.IsChecked == true,
        Position = ReadPosition(),
        Terms = _extras?.Terms,
        TermLabels = _extras?.TermLabels,
    };

    /// <summary>位置の条件 (周期・余りは数。空なら条件なし)。</summary>
    private PositionCondition? ReadPosition()
    {
        if (PositionModulus.Text.Trim().Length == 0)
        {
            return null;
        }

        var context = new NoContext();
        if (!ExpressionEvaluator.TryEvaluate(PositionModulus.Text, context, out long x, out _, DefaultRadix.Decimal))
        {
            throw new PatternException(PatternError.PositionModulus, PositionModulus.Text, null,
                [PositionCondition.MaxModulus.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        string remainder = PositionRemainder.Text.Trim().Length == 0 ? "0" : PositionRemainder.Text;
        if (!ExpressionEvaluator.TryEvaluate(remainder, context, out long y, out _, DefaultRadix.Decimal))
        {
            throw new PatternException(PatternError.PositionRemainder, remainder, null, [Math.Max(0, x - 1).ToString("N0", CultureInfo.CurrentCulture)]);
        }

        return PositionCondition.Create(x, y);
    }

    /// <summary>埋め草 (FIND-24 の仕様 3)。指定バイトが不正なら null。</summary>
    private byte[]? CurrentFiller()
    {
        switch (FillerChoice.SelectedIndex)
        {
            case 1:
                return [0xFF];
            case 2:
                return Kind == SearchKind.Text ? SearchPattern.EncodeText(" ", TextEncodings.FromCatalogId(EncodingId) ?? Encoding.ASCII, false) : [0x20];
            case 3:
                try
                {
                    (byte Value, bool Keep)[] bytes = SearchPattern.ParseReplacementHex(FillerCustom.Text);
                    return bytes.Length is >= 1 and <= 16 && !bytes.Any(b => b.Keep) ? [.. bytes.Select(b => b.Value)] : null;
                }
                catch (PatternException)
                {
                    return null;
                }

            default:
                return [0x00];
        }
    }

    /// <summary>置換の条件 (長さが違う場合の扱いと埋め草。FIND-24)。</summary>
    private ReplaceOptions CurrentReplaceOptions() => new()
    {
        Policy = (LengthPolicy)Math.Max(0, LengthPolicyChoice.SelectedIndex),
        Filler = CurrentFiller() ?? [0x00],
    };

    /// <summary>対象の指定 (FIND-30 の仕様 2)。サイズ・深さの入力が不正なら null。</summary>
    private MultiFileTargets? BuildTargets(out string? error)
    {
        string? problem = null;
        long? Size(TextBox box)
        {
            if (box.Text.Trim().Length == 0)
            {
                return null;
            }

            if (ExpressionEvaluator.TryEvaluate(box.Text, new NoContext(), out long v, out _) && v >= 0)
            {
                return v;
            }

            problem = Loc.Get("MultiFile_SizeInvalid");
            return null;
        }

        int? depth = null;
        if (DepthInput.Text.Trim().Length > 0)
        {
            if (int.TryParse(DepthInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int d))
            {
                depth = d;
            }
            else
            {
                problem = Loc.Get("MultiFile_DepthInvalid");
            }
        }

        var targets = new MultiFileTargets
        {
            Folders = [.. Folders],
            IncludeSubfolders = SubfoldersChoice.IsChecked == true,
            MaxDepth = depth,
            IncludeMasks = IncludeInput.Text,
            ExcludeMasks = ExcludeInput.Text,
            MinSize = Size(MinSizeInput),
            MaxSize = Size(MaxSizeInput),
            IncludeHidden = HiddenChoice.IsChecked == true,
            FollowLinks = LinksChoice.IsChecked == true,
            IncludeOpenDocuments = OpenDocsChoice.IsChecked == true,
        };
        error = problem;
        return problem is null ? targets : null;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Validate()
    {
        if (!_ready)
        {
            return;
        }

        bool replace = IsReplaceMode;
        SearchKind kind = Kind;
        bool terms = HasTerms;
        bool text = kind == SearchKind.Text && !terms;
        bool regexText = kind == SearchKind.RegexText && !terms;
        bool regex = kind is SearchKind.RegexText or SearchKind.RegexBytes && !terms;
        bool hex = kind == SearchKind.Hex && !terms;
        bool integer = kind == SearchKind.Integer && !terms;
        bool floating = kind == SearchKind.Float && !terms;
        bool range = (integer || floating) && NumericRange.IsRange(Query.Text);
        bool mask = hex && MaskChoice.IsChecked == true && MismatchChoice.IsChecked != true;
        ReplaceRow.Visibility = ReplaceButton.Visibility = Show(replace);
        Query.Visibility = Show(!terms);
        EncodingChoice.Visibility = Show((text && !HasEncodings) || regexText);
        CaseChoice.Visibility = WordChoice.Visibility = Show(text || terms);
        EscapeChoice.Visibility = Show(text);
        AlignChoice.Visibility = Show(text || regexText);
        IntBitsChoice.Visibility = SignChoice.Visibility = Show(integer);
        FloatChoice.Visibility = Show(floating);
        ToleranceChoice.Visibility = Show(floating && !range);
        ToleranceValue.Visibility = Show(floating && !range && ToleranceChoice.SelectedIndex > 0);
        EndianChoice.Visibility = Show(integer || floating || terms);
        RangeExcludeChoice.Visibility = Show(range);
        RegexIgnoreCase.Visibility = RegexMultiline.Visibility = RegexSingleline.Visibility = Show(regex);
        MaskChoice.Visibility = MismatchChoice.Visibility = Show(hex);
        MaskModeChoice.Visibility = Show(mask);
        MaskQuery.Visibility = Show(mask && MaskModeChoice.SelectedIndex == 0);
        MismatchAlignChoice.Visibility = Show(hex && MismatchChoice.IsChecked == true);
        FillerChoice.Visibility = Show(replace && LengthPolicyChoice.SelectedIndex == (int)LengthPolicy.PadKeepLength);
        FillerCustom.Visibility = Show(FillerChoice.Visibility == Visibility.Visible && FillerChoice.SelectedIndex == 3);
        ExtrasText.Text = _extras is null ? string.Empty
            : terms ? Loc.Format("MultiFile_Extras_Terms", _extras.Terms!.Count)
            : _extras.Encodings.Count > 0 ? Loc.Format("MultiFile_Extras_Encodings", string.Join(", ", _extras.Encodings)) : string.Empty;
        ClearExtrasButton.Visibility = Show(ExtrasText.Text.Length > 0);

        string? error = null;
        _built = null;
        _query = null;
        try
        {
            _query = CurrentQuery();
            _built = Query.Text.Length == 0 && !terms ? null : SearchQueryBuilder.Build(_query, FindBar.QueryEnvironment(null));
        }
        catch (PatternException ex)
        {
            string message = FindBar.ErrorText(ex);
            error = Loc.Format("MultiFile_QueryInvalid", ex.InMask ? Loc.Format("Find_MaskError", message) : message);
        }

        _template = null;
        if (replace && _built is { } built && _query is { } q)
        {
            if (!q.CanReplace)
            {
                error = Loc.Get("Find_ReplaceUnsupported");
            }
            else
            {
                try
                {
                    _template = SearchQueryBuilder.BuildTemplate(q, built, ReplaceQuery.Text, FindBar.QueryEnvironment(null));
                }
                catch (PatternException ex)
                {
                    error = Loc.Format("MultiFile_ReplaceInvalid", FindBar.ErrorText(ex));
                }

                if (LengthPolicyChoice.SelectedIndex == (int)LengthPolicy.PadKeepLength && CurrentFiller() is null)
                {
                    _template = null;
                    error = Loc.Get("Find_FillerInvalid");
                }
            }
        }

        Error.Text = error ?? string.Empty;
        SearchButton.IsEnabled = _built is not null && _running is null && Folders.Count > 0;
        ReplaceButton.IsEnabled = replace && _template is not null && _results is { State: not MultiFileSearchState.Running } && _running is null
            && (_view?.AnyChecked ?? false);
        Visibility checks = Show(replace);
        foreach (MultiFileRow row in Rows.Realized)
        {
            row.CheckVisibility = checks;
        }

        ContinueButton.Visibility = Show(_results is { CanContinue: true } && _running is null);
    }

    // ---- 検索 (FIND-30) ----

    /// <summary>「検索」。対象のフォルダがなければ検索の前にエラーを出す (「エラー」)。</summary>
    public Task SearchAsync() => RunSearchAsync(continuing: false);

    /// <summary>「続ける」: 上限を 2 倍にして、続きから探す (FIND-20 の仕様 6、FIND-30 の仕様 8)。</summary>
    public Task ContinueAsync() => RunSearchAsync(continuing: true);

    private async Task RunSearchAsync(bool continuing)
    {
        if (_running is not null)
        {
            return;
        }

        MultiFileSearchResults results;
        long totalLimit = Math.Clamp(App.Settings?.GetInt(MultiFileSearch.TotalLimitKey, (int)MultiFileSearch.DefaultTotalLimit) ?? MultiFileSearch.DefaultTotalLimit,
            MultiFileSearch.MinTotalLimit, MultiFileSearch.MaxTotalLimit);
        if (continuing)
        {
            if (_results is not { CanContinue: true } current)
            {
                return;
            }

            results = current;
        }
        else
        {
            if (_built is not { } built)
            {
                return;
            }

            if (BuildTargets(out string? targetError) is not { } targets)
            {
                Error.Text = targetError ?? string.Empty;
                return;
            }

            if (MultiFileSearch.MissingFolders(targets) is { Count: > 0 } missing)
            {
                Error.Text = Loc.Format("MultiFile_FolderMissing", string.Join(", ", missing));
                return;
            }

            Error.Text = string.Empty;
            _results?.Dispose();
            results = new MultiFileSearchResults(built.Pattern, targets)
            {
                MemoryLimit = MemoryLimit ?? MatchStore.DefaultMemoryLimit,
                MismatchMinRepeat = Math.Clamp(App.Settings?.GetInt(MismatchSearch.MinRepeatKey, MismatchSearch.DefaultMinRepeat) ?? MismatchSearch.DefaultMinRepeat,
                    1, MismatchSearch.MaxMinRepeat),
            };
            _results = results;
            _view = new MultiFileResultView(results);
            _outcomeTexts.Clear();
            Outcomes = [];
            Rows.Reset(0);

            // 開いているドキュメントは、検索を始めたときの (未保存の編集を含む) 状態を検索する。
            _openSnapshots = new Dictionary<string, OpenDocumentSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (OpenFileDocument doc in OpenDocuments?.Invoke() ?? [])
            {
                _openSnapshots[doc.Path] = new OpenDocumentSnapshot(doc.Editor.Document.Current, doc.Editor.Document.Length, File.GetLastWriteTimeUtc(doc.Path));
            }

            _parallelism = Math.Clamp(App.Settings?.GetInt(MultiFileSearch.ParallelismKey, MultiFileSearch.DefaultParallelism) ?? MultiFileSearch.DefaultParallelism,
                1, MultiFileSearch.MaxParallelism);
            _searchOptions = new SearchOptions { ChunkSize = FindBar.ChunkSizeSetting };
            results.Changed += (_, _) =>
            {
                if (_operation is { } op)
                {
                    op.ReportMatches(results.MatchCount);
                    op.ReportDetail(Loc.Format("MultiFile_Progress", results.ProcessedFiles.ToString("N0", CultureInfo.CurrentCulture),
                        results.FoundFiles.ToString("N0", CultureInfo.CurrentCulture), results.CurrentFile ?? string.Empty));
                }

                ScheduleRefresh(results);
            };
        }

        var cts = new CancellationTokenSource();
        _running = cts;
        CancelButton.IsEnabled = true;
        Validate();
        Dictionary<string, OpenDocumentSnapshot> open = _openSnapshots;
        OpenDocumentSnapshot? OpenSnapshot(string path) => open.TryGetValue(path, out OpenDocumentSnapshot? s) ? s : null;
        AppLog.Info($"Multi-file search: {(continuing ? "continue" : "start")} ({results.Targets.Folders.Count} folder(s))");
        try
        {
            async Task Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                }

                _operation = op;
                try
                {
                    if (continuing)
                    {
                        await MultiFileSearch.ContinueAsync(results, _searchOptions, OpenSnapshot, _parallelism, PerFileLimit, op, token);
                    }
                    else
                    {
                        await MultiFileSearch.RunAsync(results, _searchOptions, OpenSnapshot, _parallelism, PerFileLimit, totalLimit, op, token);
                    }
                }
                finally
                {
                    _operation = null;
                }

                op?.ReportDetail(Loc.Format("MultiFile_Progress", results.ProcessedFiles.ToString("N0", CultureInfo.CurrentCulture),
                    results.FoundFiles.ToString("N0", CultureInfo.CurrentCulture), string.Empty));
            }

            if (Operations is null)
            {
                await Task.Run(() => Work(null));
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_MultiFileSearch"), OperationKind.ReadOnly, null, null, Work);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }

            CancelButton.IsEnabled = false;
            AppLog.Info($"Multi-file search: end ({results.State}, {results.FileCount} file(s), {results.MatchCount} match(es), {results.InMemoryMatches} in memory)");
            if (_results == results)
            {
                RefreshRows();
                UpdateSummary();
            }

            Validate();
        }
    }

    /// <summary>一覧の更新をまとめて行う (結果が変わるたびではなく、<see cref="RefreshInterval"/> ごとに 1 回)。</summary>
    private void ScheduleRefresh(MultiFileSearchResults results)
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(RefreshInterval).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            Volatile.Write(ref _refreshPending, 0);
            if (_results == results)
            {
                RefreshRows();
                UpdateSummary();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>加わったファイル・一致を一覧に入れる (知らせは 1 回。選択している行は保つ)。</summary>
    private void RefreshRows()
    {
        if (_view is not { } view || !view.Refresh())
        {
            return;
        }

        int selected = ResultList.SelectedIndex;
        Rows.Reset((int)Math.Min(int.MaxValue, view.RowCount));
        if (selected >= 0 && selected < Rows.Count)
        {
            ResultList.SelectedIndex = selected;
        }

        Validate();
    }

    /// <summary>行を作る (一覧が尋ねた行だけ)。一致のデータは後から読み込む。</summary>
    private MultiFileRow CreateRow(int row)
    {
        (int f, int m) = _view!.RowAt(row);
        FileSearchResult file = _view.File(f);
        Visibility checks = Show(IsReplaceMode);
        if (m < 0)
        {
            return new MultiFileRow(row, f, file, -1, null, FileTitle(f), _view.IsChecked(f, -1), checks, RowChecked);
        }

        SearchMatch match = file.MatchAt(m);
        var created = new MultiFileRow(row, f, file, m, match, MatchTitle(match, null, 0), _view.IsChecked(f, m), checks, RowChecked);
        QueuePreview(created);
        return created;
    }

    /// <summary>ファイルの行の見出し: パス、件数、サイズ、更新日時。上限で止めた・続きがある・置換の結果。</summary>
    private string FileTitle(int fileIndex)
    {
        FileSearchResult file = _view!.File(fileIndex);
        CultureInfo culture = CultureInfo.CurrentCulture;
        string title = Loc.Format("MultiFile_FileRow", file.Path, _view.MatchCount(fileIndex).ToString("N0", culture), file.Size.ToString("N0", culture),
            file.LastWriteUtc.ToLocalTime().ToString("g", culture));
        if (file.LimitReached)
        {
            title += "  " + Loc.Format("MultiFile_FileLimit", PerFileLimit.ToString("N0", culture));
        }
        else if (file.Incomplete)
        {
            title += "  " + Loc.Get("MultiFile_FileIncomplete");
        }

        return _outcomeTexts.TryGetValue(fileIndex, out string? outcome) ? title + "  — " + outcome : title;
    }

    /// <summary>一致の行: オフセット、長さ、一致データ (Hex / テキスト。先頭 16 バイト)。データがまだなければ「…」。</summary>
    private static string MatchTitle(SearchMatch m, byte[]? data, int got)
    {
        string hex = data is null ? "…" : string.Join(' ', data.Take(got).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) + (m.Length > got ? " …" : string.Empty);
        string text = data is null ? string.Empty : new([.. data.Take(got).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
        return Loc.Format("MultiFile_MatchRow", StatusFormat.Hex(m.Offset), m.Length.ToString("N0", CultureInfo.CurrentCulture), hex, text);
    }

    /// <summary>見えている一致の行のデータを、UI のスレッドの外で読む (ファイルごとに 1 回開く)。</summary>
    private void QueuePreview(MultiFileRow row)
    {
        _previewQueue.Enqueue(row);
        if (Interlocked.Exchange(ref _previewRunning, 1) == 0)
        {
            _ = Task.Run(LoadPreviews);
        }
    }

    private void LoadPreviews()
    {
        while (true)
        {
            var rows = new List<MultiFileRow>();
            while (_previewQueue.TryDequeue(out MultiFileRow? r))
            {
                rows.Add(r);
            }

            var titles = new List<(MultiFileRow Row, string Title)>();
            foreach (IGrouping<FileSearchResult, MultiFileRow> group in rows.GroupBy(r => r.File))
            {
                FileSearchResult file = group.Key;
                Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null;
                try
                {
                    if (file.Snapshot is null)
                    {
                        handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                using (handle)
                {
                    foreach (MultiFileRow row in group)
                    {
                        SearchMatch m = row.Match!.Value;
                        byte[] buffer = new byte[(int)Math.Min(16, m.Length)];
                        int got = 0;
                        try
                        {
                            got = file.Snapshot is { } snapshot ? snapshot.Read(m.Offset, buffer).BytesReturned
                                : handle is not null ? RandomAccess.Read(handle, buffer, m.Offset) : 0;
                        }
                        catch (IOException)
                        {
                        }

                        titles.Add((row, MatchTitle(m, buffer, got)));
                    }
                }
            }

            if (titles.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach ((MultiFileRow row, string title) in titles)
                    {
                        row.Title = title;
                    }
                });
            }

            Volatile.Write(ref _previewRunning, 0);
            if (_previewQueue.IsEmpty || Interlocked.Exchange(ref _previewRunning, 1) == 1)
            {
                return;
            }
        }
    }

    /// <summary>行のチェックが変わった: 結果の並びに記録し、同じファイルの見えている行の表示を合わせる。</summary>
    private void RowChecked(MultiFileRow row, bool? value)
    {
        if (_view is null)
        {
            return;
        }

        _view.SetChecked(row.FileIndex, row.MatchIndex, value ?? true);
        ShowChecks(row.FileIndex);
        Validate();
    }

    private void ShowChecks(int fileIndex)
    {
        foreach (MultiFileRow r in Rows.Realized.Where(r => r.FileIndex == fileIndex))
        {
            r.ShowChecked(_view!.IsChecked(r.FileIndex, r.MatchIndex));
        }
    }

    /// <summary>
    /// 見出し: 一致のあったファイル数・件数・状態、一致のなかったファイル数、上限で止めた旨、スキップしたファイル (FIND-30 の仕様 7)。
    /// スキップしたファイルの一覧は、メニューを開くときに作る。
    /// </summary>
    private void UpdateSummary()
    {
        if (_results is not { } r)
        {
            Summary.Text = string.Empty;
            return;
        }

        string state = Loc.Get(r.State switch
        {
            MultiFileSearchState.Running => "SearchResults_State_Running",
            MultiFileSearchState.Cancelled => "SearchResults_State_Cancelled",
            MultiFileSearchState.LimitReached => "MultiFile_State_Limit",
            _ => "SearchResults_State_Completed",
        });
        string text = Loc.Format("MultiFile_Summary", r.FileCount, r.MatchCount, r.FilesWithoutMatches, state);
        if (r.State == MultiFileSearchState.LimitReached)
        {
            text += " " + (r.SpillFailed
                ? Loc.Format("MultiFile_SpillFailed", r.SpillError ?? string.Empty)
                : Loc.Format("MultiFile_LimitNote", r.Limit.ToString("N0", CultureInfo.CurrentCulture)));
        }

        Summary.Text = text;
        int skipped = r.SkippedCount;
        SkippedButton.Visibility = Show(skipped > 0);
        string skippedText = Loc.Format("MultiFile_Skipped", skipped);
        SkippedButton.Content = skippedText;
        AutomationProperties.SetName(SkippedButton, skippedText);
        ContinueButton.Visibility = Show(r.CanContinue && _running is null);
    }

    private void SkippedMenu_Opening(object sender, object e)
    {
        SkippedMenu.Items.Clear();
        foreach (SkippedFile s in _results?.SkippedHead(200) ?? [])
        {
            SkippedMenu.Items.Add(new MenuFlyoutItem { Text = Loc.Format("MultiFile_SkippedItem", s.Path, ReasonText(s.Reason, s.Message)), IsEnabled = false });
        }
    }

    internal static string ReasonText(FileSkipReason reason, string message)
    {
        string text = Loc.Get("MultiFile_Reason_" + reason);
        if (message.Length == 0)
        {
            return text;
        }

        return reason switch
        {
            FileSkipReason.IoError or FileSkipReason.RegexTimedOut => $"{text} ({message})",
            FileSkipReason.CannotReplace when Enum.TryParse(message, out ReplaceIssue issue) && Loc.TryGet("MultiFile_Issue_" + issue) is { } detail
                => $"{text} ({detail})",
            _ => text,
        };
    }

    private void Open(MultiFileRow row)
    {
        if (row.Match is { } m)
        {
            OpenRequested?.Invoke(this, (row.File.Path, m.Offset, m.Length));
        }
        else if (row.File.MatchCount > 0)
        {
            SearchMatch first = row.File.MatchAt(0);
            OpenRequested?.Invoke(this, (row.File.Path, first.Offset, first.Length));
        }
    }

    // ---- 置換 (FIND-31) ----

    /// <summary>「置換を実行」: 確認ダイアログのあと、チェックした一致を置換して保存する。</summary>
    public async Task ReplaceAsync()
    {
        if (_results is not { } results || _view is not { } view || _template is not { } template || _built is not { } built || _running is not null)
        {
            return;
        }

        SearchPattern pattern = built.Pattern;
        List<int> work = [.. Enumerable.Range(0, view.FileCount).Where(f => view.CheckedCount(f) > 0)];
        if (work.Count == 0)
        {
            return;
        }

        long count = work.Sum(f => (long)view.CheckedCount(f));
        bool backup = BackupChoice.IsChecked == true;
        ConfirmChoice choice = Confirm is null ? ConfirmChoice.Cancel : await Confirm(new ConfirmRequest(
            "MultiFileReplaceConfirm",
            Loc.Get("MultiFile_Confirm_Title"),
            Loc.Format("MultiFile_Confirm_Body", work.Count, count, Loc.Get(backup ? "MultiFile_Confirm_Backup" : "MultiFile_Confirm_NoBackup")),
            Loc.Get("MultiFile_Confirm_Replace"),
            null,
            Loc.Get("MultiFile_Confirm_Cancel")));
        if (choice != ConfirmChoice.Primary)
        {
            return;
        }

        var options = new MultiFileReplaceOptions
        {
            Template = template,
            ReplaceOptions = CurrentReplaceOptions(),
            Backup = backup,
            ClearReadOnly = ClearReadOnlyChoice.IsChecked == true,
            JournalDirectory = Path.Combine(Path.GetTempPath(), "HexEditor", "multifile-replace"),
        };
        Dictionary<string, EditorState> open = (OpenDocuments?.Invoke() ?? []).GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Editor, StringComparer.OrdinalIgnoreCase);
        var cts = new CancellationTokenSource();
        _running = cts;
        CancelButton.IsEnabled = true;
        Validate();
        var outcomes = new List<FileReplaceOutcome>();
        AppLog.Info($"Multi-file replace: start ({work.Count} file(s), {count} match(es))");
        try
        {
            async Task Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                }

                foreach (int f in work)
                {
                    token.ThrowIfCancellationRequested();
                    FileSearchResult file = view.File(f);
                    op?.ReportDetail(Loc.Format("MultiFile_ReplaceProgress", outcomes.Count, work.Count, file.Path));
                    IReadOnlyList<SearchMatch> matches = await OnUiAsync(() => view.CheckedMatches(f));
                    FileReplaceOutcome outcome;
                    if (open.TryGetValue(file.Path, out EditorState? editor))
                    {
                        // 開いているドキュメントはエディタ上で置換する (Undo で戻せる。保存は利用者が行う。仕様 6)。
                        outcome = await OnUiAsync(() => MultiFileSearch.ReplaceInEditor(editor, file.Path, matches, pattern, options, Loc.Get("History_ReplaceAll")));
                    }
                    else
                    {
                        outcome = await Task.Run(() => MultiFileSearch.ReplaceInFile(file, matches, pattern, options, null, token), token);
                    }

                    outcomes.Add(outcome);
                    DispatcherQueue.TryEnqueue(() => ShowOutcome(f, outcome));
                }
            }

            if (Operations is null)
            {
                await Work(null);
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_MultiFileReplace"), OperationKind.WritesExternal, null, null, Work);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Multi-file replace: cancelled");
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }

            // キャンセルなどで置換しなかったファイルは「処理していない」と表示する (どのファイルまで置換したか分かるように)。
            for (int i = outcomes.Count; i < work.Count; i++)
            {
                FileReplaceOutcome skipped = new(view.File(work[i]).Path, FileReplaceStatus.NotProcessed, 0, null, string.Empty);
                outcomes.Add(skipped);
                ShowOutcome(work[i], skipped);
            }

            CancelButton.IsEnabled = false;
            Outcomes = outcomes;
            int replacedFiles = outcomes.Count(o => o.Status is FileReplaceStatus.Replaced or FileReplaceStatus.ReplacedInEditor && o.Count > 0);
            long replaced = outcomes.Sum(o => o.Status is FileReplaceStatus.Replaced or FileReplaceStatus.ReplacedInEditor ? o.Count : 0);
            int skippedFiles = outcomes.Count(o => o.Status is FileReplaceStatus.Skipped or FileReplaceStatus.Failed);
            int notProcessed = outcomes.Count(o => o.Status == FileReplaceStatus.NotProcessed);
            Summary.Text = Loc.Format("MultiFile_ReplaceSummary", replacedFiles, replaced, skippedFiles, notProcessed);
            AppLog.Info($"Multi-file replace: end ({replacedFiles} file(s), {replaced} replacement(s), {skippedFiles} skipped, {notProcessed} not processed)");
            Validate();
        }
    }

    /// <summary>ファイルの行に置換の結果を出す (仕様 9)。</summary>
    private void ShowOutcome(int fileIndex, FileReplaceOutcome outcome)
    {
        _outcomeTexts[fileIndex] = OutcomeText(outcome);
        foreach (MultiFileRow r in Rows.Realized.Where(r => r.FileIndex == fileIndex && r.IsFile))
        {
            r.Title = FileTitle(fileIndex);
        }
    }

    internal static string OutcomeText(FileReplaceOutcome o) => o.Status switch
    {
        FileReplaceStatus.Replaced => Loc.Format("MultiFile_Outcome_Replaced", o.Count),
        FileReplaceStatus.ReplacedInEditor => Loc.Format("MultiFile_Outcome_InEditor", o.Count),
        FileReplaceStatus.Skipped => Loc.Format("MultiFile_Outcome_Skipped", ReasonText(o.Reason ?? FileSkipReason.IoError, o.Message)),
        FileReplaceStatus.Failed => Loc.Format("MultiFile_Outcome_Failed", ReasonText(o.Reason ?? FileSkipReason.IoError, o.Message)),
        _ => Loc.Get("MultiFile_Outcome_NotProcessed"),
    };

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return Task.FromResult(action());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>実行中の検索・置換を取り消す。</summary>
    public void Cancel() => _running?.Cancel();

    // ---- 検索バーの条件の取り込み (FIND-30 の仕様 1) ----

    /// <summary>
    /// 検索バーの今の条件を取り込む。パネルの欄にあるもの (種類・検索語・オプション・位置の条件) は欄に入れ、欄にないもの
    /// (複数の文字コード・複数の語) はまとめて持つ (「解除」で外す)。
    /// </summary>
    internal void UseQuery(SearchQuery q)
    {
        _ready = false;
        KindChoice.SelectedIndex = (int)q.Kind;
        Query.Text = q.Text;
        if (EncodingChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == q.EncodingId) is { } item)
        {
            EncodingChoice.SelectedItem = item;
        }

        CaseChoice.IsChecked = q.CaseSensitive;
        WordChoice.IsChecked = q.WholeWord;
        EscapeChoice.IsChecked = q.UseEscapes;
        AlignChoice.IsChecked = q.AlignToCharacters;
        IntBitsChoice.SelectedIndex = Math.Max(0, NumericSearch.IntegerSizes.ToList().IndexOf(q.IntegerBits));
        SignChoice.SelectedIndex = (int)q.Sign;
        EndianChoice.SelectedIndex = (int)q.Endian;
        FloatChoice.SelectedIndex = (int)q.FloatFormat;
        ToleranceChoice.SelectedIndex = (int)q.Tolerance;
        ToleranceValue.Text = q.ToleranceText;
        RangeExcludeChoice.IsChecked = q.RangeExclude;
        RegexIgnoreCase.IsChecked = q.RegexIgnoreCase;
        RegexMultiline.IsChecked = q.RegexMultiline;
        RegexSingleline.IsChecked = q.RegexSingleline ?? false;
        MaskChoice.IsChecked = q.Mask;
        MaskModeChoice.SelectedIndex = q.MaskBits ? 1 : 0;
        MaskQuery.Text = q.MaskText;
        MismatchChoice.IsChecked = q.Mismatch;
        MismatchAlignChoice.IsChecked = q.MismatchAligned;
        PositionModulus.Text = q.Position is { IsNone: false } p ? p.Modulus.ToString(CultureInfo.InvariantCulture) : string.Empty;
        PositionRemainder.Text = q.Position is { IsNone: false } r ? r.Remainder.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _extras = q.Terms is not null || q.Encodings.Count > 0 ? new SearchQuery { Terms = q.Terms, TermLabels = q.TermLabels, Encodings = q.Encodings } : null;
        _ready = true;
        Validate();
    }

    private void UseFindBar_Click(object sender, RoutedEventArgs e)
    {
        if (FindBarQuery?.Invoke() is { } q)
        {
            UseQuery(q);
        }
    }

    private void ClearExtras_Click(object sender, RoutedEventArgs e)
    {
        _extras = null;
        Validate();
    }

    // ---- 対象の指定の保存 (FIND-30 の仕様 9) ----

    private static List<(string Name, JsonObject Targets)> LoadTargetSets()
    {
        var sets = new List<(string, JsonObject)>();
        if (Commands.CommandService.State?.Get(TargetSetsKey) is JsonArray array)
        {
            foreach (JsonObject o in array.OfType<JsonObject>())
            {
                if (o["name"]?.GetValue<string>() is { } name && o["targets"] is JsonObject t)
                {
                    sets.Add((name, (JsonObject)t.DeepClone()));
                }
            }
        }

        return sets;
    }

    private void FillTargetSets()
    {
        TargetSetChoice.SelectionChanged -= TargetSet_Changed;
        TargetSetChoice.Items.Clear();
        foreach ((string name, JsonObject targets) in LoadTargetSets())
        {
            TargetSetChoice.Items.Add(new ComboBoxItem { Content = name, Tag = targets });
        }

        TargetSetChoice.PlaceholderText = Loc.Get("MultiFile_TargetSet_Placeholder");
        TargetSetChoice.SelectionChanged += TargetSet_Changed;
    }

    private void TargetSet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetSetChoice.SelectedItem is ComboBoxItem { Tag: JsonObject json })
        {
            MultiFileTargets t = MultiFileTargets.FromJson(json);
            Folders.Clear();
            foreach (string f in t.Folders)
            {
                Folders.Add(f);
            }

            SubfoldersChoice.IsChecked = t.IncludeSubfolders;
            DepthInput.Text = t.MaxDepth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            IncludeInput.Text = t.IncludeMasks;
            ExcludeInput.Text = t.ExcludeMasks;
            MinSizeInput.Text = t.MinSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            MaxSizeInput.Text = t.MaxSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            HiddenChoice.IsChecked = t.IncludeHidden;
            LinksChoice.IsChecked = t.FollowLinks;
            OpenDocsChoice.IsChecked = t.IncludeOpenDocuments;
            Validate();
        }
    }

    private void TargetSetSave_Click(object sender, RoutedEventArgs e)
    {
        string name = TargetSetName.Text.Trim();
        if (name.Length == 0 || BuildTargets(out _) is not { } targets)
        {
            return;
        }

        List<(string Name, JsonObject Targets)> sets = LoadTargetSets();
        sets.RemoveAll(s => s.Name == name);
        sets.Add((name, targets.ToJson()));
        Commands.CommandService.State?.Set(TargetSetsKey, new JsonArray([.. sets.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["targets"] = s.Targets })]));
        TargetSetName.Text = string.Empty;
        FillTargetSets();
    }

    // ---- イベント ----

    private void Option_Changed(object sender, SelectionChangedEventArgs e) => Validate();

    private void Check_Changed(object sender, RoutedEventArgs e) => Validate();

    private void Text_Changed(object sender, TextChangedEventArgs e) => Validate();

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Continue_Click(object sender, RoutedEventArgs e) => await ContinueAsync();

    private async void Replace_Click(object sender, RoutedEventArgs e) => await ReplaceAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = FolderInput.Text.Trim().Trim('"');
        if (path.Length > 0 && !Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(path);
            FolderInput.Text = string.Empty;
            Validate();
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId) { SettingsIdentifier = "HexEditor.MultiFileFolder" };
        if ((await picker.PickSingleFolderAsync())?.Path is { } path && !Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(path);
            Validate();
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string path)
        {
            Folders.Remove(path);
            Validate();
        }
    }

    private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultList.SelectedItem is MultiFileRow row)
        {
            Open(row);
        }
    }

    private void ResultList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ResultList.SelectedItem is MultiFileRow row)
        {
            e.Handled = true;
            Open(row);
        }
        else if (e.Key == VirtualKey.Space && IsReplaceMode && ResultList.SelectedItem is MultiFileRow toggle)
        {
            e.Handled = true;
            toggle.Checked = toggle.Checked != true;
        }
    }

    /// <summary>サイズ・位置の条件の入力式の文脈 (名前と読み取りは使わない)。</summary>
    private sealed class NoContext : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
