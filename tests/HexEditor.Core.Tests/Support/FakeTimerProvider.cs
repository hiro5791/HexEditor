namespace HexEditor.Core.Tests.Support;

/// <summary>テストコードが進める時計とタイマー。<see cref="Advance"/> で期限の来たタイマーをその場で呼ぶ。</summary>
public sealed class FakeTimerProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>時計を進め、期限の来たタイマーを順に呼ぶ。</summary>
    public void Advance(TimeSpan by)
    {
        long target = GetTimestamp() + by.Ticks;
        while (true)
        {
            FakeTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is long d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            Interlocked.Exchange(ref _ticks, Math.Max(GetTimestamp(), next.Due!.Value));
            next.Fire();
        }

        Interlocked.Exchange(ref _ticks, target);
    }

    private void Remove(FakeTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimerProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; private set; }

        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetTimestamp() + dueTime.Ticks;
            _period = period;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : owner.GetTimestamp() + _period.Ticks;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
