#if HEX_TEST_HOOKS
using System.Buffers.Binary;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;

namespace HexEditor.Core.Sources;

// テスト用のビルドだけに入れる、異常を再現するためのデータソース (テスト方針 7.2)。製品版には含めない。

/// <summary>仮想のデータソースの値の決め方。</summary>
public enum VirtualContent
{
    /// <summary>すべて 0。</summary>
    Zero,

    /// <summary>オフセットの下位 8 ビット (offset &amp; 0xFF)。</summary>
    OffsetLowByte,

    /// <summary>8 バイトごとに、その位置のオフセットをリトルエンディアンで書いた値 (位置が値から分かる)。</summary>
    Offset64,

    /// <summary>すべて同じ値 (<see cref="VirtualByteSource.Fill"/>)。</summary>
    Fill,

    /// <summary>種から計算する乱数列 (<see cref="GeneratedData.FillRandom"/>)。</summary>
    Random,
}

/// <summary>
/// 実際のファイルなしに、0〜2^63−1 バイトの任意の長さのデータを作るデータソース (テスト方針 7.2「仮想のデータソース」)。
/// 値はオフセットから計算するため、長さに関係なくメモリを使わない。書き込みはできない。
/// </summary>
public sealed class VirtualByteSource : ByteSourceBase
{
    public VirtualByteSource(long length, VirtualContent content = VirtualContent.Offset64, bool resizable = true,
        byte fill = 0, ulong seed = 0, string displayName = "virtual")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        Content = content;
        Fill = fill;
        Seed = seed;
        DisplayName = displayName;
        Capabilities = resizable ? SourceCapabilities.CanResize : SourceCapabilities.None;
        Identity = "virtual:" + Guid.NewGuid().ToString("N");
    }

    public override string DisplayName { get; }

    public override string Identity { get; }

    public override long Length { get; }

    public override SourceCapabilities Capabilities { get; }

    public VirtualContent Content { get; }

    public byte Fill { get; }

    public ulong Seed { get; }

    /// <summary>オフセット <paramref name="offset"/> のバイトの値 (テストの期待値の計算に使う)。</summary>
    public byte ValueAt(long offset)
    {
        Span<byte> one = stackalloc byte[1];
        Compute(offset, one);
        return one[0];
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        Compute(offset, buffer[..count]);
        return new ReadResult(count);
    }

    private void Compute(long offset, Span<byte> target)
    {
        switch (Content)
        {
            case VirtualContent.Zero:
                target.Clear();
                break;
            case VirtualContent.Fill:
                target.Fill(Fill);
                break;
            case VirtualContent.OffsetLowByte:
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = (byte)(offset + i);
                }

                break;
            case VirtualContent.Offset64:
                Span<byte> word = stackalloc byte[8];
                for (int i = 0; i < target.Length; i++)
                {
                    long p = offset + i;
                    BinaryPrimitives.WriteInt64LittleEndian(word, p & ~7L);
                    target[i] = word[(int)(p & 7)];
                }

                break;
            default:
                GeneratedData.FillRandom(Seed, offset, target);
                break;
        }
    }
}

/// <summary>
/// ほかのデータソースに、読み込みの遅延と読み込みエラーを加える (テスト方針 7.2「遅いデータソース」「読み込みエラー」)。
/// 読めない範囲は IByteSource の約束どおり、範囲だけを正確に返す (バッファは 0 で埋める)。
/// </summary>
public sealed class FaultyByteSource(IByteSource inner) : ByteSourceBase
{
    private readonly object _lock = new();
    private readonly List<UnreadableRange> _badRanges = [];

    public IByteSource Inner { get; } = inner;

    /// <summary>読み込みのたびに入れる遅延。</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>この位置より前だけを読む要求には遅延を入れない (先頭の表示を待たずに済ませるため)。</summary>
    public long DelayFromOffset { get; set; }

    /// <summary>
    /// <see cref="HoldFromOffset"/> より後ろを読む要求は、<see cref="Hold"/> が開くまで待たせる
    /// (処理の途中の状態をタイミングに頼らずに作るため)。既定は開いている。
    /// </summary>
    public ManualResetEventSlim Hold { get; } = new(initialState: true);

    /// <summary><see cref="Hold"/> で待たせる範囲の始まり。</summary>
    public long HoldFromOffset { get; set; }

    public override string DisplayName => Inner.DisplayName;

    public override string Identity => Inner.Identity;

    public override long Length => Inner.Length;

    public override long BaseAddress => Inner.BaseAddress;

    public override int LogicalSectorSize => Inner.LogicalSectorSize;

    public override int PhysicalSectorSize => Inner.PhysicalSectorSize;

    public override SourceCapabilities Capabilities => Inner.Capabilities | (BadRangeCount > 0 ? SourceCapabilities.HasGaps : 0);

    public int BadRangeCount
    {
        get
        {
            lock (_lock)
            {
                return _badRanges.Count;
            }
        }
    }

    /// <summary>読み込みのたびに増える (遅延・エラーの確認用)。</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    private long _readCount;
    private long _lastReadStarted;

    /// <summary>最後の読み込みを始めた時刻 (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>。読み込みの記録。FIND-02 の確認用)。0 は未読み込み。</summary>
    public long LastReadStartedTimestamp => Interlocked.Read(ref _lastReadStarted);

    /// <summary>指定の範囲の読み込みを I/O エラーにする (既定は ERROR_IO_DEVICE)。</summary>
    public void AddReadError(long offset, long length, UnreadableReason reason = UnreadableReason.IoError, int errorCode = 0x45D)
    {
        lock (_lock)
        {
            _badRanges.Add(new UnreadableRange(offset, length, reason, errorCode));
        }
    }

    public void ClearReadErrors()
    {
        lock (_lock)
        {
            _badRanges.Clear();
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        Interlocked.Exchange(ref _lastReadStarted, System.Diagnostics.Stopwatch.GetTimestamp());
        WaitForHold(offset, buffer.Length, CancellationToken.None);
        if (Delays(offset, buffer.Length))
        {
            Thread.Sleep(Delay);
        }

        return ReadCore(offset, buffer);
    }

    public override async ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _lastReadStarted, System.Diagnostics.Stopwatch.GetTimestamp());
        WaitForHold(offset, buffer.Length, cancellationToken);
        if (Delays(offset, buffer.Length))
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReadCore(offset, buffer.Span);
    }

    private void WaitForHold(long offset, int length, CancellationToken cancellationToken)
    {
        if (offset + length > HoldFromOffset)
        {
            Hold.Wait(cancellationToken);
        }
    }

    private bool Delays(long offset, int length) => Delay > TimeSpan.Zero && offset + length > DelayFromOffset;

    private ReadResult ReadCore(long offset, Span<byte> buffer)
    {
        Interlocked.Increment(ref _readCount);
        ReadResult inner = Inner.Read(offset, buffer);
        long end = offset + inner.BytesReturned;
        var bad = new List<UnreadableRange>(inner.Unreadable);
        lock (_lock)
        {
            foreach (UnreadableRange r in _badRanges)
            {
                long from = Math.Max(r.Offset, offset);
                long to = Math.Min(r.End, end);
                if (from < to)
                {
                    buffer.Slice((int)(from - offset), (int)(to - from)).Clear();
                    bad.Add(r with { Offset = from, Length = to - from });
                }
            }
        }

        bad.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new ReadResult(inner.BytesReturned, bad);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data) => Inner.Write(offset, data);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }
    }
}

/// <summary>保存の途中の時点に処理を差し込む (テスト方針 7.2「強制終了」)。</summary>
public static class TestSavePoints
{
    /// <summary>その場保存 (ENG-23) で、ジャーナルを書いた後、ファイルに書き込む前に呼ぶ。</summary>
    public static Action? AfterJournalWritten
    {
        get => InPlaceSaver.AfterJournalWritten;
        set => InPlaceSaver.AfterJournalWritten = value;
    }

    /// <summary>ずらしながらのその場保存 (ENG-24) で、書き込んだ量 (バイト) ごとに呼ぶ。</summary>
    public static Action<long>? AfterShiftBytesWritten
    {
        get => ShiftSaver.AfterBytesWritten;
        set => ShiftSaver.AfterBytesWritten = value;
    }
}
#endif
