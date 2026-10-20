using System.Text;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.Forensics.Images.Raid;
using HexEditor.Core.Forensics.Images.Scope;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Forensics.Images;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// ディスクイメージの形式 (E01、VHD、VHDX、VMDK)、RAID の構成、セクタ範囲の付随ファイル、取得ログ、範囲の一覧、パーティション表、
/// ファイルシステムの領域の解析 (NTFS・FAT・exFAT) のファジング (FOR-20〜26)。どれも信頼できない入力として、壊れた値で例外 (文書にある型) か
/// 読めない範囲になり、無限ループ・範囲外の読み込み・際限のない確保が起きないことを確かめる。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class ImageFuzzTests : IDisposable
{
    private readonly ImageTempDir _dir = new("hexeditor-image-fuzz");

    public void Dispose() => _dir.Dispose();

    private static bool Allowed(Exception ex) => ex is ImageFormatException or MissingSegmentsException or ParentImageNotFoundException
        or InvalidDataException or FileNotFoundException or EndOfStreamException;

    /// <summary>開けたイメージは、先頭・途中・末尾の読み込みが例外にならない (読めない範囲は結果で返す)。</summary>
    private static void Exercise(IByteSource image)
    {
        byte[] buffer = new byte[64 * 1024];
        long length = image.Length;
        foreach (long at in new[] { 0, length / 3, length / 2, Math.Max(0, length - buffer.Length), length - 1, length })
        {
            if (at >= 0)
            {
                ReadResult r = image.Read(at, buffer);
                Assert.True(r.BytesReturned <= buffer.Length);
                Assert.All(r.Unreadable, u => Assert.True(u.Length > 0));
            }
        }
    }

    private void FuzzFile(string name, byte[] seed, ImageFormat format, int seedNumber, int mutations = 400)
    {
        string path = _dir.File(name);
        Fuzz.Run(Fuzz.Cases(seedNumber, [seed], mutations, randoms: 30, maxLength: seed.Length + 4096), data =>
        {
            File.WriteAllBytes(path, data);
            using IImageSource image = ImageOpener.Open(path, format);
            Exercise(image);
        }, Allowed, describe: d => $"{d.Length} bytes");
    }

    private async Task<byte[]> E01SeedAsync(EwfCompression compression, string name)
    {
        using var source = new VirtualByteSource(256 * 1024, VirtualContent.Random, seed: 5);
        var faulty = new FaultyByteSource(source);
        faulty.AddReadError(64 * 1024, 512);
        string path = _dir.File(name);
        await ImageAcquisition.RunAsync(faulty, new AcquisitionSourceInfo { Path = "fuzz", Capacity = source.Length }, new AcquisitionOptions
        {
            Output = path,
            Format = ImageFormat.E01,
            Compression = compression,
            Verify = false,
            Case = new EwfCaseInfo { CaseNumber = "C", EvidenceNumber = "E" },
        });
        return File.ReadAllBytes(path);
    }

    [Fact]
    public async Task E01_reader_survives_mutated_segments()
    {
        byte[] fast = await E01SeedAsync(EwfCompression.Fast, "seed-fast.E01");
        byte[] none = await E01SeedAsync(EwfCompression.None, "seed-none.E01");
        FuzzFile("fuzz.E01", fast, ImageFormat.E01, 201);
        FuzzFile("fuzz.E01", none, ImageFormat.E01, 202);
    }

    [Fact]
    public void Vhd_reader_survives_mutated_files()
    {
        string dyn = _dir.File("seed.vhd");
        VirtualDiskWriters.VhdDynamic(dyn, 2 * 1024 * 1024, (o, d) => d.Fill((byte)(o >> 16)), blockSize: 512 * 1024);
        FuzzFile("fuzz.vhd", File.ReadAllBytes(dyn), ImageFormat.Vhd, 203);
        string fixedPath = _dir.File("seed-fixed.vhd");
        VirtualDiskWriters.VhdFixed(fixedPath, 64 * 1024, (o, d) => d.Fill(7));
        FuzzFile("fuzz.vhd", File.ReadAllBytes(fixedPath), ImageFormat.Vhd, 204, 200);
    }

    [Fact]
    public void Vhdx_reader_survives_mutated_headers_regions_metadata_and_bat()
    {
        string path = _dir.File("seed.vhdx");
        VirtualDiskWriters.Vhdx(path, 2 * 1024 * 1024, (o, d) => d.Fill((byte)(o >> 16)), blockSize: 1024 * 1024);
        byte[] seed = File.ReadAllBytes(path);
        string target = _dir.File("fuzz.vhdx");
        var random = new Random(205);

        // 変異は構造のある位置 (ヘッダー、領域表、BAT、メタデータ) に集める (ファイル全体の乱数の変異はほとんどデータに当たる)。
        long[] hot = [0, 64 * 1024, 128 * 1024, 192 * 1024, 256 * 1024, 2 * 1024 * 1024, 3 * 1024 * 1024, 3 * 1024 * 1024 + 64 * 1024];
        var cases = new List<byte[]>();
        for (int i = 0; i < 300; i++)
        {
            byte[] data = (byte[])seed.Clone();
            for (int e = random.Next(1, 4); e > 0; e--)
            {
                long at = hot[random.Next(hot.Length)] + random.Next(256);
                if (at < data.Length)
                {
                    data[at] = (byte)random.Next(256);
                }
            }

            cases.Add(data);
        }

        Fuzz.Run(cases, data =>
        {
            File.WriteAllBytes(target, data);
            using IImageSource image = ImageOpener.Open(target, ImageFormat.Vhdx);
            Exercise(image);
        }, Allowed, describe: d => $"{d.Length} bytes");
    }

    [Fact]
    public void Vmdk_reader_survives_mutated_sparse_and_descriptor_files()
    {
        string sparse = _dir.File("seed.vmdk");
        VirtualDiskWriters.VmdkSparse(sparse, 1024 * 1024, (o, d) => d.Fill((byte)(o >> 14)), grainSectors: 16);
        FuzzFile("fuzz.vmdk", File.ReadAllBytes(sparse), ImageFormat.Vmdk, 206);
        string stream = _dir.File("seed-so.vmdk");
        VirtualDiskWriters.VmdkSparse(stream, 1024 * 1024, (o, d) => d.Fill((byte)(o >> 14)), streamOptimized: true, grainSectors: 16);
        FuzzFile("fuzz.vmdk", File.ReadAllBytes(stream), ImageFormat.Vmdk, 207);

        string flat = _dir.File("flatseed.vmdk");
        VirtualDiskWriters.VmdkFlat(flat, 64 * 1024, 64 * 1024, 64 * 1024, (o, d) => d.Fill(3));
        string text = File.ReadAllText(flat);
        string target = _dir.File("flatseed-copy.vmdk");
        foreach (string f in Directory.GetFiles(_dir.Path, "flatseed-f*.vmdk"))
        {
            File.Copy(f, Path.Combine(_dir.Path, Path.GetFileName(f).Replace("flatseed", "flatseed-copy", StringComparison.Ordinal)), true);
        }

        var random = new Random(208);
        Fuzz.Run(Enumerable.Range(0, 400).Select(_ => Fuzz.MutateText(random, text.Replace("flatseed", "flatseed-copy", StringComparison.Ordinal), "RW FLAT SPARSE ZERO \"0123456789\n=-.")), t =>
        {
            File.WriteAllText(target, t);
            using IImageSource image = ImageOpener.Open(target, ImageFormat.Vmdk);
            Exercise(image);
        }, ex => Allowed(ex) || ex is IOException);
    }

    [Fact]
    public void Text_inputs_survive_mutation()
    {
        var random = new Random(209);
        string raid = new RaidConfig { Members = [new() { Source = "a" }, new() { Source = "b", DataOffset = 512 }, new() { Source = "c", Missing = true }] }.ToJson();
        Fuzz.Run(Enumerable.Range(0, 500).Select(_ => Fuzz.MutateText(random, raid, "{}[]\":,0123456789-truefalsenull")), t =>
        {
            RaidConfig c = RaidConfig.Parse(t);
            Assert.Null(c.Validate());
        }, ex => ex is InvalidDataException);

        string sidecar = _dir.File("s.bin");
        File.WriteAllBytes(sidecar, new byte[512]);
        string json = "{\"model\":\"m\",\"serialNumber\":\"s\",\"capacity\":1024,\"sectorSize\":512,\"startLba\":0,\"sectorCount\":1,\"savedAt\":\"x\",\"sha256\":\"00\"}";
        Fuzz.Run(Enumerable.Range(0, 400).Select(_ => Fuzz.MutateText(random, json, "{}\":,0123456789-")), t =>
        {
            File.WriteAllText(sidecar + ".json", t);
            SectorRangeArchive.ReadSidecar(sidecar);
        }, ex => ex is InvalidDataException);

        string log = _dir.File("log.txt");
        string logText = "MD5: d41d8cd98f00b204e9800998ecf8427e\r\nSHA-256: " + new string('a', 64) + "\r\nSHA-1 (x): " + new string('b', 40) + "\r\n";
        Fuzz.Run(Enumerable.Range(0, 400).Select(_ => Fuzz.MutateText(random, logText, "MDSHA-0123456789abcdef: ()\r\n")), t =>
        {
            File.WriteAllText(log, t);
            foreach ((string a, byte[] v) in AcquisitionLog.ReadHashes(log))
            {
                Assert.Equal(a == ImageHashes.Md5 ? 16 : a == ImageHashes.Sha1 ? 20 : 32, v.Length);
            }
        });

        Fuzz.Run(Enumerable.Range(0, 400).Select(_ => Fuzz.MutateText(random, "start_lba,sector_count\r\n0,34\r\n2048,100\r\n", "0123456789,\r\n-")), t =>
        {
            foreach (SectorRange r in RangesCsv.Parse(t))
            {
                Assert.True(r.SectorCount > 0 && r.StartSector >= 0);
            }

            RangesCsv.NotIncluded(RangesCsv.Parse(t), 512, 1 << 30);
        });

        string header = EwfHeaderText.Header2(new EwfCaseInfo { CaseNumber = "c" }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "v", "o", "m", "s");
        Fuzz.Run(Enumerable.Range(0, 400).Select(_ => Fuzz.MutateText(random, header, "main\t\n0123456789 ")), t =>
        {
            IReadOnlyDictionary<string, string> values = EwfHeaderText.Parse(t);
            EwfHeaderText.ParseDate(values.GetValueOrDefault("m"));
        });
    }

    [Fact]
    public void Partition_tables_sniffer_and_file_system_regions_survive_mutated_disks()
    {
        // NTFS と FAT32 (MBR) の小さなディスクと、GPT のディスク。
        string disk = SyntheticDisks.NtfsFat32(_dir.File("seed-disk.img"));
        byte[] head = new byte[2 * 1024 * 1024];
        using (FileStream s = File.OpenRead(disk))
        {
            s.ReadExactly(head);
        }

        byte[] ntfs = new byte[2 * 1024 * 1024];
        using (FileStream s = File.OpenRead(disk))
        {
            s.Position = 2048 * 512;
            s.ReadExactly(ntfs);
        }

        byte[] fat = new byte[4 * 1024 * 1024];
        using (FileStream s = File.OpenRead(disk))
        {
            s.Position = 264_192 * 512L;
            s.ReadExactly(fat);
        }

        var random = new Random(210);
        IEnumerable<byte[]> Hot(byte[] seed, int count, int window)
        {
            for (int i = 0; i < count; i++)
            {
                byte[] data = (byte[])seed.Clone();
                for (int e = random.Next(1, 5); e > 0; e--)
                {
                    int at = random.Next(3) == 0 ? random.Next(window) : random.Next(Math.Min(seed.Length, 256 * 1024));
                    data[at] = random.Next(4) switch { 0 => 0xFF, 1 => 0, 2 => (byte)(data[at] ^ 0x80), _ => (byte)random.Next(256) };
                }

                yield return data;
            }
        }

        Fuzz.Run(Hot(head, 400, 34 * 512), data =>
        {
            using var source = new MemoryByteSource(data);
            ImagePartitions.Read(source, 512);
            VolumeSniffer.Sniff(source);
            ImageScopePlanner.Plan(source, 512, AcquisitionScope.Skeleton);
        }, describe: d => $"{d.Length} bytes");

        Fuzz.Run(Hot(ntfs, 300, 4096).Concat(Hot(fat, 300, 64 * 1024)), data =>
        {
            using var source = new MemoryByteSource(data);
            if (FileSystemRegionProviders.Analyze(source) is { } regions)
            {
                Assert.All(regions.Allocated.Concat(regions.Metadata), r => Assert.True(r.Offset >= 0 && r.Length > 0 && r.Offset + r.Length <= source.Length));
            }
        }, describe: d => $"{d.Length} bytes");
    }
}
