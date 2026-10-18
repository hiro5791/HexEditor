using HexEditor.Core.Tools.Files;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-17 安全な削除。テストはテスト用の一時フォルダのファイルと、仮想ディスク (メモリ上の代わりのファイルシステム) だけに行う。</summary>
public sealed class SecureDeleteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-shred").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>メモリ上のファイルシステム。削除した時点のディスクの内容 (元の領域) を残して、上書きされたかを調べられる。</summary>
    private sealed class FakeDisk : IShredFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, byte[]>> Streams { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>削除する直前のディスク上の領域の内容 (長さを 0 にする前の最後の状態を、書き込みのたびに記録する)。</summary>
        public Dictionary<string, byte[]> LastWritten { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Log { get; } = [];

        public bool FileExists(string path) => Files.ContainsKey(path);

        public bool DirectoryExists(string path) => Files.Keys.Any(k => k.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public IEnumerable<string> EnumerateFiles(string directory) =>
            Files.Keys.Where(k => k.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToList();

        public long LengthOf(string path, string? streamName = null) => (streamName is null ? Files[path] : Streams[path][streamName]).Length;

        public IReadOnlyList<string> NamedStreams(string path) => Streams.TryGetValue(path, out var s) ? [.. s.Keys] : [];

        public IShredStream OpenExclusive(string path, string? streamName = null)
        {
            if (!Files.ContainsKey(path))
            {
                throw new FileNotFoundException(path);
            }

            if (Locked.Contains(path))
            {
                throw new IOException("sharing violation", unchecked((int)0x80070020));
            }

            return new FakeStream(this, path, streamName);
        }

        public string RenameRandom(string path)
        {
            string renamed = Path.Combine(Path.GetDirectoryName(path)!, "r4nd0m.tmp");
            Files[renamed] = Files[path];
            Files.Remove(path);
            if (Streams.Remove(path, out var s))
            {
                Streams[renamed] = s;
            }

            Log.Add("rename " + path);
            return renamed;
        }

        public void DeleteFile(string path)
        {
            Files.Remove(path);
            Streams.Remove(path);
            Log.Add("delete " + path);
        }

        public void DeleteEmptyDirectory(string path)
        {
        }

        private sealed class FakeStream(FakeDisk disk, string path, string? stream) : IShredStream
        {
            private byte[] Data
            {
                get => stream is null ? disk.Files[path] : disk.Streams[path][stream];
                set
                {
                    if (stream is null)
                    {
                        disk.Files[path] = value;
                    }
                    else
                    {
                        disk.Streams[path][stream] = value;
                    }
                }
            }

            public long Length => Data.Length;

            public void Write(long offset, ReadOnlySpan<byte> data)
            {
                data.CopyTo(Data.AsSpan((int)offset));
                disk.LastWritten[path + (stream is null ? string.Empty : ":" + stream)] = (byte[])Data.Clone();
            }

            public int Read(long offset, Span<byte> buffer)
            {
                Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
                return buffer.Length;
            }

            public void Flush() => disk.Log.Add("flush " + path);

            public void Truncate()
            {
                disk.Log.Add("truncate " + path);
                Data = [];
            }

            public void Dispose()
            {
            }
        }
    }

    private static byte[] Random(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    [Trait(TC, "TC-TOOL-17-01")]
    public void TheOriginalAreaIsOverwrittenWithTheChosenPatternBeforeTheFileIsRemoved()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\z.bin"] = Random(1024 * 1024, 1);
        disk.Files[@"V:\c.bin"] = Random(1024 * 1024, 2);

        ShredResult zero = SecureDelete.Execute([@"V:\z.bin"], ShredMethod.ZeroOnce, false, disk);
        Assert.True(zero.Succeeded);
        Assert.All(disk.LastWritten[@"V:\z.bin"], b => Assert.Equal(0, b));
        Assert.Equal(1024 * 1024, disk.LastWritten[@"V:\z.bin"].Length);

        ShredMethod custom = ShredMethod.Custom([ShredPass.Fill(0xA5, 0x5A)])!;
        Assert.True(SecureDelete.Execute([@"V:\c.bin"], custom, false, disk).Succeeded);
        byte[] area = disk.LastWritten[@"V:\c.bin"];
        for (int i = 0; i < area.Length; i++)
        {
            Assert.Equal(i % 2 == 0 ? 0xA5 : 0x5A, area[i]);
        }

        // ファイルも、名前を変えた後のファイルも残らない。ごみ箱の代わりの経路もない。
        Assert.Empty(disk.Files);
        Assert.Equal(["rename V:\\z.bin", "delete V:\\r4nd0m.tmp"], disk.Log.Where(l => l.StartsWith("rename ", StringComparison.Ordinal) || l.StartsWith("delete ", StringComparison.Ordinal)).Take(2));
    }

    [Fact]
    public void EveryPassIsFlushedAndTheOrderOfTheStepsFollowsTheSpec()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\a.bin"] = Random(5000, 3);
        Assert.True(SecureDelete.Execute([@"V:\a.bin"], ShredMethod.Dod, false, disk).Succeeded);

        // 3 回の上書きごとに書き出し → 長さを 0 → 名前の変更 → 削除。
        string[] steps = [.. disk.Log];
        int lastFlush = Array.FindLastIndex(steps, l => l.StartsWith("flush", StringComparison.Ordinal));
        Assert.Equal(4, steps.Count(l => l.StartsWith("flush", StringComparison.Ordinal)));
        Assert.True(Array.IndexOf(steps, "truncate V:\\a.bin") < Array.FindIndex(steps, l => l.StartsWith("rename", StringComparison.Ordinal)));
        Assert.True(Array.FindIndex(steps, l => l.StartsWith("rename", StringComparison.Ordinal)) < Array.FindIndex(steps, l => l.StartsWith("delete", StringComparison.Ordinal)));
        Assert.True(lastFlush > 2);
    }

    [Fact]
    public void BuiltInMethodsHaveTheSpecifiedPasses()
    {
        Assert.Equal([new byte[] { 0 }], ShredMethod.ZeroOnce.Passes.Select(p => p.Pattern!));
        Assert.True(ShredMethod.RandomOnce.Passes.Single().IsRandom);
        Assert.Equal(3, ShredMethod.Dod.Passes.Count);
        Assert.Equal(new byte[] { 0 }, ShredMethod.Dod.Passes[0].Pattern);
        Assert.Equal(new byte[] { 0xFF }, ShredMethod.Dod.Passes[1].Pattern);
        Assert.True(ShredMethod.Dod.Passes[2].IsRandom);
        Assert.Null(ShredMethod.Custom([]));
        Assert.Null(ShredMethod.Custom([.. Enumerable.Repeat(ShredPass.Random, 36)]));
        Assert.NotNull(ShredMethod.Custom([.. Enumerable.Repeat(ShredPass.Random, 35)]));
        Assert.Equal(new byte[] { 0xA5, 0x5A }, ShredMethod.ParsePass("A5 5A")!.Pattern);
        Assert.Equal(new byte[] { 0xA5, 0x5A }, ShredMethod.ParsePass("0xA5, 0x5A")!.Pattern);
        Assert.True(ShredMethod.ParsePass("random")!.IsRandom);
        Assert.Null(ShredMethod.ParsePass("XYZ"));
        Assert.Null(ShredMethod.ParsePass(string.Empty));
    }

    [Fact]
    public void RandomPassesWriteDifferentDataAndVerifyReadsTheLastPatternBack()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\a.bin"] = new byte[4096];
        Assert.True(SecureDelete.Execute([@"V:\a.bin"], ShredMethod.RandomOnce, false, disk).Succeeded);
        Assert.Contains(disk.LastWritten[@"V:\a.bin"], b => b != 0);

        disk.Files[@"V:\b.bin"] = Random(4096, 9);
        Assert.True(SecureDelete.Execute([@"V:\b.bin"], ShredMethod.Dod, verifyLastPass: true, disk).Succeeded);
        disk.Files[@"V:\c.bin"] = Random(4096, 9);
        Assert.True(SecureDelete.Execute([@"V:\c.bin"], ShredMethod.ZeroOnce, verifyLastPass: true, disk).Succeeded);
    }

    [Fact]
    public void AlternateStreamsAreOverwrittenWithTheSameMethod()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\a.bin"] = Random(100, 4);
        disk.Streams[@"V:\a.bin"] = new() { ["secret"] = Random(64, 5) };
        Assert.True(SecureDelete.Execute([@"V:\a.bin"], ShredMethod.ZeroOnce, false, disk).Succeeded);
        Assert.All(disk.LastWritten[@"V:\a.bin:secret"], b => Assert.Equal(0, b));
        Assert.Empty(disk.Files);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-17-03")]
    public void AFileOpenedByAnotherProgramIsListedAsAFailureAndLeftUntouched()
    {
        string locked = Path.Combine(_dir, "locked.bin");
        string free = Path.Combine(_dir, "free.bin");
        byte[] content = Random(1024, 6);
        File.WriteAllBytes(locked, content);
        File.WriteAllBytes(free, new byte[1024]);

        ShredResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = SecureDelete.Execute([locked, free], ShredMethod.ZeroOnce, false, WindowsShredFileSystem.Instance);
        }

        ShredFailure failure = Assert.Single(result.Failures);
        Assert.Equal((locked, ShredFailureKind.InUse), (failure.Path, failure.Kind));
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(free));
        Assert.Equal(content, File.ReadAllBytes(locked));
    }

    [Fact]
    public void AReadOnlyFileIsClearedOfItsAttributeAndShreddedForReal()
    {
        string file = Path.Combine(_dir, "ro.bin");
        File.WriteAllBytes(file, Random(2048, 7));
        File.SetAttributes(file, FileAttributes.ReadOnly);
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "tree")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "deep"));
        File.WriteAllBytes(Path.Combine(folder, "deep", "x.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "y.bin"), [4]);

        IReadOnlyList<string> files = SecureDelete.ExpandTargets([file, folder, file], WindowsShredFileSystem.Instance);
        Assert.Equal(3, files.Count);
        Assert.Equal(2048 + 3 + 1, SecureDelete.TotalSize(files, WindowsShredFileSystem.Instance));
        ShredResult result = SecureDelete.Execute(files, ShredMethod.Dod, verifyLastPass: false, WindowsShredFileSystem.Instance, foldersToRemove: [folder]);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.Equal(3, result.DeletedFiles);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void RealAlternateStreamsAreShreddedWithTheFile()
    {
        if (!OperatingSystem.IsWindows() || !AlternateStreams.IsSupported(_dir))
        {
            return;
        }

        string file = Path.Combine(_dir, "ads.bin");
        File.WriteAllBytes(file, new byte[100]);
        File.WriteAllBytes(file + ":secret", new byte[50]);
        Assert.True(SecureDelete.Execute([file], ShredMethod.ZeroOnce, false, WindowsShredFileSystem.Instance).Succeeded);
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void CancelledShredLeavesTheStartedFileAndReportsItAsCorrupted()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\a.bin"] = new byte[3 * 1024 * 1024];
        disk.Files[@"V:\b.bin"] = new byte[10];
        using var cts = new CancellationTokenSource();
        var cancelling = new CancelOnFirstFlushDisk(disk, cts);
        ShredResult result = SecureDelete.Execute([@"V:\a.bin", @"V:\b.bin"], ShredMethod.Dod, false, cancelling, cancellationToken: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Equal([@"V:\a.bin"], result.LeftCorrupted);
        Assert.True(disk.Files.ContainsKey(@"V:\a.bin"));
        Assert.True(disk.Files.ContainsKey(@"V:\b.bin"));
    }

    private sealed class CancelOnFirstFlushDisk(FakeDisk inner, CancellationTokenSource cts) : IShredFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string directory) => inner.EnumerateFiles(directory);

        public long LengthOf(string path, string? streamName = null) => inner.LengthOf(path, streamName);

        public IReadOnlyList<string> NamedStreams(string path) => inner.NamedStreams(path);

        public IShredStream OpenExclusive(string path, string? streamName = null) => new Cancelling(inner.OpenExclusive(path, streamName), cts);

        public string RenameRandom(string path) => inner.RenameRandom(path);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public void DeleteEmptyDirectory(string path) => inner.DeleteEmptyDirectory(path);

        private sealed class Cancelling(IShredStream stream, CancellationTokenSource cts) : IShredStream
        {
            public long Length => stream.Length;

            public void Write(long offset, ReadOnlySpan<byte> data)
            {
                stream.Write(offset, data);
                cts.Cancel();
            }

            public int Read(long offset, Span<byte> buffer) => stream.Read(offset, buffer);

            public void Flush() => stream.Flush();

            public void Truncate() => stream.Truncate();

            public void Dispose() => stream.Dispose();
        }
    }

    [Fact]
    [Trait(TC, "TC-TOOL-17-04")]
    public void AFileOpenInTheAppCannotBeATarget()
    {
        var disk = new FakeDisk();
        disk.Files[@"V:\work\open.bin"] = [1];
        disk.Files[@"V:\work\sub\other.bin"] = [2];
        Assert.Equal(ShredRejection.OpenInApp, SecureDelete.Check(@"V:\work\open.bin", disk, [@"V:\WORK\open.bin"], []));
        Assert.Equal(ShredRejection.None, SecureDelete.Check(@"V:\work\sub\other.bin", disk, [@"V:\WORK\open.bin"], []));

        // フォルダの中にアプリで開いているファイルがあれば、フォルダも対象にできない。
        Assert.Equal(ShredRejection.OpenInApp, SecureDelete.Check(@"V:\work", disk, [@"V:\work\open.bin"], []));
        Assert.Equal(ShredRejection.NotFound, SecureDelete.Check(@"V:\work\nothing.bin", disk, [], []));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-17-05")]
    public void SystemFoldersAndDriveRootsCannotBeTargets()
    {
        var disk = new FakeDisk();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        disk.Files[Path.Combine(windows, "win.ini")] = [1];
        disk.Files[Path.Combine(programFiles, "Common Files", "x.dll")] = [1];
        disk.Files[@"C:\data\a.bin"] = [1];

        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(Path.Combine(windows, "win.ini"), disk, []));
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(Path.Combine(programFiles, "Common Files"), disk, []));
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(windows, disk, []));
        Assert.Equal(ShredRejection.DriveRoot, SecureDelete.Check(@"C:\", disk, []));
        Assert.Equal(ShredRejection.None, SecureDelete.Check(@"C:\data\a.bin", disk, []));
        Assert.Contains(Path.TrimEndingDirectorySeparator(windows), SecureDelete.SystemFolders(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheShreddedFileInsideATestFolderOnlyAffectsThatFile()
    {
        string a = Path.Combine(_dir, "a.bin");
        string b = Path.Combine(_dir, "b.bin");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [2]);
        Assert.Equal(ShredRejection.None, SecureDelete.Check(a, WindowsShredFileSystem.Instance, []));
        Assert.True(SecureDelete.Execute([a], ShredMethod.ZeroOnce, false, WindowsShredFileSystem.Instance).Succeeded);
        Assert.True(File.Exists(b));
    }
}
