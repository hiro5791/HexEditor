using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Hashing;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-19 対応アルゴリズムのテストベクタ。</summary>
public sealed class HashAlgorithmTests
{
    private static readonly byte[] Check9 = Encoding.ASCII.GetBytes("123456789");
    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");
    private static readonly byte[] Msg448 = Encoding.ASCII.GetBytes("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq");
    private static readonly byte[] MillionA = Enumerable.Repeat((byte)'a', 1_000_000).ToArray();

    private static string Hash(string id, byte[] data, HashParameters? parameters = null) =>
        Convert.ToHexString(HashEngine.ComputeBytes(HashCatalog.Get(id), data, parameters));

    /// <summary>データを不規則な長さに分けて渡しても、1 回で渡したときと同じ値になる。</summary>
    private static string HashInPieces(string id, byte[] data, int seed, HashParameters? parameters = null)
    {
        IHasher hasher = HashCatalog.Get(id).CreateHasher(parameters);
        var random = new Random(seed);
        int at = 0;
        while (at < data.Length)
        {
            int n = Math.Min(data.Length - at, random.Next(0, 70));
            hasher.Append(data.AsSpan(at, n));
            at += n;
        }

        return Convert.ToHexString(hasher.Finish());
    }

    [Theory]
    [Trait(TC, "TC-ANA-19-01")]
    [InlineData("crc16-arc", "BB3D")]
    [InlineData("crc16-ibm3740", "29B1")]
    [InlineData("crc32", "CBF43926")]
    [InlineData("crc32c", "E3069283")]
    public void PhaseOneCrcsMatchTheCatalogueCheckValues(string id, string check)
    {
        Assert.Equal(check, Hash(id, Check9));
        Assert.Equal(check, HashInPieces(id, Check9, 1));
        CrcParameters p = HashCatalog.Get(id).Crc!;
        Assert.Equal(Convert.ToUInt64(check, 16), p.Check);
        Assert.Equal(p.Check, CrcHasher.Reference(p, Check9));
    }

    [Theory]
    [Trait(TC, "TC-ANA-19-01")]
    [InlineData("crc16-arc")]
    [InlineData("crc16-ibm3740")]
    [InlineData("crc32")]
    [InlineData("crc32c")]
    public void PhaseOneCrcsMatchTheBitwiseReferenceForRandomData(string id)
    {
        // 長さ 0〜1,024 の無作為なデータで、テーブル方式 (CRC-32C は CPU の命令) をカタログの定義どおりのビット単位の実装と比べる。
        CrcParameters p = HashCatalog.Get(id).Crc!;
        var random = new Random(0x1917);
        for (int length = 0; length <= 1024; length += random.Next(1, 9))
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            string expected = Convert.ToHexString(HashBytes.FromNumber(CrcHasher.Reference(p, data), (p.Width + 7) / 8));
            Assert.Equal(expected, Hash(id, data));
            Assert.Equal(expected, HashInPieces(id, data, length));
        }
    }

    [Fact]
    public void TableCrcMatchesReferenceForOtherWidthsAndDirections()
    {
        // カスタム CRC (ANA-20、フェーズ 2) の前提: 幅 5 (ビット単位)・幅 24 / 64 (テーブル方式)・refin と refout が異なるもの。
        CrcParameters[] parameters =
        [
            new(5, 0x05, 0x1F, true, true, 0x1F, 0x19),                                  // CRC-5/USB
            new(24, 0x864CFB, 0xB704CE, false, false, 0, 0x21CF02),                    // CRC-24/OPENPGP
            new(64, 0x42F0E1EBA9EA3693, ulong.MaxValue, true, true, ulong.MaxValue, 0x995DC9BBDF1939FA), // CRC-64/XZ
            new(64, 0x42F0E1EBA9EA3693, 0, false, false, 0, 0x6C40DF5F0B497347),       // CRC-64/ECMA-182
            new(12, 0x80F, 0, false, true, 0, 0xDAF),                                  // CRC-12/UMTS
        ];
        var random = new Random(5);
        foreach (CrcParameters p in parameters)
        {
            IHasher check = new CrcHasher(p);
            check.Append(Check9);
            Assert.Equal(p.Check, HashBytes.ToNumber(check.Finish()));
            for (int length = 0; length < 300; length += 7)
            {
                byte[] data = new byte[length];
                random.NextBytes(data);
                var hasher = new CrcHasher(p);
                hasher.Append(data);
                Assert.Equal(CrcHasher.Reference(p, data), HashBytes.ToNumber(hasher.Finish()));
            }
        }
    }

    public static TheoryData<string, string, string> CryptoVectors() => new()
    {
        // RFC 1321 の付録 A.5。
        { "md5", "", "D41D8CD98F00B204E9800998ECF8427E" },
        { "md5", "abc", "900150983CD24FB0D6963F7D28E17F72" },

        // FIPS 180-2 の付録 (NIST CSRC の「Examples with Intermediate Values」)。
        { "sha1", "", "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709" },
        { "sha1", "abc", "A9993E364706816ABA3E25717850C26C9CD0D89D" },
        { "sha1", "448", "84983E441C3BD26EBAAE4AA1F95129E5E54670F1" },
        { "sha1", "million", "34AA973CD4C4DAA4F61EEB2BDBAD27316534016F" },
        { "sha256", "", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855" },
        { "sha256", "abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" },
        { "sha256", "448", "248D6A61D20638B8E5C026930C3E6039A33CE45964FF2167F6ECEDD419DB06C1" },
        { "sha256", "million", "CDC76E5C9914FB9281A1C7E284D73E67F1809A48A497200E046D39CCC7112CD0" },
        { "sha384", "", "38B060A751AC96384CD9327EB1B1E36A21FDB71114BE07434C0CC7BF63F6E1DA274EDEBFE76F65FBD51AD2F14898B95B" },
        { "sha384", "abc", "CB00753F45A35E8BB5A03D699AC65007272C32AB0EDED1631A8B605A43FF5BED8086072BA1E7CC2358BAECA134C825A7" },
        { "sha512", "", "CF83E1357EEFB8BDF1542850D66D8007D620E4050B5715DC83F4A921D36CE9CE47D0D13C5D85F2B0FF8318D2877EEC2F63B931BD47417A81A538327AF927DA3E" },
        { "sha512", "abc", "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F" },
        { "sha512", "448", "204A8FC6DDA82F0A0CED7BEB8E08A41657C16EF468B228A8279BE331A703C33596FD15C13B1B07F9AA1D3BEA57789CA031AD85C7A71DD70354EC631238CA3445" },
        { "sha512", "million", "E718483D0CE769644E2E42C7BC15B4638E1F98B13B2044285632A803AFA973EBDE0FF244877EA60A4CB0432CE577C31BEB009C5C2C49AA2E4EADB217AD8CC09B" },
    };

    [Theory]
    [Trait(TC, "TC-ANA-19-03")]
    [MemberData(nameof(CryptoVectors))]
    public void PhaseOneCryptographicHashesMatchOfficialVectors(string id, string input, string expected)
    {
        byte[] data = input switch
        {
            "abc" => Abc,
            "448" => Msg448,
            "million" => MillionA,
            _ => [],
        };
        Assert.Equal(expected, Hash(id, data));
        Assert.Equal(expected, HashInPieces(id, data, 3));
    }

    [Theory]
    [Trait(TC, "TC-ANA-19-05")]
    [InlineData("adler32", "", "00000001")]
    [InlineData("adler32", "abc", "024D0127")]
    [InlineData("adler32", "123456789", "091E01DE")]
    [InlineData("sum8", "", "00")]
    [InlineData("sum8", "abc", "26")]
    [InlineData("sum8", "123456789", "DD")]
    [InlineData("sum16", "123456789", "01DD")]
    [InlineData("sum32", "123456789", "000001DD")]
    [InlineData("xor8", "", "00")]
    [InlineData("xor8", "abc", "60")]
    [InlineData("xor8", "123456789", "31")]
    [InlineData("xxh32", "", "02CC5D05")]
    [InlineData("xxh32", "abc", "32D153FF")]
    [InlineData("xxh64", "", "EF46DB3751D8E999")]
    [InlineData("xxh64", "abc", "44BC2CF5AD770999")]
    public void ChecksumsAndNonCryptographicHashesMatchVectors(string id, string input, string expected)
    {
        // フェーズ 1 の範囲: Adler-32 (RFC 1950 の定義)、加算 8 / 16 / 32 bit、XOR 8 bit。xxHash は TC-ANA-22-01 で使うため加えた。
        byte[] data = Encoding.ASCII.GetBytes(input);
        Assert.Equal(expected, Hash(id, data));
        Assert.Equal(expected, HashInPieces(id, data, 9));
    }

    [Fact]
    public void LongInputsAgreeBetweenOneShotAndPieces()
    {
        // ベクタ化した経路 (16 バイトずつの加算・XOR、xxHash のストライプ) と端数の扱い。
        byte[] data = new byte[100_003];
        new Random(77).NextBytes(data);
        ulong sum = 0;
        byte xor = 0;
        foreach (byte b in data)
        {
            sum += b;
            xor ^= b;
        }

        Assert.Equal(((uint)sum).ToString("X8"), Hash("sum32", data));
        Assert.Equal(xor.ToString("X2"), Hash("xor8", data));
        foreach (string id in new[] { "sum8", "sum16", "sum32", "xor8", "adler32", "xxh32", "xxh64", "crc32", "crc32c" })
        {
            Assert.Equal(Hash(id, data), HashInPieces(id, data, 11));
        }
    }

    [Fact]
    public void XxHash64SeedChangesTheValueAndTheRowName()
    {
        HashAlgorithmInfo xxh64 = HashCatalog.Get("xxh64");
        var seeded = new HashParameters { Seed = 0x1234 };
        Assert.NotEqual(Hash("xxh64", Check9), Hash("xxh64", Check9, seeded));
        Assert.Equal(Hash("xxh64", Check9, seeded), HashInPieces("xxh64", Check9, 2, seeded));
        Assert.Equal("xxHash64 (seed=0x1234)", xxh64.DisplayName(seeded));
        Assert.Equal("xxHash64", xxh64.DisplayName(HashParameters.Default));
    }

    private static Arbitrary<byte[]> Data() =>
        Gen.Choose(0, 4096).SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), n)).ToArbitrary();

    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-ANA-19-06")]
    public Property Sum8TwosComplementMakesTheTotalZero() => Prop.ForAll(Data(), data =>
    {
        byte twos = HashEngine.ComputeBytes(HashCatalog.Get("sum8"), data, new HashParameters { Complement = HashComplement.Twos })[0];
        byte ones = HashEngine.ComputeBytes(HashCatalog.Get("sum8"), data, new HashParameters { Complement = HashComplement.Ones })[0];
        int total = data.Sum(b => b);
        return (total + twos) % 256 == 0 && (total + ones) % 256 == 0xFF;
    });

    [Fact]
    public void CatalogueAndSets()
    {
        // 既定のセット「よく使う」(ANA-18 の仕様 3)。
        Assert.Equal(["crc32", "md5", "sha1", "sha256"], HashCatalog.DefaultSet.AlgorithmIds);
        Assert.All(HashCatalog.BuiltInSets.SelectMany(s => s.AlgorithmIds), id => Assert.NotNull(HashCatalog.Find(id)));

        // 絞り込みは名前と別名を、大文字・小文字を区別しない序数比較で比べる (ANA-19 の「画面」)。
        Assert.Equal(["crc16-ibm3740"], HashCatalog.Filter("ccitt-false").Select(a => a.Id));
        Assert.Equal(["crc32c"], HashCatalog.Filter("CRC-32/ISCSI").Select(a => a.Id));
        Assert.Equal(HashCatalog.All.Count, HashCatalog.Filter(" ").Count());

        // フェーズ 1 のアルゴリズムが揃っている。MD5 と SHA-1 は安全でない注記を付ける。
        string[] phaseOne = ["sum8", "sum16", "sum32", "xor8", "crc16-arc", "crc16-ibm3740", "crc32", "crc32c", "adler32", "md5", "sha1", "sha256", "sha384", "sha512"];
        Assert.All(phaseOne, id => Assert.True(HashCatalog.Get(id).IsAvailable));
        Assert.True(HashCatalog.Get("md5").IsInsecure);
        Assert.True(HashCatalog.Get("sha1").IsInsecure);
        Assert.False(HashCatalog.Get("sha256").IsInsecure);
        Assert.True(HashCatalog.Get("sha3-256").IsAvailable); // OS の SHA-3 が使えなくても代わりの実装で計算できる (ANA-19 の仕様 5)。
    }

    [Fact]
    public void SelectionRoundTripsThroughSettings()
    {
        HashAlgorithmChoice[] choices =
        [
            new(HashCatalog.Get("crc32")),
            new(HashCatalog.Get("xxh64"), new HashParameters { Seed = 0x1234 }),
            new(HashCatalog.Get("sum8"), new HashParameters { Complement = HashComplement.Twos }),
        ];
        Assert.Equal(choices, HashSelection.Deserialize(HashSelection.Serialize(choices)));
        Assert.Empty(HashSelection.Deserialize("not json"));
        Assert.Equal(["crc32"], HashSelection.Deserialize("[{\"id\":\"crc32\"},{\"id\":\"unknown\"}]").Select(c => c.Algorithm.Id));

        var sets = new[] { new UserHashSet("Mine", choices) };
        UserHashSet back = Assert.Single(HashSelection.DeserializeSets(HashSelection.SerializeSets(sets)));
        Assert.Equal("Mine", back.Name);
        Assert.Equal(choices, back.Algorithms);

        Assert.True(HashSelection.TryParseSeed("0x1234", out ulong hex));
        Assert.Equal(0x1234UL, hex);
        Assert.True(HashSelection.TryParseSeed("4660", out ulong dec));
        Assert.Equal(4660UL, dec);
        Assert.False(HashSelection.TryParseSeed("-1", out _));
    }

    [Fact]
    public void ValuesAreFormattedPerDisplaySettings()
    {
        byte[] crc = Convert.FromHexString("CBF43926");
        Assert.Equal("CBF43926", HashValueFormatter.Format(crc, true, HashValueFormat.HexUpper));
        Assert.Equal("cbf43926", HashValueFormatter.Format(crc, true, HashValueFormat.HexLower));
        Assert.Equal("2639F4CB", HashValueFormatter.Format(crc, true, HashValueFormat.HexUpper, littleEndian: true));
        Assert.Equal("Jjn0yw==", HashValueFormatter.Format(crc, true, HashValueFormat.Base64, littleEndian: true));
        Assert.Equal("3421780262", HashValueFormatter.Format(crc, true, HashValueFormat.Decimal));
        Assert.Equal("3421780262", HashValueFormatter.Format(crc, true, HashValueFormat.Decimal, littleEndian: true));

        // 64 bit を超える値は 10 進で表示せず、バイト順も変えない。
        byte[] md5 = Convert.FromHexString("25F9E794323B453885F5181F1B624D0B");
        Assert.Equal("25F9E794323B453885F5181F1B624D0B", HashValueFormatter.Format(md5, false, HashValueFormat.Decimal, littleEndian: true));
    }
}
