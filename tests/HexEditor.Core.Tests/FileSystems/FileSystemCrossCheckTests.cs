using System.Text.Json;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Apple;
using HexEditor.Core.FileSystems.Iso;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>
/// 実際の道具で作ったイメージのフォルダ (環境変数 HEXEDITOR_FS_CROSSCHECK) がないときはスキップする Fact。フォルダは CI のジョブ
/// fs-crosscheck-images (ubuntu。mke2fs・xorriso・mkudffs・lvm2・mkfs.hfsplus。build/tests/make-fs-crosscheck.sh) が作る。開発者の PC では作らない。
/// </summary>
public sealed class FsCrossCheckFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_FS_CROSSCHECK";

    public FsCrossCheckFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } dir || !Directory.Exists(dir))
        {
            Skip = $"{Variable} がない (CI のジョブ fs-crosscheck だけで動く)。";
        }
    }
}

/// <summary>
/// 生成ツールのイメージとは別に、Linux の実際の道具で作ったイメージを読み、元のファイルのマニフェスト (パス・サイズ・更新日時・SHA-256) と比べる
/// (FOR-07・FOR-12〜FOR-14。生成ツールと解析が同じ思い違いをしていないことを確かめる)。
/// </summary>
public sealed class FileSystemCrossCheckTests
{
    private sealed record Item(string Path, long Size, long Mtime, string Sha256);

    private static string Folder => Environment.GetEnvironmentVariable(FsCrossCheckFactAttribute.Variable)!;

    private static IReadOnlyList<Item> Manifest()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")));
        return [.. doc.RootElement.EnumerateArray().Select(e => new Item(e.GetProperty("path").GetString()!, e.GetProperty("size").GetInt64(),
            e.GetProperty("mtime").GetInt64(), e.GetProperty("sha256").GetString()!))];
    }

    private static IVolumeReader Image(string name) => new ByteSourceVolumeReader(FileByteSource.Open(Path.Combine(Folder, name)));

    /// <summary>ディレクトリの木の全部のファイル (パスは <c>/</c> 区切り)。</summary>
    private static Dictionary<string, FsEntry> Files(IFileSystem fs, FsEntry root, string strip = "\\")
    {
        var files = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
        var queue = new Queue<FsEntry>([root]);
        while (queue.Count > 0)
        {
            foreach (FsEntry e in fs.ListDirectory(queue.Dequeue()))
            {
                if (e.IsDirectory)
                {
                    if (e.Name != "lost+found")
                    {
                        queue.Enqueue(e);
                    }
                }
                else if (!e.Flags.HasFlag(FsEntryFlags.Stream))
                {
                    string path = e.Path.StartsWith(strip, StringComparison.Ordinal) ? e.Path[strip.Length..] : e.Path;
                    files["/" + path.Replace('\\', '/').TrimStart('/')] = e;
                }
            }
        }

        return files;
    }

    private static void Compare(IFileSystem fs, Dictionary<string, FsEntry> files, bool checkTimes = true)
    {
        IReadOnlyList<Item> manifest = Manifest();
        // xorriso が加えるブートカタログのファイル (boot.catalog) のほかに、余分なファイルがない。
        string[] extra = [.. files.Keys.Except(manifest.Select(i => i.Path)).Where(k => !k.StartsWith("/boot.cat", StringComparison.Ordinal))];
        Assert.True(extra.Length == 0, string.Join(", ", extra));
        foreach (Item item in manifest)
        {
            Assert.True(files.TryGetValue(item.Path, out FsEntry? e), item.Path);
            Assert.Equal(item.Size, e.Size);
            Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
            if (checkTimes)
            {
                Assert.Equal(DateTime.UnixEpoch.AddSeconds(item.Mtime), e.Modified.Utc!.Value.AddTicks(-(e.Modified.Utc.Value.Ticks % TimeSpan.TicksPerSecond)));
            }

            byte[] content = FsTestImages.ReadAll(fs.OpenContent(e));
            Assert.Equal(item.Sha256, FsTestImages.Sha256(content));
        }
    }

    [FsCrossCheckFact]
    [Trait("TC", "TC-FOR-12-01")]
    public void Ext_images_made_by_mke2fs_match_the_manifest()
    {
        foreach (string name in new[] { "ext2.img", "ext3.img", "ext4.img" })
        {
            IFileSystem fs = FileSystemRegistry.Open(Image(name));
            Assert.Equal(name[..4], fs.Summary.TypeName);
            Assert.DoesNotContain(fs.Issues, i => i.Kind is FsIssueKind.ExtSuperblockChecksum or FsIssueKind.ExtGroupDescriptorChecksum);
            Compare(fs, Files(fs, fs.Root));
        }
    }

    [FsCrossCheckFact]
    public void Iso_image_made_by_xorriso_matches_the_manifest()
    {
        var fs = (Iso9660FileSystem)FileSystemRegistry.Open(Image("iso.iso"));
        Assert.True(fs.HasJoliet);
        Assert.True(fs.HasRockRidge);
        fs.SelectNameKind(IsoNameKind.RockRidge);
        Compare(fs, Files(fs, fs.Root));
        IsoBootEntry boot = Assert.Single(fs.BootEntries);
        Assert.Equal("NoEmulation", boot.EmulationName);
        Assert.Equal(4, boot.SectorCount);
    }

    [FsCrossCheckFact]
    public void Udf_image_made_by_mkudffs_matches_the_manifest()
    {
        var fs = (UdfFileSystem)FileSystemRegistry.Open(Image("udf201.img"));
        Assert.Equal("2.01", fs.RevisionText);
        Assert.DoesNotContain(fs.Issues, i => i.Kind is FsIssueKind.UdfDescriptorCrc or FsIssueKind.UdfTagChecksum);
        Compare(fs, Files(fs, fs.Root));
    }

    [FsCrossCheckFact]
    public void Hfsplus_image_made_on_linux_matches_the_manifest()
    {
        string path = Path.Combine(Folder, "hfsplus.img");
        if (!File.Exists(path))
        {
            // ランナーで hfsplus をマウントできなかった (ジョブの記録 hfsplus-skipped.txt)。
            Assert.True(File.Exists(Path.Combine(Folder, "hfsplus-skipped.txt")));
            return;
        }

        var fs = (HfsPlusFileSystem)FileSystemRegistry.Open(Image("hfsplus.img"));
        Assert.DoesNotContain(fs.Issues, i => i.Kind == FsIssueKind.HfsAlternateHeaderMismatch);
        Compare(fs, Files(fs, fs.Root));
    }

    [FsCrossCheckFact]
    [Trait("TC", "TC-FOR-07-02")]
    public void Lvm_linear_volume_made_by_lvm2_tools_opens()
    {
        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. LogicalVolumeAnalyzer.Discover(Image("pv0.img"), "pv0"),
            .. LogicalVolumeAnalyzer.Discover(Image("pv1.img"), "pv1")]);
        LogicalVolumeInfo lv = Assert.Single(scan.Volumes, v => v.Name == "lv0");
        Assert.Equal("vg0", lv.Group);
        Assert.Equal(LogicalVolumeKind.Linear, lv.Kind);
        LogicalVolumeByteSource source = LogicalVolumeAnalyzer.Open(lv, scan);
        byte[] all = new byte[source.Length];
        source.Read(0, all);
        Assert.Equal(File.ReadAllText(Path.Combine(Folder, "lv0.sha256")).Trim(), FsTestImages.Sha256(all));
    }
}
