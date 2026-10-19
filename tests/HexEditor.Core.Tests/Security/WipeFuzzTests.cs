using HexEditor.Core.FileSystems;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// 消去・墨塗りの計画 (FOR-31・FOR-32) が信頼できないメタデータを読む部分の頑健性: MFT レコード・INDX・ディレクトリエントリ・ブートセクタを
/// 変えたイメージで、不整合の検査・空き領域・スラック・墨塗り (名前の置き換えを含む) の計画を作っても、許した例外以外を出さず、上限の時間内に終わる。
/// </summary>
public sealed class WipeFuzzTests
{
    private sealed class PatchedReader(IVolumeReader inner, IReadOnlyDictionary<long, byte> patches) : IVolumeReader
    {
        public long Length => inner.Length;

        public ReadResult Read(long offset, Span<byte> buffer)
        {
            ReadResult r = inner.Read(offset, buffer);
            foreach ((long at, byte value) in patches)
            {
                if (at >= offset && at < offset + r.BytesReturned)
                {
                    buffer[(int)(at - offset)] = value;
                }
            }

            return r;
        }
    }

    private static IEnumerable<Dictionary<long, byte>> Mutations(int seed, int count, IReadOnlyList<(long Start, long Length)> regions)
    {
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var patches = new Dictionary<long, byte>();
            for (int e = random.Next(1, 9); e > 0; e--)
            {
                (long start, long length) = regions[random.Next(regions.Count)];
                patches[start + (long)(random.NextDouble() * length)] = (byte)random.Next(256);
            }

            yield return patches;
        }
    }

    private static bool Allowed(Exception ex) => ex is FileSystemOpenException or OperationCanceledException;

    private static void Exercise(IVolumeReader reader)
    {
        IFileSystem fs = FileSystemRegistry.Open(reader);
        _ = FileSystemWipe.CheckConsistency(fs);
        _ = FileSystemWipe.FreeRanges(fs, 0).Take(10_000).Sum(r => r.Length);
        _ = FileSystemWipe.PlanSlack(fs, 0);
        FsEntry[] entries = [.. fs.ListDirectory(fs.Root).Take(64)];
        RedactionPlan plan = FileSystemRedaction.Plan(fs, 0, reader, entries, ShredPass.Fill(0x00), replaceNames: true);
        _ = plan.Patches.Sum(p => p.Bytes.Length);
    }

    [Fact]
    public void Ntfs_plans_survive_mutated_records_and_indexes()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        var inner = new SparseImageReader(image.Image);
        long mft = image.Builder.MftLcn * 4096;
        var fs = FileSystemRegistry.Open(inner);
        var regions = new List<(long, long)> { (0, 512), (mft, 6 * 1024), (mft + 24 * 1024, 16 * 1024) };
        foreach (FsExtent e in fs.GetExtents(fs.Root))
        {
            regions.Add((e.VolumeOffset!.Value, Math.Min(e.Length, 4096)));
        }

        Fuzz.Run(Mutations(1201, 200, regions), p => Exercise(new PatchedReader(inner, p)), Allowed);
    }

    [Fact]
    public void Fat_and_exfat_plans_survive_mutated_directories_and_boot_sectors()
    {
        (SparseImage fat, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        var fatReader = new SparseImageReader(fat);
        long table = b.ReservedSectors * 512L;
        (long, long)[] fatRegions = [(0, 512), (table, 4096), (b.Files["readme.txt"].EntryOffset - 128, 1024)];
        Fuzz.Run(Mutations(1202, 200, fatRegions), p => Exercise(new PatchedReader(fatReader, p)), Allowed);

        (SparseImage exfat, ExFatImageBuilder x) = FileSystemImageSet.ExFat();
        var exReader = new SparseImageReader(exfat);
        (long, long)[] exRegions = [(0, 512 * 12), (x.ClusterOffset(x.RootCluster), 1024)];
        Fuzz.Run(Mutations(1203, 200, exRegions), p => Exercise(new PatchedReader(exReader, p)), Allowed);
    }
}
