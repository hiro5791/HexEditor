using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-06 ブロックキャッシュと非同期読み込み、ENG-08 メモリ使用量の管理。</summary>
public sealed class BlockCacheTests
{
    [Fact]
    [Trait(TC, "TC-ENG-06-04")]
    public void OnlyUnreadableSectorBecomesUnreadable()
    {
        // ファイルと同じ読み込み単位 (論理セクタ 1、分割は 4 KiB) のデータソース。
        var file = new FakeByteSource(TestDataCatalog.MiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize)
        {
            FailureMode = FailureMode.WholeRequest,
        };
        file.BadRanges.Add(new UnreadableRange(0x11000, 0x1000, UnreadableReason.IoError, 23));
        using (var doc = new Document(file, Options()))
        {
            (byte[] bytes, ByteState[] states) = ReadForDisplayWhenLoaded(doc.Current, 0x10000, 0x10000);
            byte[] expected = new byte[0x10000];
            TestDataCatalog.Sequence(0x10000, expected);
            for (int i = 0; i < states.Length; i++)
            {
                long at = 0x10000 + i;
                bool bad = at is >= 0x11000 and < 0x12000;
                Assert.Equal(bad ? ByteState.Unreadable : ByteState.Valid, states[i]);
                if (!bad)
                {
                    Assert.Equal(expected[i], bytes[i]);
                }
            }

            // ブロック全体の読み込みは最初と再試行の 2 回だけで、その後は 4 KiB 単位。
            var reads = file.Reads.ToArray();
            Assert.Equal(2, reads.Count(r => r is (0x10000, 0x10000)));
            Assert.All(reads.Where(r => r.Length != 0x10000), r => Assert.Equal(0x1000, r.Length));
        }

        // 論理セクタ 512 バイトのデータソース: セクタ 300 だけが読めない。
        var disk = new FakeByteSource(TestDataCatalog.MiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanWrite, sectorSize: 512)
        {
            FailureMode = FailureMode.WholeRequest,
        };
        disk.BadRanges.Add(new UnreadableRange(300 * 512, 512, UnreadableReason.IoError, 23));
        using var diskDoc = new Document(disk, Options());
        long blockStart = 300 * 512 / BlockCache.DefaultBlockSize * BlockCache.DefaultBlockSize;
        (_, ByteState[] diskStates) = ReadForDisplayWhenLoaded(diskDoc.Current, blockStart, BlockCache.DefaultBlockSize);
        for (int i = 0; i < diskStates.Length; i++)
        {
            long at = blockStart + i;
            Assert.Equal(at is >= 300 * 512 and < 301 * 512 ? ByteState.Unreadable : ByteState.Valid, diskStates[i]);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-06-03")]
    public void FullScanDoesNotEvictDisplayedBlocks()
    {
        var source = new CountingFile(TestDataCatalog.Get("TD-MARKERS-1G"));
        using var doc = new Document(source, Options());

        // オフセット 0x20000000 を表示する。
        ReadForDisplayWhenLoaded(doc.Current, 0x20000000, 0x1000);
        IReadOnlyList<long> before = doc.Cache.CachedBlockIndexes();
        Assert.NotEmpty(before);

        // 全体を順次読む (検索と同じ読み方)。
        byte[] buffer = new byte[4 * 1024 * 1024];
        for (long offset = 0; offset < doc.Length; offset += buffer.Length)
        {
            doc.Current.Read(offset, buffer);
        }

        Assert.Superset(before.ToHashSet(), doc.Cache.CachedBlockIndexes().ToHashSet());

        // 再描画でデータソースを読まない。
        int readsBefore = source.ReadCount;
        doc.Current.ReadForDisplay(0x20000000, new byte[0x1000], new ByteState[0x1000]);
        Assert.Equal(readsBefore, source.ReadCount);
    }

    [Theory]
    [Trait(TC, "TC-ENG-06-05")]
    [InlineData(16)]
    [InlineData(256)]
    public void CacheNeverExceedsCapacity(int capacityMiB)
    {
        long capacity = capacityMiB * TestDataCatalog.MiB;
        using var sparse = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options(cacheCapacity: capacity));
        var rng = new Random(605);
        for (int i = 0; i < 2000; i++)
        {
            long offset = rng.NextInt64(sparse.Length - 4096);
            ReadForDisplayWhenLoaded(sparse.Current, offset, 4096);
            Assert.True(sparse.Cache.MemoryBytes <= capacity);
        }

        using var random = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-RANDOM-16M")), Options(cacheCapacity: capacity));
        for (long offset = 0; offset < random.Length; offset += 4096)
        {
            ReadForDisplayWhenLoaded(random.Current, offset, 4096);
            Assert.True(random.Cache.MemoryBytes <= capacity);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-08-03")]
    public void LowMemoryNotificationShrinksCaches()
    {
        var memory = new EngineMemory();
        using var markers = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")), Options());
        using var random = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-RANDOM-16M")), Options());
        memory.Register(markers);
        memory.Register(random);
        for (long offset = 0; offset < 96 * TestDataCatalog.MiB; offset += 64 * 1024)
        {
            ReadForDisplayWhenLoaded(markers.Current, offset, 16);
        }

        for (long offset = 0; offset < random.Length; offset += 64 * 1024)
        {
            ReadForDisplayWhenLoaded(random.Current, offset, 16);
        }

        Assert.True(memory.CacheBytes >= 100 * TestDataCatalog.MiB);

        memory.OnLowMemory();
        Assert.True(memory.CacheBytes <= EngineMemory.LowMemoryCacheBytes);

        // 縮めた後も表示は正しい。
        (byte[] bytes, _) = ReadForDisplayWhenLoaded(random.Current, 0x123456, 64);
        byte[] expected = new byte[64];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0x123456, expected);
        Assert.Equal(expected, bytes);
        (byte[] marker, _) = ReadForDisplayWhenLoaded(markers.Current, 0x100000, TestDataCatalog.MarkerLength);
        Assert.Equal(TestDataCatalog.Marker(0x100000), marker);
    }

    [Fact]
    public void DisplayReadDoesNotBlockOnSlowSource()
    {
        var slow = new FakeByteSource(TestDataCatalog.MiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize)
        {
            Delay = TimeSpan.FromSeconds(2),
        };
        using var doc = new Document(slow, Options());
        var states = new ByteState[256];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        doc.Current.ReadForDisplay(0, new byte[256], states);
        Assert.True(watch.ElapsedMilliseconds < 50, $"{watch.ElapsedMilliseconds} ms");
        Assert.All(states, s => Assert.Equal(ByteState.Loading, s));
    }

    /// <summary>読み込みの回数を数えるファイルのデータソース。</summary>
    private sealed class CountingFile(string path) : ByteSourceBase
    {
        private readonly FileByteSource _inner = FileByteSource.Open(path);
        private int _reads;

        public int ReadCount => Volatile.Read(ref _reads);

        public override string DisplayName => _inner.DisplayName;

        public override string Identity => _inner.Identity;

        public override long Length => _inner.Length;

        public override SourceCapabilities Capabilities => _inner.Capabilities;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            Interlocked.Increment(ref _reads);
            return _inner.Read(offset, buffer);
        }

        public override ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return _inner.ReadAsync(offset, buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing) => _inner.Dispose();
    }
}
