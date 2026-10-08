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
}

/// <summary>
/// Undo / Redo の履歴 (ENG-05 の仕様 3・6、EDIT-19)。各時点をスナップショットで持つため、Undo・Redo は参照の切り替えだけで済む。
/// </summary>
public sealed class EditHistory
{
    /// <summary>入力をまとめる間隔の既定値 (EDIT-19 の仕様 5)。</summary>
    public static readonly TimeSpan DefaultCoalesceInterval = TimeSpan.FromSeconds(2);

    /// <summary>1 つの編集グループにまとめる入力の上限 (EDIT-19 の仕様 5)。</summary>
    public const long CoalesceLimitBytes = 4 * 1024;

    private readonly List<HistoryEntry> _entries = [];
    private readonly TimeProvider _time;
    private int _current;
    private int _savedIndex;
    private int _groupDepth;
    private bool _groupStarted;
    private string _groupDescription = string.Empty;
    private string? _groupCoalesceKey;

    internal EditHistory(DocumentSnapshot initial, TimeProvider? time = null, TimeSpan? coalesceInterval = null)
    {
        _time = time ?? TimeProvider.System;
        CoalesceInterval = coalesceInterval ?? DefaultCoalesceInterval;
        _entries.Add(new HistoryEntry(initial, string.Empty, null));
    }

    /// <summary>連続した入力をまとめる最大の間隔 (設定で 0〜10 秒。0 はまとめない)。</summary>
    public TimeSpan CoalesceInterval { get; set; }

    public DocumentSnapshot Current => _entries[_current].Snapshot;

    public bool CanUndo => _current > 0;

    public bool CanRedo => _current < _entries.Count - 1;

    /// <summary>現在の状態が、保存した時点 (または開いた時点) の状態と違うか。</summary>
    public bool IsModified => _current != _savedIndex;

    /// <summary>次に Undo したときに取り消す操作の説明。</summary>
    public string? UndoDescription => CanUndo ? _entries[_current].Description : null;

    public string? RedoDescription => CanRedo ? _entries[_current + 1].Description : null;

    public int Count => _entries.Count;

    public int CurrentIndex => _current;

    /// <summary>現在の項目 (直前の編集グループ)。</summary>
    public HistoryEntry CurrentEntry => _entries[_current];

    /// <summary>1 つのコマンドの編集をまとめているところか (EDIT-19 の仕様 6)。</summary>
    public bool IsInGroup => _groupDepth > 0;

    /// <summary>
    /// 新しい状態を積む。Redo できる履歴は捨てる。<paramref name="coalesceKey"/> が直前の項目と同じなら、
    /// 直前の項目を置き換えて 1 つの Undo 単位にまとめる (連続した文字入力など)。保存した時点の項目、2 秒以上
    /// 空いた入力、4 KiB を超える入力はまとめない (EDIT-19 の仕様 5)。グループの中ではすべて 1 つにまとめる (仕様 6)。
    /// </summary>
    internal void Push(DocumentSnapshot snapshot, string description, string? coalesceKey,
        long offset = 0, long removed = 0, long inserted = 0)
    {
        if (CanRedo)
        {
            _entries.RemoveRange(_current + 1, _entries.Count - _current - 1);
            if (_savedIndex > _current)
            {
                _savedIndex = -1;
            }
        }

        long now = _time.GetTimestamp();
        long bytes = Math.Max(removed, inserted);
        HistoryEntry last = _entries[_current];
        if (_groupDepth > 0)
        {
            if (_groupStarted)
            {
                _entries[_current] = last with
                {
                    Snapshot = snapshot,
                    Range = last.Range?.Then(offset, removed, inserted) ?? new EditRange(offset, removed, inserted),
                    LastEditTimestamp = now,
                };
                return;
            }

            _groupStarted = true;
            description = _groupDescription;
            coalesceKey = _groupCoalesceKey;
        }
        else if (coalesceKey is not null && _current > 0 && _current != _savedIndex
            && last.CoalesceKey == coalesceKey
            && CoalesceInterval > TimeSpan.Zero
            && _time.GetElapsedTime(last.LastEditTimestamp, now) < CoalesceInterval)
        {
            // グループのバイト数は、まとめた後の範囲の (編集前と編集後の長い方の) 長さ。
            EditRange merged = last.Range?.Then(offset, removed, inserted) ?? new EditRange(offset, removed, inserted);
            long size = Math.Max(merged.BeforeLength, merged.AfterLength);
            if (size <= CoalesceLimitBytes)
            {
                _entries[_current] = last with
                {
                    Snapshot = snapshot,
                    Range = merged,
                    CoalescedBytes = size,
                    LastEditTimestamp = now,
                };
                return;
            }
        }

        _entries.Add(new HistoryEntry(snapshot, description, coalesceKey)
        {
            Range = new EditRange(offset, removed, inserted),
            CoalescedBytes = bytes,
            LastEditTimestamp = now,
        });
        _current = _entries.Count - 1;
    }

    /// <summary>以降の入力を直前の項目とまとめないようにする (カーソル移動などで呼ぶ)。</summary>
    public void BreakCoalescing()
    {
        HistoryEntry entry = _entries[_current];
        if (entry.CoalesceKey is not null)
        {
            _entries[_current] = entry with { CoalesceKey = null };
        }
    }

    /// <summary>
    /// グループを始める。入れ子にでき、一番外側の <see cref="EndGroup"/> で閉じる。<paramref name="coalesceKey"/> を指定すると、
    /// グループの後に続く同じ種類の入力をこのグループにまとめる (選択範囲を置き換える入力の続き)。
    /// </summary>
    internal void BeginGroup(string description, string? coalesceKey = null)
    {
        if (_groupDepth++ == 0)
        {
            _groupStarted = false;
            _groupDescription = description;
            _groupCoalesceKey = coalesceKey;
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

        HistoryEntry undone = _entries[_current];
        _current--;
        return undone;
    }

    /// <summary>やり直す項目を返して 1 つ進む。</summary>
    internal HistoryEntry Redo()
    {
        if (!CanRedo)
        {
            throw new InvalidOperationException("やり直せる操作がありません。");
        }

        _current++;
        return _entries[_current];
    }

    /// <summary>現在の項目のスナップショットを、内容が同じ別のスナップショットに差し替える (保存後の元データの切り替え)。</summary>
    internal void ReplaceCurrent(DocumentSnapshot snapshot) =>
        _entries[_current] = _entries[_current] with { Snapshot = snapshot, CoalesceKey = null };

    /// <summary>現在の状態を「保存した時点」にする。</summary>
    internal void MarkSaved() => _savedIndex = _current;

    /// <summary>履歴を消去して <paramref name="initial"/> だけにし、その状態を「保存した時点」にする (外部で変更された元データの再読み込み。ENG-18)。</summary>
    internal void Reset(DocumentSnapshot initial)
    {
        _entries.Clear();
        _entries.Add(new HistoryEntry(initial, string.Empty, null));
        _current = 0;
        _savedIndex = 0;
        _groupDepth = 0;
        _groupStarted = false;
    }

    /// <summary>保存した時点を履歴の外にする (復旧したドキュメントは最初から「変更あり」。ENG-27 の仕様 6)。</summary>
    internal void MarkUnsaved() => _savedIndex = -1;
}
