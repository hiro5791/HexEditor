using System.Buffers.Binary;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// パーティション・ファイルシステムの解析が読むデータ (FOR-06〜FOR-17)。ディスク・ボリューム・イメージ・ファイルの内容のどれでもよい。
/// 読み込みはスレッドセーフであること。読めなかった範囲は 0 で埋め、結果の <see cref="ReadResult.Unreadable"/> で返す (例外にしない)。
/// 証拠の解析では、変換ビューを通さない元のデータ (<see cref="Document.DataSnapshot"/>) を読む。
/// </summary>
public interface IVolumeReader
{
    /// <summary>バイト数。</summary>
    long Length { get; }

    /// <summary>位置を指定して読む。末尾を越えた部分は読まない (戻り値の長さが短くなる)。</summary>
    ReadResult Read(long offset, Span<byte> buffer);
}

/// <summary>読み込みの補助。</summary>
public static class VolumeReaderExtensions
{
    /// <summary>範囲を読んで配列で返す。末尾を越えた部分と読めなかった部分は 0。</summary>
    public static byte[] ReadBytes(this IVolumeReader reader, long offset, int count) => reader.ReadBytes(offset, count, out _);

    /// <summary>範囲を読んで配列で返す。<paramref name="complete"/> は、範囲がすべて末尾の内側にあり、読めなかった部分がないか。</summary>
    public static byte[] ReadBytes(this IVolumeReader reader, long offset, int count, out bool complete)
    {
        byte[] buffer = new byte[Math.Max(0, count)];
        if (offset < 0 || count <= 0 || offset >= reader.Length)
        {
            complete = count <= 0;
            return buffer;
        }

        ReadResult r = reader.Read(offset, buffer);
        complete = r.BytesReturned == count && r.IsComplete;
        return buffer;
    }

    /// <summary>範囲を読む。すべて読めたか。</summary>
    public static bool TryRead(this IVolumeReader reader, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > reader.Length - buffer.Length)
        {
            buffer.Clear();
            return false;
        }

        ReadResult r = reader.Read(offset, buffer);
        return r.BytesReturned == buffer.Length && r.IsComplete;
    }

    public static ushort U16(this IVolumeReader reader, long offset)
    {
        Span<byte> b = stackalloc byte[2];
        reader.TryRead(offset, b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public static uint U32(this IVolumeReader reader, long offset)
    {
        Span<byte> b = stackalloc byte[4];
        reader.TryRead(offset, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    /// <summary>一部の範囲を、先頭を 0 とする読み込みにする。</summary>
    public static IVolumeReader Slice(this IVolumeReader reader, long offset, long length) =>
        offset == 0 && length == reader.Length ? reader : new SubVolumeReader(reader, offset, length);
}

/// <summary>メモリ上のバイト列 (テストと小さなデータ)。</summary>
public sealed class ArrayVolumeReader(byte[] data) : IVolumeReader
{
    public long Length => data.Length;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return new ReadResult(0);
        }

        int count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, count).CopyTo(buffer);
        return new ReadResult(count);
    }
}

/// <summary>別の読み込みの一部 (パーティション・ファイルシステムの範囲)。読めなかった範囲の位置はこの読み込みの位置に直す。</summary>
public sealed class SubVolumeReader : IVolumeReader
{
    private readonly IVolumeReader _inner;

    public SubVolumeReader(IVolumeReader inner, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _inner = inner;
        Offset = offset;
        Length = Math.Max(0, Math.Min(length, inner.Length - offset));
    }

    /// <summary>元の読み込みでの開始位置。</summary>
    public long Offset { get; }

    public long Length { get; }

    public IVolumeReader Inner => _inner;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return new ReadResult(0);
        }

        int count = (int)Math.Min(buffer.Length, Length - offset);
        ReadResult r = _inner.Read(Offset + offset, buffer[..count]);
        if (r.IsComplete)
        {
            return new ReadResult(Math.Min(count, r.BytesReturned));
        }

        return new ReadResult(Math.Min(count, r.BytesReturned), [.. r.Unreadable.Select(u => u with { Offset = u.Offset - Offset })]);
    }
}

/// <summary>データソースを読む (ディスク・ボリュームを直接解析する場合)。</summary>
public sealed class ByteSourceVolumeReader(IByteSource source) : IVolumeReader
{
    public long Length => source.Length;

    public ReadResult Read(long offset, Span<byte> buffer) => source.Read(offset, buffer);
}

/// <summary>
/// ドキュメントの内容を読む。証拠の解析なので変換ビューを通さない (<see cref="DocumentSnapshot.Raw"/>)。編集中の内容 (未保存の変更) は
/// 反映する (FOR-08 の「パーティション表のエントリを作成」の後で解析し直すため)。
/// </summary>
public sealed class SnapshotVolumeReader(DocumentSnapshot snapshot) : IVolumeReader
{
    private readonly DocumentSnapshot _snapshot = snapshot.Raw;

    public DocumentSnapshot Snapshot => _snapshot;

    public long Length => _snapshot.Length;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _snapshot.Length)
        {
            return new ReadResult(0);
        }

        return _snapshot.Read(offset, buffer);
    }
}

/// <summary>
/// ブロック単位の読み込みのキャッシュ (FAT・MFT・ビットマップを何度も少しずつ読むため。FOR-11 の「64 KB 単位でキャッシュ」)。
/// 保持するブロック数に上限があり、メモリ使用量はボリュームの大きさに比例しない。スレッドセーフ。
/// </summary>
public sealed class CachedVolumeReader : IVolumeReader
{
    public const int DefaultBlockSize = 64 * 1024;

    private readonly IVolumeReader _inner;
    private readonly int _blockSize;
    private readonly int _maxBlocks;
    private readonly Dictionary<long, LinkedListNode<Block>> _blocks = [];
    private readonly LinkedList<Block> _lru = new();
    private readonly object _gate = new();

    private sealed record Block(long Index, byte[] Data, int Length, IReadOnlyList<UnreadableRange> Bad);

    public CachedVolumeReader(IVolumeReader inner, int blockSize = DefaultBlockSize, int maxBlocks = 256)
    {
        _inner = inner;
        _blockSize = blockSize;
        _maxBlocks = Math.Max(2, maxBlocks);
    }

    public long Length => _inner.Length;

    public IVolumeReader Inner => _inner;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.IsEmpty)
        {
            return new ReadResult(0);
        }

        int total = (int)Math.Min(buffer.Length, Length - offset);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < total)
        {
            long at = offset + done;
            long index = at / _blockSize;
            Block block = GetBlock(index);
            int within = (int)(at - index * _blockSize);
            int n = Math.Min(total - done, block.Length - within);
            if (n <= 0)
            {
                break;
            }

            block.Data.AsSpan(within, n).CopyTo(buffer.Slice(done, n));
            foreach (UnreadableRange u in block.Bad)
            {
                long from = Math.Max(u.Offset, at), to = Math.Min(u.End, at + n);
                if (to > from)
                {
                    (bad ??= []).Add(u with { Offset = from, Length = to - from });
                }
            }

            done += n;
        }

        return new ReadResult(done, bad);
    }

    /// <summary>キャッシュを捨てる (ドキュメントが変わったとき)。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _blocks.Clear();
            _lru.Clear();
        }
    }

    private Block GetBlock(long index)
    {
        lock (_gate)
        {
            if (_blocks.TryGetValue(index, out LinkedListNode<Block>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }
        }

        long start = index * _blockSize;
        int length = (int)Math.Min(_blockSize, Length - start);
        byte[] data = new byte[_blockSize];
        ReadResult r = _inner.Read(start, data.AsSpan(0, length));
        var block = new Block(index, data, Math.Min(length, r.BytesReturned), r.Unreadable);
        lock (_gate)
        {
            if (_blocks.TryGetValue(index, out LinkedListNode<Block>? existing))
            {
                return existing.Value;
            }

            var added = _lru.AddFirst(block);
            _blocks[index] = added;
            while (_blocks.Count > _maxBlocks && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _blocks.Remove(last.Value.Index);
            }
        }

        return block;
    }
}

/// <summary>小さな CRC32 (GPT のヘッダーとエントリ配列。IEEE 802.3、反転あり)。</summary>
public static class Crc32Ieee
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>続きを計算する (<paramref name="crc"/> は前回の戻り値。最初は 0)。</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }
}
