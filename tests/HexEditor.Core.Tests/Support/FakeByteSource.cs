using System.Collections.Concurrent;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Support;

/// <summary>読み込みの失敗の仕方 (テスト用)。</summary>
public enum FailureMode
{
    /// <summary>読めない範囲だけを正確に返す (IByteSource の約束どおりのデータソース)。</summary>
    Precise,

    /// <summary>読めない範囲に少しでもかかる読み込みは全体が失敗する (実際のデバイスと同じ)。</summary>
    WholeRequest,
}

/// <summary>
/// 能力のフラグ・読めない範囲・セクタサイズを自由に決められるテスト用のデータソース。内容は関数で計算するため、
/// 巨大な長さでもメモリを使わない。読み込み・書き込みの要求を記録する。
/// </summary>
public sealed class FakeByteSource : ByteSourceBase
{
    private readonly Action<long, Span<byte>> _content;
    private readonly byte[]? _writable;

    public FakeByteSource(long length, Action<long, Span<byte>> content, SourceCapabilities capabilities, int sectorSize = 1)
    {
        Length = length;
        _content = content;
        Capabilities = capabilities;
        LogicalSectorSize = sectorSize;
    }

    /// <summary>内容を配列で持ち、書き込みも反映するデータソース。</summary>
    public FakeByteSource(byte[] data, SourceCapabilities capabilities, int sectorSize = 1)
        : this(data.Length, (o, s) => data.AsSpan((int)o, s.Length).CopyTo(s), capabilities, sectorSize)
    {
        _writable = data;
    }

    public override string DisplayName => "fake";

    public override string Identity { get; } = "fake:" + Guid.NewGuid().ToString("N");

    public override long Length { get; }

    public override int LogicalSectorSize { get; }

    public override SourceCapabilities Capabilities { get; }

    public List<UnreadableRange> BadRanges { get; } = [];

    public FailureMode FailureMode { get; set; } = FailureMode.Precise;

    /// <summary>読み込みのたびに入れる遅延。</summary>
    public TimeSpan Delay { get; set; }

    public ConcurrentQueue<(long Offset, int Length)> Reads { get; } = new();

    public ConcurrentQueue<(long Offset, int Length)> Writes { get; } = new();

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        int count = ClampToLength(offset, buffer.Length);
        Reads.Enqueue((offset, count));
        Span<byte> target = buffer[..count];
        _content(offset, target);

        long end = offset + count;
        var bad = new List<UnreadableRange>();
        foreach (UnreadableRange r in BadRanges)
        {
            long from = Math.Max(r.Offset, offset);
            long to = Math.Min(r.End, end);
            if (from >= to)
            {
                continue;
            }

            if (FailureMode == FailureMode.WholeRequest)
            {
                target.Clear();
                return new ReadResult(count, [r with { Offset = offset, Length = count }]);
            }

            target.Slice((int)(from - offset), (int)(to - from)).Clear();
            bad.Add(r with { Offset = from, Length = to - from });
        }

        return new ReadResult(count, bad);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (_writable is null || !Capabilities.HasFlag(SourceCapabilities.CanWrite))
        {
            base.Write(offset, data);
        }

        Writes.Enqueue((offset, data.Length));
        data.CopyTo(_writable.AsSpan((int)offset));
    }
}
