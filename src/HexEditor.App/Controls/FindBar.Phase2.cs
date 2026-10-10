using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索バーのフェーズ 2 の検索: 正規表現 (FIND-18、FIND-19)、値の範囲 (FIND-15)、ビットマスク (FIND-16)、位置の条件 (FIND-17)、
/// 一致しない箇所 (FIND-25)、複数の文字コード (FIND-08)、複数の語 (FIND-26)。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>文字コードの「複数」の項目の名前 (FIND-08 の仕様 1)。</summary>
    internal const string MultiEncodingId = "multi";

    /// <summary>正規表現の時間の上限の設定 (秒。FIND-18 の仕様 6)。</summary>
    public const string RegexTimeLimitKey = "search.regex.timeLimit";

    /// <summary>名前を付けて保存した文字コードの組み合わせ (アプリの状態 state.json。FIND-08 の仕様 2)。</summary>
    public const string EncodingSetsKey = "search.encodingSets";

    /// <summary>複数の文字コードの既定の組み合わせ (FIND-08 の仕様 2)。</summary>
    internal static readonly string[] DefaultMultiEncodings = ["ascii", "utf-8", "utf-16le", "utf-16be"];

    private List<string> _multiEncodings = [.. DefaultMultiEncodings];

    /// <summary>複数の文字コードの検索で、種類 (一致した文字コード) ごとの文字コード (置換語の符号化に使う)。</summary>
    private IReadOnlyList<Encoding>? _variantEncodings;

    /// <summary>複数の文字コードで、符号化できずに除いた文字コード (警告。FIND-08 の仕様 6)。</summary>
    private IReadOnlyList<string> _excludedEncodings = [];

    /// <summary>検索欄の既定の説明 (XAML の x:Uid で入れたもの)。</summary>
    private string _queryPlaceholder = string.Empty;

    private bool _positionChosen;
    private bool _positionAutomatic;
    private bool _settingPosition;
    private bool _positionValid = true;

    /// <summary>複数語の一覧 (FIND-26 の画面)。</summary>
    public ObservableCollection<TermRow> Terms { get; } = [];

    /// <summary>設定画面を開く (「一致の最大長」の変更は設定画面へのリンク。FIND-18 の画面)。引数は区画と設定のキー。</summary>
    public event EventHandler<(string Category, string Key)>? SettingsRequested;

    /// <summary>複数語の検索か。</summary>
    internal bool IsMultiTerm => MultiTermToggle.IsChecked == true;

    /// <summary>複数の文字コードを選んでいるか (FIND-08)。</summary>
    private bool IsMultiEncoding => SelectedEncoding == MultiEncodingId;

    /// <summary>一致しない箇所の検索か (FIND-25)。</summary>
    private bool IsMismatch => Kind == SearchKind.Hex && MismatchChoice.IsChecked == true && !IsMultiTerm;

    /// <summary>ビットマスクの検索か (FIND-16)。</summary>
    private bool IsMask => Kind == SearchKind.Hex && MaskChoice.IsChecked == true && !IsMismatch && !IsMultiTerm;

    /// <summary>範囲の検索か (FIND-15)。</summary>
    private bool IsRange => Kind is SearchKind.Integer or SearchKind.Float && !IsMultiTerm && NumericRange.IsRange(Query.Text);

    /// <summary>テスト用: 複数の文字コードの選択。</summary>
    internal IReadOnlyList<string> MultiEncodings => _multiEncodings;

    /// <summary>テスト用: 複数の文字コードを選ぶ (最大 8)。</summary>
    internal void SetMultiEncodings(IReadOnlyList<string> ids)
    {
        _multiEncodings = [.. ids.Take(SearchPattern.MaxEncodings)];
        SelectEncoding(MultiEncodingId);
        Validate();
    }

    /// <summary>文字コードのドロップダウンで、名前 <paramref name="id"/> の項目を選ぶ。</summary>
    private void SelectEncoding(string id)
    {
        foreach (object item in EncodingChoice.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, id, StringComparison.OrdinalIgnoreCase))
            {
                EncodingChoice.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>コンストラクターから呼ぶ。</summary>
    private void InitializePhase2()
    {
        _queryPlaceholder = Query.PlaceholderText;
        EncodingChoice.Items.Insert(1, new ComboBoxItem { Content = Loc.Get("Find_Encoding_Multi"), Tag = MultiEncodingId });
        AutomationProperties.SetName(MultiTermToggle, Loc.Get("Find_MultiTerm_Name"));
        ToolTipService.SetToolTip(MultiTermToggle, Loc.Get("Find_MultiTerm_Name"));
        AutomationProperties.SetName(MaskModeChoice, Loc.Get("Find_MaskMode_Name"));
        AutomationProperties.SetName(PositionChoice, Loc.Get("Find_Position_Name"));
        AutomationProperties.SetName(PositionBaseChoice, Loc.Get("Find_PositionBase_Name"));
        AutomationProperties.SetName(MultiEncodingButton, Loc.Get("Find_MultiEncoding_Name"));
        AutomationProperties.SetName(EncodingSetChoice, Loc.Get("Find_EncodingSet_Name"));
        AutomationProperties.SetName(TermList, Loc.Get("Find_TermList_Name"));
        MultiEncodingHint.Text = Loc.Format("Find_MultiEncoding_Hint", SearchPattern.MaxEncodings);
        Terms.CollectionChanged += (_, _) => Validate();
        SyncSinglelineToKind();
    }

    // ---- `s` フラグの既定 (FIND-18 の仕様 2、FIND-19 の仕様 3) ----

    /// <summary>テキストの正規表現の `s` フラグ (既定オフ)。利用者が切り替えたらこのウィンドウの間は覚えておく。</summary>
    private bool _singlelineText;

    /// <summary>バイト列の正規表現の `s` フラグ (既定オン: `.` は 0A を含む任意のバイトに一致する)。</summary>
    private bool _singlelineBytes = true;

    /// <summary>チェックボックスが今どちらの種類の値を表しているか。</summary>
    private SearchKind? _singlelineKind;

    private bool _settingSingleline;

    /// <summary>種類が正規表現に変わったら、`s` のチェックボックスをその種類の値 (既定または利用者が選んだ値) にする。</summary>
    private void SyncSinglelineToKind()
    {
        SearchKind kind = Kind;
        if (kind is not (SearchKind.RegexText or SearchKind.RegexBytes) || kind == _singlelineKind)
        {
            return;
        }

        _singlelineKind = kind;
        _settingSingleline = true;
        try
        {
            RegexSingleline.IsChecked = kind == SearchKind.RegexBytes ? _singlelineBytes : _singlelineText;
        }
        finally
        {
            _settingSingleline = false;
        }
    }

    private void RegexSingleline_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingSingleline && _singlelineKind is { } kind)
        {
            bool value = RegexSingleline.IsChecked == true;
            if (kind == SearchKind.RegexBytes)
            {
                _singlelineBytes = value;
            }
            else
            {
                _singlelineText = value;
            }
        }

        Validate();
    }

    /// <summary>テスト用: 符号化できずに除いた文字コードの警告 (表示していなければ空)。</summary>
    internal string EncodingWarningMessage => EncodingWarning.Visibility == Visibility.Visible ? EncodingWarningText.Text : string.Empty;

    /// <summary>テスト用: `s` フラグのチェックボックスの状態。</summary>
    internal bool RegexSinglelineChecked => RegexSingleline.IsChecked == true;

    // ---- パターンの作成 ----

    /// <summary>フェーズ 2 の種類・オプションのパターン。該当しなければ null (従来の作り方)。</summary>
    private SearchPattern? BuildPhase2Pattern(SearchKind kind)
    {
        _variantEncodings = null;
        _excludedEncodings = [];
        if (IsMultiTerm)
        {
            return BuildMultiTerm();
        }

        switch (kind)
        {
            case SearchKind.RegexText:
                return RegexSearch.Text(Query.Text, SearchEncoding, CurrentRegexOptions());
            case SearchKind.RegexBytes:
                return RegexSearch.Bytes(Query.Text, CurrentRegexOptions());
            case SearchKind.Hex when IsMismatch:
                return SearchPattern.Mismatch(Query.Text, MismatchAlignChoice.IsChecked == true);
            case SearchKind.Hex when IsMask:
                return MaskModeChoice.SelectedIndex == 1
                    ? SearchPattern.FromBitPattern(Query.Text)
                    : SearchPattern.FromValueAndMask(Query.Text, MaskQuery.Text);
            case SearchKind.Integer when IsRange:
                return NumericRange.Integer(Query.Text, new IntegerSearchOptions
                {
                    Bits = SelectedBits,
                    Sign = (IntegerSign)Math.Max(0, SignChoice.SelectedIndex),
                    Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
                }, RangeExcludeChoice.IsChecked == true, Editor is { } editor ? new EditorExpressionContext(editor) : null);
            case SearchKind.Float when IsRange:
                return NumericRange.Float(Query.Text, new FloatSearchOptions
                {
                    Format = (FloatFormat)Math.Max(0, FloatChoice.SelectedIndex),
                    Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
                }, RangeExcludeChoice.IsChecked == true);
            case SearchKind.Text when IsMultiEncoding:
                var encodings = new List<(string Name, Encoding Encoding)>();
                foreach (string id in _multiEncodings)
                {
                    if (TextEncodings.FromCatalogId(id) is { } e)
                    {
                        encodings.Add((EncodingCatalog.Find(id) is { } entry ? MainWindow.EncodingDisplayText(entry) : id, e));
                    }
                }

                SearchPattern pattern = SearchPattern.FromTextEncodings(Query.Text, encodings, CurrentTextOptions(), out IReadOnlyList<string> excluded);
                _excludedEncodings = excluded;

                // 種類 (まとめた名前の最初の文字コード) ごとの文字コード。
                _variantEncodings = [.. pattern.Variants.Select(v => encodings.First(e => v.StartsWith(e.Name, StringComparison.Ordinal)).Encoding)];
                return pattern;
            default:
                return null;
        }
    }

    private TextSearchOptions CurrentTextOptions() => new()
    {
        CaseSensitive = CaseChoice.IsChecked == true,
        UseEscapes = EscapeChoice.IsChecked == true,
        AlignToCharacters = AlignChoice.IsChecked == true,
        WholeWord = WordChoice.IsChecked == true,
    };

    /// <summary>正規表現の条件 (フラグ、一致の最大長、時間の上限)。</summary>
    private RegexSearchOptions CurrentRegexOptions() => new()
    {
        IgnoreCase = RegexIgnoreCase.IsChecked == true,
        Multiline = RegexMultiline.IsChecked == true,
        Singleline = RegexSingleline.IsChecked == true,
        MaxMatchLength = MaxMatchLength,
        TimeLimit = TimeSpan.FromSeconds(Math.Clamp(App.Settings?.GetDouble(RegexTimeLimitKey, 2) ?? 2, 0.1, 60)),
        AlignToCharacters = AlignChoice.IsChecked == true && Kind == SearchKind.RegexText,
    };

    /// <summary>「一致の最大長」の設定 (FIND-01 の仕様 3)。</summary>
    private static int MaxMatchLength =>
        Math.Clamp(App.Settings?.GetInt(MaxMatchLengthKey, SearchPattern.DefaultMaxMatchLength) ?? SearchPattern.DefaultMaxMatchLength, 1, SearchPattern.MaxMaxMatchLength);

    /// <summary>複数語のパターン (FIND-26)。不正な語は行ごとに赤枠と説明文で示す。</summary>
    private SearchPattern? BuildMultiTerm()
    {
        SearchEndian endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex);
        SearchTerm[] terms = [.. Terms.Select(t => t.ToTerm(endian))];
        string[] labels = [.. Terms.Select(t => t.Label())];
        SearchPattern? pattern = SearchTerms.Build(terms, labels, CurrentTextOptions(), TextEncodings.FromCatalogId,
            out IReadOnlyList<(int Row, PatternException Error)> errors, Editor is { } e ? new EditorExpressionContext(e) : null);
        for (int i = 0; i < Terms.Count; i++)
        {
            (int Row, PatternException Error)? error = errors.FirstOrDefault(x => x.Row == i) is var x && x.Error is not null ? x : null;
            Terms[i].Error = error is { } er ? ErrorText(er.Error) : string.Empty;
        }

        TermSummary.Text = Loc.Format("Find_TermSummary", Terms.Count(t => t.Enabled), Terms.Count);
        if (pattern is null)
        {
            throw errors.FirstOrDefault().Error ?? new PatternException(PatternError.Empty);
        }

        return pattern;
    }

    /// <summary>
    /// 位置の条件 (FIND-17) を読む。条件なしなら null、入力が不正なら <see cref="_positionValid"/> を false にして null。表示も更新する。
    /// </summary>
    private PositionCondition? ReadPosition()
    {
        _positionValid = true;
        bool custom = PositionChoice.SelectedIndex == 1;
        PositionModulus.Visibility = PositionRemainder.Visibility = PositionBaseChoice.Visibility = Show(custom);
        SetBorder(PositionModulus, null);
        SetBorder(PositionRemainder, null);
        if (!custom)
        {
            PositionInfo.Text = string.Empty;
            return null;
        }

        if (Editor is not { } editor)
        {
            PositionInfo.Text = string.Empty;
            return null;
        }

        Brush critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var context = new EditorExpressionContext(editor);
        // 周期と余りは数 (接頭辞のない数値は 10 進。`0x200`、`sector` なども書ける)。
        bool xOk = ExpressionEvaluator.TryEvaluate(PositionModulus.Text, context, out long x, out _, DefaultRadix.Decimal) && x >= 1 && x <= PositionCondition.MaxModulus;
        bool yOk = ExpressionEvaluator.TryEvaluate(PositionRemainder.Text.Length == 0 ? "0" : PositionRemainder.Text, context, out long y, out _, DefaultRadix.Decimal)
            && y >= 0 && (!xOk || y < x);
        if (!xOk || !yOk)
        {
            SetBorder(PositionModulus, xOk ? null : critical);
            SetBorder(PositionRemainder, yOk ? null : critical);
            _positionValid = false;
            PositionInfo.Text = !xOk
                ? Loc.Format("Find_Error_PositionModulus", PositionCondition.MaxModulus.ToString("N0", CultureInfo.CurrentCulture))
                : Loc.Format("Find_Error_PositionRemainder", (x - 1).ToString("N0", CultureInfo.CurrentCulture));
            return null;
        }

        long baseAddress = PositionBaseChoice.SelectedIndex == 1 ? editor.Document.Source.BaseAddress : 0;
        PositionCondition condition = PositionCondition.Create(x, y, baseAddress);
        PositionInfo.Text = y == 0 && x is 2 or 4 or 8 or 16
            ? Loc.Format("Find_PositionInfo_Multiple", x.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.Format("Find_PositionInfo", x.ToString("N0", CultureInfo.CurrentCulture), y.ToString("N0", CultureInfo.CurrentCulture));
        return condition;
    }

    /// <summary>
    /// 範囲の検索では、位置の条件の既定をサイズの倍数にする (FIND-15 の仕様 6)。利用者が位置の条件を選んだら変えない。
    /// 範囲の検索でなくなったら、自動で付けた条件を外す。
    /// </summary>
    private void ApplyRangeAlignment()
    {
        if (_positionChosen)
        {
            return;
        }

        int size = Kind == SearchKind.Float ? NumericSearch.ByteLength((FloatFormat)Math.Max(0, FloatChoice.SelectedIndex)) : SelectedBits / 8;
        _settingPosition = true;
        if (IsRange)
        {
            PositionChoice.SelectedIndex = 1;
            PositionModulus.Text = size.ToString(CultureInfo.InvariantCulture);
            PositionRemainder.Text = "0";
            PositionBaseChoice.SelectedIndex = 0;
            _positionAutomatic = true;
        }
        else if (_positionAutomatic)
        {
            PositionChoice.SelectedIndex = 0;
            _positionAutomatic = false;
        }

        _settingPosition = false;
    }

    /// <summary>種類・オプションに応じた、フェーズ 2 の項目の表示 (FIND-04 の仕様 4)。</summary>
    private void UpdatePhase2Visibility()
    {
        SearchKind kind = Kind;
        bool multiTerm = IsMultiTerm;
        bool regex = kind is SearchKind.RegexText or SearchKind.RegexBytes;
        bool hex = kind == SearchKind.Hex;
        Query.Visibility = Show(!multiTerm);
        MultiTermRow.Visibility = Show(multiTerm && IsOpen);
        RegexIgnoreCase.Visibility = RegexMultiline.Visibility = RegexSingleline.Visibility = RegexNote.Visibility = RegexSettingsLink.Visibility = Show(regex && !multiTerm);
        MaskChoice.Visibility = MismatchChoice.Visibility = Show(hex && !multiTerm);
        MismatchAlignChoice.Visibility = Show(IsMismatch);
        MaskModeChoice.Visibility = Show(IsMask);
        MaskQuery.Visibility = Show(IsMask && MaskModeChoice.SelectedIndex == 0);
        RangeExcludeChoice.Visibility = Show(IsRange);
        MultiEncodingButton.Visibility = Show(kind == SearchKind.Text && IsMultiEncoding && !multiTerm);

        // 符号化できない文字コードは除いて検索し、警告を出す (FIND-08 の仕様 6)。
        bool excluded = kind == SearchKind.Text && IsMultiEncoding && !multiTerm && _pattern is not null && _excludedEncodings.Count > 0;
        EncodingWarningText.Text = excluded ? Loc.Format("Find_EncodingsExcluded", string.Join(", ", _excludedEncodings)) : string.Empty;
        EncodingWarning.Visibility = Show(excluded);
        bool custom = PositionChoice.SelectedIndex == 1;
        foreach (Button preset in new[] { PositionPreset2, PositionPreset4, PositionPreset8, PositionPreset16, PositionPresetSector })
        {
            preset.Visibility = Visibility.Visible;
        }

        PositionBaseChoice.Visibility = Show(custom);

        // 一致しない箇所の検索では、検索欄の見出しを「繰り返しのパターン」にする (FIND-25 の画面)。
        string label = Loc.Get(IsMismatch ? "Find_Query_Repeat" : IsMask && MaskModeChoice.SelectedIndex == 1 ? "Find_Query_Bits" : "Find_Query_Name");
        AutomationProperties.SetName(Query, label);
        Query.PlaceholderText = IsMismatch ? Loc.Get("Find_Query_Repeat")
            : IsMask && MaskModeChoice.SelectedIndex == 1 ? Loc.Get("Find_Query_Bits")
            : regex ? Loc.Get("Find_Query_Regex") : _queryPlaceholder;
        RegexNote.Text = regex
            ? Loc.Format("Find_RegexNote", MaxMatchLength.ToString("N0", CultureInfo.CurrentCulture))
              + (_pattern is { IsRegex: true } p && !RegexSearch.IsNonBacktracking(p) ? " " + Loc.Get("Find_RegexBacktracking") : string.Empty)
            : string.Empty;
    }

    /// <summary>「複数の語を検索」のコマンド (FIND-26): 検索欄を複数行の一覧にする。</summary>
    public void ShowMultiTerm()
    {
        OptionsToggle.IsChecked = true;
        MultiTermToggle.IsChecked = true;
    }

    /// <summary>「一致しない箇所を検索」のコマンド (FIND-25): 種類「Hex」で「一致しない箇所を探す」をオンにする。</summary>
    public void ShowMismatch()
    {
        KindChoice.SelectedIndex = (int)SearchKind.Hex;
        OptionsToggle.IsChecked = true;
        MismatchChoice.IsChecked = true;
        Query.Focus(FocusState.Programmatic);
    }

    /// <summary>テスト用: 検索欄の見出し (スクリーンリーダーが読む名前)。</summary>
    internal string QueryLabel => AutomationProperties.GetName(Query);

    /// <summary>テスト用: 位置の条件の表示。</summary>
    internal string PositionText => PositionInfo.Text;

    /// <summary>条件の鍵 (変わったら直前の一致と件数を忘れる)。</summary>
    private string Phase2Key() => string.Join('|', IsMultiTerm, string.Join(',', Terms.Select(t => $"{t.Enabled}{t.KindIndex}{t.Text}{t.OptionIndex}")),
        string.Join(',', _multiEncodings), RegexIgnoreCase.IsChecked, RegexMultiline.IsChecked, RegexSingleline.IsChecked, MaskChoice.IsChecked,
        MaskModeChoice.SelectedIndex, MaskQuery.Text, MismatchChoice.IsChecked, MismatchAlignChoice.IsChecked, RangeExcludeChoice.IsChecked,
        PositionChoice.SelectedIndex, PositionModulus.Text, PositionRemainder.Text, PositionBaseChoice.SelectedIndex);

    /// <summary>変換結果の表示 (FIND-04 の仕様 7) のうち、フェーズ 2 の種類のもの。該当しなければ null。</summary>
    private string? Phase2Preview(SearchPattern pattern)
    {
        string position = PositionInfo.Text.Length > 0 && _pattern?.Position is not null ? "  " + PositionInfo.Text : string.Empty;
        if (pattern.IsRegex)
        {
            return Loc.Get(RegexSearch.IsNonBacktracking(pattern) ? "Find_RegexMode_Linear" : "Find_RegexMode_Backtracking") + position;
        }

        if (pattern.IsMismatch)
        {
            return Loc.Format("Find_MismatchPreview", pattern.Preview(32)) + position;
        }

        if (pattern.Numeric?.Range is { } range)
        {
            string type = pattern.Numeric.IsFloat
                ? NumericSearch.FormatName(pattern.Numeric.Format)
                : Loc.Format("Find_RangeType", pattern.Numeric.Bits, Loc.Get(pattern.Numeric.Sign switch
                {
                    IntegerSign.Signed => "Find_Sign_Signed_Short",
                    IntegerSign.Unsigned => "Find_Sign_Unsigned_Short",
                    _ => "Find_Sign_Either_Short",
                }));
            string endian = EndianChoice.SelectedIndex switch { 1 => "BE", 2 => "LE/BE", _ => "LE" };
            return Loc.Format(range.Exclude ? "Find_RangePreviewExclude" : "Find_RangePreview", range.Min ?? "", range.Max ?? "", type, endian) + position;
        }

        if (pattern.Parts.Count > 0)
        {
            string parts = string.Join("  ", pattern.Variants.Take(4).Select((v, i) => $"{v}: {pattern.Parts[i].Preview(8)}"));
            return (pattern.Variants.Count > 4 ? Loc.Format("Find_PartsMore", parts, pattern.Variants.Count) : parts) + position;
        }

        return position.Length > 0 ? null : null;
    }

    /// <summary>次を検索で一致した文字コード・語の名前 (FIND-08 の仕様 4、FIND-26 の仕様 6)。種類がなければ null。</summary>
    private string? MatchedVariant(SearchPattern pattern, SearchHit hit) =>
        pattern.VariantColumn is VariantColumn.Encoding or VariantColumn.Term && hit.Variant >= 0 && hit.Variant < pattern.Variants.Count
            ? Loc.Format(pattern.VariantColumn == VariantColumn.Encoding ? "Find_MatchedEncoding" : "Find_MatchedTerm", pattern.Variants[hit.Variant])
            : null;

    /// <summary>すべて検索の結果を作る (一致しない箇所の検索は専用の処理で探す。FIND-25 の仕様 4)。</summary>
    internal static SearchResults NewResults(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options) =>
        pattern.IsMismatch
            ? MismatchSearch.CreateResults(snapshot, pattern, options,
                Math.Clamp(App.Settings?.GetInt(MismatchSearch.MinRepeatKey, MismatchSearch.DefaultMinRepeat) ?? MismatchSearch.DefaultMinRepeat, 1, MismatchSearch.MaxMinRepeat))
            : new SearchResults(snapshot, pattern, options);

    /// <summary>この検索では置換できない理由 (一致しない箇所・複数語。null なら置換できる)。</summary>
    private string? ReplaceUnavailableReason() => IsMismatch || IsMultiTerm ? Loc.Get("Find_ReplaceUnsupported") : null;

    /// <summary>フェーズ 2 の種類の置換語。該当しなければ null。</summary>
    private ReplacementTemplate? BuildPhase2Template()
    {
        if (_pattern is { IsRegex: true } regex)
        {
            return ReplacementTemplate.FromRegex(regex, ReplaceQuery.Text);
        }

        if (_variantEncodings is { } encodings && Kind == SearchKind.Text)
        {
            bool escapes = EscapeChoice.IsChecked == true;
            return ReplacementTemplate.PerVariant([.. encodings.Select(e => SearchPattern.EncodeText(ReplaceQuery.Text, e, escapes))]);
        }

        return null;
    }

    // ---- イベント ----

    private void MultiTermToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (IsMultiTerm && Terms.Count == 0)
        {
            // 最初の行は今の検索語から作る。
            Terms.Add(NewTermRow(Query.Text));
        }

        Validate();
    }

    private TermRow NewTermRow(string text)
    {
        var row = new TermRow { KindIndex = Kind is SearchKind.Text or SearchKind.Integer or SearchKind.Float ? (int)Kind : 0 };
        row.Text = text;
        row.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Validate);
        return row;
    }

    private void TermAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Terms.Count < SearchPattern.MaxTerms)
        {
            Terms.Add(NewTermRow(string.Empty));
            TermList.SelectedIndex = Terms.Count - 1;
        }
    }

    private void TermRemove_Click(object sender, RoutedEventArgs e)
    {
        int index = TermList.SelectedIndex;
        if (index >= 0 && index < Terms.Count)
        {
            Terms.RemoveAt(index);
            TermList.SelectedIndex = Math.Min(index, Terms.Count - 1);
        }
    }

    private void TermMove_Click(object sender, RoutedEventArgs e)
    {
        int index = TermList.SelectedIndex;
        int delta = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        int to = index + delta;
        if (index >= 0 && to >= 0 && to < Terms.Count)
        {
            Terms.Move(index, to);
            TermList.SelectedIndex = to;
        }
    }

    /// <summary>テキストファイル (1 行に 1 語、UTF-8) から読み込む。種類は検索バーの種類 (FIND-26 の仕様 2)。</summary>
    private async void TermImport_Click(object sender, RoutedEventArgs e)
    {
        if (await PickOpenAsync("HexEditor.SearchTermsText", ".txt") is { } path)
        {
            ImportTerms(path, json: false);
        }
    }

    private async void TermLoad_Click(object sender, RoutedEventArgs e)
    {
        if (await PickOpenAsync("HexEditor.SearchTermsJson", ".json") is { } path)
        {
            ImportTerms(path, json: true);
        }
    }

    /// <summary>語の一覧を読み込む (テスト用の命令の通り道からも呼ぶ)。</summary>
    internal void ImportTerms(string path, bool json)
    {
        try
        {
            SearchKind kind = Kind is SearchKind.Text or SearchKind.Integer or SearchKind.Float ? Kind : SearchKind.Hex;
            string encoding = kind == SearchKind.Text && SelectedEncoding is not (TextEncodings.DisplayEncodingId or MultiEncodingId) ? SelectedEncoding : "ascii";
            IReadOnlyList<SearchTerm> terms = json
                ? SearchTerms.FromJson(File.ReadAllText(path))
                : SearchTerms.FromFile(path, new SearchTerm { Kind = kind, Encoding = encoding, IntegerBits = SelectedBits });
            Terms.Clear();
            foreach (SearchTerm term in terms)
            {
                TermRow row = TermRow.From(term);
                row.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Validate);
                Terms.Add(row);
            }

            MultiTermToggle.IsChecked = true;
            Validate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PatternException or InvalidOperationException)
        {
            ReplaceFailed?.Invoke(this, Loc.Format("Find_TermLoadFailed", ex.Message));
        }
    }

    private async void TermSave_Click(object sender, RoutedEventArgs e)
    {
        string? path;
        if (!TestHooks.TrySavePicker("terms.json", out path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? default)
            {
                SuggestedFileName = "terms.json",
                SettingsIdentifier = "HexEditor.SearchTermsJson",
            };
            picker.FileTypeChoices.Add(Loc.Get("SearchResults_FileType_Json"), [".json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            SearchEndian endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex);
            File.WriteAllText(path, SearchTerms.ToJson([.. Terms.Select(t => t.ToTerm(endian))]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReplaceFailed?.Invoke(this, Loc.Format("Find_TermSaveFailed", ex.Message));
        }
    }

    private async Task<string?> PickOpenAsync(string settingsIdentifier, string extension)
    {
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? default)
        {
            SettingsIdentifier = settingsIdentifier,
        };
        picker.FileTypeFilter.Add(extension);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>テスト用: 複数語の一覧を作る (種類、検索語、オプションの番号)。</summary>
    internal void SetTerms(IReadOnlyList<(SearchKind Kind, string Text, string? Encoding)> terms)
    {
        Terms.Clear();
        foreach ((SearchKind kind, string text, string? encoding) in terms)
        {
            TermRow row = TermRow.From(new SearchTerm { Kind = kind, Text = text, Encoding = encoding ?? "ascii" });
            row.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Validate);
            Terms.Add(row);
        }

        MultiTermToggle.IsChecked = true;
        Validate();
    }

    private void PositionPreset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        long x = tag == "sector" ? (Editor?.Document.Source.LogicalSectorSize is int s and > 1 ? s : 512) : long.Parse(tag, CultureInfo.InvariantCulture);
        _positionChosen = true;
        _positionAutomatic = false;
        _settingPosition = true;
        PositionChoice.SelectedIndex = 1;
        PositionModulus.Text = x.ToString(CultureInfo.InvariantCulture);
        PositionRemainder.Text = "0";
        _settingPosition = false;
        Validate();
    }

    /// <summary>テスト用: 位置の条件の入力欄の値。</summary>
    internal (string X, string Y) PositionInputs => (PositionModulus.Text, PositionRemainder.Text);

    /// <summary>テスト用: 位置の条件の入力が正しいか。</summary>
    internal bool PositionIsValid => _positionValid;

    private void RegexSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, ("search", MaxMatchLengthKey));

    // ---- 複数の文字コードの一覧 (FIND-08 の画面) ----

    private void MultiEncodingFlyout_Opening(object? sender, object e)
    {
        MultiEncodingList.Children.Clear();
        foreach (EncodingEntry entry in TermRow.Encodings)
        {
            var box = new CheckBox
            {
                Content = MainWindow.EncodingDisplayText(entry),
                IsChecked = _multiEncodings.Contains(entry.Id, StringComparer.OrdinalIgnoreCase),
                Tag = entry.Id,
            };
            AutomationProperties.SetAutomationId(box, "Find_MultiEncoding_" + entry.Id);
            box.Checked += MultiEncodingBox_Changed;
            box.Unchecked += MultiEncodingBox_Changed;
            MultiEncodingList.Children.Add(box);
        }

        FillEncodingSets();
        UpdateMultiEncodingLimit();
    }

    private void MultiEncodingBox_Changed(object sender, RoutedEventArgs e)
    {
        _multiEncodings = [.. MultiEncodingList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag)];
        UpdateMultiEncodingLimit();
        Validate();
    }

    /// <summary>最大 8 種類 (FIND-08 の仕様 1): 8 種類を選んだら残りを選べなくする。</summary>
    private void UpdateMultiEncodingLimit()
    {
        bool full = _multiEncodings.Count >= SearchPattern.MaxEncodings;
        foreach (CheckBox box in MultiEncodingList.Children.OfType<CheckBox>())
        {
            box.IsEnabled = box.IsChecked == true || !full;
        }

        MultiEncodingButton.Content = Loc.Format("Find_MultiEncoding_Button", _multiEncodings.Count);
    }

    /// <summary>名前を付けて保存した組み合わせ (state.json)。</summary>
    private static List<(string Name, string[] Ids)> LoadEncodingSets()
    {
        var sets = new List<(string, string[])>();
        if (Commands.CommandService.State?.Get(EncodingSetsKey) is JsonArray array)
        {
            foreach (JsonObject o in array.OfType<JsonObject>())
            {
                if (o["name"]?.GetValue<string>() is { } name && o["ids"] is JsonArray ids)
                {
                    sets.Add((name, [.. ids.OfType<JsonValue>().Select(v => v.GetValue<string>())]));
                }
            }
        }

        return sets;
    }

    private void FillEncodingSets()
    {
        EncodingSetChoice.SelectionChanged -= EncodingSet_Changed;
        EncodingSetChoice.Items.Clear();
        EncodingSetChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Find_EncodingSet_Default"), Tag = DefaultMultiEncodings });
        foreach ((string name, string[] ids) in LoadEncodingSets())
        {
            EncodingSetChoice.Items.Add(new ComboBoxItem { Content = name, Tag = ids });
        }

        EncodingSetChoice.SelectedIndex = -1;
        EncodingSetChoice.SelectionChanged += EncodingSet_Changed;
    }

    private void EncodingSet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EncodingSetChoice.SelectedItem is ComboBoxItem { Tag: string[] ids })
        {
            _multiEncodings = [.. ids.Take(SearchPattern.MaxEncodings)];
            foreach (CheckBox box in MultiEncodingList.Children.OfType<CheckBox>())
            {
                box.IsChecked = _multiEncodings.Contains((string)box.Tag, StringComparer.OrdinalIgnoreCase);
            }

            Validate();
        }
    }

    private void EncodingSetSave_Click(object sender, RoutedEventArgs e)
    {
        string name = EncodingSetName.Text.Trim();
        if (name.Length == 0 || _multiEncodings.Count == 0)
        {
            return;
        }

        List<(string Name, string[] Ids)> sets = LoadEncodingSets();
        sets.RemoveAll(s => s.Name == name);
        sets.Add((name, [.. _multiEncodings]));
        Commands.CommandService.State?.Set(EncodingSetsKey, new JsonArray([.. sets.Select(s => (JsonNode?)new JsonObject
        {
            ["name"] = s.Name,
            ["ids"] = new JsonArray([.. s.Ids.Select(i => (JsonNode?)i)]),
        })]));
        EncodingSetName.Text = string.Empty;
        FillEncodingSets();
    }
}
