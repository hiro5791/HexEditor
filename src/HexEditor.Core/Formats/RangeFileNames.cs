using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Formats;

/// <summary>
/// マルチ選択を「範囲ごとに別ファイル」で書き出すときのファイル名 (TOOL-04 の仕様 1、TOOL-16 の仕様 1)。記号は TOOL-13 と同じ
/// (<c>{name}</c> 元のファイル名 (拡張子を含む)、<c>{base}</c> 拡張子なし、<c>{ext}</c> 拡張子 (点を含む)、<c>{index}</c> 1 から始まる番号、
/// <c>{offset}</c> 開始オフセットの 16 進) に、<c>{start}</c> (開始オフセットの 16 進。<c>{offset}</c> と同じ) と <c>{length}</c> (長さの 16 進) を加えたもの。
/// <c>{index:000}</c> のように書式を付けられる (数値の書式)。
/// </summary>
public static partial class RangeFileNames
{
    /// <summary>既定の形式。</summary>
    public const string DefaultPattern = "{base}_{start}{ext}";

    [GeneratedRegex(@"\{(name|base|ext|index|offset|start|length)(?::([^}]*))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Symbol();

    /// <summary>
    /// 1 つの範囲のファイル名 (フォルダを含まない)。<paramref name="fileName"/> は保存先として選んだファイル名 (拡張子を含む)。
    /// ファイル名に使えない文字は <c>_</c> に置き換える。
    /// </summary>
    public static string Expand(string pattern, string fileName, int index, long start, long length)
    {
        string ext = Path.GetExtension(fileName);
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string result = Symbol().Replace(pattern, m =>
        {
            string format = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            return m.Groups[1].Value.ToLowerInvariant() switch
            {
                "name" => fileName,
                "base" => baseName,
                "ext" => ext,
                "index" => index.ToString(format.Length > 0 ? format : "D", CultureInfo.InvariantCulture),
                "length" => length.ToString(format.Length > 0 ? format : "X", CultureInfo.InvariantCulture),
                _ => start.ToString(format.Length > 0 ? format : "X", CultureInfo.InvariantCulture),
            };
        });
        var sb = new StringBuilder(result.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in result)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    /// <summary>すべての範囲のファイル名 (同じ名前になる範囲があれば null。形式に番号か位置を入れる必要がある)。</summary>
    public static IReadOnlyList<string>? ExpandAll(string pattern, string fileName, IReadOnlyList<(long Offset, long Length)> ranges)
    {
        var names = new List<string>(ranges.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ranges.Count; i++)
        {
            string name = Expand(pattern, fileName, i + 1, ranges[i].Offset, ranges[i].Length);
            if (name.Length == 0 || !seen.Add(name))
            {
                return null;
            }

            names.Add(name);
        }

        return names;
    }
}
