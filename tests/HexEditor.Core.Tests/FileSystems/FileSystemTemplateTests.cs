using HexEditor.Core.FileSystems;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>「MFT レコードを表示」「INDX を表示」「ディレクトリエントリを表示」のテンプレート (FOR-10・FOR-11 の画面)。</summary>
public sealed class FileSystemTemplateTests
{
    private static string Apply(string source, SparseImage image, long offset, int length)
    {
        TemplateProgram program = TemplateLoader.LoadText(source, "fs.hxt");
        Assert.DoesNotContain(program.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        byte[] data = image.Read(offset, length);
        TemplateResult result = TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromBytes(data, "fs.bin"));
        Assert.Equal(TemplateRunState.Completed, result.State);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        return TemplateJsonExporter.ToJson(result);
    }

    [Fact]
    public void Mft_record_template_shows_the_attributes()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        long record = image.Builder.RecordOffset(image.Find("\\readme.txt").Number);
        string json = Apply(FileSystemTemplates.NtfsRecord(1024), image.Image, record, 1024);
        Assert.Contains("readme.txt", json, StringComparison.Ordinal);
        Assert.Contains("\"attributes\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_record_template_shows_the_entries()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        var fs = FsTestImages.Open("TD-FOR-NTFS");
        long block = fs.GetExtents(FsTestImages.Child(fs, fs.Root, "dir1"))[0].VolumeOffset!.Value;
        string json = Apply(FileSystemTemplates.NtfsIndex(4096), image.Image, block, 4096);
        Assert.Contains("f001.txt", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_entry_templates_show_fat_and_exfat_entries()
    {
        (SparseImage fat, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT16");
        string json = Apply(FileSystemTemplates.FatDirectoryEntries(4), fat, b.Files["readme.txt"].EntryOffset - 32, 128);
        Assert.Contains("\"attributes\"", json, StringComparison.Ordinal);
        (SparseImage exfat, ExFatImageBuilder e) = FileSystemImageSet.ExFat();
        string exjson = Apply(FileSystemTemplates.ExFatEntries(3), exfat, e.Files["contig.bin"].EntryOffset, 96);
        Assert.Contains("contig.bin", exjson, StringComparison.Ordinal);
    }

    [Fact]
    public void Ext_inode_template_shows_the_extent_header_and_times()
    {
        (SparseImage image, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        long offset = b.InodeOffset(b.Nodes.First(n => n.Name == "readme.txt").Inode);
        string json = Apply(FileSystemTemplates.ExtInode(256), image, offset, 256);
        Assert.Contains("extent_header", json, StringComparison.Ordinal);
        Assert.Contains("crtime_extra", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateLoader.LoadText(FileSystemTemplates.ExtInode(128), "inode128.hxt").Diagnostics, d => d.Severity == TemplateSeverity.Error);
    }
}
