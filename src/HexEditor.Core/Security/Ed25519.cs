using System.Numerics;
using System.Security.Cryptography;

namespace HexEditor.Core.Security;

/// <summary>
/// Ed25519 の署名と検証 (RFC 8032 の 5.1)。オンラインのテンプレートの索引の署名 (07 の TPL-36 の仕様 3) に使う。
/// .NET に Ed25519 がないため、RFC 8032 の 6 章の参照実装をそのまま移したもの (BigInteger による拡張座標の計算。定数時間ではないが、
/// 使うのは公開鍵による検証と、テストのフィクスチャの署名だけで、秘密鍵をアプリで扱わない)。
/// 共通の実装を tools/HexEditor.TestData がソースとして取り込む (テスト用のリポジトリの署名)。
/// </summary>
internal static class Ed25519
{
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point G = BasePoint();

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static BigInteger Mod(BigInteger a)
    {
        BigInteger r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static Point BasePoint()
    {
        BigInteger y = Mod(4 * Inv(5));
        BigInteger x = RecoverX(y, 0) ?? throw new InvalidOperationException();
        return new Point(x, y, 1, Mod(x * y));
    }

    private static Point Add(Point a, Point b)
    {
        BigInteger pa = Mod((a.Y - a.X) * (b.Y - b.X));
        BigInteger pb = Mod((a.Y + a.X) * (b.Y + b.X));
        BigInteger pc = Mod(2 * a.T * b.T * D);
        BigInteger pd = Mod(2 * a.Z * b.Z);
        BigInteger e = pb - pa;
        BigInteger f = pd - pc;
        BigInteger g = pd + pc;
        BigInteger h = pb + pa;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s.Sign > 0)
        {
            if (!s.IsEven)
            {
                q = Add(q, p);
            }

            p = Add(p, p);
            s >>= 1;
        }

        return q;
    }

    private static bool Equal(Point a, Point b) =>
        Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }

        BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero)
        {
            return sign == 0 ? BigInteger.Zero : null;
        }

        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero)
        {
            x = Mod(x * SqrtM1);
        }

        if (!Mod(x * x - x2).IsZero)
        {
            return null;
        }

        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static byte[] Compress(Point p)
    {
        BigInteger zinv = Inv(p.Z);
        BigInteger x = Mod(p.X * zinv);
        BigInteger y = Mod(p.Y * zinv);
        byte[] bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven)
        {
            bytes[31] |= 0x80;
        }

        return bytes;
    }

    private static Point? Decompress(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32)
        {
            return null;
        }

        int sign = s[31] >> 7;
        byte[] copy = s.ToArray();
        copy[31] &= 0x7F;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        return RecoverX(y, sign) is { } x ? new Point(x, y, 1, Mod(x * y)) : null;
    }

    private static BigInteger HashModQ(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (byte[] part in parts)
        {
            sha.AppendData(part);
        }

        return new BigInteger(sha.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % Q;
    }

    private static (BigInteger A, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        byte[] h = SHA512.HashData(seed);
        byte[] low = h[..32];
        low[0] &= 248;
        low[31] &= 127;
        low[31] |= 64;
        return (new BigInteger(low, isUnsigned: true, isBigEndian: false), h[32..]);
    }

    /// <summary>秘密鍵 (32 バイトの種) から公開鍵を作る。</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("The Ed25519 seed must be 32 bytes.", nameof(seed));
        }

        return Compress(Multiply(Expand(seed).A, G));
    }

    /// <summary>署名する (64 バイト)。</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        (BigInteger a, byte[] prefix) = Expand(seed);
        byte[] publicKey = Compress(Multiply(a, G));
        byte[] msg = message.ToArray();
        BigInteger r = HashModQ(prefix, msg);
        byte[] rs = Compress(Multiply(r, G));
        BigInteger h = HashModQ(rs, publicKey, msg);
        BigInteger s = (r + h * a) % Q;
        byte[] signature = new byte[64];
        rs.CopyTo(signature, 0);
        s.TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);
        return signature;
    }

    /// <summary>署名を検証する。鍵・署名の長さが違う、点が曲線上にない場合も false (例外にしない)。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        if (Decompress(publicKey) is not { } a || Decompress(signature[..32]) is not { } r)
        {
            return false;
        }

        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= Q)
        {
            return false;
        }

        BigInteger h = HashModQ(signature[..32].ToArray(), publicKey.ToArray(), message.ToArray());
        return Equal(Multiply(s, G), Add(r, Multiply(h, a)));
    }
}
