using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Bookmarks;

/// <summary>ブックマークの変更の種類。</summary>
public enum BookmarkChangeKind
{
    Added,
    Removed,

    /// <summary>名前・色・コメント・番号・範囲の変更。</summary>
    Modified,

    /// <summary>ドキュメントの編集 (または Undo / Redo) で位置が変わった。</summary>
    Positions,

    /// <summary>読み込みなどで全体が変わった。</summary>
    Reset,

    /// <summary>グループ (INSP-27) の追加・削除・色・表示の変更。ブックマークのグループの付け替えも含む。</summary>
    Groups,
}

public sealed class BookmarksChangedEventArgs(BookmarkChangeKind kind, IReadOnlyList<Bookmark> items) : EventArgs
{
    public BookmarkChangeKind Kind { get; } = kind;

    public IReadOnlyList<Bookmark> Items { get; } = items;

    /// <summary>
    /// グループの表示 / 非表示だけが変わった (<see cref="BookmarkChangeKind.Groups"/>。INSP-27 の仕様 3)。並び・件数は変わらないので、
    /// 一覧は作り直さずに行の表示 (薄く表示) だけを直せばよい (100 万件でも 200 ms 以内に反映する)。
    /// </summary>
    public bool VisibilityOnly { get; init; }
}

/// <summary>ブックマーク 1 件の位置の記録 (保存・Undo・削除の取り消しに使う)。</summary>
public readonly record struct BookmarkPosition(long Start, long Length, bool RangeDeleted);

/// <summary>ブックマークの上限に達した (INSP-23 の仕様 6)。</summary>
public sealed class BookmarkLimitException() : InvalidOperationException("ブックマークの上限に達しました。");

/// <summary>
/// ドキュメント 1 つのブックマーク (INSP-23〜INSP-26)。位置は区間木で持ち、ドキュメントの編集に合わせて調整する
/// (INSP-23 の仕様 4)。ブックマーク自体の追加・削除・変更はデータの Undo 履歴に入れず、ドキュメントを「変更あり」にしない (仕様 5)。
/// データの Undo / Redo では位置も元に戻す。UI スレッドから使う。
/// </summary>
public sealed partial class BookmarkCollection
{
    /// <summary>1 つのドキュメントのブックマークの上限 (INSP-23 の仕様 6)。</summary>
    public const int MaxCount = 1_000_000;

    /// <summary>削除の取り消しで戻せる件数 (INSP-23 の仕様 5)。</summary>
    public const int UndoLimit = 20;

    /// <summary>コメントの上限 (64 KB。INSP-24 の仕様 5)。UTF-16 の文字数で数える。</summary>
    public const int MaxCommentLength = 64 * 1024;

    public const int MaxNameLength = 256;

    private static readonly ConditionalWeakTable<Document, BookmarkCollection> Attached = new();

    private readonly BookmarkTree _tree = new();
    private readonly Dictionary<string, object> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Bookmark?[] _numbers = new Bookmark?[10];
    private readonly LinkedList<IReadOnlyList<(Bookmark Bookmark, BookmarkPosition Position)>> _deleted = new();
    private readonly TimeProvider _time;
    private long _nextId = 1;

    // データの履歴との対応 (ドキュメントにつないだとき)。_records[i] は履歴の i 番目の項目を作った編集。
    private readonly List<EditRecord?> _records = [null];
    private Document? _document;
    private int _appliedIndex;
    private int _savedIndex;

    public BookmarkCollection(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>ブックマークが変わった。</summary>
    public event EventHandler<BookmarksChangedEventArgs>? Changed;

    public int Count => _tree.Count;

    /// <summary>「ブックマーク N」の次の番号 (ドキュメントの中で増える。INSP-23 の仕様 2)。</summary>
    public int NextAutoNumber { get; set; } = 1;

    /// <summary>削除を取り消せるか (直近 20 件)。</summary>
    public bool CanUndoDelete => _deleted.Count > 0;

    public Document? Document => _document;

    // ---- ドキュメントとの結び付け ----

    /// <summary>ドキュメントに付けたブックマーク (入力式の <c>bm.名前</c> などから引く)。なければ null。</summary>
    public static BookmarkCollection? For(Document document) => Attached.TryGetValue(document, out BookmarkCollection? c) ? c : null;

    /// <summary>ドキュメントの編集に合わせて位置を調整するようにする。</summary>
    public static BookmarkCollection Attach(Document document, TimeProvider? time = null)
    {
        if (For(document) is { } existing)
        {
            return existing;
        }

        var collection = new BookmarkCollection(time);
        collection._document = document;
        collection._appliedIndex = document.History.CurrentIndex;
        collection._savedIndex = document.IsModified ? -1 : document.History.CurrentIndex;
        while (collection._records.Count <= collection._appliedIndex)
        {
            collection._records.Add(null);
        }

        document.Changed += collection.OnDocumentChanged;
        Attached.Add(document, collection);
        return collection;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_document is not { } doc)
        {
            return;
        }

        int index = doc.History.CurrentIndex;
        switch (e.Kind)
        {
            case DocumentChangeKind.Edit:
            {
                EditRecord record;
                if (index == _appliedIndex && index < _records.Count && _records[index] is { } same)
                {
                    // 直前の項目にまとめられた入力 (連続した文字入力・編集グループ)。
                    record = same;
                }
                else
                {
                    if (_records.Count > index)
                    {
                        // やり直せる履歴は捨てられた。保存した時点がその中にあれば、もう戻れない。
                        _records.RemoveRange(index, _records.Count - index);
                        if (_savedIndex >= index)
                        {
                            _savedIndex = -1;
                        }
                    }

                    while (_records.Count < index)
                    {
                        _records.Add(null);
                    }

                    record = new EditRecord();
                    _records.Add(record);
                }

                _appliedIndex = index;
                ApplyEdit(e.Offset, e.RemovedLength, e.InsertedLength, record.Ops);
                break;
            }

            case DocumentChangeKind.Undo:
                for (int i = _appliedIndex; i > index; i--)
                {
                    if (i < _records.Count && _records[i] is { } undone)
                    {
                        Revert(undone);
                    }
                }

                _appliedIndex = index;
                RaiseChanged(BookmarkChangeKind.Positions, []);
                break;
            case DocumentChangeKind.Redo:
                for (int i = _appliedIndex + 1; i <= index; i++)
                {
                    if (i < _records.Count && _records[i] is { } redone)
                    {
                        Reapply(redone);
                    }
                }

                _appliedIndex = index;
                RaiseChanged(BookmarkChangeKind.Positions, []);
                break;
            case DocumentChangeKind.Saved:
                _savedIndex = index;
                break;
        }

        if (!doc.IsModified)
        {
            _savedIndex = index;
        }
    }

    // ---- 位置の調整 (INSP-23 の仕様 4) ----

    /// <summary>編集 1 回分の、位置の変え方と変えたブックマークの元の位置。</summary>
    private sealed class EditRecord
    {
        public List<EditOp> Ops { get; } = [];
    }

    /// <summary>挿入 (Inserted &gt; 0) か削除 (Removed &gt; 0) の 1 つ。<see cref="Saved"/> は範囲が重なっていたものの元の位置。</summary>
    private sealed record EditOp(long Offset, long Removed, long Inserted)
    {
        public List<(Bookmark Bookmark, BookmarkPosition Before)> Saved { get; set; } = [];
    }

    /// <summary>
    /// ドキュメントの編集 (offset から removed バイトを inserted バイトに置き換えた) に合わせる。同じ長さの部分は上書きなので位置を
    /// 変えず、長さの違う部分を挿入か削除として扱う。
    /// </summary>
    public void ApplyEdit(long offset, long removed, long inserted) => ApplyEdit(offset, removed, inserted, null);

    private void ApplyEdit(long offset, long removed, long inserted, List<EditOp>? ops)
    {
        if (removed == inserted)
        {
            return;
        }

        long common = Math.Min(removed, inserted);
        EditOp op = inserted > removed
            ? new EditOp(offset + common, 0, inserted - removed)
            : new EditOp(offset + common, removed - inserted, 0);
        op.Saved = Forward(op);
        ops?.Add(op);
        RaiseChanged(BookmarkChangeKind.Positions, []);
    }

    /// <summary>挿入か削除を区間木に適用し、範囲が重なっていたものの元の位置を返す。</summary>
    private List<(Bookmark, BookmarkPosition)> Forward(EditOp op)
    {
        var saved = new List<(Bookmark, BookmarkPosition)>();
        if (_tree.Count == 0)
        {
            return saved;
        }

        var nodes = new List<Bookmark>();
        long o = op.Offset;
        long end = o + op.Removed;

        // 編集の位置に接するものと重なるもの (開始 ≤ 編集の終わり かつ 終了 ≥ 編集の始め) は個別に計算し、それより後ろは
        // まとめてずらす。
        _tree.Collect(end, o, strict: false, nodes);
        var moved = new List<(Bookmark Bookmark, BookmarkPosition After)>(nodes.Count);
        foreach (Bookmark node in nodes)
        {
            Bookmark b = node;
            var before = new BookmarkPosition(BookmarkTree.StartOf(node), b.Length, b.RangeDeleted);
            saved.Add((b, before));
            moved.Add((b, op.Inserted > 0 ? MapInsert(before, o, op.Inserted) : MapDelete(before, o, op.Removed)));
        }

        foreach (Bookmark node in nodes)
        {
            _tree.Remove(node);
        }

        if (op.Inserted > 0)
        {
            _tree.ShiftFrom(o, inclusive: false, op.Inserted);
        }
        else
        {
            _tree.ShiftFrom(end, inclusive: false, -op.Removed);
        }

        foreach ((Bookmark b, BookmarkPosition after) in moved)
        {
            Place(b, after);
        }

        return saved;
    }

    /// <summary>Undo: 逆の操作 (挿入なら削除、削除なら挿入) を適用してから、重なっていたものを元の位置に戻す。</summary>
    private void Revert(EditRecord record)
    {
        for (int i = record.Ops.Count - 1; i >= 0; i--)
        {
            EditOp op = record.Ops[i];
            var present = op.Saved.Where(s => s.Bookmark.Owner == this).ToList();
            foreach ((Bookmark b, _) in present)
            {
                _tree.Remove(b);
            }

            var inverse = op.Inserted > 0 ? new EditOp(op.Offset, op.Inserted, 0) : new EditOp(op.Offset, 0, op.Removed);
            Forward(inverse);
            foreach ((Bookmark b, BookmarkPosition before) in present)
            {
                Place(b, before);
            }
        }
    }

    /// <summary>Redo: 同じ操作をもう一度適用する (重なっていたものの元の位置は記録し直す)。</summary>
    private void Reapply(EditRecord record)
    {
        foreach (EditOp op in record.Ops)
        {
            op.Saved = Forward(op);
        }
    }

    private void Place(Bookmark b, BookmarkPosition position)
    {
        b.Length = position.Length;
        b.RangeDeleted = position.RangeDeleted;
        _tree.Insert(b, position.Start);
    }

    /// <summary>挿入 (o に n バイト) の後の位置。範囲の内側への挿入は長さを伸ばし、開始位置かそれより前への挿入はずらす。</summary>
    public static BookmarkPosition MapInsert(BookmarkPosition p, long o, long n)
    {
        long start = p.Start >= o ? p.Start + n : p.Start;
        long end = p.Length == 0 ? start : (p.Start + p.Length > o ? p.Start + p.Length + n : p.Start + p.Length);
        return p with { Start = start, Length = end - start };
    }

    /// <summary>削除 ([o, o + n)) の後の位置。範囲がすべて削除されたら長さ 0 にして削除位置に置き、印を付ける。</summary>
    public static BookmarkPosition MapDelete(BookmarkPosition p, long o, long n)
    {
        long Map(long x) => x < o ? x : x < o + n ? o : x - n;
        long start = Map(p.Start);
        long end = Map(p.Start + p.Length);
        bool deleted = p.RangeDeleted || p.Length > 0 && end == start;
        return new BookmarkPosition(start, end - start, deleted);
    }

    // ---- 追加・削除 (INSP-23) ----

    /// <summary>ブックマークを付ける。上限なら <see cref="BookmarkLimitException"/>。</summary>
    public Bookmark Add(long start, long length, string name, BookmarkColor? color = null)
    {
        if (Count >= MaxCount)
        {
            throw new BookmarkLimitException();
        }

        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var b = new Bookmark(_nextId++, Truncate(name, MaxNameLength), color ?? BookmarkColor.Default, _time.GetUtcNow().UtcDateTime) { Length = length };
        Attach(b, start);
        RaiseChanged(BookmarkChangeKind.Added, [b]);
        return b;
    }

    /// <summary>読み込み用: 記録された値のまま加える (通知は呼び出し側でまとめて出す)。</summary>
    internal Bookmark Restore(long start, long length, string name, BookmarkColor color, string comment, int number, string? group,
        DateTime created, DateTime updated, bool rangeDeleted, bool createdForNumber, bool editedByUser, bool customized, bool colorSet = false)
    {
        group = BookmarkGroups.Normalize(group);
        if (group is not null)
        {
            EnsureGroupQuiet(group);
        }

        var b = new Bookmark(_nextId++, Truncate(name, MaxNameLength), color, created)
        {
            Length = length,
            Comment = Truncate(comment, MaxCommentLength),
            Group = group,
            Updated = updated,
            RangeDeleted = rangeDeleted,
            CreatedForNumber = createdForNumber,
            EditedByUser = editedByUser,
            IsCustomized = customized,
            ColorSet = colorSet,
        };
        Attach(b, start);
        if (number is >= 1 and <= 9 && _numbers[number] is null)
        {
            b.Number = number;
            _numbers[number] = b;
        }

        return b;
    }

    /// <summary>全体を差し替えた (読み込み) ことを知らせる。</summary>
    internal void RaiseReset() => RaiseChanged(BookmarkChangeKind.Reset, []);

    private void Attach(Bookmark b, long start)
    {
        b.Owner = this;
        _tree.Insert(b, start);
        AddName(b);
    }

    /// <summary>外す。<paramref name="remember"/> なら削除の取り消し (直近 20 件) に記録する。</summary>
    public void Remove(Bookmark b, bool remember = true) => RemoveRange([b], remember);

    public void RemoveRange(IReadOnlyCollection<Bookmark> items, bool remember = true)
    {
        var removed = new List<(Bookmark, BookmarkPosition)>();
        foreach (Bookmark b in items)
        {
            if (b.Owner != this || !b.InTree)
            {
                continue;
            }

            long start = BookmarkTree.StartOf(b);
            removed.Add((b, new BookmarkPosition(start, b.Length, b.RangeDeleted)));
            _tree.Remove(b);
            b.Owner = null;
            b._detachedStart = start;
            RemoveName(b);
            if (b.Number != 0 && _numbers[b.Number] == b)
            {
                _numbers[b.Number] = null;
            }
        }

        if (removed.Count == 0)
        {
            return;
        }

        if (remember)
        {
            _deleted.AddFirst(removed);
            while (_deleted.Count > UndoLimit)
            {
                _deleted.RemoveLast();
            }
        }

        RaiseChanged(BookmarkChangeKind.Removed, [.. removed.Select(r => r.Item1)]);
    }

    /// <summary>直近の削除を取り消す。戻したブックマークを返す。</summary>
    public IReadOnlyList<Bookmark> UndoDelete()
    {
        if (_deleted.First is not { } first)
        {
            return [];
        }

        _deleted.RemoveFirst();
        var restored = new List<Bookmark>();
        foreach ((Bookmark b, BookmarkPosition p) in first.Value)
        {
            if (Count >= MaxCount)
            {
                break;
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

            restored.Add(b);
        }

        RaiseChanged(BookmarkChangeKind.Added, restored);
        return restored;
    }

    /// <summary>すべて外す (記録しない)。</summary>
    public void Clear()
    {
        foreach ((Bookmark node, _) in _tree.InOrder().ToList())
        {
            node.Owner = null;
            node.InTree = false;
        }

        _tree.Clear();
        _byName.Clear();
        Array.Clear(_numbers);
        _groups.Clear();
        _hiddenGroups = 0;
        RaiseChanged(BookmarkChangeKind.Reset, []);
    }

    // ---- 変更 (INSP-24、INSP-25) ----

    public void Rename(Bookmark b, string name)
    {
        name = Truncate(name, MaxNameLength);
        if (b.Name == name)
        {
            return;
        }

        RemoveName(b);
        b.Name = name;
        AddName(b);
        Touch(b, customized: true);
    }

    public void SetComment(Bookmark b, string comment)
    {
        comment = Truncate(comment, MaxCommentLength);
        if (b.Comment == comment)
        {
            return;
        }

        b.Comment = comment;
        Touch(b, customized: comment.Length > 0 || b.IsCustomized);
    }

    public void SetColor(Bookmark b, BookmarkColor color)
    {
        if (b.Color == color && b.ColorSet)
        {
            return;
        }

        b.Color = color;
        b.ColorSet = true;
        Touch(b, customized: b.IsCustomized);
    }

    /// <summary>範囲を変える (編集のフライアウト。INSP-24 の仕様 1)。</summary>
    public void SetRange(Bookmark b, long start, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (b.Owner != this || !b.InTree || BookmarkTree.StartOf(b) == start && b.Length == length)
        {
            return;
        }

        _tree.Remove(b);
        b.Length = length;
        b.RangeDeleted = false;
        _tree.Insert(b, start);
        Touch(b, customized: b.IsCustomized);
    }

    /// <summary>グループを変える (INSP-27)。グループがなければ作る (9 階層目なら <see cref="BookmarkGroupDepthException"/>)。</summary>
    public void SetGroup(Bookmark b, string? group)
    {
        group = BookmarkGroups.Normalize(group);
        if (group is not null)
        {
            EnsureGroup(group);
        }

        if (b.Group == group)
        {
            return;
        }

        b.Group = group;
        b.EditedByUser = true;
        b.Updated = _time.GetUtcNow().UtcDateTime;
        RaiseChanged(BookmarkChangeKind.Groups, [b]);
    }

    /// <summary>
    /// 番号を付ける・外す (0 は外す)。番号が別のブックマークに付いていたら、そちらから外す。外されたものが番号の設定で自動的に
    /// 作られて変更されていなければ、ブックマークごと削除する (INSP-25 の仕様 1)。
    /// </summary>
    public void SetNumber(Bookmark b, int number)
    {
        if (number is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        if (b.Number == number)
        {
            return;
        }

        if (number != 0 && _numbers[number] is { } other && other != b)
        {
            other.Number = 0;
            _numbers[number] = null;
            if (other.CreatedForNumber && !other.EditedByUser)
            {
                Remove(other, remember: false);
            }
            else
            {
                RaiseChanged(BookmarkChangeKind.Modified, [other]);
            }
        }

        if (b.Number != 0 && _numbers[b.Number] == b)
        {
            _numbers[b.Number] = null;
        }

        b.Number = number;
        if (number != 0)
        {
            _numbers[number] = b;
        }

        b.Updated = _time.GetUtcNow().UtcDateTime;
        RaiseChanged(BookmarkChangeKind.Modified, [b]);
    }

    private void Touch(Bookmark b, bool customized)
    {
        b.EditedByUser = true;
        b.IsCustomized = customized;
        b.Updated = _time.GetUtcNow().UtcDateTime;
        RaiseChanged(BookmarkChangeKind.Modified, [b]);
    }

    // ---- 検索 ----

    public long StartOf(Bookmark b) => b.InTree ? BookmarkTree.StartOf(b) : b._detachedStart;

    /// <summary>開始位置の順のすべて。</summary>
    public IEnumerable<Bookmark> All => _tree.InOrder().Select(p => p.Bookmark);

    /// <summary>開始位置の順のすべてと、その開始位置。</summary>
    public IEnumerable<(Bookmark Bookmark, long Start)> AllWithStart => _tree.InOrder();

    /// <summary>[start, end) と重なるもの (長さ 0 のものは位置が範囲内なら含む)。表示範囲の強調に使う。</summary>
    public IReadOnlyList<Bookmark> Overlapping(long start, long end)
    {
        var nodes = new List<Bookmark>();
        _tree.Collect(end - 1, start, strict: false, nodes);
        var result = new List<Bookmark>(nodes.Count);
        foreach (Bookmark node in nodes)
        {
            long s = BookmarkTree.StartOf(node);
            long e = s + node.Length;
            if (node.Length == 0 ? s >= start && s < end : e > start)
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary><paramref name="offset"/> から始まるもの (同じ位置に複数あれば最初に作ったもの)。</summary>
    public Bookmark? StartingAt(long offset) =>
        _tree.FirstFrom(offset, strictlyAfter: false) is { } node && BookmarkTree.StartOf(node) == offset ? node : null;

    /// <summary>開始位置が <paramref name="offset"/> より後ろで最も近いもの。</summary>
    public Bookmark? After(long offset) => _tree.FirstFrom(offset, strictlyAfter: true);

    /// <summary>開始位置が <paramref name="offset"/> より前で最も近いもの。</summary>
    public Bookmark? Before(long offset) => _tree.LastBefore(offset);

    public Bookmark? First => _tree.First();

    public Bookmark? Last => _tree.Last();

    public Bookmark? WithNumber(int number) => number is >= 1 and <= 9 ? _numbers[number] : null;

    /// <summary>
    /// 入力式の <c>bm.名前</c> の名前で探す (INSP-24 の仕様 2)。英字・数字・<c>_</c> の名前だけ。重複していれば最初に作ったもの。
    /// 大文字・小文字が一致するものを優先する。
    /// </summary>
    public Bookmark? FindByName(string name)
    {
        if (!Bookmark.IsExpressionName(name) || !_byName.TryGetValue(name, out object? value))
        {
            return null;
        }

        if (value is Bookmark single)
        {
            return single;
        }

        var list = (List<Bookmark>)value;
        return list.Where(b => b.Name == name).MinBy(b => b.Id) ?? list.MinBy(b => b.Id);
    }

    // 名前ごとの値は、1 件ならそのブックマーク、2 件以上なら List (100 万件の名前ごとに List を作らない)。
    private void AddName(Bookmark b)
    {
        if (!Bookmark.IsExpressionName(b.Name))
        {
            return;
        }

        if (!_byName.TryGetValue(b.Name, out object? value))
        {
            _byName[b.Name] = b;
        }
        else if (value is Bookmark other)
        {
            _byName[b.Name] = new List<Bookmark> { other, b };
        }
        else
        {
            ((List<Bookmark>)value).Add(b);
        }
    }

    private void RemoveName(Bookmark b)
    {
        if (!_byName.TryGetValue(b.Name, out object? value))
        {
            return;
        }

        if (value == b)
        {
            _byName.Remove(b.Name);
        }
        else if (value is List<Bookmark> list && list.Remove(b))
        {
            if (list.Count == 1)
            {
                _byName[b.Name] = list[0];
            }
        }
    }

    // ---- 保存 (INSP-23 の仕様 7) ----

    /// <summary>
    /// 保存する内容を <paramref name="buffer"/> に写し、件数を返す (<paramref name="buffer"/> は <see cref="Count"/> 以上の長さ)。
    /// 位置は、ファイルに保存されている内容 (保存した時点、開いた時点) での位置。保存せずに閉じても、記録した位置がファイルの内容と
    /// 合うようにする。保存した時点に戻れない (Undo で戻ってから別の編集をした) 場合は今の位置。
    /// 100 万件でも UI スレッドを長く止めないよう、1 件ごとのオブジェクトや辞書を作らない (VIEW-04 の受け入れ基準 4)。
    /// </summary>
    internal int CaptureRecords(BookmarkRecord[] buffer)
    {
        int n = 0;
        foreach ((Bookmark b, long start) in _tree.InOrder())
        {
            b.Scratch = n;
            buffer[n++] = new BookmarkRecord(start, b.Length, b.Name, b.Color, b.Comment, b.Number, b.Group, b.Created, b.Updated,
                b.RangeDeleted, b.CreatedForNumber, b.EditedByUser, b.IsCustomized, b.ColorSet);
        }

        if (_document is null || _savedIndex < 0 || _savedIndex == _appliedIndex || n == 0)
        {
            return n;
        }

        Span<BookmarkRecord> items = buffer.AsSpan(0, n);
        if (_savedIndex < _appliedIndex)
        {
            for (int i = _appliedIndex; i > _savedIndex; i--)
            {
                if (i < _records.Count && _records[i] is { } record)
                {
                    for (int k = record.Ops.Count - 1; k >= 0; k--)
                    {
                        EditOp op = record.Ops[k];
                        for (int j = 0; j < items.Length; j++)
                        {
                            BookmarkPosition p = PositionOf(items[j]);
                            items[j] = WithPosition(items[j], op.Inserted > 0 ? MapDelete(p, op.Offset, op.Inserted) : MapInsert(p, op.Offset, op.Removed));
                        }

                        foreach ((Bookmark b, BookmarkPosition before) in op.Saved)
                        {
                            if (b.Owner == this && b.InTree)
                            {
                                items[b.Scratch] = WithPosition(items[b.Scratch], before);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            for (int i = _appliedIndex + 1; i <= _savedIndex && i < _records.Count; i++)
            {
                if (_records[i] is { } record)
                {
                    foreach (EditOp op in record.Ops)
                    {
                        for (int j = 0; j < items.Length; j++)
                        {
                            BookmarkPosition p = PositionOf(items[j]);
                            items[j] = WithPosition(items[j], op.Inserted > 0 ? MapInsert(p, op.Offset, op.Inserted) : MapDelete(p, op.Offset, op.Removed));
                        }
                    }
                }
            }
        }

        // 位置の順に並べ直す (同じ位置は今の順。今の順は開始位置・作った順)。
        var keys = new (long Start, int Index)[n];
        for (int j = 0; j < n; j++)
        {
            keys[j] = (items[j].Start, j);
        }

        Array.Sort(keys, buffer, 0, n);
        return n;

        static BookmarkPosition PositionOf(in BookmarkRecord r) => new(r.Start, r.Length, r.RangeDeleted);

        static BookmarkRecord WithPosition(in BookmarkRecord r, BookmarkPosition p) =>
            r with { Start = p.Start, Length = p.Length, RangeDeleted = p.RangeDeleted };
    }

    /// <summary>保存する位置 (<see cref="CaptureRecords"/> と同じ。テスト・確認用)。</summary>
    public IReadOnlyList<BookmarkPosition> PositionsAtSavedState()
    {
        var buffer = new BookmarkRecord[Count];
        int n = CaptureRecords(buffer);
        var result = new BookmarkPosition[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = new BookmarkPosition(buffer[i].Start, buffer[i].Length, buffer[i].RangeDeleted);
        }

        return result;
    }

    private void RaiseChanged(BookmarkChangeKind kind, IReadOnlyList<Bookmark> items)
    {
        _ordered = null;
        Changed?.Invoke(this, new BookmarksChangedEventArgs(kind, items));
    }

    /// <summary>グループの表示 / 非表示の変更を知らせる (並びは変わらないので、並べた配列は捨てない)。</summary>
    private void RaiseVisibilityChanged() =>
        Changed?.Invoke(this, new BookmarksChangedEventArgs(BookmarkChangeKind.Groups, []) { VisibilityOnly = true });

    private Bookmark[]? _ordered;

    /// <summary>開始位置の順のすべて (配列。変更があるまで使い回す。一覧の絞り込み・並べ替えに使う)。</summary>
    public IReadOnlyList<Bookmark> Ordered => _ordered ??= [.. All];

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
