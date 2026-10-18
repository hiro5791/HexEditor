namespace HexEditor.Core.Engine;

/// <summary>
/// 1 つの編集グループが変えた範囲 (EDIT-19 の仕様 3・10)。<see cref="Offset"/> から、編集前の内容では
/// <see cref="BeforeLength"/> バイト、編集後の内容では <see cref="AfterLength"/> バイトが変わった。
/// 元に戻した後は編集前の範囲、やり直した後は編集後の範囲を選択する。
/// </summary>
public readonly record struct EditRange(long Offset, long BeforeLength, long AfterLength)
{
    /// <summary>
    /// この範囲の後に、編集後の内容の上で (offset, removed, inserted) の編集をしたときの、2 つを合わせた範囲。
    /// 2 つの編集が離れている場合は間を含む範囲になる。
    /// </summary>
    public EditRange Then(long offset, long removed, long inserted)
    {
        long delta = AfterLength - BeforeLength;
        long start = Shift(Offset, offset, removed, inserted, toEnd: false);
        long end = Shift(Offset + AfterLength, offset, removed, inserted, toEnd: true);
        long newStart = Math.Min(start, offset);
        long newEnd = Math.Max(end, offset + inserted);
        long newDelta = delta + inserted - removed;
        return new EditRange(newStart, newEnd - newStart - newDelta, newEnd - newStart);
    }

    private static long Shift(long position, long offset, long removed, long inserted, bool toEnd)
    {
        if (position <= offset)
        {
            return position;
        }

        if (position >= offset + removed)
        {
            return position - removed + inserted;
        }

        // 削除された部分の中: 範囲の終わりは挿入した内容の後ろまで広げる。
        return toEnd ? offset + inserted : offset;
    }
}

/// <summary>履歴の 1 項目 (編集グループ): ある時点のスナップショットと、そこに至った操作の説明と範囲。</summary>
public sealed record HistoryEntry(DocumentSnapshot Snapshot, string Description, string? CoalesceKey)
{
    /// <summary>このグループが変えた範囲。最初の項目 (開いた時点) と保存による差し替えでは null。</summary>
    public EditRange? Range { get; init; }

    /// <summary>まとめた入力のバイト数 (EDIT-19 の仕様 5 の 4 KiB の上限)。</summary>
    public long CoalescedBytes { get; init; }

    /// <summary>最後に入力をまとめた時刻 (<see cref="TimeProvider.GetTimestamp"/>)。</summary>
    public long LastEditTimestamp { get; init; }

    /// <summary>この項目の編集をした日時 (履歴パネルの「日時」。EDIT-20 の仕様 1)。</summary>
    public DateTimeOffset Time { get; init; }
}

/// <summary>
/// 履歴の木の節 (EDIT-21 の仕様 3)。1 つの編集グループの直後の状態を表す。根は開いた時点。分岐する履歴を使わない場合も木で持つが、
/// 枝は 1 本だけになる。子はリストではなく兄弟の連結で持ち、節ごとの割り当てを増やさない (通常の編集を遅くしない)。
/// </summary>
public sealed class HistoryNode
{
    private HistoryNode? _firstChild;
    private HistoryNode? _nextSibling;
    private HistoryNode? _previousSibling;

    internal HistoryNode(int id, HistoryEntry entry, HistoryNode? parent)
    {
        Id = id;
        Entry = entry;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>履歴の中で一意の番号 (作った順。保存した履歴でも同じ番号を使う)。</summary>
    public int Id { get; }

    /// <summary>この節の状態と、そこに至った操作。</summary>
    public HistoryEntry Entry { get; internal set; }

    public HistoryNode? Parent { get; private set; }

    /// <summary>根からの深さ (根は 0)。現在の経路の上では、履歴パネルの一覧の番号と同じ。</summary>
    public int Depth { get; }

    /// <summary>やり直し (Ctrl+Y) で進む子 (最後に通った子。EDIT-21 の仕様 2)。</summary>
    public HistoryNode? LastChild { get; internal set; }

    /// <summary>利用者が付けた名前 (1〜100 文字。EDIT-21 の仕様 4)。なければ null。</summary>
    public string? Name { get; internal set; }

    /// <summary>固定 (ピン留め。EDIT-21 の仕様 5)。</summary>
    public bool IsPinned { get; internal set; }

    /// <summary>子を作った順に返す。</summary>
    public IEnumerable<HistoryNode> Children
    {
        get
        {
            for (HistoryNode? c = _firstChild; c is not null; c = c._nextSibling)
            {
                yield return c;
            }
        }
    }

    public bool HasChildren => _firstChild is not null;

    /// <summary>子が 2 つ以上あるか (枝分かれ)。</summary>
    public bool IsBranchPoint => _firstChild?._nextSibling is not null;

    /// <summary><paramref name="other"/> がこの節か、この節の子孫か。</summary>
    public bool IsAncestorOrSelfOf(HistoryNode other)
    {
        for (HistoryNode? n = other; n is not null && n.Depth >= Depth; n = n.Parent)
        {
            if (ReferenceEquals(n, this))
            {
                return true;
            }
        }

        return false;
    }

    internal void AddChild(HistoryNode child)
    {
        if (_firstChild is null)
        {
            _firstChild = child;
            return;
        }

        HistoryNode last = _firstChild;
        while (last._nextSibling is not null)
        {
            last = last._nextSibling;
        }

        last._nextSibling = child;
        child._previousSibling = last;
    }

    internal void RemoveChild(HistoryNode child)
    {
        if (child._previousSibling is { } p)
        {
            p._nextSibling = child._nextSibling;
        }
        else
        {
            _firstChild = child._nextSibling;
        }

        if (child._nextSibling is { } n)
        {
            n._previousSibling = child._previousSibling;
        }

        child._previousSibling = child._nextSibling = null;
        child.Parent = null;
        if (ReferenceEquals(LastChild, child))
        {
            // 残った子のうち最後に作ったものをやり直しの先にする。
            HistoryNode? last = _firstChild;
            while (last?._nextSibling is not null)
            {
                last = last._nextSibling;
            }

            LastChild = last;
        }
    }

    public override string ToString() => $"#{Id} {Entry.Description}";
}

/// <summary>
/// Undo / Redo の履歴 (ENG-05 の仕様 3・6、EDIT-19)。各時点をスナップショットで持つため、Undo・Redo は参照の切り替えだけで済む。
/// 履歴は木で持ち (EDIT-21)、<see cref="Entries"/> などの番号は「現在の経路」(根から現在の節を通り、最後に通った子をたどった先まで) の上の位置。
/// 分岐する履歴を使わない場合 (<see cref="BranchingEnabled"/> が false) は、新しい編集でやり直しの枝を捨てるため、経路が木の全体になる。
/// </summary>
public sealed class EditHistory
{
    /// <summary>入力をまとめる間隔の既定値 (EDIT-19 の仕様 5)。</summary>
    public static readonly TimeSpan DefaultCoalesceInterval = TimeSpan.FromSeconds(2);

    /// <summary>1 つの編集グループにまとめる入力の上限 (EDIT-19 の仕様 5)。</summary>
    public const long CoalesceLimitBytes = 4 * 1024;

    /// <summary>節の名前の最大の長さ (EDIT-21 の仕様 4)。</summary>
    public const int MaxNameLength = 100;

    // 現在の経路 (根 → 現在の節 → 最後に通った子の連なり)。_path[i].Depth == i。
    private readonly List<HistoryNode> _path = [];
    private readonly PathEntries _entries;
    private readonly TimeProvider _time;
    private HistoryNode _root;
    private int _current;
    private HistoryNode? _saved;
    private int _nextId;
    private int _nodeCount;
    private int _namedCount;
    private int _groupDepth;
    private bool _groupStarted;
    private string _groupDescription = string.Empty;
    private string? _groupCoalesceKey;
    private bool _groupMergeWithPrevious;

    internal EditHistory(DocumentSnapshot initial, TimeProvider? time = null, TimeSpan? coalesceInterval = null)
    {
        _time = time ?? TimeProvider.System;
        CoalesceInterval = coalesceInterval ?? DefaultCoalesceInterval;
        _entries = new PathEntries(_path);
        _root = NewNode(new HistoryEntry(initial, string.Empty, null), null);
        _path.Add(_root);
        _saved = _root;
    }

    /// <summary>連続した入力をまとめる最大の間隔 (設定で 0〜10 秒。0 はまとめない)。</summary>
    public TimeSpan CoalesceInterval { get; set; }

    /// <summary>
    /// 分岐する履歴を使うか (EDIT-21 の設定「分岐する履歴を使う」)。オンなら、元に戻した状態での新しい編集でやり直しの履歴を捨てずに
    /// 別の枝として残す。オフに切り替えても、すでにある枝は残す (次の新しい編集で捨てるのは、現在の節のやり直しの枝だけ)。
    /// </summary>
    public bool BranchingEnabled { get; set; }

    public DocumentSnapshot Current => _path[_current].Entry.Snapshot;

    public bool CanUndo => _current > 0;

    public bool CanRedo => _current < _path.Count - 1;

    /// <summary>現在の状態が、保存した時点 (または開いた時点) の状態と違うか。</summary>
    public bool IsModified => !ReferenceEquals(_path[_current], _saved);

    /// <summary>次に Undo したときに取り消す操作の説明。</summary>
    public string? UndoDescription => CanUndo ? _path[_current].Entry.Description : null;

    public string? RedoDescription => CanRedo ? _path[_current + 1].Entry.Description : null;

    /// <summary>現在の経路の項目の数。</summary>
    public int Count => _path.Count;

    public int CurrentIndex => _current;

    /// <summary>現在の項目 (直前の編集グループ)。</summary>
    public HistoryEntry CurrentEntry => _path[_current].Entry;

    /// <summary>現在の経路のすべての項目 (0 は開いた時点。履歴パネル EDIT-20)。</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>保存した時点の項目の番号 (保存した時点が現在の経路にない場合は −1。EDIT-20 の仕様 1 の「保存」の印)。</summary>
    public int SavedIndex => _saved is { } s && IsOnPath(s) ? s.Depth : -1;

    /// <summary>木の根 (開いた時点)。</summary>
    public HistoryNode Root => _root;

    /// <summary>現在の節。</summary>
    public HistoryNode CurrentNode => _path[_current];

    /// <summary>保存した時点の節 (履歴にない場合は null)。</summary>
    public HistoryNode? SavedNode => _saved;

    /// <summary>木の節の数 (すべての枝を含む)。</summary>
    public int NodeCount => _nodeCount;

    /// <summary>現在の経路の <paramref name="index"/> 番目の節。</summary>
    public HistoryNode NodeAt(int index) => _path[index];

    /// <summary>項目が増えた・減った・現在の位置が変わった (履歴パネルの更新)。</summary>
    public event EventHandler? Changed;

    /// <summary>1 つのコマンドの編集をまとめているところか (EDIT-19 の仕様 6)。</summary>
    public bool IsInGroup => _groupDepth > 0;

    /// <summary>名前の付いた節か固定した節があるか (閉じるときの確認。EDIT-21 の仕様 10)。</summary>
    public bool HasNamedOrPinnedNodes => AllNodes().Any(n => n.Name is not null || n.IsPinned);

    /// <summary>名前の付いた節があるか (数を覚えておくので、節の数によらず一定時間。コマンドの状態に使う)。</summary>
    public bool HasNamedNodes => _namedCount > 0;

    /// <summary>木のすべての節を、深さ優先 (子は作った順) で返す。</summary>
    public IEnumerable<HistoryNode> AllNodes() => SubtreeNodes(_root);

    /// <summary>番号で節を探す (見つからなければ null)。</summary>
    public HistoryNode? FindNode(int id) => AllNodes().FirstOrDefault(n => n.Id == id);

    private HistoryNode NewNode(HistoryEntry entry, HistoryNode? parent)
    {
        _nodeCount++;
        return new HistoryNode(_nextId++, entry, parent);
    }

    /// <summary>
    /// 新しい状態を積む。Redo できる履歴は捨てる (分岐する履歴では枝として残す。EDIT-21 の仕様 1)。<paramref name="coalesceKey"/> が直前の項目と同じなら、
    /// 直前の項目を置き換えて 1 つの Undo 単位にまとめる (連続した文字入力など)。保存した時点の項目、2 秒以上
    /// 空いた入力、4 KiB を超える入力、子 (枝) を持つ項目はまとめない (EDIT-19 の仕様 5)。グループの中ではすべて 1 つにまとめる (仕様 6)。
    /// </summary>
    internal void Push(DocumentSnapshot snapshot, string description, string? coalesceKey,
        long offset = 0, long removed = 0, long inserted = 0)
    {
        HistoryNode currentNode = _path[_current];
        if (CanRedo)
        {
            if (!BranchingEnabled)
            {
                DetachSubtree(_path[_current + 1]);
            }

            _path.RemoveRange(_current + 1, _path.Count - _current - 1);
        }

        long now = _time.GetTimestamp();
        long bytes = Math.Max(removed, inserted);
        HistoryEntry last = currentNode.Entry;

        // 子 (別の枝) を持つ節の内容を変えると、その枝の親の状態が変わってしまうため、まとめない。
        bool canMerge = _current > 0 && !ReferenceEquals(currentNode, _saved) && !currentNode.HasChildren;
        if (_groupDepth > 0)
        {
            if (_groupStarted)
            {
                currentNode.Entry = last with
                {
                    Snapshot = snapshot,
                    Range = last.Range?.Then(offset, removed, inserted) ?? new EditRange(offset, removed, inserted),
                    CoalescedBytes = last.CoalescedBytes + (_groupMergeWithPrevious ? bytes : 0),
                    LastEditTimestamp = now,
                };
                return;
            }

            _groupStarted = true;
            description = _groupDescription;
            coalesceKey = _groupCoalesceKey;

            // マルチカーソルへの入力 (EDIT-08 の仕様 6): グループごと直前の入力にまとめる。バイト数は入力したバイトの合計で数える。
            if (_groupMergeWithPrevious && coalesceKey is not null && canMerge
                && last.CoalesceKey == coalesceKey
                && CoalesceInterval > TimeSpan.Zero
                && _time.GetElapsedTime(last.LastEditTimestamp, now) < CoalesceInterval
                && last.CoalescedBytes + bytes <= CoalesceLimitBytes)
            {
                currentNode.Entry = last with
                {
                    Snapshot = snapshot,
                    Range = last.Range?.Then(offset, removed, inserted) ?? new EditRange(offset, removed, inserted),
                    CoalescedBytes = last.CoalescedBytes + bytes,
                    LastEditTimestamp = now,
                };
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        else if (coalesceKey is not null && canMerge
            && last.CoalesceKey == coalesceKey
            && CoalesceInterval > TimeSpan.Zero
            && _time.GetElapsedTime(last.LastEditTimestamp, now) < CoalesceInterval)
        {
            // グループのバイト数は、まとめた後の範囲の (編集前と編集後の長い方の) 長さ。
            EditRange merged = last.Range?.Then(offset, removed, inserted) ?? new EditRange(offset, removed, inserted);
            long size = Math.Max(merged.BeforeLength, merged.AfterLength);
            if (size <= CoalesceLimitBytes)
            {
                currentNode.Entry = last with
                {
                    Snapshot = snapshot,
                    Range = merged,
                    CoalescedBytes = size,
                    LastEditTimestamp = now,
                };
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        HistoryNode node = NewNode(new HistoryEntry(snapshot, description, coalesceKey)
        {
            Range = new EditRange(offset, removed, inserted),
            CoalescedBytes = bytes,
            LastEditTimestamp = now,
            Time = _time.GetUtcNow(),
        }, currentNode);
        currentNode.AddChild(node);
        currentNode.LastChild = node;
        _path.Add(node);
        _current = _path.Count - 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>節とその子孫を木から外す。保存した時点がその中にあれば、もう戻れない。</summary>
    private void DetachSubtree(HistoryNode node)
    {
        if (_saved is { } s && node.IsAncestorOrSelfOf(s))
        {
            _saved = null;
        }

        foreach (HistoryNode n in SubtreeNodes(node))
        {
            _nodeCount--;
            if (n.Name is not null)
            {
                _namedCount--;
            }
        }

        node.Parent?.RemoveChild(node);
    }

    /// <summary>以降の入力を直前の項目とまとめないようにする (カーソル移動などで呼ぶ)。</summary>
    public void BreakCoalescing()
    {
        HistoryNode node = _path[_current];
        if (node.Entry.CoalesceKey is not null)
        {
            node.Entry = node.Entry with { CoalesceKey = null };
        }
    }

    /// <summary>
    /// グループを始める。入れ子にでき、一番外側の <see cref="EndGroup"/> で閉じる。<paramref name="coalesceKey"/> を指定すると、
    /// グループの後に続く同じ種類の入力をこのグループにまとめる (選択範囲を置き換える入力の続き)。
    /// </summary>
    internal void BeginGroup(string description, string? coalesceKey = null, bool mergeWithPrevious = false)
    {
        if (_groupDepth++ == 0)
        {
            _groupStarted = false;
            _groupDescription = description;
            _groupCoalesceKey = coalesceKey;
            _groupMergeWithPrevious = mergeWithPrevious;
        }
    }

    internal void EndGroup()
    {
        if (_groupDepth == 0)
        {
            throw new InvalidOperationException("編集グループが始まっていません。");
        }

        if (--_groupDepth == 0)
        {
            _groupStarted = false;
        }
    }

    /// <summary>取り消す項目の範囲を返して 1 つ戻る。</summary>
    internal HistoryEntry Undo()
    {
        if (!CanUndo)
        {
            throw new InvalidOperationException("元に戻せる操作がありません。");
        }

        HistoryEntry undone = _path[_current].Entry;
        _current--;
        Changed?.Invoke(this, EventArgs.Empty);
        return undone;
    }

    /// <summary><paramref name="index"/> 番目の項目に移り、その項目を返す (履歴パネルの任意の時点への移動。EDIT-20 の仕様 3)。</summary>
    internal HistoryEntry MoveTo(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _path.Count);
        _current = index;
        Changed?.Invoke(this, EventArgs.Empty);
        return _path[index].Entry;
    }

    /// <summary>
    /// 現在の経路を <paramref name="node"/> を通るものに切り替え、その節を現在にする (別の枝への移動。EDIT-21 の仕様 2)。根からその節までの
    /// 各節の「最後に通った子」をこの経路にし、その先は最後に通った子をたどる。時間は木の深さに比例し、データ量には比例しない。
    /// </summary>
    internal void SwitchTo(HistoryNode node)
    {
        RequireOwn(node);
        var toRoot = new List<HistoryNode>(node.Depth + 1);
        for (HistoryNode? n = node; n is not null; n = n.Parent)
        {
            toRoot.Add(n);
        }

        _path.Clear();
        for (int i = toRoot.Count - 1; i >= 0; i--)
        {
            _path.Add(toRoot[i]);
            if (i > 0)
            {
                toRoot[i].LastChild = toRoot[i - 1];
            }
        }

        _current = node.Depth;
        ExtendPath();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>経路の末尾から、最後に通った子をたどって経路を伸ばす。</summary>
    private void ExtendPath()
    {
        for (HistoryNode? next = _path[^1].LastChild; next is not null; next = next.LastChild)
        {
            _path.Add(next);
        }
    }

    /// <summary>やり直す項目を返して 1 つ進む。</summary>
    internal HistoryEntry Redo()
    {
        if (!CanRedo)
        {
            throw new InvalidOperationException("やり直せる操作がありません。");
        }

        _current++;
        Changed?.Invoke(this, EventArgs.Empty);
        return _path[_current].Entry;
    }

    /// <summary>現在の項目のスナップショットを、内容が同じ別のスナップショットに差し替える (保存後の元データの切り替え)。</summary>
    internal void ReplaceCurrent(DocumentSnapshot snapshot)
    {
        HistoryNode node = _path[_current];
        node.Entry = node.Entry with { Snapshot = snapshot, CoalesceKey = null };
    }

    /// <summary>現在の状態を「保存した時点」にする。</summary>
    internal void MarkSaved()
    {
        _saved = _path[_current];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>履歴を消去して <paramref name="initial"/> だけにし、その状態を「保存した時点」にする (外部で変更された元データの再読み込み。ENG-18)。</summary>
    internal void Reset(DocumentSnapshot initial)
    {
        _path.Clear();
        _nodeCount = 0;
        _namedCount = 0;
        _root = NewNode(new HistoryEntry(initial, string.Empty, null), null);
        _path.Add(_root);
        _current = 0;
        _saved = _root;
        _groupDepth = 0;
        _groupStarted = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存した時点を履歴の外にする (復旧したドキュメントは最初から「変更あり」。ENG-27 の仕様 6)。</summary>
    internal void MarkUnsaved() => _saved = null;

    // ---- 分岐する履歴の操作 (EDIT-21) ----

    /// <summary>節に名前を付ける (1〜100 文字。null か空白だけなら名前を外す。EDIT-21 の仕様 4)。</summary>
    public void SetName(HistoryNode node, string? name)
    {
        RequireOwn(node);
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is { Length: > MaxNameLength })
        {
            throw new ArgumentOutOfRangeException(nameof(name), $"名前は {MaxNameLength} 文字までです。");
        }

        _namedCount += (name is null ? 0 : 1) - (node.Name is null ? 0 : 1);
        node.Name = name;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>節を固定する・固定を外す (EDIT-21 の仕様 5)。</summary>
    public void SetPinned(HistoryNode node, bool pinned)
    {
        RequireOwn(node);
        node.IsPinned = pinned;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>名前の付いた節 (コマンドパレット「履歴の節へ移動」の候補。EDIT-21 の仕様 4)。作った順。</summary>
    public IReadOnlyList<HistoryNode> NamedNodes() => [.. AllNodes().Where(n => n.Name is not null).OrderBy(n => n.Id)];

    /// <summary>節を枝ごと削除できるか (根、現在の節とその祖先は削除できない。EDIT-21 の仕様 6)。</summary>
    public bool CanDelete(HistoryNode node) =>
        node.Parent is not null && _root.IsAncestorOrSelfOf(node) && !node.IsAncestorOrSelfOf(_path[_current]);

    /// <summary>節を枝ごと削除する (元に戻せない。EDIT-21 の仕様 6)。</summary>
    public void DeleteBranch(HistoryNode node)
    {
        if (!CanDelete(node))
        {
            throw new InvalidOperationException("現在の節とその祖先は削除できません。");
        }

        bool onPath = IsOnPath(node);
        int depth = node.Depth;
        DetachSubtree(node);
        if (onPath)
        {
            // やり直しの経路にあった: 残った子 (最後に作ったもの) をたどり直す。
            _path.RemoveRange(depth, _path.Count - depth);
            ExtendPath();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 自動の整理 (EDIT-21 の仕様 7) で消せる枝を古いものから返す。現在の経路から外れた枝のうち、固定・名前付きの節も保存した時点も含まないもの。
    /// 各要素は枝の付け根の節 (<see cref="DeleteBranch"/> に渡す)。固定した節を含む枝は、根からその節までの経路を残すため対象にしない (仕様 5)。
    /// </summary>
    public IReadOnlyList<HistoryNode> PrunableBranches()
    {
        var result = new List<HistoryNode>();
        foreach (HistoryNode node in AllNodes())
        {
            if (node.Parent is not { } parent || IsOnPath(node) || !IsOnPath(parent))
            {
                continue;
            }

            if (!SubtreeNodes(node).Any(n => n.IsPinned || n.Name is not null || ReferenceEquals(n, _saved)))
            {
                result.Add(node);
            }
        }

        return [.. result.OrderBy(n => n.Entry.Time).ThenBy(n => n.Id)];
    }

    /// <summary>節が現在の経路の上にあるか。</summary>
    public bool IsOnPath(HistoryNode node) => node.Depth < _path.Count && ReferenceEquals(_path[node.Depth], node);

    private static IEnumerable<HistoryNode> SubtreeNodes(HistoryNode node)
    {
        var stack = new Stack<HistoryNode>();
        stack.Push(node);
        var children = new List<HistoryNode>();
        while (stack.Count > 0)
        {
            HistoryNode n = stack.Pop();
            yield return n;
            children.Clear();
            children.AddRange(n.Children);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    private void RequireOwn(HistoryNode node)
    {
        if (!_root.IsAncestorOrSelfOf(node))
        {
            throw new ArgumentException("この履歴の節ではありません。", nameof(node));
        }
    }

    /// <summary>
    /// 読み込んだ木で履歴を置き換える (履歴の読み込み。EDIT-21 の仕様 9)。<paramref name="root"/> の子孫を新しい木にし、<paramref name="current"/> を
    /// 現在、<paramref name="saved"/> を保存した時点にする。番号は読み込んだ木の番号を使い、以後の番号はその続きにする。
    /// </summary>
    internal void Load(HistoryNode root, HistoryNode current, HistoryNode? saved)
    {
        _root = root;
        _saved = saved;
        _nodeCount = SubtreeNodes(root).Count();
        _namedCount = SubtreeNodes(root).Count(n => n.Name is not null);
        _nextId = SubtreeNodes(root).Max(n => n.Id) + 1;
        _groupDepth = 0;
        _groupStarted = false;
        _path.Clear();
        _path.Add(root);
        SwitchTo(current);
    }

    /// <summary>読み込み用に節を作る (<see cref="Load"/> に渡す木を組み立てる)。親の子の最後に加え、やり直しの先にする。</summary>
    internal static HistoryNode CreateLoadedNode(int id, HistoryEntry entry, HistoryNode? parent, string? name, bool pinned)
    {
        var node = new HistoryNode(id, entry, parent) { Name = name, IsPinned = pinned };
        if (parent is not null)
        {
            parent.AddChild(node);
            parent.LastChild = node;
        }

        return node;
    }

    /// <summary>現在の経路の項目を、一覧として見せる (コピーしない)。</summary>
    private sealed class PathEntries(List<HistoryNode> path) : IReadOnlyList<HistoryEntry>
    {
        public HistoryEntry this[int index] => path[index].Entry;

        public int Count => path.Count;

        public IEnumerator<HistoryEntry> GetEnumerator()
        {
            for (int i = 0; i < path.Count; i++)
            {
                yield return path[i].Entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
