using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Hashing;
using static HexEditor.Core.Tests.Hashing.HashFixtures;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-19 の仕様 1 (チェックサム) と仕様 3 (非暗号学的ハッシュ) のフェーズ 2 のアルゴリズムのテストベクタ。</summary>
public sealed class ChecksumAndNonCryptoHashTests
{
    private static string Hash(string id, byte[] data, HashParameters? parameters = null) =>
        Convert.ToHexString(Compute(HashCatalog.Get(id), data, parameters));

    private static string HashPieces(string id, byte[] data, int seed, HashParameters? parameters = null) =>
        Convert.ToHexString(ComputeInPieces(HashCatalog.Get(id), data, seed, parameters));

    private static readonly HashParameters Le = new() { BigEndian = false };
    private static readonly HashParameters Be = new() { BigEndian = true };

    /// <summary>
    /// 空のデータ、<c>abc</c>、<c>123456789</c> の値。出典: FNV-1a は draft-eastlake-fnv-30 のテストプログラムの値 (空のデータの値は
    /// offset_basis)、Fletcher は定義 (Wikipedia の Fletcher's checksum の例と同じ定義) から求めた値、加算・XOR・インターネットチェックサムは
    /// 仕様 1 の定義から求めた値。FNV-1 の 32 / 64 bit は定義から求めた値 (作者のテストプログラムの値は FnvMatchesTheAuthorsTestProgram で確かめる。128 bit は公式の値がない)。いずれも C# とは独立した Python の参照実装で求めた。
    /// </summary>
    public static TheoryData<string, string, string, string> Vectors() => new()
    {
        // 仕様 1: 加算 64 bit (バイト単位)、加算 (ワード単位。端数は 0 で埋める)、XOR (ワード単位)、インターネットチェックサム。
        { "sum64", "", "", "0000000000000000" },
        { "sum64", "abc", "", "0000000000000126" },
        { "sum64", "123456789", "", "00000000000001DD" },
        { "sum16w", "abc", "LE", "62C4" },
        { "sum16w", "abc", "BE", "C462" },
        { "sum16w", "123456789", "LE", "D509" },
        { "sum16w", "123456789", "BE", "09D4" },
        { "sum32w", "", "LE", "00000000" },
        { "sum32w", "abc", "BE", "61626300" },
        { "sum32w", "123456789", "LE", "6C6A689F" },
        { "sum32w", "123456789", "BE", "9F686A6C" },
        { "sum64w", "123456789", "LE", "383736353433326A" },
        { "sum64w", "123456789", "BE", "6A32333435363738" },
        { "xor16", "", "LE", "0000" },
        { "xor16", "abc", "LE", "6202" },
        { "xor16", "abc", "BE", "0262" },
        { "xor16", "123456789", "LE", "0839" },
        { "xor16", "123456789", "BE", "3908" },
        { "xor32", "123456789", "LE", "0C04043D" },
        { "xor32", "123456789", "BE", "3D04040C" },
        { "xor64", "123456789", "LE", "3837363534333208" },
        { "xor64", "123456789", "BE", "0832333435363738" },
        { "inet16", "", "", "FFFF" },
        { "inet16", "abc", "", "3B9D" },
        { "inet16", "abc", "LE", "9D3B" },
        { "inet16", "123456789", "", "F62A" },
        { "inet16", "123456789", "BE", "F62A" },
        { "inet16", "123456789", "LE", "2AF6" },

        // 仕様 3: Fletcher (32 / 64 の既定は LE)。
        { "fletcher16", "", "", "0000" },
        { "fletcher16", "abc", "", "4C27" },
        { "fletcher16", "123456789", "", "1EDE" },
        { "fletcher16", "abcde", "", "C8F0" },
        { "fletcher16", "abcdef", "", "2057" },
        { "fletcher16", "abcdefgh", "", "0627" },
        { "fletcher32", "", "", "00000000" },
        { "fletcher32", "abc", "", "C52562C4" },
        { "fletcher32", "abc", "BE", "25C5C462" },
        { "fletcher32", "123456789", "", "DF09D509" },
        { "fletcher32", "123456789", "BE", "09DF09D5" },
        { "fletcher32", "abcde", "", "F04FC729" },
        { "fletcher32", "abcdef", "", "56502D2A" },
        { "fletcher32", "abcdefgh", "", "EBE19591" },
        { "fletcher64", "", "", "0000000000000000" },
        { "fletcher64", "abc", "", "0063626100636261" },
        { "fletcher64", "abc", "BE", "6162630061626300" },
        { "fletcher64", "123456789", "", "0D0803376C6A689F" },
        { "fletcher64", "123456789", "BE", "3703080D9F686A6C" },
        { "fletcher64", "abcde", "", "C8C6C527646362C6" },
        { "fletcher64", "abcdef", "", "C8C72B276463C8C6" },
        { "fletcher64", "abcdefgh", "", "312E2B28CCCAC8C6" },

        // 仕様 3: FNV。draft-eastlake-fnv-30 の FNV32svalues / FNV64svalues / FNV128svalues ("", "a", "foobar")。
        { "fnv1a-32", "", "", "811C9DC5" },
        { "fnv1a-32", "a", "", "E40C292C" },
        { "fnv1a-32", "foobar", "", "BF9CF968" },
        { "fnv1a-64", "", "", "CBF29CE484222325" },
        { "fnv1a-64", "a", "", "AF63DC4C8601EC8C" },
        { "fnv1a-64", "foobar", "", "85944171F73967E8" },
        { "fnv1a-128", "", "", "6C62272E07BB014262B821756295C58D" },
        { "fnv1a-128", "a", "", "D228CB696F1A8CAF78912B704E4A8964" },
        { "fnv1a-128", "foobar", "", "343E1662793C64BF6F0D3597BA446F18" },
        { "fnv1a-32", "abc", "", "1A47E90B" },
        { "fnv1a-32", "123456789", "", "BB86B11C" },
        { "fnv1a-64", "abc", "", "E71FA2190541574B" },
        { "fnv1a-64", "123456789", "", "06D5573923C6CDFC" },
        { "fnv1a-128", "abc", "", "A68D622CEC8B5822836DBC7977AF7F3B" },
        { "fnv1a-128", "123456789", "", "DA2D42A08D04E4585DD325117F71D504" },
        { "fnv1-32", "", "", "811C9DC5" },
        { "fnv1-32", "abc", "", "439C2F4B" },
        { "fnv1-32", "123456789", "", "24148816" },
        { "fnv1-64", "", "", "CBF29CE484222325" },
        { "fnv1-64", "abc", "", "D8DCCA186BAFADCB" },
        { "fnv1-64", "123456789", "", "A72FFC362BF916D6" },
        { "fnv1-128", "", "", "6C62272E07BB014262B821756295C58D" },
        { "fnv1-128", "abc", "", "A68BB2A4348B5822836DBC78C6AEE73B" },
        { "fnv1-128", "123456789", "", "8BEA2C73BE03B30FD4142FB1EC2C2066" },

        // 仕様 3: XXH3 (sanity_test_vectors.h の長さ 0 の値と同じ)、MurmurHash3 (空のデータでシード 0 は 0)。
        { "xxh3-64", "", "", "2D06800538D394C2" },
        { "xxh3-128", "", "", "99AA06D3014798D86001C324468D497F" },
        { "murmur3-x86-32", "", "", "00000000" },
        { "murmur3-x64-128", "", "", "00000000000000000000000000000000" },
        { "murmur3-x86-128", "", "", "00000000000000000000000000000000" },
    };

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [MemberData(nameof(Vectors))]
    public void ChecksumsAndHashesMatchTheVectors(string id, string input, string endian, string expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(input);
        HashParameters? p = endian switch { "LE" => Le, "BE" => Be, _ => null };
        Assert.Equal(expected, Hash(id, data, p));
        Assert.Equal(expected, HashPieces(id, data, 5, p));
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-05")]
    public void InternetChecksumMatchesTheRfc1071Example()
    {
        // RFC 1071 の 3 章の例: 00 01 F2 03 F4 F5 F6 F7 の 1 の補数和は DDF2、チェックサムはその 1 の補数 220D。
        byte[] data = [0x00, 0x01, 0xF2, 0x03, 0xF4, 0xF5, 0xF6, 0xF7];
        Assert.Equal("220D", Hash("inet16", data));
        Assert.Equal("0D22", Hash("inet16", data, Le));

        // チェックサムを付けたデータのチェックサムは 0 になる。
        Assert.Equal("0000", Hash("inet16", [.. data, 0x22, 0x0D]));
    }

    [Theory]
    [InlineData("sum64", 64)]
    [InlineData("sum16w", 16)]
    [InlineData("sum32w", 32)]
    [InlineData("sum64w", 64)]
    public void TwosComplementMakesTheTotalZero(string id, int bits)
    {
        // 「2 の補数」: 合計に足すと 0 になる値 (ワード単位は、値をワードとしてデータに加えると合計が 0)。
        byte[] data = new byte[1003];
        new Random(bits).NextBytes(data);
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        ulong sum = HashBytes.ToNumber(Compute(algorithm, data));
        ulong twos = HashBytes.ToNumber(Compute(algorithm, data, new HashParameters { Complement = HashComplement.Twos }));
        ulong ones = HashBytes.ToNumber(Compute(algorithm, data, new HashParameters { Complement = HashComplement.Ones }));
        Assert.Equal(0UL, (sum + twos) & mask);
        Assert.Equal(mask, (sum + ones) & mask);

        // 符号ありでもビット列は同じ (符号は表示だけに影響する)。
        Assert.Equal(sum, HashBytes.ToNumber(Compute(algorithm, data, new HashParameters { Signed = true })));
    }

    [Fact]
    public void WordSumPadsTheTrailingPartialWordWithZero()
    {
        // 3 バイトの 16 bit ワード単位: [01 02] [03 00]。LE は 0201 + 0003、BE は 0102 + 0300。
        Assert.Equal("0204", Hash("sum16w", [0x01, 0x02, 0x03], Le));
        Assert.Equal("0204", Hash("sum16w", [0x01, 0x02, 0x03, 0x00], Le));
        Assert.Equal("0402", Hash("sum16w", [0x01, 0x02, 0x03], Be));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE }, "-2")]
    [InlineData(new byte[] { 0x7F, 0xFF }, "32767")]
    [InlineData(new byte[] { 0x80 }, "-128")]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "-1")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x26 }, "294")]
    public void SignedDecimalFormat(byte[] value, string expected)
    {
        Assert.Equal(expected, HashValueFormatter.FormatSignedDecimal(value));
        Assert.Equal(long.Parse(expected, CultureInfo.InvariantCulture), HashValueFormatter.ToSigned(HashBytes.ToNumber(value), value.Length * 8));
    }

    public static TheoryData<string, string> XxHashSets() => new()
    {
        { "xxh32", "xxh32" },
        { "xxh64", "xxh64" },
        { "xxh3-64", "xxh3_64" },
        { "xxh3-128", "xxh3_128" },
    };

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [MemberData(nameof(XxHashSets))]
    public void XxHashFamilyMatchesTheOfficialSanityVectors(string id, string set)
    {
        // xxHash の公式リポジトリの tests/sanity_test_vectors.h (長さ 0〜300 と、ブロック・ストライプの境界の長さ。シード 0 と素数)。
        using JsonDocument doc = Load("xxhash-sanity.json");
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        byte[] buffer = XxHashSanityBuffer(4160);
        int count = 0;
        foreach (JsonElement v in doc.RootElement.GetProperty(set).EnumerateArray())
        {
            int length = v.GetProperty("len").GetInt32();
            var p = new HashParameters { Seed = Hex(v.GetProperty("seed")) };
            string expected = v.GetProperty("hash").GetString()!.PadLeft(algorithm.Bits / 4, '0');
            byte[] data = buffer[..length];
            Assert.True(expected == Convert.ToHexString(Compute(algorithm, data, p)), $"{id} len={length} seed={p.Seed:X}");
            if (count++ % 5 == 0)
            {
                Assert.Equal(expected, Convert.ToHexString(ComputeInPieces(algorithm, data, length, p, maxPiece: length > 256 ? 300 : 40)));
            }
        }

        Assert.True(count > 250);
    }

    [Theory]
    [InlineData("xxh3-64")]
    [InlineData("xxh3-128")]
    public void Xxh3StreamingAgreesWithOneShotOnLongInputs(string id)
    {
        // 1 MiB を 1 回で、また 1〜5,000 バイトの不規則な長さに分けて渡す (ブロックの区切り・内部バッファの境界をまたぐ)。
        byte[] data = new byte[1024 * 1024 + 17];
        new Random(3).NextBytes(data);
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        foreach (ulong seed in new ulong[] { 0, 0x1234, 0x9E3779B185EBCA8D })
        {
            var p = new HashParameters { Seed = seed };
            string whole = Convert.ToHexString(Compute(algorithm, data, p));
            Assert.Equal(whole, Convert.ToHexString(ComputeInPieces(algorithm, data, 7, p, maxPiece: 5000)));
            Assert.Equal(whole, Convert.ToHexString(ComputeInPieces(algorithm, data, 8, p, maxPiece: 70)));
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-05")]
    public void XxHash64SeedChangesTheValue()
    {
        // 手順 2: シード 0 と 0x1234 の値は異なる (公式の実装の値との一致は sanity_test_vectors.h のシード付きの値で確かめる)。
        Assert.NotEqual(Hash("xxh64", Check9), Hash("xxh64", Check9, new HashParameters { Seed = 0x1234 }));
        Assert.NotEqual(Hash("xxh3-64", Check9), Hash("xxh3-64", Check9, new HashParameters { Seed = 0x1234 }));
    }

    public static TheoryData<string, uint> MurmurVerification() => new()
    {
        // SMHasher (main.cpp の g_hashes) の検証値。
        { "murmur3-x86-32", 0xB0F57EE3 },
        { "murmur3-x86-128", 0xB3ECE62A },
        { "murmur3-x64-128", 0x6384BA69 },
        { "murmur2", 0x27864C1E },
        { "murmur64a", 0x1F0D3804 },
    };

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [MemberData(nameof(MurmurVerification))]
    public void MurmurHashesMatchTheSmhasherVerificationValues(string id, uint expected)
    {
        // SMHasher の VerificationTest: {}, {0}, {0,1}, … {0..254} をシード 256−n で計算して並べ、その並びをシード 0 で計算した値の
        // 先頭 4 バイトをリトルエンディアンで読んだ値。値のバイト列は元の実装の出力 (32 / 64 bit は数値をリトルエンディアンで並べたもの)。
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        int bytes = algorithm.Bits / 8;
        byte[] key = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        byte[] hashes = new byte[bytes * 256];
        for (int i = 0; i < 256; i++)
        {
            var p = new HashParameters { Seed = (ulong)(256 - i) };
            Native(Compute(algorithm, key.AsSpan(0, i), p)).CopyTo(hashes, i * bytes);
            Assert.Equal(Compute(algorithm, key.AsSpan(0, i), p), ComputeInPieces(algorithm, key[..i], i, p, maxPiece: 9));
        }

        byte[] final = Native(Compute(algorithm, hashes));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(final));

        byte[] Native(byte[] value) => algorithm.Bits <= 64 ? [.. value.Reverse()] : value;
    }

    [Fact]
    public void Murmur2WithoutDeclaredLengthBuffersTheData()
    {
        // 長さを宣言せずに Append した場合も、Finish で同じ値になる (小さなデータ用の経路)。
        byte[] data = new byte[1000];
        new Random(9).NextBytes(data);
        foreach (string id in new[] { "murmur2", "murmur64a" })
        {
            HashAlgorithmInfo algorithm = HashCatalog.Get(id);
            IHasher hasher = algorithm.CreateHasher(new HashParameters { Seed = 77 });
            hasher.Append(data.AsSpan(0, 333));
            hasher.Append(data.AsSpan(333));
            Assert.Equal(Compute(algorithm, data, new HashParameters { Seed = 77 }), hasher.Finish());
        }
    }

    [Fact]
    public void HashEngineDeclaresTheInputLengthForMurmur2()
    {
        // 除外範囲 (飛ばす・置き換える) と範囲ごとの計算で、エンジンが宣言する長さが実際に渡すデータの長さと一致する。
        byte[] data = new byte[50_000];
        new Random(4).NextBytes(data);
        using var doc = new HexEditor.Core.Engine.Document(new HexEditor.Core.Sources.MemoryByteSource(data), Options());
        HashAlgorithmChoice[] choices = [new(HashCatalog.Get("murmur2")), new(HashCatalog.Get("murmur64a"), new HashParameters { Seed = 5 })];
        HashComputation skip = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = choices,
            Ranges = [new HashRange(100, 40_000)],
            Exclusions = [new HashRange(1000, 500)],
            ChunkSize = 4096,
        });
        byte[] skipped = [.. data.AsSpan(100, 900), .. data.AsSpan(1500, 38_600)];
        Assert.Equal(Compute(choices[0].Algorithm, skipped), skip.Rows[0].Value);
        Assert.Equal(Compute(choices[1].Algorithm, skipped, choices[1].Parameters), skip.Rows[1].Value);

        HashComputation replace = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = choices,
            Ranges = [new HashRange(0, 10_000), new HashRange(20_000, 5_000)],
            RangeMode = HashRangeMode.PerRange,
            Exclusions = [new HashRange(21_000, 10)],
            ExclusionMode = HashExclusionMode.Replace,
        });
        byte[] second = data[20_000..25_000];
        second.AsSpan(1000, 10).Clear();
        Assert.Equal(Compute(choices[0].Algorithm, data.AsSpan(0, 10_000)), replace.Rows[0].Value);
        Assert.Equal(Compute(choices[0].Algorithm, second), replace.Rows[2].Value);
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-05")]
    public void SipHashMatchesTheOfficialVectors()
    {
        // 鍵 00 01 … 0F、入力 00 01 … (長さ 0〜63)。SipHash-2-4 は公式リポジトリの vectors.h (64 bit と 128 bit)、
        // SipHash-1-3 は Rust の標準ライブラリのテスト。vectors.h のバイト列は 64 bit の値のリトルエンディアン (この実装の値はその逆順)。
        using JsonDocument doc = Load("siphash-vectors.json");
        string[] sip24 = [.. doc.RootElement.GetProperty("siphash24_64").EnumerateArray().Select(e => e.GetString()!)];
        string[] sip24Wide = [.. doc.RootElement.GetProperty("siphash24_128").EnumerateArray().Select(e => e.GetString()!)];
        string[] sip13 = [.. doc.RootElement.GetProperty("siphash13_64").EnumerateArray().Select(e => e.GetString()!)];
        var key = new HashParameters { KeyHex = "000102030405060708090A0B0C0D0E0F" };
        var keyWide = key with { OutputBits = 128 };
        byte[] input = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        for (int n = 0; n < 64; n++)
        {
            byte[] data = input[..n];
            Assert.Equal(Reverse(sip24[n]), Hash("siphash-2-4", data, key));
            Assert.Equal(Reverse(sip24[n]), HashPieces("siphash-2-4", data, n, key));
            Assert.Equal(sip24Wide[n], Hash("siphash-2-4", data, keyWide));
            Assert.Equal(sip24Wide[n], HashPieces("siphash-2-4", data, n, keyWide));
            Assert.Equal(Reverse(sip13[n]), Hash("siphash-1-3", data, key));
            Assert.Equal(Reverse(sip13[n]), HashPieces("siphash-1-3", data, n, key));
        }

        // 論文の付録 A: 15 バイトのメッセージの値 a129ca6149be45e5。
        Assert.Equal("A129CA6149BE45E5", Hash("siphash-2-4", input[..15], key));

        static string Reverse(string hex) => Convert.ToHexString([.. Convert.FromHexString(hex).Reverse()]);
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-05")]
    public void SipHash13With128BitOutputMatchesTheReferenceImplementation()
    {
        // 参照実装 (veorq/SipHash の siphash.c を cROUNDS=1、dROUNDS=3、outlen=16 で動かしたもの) の値。値のバイト列は参照実装の出力順。
        using JsonDocument doc = Load("siphash-vectors.json");
        string[] sip13Wide = [.. doc.RootElement.GetProperty("siphash13_128").EnumerateArray().Select(e => e.GetString()!)];
        var key = new HashParameters { KeyHex = "000102030405060708090A0B0C0D0E0F", OutputBits = 128 };
        byte[] input = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        Assert.Equal(64, sip13Wide.Length);
        for (int n = 0; n < 64; n++)
        {
            Assert.Equal(sip13Wide[n], Hash("siphash-1-3", input[..n], key));
            Assert.Equal(sip13Wide[n], HashPieces("siphash-1-3", input[..n], n, key));
        }

        // 128 bit の値は数値として扱わない (バイト順の選択・10 進表示をしない)。64 bit の値は数値。
        HashAlgorithmInfo sip = HashCatalog.Get("siphash-1-3");
        Assert.False(new HashResultRow(new HashAlgorithmChoice(sip, key), new byte[16], []).IsNumeric);
        Assert.True(new HashResultRow(new HashAlgorithmChoice(sip, key with { OutputBits = 0 }), new byte[8], []).IsNumeric);
    }

    private static readonly string[] FnvInputs = ["", "a", "b", "c", "d", "e", "f", "fo", "foo", "foob", "fooba", "foobar"];

    public static TheoryData<string, int, string> FnvReferenceVectors()
    {
        // FNV の作者のテストプログラム (lcn2/fnv の test_fnv.c) の fnv1_32_vector、fnv1a_32_vector、fnv1_64_vector、fnv1a_64_vector の
        // 先頭 12 件 (入力は FnvInputs)。
        var data = new TheoryData<string, int, string>();
        void Add(string id, params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                data.Add(id, i, values[i]);
            }
        }

        Add("fnv1-32", "811C9DC5", "050C5D7E", "050C5D7D", "050C5D7C", "050C5D7B", "050C5D7A", "050C5D79", "6B772514", "408F5E13", "B4B1178B",
            "FDC80FB0", "31F0B262");
        Add("fnv1a-32", "811C9DC5", "E40C292C", "E70C2DE5", "E60C2C52", "E10C2473", "E00C22E0", "E30C2799", "6222E842", "A9F37ED7", "3F5076EF",
            "39AAA18A", "BF9CF968");
        Add("fnv1-64", "CBF29CE484222325", "AF63BD4C8601B7BE", "AF63BD4C8601B7BD", "AF63BD4C8601B7BC", "AF63BD4C8601B7BB", "AF63BD4C8601B7BA",
            "AF63BD4C8601B7B9", "08326207B4EB2F34", "D8CBC7186BA13533", "0378817EE2ED65CB", "D329D59B9963F790", "340D8765A4DDA9C2");
        Add("fnv1a-64", "CBF29CE484222325", "AF63DC4C8601EC8C", "AF63DF4C8601F1A5", "AF63DE4C8601EFF2", "AF63D94C8601E773", "AF63D84C8601E5C0",
            "AF63DB4C8601EAD9", "08985907B541D342", "DCB27518FED9D577", "DD120E790C2512AF", "CAC165AFA2FEF40A", "85944171F73967E8");
        return data;
    }

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [MemberData(nameof(FnvReferenceVectors))]
    public void FnvMatchesTheAuthorsTestProgram(string id, int index, string expected) =>
        Assert.Equal(expected, Hash(id, Encoding.ASCII.GetBytes(FnvInputs[index])));

    public static TheoryData<ulong, int, string> XxHash64WideSeeds() => new()
    {
        // 公式の sanity_test_vectors.h の XXH64 はシード 0 と 0x9E3779B1 (32 bit に収まる値) だけなので、上位 32 bit を使うシードの値を補う。
        // 値は doc/xxhash_spec.md から書いた独立の Python 実装で求めた (同じ実装が sanity_test_vectors.h の XXH64 の 1,274 件と一致する)。
        // 入力は sanity_test_vectors.h と同じ XSUM_fillTestBuffer の先頭。
        { 0x1234, 0, "6E01C0317D5C53D0" },
        { 0x1234, 32, "4CD53573A83EECAE" },
        { 0x1234, 255, "B6EBBCB12A578F0A" },
        { 0x9E3779B185EBCA8D, 0, "0B303D920EC349DF" },
        { 0x9E3779B185EBCA8D, 1, "9C6678669FCD2E6D" },
        { 0x9E3779B185EBCA8D, 4, "CCFE4EAD7E01983C" },
        { 0x9E3779B185EBCA8D, 8, "768161B4E5A58DFA" },
        { 0x9E3779B185EBCA8D, 31, "51AAF1A336575F00" },
        { 0x9E3779B185EBCA8D, 32, "21D817283F4B6283" },
        { 0x9E3779B185EBCA8D, 100, "38F1D4AABFD12D0F" },
        { 0x9E3779B185EBCA8D, 255, "03D699D52E8CD292" },
        { 0xFFFFFFFFFFFFFFFF, 0, "298F4C84B24F5380" },
        { 0xFFFFFFFFFFFFFFFF, 31, "6ED1061F61C686E5" },
        { 0xFFFFFFFFFFFFFFFF, 100, "2FE1B1460F4666C4" },
        { 0x0123456789ABCDEF, 8, "33812B1E73B94CC4" },
        { 0x0123456789ABCDEF, 255, "7D2FDF1DB543C7FD" },
    };

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [MemberData(nameof(XxHash64WideSeeds))]
    public void XxHash64UsesAllSixtyFourSeedBits(ulong seed, int length, string expected)
    {
        byte[] data = XxHashSanityBuffer(length);
        var p = new HashParameters { Seed = seed };
        Assert.Equal(expected, Hash("xxh64", data, p));
        Assert.Equal(expected, HashPieces("xxh64", data, length, p));
    }

    [Theory]
    [InlineData("xxh32")]
    [InlineData("murmur3-x86-32")]
    [InlineData("murmur3-x86-128")]
    [InlineData("murmur3-x64-128")]
    [InlineData("murmur2")]
    public void ThirtyTwoBitSeedsOutOfRangeAreParameterErrors(string id)
    {
        // 参照実装のシードが 32 bit のアルゴリズムは、32 bit を超えるシードを黙って切り詰めず、パラメータの誤りにする (ANA-19 の仕様 3)。
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        Assert.Equal(HashParameterError.None, algorithm.Validate(new HashParameters { Seed = uint.MaxValue }));
        Assert.Equal(HashParameterError.SeedRange, algorithm.Validate(new HashParameters { Seed = 1UL << 32 }));
    }

    [Theory]
    [InlineData("xxh64")]
    [InlineData("xxh3-64")]
    [InlineData("xxh3-128")]
    [InlineData("murmur64a")]
    public void SixtyFourBitSeedsAreAccepted(string id) =>
        Assert.Equal(HashParameterError.None, HashCatalog.Get(id).Validate(new HashParameters { Seed = ulong.MaxValue }));

    [Fact]
    public void SipHashParameters()
    {
        HashAlgorithmInfo sip = HashCatalog.Get("siphash-2-4");
        Assert.Equal(64, sip.BitsFor(HashParameters.Default));
        Assert.Equal(128, sip.BitsFor(new HashParameters { OutputBits = 128 }));
        Assert.Equal(HashParameterError.None, sip.Validate(HashParameters.Default));
        Assert.Equal(HashParameterError.KeyLength, sip.Validate(new HashParameters { KeyHex = "0001" }));
        Assert.Equal(HashParameterError.OutputLength, sip.Validate(new HashParameters { OutputBits = 96 }));

        // 鍵の既定はすべて 0。
        Assert.Equal(Hash("siphash-2-4", Check9), Hash("siphash-2-4", Check9, new HashParameters { KeyHex = new string('0', 32) }));
        Assert.Equal(16, Compute(sip, Check9, new HashParameters { OutputBits = 128 }).Length);
    }

    [Fact]
    public void LongInputsAgreeBetweenOneShotAndPieces()
    {
        // 加算・XOR・Fletcher の剰余の遅延 (まとめて剰余を取る) とワードの端数の持ち越し。
        byte[] data = new byte[300_001];
        new Random(21).NextBytes(data);
        string[] ids = ["sum64", "sum16w", "sum32w", "sum64w", "inet16", "xor16", "xor32", "xor64", "fletcher16", "fletcher32", "fletcher64",
            "fnv1-32", "fnv1a-64", "fnv1a-128", "murmur3-x86-32", "murmur3-x86-128", "murmur3-x64-128", "murmur2", "murmur64a",
            "siphash-2-4", "siphash-1-3", "cksum-length"];
        foreach (string id in ids)
        {
            Assert.Equal(Hash(id, data), HashPieces(id, data, 13));
        }

        // Fletcher-64 の剰余の遅延 (要素がすべて最大値でも正しい)。
        byte[] ones = Enumerable.Repeat((byte)0xFF, 400_000).ToArray();
        Assert.Equal("0000000000000000", Hash("fletcher64", ones));
        Assert.Equal("0000", Hash("fletcher16", ones));
    }

    [Fact]
    public void CatalogueHasThePhaseTwoAlgorithmsInTableOrder()
    {
        string[] checksums = [.. HashCatalog.BuiltIn.Where(a => a.Group == HashGroup.Checksum).Select(a => a.Id)];
        Assert.Equal(["sum8", "sum16", "sum32", "sum64", "sum16w", "sum32w", "sum64w", "inet16", "xor8", "xor16", "xor32", "xor64"], checksums);
        string[] nonCrypto = [.. HashCatalog.BuiltIn.Where(a => a.Group == HashGroup.NonCryptographic).Select(a => a.Id)];
        Assert.Equal(
            ["adler32", "fletcher16", "fletcher32", "fletcher64", "fnv1-32", "fnv1a-32", "fnv1-64", "fnv1a-64", "fnv1-128", "fnv1a-128",
                "xxh32", "xxh64", "xxh3-64", "xxh3-128", "murmur3-x86-32", "murmur3-x86-128", "murmur3-x64-128", "murmur2", "murmur64a",
                "siphash-2-4", "siphash-1-3"],
            nonCrypto);

        // 識別子・名前・別名で探すと、そのアルゴリズムが見つかる (別名が他と重複しない)。
        foreach (HashAlgorithmInfo a in HashCatalog.BuiltIn)
        {
            foreach (string name in a.Aliases.Append(a.Name).Append(a.Id))
            {
                Assert.True(ReferenceEquals(a, HashCatalog.Find(name)), $"{a.Id}: {name}");
            }
        }

        Assert.Equal("Sum 16 (words) (two's complement, BE, signed)",
            HashCatalog.Get("sum16w").DisplayName(new HashParameters { BigEndian = true, Signed = true, Complement = HashComplement.Twos }));
    }
}
