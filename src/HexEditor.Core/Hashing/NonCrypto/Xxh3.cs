using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing;

/// <summary>
/// XXH3-64 / XXH3-128 (xxHash の公式の定義 xxhash.h。シード付き)。ストリーミングは公式の XXH3_update / XXH3_digest と同じ方式
/// (256 バイトの内部バッファ)。XXH3-64 の値は数値としての表記 (ビッグエンディアン)。XXH3-128 の値は公式の正規表現
/// (XXH128_canonicalFromHash。上位 64 bit、下位 64 bit の順にそれぞれビッグエンディアン。<c>xxhsum -H2</c> の表示と同じ)。
/// </summary>
public sealed class Xxh3Hasher : IHasher
{
    private const uint P32_1 = 0x9E3779B1U, P32_2 = 0x85EBCA77U, P32_3 = 0xC2B2AE3DU;
    private const ulong P64_1 = 0x9E3779B185EBCA87UL, P64_2 = 0xC2B2AE3D27D4EB4FUL, P64_3 = 0x165667B19E3779F9UL,
        P64_4 = 0x85EBCA77C2B2AE63UL, P64_5 = 0x27D4EB2F165667C5UL;
    private const ulong PrimeMx1 = 0x165667919E3779F9UL, PrimeMx2 = 0x9FB21C651E98DF25UL;

    private const int StripeLen = 64;
    private const int SecretConsumeRate = 8;
    private const int SecretSize = 192;
    private const int SecretLimit = SecretSize - StripeLen;
    private const int StripesPerBlock = SecretLimit / SecretConsumeRate;
    private const int BufferSize = 256;
    private const int MidSizeMax = 240;
    private const int SecretSizeMin = 136;
    private const int MidSizeStartOffset = 3;
    private const int MidSizeLastOffset = 17;
    private const int SecretLastAccStart = 7;
    private const int SecretMergeAccsStart = 11;

    /// <summary>既定の秘密 (XXH3_kSecret)。</summary>
    private static readonly byte[] KSecret =
    [
        0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
        0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
        0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
        0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
        0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
        0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
        0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
        0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
        0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
        0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
        0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
        0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
    ];

    private readonly ulong _seed;
    private readonly bool _wide;

    /// <summary>長いデータに使う秘密 (シードから作る。シード 0 では既定の秘密と同じ)。</summary>
    private readonly byte[] _secret;

    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly ulong[] _acc = new ulong[8];
    private int _buffered;
    private ulong _total;
    private int _stripesSoFar;

    public Xxh3Hasher(ulong seed, bool output128)
    {
        _seed = seed;
        _wide = output128;
        if (seed == 0)
        {
            _secret = KSecret;
        }
        else
        {
            _secret = new byte[SecretSize];
            for (int i = 0; i < SecretSize; i += 16)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_secret.AsSpan(i), unchecked(R64(KSecret, i) + seed));
                BinaryPrimitives.WriteUInt64LittleEndian(_secret.AsSpan(i + 8), unchecked(R64(KSecret, i + 8) - seed));
            }
        }

        InitAcc(_acc);
    }

    public void Append(ReadOnlySpan<byte> input)
    {
        _total += (ulong)input.Length;
        if (input.Length <= BufferSize - _buffered)
        {
            input.CopyTo(_buffer.AsSpan(_buffered));
            _buffered += input.Length;
            return;
        }

        if (_buffered > 0)
        {
            int load = BufferSize - _buffered;
            input[..load].CopyTo(_buffer.AsSpan(_buffered));
            input = input[load..];
            ConsumeStripes(_acc, ref _stripesSoFar, _buffer, BufferSize / StripeLen, _secret);
            _buffered = 0;
        }

        if (input.Length > BufferSize)
        {
            // 最後のバイトを含むストライプは最後に別の秘密で処理するため、ここでは処理しない。
            int stripes = (input.Length - 1) / StripeLen;
            ConsumeStripes(_acc, ref _stripesSoFar, input, stripes, _secret);

            // 最後のストライプの不足分を補えるよう、処理した最後のストライプをバッファの末尾に残す。
            input.Slice((stripes - 1) * StripeLen, StripeLen).CopyTo(_buffer.AsSpan(BufferSize - StripeLen));
            input = input[(stripes * StripeLen)..];
        }

        input.CopyTo(_buffer);
        _buffered = input.Length;
    }

    public byte[] Finish()
    {
        if (_total > MidSizeMax)
        {
            ulong[] acc = new ulong[8];
            DigestLong(acc);
            ulong low = MergeAccs(acc, _secret, SecretMergeAccsStart, unchecked(_total * P64_1));
            if (!_wide)
            {
                return HashBytes.FromNumber(low, 8);
            }

            ulong high = MergeAccs(acc, _secret, SecretSize - StripeLen - SecretMergeAccsStart, ~unchecked(_total * P64_2));
            return Canonical(high, low);
        }

        ReadOnlySpan<byte> data = _buffer.AsSpan(0, (int)_total);
        if (!_wide)
        {
            return HashBytes.FromNumber(Short64(data, _seed), 8);
        }

        (ulong h, ulong l) = Short128(data, _seed);
        return Canonical(h, l);
    }

    private void DigestLong(ulong[] acc)
    {
        _acc.CopyTo(acc, 0);
        Span<byte> lastStripe = stackalloc byte[StripeLen];
        if (_buffered >= StripeLen)
        {
            int stripes = (_buffered - 1) / StripeLen;
            int soFar = _stripesSoFar;
            ConsumeStripes(acc, ref soFar, _buffer, stripes, _secret);
            _buffer.AsSpan(_buffered - StripeLen, StripeLen).CopyTo(lastStripe);
        }
        else
        {
            int catchup = StripeLen - _buffered;
            _buffer.AsSpan(BufferSize - catchup, catchup).CopyTo(lastStripe);
            _buffer.AsSpan(0, _buffered).CopyTo(lastStripe[catchup..]);
        }

        Accumulate512(acc, lastStripe, _secret.AsSpan(SecretLimit - SecretLastAccStart));
    }

    private static void InitAcc(ulong[] acc)
    {
        acc[0] = P32_3;
        acc[1] = P64_1;
        acc[2] = P64_2;
        acc[3] = P64_3;
        acc[4] = P64_4;
        acc[5] = P32_2;
        acc[6] = P64_5;
        acc[7] = P32_1;
    }

    private static void ConsumeStripes(ulong[] acc, ref int stripesSoFar, ReadOnlySpan<byte> input, int stripes, byte[] secret)
    {
        int secretOffset = stripesSoFar * SecretConsumeRate;
        if (stripes >= StripesPerBlock - stripesSoFar)
        {
            int thisIter = StripesPerBlock - stripesSoFar;
            do
            {
                Accumulate(acc, input, secret.AsSpan(secretOffset), thisIter);
                Scramble(acc, secret.AsSpan(SecretLimit));
                input = input[(thisIter * StripeLen)..];
                stripes -= thisIter;
                thisIter = StripesPerBlock;
                secretOffset = 0;
            }
            while (stripes >= StripesPerBlock);
            stripesSoFar = 0;
        }

        if (stripes > 0)
        {
            Accumulate(acc, input, secret.AsSpan(secretOffset), stripes);
            stripesSoFar += stripes;
        }
    }

    private static void Accumulate(ulong[] acc, ReadOnlySpan<byte> input, ReadOnlySpan<byte> secret, int stripes)
    {
        for (int n = 0; n < stripes; n++)
        {
            Accumulate512(acc, input[(n * StripeLen)..], secret[(n * SecretConsumeRate)..]);
        }
    }

    private static void Accumulate512(Span<ulong> acc, ReadOnlySpan<byte> input, ReadOnlySpan<byte> secret)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong dataVal = BinaryPrimitives.ReadUInt64LittleEndian(input[(8 * i)..]);
            ulong dataKey = dataVal ^ BinaryPrimitives.ReadUInt64LittleEndian(secret[(8 * i)..]);
            unchecked
            {
                acc[i ^ 1] += dataVal;
                acc[i] += (dataKey & 0xFFFFFFFF) * (dataKey >> 32);
            }
        }
    }

    private static void Scramble(ulong[] acc, ReadOnlySpan<byte> secret)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong a = acc[i];
            a ^= a >> 47;
            a ^= BinaryPrimitives.ReadUInt64LittleEndian(secret[(8 * i)..]);
            acc[i] = unchecked(a * P32_1);
        }
    }

    private static ulong MergeAccs(ulong[] acc, byte[] secret, int offset, ulong start)
    {
        ulong result = start;
        for (int i = 0; i < 4; i++)
        {
            result = unchecked(result + MulFold64(acc[2 * i] ^ R64(secret, offset + (16 * i)), acc[(2 * i) + 1] ^ R64(secret, offset + (16 * i) + 8)));
        }

        return Avalanche(result);
    }

    private static byte[] Canonical(ulong high, ulong low)
    {
        byte[] result = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(result, high);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), low);
        return result;
    }

    // ---- 240 バイト以下 (既定の秘密とシードを使う) ----

    private static ulong Short64(ReadOnlySpan<byte> input, ulong seed)
    {
        byte[] s = KSecret;
        int len = input.Length;
        unchecked
        {
            if (len <= 16)
            {
                if (len > 8)
                {
                    ulong bitflip1 = (R64(s, 24) ^ R64(s, 32)) + seed;
                    ulong bitflip2 = (R64(s, 40) ^ R64(s, 48)) - seed;
                    ulong inputLo = R64(input, 0) ^ bitflip1;
                    ulong inputHi = R64(input, len - 8) ^ bitflip2;
                    ulong acc = (ulong)len + BinaryPrimitives.ReverseEndianness(inputLo) + inputHi + MulFold64(inputLo, inputHi);
                    return Avalanche(acc);
                }

                if (len >= 4)
                {
                    ulong sd = seed ^ ((ulong)BinaryPrimitives.ReverseEndianness((uint)seed) << 32);
                    uint input1 = R32(input, 0);
                    uint input2 = R32(input, len - 4);
                    ulong bitflip = (R64(s, 8) ^ R64(s, 16)) - sd;
                    ulong input64 = input2 + ((ulong)input1 << 32);
                    return Rrmxmx(input64 ^ bitflip, (ulong)len);
                }

                if (len > 0)
                {
                    uint combined = ((uint)input[0] << 16) | ((uint)input[len >> 1] << 24) | input[len - 1] | ((uint)len << 8);
                    ulong bitflip = (R32(s, 0) ^ R32(s, 4)) + seed;
                    return Xxh64Avalanche(combined ^ bitflip);
                }

                return Xxh64Avalanche(seed ^ R64(s, 56) ^ R64(s, 64));
            }

            if (len <= 128)
            {
                ulong acc = (ulong)len * P64_1;
                if (len > 32)
                {
                    if (len > 64)
                    {
                        if (len > 96)
                        {
                            acc += Mix16B(input[48..], s, 96, seed);
                            acc += Mix16B(input[(len - 64)..], s, 112, seed);
                        }

                        acc += Mix16B(input[32..], s, 64, seed);
                        acc += Mix16B(input[(len - 48)..], s, 80, seed);
                    }

                    acc += Mix16B(input[16..], s, 32, seed);
                    acc += Mix16B(input[(len - 32)..], s, 48, seed);
                }

                acc += Mix16B(input, s, 0, seed);
                acc += Mix16B(input[(len - 16)..], s, 16, seed);
                return Avalanche(acc);
            }

            {
                ulong acc = (ulong)len * P64_1;
                int rounds = len / 16;
                for (int i = 0; i < 8; i++)
                {
                    acc += Mix16B(input[(16 * i)..], s, 16 * i, seed);
                }

                ulong accEnd = Mix16B(input[(len - 16)..], s, SecretSizeMin - MidSizeLastOffset, seed);
                acc = Avalanche(acc);
                for (int i = 8; i < rounds; i++)
                {
                    accEnd += Mix16B(input[(16 * i)..], s, (16 * (i - 8)) + MidSizeStartOffset, seed);
                }

                return Avalanche(acc + accEnd);
            }
        }
    }

    private static (ulong High, ulong Low) Short128(ReadOnlySpan<byte> input, ulong seed)
    {
        byte[] s = KSecret;
        int len = input.Length;
        unchecked
        {
            if (len <= 16)
            {
                if (len > 8)
                {
                    ulong bitflipl = (R64(s, 32) ^ R64(s, 40)) - seed;
                    ulong bitfliph = (R64(s, 48) ^ R64(s, 56)) + seed;
                    ulong inputLo = R64(input, 0);
                    ulong inputHi = R64(input, len - 8);
                    ulong mHigh = Math.BigMul(inputLo ^ inputHi ^ bitflipl, P64_1, out ulong mLow);
                    mLow += (ulong)(len - 1) << 54;
                    inputHi ^= bitfliph;
                    mHigh += inputHi + ((ulong)(uint)inputHi * (P32_2 - 1));
                    mLow ^= BinaryPrimitives.ReverseEndianness(mHigh);
                    ulong hHigh = Math.BigMul(mLow, P64_2, out ulong hLow);
                    hHigh += mHigh * P64_2;
                    return (Avalanche(hHigh), Avalanche(hLow));
                }

                if (len >= 4)
                {
                    ulong sd = seed ^ ((ulong)BinaryPrimitives.ReverseEndianness((uint)seed) << 32);
                    uint inputLo = R32(input, 0);
                    uint inputHi = R32(input, len - 4);
                    ulong input64 = inputLo + ((ulong)inputHi << 32);
                    ulong bitflip = (R64(s, 16) ^ R64(s, 24)) + sd;
                    ulong keyed = input64 ^ bitflip;
                    ulong mHigh = Math.BigMul(keyed, P64_1 + ((ulong)len << 2), out ulong mLow);
                    mHigh += mLow << 1;
                    mLow ^= mHigh >> 3;
                    mLow ^= mLow >> 35;
                    mLow *= PrimeMx2;
                    mLow ^= mLow >> 28;
                    mHigh = Avalanche(mHigh);
                    return (mHigh, mLow);
                }

                if (len > 0)
                {
                    uint combinedl = ((uint)input[0] << 16) | ((uint)input[len >> 1] << 24) | input[len - 1] | ((uint)len << 8);
                    uint combinedh = BitOperations.RotateLeft(BinaryPrimitives.ReverseEndianness(combinedl), 13);
                    ulong bitflipl = (R32(s, 0) ^ R32(s, 4)) + seed;
                    ulong bitfliph = (R32(s, 8) ^ R32(s, 12)) - seed;
                    return (Xxh64Avalanche(combinedh ^ bitfliph), Xxh64Avalanche(combinedl ^ bitflipl));
                }

                {
                    ulong bitflipl = R64(s, 64) ^ R64(s, 72);
                    ulong bitfliph = R64(s, 80) ^ R64(s, 88);
                    return (Xxh64Avalanche(seed ^ bitfliph), Xxh64Avalanche(seed ^ bitflipl));
                }
            }

            ulong accLow = (ulong)len * P64_1;
            ulong accHigh = 0;
            if (len <= 128)
            {
                if (len > 32)
                {
                    if (len > 64)
                    {
                        if (len > 96)
                        {
                            Mix32B(ref accLow, ref accHigh, input[48..], input[(len - 64)..], s, 96, seed);
                        }

                        Mix32B(ref accLow, ref accHigh, input[32..], input[(len - 48)..], s, 64, seed);
                    }

                    Mix32B(ref accLow, ref accHigh, input[16..], input[(len - 32)..], s, 32, seed);
                }

                Mix32B(ref accLow, ref accHigh, input, input[(len - 16)..], s, 0, seed);
            }
            else
            {
                for (int i = 32; i < 160; i += 32)
                {
                    Mix32B(ref accLow, ref accHigh, input[(i - 32)..], input[(i - 16)..], s, i - 32, seed);
                }

                accLow = Avalanche(accLow);
                accHigh = Avalanche(accHigh);
                for (int i = 160; i <= len; i += 32)
                {
                    Mix32B(ref accLow, ref accHigh, input[(i - 32)..], input[(i - 16)..], s, MidSizeStartOffset + i - 160, seed);
                }

                Mix32B(ref accLow, ref accHigh, input[(len - 16)..], input[(len - 32)..], s, SecretSizeMin - MidSizeLastOffset - 16, 0 - seed);
            }

            ulong low = accLow + accHigh;
            ulong high = (accLow * P64_1) + (accHigh * P64_4) + (((ulong)len - seed) * P64_2);
            return (0 - Avalanche(high), Avalanche(low));
        }
    }

    private static void Mix32B(ref ulong accLow, ref ulong accHigh, ReadOnlySpan<byte> input1, ReadOnlySpan<byte> input2, byte[] secret,
        int offset, ulong seed)
    {
        unchecked
        {
            accLow += Mix16B(input1, secret, offset, seed);
            accLow ^= R64(input2, 0) + R64(input2, 8);
            accHigh += Mix16B(input2, secret, offset + 16, seed);
            accHigh ^= R64(input1, 0) + R64(input1, 8);
        }
    }

    private static ulong Mix16B(ReadOnlySpan<byte> input, byte[] secret, int offset, ulong seed) => unchecked(MulFold64(
        R64(input, 0) ^ (R64(secret, offset) + seed),
        R64(input, 8) ^ (R64(secret, offset + 8) - seed)));

    private static ulong MulFold64(ulong lhs, ulong rhs)
    {
        ulong high = Math.BigMul(lhs, rhs, out ulong low);
        return low ^ high;
    }

    private static ulong Xxh64Avalanche(ulong h)
    {
        unchecked
        {
            h ^= h >> 33;
            h *= P64_2;
            h ^= h >> 29;
            h *= P64_3;
            h ^= h >> 32;
            return h;
        }
    }

    private static ulong Avalanche(ulong h)
    {
        h ^= h >> 37;
        h = unchecked(h * PrimeMx1);
        h ^= h >> 32;
        return h;
    }

    private static ulong Rrmxmx(ulong h, ulong len)
    {
        unchecked
        {
            h ^= BitOperations.RotateLeft(h, 49) ^ BitOperations.RotateLeft(h, 24);
            h *= PrimeMx2;
            h ^= (h >> 35) + len;
            h *= PrimeMx2;
            h ^= h >> 28;
            return h;
        }
    }

    private static ulong R64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(s[offset..]);

    private static uint R32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]);
}
