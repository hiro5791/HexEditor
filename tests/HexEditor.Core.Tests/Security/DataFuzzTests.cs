using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Patches;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// 自前の展開 (EDIT-41 の deflate・zlib・gzip・LZ4)、パッチの形式 (EDIT-42/43 の IPS・UPS・BPS・.hexpatch)、履歴ファイル (EDIT-21 の .hexhist)、
/// スナップショット (ENG-35 の .hexsnap) の読み込みのファジング (フェーズ 4 の監査)。
/// </summary>
public sealed class DataFuzzTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "fuzz-" + Guid.NewGuid().ToString("N"));

    public DataFuzzTests() => Directory.CreateDirectory(_dir);

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

    private static Document Open(byte[] data) => new(new MemoryByteSource(data), Options());

    private static byte[] Sample(int seed, int length)
    {
        var random = new Random(seed);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // 繰り返しのあるデータ (一致の命令を作る)。
            data[i] = i > 64 && random.Next(3) == 0 ? data[i - random.Next(1, 64)] : (byte)random.Next(32, 100);
        }

        return data;
    }

    private static byte[] Compress(byte[] data, CompressionFormat format, int level)
    {
        using Document doc = Open(data);
        using EditContent content = CompressionEngine.Compress(doc.Current, 0, data.Length, new CompressionOptions(format, level) { GzipFileName = "a.bin" },
            ContentSink.For(doc));
        return Editing.CompressionTests.ReadContent(content);
    }

    public static TheoryData<CompressionFormat> Formats() => [.. Enum.GetValues<CompressionFormat>()];

    /// <summary>壊れた圧縮データは <see cref="CompressedDataException"/> になり、展開の大きさの確認 (仕様 7) を超えて書かない。</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void Decompressors_survive_mutated_streams(CompressionFormat format)
    {
        CompressionFormatInfo info = CompressionFormats.Info(format);
        var seeds = new List<byte[]>();
        foreach (byte[] data in new[] { [], Sample(1, 100), Sample(2, 5000), Sample(3, 70_000), new byte[200_000] })
        {
            foreach (int level in new[] { info.MinLevel, info.MaxLevel }.Distinct())
            {
                seeds.Add(Compress(data, format, level));
            }
        }

        const long Limit = 32L * 1024 * 1024;
        bool own = format is CompressionFormat.Deflate or CompressionFormat.Zlib or CompressionFormat.Gzip or CompressionFormat.Lz4Frame or CompressionFormat.Lz4Block;
        Fuzz.Run(Fuzz.Cases(4101 + (int)format, seeds, mutations: own ? 4000 : 800, randoms: own ? 500 : 100), data =>
        {
            using Document doc = Open(data);
            var output = new CodecOutput(null) { Limit = Limit, LimitReached = _ => false };
            try
            {
                DecompressionResult result = CompressionEngine.Decompress(doc.Current, 0, data.Length, format, output);
                Assert.InRange(result.ConsumedInput, 0, data.Length);
            }
            finally
            {
                Assert.True(output.Length <= Limit + 8 * 1024 * 1024, $"wrote {output.Length} bytes past the limit");
            }
        }, allowed: ex => ex is CompressedDataException or OutputLimitAbortedException);
    }

    [Fact]
    public void Automatic_detection_never_throws_on_mutated_streams()
    {
        var seeds = new List<byte[]>();
        foreach (CompressionFormat format in Enum.GetValues<CompressionFormat>())
        {
            seeds.Add(Compress(Sample(5, 3000), format, CompressionFormats.Info(format).DefaultLevel));
        }

        Fuzz.Run(Fuzz.Cases(4110, seeds, mutations: 1500, randoms: 200), data =>
        {
            using Document doc = Open(data);
            _ = CompressionEngine.DecompressAuto(doc.Current, 0, data.Length, () => new CodecOutput(null) { Limit = 32L << 20, LimitReached = _ => false });
        }, allowed: ex => ex is OutputLimitAbortedException);
    }

    /// <summary>LZ4 のフレームのブロックは、展開の途中でもブロックの最大の大きさを超えたら止まる (1 ブロックで際限なく書かない)。</summary>
    [Fact]
    public void Lz4_frame_block_cannot_expand_past_the_block_maximum()
    {
        // 64 KiB のブロックのフレーム (FLG 0x60、BD 0x40)。1 つのブロックに長い一致 (約 1 GiB) を入れる。
        var block = new MemoryStream();
        block.WriteByte(0x1F); // リテラル 1、一致 15 + 続き
        block.WriteByte((byte)'a');
        block.WriteByte(1);
        block.WriteByte(0);
        for (int i = 0; i < 4_000_000; i++)
        {
            block.WriteByte(255);
        }

        block.WriteByte(0);
        block.WriteByte(0x10); // 最後のシーケンス (リテラル 1)
        block.WriteByte((byte)'b');
        byte[] blockBytes = block.ToArray();
        var frame = new MemoryStream();
        frame.Write(BitConverter.GetBytes(Lz4Decoder.FrameMagic));
        byte[] descriptor = [0x60, 0x40];
        frame.Write(descriptor);
        frame.WriteByte((byte)(K4os.Hash.xxHash.XXH32.DigestOf(descriptor) >> 8));
        Assert.True(blockBytes.Length > 64 * 1024);

        // ブロックの大きさの上限 (64 KiB) を超えるブロックはヘッダで、超えない長さでも展開した量で止まる。
        byte[] small = blockBytes[..(60 * 1024)];
        small[^2] = 0;
        frame.Write(BitConverter.GetBytes(small.Length));
        frame.Write(small);
        frame.Write(BitConverter.GetBytes(0));
        byte[] data = frame.ToArray();
        using Document doc = Open(data);
        var output = new CodecOutput(null);
        CompressedDataException e = Assert.Throws<CompressedDataException>(() => CompressionEngine.Decompress(doc.Current, 0, data.Length, CompressionFormat.Lz4Frame, output));
        Assert.Equal(CompressedDataError.Corrupt, e.Error);
        Assert.True(output.Length <= 64 * 1024 + 1024 * 1024 + 64 * 1024, $"wrote {output.Length} bytes");
    }

    // ---- パッチ ----

    private static List<byte[]> PatchSeeds()
    {
        byte[] source = Sample(42, 4096);
        var seeds = new List<byte[]>();
        foreach (PatchFormat format in new[] { PatchFormat.Ips, PatchFormat.Ips32, PatchFormat.Ups, PatchFormat.Bps, PatchFormat.HexPatch })
        {
            foreach (int variant in new[] { 0, 1 })
            {
                using Document doc = Open(source);
                doc.Overwrite(10, [1, 2, 3]);
                doc.Overwrite(2000, Sample(7, 300));
                if (variant == 1 && format is not (PatchFormat.Ips or PatchFormat.Ips32))
                {
                    doc.Insert(100, Sample(8, 50));
                    doc.Delete(3000, 200);
                }

                var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
                using var ms = new MemoryStream();
                creator.Write(format, ms);
                seeds.Add(ms.ToArray());
            }
        }

        return seeds;
    }

    [Fact]
    public void Patch_readers_and_appliers_survive_mutated_patches()
    {
        byte[] source = Sample(42, 4096);
        Fuzz.Run(Fuzz.Cases(4201, PatchSeeds(), mutations: 4000, randoms: 300), data =>
        {
            using LoadedPatch patch = PatchFiles.Load(new MemoryStream(data));
            _ = patch.RecordCount;
            _ = patch.ChangedBytes;
            foreach (bool reverse in patch.CanReverse ? new[] { false, true } : [false])
            {
                using Document doc = Open(source);
                _ = patch.Verify(doc.Current, reverse);
                long length = patch.ResultLength(doc.Length, reverse);
                if (length > 64L * 1024 * 1024)
                {
                    // 適用後の確認が全体を読む (時間がかかる) ため、大きすぎる結果は当てない。
                    continue;
                }

                patch.ApplyTo(doc, reverse);
            }
        }, allowed: ex => ex is PatchException or FixedLengthException);
    }

    /// <summary>.hexpatch の命令の範囲があふれる値 (long の上限の近く) は、読み込みで壊れたパッチになる。</summary>
    [Theory]
    [InlineData("9223372036854775800", "100", "9223372036854775800", "100")]
    [InlineData("0", "9223372036854775807", "0", "9223372036854775807")]
    [InlineData("-5", "10", "-5", "10")]
    public void HexPatch_ops_that_overflow_are_corrupt(string src, string srcLen, string dst, string dstLen)
    {
        string identity = "{\"length\":4096,\"headSha256\":\"\",\"middleSha256\":\"\",\"tailSha256\":\"\"}";
        string json = $"{{\"format\":\"hexpatch\",\"version\":1,\"source\":{identity},\"target\":{identity},\"ops\":[{{\"op\":\"replace\",\"src\":{src},\"srcLen\":{srcLen},\"dst\":{dst},\"dstLen\":{dstLen}}}]}}";
        byte[] zip = Zip(("hexpatch.json", Encoding.UTF8.GetBytes(json)), ("data.bin", []));
        PatchException e = Assert.Throws<PatchException>(() => PatchFiles.Load(new MemoryStream(zip)));
        Assert.Equal(PatchError.Corrupt, e.Error);
    }

    /// <summary>BPS のメタデータの長さがあふれる値でも壊れたパッチになる (ストリームの位置が負にならない)。</summary>
    [Fact]
    public void Bps_metadata_length_that_overflows_is_corrupt()
    {
        var body = new MemoryStream();
        body.Write("BPS1"u8);
        WriteVarint(body, 0);
        WriteVarint(body, 0);
        WriteVarint(body, (ulong)long.MaxValue);
        body.Write(new byte[8]);
        body.Write(BitConverter.GetBytes(PatchCrc32.Compute(body.ToArray())));
        Assert.Equal(PatchError.Corrupt, Assert.Throws<PatchException>(() => PatchFiles.Load(new MemoryStream(body.ToArray()))).Error);
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (true)
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            if (v == 0)
            {
                s.WriteByte((byte)(x | 0x80));
                return;
            }

            s.WriteByte(x);
            v--;
        }
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                using Stream s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    // ---- 履歴ファイル ----

    private static (byte[] Source, byte[] Archive, string Json, byte[] Data) HistorySeed()
    {
        byte[] source = Sample(21, 4096);
        using Document doc = Open(source);
        doc.Overwrite(10, [1, 2, 3]);
        doc.Insert(100, Sample(9, 40));
        doc.Undo();
        doc.Delete(500, 10);
        doc.InsertPattern(700, 1000, [0xAB, 0xCD]);
        using var ms = new MemoryStream();
        HistoryArchive.Save(doc, ms);
        byte[] archive = ms.ToArray();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        string json = new StreamReader(zip.GetEntry("history.json")!.Open()).ReadToEnd();
        using var data = new MemoryStream();
        zip.GetEntry("data.bin")!.Open().CopyTo(data);
        return (source, archive, json, data.ToArray());
    }

    [Fact]
    public void History_archive_reader_survives_mutated_archives()
    {
        (byte[] source, byte[] archive, string json, byte[] data) = HistorySeed();
        var random = new Random(2101);
        var cases = new List<byte[]>(Fuzz.Cases(2102, [archive], mutations: 800));
        const string alphabet = "0123456789-\"[]{},:dopr9e";
        for (int i = 0; i < 3000; i++)
        {
            cases.Add(Zip(("history.json", Encoding.UTF8.GetBytes(Fuzz.MutateText(random, json, alphabet))), ("data.bin", data)));
        }

        Fuzz.Run(cases, bytes =>
        {
            using Document doc = Open(source);
            _ = HistoryArchive.ReadInfo(new MemoryStream(bytes));
            using HistoryArchive.PreparedHistory prepared = HistoryArchive.Prepare(doc, new MemoryStream(bytes));
            prepared.ApplyTo(doc);

            // 読み込んだ履歴のどの節も、ドキュメントの範囲の中だけを指す。
            foreach (HistoryNode node in doc.History.AllNodes())
            {
                DocumentSnapshot s = node.Entry.Snapshot;
                byte[] buffer = new byte[Math.Min(s.Length, 1 << 16)];
                _ = s.Read(0, buffer);
                _ = s.Read(Math.Max(0, s.Length - buffer.Length), buffer);
            }
        }, allowed: ex => ex is HistoryArchiveException);
    }

    /// <summary>元ファイルを指すピースの位置と長さの和があふれる値は、元ファイルの範囲外として読み込まない。</summary>
    [Theory]
    [InlineData("9223372036854775800", "100")]
    [InlineData("0", "-5")]
    [InlineData("-1", "1")]
    public void History_pieces_that_overflow_are_rejected(string offset, string length)
    {
        (byte[] source, _, string json, byte[] data) = HistorySeed();
        using JsonDocument parsed = JsonDocument.Parse(json);
        string first = parsed.RootElement.GetProperty("nodes")[0].GetProperty("pieces")[0].GetRawText();
        string broken = json.Replace(first, $"[\"o\",{offset},{length}]", StringComparison.Ordinal);
        Assert.NotEqual(json, broken);
        byte[] bytes = Zip(("history.json", Encoding.UTF8.GetBytes(broken)), ("data.bin", data));
        using Document doc = Open(source);
        Assert.Throws<HistoryArchiveException>(() =>
        {
            using HistoryArchive.PreparedHistory prepared = HistoryArchive.Prepare(doc, new MemoryStream(bytes));
            prepared.ApplyTo(doc);
        });
    }

    // ---- スナップショット ----

    [Fact]
    public void Snapshot_reader_survives_mutated_metadata()
    {
        string json = """
            {"processName":"dummy","pid":1,"architecture":"X64","capturedUtc":"2026-10-01T00:00:00Z","addressLimit":1048576,
             "regions":[{"baseAddress":4096,"size":8192,"protect":4,"type":"Private","moduleName":null,"fileOffset":4096},
                        {"baseAddress":65536,"size":4096,"protect":2,"type":"Image","moduleName":"a.dll","fileOffset":12288}],
             "modules":[{"name":"a.dll","path":"C:\\a.dll","baseAddress":65536,"size":4096}],
             "unreadablePages":[{"baseAddress":8192,"size":4096}]}
            """;
        var random = new Random(3501);
        var cases = new List<byte[]>();
        const string alphabet = "0123456789-\"[]{},:enul";
        for (int i = 0; i < 1500; i++)
        {
            cases.Add(SnapshotFile(i == 0 ? json : Fuzz.MutateText(random, json, alphabet)));
        }

        cases.AddRange(Fuzz.Cases(3502, [SnapshotFile(json)], mutations: 500).Skip(1));
        int n = 0;
        Fuzz.Run(cases, bytes =>
        {
            string path = Path.Combine(_dir, "s" + n++ + ".hexsnap");
            File.WriteAllBytes(path, bytes);
            try
            {
                using SnapshotByteSource source = SnapshotByteSource.Open(path);
                byte[] buffer = new byte[64 * 1024];
                foreach (SourceRegion region in source.Regions.Take(50))
                {
                    _ = source.Read(region.Offset, buffer);
                    _ = source.Read(Math.Max(0, region.Offset + region.Length - 10), buffer.AsSpan(0, 100));
                }

                _ = source.Read(0, buffer);
                _ = source.Read(Math.Max(0, source.Length - 100), buffer);
            }
            finally
            {
                File.Delete(path);
            }
        }, allowed: ex => ex is InvalidDataException);
    }

    private static byte[] SnapshotFile(string json)
    {
        byte[] meta = Encoding.UTF8.GetBytes(json);
        var ms = new MemoryStream();
        ms.Write(HexSnapshot.Magic);
        ms.Write(BitConverter.GetBytes(meta.Length));
        ms.Write(meta);
        ms.SetLength(4096 + 4096 * 4);
        return ms.ToArray();
    }
}
