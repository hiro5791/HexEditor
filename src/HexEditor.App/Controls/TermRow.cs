using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 複数語の検索 (FIND-26) の一覧の 1 行。種類 (Hex / テキスト / 整数 / 浮動小数点)、検索語、種類ごとのオプション (テキストは文字コード、
/// 整数はサイズ、浮動小数点は形式)、有効 / 無効を持つ。エンディアン・大文字と小文字の区別・単語単位・範囲・位置の条件は検索バーの共通の設定。
/// </summary>
public sealed partial class TermRow : ObservableObject
{
    /// <summary>テキストの行で選べる文字コード (表示の文字コードの一覧の名前)。</summary>
    internal static readonly IReadOnlyList<EncodingEntry> Encodings = [.. EncodingCatalog.All.Where(e => e.Selectable)];

    private static IReadOnlyList<string>? s_kindNames;
    private static IReadOnlyList<string>? s_encodingNames;
    private static IReadOnlyList<string>? s_bitNames;
    private static IReadOnlyList<string>? s_floatNames;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionNames), nameof(OptionVisibility))]
    public partial int KindIndex { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int OptionIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorBrush))]
    public partial string Error { get; set; } = string.Empty;

    public SearchKind Kind => (SearchKind)Math.Clamp(KindIndex, 0, 3);

    public IReadOnlyList<string> KindNames => s_kindNames ??=
    [
        Loc.Get("Find_KindName_Hex"), Loc.Get("Find_KindName_Text"), Loc.Get("Find_KindName_Integer"), Loc.Get("Find_KindName_Float"),
    ];

    /// <summary>種類に応じたオプションの一覧。</summary>
    public IReadOnlyList<string> OptionNames => Kind switch
    {
        SearchKind.Text => s_encodingNames ??= [.. Encodings.Select(MainWindow.EncodingDisplayText)],
        SearchKind.Integer => s_bitNames ??= [.. NumericSearch.IntegerSizes.Select(b => Loc.Format("Find_IntBits_Item", b))],
        SearchKind.Float => s_floatNames ??= ["half", "float", "double"],
        _ => [],
    };

    public Visibility OptionVisibility => Kind == SearchKind.Hex ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>不正な語は赤枠 (FIND-26 の「エラー」)。</summary>
    public Brush? ErrorBrush => Error.Length > 0 ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : null;

    public string EnabledName => Loc.Get("Find_TermEnabled_Name");

    public string KindName => Loc.Get("Find_Kind_Name");

    public string TextName => Loc.Get("Find_TermText_Name");

    public string OptionName => Loc.Get("Find_TermOption_Name");

    /// <summary>語にする (エンディアンは検索バーの共通の設定)。</summary>
    public SearchTerm ToTerm(SearchEndian endian) => new()
    {
        Kind = Kind,
        Text = Text,
        Encoding = Kind == SearchKind.Text && OptionIndex >= 0 && OptionIndex < Encodings.Count ? Encodings[OptionIndex].Id : "ascii",
        IntegerBits = Kind == SearchKind.Integer && OptionIndex >= 0 && OptionIndex < NumericSearch.IntegerSizes.Count ? NumericSearch.IntegerSizes[OptionIndex] : 32,
        FloatFormat = Kind == SearchKind.Float ? (FloatFormat)Math.Clamp(OptionIndex, 0, 2) : FloatFormat.Single,
        Endian = endian,
        Enabled = Enabled,
    };

    /// <summary>語から作る。</summary>
    public static TermRow From(SearchTerm term)
    {
        var row = new TermRow { KindIndex = (int)term.Kind, Text = term.Text, Enabled = term.Enabled };
        row.OptionIndex = term.Kind switch
        {
            SearchKind.Text => Math.Max(0, Encodings.ToList().FindIndex(e => string.Equals(e.Id, term.Encoding, StringComparison.OrdinalIgnoreCase))),
            SearchKind.Integer => Math.Max(0, NumericSearch.IntegerSizes.ToList().IndexOf(term.IntegerBits)),
            SearchKind.Float => (int)term.FloatFormat,
            _ => 0,
        };
        return row;
    }

    /// <summary>「検索語」の列に出す名前 (Hex は検索語、テキストは「検索語 (文字コード)」)。</summary>
    public string Label() => Kind switch
    {
        SearchKind.Text => $"{Text} ({(OptionIndex >= 0 && OptionIndex < Encodings.Count ? Encodings[OptionIndex].Label : "ASCII")})",
        SearchKind.Integer or SearchKind.Float when OptionIndex >= 0 && OptionIndex < OptionNames.Count => $"{Text} ({OptionNames[OptionIndex]})",
        _ => Text,
    };

    partial void OnKindIndexChanged(int value)
    {
        int defaultOption = Kind switch
        {
            SearchKind.Text => Math.Max(0, Encodings.ToList().FindIndex(e => e.Id == "ascii")),
            SearchKind.Integer => NumericSearch.IntegerSizes.ToList().IndexOf(32),
            SearchKind.Float => 1,
            _ => 0,
        };
        OptionIndex = defaultOption;
    }
}
