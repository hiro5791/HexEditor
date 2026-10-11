using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Hashing.HashFixtures;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-21 の仕様 5「一致するアルゴリズムを探す」。</summary>
[Collection(HashCatalogCustomCollection.Name)]
public sealed class AlgorithmFinderTests
{
    private static ExpectedHash Expected(string text)
    {
        Assert.True(ExpectedHash.TryParse(text, out ExpectedHash? expected));
        return expected!;
    }

    [Fact]
    [Trait(TC, "TC-ANA-21-04")]
    public void Check9And29B1FindsCrc16Ibm3740AndOnlyRealMatches()
    {
        using var doc = new Document(new MemoryByteSource(Check9), Options());
        AlgorithmSearchResult result = AlgorithmFinder.Find(doc.Current, Expected("0x29B1"));

        Assert.Equal(AlgorithmFinder.Candidates(16).Count, result.Tried);
        Assert.Equal(9, result.BytesRead);
        Assert.Contains(result.Matches, m => m.Choice.Algorithm.Id == "crc16-ibm3740" && m.Match == HashMatch.Match);

        // 結果に含まれるものはすべて、16 bit のアルゴリズムで実際に 29B1 (またはバイト順が逆の B129) になるもの。
        foreach (AlgorithmMatch m in result.Matches)
        {
            byte[] value = Compute(m.Choice.Algorithm, Check9, m.Choice.Parameters);
            Assert.Equal(16, (m.Choice.Algorithm.BitsFor(m.Choice.Parameters) + 7) / 8 * 8);
            Assert.Equal(m.Match == HashMatch.Match ? "29B1" : "B129", Convert.ToHexString(value));
        }

        // 一致しなかった組み合わせは、どちらのバイト順でも 29B1 にならない。
        foreach (HashAlgorithmChoice c in AlgorithmFinder.Candidates(16).Where(c => !result.Matches.Any(m => m.Choice == c)))
        {
            Assert.DoesNotContain(Convert.ToHexString(Compute(c.Algorithm, Check9, c.Parameters)), new[] { "29B1", "B129" });
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-21-04")]
    public void NoMatchReportsTheNumberOfCombinationsTried()
    {
        using var doc = new Document(new MemoryByteSource(Check9), Options());
        AlgorithmSearchResult result = AlgorithmFinder.Find(doc.Current, Expected("0x12345678"));
        Assert.Empty(result.Matches);
        Assert.Equal(AlgorithmFinder.Candidates(32).Count, result.Tried);

        // 32 bit の組み合わせには、全 CRC プリセット、全チェックサムの全オプション、その他の 32 bit のアルゴリズムが含まれる。
        IReadOnlyList<HashAlgorithmChoice> candidates = AlgorithmFinder.Candidates(32);
        Assert.Equal(10 + 1, candidates.Count(c => c.Algorithm.Group == HashGroup.Crc));
        Assert.Equal(3, candidates.Count(c => c.Algorithm.Id == "sum32"));
        Assert.Equal(3 * 2, candidates.Count(c => c.Algorithm.Id == "sum32w"));

        // 「符号あり」は値のビット列が同じなので組み合わせに含めない (同じ結果が 2 回並ばない)。
        Assert.DoesNotContain(candidates, c => c.Parameters.Signed);
        Assert.Equal(candidates.Count, candidates.Select(c => (c.Algorithm.Id, c.Parameters)).Distinct().Count());
        Assert.Equal(2, candidates.Count(c => c.Algorithm.Id == "xor32"));
        Assert.Equal(2, candidates.Count(c => c.Algorithm.Id == "fletcher32"));
        foreach (string id in new[] { "adler32", "fnv1-32", "fnv1a-32", "xxh32", "murmur3-x86-32", "murmur2" })
        {
            Assert.Single(candidates, c => c.Algorithm.Id == id);
        }

        Assert.All(candidates, c => Assert.Equal(32, c.Algorithm.BitsFor(c.Parameters)));
        Assert.DoesNotContain(candidates, c => c.Algorithm.Id is "crc16-arc" or "sha256" or "xxh64");
    }

    [Fact]
    public void CandidatesIncludeCustomCrcsAndVariableOutputAlgorithms()
    {
        int before = AlgorithmFinder.Candidates(16).Count;
        var store = new CustomCrcStore();
        store.Add(new CustomCrcDefinition("Finder16", 16, 0x1021, 0xFFFF));
        try
        {
            store.ApplyToCatalog();
            Assert.Equal(before + 1, AlgorithmFinder.Candidates(16).Count);
            using var doc = new Document(new MemoryByteSource(Check9), Options());
            AlgorithmSearchResult result = AlgorithmFinder.Find(doc.Current, Expected("29B1"));
            Assert.Contains(result.Matches, m => m.Choice.Algorithm.Id == "custom:Finder16");
        }
        finally
        {
            HashCatalog.SetCustom([]);
        }

        // 出力長を選べるもの (SipHash の 128 bit 版) は期待値の長さで試す。
        Assert.Contains(AlgorithmFinder.Candidates(128), c => c.Algorithm.Id == "siphash-2-4" && c.Parameters.OutputBits == 128);
        Assert.Contains(AlgorithmFinder.Candidates(64), c => c.Algorithm.Id == "siphash-2-4" && c.Parameters.OutputBits == 0);
        Assert.Empty(AlgorithmFinder.Candidates(0));
    }

    [Fact]
    public void ReversedByteOrderIsReportedAndTheTargetRangeIsUsed()
    {
        // 対象範囲 (先頭の 3 バイトの後の 123456789) の CRC-32 をバイト順を逆にして渡す。
        byte[] data = [0xAA, 0xBB, 0xCC, .. Check9, 0xDD];
        using var doc = new Document(new MemoryByteSource(data), Options());
        var target = new HashRequest { Algorithms = [], Ranges = [new HashRange(3, 9)] };
        AlgorithmSearchResult result = AlgorithmFinder.Find(doc.Current, Expected("2639F4CB"), target);
        AlgorithmMatch crc32 = Assert.Single(result.Matches, m => m.Choice.Algorithm.Id == "crc32");
        Assert.Equal(HashMatch.MatchReversed, crc32.Match);
        Assert.Equal(9, result.BytesRead);
    }

    [Fact]
    public void CancellationStopsTheSearch()
    {
        using var doc = new Document(new MemoryByteSource(new byte[100_000]), Options());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            AlgorithmFinder.Find(doc.Current, Expected("00000000"), cancellationToken: new CancellationToken(canceled: true)));
    }

    [Fact]
    [Trait(TC, "TC-ANA-21-04")]
    public void CustomCrcWhoseWidthIsNotAMultipleOfEightIsACandidate()
    {
        // 幅 5 の CRC の値は 1 バイト (19)。期待値 0x19 (8 bit) の候補に入り、一致する。
        HashAlgorithmInfo crc5 = new CustomCrcDefinition("Finder CRC-5/USB", 5, 0x05, 0x1F, true, true, 0x1F).ToAlgorithm();
        HashCatalog.SetCustom([crc5]);
        try
        {
            Assert.Contains(AlgorithmFinder.Candidates(8), c => c.Algorithm == crc5);
            using var doc = new Document(new MemoryByteSource(Check9), Options());
            AlgorithmSearchResult result = AlgorithmFinder.Find(doc.Current, Expected("0x19"));
            Assert.Contains(result.Matches, m => m.Choice.Algorithm == crc5 && m.Match == HashMatch.Match);
        }
        finally
        {
            HashCatalog.SetCustom([]);
        }
    }
}
