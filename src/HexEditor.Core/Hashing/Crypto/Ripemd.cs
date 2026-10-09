using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// RIPEMD-128 / 160 / 256 / 320 (Dobbertin、Bosselaers、Preneel)。128 と 256 は 4 ラウンド、160 と 320 は 5 ラウンド。
/// 256 と 320 は 2 本の系列を合わせずに、ラウンドの終わりごとに 1 つの変数を系列の間で入れ替える。
/// 変数は参照実装と同じく名前 (a〜e) で持ち、手順ごとに引数の並びを回す (入れ替えは名前で行う)。
/// </summary>
public sealed class RipemdHasher : BlockHasher
{
    private static ReadOnlySpan<byte> R =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
        4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13,
    ];

    private static ReadOnlySpan<byte> RPrime =>
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
        12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11,
    ];

    private static ReadOnlySpan<byte> S =>
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
        9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6,
    ];

    private static ReadOnlySpan<byte> SPrime =>
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
        8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11,
    ];

    private static ReadOnlySpan<uint> KLeft => [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E];

    private static ReadOnlySpan<uint> KRight5 => [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000];

    private static ReadOnlySpan<uint> KRight4 => [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x00000000];

    private readonly int _bits;
    private readonly int _vars;
    private readonly uint[] _h;

    /// <param name="bits">128、160、256、320。</param>
    public RipemdHasher(int bits)
        : base(64)
    {
        _bits = bits;
        _vars = bits is 128 or 256 ? 4 : 5;
        _h = bits switch
        {
            128 => [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476],
            160 => [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0],
            256 => [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0x76543210, 0xFEDCBA98, 0x89ABCDEF, 0x01234567],
            320 =>
            [
                0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0,
                0x76543210, 0xFEDCBA98, 0x89ABCDEF, 0x01234567, 0x3C2D1E0F,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(bits)),
        };
    }

    private static uint F(int round, uint x, uint y, uint z) => round switch
    {
        0 => x ^ y ^ z,
        1 => (x & y) | (~x & z),
        2 => (x | ~y) ^ z,
        3 => (x & z) | (y & ~z),
        _ => x ^ (y | ~z),
    };

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);
        }

        int n = _vars;
        bool wide = _bits is 256 or 320;
        Span<uint> left = stackalloc uint[n];
        Span<uint> right = stackalloc uint[n];
        _h.AsSpan(0, n).CopyTo(left);
        (wide ? _h.AsSpan(n, n) : _h.AsSpan(0, n)).CopyTo(right);
        ReadOnlySpan<uint> kRight = n == 4 ? KRight4 : KRight5;

        for (int round = 0; round < n; round++)
        {
            for (int i = 0; i < 16; i++)
            {
                int j = 16 * round + i;
                Step(left, j, n, F(round, Var(left, j, n, 1), Var(left, j, n, 2), Var(left, j, n, 3)) + x[R[j]] + KLeft[round], S[j]);
                int rr = n - 1 - round;
                Step(right, j, n, F(rr, Var(right, j, n, 1), Var(right, j, n, 2), Var(right, j, n, 3)) + x[RPrime[j]] + kRight[round], SPrime[j]);
            }

            if (wide)
            {
                // ラウンド r の終わりに名前 r の変数 (a, b, c, d, e の順) を入れ替える。RIPEMD-320 では手順ごとに並びが回るため、
                // 位置で数えると B, D, A, C, E の順になる (仕様書の記述)。
                (left[round], right[round]) = (right[round], left[round]);
            }
        }

        uint[] h = _h;
        if (wide)
        {
            // 合わせずにそれぞれの連鎖変数に足す。
            for (int i = 0; i < n; i++)
            {
                h[i] += left[i];
                h[n + i] += right[i];
            }
        }
        else if (n == 4)
        {
            uint t = h[1] + left[2] + right[3];
            h[1] = h[2] + left[3] + right[0];
            h[2] = h[3] + left[0] + right[1];
            h[3] = h[0] + left[1] + right[2];
            h[0] = t;
        }
        else
        {
            uint t = h[1] + left[2] + right[3];
            h[1] = h[2] + left[3] + right[4];
            h[2] = h[3] + left[4] + right[0];
            h[3] = h[4] + left[0] + right[1];
            h[4] = h[0] + left[1] + right[2];
            h[0] = t;
        }
    }

    /// <summary>手順 j で位置 p (0 = 更新する変数) にある変数の値。手順ごとに名前の並びが 1 つずつ回る。</summary>
    private static uint Var(Span<uint> v, int j, int n, int p) => v[Name(j, n, p)];

    private static int Name(int j, int n, int p) => ((p - j) % n + n) % n;

    private static void Step(Span<uint> v, int j, int n, uint sum, int shift)
    {
        int a = Name(j, n, 0);
        if (n == 4)
        {
            v[a] = BitOperations.RotateLeft(v[a] + sum, shift);
        }
        else
        {
            int c = Name(j, n, 2);
            v[a] = BitOperations.RotateLeft(v[a] + sum, shift) + v[Name(j, n, 4)];
            v[c] = BitOperations.RotateLeft(v[c], 10);
        }
    }

    protected override byte[] FinishCore()
    {
        PadMd(0x80, 8, bigEndianLength: false);
        byte[] value = new byte[_bits / 8];
        for (int i = 0; i < _h.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4 * i), _h[i]);
        }

        return value;
    }
}
