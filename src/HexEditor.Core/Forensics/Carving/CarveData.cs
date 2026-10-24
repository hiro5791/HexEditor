using System.Buffers;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>カービングの対象のデータ (読み取り専用)。ドキュメントの生のデータ・テスト用のメモリなど。</summary>
public interface ICarveData
{
    long Length { get; }

    /// <summary>[offset, offset + destination.Length) を読む。末尾を越える部分は返さない。読めない範囲は <see cref="ReadResult.Unreadable"/> に入る。</summary>
    ReadResult Read(long offset, Span<byte> destination);
}

/// <summary>ドキュメントの生のデータ (<see cref="DocumentSnapshot.Raw"/>。変換表示や編集の影響を受けない) を対象にする。</summary>
public sealed class SnapshotCarveData(DocumentSnapshot snapshot) : ICarveData
{
    public DocumentSnapshot Snapshot { get; } = snapshot.Raw;

    public long Length => Snapshot.Length;

    public ReadResult Read(long offset, Span<byte> destination) => Snapshot.Read(offset, destination);
}

/// <summary>メモリ上のバイト列 (テスト用)。</summary>
public sealed class MemoryCarveData(byte[] data) : ICarveData
{
    public long Length => data.LongLength;

    public ReadResult Read(long offset, Span<byte> destination)
    {
        if (offset >= data.LongLength)
        {
            return new ReadResult(0);
        }

        int count = (int)Math.Min(destination.Length, data.LongLength - offset);
        data.AsSpan((int)offset, count).CopyTo(destination);
        return new ReadResult(count);
    }
}

/// <summary>
/// 構造の検証・サイズの取得のための読み取り窓。64 KiB を単位にキャッシュし、1 回の検証で行う読み取りの回数に上限を持つ
/// (壊れたデータで無限に読み続けない)。範囲外・読めない範囲・上限超過は <see cref="Failed"/> になり、以降の読み取りはすべて失敗する。
/// </summary>
public sealed class ByteWindow
{
    /// <summary>キャッシュの単位 (<see cref="ByteWindow(ICarveData, long, long, CancellationToken, byte[])"/> に渡す作業用の配列の最小の長さ)。</summary>
    public const int BlockSize = 64 * 1024;

    private readonly ICarveData _data;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _block;
    private long _blockStart = -1;
    private int _blockLength;
    private long _budget;

    public ByteWindow(ICarveData data, long limit, long budget, CancellationToken cancellationToken = default)
        : this(data, limit, budget, cancellationToken, null)
    {
    }

    /// <summary>
    /// 作業用の配列 <paramref name="buffer"/> (<see cref="BlockSize"/> 以上。null なら新しく作る) を使う窓。多くの位置を順に検証するとき
    /// (ファイルマップの埋め込まれたファイルの候補など) に、窓ごとに 64 KiB を確保しないため。同時に 2 つの窓で同じ配列を使わないこと。
    /// </summary>
    public ByteWindow(ICarveData data, long limit, long budget, CancellationToken cancellationToken, byte[]? buffer)
    {
        _block = buffer is { Length: >= BlockSize } ? buffer : new byte[BlockSize];
        _data = data;
        Limit = Math.Min(limit, data.Length);
        _budget = budget;
        _cancellationToken = cancellationToken;
    }

    /// <summary>これより先は読まない (絶対位置)。</summary>
    public long Limit { get; }

    /// <summary>残りの読み取りの回数 (上限を超えたら 0 以下)。</summary>
    public long RemainingBudget => _budget;

    internal ICarveData Data => _data;

    internal CancellationToken CancellationToken => _cancellationToken;

    /// <summary>読み取りに失敗した (範囲外・読めない・回数の上限)。</summary>
    public bool Failed { get; private set; }

    /// <summary>読めない範囲に当たった。</summary>
    public bool HitUnreadable { get; private set; }

    /// <summary>1 バイト。</summary>
    public bool TryU8(long offset, out byte value)
    {
        value = 0;
        if (Failed || --_budget < 0)
        {
            return Fail();
        }

        // 範囲の外 (ファイルの終わりを越えた読み取り) は失敗ではなく「読めない」だけ。検証が終わりの先を調べて、なければそれまでを使えるようにする。
        if (offset < 0 || offset >= Limit)
        {
            return false;
        }

        if (!InBlock(offset) && !Fill(offset))
        {
            return false;
        }

        value = _block[offset - _blockStart];
        return true;
    }

    private bool InBlock(long offset) => _blockStart >= 0 && offset >= _blockStart && offset < _blockStart + _blockLength;

    private bool Fail()
    {
        Failed = true;
        return false;
    }

    private bool Fill(long offset)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        long start = offset / 4096 * 4096;
        int count = (int)Math.Min(BlockSize, Limit - start);
        ReadResult r = _data.Read(start, _block.AsSpan(0, count));
        if (r.Unreadable.Count > 0)
        {
            HitUnreadable = true;
            _blockStart = -1;
            return Fail();
        }

        _blockStart = start;
        _blockLength = r.BytesReturned;
        if (!InBlock(offset))
        {
            return Fail();
        }

        return true;
    }

    /// <summary><paramref name="destination"/> の長さ分を読む (すべて読めたときだけ true)。</summary>
    public bool TryRead(long offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            if (!TryU8(offset + i, out destination[i]))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryU16(long offset, bool bigEndian, out ushort value)
    {
        value = 0;
        if (!TryU8(offset, out byte a) || !TryU8(offset + 1, out byte b))
        {
            return false;
        }

        value = bigEndian ? (ushort)((a << 8) | b) : (ushort)((b << 8) | a);
        return true;
    }

    public bool TryU32(long offset, bool bigEndian, out uint value)
    {
        value = 0;
        if (!TryU16(offset, bigEndian, out ushort a) || !TryU16(offset + 2, bigEndian, out ushort b))
        {
            return false;
        }

        value = bigEndian ? ((uint)a << 16) | b : ((uint)b << 16) | a;
        return true;
    }

    public bool TryU64(long offset, bool bigEndian, out ulong value)
    {
        value = 0;
        if (!TryU32(offset, bigEndian, out uint a) || !TryU32(offset + 4, bigEndian, out uint b))
        {
            return false;
        }

        value = bigEndian ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
        return true;
    }

    /// <summary>1〜8 バイトの符号なし整数。</summary>
    public bool TryUInt(long offset, int bytes, bool bigEndian, out ulong value)
    {
        value = 0;
        for (int i = 0; i < bytes; i++)
        {
            if (!TryU8(offset + (bigEndian ? i : bytes - 1 - i), out byte b))
            {
                return false;
            }

            value = (value << 8) | b;
        }

        return true;
    }

    /// <summary>位置の先頭が <paramref name="expected"/> に一致するか。</summary>
    public bool Matches(long offset, ReadOnlySpan<byte> expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (!TryU8(offset + i, out byte b) || b != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>パターンを [from, to) から探す。見つからない・読めなければ -1。</summary>
    public long Find(ReadOnlySpan<byte> pattern, long from, long to)
    {
        if (Failed)
        {
            return -1;
        }

        long result = ByteSearch.Find(_data, from, Math.Min(to, Limit), pattern, _cancellationToken, ref _budget);
        if (_budget < 0)
        {
            Failed = true;
        }

        return result;
    }
}

/// <summary>データの中のバイト列の探索 (ストリーミング。メモリは固定)。</summary>
public static class ByteSearch
{
    private const int BlockSize = 256 * 1024;

    /// <summary>パターン (固定のバイト列) を [from, to) から探す。見つからなければ -1。読めない範囲は飛ばす。</summary>
    public static long Find(ICarveData data, long from, long to, ReadOnlySpan<byte> pattern, CancellationToken cancellationToken)
    {
        long budget = long.MaxValue;
        return Find(data, from, to, pattern, cancellationToken, ref budget);
    }

    internal static long Find(ICarveData data, long from, long to, ReadOnlySpan<byte> pattern, CancellationToken cancellationToken, ref long budget)
    {
        return FindMask(data, from, to, pattern, [], cancellationToken, ref budget);
    }

    /// <summary>ワイルドカード付きのパターンを [from, to) から探す。</summary>
    public static long Find(ICarveData data, long from, long to, HexMaskPattern pattern, CancellationToken cancellationToken)
    {
        long budget = long.MaxValue;
        return FindMask(data, from, to, pattern.Value, pattern.Mask, cancellationToken, ref budget, pattern.AnchorOffset, pattern.AnchorLength);
    }

    private static long FindMask(ICarveData data, long from, long to, ReadOnlySpan<byte> value, ReadOnlySpan<byte> mask, CancellationToken cancellationToken,
        ref long budget, int anchorOffset = 0, int anchorLength = -1)
    {
        to = Math.Min(to, data.Length);
        if (value.Length == 0 || to - from < value.Length)
        {
            return -1;
        }

        bool masked = mask.Length > 0;
        if (anchorLength < 0)
        {
            anchorLength = value.Length;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize + value.Length);
        try
        {
            long position = from;
            while (position + value.Length <= to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(BlockSize + value.Length - 1, to - position);
                ReadResult r = data.Read(position, buffer.AsSpan(0, want));
                budget -= want / 64 + 1;
                if (budget < 0)
                {
                    return -1;
                }

                Span<byte> span = buffer.AsSpan(0, r.BytesReturned);
                if (r.Unreadable.Count == 0)
                {
                    ReadOnlySpan<byte> anchor = value.Slice(anchorOffset, anchorLength);
                    int from0 = 0;
                    while (from0 + value.Length <= span.Length)
                    {
                        int i = span[(from0 + anchorOffset)..].IndexOf(anchor);
                        if (i < 0)
                        {
                            break;
                        }

                        int at = from0 + i;
                        if (at + value.Length > span.Length)
                        {
                            break;
                        }

                        if (!masked || Matches(span.Slice(at, value.Length), value, mask))
                        {
                            return position + at;
                        }

                        from0 = at + 1;
                    }
                }

                if (r.BytesReturned <= value.Length - 1)
                {
                    break;
                }

                // 次のブロックは境界をまたぐ一致のために value.Length - 1 バイト重ねる。
                position += r.BytesReturned - (value.Length - 1);
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value, ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if ((data[i] & mask[i]) != value[i])
            {
                return false;
            }
        }

        return true;
    }
}
