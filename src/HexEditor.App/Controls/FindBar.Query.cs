using HexEditor.Core.Expressions;
using HexEditor.Core.Search;
using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索バーの今の条件を <see cref="SearchQuery"/> にまとめる (複数ファイル検索 (FIND-30 の仕様 1) が「検索バーの条件を使う」で取り込む)。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>今の種類・オプション・検索語 (位置の条件は今の検索語のもの)。</summary>
    internal SearchQuery CaptureQuery()
    {
        bool multiTerm = IsMultiTerm;
        SearchEndian endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex);
        return new SearchQuery
        {
            Kind = Kind,
            Text = Query.Text,
            EncodingId = EffectiveEncodingId,
            Encodings = IsMultiEncoding && Kind == SearchKind.Text ? [.. _multiEncodings] : [],
            CaseSensitive = CaseChoice.IsChecked == true,
            WholeWord = WordChoice.IsChecked == true,
            UseEscapes = EscapeChoice.IsChecked == true,
            AlignToCharacters = AlignChoice.IsChecked == true,
            IntegerBits = SelectedBits,
            Sign = (IntegerSign)Math.Max(0, SignChoice.SelectedIndex),
            Endian = endian,
            FloatFormat = (FloatFormat)Math.Max(0, FloatChoice.SelectedIndex),
            Tolerance = (ToleranceKind)Math.Max(0, ToleranceChoice.SelectedIndex),
            ToleranceText = ToleranceValue.Text,
            RangeExclude = RangeExcludeChoice.IsChecked == true,
            RegexIgnoreCase = RegexIgnoreCase.IsChecked == true,
            RegexMultiline = RegexMultiline.IsChecked == true,
            RegexSingleline = RegexSingleline.IsChecked == true,
            Mask = IsMask,
            MaskBits = MaskModeChoice.SelectedIndex == 1,
            MaskText = MaskQuery.Text,
            Mismatch = IsMismatch,
            MismatchAligned = MismatchAlignChoice.IsChecked == true,
            Position = _pattern?.Position,
            Terms = multiTerm ? [.. Terms.Select(t => t.ToTerm(endian))] : null,
            TermLabels = multiTerm ? [.. Terms.Select(t => t.Label())] : null,
        };
    }

    /// <summary>
    /// パターンを作るときの環境 (「一致の最大長」・正規表現の時間の上限の設定、文字コードの表示名、入力式の文脈)。検索バーと複数ファイル検索で共通。
    /// </summary>
    internal static SearchQueryEnvironment QueryEnvironment(IExpressionContext? context) => new()
    {
        MaxMatchLength = MaxMatchLength,
        RegexTimeLimit = TimeSpan.FromSeconds(Math.Clamp(App.Settings?.GetDouble(RegexTimeLimitKey, 2) ?? 2, 0.1, 60)),
        Context = context,
        EncodingName = id => EncodingCatalog.Find(id) is { } entry ? MainWindow.EncodingDisplayText(entry) : id,
    };
}
