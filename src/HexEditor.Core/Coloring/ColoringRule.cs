using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Coloring;

/// <summary>色付けルールの条件の種類 (INSP-33 の仕様 2)。</summary>
public enum ColoringConditionKind
{
    /// <summary>バイト値: 値と範囲の並び (<c>00</c>、<c>20-7E</c>、<c>7F, 80-FF</c>)。</summary>
    ByteValues,

    /// <summary>オフセット範囲: 開始と「終了 (このバイトを含む)」の入力式。</summary>
    OffsetRange,

    /// <summary>周期: オフセット mod x = y の位置から長さ L。</summary>
    Period,

    /// <summary>Hex パターン (ワイルドカード付き。FIND-06)。</summary>
    HexPattern,

    /// <summary>テキスト (文字コード、大文字・小文字の区別。FIND-07、FIND-10)。</summary>
    Text,

    /// <summary>正規表現 (テキストまたはバイト列。一致の最大長は 256 バイト)。</summary>
    Regex,

    /// <summary>数値: 型、エンディアン、値の範囲、位置の条件 (倍数)。</summary>
    Number,
}

/// <summary>枠線の形 (INSP-33 の仕様 1)。</summary>
public enum ColoringBorder
{
    None,
    Solid,
    Dashed,
    Dotted,
}

/// <summary>色を付ける列 (INSP-33 の仕様 1。既定は両方)。</summary>
public enum ColoringTarget
{
    Both,
    Hex,
    Text,
}

/// <summary>ルールの集まりの種類 (INSP-33 の仕様 3)。</summary>
public enum ColoringScope
{
    /// <summary>全体のルール: すべてのドキュメントに適用する。設定として保存する。</summary>
    Global,

    /// <summary>ドキュメントのルール: そのドキュメントだけに適用する。付随データとして保存する。</summary>
    Document,
}

/// <summary>
/// 色付けルール 1 つ (INSP-33 の仕様 1・2)。条件の値は種類ごとの欄に持つ (使わない欄は無視する)。色は 0xRRGGBB (null は指定なし)。
/// 変更は新しいインスタンスを作る。
/// </summary>
public sealed record ColoringRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public ColoringConditionKind Kind { get; init; } = ColoringConditionKind.ByteValues;

    /// <summary>
    /// 条件の主な値: バイト値の並び、Hex パターン、テキスト、正規表現、オフセット範囲の開始の入力式、数値の範囲 (<c>0..0xFF</c> または 1 つの値)。
    /// </summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>オフセット範囲の「終了 (このバイトを含む)」の入力式。</summary>
    public string EndExpression { get; init; } = string.Empty;

    /// <summary>周期の x (オフセット mod x)、数値の位置の条件の倍数 (1 なら条件なし)。</summary>
    public long Modulus { get; init; } = 1;

    /// <summary>周期の y、数値の位置の条件の余り。</summary>
    public long Remainder { get; init; }

    /// <summary>周期の長さ。</summary>
    public long PeriodLength { get; init; } = 1;

    /// <summary>テキスト・正規表現の文字コード (コードページ)。</summary>
    public int CodePage { get; init; } = 65001;

    public bool CaseSensitive { get; init; } = true;

    /// <summary>正規表現をテキスト (文字コードで解読した文字列) に使う。false ならバイト列 (各バイトを U+0000〜U+00FF の文字とみなす)。</summary>
    public bool RegexOnText { get; init; }

    /// <summary>数値の型 (インスペクタの型の ID: int8〜uint64、float、double)。</summary>
    public string NumberType { get; init; } = "uint32";

    public bool BigEndian { get; init; }

    public uint? Foreground { get; init; }

    public uint? Background { get; init; }

    public ColoringBorder Border { get; init; } = ColoringBorder.None;

    public ColoringTarget Target { get; init; } = ColoringTarget.Both;

    /// <summary>適用する範囲の開始の入力式 (空ならドキュメント全体)。</summary>
    public string RangeStart { get; init; } = string.Empty;

    /// <summary>適用する範囲の「終了 (このバイトを含む)」の入力式。</summary>
    public string RangeEnd { get; init; } = string.Empty;

    /// <summary>組み込みのプリセット (INSP-33 の仕様 5) の ID。利用者が作ったルールは null。</summary>
    public string? Preset { get; init; }

    /// <summary>1 つの集まりのルールの上限 (INSP-33 の仕様 4)。</summary>
    public const int MaxRules = 256;

    // ---- 保存 ----

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["enabled"] = Enabled,
            ["kind"] = Kind.ToString(),
            ["pattern"] = Pattern,
        };
        if (EndExpression.Length > 0)
        {
            o["end"] = EndExpression;
        }

        if (Kind is ColoringConditionKind.Period or ColoringConditionKind.Number)
        {
            o["modulus"] = Modulus;
            o["remainder"] = Remainder;
        }

        if (Kind == ColoringConditionKind.Period)
        {
            o["periodLength"] = PeriodLength;
        }

        if (Kind is ColoringConditionKind.Text or ColoringConditionKind.Regex)
        {
            o["codePage"] = CodePage;
            o["caseSensitive"] = CaseSensitive;
        }

        if (Kind == ColoringConditionKind.Regex)
        {
            o["regexOnText"] = RegexOnText;
        }

        if (Kind == ColoringConditionKind.Number)
        {
            o["numberType"] = NumberType;
            o["bigEndian"] = BigEndian;
        }

        if (Foreground is { } fore)
        {
            o["foreground"] = Hex(fore);
        }

        if (Background is { } back)
        {
            o["background"] = Hex(back);
        }

        if (Border != ColoringBorder.None)
        {
            o["border"] = Border.ToString();
        }

        if (Target != ColoringTarget.Both)
        {
            o["target"] = Target.ToString();
        }

        if (RangeStart.Length > 0 || RangeEnd.Length > 0)
        {
            o["rangeStart"] = RangeStart;
            o["rangeEnd"] = RangeEnd;
        }

        if (Preset is not null)
        {
            o["preset"] = Preset;
        }

        return o;
    }

    public static string Hex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary><c>#RRGGBB</c> を読む。読めなければ null。</summary>
    public static uint? ParseHex(string? text)
    {
        text = text?.Trim();
        return text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? rgb
            : null;
    }

    /// <summary>保存した値を読む。読めない欄は既定の値。</summary>
    public static ColoringRule FromJson(JsonNode node)
    {
        JsonObject o = node.AsObject();
        string Str(string key, string fallback = "") => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : fallback;
        long Num(string key, long fallback) => o[key] is JsonValue v && v.TryGetValue(out long n) ? n : fallback;
        bool Flag(string key, bool fallback) => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
        T Enum<T>(string key, T fallback)
            where T : struct, System.Enum => System.Enum.TryParse(Str(key), ignoreCase: true, out T value) ? value : fallback;
        return new ColoringRule
        {
            Id = Str("id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
            Name = Str("name"),
            Enabled = Flag("enabled", true),
            Kind = Enum("kind", ColoringConditionKind.ByteValues),
            Pattern = Str("pattern"),
            EndExpression = Str("end"),
            Modulus = Math.Max(1, Num("modulus", 1)),
            Remainder = Num("remainder", 0),
            PeriodLength = Math.Max(1, Num("periodLength", 1)),
            CodePage = (int)Num("codePage", 65001),
            CaseSensitive = Flag("caseSensitive", true),
            RegexOnText = Flag("regexOnText", false),
            NumberType = Str("numberType", "uint32"),
            BigEndian = Flag("bigEndian", false),
            Foreground = ParseHex(Str("foreground")),
            Background = ParseHex(Str("background")),
            Border = Enum("border", ColoringBorder.None),
            Target = Enum("target", ColoringTarget.Both),
            RangeStart = Str("rangeStart"),
            RangeEnd = Str("rangeEnd"),
            Preset = o["preset"] is JsonValue p && p.TryGetValue(out string? preset) ? preset : null,
        };
    }

    /// <summary>ルールの並びを保存の文字列にする (上の行ほど優先。INSP-34 の仕様 1)。</summary>
    public static string Serialize(IEnumerable<ColoringRule> rules) =>
        new JsonArray([.. rules.Select(r => (JsonNode?)r.ToJson())]).ToJsonString();

    /// <summary>保存の文字列を読む。読めなければ null。</summary>
    public static IReadOnlyList<ColoringRule>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) is JsonArray array ? [.. array.OfType<JsonNode>().Select(FromJson).Take(MaxRules)] : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>組み込みのプリセット (INSP-33 の仕様 5。既定ではすべて無効)。</summary>
public static class ColoringPresets
{
    public const string DimZero = "dimZero";
    public const string PrintableAscii = "printableAscii";
    public const string ControlCharacters = "controlCharacters";
    public const string Ff = "ff";
    public const string HighBit = "highBit";

    /// <summary>「ゼロを薄く表示」の文字色 (ライト・ダークのどちらでも読める灰色)。</summary>
    public const uint DimGray = 0x9A9A9A;

    /// <summary>プリセットの並び (名前は表示の言語で <paramref name="name"/> から作る)。</summary>
    public static IReadOnlyList<ColoringRule> All(Func<string, string> name) =>
    [
        new() { Id = DimZero, Preset = DimZero, Name = name(DimZero), Enabled = false, Pattern = "00", Foreground = DimGray },
        new() { Id = PrintableAscii, Preset = PrintableAscii, Name = name(PrintableAscii), Enabled = false, Pattern = "20-7E", Foreground = 0x0063B1 },
        new() { Id = ControlCharacters, Preset = ControlCharacters, Name = name(ControlCharacters), Enabled = false, Pattern = "00-1F, 7F", Foreground = 0xCA5010 },
        new() { Id = Ff, Preset = Ff, Name = name(Ff), Enabled = false, Pattern = "FF", Foreground = 0xD13438 },
        new() { Id = HighBit, Preset = HighBit, Name = name(HighBit), Enabled = false, Pattern = "80-FF", Background = 0xE6D9FF },
    ];
}
