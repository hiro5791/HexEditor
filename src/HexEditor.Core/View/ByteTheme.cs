using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.View;

/// <summary>バイトテーマの種類 (VIEW-17 の仕様 2)。</summary>
public enum ByteThemeKind
{
    None,

    /// <summary>種類別 (6 分類)。</summary>
    Category,

    /// <summary>グラデーション (0〜255 を連続した色の帯に対応させる)。</summary>
    Gradient,

    /// <summary>独自 (JSON で 256 個の値に色を指定)。</summary>
    Custom,
}

/// <summary>「種類別」の分類 (VIEW-17 の仕様 2。ミニマップの「バイトの種類」VIEW-35 でも使う)。</summary>
public enum ByteCategory
{
    /// <summary><c>00</c>。</summary>
    Zero,

    /// <summary><c>FF</c>。</summary>
    Ff,

    /// <summary>印字可能な ASCII (<c>21</c>〜<c>7E</c>)。</summary>
    Printable,

    /// <summary>空白類 (<c>09</c> <c>0A</c> <c>0D</c> <c>20</c>)。</summary>
    Whitespace,

    /// <summary>その他の制御文字 (<c>01</c>〜<c>1F</c>、<c>7F</c>)。</summary>
    Control,

    /// <summary><c>80</c>〜<c>FE</c>。</summary>
    High,
}

/// <summary>1 つの値の色 (文字色と背景色。どちらも省略できる)。</summary>
public readonly record struct ByteThemeColor(SchemeColor? Text, SchemeColor? Background);

/// <summary>独自テーマの JSON の誤り (VIEW-17 の「エラー」: 何行目のどのキーが不正か)。</summary>
public sealed record ByteThemeError(int Line, string Key, string Message);

/// <summary>
/// バイトテーマ (VIEW-17)。バイトの値ごとの文字色と背景色を、ライトとダークで別に持つ。ハイコントラストでは使わない (仕様 4。呼び出し側で判定)。
/// 組み込みのテーマの色は配色 (UI-28) の組み込みと同じく、テーマのデータとして持つ。
/// </summary>
public sealed class ByteTheme
{
    private readonly ByteThemeColor[] _light;
    private readonly ByteThemeColor[] _dark;

    private ByteTheme(string name, ByteThemeKind kind, ByteThemeColor[] light, ByteThemeColor[] dark)
    {
        Name = name;
        Kind = kind;
        _light = light;
        _dark = dark;
    }

    public string Name { get; }

    public ByteThemeKind Kind { get; }

    /// <summary>値の色。指定がなければ両方 null。</summary>
    public ByteThemeColor ColorOf(byte value, bool dark) => (dark ? _dark : _light)[value];

    /// <summary>「種類別」の分類。<c>20</c> は空白類として扱う。</summary>
    public static ByteCategory Classify(byte value) => value switch
    {
        0x00 => ByteCategory.Zero,
        0xFF => ByteCategory.Ff,
        0x09 or 0x0A or 0x0D or 0x20 => ByteCategory.Whitespace,
        < 0x20 or 0x7F => ByteCategory.Control,
        < 0x7F => ByteCategory.Printable,
        _ => ByteCategory.High,
    };

    // 「種類別」の色 (ライト・ダーク)。分類の順 (ByteCategory) に並べる。色覚の多様性に配慮し、明度と色相の両方を変える。
    private static readonly SchemeColor[] CategoryLight =
    [
        new(0xFF, 0x80, 0x80, 0x80), new(0xFF, 0xC4, 0x2B, 0x1C), new(0xFF, 0x00, 0x5F, 0xB8),
        new(0xFF, 0x00, 0x7A, 0x6C), new(0xFF, 0x87, 0x48, 0xB8), new(0xFF, 0xA8, 0x4A, 0x00),
    ];

    private static readonly SchemeColor[] CategoryDark =
    [
        new(0xFF, 0x9A, 0x9A, 0x9A), new(0xFF, 0xFF, 0x99, 0xA4), new(0xFF, 0x6C, 0xB8, 0xF6),
        new(0xFF, 0x4F, 0xD1, 0xC5), new(0xFF, 0xC7, 0xA6, 0xF7), new(0xFF, 0xF7, 0xA3, 0x5C),
    ];

    /// <summary>「種類別」の分類の色 (ミニマップの「バイトの種類」でも使う)。</summary>
    public static SchemeColor CategoryColor(ByteCategory category, bool dark) => (dark ? CategoryDark : CategoryLight)[(int)category];

    /// <summary>「種類別」のテーマ。</summary>
    public static ByteTheme Category { get; } = new("category", ByteThemeKind.Category,
        [.. Enumerable.Range(0, 256).Select(b => new ByteThemeColor(CategoryLight[(int)Classify((byte)b)], null))],
        [.. Enumerable.Range(0, 256).Select(b => new ByteThemeColor(CategoryDark[(int)Classify((byte)b)], null))]);

    // cividis の色の帯の制御点 (0、0.25、0.5、0.75、1)。ライトでは暗い側を、ダークでは明るい側を使って背景との差を保つ。
    private static readonly SchemeColor[] Cividis =
    [
        new(0xFF, 0x00, 0x22, 0x4E), new(0xFF, 0x35, 0x45, 0x6C), new(0xFF, 0x7C, 0x7B, 0x78), new(0xFF, 0xC4, 0xB5, 0x6C),
        new(0xFF, 0xFE, 0xE8, 0x38),
    ];

    /// <summary>グラデーションの色 (0〜1 の値。cividis 系。ミニマップの「バイト値」でも使う)。</summary>
    public static SchemeColor GradientColor(double t)
    {
        t = Math.Clamp(t, 0, 1) * (Cividis.Length - 1);
        int i = Math.Min(Cividis.Length - 2, (int)t);
        double f = t - i;
        SchemeColor a = Cividis[i];
        SchemeColor b = Cividis[i + 1];
        return new SchemeColor(0xFF, Lerp(a.R, b.R, f), Lerp(a.G, b.G, f), Lerp(a.B, b.B, f));

        static byte Lerp(byte x, byte y, double f) => (byte)Math.Round(x + (y - x) * f);
    }

    /// <summary>「グラデーション」のテーマ。ライトでは帯の 0〜0.7、ダークでは 0.3〜1 を使う (背景に溶けないように)。</summary>
    public static ByteTheme Gradient { get; } = new("gradient", ByteThemeKind.Gradient,
        [.. Enumerable.Range(0, 256).Select(b => new ByteThemeColor(GradientColor(b / 255.0 * 0.7), null))],
        [.. Enumerable.Range(0, 256).Select(b => new ByteThemeColor(GradientColor(0.3 + b / 255.0 * 0.7), null))]);

    /// <summary>
    /// 文字色と背景色のコントラスト比が 3:1 未満なら、通常の文字色に置き換える (VIEW-17 の仕様 9)。
    /// </summary>
    public static SchemeColor ReadableText(SchemeColor text, SchemeColor background, SchemeColor normal) =>
        SchemeColor.ContrastRatio(text, background) < MinimumContrast ? normal : text;

    /// <summary>置き換えの基準のコントラスト比 (VIEW-17 の仕様 9)。</summary>
    public const double MinimumContrast = 3.0;

    /// <summary>
    /// 独自テーマの JSON を読む (仕様 3)。キーは 2 桁の Hex か <c>XX-YY</c>、後に書いたものが優先。値は色の文字列か
    /// <c>{"text", "background"}</c>。<c>light</c> と <c>dark</c> の片方しかなければ両方に使う。誤りがあれば <paramref name="error"/> に
    /// 行番号とキーを入れて false を返す (テーマは作らない)。
    /// </summary>
    public static bool TryParse(string json, string fallbackName, out ByteTheme? theme, out ByteThemeError? error)
    {
        theme = null;
        error = null;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string name = fallbackName;
        ByteThemeColor[]? light = null;
        ByteThemeColor[]? dark = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = new ByteThemeError(LineOf(bytes, reader.TokenStartIndex), string.Empty, "root");
                return false;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string key = reader.GetString() ?? string.Empty;
                int keyLine = LineOf(bytes, reader.TokenStartIndex);
                reader.Read();
                switch (key)
                {
                    case "name" when reader.TokenType == JsonTokenType.String:
                        name = reader.GetString() is { Length: > 0 } n ? n : fallbackName;
                        break;
                    case "light" or "dark":
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            error = new ByteThemeError(keyLine, key, "object");
                            return false;
                        }

                        ByteThemeColor[]? table = ReadTable(ref reader, bytes, out error);
                        if (table is null)
                        {
                            return false;
                        }

                        if (key == "light")
                        {
                            light = table;
                        }
                        else
                        {
                            dark = table;
                        }

                        break;
                    default:
                        // 知らない項目は飛ばす。
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            error = new ByteThemeError((int)(ex.LineNumber ?? 0) + 1, string.Empty, "syntax");
            return false;
        }

        if (light is null && dark is null)
        {
            error = new ByteThemeError(1, "light", "missing");
            return false;
        }

        theme = new ByteTheme(name, ByteThemeKind.Custom, light ?? dark!, dark ?? light!);
        return true;
    }

    private static ByteThemeColor[]? ReadTable(ref Utf8JsonReader reader, byte[] bytes, out ByteThemeError? error)
    {
        error = null;
        var table = new ByteThemeColor[256];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString() ?? string.Empty;
            int line = LineOf(bytes, reader.TokenStartIndex);
            if (!TryParseKey(key, out int from, out int to))
            {
                error = new ByteThemeError(line, key, "key");
                return null;
            }

            reader.Read();
            ByteThemeColor color;
            if (reader.TokenType == JsonTokenType.String)
            {
                if (!SchemeColor.TryParse(reader.GetString(), out SchemeColor text))
                {
                    error = new ByteThemeError(line, key, "color");
                    return null;
                }

                color = new ByteThemeColor(text, null);
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                SchemeColor? text = null;
                SchemeColor? background = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string part = reader.GetString() ?? string.Empty;
                    reader.Read();
                    if (part is "text" or "background")
                    {
                        if (reader.TokenType != JsonTokenType.String || !SchemeColor.TryParse(reader.GetString(), out SchemeColor c))
                        {
                            error = new ByteThemeError(LineOf(bytes, reader.TokenStartIndex), key, "color");
                            return null;
                        }

                        if (part == "text")
                        {
                            text = c;
                        }
                        else
                        {
                            background = c;
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                color = new ByteThemeColor(text, background);
            }
            else
            {
                error = new ByteThemeError(line, key, "value");
                return null;
            }

            // 後に書いたものが優先する。
            for (int b = from; b <= to; b++)
            {
                table[b] = color;
            }
        }

        return table;
    }

    /// <summary>キー (<c>XX</c> か <c>XX-YY</c>) を読む。</summary>
    private static bool TryParseKey(string key, out int from, out int to)
    {
        from = to = 0;
        string[] parts = key.Split('-');
        if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length != 2
            || !int.TryParse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        from = int.Parse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        to = parts.Length == 2 ? int.Parse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : from;
        return from <= to;
    }

    /// <summary>バイト位置の行番号 (1 始まり)。</summary>
    private static int LineOf(byte[] bytes, long index)
    {
        int line = 1;
        for (long i = 0; i < Math.Min(index, bytes.Length); i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
            }
        }

        return line;
    }
}

/// <summary>
/// 独自のバイトテーマのファイルの置き場所 (設定フォルダの <c>byte-themes</c>)。読み込んだテーマは設定フォルダにコピーして保存し、元のファイルが
/// なくても使える (UI-28 の配色の保存と同じ考え方)。
/// </summary>
public sealed class ByteThemeStore(string settingsFolder)
{
    public const string FolderName = "byte-themes";

    /// <summary>設定の値 (<c>view.byteTheme</c>) の、独自テーマの接頭辞。</summary>
    public const string CustomPrefix = "custom:";

    public string Folder => Path.Combine(settingsFolder, FolderName);

    /// <summary>独自テーマを読み、設定フォルダにコピーする。誤りがあれば null と誤り。</summary>
    public (ByteTheme? Theme, string? Key, ByteThemeError? Error) Import(string path)
    {
        string json = File.ReadAllText(path);
        string name = Path.GetFileNameWithoutExtension(path);
        if (!ByteTheme.TryParse(json, name, out ByteTheme? theme, out ByteThemeError? error))
        {
            return (null, null, error);
        }

        Directory.CreateDirectory(Folder);
        string file = SafeFileName(name) + ".json";
        File.WriteAllText(Path.Combine(Folder, file), json, new UTF8Encoding(false));
        return (theme, CustomPrefix + file, null);
    }

    /// <summary>設定の値からテーマを得る (<c>none</c> / <c>category</c> / <c>gradient</c> / <c>custom:ファイル名</c>)。読めなければ null。</summary>
    public ByteTheme? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == "none")
        {
            return null;
        }

        if (key == "category")
        {
            return ByteTheme.Category;
        }

        if (key == "gradient")
        {
            return ByteTheme.Gradient;
        }

        if (!key.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string path = Path.Combine(Folder, Path.GetFileName(key[CustomPrefix.Length..]));
        try
        {
            return File.Exists(path) && ByteTheme.TryParse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), out ByteTheme? theme, out _)
                ? theme
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return sb.Length == 0 ? "theme" : sb.ToString();
    }
}
