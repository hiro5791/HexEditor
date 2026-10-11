using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-16 暗号化・圧縮データの検出。</summary>
public sealed class ClassifierTests
{
    [Fact]
    [Trait(TC, "TC-ANA-16-01")]
    public void FourKindsOfDataAreClassified()
    {
        using Document doc = File("TD-ANA-CLASSES");
        ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest());
        Assert.True(r.Completed);
        Assert.Equal([DataClass.Zero, DataClass.Text, DataClass.Compressed, DataClass.Encrypted], r.Regions.Select(x => x.Class));
        IReadOnlyList<long> starts = TestDataCatalog.ClassesStarts;
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(r.Regions[i].Offset, starts[i] - 4096, starts[i] + 4096);
        }

        Assert.Contains(r.Signatures, s => s.Format == "gzip" && s.Offset == starts[2]);
        Assert.All(r.Regions, x => Assert.True(x.MeanEntropy >= 0 && x.MeanEntropy <= 8));
    }

    [Fact]
    [Trait(TC, "TC-ANA-16-02")]
    public void EmbeddedZlibStreamIsListedOnce()
    {
        using Document doc = File("TD-ANA-ZLIB-EMB");
        ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest());
        SignatureHit zlib = Assert.Single(r.Signatures, s => s.Format == "zlib");
        Assert.Equal(0x10000, zlib.Offset);
        Assert.True(zlib.IsCompression);
        Assert.NotNull(zlib.StreamLength);
    }

    [Fact]
    public void BlocksWithUnreadableBytesAreUnreadable()
    {
        var source = Virtual(64 * KiB, (o, s) => TestDataCatalog.Expected("TD-RANDOM-16M", o, s));
        source.BadRanges.Add(new UnreadableRange(8192, 100, UnreadableReason.IoError));
        using var doc = Doc(source);
        ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest());
        Assert.Contains(r.Regions, x => x.Class == DataClass.Unreadable && x.Offset == 8192 && x.Length == 4096);
    }

    [Fact]
    public void ThresholdsCanBeChanged()
    {
        byte[] text = new byte[8192];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = (byte)(i % 10 == 0 ? 0x01 : 'a' + (i % 26));
        }

        using var doc = Doc(text);
        Assert.All(DataClassifier.Classify(doc.Current, new ClassifyRequest()).Regions, x => Assert.Equal(DataClass.Binary, x.Class));
        ClassificationResult loose = DataClassifier.Classify(doc.Current,
            new ClassifyRequest { Thresholds = new ClassThresholds { TextShare = 0.85 } });
        Assert.All(loose.Regions, x => Assert.Equal(DataClass.Text, x.Class));
    }

    [Fact]
    public void ConstantBlocksOtherThanZero()
    {
        byte[] data = new byte[16384];
        data.AsSpan(8192).Fill(0xFF);
        using var doc = Doc(data);
        ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest());
        Assert.Equal([DataClass.Zero, DataClass.Constant], r.Regions.Select(x => x.Class));
        Assert.All(r.Regions, x => Assert.Equal(Confidence.High, x.Confidence));
    }
}
