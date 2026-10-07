using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-03 生成ピース。</summary>
public sealed class GeneratedPieceTests
{
    [Theory]
    [Trait(TC, "TC-ENG-03-02")]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0x5A })]
    [InlineData(null)]
    public void InsertingInsideFillKeepsPattern(byte[]? pattern)
    {
        pattern ??= Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 1)).ToArray();
        using var doc = new Document(new MemoryByteSource(new byte[TestDataCatalog.MiB]), Options());
        doc.OverwritePattern(0x100, 0x10000, pattern);

        // 手順 1: 塗りつぶしの途中に 1 バイト挿入する。
        doc.Insert(0x1001, [0xFF]);

        // 手順 2: 挿入位置の前後
        byte[] around = Read(doc.Current, 0xFF0, 0x20);
        for (int i = 0; i < around.Length; i++)
        {
            long at = 0xFF0 + i;
            byte expected = at == 0x1001 ? (byte)0xFF : pattern[(at > 0x1001 ? at - 1 - 0x100 : at - 0x100) % pattern.Length];
            Assert.Equal(expected, around[i]);
        }

        // 手順 3: FF を取り除いた列がパターンのくり返しと一致する。
        List<byte> filled = [.. Read(doc.Current, 0x100, 0x10001)];
        filled.RemoveAt(0x1001 - 0x100);
        Assert.Equal(ReferenceModel.Repeat(pattern, 0x10000), filled);
    }

    [Theory]
    [Trait(TC, "TC-ENG-03-02")]
    [InlineData(0)]
    [InlineData(4097)]
    public void PatternLengthOutOfRangeIsRejected(int patternLength)
    {
        using var doc = new Document(new MemoryByteSource(new byte[1024]), Options());
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.OverwritePattern(0, 100, new byte[patternLength]));
        Assert.False(doc.IsModified);
    }

    [Fact]
    [Trait(TC, "TC-ENG-03-03")]
    public void RandomPieceIsReproducible()
    {
        const ulong seed = 0x0123456789ABCDEF;
        const long length = 1L << 33;
        long[] positions = [0, (1L << 31) - 1, 1L << 32, length - 64];

        using var doc = new Document(MemoryByteSource.CreateEmpty("無題 1"), Options());
        doc.InsertRandom(0, length, seed);

        // 手順 2: 5 回読んで同じ
        var first = positions.Select(p => Read(doc.Current, p, 64)).ToArray();
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.Equal(first[i], Read(doc.Current, positions[i], 64));
            }
        }

        // 手順 3: 2^32 に 1 バイト挿入して分割しても内容は変わらない (2^32 以降は 1 バイトずれる)。
        doc.Insert(1L << 32, [0x00]);
        for (int i = 0; i < positions.Length; i++)
        {
            long p = positions[i] >= 1L << 32 ? positions[i] + 1 : positions[i];
            Assert.Equal(first[i], Read(doc.Current, p, 64));
        }

        // 手順 4: 別のドキュメントでも同じ
        using var other = new Document(MemoryByteSource.CreateEmpty("無題 2"), Options());
        other.InsertRandom(0, length, seed);
        for (int i = 0; i < positions.Length; i++)
        {
            Assert.Equal(first[i], Read(other.Current, positions[i], 64));
        }
    }
}
