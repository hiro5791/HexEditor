using System.Diagnostics;
using HexEditor.Core.Engine;
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
}
