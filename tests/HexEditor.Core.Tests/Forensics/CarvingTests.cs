using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>全部 00 の巨大なデータ (100 GB など)。メモリには何も持たない。</summary>
internal sealed class ZeroData(long length) : ICarveData
{
    public long Length { get; } = length;

    public ReadResult Read(long offset, Span<byte> destination)
    {
        if (offset >= Length)
        {
            return new ReadResult(0);
        }

        int count = (int)Math.Min(destination.Length, Length - offset);
        destination[..count].Clear();
        return new ReadResult(count);
    }
}

/// <summary>範囲を読めないものにするラッパー。</summary>
internal sealed class FaultyData(ICarveData inner, long badOffset, long badLength) : ICarveData
{
    public long Length => inner.Length;

    public ReadResult Read(long offset, Span<byte> destination)
    {
        ReadResult r = inner.Read(offset, destination);
        long from = Math.Max(offset, badOffset), to = Math.Min(offset + r.BytesReturned, badOffset + badLength);
        if (to > from)
        {
            destination.Slice((int)(from - offset), (int)(to - from)).Clear();
            return new ReadResult(r.BytesReturned, [new UnreadableRange(from, to - from, UnreadableReason.IoError, 23)]);
        }

        return r;
    }
}

internal sealed class FakeAllocation(IReadOnlyList<SearchRange> unallocated, IReadOnlyList<SearchRange> slack) : IAllocationMap
{
    public IReadOnlyList<SearchRange> Unallocated { get; } = unallocated;

    public IReadOnlyList<SearchRange> Slack { get; } = slack;
}

/// <summary>FOR-19 カービングの実行。</summary>
public sealed class CarvingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-carve").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CarvingOptions Options(IReadOnlyList<CarvingSignature> signatures, long length, string? output = null, bool export = true) => new()
    {
        Signatures = signatures,
        Ranges = [new SearchRange(0, length)],
        OutputFolder = output,
        Export = export,
        ChunkSize = 64 * 1024,
        Parallelism = 2,
        FreeSpace = _ => long.MaxValue,
    };

    private static CarvingSignature Sig(string name, string header, long max = 1024, long headerOffset = 0, bool aligned = true, string? footer = null, long? include = null,
        SizeFieldRule? size = null, string? structure = null, long min = 0) =>
        new() { Name = name, Extension = name.ToLowerInvariant(), Headers = [header], MaxSize = max, HeaderOffset = headerOffset, SectorAligned = aligned, Footer = footer, FooterInclude = include, SizeField = size, Structure = structure, MinSize = min };

    private static List<string[]> ReadManifest(string path) =>
        [.. File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0 && l[0] != (char)35).Select(l => l.Split((char)44))];

    [Fact]
    [Trait(TC, "TC-FOR-19-01")]
    public async Task FourEmbeddedFilesAreFoundExportedAndMatchTheOriginalHashes()
    {
        byte[] image = TestDataCatalog.ForCarve();
        string folder = Path.Combine(_dir, "exports", "carved");
        string auditPath = Path.Combine(_dir, "audit.log");
        var identity = new AuditIdentity("tester", "PC", "1");
        var audit = new ForensicAudit(identity);
        audit.AttachCase(AuditLog.Open(auditPath, identity));

        CarvingResult result = await CarvingEngine.RunAsync(new MemoryCarveData(image), Options(BuiltInSignatures.All, image.Length, folder) with { Audit = audit });

        Assert.False(result.Cancelled);
        Assert.Equal(4, result.Files.Count);
        string[] expectedNames = ["jpeg/0000000000100000.jpg", "png/0000000000200000.png", "pdf/0000000000300000.pdf", "zip/0000000000400000.zip"];
        byte[][] originals = [TestDataCatalog.ForJpeg(), TestDataCatalog.Png16(), TestDataCatalog.ForPdf(), TestDataCatalog.ZipSample()];
        List<string[]> rows = ReadManifest(result.ManifestPath!);
        Assert.Equal(CarvingEngine.ManifestHeader, File.ReadAllLines(result.ManifestPath!)[0].Split(','));
        Assert.Equal(4, rows.Count);
        for (int i = 0; i < 4; i++)
        {
            CarvedFile file = result.Files[i];
            Assert.Equal(expectedNames[i], file.RelativePath);
            Assert.Equal(TestDataCatalog.ForCarveOffsets[i], file.Offset);
            Assert.Equal(originals[i].Length, file.Length);
            Assert.Equal(LengthMethod.Structure, file.Method);
            Assert.True(file.Exported);
            string path = Path.Combine(folder, expectedNames[i].Replace('/', Path.DirectorySeparatorChar));
            byte[] written = File.ReadAllBytes(path);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originals[i])), Convert.ToHexStringLower(SHA256.HashData(written)));
            Assert.Equal((i + 1).ToString(), rows[i][0]);
            Assert.Equal(file.Offset.ToString(), rows[i][2]);
            Assert.Equal((file.Offset / 512).ToString(), rows[i][3]);
            Assert.Equal(file.Length.ToString(), rows[i][4]);
            Assert.Equal("Structure", rows[i][5]);
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(originals[i])), rows[i][6]);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originals[i])), rows[i][7]);
            Assert.Equal(expectedNames[i], rows[i][8]);
        }

        // 監査ログに開始・終了・件数・マニフェストのハッシュが記録される。
        string[] lines = File.ReadAllLines(auditPath);
        Assert.Equal(2, lines.Length);
        AuditLog.TryParse(lines[1], out AuditEntry? end);
        Assert.Equal(AuditOperations.Carve, end!.Operation);
        Assert.Equal("4", end.Parameters.Single(p => p.Key == "files").Value);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.ManifestPath!))), end.Parameters.Single(p => p.Key == "manifestSha256").Value);
        Assert.Equal(result.ManifestSha256, end.Parameters.Single(p => p.Key == "manifestSha256").Value);
        Assert.Equal(1, result.CountsByType["JPEG"]);
    }

    [Fact]
    [Trait(TC, "TC-FOR-19-02")]
    public async Task UnallocatedOnlyScopeFindsOnlyTheJpegInTheUnallocatedArea()
    {
        // container.bin (0x4000 から 8 KiB: 00 × 4 KiB + JPEG + 00 × 4 KiB より小さい配置) と、削除済みの tmp.jpg を未割り当て領域に残した、64 KiB のボリューム。
        byte[] jpeg = TestDataCatalog.ForJpeg();
        byte[] volume = new byte[256 * 1024];
        jpeg.CopyTo(volume, 0x5000);
        jpeg.CopyTo(volume, 0x20000);
        var allocation = new FakeAllocation([new SearchRange(0, 0x4000), new SearchRange(0x6000, volume.Length - 0x6000)], []);
        CarvingSignature jpegSignature = BuiltInSignatures.All.Single(s => s.Name == "JPEG");

        IReadOnlyList<SearchRange> unallocated = CarveScope.Resolve(CarveScopeKind.Unallocated, volume.Length, [], allocation);
        CarvingResult only = await CarvingEngine.RunAsync(new MemoryCarveData(volume), new CarvingOptions { Signatures = [jpegSignature], Ranges = unallocated, Export = false, SectorSize = 512 });
        Assert.Single(only.Files.Enumerate());
        Assert.Equal(0x20000, only.Files[0].Offset);

        IReadOnlyList<SearchRange> whole = CarveScope.Resolve(CarveScopeKind.WholeDocument, volume.Length, [], allocation);
        CarvingResult all = await CarvingEngine.RunAsync(new MemoryCarveData(volume), new CarvingOptions { Signatures = [jpegSignature], Ranges = whole, Export = false, SectorSize = 512 });
        Assert.Equal([0x5000L, 0x20000L], all.Files.Enumerate().Select(f => f.Offset));

        // スラックのみ・選択範囲。
        Assert.Throws<InvalidOperationException>(() => CarveScope.Resolve(CarveScopeKind.Slack, 10, [], null));
        Assert.Equal([new SearchRange(0x5000, 0x100)], CarveScope.Resolve(CarveScopeKind.Selection, volume.Length, [new SearchRange(0x5000, 0x100)], null));
    }

    [Fact]
    [Trait(TC, "TC-FOR-19-03")]
    public async Task CancellingAHugeSourceKeepsTheResultsAndTheManifestWithBoundedMemory()
    {
        const long Length = 100L * 1024 * 1024 * 1024;
        string folder = Path.Combine(_dir, "out");
        using var cts = new CancellationTokenSource();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var progress = new Progress<CarvingProgress>(p =>
        {
            if (p.BytesScanned >= 256L * 1024 * 1024)
            {
                cts.Cancel();
            }
        });
        CarvingOptions options = Options(BuiltInSignatures.All, Length, folder) with { ChunkSize = 4 * 1024 * 1024, Progress = progress };
        CarvingResult result = await CarvingEngine.RunAsync(new ZeroData(Length), options, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.BytesScanned >= 256L * 1024 * 1024);
        Assert.True(result.BytesScanned < Length / 10);
        Assert.Equal(Length, result.TotalBytes);
        string manifest = Path.Combine(folder, CarvingEngine.ManifestFileName);
        Assert.True(File.Exists(manifest));
        Assert.Equal(CarvingEngine.ManifestHeader, File.ReadAllLines(manifest)[0].Split(','));
        Assert.Equal(result.Files.Count, ReadManifest(manifest).Count);

        // メモリはファイルの大きさに比例しない (チャンク × 並列数 程度)。
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore < 600L * 1024 * 1024);
    }

    [Fact]
    [Trait(TC, "TC-FOR-19-04")]
    public void AnOutputFolderOnTheSameDiskAsTheInvestigatedDeviceIsRefused()
    {
        var catalog = new DeviceCatalog
        {
            Disks = [new DiskInfo { Number = 3 }],
            Volumes =
            [
                new VolumeDeviceInfo { Path = @"\\.\X:", DriveLetter = "X", DiskNumber = 3 },
                new VolumeDeviceInfo { Path = @"\\.\Y:", DriveLetter = "Y", DiskNumber = 3 },
            ],
        };
        OutputCheck check = OutputGuard.Check(@"Y:\carved", [DevicePath.PhysicalDrive(3)], 0, 0, new CatalogDiskLocator(() => catalog));
        Assert.Equal(OutputProblem.SameDisk, check.Problem);
        Assert.False(Directory.Exists(@"Y:\carved"));
    }

    // ---- 走査の細部 ----

    [Fact]
    public async Task AHeaderThatCrossesAChunkBoundaryIsFoundOnceForAlignedAndUnalignedDefinitions()
    {
        byte[] data = new byte[300 * 1024];
        byte[] header = [0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04];
        // チャンク (64 KiB) の境界をまたぐ位置に置く。
        foreach (long at in new long[] { 64 * 1024 - 3, 128 * 1024 - 1, 196 * 1024 + 5 })
        {
            header.CopyTo(data, at);
        }

        // 同じ位置でも、セクタ境界のみの定義は 512 の倍数にだけ一致する。
        header.CopyTo(data, 256 * 1024);
        CarvingResult unaligned = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([Sig("Cls", "CA FE BA BE 01 02 03 04", 100, aligned: false)], data.Length));
        Assert.Equal([64 * 1024L - 3, 128 * 1024L - 1, 196 * 1024L + 5, 256 * 1024L], unaligned.Files.Enumerate().Select(f => f.Offset));
        CarvingResult aligned = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([Sig("Cls", "CA FE BA BE 01 02 03 04", 100)], data.Length));
        Assert.Equal([256 * 1024L], aligned.Files.Enumerate().Select(f => f.Offset));
    }

    [Fact]
    public async Task LengthIsDecidedByStructureThenSizeFieldThenFooterThenMaxSize()
    {
        byte[] data = new byte[8192];
        // フッター付き: ヘッダー AA BB、フッター EE FF の位置は 100。フッターの先頭から 2 バイト (フッター自身) を含める。
        data[0] = 0xAA;
        data[1] = 0xBB;
        data[100] = 0xEE;
        data[101] = 0xFF;
        // サイズの取得: 位置 1024 から。ヘッダー AC BD、オフセット 4 の 16 bit リトルエンディアンの値 + 8。
        data[1024] = 0xAC;
        data[1025] = 0xBD;
        data[1028] = 0x0A;
        data[1029] = 0x00;
        // フッターなし・サイズなし: 最大サイズで切る。
        data[2048] = 0xAD;
        data[2049] = 0xBE;
        // 範囲の終わりに近い: 最大サイズより範囲の終わりで切る。
        data[7680] = 0xAD;
        data[7681] = 0xBE;

        CarvingSignature footer = Sig("Foot", "AA BB", 4096, footer: "EE FF");
        CarvingSignature includeMore = Sig("Foot2", "AA BB", 4096, footer: "EE FF", include: 6);
        CarvingSignature size = Sig("Size", "AC BD", 4096, size: new SizeFieldRule { Offset = 4, Bits = 16, Add = 8 });
        CarvingSignature max = Sig("Max", "AD BE", 600);

        async Task<IReadOnlyList<CarvedFile>> Run(params CarvingSignature[] sigs)
        {
            CarvingResult r = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options(sigs, data.Length));
            return [.. r.Files.Enumerate()];
        }

        CarvedFile f = Assert.Single(await Run(footer));
        Assert.Equal((0L, 102L, LengthMethod.Footer), (f.Offset, f.Length, f.Method));
        Assert.Equal(106, Assert.Single(await Run(includeMore)).Length);
        CarvedFile s = Assert.Single(await Run(size));
        Assert.Equal((1024L, 18L, LengthMethod.SizeField), (s.Offset, s.Length, s.Method));
        IReadOnlyList<CarvedFile> m = await Run(max);
        Assert.Equal([(2048L, 600L), (7680L, 512L)], m.Select(x => (x.Offset, x.Length)));
        Assert.All(m, x => Assert.Equal(LengthMethod.MaxSize, x.Method));

        // 最小サイズ未満は結果に含めない。サイズの取得が最大サイズを超えるものも含めない。
        Assert.Empty(await Run(Sig("Foot", "AA BB", 4096, footer: "EE FF", min: 200)));
        Assert.Empty(await Run(Sig("Size", "AC BD", 10, size: new SizeFieldRule { Offset = 4, Bits = 16, Add = 8 })));
        // フッターが見つからなければ最大サイズ。
        Assert.Equal(50, Assert.Single(await Run(Sig("Foot", "AA BB", 50, footer: "12 34 56"))).Length);
    }

    [Fact]
    public async Task HeaderOffsetAndWildcardsAndSelectionRangesWork()
    {
        byte[] data = new byte[4096];
        // MP4 のように、ヘッダー (ftyp) がファイルの先頭から 4 バイト後にある。ファイルの先頭はセクタ境界 (1024)。
        "ftyp"u8.CopyTo(data.AsSpan(1024 + 4));
        data[2000] = 0x11;
        data[2001] = 0x07;
        data[2002] = 0x33;
        CarvingSignature off = Sig("Off", "66 74 79 70", 100, headerOffset: 4);
        CarvingSignature wild = Sig("Wild", "11 ?7 ??", 10, aligned: false);

        CarvingResult r = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([off, wild], data.Length));
        Assert.Equal([(1024L, "Off"), (2000L, "Wild")], r.Files.Enumerate().Select(f => (f.Offset, f.Type)));

        // 選択範囲の外は見つからない。ファイルの先頭が範囲より前になるヘッダーも含めない。
        CarvingResult narrow = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([off, wild], data.Length) with { Ranges = [new SearchRange(1030, 2000)] });
        Assert.Equal([(2000L, "Wild")], narrow.Files.Enumerate().Select(f => (f.Offset, f.Type)));
    }

    [Fact]
    public async Task SkipInsideIgnoresHeadersInsideAFoundFile()
    {
        byte[] data = new byte[8192];
        foreach (int at in new[] { 0, 512, 1024, 4096 })
        {
            data[at] = 0xAA;
            data[at + 1] = 0xBB;
            data[at + 2] = 0xCC;
        }

        // 各ファイルは AA BB CC から 1000 バイト (最大サイズ)。0 のファイルは 0〜999 なので、512 のファイルは内側。1024 は外側。
        CarvingSignature sig = Sig("Blob", "AA BB CC", 1000);
        CarvingResult both = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([sig], data.Length));
        Assert.Equal([0L, 512L, 1024L, 4096L], both.Files.Enumerate().Select(f => f.Offset));
        CarvingResult skipping = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([sig], data.Length) with { SkipInside = true });
        Assert.Equal([0L, 1024L, 4096L], skipping.Files.Enumerate().Select(f => f.Offset));
    }

    [Fact]
    public async Task UnreadableChunksAreSkippedAndRecordedAtTheEndOfTheManifest()
    {
        byte[] data = new byte[256 * 1024];
        foreach (int at in new[] { 0x1000, 0x11000, 0x21000 })
        {
            data[at] = 0xAA;
            data[at + 1] = 0xBB;
        }

        // 2 つ目のヘッダー (0x11000) を含むブロックが読めない。
        var faulty = new FaultyData(new MemoryCarveData(data), 0x11000, 0x100);
        string folder = Path.Combine(_dir, "out");
        CarvingResult r = await CarvingEngine.RunAsync(faulty, Options([Sig("Blob", "AA BB", 100)], data.Length, folder));
        Assert.Equal([0x1000L, 0x21000L], r.Files.Enumerate().Select(f => f.Offset));
        Assert.Contains(r.Unreadable, u => u.Offset == 0x11000);
        string[] lines = File.ReadAllLines(r.ManifestPath!);
        Assert.StartsWith("#unreadable,", lines[^1], StringComparison.Ordinal);
        Assert.Contains("69632", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListOnlyCreatesTheManifestWithoutWritingFilesAndWithoutAFolderNothingIsWritten()
    {
        byte[] image = TestDataCatalog.ForCarve();
        string folder = Path.Combine(_dir, "list");
        CarvingResult listed = await CarvingEngine.RunAsync(new MemoryCarveData(image), Options(BuiltInSignatures.All, image.Length, folder, export: false));
        Assert.Equal(4, listed.Files.Count);
        Assert.False(Directory.Exists(Path.Combine(folder, "jpeg")));
        List<string[]> rows = ReadManifest(listed.ManifestPath!);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(string.Empty, r[7]));
        Assert.All(listed.Files.Enumerate(), f => Assert.False(f.Exported));

        CarvingResult nowhere = await CarvingEngine.RunAsync(new MemoryCarveData(image), Options(BuiltInSignatures.All, image.Length));
        Assert.Equal(4, nowhere.Files.Count);
        Assert.Null(nowhere.ManifestPath);
    }

    [Fact]
    public async Task WhenTheOutputRunsOutOfSpaceTheScanPausesUntilTheUserChoosesAnotherFolderOrStops()
    {
        byte[] image = TestDataCatalog.ForCarve();
        string folder = Path.Combine(_dir, "small");
        string other = Path.Combine(_dir, "big");
        var asked = new List<OutputProblemInfo>();
        long free = 10;
        CarvingOptions options = Options(BuiltInSignatures.All, image.Length, folder) with
        {
            FreeSpace = f => f == other ? long.MaxValue : free,
            OnOutputProblem = (info, _) =>
            {
                asked.Add(info);
                return Task.FromResult(asked.Count == 1 ? new OutputDecision(OutputDecisionKind.Retry) : new OutputDecision(OutputDecisionKind.ChangeFolder, other));
            },
        };
        CarvingResult r = await CarvingEngine.RunAsync(new MemoryCarveData(image), options);
        Assert.Equal(2, asked.Count);
        Assert.All(asked, a => Assert.Equal(OutputProblem.NotEnoughSpace, a.Kind));
        Assert.Equal(4, r.Files.Count);
        Assert.Equal(other, r.OutputFolder);
        Assert.True(File.Exists(Path.Combine(other, "jpeg", "0000000000100000.jpg")));
        Assert.True(r.Files.Enumerate().All(f => f.Exported));

        // 中止を選ぶと止まり、それまでのマニフェストは残る。
        string stop = Path.Combine(_dir, "stop");
        CarvingResult aborted = await CarvingEngine.RunAsync(new MemoryCarveData(image), Options(BuiltInSignatures.All, image.Length, stop) with { FreeSpace = _ => 5, OnOutputProblem = (_, _) => Task.FromResult(new OutputDecision(OutputDecisionKind.Abort)) });
        Assert.True(aborted.AbortedByOutputProblem);
        Assert.True(aborted.Cancelled);
        Assert.True(File.Exists(Path.Combine(stop, CarvingEngine.ManifestFileName)));
    }

    [Fact]
    public async Task CancellingBeforeStartReturnsAnEmptyResultWithAManifestHeader()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        string folder = Path.Combine(_dir, "cancelled");
        CarvingResult r = await CarvingEngine.RunAsync(new ZeroData(1 << 30), Options(BuiltInSignatures.All, 1 << 30, folder), cts.Token);
        Assert.True(r.Cancelled);
        Assert.Equal(0, r.Files.Count);
        Assert.Single(File.ReadAllLines(r.ManifestPath!));
    }

    [Fact]
    public async Task ASignatureWhoseStructureValidatorIsMissingYieldsNoResults()
    {
        byte[] data = new byte[2048];
        data[0] = 0xAA;
        data[1] = 0xBB;
        CarvingResult r = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([Sig("S", "AA BB", 100, structure: "no-such-template")], data.Length));
        Assert.Equal(0, r.Files.Count);
    }

    [Fact]
    public async Task AStructureTemplateCanDecideTheLength()
    {
        byte[] png = TestDataCatalog.Png16();
        byte[] data = [.. new byte[512], .. png, .. new byte[2048]];
        var catalog = TemplateCatalog.CreateDefault(null);
        var resolver = new TemplateStructureResolver(catalog);
        TemplateCatalogEntry? entry = catalog.Find("png");
        Assert.NotNull(entry);
        CarvingSignature sig = Sig("PNGT", "89 50 4E 47", 100_000, structure: entry!.Name);
        CarvingResult r = await CarvingEngine.RunAsync(new MemoryCarveData(data), Options([sig], data.Length) with { Resolver = resolver });
        CarvedFile file = Assert.Single(r.Files.Enumerate());
        Assert.Equal((512L, (long)png.Length, LengthMethod.Structure), (file.Offset, file.Length, file.Method));
    }

    [Fact]
    public void TheResultStoreHoldsMoreThanAMillionEntriesCompactly()
    {
        CarvingSignature[] sigs = [Sig("A", "AA"), Sig("B", "BB")];
        var store = new CarvedFileStore(sigs, 512);
        long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 1_200_000; i++)
        {
            store.Add(i & 1, i * 512L, 100, LengthMethod.MaxSize);
        }

        long used = GC.GetTotalMemory(true) - before;
        Assert.Equal(1_200_000, store.Count);
        Assert.Equal((1_199_999 * 512L, "B"), (store[1_199_999].Offset, store[1_199_999].Type));
        Assert.Equal(1_200_000, store[1_199_999].Number);
        Assert.True(used < 80L * 1024 * 1024, $"{used:N0} bytes");
        _ = Encoding.UTF8;
    }

    [Fact]
    public void ManifestPathsStayRelativeAndTypeFoldersAreSafe()
    {
        Assert.Equal("java-class", new CarvingSignature { Name = "Java class" }.Folder);
        Assert.Equal("unknown", new CarvingSignature { Name = "***" }.Folder);
        Assert.Equal("a-b", new CarvingSignature { Name = "../a/b" }.Folder);
    }
}
