using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>BLAKE2 の共通の定数 (RFC 7693)。</summary>
internal static class Blake2Sigma
{
    /// <summary>メッセージのワードの並べ替え (10 行。BLAKE2b の 11・12 ラウンドは 0・1 行目を使う)。</summary>
    public static ReadOnlySpan<byte> Table =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];
}

/// <summary>
/// BLAKE2b (RFC 7693)。出力 1〜64 バイト、鍵 0〜64 バイト。最後のブロックには終わりの印を付けるため、
/// 揃ったブロックも次のデータが来るまで処理しない。
/// </summary>
public sealed class Blake2bHasher : IHasher
{
    private const int BlockSize = 128;

    private static ReadOnlySpan<ulong> IV =>
    [
        0x6A09E667F3BCC908, 0xBB67AE8584CAA73B, 0x3C6EF372FE94F82B, 0xA54FF53A5F1D36F1,
        0x510E527FADE682D1, 0x9B05688C2B3E6C1F, 0x1F83D9ABFB41BD6B, 0x5BE0CD19137E2179,
    ];

    private readonly ulong[] _h = new ulong[8];
    private readonly byte[] _buffer = new byte[BlockSize];
    private readonly int _outputBytes;
    private int _buffered;
    private UInt128 _counter;

    public Blake2bHasher(int outputBytes = 64, ReadOnlySpan<byte> key = default)
    {
        if (outputBytes is < 1 or > 64 || key.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBytes));
        }

        _outputBytes = outputBytes;
        IV.CopyTo(_h);
        _h[0] ^= 0x01010000UL ^ ((ulong)key.Length << 8) ^ (ulong)outputBytes;
        if (key.Length > 0)
        {
            key.CopyTo(_buffer);
            _buffered = BlockSize;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_buffered == BlockSize)
            {
                _counter += BlockSize;
                Compress(_buffer, false);
                _buffered = 0;
            }

            if (_buffered == 0)
            {
                // 最後のブロックになり得ないものは入力から直接処理する。
                while (data.Length > BlockSize)
                {
                    _counter += BlockSize;
                    Compress(data[..BlockSize], false);
                    data = data[BlockSize..];
                }
            }

            int n = Math.Min(data.Length, BlockSize - _buffered);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
        }
    }

    public byte[] Finish()
    {
        _counter += (ulong)_buffered;
        _buffer.AsSpan(_buffered).Clear();
        Compress(_buffer, true);
        Span<byte> full = stackalloc byte[64];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(full[(8 * i)..], _h[i]);
        }

        return full[.._outputBytes].ToArray();
    }

    private void Compress(ReadOnlySpan<byte> block, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(8 * i)..]);
        }

        _h.CopyTo(v);
        IV.CopyTo(v[8..]);
        v[12] ^= (ulong)_counter;
        v[13] ^= (ulong)(_counter >> 64);
        if (last)
        {
            v[14] = ~v[14];
        }

        ReadOnlySpan<byte> sigma = Blake2Sigma.Table;
        for (int round = 0; round < 12; round++)
        {
            ReadOnlySpan<byte> s = sigma.Slice(16 * (round % 10), 16);
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (int i = 0; i < 8; i++)
        {
            _h[i] ^= v[i] ^ v[i + 8];
        }
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}

/// <summary>BLAKE2s (RFC 7693)。出力 1〜32 バイト、鍵 0〜32 バイト。</summary>
public sealed class Blake2sHasher : IHasher
{
    private const int BlockSize = 64;

    private static ReadOnlySpan<uint> IV =>
        [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    private readonly uint[] _h = new uint[8];
    private readonly byte[] _buffer = new byte[BlockSize];
    private readonly int _outputBytes;
    private int _buffered;
    private ulong _counter;

    public Blake2sHasher(int outputBytes = 32, ReadOnlySpan<byte> key = default)
    {
        if (outputBytes is < 1 or > 32 || key.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBytes));
        }

        _outputBytes = outputBytes;
        IV.CopyTo(_h);
        _h[0] ^= 0x01010000U ^ ((uint)key.Length << 8) ^ (uint)outputBytes;
        if (key.Length > 0)
        {
            key.CopyTo(_buffer);
            _buffered = BlockSize;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_buffered == BlockSize)
            {
                _counter += BlockSize;
                Compress(_buffer, false);
                _buffered = 0;
            }

            if (_buffered == 0)
            {
                while (data.Length > BlockSize)
                {
                    _counter += BlockSize;
                    Compress(data[..BlockSize], false);
                    data = data[BlockSize..];
                }
            }

            int n = Math.Min(data.Length, BlockSize - _buffered);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
        }
    }

    public byte[] Finish()
    {
        _counter += (ulong)_buffered;
        _buffer.AsSpan(_buffered).Clear();
        Compress(_buffer, true);
        Span<byte> full = stackalloc byte[32];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(full[(4 * i)..], _h[i]);
        }

        return full[.._outputBytes].ToArray();
    }

    private void Compress(ReadOnlySpan<byte> block, bool last)
    {
        Span<uint> m = stackalloc uint[16];
        Span<uint> v = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);
        }

        _h.CopyTo(v);
        IV.CopyTo(v[8..]);
        v[12] ^= (uint)_counter;
        v[13] ^= (uint)(_counter >> 32);
        if (last)
        {
            v[14] = ~v[14];
        }

        ReadOnlySpan<byte> sigma = Blake2Sigma.Table;
        for (int round = 0; round < 10; round++)
        {
            ReadOnlySpan<byte> s = sigma.Slice(16 * round, 16);
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (int i = 0; i < 8; i++)
        {
            _h[i] ^= v[i] ^ v[i + 8];
        }
    }

    private static void G(Span<uint> v, int a, int b, int c, int d, uint x, uint y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
    }
}
