using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.Core.View;

namespace HexEditor.Core.Printing;

/// <summary>用紙の種類 (TOOL-03 の仕様 1: A4 / Letter / A3 / Legal。印刷ではこれに A5 を加える)。</summary>
public enum PaperKind
{
    A4,
    Letter,
    A3,
    Legal,
    A5,
}

public static class PaperSizes
{
    private const double PointsPerMm = 72.0 / 25.4;

    public static IReadOnlyList<PaperKind> PdfKinds { get; } = [PaperKind.A4, PaperKind.Letter, PaperKind.A3, PaperKind.Legal];

    /// <summary>用紙の大きさ (点。縦向きの幅と高さ)。</summary>
    public static (double Width, double Height) Size(PaperKind kind) => kind switch
    {
        PaperKind.Letter => (612, 792),
        PaperKind.Legal => (612, 1008),
        PaperKind.A3 => (297 * PointsPerMm, 420 * PointsPerMm),
        PaperKind.A5 => (148 * PointsPerMm, 210 * PointsPerMm),
        _ => (210 * PointsPerMm, 297 * PointsPerMm),
    };

    /// <summary>地域設定に合わせた既定の用紙 (メートル法の国は A4、そうでなければ Letter。TOOL-03 の仕様 1)。</summary>
    public static PaperKind DefaultFor(CultureInfo culture)
    {
        try
        {
            return new RegionInfo(culture.Name).IsMetric ? PaperKind.A4 : PaperKind.Letter;
        }
        catch (ArgumentException)
        {
            return PaperKind.A4;
        }
    }

    public static double MmToPoints(double mm) => mm * PointsPerMm;

    public static double PointsToMm(double points) => points / PointsPerMm;
}

/// <summary>印刷範囲の種類 (TOOL-01 の仕様 2)。</summary>
public enum PrintRangeKind
{
    All,

    /// <summary>選択範囲 (マルチ選択は範囲ごとに区切って印刷する)。</summary>
    Selection,

    /// <summary>オフセットの範囲 (入力式)。</summary>
    Offsets,

    /// <summary>ページの範囲 (<c>1-3, 8</c>)。</summary>
    Pages,
}

/// <summary>色の指定 (TOOL-01 の仕様 2)。</summary>
public enum PrintColorMode
{
    /// <summary>白黒: 色の代わりに下線 (変更したバイト) と網掛け (ブックマーク・色付けルール) で示す (既定)。</summary>
    BlackWhite,

    /// <summary>カラー: 変更バイト・ブックマーク・色付けルールの背景色を印刷する。</summary>
    Color,
}

public readonly record struct PrintColor(byte R, byte G, byte B)
{
    public static PrintColor Black { get; } = new(0, 0, 0);

    public static PrintColor Shade { get; } = new(217, 217, 217);

    public static PrintColor ModifiedColor { get; } = new(255, 236, 160);
}

/// <summary>ヘッダ・フッタの左・中央・右の文字列 (置換記号を含む)。</summary>
public sealed record HeaderFooterText(string Left, string Center, string Right)
{
    public static HeaderFooterText Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => Left.Length == 0 && Center.Length == 0 && Right.Length == 0;
}

/// <summary>印刷・PDF の設定 (TOOL-01 の仕様 2・5)。</summary>
public sealed record PrintSettings
{
    public const int MaxPages = 100_000;

    public const int ConfirmPages = 1000;

    public PrintRangeKind Range { get; init; } = PrintRangeKind.All;

    /// <summary>選択範囲 (<see cref="PrintRangeKind.Selection"/>)。</summary>
    public IReadOnlyList<(long Start, long Length)> Selection { get; init; } = [];

    /// <summary>オフセットの範囲 (<see cref="PrintRangeKind.Offsets"/>)。</summary>
    public (long Start, long Length) Offsets { get; init; }

    /// <summary>ページの範囲の文字列 (<see cref="PrintRangeKind.Pages"/>)。</summary>
    public string Pages { get; init; } = string.Empty;

    public PaperKind Paper { get; init; } = PaperKind.A4;

    public bool Landscape { get; init; }

    public double MarginTopMm { get; init; } = 15;

    public double MarginBottomMm { get; init; } = 15;

    public double MarginLeftMm { get; init; } = 15;

    public double MarginRightMm { get; init; } = 15;

    public string FontFamily { get; init; } = SystemFontCatalog.DefaultFamily;

    public double FontSize { get; init; } = 9;

    /// <summary>1 行のバイト数 (4〜64)。0 は「用紙に合わせる」。</summary>
    public int BytesPerRow { get; init; } = 16;

    /// <summary>画面と同じ表示の設定 (列の表示、グループ化、オフセットの基数、文字コード)。セルの形式は Hex に固定して印刷する。</summary>
    public ViewSettings View { get; init; } = ViewSettings.Default;

    public PrintColorMode Color { get; init; } = PrintColorMode.BlackWhite;

    public HeaderFooterText Header { get; init; } = new("{name}", string.Empty, "{range}");

    public HeaderFooterText Footer { get; init; } = new(string.Empty, "{page} / {pages}", "{date}");

    public int Copies { get; init; } = 1;

    /// <summary>プリンタの名前 (PDF では使わない)。</summary>
    public string? Printer { get; init; }

    public static IReadOnlyList<double> FontSizes { get; } = [6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24];

    /// <summary>範囲を丸めた設定 (フォント 6〜24 pt、余白 0〜50 mm、1 行 4〜64 バイトまたは 0、部数 1〜999)。</summary>
    public PrintSettings Normalize() => this with
    {
        FontSize = Math.Clamp(FontSize, 6, 24),
        MarginTopMm = Math.Clamp(MarginTopMm, 0, 50),
        MarginBottomMm = Math.Clamp(MarginBottomMm, 0, 50),
        MarginLeftMm = Math.Clamp(MarginLeftMm, 0, 50),
        MarginRightMm = Math.Clamp(MarginRightMm, 0, 50),
        BytesPerRow = BytesPerRow == 0 ? 0 : Math.Clamp(BytesPerRow, 4, 64),
        Copies = Math.Clamp(Copies, 1, 999),
    };

    /// <summary>「既定として保存」する値 (範囲、プリンタ、画面の設定は含めない)。</summary>
    public JsonObject ToJson() => new()
    {
        ["paper"] = Paper.ToString(),
        ["landscape"] = Landscape,
        ["marginTop"] = MarginTopMm,
        ["marginBottom"] = MarginBottomMm,
        ["marginLeft"] = MarginLeftMm,
        ["marginRight"] = MarginRightMm,
        ["font"] = FontFamily,
        ["fontSize"] = FontSize,
        ["bytesPerRow"] = BytesPerRow,
        ["color"] = Color.ToString(),
        ["headerLeft"] = Header.Left,
        ["headerCenter"] = Header.Center,
        ["headerRight"] = Header.Right,
        ["footerLeft"] = Footer.Left,
        ["footerCenter"] = Footer.Center,
        ["footerRight"] = Footer.Right,
        ["copies"] = Copies,
    };

    public static PrintSettings FromJson(JsonObject? json, PrintSettings? baseSettings = null)
    {
        PrintSettings s = baseSettings ?? new PrintSettings();
        if (json is null)
        {
            return s;
        }

        string Str(string key, string fallback) => json[key] is JsonValue v && v.TryGetValue(out string? t) && t is not null ? t : fallback;
        double Num(string key, double fallback) => json[key] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;
        int Int(string key, int fallback) => json[key] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;
        bool Bool(string key, bool fallback) => json[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
        return (s with
        {
            Paper = Enum.TryParse(Str("paper", string.Empty), out PaperKind paper) ? paper : s.Paper,
            Landscape = Bool("landscape", s.Landscape),
            MarginTopMm = Num("marginTop", s.MarginTopMm),
            MarginBottomMm = Num("marginBottom", s.MarginBottomMm),
            MarginLeftMm = Num("marginLeft", s.MarginLeftMm),
            MarginRightMm = Num("marginRight", s.MarginRightMm),
            FontFamily = Str("font", s.FontFamily),
            FontSize = Num("fontSize", s.FontSize),
            BytesPerRow = Int("bytesPerRow", s.BytesPerRow),
            Color = Enum.TryParse(Str("color", string.Empty), out PrintColorMode color) ? color : s.Color,
            Header = new HeaderFooterText(Str("headerLeft", s.Header.Left), Str("headerCenter", s.Header.Center), Str("headerRight", s.Header.Right)),
            Footer = new HeaderFooterText(Str("footerLeft", s.Footer.Left), Str("footerCenter", s.Footer.Center), Str("footerRight", s.Footer.Right)),
            Copies = Int("copies", s.Copies),
        }).Normalize();
    }
}
