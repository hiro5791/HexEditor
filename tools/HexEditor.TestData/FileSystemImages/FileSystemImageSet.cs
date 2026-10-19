using System.Collections.Concurrent;
using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>NTFS のイメージと、テストの期待値に使う配置 (どのファイルがどのクラスタにあるか)。</summary>
public sealed record NtfsImage(SparseImage Image, NtfsImageBuilder Builder)
{
    public NtfsImageBuilder.Node Find(string path) =>
        Builder.Root.Children.Concat(Builder.Root.Children.SelectMany(c => c.Children)).FirstOrDefault(n => n.Path == path)
        ?? throw new KeyNotFoundException(path);

    /// <summary>ファイルの名前なしのストリームの断片 (ボリューム上のバイト位置と長さ。スパースを除く)。</summary>
    public IReadOnlyList<(long Offset, long Length)> Fragments(string path)
    {
        NtfsImageBuilder.Node node = Find(path);
        var list = new List<(long, long)>();
        long logical = 0;
        long size = node.Data!.Content.Length;
        foreach ((long length, long? lcn) in node.Data.Runs)
        {
            if (lcn is long l)
            {
                list.Add((l * Builder.ClusterSize, Math.Min(length * Builder.ClusterSize, size - logical)));
            }

            logical += length * Builder.ClusterSize;
        }

        return list;
    }
}

/// <summary>
/// ファイルシステム・パーティションのテスト用イメージの作成 (docs/test/cases/12-forensics.md の「このファイルで使うテストデータ」の TD-FOR-*)。
/// 同じ ID からは毎回同じ内容を作る。メモリ上のイメージは作ったものを使い回す。
/// </summary>
public static class FileSystemImageSet
{
    public const long MiB = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Lazy<object>> Cache = new();

    private static T Cached<T>(string key, Func<T> create) =>
        (T)Cache.GetOrAdd(key, _ => new Lazy<object>(() => create()!)).Value;

    /// <summary>TD-FOR-NTFS (<paramref name="variant"/>: 空、BADFIXUP、BADTIME)。</summary>
    public static NtfsImage Ntfs(string variant = "", string label = "NTFSTEST") => Cached($"ntfs:{variant}:{label}", () => BuildNtfs(variant, label));

    private static NtfsImage BuildNtfs(string variant, string label)
    {
        var b = new NtfsImageBuilder(128 * MiB, label);
        NtfsImageBuilder.Node root = b.Root;
        NtfsImageBuilder.Node? deleted = null;
        foreach (FileSetItem item in FileSet.Common)
        {
            NtfsImageBuilder.Node node = item.Name switch
            {
                "frag.bin" => b.AddFile(root, item.Name, item.Content, item.ModifiedUtc, fragments: 3, gapClusters: 16),
                "small100.bin" => b.AddFile(root, item.Name, item.Content, item.ModifiedUtc, resident: false,
                    allocatedContent: [.. Enumerable.Repeat((byte)0x41, 4096)]),
                FileSet.LongName => b.AddFile(root, item.Name, item.Content, item.ModifiedUtc, dosName: "______~1.TXT"),
                _ => b.AddFile(root, item.Name, item.Content, item.ModifiedUtc, resident: item.Content.Length > 600 ? false : null),
            };
            if (item.Name == "deleted.txt")
            {
                deleted = node;
            }
        }

        NtfsImageBuilder.Node file = b.AddFile(root, "file.txt", FileSet.FileTxt, FileSet.TimeOf(8));
        b.AddStream(file, "secret", FileSet.SecretStream);
        b.AddFile(root, "compressed.bin", FileSet.Compressed, FileSet.TimeOf(9), compressed: true);
        NtfsImageBuilder.Node stomped = b.AddFile(root, "stomped.txt", FileSet.Stomped, FileSet.TimeOf(10));
        stomped.SiTimes = NtfsImageBuilder.Times.All(FileSet.StompedTime);
        if (variant == "BADTIME")
        {
            NtfsImageBuilder.Node bad = b.AddFile(root, "badtime.txt", Encoding.ASCII.GetBytes("BADTIME!"), FileSet.TimeOf(11));
            bad.SiTimes = new NtfsImageBuilder.Times
            {
                Created = FileSet.TimeOf(11), Modified = DateTime.MaxValue, MftModified = FileSet.TimeOf(11), Accessed = FileSet.TimeOf(11),
            };
        }

        NtfsImageBuilder.Node dir1 = b.AddDirectory(root, "dir1", FileSet.TimeOf(12));
        for (int i = 1; i <= 40; i++)
        {
            b.AddFile(dir1, $"f{i:D3}.txt", FileSet.Dir1File(i), FileSet.TimeOf(12).AddSeconds(i));
        }

        NtfsImageBuilder.Node victim = b.AddFile(dir1, "victim.txt", Encoding.ASCII.GetBytes("victim!!\r\n"), FileSet.TimeOf(13));
        b.Delete(victim, leaveInIndexSlack: true);

        // 最後の操作として deleted.txt を削除する (クラスタは再利用しない)。
        b.Delete(deleted!);

        var image = new SparseImage(128 * MiB);
        long readme = root.Children.First(c => c.Name == "readme.txt").Number;
        b.Build(new SparseImageTarget(image), editRecord: (number, record) =>
        {
            if (variant == "BADFIXUP" && number == readme)
            {
                // 1 つ目のセクタの末尾 2 バイト (更新シーケンス番号の位置) を壊す。
                record[510] ^= 0xFF;
                record[511] ^= 0xFF;
            }
        });
        return new NtfsImage(image, b);
    }

    /// <summary>TD-FOR-NTFS-OVERWRITE (64 MiB)。</summary>
    public static NtfsImage NtfsOverwrite() => Cached("ntfs:overwrite", () =>
    {
        var b = new NtfsImageBuilder(64 * MiB, "OVERWRITE");
        NtfsImageBuilder.Node gone = b.AddFile(b.Root, "gone.bin", FileSet.Repeat("GONE", 256 * 1024), FileSet.TimeOf(1));
        NtfsImageBuilder.Node half = b.AddFile(b.Root, "half.bin", FileSet.Repeat("HALF", 256 * 1024), FileSet.TimeOf(2));
        b.Delete(gone);
        b.Delete(half);
        b.AddFileAt(b.Root, "over1.bin", [.. Enumerable.Repeat((byte)0x11, 256 * 1024)], FileSet.TimeOf(3), gone.Data!.Runs[0].Lcn!.Value);
        b.AddFileAt(b.Root, "over2.bin", [.. Enumerable.Repeat((byte)0x22, 128 * 1024)], FileSet.TimeOf(4), half.Data!.Runs[0].Lcn!.Value);
        var image = new SparseImage(64 * MiB);
        b.Build(new SparseImageTarget(image));
        return new NtfsImage(image, b);
    });

    /// <summary>TD-FOR-NTFS-1M: フォルダ many に長さ 0 のファイル 100 万個 (4 GiB のスパースファイルに直接書く)。</summary>
    public static void WriteNtfs1M(string path, int files = 1_000_000)
    {
        var b = new NtfsImageBuilder(4L * 1024 * MiB, "NTFS1M", reservedMftRecords: files + 64);
        NtfsImageBuilder.Node many = b.AddDirectory(b.Root, "many", FileSet.TimeOf(1));
        for (int i = 0; i < files; i++)
        {
            b.AddFile(many, $"f{i:D7}", [], FileSet.TimeOf(2), resident: true);
        }

        using var target = new SparseFileTarget(path, 4L * 1024 * MiB);
        b.Build(target);
    }

    /// <summary>FAT のイメージ (TD-FOR-FAT12・FAT16・FAT32 と変形 FATDIFF・LOOP)。</summary>
    public static (SparseImage Image, FatImageBuilder Builder) Fat(string id) => Cached("fat:" + id, () =>
    {
        FatImageBuilder b = id switch
        {
            "TD-FOR-FAT12" => FatImageBuilder.WithFileSet(FatType.Fat12, 4 * MiB, 2048, "FAT12"),
            "TD-FOR-FAT16" => FatImageBuilder.WithFileSet(FatType.Fat16, 64 * MiB, 2048, "FAT16"),
            _ => FatImageBuilder.WithFileSet(FatType.Fat32, 512 * MiB, 4096, "FAT32"),
        };
        if (id == "TD-FOR-FAT32-FATDIFF")
        {
            // 2 つ目の FAT で data.bin のチェーンの 2〜11 番目のクラスタのエントリを終端にする。
            IReadOnlyList<long> chain = b.Files["data.bin"].Clusters;
            for (int i = 1; i <= 10; i++)
            {
                b.SetFatEntry(chain[i], b.EndOfChain, fatIndex: 1);
            }
        }
        else if (id == "TD-FOR-FAT32-LOOP")
        {
            // 両方の FAT で frag.bin のチェーンの 100 番目のクラスタのエントリを 1 番目のクラスタにする。
            IReadOnlyList<long> chain = b.Files["frag.bin"].Clusters;
            b.SetFatEntry(chain[99], (uint)chain[0]);
        }

        return (b.Build(), b);
    });

    /// <summary>TD-FOR-EXFAT。</summary>
    public static (SparseImage Image, ExFatImageBuilder Builder) ExFat() => Cached("exfat", () =>
    {
        ExFatImageBuilder b = ExFatImageBuilder.WithFileSet();
        return (b.Build(), b);
    });

    public const long NtfsPartitionStart = 2048, NtfsPartitionSectors = 262144, FatPartitionStart = 264192, FatPartitionSectors = 196608;

    /// <summary>TD-FOR-NTFS-FAT32 (<paramref name="lostMbr"/> なら LBA 0 を 0 にした TD-FOR-LOST-PART)。</summary>
    public static SparseImage NtfsFat32Disk(bool lostMbr = false) => Cached($"ntfsfat32:{lostMbr}", () =>
    {
        var disk = new SparseImage(256 * MiB);
        if (!lostMbr)
        {
            PartitionImages.WriteMbr(disk, 0x4E544653,
                PartitionImages.MbrEntry(0x07, NtfsPartitionStart, NtfsPartitionSectors),
                PartitionImages.MbrEntry(0x0C, FatPartitionStart, FatPartitionSectors));
        }

        disk.Place(NtfsPartitionStart * 512, Ntfs(label: "NTFSPART").Image);
        FatImageBuilder fat = FatImageBuilder.WithFileSet(FatType.Fat32, FatPartitionSectors * 512, 1024, "FAT32PART", hiddenSectors: FatPartitionStart);
        disk.Place(FatPartitionStart * 512, fat.Build());
        return disk;
    });

    /// <summary>ID からメモリ上のイメージを作る。</summary>
    public static SparseImage Image(string id) => id switch
    {
        "TD-FOR-MBR-EXT" => Cached(id, () => PartitionImages.MbrExtended()),
        "TD-FOR-MBR-EBRLOOP" => Cached(id, () => PartitionImages.MbrExtended(loop: true)),
        "TD-FOR-GPT" => Cached(id, () => PartitionImages.Gpt()),
        "TD-FOR-GPT-BADCRC" => Cached(id, () => PartitionImages.Gpt("BADCRC")),
        "TD-FOR-GPT-BADBACKUP" => Cached(id, () => PartitionImages.Gpt("BADBACKUP")),
        "TD-FOR-GPT-4K" => Cached(id, PartitionImages.Gpt4K),
        "TD-FOR-APM" => Cached(id, PartitionImages.Apm),
        "TD-FOR-BSD" => Cached(id, PartitionImages.Bsd),
        "TD-FOR-NTFS" => Ntfs().Image,
        "TD-FOR-NTFS-BADFIXUP" => Ntfs("BADFIXUP").Image,
        "TD-FOR-NTFS-BADTIME" => Ntfs("BADTIME").Image,
        "TD-FOR-NTFS-OVERWRITE" => NtfsOverwrite().Image,
        "TD-FOR-NTFS-FAT32" => NtfsFat32Disk(),
        "TD-FOR-LOST-PART" => NtfsFat32Disk(lostMbr: true),
        "TD-FOR-FAT12" or "TD-FOR-FAT16" or "TD-FOR-FAT32" or "TD-FOR-FAT32-FATDIFF" or "TD-FOR-FAT32-LOOP" => Fat(id).Image,
        "TD-FOR-EXFAT" => ExFat().Image,
        _ => throw new ArgumentException(id),
    };

    /// <summary>イメージの長さ (テストデータの一覧の大きさ)。</summary>
    public static long LengthOf(string id) => id switch
    {
        "TD-FOR-MBR-EXT" or "TD-FOR-MBR-EBRLOOP" or "TD-FOR-NTFS-FAT32" or "TD-FOR-LOST-PART" => 256 * MiB,
        "TD-FOR-GPT" or "TD-FOR-GPT-BADCRC" or "TD-FOR-GPT-BADBACKUP" or "TD-FOR-GPT-4K" or "TD-FOR-NTFS-OVERWRITE" or "TD-FOR-FAT16" or "TD-FOR-EXFAT" => 64 * MiB,
        "TD-FOR-APM" or "TD-FOR-BSD" => 32 * MiB,
        "TD-FOR-NTFS" or "TD-FOR-NTFS-BADFIXUP" or "TD-FOR-NTFS-BADTIME" => 128 * MiB,
        "TD-FOR-FAT12" => 4 * MiB,
        "TD-FOR-FAT32" or "TD-FOR-FAT32-FATDIFF" or "TD-FOR-FAT32-LOOP" => 512 * MiB,
        "TD-FOR-NTFS-1M" => 4096 * MiB,
        _ => throw new ArgumentException(id),
    };

    public static readonly string[] Ids =
    [
        "TD-FOR-MBR-EXT", "TD-FOR-MBR-EBRLOOP", "TD-FOR-GPT", "TD-FOR-GPT-BADCRC", "TD-FOR-GPT-BADBACKUP", "TD-FOR-GPT-4K", "TD-FOR-APM", "TD-FOR-BSD",
        "TD-FOR-NTFS", "TD-FOR-NTFS-BADFIXUP", "TD-FOR-NTFS-BADTIME", "TD-FOR-NTFS-OVERWRITE", "TD-FOR-NTFS-FAT32", "TD-FOR-LOST-PART",
        "TD-FOR-FAT12", "TD-FOR-FAT16", "TD-FOR-FAT32", "TD-FOR-FAT32-FATDIFF", "TD-FOR-FAT32-LOOP", "TD-FOR-EXFAT",
    ];
}
