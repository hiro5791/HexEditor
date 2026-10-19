using System.Security.Cryptography;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Wiping;

/// <summary>FOR-32 ファイルシステムを理解した墨塗り (テスト用イメージのコピーを一時フォルダに作って開く)。</summary>
public sealed class RedactionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-redact").FullName;

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

    private Document OpenCopy(SparseImage image, string name)
    {
        string path = Path.Combine(_dir, name);
        image.Save(path);
        return new Document(DiskImage.Open(path, new DiskImageOptions()));
    }

    private static IFileSystem Open(Document doc) => FileSystemRegistry.Open(new SnapshotVolumeReader(doc.DataSnapshot), new FileSystemOpenOptions());

    private static IFileSystem OpenFile(string path) =>
        FileSystemRegistry.Open(new SnapshotVolumeReader(new Document(FileByteSource.Open(path)).DataSnapshot), new FileSystemOpenOptions());

    private static IEnumerable<FsEntry> AllFiles(IFileSystem fs, FsEntry dir, int depth = 0)
    {
        foreach (FsEntry e in fs.ListDirectory(dir))
        {
            if (e.Name.StartsWith('$') || e.Name is "." or "..")
            {
                continue;
            }

            if (e.IsDirectory && !e.Flags.HasFlag(FsEntryFlags.Stream))
            {
                if (depth < 4)
                {
                    foreach (FsEntry child in AllFiles(fs, e, depth + 1))
                    {
                        yield return child;
                    }
                }
            }
            else
            {
                yield return e;
            }
        }
    }

    private static Dictionary<string, string> Hashes(IFileSystem fs) =>
        AllFiles(fs, fs.Root).ToDictionary(e => e.Path, e => Convert.ToHexStringLower(SHA256.HashData(FsTestImages.ReadAll(fs.OpenContent(e)))));

    private static string Sha(Document doc)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        for (long at = 0; at < doc.Length; at += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, doc.Length - at);
            doc.Current.Read(at, buffer.AsSpan(0, n));
            sha.AppendData(buffer, 0, n);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    [Fact]
    [Trait("TC", "TC-FOR-32-01")]
    public void Redacting_a_file_and_saving_as_another_image_zeroes_only_that_file_and_logs_the_ranges()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "ntfs.img");
        IFileSystem fs = Open(doc);
        Dictionary<string, string> before = Hashes(fs);
        FsEntry photo = FsTestImages.Child(fs, fs.Root, "photo.png");
        var hooks = new RecordingWipeHooks();
        string log = Path.Combine(_dir, FileSystemRedaction.LogFileName);

        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [photo], ShredPass.Fill(0x00), replaceNames: false);
        RedactionItem item = Assert.Single(plan.Items);
        Assert.Empty(plan.Excluded);
        FileSystemRedaction.Run(doc, plan, "ntfs.img", log, DateTimeOffset.UtcNow, hooks);
        Assert.True(doc.IsModified);

        string redacted = Path.Combine(_dir, "redacted.img");
        DocumentSaver.Save(doc.Current, redacted);
        doc.Dispose();

        IFileSystem after = OpenFile(redacted);
        FsEntry photo2 = FsTestImages.Child(after, after.Root, "photo.png");
        Assert.Equal(photo.Size, photo2.Size);
        Assert.Equal(photo.Modified, photo2.Modified);
        Assert.Equal(photo.Created, photo2.Created);
        Assert.All(FsTestImages.ReadAll(after.OpenContent(photo2)), b => Assert.Equal(0, b));
        Dictionary<string, string> others = Hashes(after);
        foreach ((string path, string hash) in before.Where(kv => kv.Key != "\\photo.png"))
        {
            Assert.Equal(hash, others[path]);
        }

        // 墨塗りの記録 (パス・ファイル ID・データの断片とスラックの範囲) と監査ログ。
        string[] lines = File.ReadAllLines(log);
        Assert.StartsWith("TimeUtc,Image,Path,FileId,Kind,Offset,Length", lines[0], StringComparison.Ordinal);
        string[] rows = [.. lines.Skip(1).Where(l => l.Contains("\\photo.png", StringComparison.Ordinal))];
        // TD-FOR-NTFS の photo.png は小さいため MFT レコードに常駐する (上書きした範囲は MFT レコードの中の値)。
        RedactionRange content = Assert.Single(item.Ranges, r => r.Kind is RedactionRangeKind.Data or RedactionRangeKind.Resident);
        Assert.Equal(photo.Size, content.Length);
        Assert.Contains(rows, r => r.Contains($",{photo.FileIdText},{content.Kind.ToString().ToLowerInvariant()},{content.Offset},{content.Length},",
            StringComparison.Ordinal));
        Assert.Equal(item.Ranges.Count, rows.Length);

        // 常駐しないファイル (data.bin) では、データの断片とスラックが記録される。
        Document doc2 = OpenCopy(FileSystemImageSet.Ntfs().Image, "ntfs2.img");
        IFileSystem fs2 = Open(doc2);
        FsEntry frag = FsTestImages.Child(fs2, fs2.Root, "small100.bin");
        RedactionPlan plan2 = FileSystemRedaction.Plan(fs2, 0, new SnapshotVolumeReader(doc2.DataSnapshot), [frag], ShredPass.Fill(0x00), replaceNames: false);
        FileSystemRedaction.Run(doc2, plan2, "ntfs2.img", log, DateTimeOffset.UtcNow, hooks);
        string[] rows2 = [.. File.ReadAllLines(log).Where(l => l.Contains("\\small100.bin",StringComparison.Ordinal))];
        Assert.Contains(rows2, r => r.Contains(",data,", StringComparison.Ordinal) && r.Contains(",100,", StringComparison.Ordinal));
        Assert.Contains(rows2, r => r.Contains(",slack,", StringComparison.Ordinal));
        doc2.Dispose();
        hooks.Records.RemoveAt(1);
        var record = Assert.Single(hooks.Records);
        Assert.Equal(WipeAuditOperations.Redact, record.Operation);
        Assert.Equal(WipeAuditOutcome.Succeeded, record.Outcome);
        Assert.Contains("\\photo.png", record.Details["paths"], StringComparison.Ordinal);
        Assert.Equal(log, record.Details["log"]);
    }

    [Fact]
    [Trait("TC", "TC-FOR-32-02")]
    public void Undo_right_after_redaction_restores_the_document_without_writing_to_the_image()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "undo.img");
        string path = ((DiskImageByteSource)doc.Source).Path;
        DateTime written = File.GetLastWriteTimeUtc(path);
        string sha = Sha(doc);
        IFileSystem fs = Open(doc);
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [FsTestImages.Child(fs, fs.Root, "photo.png")],
            ShredPass.Fill(0x00), replaceNames: true);
        FileSystemRedaction.Run(doc, plan, "undo.img", null, DateTimeOffset.UtcNow, new RecordingWipeHooks());
        Assert.NotEqual(sha, Sha(doc));
        doc.Undo();
        Assert.Equal(sha, Sha(doc));
        Assert.False(doc.IsModified);
        doc.Dispose();
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Resident_and_compressed_files_and_names_are_redacted_and_the_volume_stays_readable()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "names.img");
        IFileSystem fs = Open(doc);
        Dictionary<string, string> before = Hashes(fs);
        string[] names = ["small100.bin", "compressed.bin", "readme.txt", "stomped.txt"];
        FsEntry[] entries = [.. names.Select(n => FsTestImages.Child(fs, fs.Root, n))];
        Assert.True(entries[3].Flags.HasFlag(FsEntryFlags.Resident) || fs.GetExtents(entries[3]).Any(x => x.Resident));
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), entries, ShredPass.Fill(0xAA), replaceNames: true);
        Assert.Equal(4, plan.Items.Count);
        Assert.Contains(plan.Items.Single(i => i.Path == "\\stomped.txt").Ranges, r => r.Kind == RedactionRangeKind.Resident);
        Assert.All(plan.Items, i => Assert.Empty(i.Warnings));
        FileSystemRedaction.Apply(doc, plan);

        IFileSystem after = Open(doc);
        Assert.DoesNotContain(after.ListDirectory(after.Root), e => e.Flags.HasFlag(FsEntryFlags.Corrupt));
        FsEntry[] listed = [.. after.ListDirectory(after.Root)];
        foreach (string name in names)
        {
            Assert.DoesNotContain(listed, e => e.Name == name);
            Assert.Contains(listed, e => e.Name == new string('_', name.Length));
        }

        FsEntry stomped = listed.First(e => e.Name == new string('_', "stomped.txt".Length) && e.Size == FileSet.Stomped.Length);
        Assert.All(FsTestImages.ReadAll(after.OpenContent(stomped)), b => Assert.Equal(0xAA, b));
        FsEntry readme = listed.First(e => e.Name == "__________" && e.Size == FileSet.Readme.Length);
        Assert.All(FsTestImages.ReadAll(after.OpenContent(readme)), b => Assert.Equal(0xAA, b));

        // 墨塗りしていないファイルは同じ。
        Dictionary<string, string> others = Hashes(after);
        foreach ((string path, string hash) in before.Where(kv => !names.Contains(kv.Key.TrimStart('\\'))))
        {
            Assert.Equal(hash, others[path]);
        }
    }

    [Theory]
    [InlineData("TD-FOR-FAT16")]
    [InlineData("TD-FOR-FAT32")]
    [InlineData("TD-FOR-EXFAT")]
    public void Fat_and_exfat_names_are_replaced_with_valid_checksums(string id)
    {
        SparseImage image = id == "TD-FOR-EXFAT" ? FileSystemImageSet.ExFat().Image : FileSystemImageSet.Fat(id).Image;
        Document doc = OpenCopy(image, id + ".img");
        IFileSystem fs = Open(doc);
        Dictionary<string, string> before = Hashes(fs);
        FsEntry longName = FsTestImages.Child(fs, fs.Root, FileSet.LongName);
        FsEntry photo = FsTestImages.Child(fs, fs.Root, "photo.png");
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [longName, photo], ShredPass.Fill(0x00), replaceNames: true);
        Assert.All(plan.Items, i => Assert.Empty(i.Warnings));
        FileSystemRedaction.Apply(doc, plan);

        IFileSystem after = Open(doc);
        FsEntry[] listed = [.. after.ListDirectory(after.Root)];
        Assert.DoesNotContain(listed, e => e.Flags.HasFlag(FsEntryFlags.Corrupt) || e.Flags.HasFlag(FsEntryFlags.LfnMismatch));
        FsEntry renamed = Assert.Single(listed, e => e.Name == new string('_', FileSet.LongName.Length));
        Assert.Equal(longName.Size, renamed.Size);
        Assert.Equal(longName.Modified, renamed.Modified);
        Assert.All(FsTestImages.ReadAll(after.OpenContent(renamed)), b => Assert.Equal(0, b));
        Assert.Single(listed, e => e.Name == "_________" && e.Size == photo.Size);
        Dictionary<string, string> others = Hashes(after);
        foreach ((string path, string hash) in before.Where(kv => kv.Key != "\\photo.png" && kv.Key != "\\" + FileSet.LongName))
        {
            Assert.Equal(hash, others[path]);
        }
    }

    [Fact]
    public void Directories_deleted_entries_and_metadata_are_excluded_with_a_reason()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "excl.img");
        IFileSystem fs = Open(doc);
        FsEntry dir = FsTestImages.Child(fs, fs.Root, "dir1");
        FsEntry deleted = fs.EnumerateDeleted().First(e => e.Name == "deleted.txt");
        FsEntry mft = fs.EnumerateMetadata().First(e => e.Name == "$MFT");
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [dir, deleted, mft], ShredPass.Fill(0x00), replaceNames: false);
        Assert.Empty(plan.Items);
        Assert.Equal([RedactionExclusionReason.Directory, RedactionExclusionReason.Deleted, RedactionExclusionReason.Metadata],
            plan.Excluded.Select(x => x.Reason));
        string log = Path.Combine(_dir, "excluded.csv");
        FileSystemRedaction.WriteLog(log, plan, "excl.img", DateTimeOffset.UtcNow);
        Assert.Equal(4, File.ReadAllLines(log).Length);
        Assert.Contains(File.ReadAllLines(log), l => l.Contains(",excluded,Deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_only_document_is_refused_and_the_refusal_is_audited()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "ro.img");
        doc.SetReadOnly(ReadOnlyReason.User);
        IFileSystem fs = Open(doc);
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [FsTestImages.Child(fs, fs.Root, "photo.png")],
            ShredPass.Fill(0x00), replaceNames: false);
        var hooks = new RecordingWipeHooks();
        Assert.Equal(WipeRefusal.WriteBlock, Assert.Throws<WipeRefusedException>(() => FileSystemRedaction.Run(doc, plan, "ro.img", null, DateTimeOffset.UtcNow, hooks)).Reason);
        Assert.False(doc.IsModified);
        Assert.Equal(WipeAuditOutcome.Refused, Assert.Single(hooks.Records).Outcome);
    }

    [Fact]
    public void Random_redaction_uses_fill_pieces_and_replaces_the_data()
    {
        Document doc = OpenCopy(FileSystemImageSet.Ntfs().Image, "random.img");
        IFileSystem fs = Open(doc);
        FsEntry data = FsTestImages.Child(fs, fs.Root, "data.bin");
        byte[] original = FsTestImages.ReadAll(fs.OpenContent(data));
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, new SnapshotVolumeReader(doc.DataSnapshot), [data], ShredPass.Random, replaceNames: false);
        FileSystemRedaction.Apply(doc, plan);
        byte[] after = FsTestImages.ReadAll(Open(doc).OpenContent(FsTestImages.Child(Open(doc), Open(doc).Root, "data.bin")));
        Assert.Equal(original.Length, after.Length);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(after)));
    }
}
