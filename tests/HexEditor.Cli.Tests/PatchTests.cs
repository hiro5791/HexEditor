using System.Security.Cryptography;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>パッチの適用 (AUTO-43)。</summary>
public sealed class PatchTests
{
    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    [Trait("TC", "TC-AUTO-43-01")]
    public void Output_of_an_ips_patch_equals_applying_the_same_patch_in_the_gui_and_saving()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        string ips = TestDataCatalog.Get("TD-AUTO-IPS");
        string before = Sha256(seq);

        CliResult cli = Hexed.Run(dir.Path, "patch", seq, ips, "-o", "out-cli.bin");
        Assert.True(cli.ExitCode == 0, cli.Err);
        Assert.Equal(before, Sha256(seq));

        // GUI の「データ > パッチ」と同じ部品 (IpsPatch の読み込みと適用 = 1 つの編集グループ、名前を付けて保存) で作ったファイル。
        string gui = dir.File("out-gui.bin");
        using (var document = new Document(FileByteSource.Open(dir.Copy("TD-SEQ-1M", "copy.bin")), new DocumentOptions { TempDirectory = dir.File("recovery") }))
        {
            using FileStream stream = File.OpenRead(ips);
            IpsPatch.ApplyTo(document, IpsPatch.Read(stream));
            using FileByteSource saved = DocumentSaver.Save(document.Current, gui);
        }

        byte[] expected = File.ReadAllBytes(gui);
        byte[] actual = File.ReadAllBytes(dir.File("out-cli.bin"));
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(0xFFF00, actual.Length);
        Assert.Equal(Sha256(gui), Sha256(dir.File("out-cli.bin")));

        // RLE と通常のレコードが反映されている。
        Assert.Equal([0x48, 0x45, 0x58], actual[0x10..0x13]);
        Assert.All(actual[0x1000..0x1100], b => Assert.Equal(0xAA, b));
        Assert.Equal([0x00, 0xFF], actual[0x80000..0x80002]);
        Assert.Empty(Directory.GetFiles(dir.Path, ".*"));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-43-02")]
    public void Without_output_or_in_place_the_exit_code_is_2_and_the_target_is_unchanged()
    {
        using var dir = new TempDir();
        string target = dir.Copy("TD-SEQ-1M", "t.bin");
        string before = Sha256(target);

        CliResult result = Hexed.Run(dir.Path, "patch", "t.bin", TestDataCatalog.Get("TD-AUTO-IPS"), "--lang", "en");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("-o", result.Err);
        Assert.Contains("--in-place", result.Err);
        Assert.Equal(before, Sha256(target));

        CliResult both = Hexed.Run(dir.Path, "patch", "t.bin", TestDataCatalog.Get("TD-AUTO-IPS"), "-o", "x.bin", "--in-place");
        Assert.Equal(2, both.ExitCode);
        Assert.Equal(before, Sha256(target));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-43-03")]
    public void A_verify_before_mismatch_writes_no_output_and_exits_with_1()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        string ips = TestDataCatalog.Get("TD-AUTO-IPS");

        CliResult mismatch = Hexed.Run(dir.Path, "patch", seq, ips, "-o", "out.bin", "--verify-before", "sha256:" + new string('0', 64), "--lang", "en");
        Assert.Equal(1, mismatch.ExitCode);
        Assert.False(File.Exists(dir.File("out.bin")));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));

        CliResult ok = Hexed.Run(dir.Path, "patch", seq, ips, "-o", "out.bin", "--verify-before", "sha256:" + Sha256(seq));
        Assert.Equal(0, ok.ExitCode);
        Assert.True(File.Exists(dir.File("out.bin")));
    }

    [Fact]
    public void In_place_replaces_the_target_dry_run_changes_nothing_and_verify_after_is_checked()
    {
        using var dir = new TempDir();
        string target = dir.Copy("TD-SEQ-1M", "t.bin");
        string ips = TestDataCatalog.Get("TD-AUTO-IPS");
        string original = Sha256(target);

        CliResult dry = Hexed.Run(dir.Path, "patch", "t.bin", ips, "--dry-run", "--lang", "en");
        Assert.Equal(0, dry.ExitCode);
        Assert.Contains("3 record(s), 261 byte(s) would change", dry.Out);
        Assert.Contains("new length 1048320", dry.Out);
        Assert.Equal(original, Sha256(target));

        CliResult wrongAfter = Hexed.Run(dir.Path, "patch", "t.bin", ips, "--in-place", "--verify-after", "sha256:" + new string('1', 64));
        Assert.Equal(1, wrongAfter.ExitCode);
        Assert.Equal(original, Sha256(target));

        CliResult inPlace = Hexed.Run(dir.Path, "patch", "t.bin", ips, "--in-place", "-q");
        Assert.Equal(0, inPlace.ExitCode);
        Assert.Equal(string.Empty, inPlace.Err);
        Assert.Equal(0xFFF00, new FileInfo(target).Length);
        Assert.Equal(Sha256(target), Convert.ToHexStringLower(SHA256.HashData(IpsPatch.Apply(File.ReadAllBytes(TestDataCatalog.Get("TD-SEQ-1M")), ReadPatch(ips)))));
        Assert.Empty(Directory.GetFiles(dir.Path, ".*"));
    }

    private static IpsPatchData ReadPatch(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return IpsPatch.Read(stream);
    }

    [Fact]
    public void Broken_and_unsupported_patches_are_reported()
    {
        using var dir = new TempDir();
        string target = dir.Copy("TD-BYTES-256", "t.bin");
        byte[] ips = File.ReadAllBytes(TestDataCatalog.Get("TD-AUTO-IPS"));

        File.WriteAllBytes(dir.File("broken.ips"), ips[..14]);
        CliResult broken = Hexed.Run(dir.Path, "patch", "t.bin", "broken.ips", "-o", "o.bin", "--lang", "en");
        Assert.Equal(8, broken.ExitCode);
        Assert.Contains("record", broken.Err);
        Assert.False(File.Exists(dir.File("o.bin")));

        File.WriteAllBytes(dir.File("x.bps"), "BPS1xxxx"u8.ToArray());
        CliResult bps = Hexed.Run(dir.Path, "patch", "t.bin", "x.bps", "-o", "o.bin", "--lang", "en");
        Assert.Equal(8, bps.ExitCode);
        Assert.Contains("broken", bps.Err);

        File.WriteAllBytes(dir.File("junk.bin"), "junkjunk"u8.ToArray());
        Assert.Equal(8, Hexed.Run(dir.Path, "patch", "t.bin", "junk.bin", "-o", "o.bin").ExitCode);
        Assert.Equal(2, Hexed.Run(dir.Path, "patch", "t.bin", "broken.ips", "-o", "t.bin").ExitCode);
        Assert.Equal(8, Hexed.Run(dir.Path, "patch", "t.bin", "broken.ips", "-o", "o.bin", "--format", "ups").ExitCode);
        Assert.Equal(3, Hexed.Run(dir.Path, "patch", "none.bin", "broken.ips", "-o", "o.bin").ExitCode);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(TestDataCatalog.Get("TD-BYTES-256")))), Sha256(target));
    }

    [Fact]
    public void Ups_bps_and_hexpatch_are_applied_and_checked_against_the_original()
    {
        using var dir = new TempDir();
        string src = TestDataCatalog.Get("TD-EDIT-PATCH-SRC");
        string bps = TestDataCatalog.Get("TD-EDIT-PATCH-BPS");
        CliResult ok = Hexed.Run(dir.Path, "patch", src, bps, "-o", "out.bin");
        Assert.True(ok.ExitCode == 0, ok.Err);
        Assert.Equal(Sha256(TestDataCatalog.Get("TD-EDIT-PATCH-INS100")), Sha256(dir.File("out.bin")));

        // 別のファイルには当てない (変換前の CRC-32 が合わない)。
        CliResult other = Hexed.Run(dir.Path, "patch", TestDataCatalog.Get("TD-EDIT-PATCH-OTHER"), bps, "-o", "other.bin", "--lang", "en");
        Assert.Equal(1, other.ExitCode);
        Assert.Contains("different file", other.Err);
        Assert.False(File.Exists(dir.File("other.bin")));

        // .hexpatch と UPS (Core で作ったもの)。
        foreach (HexEditor.Core.Patches.PatchFormat format in new[] { HexEditor.Core.Patches.PatchFormat.HexPatch, HexEditor.Core.Patches.PatchFormat.Ups })
        {
            string patch = dir.File("p." + format);
            using (var document = new Document(FileByteSource.Open(dir.Copy("TD-EDIT-PATCH-SRC", "c-" + format + ".bin")), new DocumentOptions { TempDirectory = dir.File("recovery") }))
            {
                document.Overwrite(0x100, [1, 2, 3]);
                document.Delete(0x9000, 10);
                using FileStream output = File.Create(patch);
                new HexEditor.Core.Patches.PatchCreator(HexEditor.Core.Patches.PatchDiff.FromSaved(document.DataSnapshot)).Write(format, output);
                using FileByteSource saved = DocumentSaver.Save(document.Current, dir.File("expected-" + format + ".bin"));
            }

            CliResult applied = Hexed.Run(dir.Path, "patch", src, patch, "-o", "o-" + format + ".bin", "--format", format == HexEditor.Core.Patches.PatchFormat.Ups ? "ups" : "hexpatch");
            Assert.True(applied.ExitCode == 0, applied.Err);
            Assert.Equal(Sha256(dir.File("expected-" + format + ".bin")), Sha256(dir.File("o-" + format + ".bin")));
        }
    }
}
