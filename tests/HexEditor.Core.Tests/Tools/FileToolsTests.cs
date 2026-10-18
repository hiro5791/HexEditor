using System.Security.Cryptography;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tools.Files;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-13 ファイルの分割、TOOL-14 ファイルの結合、TOOL-15 インターリーブとデインターリーブ。</summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-ftools").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ShaOfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static SplitResult Split(string file, SplitOptions options, out SplitPlan plan)
    {
        using var reader = new FileContentReader(file);
        plan = FileSplitter.Plan(reader.Length, System.IO.Path.GetFileName(file), System.IO.Path.GetDirectoryName(file)!, options);
        return FileSplitter.Execute(plan, reader, options);
    }

    // ---- TOOL-13 ----

    /// <summary>
    /// 10 GiB のスパースなファイルを 4G で分け、結合して元と一致する (毎晩。出力先に 25 GB 以上の空き容量が要る)。
    /// </summary>
    [Fact]
    [Trait("Category", "Nightly")]
    [Trait(TC, "TC-TOOL-13-01")]
    public void TenGibFileSplitInto4GibPartsJoinsBackToTheSameHash()
    {
        const long GiB = 1L << 30;
        string big = Path("big.bin");
        using (var stream = new FileStream(big, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(10 * GiB);
            SparseFiles.TryMakeSparse(stream.SafeFileHandle);
            foreach (long at in new[] { 0L, 4 * GiB, 8 * GiB, 10 * GiB - 17 })
            {
                stream.Position = at;
                stream.Write(TestDataCatalog.Marker(at));
            }
        }

        string expected = ShaOfFile(big);
        SplitResult result = Split(big, new SplitOptions { Mode = SplitMode.Size, PartSize = 4 * GiB }, out SplitPlan plan);
        Assert.True(result.Succeeded);
        Assert.Equal(["big.bin.001", "big.bin.002", "big.bin.003"], plan.Parts.Select(p => p.FileName));
        Assert.Equal([4 * GiB, 4 * GiB, 2 * GiB], plan.Parts.Select(p => new FileInfo(plan.PathOf(p)).Length));

        string joined = Path("joined.bin");
        JoinPlan joinPlan = FileJoiner.Plan([.. plan.Parts.Select(plan.PathOf)], new JoinOptions());
        FileJoiner.WriteToFile(joinPlan, new JoinOptions(), joined);
        Assert.Equal(expected, ShaOfFile(joined));
    }

    [Fact]
    public void SizeSplitProducesTheExpectedPartsAndTheyJoinBackToTheOriginal()
    {
        string file = Path("big.bin");
        byte[] data = new byte[10 * 1024 * 1024 + 123];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(file, data);

        SplitResult result = Split(file, new SplitOptions { Mode = SplitMode.Size, PartSize = 4 * 1024 * 1024 }, out SplitPlan plan);
        Assert.True(result.Succeeded);
        Assert.Equal(["big.bin.001", "big.bin.002", "big.bin.003"], plan.Parts.Select(p => p.FileName));
        Assert.Equal([4L << 20, 4L << 20, (2L << 20) + 123], plan.Parts.Select(p => new FileInfo(plan.PathOf(p)).Length));
        Assert.Empty(Directory.GetFiles(_dir, "*.~part"));

        string joined = Path("joined.bin");
        JoinPlan joinPlan = FileJoiner.Plan([.. plan.Parts.Select(plan.PathOf)], new JoinOptions());
        FileJoiner.WriteToFile(joinPlan, new JoinOptions(), joined);
        Assert.Equal(Sha(file), Sha(joined));
    }

    [Fact]
    public void CountSplitRoundsUpAndAlignsTheSizes()
    {
        SplitPlan plan = FileSplitter.Plan(1_048_578, "u.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 2 });
        Assert.Equal([524_289L, 524_289L], plan.Parts.Select(p => p.Length));

        SplitPlan aligned = FileSplitter.Plan(1000, "u.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 3, Alignment = 512 });
        Assert.Equal([512L, 488L], aligned.Parts.Select(p => p.Length));

        Assert.Equal(FileToolError.InvalidArgument, Assert.Throws<FileToolException>(() =>
            FileSplitter.Plan(10, "u.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 1 })).Error);
        Assert.Equal(FileToolError.InvalidArgument, Assert.Throws<FileToolException>(() =>
            FileSplitter.Plan(10, "u.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 10_001 })).Error);
    }

    [Fact]
    public void NameFormatSymbolsAreExpanded()
    {
        SplitPlan plan = FileSplitter.Plan(30, "dir.tar.gz", _dir, new SplitOptions
        {
            Mode = SplitMode.Size,
            PartSize = 10,
            NameFormat = "{base}_{index:00}_{offset:X4}{ext}",
        });
        Assert.Equal(["dir.tar_01_0000.gz", "dir.tar_02_000A.gz", "dir.tar_03_0014.gz"], plan.Parts.Select(p => p.FileName));
        Assert.Throws<FileToolException>(() => FileSplitter.Plan(30, "x.bin", _dir, new SplitOptions { PartSize = 10, NameFormat = "same" }));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-13-02")]
    public void SplitAtBookmarkPositionsGivesTheExpectedParts()
    {
        string s = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        string copy = Path("s.bin");
        File.Copy(s, copy);
        using var doc = new Document(FileByteSource.Open(copy));
        BookmarkCollection bookmarks = BookmarkCollection.Attach(doc);
        bookmarks.Add(0x1000, 1, "a");
        bookmarks.Add(0x80000, 1, "b");

        var options = new SplitOptions { Mode = SplitMode.Positions, Positions = FileSplitter.PositionsFromBookmarks(bookmarks) };
        using var reader = new SnapshotContentReader(doc.Current);
        SplitPlan plan = FileSplitter.Plan(reader.Length, "s.bin", _dir, options);
        Assert.Equal((3, "s.bin.001", "s.bin.003", 1_048_576L), (plan.FileCount, plan.FirstName, plan.LastName, plan.TotalBytes));
        Assert.True(FileSplitter.Execute(plan, reader, options).Succeeded);

        Assert.Equal([0x1000L, 0x7F000L, 0x80000L], plan.Parts.Select(p => new FileInfo(plan.PathOf(p)).Length));
        byte[] second = File.ReadAllBytes(plan.PathOf(plan.Parts[1]));
        byte[] third = File.ReadAllBytes(plan.PathOf(plan.Parts[2]));
        Assert.Equal((byte)0x00, second[0]);
        Assert.Equal((byte)0x01, second[1]);
        Assert.Equal((byte)0x00, third[0]);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-13-03")]
    public void SplitDoesNotRunWhenTheOutputVolumeHasNoRoom()
    {
        SplitPlan plan = FileSplitter.Plan(16 * 1024 * 1024, "r.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 2, OutputDirectory = Path("out") });
        SpaceShortage? shortage = FileSplitter.CheckSpace(plan, new FixedSpace(8 * 1024 * 1024));
        Assert.NotNull(shortage);
        Assert.Equal((16L * 1024 * 1024, 8L * 1024 * 1024), (shortage.Required, shortage.Available));
        Assert.Empty(Directory.GetFiles(Path("out")));

        Assert.Null(FileSplitter.CheckSpace(plan, new FixedSpace(64 * 1024 * 1024)));
    }

    private sealed class FixedSpace(long free) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("T:", "NTFS", free);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-13-04")]
    public void UnsavedChangesAreSplitWithTheirNewContent()
    {
        string copy = Path("u.bin");
        File.Copy(TestDataCatalog.Generate("TD-SEQ-1M", _dir), copy);
        string before = Sha(copy);
        using var doc = new Document(FileByteSource.Open(copy));
        doc.Overwrite(0, [0xEE]);
        doc.Insert(0x10, [0x11, 0x22]);

        var options = new SplitOptions { Mode = SplitMode.Count, Count = 2 };
        using var reader = new SnapshotContentReader(doc.Current);
        SplitPlan plan = FileSplitter.Plan(reader.Length, "u.bin", _dir, options);
        Assert.True(FileSplitter.Execute(plan, reader, options).Succeeded);

        Assert.Equal(1_048_578L, plan.TotalBytes);
        Assert.Equal(524_289L, plan.Parts[0].Length);
        byte[] first = File.ReadAllBytes(plan.PathOf(plan.Parts[0]))[..0x14];
        byte[] expected = [0xEE, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0x11, 0x22, 0x10, 0x11];
        Assert.Equal(expected, first);
        Assert.Equal(before, Sha(copy));
    }

    [Fact]
    public void JoinScriptAndChecksumFileAreCreatedOnRequest()
    {
        string file = Path("a.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        var options = new SplitOptions { Mode = SplitMode.Size, PartSize = 40, CreateJoinScript = true, CreateChecksumFile = true };
        SplitResult result = Split(file, options, out SplitPlan plan);
        Assert.True(result.Succeeded);
        string script = File.ReadAllText(Path("a.bin.join.cmd"));
        Assert.Contains("copy /b \"a.bin.001\"+\"a.bin.002\"+\"a.bin.003\" \"a.bin\"", script, StringComparison.Ordinal);
        string[] sums = File.ReadAllLines(plan.ExtraFiles.Single(f => !f.EndsWith(".cmd", StringComparison.Ordinal)));
        Assert.Equal(4, sums.Length);
        Assert.StartsWith(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), sums[0], StringComparison.Ordinal);
        Assert.EndsWith("a.bin.002", sums[2], StringComparison.Ordinal);
        Assert.Equal(plan.ExtraFiles.Count + plan.Parts.Count, plan.AllOutputs.Count);
    }

    [Fact]
    public void CancelledSplitDeletesTheUnfinishedFileAndKeepsTheCompletedOnes()
    {
        string file = Path("c.bin");
        File.WriteAllBytes(file, new byte[10 * 1024 * 1024]);
        using var reader = new FileContentReader(file);
        var options = new SplitOptions { Mode = SplitMode.Size, PartSize = 4 * 1024 * 1024 };
        SplitPlan plan = FileSplitter.Plan(reader.Length, "c.bin", _dir, options);
        using var cts = new CancellationTokenSource();
        var cancelling = new CancelAfterFirstRead(reader, cts);
        SplitResult result = FileSplitter.Execute(plan, cancelling, options, cancellationToken: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(Directory.GetFiles(_dir, "*.~part"));
        Assert.All(result.Completed, f => Assert.True(File.Exists(f)));
        FileSplitter.DeleteCompleted(result.Completed);
        Assert.DoesNotContain(Directory.GetFiles(_dir).Select(System.IO.Path.GetFileName), n => n!.StartsWith("c.bin.", StringComparison.Ordinal));
    }

    private sealed class CancelAfterFirstRead(IContentReader inner, CancellationTokenSource cts) : IContentReader
    {
        private int _reads;

        public long Length => inner.Length;

        public int Read(long offset, Span<byte> buffer)
        {
            if (++_reads == 2)
            {
                cts.Cancel();
            }

            return inner.Read(offset, buffer);
        }

        public void Dispose()
        {
        }
    }

    // ---- TOOL-14 ----

    [Fact]
    [Trait(TC, "TC-TOOL-14-02")]
    public void AddingTheFirstNumberedFileFindsTheFollowingOnesInNaturalOrder()
    {
        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllBytes(Path($"p.bin.{i:000}"), new byte[i]);
        }

        File.WriteAllBytes(Path("q.bin.001"), [1]);
        File.WriteAllBytes(Path("p.bin"), [1]);
        IReadOnlyList<string> found = FileJoiner.FindSequence(Path("p.bin.001"));
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"p.bin.{i:000}"), found.Select(System.IO.Path.GetFileName));
        Assert.Equal(["p.bin.011", "p.bin.012"], FileJoiner.FindSequence(Path("p.bin.011")).Select(System.IO.Path.GetFileName));
        Assert.Empty(FileJoiner.FindSequence(Path("p.bin")));

        string[] scrambled = [Path("p.bin.010"), Path("p.bin.009"), Path("p.bin.002")];
        Assert.Equal(["p.bin.002", "p.bin.009", "p.bin.010"], FileJoiner.SortNatural(scrambled).Select(System.IO.Path.GetFileName));
        string[] names = [Path("file10.bin"), Path("file9.bin"), Path("File2.bin")];
        Assert.Equal(["File2.bin", "file9.bin", "file10.bin"], FileJoiner.SortNatural(names).Select(System.IO.Path.GetFileName));
        Assert.Equal(["file10.bin", "File2.bin", "file9.bin"], FileJoiner.SortByName(names).Select(System.IO.Path.GetFileName));

        JoinPlan plan = FileJoiner.Plan(found, new JoinOptions());
        Assert.Equal(Enumerable.Range(1, 12).Sum(), plan.TotalLength);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-14-03")]
    public void AlignedJoinStartsEveryFileOnAMultipleOf512()
    {
        string[] files = [Path("a.bin"), Path("b.bin"), Path("c.bin")];
        File.WriteAllBytes(files[0], Enumerable.Repeat((byte)0xA1, 100).ToArray());
        File.WriteAllBytes(files[1], Enumerable.Repeat((byte)0xB2, 700).ToArray());
        File.WriteAllBytes(files[2], [0xC3, 0xC3, 0xC3]);
        var options = new JoinOptions { AlignTo = 512, PadValue = 0x00 };
        JoinPlan plan = FileJoiner.Plan(files, options);
        Assert.Equal([0L, 512L, 1536L], plan.Entries.Select(e => e.Start));
        Assert.Equal(1539L, plan.TotalLength);

        string output = Path("joined.bin");
        FileJoiner.WriteToFile(plan, options, output);
        byte[] result = File.ReadAllBytes(output);
        Assert.Equal(1539, result.Length);
        Assert.Equal(0xA1, result[0]);
        Assert.Equal(0xB2, result[512]);
        Assert.Equal(0xC3, result[1536]);
        Assert.All(result[100..512], b => Assert.Equal(0, b));
        Assert.All(result[1212..1536], b => Assert.Equal(0, b));

        // 新しいドキュメントとしても同じ内容 (詰め物はパターンのピース)。
        using Document doc = FileJoiner.CreateDocument(plan, options, "joined", new DocumentOptions { TempDirectory = Path("recovery") });
        byte[] content = new byte[doc.Length];
        doc.Current.Read(0, content);
        Assert.Equal(result, content);
    }

    [Fact]
    public void JoinReportsUnreadableFilesAndDoesNotRun()
    {
        string a = Path("a.bin");
        File.WriteAllBytes(a, [1]);
        JoinPlan plan = FileJoiner.Plan([a, Path("missing.bin")], new JoinOptions());
        Assert.False(plan.CanRun);
        Assert.Equal(Path("missing.bin"), Assert.Single(plan.Unreadable).Path);
        Assert.Throws<FileToolException>(() => FileJoiner.WriteToFile(plan, new JoinOptions(), Path("x.bin")));
    }

    // ---- TOOL-15 ----

    [Fact]
    [Trait(TC, "TC-TOOL-15-01")]
    public void DeinterleavingFourBytesIntoTwoGivesEvenAndOdd()
    {
        string rom = Path("rom.bin");
        File.WriteAllBytes(rom, [0x00, 0x11, 0x22, 0x33]);
        var options = new DeinterleaveOptions { Count = 2, Unit = 1 };
        using var reader = new FileContentReader(rom);
        DeinterleavePlan plan = Interleaver.PlanDeinterleave(reader.Length, "rom.bin", _dir, options);
        Assert.Equal([2L, 2L], plan.Outputs.Select(o => o.Length));
        Assert.True(Interleaver.Deinterleave(plan, reader, options).Succeeded);
        Assert.Equal([0x00, 0x22], File.ReadAllBytes(Path("rom.1.bin")));
        Assert.Equal([0x11, 0x33], File.ReadAllBytes(Path("rom.2.bin")));
    }

    [Theory]
    [Trait(TC, "TC-TOOL-15-02")]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(4, 4)]
    public void DeinterleaveThenInterleaveReproducesTheOriginal(int unit, int count)
    {
        string source = Path("r.bin");
        byte[] data = new byte[2 * 1024 * 1024 + 64];
        new Random(unit * 10 + count).NextBytes(data);
        File.WriteAllBytes(source, data);

        var options = new DeinterleaveOptions { Count = count, Unit = unit, NameFormat = "{base}.{index0}{ext}" };
        using (var reader = new FileContentReader(source))
        {
            DeinterleavePlan plan = Interleaver.PlanDeinterleave(reader.Length, "r.bin", _dir, options);
            Assert.True(Interleaver.Deinterleave(plan, reader, options).Succeeded);
        }

        IContentReader[] parts = [.. Enumerable.Range(0, count).Select(i => (IContentReader)new FileContentReader(Path($"r.{i}.bin")))];
        try
        {
            string output = Path("back.bin");
            Interleaver.InterleaveToFile(parts, new InterleaveOptions { Unit = unit }, output);
            Assert.Equal(Sha(source), Sha(output));
        }
        finally
        {
            foreach (IContentReader part in parts)
            {
                part.Dispose();
            }
        }
    }

    [Fact]
    public void DeinterleaveHandlesAnIncompleteLastStripeByPaddingOrKeeping()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7];
        File.WriteAllBytes(Path("t.bin"), data);
        using var reader = new FileContentReader(Path("t.bin"));
        var padded = new DeinterleaveOptions { Count = 2, Unit = 2, NameFormat = "p{index}.bin" };
        DeinterleavePlan plan = Interleaver.PlanDeinterleave(reader.Length, "t.bin", _dir, padded);
        Assert.True(Interleaver.Deinterleave(plan, reader, padded).Succeeded);
        Assert.Equal([1, 2, 5, 6], File.ReadAllBytes(Path("p1.bin")));
        Assert.Equal([3, 4, 7, 0xFF], File.ReadAllBytes(Path("p2.bin")));

        var kept = new DeinterleaveOptions { Count = 2, Unit = 2, Remainder = RemainderMode.KeepInLast, NameFormat = "k{index}.bin" };
        DeinterleavePlan keptPlan = Interleaver.PlanDeinterleave(reader.Length, "t.bin", _dir, kept);
        Assert.True(Interleaver.Deinterleave(keptPlan, reader, kept).Succeeded);
        Assert.Equal([1, 2], File.ReadAllBytes(Path("k1.bin")));
        Assert.Equal([3, 4, 5, 6, 7], File.ReadAllBytes(Path("k2.bin")));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-15-03")]
    public void UnevenInputsArePaddedOrRejected()
    {
        using var a = new MemoryContentReader([0xA0, 0xA1, 0xA2]);
        using var b = new MemoryContentReader([0xB0]);
        IContentReader[] inputs = [a, b];
        using var padded = new MemoryStream();
        Interleaver.Interleave(inputs, new InterleaveOptions(), padded);
        Assert.Equal([0xA0, 0xB0, 0xA1, 0xFF, 0xA2, 0xFF], padded.ToArray());

        using var rejected = new MemoryStream();
        FileToolException error = Assert.Throws<FileToolException>(() =>
            Interleaver.Interleave(inputs, new InterleaveOptions { Mismatch = LengthMismatchMode.Error }, rejected));
        Assert.Equal(FileToolError.LengthMismatch, error.Error);
        Assert.Equal(0, rejected.Length);
    }

    [Fact]
    public void InterleaveToTheDocumentContentKeepsTheBytesInOrder()
    {
        using var doc = new Document(MemoryByteSource.CreateEmpty("new"), new DocumentOptions { TempDirectory = Path("recovery") });
        using var a = new MemoryContentReader([1, 2]);
        using var b = new MemoryContentReader([3, 4]);
        EditContent content = Interleaver.InterleaveToContent([a, b], new InterleaveOptions(), doc);
        doc.InsertContent(0, content);
        byte[] result = new byte[doc.Length];
        doc.Current.Read(0, result);
        Assert.Equal([1, 3, 2, 4], result);
    }
}
