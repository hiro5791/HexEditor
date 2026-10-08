using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-18 ハッシュパネルの計算 (1 回の読み込みで同時に計算、除外範囲、マルチ選択、読み込みエラー、キャンセル)。</summary>
public sealed class HashEngineTests
{
    private static HashAlgorithmChoice[] Choices(params string[] ids) => [.. ids.Select(id => new HashAlgorithmChoice(HashCatalog.Get(id)))];

    private static string Hex(HashResultRow row) => Convert.ToHexString(row.Value);

    private static string Crc32(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(HashBytes.FromNumber(CrcHasher.Reference(HashCatalog.Crc32, data), 4));

    [Fact]
    [Trait(TC, "TC-ANA-18-01")]
    public void FourAlgorithmsReadTheDocumentOnce()
    {
        // TD-RANDOM-16M を、読み込んだバイト数を数えるデータソースで開く (内容はテストデータの定義から計算する)。
        const int Length = 16 * 1024 * 1024;
        var source = new FakeByteSource(Length, (o, s) => TestDataCatalog.Expected("TD-RANDOM-16M", o, s), SourceCapabilities.CanResize);
        using var doc = new Document(source, Options());
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("crc32", "md5", "sha1", "sha256") });

        Assert.Equal(Length, source.Reads.Sum(r => (long)r.Length));
        Assert.Equal(Length, result.BytesRead);

        byte[] data = new byte[Length];
        TestDataCatalog.Expected("TD-RANDOM-16M", 0, data);
        Assert.Equal(
            [Crc32(data), Convert.ToHexString(MD5.HashData(data)), Convert.ToHexString(SHA1.HashData(data)), Convert.ToHexString(SHA256.HashData(data))],
            result.Rows.Select(Hex));
        Assert.All(result.Rows, r => Assert.Equal((0L, (long)Length), (r.Start, r.Length)));
    }

    [Fact]
    public void Check9GivesTheCatalogueValues()
    {
        using var doc = new Document(new MemoryByteSource("123456789"u8.ToArray()), Options());
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("crc32", "md5") });
        Assert.Equal(["CBF43926", "25F9E794323B453885F5181F1B624D0B"], result.Rows.Select(Hex));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(5000)]
    public void ChunkBoundariesDoNotChangeTheValues(int chunkSize)
    {
        byte[] data = new byte[100_000];
        new Random(3).NextBytes(data);
        using var doc = new Document(new MemoryByteSource(data), Options());
        HashComputation result = HashEngine.Compute(doc.Current,
            new HashRequest { Algorithms = Choices("crc32", "sha256", "adler32"), ChunkSize = chunkSize, Ranges = [new(10, 90_000)] });
        byte[] part = data[10..90_010];
        Assert.Equal([Crc32(part), Convert.ToHexString(SHA256.HashData(part)), Convert.ToHexString(HashEngine.ComputeBytes(HashCatalog.Get("adler32"), part))],
            result.Rows.Select(Hex));
    }

    [Fact]
    public void ExclusionsAreSkippedOrReplaced()
    {
        // TC-ANA-18-03 (フェーズ 2) の Core 側: 「置き換える (0x00)」はその範囲を 0 にしたデータ、「飛ばす」は取り除いたデータと同じ値。
        byte[] data = new byte[20_000];
        new Random(18).NextBytes(data);
        using var doc = new Document(new MemoryByteSource(data), Options());
        HashRange exclusion = new(0x158, 4);

        HashComputation replaced = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = Choices("sha256", "crc32"),
            Exclusions = [exclusion],
            ExclusionMode = HashExclusionMode.Replace,
            ChunkSize = 4096,
        });
        byte[] zeroed = (byte[])data.Clone();
        zeroed.AsSpan(0x158, 4).Clear();
        Assert.Equal([Convert.ToHexString(SHA256.HashData(zeroed)), Crc32(zeroed)], replaced.Rows.Select(Hex));

        HashComputation skipped = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = Choices("sha256", "crc32"),
            Exclusions = [exclusion, new(50_000, 10)],
            ChunkSize = 4096,
        });
        byte[] removed = [.. data[..0x158], .. data[0x15C..]];
        Assert.Equal([Convert.ToHexString(SHA256.HashData(removed)), Crc32(removed)], skipped.Rows.Select(Hex));

        // 対象範囲の外の除外範囲は警告の対象 (計算では無視する)。
        Assert.Equal([new HashRange(50_000, 10)], HashEngine.ExclusionsOutside([new(0, data.Length)], [exclusion, new(50_000, 10)]));
    }

    [Fact]
    public void MultipleRangesAreConcatenatedOrHashedPerRange()
    {
        // TC-ANA-18-04 (フェーズ 2) の Core 側。範囲は順不同で渡しても、オフセット順に連結する。重なった部分は 1 回だけ数える。
        byte[] data = new byte[0x3000];
        TestDataCatalog.Sequence(0, data);
        using var doc = new Document(new MemoryByteSource(data), Options());
        HashRange[] ranges = [new(0x2000, 1), new(0, 0x100), new(0x1000, 0x10)];

        HashComputation per = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("crc32"), Ranges = ranges, RangeMode = HashRangeMode.PerRange });
        Assert.Equal([Crc32(data[..0x100]), Crc32(data[0x1000..0x1010]), Crc32(data[0x2000..0x2001])], per.Rows.Select(Hex));
        Assert.Equal([0L, 0x1000L, 0x2000L], per.Rows.Select(r => r.Start));

        HashComputation joined = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("crc32"), Ranges = ranges });
        HashResultRow row = Assert.Single(joined.Rows);
        Assert.Equal(Crc32([.. data[..0x100], .. data[0x1000..0x1010], .. data[0x2000..0x2001]]), Hex(row));
        Assert.Equal(0x111, row.Length);

        Assert.Equal([new HashRange(0, 0x20)], HashEngine.Normalize([new(0x10, 0x10), new(0, 0x18)], 0x100));
        Assert.Equal([new HashRange(0xF0, 0x10)], HashEngine.Normalize([new(0xF0, 0x100)], 0x100));
    }

    [Fact]
    public void UnreadableBytesStopTheComputation()
    {
        var source = new FakeByteSource(1 << 20, (_, s) => s.Clear(), SourceCapabilities.HasGaps);
        source.BadRanges.Add(new UnreadableRange(0x9_0000, 0x200, UnreadableReason.IoError));
        using var doc = new Document(source, Options());
        HashReadException e = Assert.Throws<HashReadException>(() =>
            HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("crc32", "sha256"), ChunkSize = 0x10000 }));
        Assert.Equal(0x9_0000, e.Range.Offset);
    }

    [Fact]
    public async Task CancellationDiscardsTheResultQuickly()
    {
        // 遅いデータソース (読み込み 1 回ごとに 20 ms) の 1 GiB を計算し、キャンセルから 1 秒以内に止まる。
        var source = new FakeByteSource(1L << 30, (_, s) => s.Clear(), SourceCapabilities.None) { Delay = TimeSpan.FromMilliseconds(20) };
        using var doc = new Document(source, Options());
        var center = new OperationCenter();
        DocumentSnapshot snapshot = doc.Current;
        var request = new HashRequest { Algorithms = Choices("sha256"), ChunkSize = 1 << 20 };
        Task<HashComputation> task = center.RunAsync("Hash", OperationKind.ReadOnly, doc, HashEngine.TotalBytes(snapshot, request),
            op => Task.FromResult(HashEngine.Compute(snapshot, request, op)));
        await Task.Delay(200);
        LongRunningOperation op = Assert.Single(center.Active);
        Assert.True(op.ProcessedBytes > 0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        op.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(OperationState.Cancelled, op.State);
    }

    [Fact]
    public void EditsDuringTheComputationDoNotChangeTheResult()
    {
        // 開始時のスナップショットで計算する (0.2)。
        byte[] data = new byte[50_000];
        new Random(2).NextBytes(data);
        using var doc = new Document(new MemoryByteSource((byte[])data.Clone()), Options());
        DocumentSnapshot start = doc.Current;
        doc.Overwrite(0, [1, 2, 3]);
        doc.Insert(100, [9]);
        HashComputation result = HashEngine.Compute(start, new HashRequest { Algorithms = Choices("sha256") });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Hex(result.Rows[0]));
    }

    [Fact]
    public void EmptyTargetsGiveTheEmptyDataValue()
    {
        using var doc = new Document(new MemoryByteSource([]), Options());
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = Choices("md5", "crc32") });
        Assert.Equal(["D41D8CD98F00B204E9800998ECF8427E", "00000000"], result.Rows.Select(Hex));
    }
}
