using Microsoft.Win32;

namespace HexEditor.Core.Printing;

/// <summary>読み込んだ書体: フォントファイルの内容と、解析した TrueType。</summary>
public sealed class FontFace(string family, string path, int faceIndex, byte[] data, TrueTypeFont font)
{
    public string Family { get; } = family;

    public string Path { get; } = path;

    public int FaceIndex { get; } = faceIndex;

    public byte[] Data { get; } = data;

    public TrueTypeFont Font { get; } = font;
}

/// <summary>
/// インストールされているフォントの検索 (印刷・PDF の書き出し用。TOOL-01 の仕様 2、TOOL-03 の仕様 2)。レジストリのフォント一覧
/// (読み取りだけ) からファイルを探し、必要になった書体だけを読み込む。TrueType アウトラインでないフォントは使えない。
/// </summary>
public sealed class SystemFontCatalog
{
    /// <summary>既定の等幅フォント (TOOL-01 の仕様 2)。</summary>
    public const string DefaultFamily = "Cascadia Mono";

    /// <summary>等幅フォントが見つからないときに順に試す書体。</summary>
    private static readonly string[] MonospaceFallbacks = ["Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"];

    /// <summary>文字が主フォントにないときに順に試す代替フォント (日本語・中国語・韓国語・記号)。</summary>
    private static readonly string[] SubstituteFamilies =
    [
        "Yu Gothic", "Meiryo", "MS Gothic", "BIZ UDGothic", "Microsoft YaHei", "Microsoft JhengHei", "Malgun Gothic", "SimSun", "MingLiU",
        "Segoe UI Symbol", "Segoe UI", "Arial Unicode MS", "Arial", "DejaVu Sans",
    ];

    private static readonly string[] StyleWords =
    [
        "Bold", "Italic", "Oblique", "Light", "Semilight", "Semibold", "Black", "Thin", "ExtraLight", "ExtraBold", "Medium", "Heavy", "Condensed",
        "Demi", "Narrow", "Ultra", "Book", "SemiLight", "SemiBold", "Italic",
    ];

    private readonly object _lock = new();
    private readonly Dictionary<string, List<(string Path, int Face)>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFace?> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _fileData = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexed;

    public static SystemFontCatalog Instance { get; } = new();

    /// <summary>テスト用: 指定したフォントファイルを索引に加える。</summary>
    public void AddFile(string family, string path, int face = 0)
    {
        lock (_lock)
        {
            EnsureIndexed();
            if (!_index.TryGetValue(family, out var list))
            {
                _index[family] = list = [];
            }

            list.Insert(0, (path, face));
            _loaded.Remove(family);
        }
    }

    /// <summary>インストールされている書体名 (標準の太さと斜体でないもの)。</summary>
    public IReadOnlyList<string> Families
    {
        get
        {
            lock (_lock)
            {
                EnsureIndexed();
                return [.. _index.Keys.Order(StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    /// <summary>書体を探して読み込む。なければ null。</summary>
    public FontFace? Find(string family)
    {
        lock (_lock)
        {
            EnsureIndexed();
            if (_loaded.TryGetValue(family, out FontFace? cached))
            {
                return cached;
            }

            FontFace? face = null;
            if (_index.TryGetValue(family, out var candidates))
            {
                foreach ((string path, int index) in candidates)
                {
                    face = Open(family, path, index);
                    if (face is not null)
                    {
                        break;
                    }
                }
            }

            _loaded[family] = face;
            return face;
        }
    }

    /// <summary>指定の等幅フォント。なければ既定の等幅フォント、それもなければ最初に見つかったフォント。</summary>
    public FontFace? Primary(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family) && Find(family) is { } found)
        {
            return found;
        }

        foreach (string name in MonospaceFallbacks)
        {
            if (Find(name) is { } face)
            {
                return face;
            }
        }

        return null;
    }

    /// <summary>符号位置の文字を持つ代替フォント。なければ null。</summary>
    public FontFace? Substitute(int codePoint)
    {
        foreach (string name in SubstituteFamilies)
        {
            if (Find(name) is { } face && face.Font.GlyphIndex(codePoint) != 0)
            {
                return face;
            }
        }

        return null;
    }

    private FontFace? Open(string family, string path, int faceIndex)
    {
        try
        {
            if (!_fileData.TryGetValue(path, out byte[]? data))
            {
                data = File.ReadAllBytes(path);
                _fileData[path] = data;
            }

            TrueTypeFont? font = TrueTypeFont.Load(data, faceIndex);
            if (font is not null && NameMatches(font, family))
            {
                return new FontFace(family, path, faceIndex, data, font);
            }

            // ttc の書体の並びが一覧と違うときは、名前が合う書体を探す。
            for (int i = 0; i < TrueTypeFont.FaceCount(data); i++)
            {
                TrueTypeFont? other = TrueTypeFont.Load(data, i);
                if (other is not null && NameMatches(other, family) && other.IsRegular)
                {
                    return new FontFace(family, path, i, data, other);
                }
            }

            return font is not null && TrueTypeFont.FaceCount(data) == 1 ? new FontFace(family, path, faceIndex, data, font) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool NameMatches(TrueTypeFont font, string family) =>
        string.Equals(font.FamilyName, family, StringComparison.OrdinalIgnoreCase)
        || string.Equals(font.AlternativeFamily, family, StringComparison.OrdinalIgnoreCase);

    private void EnsureIndexed()
    {
        if (_indexed)
        {
            return;
        }

        _indexed = true;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        try
        {
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            Add(machine, folder);
            using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            Add(user, folder);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 読めなければ索引は空のまま。
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void Add(RegistryKey? key, string folder)
    {
        if (key is null)
        {
            return;
        }

        foreach (string valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string file || !(file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string path = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
            string names = valueName;
            int paren = names.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                names = names[..paren];
            }

            string[] parts = names.Split(" & ", StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string? family = RegularFamily(parts[i]);
                if (family is null)
                {
                    continue;
                }

                if (!_index.TryGetValue(family, out var list))
                {
                    _index[family] = list = [];
                }

                list.Add((path, parts.Length > 1 ? i : 0));
            }
        }
    }

    /// <summary>「Consolas」「Cascadia Mono Regular」の形の名前から書体名を取り出す。太字・斜体などの名前は null。</summary>
    internal static string? RegularFamily(string name)
    {
        string n = name.Trim();
        if (n.EndsWith(" Regular", StringComparison.OrdinalIgnoreCase))
        {
            return n[..^8].Trim();
        }

        foreach (string word in StyleWords)
        {
            if (n.EndsWith(" " + word, StringComparison.OrdinalIgnoreCase) || n.Contains(" " + word + " ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return n;
    }
}
