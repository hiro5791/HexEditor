using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.View;

/// <summary>配色で決める Hex 表示の要素 (UI-28 の仕様 1)。</summary>
public enum SchemeElement
{
    Background,
    AlternateBackground,
    OffsetText,
    HexText,
    TextText,
    Zero,
    NonPrintable,
    Modified,
    Inserted,
    SelectionBackground,
    SelectionText,
    Caret,
    CurrentRowBackground,
    Match,
    Bookmark,
    DiffAdded,
    DiffRemoved,
    DiffChanged,
    Separator,
    RecordAlternate,
}

/// <summary>色 (不透明度付き)。</summary>
public readonly record struct SchemeColor(byte A, byte R, byte G, byte B)
{
    /// <summary><c>#RRGGBB</c> または <c>#AARRGGBB</c> を読む。</summary>
    public static bool TryParse(string? text, out SchemeColor color)
    {
        color = default;
        if (text is null || text.Length is not (7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
        {
            return false;
        }

        color = text.Length == 7
            ? new SchemeColor(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new SchemeColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public override string ToString() => A == 0xFF ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>相対輝度 (WCAG 2.x)。</summary>
    public double Luminance()
    {
        static double Channel(byte c)
        {
            double s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
    }

    /// <summary>コントラスト比 (1〜21)。</summary>
    public static double ContrastRatio(SchemeColor a, SchemeColor b)
    {
        double la = a.Luminance();
        double lb = b.Luminance();
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}

/// <summary>コントラストの警告 (UI-28 の仕様 4)。</summary>
public readonly record struct ContrastWarning(bool Dark, SchemeElement Foreground, SchemeElement Background, double Ratio);

/// <summary>
/// Hex 表示の配色 (UI-28)。ライト用とダーク用の組を持ち、テーマ (UI-26) に合わせて切り替える。値のない要素はテーマのリソースの色を使う
/// (「既定」の配色はすべての要素が値なし)。ハイコントラストでは配色を使わない (仕様 6。呼び出し側で判定する)。
/// </summary>
public sealed class ColorScheme
{
    /// <summary>文字と背景の組のコントラスト比の下限 (仕様 4)。</summary>
    public const double MinimumContrast = 4.5;

    public const string DefaultName = "default";

    /// <summary>コントラストを確かめる文字と背景の組。</summary>
    private static readonly (SchemeElement Fore, SchemeElement Back)[] ContrastPairs =
    [
        (SchemeElement.HexText, SchemeElement.Background),
        (SchemeElement.TextText, SchemeElement.Background),
        (SchemeElement.OffsetText, SchemeElement.Background),
        (SchemeElement.Modified, SchemeElement.Background),
        (SchemeElement.Inserted, SchemeElement.Background),
        (SchemeElement.SelectionText, SchemeElement.SelectionBackground),
        (SchemeElement.HexText, SchemeElement.CurrentRowBackground),
        (SchemeElement.HexText, SchemeElement.AlternateBackground),
    ];

    private readonly Dictionary<SchemeElement, SchemeColor> _light = [];
    private readonly Dictionary<SchemeElement, SchemeColor> _dark = [];

    public ColorScheme(string name, bool builtIn = false)
    {
        Name = name;
        BuiltIn = builtIn;
    }

    /// <summary>配色の名前 (ファイル名にも使う)。</summary>
    public string Name { get; }

    /// <summary>同梱の配色 (編集できない。複製して使う。仕様 3)。</summary>
    public bool BuiltIn { get; }

    /// <summary>読み込んだときの不正な要素 (UI-28 の「エラー」。不正な要素だけ既定の色を使う)。</summary>
    public IReadOnlyList<string> LoadWarnings { get; private set; } = [];

    /// <summary>要素の色。値がなければ null (テーマのリソースの色を使う)。</summary>
    public SchemeColor? Get(SchemeElement element, bool dark) =>
        (dark ? _dark : _light).TryGetValue(element, out SchemeColor c) ? c : null;

    public void Set(SchemeElement element, bool dark, SchemeColor? color)
    {
        Dictionary<SchemeElement, SchemeColor> map = dark ? _dark : _light;
        if (color is { } c)
        {
            map[element] = c;
        }
        else
        {
            map.Remove(element);
        }
    }

    /// <summary>名前を変えた複製 (仕様 3)。</summary>
    public ColorScheme Duplicate(string name)
    {
        var copy = new ColorScheme(name);
        foreach ((SchemeElement e, SchemeColor c) in _light)
        {
            copy._light[e] = c;
        }

        foreach ((SchemeElement e, SchemeColor c) in _dark)
        {
            copy._dark[e] = c;
        }

        return copy;
    }

    /// <summary>
    /// コントラスト比が 4.5:1 未満の文字と背景の組 (仕様 4)。値のない要素は <paramref name="fallback"/> の色 (テーマの色) で比べる。
    /// </summary>
    public IReadOnlyList<ContrastWarning> ContrastWarnings(Func<SchemeElement, bool, SchemeColor?>? fallback = null)
    {
        var warnings = new List<ContrastWarning>();
        foreach (bool dark in (bool[])[false, true])
        {
            foreach ((SchemeElement fore, SchemeElement back) in ContrastPairs)
            {
                SchemeColor? f = Get(fore, dark) ?? fallback?.Invoke(fore, dark);
                SchemeColor? b = Get(back, dark) ?? fallback?.Invoke(back, dark);
                if (f is null || b is null || (Get(fore, dark) is null && Get(back, dark) is null))
                {
                    continue;
                }

                double ratio = SchemeColor.ContrastRatio(f.Value, b.Value);
                if (ratio < MinimumContrast)
                {
                    warnings.Add(new ContrastWarning(dark, fore, back, ratio));
                }
            }
        }

        return warnings;
    }

    // ---- JSON (仕様 5) ----

    public static string ElementKey(SchemeElement element)
    {
        string name = element.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject { ["name"] = Name };
        foreach ((string key, Dictionary<SchemeElement, SchemeColor> map) in new[] { ("light", _light), ("dark", _dark) })
        {
            var obj = new JsonObject();
            foreach (SchemeElement e in Enum.GetValues<SchemeElement>())
            {
                if (map.TryGetValue(e, out SchemeColor c))
                {
                    obj[ElementKey(e)] = c.ToString();
                }
            }

            root[key] = obj;
        }

        return root;
    }

    public string ToJsonString() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// JSON から読む。不正な要素は無視して <see cref="LoadWarnings"/> に記録する (UI-28 の「エラー」)。JSON として読めない場合と
    /// 名前がない場合は <see cref="JsonException"/>。
    /// </summary>
    public static ColorScheme Parse(string json, string? fallbackName = null)
    {
        JsonNode? node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (node is not JsonObject root)
        {
            throw new JsonException("The color scheme must be a JSON object.");
        }

        string? name = root["name"] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : fallbackName;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new JsonException("The color scheme has no name.");
        }

        var scheme = new ColorScheme(name);
        var warnings = new List<string>();
        var keys = Enum.GetValues<SchemeElement>().ToDictionary(ElementKey, e => e, StringComparer.OrdinalIgnoreCase);
        foreach ((string section, bool dark) in new[] { ("light", false), ("dark", true) })
        {
            if (root[section] is not JsonObject obj)
            {
                continue;
            }

            foreach ((string key, JsonNode? value) in obj)
            {
                if (!keys.TryGetValue(key, out SchemeElement element))
                {
                    warnings.Add($"{section}.{key}");
                    continue;
                }

                string? text = value is JsonValue jv && jv.TryGetValue(out string? t) ? t : null;
                if (SchemeColor.TryParse(text, out SchemeColor color))
                {
                    scheme.Set(element, dark, color);
                }
                else
                {
                    warnings.Add($"{section}.{key}");
                }
            }
        }

        // 片方しかない場合は両方に使う (バイトテーマの JSON と同じ扱い)。
        if (root["dark"] is null)
        {
            foreach ((SchemeElement e, SchemeColor c) in scheme._light)
            {
                scheme._dark[e] = c;
            }
        }
        else if (root["light"] is null)
        {
            foreach ((SchemeElement e, SchemeColor c) in scheme._dark)
            {
                scheme._light[e] = c;
            }
        }

        scheme.LoadWarnings = warnings;
        return scheme;
    }

    // ---- 同梱の配色 (仕様 2) ----

    /// <summary>同梱の配色: 既定・ソラライズド・高コントラスト風。</summary>
    public static IReadOnlyList<ColorScheme> BuiltIns { get; } = CreateBuiltIns();

    private static List<ColorScheme> CreateBuiltIns()
    {
        var defaults = new ColorScheme(DefaultName, builtIn: true);

        // Solarized (Ethan Schoonover) の公開されている値。
        var solarized = new ColorScheme("solarized", builtIn: true);
        Fill(solarized, dark: false, new()
        {
            [SchemeElement.Background] = "#FDF6E3",
            [SchemeElement.AlternateBackground] = "#F5EFDC",
            [SchemeElement.CurrentRowBackground] = "#EEE8D5",
            [SchemeElement.OffsetText] = "#586E75",
            [SchemeElement.HexText] = "#073642",
            [SchemeElement.TextText] = "#073642",
            [SchemeElement.Zero] = "#93A1A1",
            [SchemeElement.NonPrintable] = "#93A1A1",
            [SchemeElement.Modified] = "#B4232A",
            [SchemeElement.Inserted] = "#4F6100",
            [SchemeElement.SelectionBackground] = "#268BD2",
            [SchemeElement.SelectionText] = "#FDF6E3",
            [SchemeElement.Caret] = "#073642",
            [SchemeElement.Match] = "#F3D88B",
            [SchemeElement.Bookmark] = "#D7E3B0",
            [SchemeElement.Separator] = "#93A1A1",
        });
        Fill(solarized, dark: true, new()
        {
            [SchemeElement.Background] = "#002B36",
            [SchemeElement.AlternateBackground] = "#04303B",
            [SchemeElement.CurrentRowBackground] = "#073642",
            [SchemeElement.OffsetText] = "#93A1A1",
            [SchemeElement.HexText] = "#EEE8D5",
            [SchemeElement.TextText] = "#EEE8D5",
            [SchemeElement.Zero] = "#657B83",
            [SchemeElement.NonPrintable] = "#657B83",
            [SchemeElement.Modified] = "#FF8A80",
            [SchemeElement.Inserted] = "#B5D334",
            [SchemeElement.SelectionBackground] = "#268BD2",
            [SchemeElement.SelectionText] = "#FDF6E3",
            [SchemeElement.Caret] = "#EEE8D5",
            [SchemeElement.Match] = "#5C4A00",
            [SchemeElement.Bookmark] = "#2E3F00",
            [SchemeElement.Separator] = "#586E75",
        });

        var contrast = new ColorScheme("contrast", builtIn: true);
        Fill(contrast, dark: false, new()
        {
            [SchemeElement.Background] = "#FFFFFF",
            [SchemeElement.AlternateBackground] = "#EDEDED",
            [SchemeElement.CurrentRowBackground] = "#E0E0E0",
            [SchemeElement.OffsetText] = "#000000",
            [SchemeElement.HexText] = "#000000",
            [SchemeElement.TextText] = "#000000",
            [SchemeElement.Zero] = "#595959",
            [SchemeElement.NonPrintable] = "#595959",
            [SchemeElement.Modified] = "#B00000",
            [SchemeElement.Inserted] = "#006100",
            [SchemeElement.SelectionBackground] = "#000080",
            [SchemeElement.SelectionText] = "#FFFFFF",
            [SchemeElement.Caret] = "#000000",
            [SchemeElement.Match] = "#FFE14D",
            [SchemeElement.Separator] = "#000000",
        });
        Fill(contrast, dark: true, new()
        {
            [SchemeElement.Background] = "#000000",
            [SchemeElement.AlternateBackground] = "#1A1A1A",
            [SchemeElement.CurrentRowBackground] = "#262626",
            [SchemeElement.OffsetText] = "#FFFFFF",
            [SchemeElement.HexText] = "#FFFFFF",
            [SchemeElement.TextText] = "#FFFFFF",
            [SchemeElement.Zero] = "#A6A6A6",
            [SchemeElement.NonPrintable] = "#A6A6A6",
            [SchemeElement.Modified] = "#FF7070",
            [SchemeElement.Inserted] = "#6CFF6C",
            [SchemeElement.SelectionBackground] = "#FFFF00",
            [SchemeElement.SelectionText] = "#000000",
            [SchemeElement.Caret] = "#FFFFFF",
            [SchemeElement.Match] = "#5A4600",
            [SchemeElement.Separator] = "#FFFFFF",
        });
        return [defaults, solarized, contrast];
    }

    private static void Fill(ColorScheme scheme, bool dark, Dictionary<SchemeElement, string> colors)
    {
        foreach ((SchemeElement e, string text) in colors)
        {
            SchemeColor.TryParse(text, out SchemeColor c);
            scheme.Set(e, dark, c);
        }
    }

    /// <summary>ファイル名に使えない文字を置き換える。</summary>
    public static string FileNameFor(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return sb + ".json";
    }
}

/// <summary>
/// 配色の保存先 (UI-28 の仕様 5): 設定フォルダの <c>themes/&lt;名前&gt;.json</c>。同梱の配色と独自の配色の一覧、保存、
/// エクスポート・インポート。
/// </summary>
public sealed class ColorSchemeStore(string settingsFolder)
{
    public string Folder => Path.Combine(settingsFolder, "themes");

    /// <summary>同梱の配色と、フォルダの独自の配色 (読めないファイルは除く)。</summary>
    public IReadOnlyList<ColorScheme> All()
    {
        var list = new List<ColorScheme>(ColorScheme.BuiltIns);
        if (!Directory.Exists(Folder))
        {
            return list;
        }

        foreach (string file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ColorScheme scheme = ColorScheme.Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
                if (!list.Any(s => s.Name.Equals(scheme.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(scheme);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    /// <summary>名前の配色。なければ「既定」。</summary>
    public ColorScheme Find(string? name) =>
        All().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? ColorScheme.BuiltIns[0];

    /// <summary>独自の配色を保存する (同梱の配色は保存しない)。保存したファイルのパス。</summary>
    public string Save(ColorScheme scheme)
    {
        if (scheme.BuiltIn)
        {
            throw new InvalidOperationException("Built-in color schemes cannot be saved.");
        }

        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, ColorScheme.FileNameFor(scheme.Name));
        string temp = path + ".tmp";
        File.WriteAllText(temp, scheme.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>エクスポート (仕様 5)。</summary>
    public static void Export(ColorScheme scheme, string path) => File.WriteAllText(path, scheme.ToJsonString(), new UTF8Encoding(false));

    /// <summary>インポートして保存する。読めない JSON は <see cref="JsonException"/>。</summary>
    public ColorScheme Import(string path)
    {
        ColorScheme parsed = ColorScheme.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        ColorScheme scheme = parsed.Duplicate(parsed.Name);
        Save(scheme);
        return parsed;
    }
}
