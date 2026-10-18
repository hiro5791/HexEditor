using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Printing;
using HexEditor.Core.Sources;
using HexEditor.Core.Tools.Files;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>ファイルツールの性能 (TOOL-14)。</summary>
[Trait("Category", "Performance")]
public sealed class ToolsFilesPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string TC = "TC";
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-toolsperf").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// TC-TOOL-14-01: 4 GiB のファイル 3 つを新しいドキュメントとして結合するまで 1 秒以内。長さが 12 GiB で各位置に目印があり、データをコピーしない
    /// (一時フォルダの増加が 1 MB 未満)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-TOOL-14-01")]
    public void JoiningThreeFilesAsADocumentIsFastAndDoesNotCopy()
    {
        string[] files = new string[3];
        for (int i = 0; i < 3; i++)
        {
            files[i] = System.IO.Path.Combine(_dir, $"s{i + 1}.bin");
            using var stream = new FileStream(files[i], FileMode.Create, FileAccess.Write);
            stream.SetLength(4L << 30);
            SparseFiles.TryMakeSparse(stream.SafeFileHandle);
            stream.Write(TestDataCatalog.Marker(i + 1));
        }

        string recovery = System.IO.Path.Combine(_dir, "recovery");
        var watch = Stopwatch.StartNew();
        JoinPlan plan = FileJoiner.Plan(files, new JoinOptions());
        using Document doc = FileJoiner.CreateDocument(plan, new JoinOptions(), "joined", Options() with { TempDirectory = recovery });
        watch.Stop();
        output.WriteLine($"結合 {Ms(watch.Elapsed)}");

        Assert.Equal(12L << 30, doc.Length);
        foreach ((long at, int n) in new[] { (0L, 1), (4L << 30, 2), (8L << 30, 3) })
        {
            Assert.Equal(TestDataCatalog.Marker(n), Read(doc, at, 17));
        }

        long grown = Directory.Exists(recovery) ? Directory.GetFiles(recovery, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        Assert.True(grown < 1024 * 1024, $"一時フォルダが {grown} バイト増えた");
        TimeLimit(watch.Elapsed < TimeSpan.FromSeconds(1), Ms(watch.Elapsed));
    }

    private static SystemFontMetrics? Fonts() =>
        SystemFontCatalog.Instance.Primary(null) is { } face ? new SystemFontMetrics(face, SystemFontCatalog.Instance) : null;

    private static PrintFields Fields(string name) => new(name, null, 0, false, "(modified)", DateTimeOffset.Now, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// TC-TOOL-01-04: 100 GiB のファイルで印刷ダイアログを開く (印刷の計画を作る) と、データを読まずに 1 秒以内にページ数が分かる。
    /// 5 回の中央値で判定する。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-TOOL-01-04")]
    public void PageCountOfA100GibFileIsCalculatedWithoutReading()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        using Document doc = Open("TD-SPARSE-100G");
        var times = new List<TimeSpan>();
        for (int i = 0; i < 5; i++)
        {
            long pages = 0;
            times.Add(Time(() => pages = PrintPlan.Create(new PrintSettings(), doc.Length, fonts).PrintedPages));
            PrintPlan plan = PrintPlan.Create(new PrintSettings(), doc.Length, fonts);
            long rows = (doc.Length + plan.BytesPerRow - 1) / plan.BytesPerRow;
            Assert.Equal((rows + plan.RowsPerPage - 1) / plan.RowsPerPage, pages);
        }

        output.WriteLine(Summary(times));
        TimeLimit(times.Order().ElementAt(2) <= TimeSpan.FromSeconds(1), Summary(times));
    }

    /// <summary>
    /// TC-TOOL-02-02: 100 GiB のファイルで最後のページへ移動して描画するまでが 200 ms 以内 (5 回の中央値)。最後の行に末尾の目印がある。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-TOOL-02-02")]
    public void LastPageOfA100GibFileIsRenderedQuickly()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        using Document doc = Open("TD-SPARSE-100G");
        var data = new SnapshotPrintData(doc.Current, "TD-SPARSE-100G", null, false);
        PrintPlan plan = PrintPlan.Create(new PrintSettings(), doc.Length, fonts);
        var times = new List<TimeSpan>();
        PrintPage? last = null;
        for (int i = 0; i < 5; i++)
        {
            plan.Render(0, data, Fields("x"));
            times.Add(Time(() => last = plan.Render(plan.PrintedPages - 1, data, Fields("x"))));
        }

        output.WriteLine(Summary(times));
        string marker = TestDataCatalog.Marker(doc.Length - TestDataCatalog.MarkerLength).Length > 0
            ? System.Text.Encoding.ASCII.GetString(TestDataCatalog.Marker(doc.Length - TestDataCatalog.MarkerLength))
            : string.Empty;
        string text = string.Concat(last!.Runs.Select(r => r.Text));
        // 目印 (17 バイト) は 16 バイトの行の境目をまたぐので、先頭の "@" を除いた 16 文字 (最後の行にぴったり入る) を探す
        Assert.Contains(marker[1..], text, StringComparison.Ordinal);
        TimeLimit(times.Order().ElementAt(2) <= TimeSpan.FromMilliseconds(200), Summary(times));
    }

    /// <summary>
    /// TC-TOOL-03-02: 10,000 ページの PDF を書き出す間、プライベートバイトの増加が 200 MB を超えない。ページ数は 10,000。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-TOOL-03-02")]
    public async Task TenThousandPagePdfDoesNotGrowTheMemory()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        using Document doc = Open("TD-RANDOM-16M");
        var data = new SnapshotPrintData(doc.Current, "TD-RANDOM-16M", null, false);
        var settings = new PrintSettings { Range = PrintRangeKind.Pages, Pages = "1-10000" };
        PrintPlan plan = PrintPlan.Create(settings, doc.Length, fonts);
        Assert.Equal(10_000, plan.PrintedPages);
        string pdf = System.IO.Path.Combine(_dir, "big.pdf");
        PdfExportResult? result = null;
        long grown = await SampleMemoryAsync(() => result = PdfExporter.WriteFile(pdf, plan, data, Fields("TD-RANDOM-16M"), fonts));
        output.WriteLine($"増加 {grown / (1024 * 1024)} MiB");
        Assert.Equal(10_000, result!.Pages);
        Assert.Contains("/Count 10000", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)[^2048..]) + System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)), StringComparison.Ordinal);
        Assert.True(grown <= 200L * 1024 * 1024, $"{grown / (1024 * 1024)} MiB 増えた");
    }

    private static async Task<long> SampleMemoryAsync(Action work)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long start = process.PrivateMemorySize64;
        long max = start;
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                max = Math.Max(max, process.PrivateMemorySize64);
                await Task.Delay(100);
            }
        });
        try
        {
            await Task.Run(work);
        }
        finally
        {
            stop.Cancel();
            await sampler;
        }

        process.Refresh();
        return Math.Max(max, process.PrivateMemorySize64) - start;
    }
}
