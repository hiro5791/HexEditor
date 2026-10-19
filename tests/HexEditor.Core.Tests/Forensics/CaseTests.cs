using System.Text.Json;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>FOR-03 ケースと証拠一覧。</summary>
public sealed class CaseTests : IDisposable
{
    private static readonly AuditIdentity Identity = new("tester", "TESTPC", "1.2.3");

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-case").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CaseInfo Info() => new() { Number = "C-0001", Name = "テストケース", Investigator = "tester", TimeZoneId = "Tokyo Standard Time" };

    private CaseManager NewManager(ForensicAudit? audit = null)
    {
        audit ??= new ForensicAudit(Identity, new FixedTime(new DateTime(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc)));
        return new CaseManager(audit, "tester", new FixedTime(new DateTime(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc))) { AppVersion = "1.2.3" };
    }

    [Fact]
    [Trait(TC, "TC-FOR-03-01")]
    public void NewCaseCreatesTheFolderLayoutAndCaseJson()
    {
        string folder = Path.Combine(_dir, "case1");
        Directory.CreateDirectory(folder);

        // ケース番号が空なら作れない (ダイアログの作成ボタンは、このチェックで無効になる)。
        Assert.Contains(nameof(CaseInfo.Number), new CaseInfo { Name = "x" }.Validate());
        Assert.Contains(nameof(CaseInfo.Name), new CaseInfo { Number = "x" }.Validate());
        CaseManager manager = NewManager();
        Assert.Throws<CaseValidationException>(() => manager.Create(folder, new CaseInfo { Name = "no number" }));
        Assert.Empty(Directory.GetFileSystemEntries(folder));

        ForensicCase created = manager.Create(folder, Info());
        Assert.Equal(manager.CurrentCase, created);
        Assert.True(File.Exists(Path.Combine(folder, "case.json")));
        Assert.True(File.Exists(Path.Combine(folder, "audit.log")));
        Assert.True(Directory.Exists(Path.Combine(folder, "exports")));
        Assert.True(Directory.Exists(Path.Combine(folder, "reports")));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "case.json")));
        JsonElement c = json.RootElement.GetProperty("case");
        Assert.Equal("C-0001", c.GetProperty("number").GetString());
        Assert.Equal("テストケース", c.GetProperty("name").GetString());
        Assert.Equal("tester", c.GetProperty("investigator").GetString());
        Assert.Equal("Tokyo Standard Time", c.GetProperty("timeZoneId").GetString());
        Assert.StartsWith("2026-10-07T03:15:42", c.GetProperty("createdUtc").GetString(), StringComparison.Ordinal);

        // 同時に開けるケースは 1 つ。
        Assert.Throws<InvalidOperationException>(() => manager.Create(Path.Combine(_dir, "case2"), Info()));
        // 既にケースがあるフォルダには作れない。
        manager.Close();
        Assert.Throws<CaseFolderException>(() => manager.Create(folder, Info()));

        // 作成が監査ログに記録される。
        Assert.Contains("case.create", File.ReadAllText(Path.Combine(folder, "audit.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void CreatingInAFolderThatCannotBeWrittenFailsWithoutCreatingTheCase()
    {
        // フォルダの場所にファイルがあるため、フォルダを作れない。
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        CaseManager manager = NewManager();
        Assert.ThrowsAny<IOException>(() => manager.Create(Path.Combine(blocker, "case"), Info()));
        Assert.Null(manager.CurrentCase);
    }

    [Fact]
    [Trait(TC, "TC-FOR-03-02")]
    public void AddingADiskRecordsItsDetailsAndHashesAndRefusesTheSameSourceTwice()
    {
        // 偽のディスク (モデル名・シリアル番号・容量) を、既定の設定 (SHA-256 と MD5) で証拠に追加する。
        string file = TestDataCatalog.Generate("TD-RANDOM-16M", _dir);
        byte[] data = File.ReadAllBytes(file);
        using var doc = new Document(new MemoryByteSource(data));
        IReadOnlyList<EvidenceHash> hashes = EvidenceHasher.Compute(doc.DataSnapshot, EvidenceHasher.DefaultAlgorithms);
        Assert.Equal(["SHA-256", "MD5"], hashes.Select(h => h.Algorithm));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), hashes[0].Value);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data)), hashes[1].Value);

        CaseManager manager = NewManager();
        manager.Create(Path.Combine(_dir, "case"), Info());
        EvidenceItem item = manager.Register(new EvidenceRegistration
        {
            Kind = EvidenceKind.PhysicalDisk,
            Path = @"\\.\PhysicalDrive2",
            Description = "Disk 2",
            Model = "Virtual HD",
            Serial = "SN-0001",
            CapacityBytes = data.Length,
            SectorCount = data.Length / 512,
            SectorSize = 512,
        })!;
        Assert.Equal("E001", item.Id);
        Assert.Equal(EvidenceHashState.NotComputed, item.HashState);

        // ハッシュは後から記録する (登録時の計算を中止したら未計算のまま)。
        manager.RecordHashes(item.Id, hashes);
        EvidenceItem stored = manager.CurrentCase!.Find("E001")!;
        Assert.Equal("Virtual HD", stored.Model);
        Assert.Equal("SN-0001", stored.Serial);
        Assert.Equal(data.Length, stored.CapacityBytes);
        Assert.Equal(data.Length / 512, stored.SectorCount);
        Assert.Equal(hashes[0].Value, stored.HashOf("sha-256"));
        Assert.Equal(hashes[1].Value, stored.HashOf("MD5"));
        Assert.Contains("SHA-256: ", stored.HashSummary, StringComparison.Ordinal);

        // 同じディスクをもう一度は、既存の証拠番号を示して止める。
        DuplicateEvidenceException duplicate = Assert.Throws<DuplicateEvidenceException>(() => manager.Register(
            new EvidenceRegistration { Kind = EvidenceKind.PhysicalDisk, Path = @"\\.\physicaldrive2" }));
        Assert.Equal("E001", duplicate.ExistingId);

        // ハッシュ計算とその結果が監査ログに記録される。
        string log = File.ReadAllText(Path.Combine(_dir, "case", "audit.log"));
        Assert.Contains("evidence.add", log, StringComparison.Ordinal);
        Assert.Contains(hashes[0].Value, log, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingTheHashLeavesTheEvidenceRegisteredWithoutAHash()
    {
        string file = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        CaseManager manager = NewManager();
        manager.Create(Path.Combine(_dir, "case"), Info());
        EvidenceItem item = manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = file })!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var doc = new Document(FileByteSource.Open(file));
        Assert.ThrowsAny<OperationCanceledException>(() => EvidenceHasher.Compute(doc.DataSnapshot, ["SHA-256"], cancellationToken: cts.Token));
        manager.RecordHashCancelled(item.Id);
        EvidenceItem stored = manager.CurrentCase!.Find(item.Id)!;
        Assert.Equal(EvidenceHashState.NotComputed, stored.HashState);
        Assert.Empty(stored.Hashes);
    }

    [Fact]
    public void HashesAreComputedOnTheRawDataNotOnTheEditedContent()
    {
        byte[] data = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        using var doc = new Document(new MemoryByteSource(data));
        doc.SetViewTransform(new XorView());

        // 変換ビューで表示している内容 (Current) ではなく、元のデータ (DataSnapshot) のハッシュを求める。
        Assert.Equal((byte)(data[1] ^ 0x5A), ReadFirst(doc.Current));
        IReadOnlyList<EvidenceHash> hashes = EvidenceHasher.Compute(doc.Current, ["SHA-256"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), hashes[0].Value);
    }

    private static byte ReadFirst(DocumentSnapshot snapshot)
    {
        byte[] one = new byte[1];
        snapshot.Read(1, one);
        return one[0];
    }

    private sealed class XorView : IViewTransform
    {
        public string Label => "XOR 5A";

        public bool CanInverse => true;

        public int Alignment => 1;

        public long AlignmentOrigin => 0;

        public void Forward(long offset, Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= 0x5A;
            }
        }

        public void Inverse(long offset, Span<byte> data) => Forward(offset, data);
    }

    [Fact]
    [Trait(TC, "TC-FOR-03-03")]
    public void ReopeningTheCaseRestoresTheEvidenceListExactly()
    {
        string seq = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        string rnd = TestDataCatalog.Generate("TD-RANDOM-16M", _dir);
        string folder = Path.Combine(_dir, "case");
        CaseManager manager = NewManager();
        manager.Create(folder, Info());
        foreach ((string path, string description) in new[] { (seq, "証拠 1"), (rnd, "証拠 2") })
        {
            EvidenceItem item = manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = path, Description = description })!;
            using var doc = new Document(FileByteSource.Open(path));
            manager.RecordHashes(item.Id, EvidenceHasher.Compute(doc.DataSnapshot, EvidenceHasher.DefaultAlgorithms));
        }

        EvidenceItem[] before = [.. manager.ActiveEvidence];
        manager.Close();
        Assert.Null(manager.CurrentCase);

        // 別のマネージャー (再起動に相当) で同じフォルダを開く。
        CaseManager second = NewManager();
        second.Open(folder);
        EvidenceItem[] after = [.. second.ActiveEvidence];
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].Description, after[i].Description);
            Assert.Equal(before[i].Kind, after[i].Kind);
            Assert.Equal(before[i].Path, after[i].Path);
            Assert.Equal(before[i].Hashes, after[i].Hashes);
            Assert.Equal(before[i].AddedUtc, after[i].AddedUtc);
        }

        Assert.Equal("証拠 1", after[0].Description);
        Assert.Equal("Tokyo Standard Time", second.CurrentCase!.Info.TimeZoneId);
        Assert.True(AuditVerifier.Verify(Path.Combine(folder, "audit.log")).IsIntact);
    }

    [Fact]
    [Trait(TC, "TC-FOR-03-04")]
    public void RemovingEvidenceKeepsItInCaseJsonMarkedExcludedAndLogsIt()
    {
        string file = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        byte[] before = File.ReadAllBytes(file);
        string folder = Path.Combine(_dir, "case");
        CaseManager manager = NewManager();
        manager.Create(folder, Info());
        manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = file });

        Assert.True(manager.Exclude("E001"));
        Assert.False(manager.Exclude("E001"));
        Assert.Empty(manager.ActiveEvidence);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "case.json")));
        JsonElement e = json.RootElement.GetProperty("evidence")[0];
        Assert.Equal("E001", e.GetProperty("id").GetString());
        Assert.True(e.GetProperty("excluded").GetBoolean());
        string last = File.ReadAllLines(Path.Combine(folder, "audit.log"))[^1];
        AuditLog.TryParse(last, out AuditEntry? entry);
        Assert.Equal(AuditOperations.EvidenceExclude, entry!.Operation);
        Assert.Equal("E001", entry.EvidenceId);

        // データソース自体には何もしない。
        Assert.Equal(before, File.ReadAllBytes(file));

        // 除外したデータソースは、もう一度登録できる (新しい証拠番号)。
        Assert.Equal("E002", manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = file })!.Id);
    }

    [Fact]
    public void ABrokenCaseFileIsReportedWithALineNumberAndLeftUntouched()
    {
        string folder = Path.Combine(_dir, "case");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "case.json");
        File.WriteAllText(path, "{\n  \"version\": 1,\n  \"case\": {\n    \"number\": \"C-1\",,\n  }\n}\n");
        byte[] bytes = File.ReadAllBytes(path);
        CaseFileException ex = Assert.Throws<CaseFileException>(() => ForensicCase.Open(folder, "tester"));
        Assert.Equal(4, ex.Line);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        // 必須の項目がない・証拠番号の重複も開かない。
        File.WriteAllText(path, "{\"version\":1,\"case\":{\"number\":\"C-1\",\"name\":\"\"},\"evidence\":[]}");
        Assert.Throws<CaseFileException>(() => ForensicCase.Open(folder, "tester"));
        File.WriteAllText(path, "{\"version\":1,\"case\":{\"number\":\"C-1\",\"name\":\"n\"},\"evidence\":[{\"id\":\"E001\"},{\"id\":\"E001\"}]}");
        Assert.Throws<CaseFileException>(() => ForensicCase.Open(folder, "tester"));
        Assert.Throws<CaseFolderException>(() => ForensicCase.Open(Path.Combine(_dir, "none"), "tester"));
    }

    [Fact]
    public void CaseJsonIsSavedThroughATemporaryFileAndASaveFailureKeepsTheMemoryState()
    {
        string folder = Path.Combine(_dir, "case");
        CaseManager manager = NewManager();
        ForensicCase c = manager.Create(folder, Info());
        manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = Path.Combine(_dir, "a.bin") });
        Assert.False(File.Exists(Path.Combine(folder, "case.json.tmp")));

        // case.json を排他で開いて保存を失敗させる。メモリの状態は変わらない。
        using (new FileStream(Path.Combine(folder, "case.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => c.AddEvidence(new EvidenceRegistration { Kind = EvidenceKind.File, Path = Path.Combine(_dir, "b.bin") }));
        }

        Assert.Single(c.AllEvidence);
        Assert.NotNull(c.AddEvidence(new EvidenceRegistration { Kind = EvidenceKind.File, Path = Path.Combine(_dir, "b.bin") }));
    }

    [Fact]
    public void OutputGuardRefusesTheSameDiskAndChecksSpaceAndFileSizeLimits()
    {
        // 偽のデバイス: ディスク 2 に C: (ボリューム) と、ディスク 5 に E:。
        var catalog = new DeviceCatalog
        {
            Disks = [new DiskInfo { Number = 2 }, new DiskInfo { Number = 5 }],
            Volumes =
            [
                new VolumeDeviceInfo { Path = @"\\.\X:", DriveLetter = "X", DiskNumber = 2 },
                new VolumeDeviceInfo { Path = @"\\.\Y:", DriveLetter = "Y", DiskNumber = 2 },
                new VolumeDeviceInfo { Path = @"\\.\Z:", DriveLetter = "Z", DiskNumber = 5 },
            ],
        };
        var locator = new CatalogDiskLocator(() => catalog);

        // 調査対象 = 物理ディスク 2。同じディスクの Y: には出力できない (Y:\carved は作られない)。
        OutputCheck same = OutputGuard.Check(@"Y:\carved", [DevicePath.PhysicalDrive(2)], 0, 0, locator);
        Assert.Equal(OutputProblem.SameDisk, same.Problem);
        Assert.Equal(2, same.DiskNumber);
        Assert.False(Directory.Exists(@"Y:\carved"));

        // ボリュームが入力元でも、そのボリュームのあるディスク全体が対象。
        Assert.Equal(OutputProblem.SameDisk, OutputGuard.Check(@"Y:\carved", [@"\\.\X:"], 0, 0, locator).Problem);
        // 別のディスクは (容量の検査を通れば) よい。
        Assert.True(OutputGuard.Check(@"Z:\out", [DevicePath.PhysicalDrive(2)], 0, 0, locator).Ok);
        // 出力先のディスクが分からないときは止める。
        Assert.Equal(OutputProblem.SameDisk, OutputGuard.Check(@"Q:\out", [DevicePath.PhysicalDrive(2)], 0, 0, locator).Problem);
        // イメージファイルだけが入力元なら、デバイスの検査はしない。
        Assert.True(OutputGuard.Check(_dir, [], 0, 0, locator).Ok);

        // 空き容量と FAT32 の上限。
        var small = new FixedLocator { Free = 1000, FileSystem = "FAT32" };
        Assert.Equal(OutputProblem.NotEnoughSpace, OutputGuard.Check(_dir, [], 5000, 0, small).Problem);
        Assert.Equal(OutputProblem.FileTooLarge, OutputGuard.Check(_dir, [], 0, 5L * 1024 * 1024 * 1024, small).Problem);
        Assert.True(OutputGuard.Check(_dir, [], 0, OutputGuard.Fat32FileLimit, small).Ok);
    }

    private sealed class FixedLocator : IDiskLocator
    {
        public long Free { get; init; }

        public string FileSystem { get; init; } = "NTFS";

        public IReadOnlyList<int>? DisksOfPath(string path) => null;

        public IReadOnlyList<int>? DisksOfDevice(string devicePath) => null;

        public long? FreeBytes(string path) => Free;

        public string? FileSystemName(string path) => FileSystem;
    }
}
