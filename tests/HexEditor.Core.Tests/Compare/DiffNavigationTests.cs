using HexEditor.Core.Compare;
using static HexEditor.Core.Tests.Compare.CompareTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-04 (対応付け)・ANA-05 (次 / 前の差分)・ANA-06 (分布・書き出し) の Core 側。</summary>
public sealed class DiffNavigationTests
{
    private static CompareResult Diff3(CompareMethod method = CompareMethod.InsertDelete) =>
        Diff(DiffMergeTests.Diff3A(), DiffMergeTests.Diff3B(), new CompareOptions { Method = method });

    [Fact]
    public void NextAndPreviousMoveBetweenDiffsAndWrap()
    {
        using CompareResult r = Diff3();
        DiffStore d = r.Diffs;

        Assert.Equal(new DiffStep(0, false), DiffNavigation.Next(d, false, 0));
        // 1 件目を選んだ (カーソルは先頭) 状態から次へ。
        Assert.Equal(new DiffStep(1, false), DiffNavigation.Next(d, false, 0x100, currentIndex: 0));
        Assert.Equal(new DiffStep(2, false), DiffNavigation.Next(d, false, 0x800, currentIndex: 1));
        Assert.Equal(new DiffStep(0, true), DiffNavigation.Next(d, false, 0xC00, currentIndex: 2));

        Assert.Equal(new DiffStep(0, false), DiffNavigation.Previous(d, false, 0x800));
        // 差分の中にあれば、その差分の先頭へ。
        Assert.Equal(new DiffStep(2, false), DiffNavigation.Previous(d, false, 0xC05));
        Assert.Equal(new DiffStep(2, true), DiffNavigation.Previous(d, false, 0x50));

        // 右側のフォーカス: 右の位置で探す。
        Assert.Equal(new DiffStep(1, false), DiffNavigation.Next(d, true, 0x200));
        Assert.Equal(new DiffStep(2, false), DiffNavigation.Next(d, true, 0x810));
    }

    [Fact]
    public void NoDiffsMeansNoStep()
    {
        using CompareResult r = Diff(new byte[16], new byte[16], new CompareOptions());
        Assert.Null(DiffNavigation.Next(r.Diffs, false, 0));
        Assert.Null(DiffNavigation.Previous(r.Diffs, false, 0));
    }

    [Fact]
    [Trait(TC, "TC-ANA-04-04")]
    public void PositionsAfterAnInsertionMapToTheSameContent()
    {
        using CompareResult r = Diff3();
        Assert.Equal(0x820, DiffNavigation.Map(r, fromRight: false, 0x810));
        Assert.Equal(0x810, DiffNavigation.Map(r, fromRight: true, 0x820));
        // 挿入区間の中 (右) は、左の挿入位置。
        Assert.Equal(0x800, DiffNavigation.Map(r, fromRight: true, 0x805));
        // 削除区間の中 (左) は、右の削除位置。
        Assert.Equal(0xC10, DiffNavigation.Map(r, fromRight: false, 0xC08));
        // 削除の後ろ。
        Assert.Equal(0xC10 + 0x10, DiffNavigation.Map(r, fromRight: false, 0xC30));
        // 変更の中は同じ相対位置。
        Assert.Equal(0x102, DiffNavigation.Map(r, fromRight: false, 0x102));
        Assert.Equal(DiffKind.Changed, DiffNavigation.At(r.Diffs, false, 0x102)?.Diff.Kind);
        Assert.Null(DiffNavigation.At(r.Diffs, false, 0x0));
    }

    [Fact]
    public void DifferentStartOffsetsMapLinearly()
    {
        byte[] data = new byte[0x1000];
        using CompareResult r = Diff(new CompareRange(CompareData.FromBytes(data), 0x200, 0xE00), new CompareRange(CompareData.FromBytes(data), 0, 0xE00),
            new CompareOptions());
        Assert.Equal(0x0, DiffNavigation.Map(r, false, 0x200));
        Assert.Equal(0x210, DiffNavigation.Map(r, true, 0x10));
    }

    [Fact]
    [Trait(TC, "TC-ANA-06-05")]
    public void TheDistributionHasTheRatioOfEachBucket()
    {
        // TD-SEQ-1M と TD-ANA-SEQ-1M-MOD (0x10000、0x40000、0x80000、0xC0000 から 16 バイトずつ FF と XOR)。
        byte[] seq = [.. Enumerable.Range(0, (int)MiB).Select(i => (byte)i)];
        byte[] mod = (byte[])seq.Clone();
        foreach (int at in new[] { 0x10000, 0x40000, 0x80000, 0xC0000 })
        {
            for (int k = 0; k < 16; k++)
            {
                mod[at + k] ^= 0xFF;
            }
        }

        using CompareResult r = Diff(seq, mod, new CompareOptions());
        DiffDistribution dist = DiffDistribution.Compute(r);

        Assert.Equal(512, dist.Count);
        Assert.Equal(2048, dist.BucketSize);
        int[] modified = [0x10000 / 2048, 0x40000 / 2048, 0x80000 / 2048, 0xC0000 / 2048];
        for (int b = 0; b < dist.Count; b++)
        {
            Assert.Equal(modified.Contains(b) ? 16.0 / 2048 : 0, dist.Ratios[b], 9);
        }

        // TC-ANA-06-04 の Core 側: 256 番目の区間の最初の差分は 0x80000。
        Assert.Equal(0x80000, r.Diffs[dist.FirstDiffIn(r.Diffs, 256)!.Value].LeftOffset);
        Assert.Null(dist.FirstDiffIn(r.Diffs, 0));
    }

    [Fact]
    public void CsvAndReportContainEveryDiff()
    {
        using CompareResult r = Diff3();
        using var csv = new MemoryStream();
        DiffExport.WriteCsv(r, [0, 1, 2], csv);
        string[] lines = System.Text.Encoding.UTF8.GetString(csv.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("1,changed,256,4,256,4,00 01 02 03,FF FF FF FF", lines[1]);
        Assert.Equal("2,inserted,2048,0,2048,16,,EE EE EE EE EE EE EE EE EE EE EE EE EE EE EE EE", lines[2]);
        Assert.StartsWith("3,deleted,3072,32,3088,0,00 01 02", lines[3]);
        Assert.False(csv.ToArray().AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));

        using var report = new MemoryStream();
        DiffExport.WriteReport(r, ["Summary"], k => k.ToString(), report);
        string text = System.Text.Encoding.UTF8.GetString(report.ToArray());
        Assert.Contains("Summary", text);
        Assert.Contains("3\tDeleted\t0xC00\t0xC10\t32\t0\t", text);

        using var json = new MemoryStream();
        DiffExport.WriteJson(r, [2], json);
        Assert.Contains("\"kind\": \"deleted\"", System.Text.Encoding.UTF8.GetString(json.ToArray()));
    }

    [Fact]
    public void RemovedDiffsAreSkippedAndLaterOffsetsShift()
    {
        var store = new DiffStore(memoryLimit: 3);
        for (int i = 0; i < 10; i++)
        {
            store.Add(new DiffRange(DiffKind.Changed, i * 10, 2, i * 10, 2));
        }

        store.RemoveAt(7, 0, 5);
        store.RemoveAt(2, 0, -1);
        store.RemoveAt(2);
        Assert.Equal(7, store.Count);
        Assert.Equal([0, 10, 40, 50, 60, 80, 90], store.Enumerate().Select(d => d.LeftOffset));
        Assert.Equal([0, 10, 39, 49, 59, 84, 94], store.Enumerate().Select(d => d.RightOffset));
        Assert.Equal(5, store.FirstStartingAtOrAfter(true, 80));
    }

    [Fact]
    public void OptionsAreRememberedAsJson()
    {
        var options = new CompareOptions { Method = CompareMethod.InsertDelete, Window = 1 << 20, MinMatch = 16, MergeGap = 3, Unit = 4 };
        Assert.Equal(options, CompareOptions.FromJson(options.ToJson()));
        Assert.Equal(new CompareOptions(), CompareOptions.FromJson(null));
        Assert.Equal(new CompareOptions().Window, CompareOptions.FromJson(new System.Text.Json.Nodes.JsonObject { ["window"] = 1 }).Window);
        Assert.Equal("minMatch", (new CompareOptions { MinMatch = 0 }).Validate());
    }
}
