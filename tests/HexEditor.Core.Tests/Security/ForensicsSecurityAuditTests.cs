using System.Diagnostics;
using System.Globalization;
using HexEditor.Core.Devices;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Apple;
using HexEditor.Core.Files;
using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// フェーズ 5 (フォレンジック) のセキュリティ監査で直した点の回帰テスト: 書き込み保護・証拠の照合を別名のパスで抜けない、証拠のボリュームを含むディスクを
/// 証拠とみなす、出力先の検査がマウントポイントを見る、監査ログの末尾の切り詰めを検証で見つける、CSV インジェクション、書き出しの名前、
/// ファジングで見つけた解析の例外・終わらない処理。
/// </summary>
public sealed class ForensicsSecurityAuditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-sec-audit").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- デバイスのパス ----

    [Theory]
    [InlineData("\\\\.\\PhysicalDrive0\n")]
    [InlineData("\\\\.\\C:\n")]
    [InlineData("\\\\.\\CdRom0\n")]
    [InlineData("\\\\?\\Volume{01234567-89ab-cdef-0123-456789abcdef}\n")]
    public void Device_paths_with_a_trailing_newline_are_rejected(string path)
    {
        Assert.False(DevicePath.IsValid(path));
        Assert.True(DevicePath.IsValid(path.TrimEnd('\n')));
        Assert.Null(DevicePath.DiskNumber(path));
    }

    // ---- 別名のパス ----

    [Fact]
    public void Long_path_prefix_and_dot_segments_normalize_to_the_same_path()
    {
        string file = Path.Combine(_dir, "evidence.img");
        File.WriteAllBytes(file, [1, 2, 3]);
        string expected = CanonicalPath.Normalize(file);
        Assert.Equal(expected, CanonicalPath.Normalize(@"\\?\" + file), ignoreCase: true);
        Assert.Equal(expected, CanonicalPath.Normalize(Path.Combine(_dir, "sub", "..", "evidence.img")), ignoreCase: true);
        Assert.Equal(expected + ":ads", CanonicalPath.Normalize(file + ":ads"), ignoreCase: true);

        // まだないファイルも、既存のフォルダの部分をそろえて比べられる。
        Assert.Equal(CanonicalPath.Normalize(Path.Combine(_dir, "new.bin")), CanonicalPath.Normalize(@"\\?\" + Path.Combine(_dir, "new.bin")), ignoreCase: true);
        Assert.Equal(@"\\.\PhysicalDrive1", CanonicalPath.Normalize(@"\\.\PhysicalDrive1"));
    }

    [Fact]
    public void Write_block_cannot_be_bypassed_with_a_long_path_prefix_or_a_junction()
    {
        string evidenceDir = Directory.CreateDirectory(Path.Combine(_dir, "evidence")).FullName;
        string file = Path.Combine(evidenceDir, "disk.img");
        File.WriteAllBytes(file, new byte[16]);
        var policy = new WriteBlockPolicy();
        string key = WriteBlockPolicy.Normalize(file);
        policy.IsProtectedPath = p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase);
        policy.SetEnabled(true);
        Assert.Throws<WriteBlockedException>(() => policy.EnsureFileWrite(file));
        Assert.Throws<WriteBlockedException>(() => policy.EnsureFileWrite(@"\\?\" + file));
        Assert.Throws<WriteBlockedException>(() => policy.EnsureFileSave(@"\\?\" + file, null));

        // ジャンクション (管理者権限なしで作れる) を通したパス。作れない環境では確かめない。
        string junction = Path.Combine(_dir, "alias");
        if (TryCreateJunction(junction, evidenceDir))
        {
            Assert.Throws<WriteBlockedException>(() => policy.EnsureFileWrite(Path.Combine(junction, "disk.img")));
            Assert.Equal(WriteBlockTarget.FileSave, policy.CheckFileSave(Path.Combine(junction, "disk.img"), file));
            Directory.Delete(junction);
        }

        // 保護の対象でない場所には書ける。
        policy.EnsureFileWrite(Path.Combine(_dir, "report.html"));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null)
            {
                return false;
            }

            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void Evidence_lookup_matches_aliases_of_registered_files()
    {
        string file = Path.Combine(_dir, "ev.E01");
        File.WriteAllBytes(file, [0]);
        Assert.Equal(HexEditor.Core.Forensics.Cases.ForensicCase.SourceKey(HexEditor.Core.Forensics.Cases.EvidenceKind.ImageFile, file),
            HexEditor.Core.Forensics.Cases.ForensicCase.SourceKey(HexEditor.Core.Forensics.Cases.EvidenceKind.ImageFile, @"\\?\" + file));
    }

    // ---- 証拠のディスクとボリューム ----

    private static DeviceCatalog Catalog() => new()
    {
        Volumes =
        [
            new VolumeDeviceInfo { Path = @"\\?\Volume{11111111-1111-1111-1111-111111111111}", DriveLetter = "E:", DiskNumber = 2 },
            new VolumeDeviceInfo { Path = @"\\?\Volume{22222222-2222-2222-2222-222222222222}", DriveLetter = "F:", DiskNumber = 2 },
            new VolumeDeviceInfo { Path = @"\\.\C:", DriveLetter = "C:", DiskNumber = 0 },
            new VolumeDeviceInfo
            {
                Path = @"\\?\Volume{33333333-3333-3333-3333-333333333333}", DiskNumber = 5, MountPoints = [@"C:\mnt\evidence\"],
            },
        ],
    };

    [Fact]
    public void A_disk_that_holds_an_evidence_volume_is_treated_as_evidence()
    {
        Func<DeviceCatalog?> catalog = Catalog;
        Assert.True(EvidenceOverlap.Overlaps(@"\\.\E:", @"\\.\PhysicalDrive2", catalog));
        Assert.True(EvidenceOverlap.Overlaps(@"\\.\PhysicalDrive2", @"\\.\F:", catalog));
        Assert.True(EvidenceOverlap.Overlaps(@"\\.\E:", @"\\?\Volume{11111111-1111-1111-1111-111111111111}", catalog));
        Assert.True(EvidenceOverlap.Overlaps(@"\\.\physicaldrive2", @"\\.\PhysicalDrive2", catalog));

        // 同じディスクの別のボリューム、別のディスクは重ならない。
        Assert.False(EvidenceOverlap.Overlaps(@"\\.\E:", @"\\.\F:", catalog));
        Assert.False(EvidenceOverlap.Overlaps(@"\\.\E:", @"\\.\PhysicalDrive0", catalog));
        Assert.False(EvidenceOverlap.Overlaps(@"\\.\PhysicalDrive1", @"\\.\PhysicalDrive2", catalog));

        // ファイルとデバイスは比べない。ファイル同士は別名のパスでも同じ。
        Assert.False(EvidenceOverlap.Overlaps(@"\\.\E:", @"E:\image.dd", catalog));
        string file = Path.Combine(_dir, "a.dd");
        File.WriteAllBytes(file, [0]);
        Assert.True(EvidenceOverlap.Overlaps(file, @"\\?\" + file, catalog));
    }

    [Fact]
    public void The_output_check_uses_the_volume_mounted_at_a_folder()
    {
        var locator = new CatalogDiskLocator(Catalog);
        Assert.Equal([5], locator.DisksOfPath(@"C:\mnt\evidence\out"));
        Assert.Equal([0], locator.DisksOfPath(@"C:\mnt\other"));
        Assert.Equal([2], locator.DisksOfDevice(@"\\.\E:"));
        OutputCheck check = OutputGuard.Check(@"C:\mnt\evidence\out", [@"\\.\PhysicalDrive5"], 0, 0, locator);
        Assert.Equal(OutputProblem.SameDisk, check.Problem);

        // \\?\ の書き方でも同じディスクと分かる。
        Assert.Equal([5], locator.DisksOfPath(@"\\?\C:\mnt\evidence\out"));
    }

    // ---- 監査ログ ----

    [Fact]
    public void Truncating_the_end_of_the_audit_log_is_detected()
    {
        string path = Path.Combine(_dir, "audit.log");
        var identity = new AuditIdentity("tester", "PC", "1.0");
        AuditLog log = AuditLog.Open(path, identity);
        for (int i = 0; i < 5; i++)
        {
            log.Append(new AuditRecord(AuditOperations.DocumentOpen) { Message = "line " + i.ToString(CultureInfo.InvariantCulture) });
        }

        Assert.True(AuditVerifier.Verify(path).IsIntact);
        Assert.True(File.Exists(FileAuditStorage.HeadPath(path)));

        string[] lines = File.ReadAllLines(path);
        File.WriteAllText(path, string.Join('\n', lines[..3]) + "\n");
        AuditVerifyResult cut = AuditVerifier.Verify(path);
        Assert.False(cut.IsIntact);
        Assert.Equal(AuditBreakKind.Truncated, Assert.Single(cut.Breaks).Kind);
        Assert.Equal(4, cut.FirstBrokenLine);

        // 印が古いだけ (印の後に行が増えた) なら正常。
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        Assert.True(AuditLog.TryParse(lines[2], out AuditEntry? third));
        File.WriteAllText(FileAuditStorage.HeadPath(path), "3 " + third!.Hash + "\n");
        Assert.True(AuditVerifier.Verify(path).IsIntact);
    }

    // ---- CSV と書き出しの名前 ----

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", true)]
    [InlineData("+1+cmd", true)]
    [InlineData("@SUM(A1)", true)]
    [InlineData("-2+3+cmd|' /C calc'!A0", true)]
    [InlineData("-1", false)]
    [InlineData("-1.5", false)]
    [InlineData("\\dir\\file.txt", false)]
    public void Csv_fields_from_untrusted_names_cannot_start_a_formula(string value, bool prefixed)
    {
        string cell = AuditExporter.CsvEscape(value);
        Assert.Equal(prefixed, cell.TrimStart('"').StartsWith('\''));
    }

    [Theory]
    [InlineData("CONIN$")]
    [InlineData("conout$.txt")]
    [InlineData("COM\u00B9")]
    [InlineData("NUL .txt")]
    [InlineData("LPT0")]
    public void Export_names_avoid_reserved_device_names(string name) => Assert.StartsWith("_", FileExporter.SafeName(name));

    [Fact]
    public void Exports_and_manifests_are_refused_on_protected_paths_in_write_block_mode()
    {
        string target = Path.Combine(_dir, "evidence.csv");
        File.WriteAllText(target, "original");
        var policy = new WriteBlockPolicy { IsProtectedPath = p => string.Equals(p, WriteBlockPolicy.Normalize(target), StringComparison.OrdinalIgnoreCase) };
        policy.SetEnabled(true);
        using (WriteBlockGate.Use(policy))
        {
            Assert.Throws<WriteBlockedException>(() => FileExporter.WriteManifest(target, []));
            Assert.Throws<WriteBlockedException>(() => HexEditor.Core.Forensics.Images.RangesCsv.Write(target, []));
        }

        Assert.Equal("original", File.ReadAllText(target));
    }

    // ---- ファジングで見つけた解析の不具合 ----

    private static Dictionary<long, byte> Patches(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('='))
        .ToDictionary(p => long.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), p => byte.Parse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static bool Allowed(Exception ex) => ex is FileSystemOpenException or OperationCanceledException;

    [Fact]
    public void Udf_file_entry_with_an_oversized_extended_attribute_length_is_read_as_broken()
    {
        (SparseImage udf, _) = FileSystemImageSet.Udf250();
        var reader = new SparseImageReader(udf);
        Fuzz.Run([Patches("1080D0=FF 1080D1=FF 1080D2=FF 1080D3=FF")],
            p => FileSystemFuzzMoreTests.Exercise(new PatchedImageReader(reader, p), FileSystemKind.Udf), Allowed);
    }

    [Fact]
    public void Iso_root_record_at_the_end_of_the_volume_is_not_read_out_of_range()
    {
        (SparseImage iso, _) = FileSystemImageSet.IsoJrr();
        var reader = new SparseImageReader(iso);
        Fuzz.Run([Patches("D430=2E D431=B6 D432=24 D433=3F D434=07 D435=85 D436=CC D437=75 9438=00 D59D=FF 80A8=FF 80A9=FF 95AC=00 95AD=01 95AE=00 95AF=00")],
            p => FileSystemFuzzMoreTests.Exercise(new PatchedImageReader(reader, p)), Allowed);
    }

    [Fact]
    public void Hfs_short_overflow_records_and_unpaired_surrogates_in_names_do_not_throw()
    {
        (SparseImage hfs, _) = FileSystemImageSet.HfsPlus();
        var reader = new SparseImageReader(hfs);
        Fuzz.Run(
            [
                Patches("300E=02 300F=00 5A6=00 4B4=02 4B5=00 B5F0=00 B5F1=02 B5F2=00 B5F3=00"),
                Patches("442=FF 9BC4=FF 9BC5=FF 9BC6=FF 9BC7=FE 3680=FF 3681=FF 3682=00 3683=00 3684=00 3685=00 3686=00 3687=00 484=CD"),
                Patches("8FB0=FF 8FB1=FE 548=7D 549=17 54A=0A 54B=17 54C=D7 54D=07 54E=98 54F=12 34FC=D9 34FD=BD 34FE=D9 34FF=79"),
            ],
            p => FileSystemFuzzMoreTests.Exercise(new PatchedImageReader(reader, p)), Allowed);
        Assert.Equal("a\uD800b", FsText.NormalizeC("a\uD800b"));
        Assert.Empty(HfsFork.Parse(new byte[10]).Extents);
    }

    [Fact]
    public void Ntfs_log_file_with_a_huge_data_size_is_scanned_only_within_its_runs()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        var reader = new SparseImageReader(image.Image);
        Fuzz.Run(
            [
                // $LogFile の実サイズ。
                Patches("6C014=47 6C015=95 6E180=5B 6E181=04 6E182=5D 6E183=D0 6E184=8A 6E185=2A 6E186=46 6E187=51 6593C=FF 6593D=FF 6593E=FF 6593F=7F 654CC=FF 654CD=FF 65FC0=FB 65FC1=7C"),

                // $MFT の実サイズ (削除されたファイルの走査)。
                Patches("666D0=FD 666D1=FF 6DC30=00 6DC31=80 6DC32=00 6DC33=00 6DC34=00 6DC35=00 6DC36=00 6DC37=00 6B374=01 6B375=00 6B376=00 6B377=00 6E2C8=FE 6E2C9=FF 6570C=3F 6570D=FF 6570E=FF 6570F=FE"),

                // ルートの $INDEX_ALLOCATION の実サイズ (INDX の走査)。
                Patches("65A14=BF 65A15=D5 65A16=C6 65A17=A1 6B078=FE 6B079=FF 6B07A=FF 6B07B=7F 66584=64 66585=B7 66586=02 66587=74 3B9DE3=00"),

                // INDX のエントリのオフセットが負 (範囲外の読み込み)。
                Patches("148=FF 149=7F 14A=00 14B=00 6DD30=B2 6DD31=79 6DD32=FF 6DD33=9B"),
                Patches("6B1A9=FF 3BAB6C=03 3BAB6D=00 3BAB6E=00 3BAB6F=00 6EC3C=FF 6EC3D=FF 6EC3E=FF 6EC3F=7F"),

                // 属性の長さ (32 bit) と位置の和のあふれ。
                Patches("6DCC=02 6DCD=00 6DCE=00 6DCF=00 4250=00 4251=00 4252=00 4253=00 4254=00 4255=00 4256=00 4257=02 3B9C78=80 3B9C79=00 3BA018=47 3BA019=07"),
                Patches("3BA4F8=1A 3BA4F9=CD 3BA4FA=FB 3BA4FB=D9 6E034=7E A0=00 A1=00 A2=00 A3=00 A4=00 A5=00 A6=00 A7=7F 3BA01B=BD 6E3E8=00 6E3E9=00"),
            ],
            p =>
            {
                FileSystemFuzzTests.Exercise(new PatchedImageReader(reader, p));
                WipeFuzzTests.Exercise(new PatchedImageReader(reader, p));
            }, Allowed, timeoutMs: 20_000);
    }
}
