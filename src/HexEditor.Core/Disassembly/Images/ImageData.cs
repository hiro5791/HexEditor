using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Disassembly.Images;

/// <summary>ヘッダの範囲外を読もうとした (解釈できた部分だけを使う)。</summary>
internal sealed class ImageFormatException(string code, string detail = "") : Exception(code + " " + detail)
{
    public string Code { get; } = code;

    public string Detail { get; } = detail;
}

/// <summary>
/// 実行形式のヘッダを読む補助 (小さな範囲を読み、範囲外は <see cref="ImageFormatException"/>)。最後に読んだ 64 KB を覚えて、
/// 近い位置の読み込みを速くする。
/// </summary>
internal sealed class ImageData
{
    private const int BlockSize = 64 * 1024;

    private readonly ImageReader _read;
    private readonly byte[] _block = new byte[BlockSize];
    private long _blockStart = -1;
    private int _blockLength;

    public ImageData(ImageReader read, long length, bool bigEndian = false)
    {
        _read = read;
        Length = length;
        BigEndian = bigEndian;
    }

    public long Length { get; }

    public bool BigEndian { get; set; }

    public CancellationToken Token { get; init; }

    /// <summary><paramref name="count"/> バイトを読む。範囲外なら例外。</summary>
    public ReadOnlySpan<byte> Bytes(long offset, int count, string code = "OutOfRange")
    {
        if (offset < 0 || count < 0 || offset > Length - count)
        {
            throw new ImageFormatException(code, $"0x{offset:X}");
        }

        if (count > BlockSize)
        {
            byte[] big = new byte[count];
            int n = _read(offset, big);
            return n == count ? big : throw new ImageFormatException("Unreadable", $"0x{offset:X}");
        }

        if (_blockStart < 0 || offset < _blockStart || offset + count > _blockStart + _blockLength)
        {
            Token.ThrowIfCancellationRequested();
            long start = offset;
            int want = (int)Math.Min(BlockSize, Length - start);
            _blockLength = _read(start, _block.AsSpan(0, want));
            _blockStart = start;
            if (_blockLength < count)
            {
                throw new ImageFormatException("Unreadable", $"0x{offset:X}");
            }
        }

        return _block.AsSpan((int)(offset - _blockStart), count);
    }

    public bool Has(long offset, long count) => offset >= 0 && count >= 0 && offset <= Length - count;

    public byte U8(long offset) => Bytes(offset, 1)[0];

    public ushort U16(long offset) =>
        BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Bytes(offset, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(Bytes(offset, 2));

    public uint U32(long offset) =>
        BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Bytes(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(Bytes(offset, 4));

    public ulong U64(long offset) =>
        BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Bytes(offset, 8)) : BinaryPrimitives.ReadUInt64LittleEndian(Bytes(offset, 8));

    public ulong Word(long offset, bool wide) => wide ? U64(offset) : U32(offset);

    /// <summary>NUL 終端の ASCII / UTF-8 の文字列 (最大 <paramref name="max"/> バイト)。</summary>
    public string CString(long offset, int max = 4096)
    {
        if (!Has(offset, 1))
        {
            throw new ImageFormatException("StringOutOfRange", $"0x{offset:X}");
        }

        int n = (int)Math.Min(max, Length - offset);
        ReadOnlySpan<byte> s = Bytes(offset, Math.Min(n, 4096));
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }

    /// <summary>固定長の名前 (NUL で終わる部分まで)。</summary>
    public string FixedName(long offset, int size)
    {
        ReadOnlySpan<byte> s = Bytes(offset, size);
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }
}
