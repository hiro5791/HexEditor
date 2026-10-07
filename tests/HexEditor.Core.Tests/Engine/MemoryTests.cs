using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-08 アプリ全体のメモリの上限、ENG-09 の仕様 12 (処理をキャンセルして閉じる)。</summary>
public sealed class MemoryTests
{
    private static Document Big() =>
        new(new FakeByteSource(10 * TestDataCatalog.GiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize), Options());

    [Fact]
    public void CacheLimitIsSharedByAllDocuments()
    {
        var memory = new EngineMemory();
        var docs = Enumerable.Range(0, 10).Select(_ => Big()).ToList();
        try
        {
            foreach (Document doc in docs)
            {
                memory.Register(doc);
            }

            // 10 個のドキュメントで、キャッシュの上限の合計がアプリ全体の上限 (256 MiB) を超えない (ENG-08 の仕様 1)。
            Assert.True(docs.Sum(d => d.Cache.CapacityBytes) <= EngineMemory.DefaultCacheLimit);
            foreach (Document doc in docs)
            {
                for (long block = 0; block < 1000; block++)
                {
                    DocumentAssert.ReadForDisplayWhenLoaded(doc.Current, block * doc.Cache.BlockSize, 16);
                }
            }

            Assert.True(memory.CacheBytes <= EngineMemory.DefaultCacheLimit, $"{memory.CacheBytes}");

            // 閉じるとほかのドキュメントの割り当てが増える。
            memory.Unregister(docs[0]);
            Assert.Equal(EngineMemory.DefaultCacheLimit / 9, docs[1].Cache.CapacityBytes);
        }
        finally
        {
            docs.ForEach(d => d.Dispose());
        }
    }

    [Fact]
    public void EnforceShrinksCacheThenSpillsAddBuffer()
    {
        var memory = new EngineMemory { Limit = EngineMemory.MinimumLimit };
        using Document doc = Big();
        memory.Register(doc);
        Assert.Equal(MemoryStatus.WithinLimit, memory.Enforce());

        // 追加バッファに 300 MiB (メモリ上) を入れて上限 (256 MiB) を超えさせる。
        doc.AddBuffer.MemoryLimit = 512L * 1024 * 1024;
        byte[] chunk = new byte[AddBuffer.ChunkSize];
        for (int i = 0; i < 300; i++)
        {
            doc.Insert(0, chunk);
        }

        Assert.True(doc.MemoryUsage.TotalInMemory > memory.Limit);
        Assert.Equal(MemoryStatus.Reduced, memory.Enforce());
        Assert.True(doc.MemoryUsage.TotalInMemory <= memory.Limit);
        Assert.True(doc.AddBuffer.SpilledBytes > 0);
        Assert.Equal(new byte[16], Read(doc.Current, 0, 16));
    }

    [Fact]
    public void LowMemoryMonitorShrinksCaches()
    {
        var memory = new EngineMemory();
        using Document doc = Big();
        memory.Register(doc);
        for (long block = 0; block < 600; block++)
        {
            ReadForDisplayWhenLoaded(doc.Current, block * doc.Cache.BlockSize, 16);
        }

        using var monitor = new MemoryMonitor(memory, TimeSpan.FromHours(1));
        int notified = 0;
        monitor.LowMemory += (_, _) => notified++;
        monitor.SimulateLowMemory();
        Assert.Equal(1, notified);
        Assert.True(memory.CacheBytes <= EngineMemory.LowMemoryCacheBytes);
    }

    [Fact]
    public async Task CancelAndWaitStopsOperationsOfDocument()
    {
        var center = new OperationCenter();
        using Document doc = Big();
        var started = new TaskCompletionSource();
        Task running = center.RunAsync("検索", OperationKind.ReadOnly, doc, null, async op =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, op.CancellationToken);
        });
        await started.Task;
        Assert.Single(center.DescribeActive(doc).Operations);
        Assert.False(center.DescribeActive(doc).IncludesSave);

        // 「処理をキャンセルして閉じる」: キャンセルし、止まるまで待ってから閉じる。
        await center.CancelAndWaitAsync(doc).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(center.ActiveFor(doc));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task WaitingForSaveDoesNotCancelIt()
    {
        var center = new OperationCenter();
        using Document doc = Big();
        var release = new TaskCompletionSource();
        Task save = center.RunAsync("保存", OperationKind.WritesExternal, doc, null, _ => release.Task);
        while (center.ActiveFor(doc).Count == 0)
        {
            await Task.Delay(1);
        }

        Assert.True(center.DescribeActive(doc).IncludesSave);
        Task wait = center.CancelAndWaitAsync(doc, exceptSaves: true);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        release.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        await save; // 「保存の完了を待って閉じる」: 保存は完了する
    }
}
