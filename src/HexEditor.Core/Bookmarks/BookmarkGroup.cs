namespace HexEditor.Core.Bookmarks;

/// <summary>
/// ブックマークのグループ (INSP-27)。パス (<c>Body/Tables</c> のように名前を <c>/</c> で区切ったもの) で区別し、入れ子は 8 階層まで。
/// 色 (省略可) と表示 / 非表示の状態を持つ。変更は <see cref="BookmarkCollection"/> の操作で行う。
/// </summary>
public sealed class BookmarkGroup
{
    internal BookmarkGroup(string path)
    {
        Path = path;
    }

    /// <summary>パス (区切りは <c>/</c>)。</summary>
    public string Path { get; internal set; }

    /// <summary>グループの名前 (パスの最後の部分)。</summary>
    public string Name => BookmarkGroups.LastSegment(Path);

    /// <summary>親のグループのパス。最上位なら null。</summary>
    public string? ParentPath => BookmarkGroups.Parent(Path);

    /// <summary>階層 (最上位が 1)。</summary>
    public int Depth => BookmarkGroups.DepthOf(Path);

    /// <summary>グループの色 (INSP-27 の仕様 2)。設定していなければ null。</summary>
    public BookmarkColor? Color { get; internal set; }

    /// <summary>表示するか (INSP-27 の仕様 3)。親が非表示なら、この値によらず非表示。</summary>
    public bool Visible { get; internal set; } = true;

    public override string ToString() => Path;
}

/// <summary>グループの入れ子の上限を超える (INSP-27 の「エラー」)。</summary>
public sealed class BookmarkGroupDepthException() : InvalidOperationException("グループは 8 階層までです。");

/// <summary>グループの記録 (保存・削除の取り消し)。</summary>
public readonly record struct BookmarkGroupRecord(string Path, BookmarkColor? Color, bool Visible);

/// <summary>
/// グループの削除の取り消しに使う記録 (INSP-27 の仕様 4)。消したグループ、移したブックマークの元のグループ、消したブックマークの位置。
/// </summary>
public sealed class BookmarkGroupDeletion
{
    internal List<BookmarkGroupRecord> Groups { get; } = [];

    internal List<(Bookmark Bookmark, string? Group)> Moved { get; } = [];

    internal List<(Bookmark Bookmark, BookmarkPosition Position)> Removed { get; } = [];

    /// <summary>消したグループのパス (通知の文に使う)。</summary>
    public required string Path { get; init; }

    public int RemovedCount => Removed.Count;
}

/// <summary>グループのパスの操作。</summary>
public static class BookmarkGroups
{
    /// <summary>入れ子の上限 (INSP-27 の仕様 1)。</summary>
    public const int MaxDepth = 8;

    public const char Separator = '/';

    /// <summary>パスを整える: 各部分の前後の空白を除き、空の部分を除く。空なら null。</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] parts = [.. path.Split(Separator).Select(p => p.Trim()).Where(p => p.Length > 0)];
        return parts.Length == 0 ? null : string.Join(Separator, parts);
    }

    public static int DepthOf(string path) => path.Count(c => c == Separator) + 1;

    public static string? Parent(string path)
    {
        int i = path.LastIndexOf(Separator);
        return i < 0 ? null : path[..i];
    }

    public static string LastSegment(string path)
    {
        int i = path.LastIndexOf(Separator);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>親のパスと名前をつなぐ (名前の中の <c>/</c> は区切りにしない文字に置き換える)。</summary>
    public static string Combine(string? parent, string name)
    {
        string clean = name.Replace(Separator, '∕').Trim();
        return parent is null ? clean : parent + Separator + clean;
    }

    /// <summary><paramref name="path"/> が <paramref name="ancestor"/> 自身かその下か。</summary>
    public static bool IsWithin(string? path, string ancestor) =>
        path is not null && (path == ancestor || path.Length > ancestor.Length && path.StartsWith(ancestor, StringComparison.Ordinal) && path[ancestor.Length] == Separator);

    /// <summary>自身と祖先のパス (近い順)。</summary>
    public static IEnumerable<string> SelfAndAncestors(string path)
    {
        for (string? p = path; p is not null; p = Parent(p))
        {
            yield return p;
        }
    }
}
