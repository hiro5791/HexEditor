using System.Text;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Apple;
using HexEditor.Core.FileSystems.Ext;
using HexEditor.Core.FileSystems.Iso;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// ext・ISO 9660・UDF・HFS+・APFS・LDM・LVM2 の解析 (FOR-07・FOR-12〜FOR-14) のファジング。テスト用イメージのメタデータ (スーパーブロック、
/// グループ記述子、inode、エクステント、ディレクトリ、ボリューム記述子、ファイルエントリ、B 木のノード、オブジェクトマップ、VBLK、LVM2 のテキスト) の
/// バイトを変えたものを解析し、例外 (開けない場合の <see cref="FileSystemOpenException"/> 以外)・無限ループ・確保量の上限超えがないことを確かめる。
/// </summary>
public sealed class FileSystemFuzzMoreTests
{
    private sealed record Case(string Name, Dictionary<long, byte> Patches)
    {
        public override string ToString() => $"{Name}: {string.Join(' ', Patches.Take(8).Select(p => $"{p.Key:X}={p.Value:X2}"))}";
    }

    private static readonly byte[] Special = [0x00, 0xFF, 0x7F, 0x80, 0x01, 0xFE];

    private static IEnumerable<Case> Mutations(string name, int seed, int count, IReadOnlyList<(long Start, long Length)> regions)
    {
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var patches = new Dictionary<long, byte>();
            int edits = random.Next(1, 9);
            for (int e = 0; e < edits; e++)
            {
                (long start, long length) = regions[random.Next(regions.Count)];
                long at = start + (long)(random.NextDouble() * length);
                patches[at] = random.Next(3) == 0 ? Special[random.Next(Special.Length)] : (byte)random.Next(256);
            }

            yield return new Case(name, patches);
        }
    }

    private static bool Allowed(Exception ex) => ex is FileSystemOpenException or OperationCanceledException;

    /// <summary>ファイルシステムを開き、ブラウザが行う操作を一通り行う (件数・深さに上限)。</summary>
    private static void Exercise(IVolumeReader reader, FileSystemKind? kind = null)
    {
        IFileSystem fs = FileSystemRegistry.Open(reader, kind is null ? null : new FileSystemOpenOptions { Kind = kind });
        _ = fs.Summary;
        _ = fs.Issues;
        _ = fs.MetadataRegions;
        if (fs is IFsChoices choices)
        {
            foreach (FsChoice c in choices.Choices)
            {
                for (int i = 0; i < c.Options.Count; i++)
                {
                    choices.Select(c.Key, i);
                }
            }
        }

        var queue = new Queue<(FsEntry, int)>();
        queue.Enqueue((fs.Root, 0));
        int seen = 0;
        while (queue.Count > 0 && seen < 400)
        {
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in fs.ListDirectory(dir).Take(200))
            {
                seen++;
                _ = fs.GetExtents(e);
                _ = fs.Describe(e);
                IVolumeReader content = fs.OpenContent(e);
                byte[] head = new byte[(int)Math.Min(content.Length, 8192)];
                content.Read(0, head);
                if (content.Length > 8192)
                {
                    content.Read(content.Length - 1, new byte[1]);
                }

                _ = fs.GetStreams(e).Take(4).ToList();
                if (e.IsDirectory && depth < 4)
                {
                    queue.Enqueue((e, depth + 1));
                }
            }
        }

        _ = fs.EnumerateMetadata().Take(64).ToList();
        _ = fs.EnumerateDeleted().Take(200).ToList();
        if (fs.Allocation is { } map)
        {
            _ = map.FreeRuns(fs.FirstClusterNumber, Math.Min(fs.ClusterCount, 1 << 16)).Take(1000).ToList();
        }

        if (fs is ExtFileSystem ext)
        {
            _ = ExtJournal.Read(ext)?.Blocks().Take(100).ToList();
            _ = ext.EntryForInode(12);
        }

        if (fs is ApfsFileSystem apfs)
        {
            foreach (ApfsVolume v in apfs.Volumes)
            {
                _ = apfs.Snapshots(v);
            }
        }
    }

    [Fact]
    public void Ext_parser_survives_mutated_superblock_descriptors_inodes_and_directories()
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
            Fuzz.Run(Mutations("ext-" + variant, 1201 + (int)variant, 300, regions), c => Exercise(new PatchedImageReader(inner, c.Patches)), Allowed);
        }
    }

    [Fact]
    public void Iso_and_udf_parsers_survive_mutated_descriptors_and_directories()
    {
        (SparseImage iso, IsoImageBuilder ib) = FileSystemImageSet.IsoJrr();
        var isoReader = new SparseImageReader(iso);
        (long, long)[] isoRegions =
        [
            (16 * 2048, 4 * 2048), (ib.Root.Extent * 2048, 2048), (ib.Root.JolietExtent * 2048, 2048), (ib.BootCatalogSector * 2048, 64),
        ];
        Fuzz.Run(Mutations("iso", 1210, 400, isoRegions), c => Exercise(new PatchedImageReader(isoReader, c.Patches)), Allowed);

        (SparseImage udf, UdfImageBuilder ub) = FileSystemImageSet.Udf250();
        var udfReader = new SparseImageReader(udf);
        (long, long)[] udfRegions =
        [
            (16 * 2048, 3 * 2048), (32 * 2048, 6 * 2048), (256 * 2048, 512), (ub.PartitionSector(0) * 2048, 2048), (ub.PartitionSector(2) * 2048, 2048),
            (ub.MetadataSector(0) * 2048, 8 * 2048),
        ];
        Fuzz.Run(Mutations("udf", 1211, 400, udfRegions), c => Exercise(new PatchedImageReader(udfReader, c.Patches), FileSystemKind.Udf), Allowed);
    }

    [Fact]
    public void Hfs_and_apfs_parsers_survive_mutated_headers_and_tree_nodes()
    {
        (SparseImage hfs, HfsPlusImageBuilder hb) = FileSystemImageSet.HfsPlus();
        var hfsReader = new SparseImageReader(hfs);
        long catalog = (long)hb.CatalogStart * hb.BlockSize;
        (long, long)[] hfsRegions = [(1024, 512), (catalog, 3 * HfsPlusImageBuilder.CatalogNodeSize), (2L * hb.BlockSize, 2 * HfsPlusImageBuilder.ExtentsNodeSize)];
        Fuzz.Run(Mutations("hfs", 1220, 400, hfsRegions), c => Exercise(new PatchedImageReader(hfsReader, c.Patches)), Allowed);

        // APFS はチェックサムで守られているので、チェックサムを付け直したブロックも混ぜる。
        (SparseImage apfs, _) = FileSystemImageSet.Apfs();
        var apfsReader = new SparseImageReader(apfs);
        (long, long)[] apfsRegions = [(0, 4096), (4 * 4096, 4096), (30 * 4096, 2 * 4096), (40 * 4096, 6 * 4096), (60 * 4096, 4096)];
        Fuzz.Run(Mutations("apfs", 1221, 300, apfsRegions), c => Exercise(new PatchedImageReader(apfsReader, c.Patches)), Allowed);
        Fuzz.Run(Mutations("apfs-csum", 1222, 300, apfsRegions), c => Exercise(new ResealedReader(apfsReader, c.Patches)), Allowed);
    }

    [Fact]
    public void Logical_volume_metadata_survives_mutations()
    {
        var a = FsTestImages.Reader("TD-FOR-LDM-STRIPE-A");
        long config = LogicalVolumeImages.LdmConfigStart * 512;
        (long, long)[] ldmRegions = [(6 * 512, 512), (config + 512, 1024), (config + 17 * 512, 4 * 512)];
        Fuzz.Run(Mutations("ldm", 1230, 400, ldmRegions), c =>
        {
            var reader = new PatchedImageReader(a, c.Patches);
            LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze(LogicalVolumeAnalyzer.Discover(reader, "A", PartitionAnalyzer.Analyze(reader, 512)));
            foreach (LogicalVolumeInfo v in scan.Volumes)
            {
                _ = scan.MissingMembers(v);
            }
        }, Allowed);

        var pv = FsTestImages.Reader("TD-FOR-LVM-LINEAR-A");
        (long, long)[] lvmRegions = [(512, 512), (4096, 512), (4096 + 512, 2048)];
        Fuzz.Run(Mutations("lvm", 1231, 400, lvmRegions), c =>
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

        // LVM2 のテキストの乱数。
        string seed = LogicalVolumeImages.LvmLinearMetadata();
        var random = new Random(1232);
        Fuzz.Run(Enumerable.Range(0, 500).Select(_ => Fuzz.MutateText(random, seed, "{}[]=,\"#\\ \n0123456789abcxyz-")), text => LvmText.ParseVolumeGroup(text));
    }

    [Fact]
    public void Structure_readers_survive_random_input()
    {
        Fuzz.Run(Fuzz.Cases(1240, [], 0, randoms: 1500, maxRandomLength: 4096), data =>
        {
            byte[] block = new byte[4096];
            data.CopyTo(block, 0);
            _ = ExtSuperblock.Parse(block);
            _ = ExtInode.Parse(12, 0, block.AsSpan(0, 256).ToArray());
            _ = ExtGroupDescriptor.Parse(block, true);
            _ = UdfFileSystem.Time(block.AsSpan(0, 12));
            _ = UdfFileSystem.Cs0(data);
            _ = Iso9660FileSystem.RecordingDate(block.AsSpan(0, 7));
            _ = Iso9660FileSystem.DecDate(block.AsSpan(0, 17));
            _ = ExtJournal.Open(new ArrayVolumeReader(block))?.Blocks().Take(10).ToList();
            _ = FsChecksums.ApfsObjectValid(block);
        });
    }

    /// <summary>変えたブロックの APFS のチェックサムを付け直して読む (チェックサムの先の構造の検査を通すため)。</summary>
    private sealed class ResealedReader(IVolumeReader inner, IReadOnlyDictionary<long, byte> patches) : IVolumeReader
    {
        private readonly Dictionary<long, byte[]> _blocks = Build(inner, patches);

        private static Dictionary<long, byte[]> Build(IVolumeReader inner, IReadOnlyDictionary<long, byte> patches)
        {
            var blocks = new Dictionary<long, byte[]>();
            foreach ((long at, byte value) in patches)
            {
                long index = at / 4096;
                if (!blocks.TryGetValue(index, out byte[]? b))
                {
                    blocks[index] = b = inner.ReadBytes(index * 4096, 4096);
                }

                b[at % 4096] = value;
            }

            foreach (byte[] b in blocks.Values)
            {
                ImageBytes.SetFletcher64(b);
            }

            return blocks;
        }

        public long Length => inner.Length;

        public HexEditor.Core.Sources.ReadResult Read(long offset, Span<byte> buffer)
        {
            HexEditor.Core.Sources.ReadResult r = inner.Read(offset, buffer);
            foreach ((long index, byte[] block) in _blocks)
            {
                long start = Math.Max(offset, index * 4096), end = Math.Min(offset + r.BytesReturned, index * 4096 + 4096);
                if (end > start)
                {
                    block.AsSpan((int)(start - index * 4096), (int)(end - start)).CopyTo(buffer[(int)(start - offset)..]);
                }
            }

            return r;
        }
    }

    [Fact]
    public void Detector_recognizes_the_new_file_systems()
    {
        foreach ((string id, FileSystemKind kind) in new[]
        {
            ("TD-FOR-EXT2", FileSystemKind.Ext), ("TD-FOR-ISO-JRR", FileSystemKind.Iso9660), ("TD-FOR-UDF250", FileSystemKind.Udf),
            ("TD-FOR-HFSPLUS", FileSystemKind.HfsPlus), ("TD-FOR-APFS", FileSystemKind.Apfs), ("TD-FOR-LVM-LINEAR-A", FileSystemKind.Lvm2),
        })
        {
            Assert.Equal(kind, FileSystemDetector.Detect(FsTestImages.Reader(id))?.Kind);
        }

        _ = Encoding.ASCII;
    }
}
