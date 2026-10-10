namespace HexEditor.Core.Formats;

/// <summary>
/// インポート / エクスポートの形式の ID (TOOL-04 の仕様 1 の表)。コマンドライン (AUTO-44)・スクリプト (AUTO-09) と同じ ID を使う。
/// </summary>
public static class FormatIds
{
    public const string IntelHex = "ihex";
    public const string SRecord = "srec";
    public const string Base64 = "base64";
    public const string Base32 = "base32";
    public const string Ascii85 = "ascii85";
    public const string UUEncode = "uu";
    public const string QuotedPrintable = "qp";
    public const string Url = "url";
    public const string HexText = "hextext";
    public const string DecimalText = "dectext";
    public const string C = "c";
    public const string Cpp = "cpp";
    public const string CSharp = "csharp";
    public const string Java = "java";
    public const string JavaScript = "js";
    public const string Python = "python";
    public const string Rust = "rust";
    public const string Go = "go";
    public const string Pascal = "pascal";
    public const string VisualBasic = "vbnet";
    public const string DumpText = "dump-text";
    public const string Html = "html";
    public const string Rtf = "rtf";
    public const string Tex = "tex";
    public const string Markdown = "markdown";
    public const string Ips = "ips";
    public const string Ips32 = "ips32";
    public const string Binary = "bin";

    /// <summary>ソースコードの配列の形式 (TOOL-09)。</summary>
    public static readonly IReadOnlyList<string> SourceArrays = [C, Cpp, CSharp, Java, JavaScript, Python, Rust, Go, Pascal, VisualBasic];

    /// <summary>エンコード形式 (TOOL-07)。</summary>
    public static readonly IReadOnlyList<string> Encodings = [Base64, Base32, Ascii85, UUEncode, QuotedPrintable, Url];

    /// <summary>ダンプの形式 (TOOL-10)。エクスポートだけ。</summary>
    public static readonly IReadOnlyList<string> Dumps = [DumpText, Html, Rtf, Tex, Markdown];

    /// <summary>インポートできる形式 (表の順)。</summary>
    public static readonly IReadOnlyList<string> Importable =
        [IntelHex, SRecord, .. Encodings, HexText, DecimalText, .. SourceArrays, Ips, Ips32, Binary];

    /// <summary>エクスポートできる形式 (表の順。PDF (TOOL-03) はフェーズ 4)。</summary>
    public static readonly IReadOnlyList<string> Exportable =
        [IntelHex, SRecord, .. Encodings, HexText, DecimalText, .. SourceArrays, .. Dumps, Ips, Ips32, Binary];

    /// <summary>出力がテキストの形式 (改行と文字コードを選べる。TOOL-04 の仕様 5)。</summary>
    public static bool IsText(string id) => id is not (Binary or Ips or Ips32);

    /// <summary>アドレスを持つ形式。</summary>
    public static bool HasAddresses(string id) => id is IntelHex or SRecord;

    /// <summary>ファイルの拡張子 (保存ダイアログの既定)。</summary>
    public static string Extension(string id) => id switch
    {
        IntelHex => ".hex",
        SRecord => ".s37",
        Base64 => ".b64",
        Base32 => ".b32",
        Ascii85 => ".a85",
        UUEncode => ".uu",
        QuotedPrintable => ".qp",
        Url => ".txt",
        HexText or DecimalText or DumpText => ".txt",
        C => ".c",
        Cpp => ".cpp",
        CSharp => ".cs",
        Java => ".java",
        JavaScript => ".js",
        Python => ".py",
        Rust => ".rs",
        Go => ".go",
        Pascal => ".pas",
        VisualBasic => ".vb",
        Html => ".html",
        Rtf => ".rtf",
        Tex => ".tex",
        Markdown => ".md",
        Ips => ".ips",
        Ips32 => ".ips",
        _ => ".bin",
    };

    /// <summary>
    /// 拡張子から「開く」でデコードする形式 (ENG-38 の仕様 1)。対象外なら null。
    /// </summary>
    public static string? ForOpenExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hex" or ".ihx" or ".ihex" => IntelHex,
        ".s19" or ".s28" or ".s37" or ".srec" or ".mot" or ".mhx" => SRecord,
        ".b64" => Base64,
        _ => null,
    };
}
