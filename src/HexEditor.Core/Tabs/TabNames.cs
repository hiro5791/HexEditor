namespace HexEditor.Core.Tabs;

/// <summary>
/// タブの表示名 (UI-09 の仕様 3)。表示名はファイル名で、同じファイル名のタブが複数あるときは、区別できる最小の親フォルダ名を
/// 付ける (例: <c>data.bin — a\x</c>、<c>data.bin — b\x</c>)。
/// </summary>
public static class TabNames
{
    /// <summary>ファイル名と親フォルダの区切り。</summary>
    public const string Separator = " — ";

    /// <summary>
    /// 各タブの表示名の後ろに付ける文字列 (付けなければ空)。<paramref name="tabs"/> は表示名と完全なパス (無題などは null) の組。
    /// </summary>
    public static string[] Suffixes(IReadOnlyList<(string Name, string? Path)> tabs)
    {
        string[] result = new string[tabs.Count];
        Array.Fill(result, string.Empty);
        var groups = Enumerable.Range(0, tabs.Count)
            .Where(i => tabs[i].Path is not null)
            .GroupBy(i => tabs[i].Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (IGrouping<string, int> group in groups)
        {
            int[] members = [.. group];
            string[][] folders = [.. members.Select(i => ParentFolders(tabs[i].Path!))];

            // 同じフォルダのものしかない (同じパスを別のタブで開いている) 場合は付けない。
            if (folders.Select(f => string.Join('\\', f)).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            {
                continue;
            }

            int depth = 1;
            int max = folders.Max(f => f.Length);
            while (depth < max && !Unique(folders, depth))
            {
                depth++;
            }

            for (int k = 0; k < members.Length; k++)
            {
                string[] f = folders[k];
                if (f.Length > 0)
                {
                    result[members[k]] = Separator + string.Join('\\', f.Skip(Math.Max(0, f.Length - depth)));
                }
            }
        }

        return result;
    }

    /// <summary>親フォルダの名前の並び (ルートから順。ドライブは <c>C:</c>)。</summary>
    private static string[] ParentFolders(string path)
    {
        string? folder = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
        {
            return [];
        }

        return folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Unique(string[][] folders, int depth) =>
        folders.Select(f => string.Join('\\', f.Skip(Math.Max(0, f.Length - depth))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == folders.Length;
}
