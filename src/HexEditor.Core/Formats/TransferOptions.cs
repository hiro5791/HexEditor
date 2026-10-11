using System.Globalization;
using System.Text.Json;
using HexEditor.Core.Clipboard;
using HexEditor.Core.View;

namespace HexEditor.Core.Formats;

/// <summary>インポート / エクスポートのダイアログの、形式ごとの設定の欄の種類。</summary>
public enum TransferFieldKind
{
    Text,
    Check,
    Choice,

    /// <summary>候補 (<see cref="TransferField.Choices"/>) を示す入力欄 (候補以外の値も入力できる。TOOL-05 の「16 (32 も選択肢として示す)」)。</summary>
    Suggest,
}

/// <summary>
/// 設定の欄 1 つ (TOOL-04 の仕様 2 の 3・3 の 2)。<see cref="Choices"/> は選択肢の値 (表示は <c>Transfer_Choice_&lt;値&gt;</c>。
/// <see cref="TransferFieldKind.Suggest"/> では値をそのまま表示する)。値は文字列で持ち、形式ごとに前回の値を記憶する (仕様 4)。
/// <see cref="Remember"/> が偽の欄 (ファイル名・インポート時の値など、ドキュメントごとに既定が決まるもの) は記憶しない。
/// </summary>
public sealed record TransferField(string Key, TransferFieldKind Kind, string Default, IReadOnlyList<string>? Choices = null, bool Remember = true)
{
    /// <summary>見出しのリソースのキー。</summary>
    public string LabelKey => "Transfer_" + Key;
}

/// <summary>
/// エクスポートの欄の、ドキュメントごとの既定値 (TOOL-05・TOOL-06 の「インポート時の値」、TOOL-10 の「画面と同じ」)。
/// </summary>
public sealed record ExportDefaults
{
    /// <summary>元のファイル名 (S0 のヘッダ・UUEncode の begin 行の既定)。</summary>
    public string FileName { get; init; } = "data.bin";

    /// <summary>インポート時 (ENG-38 で開いた・インポートで新しいドキュメントにした) の実行開始アドレス。</summary>
    public long? ExecAddress { get; init; }

    /// <summary>インポート時の実行開始アドレスが Intel HEX の <c>03</c> (開始セグメントアドレス) だった。</summary>
    public bool ExecIsSegment { get; init; }

    /// <summary>インポート時の <c>S0</c> の文字列。</summary>
    public string? Header { get; init; }

    /// <summary>画面の文字コード (ダンプの既定。TOOL-10 の仕様 1)。</summary>
    public string TextEncodingId { get; init; } = "ascii";

    /// <summary>インポートの結果 (付随データ) から既定値を作る。</summary>
    public static ExportDefaults From(string fileName, EncodedFileSettings? imported, string? textEncodingId = null) => new()
    {
        FileName = fileName,
        ExecAddress = imported?.StartAddress,
        ExecIsSegment = imported?.StartIsSegment ?? false,
        Header = imported?.Header,
        TextEncodingId = textEncodingId ?? "ascii",
    };
}

/// <summary>
/// インポート / エクスポートの形式ごとの設定の欄と、欄の値から設定 (<see cref="ExportOptions"/>・<see cref="ImportOptions"/>) を作る規則。
/// 値の保存先 (アプリの状態) は呼び出し側が持ち、ここでは JSON との変換だけを行う。
/// </summary>
public static class TransferOptions
{
    private static readonly string[] Sizes = ["1", "2", "4", "8"];

    /// <summary>1 レコードのデータ長の候補 (TOOL-05 の仕様 3: 16 が既定、32 も示す)。</summary>
    private static readonly string[] RecordLengths = ["16", "32"];

    private static TransferField T(string key, string value, bool remember = true) => new(key, TransferFieldKind.Text, value, null, remember);

    private static TransferField C(string key, bool value) => new(key, TransferFieldKind.Check, value ? "true" : "false");

    private static TransferField L(string key, string value, params string[] choices) => new(key, TransferFieldKind.Choice, value, choices);

    /// <summary>テキストの出力の改行と文字コード (TOOL-04 の仕様 5)。</summary>
    private static readonly TransferField[] TextOutput =
    [
        L("newLine", "crlf", "crlf", "lf"),
        L("encoding", "utf8", "utf8", "utf8bom", "ascii"),
    ];

    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>エクスポートの欄。<paramref name="view"/> は画面の表示設定 (ダンプの既定値。TOOL-10 の仕様 1)。</summary>
    public static IReadOnlyList<TransferField> ExportFields(string format, ViewSettings? view = null, string fileName = "data.bin") =>
        ExportFields(format, view, new ExportDefaults { FileName = fileName });

    /// <summary>
    /// エクスポートの欄。<paramref name="defaults"/> はドキュメントごとの既定値: S0 のヘッダは「インポート時の値。なければファイル名」、実行開始アドレスは
    /// 「インポート時の値。なければなし (S-record は 0)」(TOOL-05・TOOL-06 の仕様 3)。これらは記憶しない (別のファイルの値を持ち越さない)。
    /// </summary>
    public static IReadOnlyList<TransferField> ExportFields(string format, ViewSettings? view, ExportDefaults defaults)
    {
        view ??= ViewSettings.Default;
        string fileName = Path.GetFileName(defaults.FileName);
        string exec = defaults.ExecAddress is { } e ? Hex(e) : string.Empty;
        TransferField[] fields = format switch
        {
            FormatIds.IntelHex =>
            [
                new("recordLength", TransferFieldKind.Suggest, "16", RecordLengths), L("intelMode", "auto", "auto", "i8", "i16", "i32"),
                T("startAddress", string.Empty, remember: false), T("execAddress", exec, remember: false),
                new("execType", TransferFieldKind.Choice, defaults.ExecIsSegment ? "segment" : "linear", ["linear", "segment"], Remember: false),
                C("omitGaps", false), T("gapValue", "FF"), T("gapRun", "16"), C("upperCase", true),
            ],
            FormatIds.SRecord =>
            [
                new("recordLength", TransferFieldKind.Suggest, "16", RecordLengths), L("srecMode", "auto", "auto", "s1", "s2", "s3"),
                T("startAddress", string.Empty, remember: false), T("execAddress", defaults.ExecAddress is null ? "0" : exec, remember: false),
                T("header", defaults.Header ?? fileName, remember: false), C("writeCount", true), C("omitGaps", false),
                T("gapValue", "FF"), T("gapRun", "16"), C("upperCase", true),
            ],
            FormatIds.Base64 => [C("urlSafe", false), T("lineLength", "76"), C("padding", true)],
            FormatIds.Base32 => [T("lineLength", "0"), C("padding", true)],
            FormatIds.Ascii85 => [C("delimiters", true), T("lineLength", "75")],
            FormatIds.UUEncode => [T("uuName", fileName, remember: false), T("uuMode", "644")],
            FormatIds.QuotedPrintable => [T("lineLength", "76")],
            FormatIds.Url => [C("keepUnreserved", true)],
            FormatIds.HexText =>
            [
                T("bytesPerLine", "16"), L("separator", "space", "space", "commaSpace", "none", "custom"), T("customSeparator", string.Empty),
                L("prefix", "none", "none", "0x", "backslashX", "dollar", "custom"), T("customPrefix", string.Empty), C("upperCase", true),
                C("offsets", false),
            ],
            FormatIds.DecimalText =>
            [
                L("valueSize", "1", Sizes), C("signed", false), C("bigEndian", false), T("perLine", "16"), T("decSeparator", ", "),
            ],
            FormatIds.Ips or FormatIds.Ips32 => [L("ipsFormat", format == FormatIds.Ips32 ? "ips32" : "auto", "auto", "ips", "ips32"), C("rle", true)],
            FormatIds.Binary => [],
            _ when FormatIds.SourceArrays.Contains(format) =>
            [
                L("elementSize", "1", Sizes), C("bigEndian", false), C("decimal", false), T("bytesPerLine", "16"), T("variableName", "data"),
                .. ModifierField(format) is { } modifier ? new[] { modifier } : [],
                .. format is FormatIds.C or FormatIds.Cpp ? new[] { C("headerFile", false), C("lengthConstant", false) } : [],
                C("comment", true),
            ],
            _ when FormatIds.Dumps.Contains(format) =>
            [
                T("bytesPerRow", view.BytesPerRow.ToString(CultureInfo.InvariantCulture)),
                T("groupSize", view.GroupSize.ToString(CultureInfo.InvariantCulture)),
                C("showOffset", view.ShowOffsetColumn), C("showHex", view.ShowHexColumn), C("showText", view.ShowTextColumn),
                L("radix", view.Radix switch { OffsetRadix.Decimal => "dec", OffsetRadix.Octal => "oct", _ => "hex" }, "hex", "dec", "oct"),
                C("upperCase", !view.LowercaseHex),
                T("textEncoding", defaults.TextEncodingId, remember: false),
                .. format switch
                {
                    FormatIds.DumpText => new[] { L("columnSeparator", "bar", "space", "bar") },
                    FormatIds.Html => [L("columnSeparator", "bar", "space", "bar"), C("colors", true), C("dark", false), C("tooltips", true)],
                    FormatIds.Rtf => [L("columnSeparator", "bar", "space", "bar"), C("colors", true), T("rtfFont", "Cascadia Mono"), T("rtfSize", "10")],
                    FormatIds.Tex =>
                    [
                        L("columnSeparator", "bar", "space", "bar"), L("texEnv", "verbatim", "verbatim", "alltt"), C("colors", true),
                        C("texDocument", false),
                    ],
                    _ => [L("columnSeparator", "bar", "space", "bar"), C("table", false)],
                },
            ],
            _ => [],
        };
        return FormatIds.IsText(format) ? [.. fields, .. TextOutput] : fields;
    }

    /// <summary>配列の修飾 (<c>const</c> / <c>static</c> など。TOOL-09 の仕様 2)。修飾のない言語 (Python) では null。</summary>
    private static TransferField? ModifierField(string format) => format == FormatIds.Python ? null
        : L("modifier", "default", "default", "none", "const", "static", "staticConst");

    /// <summary>インポートの欄。</summary>
    public static IReadOnlyList<TransferField> ImportFields(string format) => format switch
    {
        FormatIds.IntelHex or FormatIds.SRecord =>
        [
            L("placement", "lowest", "lowest", "absolute"), T("gapFill", "FF"), C("preferLater", false), C("gapBookmarks", false),
        ],
        FormatIds.Base64 => [C("ignoreWhitespace", true), C("allowMissingPadding", true)],
        FormatIds.Base32 => [C("allowMissingPadding", true)],
        FormatIds.Url => [C("plusAsSpace", false)],

        // 読むファイルはファイルごとに違うため記憶しない (複数あれば、名前の一覧から選ぶ。TOOL-07 の仕様 1)。
        FormatIds.UUEncode => [T("uuIndex", "1", remember: false)],
        FormatIds.HexText => [C("dumpAuto", true)],
        FormatIds.DecimalText => [L("valueSize", "1", Sizes), C("signed", false), C("bigEndian", false)],
        _ when FormatIds.SourceArrays.Contains(format) => [L("elementSize", "auto", "auto", "1", "2", "4", "8"), C("bigEndian", false)],
        _ => [],
    };

    // ---- 値の読み書き (形式ごとに前回の値を記憶する。TOOL-04 の仕様 4) ----

    /// <summary>欄の既定値に、記憶していた値 (<paramref name="savedJson"/>) を重ねる。記憶しない欄は既定値のまま。</summary>
    public static Dictionary<string, string> Load(string? savedJson, IReadOnlyList<TransferField> fields)
    {
        var values = fields.ToDictionary(f => f.Key, f => f.Default);
        var remembered = fields.Where(f => f.Remember).Select(f => f.Key).ToHashSet();
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrEmpty(savedJson) ? "{}" : savedJson) is { } saved)
            {
                foreach ((string key, string value) in saved)
                {
                    if (remembered.Contains(key))
                    {
                        values[key] = value;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }

    /// <summary>記憶する値の JSON (<see cref="TransferField.Remember"/> が偽の欄は含めない)。</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> values, IReadOnlyList<TransferField> fields)
    {
        var remembered = fields.Where(f => f.Remember).Select(f => f.Key).ToHashSet();
        return JsonSerializer.Serialize(values.Where(p => remembered.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    private static string Get(IReadOnlyDictionary<string, string> v, string key, string fallback = "") => v.TryGetValue(key, out string? s) ? s : fallback;

    private static bool Flag(IReadOnlyDictionary<string, string> v, string key, bool fallback = false) =>
        v.TryGetValue(key, out string? s) ? s == "true" : fallback;

    private static int Int(IReadOnlyDictionary<string, string> v, string key, int fallback) =>
        int.TryParse(Get(v, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;

    /// <summary>入力式の値 (空欄は null)。解釈できなければ例外。</summary>
    private static long? Address(IReadOnlyDictionary<string, string> v, string key, Func<string, long?> evaluate) =>
        Get(v, key).Trim() is { Length: > 0 } text ? evaluate(text) : null;

    private static byte Byte(IReadOnlyDictionary<string, string> v, string key, byte fallback) =>
        byte.TryParse(Get(v, key), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b) ? b : fallback;

    /// <summary>
    /// 欄の値の誤りの一覧 (欄のキー)。数値の欄が範囲外・解釈できない場合。誤りがあれば「エクスポート」を押せない。
    /// </summary>
    public static IReadOnlyList<string> Validate(string format, IReadOnlyDictionary<string, string> v, Func<string, long?> evaluate)
    {
        var bad = new List<string>();
        void Range(string key, int min, int max)
        {
            if (v.ContainsKey(key) && (!int.TryParse(Get(v, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i < min || i > max))
            {
                bad.Add(key);
            }
        }

        Range("recordLength", 1, format == FormatIds.SRecord ? 250 : 255);
        Range("lineLength", 0, 100_000);
        Range("bytesPerLine", 0, 1024);
        Range("perLine", 1, 100_000);
        Range("bytesPerRow", 1, 1024);
        Range("groupSize", 0, 1024);
        Range("gapRun", 1, int.MaxValue);
        Range("uuIndex", 1, 1000);
        foreach (string key in new[] { "startAddress", "execAddress" })
        {
            if (Get(v, key).Trim().Length > 0 && evaluate(Get(v, key)) is null)
            {
                bad.Add(key);
            }
        }

        foreach (string key in new[] { "gapValue", "gapFill" })
        {
            if (v.ContainsKey(key) && !byte.TryParse(Get(v, key), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                bad.Add(key);
            }
        }

        if (v.ContainsKey("variableName") && CopyFormatterIdentifier(format, Get(v, "variableName")) == false)
        {
            bad.Add("variableName");
        }

        // S0 のヘッダは ASCII で最大 64 文字 (TOOL-06 の仕様 3)。
        if (v.ContainsKey("header") && (Get(v, "header").Length > 64 || Get(v, "header").Any(c => c is < ' ' or > '~')))
        {
            bad.Add("header");
        }

        // 文字コードは知っている名前だけ (知らない名前は ASCII にせず誤りにする)。
        if (v.ContainsKey("textEncoding") && !IsKnownEncoding(Get(v, "textEncoding")))
        {
            bad.Add("textEncoding");
        }

        return bad;
    }

    private static bool IsKnownEncoding(string id)
    {
        id = id.Trim();
        return TextEncoding.SelectableIds.Contains(id, StringComparer.OrdinalIgnoreCase) || id.Equals("oem", StringComparison.OrdinalIgnoreCase)
            || EncodingCatalog.Find(id) is not null
            || id.StartsWith("cp", StringComparison.OrdinalIgnoreCase) && int.TryParse(id.AsSpan(2), out int page) && page > 0;
    }

    private static bool? CopyFormatterIdentifier(string format, string name) =>
        Exporter.CopyFormatOf(format) is { } f ? CopyFormatter.IsValidIdentifier(f, name) : null;

    /// <summary>
    /// 欄の値からエクスポートの設定を作る。<paramref name="dump"/> はダンプの既定 (強調・タイトルなど)。実行開始アドレスの欄が空なら、Intel HEX は
    /// 「なし」、S-record は 0 (TOOL-05・TOOL-06 の仕様 3。インポート時の値は欄の既定値として入っている)。
    /// </summary>
    public static ExportOptions ToExportOptions(string format, IReadOnlyDictionary<string, string> v, Func<string, long?> evaluate, DumpOptions dump)
    {
        string separator = Get(v, "separator") switch
        {
            "commaSpace" => ", ",
            "none" => string.Empty,
            "custom" => Get(v, "customSeparator"),
            _ => " ",
        };
        string prefix = Get(v, "prefix") switch
        {
            "0x" => "0x",
            "backslashX" => "\\x",
            "dollar" => "$",
            "custom" => Get(v, "customPrefix"),
            _ => string.Empty,
        };
        GapOmission? omit = Flag(v, "omitGaps") ? new GapOmission(Byte(v, "gapValue", 0xFF), Int(v, "gapRun", 16)) : null;
        long? exec = Address(v, "execAddress", evaluate);
        return new ExportOptions
        {
            Format = format,
            NewLine = Get(v, "newLine") == "lf" ? "\n" : "\r\n",
            Encoding = Get(v, "encoding") switch { "utf8bom" => TextFileEncoding.Utf8Bom, "ascii" => TextFileEncoding.Ascii, _ => TextFileEncoding.Utf8 },
            StartAddress = Address(v, "startAddress", evaluate),
            Records = new RecordExportOptions
            {
                RecordLength = Int(v, "recordLength", 16),
                IntelMode = Get(v, "intelMode") switch
                {
                    "i8" => IntelHexAddressMode.I8Hex,
                    "i16" => IntelHexAddressMode.I16Hex,
                    "i32" => IntelHexAddressMode.I32Hex,
                    _ => IntelHexAddressMode.Auto,
                },
                SRecordMode = Get(v, "srecMode") switch
                {
                    "s1" => SRecordAddressMode.S1,
                    "s2" => SRecordAddressMode.S2,
                    "s3" => SRecordAddressMode.S3,
                    _ => SRecordAddressMode.Auto,
                },
                ExecAddress = exec ?? (format == FormatIds.SRecord ? 0 : null),
                ExecIsSegment = Get(v, "execType") == "segment",
                Header = Get(v, "header"),
                WriteCount = Flag(v, "writeCount", true),
                UpperCase = Flag(v, "upperCase", true),
                OmitGaps = omit,
            },
            Copy = new CopyOptions
            {
                Base64UrlSafe = Flag(v, "urlSafe"),
                Base64LineLength = format == FormatIds.Base64 ? Int(v, "lineLength", 76) : null,
                Base64Padding = Flag(v, "padding", true),
                Base32LineLength = format == FormatIds.Base32 ? Int(v, "lineLength", 0) : 0,
                Base32Padding = Flag(v, "padding", true),
                Ascii85Delimiters = Flag(v, "delimiters", true),
                Ascii85LineLength = format == FormatIds.Ascii85 ? Int(v, "lineLength", 75) : 0,
                EncodedFileName = Get(v, "uuName", "data.bin"),
                EncodedFileMode = Get(v, "uuMode", "644"),
                QuotedPrintableLineLength = format == FormatIds.QuotedPrintable ? Int(v, "lineLength", 76) : 76,
                UrlKeepUnreserved = Flag(v, "keepUnreserved", true),
                ElementSize = Int(v, "elementSize", 1),
                BigEndian = Flag(v, "bigEndian"),
                ArrayDecimal = Flag(v, "decimal"),
                BytesPerLine = Math.Clamp(Int(v, "bytesPerLine", 16), 1, 1024),
                VariableName = Get(v, "variableName", "data"),
                UpperCase = Flag(v, "upperCase", true),
                Modifier = Get(v, "modifier") switch
                {
                    "none" => ArrayModifier.None,
                    "const" => ArrayModifier.Const,
                    "static" => ArrayModifier.Static,
                    "staticConst" => ArrayModifier.StaticConst,
                    _ => ArrayModifier.Default,
                },
            },
            HeaderFile = Flag(v, "headerFile"),
            LengthConstant = Flag(v, "lengthConstant"),
            SourceComment = Flag(v, "comment", true),
            HexBytesPerLine = Int(v, "bytesPerLine", 16),
            HexSeparator = separator,
            HexPrefix = prefix,
            HexUpperCase = Flag(v, "upperCase", true),
            HexOffsets = Flag(v, "offsets"),
            DecimalValueSize = Int(v, "valueSize", 1),
            DecimalSigned = Flag(v, "signed"),
            DecimalBigEndian = Flag(v, "bigEndian"),
            DecimalPerLine = Int(v, "perLine", 16),
            DecimalSeparator = Get(v, "decSeparator", ", "),
            Dump = dump with
            {
                BytesPerRow = Int(v, "bytesPerRow", dump.BytesPerRow),
                GroupSize = Int(v, "groupSize", dump.GroupSize),
                ShowOffset = Flag(v, "showOffset", true),
                ShowHex = Flag(v, "showHex", true),
                ShowText = Flag(v, "showText", true),
                OffsetRadix = Get(v, "radix") switch { "dec" => OffsetRadix.Decimal, "oct" => OffsetRadix.Octal, _ => OffsetRadix.Hex },
                UpperCase = Flag(v, "upperCase", true),
                Encoding = Get(v, "textEncoding") is { Length: > 0 } enc && IsKnownEncoding(enc) ? TextEncoding.FromId(enc.Trim()) : dump.Encoding,
                Separator = Get(v, "columnSeparator") == "space" ? DumpColumnSeparator.Space : DumpColumnSeparator.Bar,
                Colors = Flag(v, "colors", true),
                DarkScheme = Flag(v, "dark"),
                BookmarkTooltips = Flag(v, "tooltips", true),
                RtfFont = Get(v, "rtfFont", "Cascadia Mono"),
                RtfFontSize = double.TryParse(Get(v, "rtfSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? size : 10,
                Tex = Get(v, "texEnv") == "alltt" ? TexEnvironment.AlltColor : TexEnvironment.Verbatim,
                TexDocument = Flag(v, "texDocument"),
                MarkdownTable = Flag(v, "table"),
            },
            Ips = Get(v, "ipsFormat") switch { "ips" => IpsFormat.Ips, "ips32" => IpsFormat.Ips32, _ => IpsFormat.Auto },
            IpsRle = Flag(v, "rle", true),
        };
    }

    /// <summary>欄の値からインポートの設定を作る。</summary>
    public static ImportOptions ToImportOptions(string format, IReadOnlyDictionary<string, string> v) => new()
    {
        Format = format,
        Placement = Get(v, "placement") == "absolute" ? AddressPlacement.Absolute : AddressPlacement.Lowest,
        GapFill = Byte(v, "gapFill", 0xFF),
        PreferLater = Flag(v, "preferLater"),
        GapBookmarks = Flag(v, "gapBookmarks"),
        IgnoreWhitespace = Flag(v, "ignoreWhitespace", true),
        AllowMissingPadding = Flag(v, "allowMissingPadding", true),
        PlusAsSpace = Flag(v, "plusAsSpace"),
        UuFileIndex = Math.Max(0, Int(v, "uuIndex", 1) - 1),
        DumpAuto = Flag(v, "dumpAuto", true),
        ValueSize = Get(v, FormatIds.SourceArrays.Contains(format) ? "elementSize" : "valueSize", "1") is "auto" ? 0
            : Int(v, FormatIds.SourceArrays.Contains(format) ? "elementSize" : "valueSize", 1),
        Signed = Flag(v, "signed"),
        BigEndian = Flag(v, "bigEndian"),
    };
}
