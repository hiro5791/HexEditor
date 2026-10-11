namespace HexEditor.Core.View;

/// <summary>同期スクロールのモード (VIEW-39 の仕様 2、VIEW-37 の仕様 8)。</summary>
public enum SyncMode
{
    /// <summary>同期しない。</summary>
    Off,

    /// <summary>一方の一番上の行の先頭オフセットとカーソルのオフセットを、ほかのビューにも同じ値で設定する (VIEW-39 の既定)。</summary>
    SameOffset,

    /// <summary>同期を有効にした時点の各ビューのオフセットの差を保って動かす。</summary>
    KeepDifference,

    /// <summary>
    /// 比較 (ANA) の対応位置に合わせて動かす (<see cref="IOffsetMapper"/> が必要。比較の結果がある場合だけ選べる)。
    /// </summary>
    Mapped,

    /// <summary>スクロール量 (行数) だけを伝える (分割したペインの「スクロールを同期」。VIEW-37 の仕様 8)。カーソルは同期しない。</summary>
    ScrollRows,
}

/// <summary>
/// 比較の対応位置 (ANA-04 が実装する)。<paramref name="from"/> 番目のビューのオフセットを、<paramref name="to"/> 番目のビューの対応する
/// オフセットにする。対応がなければ最も近い位置を返す。
/// </summary>
public interface IOffsetMapper
{
    long Map(long offset, int from, int to);
}

/// <summary>
/// 複数のビュー (<see cref="EditorState"/>) のスクロール・カーソル移動・ジャンプを同期する (VIEW-37 の仕様 8、VIEW-39)。UI に依存しない。
/// どのビューを動かしても、ほかのビューを同じ規則で動かす (選んだ 1 つが主になるのではない)。並列表示 (VIEW-39) と比較 (ANA-04) の
/// 同期スクロールの共通の部品。
/// <para>
/// 使い方: <c>new ViewSync(members, mode)</c> で作り、要らなくなったら <see cref="Dispose"/> する (ビューの <see cref="EditorState.Changed"/>
/// の購読を外す)。<see cref="Mode"/> を変えると、その時点の位置を基準にする (「位置の差を保つ」の差はこの時点の差)。
/// 比較に従う同期は <see cref="Mapper"/> を設定して <see cref="SyncMode.Mapped"/> にする。
/// </para>
/// </summary>
public sealed class ViewSync : IDisposable
{
    private readonly List<EditorState> _members = [];
    private readonly Dictionary<EditorState, (long Top, long TopRow, long Cursor, long SelStart, long SelLength)> _last = [];
    private readonly Dictionary<EditorState, (long Top, long Cursor)> _base = [];
    private readonly Dictionary<EditorState, long> _rowBase = [];
    private SyncMode _mode;
    private bool _applying;

    public ViewSync(IEnumerable<EditorState> members, SyncMode mode = SyncMode.SameOffset)
    {
        foreach (EditorState member in members)
        {
            Add(member);
        }

        Mode = mode;
    }

    /// <summary>同期しているビュー (並べた順)。</summary>
    public IReadOnlyList<EditorState> Members => _members;

    /// <summary>同期のモード。変えた時点の位置を基準にする。</summary>
    public SyncMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Rebase();
        }
    }

    /// <summary>選択範囲も同期する (VIEW-39 の仕様 3。既定オフ)。</summary>
    public bool SyncSelection { get; set; }

    /// <summary>比較の対応位置 (<see cref="SyncMode.Mapped"/> で使う)。</summary>
    public IOffsetMapper? Mapper { get; set; }

    /// <summary>同期が働いているか (ステータスバーの「同期」。VIEW-39 の仕様 6)。</summary>
    public bool IsActive => _mode != SyncMode.Off && _members.Count > 1;

    /// <summary>ほかのビューを動かした (同期した) あと。表示側の描き直しなどに使う。</summary>
    public event EventHandler? Synced;

    public void Add(EditorState member)
    {
        if (_members.Contains(member))
        {
            return;
        }

        _members.Add(member);
        member.Changed += Member_Changed;
        Remember(member);
        Rebase();
    }

    public void Remove(EditorState member)
    {
        if (_members.Remove(member))
        {
            member.Changed -= Member_Changed;
            _last.Remove(member);
            _base.Remove(member);
            _rowBase.Remove(member);
        }
    }

    public void Dispose()
    {
        foreach (EditorState member in _members)
        {
            member.Changed -= Member_Changed;
        }

        _members.Clear();
        _last.Clear();
        _base.Clear();
        _rowBase.Clear();
    }

    /// <summary>今の位置を基準にする (「位置の差を保つ」「スクロール量」の差を取り直す)。</summary>
    public void Rebase()
    {
        _base.Clear();
        _rowBase.Clear();
        foreach (EditorState m in _members)
        {
            _base[m] = (m.TopOffset, m.Cursor);
            _rowBase[m] = m.TopRow;
            Remember(m);
        }
    }

    /// <summary>
    /// <paramref name="leader"/> の位置に、ほかのビューを合わせる (同期を有効にした直後など)。「同じオフセット」では、並べた時点で
    /// ほかのビューを同じ位置にそろえる。
    /// </summary>
    public void SyncFrom(EditorState leader)
    {
        if (_members.Contains(leader))
        {
            Propagate(leader, cursorMoved: true, scrolled: true);
        }
    }

    private void Remember(EditorState m) =>
        _last[m] = (m.TopOffset, m.TopRow, m.Cursor, m.SelectionStart, m.SelectionLength);

    private void Member_Changed(object? sender, EventArgs e)
    {
        if (_applying || sender is not EditorState leader || !IsActive)
        {
            if (sender is EditorState m && !_applying)
            {
                Remember(m);
            }

            return;
        }

        (long top, long topRow, long cursor, long selStart, long selLength) = _last.TryGetValue(leader, out var last)
            ? last
            : (leader.TopOffset, leader.TopRow, leader.Cursor, leader.SelectionStart, leader.SelectionLength);
        bool scrolled = top != leader.TopOffset || topRow != leader.TopRow;
        bool cursorMoved = cursor != leader.Cursor;
        bool selection = SyncSelection && (selStart != leader.SelectionStart || selLength != leader.SelectionLength);
        Remember(leader);
        if (scrolled || cursorMoved || selection)
        {
            Propagate(leader, cursorMoved || selection, scrolled);
        }
    }

    private void Propagate(EditorState leader, bool cursorMoved, bool scrolled)
    {
        _applying = true;
        try
        {
            int from = _members.IndexOf(leader);
            for (int i = 0; i < _members.Count; i++)
            {
                EditorState other = _members[i];
                if (ReferenceEquals(other, leader))
                {
                    continue;
                }

                switch (_mode)
                {
                    case SyncMode.ScrollRows:
                        if (scrolled && _rowBase.TryGetValue(leader, out long leaderBase) && _rowBase.TryGetValue(other, out long otherBase))
                        {
                            other.ScrollToRow(otherBase + (leader.TopRow - leaderBase));
                        }

                        break;
                    case SyncMode.KeepDifference:
                        (long lTop, long lCursor) = _base[leader];
                        (long oTop, long oCursor) = _base[other];
                        other.SyncTo(Math.Max(0, leader.TopOffset - lTop + oTop), Math.Max(0, leader.Cursor - lCursor + oCursor),
                            Selection(leader, oCursor - lCursor));
                        break;
                    case SyncMode.Mapped when Mapper is { } mapper:
                        long mappedCursor = mapper.Map(leader.Cursor, from, i);
                        other.SyncTo(mapper.Map(leader.TopOffset, from, i), mappedCursor,
                            Selection(leader, mappedCursor - leader.Cursor));
                        break;
                    default:
                        other.SyncTo(leader.TopOffset, leader.Cursor, Selection(leader, 0));
                        break;
                }

                Remember(other);
            }
        }
        finally
        {
            _applying = false;
        }

        Synced?.Invoke(this, EventArgs.Empty);
    }

    private (long, long)? Selection(EditorState leader, long delta) =>
        SyncSelection && leader.HasSelection ? (Math.Max(0, leader.SelectionStart + delta), leader.SelectionLength) : null;
}
