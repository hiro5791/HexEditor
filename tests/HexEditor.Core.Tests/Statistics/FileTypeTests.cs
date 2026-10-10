using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-17 ファイル形式の判定。</summary>
public sealed class FileTypeTests
{
    private static readonly FileTypeDetector Detector = new(FileTypeDatabase.BuiltIn);

    private static FileTypeReport Detect(string id, string? extension = null)
    {
        using Document doc = File(id);
        return Detector.Detect(new HeadTailMagicData(doc.Current), extension);
    }

    [Theory]
    [Trait(TC, "TC-ANA-17-01")]
    [InlineData("TD-PNG", "PNG", "image/png")]
    [InlineData("TD-ANA-JPEG", "JPEG", "image/jpeg")]
    [InlineData("TD-ANA-PDF", "PDF", "application/pdf")]
    [InlineData("TD-ZIP", "ZIP", "application/zip")]
    [InlineData("TD-PE-X64", "PE", "application/vnd.microsoft.portable-executable")]
    [InlineData("TD-ELF-X64", "ELF", "application/x-elf")]
    public void RepresentativeFormats(string id, string name, string mime)
    {
        FileTypeCandidate best = Detect(id).Best!;
        Assert.Contains(name, best.Name, StringComparison.Ordinal);
        Assert.Equal(mime, best.Mime);
    }

    [Fact]
    public void DatabaseHasAtLeast300Formats()
    {
        Assert.True(FileTypeDatabase.BuiltIn.Formats.Count >= 300, $"{FileTypeDatabase.BuiltIn.Formats.Count}");
        Assert.Equal(FileTypeDatabase.BuiltIn.Formats.Count, FileTypeDatabase.BuiltIn.Formats.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void ExtensionMismatch()
    {
        FileTypeReport report = Detect("TD-PNG", ".jpg");
        Assert.Contains("PNG", report.Best!.Name, StringComparison.Ordinal);
        Assert.True(report.ExtensionMismatch);
        Assert.False(Detect("TD-PNG", ".png").ExtensionMismatch);
        Assert.False(Detect("TD-PNG", ".bin").ExtensionMismatch);
    }

    [Fact]
    public void UnknownDataAndText()
    {
        using var data = Doc([0x01, 0x02, 0x03, 0x00, 0x99]);
        FileTypeReport unknown = Detector.Detect(new HeadTailMagicData(data.Current));
        Assert.Empty(unknown.Candidates);
        Assert.Null(unknown.Text);

        using Document text = File("TD-TEXT-ASCII");
        Assert.Equal(TextKind.Ascii, Detector.Detect(new HeadTailMagicData(text.Current)).Text);
        using var utf8 = Doc("日本語のテキスト\r\n"u8.ToArray());
        Assert.Equal(TextKind.Utf8, Detector.Detect(new HeadTailMagicData(utf8.Current)).Text);
    }

    /// <summary>TC-ANA-17-03 の Core の部分: 開いたときの自動判定は先頭と末尾の 64 KiB ずつ (最大 128 KiB) しか読まない。</summary>
    [Fact]
    public void AutomaticDetectionReadsAtMost128Kilobytes()
    {
        var source = Virtual(100 * GiB, (o, s) => s.Clear());
        using var doc = Doc(source);
        var data = new HeadTailMagicData(doc.Current);
        Detector.Detect(data);
        Assert.True(data.BytesRead <= 128 * KiB);
        Assert.True(source.Reads.Sum(r => (long)r.Length) <= 128 * KiB);
    }

    [Fact]
    [Trait(TC, "TC-ANA-17-04")]
    public void EmbeddedPngInAnExecutable()
    {
        using Document doc = File("TD-ANA-PE-PNG");
        IReadOnlyList<EmbeddedFormat> found = Detector.FindEmbedded(doc.Current, []);
        Assert.Contains(found, f => f.Offset == 0 && f.Name.Contains("PE", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Offset == 0x2000 && f.Name.Contains("PNG", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-ANA-17-05")]
    public void UserDatabaseInTheMagicFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "magic-" + Guid.NewGuid().ToString("N"), FileTypeDatabase.UserFolderName);
        Directory.CreateDirectory(folder);
        try
        {
            System.IO.File.Copy(TestDataCatalog.Get("TD-ANA-MAGIC-JSON"), Path.Combine(folder, "hexed-test.json"));
            System.IO.File.Copy(TestDataCatalog.Get("TD-ANA-MAGIC-BAD"), Path.Combine(folder, "broken.json"));
            FileTypeDatabase db = FileTypeDatabase.WithUserFolder(folder);
            MagicLoadError error = Assert.Single(db.Errors);
            Assert.Equal("magic/broken.json", error.FileName);
            Assert.Equal(3, error.Line);

            using Document doc = File("TD-ANA-MAGIC-FILE");
            FileTypeCandidate best = new FileTypeDetector(db).Detect(new HeadTailMagicData(doc.Current)).Best!;
            Assert.Equal("HexEditor Test Format", best.Name);
            Assert.Equal("application/x-hexed-test", best.Mime);
            Assert.Equal("hexed-test.json", best.Source);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void ConditionsWildcardsNumbersAndNesting()
    {
        IReadOnlyList<FileFormat> formats = FileTypeDatabase.Parse("""
            { "formats": [
              { "id": "a", "name": "A", "match": { "all": [ { "offset": 0, "bytes": "41 ?? 43" }, { "any": [ { "offset": 3, "type": "u16le", "op": ">=", "value": 5 }, { "not": { "offset": 0, "string": "x" } } ] } ] } },
              { "id": "b", "name": "B", "match": { "offsetFrom": { "offset": 4, "type": "u8", "add": 1 }, "string": "zz" } },
              { "id": "c", "name": "C", "match": { "offset": -2, "bytes": "FF FF" } }
            ] }
            """, "test");
        var detector = new FileTypeDetector(new FileTypeDatabase(formats));
        using var doc = Doc([0x41, 0x00, 0x43, 0x07, 0x05, 0x00, (byte)'z', (byte)'z', 0xFF, 0xFF]);
        string[] names = [.. detector.Detect(new SnapshotMagicData(doc.Current, 0)).Candidates.Select(c => c.Name)];
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.Contains("C", names);
    }
}
