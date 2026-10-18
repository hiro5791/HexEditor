namespace HexEditor.Core.Patches;

/// <summary>UPS・BPS の可変長整数 (beat の形式。7 ビットずつ、最後のバイトに最上位ビット)。</summary>
internal static class BeatVarint
{
    public static void Write(Stream output, ulong value)
    {
        while (true)
        {
            byte x = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0)
            {
                output.WriteByte((byte)(0x80 | x));
                return;
            }

            output.WriteByte(x);
            value--;
        }
    }

    /// <summary>書いたときのバイト数。</summary>
    public static int Size(ulong value)
    {
        int n = 1;
        while (true)
        {
            value >>= 7;
            if (value == 0)
            {
                return n;
            }

            n++;
            value--;
        }
    }

    /// <summary>読む。ファイルの終わりなら <see cref="EndOfStreamException"/>、値があふれたら <see cref="OverflowException"/>。</summary>
    public static ulong Read(Stream input)
    {
        ulong data = 0, shift = 1;
        for (int i = 0; i < 10; i++)
        {
            int x = input.ReadByte();
            if (x < 0)
            {
                throw new EndOfStreamException();
            }

            data += (ulong)(x & 0x7F) * shift;
            if ((x & 0x80) != 0)
            {
                return data;
            }

            shift <<= 7;
            data += shift;
        }

        throw new OverflowException();
    }

    /// <summary>BPS の符号付きの相対位置 (最下位ビットが符号)。</summary>
    public static void WriteSigned(Stream output, long value) =>
        Write(output, (ulong)Math.Abs(value) << 1 | (value < 0 ? 1UL : 0));

    public static long ReadSigned(Stream input)
    {
        ulong v = Read(input);
        long magnitude = checked((long)(v >> 1));
        return (v & 1) != 0 ? -magnitude : magnitude;
    }
}

/// <summary>CRC-32 を計算しながら書くストリーム (パッチ自身の CRC-32)。</summary>
internal sealed class CrcWriteStream(Stream inner) : Stream
{
    private uint _state = PatchCrc32.Initial;

    public uint Crc => PatchCrc32.Finish(_state);

    public long Written { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _state = PatchCrc32.Append(_state, buffer);
        Written += buffer.Length;
        inner.Write(buffer);
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
