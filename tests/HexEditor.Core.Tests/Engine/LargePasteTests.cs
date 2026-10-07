using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// ENG-04 の受け入れ基準 1・2: 2 GiB の貼り付けでエンジンのメモリ使用量が上限を超えず、退避したデータを表示・検索・保存できる。
/// 一時ファイル (2 GiB) と保存したファイル (2 GiB) を作るため、毎晩のテストで行い、終わったら消す。
/// </summary>
[Trait("Category", "Nightly")]
public sealed class LargePasteTests : IDisposable
{
    private const long MiB = TestDataCatalog.MiB;
    private const long GiB = TestDataCatalog.GiB;
    private const ulong Seed = 0x2_6170_7374_65UL;
    private const int Chunk = 64 * (int)MiB;

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-paste2g").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    [Trait(TC, "TC-ENG-04-01")]
    public void PastingTwoGigabytesStaysUnderTheMemoryLimit()
    {
        const long At = 0x80000;
        const long Length = 2 * GiB;

        // 前提: 既定の設定 (エンジンのメモリ上限 1 GiB、追加データをメモリに置く上限 256 MiB) で TD-SEQ-1M を開く。
        var options = new DocumentOptions { TempDirectory = Path.Combine(_dir, "recovery") };
        Assert.Equal(256 * MiB, options.AddBufferMemoryLimit);
        var memory = new EngineMemory();
        Assert.Equal(GiB, memory.Limit);
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        using var doc = new Document(FileByteSource.Open(seq), options);
        memory.Register(doc);
        using var monitor = new MemoryMonitor(memory);

        // 手順 1・2: 固定の種の乱数 2 GiB を 0x80000 に挿入貼り付けし (1 回の貼り付けとして 1 つの Undo 単位)、その間 100 ms ごとに
        // エンジンのメモリ使用量を記録する。
        long max = 0;
        void Sample() => max = Math.Max(max, memory.TotalUsage.TotalInMemory);
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Chunk];
        int entriesBefore = doc.History.CurrentIndex;
        using (var sampler = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100)))
        using (doc.BeginGroup("貼り付け"))
        {
            for (long done = 0; done < Length; done += Chunk)
            {
                TestDataCatalog.Random(Seed, done, buffer);
                sourceHash.AppendData(buffer);
                doc.Insert(At + done, buffer);
                Sample();
            }
        }

        Sample();
        byte[] expectedHash = sourceHash.GetHashAndReset();
        Assert.Equal(entriesBefore + 1, doc.History.CurrentIndex);
        Assert.True(max <= GiB, $"エンジンのメモリ使用量の最大: {max / MiB} MiB");
        string addBin = Path.Combine(options.TempDirectory, doc.Id.ToString("N"), "add.bin");
        Assert.True(File.Exists(addBin), addBin);
        Assert.True(doc.MemoryUsage.AddBufferSpilled > GiB);

        // 手順 3: 貼り付けた範囲の SHA-256。
        using (var rangeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (long offset = 0; offset < Length; offset += Chunk)
            {
                Assert.True(doc.Current.Read(At + offset, buffer).IsComplete);
                rangeHash.AppendData(buffer);
            }

            Assert.Equal(expectedHash, rangeHash.GetHashAndReset());
        }

        // 手順 4: 貼り付けたデータの末尾 16 バイトを検索する。
        byte[] tail = new byte[16];
        TestDataCatalog.Random(Seed, Length - 16, tail);
        SearchResults found = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex(string.Join(" ", tail.Select(b => b.ToString("X2")))));
        Assert.Equal([At + Length - 16], found.Matches.Select(m => m.Offset));

        // 手順 5: 名前を付けて保存し、基準モデル (TD-SEQ-1M の前半 + 乱数 + 後半) の SHA-256 と比べる。
        string saved = Path.Combine(_dir, "saved.bin");
        using (FileByteSource file = DocumentSaver.Save(doc.Current, saved))
        {
            Assert.Equal(MiB + Length, file.Length);
        }

        using var model = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] seqBytes = File.ReadAllBytes(seq);
        model.AppendData(seqBytes, 0, (int)At);
        for (long done = 0; done < Length; done += Chunk)
        {
            TestDataCatalog.Random(Seed, done, buffer);
            model.AppendData(buffer);
        }

        model.AppendData(seqBytes, (int)At, (int)(MiB - At));
        using FileStream stream = File.OpenRead(saved);
        Assert.Equal(model.GetHashAndReset(), SHA256.HashData(stream));
        memory.Unregister(doc);
    }
}
