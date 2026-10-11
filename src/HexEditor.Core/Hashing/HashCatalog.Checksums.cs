namespace HexEditor.Core.Hashing;

/// <summary>チェックサム・CRC・非暗号学的ハッシュの一覧 (ANA-19 の仕様 1〜3)。</summary>
public static partial class HashCatalog
{
    /// <summary>ワード単位のチェックサム・Fletcher の既定のエンディアン (null のとき)。</summary>
    private const bool WordDefaultBigEndian = false;

    /// <summary>チェックサム、CRC、非暗号学的ハッシュ (グループの順、グループの中は表の順)。</summary>
    private static IEnumerable<HashAlgorithmInfo> ChecksumAlgorithms() =>
    [
        // 仕様 1: 加算 (バイト単位)。
        new("sum8", "Sum 8", HashGroup.Checksum, 8, p => new ByteSumHasher(8, p.Complement), ["add8", "加算 8"], HashParameterKinds.Complement),
        new("sum16", "Sum 16", HashGroup.Checksum, 16, p => new ByteSumHasher(16, p.Complement), ["add16", "加算 16"], HashParameterKinds.Complement),
        new("sum32", "Sum 32", HashGroup.Checksum, 32, p => new ByteSumHasher(32, p.Complement), ["add32", "加算 32"], HashParameterKinds.Complement),
        new("sum64", "Sum 64", HashGroup.Checksum, 64, p => new ByteSumHasher(64, p.Complement), ["add64", "加算 64"], HashParameterKinds.Complement),

        // 仕様 1: 加算 (ワード単位。既定は LE)。
        WordSum(16),
        WordSum(32),
        WordSum(64),

        // 仕様 1: インターネットチェックサム (既定は BE)。
        new("inet16", "Internet checksum", HashGroup.Checksum, 16, p => new InternetChecksumHasher(p.BigEndian ?? true),
            ["RFC 1071", "IP checksum", "インターネットチェックサム"], HashParameterKinds.Endian),

        // 仕様 1: XOR。
        new("xor8", "XOR 8", HashGroup.Checksum, 8, _ => new ByteXorHasher()),
        WordXor(16),
        WordXor(32),
        WordXor(64),

        // 仕様 2: CRC (表の順。名前はカタログの名前、別名はカタログの Alias と表のカッコ内)。
        Crc("crc8-smbus", "CRC-8/SMBUS", new(8, 0x07, 0x00, false, false, 0x00, 0xF4), ["CRC-8"]),
        Crc("crc8-autosar", "CRC-8/AUTOSAR", new(8, 0x2F, 0xFF, false, false, 0xFF, 0xDF), []),
        Crc("crc8-bluetooth", "CRC-8/BLUETOOTH", new(8, 0xA7, 0x00, true, true, 0x00, 0x26), []),
        Crc("crc8-cdma2000", "CRC-8/CDMA2000", new(8, 0x9B, 0xFF, false, false, 0x00, 0xDA), []),
        Crc("crc8-darc", "CRC-8/DARC", new(8, 0x39, 0x00, true, true, 0x00, 0x15), []),
        Crc("crc8-dvb-s2", "CRC-8/DVB-S2", new(8, 0xD5, 0x00, false, false, 0x00, 0xBC), []),
        Crc("crc8-gsm-a", "CRC-8/GSM-A", new(8, 0x1D, 0x00, false, false, 0x00, 0x37), []),
        Crc("crc8-i-code", "CRC-8/I-CODE", new(8, 0x1D, 0xFD, false, false, 0x00, 0x7E), []),
        Crc("crc8-i-432-1", "CRC-8/I-432-1", new(8, 0x07, 0x00, false, false, 0x55, 0xA1), ["CRC-8/ITU"]),
        Crc("crc8-maxim-dow", "CRC-8/MAXIM-DOW", new(8, 0x31, 0x00, true, true, 0x00, 0xA1), ["CRC-8/MAXIM", "DOW-CRC"]),
        Crc("crc8-nrsc-5", "CRC-8/NRSC-5", new(8, 0x31, 0xFF, false, false, 0x00, 0xF7), []),
        Crc("crc8-rohc", "CRC-8/ROHC", new(8, 0x07, 0xFF, true, true, 0x00, 0xD0), []),
        Crc("crc8-sae-j1850", "CRC-8/SAE-J1850", new(8, 0x1D, 0xFF, false, false, 0xFF, 0x4B), []),
        Crc("crc8-wcdma", "CRC-8/WCDMA", new(8, 0x9B, 0x00, true, true, 0x00, 0x25), []),
        Crc("crc16-arc", "CRC-16/ARC", Crc16Arc, ["CRC-16/IBM", "ARC", "CRC-16", "CRC-16/LHA", "CRC-IBM"]),
        Crc("crc16-ibm3740", "CRC-16/IBM-3740", Crc16Ibm3740, ["CRC-16/CCITT-FALSE", "CRC-16/AUTOSAR", "CRC-CCITT-FALSE"]),
        Crc("crc16-xmodem", "CRC-16/XMODEM", new(16, 0x1021, 0x0000, false, false, 0x0000, 0x31C3), ["CRC-16/ACORN", "CRC-16/LTE", "CRC-16/V-41-MSB", "XMODEM", "ZMODEM"]),
        Crc("crc16-kermit", "CRC-16/KERMIT", new(16, 0x1021, 0x0000, true, true, 0x0000, 0x2189), ["CRC-16/BLUETOOTH", "CRC-16/CCITT", "CRC-16/CCITT-TRUE", "CRC-16/V-41-LSB", "CRC-CCITT", "KERMIT"]),
        Crc("crc16-modbus", "CRC-16/MODBUS", new(16, 0x8005, 0xFFFF, true, true, 0x0000, 0x4B37), ["MODBUS"]),
        Crc("crc16-usb", "CRC-16/USB", new(16, 0x8005, 0xFFFF, true, true, 0xFFFF, 0xB4C8), []),
        Crc("crc16-ibm-sdlc", "CRC-16/IBM-SDLC", new(16, 0x1021, 0xFFFF, true, true, 0xFFFF, 0x906E), ["CRC-16/ISO-HDLC", "CRC-16/ISO-IEC-14443-3-B", "CRC-16/X-25", "CRC-B", "X-25"]),
        Crc("crc16-dnp", "CRC-16/DNP", new(16, 0x3D65, 0x0000, true, true, 0xFFFF, 0xEA82), []),
        Crc("crc16-maxim-dow", "CRC-16/MAXIM-DOW", new(16, 0x8005, 0x0000, true, true, 0xFFFF, 0x44C2), ["CRC-16/MAXIM"]),
        Crc("crc16-genibus", "CRC-16/GENIBUS", new(16, 0x1021, 0xFFFF, false, false, 0xFFFF, 0xD64E), ["CRC-16/DARC", "CRC-16/EPC", "CRC-16/EPC-C1G2", "CRC-16/I-CODE"]),
        Crc("crc16-spi-fujitsu", "CRC-16/SPI-FUJITSU", new(16, 0x1021, 0x1D0F, false, false, 0x0000, 0xE5CC), ["CRC-16/AUG-CCITT"]),
        Crc("crc16-umts", "CRC-16/UMTS", new(16, 0x8005, 0x0000, false, false, 0x0000, 0xFEE8), ["CRC-16/BUYPASS", "CRC-16/VERIFONE"]),
        Crc("crc16-dect-x", "CRC-16/DECT-X", new(16, 0x0589, 0x0000, false, false, 0x0000, 0x007F), ["X-CRC-16"]),
        Crc("crc16-en-13757", "CRC-16/EN-13757", new(16, 0x3D65, 0x0000, false, false, 0xFFFF, 0xC2B7), []),
        Crc("crc16-t10-dif", "CRC-16/T10-DIF", new(16, 0x8BB7, 0x0000, false, false, 0x0000, 0xD0DB), []),
        Crc("crc16-teledisk", "CRC-16/TELEDISK", new(16, 0xA097, 0x0000, false, false, 0x0000, 0x0FB3), []),
        Crc("crc16-cdma2000", "CRC-16/CDMA2000", new(16, 0xC867, 0xFFFF, false, false, 0x0000, 0x4C06), []),
        Crc("crc16-mcrf4xx", "CRC-16/MCRF4XX", new(16, 0x1021, 0xFFFF, true, true, 0x0000, 0x6F91), []),
        Crc("crc24-openpgp", "CRC-24/OPENPGP", new(24, 0x864CFB, 0xB704CE, false, false, 0x000000, 0x21CF02), ["CRC-24"]),
        Crc("crc24-ble", "CRC-24/BLE", new(24, 0x00065B, 0x555555, true, true, 0x000000, 0xC25A56), []),
        Crc("crc24-flexray-a", "CRC-24/FLEXRAY-A", new(24, 0x5D6DCB, 0xFEDCBA, false, false, 0x000000, 0x7979BD), []),
        Crc("crc32", "CRC-32", Crc32, ["CRC-32/ISO-HDLC", "CRC-32/ADCCP", "CRC-32/V-42", "CRC-32/XZ", "PKZIP"]),
        Crc("crc32c", "CRC-32C", Crc32C, ["CRC-32/ISCSI", "CRC-32/BASE91-C", "CRC-32/CASTAGNOLI", "CRC-32/INTERLAKEN", "CRC-32/NVME", "Castagnoli"]),
        Crc("crc32-bzip2", "CRC-32/BZIP2", new(32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0xFFFFFFFF, 0xFC891918), ["CRC-32/AAL5", "CRC-32/DECT-B", "B-CRC-32"]),
        Crc("crc32-mpeg-2", "CRC-32/MPEG-2", new(32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0x00000000, 0x0376E6E7), []),
        Crc("crc32-cksum", "CRC-32/CKSUM", CksumHasher.Crc32Cksum, ["CKSUM", "CRC-32/POSIX"]),

        // CRC-32/CKSUM の、POSIX の cksum と同じく末尾にデータ長を加える方式。
        new("cksum-length", "cksum (with length)", HashGroup.Crc, 32, _ => new CksumHasher(), ["cksum (長さ付き)", "POSIX cksum"]),
        Crc("crc32-jamcrc", "CRC-32/JAMCRC", new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0x00000000, 0x340BC6D9), ["JAMCRC"]),
        Crc("crc32-xfer", "CRC-32/XFER", new(32, 0x000000AF, 0x00000000, false, false, 0x00000000, 0xBD0BE338), ["XFER"]),
        Crc("crc32-autosar", "CRC-32/AUTOSAR", new(32, 0xF4ACFB13, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0x1697D06A), []),
        Crc("crc32-aixm", "CRC-32/AIXM", new(32, 0x814141AB, 0x00000000, false, false, 0x00000000, 0x3010BF7F), ["CRC-32Q"]),
        Crc("crc32-base91-d", "CRC-32/BASE91-D", new(32, 0xA833982B, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0x87315576), ["CRC-32D"]),
        Crc("crc64-ecma-182", "CRC-64/ECMA-182", new(64, 0x42F0E1EBA9EA3693, 0x0000000000000000, false, false, 0x0000000000000000, 0x6C40DF5F0B497347), ["CRC-64"]),
        Crc("crc64-xz", "CRC-64/XZ", new(64, 0x42F0E1EBA9EA3693, 0xFFFFFFFFFFFFFFFF, true, true, 0xFFFFFFFFFFFFFFFF, 0x995DC9BBDF1939FA), ["CRC-64/GO-ECMA"]),
        Crc("crc64-go-iso", "CRC-64/GO-ISO", new(64, 0x000000000000001B, 0xFFFFFFFFFFFFFFFF, true, true, 0xFFFFFFFFFFFFFFFF, 0xB90956C775A41001), []),
        Crc("crc64-we", "CRC-64/WE", new(64, 0x42F0E1EBA9EA3693, 0xFFFFFFFFFFFFFFFF, false, false, 0xFFFFFFFFFFFFFFFF, 0x62EC59E3F1A4F00A), []),
        Crc("crc64-redis", "CRC-64/REDIS", new(64, 0xAD93D23594C935A9, 0x0000000000000000, true, true, 0x0000000000000000, 0xE9C6D914C4B8D9CA), []),
        Crc("crc64-nvme", "CRC-64/NVME", new(64, 0xAD93D23594C93659, 0xFFFFFFFFFFFFFFFF, true, true, 0xFFFFFFFFFFFFFFFF, 0xAE8B14860A799888), []),

        // 仕様 3: 非暗号学的ハッシュ (表の順)。
        new("adler32", "Adler-32", HashGroup.NonCryptographic, 32, _ => new Adler32Hasher()),
        new("fletcher16", "Fletcher-16", HashGroup.NonCryptographic, 16, _ => new FletcherHasher(16, false)),
        new("fletcher32", "Fletcher-32", HashGroup.NonCryptographic, 32, p => new FletcherHasher(32, p.BigEndian ?? WordDefaultBigEndian),
            parameters: HashParameterKinds.Endian),
        new("fletcher64", "Fletcher-64", HashGroup.NonCryptographic, 64, p => new FletcherHasher(64, p.BigEndian ?? WordDefaultBigEndian),
            parameters: HashParameterKinds.Endian),
        new("fnv1-32", "FNV-1 32", HashGroup.NonCryptographic, 32, _ => new FnvHasher(32, false), ["FNV-1/32", "FNV1-32"]),
        new("fnv1a-32", "FNV-1a 32", HashGroup.NonCryptographic, 32, _ => new FnvHasher(32, true), ["FNV-1a/32", "FNV1a-32"]),
        new("fnv1-64", "FNV-1 64", HashGroup.NonCryptographic, 64, _ => new FnvHasher(64, false), ["FNV-1/64", "FNV1-64"]),
        new("fnv1a-64", "FNV-1a 64", HashGroup.NonCryptographic, 64, _ => new FnvHasher(64, true), ["FNV-1a/64", "FNV1a-64"]),
        new("fnv1-128", "FNV-1 128", HashGroup.NonCryptographic, 128, _ => new FnvHasher(128, false), ["FNV-1/128", "FNV1-128"]),
        new("fnv1a-128", "FNV-1a 128", HashGroup.NonCryptographic, 128, _ => new FnvHasher(128, true), ["FNV-1a/128", "FNV1a-128"]),
        new("xxh32", "xxHash32", HashGroup.NonCryptographic, 32, p => new XxHash32Hasher(p.Seed), ["XXH32"], HashParameterKinds.Seed, validate: Seed32),
        new("xxh64", "xxHash64", HashGroup.NonCryptographic, 64, p => new XxHash64Hasher(p.Seed), ["XXH64"], HashParameterKinds.Seed),
        new("xxh3-64", "XXH3-64", HashGroup.NonCryptographic, 64, p => new Xxh3Hasher(p.Seed, false), ["XXH3", "XXH3_64bits"], HashParameterKinds.Seed),
        new("xxh3-128", "XXH3-128", HashGroup.NonCryptographic, 128, p => new Xxh3Hasher(p.Seed, true), ["XXH128", "XXH3_128bits"], HashParameterKinds.Seed),
        new("murmur3-x86-32", "MurmurHash3 x86_32", HashGroup.NonCryptographic, 32, p => new Murmur3Hasher(Murmur3Variant.X86_32, p.Seed),
            ["Murmur3A", "MurmurHash3_x86_32"], HashParameterKinds.Seed, validate: Seed32),
        new("murmur3-x86-128", "MurmurHash3 x86_128", HashGroup.NonCryptographic, 128, p => new Murmur3Hasher(Murmur3Variant.X86_128, p.Seed),
            ["Murmur3C", "MurmurHash3_x86_128"], HashParameterKinds.Seed, validate: Seed32),
        new("murmur3-x64-128", "MurmurHash3 x64_128", HashGroup.NonCryptographic, 128, p => new Murmur3Hasher(Murmur3Variant.X64_128, p.Seed),
            ["Murmur3F", "MurmurHash3_x64_128"], HashParameterKinds.Seed, validate: Seed32),
        new("murmur2", "MurmurHash2", HashGroup.NonCryptographic, 32, p => new Murmur2Hasher(false, p.Seed), ["Murmur2"], HashParameterKinds.Seed, validate: Seed32),
        new("murmur64a", "MurmurHash64A", HashGroup.NonCryptographic, 64, p => new Murmur2Hasher(true, p.Seed), ["Murmur2B"], HashParameterKinds.Seed),
        Sip("siphash-2-4", "SipHash-2-4", 2, 4, ["SipHash"]),
        Sip("siphash-1-3", "SipHash-1-3", 1, 3, []),
    ];

    /// <summary>シードが 32 bit のアルゴリズム (参照実装の API が uint32_t): 範囲外のシードはパラメータの誤りにする (ANA-19 の仕様 3)。</summary>
    private static HashParameterError Seed32(HashParameters p) => p.Seed > uint.MaxValue ? HashParameterError.SeedRange : HashParameterError.None;

    private static HashAlgorithmInfo Crc(string id, string name, CrcParameters p, IReadOnlyList<string> aliases) =>
        new(id, name, HashGroup.Crc, p.Width, _ => new CrcHasher(p), aliases, crc: p);

    private static HashAlgorithmInfo WordSum(int bits) =>
        new($"sum{bits}w", $"Sum {bits} (words)", HashGroup.Checksum, bits,
            p => new WordSumHasher(bits, p.BigEndian ?? WordDefaultBigEndian, p.Complement),
            [$"add{bits}w", $"加算 {bits} (ワード単位)"], HashParameterKinds.Endian | HashParameterKinds.Signed | HashParameterKinds.Complement);

    private static HashAlgorithmInfo WordXor(int bits) =>
        new($"xor{bits}", $"XOR {bits}", HashGroup.Checksum, bits, p => new WordXorHasher(bits, p.BigEndian ?? WordDefaultBigEndian),
            parameters: HashParameterKinds.Endian);

    /// <summary>SipHash (鍵 128 bit。既定は全 0。出力は 64 bit、出力長 128 で 128 bit 版)。</summary>
    private static HashAlgorithmInfo Sip(string id, string name, int c, int d, IReadOnlyList<string> aliases) =>
        new(id, name, HashGroup.NonCryptographic, 64,
            p => new SipHasher(p.KeyHex is { Length: > 0 } ? p.Key : new byte[16], c, d, p.OutputBits == 128),
            aliases, HashParameterKinds.Key | HashParameterKinds.OutputLength,
            bitsFor: p => p.OutputBits == 128 ? 128 : 64,
            validate: ValidateSip);

    private static HashParameterError ValidateSip(HashParameters p)
    {
        if (p.KeyHex is { Length: > 0 } key && (key.Length != 32 || !key.All(char.IsAsciiHexDigit)))
        {
            return HashParameterError.KeyLength;
        }

        return p.OutputBits is 0 or 64 or 128 ? HashParameterError.None : HashParameterError.OutputLength;
    }
}
