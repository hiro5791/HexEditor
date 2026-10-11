using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>
/// すべて検索・件数の数え上げで見つかった一致 1 件。<see cref="Variant"/> は一致した種類 (<see cref="SearchPattern.Variants"/> の添字。
/// エンディアン「両方」の LE / BE など)。
/// </summary>
public readonly record struct SearchMatch(long Offset, long Length, int Variant = 0, int Extra = 0)
{
    public long End => Offset + Length;
}

/// <summary>すべて検索の状態 (FIND-20 の「画面」の見出し)。</summary>
public enum SearchResultsState
{
    Running,
    Completed,

    /// <summary>キャンセルされた (見つかった分は残る。FIND-02 の仕様 3)。</summary>
    Cancelled,

    /// <summary>件数の上限に達して止めた (FIND-12 の仕様 3、FIND-20 の仕様 6)。</summary>
    LimitReached,

    /// <summary>読めない範囲のため中止した、またはそのほかの失敗。</summary>
    Failed,
}

/// <summary>
/// すべて検索・件数の数え上げの結果 (FIND-12、FIND-20 の基盤)。検索のスレッドが開始オフセットの昇順に追加していき、
/// 追加のたびに <see cref="MatchesAdded"/> を出す (ストリーミング)。読み取りはどのスレッドからでもできる。
/// 一致は検索した <see cref="Snapshot"/> 上の位置で、その後の編集に合わせた位置は <see cref="MatchTracker"/> で求める (FIND-03)。
/// <para>
/// メモリ上に置くのは <see cref="MemoryLimit"/> 件までで、それを超える分は一時ファイルに書き出す (FIND-20 の仕様 7。1 件 24 バイト)。
/// 一時ファイルを作れなかった場合は、メモリ上の件数で止める (<see cref="SpillFailed"/>)。使い終わったら <see cref="Dispose"/> で一時ファイルを消す。
/// </para>
/// </summary>
public sealed class SearchResults : IDisposable
{
    /// <summary>メモリ上に置く件数の既定の上限 (FIND-20 の仕様 7)。</summary>
    public const int DefaultMemoryLimit = 1_000_000;

    /// <summary>一時ファイルの 1 件のバイト数 (オフセット 8、長さ 8、種類 4、予備 4)。</summary>
    private const int RecordSize = 24;

    /// <summary>一時ファイルにまとめて書く件数。</summary>
    private const int WriteBatch = 4096;

    private readonly object _lock = new();
    private readonly List<SearchMatch> _matches = [];
    private readonly List<UnreadableRange> _skipped = [];
    private readonly List<SearchRange> _timedOut = [];
    private long _maxLength;
    private readonly List<SearchMatch> _pending = [];
    private SafeFileHandle? _spill;
    private string? _spillPath;
    private long _spilled;
    private long _limit;
    private bool _disposed;

    public SearchResults(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options)
    {
        Snapshot = snapshot;
        Pattern = pattern;
        Options = options;
        TotalBytes = options.Scope.TotalLength(snapshot.Length);
        _limit = options.MaxMatches;
    }

    /// <summary>検索したスナップショット (一致の版。FIND-03 の仕様 2)。</summary>
    public DocumentSnapshot Snapshot { get; }

    public SearchPattern Pattern { get; }

    public SearchOptions Options { get; }

    /// <summary>
    /// 結果が古いか (FIND-03 の「エラー」): 外部変更の検知 (ENG-19) などで元データが読み直され、<paramref name="current"/> が
    /// 検索したスナップショットと別の元データを指している。古い結果の行は「古い結果」と表示し、再検索ボタンを出す。
    /// </summary>
    public bool IsStale(DocumentSnapshot current) => !ReferenceEquals(Snapshot.Storage, current.Storage);

    /// <summary>検索範囲の合計のバイト数。</summary>
    public long TotalBytes { get; }

    public SearchResultsState State { get; private set; } = SearchResultsState.Running;

    /// <summary>上限に達して止めた (「100 万件以上」の表示)。</summary>
    public bool LimitReached => State == SearchResultsState.LimitReached;

    /// <summary>件数の今の上限 (「続ける」で 2 倍になる。FIND-20 の仕様 6)。</summary>
    public long Limit
    {
        get
        {
            lock (_lock)
            {
                return _limit;
            }
        }
    }

    /// <summary>メモリ上に置く件数の上限。超える分は一時ファイルに書く (テストでは小さくできる)。</summary>
    public int MemoryLimit { get; init; } = DefaultMemoryLimit;

    /// <summary>一時ファイルを置くフォルダ (null なら %TEMP%\HexEditor\search)。</summary>
    public string? SpillDirectory { get; init; }

    /// <summary>一時ファイルを作れなかったため、メモリ上の件数で止めた (FIND-20 の「エラー」)。理由は <see cref="SpillError"/>。</summary>
    public bool SpillFailed { get; private set; }

    public string? SpillError { get; private set; }

    /// <summary>一時ファイルに書き出した件数。</summary>
    public long SpilledCount
    {
        get
        {
            lock (_lock)
            {
                return _spilled + _pending.Count;
            }
        }
    }

    /// <summary>これまでに見つかった件数。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return (int)Math.Min(int.MaxValue, TotalCountUnlocked);
            }
        }
    }

    /// <summary>これまでに見つかった件数 (long)。</summary>
    public long LongCount
    {
        get
        {
            lock (_lock)
            {
                return TotalCountUnlocked;
            }
        }
    }

    private long TotalCountUnlocked => _matches.Count + _spilled + _pending.Count;

    /// <summary>これまでに見つかった一致 (開始オフセットの昇順) の写し。件数が多い場合は <see cref="GetRange"/> を使う。</summary>
    public IReadOnlyList<SearchMatch> Matches => GetRange(0, (int)Math.Min(int.MaxValue, LongCount));

    /// <summary>飛ばした読めない範囲 (結果一覧の「読み込めなかった範囲」。FIND-01 の「エラー」)。</summary>
    public IReadOnlyList<UnreadableRange> SkippedRanges
    {
        get
        {
            lock (_lock)
            {
                return [.. _skipped];
            }
        }
    }

    /// <summary>正規表現の時間の上限に達して飛ばしたチャンク (結果一覧に記録する。FIND-18 の「エラー」)。</summary>
    public IReadOnlyList<SearchRange> TimedOutRanges
    {
        get
        {
            lock (_lock)
            {
                return [.. _timedOut];
            }
        }
    }

    /// <summary>
    /// 種類ごとの文字コード (文字列の抽出 (FIND-32) の「文字列」の列。種類の番号の順)。null なら一覧のテキストの列は表示中の文字コード。
    /// </summary>
    public IReadOnlyList<System.Text.Encoding>? VariantEncodings { get; init; }

    /// <summary>
    /// 表示中の一致の強調に、検索語を照合し直さず結果そのものを使う (一致しない箇所の検索、文字列の抽出)。
    /// </summary>
    public bool HighlightFromResults { get; init; }

    /// <summary>種類の列の名前 (null ならパターンの <see cref="SearchPattern.Variants"/>)。</summary>
    public IReadOnlyList<string>? VariantNames { get; init; }

    /// <summary>種類の列の見出し (null ならパターンのもの)。</summary>
    public VariantColumn? VariantColumnOverride { get; init; }

    /// <summary>
    /// 検索エンジンの代わりにすべて検索を行う処理 (一致しない箇所 (FIND-25)・文字列の抽出 (FIND-32))。引数は結果、続きを探す開始位置
    /// (最初からなら long.MinValue)、長時間処理、キャンセル。件数の上限で止めたら true を返す。
    /// </summary>
    public Func<SearchResults, long, Operations.LongRunningOperation?, CancellationToken, bool>? CustomFindAll { get; init; }

    /// <summary>同じ条件で、別のスナップショットを探す新しい結果 (再検索。FIND-03 の「エラー」の再検索ボタン)。</summary>
    public SearchResults Renew(DocumentSnapshot snapshot) => new(snapshot, Pattern, Options)
    {
        VariantEncodings = VariantEncodings,
        HighlightFromResults = HighlightFromResults,
        VariantNames = VariantNames,
        VariantColumnOverride = VariantColumnOverride,
        CustomFindAll = CustomFindAll,
        MemoryLimit = MemoryLimit,
        SpillDirectory = SpillDirectory,
    };

    /// <summary>これまでに見つかった一致の最長の長さ (表示中の範囲と重なる一致を探すときに使う)。</summary>
    public long MaxLength
    {
        get
        {
            lock (_lock)
            {
                return _maxLength;
            }
        }
    }

    /// <summary>一致が追加された (検索のスレッドで呼ぶ)。引数は追加後の件数。</summary>
    public event EventHandler<int>? MatchesAdded;

    /// <summary>状態が変わった (完了・キャンセル・上限・失敗)。</summary>
    public event EventHandler? StateChanged;

    /// <summary><paramref name="index"/> 番目の一致 (0 から)。</summary>
    public SearchMatch this[int index] => this[(long)index];

    /// <summary><paramref name="index"/> 番目の一致 (0 から)。</summary>
    public SearchMatch this[long index]
    {
        get
        {
            lock (_lock)
            {
                return At(index);
            }
        }
    }

    /// <summary>[start, start + count) 番目の一致の写し。</summary>
    public IReadOnlyList<SearchMatch> GetRange(long start, int count)
    {
        lock (_lock)
        {
            long total = TotalCountUnlocked;
            long end = Math.Min(total, start + Math.Max(0, count));
            var result = new List<SearchMatch>((int)Math.Max(0, end - start));
            for (long i = Math.Max(0, start); i < end; i++)
            {
                if (i < _matches.Count)
                {
                    result.Add(_matches[(int)i]);
                    continue;
                }

                // 一時ファイルの部分はまとめて読む。
                long fileIndex = i - _matches.Count;
                if (fileIndex < _spilled)
                {
                    int n = (int)Math.Min(end - i, _spilled - fileIndex);
                    ReadSpilled(fileIndex, n, result);
                    i += n - 1;
                    continue;
                }

                result.Add(_pending[(int)(fileIndex - _spilled)]);
            }

            return result;
        }
    }

    /// <summary>
    /// 開始が <paramref name="offset"/> の一致の番号 (0 から)。なければ −1。「3 / 125」の 3 は、これ + 1 (FIND-12 の仕様 1)。
    /// </summary>
    public int IndexOf(long offset)
    {
        lock (_lock)
        {
            long i = LowerBound(offset);
            return i < TotalCountUnlocked && At(i).Offset == offset ? (int)i : -1;
        }
    }

    /// <summary>開始が <paramref name="offset"/> 未満の一致の件数。</summary>
    public int CountBefore(long offset)
    {
        lock (_lock)
        {
            return (int)LowerBound(offset);
        }
    }

    /// <summary>[offset, offset + length) と重なる一致 (表示中の範囲の強調用)。</summary>
    public IReadOnlyList<SearchMatch> Overlapping(long offset, long length)
    {
        lock (_lock)
        {
            // 開始が offset − 最長の一致 + 1 以上のものだけが重なりうる。
            long i = LowerBound(offset - Math.Max(Pattern.MaxMatchLength, _maxLength) + 1);
            var result = new List<SearchMatch>();
            long total = TotalCountUnlocked;
            for (; i < total; i++)
            {
                SearchMatch m = At(i);
                if (m.Offset >= offset + length)
                {
                    break;
                }

                if (m.End > offset)
                {
                    result.Add(m);
                }
            }

            return result;
        }
    }

    /// <summary>上限を 2 倍にする (「続ける」。FIND-20 の仕様 6)。続きを探す開始位置を返す (一致がなければ null)。</summary>
    internal long? PrepareContinue()
    {
        lock (_lock)
        {
            _limit = _limit >= long.MaxValue / 2 ? long.MaxValue : _limit * 2;
            State = SearchResultsState.Running;
            long total = TotalCountUnlocked;
            if (total == 0)
            {
                return null;
            }

            SearchMatch last = At(total - 1);
            return Options.IncludeOverlapping ? last.Offset + 1 : last.End;
        }
    }

    internal void Add(List<SearchMatch> matches)
    {
        if (matches.Count == 0)
        {
            return;
        }

        int count;
        lock (_lock)
        {
            foreach (SearchMatch m in matches)
            {
                _maxLength = Math.Max(_maxLength, m.Length);
                if (_spilled == 0 && _pending.Count == 0 && _matches.Count < MemoryLimit)
                {
                    _matches.Add(m);
                    continue;
                }

                if (SpillFailed || (_spill is null && !OpenSpill()))
                {
                    break;
                }

                _pending.Add(m);
                if (_pending.Count >= WriteBatch)
                {
                    FlushPending();
                }
            }

            count = (int)Math.Min(int.MaxValue, TotalCountUnlocked);
        }

        MatchesAdded?.Invoke(this, count);
    }

    /// <summary>上限 (または一時ファイルの失敗) で、これ以上加えられないか。</summary>
    internal bool IsFull(long count) => count >= Limit || SpillFailed;

    internal void AddTimedOut(SearchRange range)
    {
        lock (_lock)
        {
            _timedOut.Add(range);
        }
    }

    /// <summary>一致を加える (検索エンジンの外で一致を作る処理: 一致しない箇所のすべて検索、文字列の抽出)。</summary>
    internal void AddMatches(List<SearchMatch> matches) => Add(matches);

    internal void AddSkipped(UnreadableRange range)
    {
        lock (_lock)
        {
            if (_skipped.Count > 0 && _skipped[^1].End == range.Offset && _skipped[^1].Reason == range.Reason)
            {
                _skipped[^1] = _skipped[^1] with { Length = _skipped[^1].Length + range.Length };
            }
            else
            {
                _skipped.Add(range);
            }
        }
    }

    /// <summary>
    /// 探さないまま終わった結果を「中断」にする (「開いているすべてのドキュメント」のすべて検索を途中でキャンセルし、まだ探していない
    /// ドキュメントがあるとき)。探し終えた結果は変えない。
    /// </summary>
    public void SetCancelled()
    {
        if (State == SearchResultsState.Running)
        {
            SetState(SearchResultsState.Cancelled);
        }
    }

    internal void SetState(SearchResultsState state)
    {
        lock (_lock)
        {
            if (state != SearchResultsState.Running && _pending.Count > 0 && !SpillFailed)
            {
                FlushPending();
            }

            if (SpillFailed && state == SearchResultsState.Completed)
            {
                state = SearchResultsState.LimitReached;
            }
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _spill?.Dispose();
            _spill = null;
            TryDelete(_spillPath);
        }
    }

    private SearchMatch At(long index)
    {
        if (index < _matches.Count)
        {
            return _matches[(int)index];
        }

        long fileIndex = index - _matches.Count;
        if (fileIndex < _spilled)
        {
            var one = new List<SearchMatch>(1);
            ReadSpilled(fileIndex, 1, one);
            return one[0];
        }

        return _pending[(int)(fileIndex - _spilled)];
    }

    private void ReadSpilled(long fileIndex, int count, List<SearchMatch> sink)
    {
        byte[] buffer = new byte[Math.Min(count, WriteBatch) * RecordSize];
        long at = fileIndex;
        int left = count;
        while (left > 0)
        {
            int n = Math.Min(left, WriteBatch);
            Span<byte> span = buffer.AsSpan(0, n * RecordSize);
            int read = 0;
            while (read < span.Length)
            {
                int r = RandomAccess.Read(_spill!, span[read..], (at * RecordSize) + read);
                if (r <= 0)
                {
                    throw new IOException("検索結果の一時ファイルを読めません。");
                }

                read += r;
            }

            for (int k = 0; k < n; k++)
            {
                ReadOnlySpan<byte> rec = span.Slice(k * RecordSize, RecordSize);
                sink.Add(new SearchMatch(
                    BinaryPrimitives.ReadInt64LittleEndian(rec),
                    BinaryPrimitives.ReadInt64LittleEndian(rec[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(rec[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(rec[20..])));
            }

            at += n;
            left -= n;
        }
    }

    /// <summary>まだ書いていない一致を一時ファイルに書く。失敗したら一時ファイルの分を捨て、メモリ上の件数で止める。</summary>
    private void FlushPending()
    {
        if (_pending.Count == 0 || _disposed)
        {
            return;
        }

        try
        {
            if (_spill is null && !OpenSpill())
            {
                return;
            }

            byte[] buffer = new byte[_pending.Count * RecordSize];
            for (int k = 0; k < _pending.Count; k++)
            {
                Span<byte> rec = buffer.AsSpan(k * RecordSize, RecordSize);
                BinaryPrimitives.WriteInt64LittleEndian(rec, _pending[k].Offset);
                BinaryPrimitives.WriteInt64LittleEndian(rec[8..], _pending[k].Length);
                BinaryPrimitives.WriteInt32LittleEndian(rec[16..], _pending[k].Variant);
                BinaryPrimitives.WriteInt32LittleEndian(rec[20..], _pending[k].Extra);
            }

            RandomAccess.Write(_spill!, buffer, _spilled * RecordSize);
            _spilled += _pending.Count;
            _pending.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SpillFailed = true;
            SpillError = ex.Message;
            _pending.Clear();
            _spill?.Dispose();
            _spill = null;
            TryDelete(_spillPath);
            _spilled = 0;
        }
    }

    /// <summary>一時ファイルを作る。作れなければ <see cref="SpillFailed"/> にして false。</summary>
    private bool OpenSpill()
    {
        try
        {
            string folder = SpillDirectory ?? Path.Combine(Path.GetTempPath(), "HexEditor", "search");
            Directory.CreateDirectory(folder);
            _spillPath = Path.Combine(folder, $"results-{Guid.NewGuid():N}.bin");
            _spill = File.OpenHandle(_spillPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SpillFailed = true;
            SpillError = ex.Message;
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private long LowerBound(long offset)
    {
        long lo = 0;
        long hi = TotalCountUnlocked;
        while (lo < hi)
        {
            long mid = (lo + hi) >>> 1;
            if (At(mid).Offset < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
