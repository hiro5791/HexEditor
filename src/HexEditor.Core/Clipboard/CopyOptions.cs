using HexEditor.Core.View;

namespace HexEditor.Core.Clipboard;

/// <summary>「形式を選択してコピー」の形式 (EDIT-25 の仕様 6 の表)。表記の違い (C# の Span、Python の bytes リテラル、アセンブリの方言など) は <see cref="CopyOptions"/> で選ぶ。</summary>
public enum CopyFormat
{
    // Hex 文字列
    HexSpaced,
    HexPlain,
    HexCommaPrefixed,
    HexEscaped,
    HexUrl,
    HexCustom,

    // 数値列
    Decimal,
    Octal,
    Binary,

    // 配列
    ArrayC,
    ArrayCpp,
    ArrayCSharp,
    ArrayJava,
    ArrayJavaScript,
    ArrayPython,
    ArrayRust,
    ArrayGo,
    ArrayPascal,
    ArrayVisualBasic,
    ArrayPureBasic,
    ArrayAssembly,

    // エンコード
    Base64,
    Base32,
    Ascii85,
    UUEncode,
    XXEncode,
    QuotedPrintable,

    // 文書
    Html,
    Rtf,
    Markdown,
    Tex,

    // 画面表示
    ScreenDump,
    Text,

    // テキスト形式
    IntelHex,
    SRecord,
    Json,

    // 位置
    Position,
}

/// <summary>形式の分類 (ダイアログの一覧のグループ)。</summary>
public enum CopyFormatCategory
{
    HexString,
    Numbers,
    Array,
    Encoding,
    Document,
    Screen,
    TextFormat,
    Position,
}

public enum AssemblySyntax
{
    Nasm,
    Masm,
    Gas,
}

public enum Ascii85Variant
{
    Adobe,
    Z85,
}

/// <summary>文書形式 (HTML・Markdown・TeX) の作り方。</summary>
public enum DocumentLayout
{
    /// <summary>ダンプ (HTML は &lt;pre&gt;、Markdown はコードブロック、TeX は verbatim)。</summary>
    Dump,

    /// <summary>表 (オフセット・Hex・テキストの列)。</summary>
    Table,
}

public enum JsonDataEncoding
{
    Base64,
    Hex,
    Numbers,
}

/// <summary>S-record のレコードの種類 (アドレスの大きさ)。</summary>
public enum SRecordKind
{
    Auto,
    S19,
    S28,
    S37,
}

/// <summary>出力に付ける注記 (プレビューに示す)。</summary>
public enum CopyNote
{
    /// <summary>「最後の要素は 0 で補いました」(EDIT-25 の仕様 5)。</summary>
    PaddedLastElement,
}

/// <summary>設定の誤り (入力欄のエラー。コピーボタンを無効にする)。</summary>
public enum CopyOptionError
{
    /// <summary>変数名がその言語の識別子として正しくない (仕様 4)。</summary>
    InvalidVariableName,

    /// <summary>Z85 は長さが 4 の倍数のときだけ使える。</summary>
    Z85Length,

    /// <summary>アドレスが Intel HEX・S-record で表せる範囲 (32 bit) を超える。</summary>
    AddressTooLarge,

    /// <summary>1 行のバイト数・1 レコードのバイト数が範囲外。</summary>
    InvalidLineLength,
}

/// <summary>
/// 「形式を選択してコピー」の設定 (EDIT-25 の仕様 3〜5 と形式ごとの設定)。既定値は仕様の既定値。
/// </summary>
/// <summary>
/// 配列の定義の修飾 (TOOL-09 の仕様 2 の「<c>const</c> / <c>static</c> などの修飾」)。<see cref="Default"/> は言語ごとの従来の形
/// (C++ の <c>constexpr</c>、JavaScript の <c>const</c>、Rust の <c>let</c> など)。表せない組み合わせは、その言語で近い形にする。
/// </summary>
public enum ArrayModifier
{
    Default,
    None,
    Const,
    Static,
    StaticConst,
}

public sealed record CopyOptions
{
    /// <summary>改行 (CRLF が既定。仕様 3)。</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>インデント (空白 4 個が既定。仕様 3)。</summary>
    public string Indent { get; init; } = "    ";

    /// <summary>1 行のバイト数 (1〜1024、既定 16。仕様 4)。</summary>
    public int BytesPerLine { get; init; } = 16;

    /// <summary>16 進の大文字 (既定)。</summary>
    public bool UpperCase { get; init; } = true;

    /// <summary>変数名 (既定 data)。</summary>
    public string VariableName { get; init; } = "data";

    /// <summary>配列の要素の大きさ (1 / 2 / 4 / 8。仕様 5)。</summary>
    public int ElementSize { get; init; } = 1;

    /// <summary>配列の要素を 10 進で書く (既定は 16 進。TOOL-09 の仕様 2)。</summary>
    public bool ArrayDecimal { get; init; }

    /// <summary>配列の定義の修飾 (既定は言語ごとの従来の形)。</summary>
    public ArrayModifier Modifier { get; init; }

    /// <summary>配列の要素のエンディアン (既定はリトルエンディアン。ドキュメントの既定エンディアンを呼び出し側が入れる)。</summary>
    public bool BigEndian { get; init; }

    // ---- 形式ごとの設定 ----

    /// <summary>「区切り文字を指定」の区切り・接頭辞・接尾辞。</summary>
    public string CustomSeparator { get; init; } = ":";

    public string CustomPrefix { get; init; } = string.Empty;

    public string CustomSuffix { get; init; } = string.Empty;

    /// <summary>C# を <c>ReadOnlySpan&lt;byte&gt; data =&gt; [...]</c> の形にする。</summary>
    public bool CSharpSpan { get; init; }

    /// <summary>Python を <c>b"\x.."</c> のバイト列リテラルにする。</summary>
    public bool PythonBytesLiteral { get; init; }

    public AssemblySyntax Assembly { get; init; } = AssemblySyntax.Nasm;

    /// <summary>Base64 を URL 安全 (RFC 4648 の 5 章) にする。</summary>
    public bool Base64UrlSafe { get; init; }

    /// <summary>Base64 を 76 文字で改行する。</summary>
    public bool Base64Wrap { get; init; }

    /// <summary>Base32 を Base32hex にする。</summary>
    public bool Base32Hex { get; init; }

    public Ascii85Variant Ascii85 { get; init; } = Ascii85Variant.Adobe;

    /// <summary>Ascii85 (Adobe) を <c>&lt;~</c> <c>~&gt;</c> で囲む (既定)。</summary>
    public bool Ascii85Delimiters { get; init; } = true;

    /// <summary>UUEncode / XXEncode の begin 行のファイル名。</summary>
    public string EncodedFileName { get; init; } = "data.bin";

    // ---- ファイルへのエクスポートで選べる設定 (TOOL-07 の仕様 1)。既定値は Copy As の出力と同じになる ----

    /// <summary>Base64 の 1 行の文字数 (0 は改行なし)。null なら <see cref="Base64Wrap"/> に従う (76 または改行なし)。</summary>
    public int? Base64LineLength { get; init; }

    /// <summary>Base64 のパディング (<c>=</c>) を付ける (既定)。</summary>
    public bool Base64Padding { get; init; } = true;

    /// <summary>Base32 の 1 行の文字数 (0 は改行なし。既定)。</summary>
    public int Base32LineLength { get; init; }

    /// <summary>Base32 のパディングを付ける (既定)。</summary>
    public bool Base32Padding { get; init; } = true;

    /// <summary>Ascii85 の 1 行の文字数 (0 は改行なし。既定)。<c>&lt;~</c> <c>~&gt;</c> も数える。</summary>
    public int Ascii85LineLength { get; init; }

    /// <summary>UUEncode の begin 行のモード (既定 644)。</summary>
    public string EncodedFileMode { get; init; } = "644";

    /// <summary>Quoted-Printable の 1 行の文字数 (ソフト改行の <c>=</c> を含む。既定 76)。</summary>
    public int QuotedPrintableLineLength { get; init; } = 76;

    /// <summary>URL エンコードで RFC 3986 の非予約文字 (英数字と <c>-._~</c>) を変換しない。偽ならすべて <c>%XX</c> にする (Copy As の既定)。</summary>
    public bool UrlKeepUnreserved { get; init; }

    public DocumentLayout Layout { get; init; } = DocumentLayout.Dump;

    /// <summary>HTML・RTF に現在の色付け (変更されたバイトの強調) を含める。</summary>
    public bool IncludeColors { get; init; } = true;

    /// <summary>変更されたバイトの範囲 (ドキュメント上の位置。色付けに使う)。</summary>
    public IReadOnlyList<(long Offset, long Length)> ModifiedRanges { get; init; } = [];

    /// <summary>変更されたバイトの色 (HTML・RTF に書く。`#RRGGBB`)。</summary>
    public string ModifiedColor { get; init; } = "#C42B1C";

    // 画面表示どおり (現在の表示設定。EDIT-25 の仕様 6、VIEW-11 の仕様 5)
    public int ScreenBytesPerRow { get; init; } = 16;

    /// <summary>グループ化のバイト数 (VIEW-09。既定 1)。</summary>
    public int ScreenGroupSize { get; init; } = 1;

    /// <summary>中央区切り (VIEW-09 の仕様 3。<see cref="ViewSettings.EffectiveMiddleSeparator"/> の値)。</summary>
    public bool ScreenMiddleSeparator { get; init; }

    /// <summary>セルの表示形式 (VIEW-10)。</summary>
    public CellFormat ScreenCellFormat { get; init; } = CellFormat.Hex;

    /// <summary>グループ内のバイトを逆順に表示 (VIEW-11 の仕様 4。Hex 形式でグループ化が 2 以上のときだけ効く)。</summary>
    public bool ScreenReverseGroups { get; init; }

    /// <summary>2 バイト以上のセルの値のエンディアン (ドキュメントのエンディアン。VIEW-11)。</summary>
    public bool ScreenBigEndian { get; init; }

    /// <summary>10 進・8 進のセルを 0 ではなく空白で埋める (VIEW-10 の仕様 2)。</summary>
    public bool ScreenSpacePadding { get; init; }

    public bool ShowOffset { get; init; } = true;

    public bool ShowHex { get; init; } = true;

    public bool ShowText { get; init; } = true;

    /// <summary>オフセットを 10 進で表す (既定は 16 進)。</summary>
    public bool DecimalOffsets { get; init; }

    /// <summary>テキストの列・テキスト形式の文字コード。</summary>
    public TextEncoding Encoding { get; init; } = TextEncoding.Ascii;

    /// <summary>Intel HEX・S-record の 1 レコードのバイト数 (既定 16、最大 255)。</summary>
    public int RecordBytes { get; init; } = 16;

    /// <summary>ベースアドレス (開始アドレス = 選択範囲の開始オフセット + ベースアドレス)。</summary>
    public long BaseAddress { get; init; }

    public SRecordKind SRecordKind { get; init; } = SRecordKind.Auto;

    /// <summary>S0 のヘッダの文字列。</summary>
    public string SRecordHeader { get; init; } = string.Empty;

    public JsonDataEncoding JsonData { get; init; } = JsonDataEncoding.Base64;

    /// <summary>位置の形式で 10 進を使う (既定は 16 進)。</summary>
    public bool DecimalPosition { get; init; }

    /// <summary>位置の形式の「(256 バイト)」の書式。{0} にバイト数 (区切り付き) が入る。UI の言語のリソースを呼び出し側が入れる。</summary>
    public string PositionLengthFormat { get; init; } = "({0} bytes)";
}
