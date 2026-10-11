using System.Buffers.Binary;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// 固定長のブロックごとに圧縮関数を呼ぶハッシュの共通部分 (MD4、SHA-2、RIPEMD、Tiger、Whirlpool)。
/// 端数だけを内部のバッファに持ち、揃ったブロックは入力から直接処理する (Append ごとの割り当てはない)。
/// </summary>
public abstract class BlockHasher : IHasher
{
    private readonly byte[] _buffer;
    private int _buffered;
    private bool _finished;

    protected BlockHasher(int blockSize) => _buffer = new byte[blockSize];

    protected int BlockSize => _buffer.Length;

    /// <summary>これまでに渡したバイト数。</summary>
    protected ulong Length { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        Length += (ulong)data.Length;
        if (_buffered > 0)
        {
            int n = Math.Min(data.Length, _buffer.Length - _buffered);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
            if (_buffered < _buffer.Length)
            {
                return;
            }

            ProcessBlock(_buffer);
            _buffered = 0;
        }

        while (data.Length >= _buffer.Length)
        {
            ProcessBlock(data[.._buffer.Length]);
            data = data[_buffer.Length..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    public byte[] Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException("Finish は 1 回だけ呼べます。");
        }

        _finished = true;
        return FinishCore();
    }

    /// <summary>1 ブロックを処理する。</summary>
    protected abstract void ProcessBlock(ReadOnlySpan<byte> block);

    /// <summary>パディングを加えて値を返す。</summary>
    protected abstract byte[] FinishCore();

    /// <summary>
    /// MD 方式のパディング: 区切りのバイト、0 の並び、末尾にビット長 (<paramref name="lengthBytes"/> バイト。8 バイトを超える部分の
    /// 上位は 0 か、ビット長のあふれ)。
    /// </summary>
    protected void PadMd(byte marker, int lengthBytes, bool bigEndianLength)
    {
        ulong bits = Length << 3;
        ulong high = Length >> 61;
        Span<byte> tail = stackalloc byte[2 * 128];
        int total = _buffered + 1 + lengthBytes <= _buffer.Length ? _buffer.Length : 2 * _buffer.Length;
        tail = tail[..total];
        tail.Clear();
        _buffer.AsSpan(0, _buffered).CopyTo(tail);
        tail[_buffered] = marker;
        Span<byte> len = tail[(total - lengthBytes)..];
        if (bigEndianLength)
        {
            BinaryPrimitives.WriteUInt64BigEndian(len[^8..], bits);
            if (lengthBytes > 8)
            {
                BinaryPrimitives.WriteUInt64BigEndian(len[^16..^8], high);
            }
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(len, bits);
            if (lengthBytes > 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(len[8..], high);
            }
        }

        for (int at = 0; at < total; at += _buffer.Length)
        {
            ProcessBlock(tail.Slice(at, _buffer.Length));
        }

        _buffered = 0;
    }

    /// <summary>最初の状態に戻す (派生クラスは自分の連鎖変数も戻す)。</summary>
    protected void ResetBuffer()
    {
        _buffered = 0;
        _finished = false;
        Length = 0;
    }

    /// <summary>端数のバッファ (MD2 など独自のパディングを持つもの向け)。</summary>
    protected ReadOnlySpan<byte> Pending => _buffer.AsSpan(0, _buffered);
}
