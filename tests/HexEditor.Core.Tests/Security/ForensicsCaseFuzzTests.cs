using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Search;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// フェーズ 5 のケース・カービング・監査ログの、信頼できない入力 (ディスクの内容、読み込むファイル) のファジング。カービングの構造の検証は
/// 壊れたデータを読むので、種の入力を変異させたものと乱数のデータを与えて、想定外の例外・無限ループ・大量のメモリがないことを確かめる。
/// </summary>
public sealed class ForensicsCaseFuzzTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "fuzz-case-" + Guid.NewGuid().ToString("N"));

    public ForensicsCaseFuzzTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] GzipSample()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("fuzz seed text for gzip ", 80))));
        }

        return ms.ToArray();
    }

    private static byte[] Bzip2Sample() =>
        [.. "BZh9"u8, 0x31, 0x41, 0x59, 0x26, 0x53, 0x59, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0x17, 0x72, 0x45, 0x38, 0x50, 0x90, 0xDE, 0xAD, 0xBE, 0xEF];

    private static byte[] Mp3Sample()
    {
        var f = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 12; i++)
        {
            byte[] frame = new byte[417];
            frame[0] = 0xFF;
            frame[1] = 0xFB;
            frame[2] = 0x90;
            f.AddRange(frame);
        }

        return [.. f];
    }

    private static byte[] Mp4Sample()
    {
        var f = new List<byte>();
        foreach ((string type, int size) in new[] { ("ftyp", 24), ("moov", 40), ("mdat", 64) })
        {
            byte[] b = new byte[size];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            f.AddRange(b);
        }

        return [.. f];
    }

    private static byte[] TiffSample()
    {
        byte[] t = new byte[66];
        "II*\0"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), 3);
        int e = 10;
        foreach ((int tag, int type, int value) in new[] { (256, 3, 1), (273, 4, 50), (279, 4, 16) })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(e), (ushort)tag);
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(e + 2), (ushort)type);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(e + 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(e + 8), (uint)value);
            e += 12;
        }

        return t;
    }

    private static byte[] OleSample()
    {
        byte[] f = new byte[512 * 3];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x44), 0xFFFFFFFE);
        for (int i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x4C + 4 * i), i == 0 ? 0u : 0xFFFFFFFF);
        }

        for (int i = 0; i < 128; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(512 + 4 * i), i == 0 ? 0xFFFFFFFD : i == 1 ? 0xFFFFFFFE : 0xFFFFFFFF);
        }

        return f;
    }

    private static byte[] ElfSample()
    {
        byte[] f = new byte[300];
        new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x20), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x28), 200);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x3A), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x3C), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(64 + 32), 200);
        return f;
    }

    private static byte[] SevenZipSample()
    {
        byte[] f = new byte[80];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(20), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), StructureValidators.Crc32(f.AsSpan(12, 20)));
        return f;
    }

    private static byte[] RarSample() =>
        [.. "Rar!\x1A\x07\x00"u8, 0, 0, 0x73, 0, 0, 13, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x7B, 0, 0, 7, 0];

    /// <summary>各構造の検証が、壊れたデータで想定外の例外・無限ループ・大量のメモリを起こさない。</summary>
    [Fact]
    public void StructureValidatorsSurviveMutatedAndRandomData()
    {
        (string Name, byte[] Seed)[] seeds =
        [
            ("png", TestDataCatalog.Png16()), ("jpeg", TestDataCatalog.ForJpeg()), ("jpeg", TestDataCatalog.Jpeg16()), ("pdf", TestDataCatalog.ForPdf()),
            ("zip", TestDataCatalog.ZipSample()), ("zip", TestDataCatalog.Zip3()), ("gzip", GzipSample()), ("bzip2", Bzip2Sample()), ("mp3", Mp3Sample()),
            ("mp4", Mp4Sample()), ("tiff", TiffSample()), ("ole", OleSample()), ("elf", ElfSample()), ("7z", SevenZipSample()), ("rar", RarSample()),
            ("pe", TestDataCatalog.PeX64()), ("rtf", "{\\rtf1 {\\b x}}"u8.ToArray()), ("html", "<html><body>x</body></html>"u8.ToArray()),
            ("xml", "<?xml version=\"1.0\"?><a><b/></a>"u8.ToArray()), ("gif", [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x4C, 1, 0, 0x3B]),
            ("sqlite", [.. "SQLite format 3\0"u8, 16, 0, .. new byte[200]]), ("evtx", [.. "ElfFile\0"u8, .. new byte[100]]), ("lnk", [0x4C, 0, 0, 0, 1, 0x14, 2, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46, .. new byte[70]]),
            ("bmp", [.. "BM"u8, 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 24, 0, .. new byte[44]]),
        ];

        foreach (IGrouping<string, (string Name, byte[] Seed)> group in seeds.GroupBy(s => s.Name))
        {
            IStructureValidator validator = StructureValidators.Find(group.Key)!;
            List<byte[]> seedData = [.. group.Select(g => g.Seed)];
            Fuzz.Run(Fuzz.Cases(group.Key.GetHashCode() & 0xFFFF, seedData, mutations: 400, randoms: 60, maxRandomLength: 2048, maxLength: 1 << 16), data =>
            {
                var window = new ByteWindow(new MemoryCarveData(data), data.Length, 1 << 22);
                long? length = validator.Measure(window, 0);
                if (length is { } l)
                {
                    Assert.InRange(l, 1, data.Length);
                }
            }, timeoutMs: 20_000, maxAllocatedBytes: 64L * 1024 * 1024);
        }
    }

    [Fact]
    public void DeflateSkipperAndWindowSurviveRandomStreams()
    {
        var seeds = new List<byte[]> { GzipSample() };
        Fuzz.Run(Fuzz.Cases(77, seeds, mutations: 500, randoms: 300, maxRandomLength: 4096), data =>
        {
            var window = new ByteWindow(new MemoryCarveData(data), data.Length, 1 << 22);
            long? end = DeflateSkipper.FindEnd(window, 0);
            Assert.True(end is null || end <= data.Length);
        });
    }

    /// <summary>全部の組み込みの定義でカービングを走らせても、乱数のデータで例外や暴走が起きない。</summary>
    [Fact]
    public void CarvingEngineSurvivesRandomImagesWithEveryBuiltInSignature()
    {
        var random = new Random(5);
        byte[][] images =
        [
            RandomImage(random, 256 * 1024, plant: true),
            RandomImage(random, 64 * 1024, plant: true),
            new byte[300 * 1024],
        ];
        foreach (byte[] image in images)
        {
            CarvingSignature[] unaligned = [.. BuiltInSignatures.All.Select(s => s with { SectorAligned = false })];
            foreach (CarvingSignature[] sigs in new[] { [.. BuiltInSignatures.All], unaligned })
            {
                Fuzz.Run(new[] { image }, data =>
                {
                    CarvingResult r = CarvingEngine.RunAsync(new MemoryCarveData(data), new CarvingOptions
                    {
                        Signatures = sigs,
                        Ranges = [new SearchRange(0, data.Length)],
                        Export = false,
                        ChunkSize = 32 * 1024,
                        Parallelism = 2,
                    }).GetAwaiter().GetResult();
                    foreach (CarvedFile f in r.Files.Enumerate())
                    {
                        Assert.True(f.Offset >= 0 && f.Length > 0 && f.End <= data.Length, $"{f.Type} {f.Offset} {f.Length}");
                    }
                }, timeoutMs: 60_000, maxAllocatedBytes: 1024L * 1024 * 1024, describe: _ => "random image");
            }
        }
    }

    private static byte[] RandomImage(Random random, int length, bool plant)
    {
        byte[] data = new byte[length];
        random.NextBytes(data);
        if (plant)
        {
            // セクタの先頭に、各形式のヘッダーの断片 (壊れたファイルの先頭) を置く。
            foreach (CarvingSignature s in BuiltInSignatures.All)
            {
                long at = random.Next(0, length / 512 - 2) * 512L + s.HeaderOffset;
                byte[] header = s.ParsedHeaders()[0].Value;
                header.CopyTo(data, at);
            }
        }

        return data;
    }

    [Fact]
    public void SignatureJsonAndHexPatternsSurviveMutatedText()
    {
        string seed = Encoding.UTF8.GetString(TestDataCatalog.ForSigMixed());
        var random = new Random(9);
        var cases = new List<string> { seed };
        for (int i = 0; i < 600; i++)
        {
            cases.Add(Fuzz.MutateText(random, seed, "{}[]\":,0123456789abcdefABCDEF?xX \n\\-"));
        }

        Fuzz.Run(cases, text =>
        {
            var store = new SignatureStore(null);
            store.Import(text);
        }, allowed: ex => ex is SignatureFileException);

        Fuzz.Run(Enumerable.Range(0, 800).Select(i => Fuzz.MutateText(random, "CA FE ?? B? 0xDE \\xAD", "0123456789abcdefABCDEFxX?\\ ,;\n zZ")), text =>
        {
            HexPatternError error = HexMaskPattern.TryParse(text, out HexMaskPattern? pattern);
            Assert.Equal(error == HexPatternError.None, pattern is not null);
            if (pattern is not null)
            {
                Assert.True(pattern.Length > 0 && pattern.AnchorLength > 0);
                _ = HexMaskPattern.Parse(pattern.ToString());
            }

            _ = SizeInput.TryParse(text, out _);
        });
    }

    [Fact]
    public void CaseFileAndAuditLogReadersSurviveMutatedFiles()
    {
        string folder = Path.Combine(_dir, "case");
        var manager = new CaseManager(new ForensicAudit(new AuditIdentity("u", "pc", "1")), "u") { AppVersion = "1" };
        manager.Create(folder, new CaseInfo { Number = "C-1", Name = "n" });
        manager.Register(new EvidenceRegistration { Kind = EvidenceKind.File, Path = Path.Combine(_dir, "x.bin"), Hashes = [new("MD5", "ab")] });
        manager.Close();
        string caseJson = File.ReadAllText(Path.Combine(folder, "case.json"));
        string auditLog = File.ReadAllText(Path.Combine(folder, "audit.log"));
        var random = new Random(3);

        string mutatedFolder = Path.Combine(_dir, "mutated");
        Directory.CreateDirectory(mutatedFolder);
        Fuzz.Run(Enumerable.Range(0, 500).Select(_ => Fuzz.MutateText(random, caseJson, "{}[]\":,0123456789abcdef \n\\nulltrue")), text =>
        {
            File.WriteAllText(Path.Combine(mutatedFolder, "case.json"), text);
            try
            {
                ForensicCase.Open(mutatedFolder, "u");
            }
            catch (Exception ex) when (ex is CaseFileException or CaseFolderException)
            {
            }
        });

        Fuzz.Run(Enumerable.Range(0, 300).Select(_ => Fuzz.MutateText(random, auditLog, "{}[]\":,0123456789abcdef \n\\")), text =>
        {
            string path = Path.Combine(mutatedFolder, "audit.log");
            File.WriteAllText(path, text);
            AuditVerifyResult verified = AuditVerifier.Verify(path);
            Assert.True(verified.Breaks.Count <= AuditVerifier.MaxBreaks);
            using var reader = new AuditLogReader(path);
            for (long i = 0; i < reader.Count; i++)
            {
                _ = reader.Read(i);
            }

            _ = AuditLog.Open(path, new AuditIdentity("u", "pc", "1"));
        });
    }
}
