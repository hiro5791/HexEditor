using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Performance.Tests;

/// <summary>
/// データエンジンの性能テスト (00-overview 11.1)。100 GB・2 TB のスパースファイルで、操作がデータ量に関係なく
/// 100 ms 以内に終わることを確かめる。時間は 20 回の計測の最大値で判定する。
/// </summary>
[Trait("Category", "Performance")]
public sealed class EnginePerformanceTests
{
    private const string TC = "TC";
    private const long GiB = TestDataCatalog.GiB;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    private static DocumentOptions Options() => new()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditorTests", "recovery"),
    };

    private static Document Open(string id, SourceCapabilities? capabilities = null)
    {
        FileByteSource file = FileByteSource.Open(TestDataCatalog.Get(id));
        IByteSource source = capabilities is null ? file : new RestrictedSource(file, capabilities.Value);
        return new Document(source, Options());
    }

    private static byte[] Read(Document doc, long offset, int length)
    {
        byte[] buffer = new byte[length];
        Assert.True(doc.Current.Read(offset, buffer).IsComplete);
        return buffer;
    }

    /// <summary>操作を 20 回行い、最も遅かった時間を返す。毎回の前に <paramref name="reset"/> で状態を戻す。</summary>
    private static TimeSpan MaxOf20(Action reset, Action action)
    {
        TimeSpan max = TimeSpan.Zero;
        for (int i = 0; i < 20; i++)
        {
            reset();
            var watch = Stopwatch.StartNew();
            action();
            max = watch.Elapsed > max ? watch.Elapsed : max;
        }

        return max;
    }

    [Fact]
    [Trait(TC, "TC-ENG-02-01")]
    public void SingleByteInsertIntoHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");

        // 事前に 10,000 回の 1 バイト上書きでピースを増やす。
        var rng = new Random(201);
        for (int i = 0; i < 10_000; i++)
        {
            doc.Overwrite(rng.NextInt64(doc.Length), [(byte)i]);
        }

        DocumentSnapshot prepared = doc.Current;
        long[] offsets = [0, (1L << 31) - 1, 1L << 32, 50 * GiB, doc.Length];
        foreach (long offset in offsets)
        {
            TimeSpan max = MaxOf20(() => RestoreTo(doc, prepared), () => doc.Insert(offset, [0xAA]));
            Assert.True(max < Limit, $"オフセット {offset:X}: {max.TotalMilliseconds} ms");

            byte[] around = Read(doc, Math.Max(0, offset - 16), (int)Math.Min(33, doc.Length - Math.Max(0, offset - 16)));
            int at = (int)(offset - Math.Max(0, offset - 16));
            Assert.Equal(0xAA, around[at]);
            byte[] before = ReadSnapshot(prepared, offset, around.Length - at - 1);
            Assert.Equal(before, around[(at + 1)..]);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-02-02")]
    public void DeletingAndDuplicatingTenGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        DocumentSnapshot original = doc.Current;

        TimeSpan delete = MaxOf20(() => RestoreTo(doc, original), () => doc.Delete(10 * GiB, 10 * GiB));
        Assert.True(delete < Limit, $"削除: {delete.TotalMilliseconds} ms");
        Assert.Equal(90 * GiB, doc.Length);
        Assert.Equal(ReadSnapshot(original, 20 * GiB, 17), Read(doc, 10 * GiB, 17));

        long from = (1L << 32) - GiB;
        TimeSpan copy = MaxOf20(() => RestoreTo(doc, original), () => doc.InsertCopy(60 * GiB, from, 10 * GiB));
        Assert.True(copy < Limit, $"複製: {copy.TotalMilliseconds} ms");
        Assert.Equal(110 * GiB, doc.Length);
        Assert.Equal(TestDataCatalog.Marker(1L << 32), Read(doc, 60 * GiB + GiB, 17));
    }

    [Fact]
    [Trait(TC, "TC-ENG-03-01")]
    public void FillingOneTerabyteIsFastAndUsesNoMemory()
    {
        using Document doc = Open("TD-SPARSE-2T");
        long memoryBefore = doc.MemoryUsage.TotalInMemory;
        var watch = Stopwatch.StartNew();
        doc.OverwritePattern(0, 1L << 40, [0xDE, 0xAD, 0xBE, 0xEF]);
        Assert.True(watch.Elapsed < Limit, $"{watch.Elapsed.TotalMilliseconds} ms");
        Assert.True(doc.MemoryUsage.TotalInMemory - memoryBefore <= TestDataCatalog.MiB);

        byte[] pattern = [0xDE, 0xAD, 0xBE, 0xEF];
        foreach (long offset in new[] { 0L, (1L << 31) - 2, (1L << 32) + 1, (1L << 40) - 8 })
        {
            byte[] expected = Enumerable.Range(0, 8).Select(i => pattern[(offset + i) % 4]).ToArray();
            Assert.Equal(expected, Read(doc, offset, 8));
        }

        Assert.Equal(TestDataCatalog.Marker(1L << 40), Read(doc, 1L << 40, 17));
    }

    [Fact]
    [Trait(TC, "TC-ENG-05-01")]
    public void UndoAndRedoOfTenGigabyteDeleteAreFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        doc.Delete(1L << 32, 10 * GiB);

        TimeSpan undo = TimeSpan.Zero;
        TimeSpan redo = TimeSpan.Zero;
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            doc.Undo();
            undo = watch.Elapsed > undo ? watch.Elapsed : undo;
            Assert.Equal(TestDataCatalog.Marker(1L << 32), Read(doc, 1L << 32, 17));

            watch.Restart();
            doc.Redo();
            redo = watch.Elapsed > redo ? watch.Elapsed : redo;
            Assert.Equal(TestDataCatalog.Marker((1L << 32) + 10 * GiB), Read(doc, 1L << 32, 17));
        }

        Assert.True(undo < Limit, $"Undo: {undo.TotalMilliseconds} ms");
        Assert.True(redo < Limit, $"Redo: {redo.TotalMilliseconds} ms");
    }

    [Fact]
    [Trait(TC, "TC-ENG-07-02")]
    public void FillingWholeTwoTerabyteFixedLengthSourceIsFast()
    {
        using Document doc = Open("TD-SPARSE-2T", SourceCapabilities.CanWrite);
        Assert.False(doc.CanResize);

        var watch = Stopwatch.StartNew();
        doc.OverwritePattern(0, doc.Length, [0x00]);
        Assert.True(watch.Elapsed < Limit, $"塗りつぶし: {watch.Elapsed.TotalMilliseconds} ms");
        long[] offsets = [1L << 31, 1L << 32, (1L << 40) - 8, doc.Length - 8];
        foreach (long offset in offsets)
        {
            Assert.Equal(new byte[8], Read(doc, offset, 8));
        }

        Assert.Single(doc.Current.EnumerateModifiedRanges());

        watch.Restart();
        doc.Undo();
        Assert.True(watch.Elapsed < Limit, $"Undo: {watch.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(TestDataCatalog.Marker(1L << 32), Read(doc, 1L << 32, 17));
        Assert.Empty(doc.Current.EnumerateModifiedRanges());
    }

    /// <summary>Undo / Redo で <paramref name="target"/> の時点に戻す (履歴の上で同じ時点に戻るだけで、内容はコピーしない)。</summary>
    private static void RestoreTo(Document doc, DocumentSnapshot target)
    {
        while (!ReferenceEquals(doc.Current, target) && doc.History.CanUndo)
        {
            doc.Undo();
        }
    }

    private static byte[] ReadSnapshot(DocumentSnapshot snapshot, long offset, int length)
    {
        byte[] buffer = new byte[length];
        snapshot.Read(offset, buffer);
        return buffer;
    }

    /// <summary>能力のフラグを制限したデータソース (長さ固定のディスクの代わり)。</summary>
    private sealed class RestrictedSource(IByteSource inner, SourceCapabilities capabilities) : ByteSourceBase
    {
        public override string DisplayName => inner.DisplayName;

        public override string Identity => inner.Identity;

        public override long Length => inner.Length;

        public override SourceCapabilities Capabilities => capabilities;

        public override ReadResult Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public override ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(offset, buffer, cancellationToken);

        protected override void Dispose(bool disposing) => inner.Dispose();
    }
}
