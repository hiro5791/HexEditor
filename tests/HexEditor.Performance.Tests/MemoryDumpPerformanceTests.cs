using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.MemoryDumps;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>ENG-36 メモリダンプを開く速さ (「巨大ファイル・長時間処理」: ヘッダと範囲の一覧だけを読み、64 GB のダンプも 1 秒以内に開く)。</summary>
[Trait("Category", "Performance")]
public sealed class MemoryDumpPerformanceTests
{
    [Fact]
    [Trait("TC", "TC-ENG-36-03")]
    public void A_64_GB_dump_opens_within_one_second_reading_at_most_one_MiB()
    {
        string path = TestDataCatalog.Get("TD-ENG-DUMP-64G");

        // 前提: OS のファイルキャッシュから追い出す (管理者権限が要るため、性能テスト用の計測機だけで行う)。
        if (IsPerfMachine)
        {
            FileCache.Purge();
        }

        // 1. 開いて、先頭の表示 (48 行 × 16 バイト) を読み終えるまでの時間。2. その間のファイルからの読み込み量。
        (long readBefore, _) = IoCounters();
        var watch = Stopwatch.StartNew();
        OpenedMemoryDump opened = MemoryDumpOpener.Open(path);
        using IByteSource source = opened.Source;
        using var doc = new Document(source, Options());
        byte[] screen = new byte[VisibleRows * 16];
        ReadResult first = doc.Current.Read(0, screen);
        watch.Stop();
        (long readAfter, _) = IoCounters();

        Assert.True(first.IsComplete);
        Assert.Equal(TestDataCatalog.PfnMarker(0), screen[..20]);
        Assert.Equal(MemoryDumpKind.CompleteDump, opened.Info.Kind);
        Assert.Equal(64 * GiB, source.Length);
        TimeLimit(watch.Elapsed < TimeSpan.FromSeconds(1), $"開くまで {watch.Elapsed.TotalMilliseconds} ms");
        Assert.True(readAfter - readBefore <= MiB, $"読み込み量 {readAfter - readBefore} バイト");
        Assert.True(((CrashDumpByteSource)source).OpenBytesRead <= 64 * 1024);

        // 1 GiB ごとの目印も読める (全体を走査しない)。
        byte[] marker = new byte[20];
        Assert.True(doc.Current.Read(63 * GiB, marker).IsComplete);
        Assert.Equal(TestDataCatalog.PfnMarker(63 * GiB / 4096), marker);
    }
}
