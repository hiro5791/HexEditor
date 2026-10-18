using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Tools.Files;

/// <summary>
/// 分割・デインターリーブの出力のファイル名の形式 (TOOL-13 の仕様 3、TOOL-15 の仕様 1)。記号:
/// <c>{name}</c> 元のファイル名 (拡張子を含む)、<c>{base}</c> 拡張子なし、<c>{ext}</c> 拡張子 (点を含む)、<c>{index}</c> 1 から始まる番号、
/// <c>{index0}</c> 0 から始まる番号、<c>{offset}</c> 開始オフセットの 16 進。<c>{index:000}</c> のように数値の書式を付けられる。
/// </summary>
public static partial class FileNameTemplate
{
    public const string SplitDefault = "{name}.{index:000}";

    public const string DeinterleaveDefault = "{base}.{index}{ext}";

    [GeneratedRegex(@"\{(name|base|ext|index0|index|offset)(?::([^}]*))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Symbol();

    public static string Expand(string pattern, string sourceName, int index, long offset)
    {
        string ext = Path.GetExtension(sourceName);
        string baseName = Path.GetFileNameWithoutExtension(sourceName);
        string result = Symbol().Replace(pattern, m =>
        {
            string format = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            try
            {
                return m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "name" => sourceName,
                    "base" => baseName,
                    "ext" => ext,
                    "index" => index.ToString(format.Length > 0 ? format : "D", CultureInfo.InvariantCulture),
                    "index0" => (index - 1).ToString(format.Length > 0 ? format : "D", CultureInfo.InvariantCulture),
                    _ => offset.ToString(format.Length > 0 ? format : "X", CultureInfo.InvariantCulture),
                };
            }
            catch (FormatException)
            {
                return m.Value;
            }
        });
        var sb = new StringBuilder(result.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in result)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    /// <summary>すべての出力の名前 (空、または同じ名前が重なれば null)。</summary>
    public static IReadOnlyList<string>? ExpandAll(string pattern, string sourceName, IReadOnlyList<long> offsets)
    {
        var names = new List<string>(offsets.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < offsets.Count; i++)
        {
            string name = Expand(pattern, sourceName, i + 1, offsets[i]);
            if (name.Length == 0 || name is "." or ".." || !seen.Add(name))
            {
                return null;
            }

            names.Add(name);
        }

        return names;
    }
}
