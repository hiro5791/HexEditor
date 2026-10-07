using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-04 追加バッファと一時ファイルへの退避。</summary>
public sealed class AddBufferTests
{
    private const long SixteenMiB = 16 * TestDataCatalog.MiB;

    private static byte[] RandomBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    [Trait(TC, "TC-ENG-04-02")]
    public void ClosingDocumentDeletesTempFolder()
    {
        var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SEQ-1M")), Options(addBufferMemoryLimit: SixteenMiB));
        string folder = Path.GetDirectoryName(doc.AddBuffer.SpillPath)!;

        doc.Insert(0x1000, RandomBytes(64 * 1024 * 1024, 42));
        Assert.True(File.Exists(doc.AddBuffer.SpillPath));
        Assert.True(doc.AddBuffer.MemoryBytes <= SixteenMiB);

        doc.Dispose();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void SpilledDataReadsBackCorrectly()
    {
        byte[] data = RandomBytes(64 * 1024 * 1024, 7);
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SEQ-1M")), Options(addBufferMemoryLimit: SixteenMiB));
        doc.Insert(0x80000, data);
        Assert.True(doc.AddBuffer.MemoryBytes <= SixteenMiB);
        Assert.True(doc.AddBuffer.SpilledBytes > 0);
        Assert.True(Read(doc.Current, 0x80000, data.Length).AsSpan().SequenceEqual(data));
    }

    [Fact]
    [Trait(TC, "TC-ENG-04-03")]
    public void FailingSpillLeavesDocumentUnchanged()
    {
        // 退避先のフォルダの位置にファイルを置き、フォルダを作れないようにする (書き込みの失敗を再現する)。
        string blocker = Path.Combine(Path.GetTempPath(), "hexeditor-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        var options = Options(addBufferMemoryLimit: SixteenMiB) with { TempDirectory = Path.Combine(blocker, "recovery") };
        try
        {
            using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SEQ-1M")), options);
            byte[] hash = Sha256(doc.Current);
            long length = doc.Length;
            int history = doc.History.Count;

            TempFileWriteException ex = Assert.Throws<TempFileWriteException>(() => doc.Insert(0, RandomBytes(64 * 1024 * 1024, 9)));
            Assert.Contains("一時ファイルに書き込めないため、この編集を適用できませんでした", ex.Message);
            Assert.Contains(doc.AddBuffer.SpillPath, ex.Message);

            Assert.Equal(hash, Sha256(doc.Current));
            Assert.Equal(length, doc.Length);
            Assert.Equal(history, doc.History.Count);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ManySmallAppendsSpillLeastRecentlyUsedChunks()
    {
        string path = Path.Combine(Path.GetTempPath(), "HexEditorTests", Guid.NewGuid().ToString("N"), "add.bin");
        using var buffer = new AddBuffer(path, 4 * AddBuffer.ChunkSize);
        byte[] expected = RandomBytes(20 * AddBuffer.ChunkSize + 123, 11);
        for (int i = 0; i < expected.Length; i += 4099)
        {
            buffer.Append(expected.AsSpan(i, Math.Min(4099, expected.Length - i)));
        }

        Assert.True(buffer.MemoryBytes <= 4 * AddBuffer.ChunkSize);
        byte[] actual = new byte[expected.Length];
        buffer.Read(0, actual);
        Assert.Equal(expected, actual);
    }
}
