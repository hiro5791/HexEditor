using System.Text;
using System.Xml.Linq;

namespace HexEditor.I18nTool;

/// <summary>23 言語 (00-overview 5.1) とその言語自身の表記 (リリースノートの表に使う。Platform の DisplayLanguages と同じ)。</summary>
public static class Languages
{
    public static IReadOnlyList<(string Tag, string NativeName)> All { get; } =
    [
        ("en", "English"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"), ("ko", "한국어"),
        ("id", "Bahasa Indonesia"), ("vi", "Tiếng Việt"), ("th", "ไทย"), ("de", "Deutsch"), ("fr", "Français"),
        ("es", "Español"), ("pt", "Português"), ("it", "Italiano"), ("ru", "Русский"), ("uk", "Українська"),
        ("pl", "Polski"), ("cs", "Čeština"), ("hu", "Magyar"), ("ro", "Română"), ("el", "Ελληνικά"),
        ("ar", "العربية"), ("tr", "Türkçe"), ("fa", "فارسی"),
    ];

    /// <summary>機械翻訳する 21 言語 (英語と日本語は開発者が書く。00-overview 5.3)。</summary>
    public static IReadOnlyList<string> MachineTranslated { get; } = [.. All.Select(l => l.Tag).Where(t => t is not ("en" or "ja"))];
}

/// <summary>
/// .resw (1 行に 1 つの data) の読み書き。書くときは英語のキーの順に並べ、ヘッダーは英語のファイルと同じにする。
/// </summary>
public static class Resw
{
    public static string PathFor(string stringsFolder, string language) => Path.Combine(stringsFolder, language, "Resources.resw");

    /// <summary>キーと値 (ファイルの順)。ファイルがなければ空。</summary>
    public static List<KeyValuePair<string, string>> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. XDocument.Load(path).Root!.Elements("data")
            .Select(d => new KeyValuePair<string, string>(d.Attribute("name")!.Value, d.Element("value")?.Value ?? string.Empty))];
    }

    public static Dictionary<string, string> LoadMap(string path) =>
        Load(path).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="keyOrder"/> の順に <paramref name="values"/> を書く。値のないキーは書かない (英語で表示される)。
    /// ヘッダー (resheader) は <paramref name="headerFrom"/> のファイルから写す。
    /// </summary>
    public static void Save(string path, IEnumerable<string> keyOrder, IReadOnlyDictionary<string, string> values, string headerFrom)
    {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n");
        foreach (string line in File.ReadAllLines(headerFrom).Where(l => l.TrimStart().StartsWith("<resheader", StringComparison.Ordinal)))
        {
            text.Append(line.TrimEnd()).Append('\n');
        }

        foreach (string key in keyOrder)
        {
            if (values.TryGetValue(key, out string? value))
            {
                text.Append("  <data name=\"").Append(Escape(key, attribute: true)).Append("\" xml:space=\"preserve\"><value>")
                    .Append(Escape(value, attribute: false)).Append("</value></data>\n");
            }
        }

        text.Append("</root>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>XML の特別な文字だけを置き換える (既存のファイルと同じ書き方。' はそのまま)。</summary>
    private static string Escape(string s, bool attribute)
    {
        string e = s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        return attribute ? e.Replace("\"", "&quot;", StringComparison.Ordinal) : e;
    }
}

/// <summary>CSV (RFC 4180。UTF-8、BOM なし) の読み込み。</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add([.. row]);
                }

                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }
}
