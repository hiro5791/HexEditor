using HexEditor.Core.Hashing;
using static HexEditor.Core.Tests.Hashing.HashFixtures;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-19 の仕様 2 (CRC プリセット) と ANA-20 (パラメータからの CRC の計算) を、CRC カタログのフィクスチャで確かめる。</summary>
public sealed class CrcCatalogueTests
{
    /// <summary>仕様 2 の表のプリセット (幅 8・16・24・32・64 の 51 個)。「cksum (長さ付き)」は含めない。</summary>
    private static IReadOnlyList<HashAlgorithmInfo> Presets =>
        [.. HashCatalog.BuiltIn.Where(a => a.Group == HashGroup.Crc && a.Crc is not null)];

    private static CatalogueEntry EntryFor(HashAlgorithmInfo preset) =>
        Catalogue.Single(e => e.Name == preset.Name || preset.Aliases.Contains(e.Name));

    [Fact]
    [Trait(TC, "TC-ANA-19-02")]
    public void ThereAre51PresetsInTheSpecTableWidths()
    {
        Assert.Equal(51, Presets.Count);
        Assert.Equal(
            [(8, 14), (16, 18), (24, 3), (32, 10), (64, 6)],
            Presets.GroupBy(a => a.Bits).Select(g => (g.Key, g.Count())).OrderBy(x => x.Key));
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-02")]
    public void EveryPresetMatchesTheCatalogueDefinitionAndCheckValue()
    {
        foreach (HashAlgorithmInfo preset in Presets)
        {
            CatalogueEntry entry = EntryFor(preset);
            Assert.Equal(entry.Parameters, preset.Crc);
            Assert.Equal(entry.Width, preset.Bits);

            // カタログの名前と別名は、すべてこのプリセットの名前か別名になっている。
            foreach (string alias in entry.Aliases.Append(entry.Name))
            {
                Assert.True(preset.Name == alias || preset.Aliases.Contains(alias), $"{preset.Name}: {alias}");
            }

            string expected = Convert.ToHexString(HashBytes.FromNumber(entry.Check, (entry.Width + 7) / 8));
            Assert.Equal(expected, Convert.ToHexString(Compute(preset, Check9)));
            Assert.Equal(expected, Convert.ToHexString(ComputeInPieces(preset, Check9, 3, maxPiece: 4)));
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-02")]
    public void EveryAliasSelectsTheSamePreset()
    {
        foreach (HashAlgorithmInfo preset in Presets)
        {
            string value = Convert.ToHexString(Compute(preset, Check9));
            foreach (string name in preset.Aliases.Append(preset.Name).Append(preset.Id))
            {
                HashAlgorithmInfo? found = HashCatalog.Find(name);
                Assert.Same(preset, found);
                Assert.Equal(value, Convert.ToHexString(Compute(found!, Check9)));
            }
        }
    }

    [Theory]
    [Trait(TC, "TC-ANA-19-02")]
    [InlineData("CRC-8/SMBUS", "F4")]
    [InlineData("CRC-16/KERMIT", "2189")]
    [InlineData("CRC-24/OPENPGP", "21CF02")]
    [InlineData("CRC-32/BZIP2", "FC891918")]
    [InlineData("CRC-64/XZ", "995DC9BBDF1939FA")]
    [InlineData("CRC-8", "F4")]
    [InlineData("CRC-16/CCITT-FALSE", "29B1")]
    [InlineData("CRC-32/ISO-HDLC", "CBF43926")]
    [InlineData("Castagnoli", "E3069283")]
    [InlineData("CRC-64/GO-ECMA", "995DC9BBDF1939FA")]
    public void ExamplesFromTheTestCase(string name, string check) =>
        Assert.Equal(check, Convert.ToHexString(Compute(HashCatalog.Get(name), Check9)));

    [Theory]
    [Trait(TC, "TC-ANA-19-02")]
    [InlineData("", 4294967295L)]
    [InlineData("123456789", 930766865L)]
    public void CksumWithLengthMatchesPosixCksum(string input, long expected)
    {
        HashAlgorithmInfo cksum = HashCatalog.Get("cksum (長さ付き)");
        Assert.Equal("cksum-length", cksum.Id);
        Assert.Equal(HashGroup.Crc, cksum.Group);
        byte[] data = System.Text.Encoding.ASCII.GetBytes(input);
        Assert.Equal((ulong)expected, HashBytes.ToNumber(Compute(cksum, data)));
        Assert.Equal((ulong)expected, HashBytes.ToNumber(ComputeInPieces(cksum, data, 1, maxPiece: 3)));
    }

    [Fact]
    public void CksumWithLengthAppendsMultiByteLengths()
    {
        // 長さ 256 (2 バイト: 00 01) と 65,536 (3 バイト: 00 00 01) で、データの後に長さのバイトを加えた CRC-32/CKSUM と同じになる。
        foreach (int length in new[] { 255, 256, 65536 })
        {
            byte[] data = new byte[length];
            new Random(length).NextBytes(data);
            byte[] withLength = [.. data, .. LengthBytes(length)];
            Assert.Equal(Compute(HashCatalog.Get("crc32-cksum"), withLength), Compute(HashCatalog.Get("cksum-length"), data));
        }

        static byte[] LengthBytes(long n)
        {
            var bytes = new List<byte>();
            for (; n != 0; n >>= 8)
            {
                bytes.Add((byte)n);
            }

            return [.. bytes];
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-20-04")]
    public void Crc5UsbDefinedAsCustomGivesCheck19()
    {
        var crc5 = new CustomCrcDefinition("My CRC-5/USB", 5, 0x05, 0x1F, true, true, 0x1F);
        Assert.Empty(crc5.Validate([]));
        Assert.Equal(0x19UL, crc5.Check);
        Assert.Equal("19", crc5.FormatValue(crc5.Check!.Value));
        HashAlgorithmInfo algorithm = crc5.ToAlgorithm();
        Assert.Equal("19", Convert.ToHexString(Compute(algorithm, Check9)));
        Assert.Equal("19", Convert.ToHexString(ComputeInPieces(algorithm, Check9, 2, maxPiece: 3)));
    }

    [Fact]
    [Trait(TC, "TC-ANA-20-04")]
    public void EveryCatalogueEntryDefinedAsCustomGivesItsCheckAndResidue()
    {
        // カタログの全項目 (幅 3〜64。プリセットにない幅も含む) をパラメータで定義する。
        Assert.Contains(Catalogue, e => e.Width < 8);
        Assert.Contains(Catalogue, e => e.Width is > 8 and < 16);
        foreach (CatalogueEntry entry in Catalogue)
        {
            var custom = new CustomCrcDefinition("Custom " + entry.Name, entry.Width, entry.Poly, entry.Init, entry.RefIn, entry.RefOut, entry.XorOut);
            Assert.Empty(custom.Validate([]));
            Assert.True(entry.Check == custom.Check, $"{entry.Name}: check");
            Assert.True(entry.Residue == custom.Residue, $"{entry.Name}: residue {custom.Residue:X} != {entry.Residue:X}");
            HashAlgorithmInfo algorithm = custom.ToAlgorithm();
            Assert.Equal(entry.Check, HashBytes.ToNumber(Compute(algorithm, Check9)));
            Assert.Equal(entry.Check, HashBytes.ToNumber(ComputeInPieces(algorithm, Check9, entry.Width, maxPiece: 5)));
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-20-04")]
    public void TableMethodMatchesTheBitwiseReferenceForWidth8AndAbove()
    {
        // 幅 8 以上の項目で、長さ 0〜4,096 の無作為なデータについて、テーブル方式 (スライス 8) をビット単位の基準の実装と比べる。
        var random = new Random(0x2004);
        int[] lengths = [.. Enumerable.Range(0, 17), 31, 63, 64, 65, 255, 1000, 1023, 4095, 4096];
        foreach (CatalogueEntry entry in Catalogue.Where(e => e.Width >= 8))
        {
            CrcParameters p = entry.Parameters;
            foreach (int length in lengths.Append(random.Next(0, 4097)))
            {
                byte[] data = new byte[length];
                random.NextBytes(data);
                var hasher = new CrcHasher(p);
                hasher.Append(data);
                Assert.True(CrcHasher.Reference(p, data) == HashBytes.ToNumber(hasher.Finish()), $"{entry.Name}, {length} bytes");

                // データを分けて渡しても同じ (スライス 8 の端数の処理)。
                var pieces = new CrcHasher(p);
                int half = length / 3;
                pieces.Append(data.AsSpan(0, half));
                pieces.Append(data.AsSpan(half));
                Assert.True(CrcHasher.Reference(p, data) == HashBytes.ToNumber(pieces.Finish()), $"{entry.Name}, {length} bytes in pieces");
            }
        }
    }
}
