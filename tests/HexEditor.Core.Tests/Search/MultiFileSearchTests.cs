using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using HexEditor.Core.Search;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>複数ファイル検索 (FIND-30) と複数ファイル置換 (FIND-31) の結合テスト。一時フォルダにテストデータを作る。</summary>
public sealed class MultiFileSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HexEditor", "tests", "multifile-" + Guid.NewGuid().ToString("N")[..8]);

    public MultiFileSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly SearchPattern Marker = SearchPattern.FromHex("CA FE BA BE");

    private static async Task<MultiFileSearchResults> SearchAsync(MultiFileTargets targets, int parallelism = 4, bool record = false)
    {
        var results = new MultiFileSearchResults(Marker, targets) { RecordOpenedFiles = record };
        await MultiFileSearch.RunAsync(results, new SearchOptions(), null, parallelism);
        return results;
    }

    private string Tree1000()
    {
        string dir = Path.Combine(_root, "tree1000");
        SearchTrees.WriteTree1000(dir);
        return dir;
    }

    private string Tree10()
    {
        string dir = Path.Combine(_root, "tree10");
        SearchTrees.WriteTree10(dir);
        return dir;
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-01")]
    public async Task ThousandFilesAreGroupedByFile()
    {
        // TC-FIND-30-01 の Core の部分: 25 ファイルにまとまり、.bin は 2 件、.log は 1 件。一致がなかったファイルは 980。
        string dir = Tree1000();
        MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [dir] });
        Assert.Equal(MultiFileSearchState.Completed, results.State);
        Assert.Equal(1005, results.FoundFiles);
        Assert.Equal(1005, results.ProcessedFiles);
        Assert.Equal(25, results.Files.Count);
        Assert.All(results.Files.Where(f => f.Path.EndsWith(".bin", StringComparison.Ordinal)), f => Assert.Equal([0x100L, 0x200], f.Matches.Select(m => m.Offset)));
        Assert.All(results.Files.Where(f => f.Path.EndsWith(".log", StringComparison.Ordinal)), f => Assert.Equal([0x10L], f.Matches.Select(m => m.Offset)));
        Assert.Equal(20, results.Files.Count(f => f.Path.EndsWith(".bin", StringComparison.Ordinal)));
        Assert.Equal(980, results.FilesWithoutMatches);
        Assert.Empty(results.Skipped);
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-02")]
    public async Task ExcludedMasksAreNotSearched()
    {
        string dir = Tree1000();

        // 手順 1〜2: *.log を除くと 20 個の .bin だけ。.log は開かれていない。
        MultiFileSearchResults noLogs = await SearchAsync(new MultiFileTargets { Folders = [dir], ExcludeMasks = "*.log" }, record: true);
        Assert.Equal(20, noLogs.Files.Count);
        Assert.All(noLogs.Files, f => Assert.EndsWith(".bin", f.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(noLogs.OpenedFiles, p => p.EndsWith(".log", StringComparison.Ordinal));
        Assert.Equal(1000, noLogs.OpenedFiles.Count);

        // 手順 3: d1;d2 (フォルダ) を除くと、その下のファイルは結果にも開いた記録にもない。
        MultiFileSearchResults noFolders = await SearchAsync(new MultiFileTargets { Folders = [dir], ExcludeMasks = "d1;d2" }, record: true);
        string d1 = Path.Combine(dir, "d1") + Path.DirectorySeparatorChar;
        string d2 = Path.Combine(dir, "d2") + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(noFolders.Files, f => f.Path.StartsWith(d1, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(d2, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(noFolders.OpenedFiles, p => p.StartsWith(d1, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d2, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(803, noFolders.OpenedFiles.Count);

        // 含めるマスク・サイズの範囲・隠しファイル (仕様 2)。
        MultiFileSearchResults logs = await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeMasks = "*.log;*.txt" });
        Assert.Equal(5, logs.Files.Count);
        File.SetAttributes(Path.Combine(dir, "d0", "x.log"), FileAttributes.Hidden);
        Assert.Equal(4, (await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeMasks = "*.log" })).Files.Count);
        Assert.Equal(5, (await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeMasks = "*.log", IncludeHidden = true })).Files.Count);
        Assert.Empty((await SearchAsync(new MultiFileTargets { Folders = [dir], MinSize = 5000 })).Files);
        Assert.Equal(20, (await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeSubfolders = true, MaxDepth = 1, ExcludeMasks = "*.log" })).Files.Count);
        Assert.Empty((await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeSubfolders = false })).Files);

        // 対象の指定の保存と読み込み (仕様 9)。
        var targets = new MultiFileTargets { Folders = [dir], ExcludeMasks = "d1;d2", MinSize = 10, IncludeHidden = true };
        Assert.Equal(targets.ToJson().ToJsonString(), MultiFileTargets.FromJson(targets.ToJson()).ToJson().ToJsonString());
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-03")]
    public async Task FilesOpenForWritingByOthersAreSearched()
    {
        string dir = Tree10();
        using var other = new FileStream(Path.Combine(dir, "r3.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [dir] });
        FileSearchResult r3 = Assert.Single(results.Files, f => f.Path.EndsWith("r3.bin", StringComparison.Ordinal));
        Assert.Equal([0x1000L, 0x2000], r3.Matches.Select(m => m.Offset));
        Assert.Empty(results.Skipped);
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-04")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task UnreadableFilesAreListedWithReasons()
    {
        string dir = Tree10();
        string r4 = Path.Combine(dir, "r4.bin");
        var info = new FileInfo(r4);
        FileSecurity security = info.GetAccessControl();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            using var locked = new FileStream(Path.Combine(dir, "r5.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [dir] });
            Assert.Equal(MultiFileSearchState.Completed, results.State);
            Assert.Equal(8, results.Files.Count);
            Assert.Contains(results.Skipped, s => s.Path == r4 && s.Reason == FileSkipReason.AccessDenied);
            Assert.Contains(results.Skipped, s => s.Path.EndsWith("r5.bin", StringComparison.Ordinal) && s.Reason == FileSkipReason.InUse);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }

        // 指定したフォルダが存在しない場合は、検索の前にわかる (「エラー」)。
        Assert.Equal([Path.Combine(dir, "nothing")], MultiFileSearch.MissingFolders(new MultiFileTargets { Folders = [dir, Path.Combine(dir, "nothing")] }));
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-06")]
    public async Task PathsLongerThan260Characters()
    {
        string deep = _root;
        for (int i = 0; i < 5; i++)
        {
            deep = Path.Combine(deep, new string((char)('a' + i), 60));
        }

        Directory.CreateDirectory(@"\\?\" + deep);
        string file = Path.Combine(deep, "r0.bin");
        File.WriteAllBytes(@"\\?\" + file, SearchTrees.Tree10File());
        Assert.True(file.Length >= 300, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [_root] });
        FileSearchResult found = Assert.Single(results.Files);
        Assert.Equal(file, found.Path);
        Assert.Equal(2, found.Matches.Count);
        Assert.DoesNotContain(results.Skipped, s => s.Reason == FileSkipReason.PathTooLong);
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-07")]
    public async Task LinkLoopsTerminate()
    {
        string loop = Path.Combine(_root, "loop");
        Directory.CreateDirectory(loop);
        File.WriteAllBytes(Path.Combine(loop, "r0.bin"), SearchTrees.Tree10File());

        // loop\self: loop を指すジャンクション (管理者権限は要らない)。loop\up: loop を指すディレクトリのシンボリックリンク
        // (作れない環境 (管理者でも開発者モードでもない) では、もう 1 つのジャンクションで代わりにする)。
        CreateJunction(Path.Combine(loop, "self"), loop);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(loop, "up"), loop);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CreateJunction(Path.Combine(loop, "up"), loop);
        }

        foreach (bool follow in new[] { false, true })
        {
            var watch = Stopwatch.StartNew();
            MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [loop], FollowLinks = follow });
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
            FileSearchResult only = Assert.Single(results.Files);
            Assert.Equal(Path.Combine(loop, "r0.bin"), only.Path);
            Assert.Equal(2, only.Matches.Count);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "ジャンクションを作れません。");
    }

    private MultiFileReplaceOptions ReplaceOptions() => new()
    {
        Template = ReplacementTemplate.FromHex("11 22 33 44"),
        JournalDirectory = Path.Combine(_root, "journal"),
    };

    [Fact]
    [Trait(TC, "TC-FIND-31-03")]
    public async Task FilesChangedAfterTheSearchAreSkipped()
    {
        string dir = Tree10();
        MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [dir] });
        string r6 = Path.Combine(dir, "r6.bin");
        using (var stream = new FileStream(r6, FileMode.Open, FileAccess.Write))
        {
            stream.WriteByte(0xFF);
        }

        File.SetLastWriteTimeUtc(r6, File.GetLastWriteTimeUtc(r6).AddSeconds(5));
        var outcomes = results.Files.Select(f => MultiFileSearch.ReplaceInFile(f, f.Matches, Marker, ReplaceOptions())).ToList();
        FileReplaceOutcome six = Assert.Single(outcomes, o => o.Path == r6);
        Assert.Equal((FileReplaceStatus.Skipped, FileSkipReason.ChangedSinceSearch), (six.Status, six.Reason!.Value));
        byte[] bytes = File.ReadAllBytes(r6);
        Assert.Equal(SearchTrees.Marker, bytes.AsSpan(0x1000, 4).ToArray());
        Assert.Equal(0xFF, bytes[0]);
        Assert.False(File.Exists(r6 + ".bak"));

        // ほかの 9 ファイルは置換され、.bak が作られる (仕様 5)。
        Assert.Equal(9, outcomes.Count(o => o.Status == FileReplaceStatus.Replaced && o.Count == 2));
        foreach (FileReplaceOutcome o in outcomes.Where(o => o.Status == FileReplaceStatus.Replaced))
        {
            byte[] after = File.ReadAllBytes(o.Path);
            Assert.Equal([0x11, 0x22, 0x33, 0x44], after.AsSpan(0x1000, 4).ToArray());
            Assert.Equal([0x11, 0x22, 0x33, 0x44], after.AsSpan(0x2000, 4).ToArray());
            Assert.Equal(SearchTrees.Tree10File(), File.ReadAllBytes(o.Path + ".bak"));
        }

        // 読み取り専用属性のファイルは飛ばす (仕様 7)。外す設定なら置換し、属性を戻す。
        string r0 = Path.Combine(dir, "r0.bin");
        File.WriteAllBytes(r0, SearchTrees.Tree10File());
        File.SetAttributes(r0, FileAttributes.ReadOnly);
        MultiFileSearchResults again = await SearchAsync(new MultiFileTargets { Folders = [dir], IncludeMasks = "r0.bin" });
        FileSearchResult zero = Assert.Single(again.Files);
        Assert.Equal(FileSkipReason.ReadOnly, MultiFileSearch.ReplaceInFile(zero, zero.Matches, Marker, ReplaceOptions() with { Backup = false }).Reason);
        FileReplaceOutcome cleared = MultiFileSearch.ReplaceInFile(zero, zero.Matches, Marker, ReplaceOptions() with { ClearReadOnly = true });
        Assert.Equal(FileReplaceStatus.Replaced, cleared.Status);
        Assert.True(File.GetAttributes(r0).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(r0 + ".bak1", cleared.BackupPath); // 既存の .bak を上書きしない
    }

    [Fact]
    [Trait(TC, "TC-FIND-31-05")]
    public async Task CancellingInTheMiddleLeavesNoBrokenFiles()
    {
        string dir = Tree10();
        MultiFileSearchResults results = await SearchAsync(new MultiFileTargets { Folders = [dir] }, parallelism: 1);
        FileSearchResult[] files = [.. results.Files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        using var cts = new CancellationTokenSource();
        int written = 0;
        MultiFileSearch.BeforeFileWrite = (path, token) =>
        {
            // 書き込みに 500 ms かかるものとし、2 ファイル目の書き込みの 200 ms 後にキャンセルする。
            if (++written == 2)
            {
                Thread.Sleep(200);
                cts.Cancel();
            }
        };
        var outcomes = new List<FileReplaceOutcome>();
        try
        {
            foreach (FileSearchResult f in files)
            {
                outcomes.Add(MultiFileSearch.ReplaceInFile(f, f.Matches, Marker, ReplaceOptions(), null, cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            MultiFileSearch.BeforeFileWrite = null;
        }

        byte[] original = SearchTrees.Tree10File();
        byte[] replaced = SearchTrees.Tree10File();
        new byte[] { 0x11, 0x22, 0x33, 0x44 }.CopyTo(replaced, 0x1000);
        new byte[] { 0x11, 0x22, 0x33, 0x44 }.CopyTo(replaced, 0x2000);
        Assert.Equal(replaced, File.ReadAllBytes(files[0].Path));
        foreach (FileSearchResult f in files.Skip(1))
        {
            Assert.Equal(original, File.ReadAllBytes(f.Path));
        }

        // 保存の途中の一時ファイルは残らない。置換を終えたのは 1 ファイル目だけ。
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        Assert.Equal([FileReplaceStatus.Replaced], outcomes.Select(o => o.Status));
        Assert.False(File.Exists(files[1].Path + ".bak"));
    }
}
