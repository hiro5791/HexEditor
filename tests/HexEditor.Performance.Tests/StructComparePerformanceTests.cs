using System.Diagnostics;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>構造を理解した比較の性能テスト (ANA-31 の「巨大ファイル・長時間処理」)。比較の処理を直接呼ぶ。</summary>
[Trait("Category", "Performance")]
public sealed class StructComparePerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    /// <summary>
    /// TC-ANA-31-10: 1 億要素の葉の配列 (<c>u64 a[eof]</c>) を持つ 2 つのデータ (TD-ANA-STRUCT-U64-A と B。計算で作る仮想のデータソース) の比較で、
    /// 違う要素の連続が 2 件の範囲の差分にまとまり、比べたフィールドの数が 1,000,000 未満で、プライベートバイトの最大が 1 GB 以下。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-31-10")]
    public async Task HundredMillionLeafElementsAreComparedInRuns()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TemplateProgram program = TemplateLoader.LoadText(TestDataCatalog.StructU64Hxt, "u64-eof.hxt");
        var left = new U64Source(b: false);
        var right = new U64Source(b: true);
        StructCompareResult? result = null;
        var watch = Stopwatch.StartNew();
        long max = await SampleMemoryAsync(() => result = StructComparer.Run(program, new StructTemplateChoice(program.Path, 0, 0, StructTemplateReason.UserChoice),
            new StructCompareSide(TemplateDataSource.FromByteSource(left), 0, 0, left.Length, "a", null),
            new StructCompareSide(TemplateDataSource.FromByteSource(right), 0, 0, right.Length, "b", null)));
        output.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s, プライベートバイトの最大: {max / MiB} MiB");

        Assert.Equal(StructCompareState.Completed, result!.State);
        Assert.Equal(["a[1000000 … 1000099]", "a[50000000 … 50000009]"], result.Diffs().Select(d => d.DisplayPath));
        Assert.All(result.Diffs(), d => Assert.Equal(StructDiffKind.Changed, d.Kind));
        Assert.True(result.LeftFields < 1_000_000);
        Assert.True(max <= 1024 * MiB, $"プライベートバイトの最大: {max / MiB} MiB");
        result.Dispose();
    }

    private static async Task<long> SampleMemoryAsync(Action work)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long leftover = Math.Max(0, process.PrivateMemorySize64 - StartupPrivateBytes);
        long max = 0;
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                max = Math.Max(max, process.PrivateMemorySize64);
                await Task.Delay(1000);
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
        return Math.Max(max, process.PrivateMemorySize64) - leftover;
    }

    /// <summary>TD-ANA-STRUCT-U64-A / B と同じ内容を計算で作るデータソース (800,000,000 バイト)。</summary>
    private sealed class U64Source(bool b) : ByteSourceBase
    {
        public override string DisplayName => b ? "u64-b" : "u64-a";

        public override string Identity { get; } = "u64:" + Guid.NewGuid().ToString("N");

        public override long Length => TestDataCatalog.StructU64Length;

        public override SourceCapabilities Capabilities => SourceCapabilities.None;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int n = (int)Math.Clamp(Length - offset, 0, buffer.Length);
            int v = (int)(offset % 251);
            for (int i = 0; i < n; i++)
            {
                buffer[i] = (byte)v;
                if (++v == 251)
                {
                    v = 0;
                }
            }

            if (b)
            {
                foreach ((long from, long to) in new[] { (1_000_000L, 1_000_099L), (50_000_000L, 50_000_009L) })
                {
                    long start = Math.Max(offset, from * 8);
                    long end = Math.Min(offset + n, (to + 1) * 8);
                    for (long p = start; p < end; p++)
                    {
                        buffer[(int)(p - offset)] ^= 0xFF;
                    }
                }
            }

            return new ReadResult(n);
        }
    }
}
