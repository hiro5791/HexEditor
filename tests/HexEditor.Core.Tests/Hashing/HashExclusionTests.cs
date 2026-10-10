using System.Buffers.Binary;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>除外範囲 (ANA-18 の仕様 2)。</summary>
public sealed class HashExclusionTests
{
    /// <summary>
    /// PE のチェックサム欄を除外する。TD-PE-X64 はまだテストデータの一覧にないため、同じ形の PE (このテストのアセンブリ) を使う。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-18-03")]
    public void Excluding_the_pe_checksum_field_by_replacing_or_skipping()
    {
        byte[] pe = File.ReadAllBytes(typeof(HashExclusionTests).Assembly.Location);
        Assert.Equal((byte)'M', pe[0]);
        long checksum = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(0x3C)) + 0x58;
        using var doc = new Document(new MemoryByteSource(pe), Options());
        HashAlgorithmChoice[] algorithms = [new(HashCatalog.Get("sha256")), new(HashCatalog.Get("crc32"))];

        // 1〜2. 「置き換える (0x00)」は、その 4 バイトを 00 にしたバイト列と同じ値。
        HashComputation replaced = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = algorithms,
            Exclusions = [new HashRange(checksum, 4)],
            ExclusionMode = HashExclusionMode.Replace,
            ReplacementValue = 0,
        });
        byte[] zeroed = (byte[])pe.Clone();
        zeroed.AsSpan((int)checksum, 4).Clear();
        foreach (HashResultRow row in replaced.Rows)
        {
            Assert.Equal(HashEngine.ComputeBytes(row.Algorithm, zeroed), row.Value);
        }

        // 3. 「飛ばす」は、その 4 バイトを取り除いたバイト列と同じ値。
        HashComputation skipped = HashEngine.Compute(doc.Current, new HashRequest
        {
            Algorithms = algorithms,
            Exclusions = [new HashRange(checksum, 4)],
            ExclusionMode = HashExclusionMode.Skip,
        });
        byte[] removed = [.. pe.AsSpan(0, (int)checksum), .. pe.AsSpan((int)checksum + 4)];
        foreach (HashResultRow row in skipped.Rows)
        {
            Assert.Equal(HashEngine.ComputeBytes(row.Algorithm, removed), row.Value);
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-18-03")]
    public async Task Progress_reaches_the_total_when_exclusions_are_skipped_or_replaced()
    {
        // 進捗の全体量は「飛ばす」除外範囲の分を含めず、「置き換える」分を含める (除外があっても 100% に届く)。
        using var doc = new Document(new MemoryByteSource(new byte[100_000]), Options());
        foreach (HashExclusionMode mode in new[] { HashExclusionMode.Skip, HashExclusionMode.Replace })
        {
            var request = new HashRequest
            {
                Algorithms = [new(HashCatalog.Get("crc32"))],
                Ranges = [new HashRange(0, 60_000)],
                Exclusions = [new HashRange(10_000, 5_000), new HashRange(50_000, 20_000)],
                ExclusionMode = mode,
                ChunkSize = 4096,
            };
            long total = HashEngine.TotalBytes(doc.Current, request);
            Assert.Equal(mode == HashExclusionMode.Skip ? 60_000 - 5_000 - 10_000 : 60_000, total);
            var center = new HexEditor.Core.Operations.OperationCenter();
            long processed = 0;
            await center.RunAsync("hash", HexEditor.Core.Operations.OperationKind.ReadOnly, doc, total, op =>
            {
                HashEngine.Compute(doc.Current, request, op);
                processed = op.ProcessedBytes;
                return Task.CompletedTask;
            });
            Assert.Equal(total, processed);
        }
    }
}
