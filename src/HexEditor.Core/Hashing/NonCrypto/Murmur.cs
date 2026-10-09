using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing;

/// <summary>
/// 計算の始めにデータ全体の長さが必要なアルゴリズム (MurmurHash2、MurmurHash64A)。<see cref="HashEngine"/> は最初の
/// <see cref="IHasher.Append"/> の前に <see cref="DeclareLength"/> で長さを渡す。長さを渡さずに使った場合は、データを
/// 内部に溜めて <see cref="IHasher.Finish"/> で計算する (小さなデータ用)。
/// </summary>
public interface ILengthPrefixedHasher : IHasher
{
    /// <summary>これから渡すデータの長さの合計を宣言する。</summary>
    void DeclareLength(long totalLength);
}

/// <summary>
/// MurmurHash2 (32 bit) と MurmurHash64A (SMHasher の MurmurHash2.cpp)。要素はリトルエンディアンで読む。シードは
/// MurmurHash2 が下位 32 bit、MurmurHash64A が 64 bit。長さは MurmurHash2 が下位 32 bit を使う (元の実装の <c>int len</c>)。
/// 値は数値としての表記 (ビッグエンディアン)。
/// </summary>
public sealed class Murmur2Hasher : ILengthPrefixedHasher
{
    private const uint M32 = 0x5BD1E995;
    private const ulong M64 = 0xC6A4A7935BD1E995;

    private readonly bool _wide;
    private readonly ulong _seed;
    private readonly byte[] _partial = new byte[8];
    private int _partialCount;
    private ulong _h;
    private long _declared = -1;
    private long _total;
    private ArrayBufferWriter<byte>? _pending;

    public Murmur2Hasher(bool wide, ulong seed)
    {
        _wide = wide;
        _seed = wide ? seed : (uint)seed;
    }

    public void DeclareLength(long totalLength)
    {
        if (_total != 0 || _pending is { WrittenCount: > 0 })
        {
            throw new InvalidOperationException("長さはデータを渡す前に宣言します。");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(totalLength);
        _declared = totalLength;
        _h = _wide ? _seed ^ unchecked((ulong)totalLength * M64) : (uint)_seed ^ (uint)totalLength;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_declared < 0)
        {
            (_pending ??= new ArrayBufferWriter<byte>()).Write(data);
            return;
        }

        _total += data.Length;
        int block = _wide ? 8 : 4;
        if (_partialCount > 0)
        {
            int n = Math.Min(block - _partialCount, data.Length);
            data[..n].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += n;
            data = data[n..];
            if (_partialCount < block)
            {
                return;
            }

            Blocks(_partial.AsSpan(0, block));
            _partialCount = 0;
        }

        int whole = data.Length - (data.Length % block);
        Blocks(data[..whole]);
        data[whole..].CopyTo(_partial);
        _partialCount = data.Length - whole;
    }

    public byte[] Finish()
    {
        if (_declared < 0)
        {
            ArrayBufferWriter<byte>? pending = _pending;
            _pending = null;
            ReadOnlySpan<byte> all = pending is null ? [] : pending.WrittenSpan;
            DeclareLength(all.Length);
            Append(all);
        }

        if (_total != _declared)
        {
            throw new InvalidOperationException("宣言した長さと渡したデータの長さが違います。");
        }

        ReadOnlySpan<byte> tail = _partial.AsSpan(0, _partialCount);
        if (_wide)
        {
            ulong h = _h;
            if (tail.Length > 0)
            {
                for (int i = tail.Length - 1; i >= 0; i--)
                {
                    h ^= (ulong)tail[i] << (8 * i);
                }

                h = unchecked(h * M64);
            }

            h ^= h >> 47;
            h = unchecked(h * M64);
            h ^= h >> 47;
            return HashBytes.FromNumber(h, 8);
        }
        else
        {
            uint h = (uint)_h;
            if (tail.Length > 0)
            {
                for (int i = tail.Length - 1; i >= 0; i--)
                {
                    h ^= (uint)tail[i] << (8 * i);
                }

                h = unchecked(h * M32);
            }

            h ^= h >> 13;
            h = unchecked(h * M32);
            h ^= h >> 15;
            return HashBytes.FromNumber(h, 4);
        }
    }

    private void Blocks(ReadOnlySpan<byte> data)
    {
        if (_wide)
        {
            ulong h = _h;
            for (int i = 0; i + 8 <= data.Length; i += 8)
            {
                ulong k = unchecked(BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) * M64);
                k ^= k >> 47;
                k = unchecked(k * M64);
                h ^= k;
                h = unchecked(h * M64);
            }

            _h = h;
        }
        else
        {
            uint h = (uint)_h;
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                uint k = unchecked(BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * M32);
                k ^= k >> 24;
                k = unchecked(k * M32);
                h = unchecked(h * M32) ^ k;
            }

            _h = h;
        }
    }
}

/// <summary>
/// MurmurHash3 の x86_32 / x86_128 / x64_128 (SMHasher の MurmurHash3.cpp)。シードは下位 32 bit。長さは x86 が下位 32 bit、
/// x64 が 64 bit。x86_32 の値は数値としての表記 (ビッグエンディアン)。128 bit の値は元の実装が出力するバイト列
/// (x86_128 は h1〜h4、x64_128 は h1・h2 をそれぞれリトルエンディアンで並べたもの。Python の mmh3.hash_bytes と同じ)。
/// </summary>
public sealed class Murmur3Hasher : IHasher
{
    private const uint C1x86 = 0x239B961B, C2x86 = 0xAB0E9789, C3x86 = 0x38B34AE5, C4x86 = 0xA1E38B93;
    private const ulong C1x64 = 0x87C37B91114253D5, C2x64 = 0x4CF5AD432745937F;

    private readonly Murmur3Variant _variant;
    private readonly byte[] _partial = new byte[16];
    private readonly int _block;
    private int _partialCount;
    private ulong _total;
    private uint _h1, _h2, _h3, _h4;
    private ulong _g1, _g2;

    public Murmur3Hasher(Murmur3Variant variant, ulong seed)
    {
        _variant = variant;
        uint s = (uint)seed;
        _h1 = _h2 = _h3 = _h4 = s;
        _g1 = _g2 = s;
        _block = variant == Murmur3Variant.X86_32 ? 4 : 16;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_partialCount > 0)
        {
            int n = Math.Min(_block - _partialCount, data.Length);
            data[..n].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += n;
            data = data[n..];
            if (_partialCount < _block)
            {
                return;
            }

            Blocks(_partial.AsSpan(0, _block));
            _partialCount = 0;
        }

        int whole = data.Length - (data.Length % _block);
        Blocks(data[..whole]);
        data[whole..].CopyTo(_partial);
        _partialCount = data.Length - whole;
    }

    public byte[] Finish()
    {
        ReadOnlySpan<byte> tail = _partial.AsSpan(0, _partialCount);
        switch (_variant)
        {
            case Murmur3Variant.X86_32:
                {
                    uint h1 = _h1;
                    if (tail.Length > 0)
                    {
                        h1 ^= MixK1x86_32(TailWord32(tail, 0));
                    }

                    h1 ^= (uint)_total;
                    return HashBytes.FromNumber(Fmix32(h1), 4);
                }

            case Murmur3Variant.X86_128:
                {
                    uint h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
                    if (tail.Length > 12)
                    {
                        h4 ^= unchecked(BitOperations.RotateLeft(TailWord32(tail, 12) * C4x86, 18) * C1x86);
                    }

                    if (tail.Length > 8)
                    {
                        h3 ^= unchecked(BitOperations.RotateLeft(TailWord32(tail, 8) * C3x86, 17) * C4x86);
                    }

                    if (tail.Length > 4)
                    {
                        h2 ^= unchecked(BitOperations.RotateLeft(TailWord32(tail, 4) * C2x86, 16) * C3x86);
                    }

                    if (tail.Length > 0)
                    {
                        h1 ^= unchecked(BitOperations.RotateLeft(TailWord32(tail, 0) * C1x86, 15) * C2x86);
                    }

                    uint len = (uint)_total;
                    h1 ^= len;
                    h2 ^= len;
                    h3 ^= len;
                    h4 ^= len;
                    unchecked
                    {
                        h1 += h2 + h3 + h4;
                        h2 += h1;
                        h3 += h1;
                        h4 += h1;
                        h1 = Fmix32(h1);
                        h2 = Fmix32(h2);
                        h3 = Fmix32(h3);
                        h4 = Fmix32(h4);
                        h1 += h2 + h3 + h4;
                        h2 += h1;
                        h3 += h1;
                        h4 += h1;
                    }

                    byte[] result = new byte[16];
                    BinaryPrimitives.WriteUInt32LittleEndian(result, h1);
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), h2);
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), h3);
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), h4);
                    return result;
                }

            default:
                {
                    ulong h1 = _g1, h2 = _g2;
                    if (tail.Length > 8)
                    {
                        h2 ^= unchecked(BitOperations.RotateLeft(TailWord64(tail, 8) * C2x64, 33) * C1x64);
                    }

                    if (tail.Length > 0)
                    {
                        h1 ^= unchecked(BitOperations.RotateLeft(TailWord64(tail, 0) * C1x64, 31) * C2x64);
                    }

                    h1 ^= _total;
                    h2 ^= _total;
                    unchecked
                    {
                        h1 += h2;
                        h2 += h1;
                        h1 = Fmix64(h1);
                        h2 = Fmix64(h2);
                        h1 += h2;
                        h2 += h1;
                    }

                    byte[] result = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(result, h1);
                    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), h2);
                    return result;
                }
        }
    }

    private void Blocks(ReadOnlySpan<byte> data)
    {
        switch (_variant)
        {
            case Murmur3Variant.X86_32:
                {
                    uint h1 = _h1;
                    for (int i = 0; i + 4 <= data.Length; i += 4)
                    {
                        h1 ^= MixK1x86_32(BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
                        h1 = unchecked((BitOperations.RotateLeft(h1, 13) * 5) + 0xE6546B64);
                    }

                    _h1 = h1;
                    break;
                }

            case Murmur3Variant.X86_128:
                {
                    uint h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
                    for (int i = 0; i + 16 <= data.Length; i += 16)
                    {
                        uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                        uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                        uint k3 = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 8)..]);
                        uint k4 = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 12)..]);
                        unchecked
                        {
                            h1 ^= BitOperations.RotateLeft(k1 * C1x86, 15) * C2x86;
                            h1 = (BitOperations.RotateLeft(h1, 19) + h2) * 5 + 0x561CCD1B;
                            h2 ^= BitOperations.RotateLeft(k2 * C2x86, 16) * C3x86;
                            h2 = (BitOperations.RotateLeft(h2, 17) + h3) * 5 + 0x0BCAA747;
                            h3 ^= BitOperations.RotateLeft(k3 * C3x86, 17) * C4x86;
                            h3 = (BitOperations.RotateLeft(h3, 15) + h4) * 5 + 0x96CD1C35;
                            h4 ^= BitOperations.RotateLeft(k4 * C4x86, 18) * C1x86;
                            h4 = (BitOperations.RotateLeft(h4, 13) + h1) * 5 + 0x32AC3B17;
                        }
                    }

                    _h1 = h1;
                    _h2 = h2;
                    _h3 = h3;
                    _h4 = h4;
                    break;
                }

            default:
                {
                    ulong h1 = _g1, h2 = _g2;
                    for (int i = 0; i + 16 <= data.Length; i += 16)
                    {
                        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
                        ulong k2 = BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]);
                        unchecked
                        {
                            h1 ^= BitOperations.RotateLeft(k1 * C1x64, 31) * C2x64;
                            h1 = (BitOperations.RotateLeft(h1, 27) + h2) * 5 + 0x52DCE729;
                            h2 ^= BitOperations.RotateLeft(k2 * C2x64, 33) * C1x64;
                            h2 = (BitOperations.RotateLeft(h2, 31) + h1) * 5 + 0x38495AB5;
                        }
                    }

                    _g1 = h1;
                    _g2 = h2;
                    break;
                }
        }
    }

    private static uint MixK1x86_32(uint k1) => unchecked(BitOperations.RotateLeft(k1 * 0xCC9E2D51, 15) * 0x1B873593);

    /// <summary>端数の <paramref name="at"/> からの最大 4 バイトをリトルエンディアンの 32 bit にする (足りない上位は 0)。</summary>
    private static uint TailWord32(ReadOnlySpan<byte> tail, int at)
    {
        uint k = 0;
        for (int i = Math.Min(tail.Length, at + 4) - 1; i >= at; i--)
        {
            k = (k << 8) | tail[i];
        }

        return k;
    }

    private static ulong TailWord64(ReadOnlySpan<byte> tail, int at)
    {
        ulong k = 0;
        for (int i = Math.Min(tail.Length, at + 8) - 1; i >= at; i--)
        {
            k = (k << 8) | tail[i];
        }

        return k;
    }

    private static uint Fmix32(uint h)
    {
        h ^= h >> 16;
        h = unchecked(h * 0x85EBCA6B);
        h ^= h >> 13;
        h = unchecked(h * 0xC2B2AE35);
        h ^= h >> 16;
        return h;
    }

    private static ulong Fmix64(ulong k)
    {
        k ^= k >> 33;
        k = unchecked(k * 0xFF51AFD7ED558CCD);
        k ^= k >> 33;
        k = unchecked(k * 0xC4CEB9FE1A85EC53);
        k ^= k >> 33;
        return k;
    }
}

/// <summary>MurmurHash3 の種類。</summary>
public enum Murmur3Variant
{
    X86_32,
    X86_128,
    X64_128,
}
