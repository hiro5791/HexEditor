using System.Security.Cryptography;

namespace HexEditor.Core.Hashing;

/// <summary>暗号学的ハッシュの一覧 (ANA-19 の仕様 4)。</summary>
public static partial class HashCatalog
{
    /// <summary>暗号学的ハッシュ (表の順)。</summary>
    private static IEnumerable<HashAlgorithmInfo> CryptographicAlgorithms() =>
    [
        Crypto("md5", "MD5", 128, HashAlgorithmName.MD5, insecure: true),
        Crypto("sha1", "SHA-1", 160, HashAlgorithmName.SHA1, insecure: true, aliases: ["SHA1"]),
        Crypto("sha256", "SHA-256", 256, HashAlgorithmName.SHA256, aliases: ["SHA256", "SHA-2"]),
        Crypto("sha384", "SHA-384", 384, HashAlgorithmName.SHA384, aliases: ["SHA384"]),
        Crypto("sha512", "SHA-512", 512, HashAlgorithmName.SHA512, aliases: ["SHA512"]),
        Crypto("sha3-256", "SHA3-256", 256, HashAlgorithmName.SHA3_256, available: Sha3Supported(256), aliases: ["SHA3_256"]),
        Crypto("sha3-384", "SHA3-384", 384, HashAlgorithmName.SHA3_384, available: Sha3Supported(384), aliases: ["SHA3_384"]),
        Crypto("sha3-512", "SHA3-512", 512, HashAlgorithmName.SHA3_512, available: Sha3Supported(512), aliases: ["SHA3_512"]),
    ];

    private static HashAlgorithmInfo Crypto(string id, string name, int bits, HashAlgorithmName algorithm, bool insecure = false,
        bool available = true, IReadOnlyList<string>? aliases = null) =>
        new(id, name, HashGroup.Cryptographic, bits, _ => new IncrementalHasher(algorithm), aliases, insecure: insecure, available: available);

    private static bool Sha3Supported(int bits)
    {
        try
        {
            return bits switch
            {
                256 => SHA3_256.IsSupported,
                384 => SHA3_384.IsSupported,
                _ => SHA3_512.IsSupported,
            };
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
