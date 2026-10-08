using HexEditor.Platform.Network;

namespace HexEditor.Platform.Tests;

/// <summary>更新のダウンロードの速さの上限 (10 の PKG-18 の仕様 1: 帯域を使い切らない)。実際には待たず、待つ時間を数える。</summary>
public sealed class ThrottledCopyTests
{
    [Fact]
    public void Wait_is_the_time_needed_to_stay_under_the_limit()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ThrottledCopy.WaitFor(2_000, 1_000, TimeSpan.FromSeconds(1)));
        Assert.Null(ThrottledCopy.WaitFor(1_000, 1_000, TimeSpan.FromSeconds(2)));
        Assert.Null(ThrottledCopy.WaitFor(500, 1_000, TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public async Task Copy_keeps_the_average_under_the_limit_and_reports_progress()
    {
        byte[] data = new byte[1_000_000];
        Random.Shared.NextBytes(data);
        using var source = new MemoryStream(data);
        using var target = new MemoryStream();
        var time = new ManualTime();
        var progress = new List<int>();
        await ThrottledCopy.CopyAsync(source, target, data.Length, 250_000, progress.Add, CancellationToken.None, time, (d, _) =>
        {
            time.Advance(d);
            return Task.CompletedTask;
        });

        Assert.Equal(data, target.ToArray());

        // 1,000,000 バイトを 250,000 バイト/秒: 待つ時間の合計はほぼ 4 秒 (最後の塊の分だけ短い)。
        Assert.InRange(time.Elapsed.TotalSeconds, 3.7, 4.01);
        Assert.Equal(100, progress[^1]);
        Assert.Equal(progress.Order(), progress);
    }

    [Fact]
    public async Task Without_a_limit_it_never_waits()
    {
        using var source = new MemoryStream(new byte[300_000]);
        using var target = new MemoryStream();
        int waits = 0;
        await ThrottledCopy.CopyAsync(source, target, null, 0, null, CancellationToken.None, new ManualTime(), (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });
        Assert.Equal(0, waits);
        Assert.Equal(300_000, target.Length);
    }

    [Fact]
    public async Task Cancelling_stops_the_copy()
    {
        using var source = new MemoryStream(new byte[300_000]);
        using var target = new MemoryStream();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ThrottledCopy.CopyAsync(source, target, 300_000, 1_000, null, cancel.Token, new ManualTime(), (d, ct) => Task.Delay(d, ct)));
    }

    /// <summary>進めた分だけ進む時計。</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

        public void Advance(TimeSpan d) => _ticks += d.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;
    }
}
