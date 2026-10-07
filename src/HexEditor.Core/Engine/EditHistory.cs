namespace HexEditor.Core.Engine;

/// <summary>履歴の 1 項目: ある時点のスナップショットと、そこに至った操作の説明。</summary>
public sealed record HistoryEntry(DocumentSnapshot Snapshot, string Description, string? CoalesceKey);

/// <summary>
/// Undo / Redo の履歴 (ENG-05 の仕様 3・6)。各時点をスナップショットで持つため、Undo・Redo は参照の切り替えだけで済む。
/// </summary>
public sealed class EditHistory
{
    private readonly List<HistoryEntry> _entries = [];
    private int _current;
    private int _savedIndex;

    internal EditHistory(DocumentSnapshot initial)
    {
        _entries.Add(new HistoryEntry(initial, string.Empty, null));
    }

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

    /// <summary>
    /// 新しい状態を積む。Redo できる履歴は捨てる。<paramref name="coalesceKey"/> が直前の項目と同じなら、
    /// 直前の項目を置き換えて 1 つの Undo 単位にまとめる (連続した文字入力など)。保存した時点の項目はまとめない。
    /// </summary>
    internal void Push(DocumentSnapshot snapshot, string description, string? coalesceKey)
    {
        if (CanRedo)
        {
            _entries.RemoveRange(_current + 1, _entries.Count - _current - 1);
            if (_savedIndex > _current)
            {
                _savedIndex = -1;
            }
        }

        if (coalesceKey is not null && _current > 0 && _current != _savedIndex
            && _entries[_current].CoalesceKey == coalesceKey)
        {
            _entries[_current] = new HistoryEntry(snapshot, _entries[_current].Description, coalesceKey);
            return;
        }

        _entries.Add(new HistoryEntry(snapshot, description, coalesceKey));
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

    internal DocumentSnapshot Undo()
    {
        if (!CanUndo)
        {
            throw new InvalidOperationException("元に戻せる操作がありません。");
        }

        _current--;
        return Current;
    }

    internal DocumentSnapshot Redo()
    {
        if (!CanRedo)
        {
            throw new InvalidOperationException("やり直せる操作がありません。");
        }

        _current++;
        return Current;
    }

    /// <summary>現在の状態を「保存した時点」にする。</summary>
    internal void MarkSaved() => _savedIndex = _current;
}
