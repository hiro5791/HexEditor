namespace HexEditor.Core.Hashing;

/// <summary>チェックサム・CRC・非暗号学的ハッシュの一覧 (ANA-19 の仕様 1〜3)。</summary>
public static partial class HashCatalog
{
    /// <summary>チェックサム、CRC、非暗号学的ハッシュ (グループの順、グループの中は表の順)。</summary>
    private static IEnumerable<HashAlgorithmInfo> ChecksumAlgorithms() =>
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
    ];

    private static HashAlgorithmInfo Crc(string id, string name, CrcParameters p, IReadOnlyList<string> aliases) =>
        new(id, name, HashGroup.Crc, p.Width, _ => new CrcHasher(p), aliases, crc: p);
}
