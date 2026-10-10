using System.Globalization;
using System.Text.Json;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Formats;
using HexEditor.Core.View;

namespace HexEditor.App.Services;

/// <summary>インポート / エクスポートのダイアログの、形式ごとの設定の欄の種類。</summary>
public enum TransferFieldKind
{
    Text,
    Check,
    Choice,
}

/// <summary>
/// 設定の欄 1 つ (TOOL-04 の仕様 2 の 3・3 の 2)。<see cref="Choices"/> は選択肢の値 (表示は <c>Transfer_Choice_&lt;値&gt;</c>)。
/// 値は文字列で持ち、形式ごとに前回の値を記憶する (仕様 4)。
/// </summary>
public sealed record TransferField(string Key, TransferFieldKind Kind, string Default, IReadOnlyList<string>? Choices = null)
{
    /// <summary>見出しのリソースのキー。</summary>
    public string LabelKey => "Transfer_" + Key;
}

/// <summary>
/// インポート / エクスポートの形式ごとの設定の欄と、欄の値から設定 (<see cref="ExportOptions"/>・<see cref="ImportOptions"/>) を作る規則。
/// </summary>
public static class TransferOptions
{
    private static readonly string[] Sizes = ["1", "2", "4", "8"];

    private static TransferField T(string key, string value) => new(key, TransferFieldKind.Text, value);

    private static TransferField C(string key, bool value) => new(key, TransferFieldKind.Check, value ? "true" : "false");

    private static TransferField L(string key, string value, params string[] choices) => new(key, TransferFieldKind.Choice, value, choices);

    /// <summary>テキストの出力の改行と文字コード (TOOL-04 の仕様 5)。</summary>
    private static readonly TransferField[] TextOutput =
    [
        L("newLine", "crlf", "crlf", "lf"),
        L("encoding", "utf8", "utf8", "utf8bom", "ascii"),
    ];

    /// <summary>エクスポートの欄。<paramref name="view"/> は画面の表示設定 (ダンプの既定値。TOOL-10 の仕様 1)。</summary>
    public static IReadOnlyList<TransferField> ExportFields(string format, ViewSettings? view = null, string fileName = "data.bin")
    {
        view ??= ViewSettings.Default;
        TransferField[] fields = format switch
        {
            FormatIds.IntelHex =>
            [
                T("recordLength", "16"), L("intelMode", "auto", "auto", "i8", "i16", "i32"), T("startAddress", string.Empty),
                T("execAddress", string.Empty), C("omitGaps", false), T("gapValue", "FF"), T("gapRun", "16"), C("upperCase", true),
            ],
            FormatIds.SRecord =>
            [
                T("recordLength", "16"), L("srecMode", "auto", "auto", "s1", "s2", "s3"), T("startAddress", string.Empty),
                T("execAddress", string.Empty), T("header", Path.GetFileName(fileName)), C("writeCount", true), C("omitGaps", false),
                T("gapValue", "FF"), T("gapRun", "16"), C("upperCase", true),
            ],
            FormatIds.Base64 => [C("urlSafe", false), T("lineLength", "76"), C("padding", true)],
            FormatIds.Base32 => [T("lineLength", "0"), C("padding", true)],
            FormatIds.Ascii85 => [C("delimiters", true), T("lineLength", "75")],
            FormatIds.UUEncode => [T("uuName", Path.GetFileName(fileName)), T("uuMode", "644")],
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
                .. format switch
                {
                    FormatIds.DumpText => new[] { L("columnSeparator", "bar", "space", "bar") },
                    FormatIds.Html => [L("columnSeparator", "bar", "space", "bar"), C("colors", true), C("dark", false), C("tooltips", true)],
                    FormatIds.Rtf => [L("columnSeparator", "bar", "space", "bar"), C("colors", true), T("rtfFont", "Cascadia Mono"), T("rtfSize", "10")],
                    FormatIds.Tex => [L("columnSeparator", "bar", "space", "bar"), L("texEnv", "verbatim", "verbatim", "alltt"), C("colors", true)],
                    _ => [L("columnSeparator", "bar", "space", "bar"), C("table", false)],
                },
            ],
            _ => [],
        };
        return FormatIds.IsText(format) ? [.. fields, .. TextOutput] : fields;
    }

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
        FormatIds.UUEncode => [T("uuIndex", "1")],
        FormatIds.HexText => [C("dumpAuto", true)],
        FormatIds.DecimalText => [L("valueSize", "1", Sizes), C("signed", false), C("bigEndian", false)],
        _ when FormatIds.SourceArrays.Contains(format) => [L("elementSize", "auto", "auto", "1", "2", "4", "8"), C("bigEndian", false)],
        _ => [],
    };

    // ---- 値の読み書き (形式ごとに前回の値を記憶する。TOOL-04 の仕様 4) ----

    public static Dictionary<string, string> Load(string stateKey, IReadOnlyList<TransferField> fields)
    {
        var values = fields.ToDictionary(f => f.Key, f => f.Default);
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(AppState.GetString(stateKey, "{}")) is { } saved)
            {
                foreach ((string key, string value) in saved)
                {
                    if (values.ContainsKey(key))
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

    public static void Save(string stateKey, IReadOnlyDictionary<string, string> values) =>
        AppState.SetString(stateKey, JsonSerializer.Serialize(values));

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

        return bad;
    }

    private static bool? CopyFormatterIdentifier(string format, string name) =>
        Exporter.CopyFormatOf(format) is { } f ? CopyFormatter.IsValidIdentifier(f, name) : null;

    /// <summary>欄の値からエクスポートの設定を作る。<paramref name="dump"/> はダンプの既定 (文字コード・強調・タイトル)。</summary>
    public static ExportOptions ToExportOptions(string format, IReadOnlyDictionary<string, string> v, Func<string, long?> evaluate, DumpOptions dump,
        long? importedExec = null, string? importedHeader = null)
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
                ExecAddress = exec ?? (format == FormatIds.SRecord ? importedExec ?? 0 : importedExec),
                Header = Get(v, "header") is { Length: > 0 } h ? h : importedHeader ?? string.Empty,
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
                Separator = Get(v, "columnSeparator") == "space" ? DumpColumnSeparator.Space : DumpColumnSeparator.Bar,
                Colors = Flag(v, "colors", true),
                DarkScheme = Flag(v, "dark"),
                BookmarkTooltips = Flag(v, "tooltips", true),
                RtfFont = Get(v, "rtfFont", "Cascadia Mono"),
                RtfFontSize = double.TryParse(Get(v, "rtfSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? size : 10,
                Tex = Get(v, "texEnv") == "alltt" ? TexEnvironment.AlltColor : TexEnvironment.Verbatim,
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
