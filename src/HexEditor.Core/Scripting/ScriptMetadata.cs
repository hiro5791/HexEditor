using System.Globalization;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Scripting;

/// <summary>スクリプトの言語 (AUTO-01 の仕様 1)。</summary>
public enum ScriptLanguage
{
    JavaScript,
    CSharp,
}

/// <summary>パラメータの宣言 (<c>@param 名前 型 既定値 ラベル</c>。AUTO-26)。</summary>
/// <param name="Choices"><c>choice</c> の選択肢 (既定値の欄に <c>a|b|c</c> と書く。先頭が既定値)。</param>
public sealed record ScriptParameter(string Name, string Type, object? Default, string Label, IReadOnlyList<string>? Choices = null);

/// <summary>メタデータの書式の誤り (AUTO-26 の仕様 5。警告として出力パネルに出し、実行は続ける)。</summary>
public sealed record ScriptMetadataWarning(int Line, string Message);

/// <summary>
/// スクリプトのメタデータ (AUTO-26)。ファイル先頭の <c>// ==HexEditorScript==</c> 〜 <c>// ==/HexEditorScript==</c> のコメントで宣言する。
/// メタデータがないスクリプトは、名前がファイル名、許可なし、パラメータなし (仕様 4)。
/// </summary>
public sealed class ScriptMetadata
{
    public const string StartMarker = "==HexEditorScript==";
    public const string EndMarker = "==/HexEditorScript==";

    /// <summary>パラメータの型 (<c>ui.prompt</c> / <c>ui.form</c> と同じ + <c>checkbox</c>)。</summary>
    public static readonly IReadOnlySet<string> ParameterTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "text", "number", "offset", "range", "hex", "choice", "checkbox",
    };

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>メタデータの区切りがあったか。</summary>
    public bool HasHeader { get; private set; }

    /// <summary>言語タグのない <c>@name</c>。</summary>
    public string? Name { get; private set; }

    public string? Description { get; private set; }

    public string? Version { get; private set; }

    public string? Author { get; private set; }

    /// <summary><c>@api</c> (必要な API のバージョン。AUTO-04 の仕様 8)。</summary>
    public string? Api { get; private set; }

    public List<ScriptParameter> Parameters { get; } = [];

    /// <summary><c>@permission</c> (AUTO-34 の仕様 3)。</summary>
    public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);

    /// <summary><c>@timeout</c> (秒)。</summary>
    public double? TimeoutSeconds { get; private set; }

    /// <summary><c>@memory</c> (MB)。</summary>
    public long? MemoryMegabytes { get; private set; }

    /// <summary><c>@on</c> (自動実行の候補。AUTO-25)。</summary>
    public List<string> On { get; } = [];

    /// <summary><c>@menu</c> (「ツール > スクリプト」の下の位置。<c>/</c> 区切り)。</summary>
    public string? Menu { get; private set; }

    /// <summary><c>@context</c> (<c>selection</c> / <c>always</c>)。</summary>
    public string? Context { get; private set; }

    public List<ScriptMetadataWarning> Warnings { get; } = [];

    /// <summary>表示言語に合う <c>@name:&lt;言語タグ&gt;</c>、なければ <c>@name</c>、なければ <paramref name="fallback"/> (仕様 2)。</summary>
    public string DisplayName(string language, string fallback) => Localized(_names, language) ?? Name ?? fallback;

    /// <summary>表示言語に合う説明。</summary>
    public string? DisplayDescription(string language) => Localized(_descriptions, language) ?? Description;

    private static string? Localized(Dictionary<string, string> values, string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return null;
        }

        if (values.TryGetValue(language, out string? exact))
        {
            return exact;
        }

        // ja-JP → ja のように地域を外して探す。
        int dash = language.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && values.TryGetValue(language[..dash], out string? neutral) ? neutral : null;
    }

    /// <summary>メタデータを読む。書式の誤りは <see cref="Warnings"/> に入れ、読める項目は使う。</summary>
    public static ScriptMetadata Parse(string source)
    {
        var meta = new ScriptMetadata();
        string[] lines = source.Split('\n');
        int i = 0;

        // 先頭の空行・コメント・シバンを飛ばして開始の印を探す。コードが先に出たらメタデータなし。
        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("#!", StringComparison.Ordinal))
            {
                continue;
            }

            if (!t.StartsWith("//", StringComparison.Ordinal))
            {
                return meta;
            }

            if (t[2..].Trim() == StartMarker)
            {
                meta.HasHeader = true;
                i++;
                break;
            }
        }

        if (!meta.HasHeader)
        {
            return meta;
        }

        bool closed = false;
        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith("//", StringComparison.Ordinal))
            {
                if (t.Length == 0)
                {
                    continue;
                }

                break;
            }

            string body = t[2..].Trim();
            if (body == EndMarker)
            {
                closed = true;
                break;
            }

            if (body.Length == 0)
            {
                continue;
            }

            if (body[0] != '@')
            {
                meta.Warnings.Add(new(i + 1, Msg.T($"メタデータの行が @ で始まっていません: {body}", $"A metadata line does not start with @: {body}")));
                continue;
            }

            int space = body.IndexOfAny([' ', '\t']);
            string key = space < 0 ? body[1..] : body[1..space];
            string value = space < 0 ? "" : body[(space + 1)..].Trim();
            meta.ParseItem(key, value, i + 1);
        }

        if (!closed)
        {
            meta.Warnings.Add(new(i, Msg.T($"メタデータの終わり ({EndMarker}) がありません。", $"The end of the metadata ({EndMarker}) is missing.")));
        }

        return meta;
    }

    private void ParseItem(string key, string value, int line)
    {
        string baseKey = key;
        string? lang = null;
        int colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            baseKey = key[..colon];
            lang = key[(colon + 1)..];
        }

        if (lang is not null && baseKey is not ("name" or "description"))
        {
            Warnings.Add(new(line, Msg.T($"言語タグを付けられない項目です: @{key}", $"A language tag cannot be added to this item: @{key}")));
            return;
        }

        switch (baseKey)
        {
            case "name":
                if (lang is null)
                {
                    Name = value;
                }
                else
                {
                    _names[lang] = value;
                }

                break;
            case "description":
                if (lang is null)
                {
                    Description = value;
                }
                else
                {
                    _descriptions[lang] = value;
                }

                break;
            case "version":
                Version = value;
                break;
            case "author":
                Author = value;
                break;
            case "api":
                if (ApiVersion.TryParse(value, out _))
                {
                    Api = value;
                }
                else
                {
                    Warnings.Add(new(line, Msg.T($"@api のバージョンを読めません: {value}", $"Cannot parse the @api version: {value}")));
                }

                break;
            case "param":
                ParseParameter(value, line);
                break;
            case "permission":
                foreach (string p in value.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ScriptPermissions.IsKnown(p))
                    {
                        Permissions.Add(p);
                    }
                    else
                    {
                        Warnings.Add(new(line, Msg.T($"不明な許可です: {p}", $"Unknown permission: {p}")));
                    }
                }

                break;
            case "timeout":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
                {
                    TimeoutSeconds = seconds;
                }
                else
                {
                    Warnings.Add(new(line, Msg.T($"@timeout は秒数で書いてください: {value}", $"Write @timeout as a number of seconds: {value}")));
                }

                break;
            case "memory":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mb) && mb > 0)
                {
                    MemoryMegabytes = mb;
                }
                else
                {
                    Warnings.Add(new(line, Msg.T($"@memory は MB の数で書いてください: {value}", $"Write @memory as a number of MB: {value}")));
                }

                break;
            case "on":
                On.Add(value);
                break;
            case "menu":
                Menu = value;
                break;
            case "context":
                if (value is "selection" or "always")
                {
                    Context = value;
                }
                else
                {
                    Warnings.Add(new(line, Msg.T($"@context は selection か always です: {value}", $"@context must be selection or always: {value}")));
                }

                break;
            default:
                Warnings.Add(new(line, Msg.T($"不明な項目です: @{key}", $"Unknown item: @{key}")));
                break;
        }
    }

    private static readonly Regex Token = new("\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+)", RegexOptions.CultureInvariant);

    private void ParseParameter(string value, int line)
    {
        // 名前 型 既定値 ラベル。既定値は "..." で囲める。ラベルは残りすべて。
        var tokens = new List<(string Text, int End, bool Quoted)>();
        foreach (Match m in Token.Matches(value))
        {
            tokens.Add(m.Groups[1].Success ? (Regex.Unescape(m.Groups[1].Value), m.Index + m.Length, true) : (m.Groups[2].Value, m.Index + m.Length, false));
            if (tokens.Count == 3)
            {
                break;
            }
        }

        if (tokens.Count < 2)
        {
            Warnings.Add(new(line, Msg.T($"@param は「名前 型 既定値 ラベル」の形で書いてください: {value}", $"Write @param as \"name type default label\": {value}")));
            return;
        }

        string name = tokens[0].Text;
        string type = tokens[1].Text;
        if (!ParameterTypes.Contains(type))
        {
            Warnings.Add(new(line, Msg.T($"@param の型が不明です: {type}", $"Unknown @param type: {type}")));
            return;
        }

        string? defaultText = tokens.Count > 2 ? tokens[2].Text : null;
        string label = tokens.Count > 2 ? value[tokens[2].End..].Trim() : "";
        if (label.Length == 0)
        {
            label = name;
        }

        object? def = defaultText;
        IReadOnlyList<string>? choices = null;
        switch (type)
        {
            case "checkbox":
                if (defaultText is null)
                {
                    def = false;
                }
                else if (bool.TryParse(defaultText, out bool b))
                {
                    def = b;
                }
                else
                {
                    Warnings.Add(new(line, Msg.T($"checkbox の既定値は true か false です: {defaultText}", $"The default of a checkbox must be true or false: {defaultText}")));
                    def = false;
                }

                break;
            case "choice":
                choices = (defaultText ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (choices.Count == 0)
                {
                    Warnings.Add(new(line, Msg.T($"choice の選択肢を既定値の欄に a|b|c の形で書いてください: {name}", $"Write the choices of a choice parameter in the default column as a|b|c: {name}")));
                }

                def = choices.Count > 0 ? choices[0] : null;
                break;
        }

        if (Parameters.Any(p => p.Name == name))
        {
            Warnings.Add(new(line, Msg.T($"@param の名前が重複しています: {name}", $"Duplicate @param name: {name}")));
            return;
        }

        Parameters.Add(new ScriptParameter(name, type, def, label, choices));
    }
}

/// <summary>API のバージョン (<c>メジャー.マイナー</c>。AUTO-04 の仕様 7・8)。</summary>
public readonly record struct ApiVersion(int Major, int Minor) : IComparable<ApiVersion>
{
    public static ApiVersion Current { get; } = Parse(Sdk.ApiInfo.Version);

    public static bool TryParse(string? text, out ApiVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        int minor = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        version = new ApiVersion(major, minor);
        return true;
    }

    public static ApiVersion Parse(string text) => TryParse(text, out ApiVersion v) ? v : throw new FormatException(text);

    /// <summary>アプリの API がスクリプトの要求 (<paramref name="required"/>) を満たすか (同じメジャーで、マイナーが同じか大きい)。</summary>
    public bool Satisfies(ApiVersion required) => Major == required.Major && Minor >= required.Minor;

    public int CompareTo(ApiVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}
