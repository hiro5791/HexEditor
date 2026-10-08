namespace HexEditor.Core.Tabs;

/// <summary>
/// タブを最近使った順 (MRU) に並べる (UI-09 の仕様 6)。Ctrl+Tab は Ctrl を押している間、候補の一覧の中を順に進み、Ctrl を離したときに
/// 選んだタブを先頭にする。<typeparamref name="T"/> はタブ (参照で比べる)。
/// </summary>
public sealed class TabMru<T>
    where T : class
{
    private readonly List<T> _order = [];
    private List<T>? _cycle;
    private int _cycleIndex;

    /// <summary>最近使った順 (先頭が今のタブ)。</summary>
    public IReadOnlyList<T> Order => _order;

    /// <summary>Ctrl+Tab の切り替えの途中か (候補の一覧を出している)。</summary>
    public bool IsCycling => _cycle is not null;

    /// <summary>切り替えの候補 (切り替えを始めたときの最近使った順)。切り替え中でなければ空。</summary>
    public IReadOnlyList<T> Candidates => _cycle ?? [];

    /// <summary>切り替えの候補のうち今選んでいるものの位置。</summary>
    public int CandidateIndex => _cycleIndex;

    /// <summary>タブを使った (アクティブにした)。切り替えの途中は並びを変えない (Ctrl を離したときに決める)。</summary>
    public void Touch(T item)
    {
        if (_cycle is not null)
        {
            return;
        }

        _order.Remove(item);
        _order.Insert(0, item);
    }

    /// <summary>タブを閉じた・別のウィンドウに移した。</summary>
    public void Remove(T item)
    {
        _order.Remove(item);
        if (_cycle is not null && _cycle.IndexOf(item) is int index and >= 0)
        {
            _cycle.RemoveAt(index);
            if (_cycle.Count == 0)
            {
                _cycle = null;
            }
            else if (_cycleIndex >= _cycle.Count || index < _cycleIndex)
            {
                _cycleIndex = Math.Max(0, Math.Min(_cycleIndex - (index < _cycleIndex ? 1 : 0), _cycle.Count - 1));
            }
        }
    }

    /// <summary>
    /// Ctrl+Tab (<paramref name="forward"/>) / Ctrl+Shift+Tab: 切り替えの次の候補を返す。最初の押下では直前に使っていたタブ。
    /// <paramref name="open"/> は今開いているタブ (記録にないタブは末尾に足す)。候補がなければ null。
    /// </summary>
    public T? Step(IReadOnlyList<T> open, bool forward)
    {
        if (_cycle is null)
        {
            foreach (T item in open)
            {
                if (!_order.Contains(item))
                {
                    _order.Add(item);
                }
            }

            _order.RemoveAll(i => !open.Contains(i));
            if (_order.Count < 2)
            {
                return null;
            }

            _cycle = [.. _order];
            _cycleIndex = 0;
        }

        _cycleIndex = ((_cycleIndex + (forward ? 1 : -1)) % _cycle.Count + _cycle.Count) % _cycle.Count;
        return _cycle[_cycleIndex];
    }

    /// <summary>Ctrl を離した: 選んでいたタブを先頭にして切り替えを終える。選んでいたタブを返す (切り替え中でなければ null)。</summary>
    public T? Commit()
    {
        if (_cycle is null)
        {
            return null;
        }

        T chosen = _cycle[_cycleIndex];
        _cycle = null;
        Touch(chosen);
        return chosen;
    }
}
