namespace HexEditor.Core.Operations;

/// <summary>長時間処理の種類 (ENG-09 の仕様 1・6)。</summary>
public enum OperationKind
{
    /// <summary>読み取りのみ (検索、ハッシュ、統計)。同じドキュメントでも同時に複数実行できる。</summary>
    ReadOnly,

    /// <summary>ドキュメントを変更する (すべて置換、ファイルの挿入など)。</summary>
    ModifiesDocument,

    /// <summary>外部に書き出す (保存、エクスポート)。</summary>
    WritesExternal,
}

public enum OperationState
{
    Pending,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 0.5 秒以上かかりうる処理 1 つ (ENG-09)。処理側は <see cref="Report"/> で進捗を報告し、
/// <see cref="CancellationToken"/> を 1 MiB または 100 ms ごとに確認する。
/// </summary>
public sealed class LongRunningOperation
{
    /// <summary>進捗表示を出すまでの時間 (ENG-09 の仕様 2)。</summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromSeconds(0.5);

    /// <summary>残り時間を出し始めるまでの時間 (ENG-09 の仕様 3)。</summary>
    public static readonly TimeSpan EstimateDelay = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly object _lock = new();
    private long _processed;
    private long _lastNotifyTicks = long.MinValue;
    private long _matches = -1;

    internal LongRunningOperation(string name, OperationKind kind, object? target, long? totalBytes, TimeProvider time)
    {
        Name = name;
        Kind = kind;
        Target = target;
        TotalBytes = totalBytes;
        _time = time;
        StartedAt = time.GetUtcNow();
        _samples.Enqueue((time.GetTimestamp(), 0));
    }

    public string Name { get; }

    public OperationKind Kind { get; }

    /// <summary>対象のドキュメント (なければ null)。</summary>
    public object? Target { get; }

    /// <summary>全体のバイト数。分からない場合は null (不確定の進捗)。</summary>
    public long? TotalBytes { get; private set; }

    public long ProcessedBytes => Interlocked.Read(ref _processed);

    public DateTimeOffset StartedAt { get; }

    public OperationState State
    {
        get => _state;
        internal set
        {
            _state = value;
            if (value is OperationState.Completed or OperationState.Failed or OperationState.Cancelled)
            {
                EndedAt ??= _time.GetUtcNow();
            }
        }
    }

    private OperationState _state = OperationState.Pending;

    /// <summary>キャンセルを要求した時刻 (<see cref="Cancel"/>)。要求していなければ null。</summary>
    public DateTimeOffset? CancelRequestedAt { get; private set; }

    /// <summary>処理が終わった (完了・失敗・キャンセル済みになった) 時刻。実行中は null。</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public Exception? Error { get; internal set; }

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>開始からの経過時間。</summary>
    public TimeSpan Elapsed => _time.GetUtcNow() - StartedAt;

    /// <summary>進捗表示を出すべきか (0.5 秒を超えて実行中)。</summary>
    public bool ShouldShow => State is OperationState.Running or OperationState.Cancelling && Elapsed > ShowDelay;

    /// <summary>進捗が変わった。1 秒に最大 10 回 (ENG-09 の仕様 4)。</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>直近 10 秒の移動平均の処理速度 (バイト / 秒)。</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                (long firstTicks, long firstBytes) = _samples.Peek();
                double seconds = _time.GetElapsedTime(firstTicks).TotalSeconds;
                return seconds <= 0 ? 0 : (ProcessedBytes - firstBytes) / seconds;
            }
        }
    }

    /// <summary>残り時間。開始から 3 秒たつまで、または全体が分からない場合は null。</summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (TotalBytes is not long total || Elapsed < EstimateDelay)
            {
                return null;
            }

            double speed = BytesPerSecond;
            return speed <= 0 ? null : TimeSpan.FromSeconds(Math.Max(0, total - ProcessedBytes) / speed);
        }
    }

    /// <summary>進捗率 (0〜1)。全体が分からない場合は null。</summary>
    public double? Fraction => TotalBytes is long total && total > 0 ? Math.Clamp((double)ProcessedBytes / total, 0, 1) : null;

    /// <summary>処理済みのバイト数を報告する。キャンセルされていれば例外を投げる。</summary>
    public void Report(long processedBytes)
    {
        Interlocked.Exchange(ref _processed, processedBytes);
        long now = _time.GetTimestamp();
        bool notify = false;
        lock (_lock)
        {
            _samples.Enqueue((now, processedBytes));
            while (_samples.Count > 2 && _time.GetElapsedTime(_samples.Peek().Ticks, now) > SpeedWindow)
            {
                _samples.Dequeue();
            }

            if (_lastNotifyTicks == long.MinValue || _time.GetElapsedTime(_lastNotifyTicks, now) >= ProgressInterval)
            {
                _lastNotifyTicks = now;
                notify = true;
            }
        }

        if (notify)
        {
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        CancellationToken.ThrowIfCancellationRequested();
    }

    public void SetTotal(long? totalBytes) => TotalBytes = totalBytes;

    /// <summary>検索の処理で、これまでに見つかった一致の数 (FIND-02 の仕様 2)。一致を数えない処理では null。</summary>
    public long? Matches => Interlocked.Read(ref _matches) is var m && m >= 0 ? m : null;

    /// <summary>これまでに見つかった一致の数を報告する (検索の処理だけが呼ぶ)。</summary>
    public void ReportMatches(long count) => Interlocked.Exchange(ref _matches, Math.Max(0, count));

    /// <summary>キャンセルできるか (ずらしながらのその場保存は書き込みを始めたらキャンセルできない。ENG-24 の仕様 5)。</summary>
    public bool CanCancel { get; private set; } = true;

    /// <summary>これ以降キャンセルを受け付けない。UI はキャンセルボタンを無効にし、理由をツールチップで示す。</summary>
    public void DisallowCancel()
    {
        CanCancel = false;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>キャンセルを要求する。処理は 200 ms 以内に止まる (ENG-09 の仕様 5)。キャンセルできない処理では何もしない。</summary>
    public void Cancel()
    {
        if (CanCancel && State is OperationState.Pending or OperationState.Running)
        {
            State = OperationState.Cancelling;
            CancelRequestedAt = _time.GetUtcNow();
            _cts.Cancel();
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RaiseProgressChanged() => ProgressChanged?.Invoke(this, EventArgs.Empty);
}
