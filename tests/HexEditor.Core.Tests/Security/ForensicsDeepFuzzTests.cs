using System.Buffers.Binary;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// フェーズ 5 のセキュリティ監査で足した、ファイルシステム・パーティション・論理ボリュームの解析の深いファジング。既存のファジング
/// (<see cref="FileSystemFuzzTests"/>・<see cref="FileSystemFuzzMoreTests"/>・<see cref="WipeFuzzTests"/>) は 1 バイトずつの変更なので、
/// ここでは長さ・位置・個数の欄に当たりやすい「2/4/8 バイトの境界にそろえた特別な値」(0、-1、0x7FFFFFFF、long.MaxValue など) を書き込む。
/// 件数は環境変数 HEXEDITOR_FUZZ_SCALE (既定 1)、種は HEXEDITOR_FUZZ_SEED (既定 0) で変えられる (手元で長く回すため)。
/// </summary>
[Trait("Category", "Nightly")]
public sealed class ForensicsDeepFuzzTests
{
    private static readonly long[] Interesting =
    [
        0, 1, -1, 2, 3, 0x7F, 0x80, 0xFF, 0x100, 0x200, 0x1000, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF,
        0x100000000, 0x7FFFFFFFFFFFFFFF, long.MinValue, 0x40000000, 0x3FFFFFFF, 0xFFFFFFFE,
    ];

    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("HEXEDITOR_FUZZ_SCALE"), out int s) && s > 0 ? s : 1;

    private static int SeedOffset => int.TryParse(Environment.GetEnvironmentVariable("HEXEDITOR_FUZZ_SEED"), out int s) ? s : 0;

    internal sealed record Case(string Name, Dictionary<long, byte> Patches)
    {
        public override string ToString() => $"{Name}: {string.Join(' ', Patches.Take(16).Select(p => $"{p.Key:X}={p.Value:X2}"))}";
    }

    /// <summary>領域の中の境界にそろえた位置に、特別な値 (幅 1/2/4/8、リトルまたはビッグエンディアン) か乱数のバイトを書く。</summary>
    internal static IEnumerable<Case> WordMutations(string name, int seed, int count, IReadOnlyList<(long Start, long Length)> regions)
    {
        var random = new Random(seed + SeedOffset * 7919);
        for (int i = 0; i < count * Scale; i++)
        {
            var patches = new Dictionary<long, byte>();
            for (int e = random.Next(1, 6); e > 0; e--)
            {
                (long start, long length) = regions[random.Next(regions.Count)];
                int width = random.Next(4) switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
                long at = start + (long)(random.NextDouble() * length);
                at -= (at - start) % width;
                byte[] word = new byte[8];
                if (random.Next(5) == 0)
                {
                    random.NextBytes(word);
                }
                else
                {
                    long v = Interesting[random.Next(Interesting.Length)];
                    if (random.Next(4) == 0)
                    {
                        v += random.Next(-2, 3);
                    }

                    if (random.Next(4) == 0)
                    {
                        BinaryPrimitives.WriteInt64BigEndian(word, v << (8 * (8 - width)));
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(word, v);
                    }
                }

                for (int k = 0; k < width; k++)
                {
                    patches[at + k] = word[k];
                }
            }

            yield return new Case(name, patches);
        }
    }

    private static bool Allowed(Exception ex) => ex is FileSystemOpenException or OperationCanceledException;

    private static void All(IVolumeReader reader)
    {
        FileSystemFuzzMoreTests.Exercise(reader);
        if (FileSystemDetector.Detect(reader)?.Kind is FileSystemKind.Ntfs or FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32 or FileSystemKind.ExFat)
        {
            FileSystemFuzzTests.Exercise(reader);

            // 消去・墨塗りの計画は、アプリと同じく不整合の検査を通ったファイルシステムでだけ作る (不整合なら実行前に拒否するため。
            // BPB が主張する巨大なボリューム全体の走査は、長時間処理としては正しい動作)。
            if (HexEditor.Core.Wiping.FileSystemWipe.CheckConsistency(FileSystemRegistry.Open(reader)) == HexEditor.Core.Wiping.FsInconsistency.None)
            {
                WipeFuzzTests.Exercise(reader);
            }
        }
    }

    [Fact]
    public void Fat_and_exfat_survive_word_mutations()
    {
        foreach (string id in new[] { "TD-FOR-FAT12", "TD-FOR-FAT32" })
        {
            (SparseImage image, FatImageBuilder b) = FileSystemImageSet.Fat(id);
            var inner = new SparseImageReader(image);
            long fat = b.ReservedSectors * 512L;
            (long, long)[] regions =
            [
                (0, 512), (512, 512 * 7), (fat, Math.Min(4096, b.FatSectors * 512)), (fat + b.FatSectors * 512, 2048),
                (b.Files["readme.txt"].EntryOffset - 64, 512),
            ];
            Fuzz.Run(WordMutations(id, 3101, 300, regions), c => All(new PatchedImageReader(inner, c.Patches)), Allowed);
        }

        (SparseImage ex, ExFatImageBuilder x) = FileSystemImageSet.ExFat();
        var exReader = new SparseImageReader(ex);
        (long, long)[] exRegions = [(0, 512 * 24), (128 * 512, 2048), (x.ClusterOffset(x.RootCluster), 1024), (x.ClusterOffset(x.BitmapCluster), 512)];
        Fuzz.Run(WordMutations("exfat", 3102, 300, exRegions), c => All(new PatchedImageReader(exReader, c.Patches)), Allowed);
    }

    [Fact]
    public void Ntfs_survives_word_mutations()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        var inner = new SparseImageReader(image.Image);
        long mft = image.Builder.MftLcn * 4096;
        var regions = new List<(long, long)>
        {
            (0, 512), (mft, 6 * 1024), (mft + 24 * 1024, 16 * 1024), (image.Builder.RecordOffset(image.Find("\\readme.txt").Number), 1024),
            (image.Builder.LogLcn * 4096, 0x4000),
        };
        var fs = FileSystemRegistry.Open(inner);
        foreach (FsEntry dir in new[] { fs.Root, FsTestImages.Child(fs, fs.Root, "dir1") })
        {
            foreach (FsExtent e in fs.GetExtents(dir))
            {
                regions.Add((e.VolumeOffset!.Value, Math.Min(e.Length, 4096)));
            }
        }

        Fuzz.Run(WordMutations("ntfs", 3103, 500, regions), c => All(new PatchedImageReader(inner, c.Patches)), Allowed);
    }

    [Fact]
    public void Ext_iso_udf_hfs_apfs_survive_word_mutations()
    {
        foreach (ExtVariant variant in new[] { ExtVariant.Ext2, ExtVariant.Ext4 })
        {
            (SparseImage image, ExtImageBuilder b) = FileSystemImageSet.Ext(variant);
            var inner = new SparseImageReader(image);
            long table = b.InodeTable(0) * b.BlockSize;
            ExtImageBuilder.Node dir1 = b.Nodes.First(n => n.Name == "dir1");
            ExtImageBuilder.Node frag = b.Nodes.First(n => n.Name == "frag.bin");
            (long, long)[] regions =
            [
                (1024, 1024), (b.BlockSize, 512), (table, 16 * b.InodeSize), (b.InodeOffset(frag.Inode), b.InodeSize),
                (b.Root.Runs[0].Physical * b.BlockSize, b.BlockSize), (dir1.Runs[0].Physical * b.BlockSize, b.BlockSize),
            ];
            Fuzz.Run(WordMutations("ext-" + variant, 3110 + (int)variant, 300, regions), c => All(new PatchedImageReader(inner, c.Patches)), Allowed);
        }

        (SparseImage iso, IsoImageBuilder ib) = FileSystemImageSet.IsoJrr();
        var isoReader = new SparseImageReader(iso);
        (long, long)[] isoRegions = [(16 * 2048, 4 * 2048), (ib.Root.Extent * 2048, 2048), (ib.Root.JolietExtent * 2048, 2048), (ib.BootCatalogSector * 2048, 64)];
        Fuzz.Run(WordMutations("iso", 3120, 300, isoRegions), c => All(new PatchedImageReader(isoReader, c.Patches)), Allowed);

        (SparseImage udf, UdfImageBuilder ub) = FileSystemImageSet.Udf250();
        var udfReader = new SparseImageReader(udf);
        (long, long)[] udfRegions =
        [
            (16 * 2048, 3 * 2048), (32 * 2048, 6 * 2048), (256 * 2048, 512), (ub.PartitionSector(0) * 2048, 2048), (ub.PartitionSector(2) * 2048, 2048),
            (ub.MetadataSector(0) * 2048, 8 * 2048),
        ];
        Fuzz.Run(WordMutations("udf", 3121, 300, udfRegions), c => FileSystemFuzzMoreTests.Exercise(new PatchedImageReader(udfReader, c.Patches), FileSystemKind.Udf), Allowed);

        (SparseImage hfs, HfsPlusImageBuilder hb) = FileSystemImageSet.HfsPlus();
        var hfsReader = new SparseImageReader(hfs);
        long catalog = (long)hb.CatalogStart * hb.BlockSize;
        (long, long)[] hfsRegions = [(1024, 512), (catalog, 3 * HfsPlusImageBuilder.CatalogNodeSize), (2L * hb.BlockSize, 2 * HfsPlusImageBuilder.ExtentsNodeSize)];
        Fuzz.Run(WordMutations("hfs", 3122, 300, hfsRegions), c => All(new PatchedImageReader(hfsReader, c.Patches)), Allowed);

        (SparseImage apfs, _) = FileSystemImageSet.Apfs();
        var apfsReader = new SparseImageReader(apfs);
        (long, long)[] apfsRegions = [(0, 4096), (4 * 4096, 4096), (30 * 4096, 2 * 4096), (40 * 4096, 6 * 4096), (60 * 4096, 4096)];
        Fuzz.Run(WordMutations("apfs-csum", 3123, 400, apfsRegions), c => All(new FileSystemFuzzMoreTests.ResealedReader(apfsReader, c.Patches)), Allowed);
    }

    [Fact]
    public void Partitions_and_logical_volumes_survive_word_mutations()
    {
        foreach (string id in new[] { "TD-FOR-MBR-EXT", "TD-FOR-GPT", "TD-FOR-APM", "TD-FOR-BSD" })
        {
            var inner = FsTestImages.Reader(id);
            (long, long)[] regions = id == "TD-FOR-MBR-EXT"
                ? [(0, 512), (100352L * 512 + 0x1B0, 0x50), (166400L * 512 + 0x1B0, 0x50), (231424L * 512 + 0x1B0, 0x50)]
                : [(0, 512 * 34), (inner.Length - 512 * 33, 512 * 33)];
            Fuzz.Run(WordMutations(id, 3130, 300, regions), c =>
            {
                var reader = new PatchedImageReader(inner, c.Patches);
                foreach (int sector in new[] { 512, 4096 })
                {
                    PartitionAnalysis a = PartitionAnalyzer.Analyze(reader, sector, new PartitionAnalyzerOptions { ShowSmallGaps = true });
                    Assert.True(a.Rows.Count(r => r.Scheme == PartitionScheme.Logical) <= PartitionAnalyzer.MaxLogical);
                }
            }, Allowed);
        }

        var ldm = FsTestImages.Reader("TD-FOR-LDM-STRIPE-A");
        long config = LogicalVolumeImages.LdmConfigStart * 512;
        (long, long)[] ldmRegions = [(6 * 512, 512), (config + 512, 1024), (config + 17 * 512, 4 * 512)];
        Fuzz.Run(WordMutations("ldm", 3131, 400, ldmRegions), c =>
        {
            var reader = new PatchedImageReader(ldm, c.Patches);
            LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze(LogicalVolumeAnalyzer.Discover(reader, "A", PartitionAnalyzer.Analyze(reader, 512)));
            foreach (LogicalVolumeInfo v in scan.Volumes)
            {
                _ = scan.MissingMembers(v);
                if (v.CanOpenDirectly && scan.MissingMembers(v).Count == 0)
                {
                    LogicalVolumeByteSource s = LogicalVolumeAnalyzer.Open(v, scan);
                    s.Read(0, new byte[4096]);
                    s.Read(Math.Max(0, s.Length - 1), new byte[1]);
                }
            }
        }, Allowed);

        var pv = FsTestImages.Reader("TD-FOR-LVM-LINEAR-A");
        (long, long)[] lvmRegions = [(512, 512), (4096, 512), (4096 + 512, 2048)];
        Fuzz.Run(WordMutations("lvm", 3132, 400, lvmRegions), c =>
        {
            var reader = new PatchedImageReader(pv, c.Patches);
            LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze(LogicalVolumeAnalyzer.Discover(reader, "A"));
            foreach (LogicalVolumeInfo v in scan.Volumes.Where(v => v.CanOpenDirectly && scan.MissingMembers(v).Count == 0))
            {
                LogicalVolumeByteSource s = LogicalVolumeAnalyzer.Open(v, scan);
                s.Read(0, new byte[4096]);
                s.Read(Math.Max(0, s.Length - 1), new byte[1]);
            }
        }, Allowed);
    }
}
