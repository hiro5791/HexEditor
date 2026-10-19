using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>FOR-21 の受け入れ基準 3 (1 TB の E01 を開いて 1 秒以内に先頭を表示)。</summary>
[Trait("Category", "Performance")]
public sealed class ImagePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("TC", "TC-FOR-21-03")]
    public void A_one_terabyte_e01_opens_and_shows_its_head_within_a_second()
    {
        string path = TestDataCatalog.Get("TD-FOR-E01-1T");
        if (IsPerfMachine)
        {
            // OS のファイルキャッシュからテスト用データを追い出す (計測機だけ。管理者権限が要る)。
            FileCache.Purge();
        }

        var watch = Stopwatch.StartNew();
        using var doc = new Document(ImageOpener.Open(path), Options());
        EditorState editor = Editor(doc);
        byte[] head = ReadVisible(editor);
        watch.Stop();
        output.Report($"open + first screen: {Ms(watch.Elapsed)}");
        TimeLimit(watch.Elapsed <= TimeSpan.FromSeconds(1), $"opening the 1 TiB E01 took {Ms(watch.Elapsed)} (limit 1 s)");

        Assert.Equal(TiB, doc.Length);
        Assert.Equal(TestDataCatalog.Marker(0), head[..TestDataCatalog.MarkerLength]);
        foreach (long at in new[] { 1L << 31, 1L << 32 })
        {
            Assert.Equal(TestDataCatalog.Marker(at), Read(doc, at, TestDataCatalog.MarkerLength));
        }

        Assert.Equal(new byte[4096], Read(doc, (1L << 40) - 4096, 4096));
    }
}
