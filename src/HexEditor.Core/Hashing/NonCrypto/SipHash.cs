using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing;

/// <summary>
/// SipHash-c-d (Aumasson と Bernstein の論文、公式リポジトリ veorq/SipHash の siphash.c)。鍵は 16 バイト (k0、k1 をリトルエンディアンで読む)。
/// 64 bit 版の値は数値としての表記 (ビッグエンディアン。公式の vectors.h のバイト列はこれを逆順にしたもの)。128 bit 版の値は公式の実装が
/// 出力するバイト列 (2 つの 64 bit の値をそれぞれリトルエンディアンで並べたもの。vectors.h の vectors_sip128 と同じ)。
/// </summary>
public sealed class SipHasher : IHasher
{
    private readonly int _c;
    private readonly int _d;
    private readonly bool _wide;
    private readonly byte[] _partial = new byte[8];
    private int _partialCount;
    private ulong _total;
    private ulong _v0, _v1, _v2, _v3;

    public SipHasher(ReadOnlySpan<byte> key, int compressionRounds, int finalizationRounds, bool output128)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("SipHash の鍵は 16 バイトです。", nameof(key));
        }

        _c = compressionRounds;
        _d = finalizationRounds;
        _wide = output128;
        ulong k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);
        _v0 = k0 ^ 0x736F6D6570736575;
        _v1 = k1 ^ 0x646F72616E646F6D;
        _v2 = k0 ^ 0x6C7967656E657261;
        _v3 = k1 ^ 0x7465646279746573;
        if (output128)
        {
            _v1 ^= 0xEE;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_partialCount > 0)
        {
            int n = Math.Min(8 - _partialCount, data.Length);
            data[..n].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += n;
            data = data[n..];
            if (_partialCount < 8)
            {
                return;
            }

            Compress(BinaryPrimitives.ReadUInt64LittleEndian(_partial));
            _partialCount = 0;
        }

        while (data.Length >= 8)
        {
            Compress(BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        data.CopyTo(_partial);
        _partialCount = data.Length;
    }

    public byte[] Finish()
    {
        ulong b = _total << 56;
        for (int i = 0; i < _partialCount; i++)
        {
            b |= (ulong)_partial[i] << (8 * i);
        }

        Compress(b);
        _v2 ^= _wide ? 0xEEUL : 0xFFUL;
        Rounds(_d);
        ulong first = _v0 ^ _v1 ^ _v2 ^ _v3;
        if (!_wide)
        {
            return HashBytes.FromNumber(first, 8);
        }

        _v1 ^= 0xDD;
        Rounds(_d);
        ulong second = _v0 ^ _v1 ^ _v2 ^ _v3;
        byte[] result = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(result, first);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), second);
        return result;
    }

    private void Compress(ulong m)
    {
        _v3 ^= m;
        Rounds(_c);
        _v0 ^= m;
    }

    private void Rounds(int count)
    {
        ulong v0 = _v0, v1 = _v1, v2 = _v2, v3 = _v3;
        for (int i = 0; i < count; i++)
        {
            unchecked
            {
                v0 += v1;
                v1 = BitOperations.RotateLeft(v1, 13);
                v1 ^= v0;
                v0 = BitOperations.RotateLeft(v0, 32);
                v2 += v3;
                v3 = BitOperations.RotateLeft(v3, 16);
                v3 ^= v2;
                v0 += v3;
                v3 = BitOperations.RotateLeft(v3, 21);
                v3 ^= v0;
                v2 += v1;
                v1 = BitOperations.RotateLeft(v1, 17);
                v1 ^= v2;
                v2 = BitOperations.RotateLeft(v2, 32);
            }
        }

        _v0 = v0;
        _v1 = v1;
        _v2 = v2;
        _v3 = v3;
    }
}
