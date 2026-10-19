using System.Text;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Iso;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-14 ISO 9660 / UDF の解析。イメージはテストデータの生成ツール (IsoImageBuilder・UdfImageBuilder) がコードで書いたもの。</summary>
public sealed class OpticalFileSystemTests
{
    private static Iso9660FileSystem OpenIso() => (Iso9660FileSystem)FsTestImages.Open("TD-FOR-ISO-JRR");

    [Fact]
    public void Joliet_is_the_default_and_names_switch_to_rock_ridge_and_basic()
    {
        Iso9660FileSystem fs = OpenIso();
        Assert.Equal(FileSystemKind.Iso9660, fs.Kind);
        Assert.True(fs.HasJoliet);
        Assert.True(fs.HasRockRidge);
        Assert.Equal(IsoNameKind.Joliet, fs.NameKind);
        string[] joliet = [.. fs.ListDirectory(fs.Root).Select(e => e.Name)];
        Assert.Contains("日本語の名前.txt", joliet);
        Assert.Contains("MixedCase_Long_Name.txt", joliet);
        Assert.Contains("colon_name.txt", joliet);

        FsChoice choice = Assert.Single(fs.Choices, c => c.Key == "NameKind");
        Assert.Equal(["Joliet", "RockRidge", "Basic"], choice.Options);
        Assert.Null(fs.Select("NameKind", 1));
        FsEntry[] rr = [.. fs.ListDirectory(fs.Root)];
        Assert.Contains(rr, e => e.Name == "colon:name.txt");
        Assert.Contains(rr, e => e.Name == "日本語の名前.txt");
        Assert.All(rr, e => Assert.Equal("-rw-r--r--", e.Attributes));

        fs.Select("NameKind", 2);
        string[] basic = [.. fs.ListDirectory(fs.Root).Select(e => e.Name)];
        Assert.Contains("MIXEDCAS.TXT;1", basic);
        Assert.Contains("COLON_NA.TXT;1", basic);
    }

    [Fact]
    public void File_contents_and_times_match_in_every_name_kind()
    {
        Iso9660FileSystem fs = OpenIso();
        foreach (IsoNameKind kind in fs.NameKinds)
        {
            fs.SelectNameKind(kind);
            FsEntry[] list = [.. fs.ListDirectory(fs.Root)];
            Assert.Equal(FileSystemImageSet.IsoFiles.Count, list.Length);
            foreach (FsEntry e in list)
            {
                byte[] content = FsTestImages.ReadAll(fs.OpenContent(e));
                FileSetItem item = Assert.Single(FileSystemImageSet.IsoFiles, i => i.Content.SequenceEqual(content));
                Assert.Equal(item.ModifiedUtc, e.Modified.Utc);
            }
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-14-02")]
    public void El_torito_boot_entry_is_shown_and_the_boot_image_can_be_exported()
    {
        Iso9660FileSystem fs = OpenIso();
        IsoBootEntry boot = Assert.Single(fs.BootEntries);
        Assert.Equal("NoEmulation", boot.EmulationName);
        Assert.Equal(4, boot.SectorCount);
        Assert.Equal("x86", boot.PlatformName);
        Assert.True(boot.Bootable);
        (_, IsoImageBuilder b) = FileSystemImageSet.IsoJrr();
        Assert.Equal(b.BootImage!.Extent, boot.LoadRba);
        Assert.Contains(fs.Summary.Details, d => d.Key == "BootEntry1" && d.Value.Contains("NoEmulation", StringComparison.Ordinal));

        // ブートイメージは「$メタデータ」の項目として書き出せる。
        FsEntry image = Assert.Single(fs.EnumerateMetadata(), m => m.Name == "$BootImage1");
        Assert.Contains(fs.Describe(image), d => d.Key == "BootEmulation" && d.Value == "NoEmulation");
        Assert.Contains(fs.Describe(image), d => d.Key == "BootSectorCount" && d.Value == "4");
        string folder = Path.Combine(Path.GetTempPath(), "HexEditor", "tests", "eltorito-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<FileExportRow> rows = FileExporter.Export(fs, [image], folder, new FileExportOptions());
            FileExportRow row = Assert.Single(rows);
            byte[] written = File.ReadAllBytes(row.Destination);
            Assert.Equal(2048, written.Length);
            Assert.Equal(FsTestImages.Sha256(FileSystemImageSet.IsoBootImage), FsTestImages.Sha256(written));
            Assert.Equal(FsTestImages.Sha256(FileSystemImageSet.IsoBootImage), row.Sha256, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal(FileSystemImageSet.IsoBootImage, FsTestImages.ReadAll(fs.OpenBootImage(boot)));
    }

    [Fact]
    public void Multisession_uses_the_last_session_and_can_switch()
    {
        var image = new SparseImage(1200L * 2048);
        var first = new IsoImageBuilder("SESSION1", FileSet.ReadmeModifiedUtc) { Joliet = false, RockRidge = false };
        first.AddFile(first.Root, "one.txt", Encoding.ASCII.GetBytes("first"), FileSet.TimeOf(1));
        first.Build(image, totalSectors: 300);
        var second = new IsoImageBuilder("SESSION2", FileSet.ReadmeModifiedUtc, startSector: 300 + 150);
        second.AddFile(second.Root, "one.txt", Encoding.ASCII.GetBytes("first"), FileSet.TimeOf(1));
        second.AddFile(second.Root, "two.txt", Encoding.ASCII.GetBytes("second"), FileSet.TimeOf(2));
        FsEntry[] Names(Iso9660FileSystem fs) => [.. fs.ListDirectory(fs.Root)];
        second.Build(image, totalSectors: 400);

        var iso = (Iso9660FileSystem)FileSystemRegistry.Open(new SparseImageReader(image));
        Assert.Equal(2, iso.Sessions.Count);
        Assert.Equal(1, iso.SessionIndex);
        Assert.Contains(iso.Issues, i => i.Kind == FsIssueKind.IsoMultiSession);
        Assert.Equal(["one.txt", "two.txt"], Names(iso).Select(e => e.Name).Order());
        Assert.Equal("second"u8.ToArray(), FsTestImages.ReadAll(iso.OpenContent(Names(iso).First(e => e.Name == "two.txt"))));
        Assert.Contains(iso.Choices, c => c.Key == "Session" && c.Selected == 1);
        iso.Select("Session", 0);
        Assert.Equal(["ONE.TXT;1"], Names(iso).Select(e => e.Name));
        Assert.Equal("SESSION1", iso.Summary.Label);
    }

    [Fact]
    public void Nested_directories_are_listed()
    {
        var b = new IsoImageBuilder("NESTED", FileSet.ReadmeModifiedUtc);
        IsoImageBuilder.Node sub = b.AddDirectory(b.Root, "Sub Folder", FileSet.TimeOf(1));
        b.AddFile(sub, "inner.txt", Encoding.ASCII.GetBytes("inner"), FileSet.TimeOf(2));
        var fs = (Iso9660FileSystem)FileSystemRegistry.Open(new SparseImageReader(b.Build()));
        FsEntry folder = FsTestImages.Child(fs, fs.Root, "Sub Folder");
        Assert.True(folder.IsDirectory);
        FsEntry inner = FsTestImages.Child(fs, folder, "inner.txt");
        Assert.Equal("\\Sub Folder\\inner.txt", inner.Path);
        Assert.Equal("inner"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(inner)));
        Assert.Contains(fs.EnumerateAll(), e => e.Path == "\\Sub Folder\\inner.txt");
        Assert.Null(fs.Allocation);
        Assert.Empty(fs.EnumerateDeleted());
    }

    [Fact]
    [Trait("TC", "TC-FOR-14-03")]
    public void Udf_250_lists_files_and_contents()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-UDF250");
        var udf = Assert.IsType<UdfFileSystem>(fs);
        Assert.Equal(FileSystemKind.Udf, fs.Kind);
        Assert.Equal("UDF 2.50", fs.Summary.TypeName);
        Assert.Equal("2.50", udf.RevisionText);
        Assert.True(udf.HasMetadataPartition);
        Assert.Equal("UDFTEST", fs.Summary.Label);
        Assert.Empty(fs.Issues);
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        Assert.Equal(FileSystemImageSet.UdfFiles.Select(i => i.Name), root.Select(e => e.Name));
        foreach (FileSetItem item in FileSystemImageSet.UdfFiles)
        {
            FsEntry e = root.Single(r => r.Name == item.Name);
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(FsTestImages.Sha256(item.Content), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(e))));
            // UDF の日時はマイクロ秒まで。
            Assert.Equal(item.ModifiedUtc.AddTicks(-(item.ModifiedUtc.Ticks % 10)), e.Modified.Utc);
            Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
        }
    }

    [Fact]
    public void Udf_space_bitmap_marks_file_data_allocated()
    {
        var fs = (UdfFileSystem)FsTestImages.Open("TD-FOR-UDF250");
        IAllocationMap map = fs.Allocation!;
        FsEntry data = FsTestImages.Child(fs, fs.Root, "data.bin");
        FsExtent extent = Assert.Single(fs.GetExtents(data));
        Assert.True(map.IsAllocated(extent.StartCluster));
        Assert.True(map.IsAllocated(extent.StartCluster + extent.ClusterCount - 1));
        Assert.False(map.IsAllocated(extent.StartCluster + extent.ClusterCount + 10));
        Assert.True(map.IsAllocated(256));
    }

    [Fact]
    public void Udf_embedded_data_subdirectories_and_bridge_choice()
    {
        var b = new UdfImageBuilder(8 * FileSystemImageSet.MiB, "BRIDGE", FileSet.ReadmeModifiedUtc);
        UdfImageBuilder.Node dir = b.AddDirectory(b.Root, "フォルダ", FileSet.TimeOf(1));
        b.AddFile(dir, "tiny.txt", "embedded!"u8.ToArray(), FileSet.TimeOf(2), embedded: true);
        SparseImage image = b.Build();

        // 同じイメージに ISO 9660 の記述子を足す (ブリッジ形式)。
        var iso = new IsoImageBuilder("BRIDGE", FileSet.ReadmeModifiedUtc) { Joliet = false, RockRidge = false };
        iso.AddFile(iso.Root, "x.txt", "x"u8.ToArray(), FileSet.TimeOf(3));
        SparseImage isoImage = iso.Build(totalSectors: 40);
        image.Write(16L * 2048, isoImage.Read(16L * 2048, 2048));

        var fs = (UdfFileSystem)FileSystemRegistry.Open(new SparseImageReader(image));
        FsEntry folder = FsTestImages.Child(fs, fs.Root, "フォルダ");
        FsEntry tiny = FsTestImages.Child(fs, folder, "tiny.txt");
        Assert.True(tiny.Flags.HasFlag(FsEntryFlags.Resident));
        Assert.Equal("embedded!"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(tiny)));
        FsChoice choice = Assert.Single(fs.Choices);
        Assert.Equal("Format", choice.Key);
        Assert.Equal(0, choice.Selected);
        Assert.Equal(FileSystemKind.Iso9660, fs.Select("Format", 1));
    }

    [Fact]
    public void Udf_tag_checksum_and_crc_errors_are_warnings()
    {
        (SparseImage image, UdfImageBuilder b) = FileSystemImageSet.Udf250();
        FsEntry readme = FsTestImages.Child(FsTestImages.Open("TD-FOR-UDF250"), FsTestImages.Open("TD-FOR-UDF250").Root, "readme.txt");
        long fe = readme.MetadataOffset!.Value;

        // readme.txt の EFE の CRC の範囲の 1 バイトと、PVD のタグのチェックサムを変える。
        var patches = new Dictionary<long, byte> { [fe + 60] = 0x55, [32L * 2048 + 4] = 0x00 };
        IFileSystem fs = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), patches));
        Assert.Equal(3, fs.ListDirectory(fs.Root).Count());
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.UdfDescriptorCrc);
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.UdfTagChecksum);
        _ = b;
    }
}
