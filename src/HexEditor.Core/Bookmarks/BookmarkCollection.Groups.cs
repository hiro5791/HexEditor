namespace HexEditor.Core.Bookmarks;

/// <summary>
/// ブックマークのグループ (INSP-27)。グループはパスで区別し、ブックマークは <see cref="Bookmark.Group"/> にパスを持つ。
/// グループの変更もデータの Undo 履歴には入れない (INSP-23 の仕様 5 と同じ)。
/// </summary>
public sealed partial class BookmarkCollection
{
    private readonly Dictionary<string, BookmarkGroup> _groups = new(StringComparer.Ordinal);
    private int _hiddenGroups;

    /// <summary>すべてのグループ (順序は決まっていない。表示はパスで並べる)。</summary>
    public IReadOnlyCollection<BookmarkGroup> Groups => _groups.Values;

    public BookmarkGroup? FindGroup(string? path) => BookmarkGroups.Normalize(path) is { } p && _groups.TryGetValue(p, out BookmarkGroup? g) ? g : null;

    /// <summary>グループがなければ作る (祖先も作る)。9 階層目なら <see cref="BookmarkGroupDepthException"/>。</summary>
    public BookmarkGroup EnsureGroup(string path)
    {
        bool created = !_groups.ContainsKey(path);
        BookmarkGroup group = EnsureGroupQuiet(path);
        if (created)
        {
            RaiseChanged(BookmarkChangeKind.Groups, []);
        }

        return group;
    }

    private BookmarkGroup EnsureGroupQuiet(string path)
    {
        if (_groups.TryGetValue(path, out BookmarkGroup? existing))
        {
            return existing;
        }

        if (BookmarkGroups.DepthOf(path) > BookmarkGroups.MaxDepth)
        {
            throw new BookmarkGroupDepthException();
        }

        if (BookmarkGroups.Parent(path) is { } parent)
        {
            EnsureGroupQuiet(parent);
        }

        var group = new BookmarkGroup(path);
        _groups[path] = group;
        return group;
    }

    /// <summary>
    /// 「グループの作成」(INSP-27): <paramref name="parent"/> の下に名前 <paramref name="name"/> のグループを作る。同じ名前があれば
    /// 「名前 (2)」のように番号を付ける。9 階層目なら <see cref="BookmarkGroupDepthException"/>。
    /// </summary>
    public BookmarkGroup CreateGroup(string? parent, string name)
    {
        parent = BookmarkGroups.Normalize(parent);
        if (parent is not null && BookmarkGroups.DepthOf(parent) >= BookmarkGroups.MaxDepth)
        {
            throw new BookmarkGroupDepthException();
        }

        string trimmed = name.Trim().Length == 0 ? "Group" : name.Trim();
        string path = BookmarkGroups.Combine(parent, trimmed);
        for (int n = 2; _groups.ContainsKey(path); n++)
        {
            path = BookmarkGroups.Combine(parent, $"{trimmed} ({n})");
        }

        return EnsureGroup(path);
    }

    /// <summary>グループの名前を変える (中のグループとブックマークのパスも変える)。同じパスのグループがあれば変えずに false。</summary>
    public bool RenameGroup(BookmarkGroup group, string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || group.Name == trimmed)
        {
            return false;
        }

        string oldPath = group.Path;
        string newPath = BookmarkGroups.Combine(group.ParentPath, trimmed);
        if (_groups.ContainsKey(newPath))
        {
            return false;
        }

        foreach (BookmarkGroup g in _groups.Values.Where(g => BookmarkGroups.IsWithin(g.Path, oldPath)).ToList())
        {
            _groups.Remove(g.Path);
            g.Path = newPath + g.Path[oldPath.Length..];
            _groups[g.Path] = g;
        }

        foreach (Bookmark b in All.Where(b => BookmarkGroups.IsWithin(b.Group, oldPath)))
        {
            b.Group = newPath + b.Group![oldPath.Length..];
        }

        RaiseChanged(BookmarkChangeKind.Groups, []);
        return true;
    }

    /// <summary>グループの色を変える (null は色なし。INSP-27 の仕様 2)。</summary>
    public void SetGroupColor(BookmarkGroup group, BookmarkColor? color)
    {
        if (group.Color == color)
        {
            return;
        }

        group.Color = color;
        RaiseChanged(BookmarkChangeKind.Groups, []);
    }

    /// <summary>グループの表示 / 非表示を変える (INSP-27 の仕様 3)。100 万件でも件数によらない時間で終わる。</summary>
    public void SetGroupVisible(BookmarkGroup group, bool visible)
    {
        if (group.Visible == visible)
        {
            return;
        }

        group.Visible = visible;
        _hiddenGroups += visible ? -1 : 1;
        RaiseVisibilityChanged();
    }

    /// <summary>グループが表示されるか (自身と祖先がすべて表示)。グループなし (null) は常に表示。</summary>
    public bool IsGroupVisible(string? path)
    {
        if (path is null || _hiddenGroups == 0)
        {
            return true;
        }

        foreach (string p in BookmarkGroups.SelfAndAncestors(path))
        {
            if (_groups.TryGetValue(p, out BookmarkGroup? g) && !g.Visible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ブックマークを表示するか (非表示のグループのものは Hex ビューに出さず、F2 で飛ばす。INSP-27 の仕様 3)。</summary>
    public bool IsVisible(Bookmark b) => IsGroupVisible(b.Group);

    /// <summary>
    /// 表示する色: 色を個別に設定していなければ、最も近い祖先のグループの色 (INSP-27 の仕様 2)。どのグループにも色がなければ自身の色。
    /// </summary>
    public BookmarkColor EffectiveColor(Bookmark b)
    {
        if (b.ColorSet || b.Group is null || _groups.Count == 0)
        {
            return b.Color;
        }

        foreach (string p in BookmarkGroups.SelfAndAncestors(b.Group))
        {
            if (_groups.TryGetValue(p, out BookmarkGroup? g) && g.Color is { } color)
            {
                return color;
            }
        }

        return b.Color;
    }

    /// <summary>「グループに移動」(INSP-27 の仕様 5): まとめてグループを付け替える (null はグループなし)。</summary>
    public void MoveToGroup(IReadOnlyCollection<Bookmark> items, string? group)
    {
        group = BookmarkGroups.Normalize(group);
        if (group is not null)
        {
            EnsureGroupQuiet(group);
        }

        var moved = new List<Bookmark>();
        foreach (Bookmark b in items)
        {
            if (b.Owner == this && b.Group != group)
            {
                b.Group = group;
                b.EditedByUser = true;
                b.Updated = _time.GetUtcNow().UtcDateTime;
                moved.Add(b);
            }
        }

        RaiseChanged(BookmarkChangeKind.Groups, moved);
    }

    /// <summary>グループ (と、その下のグループ) に入っているブックマーク。</summary>
    public IReadOnlyList<Bookmark> InGroup(string path) => [.. All.Where(b => BookmarkGroups.IsWithin(b.Group, path))];

    /// <summary>
    /// グループを削除する (INSP-27 の仕様 4)。<paramref name="deleteContents"/> なら中のブックマークと下のグループも消す。そうでなければ
    /// 下のグループとブックマークを親のグループに移す。取り消しの記録を返す (<see cref="UndoGroupDeletion"/>)。
    /// </summary>
    public BookmarkGroupDeletion DeleteGroup(BookmarkGroup group, bool deleteContents)
    {
        string path = group.Path;
        string? parent = group.ParentPath;
        var record = new BookmarkGroupDeletion { Path = path };
        List<BookmarkGroup> affected = [.. _groups.Values.Where(g => BookmarkGroups.IsWithin(g.Path, path)).OrderBy(g => g.Path.Length)];
        foreach (BookmarkGroup g in affected)
        {
            record.Groups.Add(new BookmarkGroupRecord(g.Path, g.Color, g.Visible));
            _groups.Remove(g.Path);
            if (!g.Visible)
            {
                _hiddenGroups--;
            }
        }

        List<Bookmark> inside = [.. All.Where(b => BookmarkGroups.IsWithin(b.Group, path))];
        if (deleteContents)
        {
            foreach (Bookmark b in inside)
            {
                record.Removed.Add((b, new BookmarkPosition(StartOf(b), b.Length, b.RangeDeleted)));
            }

            RemoveRange(inside, remember: false);
        }
        else
        {
            // 下のグループは親の下に付け替え (同じパスのグループがあればまとめる)、直下のブックマークは親に移す。
            foreach (BookmarkGroupRecord r in record.Groups.Where(r => r.Path != path))
            {
                string rest = r.Path[(path.Length + 1)..];
                string moved = parent is null ? rest : parent + BookmarkGroups.Separator + rest;
                BookmarkGroup target = EnsureGroupQuiet(moved);
                target.Color ??= r.Color;
                if (!r.Visible && target.Visible)
                {
                    target.Visible = false;
                    _hiddenGroups++;
                }
            }

            foreach (Bookmark b in inside)
            {
                record.Moved.Add((b, b.Group));
                string rest = b.Group == path ? string.Empty : b.Group![(path.Length + 1)..];
                b.Group = rest.Length == 0 ? parent : parent is null ? rest : parent + BookmarkGroups.Separator + rest;
            }
        }

        RaiseChanged(BookmarkChangeKind.Groups, inside);
        return record;
    }

    /// <summary>グループの削除を取り消す (InfoBar の「元に戻す」)。</summary>
    public void UndoGroupDeletion(BookmarkGroupDeletion record)
    {
        foreach (BookmarkGroupRecord r in record.Groups)
        {
            RestoreGroup(r);
        }

        foreach ((Bookmark b, string? group) in record.Moved)
        {
            b.Group = group;
        }

        foreach ((Bookmark b, BookmarkPosition p) in record.Removed)
        {
            if (Count >= MaxCount || b.Owner is not null)
            {
                continue;
            }

            b.Length = p.Length;
            b.RangeDeleted = p.RangeDeleted;
            Attach(b, p.Start);
            if (b.Number != 0)
            {
                if (_numbers[b.Number] is null)
                {
                    _numbers[b.Number] = b;
                }
                else
                {
                    b.Number = 0;
                }
            }
        }

        RaiseChanged(BookmarkChangeKind.Reset, []);
    }

    /// <summary>記録したグループを戻す (読み込み・取り消し)。</summary>
    internal void RestoreGroup(BookmarkGroupRecord r)
    {
        if (BookmarkGroups.Normalize(r.Path) is not { } path || BookmarkGroups.DepthOf(path) > BookmarkGroups.MaxDepth)
        {
            return;
        }

        BookmarkGroup g = EnsureGroupQuiet(path);
        g.Color = r.Color;
        if (g.Visible != r.Visible)
        {
            g.Visible = r.Visible;
            _hiddenGroups += r.Visible ? -1 : 1;
        }
    }

    /// <summary>
    /// まとめて付けるとき用: 通知せずに付ける (最後に <see cref="RaiseAdded"/> を呼ぶ)。上限なら <see cref="BookmarkLimitException"/>。
    /// </summary>
    public Bookmark AddQuiet(long start, long length, string name, BookmarkColor? color = null, string? group = null, bool colorSet = false)
    {
        if (Count >= MaxCount)
        {
            throw new BookmarkLimitException();
        }

        group = BookmarkGroups.Normalize(group);
        if (group is not null)
        {
            EnsureGroupQuiet(group);
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        Bookmark b = Restore(Math.Max(0, start), Math.Max(0, length), name, color ?? BookmarkColor.Default, string.Empty, 0, group, now, now,
            false, false, false, false, colorSet);
        return b;
    }

    /// <summary>
    /// インポート用: 番号を付ける。番号が別のブックマークに付いていれば、そちらから外す (ブックマーク自体は消さない。INSP-30 の仕様 4)。
    /// </summary>
    internal void SetNumberQuiet(Bookmark b, int number)
    {
        if (_numbers[number] is { } other && other != b)
        {
            other.Number = 0;
        }

        if (b.Number != 0 && _numbers[b.Number] == b)
        {
            _numbers[b.Number] = null;
        }

        b.Number = number;
        _numbers[number] = b;
    }

    /// <summary><see cref="AddQuiet"/> で付けたものをまとめて知らせる。</summary>
    public void RaiseAdded(IReadOnlyList<Bookmark> added)
    {
        if (added.Count > 0)
        {
            RaiseChanged(BookmarkChangeKind.Added, added);
        }
    }

    /// <summary>グループの記録 (保存用。パスの順)。</summary>
    public IReadOnlyList<BookmarkGroupRecord> GroupRecords() =>
        [.. _groups.Values.OrderBy(g => g.Path, StringComparer.Ordinal).Select(g => new BookmarkGroupRecord(g.Path, g.Color, g.Visible))];
}
