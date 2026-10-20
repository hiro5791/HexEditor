using System.Security.Cryptography;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>FOR-20 ディスクイメージの作成 (Core の結合テスト)。入力元はテストデータのファイル・仮想のデータソース、出力はテスト用の一時フォルダ。</summary>
[Collection(SerialCollection.Name)]
public sealed class ImageAcquisitionTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 7, 3, 15, 42, TimeSpan.Zero);

    private static AcquisitionOptions Options(string output, ImageFormat format = ImageFormat.Raw) => new()
    {
        Output = output,
        Format = format,
        Hashes = [ImageHashes.Md5, ImageHashes.Sha256],
        AppVersion = "HexEditor 0.0.0-test",
        Case = new EwfCaseInfo { CaseNumber = "C-0001", EvidenceNumber = "E001", Examiner = "tester", Description = "テストケース" },
    };

    private static (byte[] Md5, byte[] Sha256) Expected(IByteSource source) => (Hash(source, HashAlgorithmName.MD5), Hash(source, HashAlgorithmName.SHA256));

    [Fact]
    [Trait("TC", "TC-FOR-20-01")]
    public async Task Raw_acquisition_hashes_match_the_source_and_the_log_has_every_item()
    {
        using var dir = new ImageTempDir();
        using FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
        (byte[] md5, byte[] sha256) = Expected(source);
        var recorder = new RecordingHooks();
        ImageForensics.Hooks = recorder;
        try
        {
            string output = dir.File(Path.Combine("images", "m.dd"));
            AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(output));

            Assert.Equal(AcquisitionStatus.Completed, result.Status);
            Assert.Equal(sha256, HashFiles([output], HashAlgorithmName.SHA256));
            Assert.Equal(md5, result.Hashes[ImageHashes.Md5]);
            Assert.Equal(sha256, result.Hashes[ImageHashes.Sha256]);
            Assert.Equal(VerificationVerdict.Match, result.Verification!.Overall);

            string log = File.ReadAllText(dir.File(Path.Combine("images", "m.txt")));
            IReadOnlyDictionary<string, byte[]> logged = AcquisitionLog.ReadHashes(dir.File(Path.Combine("images", "m.txt")));
            Assert.Equal(md5, logged[ImageHashes.Md5]);
            Assert.Equal(sha256, logged[ImageHashes.Sha256]);
            Assert.Contains("test-source", log);                  // 入力元
            Assert.Contains("Test Model", log);                   // モデル名
            Assert.Contains("SERIAL-1", log);                     // シリアル番号
            Assert.Contains("raw", log);                          // 出力形式
            Assert.Contains("m.dd", log);                         // ファイル一覧
            Assert.Matches(@"Started: \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ \(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d\)", log); // UTC とローカル
            Assert.Contains("Sectors read: " + (GiB / 512), log); // 読み込んだセクタ数
            Assert.Contains("Verification: match", log);         // 検証結果
            Assert.Contains("HexEditor 0.0.0-test", log);         // アプリのバージョン
            Assert.Contains("C-0001", log);                       // ケース情報

            // 監査ログに開始と終了 (ハッシュ値付き) の 2 件 (FOR-04 の受け入れ基準 1)。
            Assert.Equal([ImageAuditResult.Started, ImageAuditResult.Success], recorder.Records.Where(r => r.Operation == "image.create").Select(r => r.Result));
            Assert.Contains(recorder.Records, r => r.Operation == "image.verify" && r.Parameters["verdict"] == "Match");
            Assert.Equal(Convert.ToHexStringLower(sha256), recorder.Records[1].Parameters["hash.SHA-256"]);
            Assert.Single(recorder.Registered);
        }
        finally
        {
            ImageForensics.Hooks = NullImageForensicHooks.Instance;
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-20-02")]
    public async Task Split_raw_of_100M_makes_eleven_segments_that_concatenate_to_the_source()
    {
        using var dir = new ImageTempDir();
        using FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
        byte[] sha256 = Hash(source, HashAlgorithmName.SHA256);
        string output = dir.File(Path.Combine("split", "m"));
        AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(output, ImageFormat.SplitRaw) with
        {
            SplitSize = 100 * MiB,
            Verify = false,
        });

        Assert.Equal(AcquisitionStatus.Completed, result.Status);
        string[] files = [.. Directory.GetFiles(dir.File("split"), "m.0*").Order(StringComparer.Ordinal)];
        Assert.Equal([.. Enumerable.Range(1, 11).Select(i => $"m.{i:D3}")], files.Select(Path.GetFileName));
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(104_857_600, new FileInfo(files[i]).Length);
        }

        Assert.Equal(25_165_824, new FileInfo(files[10]).Length);
        Assert.Equal(sha256, HashFiles(files, HashAlgorithmName.SHA256));
    }

    [Fact]
    [Trait("TC", "TC-FOR-20-03")]
    public async Task E01_acquisition_opens_in_the_app_and_the_stored_md5_matches_the_content()
    {
        using var dir = new ImageTempDir();
        using FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
        byte[] md5 = Hash(source, HashAlgorithmName.MD5);
        string output = dir.File(Path.Combine("e01", "m.E01"));
        AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(output, ImageFormat.E01) with
        {
            Compression = EwfCompression.Fast,
        });
        Assert.Equal(AcquisitionStatus.Completed, result.Status);
        Assert.Equal(VerificationVerdict.Match, result.Verification!.Overall);

        using var image = (EwfByteSource)ImageOpener.Open(output);
        Assert.Equal(GiB, image.Length);
        Assert.Equal(md5, image.Info.StoredHashes[ImageHashes.Md5]);
        Assert.Equal(md5, Hash(image, HashAlgorithmName.MD5));
        Assert.Equal("C-0001", image.Info.Case!.CaseNumber);
        Assert.Equal("E001", image.Info.Case.EvidenceNumber);
        Assert.Equal(64, image.Info.SectorsPerChunk);
        Assert.True(image.Info.Compressed);

        // 基準の検証: libewf の ewfverify と ewfinfo (ツールのあるランナーだけ。EwfToolsFact のテストで行う)。
        EwfTools.VerifyIfAvailable(output, md5, "C-0001", "E001");
    }

    [Fact]
    [Trait("TC", "TC-FOR-20-04")]
    public async Task Unreadable_sectors_are_zero_filled_and_recorded_in_the_log_and_error2()
    {
        using var dir = new ImageTempDir();
        var faulty = new FaultyByteSource(FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")));
        using (faulty)
        {
            faulty.AddReadError(0x100000, 0x1000);
            string raw = dir.File("r.dd");
            string e01 = dir.File("r.E01");
            AcquisitionResult rawResult = await ImageAcquisition.RunAsync(faulty, Info(faulty), Options(raw) with { Retries = 3, Verify = false });
            AcquisitionResult e01Result = await ImageAcquisition.RunAsync(faulty, Info(faulty), Options(e01, ImageFormat.E01) with { Retries = 3, Verify = false });

            // 読めない範囲は 00、それ以外は入力元と同じ。
            using FileByteSource original = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
            using FileByteSource image = FileByteSource.Open(raw);
            Assert.All(ReadAll(image, 0x100000, 0x1000), b => Assert.Equal(0, b));
            Assert.Equal(ReadAll(original, 0xFF000, 0x1000), ReadAll(image, 0xFF000, 0x1000));
            Assert.Equal(ReadAll(original, 0x101000, 0x1000), ReadAll(image, 0x101000, 0x1000));

            // 取得ログと error2 の範囲: セクタ 2048 から 8 セクタ。完了時の警告の数 8。
            Assert.Equal([new SectorRange(2048, 8)], rawResult.BadSectors);
            Assert.Equal(8, rawResult.BadSectorCount);
            Assert.Contains("LBA 2048 + 8", File.ReadAllText(dir.File("r.txt")));
            using var ewf = (EwfByteSource)ImageOpener.Open(e01);
            Assert.Equal([new SectorRange(2048, 8)], ewf.Info.AcquisitionErrors);
            Assert.Equal(8, e01Result.BadSectorCount);
            EwfTools.CheckErrorsIfAvailable(e01, 2048, 8);
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-20-05")]
    public async Task Cancelling_asks_whether_to_keep_the_files_and_the_log_says_interrupted()
    {
        using var dir = new ImageTempDir();
        var faulty = new FaultyByteSource(new VirtualByteSource(64 * MiB, VirtualContent.Offset64)) { HoldFromOffset = 16 * MiB };
        faulty.Hold.Reset();
        using (faulty)
        {
            var center = new OperationCenter();
            var prompts = new FakePrompts(keep: true);
            string output = dir.File("c.dd");
            Task<AcquisitionResult> run = center.RunAsync("acquire", OperationKind.ReadOnly, null, null,
                op => ImageAcquisition.RunAsync(faulty, Info(faulty), Options(output) with { LogText = new AcquisitionLogText { StatusCancelled = "中断" } }, op, prompts));

            // 20% を超えてから (16 MiB で止めた読み込みの手前まで進んだら) キャンセルする。
            LongRunningOperation op = await WaitForAsync(() => center.Active.FirstOrDefault(o => o.ProcessedBytes >= 12 * MiB));
            op.Cancel();
            faulty.Hold.Set();
            AcquisitionResult result = await run;

            Assert.Equal(AcquisitionStatus.Cancelled, result.Status);
            Assert.True(prompts.AskedKeep);
            Assert.True(File.Exists(output));
            Assert.Contains("中断", File.ReadAllText(dir.File("c.txt")));
        }
    }

    [Fact]
    public async Task Cancelling_and_choosing_delete_removes_the_partial_files()
    {
        using var dir = new ImageTempDir();
        var faulty = new FaultyByteSource(new VirtualByteSource(16 * MiB, VirtualContent.Offset64)) { HoldFromOffset = 8 * MiB };
        faulty.Hold.Reset();
        using (faulty)
        {
            var center = new OperationCenter();
            string output = dir.File("d.E01");
            Task<AcquisitionResult> run = center.RunAsync("acquire", OperationKind.ReadOnly, null, null,
                op => ImageAcquisition.RunAsync(faulty, Info(faulty), Options(output, ImageFormat.E01), op, new FakePrompts(keep: false)));
            LongRunningOperation op = await WaitForAsync(() => center.Active.FirstOrDefault(o => o.ProcessedBytes >= 4 * MiB));
            op.Cancel();
            faulty.Hold.Set();
            AcquisitionResult result = await run;
            Assert.Equal(AcquisitionStatus.Cancelled, result.Status);
            Assert.False(File.Exists(output));
            Assert.True(File.Exists(dir.File("d.txt")));
        }
    }

    [Fact]
    public async Task A_removed_source_stops_and_the_log_says_the_source_was_lost()
    {
        using var dir = new ImageTempDir();
        var faulty = new FaultyByteSource(new VirtualByteSource(8 * MiB, VirtualContent.Offset64));
        using (faulty)
        {
            faulty.AddReadError(4 * MiB, 4 * MiB, UnreadableReason.Disconnected);
            AcquisitionResult result = await ImageAcquisition.RunAsync(faulty, Info(faulty), Options(dir.File("lost.dd")) with
            {
                LogText = new AcquisitionLogText { StatusSourceLost = "入力元が失われました" },
            });
            Assert.Equal(AcquisitionStatus.SourceLost, result.Status);
            Assert.Contains("入力元が失われました", File.ReadAllText(dir.File("lost.txt")));
        }
    }

    [Fact]
    public async Task A_write_error_pauses_and_retry_continues_to_a_correct_image()
    {
        using var dir = new ImageTempDir();
        using var source = new VirtualByteSource(4 * MiB, VirtualContent.Offset64);
        foreach (ImageFormat format in new[] { ImageFormat.Raw, ImageFormat.E01, ImageFormat.SplitRaw })
        {
            // 2 つ目のブロックの書き込みを 1 回だけ失敗させる (容量不足の代わり)。利用者は「再試行」を選ぶ。
            int failures = 0;
            ImageAcquisition.BeforeWrite = offset =>
            {
                if (offset == MiB && failures++ == 0)
                {
                    throw new IOException("There is not enough space on the disk.");
                }
            };
            try
            {
                var prompts = new FakePrompts(retries: 1);
                string output = dir.File("w-" + format + (format == ImageFormat.E01 ? ".E01" : format == ImageFormat.Raw ? ".dd" : string.Empty));
                AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(output, format) with { SplitSize = 3 * MiB }, null, prompts);
                Assert.Equal(1, prompts.WriteErrors);
                Assert.Equal(AcquisitionStatus.Completed, result.Status);
                Assert.Equal(VerificationVerdict.Match, result.Verification!.Overall);
            }
            finally
            {
                ImageAcquisition.BeforeWrite = null;
            }
        }
    }

    [Fact]
    public async Task Choosing_abort_after_a_write_error_fails_the_acquisition()
    {
        using var dir = new ImageTempDir();
        using var source = new VirtualByteSource(4 * MiB, VirtualContent.Offset64);
        ImageAcquisition.BeforeWrite = offset => throw new IOException("disk full");
        try
        {
            var prompts = new FakePrompts(retries: 0);
            AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(dir.File("a.dd")), null, prompts);
            Assert.Equal(AcquisitionStatus.Failed, result.Status);
            Assert.Equal(1, prompts.WriteErrors);
            Assert.True(File.Exists(dir.File("a.txt")));
        }
        finally
        {
            ImageAcquisition.BeforeWrite = null;
        }
    }

    [Fact]
    public async Task Selected_range_of_a_source_is_acquired_with_sector_alignment()
    {
        using var dir = new ImageTempDir();
        using var source = new VirtualByteSource(8 * MiB, VirtualContent.Offset64);
        AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source), Options(dir.File("part.E01"), ImageFormat.E01) with
        {
            RangeStart = 1024 * 512,
            RangeLength = 3000 * 512,
        });
        Assert.Equal(AcquisitionStatus.Completed, result.Status);
        using IImageSource image = ImageOpener.Open(dir.File("part.E01"));
        Assert.Equal(3000 * 512, image.Length);
        Assert.Equal(ReadAll(source, 1024 * 512, 4096), ReadAll(image, 0, 4096));
        Assert.Equal(ReadAll(source, 1024 * 512 + 2999 * 512, 512), ReadAll(image, 2999 * 512, 512));
    }

    private static async Task<LongRunningOperation> WaitForAsync(Func<LongRunningOperation?> find)
    {
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < limit)
        {
            if (find() is { } op)
            {
                return op;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The operation did not reach the expected progress.");
    }
}

/// <summary>監査ログ・証拠一覧の口の記録 (FOR-03・04 の代わり)。</summary>
internal sealed class RecordingHooks : IImageForensicHooks
{
    public List<ImageAuditRecord> Records { get; } = [];

    public List<(string Image, string Source, IReadOnlyDictionary<string, byte[]> Hashes)> Registered { get; } = [];

    public List<(string Target, VerificationResult Result)> Verifications { get; } = [];

    public Dictionary<string, IReadOnlyDictionary<string, byte[]>> EvidenceHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Evidence { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<int> EvidenceDisks { get; } = [];

    public bool IsWriteBlocked { get; set; }

    public ImageCaseInfo? Case { get; set; }

    public IReadOnlyList<int> EvidenceDiskNumbers => EvidenceDisks;

    public string? EvidenceNumberOf(string pathOrIdentity) => Evidence.Contains(pathOrIdentity) ? "E001" : null;

    public void Audit(ImageAuditRecord record)
    {
        lock (Records)
        {
            Records.Add(record);
        }
    }

    public void RegisterAcquiredImage(string imagePath, string sourcePath, IReadOnlyDictionary<string, byte[]> hashes) =>
        Registered.Add((imagePath, sourcePath, hashes));

    public IReadOnlyDictionary<string, byte[]> EvidenceHashesOf(string pathOrIdentity) =>
        EvidenceHashes.GetValueOrDefault(pathOrIdentity) ?? new Dictionary<string, byte[]>();

    public void RecordVerification(string pathOrIdentity, VerificationResult result) => Verifications.Add((pathOrIdentity, result));
}
