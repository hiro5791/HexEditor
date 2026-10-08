using System.Security.Cryptography;

namespace HexEditor.Core.Hashing;

/// <summary>アルゴリズムが持つパラメータ (ANA-19 の表の「パラメータ」)。</summary>
[Flags]
public enum HashParameterKinds
{
    None = 0,
    Seed = 1,
    Complement = 2,
}

/// <summary>ハッシュパネルで選べるアルゴリズム 1 つ (ANA-19)。</summary>
public sealed class HashAlgorithmInfo
{
    private readonly Func<HashParameters, IHasher> _create;

    internal HashAlgorithmInfo(string id, string name, HashGroup group, int bits, Func<HashParameters, IHasher> create,
        IReadOnlyList<string>? aliases = null, HashParameterKinds parameters = HashParameterKinds.None, bool insecure = false,
        bool available = true, CrcParameters? crc = null)
    {
        Id = id;
        Name = name;
        Group = group;
        Bits = bits;
        _create = create;
        Aliases = aliases ?? [];
        Parameters = parameters;
        IsInsecure = insecure;
        IsAvailable = available;
        Crc = crc;
    }

    /// <summary>設定・スクリプトで使う識別子 (地域設定に依存しない小文字)。</summary>
    public string Id { get; }

    /// <summary>表示名 (CRC はカタログの名前)。</summary>
    public string Name { get; }

    /// <summary>別名 (絞り込みで一致させる)。</summary>
    public IReadOnlyList<string> Aliases { get; }

    public HashGroup Group { get; }

    /// <summary>出力のビット数。</summary>
    public int Bits { get; }

    public HashParameterKinds Parameters { get; }

    /// <summary>安全でないことを行の注記に表示する (MD5、SHA-1)。</summary>
    public bool IsInsecure { get; }

    /// <summary>この環境で使えるか (ANA-19 の「エラー」。使えなければ一覧で無効にする)。</summary>
    public bool IsAvailable { get; }

    /// <summary>CRC のパラメータ (CRC 以外は null)。</summary>
    public CrcParameters? Crc { get; }

    /// <summary>64 bit 以下の値 (10 進表示とバイト順の選択ができる。ANA-18 の仕様 5・6)。</summary>
    public bool IsNumeric => Bits <= 64 && Group is HashGroup.Checksum or HashGroup.Crc or HashGroup.NonCryptographic;

    public IHasher CreateHasher(HashParameters? parameters = null)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException($"{Name} はこの環境では利用できません。");
        }

        return _create(parameters ?? HashParameters.Default);
    }

    /// <summary>名前・別名が絞り込みの文字列を含むか (大文字・小文字を区別しない序数比較。ANA-19 の「画面」)。</summary>
    public bool MatchesFilter(string filter)
    {
        filter = filter.Trim();
        return filter.Length == 0
            || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>結果の行に出す名前。既定と異なるパラメータを付ける (例: <c>xxHash64 (seed=0x1234)</c>。ANA-18 の仕様 8)。</summary>
    public string DisplayName(HashParameters? parameters)
    {
        parameters ??= HashParameters.Default;
        var parts = new List<string>();
        if (Parameters.HasFlag(HashParameterKinds.Seed) && parameters.Seed != 0)
        {
            parts.Add($"seed=0x{parameters.Seed:X}");
        }

        if (Parameters.HasFlag(HashParameterKinds.Complement) && parameters.Complement != HashComplement.None)
        {
            parts.Add(parameters.Complement == HashComplement.Ones ? "ones' complement" : "two's complement");
        }

        return parts.Count == 0 ? Name : $"{Name} ({string.Join(", ", parts)})";
    }

    public override string ToString() => Name;
}

/// <summary>最初から用意するアルゴリズムのセット (ANA-18 の仕様 3)。</summary>
public sealed record HashAlgorithmSet(string Id, IReadOnlyList<string> AlgorithmIds);

/// <summary>
/// 対応するアルゴリズムの一覧 (ANA-19)。フェーズ 1 の範囲 (加算 8 / 16 / 32 bit、XOR 8 bit、CRC-16/ARC、CRC-16/IBM-3740、CRC-32、
/// CRC-32C、Adler-32、MD5、SHA-1、SHA-256、SHA-384、SHA-512) と、.NET 標準にある SHA-3、パラメータの例として xxHash32 / xxHash64。
/// </summary>
public static class HashCatalog
{
    public static readonly CrcParameters Crc16Arc = new(16, 0x8005, 0, true, true, 0, 0xBB3D);
    public static readonly CrcParameters Crc16Ibm3740 = new(16, 0x1021, 0xFFFF, false, false, 0, 0x29B1);
    public static readonly CrcParameters Crc32 = new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xCBF43926);
    public static readonly CrcParameters Crc32C = new(32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xE3069283);

    /// <summary>一覧の順 (グループの順、グループの中は表の順)。</summary>
    public static IReadOnlyList<HashAlgorithmInfo> All { get; } =
    [
        new("sum8", "Sum 8", HashGroup.Checksum, 8, p => new ByteSumHasher(8, p.Complement), ["add8", "加算 8"], HashParameterKinds.Complement),
        new("sum16", "Sum 16", HashGroup.Checksum, 16, p => new ByteSumHasher(16, p.Complement), ["add16", "加算 16"], HashParameterKinds.Complement),
        new("sum32", "Sum 32", HashGroup.Checksum, 32, p => new ByteSumHasher(32, p.Complement), ["add32", "加算 32"], HashParameterKinds.Complement),
        new("xor8", "XOR 8", HashGroup.Checksum, 8, _ => new ByteXorHasher()),
        Crc("crc16-arc", "CRC-16/ARC", Crc16Arc, ["CRC-16", "CRC-16/IBM", "ARC", "CRC-IBM"]),
        Crc("crc16-ibm3740", "CRC-16/IBM-3740", Crc16Ibm3740, ["CRC-16/CCITT-FALSE", "CRC-16/AUTOSAR", "CRC-CCITT-FALSE"]),
        Crc("crc32", "CRC-32", Crc32, ["CRC-32/ISO-HDLC", "CRC-32/ADCCP", "CRC-32/V-42", "CRC-32/XZ", "PKZIP"]),
        Crc("crc32c", "CRC-32C", Crc32C, ["CRC-32/ISCSI", "CRC-32/BASE91-C", "CRC-32/CASTAGNOLI", "CRC-32/INTERLAKEN", "Castagnoli"]),
        new("adler32", "Adler-32", HashGroup.NonCryptographic, 32, _ => new Adler32Hasher()),
        new("xxh32", "xxHash32", HashGroup.NonCryptographic, 32, p => new XxHash32Hasher(p.Seed), ["XXH32"], HashParameterKinds.Seed),
        new("xxh64", "xxHash64", HashGroup.NonCryptographic, 64, p => new XxHash64Hasher(p.Seed), ["XXH64"], HashParameterKinds.Seed),
        Crypto("md5", "MD5", 128, HashAlgorithmName.MD5, insecure: true),
        Crypto("sha1", "SHA-1", 160, HashAlgorithmName.SHA1, insecure: true, aliases: ["SHA1"]),
        Crypto("sha256", "SHA-256", 256, HashAlgorithmName.SHA256, aliases: ["SHA256", "SHA-2"]),
        Crypto("sha384", "SHA-384", 384, HashAlgorithmName.SHA384, aliases: ["SHA384"]),
        Crypto("sha512", "SHA-512", 512, HashAlgorithmName.SHA512, aliases: ["SHA512"]),
        Crypto("sha3-256", "SHA3-256", 256, HashAlgorithmName.SHA3_256, available: Sha3Supported(256), aliases: ["SHA3_256"]),
        Crypto("sha3-384", "SHA3-384", 384, HashAlgorithmName.SHA3_384, available: Sha3Supported(384), aliases: ["SHA3_384"]),
        Crypto("sha3-512", "SHA3-512", 512, HashAlgorithmName.SHA3_512, available: Sha3Supported(512), aliases: ["SHA3_512"]),
    ];

    /// <summary>最初から用意するセット (ANA-18 の仕様 3)。一覧にない (この版で未対応の) アルゴリズムは含めない。</summary>
    public static IReadOnlyList<HashAlgorithmSet> BuiltInSets { get; } =
    [
        new("common", ["crc32", "md5", "sha1", "sha256"]),
        new("cryptographic", Existing("md5", "sha1", "sha256", "sha512", "sha3-256", "blake3")),
        new("checksums", Existing("sum8", "sum16", "sum32", "xor8", "crc16-arc", "crc32", "adler32")),
    ];

    /// <summary>既定のセット (「よく使う」)。</summary>
    public static HashAlgorithmSet DefaultSet => BuiltInSets[0];

    public static HashAlgorithmInfo? Find(string idOrName)
    {
        foreach (HashAlgorithmInfo a in All)
        {
            if (string.Equals(a.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                || a.Aliases.Any(x => string.Equals(x, idOrName, StringComparison.OrdinalIgnoreCase)))
            {
                return a;
            }
        }

        return null;
    }

    public static HashAlgorithmInfo Get(string id) => Find(id) ?? throw new KeyNotFoundException($"未知のアルゴリズム: {id}");

    /// <summary>絞り込み (ANA-19 の「画面」)。</summary>
    public static IEnumerable<HashAlgorithmInfo> Filter(string filter) => All.Where(a => a.MatchesFilter(filter));

    private static HashAlgorithmInfo Crc(string id, string name, CrcParameters p, IReadOnlyList<string> aliases) =>
        new(id, name, HashGroup.Crc, p.Width, _ => new CrcHasher(p), aliases, crc: p);

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

    private static string[] Existing(params string[] ids) =>
        [.. ids.Where(id => All.Any(a => a.Id == id && a.IsAvailable))];
}
