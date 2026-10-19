using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Reports;
using HexEditor.Core.Printing;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using UglyToad.PdfPig;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>FOR-33 レポートの作成。</summary>
public sealed class ReportTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc);

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-report").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (CaseManager Manager, ForensicCase Case, string AuditPath) NewCase(out ForensicAudit audit)
    {
        var identity = new AuditIdentity("tester", "PC", "1.0");
        audit = new ForensicAudit(identity, new FixedTime(Now));
        var manager = new CaseManager(audit, "tester", new FixedTime(Now)) { AppVersion = "1.0" };
        ForensicCase c = manager.Create(Path.Combine(_dir, "case"), new CaseInfo { Number = "C-0001", Name = "テストケース", Investigator = "tester", Organization = "Org", TimeZoneId = Tokyo.Id });
        return (manager, c, c.AuditPath);
    }

    private ReportInput InputOf(ForensicCase c, IEnumerable<ReportBookmark>? bookmarks = null, params string[] manifests) => new()
    {
        Case = c.Info,
        Evidence = c.Evidence,
        Bookmarks = [.. bookmarks ?? []],
        CarvingManifests = manifests,
        AuditLogPath = c.AuditPath,
        AppName = "HexEditor",
        AppVersion = "1.0",
        NowUtc = Now,
        Registry = new ReportRegistry(),
    };

    private ReportOptions OptionsOf(ForensicCase c, ReportFormat format, ForensicAudit? audit = null) => new()
    {
        OutputFolder = c.ReportsFolder,
        Format = format,
        Zone = Tokyo,
        Audit = audit,
    };

    [Fact]
    [Trait(TC, "TC-FOR-33-01")]
    public void HtmlReportHasEverySectionWithoutExternalReferencesAndShowsUtcAndLocalTimes()
    {
        (CaseManager manager, ForensicCase c, _) = NewCase(out ForensicAudit audit);
        byte[] disk = new byte[2 * 1024 * 1024];
        new Random(1).NextBytes(disk);
        foreach ((string name, string path) in new[] { ("E1", "disk1.dd"), ("E2", "disk2.dd") })
        {
            EvidenceItem item = manager.Register(new EvidenceRegistration { Kind = EvidenceKind.ImageFile, Path = Path.Combine(_dir, path), Description = $"Image <{name}> & co", CapacityBytes = disk.Length, SectorSize = 512, SectorCount = disk.Length / 512 })!;
            using var doc = new Document(new MemoryByteSource(disk));
            manager.RecordHashes(item.Id, EvidenceHasher.Compute(doc.DataSnapshot, EvidenceHasher.DefaultAlgorithms));
        }

        manager.RecordVerification("E001", true);
        ReportBookmark[] bookmarks =
        [
            new("disk1.dd", "bm1", 0x1BE, 0, "partition entry", disk[0x1BE..(0x1BE + 64)]),
            new("disk1.dd", "bm2", 0x200, 1, "second sector with <b>html</b>", disk[0x200..(0x200 + 64)]),
            new("disk1.dd", "bm3", 0x100000, 2048, "one MiB", disk[0x100000..(0x100000 + 64)]),
        ];

        // カービングのマニフェストと、他の領域の節 (パーティションの配置図、ファイルシステムの概要) も入れる。
        string manifest = Path.Combine(_dir, "carving-manifest.csv");
        File.WriteAllLines(manifest, ["Number,Type,Offset,LBA,Length,LengthMethod,MD5,SHA256,OutputPath", "1,JPEG,1048576,2048,415,Structure,aa,bb,jpeg/0000000000100000.jpg"]);
        // パーティション (FOR-06) とファイルシステム (FOR-09) の節は、アプリと同じ部品 (PartitionReportContributor・FileSystemReportContributor) が
        // TD-FOR-GPT の解析と TD-FOR-NTFS のファイルシステムから作る。
        var gpt = new HexEditor.Core.Tests.FileSystems.SparseImageReader(HexEditor.TestData.FileSystemImages.FileSystemImageSet.Image("TD-FOR-GPT"));
        HexEditor.Core.FileSystems.Partitions.PartitionAnalysis partitions = HexEditor.Core.FileSystems.Partitions.PartitionAnalyzer.Analyze(gpt, 512);
        HexEditor.Core.FileSystems.IFileSystem ntfs = HexEditor.Core.Tests.FileSystems.FsTestImages.Open("TD-FOR-NTFS");
        var registry = new ReportRegistry();
        registry.Register(new PartitionReportContributor(() => [("TD-FOR-GPT", partitions)]));
        registry.Register(new FileSystemReportContributor(() => [("TD-FOR-NTFS", 0L, ntfs)], () => []));
        ReportInput input = InputOf(c, bookmarks, manifest) with { Registry = registry };
        ReportResult result = ReportBuilder.Create(input, OptionsOf(c, ReportFormat.Html, audit));

        Assert.Equal(ReportFormat.Html, result.Format);
        string html = File.ReadAllText(result.Path);
        Assert.StartsWith(c.ReportsFolder, result.Path, StringComparison.OrdinalIgnoreCase);

        // 外部の参照がない (src・href・url( が http・https・// を含まない)。
        Assert.DoesNotMatch(new Regex(@"(src|href)\s*=\s*[""']?\s*(https?:|//)", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"url\(\s*[""']?\s*(https?:|//)", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        // 各項目の見出し。
        foreach (string section in new[] { "cover", "evidence", "bookmarks", "carving", "audit", "partitions", "filesystem" })
        {
            Assert.Contains($"id=\"section-{section}\"", html, StringComparison.Ordinal);
        }

        // 証拠 2 件、ブックマーク 3 件と Hex ダンプ、配置図の SVG。
        Assert.Contains("E001", html, StringComparison.Ordinal);
        Assert.Contains("E002", html, StringComparison.Ordinal);
        Assert.Contains("Image &lt;E1&gt; &amp; co", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>html</b>", html, StringComparison.Ordinal);
        foreach (string bm in new[] { "bm1", "bm2", "bm3" })
        {
            Assert.Contains($">{bm}</td>", html, StringComparison.Ordinal);
        }

        Assert.Contains("0x1BE (446)", html, StringComparison.Ordinal);
        Assert.Contains("0x100000 (1048576)", html, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(html, "<pre>").Count);
        Assert.Contains("000001BE  ", html, StringComparison.Ordinal);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.Contains("Passed", html, StringComparison.Ordinal);
        Assert.Contains("jpeg/0000000000100000.jpg", html, StringComparison.Ordinal);

        // 日時: UTC が主、ローカル (タイムゾーン名付き) を併記する。
        Assert.Contains("2026-10-07T03:15:42Z (2026-10-07T12:15:42+09:00, Tokyo Standard Time", html, StringComparison.Ordinal);
        Assert.Contains("2026-10-07T12:15:42+09:00", html, StringComparison.Ordinal);

        // 項目を選ぶと、含めない項目は出さない。該当するデータがない項目も出さない。
        ReportResult small = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Html) with { Sections = new HashSet<string> { ReportSections.Cover, ReportSections.Bookmarks, ReportSections.Carving }, FileName = "small" });
        string smallHtml = File.ReadAllText(small.Path);
        Assert.Contains("section-cover", smallHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("section-evidence", smallHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("section-bookmarks", smallHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("section-carving", smallHtml, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-FOR-33-02")]
    public void PdfReportHasTheCaseNumberAndThePageNumberOnEveryPage()
    {
        if (SystemFontCatalog.Instance.Primary(null) is null)
        {
            return;
        }

        (CaseManager manager, ForensicCase c, _) = NewCase(out ForensicAudit audit);
        manager.Register(new EvidenceRegistration { Kind = EvidenceKind.ImageFile, Path = Path.Combine(_dir, "ntfs-fat32.dd"), Description = "NTFS and FAT32" });
        for (int i = 0; i < 200; i++)
        {
            audit.Record(new AuditRecord(AuditOperations.DocumentOpen) { Path = "TD-SEQ-1M.bin", Message = "Opened for the report test." });
            audit.Record(new AuditRecord(AuditOperations.DocumentClose) { Path = "TD-SEQ-1M.bin" });
        }

        ReportResult result = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Pdf, audit));
        Assert.Equal(ReportFormat.Pdf, result.Format);
        Assert.Null(result.PdfError);
        using PdfDocument pdf = PdfDocument.Open(result.Path);
        Assert.True(pdf.NumberOfPages >= 3, $"{pdf.NumberOfPages} pages");
        Assert.Equal(result.Pages, pdf.NumberOfPages);
        int number = 0;
        foreach (var page in pdf.GetPages())
        {
            number++;
            Assert.Contains("C-0001", page.Text, StringComparison.Ordinal);
            Assert.Matches(new Regex($@"Page\s*{number}\s*/\s*{pdf.NumberOfPages}"), page.Text);
        }

        // 日時は UTC が主、ローカルを併記する。
        string all = string.Join('\n', pdf.GetPages().Select(p => p.Text));
        Assert.Contains("2026-10-07T03:15:42Z", all, StringComparison.Ordinal);
        Assert.Contains("+09:00", all, StringComparison.Ordinal);

        // A4 とレター。
        Assert.Equal(595.28, pdf.GetPage(1).Width, 1);
        ReportResult letter = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Pdf) with { Paper = ReportPaper.Letter, FileName = "letter" });
        using PdfDocument letterPdf = PdfDocument.Open(letter.Path);
        Assert.Equal(612, letterPdf.GetPage(1).Width, 1);
        Assert.Equal(792, letterPdf.GetPage(1).Height, 1);
    }

    [Fact]
    [Trait(TC, "TC-FOR-33-03")]
    public void TheReportSha256IsRecordedInTheAuditLogAndMatchesTheFile()
    {
        (CaseManager manager, ForensicCase c, string auditPath) = NewCase(out ForensicAudit audit);
        manager.Register(new EvidenceRegistration { Kind = EvidenceKind.ImageFile, Path = Path.Combine(_dir, "gpt.dd"), Description = "GPT" });

        ReportResult result = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Html, audit));

        string last = File.ReadAllLines(auditPath)[^1];
        AuditLog.TryParse(last, out AuditEntry? entry);
        Assert.Equal(AuditOperations.ReportCreate, entry!.Operation);
        string recorded = entry.Parameters.Single(p => p.Key == "sha256").Value;
        string computed = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.Path)));
        Assert.Equal(recorded, computed);
        Assert.Equal(result.Sha256, computed);
        Assert.Equal(Path.GetFileName(result.Path), entry.Parameters.Single(p => p.Key == "file").Value);
        Assert.True(AuditVerifier.Verify(auditPath).IsIntact);

        // 開始の行はレポートの監査ログの節に入る。
        Assert.Contains("report.create", File.ReadAllText(result.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void LongTablesAreCutAtOneThousandRowsWithTheFullListAttachedAsCsv()
    {
        (CaseManager _, ForensicCase c, _) = NewCase(out ForensicAudit audit);
        string manifest = Path.Combine(_dir, "carving-manifest.csv");
        File.WriteAllLines(manifest, ["Number,Type,Offset,LBA,Length,LengthMethod,MD5,SHA256,OutputPath", .. Enumerable.Range(1, 2500).Select(i => $"{i},JPEG,{i * 512},{i},100,Structure,aa,bb,jpeg/{i:D16}.jpg"), "#unreadable,0,512,IoError"]);

        ReportResult result = ReportBuilder.Create(InputOf(c, null, manifest), OptionsOf(c, ReportFormat.Html, audit) with { Sections = new HashSet<string> { ReportSections.Carving } });
        string html = File.ReadAllText(result.Path);
        Assert.Equal(1000, Regex.Matches(html, @"<td>\d+</td><td>JPEG</td>").Count);
        Assert.Contains("Showing the first 1000 of 2500 rows", html, StringComparison.Ordinal);
        string attachment = Assert.Single(result.Attachments);
        Assert.Equal(2501, File.ReadAllLines(attachment).Count(l => !l.StartsWith('#')));
        Assert.Contains("2500", html, StringComparison.Ordinal);

        // PDF では上限の行数までにして、全件は CSV か HTML で出す。
        if (SystemFontCatalog.Instance.Primary(null) is not null)
        {
            ReportResult pdf = ReportBuilder.Create(InputOf(c, null, manifest), OptionsOf(c, ReportFormat.Pdf) with { Sections = new HashSet<string> { ReportSections.Carving }, PdfMaxRows = 50, FileName = "cut" });
            Assert.True(pdf.PdfTruncated);
            using PdfDocument doc = PdfDocument.Open(pdf.Path);
            string text = string.Join('\n', doc.GetPages().Select(p => p.Text));
            Assert.DoesNotContain("0000000000002000.jpg", text, StringComparison.Ordinal);
            Assert.Contains("Showing the first 50 rows", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WhenThePdfCannotBeMadeOnlyHtmlIsCreatedWithTheReason()
    {
        (CaseManager _, ForensicCase c, _) = NewCase(out ForensicAudit audit);
        Func<FontFace?> original = ReportPdf.PrimaryFontProvider;
        try
        {
            ReportPdf.PrimaryFontProvider = () => null;
            ReportResult result = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Pdf, audit));
            Assert.Equal(ReportFormat.Html, result.Format);
            Assert.EndsWith(".html", result.Path, StringComparison.Ordinal);
            Assert.Contains("font", result.PdfError, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Path));
            Assert.Empty(Directory.GetFiles(c.ReportsFolder, "*.pdf*"));
        }
        finally
        {
            ReportPdf.PrimaryFontProvider = original;
        }
    }

    [Fact]
    public void ACancelledReportLeavesNoFileAndIsLoggedAsCancelled()
    {
        (CaseManager _, ForensicCase c, string auditPath) = NewCase(out ForensicAudit audit);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Html, audit), cts.Token));
        Assert.Empty(Directory.GetFiles(c.ReportsFolder));
        AuditLog.TryParse(File.ReadAllLines(auditPath)[^1], out AuditEntry? entry);
        Assert.Equal(AuditResult.Cancelled, entry!.Result);
    }

    [Fact]
    public void HeaderTextAndLogoAreEmbeddedWithoutExternalReferences()
    {
        (CaseManager _, ForensicCase c, _) = NewCase(out ForensicAudit _);
        string logo = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(logo, TestDataCatalog.Png16());
        ReportResult result = ReportBuilder.Create(InputOf(c), OptionsOf(c, ReportFormat.Html) with { HeaderText = "Lab Unit 7 <Forensics>", LogoPath = logo });
        string html = File.ReadAllText(result.Path);
        Assert.Contains("Lab Unit 7 &lt;Forensics&gt;", html, StringComparison.Ordinal);
        Assert.Contains("src=\"data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html.Replace("data:image", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        _ = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void DumpLengthIsLimitedToFourKiB()
    {
        (CaseManager _, ForensicCase c, _) = NewCase(out ForensicAudit _);
        byte[] big = new byte[10_000];
        ReportBookmark bm = new("d", "big", 0, 0, string.Empty, big);
        ReportResult result = ReportBuilder.Create(InputOf(c, [bm]), OptionsOf(c, ReportFormat.Html) with { Sections = new HashSet<string> { ReportSections.Bookmarks }, DumpBytes = 100_000 });
        string html = File.ReadAllText(result.Path);
        Assert.Equal(4096 / 16, Regex.Matches(html, @"(^|<pre>)[0-9A-F]{8}  ", RegexOptions.Multiline).Count);
    }
}
