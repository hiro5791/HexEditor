namespace HexEditor.Core.Files;

/// <summary>
/// 保存を間引いて UI スレッドの外で行う (ユーザークリップボード (EDIT-28 の仕様 6)・選択セット (EDIT-09) の保存)。<see cref="Request"/> には
/// その時点の内容を写し取った書き込みを渡す (写しは呼び出し側のスレッドで作る)。最後の要求から <c>delay</c> 経ったら、最後の書き込みだけを
/// スレッドプールで行う。書き込みは順に 1 つずつ行う (前の書き込みが終わってから次を始める)。
/// </summary>
public sealed class DebouncedWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly Action<Exception> _onError;
    private Action? _pending;
    private ITimer? _timer;
    private Task _running = Task.CompletedTask;
    private bool _disposed;

    /// <param name="onError">書き込みの例外 (書けなかった)。スレッドプールで呼ぶ。</param>
    public DebouncedWriter(TimeSpan delay, Action<Exception> onError, TimeProvider? time = null)
    {
        _delay = delay;
        _onError = onError;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>書き込みを予約する。前の予約でまだ始めていないものは捨てる (新しい内容で書く)。</summary>
    public void Request(Action write)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = write;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Start(), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>まだ始めていない書き込みがあるか。</summary>
    public bool HasPending
    {
        get
        {
            lock (_lock)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>予約した書き込みをすぐに始め、それまでの書き込みがすべて終わるのを待つ (終了時・テスト)。</summary>
    public Task FlushAsync()
    {
        Start();
        lock (_lock)
        {
            return _running;
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_pending is not { } write)
            {
                return;
            }

            _pending = null;
            _running = _running.ContinueWith(_ => Run(write), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Run(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
