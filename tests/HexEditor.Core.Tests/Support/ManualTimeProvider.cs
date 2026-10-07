namespace HexEditor.Core.Tests.Support;

/// <summary>テストコードが進める時計 (TC-EDIT-19-05 の「テスト用の時計」)。</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
