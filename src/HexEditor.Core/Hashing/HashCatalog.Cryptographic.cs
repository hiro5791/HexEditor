using System.Security.Cryptography;
using HexEditor.Core.Hashing.Crypto;

namespace HexEditor.Core.Hashing;

/// <summary>暗号学的ハッシュの一覧 (ANA-19 の仕様 4)。</summary>
public static partial class HashCatalog
{
    /// <summary>暗号学的ハッシュ (表の順)。</summary>
    private static IEnumerable<HashAlgorithmInfo> CryptographicAlgorithms() =>
    [
        Managed("md2", "MD2", 128, _ => new Md2Hasher(), insecure: true, slow: true),
        Managed("md4", "MD4", 128, _ => new Md4Hasher(), insecure: true),
        Crypto("md5", "MD5", 128, HashAlgorithmName.MD5, insecure: true),
        Crypto("sha1", "SHA-1", 160, HashAlgorithmName.SHA1, insecure: true, aliases: ["SHA1"]),
        Managed("sha224", "SHA-224", 224, _ => new Sha224Hasher(), aliases: ["SHA224"]),
        Crypto("sha256", "SHA-256", 256, HashAlgorithmName.SHA256, aliases: ["SHA256", "SHA-2"]),
        Crypto("sha384", "SHA-384", 384, HashAlgorithmName.SHA384, aliases: ["SHA384"]),
        Crypto("sha512", "SHA-512", 512, HashAlgorithmName.SHA512, aliases: ["SHA512"]),
        Managed("sha512-224", "SHA-512/224", 224, _ => new Sha512TruncatedHasher(224), aliases: ["SHA512/224", "SHA512_224"]),
        Managed("sha512-256", "SHA-512/256", 256, _ => new Sha512TruncatedHasher(256), aliases: ["SHA512/256", "SHA512_256"]),
        Managed("sha3-224", "SHA3-224", 224, _ => Sha3Provider.CreateSha3(224), aliases: ["SHA3_224"]),
        Managed("sha3-256", "SHA3-256", 256, _ => Sha3Provider.CreateSha3(256), aliases: ["SHA3_256"]),
        Managed("sha3-384", "SHA3-384", 384, _ => Sha3Provider.CreateSha3(384), aliases: ["SHA3_384"]),
        Managed("sha3-512", "SHA3-512", 512, _ => Sha3Provider.CreateSha3(512), aliases: ["SHA3_512"]),
        Shake(128, 256),
        Shake(256, 512),
        Managed("keccak256", "Keccak-256", 256, _ => KeccakHasher.Keccak(256), aliases: ["Keccak256", "KECCAK-256"]),
        Managed("ripemd128", "RIPEMD-128", 128, _ => new RipemdHasher(128), aliases: ["RIPEMD128", "RMD128"]),
        Managed("ripemd160", "RIPEMD-160", 160, _ => new RipemdHasher(160), aliases: ["RIPEMD160", "RMD160"]),
        Managed("ripemd256", "RIPEMD-256", 256, _ => new RipemdHasher(256), aliases: ["RIPEMD256", "RMD256"]),
        Managed("ripemd320", "RIPEMD-320", 320, _ => new RipemdHasher(320), aliases: ["RIPEMD320", "RMD320"]),
        Tiger("tiger", "Tiger", tiger2: false),
        Tiger("tiger2", "Tiger2", tiger2: true),
        Managed("whirlpool", "Whirlpool", 512, _ => new WhirlpoolHasher(), slow: true),
        new("blake2s", "BLAKE2s", HashGroup.Cryptographic, 256,
            p => new Blake2sHasher(OutputBits(p, 256) / 8, p.Key),
            aliases: ["BLAKE2s-256"], parameters: HashParameterKinds.OutputLength | HashParameterKinds.Key,
            bitsFor: p => OutputBits(p, 256), validate: p => ValidateBlake2(p, 256, 32)),
        new("blake2b", "BLAKE2b", HashGroup.Cryptographic, 512,
            p => new Blake2bHasher(OutputBits(p, 512) / 8, p.Key),
            aliases: ["BLAKE2b-512"], parameters: HashParameterKinds.OutputLength | HashParameterKinds.Key,
            bitsFor: p => OutputBits(p, 512), validate: p => ValidateBlake2(p, 512, 64)),
        new("blake3", "BLAKE3", HashGroup.Cryptographic, 256,
            p => new Blake3Hasher(OutputBits(p, 256) / 8, p.Key),
            parameters: HashParameterKinds.OutputLength | HashParameterKinds.Key,
            bitsFor: p => OutputBits(p, 256), validate: ValidateBlake3),
        new("tth", "TTH", HashGroup.Cryptographic, 192, _ => new TigerTreeHasher(),
            aliases: ["Tiger Tree Hash", "TigerTree"], preferBase32: true),
        new("ed2k", "ed2k", HashGroup.Cryptographic, 128, p => new Ed2kHasher(p.Ed2k),
            aliases: ["eDonkey", "eMule"], parameters: HashParameterKinds.Ed2kMode),
    ];

    /// <summary>可変長の出力の範囲 (SHAKE、BLAKE3。8〜8,192 bit、8 の倍数)。</summary>
    internal const int MaxVariableOutputBits = 8192;

    private static HashAlgorithmInfo Crypto(string id, string name, int bits, HashAlgorithmName algorithm, bool insecure = false,
        IReadOnlyList<string>? aliases = null) =>
        new(id, name, HashGroup.Cryptographic, bits, _ => new IncrementalHasher(algorithm), aliases, insecure: insecure);

    /// <summary>Core で実装したもの (.NET 標準にない。ANA-19 の仕様 5)。</summary>
    private static HashAlgorithmInfo Managed(string id, string name, int bits, Func<HashParameters, IHasher> create,
        bool insecure = false, bool slow = false, IReadOnlyList<string>? aliases = null) =>
        new(id, name, HashGroup.Cryptographic, bits, create, aliases, insecure: insecure, slow: slow);

    private static HashAlgorithmInfo Shake(int variant, int defaultBits) =>
        new($"shake{variant}", $"SHAKE{variant}", HashGroup.Cryptographic, defaultBits,
            p => Sha3Provider.CreateShake(variant, OutputBits(p, defaultBits)),
            aliases: [$"SHAKE-{variant}"], parameters: HashParameterKinds.OutputLength,
            bitsFor: p => OutputBits(p, defaultBits),
            validate: p => ValidOutput(p, MaxVariableOutputBits) ? HashParameterError.None : HashParameterError.OutputLength);

    private static HashAlgorithmInfo Tiger(string id, string name, bool tiger2) =>
        new(id, name, HashGroup.Cryptographic, 192, p => new TigerHasher(tiger2, OutputBits(p, 192)),
            parameters: HashParameterKinds.OutputLength, bitsFor: p => OutputBits(p, 192),
            validate: p => p.OutputBits is 0 or 128 or 160 or 192 ? HashParameterError.None : HashParameterError.OutputLength);

    /// <summary>パラメータの出力長 (0 は既定)。</summary>
    private static int OutputBits(HashParameters p, int defaultBits) => p.OutputBits == 0 ? defaultBits : p.OutputBits;

    /// <summary>出力長が 8〜max bit で 8 の倍数か (0 は既定)。</summary>
    private static bool ValidOutput(HashParameters p, int maxBits) =>
        p.OutputBits == 0 || (p.OutputBits >= 8 && p.OutputBits <= maxBits && p.OutputBits % 8 == 0);

    /// <summary>鍵の長さ (Hex が壊れていれば -1)。</summary>
    private static int KeyLength(HashParameters p)
    {
        try
        {
            return p.Key.Length;
        }
        catch (FormatException)
        {
            return -1;
        }
    }

    private static HashParameterError ValidateBlake2(HashParameters p, int maxBits, int maxKeyBytes)
    {
        if (!ValidOutput(p, maxBits))
        {
            return HashParameterError.OutputLength;
        }

        int key = KeyLength(p);
        return key < 0 || key > maxKeyBytes ? HashParameterError.KeyLength : HashParameterError.None;
    }

    private static HashParameterError ValidateBlake3(HashParameters p)
    {
        if (!ValidOutput(p, MaxVariableOutputBits))
        {
            return HashParameterError.OutputLength;
        }

        return KeyLength(p) is 0 or 32 ? HashParameterError.None : HashParameterError.KeyLength;
    }
}
