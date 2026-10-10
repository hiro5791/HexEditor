namespace HexEditor.Core.Compare;

/// <summary>比較の状態。</summary>
public enum CompareState
{
    Running,
    Completed,

    /// <summary>キャンセルした (<see cref="CompareResult.StoppedAt"/> まで比較した結果が残る。ANA-02 の「巨大ファイル」)。</summary>
    Cancelled,

    /// <summary>一時ファイルに書けないなどで中止した (それまでの結果は残る。ANA-02 の「エラー」)。</summary>
    Failed,
}

/// <summary>
/// 比較の結果 (ANA-02 の仕様 6、ANA-06 の仕様 6)。比較の処理が書き込み、画面は比較中も読む (見つかった差分をすぐ表示する)。
/// </summary>
public sealed class CompareResult : IDisposable
{
    private readonly object _lock = new();
    private readonly long[] _kindCounts = new long[4];
    private long _differentBytes;
    private long _matchedBytes;
    private long _leftPosition;
    private long _rightPosition;

    public CompareResult(CompareMethod method, CompareRange left, CompareRange right, DiffStore? store = null)
    {
        Method = method;
        Left = left;
        Right = right;
        Diffs = store ?? new DiffStore();
    }

    public CompareMethod Method { get; }

    public CompareRange Left { get; }

    public CompareRange Right { get; }

    public DiffStore Diffs { get; }

    public CompareState State { get; internal set; } = CompareState.Running;

    /// <summary>失敗の理由 (<see cref="CompareState.Failed"/> のとき)。</summary>
    public Exception? Error { get; internal set; }

    /// <summary>比較にかかった時間 (終わったとき)。</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>打ち切ったウィンドウの数 (ANA-03 の「巨大ファイル」)。</summary>
    public int AbortedWindows { get; internal set; }

    /// <summary>左の位置で、どこまで比較したか (絶対位置。キャンセルしたとき「0x… まで比較して中止」に出す)。</summary>
    public long StoppedAt => Left.Start + LeftPosition;

    /// <summary>比較した左の相対位置 (進捗)。</summary>
    public long LeftPosition => Interlocked.Read(ref _leftPosition);

    /// <summary>比較した右の相対位置 (進捗)。</summary>
    public long RightPosition => Interlocked.Read(ref _rightPosition);

    /// <summary>種類ごとの差分の件数。</summary>
    public long CountOf(DiffKind kind)
    {
        lock (_lock)
        {
            return _kindCounts[(int)kind];
        }
    }

    /// <summary>差分の件数 (読み込み不可を含む)。</summary>
    public long DiffCount => Diffs.Count;

    /// <summary>
    /// 異なるバイトの総数 (ANA-02 の仕様 6)。単純比較では実際に値の違うバイトと、長さの違いの残りの部分。挿入・削除を考慮した比較では
    /// 差分の左右の長さの大きい方の合計。読み込み不可の範囲は数えない。
    /// </summary>
    public long DifferentBytes
    {
        get
        {
            lock (_lock)
            {
                return _differentBytes;
            }
        }
    }

    /// <summary>一致したバイト数。</summary>
    public long MatchedBytes
    {
        get
        {
            lock (_lock)
            {
                return _matchedBytes;
            }
        }
    }

    /// <summary>比較したバイト数 (左右の長い方。一致率の分母)。</summary>
    public long ComparedBytes => Math.Max(Left.Length, Right.Length);

    /// <summary>一致率 (一致バイト数 / 比較したバイト数)。比較するバイトがなければ 1。</summary>
    public double MatchRate => ComparedBytes == 0 ? 1 : (double)MatchedBytes / ComparedBytes;

    /// <summary>一致率の百分率を小数第 2 位までに丸めたもの (1 GB の中の 1 バイトの違いは 100.00%。TC-ANA-02-01)。</summary>
    public double MatchPercent => Math.Round(MatchRate * 100, 2, MidpointRounding.AwayFromZero);

    internal void ReportPosition(long left, long right)
    {
        Interlocked.Exchange(ref _leftPosition, left);
        Interlocked.Exchange(ref _rightPosition, right);
    }

    internal void Count(DiffKind kind, long differentBytes)
    {
        lock (_lock)
        {
            _kindCounts[(int)kind]++;
            _differentBytes += differentBytes;
        }
    }

    internal void AddMatched(long bytes)
    {
        lock (_lock)
        {
            _matchedBytes += bytes;
        }
    }

    /// <summary>
    /// マージで差分を一覧から外した (ANA-07 の仕様 5)。件数と異なるバイト数から引き、一致として数える。
    /// </summary>
    public void Forget(DiffRange diff)
    {
        lock (_lock)
        {
            _kindCounts[(int)diff.Kind] = Math.Max(0, _kindCounts[(int)diff.Kind] - 1);
            if (diff.Kind != DiffKind.Unreadable)
            {
                long bytes = Method == CompareMethod.Simple && diff.Kind == DiffKind.Changed ? diff.LeftLength : diff.MaxLength;
                _differentBytes = Math.Max(0, _differentBytes - bytes);
                _matchedBytes += diff.MaxLength;
            }
        }
    }

    public void Dispose() => Diffs.Dispose();
}
