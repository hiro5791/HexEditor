using System.Diagnostics;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Clipboard;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>IPS / IPS32 のパッチの形式 (TOOL-12)。</summary>
public sealed class IpsPatchTests
{
    private static byte[] Sequence(int length)
    {
        byte[] data = new byte[length];
        TestDataCatalog.Sequence(0, data);
        return data;
    }

    private static byte[] Create(Document doc, IpsFormat format = IpsFormat.Auto, bool rle = true)
    {
        IByteSource original = doc.Source;
        ByteReader readOriginal = (o, d) => original.Read(o, d);
        List<(long, long)> changes = IpsPatch.ChangedRanges(doc.Current, readOriginal, original.Length);
        using var output = new MemoryStream();
        IpsPatch.Write(output, changes, (o, d) => doc.Current.Read(o, d), doc.Length, original.Length, format, rle);
        return output.ToArray();
    }

    private static byte[] Apply(byte[] target, byte[] patch) => IpsPatch.Apply(target, IpsPatch.Read(new MemoryStream(patch)));

    [Fact]
    public void Patch_reproduces_edits_with_rle_and_truncation()
    {
        byte[] original = Sequence(1 << 20);
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        doc.Overwrite(0x10, [0x48, 0x45, 0x58]);
        doc.OverwritePattern(0x1000, 0x100, [0xAA]);
        doc.Delete(0xFFF00, doc.Length - 0xFFF00);
        byte[] patch = Create(doc);
        Assert.Equal(ReadAll(doc.Current), Apply(original, patch));
        IpsPatchData data = IpsPatch.Read(new MemoryStream(patch));
        Assert.Contains(data.Records, r => r.IsRle);
        Assert.Equal(0xFFF00, data.TruncateTo);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-12-02")]
    public void Changes_at_the_eof_offset_start_one_byte_earlier()
    {
        byte[] original = new byte[8 << 20];
        TestDataCatalog.Sequence(0, original);
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        doc.Overwrite(0x454F46, [0x11, 0x22, 0x33, 0x44]);
        byte[] patch = Create(doc);
        IpsPatchData data = IpsPatch.Read(new MemoryStream(patch));
        Assert.DoesNotContain(data.Records, r => r.Offset == 0x454F46);
        IpsRecord record = Assert.Single(data.Records, r => r.Offset == 0x454F45);
        Assert.Equal(0x45, record.Data![0]);
        Assert.Equal(ReadAll(doc.Current), Apply(original, patch));
        if (CopyFormatCompileTests.FindTool("flips") is not null)
        {
            Assert.Equal(ReadAll(doc.Current), ApplyWithFlips(original, patch));
        }
    }

    [Fact]
    [Trait(TC, "TC-TOOL-12-02")]
    public void Rle_run_at_the_eof_offset_is_split()
    {
        byte[] original = new byte[8 << 20];
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        doc.OverwritePattern(0x454F46, 32, [0x77]);
        byte[] patch = Create(doc);
        Assert.DoesNotContain(IpsPatch.Read(new MemoryStream(patch)).Records, r => r.Offset == 0x454F46);
        Assert.Equal(ReadAll(doc.Current), Apply(original, patch));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-12-03")]
    public void Shortened_content_gets_a_truncation_length()
    {
        byte[] original = Sequence(1 << 20);
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        doc.Delete(0xFFF00, 0x100);
        doc.Overwrite(0x20, [0x99]);
        byte[] patch = Create(doc);
        Assert.Equal(new byte[] { 0x45, 0x4F, 0x46, 0x0F, 0xFF, 0x00 }, patch[^6..]);
        byte[] applied = Apply(original, patch);
        Assert.Equal(0xFFF00, applied.Length);
        Assert.Equal(ReadAll(doc.Current), applied);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-12-04")]
    public void Changes_beyond_16_mib_use_ips32()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-TOOL-ZERO-32M")), Options());
        doc.Overwrite(0x1400000, [0xFF]);
        byte[] patch = Create(doc);
        Assert.Equal("IPS32"u8.ToArray(), patch[..5]);
        Assert.Equal("EEOF"u8.ToArray(), patch[^4..]);
        Assert.Equal(new byte[] { 0x01, 0x40, 0x00, 0x00 }, patch[5..9]);
        Assert.Throws<IpsLimitException>(() => Create(doc, IpsFormat.Ips));
    }

    [Fact]
    public void Long_changes_are_split_and_nearby_changes_merged()
    {
        byte[] original = new byte[200_000];
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        byte[] noise = new byte[150_000];
        new Random(5).NextBytes(noise);
        doc.Overwrite(0, noise);
        doc.Overwrite(150_003, [1]);
        byte[] patch = Create(doc, rle: false);
        IpsPatchData data = IpsPatch.Read(new MemoryStream(patch));
        Assert.All(data.Records, r => Assert.True(r.Length <= IpsPatch.MaxRecord));
        Assert.Equal(ReadAll(doc.Current), Apply(original, patch));
    }

    [Fact]
    public void Broken_patches_report_the_record()
    {
        Assert.Equal("header", Assert.Throws<IpsFormatException>(() => IpsPatch.Read(new MemoryStream("PATCX"u8.ToArray()))).Reason);
        byte[] truncated = [.. "PATCH"u8, 0, 0, 0x10, 0, 4, 1, 2];
        IpsFormatException ex = Assert.Throws<IpsFormatException>(() => IpsPatch.Read(new MemoryStream(truncated)));
        Assert.Equal((0, 5L), (ex.Record, ex.FileOffset));
        byte[] noEnd = [.. "PATCH"u8, 0, 0, 0x10, 0, 1, 7];
        Assert.Throws<IpsFormatException>(() => IpsPatch.Read(new MemoryStream(noEnd)));
    }

    [Fact]
    public void Applying_to_a_document_is_one_undo_step()
    {
        byte[] original = Sequence(4096);
        using var target = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        byte[] patch = [.. "PATCH"u8, 0, 0, 0x10, 0, 2, 0xAA, 0xBB, 0, 0x10, 0x00, 0, 0, 0, 16, 0x55, .. "EOF"u8];
        IpsPatch.ApplyTo(target, IpsPatch.Read(new MemoryStream(patch)));
        Assert.Equal(0x1010, target.Length);
        target.Undo();
        Assert.Equal(original, ReadAll(target.Current));
    }

    /// <summary>Flips (コマンドライン版) で当てる (TC-TOOL-12-01・02 の「環境」。PATH にあるときだけ)。</summary>
    private static byte[] ApplyWithFlips(byte[] original, byte[] patch)
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "flips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "in.bin"), original);
        File.WriteAllBytes(Path.Combine(dir, "p.ips"), patch);
        var info = new ProcessStartInfo(CopyFormatCompileTests.FindTool("flips")!, "--apply p.ips in.bin out.bin")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(info)!;
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)));
        return File.ReadAllBytes(Path.Combine(dir, "out.bin"));
    }

    [RequiresToolFact("flips")]
    [Trait("Category", "Integration")]
    [Trait(TC, "TC-TOOL-12-01")]
    public void Flips_applies_the_created_patch()
    {
        byte[] original = Sequence(1 << 20);
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        doc.Overwrite(0x10, [0x48, 0x45, 0x58]);
        doc.OverwritePattern(0x1000, 0x100, [0xAA]);
        doc.Delete(0xFFF00, doc.Length - 0xFFF00);
        byte[] patch = Create(doc);
        Assert.Contains(IpsPatch.Read(new MemoryStream(patch)).Records, r => r.IsRle);
        Assert.Equal(ReadAll(doc.Current), ApplyWithFlips(original, patch));
    }
}
