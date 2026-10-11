using System.Buffers.Binary;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// Whirlpool (ISO/IEC 10118-3 の版。NESSIE の最終版)。512 bit。S ボックスは仕様の E・R の 4 bit の小箱から作り、
/// 列の拡散 (θ) は巡回行列 cir(1, 1, 4, 1, 8, 5, 2, 9) を GF(2^8) (多項式 0x11D) で掛ける。表は起動時に計算する。
/// </summary>
public sealed class WhirlpoolHasher() : BlockHasher(64)
{
    private const int Rounds = 10;

    /// <summary>C[k][x]: 行の k 番目のバイトが x のときの、θ・γ 後の行への寄与 (バイト 0 が最上位)。</summary>
    private static readonly ulong[][] C;

    private static readonly ulong[] RoundConstants;

    static WhirlpoolHasher() => C = BuildTables(out RoundConstants);

    private readonly ulong[] _h = new ulong[8];

    internal static byte SBox(int u)
    {
        ReadOnlySpan<byte> e = [0x1, 0xB, 0x9, 0xC, 0xD, 0x6, 0xF, 0x3, 0xE, 0x8, 0x7, 0x4, 0xA, 0x2, 0x5, 0x0];
        ReadOnlySpan<byte> r = [0x7, 0xC, 0xB, 0xD, 0xE, 0x4, 0x9, 0xF, 0x6, 0x3, 0x8, 0xA, 0x2, 0x5, 0x1, 0x0];
        Span<byte> eInv = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            eInv[e[i]] = (byte)i;
        }

        int a = e[u >> 4];
        int b = eInv[u & 0xF];
        int t = r[a ^ b];
        return (byte)((e[a ^ t] << 4) | eInv[b ^ t]);
    }

    private static int Mul(int a, int b)
    {
        int p = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
            {
                p ^= a;
            }

            a <<= 1;
            if ((a & 0x100) != 0)
            {
                a ^= 0x11D;
            }

            b >>= 1;
        }

        return p;
    }

    private static ulong[][] BuildTables(out ulong[] roundConstants)
    {
        ReadOnlySpan<byte> row = [1, 1, 4, 1, 8, 5, 2, 9];
        var s = new byte[256];
        for (int u = 0; u < 256; u++)
        {
            s[u] = SBox(u);
        }

        var tables = new ulong[8][];
        for (int k = 0; k < 8; k++)
        {
            tables[k] = new ulong[256];
            for (int x = 0; x < 256; x++)
            {
                ulong v = 0;
                for (int j = 0; j < 8; j++)
                {
                    // 行列の (k, j) 成分は row[(j - k) mod 8]。
                    v |= (ulong)Mul(s[x], row[(j - k + 8) & 7]) << (56 - 8 * j);
                }

                tables[k][x] = v;
            }
        }

        roundConstants = new ulong[Rounds + 1];
        for (int r = 1; r <= Rounds; r++)
        {
            ulong v = 0;
            for (int j = 0; j < 8; j++)
            {
                v |= (ulong)s[8 * (r - 1) + j] << (56 - 8 * j);
            }

            roundConstants[r] = v;
        }

        return tables;
    }

    /// <summary>ρ: γ・π・θ をまとめた表引き (π で列 k を k 行下へずらす) のあと鍵を足す。</summary>
    private static void Rho(ReadOnlySpan<ulong> input, Span<ulong> output, ReadOnlySpan<ulong> key)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong v = key[i];
            for (int k = 0; k < 8; k++)
            {
                v ^= C[k][(byte)(input[(i - k + 8) & 7] >> (56 - 8 * k))];
            }

            output[i] = v;
        }
    }

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<ulong> m = stackalloc ulong[8];
        Span<ulong> key = stackalloc ulong[8];
        Span<ulong> state = stackalloc ulong[8];
        Span<ulong> temp = stackalloc ulong[8];
        Span<ulong> rc = stackalloc ulong[8];
        for (int i = 0; i < 8; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt64BigEndian(block[(8 * i)..]);
            key[i] = _h[i];
            state[i] = m[i] ^ key[i];
        }

        for (int r = 1; r <= Rounds; r++)
        {
            rc.Clear();
            rc[0] = RoundConstants[r];
            Rho(key, temp, rc);
            temp.CopyTo(key);
            Rho(state, temp, key);
            temp.CopyTo(state);
        }

        // 宮口・Preneel の構成。
        for (int i = 0; i < 8; i++)
        {
            _h[i] ^= state[i] ^ m[i];
        }
    }

    protected override byte[] FinishCore()
    {
        PadMd(0x80, 32, bigEndianLength: true);
        byte[] value = new byte[64];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(8 * i), _h[i]);
        }

        return value;
    }
}
