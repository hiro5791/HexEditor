using HexEditor.Core.Clipboard;
using HexEditor.Core.View;

namespace HexEditor.Core.Formats;

/// <summary>テキスト形式の出力の文字コード (TOOL-04 の仕様 5)。</summary>
public enum TextFileEncoding
{
    /// <summary>UTF-8 (BOM なし)。既定。</summary>
    Utf8,

    /// <summary>UTF-8 (BOM あり)。</summary>
    Utf8Bom,

    /// <summary>ASCII (表せない文字は <c>?</c>)。</summary>
    Ascii,
}

/// <summary>ダンプの列の区切り (TOOL-10 のテキスト)。</summary>
public enum DumpColumnSeparator
{
    /// <summary>空白。</summary>
    Space,

    /// <summary>縦線 (<c>|</c>)。</summary>
    Bar,
}

/// <summary>TeX の環境 (TOOL-10)。</summary>
public enum TexEnvironment
{
    /// <summary><c>verbatim</c> 環境 (色なし)。</summary>
    Verbatim,

    /// <summary>色付きの <c>alltt</c> 環境 (<c>xcolor</c>)。</summary>
    AlltColor,
}

/// <summary>ダンプの強調の種類 (TOOL-10 の HTML・RTF の色付け)。</summary>
public enum DumpHighlightKind
{
    /// <summary>変更されたバイト。</summary>
    Modified,

    /// <summary>ブックマーク。</summary>
    Bookmark,

    /// <summary>色付けルール (VIEW の F2-06)。</summary>
    Rule,
}

/// <summary>ダンプで強調する範囲 (ドキュメント上のオフセット)。<see cref="Name"/> はブックマークの名前 (ツールチップ)。</summary>
public sealed record DumpHighlight(long Offset, long Length, DumpHighlightKind Kind, string? Name = null, string? Color = null);

/// <summary>ダンプのエクスポートの設定 (TOOL-10 の仕様 1・2)。既定値は画面の表示と同じものを呼び出し側が入れる。</summary>
public sealed record DumpOptions
{
    public int BytesPerRow { get; init; } = 16;

    /// <summary>グループの大きさ (この数のバイトごとに空白を 1 つ足す。0 はなし)。</summary>
    public int GroupSize { get; init; } = 8;

    public bool ShowOffset { get; init; } = true;

    public bool ShowHex { get; init; } = true;

    public bool ShowText { get; init; } = true;

    public OffsetRadix OffsetRadix { get; init; } = OffsetRadix.Hex;

    /// <summary>オフセットの列に足すベースアドレス (画面と同じ)。</summary>
    public long BaseAddress { get; init; }

    public TextEncoding Encoding { get; init; } = TextEncoding.Ascii;

    public bool UpperCase { get; init; } = true;

    /// <summary>テキストの列の区切り (既定は縦線)。</summary>
    public DumpColumnSeparator Separator { get; init; } = DumpColumnSeparator.Bar;

    /// <summary>HTML・RTF・TeX で色を付ける (変更バイト・ブックマーク・色付けルール。既定: 付ける)。</summary>
    public bool Colors { get; init; } = true;

    /// <summary>HTML の配色をダークにする (既定はライト)。</summary>
    public bool DarkScheme { get; init; }

    /// <summary>HTML でブックマークの名前をツールチップ (<c>title</c>) に入れる (既定)。</summary>
    public bool BookmarkTooltips { get; init; } = true;

    /// <summary>RTF のフォント (既定 Cascadia Mono)。</summary>
    public string RtfFont { get; init; } = "Cascadia Mono";

    /// <summary>RTF のフォントの大きさ (ポイント。既定 10)。</summary>
    public double RtfFontSize { get; init; } = 10;

    public TexEnvironment Tex { get; init; } = TexEnvironment.Verbatim;

    /// <summary>Markdown を表にする (既定はコードブロック)。</summary>
    public bool MarkdownTable { get; init; }

    /// <summary>強調する範囲 (オフセットの昇順)。</summary>
    public IReadOnlyList<DumpHighlight> Highlights { get; init; } = [];

    /// <summary>HTML の title (ファイル名など)。</summary>
    public string Title { get; init; } = "Hex dump";
}

/// <summary>IPS の形式 (TOOL-12)。</summary>
public enum IpsFormat
{
    /// <summary>16 MiB 以上の位置に変更があれば IPS32、なければ IPS。</summary>
    Auto,
    Ips,
    Ips32,
}

/// <summary>
/// エクスポートの設定 (TOOL-04〜TOOL-12)。形式ごとの設定を 1 つにまとめる (前回の値を形式ごとに記憶する。TOOL-04 の仕様 4)。
/// </summary>
public sealed record ExportOptions
{
    /// <summary>形式の ID (<see cref="FormatIds"/>)。</summary>
    public string Format { get; init; } = FormatIds.Binary;

    /// <summary>テキスト形式の改行 (CRLF が既定。TOOL-04 の仕様 5)。</summary>
    public string NewLine { get; init; } = "\r\n";

    public TextFileEncoding Encoding { get; init; } = TextFileEncoding.Utf8;

    /// <summary>最後の行の後に改行を付ける (既定)。元の形式での保存 (TOOL-11) では元のファイルに合わせる。</summary>
    public bool FinalNewLine { get; init; } = true;

    // ---- Intel HEX・S-record (TOOL-05、TOOL-06) ----

    public RecordExportOptions Records { get; init; } = new();

    /// <summary>出力する範囲の先頭のアドレス。null ならドキュメントの表示上のアドレス (ベースアドレス + オフセット)。</summary>
    public long? StartAddress { get; init; }

    // ---- エンコード形式・配列 (TOOL-07、TOOL-09。Copy As と共通の設定) ----

    public CopyOptions Copy { get; init; } = new();

    /// <summary>C / C++ をヘッダファイルとして出す (インクルードガード付き。TOOL-09 の仕様 3)。</summary>
    public bool HeaderFile { get; init; }

    /// <summary>C / C++ に長さの定数を付ける。</summary>
    public bool LengthConstant { get; init; }

    /// <summary>先頭に元のファイル名・範囲・作成日時のコメントを付ける (既定: 付ける)。</summary>
    public bool SourceComment { get; init; } = true;

    // ---- Hex テキスト・10 進テキスト (TOOL-08) ----

    /// <summary>1 行のバイト数 (1〜1,024。0 は改行なし。既定 16)。</summary>
    public int HexBytesPerLine { get; init; } = 16;

    /// <summary>区切り (既定は空白)。</summary>
    public string HexSeparator { get; init; } = " ";

    /// <summary>接頭辞 (なし / 0x / \x / $ / 任意)。</summary>
    public string HexPrefix { get; init; } = string.Empty;

    public bool HexUpperCase { get; init; } = true;

    /// <summary>行頭にオフセットを付ける (既定: しない)。</summary>
    public bool HexOffsets { get; init; }

    /// <summary>10 進の 1 つの値のサイズ (1 / 2 / 4 / 8)。</summary>
    public int DecimalValueSize { get; init; } = 1;

    public bool DecimalSigned { get; init; }

    public bool DecimalBigEndian { get; init; }

    /// <summary>10 進の 1 行の値の数 (既定 16)。</summary>
    public int DecimalPerLine { get; init; } = 16;

    /// <summary>10 進の区切り (既定 カンマと空白)。</summary>
    public string DecimalSeparator { get; init; } = ", ";

    // ---- ダンプ (TOOL-10) ----

    public DumpOptions Dump { get; init; } = new();

    // ---- IPS (TOOL-12) ----

    public IpsFormat Ips { get; init; } = IpsFormat.Auto;

    /// <summary>同じ値が 8 バイト以上続く部分を RLE レコードにする (既定)。</summary>
    public bool IpsRle { get; init; } = true;
}
