namespace HexEditor.Core.Hashing;

/// <summary>
/// FNV-1 / FNV-1a の 32 / 64 / 128 bit (draft-eastlake-fnv)。FNV-1 は「掛けてから XOR」、FNV-1a は「XOR してから掛ける」。
/// 値は数値としての表記 (ビッグエンディアン。128 bit も draft-eastlake-fnv のテストベクタと同じ上位バイトからの並び)。
/// </summary>
public sealed class FnvHasher : IHasher
{
    private const uint Prime32 = 0x01000193;
    private const ulong Prime64 = 0x00000100000001B3;

    /// <summary>128 bit の素数 2^88 + 0x13B の下位部分。</summary>
    private const ulong Prime128Low = 0x13B;

    private readonly int _bits;
    private readonly bool _a;
    private ulong _lo;
    private ulong _hi;

    public FnvHasher(int bits, bool fnv1a)
    {
        _bits = bits;
        _a = fnv1a;
        switch (bits)
        {
            case 32:
                _lo = 0x811C9DC5;
                break;
            case 64:
                _lo = 0xCBF29CE484222325;
                break;
            case 128:
                _hi = 0x6C62272E07BB0142;
                _lo = 0x62B821756295C58D;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bits));
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        switch (_bits)
        {
            case 32:
                {
                    uint h = (uint)_lo;
                    if (_a)
                    {
                        foreach (byte b in data)
                        {
                            h = unchecked((h ^ b) * Prime32);
                        }
                    }
                    else
                    {
                        foreach (byte b in data)
                        {
                            h = unchecked(h * Prime32) ^ b;
                        }
                    }

                    _lo = h;
                    break;
                }

            case 64:
                {
                    ulong h = _lo;
                    if (_a)
                    {
                        foreach (byte b in data)
                        {
                            h = unchecked((h ^ b) * Prime64);
                        }
                    }
                    else
                    {
                        foreach (byte b in data)
                        {
                            h = unchecked(h * Prime64) ^ b;
                        }
                    }

                    _lo = h;
                    break;
                }

            default:
                {
                    ulong lo = _lo, hi = _hi;
                    foreach (byte b in data)
                    {
                        if (_a)
                        {
                            lo ^= b;
                        }

                        Multiply128(ref hi, ref lo);
                        if (!_a)
                        {
                            lo ^= b;
                        }
                    }

                    _lo = lo;
                    _hi = hi;
                    break;
                }
        }
    }

    public byte[] Finish()
    {
        if (_bits != 128)
        {
            return HashBytes.FromNumber(_lo, _bits / 8);
        }

        byte[] result = new byte[16];
        HashBytes.FromNumber(_hi, 8).CopyTo(result, 0);
        HashBytes.FromNumber(_lo, 8).CopyTo(result, 8);
        return result;
    }

    /// <summary>(hi:lo) に 2^88 + 0x13B を掛ける (2^128 で切り捨て)。</summary>
    private static void Multiply128(ref ulong hi, ref ulong lo)
    {
        ulong productHigh = Math.BigMul(lo, Prime128Low, out ulong productLow);
        hi = unchecked(productHigh + (hi * Prime128Low) + (lo << 24));
        lo = productLow;
    }
}
