using BcEd25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace HexEditor.Core.Security;

/// <summary>
/// Ed25519 の署名と検証 (RFC 8032 の 5.1)。オンラインのテンプレートの索引の署名 (07 の TPL-36 の仕様 3) に使う。
/// .NET に Ed25519 がないため、検証済みの実装 (BouncyCastle.Cryptography。SSH.NET が使うので配布物に入っている。MIT) を呼ぶ。
/// 検証は RFC 8032 の厳密な形 (S &lt; L、公開鍵の点の復元に失敗すれば不一致) で、壊れた入力でも例外にせず false を返す。
/// 共通の実装を tools/HexEditor.TestData がソースとして取り込む (テスト用のリポジトリの署名)。
/// </summary>
internal static class Ed25519
{
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;
    public const int SeedSize = 32;

    /// <summary>秘密鍵 (32 バイトの種) から公開鍵を作る。</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("The Ed25519 seed must be 32 bytes.", nameof(seed));
        }

        byte[] publicKey = new byte[PublicKeySize];
        BcEd25519.GeneratePublicKey(seed.ToArray(), 0, publicKey, 0);
        return publicKey;
    }

    /// <summary>署名する (テストのフィクスチャの署名だけに使う。アプリは秘密鍵を扱わない)。</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("The Ed25519 seed must be 32 bytes.", nameof(seed));
        }

        byte[] m = message.ToArray();
        byte[] signature = new byte[SignatureSize];
        BcEd25519.Sign(seed.ToArray(), 0, m, 0, m.Length, signature, 0);
        return signature;
    }

    /// <summary>検証する。鍵・署名の長さが違う、点を復元できない、S が範囲外などは false。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        try
        {
            byte[] m = message.ToArray();
            return BcEd25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
