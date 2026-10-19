using System.Runtime.InteropServices;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Scope;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>FOR-23 スケルトンイメージとスパースイメージ。</summary>
[Collection(SerialCollection.Name)]
public sealed class ImageScopeTests
{
    private static async Task<(AcquisitionResult Result, ImageScopePlan Plan)> AcquireAsync(string disk, string output, AcquisitionScope scope)
    {
        using IImageSource source = ImageOpener.Open(disk, ImageFormat.Raw);
        ImageScopePlan plan = ImageScopePlanner.Plan(source, 512, scope);
        AcquisitionResult result = await ImageAcquisition.RunAsync(source, Info(source, disk), new AcquisitionOptions
        {
            Output = output,
            Scope = scope,
            Included = plan.Included,
            Hashes = [ImageHashes.Md5, ImageHashes.Sha256],
        });
        return (result, plan);
    }

    [Fact]
    [Trait("TC", "TC-FOR-23-01")]
    public async Task A_sparse_raw_image_of_a_ten_percent_used_ntfs_uses_about_ten_percent_on_disk()
    {
        using var dir = new ImageTempDir();
        string disk = SyntheticDisks.Ntfs10Pct(dir.File("ntfs10.img"));
        string output = dir.File("sp.dd");
        (AcquisitionResult result, ImageScopePlan plan) = await AcquireAsync(disk, output, AcquisitionScope.Sparse);

        Assert.Equal(AcquisitionStatus.Completed, result.Status);
        Assert.Equal(VerificationVerdict.Match, result.Verification!.Overall);
        Assert.Contains(plan.Analyzed, a => a.FileSystem == "NTFS");
        Assert.Equal(GiB, new FileInfo(output).Length);
        long actual = CompressedSize(output);
        Assert.InRange(actual, GiB * 8 / 100, GiB * 15 / 100);

        string log = File.ReadAllText(dir.File("sp.txt"));
        Assert.Contains("sparse image", log);
        Assert.Contains("do not match the hashes of the whole source disk", log);
        Assert.Matches(@"(?m)^SHA-256: [0-9a-f]{64}\r?$", log);
        Assert.Matches(@"(?m)^SHA-256 \(included ranges\): [0-9a-f]{64}\r?$", log);
        Assert.NotNull(result.IncludedHashes);
        Assert.NotEqual(result.Hashes[ImageHashes.Sha256], result.IncludedHashes![ImageHashes.Sha256]);
    }

    [Fact]
    [Trait("TC", "TC-FOR-23-02")]
    public async Task A_skeleton_image_still_lists_the_files_but_holds_no_file_content()
    {
        using var dir = new ImageTempDir();
        string disk = SyntheticDisks.NtfsFat32(dir.File("ntfsfat32.img"));
        string output = dir.File("sk.dd");
        (AcquisitionResult result, ImageScopePlan plan) = await AcquireAsync(disk, output, AcquisitionScope.Skeleton);
        Assert.Equal(AcquisitionStatus.Completed, result.Status);
        Assert.Equal(["NTFS", "FAT32"], plan.Analyzed.Select(a => a.FileSystem));
        Assert.Empty(plan.WholePartitions);
        Assert.Contains("skeleton image", File.ReadAllText(dir.File("sk.txt")));

        using IImageSource original = ImageOpener.Open(disk, ImageFormat.Raw);
        using IImageSource skeleton = ImageOpener.Open(output, ImageFormat.Raw);

        // パーティションの一覧が同じ。
        ImagePartitionTable a = ImagePartitions.Read(original, 512);
        ImagePartitionTable b = ImagePartitions.Read(skeleton, 512);
        Assert.Equal(a.Partitions, b.Partitions);

        // NTFS・FAT32 のファイル一覧 (名前とサイズ) が同じ。
        ImagePartition ntfs = a.Partitions[0];
        ImagePartition fat = a.Partitions[1];
        Assert.Equal(TestFileLister.Ntfs(new SubRangeSource(original, ntfs.Offset, ntfs.Length)),
            TestFileLister.Ntfs(new SubRangeSource(skeleton, ntfs.Offset, ntfs.Length)));
        Assert.Equal(TestFileLister.Fat32(new SubRangeSource(original, fat.Offset, fat.Length)),
            TestFileLister.Fat32(new SubRangeSource(skeleton, fat.Offset, fat.Length)));
        Assert.Contains(("readme.txt", 4096L), TestFileLister.Ntfs(new SubRangeSource(skeleton, ntfs.Offset, ntfs.Length)));
        Assert.Contains(("README.TXT", 4096L), TestFileLister.Fat32(new SubRangeSource(skeleton, fat.Offset, fat.Length)));

        // readme.txt の内容はすべて 00 (データを取得していない)。
        (long offset, long length) = SyntheticDisks.FatDataRanges["readme.txt"];
        Assert.All(ReadAll(skeleton, fat.Offset + offset, (int)length), x => Assert.Equal(0, x));
        Assert.Contains(ReadAll(original, fat.Offset + offset, (int)length), x => x != 0);

        // 取得していない範囲 (.ranges.csv の補集合) に readme.txt の内容が入っている (Hex ビューでの強調の元)。
        string csv = RangesCsv.FindFor(output, ImageFormat.Raw)!;
        IReadOnlyList<(long Offset, long Length)> missing = RangesCsv.NotIncluded(RangesCsv.Parse(File.ReadAllText(csv)), 512, skeleton.Length);
        Assert.Contains(missing, m => m.Offset <= fat.Offset + offset && fat.Offset + offset + length <= m.Offset + m.Length);
    }

    [Fact]
    [Trait("TC", "TC-FOR-23-03")]
    public async Task Every_included_range_matches_the_original_and_everything_else_is_zero()
    {
        using var dir = new ImageTempDir();
        string disk = SyntheticDisks.NtfsFat32(dir.File("ntfsfat32.img"));
        string output = dir.File("sk.dd");
        await AcquireAsync(disk, output, AcquisitionScope.Skeleton);
        IReadOnlyList<SectorRange> ranges = RangesCsv.Parse(File.ReadAllText(dir.File("sk.ranges.csv")));
        Assert.NotEmpty(ranges);

        using IImageSource original = ImageOpener.Open(disk, ImageFormat.Raw);
        using IImageSource skeleton = ImageOpener.Open(output, ImageFormat.Raw);
        foreach (SectorRange r in ranges)
        {
            for (long s = r.StartSector; s < r.EndSector; s += 2048)
            {
                int n = (int)Math.Min(2048, r.EndSector - s) * 512;
                Assert.Equal(ReadAll(original, s * 512, n), ReadAll(skeleton, s * 512, n));
            }
        }

        // 範囲に含まれない部分から、固定の種で選んだ 100 セクタがすべて 00。
        IReadOnlyList<(long Offset, long Length)> outside = RangesCsv.NotIncluded(ranges, 512, skeleton.Length);
        long outsideSectors = outside.Sum(o => o.Length / 512);
        var random = new Random(2310);
        for (int i = 0; i < 100; i++)
        {
            long k = (long)(random.NextDouble() * outsideSectors);
            long sector = -1;
            foreach ((long o, long l) in outside)
            {
                if (k < l / 512)
                {
                    sector = o / 512 + k;
                    break;
                }

                k -= l / 512;
            }

            Assert.All(ReadAll(skeleton, sector * 512, 512), x => Assert.Equal(0, x));
        }
    }

    [Fact]
    public void A_partition_without_a_known_file_system_is_included_whole()
    {
        using var dir = new ImageTempDir();
        using var disk = new MemoryByteSource(MbrWithUnknownPartition());
        ImageScopePlan plan = ImageScopePlanner.Plan(disk, 512, AcquisitionScope.Skeleton);
        ImagePartition p = Assert.Single(plan.WholePartitions);
        Assert.Contains(plan.Included, r => r.Offset <= p.Offset && p.Offset + p.Length <= r.Offset + r.Length);
        Assert.Contains(plan.Included, r => r.Offset == 0);
    }

    private static byte[] MbrWithUnknownPartition()
    {
        byte[] data = new byte[4 * 1024 * 1024];
        data[446 + 4] = 0x83;
        BitConverter.GetBytes(2048u).CopyTo(data, 446 + 8);
        BitConverter.GetBytes(4096u).CopyTo(data, 446 + 12);
        data[510] = 0x55;
        data[511] = 0xAA;
        return data;
    }

    private static long CompressedSize(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
