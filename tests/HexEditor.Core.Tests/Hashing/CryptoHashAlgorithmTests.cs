using System.Security.Cryptography;
using HexEditor.Core.Hashing;
using HexEditor.Core.Hashing.Crypto;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>暗号学的ハッシュの一覧・パラメータ・分割入力・SHA-3 の代わりの実装 (ANA-19 の仕様 4・5)。</summary>
public sealed class CryptoHashAlgorithmTests
{
    private static readonly string[] TableOrder =
    [
        "md2", "md4", "md5", "sha1", "sha224", "sha256", "sha384", "sha512", "sha512-224", "sha512-256",
        "sha3-224", "sha3-256", "sha3-384", "sha3-512", "shake128", "shake256", "keccak256",
        "ripemd128", "ripemd160", "ripemd256", "ripemd320", "tiger", "tiger2", "whirlpool", "blake2s", "blake2b", "blake3", "tth", "ed2k",
    ];

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value);

    [Fact]
    public void CryptographicGroupFollowsTheSpecTableOrder()
    {
        Assert.Equal(TableOrder, HashCatalog.BuiltIn.Where(a => a.Group == HashGroup.Cryptographic).Select(a => a.Id));
        Assert.All(TableOrder, id => Assert.True(HashCatalog.Get(id).IsAvailable));

        // 安全でない注記は MD2 / MD4 / MD5 / SHA-1 だけ。遅い注記は MD2 と Whirlpool。
        Assert.Equal(["md2", "md4", "md5", "sha1"], TableOrder.Where(id => HashCatalog.Get(id).IsInsecure));
        Assert.Equal(["md2", "whirlpool"], TableOrder.Where(id => HashCatalog.Get(id).IsSlow));
        Assert.Equal(["tth"], TableOrder.Where(id => HashCatalog.Get(id).PrefersBase32));
        Assert.All(TableOrder, id => Assert.False(HashCatalog.Get(id).IsNumeric));

        // 別名で探せる。
        Assert.Equal("ripemd160", HashCatalog.Get("RMD160").Id);
        Assert.Equal("ripemd160", HashCatalog.Get("RIPEMD-160").Id);
        Assert.Equal("sha512-256", HashCatalog.Get("SHA512/256").Id);
        Assert.Equal("sha3-256", HashCatalog.Get("SHA3_256").Id);
        Assert.Equal(["tth"], HashCatalog.Filter("tiger tree").Select(a => a.Id));

        // 既定のビット数。
        int[] bits = [128, 128, 128, 160, 224, 256, 384, 512, 224, 256, 224, 256, 384, 512, 256, 512, 256, 128, 160, 256, 320, 192, 192, 512, 256, 512, 256, 192, 128];
        Assert.Equal(bits, TableOrder.Select(id => HashCatalog.Get(id).Bits));
        Assert.All(TableOrder, id => Assert.Equal(HashCatalog.Get(id).Bits / 8, HashEngine.ComputeBytes(HashCatalog.Get(id), "abc"u8).Length));
    }

    [Fact]
    public void ParameterKindsFollowTheSpecTable()
    {
        Assert.Equal(HashParameterKinds.OutputLength, HashCatalog.Get("shake128").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength, HashCatalog.Get("shake256").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength, HashCatalog.Get("tiger").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength, HashCatalog.Get("tiger2").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength | HashParameterKinds.Key, HashCatalog.Get("blake2s").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength | HashParameterKinds.Key, HashCatalog.Get("blake2b").Parameters);
        Assert.Equal(HashParameterKinds.OutputLength | HashParameterKinds.Key, HashCatalog.Get("blake3").Parameters);
        Assert.Equal(HashParameterKinds.Ed2kMode, HashCatalog.Get("ed2k").Parameters);
        string[] withParameters = ["shake128", "shake256", "tiger", "tiger2", "blake2s", "blake2b", "blake3", "ed2k"];
        Assert.All(TableOrder.Except(withParameters), id => Assert.Equal(HashParameterKinds.None, HashCatalog.Get(id).Parameters));
    }

    [Theory]
    [InlineData("shake128", 0, null, HashParameterError.None, 256)]
    [InlineData("shake128", 8, null, HashParameterError.None, 8)]
    [InlineData("shake128", 8192, null, HashParameterError.None, 8192)]
    [InlineData("shake128", 8200, null, HashParameterError.OutputLength, 8200)]
    [InlineData("shake256", 12, null, HashParameterError.OutputLength, 12)]
    [InlineData("shake256", -8, null, HashParameterError.OutputLength, -8)]
    [InlineData("tiger", 160, null, HashParameterError.None, 160)]
    [InlineData("tiger2", 128, null, HashParameterError.None, 128)]
    [InlineData("tiger", 64, null, HashParameterError.OutputLength, 64)]
    [InlineData("blake2s", 256, "", HashParameterError.None, 256)]
    [InlineData("blake2s", 264, null, HashParameterError.OutputLength, 264)]
    [InlineData("blake2s", 8, "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", HashParameterError.None, 8)]
    [InlineData("blake2s", 0, "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20", HashParameterError.KeyLength, 256)]
    [InlineData("blake2b", 520, null, HashParameterError.OutputLength, 520)]
    [InlineData("blake2b", 0, "00", HashParameterError.None, 512)]
    [InlineData("blake2b", 0, "XYZ", HashParameterError.KeyLength, 512)]
    [InlineData("blake3", 1024, null, HashParameterError.None, 1024)]
    [InlineData("blake3", 0, "000102030405060708090A0B0C0D0E0F", HashParameterError.KeyLength, 256)]
    [InlineData("blake3", 0, "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", HashParameterError.None, 256)]
    [InlineData("blake3", 4, null, HashParameterError.OutputLength, 4)]
    public void ParametersAreValidated(string id, int outputBits, string? keyHex, HashParameterError error, int bits)
    {
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        var parameters = new HashParameters { OutputBits = outputBits, KeyHex = keyHex };
        Assert.Equal(error, algorithm.Validate(parameters));
        Assert.Equal(bits, algorithm.BitsFor(parameters));
        if (error == HashParameterError.None)
        {
            Assert.Equal(bits / 8, HashEngine.ComputeBytes(algorithm, "abc"u8, parameters).Length);
        }
    }

    [Fact]
    public void ParametersAppearInTheRowName()
    {
        Assert.Equal("SHAKE128 (1024 bit)", HashCatalog.Get("shake128").DisplayName(new HashParameters { OutputBits = 1024 }));
        Assert.Equal("SHAKE128", HashCatalog.Get("shake128").DisplayName(new HashParameters { OutputBits = 256 }));
        Assert.Equal("BLAKE3 (key=00FF)", HashCatalog.Get("blake3").DisplayName(new HashParameters { KeyHex = "00FF" }));
        Assert.Equal("ed2k (red)", HashCatalog.Get("ed2k").DisplayName(new HashParameters { Ed2k = Ed2kMode.Red }));
        Assert.Equal("ed2k", HashCatalog.Get("ed2k").DisplayName(HashParameters.Default));
    }

    [Fact]
    public void OutputLengthsArePrefixesOfTheLongOutput()
    {
        // SHAKE と BLAKE3 は XOF なので短い出力は長い出力の先頭。Tiger の切り詰めも先頭。BLAKE2 は出力長が初期値に入るので別の値。
        byte[] data = RandomBytes(777, 1);
        foreach ((string id, int longBits, int shortBits) in new[] { ("shake128", 8192, 136), ("shake256", 4096, 8), ("blake3", 2048, 40), ("tiger", 192, 128) })
        {
            HashAlgorithmInfo a = HashCatalog.Get(id);
            byte[] full = HashEngine.ComputeBytes(a, data, new HashParameters { OutputBits = longBits });
            byte[] part = HashEngine.ComputeBytes(a, data, new HashParameters { OutputBits = shortBits });
            Assert.Equal(Hex(full[..(shortBits / 8)]), Hex(part));
        }

        HashAlgorithmInfo b2 = HashCatalog.Get("blake2b");
        Assert.NotEqual(
            Hex(HashEngine.ComputeBytes(b2, data)[..32]),
            Hex(HashEngine.ComputeBytes(b2, data, new HashParameters { OutputBits = 256 })));
    }

    public static TheoryData<string> AllIds()
    {
        var data = new TheoryData<string>();
        foreach (string id in TableOrder)
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void DataInArbitraryPiecesGivesTheSameValueAsOneCall(string id)
    {
        HashAlgorithmInfo algorithm = HashCatalog.Get(id);
        HashParameters? parameters = id switch
        {
            "blake2b" => new HashParameters { KeyHex = "0102030405" },
            "blake3" => new HashParameters { KeyHex = Convert.ToHexString(RandomBytes(32, 9)), OutputBits = 600 },
            "shake128" => new HashParameters { OutputBits = 2000 },
            _ => null,
        };

        // ブロック・チャンク (BLAKE3 は 1,024 バイト、TTH の葉も 1,024 バイト) の境目の前後の長さを含める。
        int[] lengths = [0, 1, 15, 16, 17, 55, 56, 63, 64, 65, 111, 112, 127, 128, 129, 135, 136, 137, 1023, 1024, 1025, 2048, 3072, 3073, 5000, 9000];
        foreach (int length in lengths)
        {
            byte[] data = RandomBytes(length, length);
            string expected = Hex(HashEngine.ComputeBytes(algorithm, data, parameters));
            for (int seed = 0; seed < 3; seed++)
            {
                Assert.Equal(expected, Hex(CryptoHashVectorTests.HashInPieces(algorithm, data, seed, parameters, seed == 0 ? 3 : 200)));
            }
        }
    }

    [Fact]
    public void TigerTreeHashMatchesALevelByLevelTree()
    {
        // 葉を左から 2 つずつ組み合わせ、相手のいない節はそのまま上げる (THEX の 4 章)。スタックでの計算と同じになる。
        foreach (int length in new[] { 1024 * 3, 1024 * 5 + 1, 1024 * 6, 1024 * 7 + 1023, 1024 * 16, 1024 * 17 + 5 })
        {
            byte[] data = RandomBytes(length, length);
            var level = new List<byte[]>();
            for (int at = 0; at < length; at += 1024)
            {
                level.Add(Tiger([0x00, .. data.AsSpan(at, Math.Min(1024, length - at))]));
            }

            while (level.Count > 1)
            {
                var next = new List<byte[]>();
                for (int i = 0; i < level.Count; i += 2)
                {
                    next.Add(i + 1 < level.Count ? Tiger([0x01, .. level[i], .. level[i + 1]]) : level[i]);
                }

                level = next;
            }

            Assert.Equal(Hex(level[0]), Hex(HashEngine.ComputeBytes(HashCatalog.Get("tth"), data)));
        }

        static byte[] Tiger(byte[] input)
        {
            var tiger = new TigerHasher();
            tiger.Append(input);
            return tiger.Finish();
        }
    }

    [Fact]
    public void Ed2kCombinesChunkHashesWithMd4()
    {
        // 部品が 2 つのとき (端数あり): 部品ごとの MD4 を連結した列の MD4。新方式と旧方式は同じ値。
        byte[] data = RandomBytes(Ed2kHasher.ChunkSize + 1000, 5);
        byte[] list = [.. Md4Hasher.Compute(data.AsSpan(0, Ed2kHasher.ChunkSize)), .. Md4Hasher.Compute(data.AsSpan(Ed2kHasher.ChunkSize))];
        string expected = Hex(Md4Hasher.Compute(list));
        Assert.Equal(expected, Hex(HashEngine.ComputeBytes(HashCatalog.Get("ed2k"), data)));
        Assert.Equal(expected, Hex(HashEngine.ComputeBytes(HashCatalog.Get("ed2k"), data, new HashParameters { Ed2k = Ed2kMode.Red })));

        // 部品より短ければデータの MD4 そのもの。
        Assert.Equal(Hex(Md4Hasher.Compute("abc"u8)), Hex(HashEngine.ComputeBytes(HashCatalog.Get("ed2k"), "abc"u8)));
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-08")]
    public void Sha3FallsBackToTheManagedImplementation()
    {
        byte[] abc = "abc"u8.ToArray();
        var random = new Random(1908);
        List<byte[]> inputs = [[], abc, .. Enumerable.Range(0, 200).Select(_ => RandomBytes(random.Next(0, 1001), random.Next()))];
        bool platform = SHA3_256.IsSupported;
        HashAlgorithmInfo sha3 = HashCatalog.Get("sha3-256");

        Sha3Provider.ForceManaged = true;
        try
        {
            // 一覧では有効のまま。
            Assert.True(sha3.IsAvailable);
            Assert.False(Sha3Provider.PlatformSupports(256));

            // 代わりの実装が使われる (ログの代わりに使った実装の種類を確認する)。
            IHasher hasher = sha3.CreateHasher();
            Assert.Equal(Sha3ImplementationKind.Managed, Sha3Provider.LastUsed);
            Assert.IsType<KeccakHasher>(hasher);

            // FIPS 202 の例 (NIST CSRC の「Examples with Intermediate Values」)。
            Assert.Equal("A7FFC6F8BF1ED76651C14756A061D662F580FF4DE43B49FA82D80A4B80F8434A", Hex(HashEngine.ComputeBytes(sha3, [])));
            Assert.Equal("3A985DA74FE225B2045C172D6BD390BD855F086E3E9D525B46BFE24511431532", Hex(HashEngine.ComputeBytes(sha3, abc)));

            // .NET 標準の実装 (使える環境のみ) と同じ値。
            foreach (byte[] input in inputs)
            {
                byte[] managed = HashEngine.ComputeBytes(sha3, input);
                if (platform)
                {
                    Assert.Equal(Hex(SHA3_256.HashData(input)), Hex(managed));
                }
            }

            // SHA3-384 / 512 と SHAKE も同じく代わりの実装に切り替わる。
            foreach (byte[] input in inputs.Take(40))
            {
                if (SHA3_384.IsSupported)
                {
                    Assert.Equal(Hex(SHA3_384.HashData(input)), Hex(HashEngine.ComputeBytes(HashCatalog.Get("sha3-384"), input)));
                }

                if (SHA3_512.IsSupported)
                {
                    Assert.Equal(Hex(SHA3_512.HashData(input)), Hex(HashEngine.ComputeBytes(HashCatalog.Get("sha3-512"), input)));
                }

                if (Shake128.IsSupported)
                {
                    Assert.Equal(Hex(Shake128.HashData(input, 300)),
                        Hex(HashEngine.ComputeBytes(HashCatalog.Get("shake128"), input, new HashParameters { OutputBits = 2400 })));
                }

                if (Shake256.IsSupported)
                {
                    Assert.Equal(Hex(Shake256.HashData(input, 64)), Hex(HashEngine.ComputeBytes(HashCatalog.Get("shake256"), input)));
                }

                Assert.Equal(Sha3ImplementationKind.Managed, Sha3Provider.LastUsed);
            }
        }
        finally
        {
            Sha3Provider.ForceManaged = false;
        }

        // 切り替えを戻すと、使える環境では .NET 標準の実装を使う。
        sha3.CreateHasher();
        Assert.Equal(platform ? Sha3ImplementationKind.Platform : Sha3ImplementationKind.Managed, Sha3Provider.LastUsed);
    }
}
