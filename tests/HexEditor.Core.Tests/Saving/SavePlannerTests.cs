using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-20 保存方式の選択、ENG-23 のジャーナル、ENG-25 の事前の確認。</summary>
[Collection("InPlaceSaver")]
public sealed class SavePlannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-plan").FullName;

    private string Journals => Path.Combine(_dir, "recovery");

    private SaveSettings Settings(IVolumeInfoProvider? volumes = null, long journalLimit = InPlaceSaver.DefaultJournalLimit) => new()
    {
        JournalDirectory = Journals,
        JournalLimit = journalLimit,
        Volumes = volumes ?? SystemVolumeInfoProvider.Instance,
    };

    private string CopyOf(string id, string name)
    {
        string path = Path.Combine(_dir, name);
        File.Copy(TestDataCatalog.Get(id), path);
        return path;
    }

    private static void Save(SavePlan plan)
    {
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));
    }

    private sealed class FakeVolumes(string fileSystem, long? available) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("X:", fileSystem, available);
    }

    private static string[] AllFiles(string dir) =>
        Directory.GetFiles(dir, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true });

    [Fact]
    [Trait(TC, "TC-ENG-20-02")]
    public void SavingUnmodifiedDocumentDoesNotWrite()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1: 変更なしで保存。
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveMethod.NoChanges, plan.Method);
        Assert.False(plan.CanExecute);

        // 手順 2: 上書きして取り消してから保存。
        doc.Overwrite(0, [0xFF]);
        doc.Undo();
        Assert.Equal(SaveMethod.NoChanges, SavePlanner.Plan(doc, path, Settings()).Method);

        // 手順 3: 更新日時が変わらず、ジャーナルも一時ファイルも作られていない。
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(Journals));
        Assert.Single(AllFiles(_dir));
    }

    [Fact]
    [Trait(TC, "TC-ENG-23-02")]
    public void BalancedLengthEditUsesSafeSaveButOverwriteUsesInPlace()
    {
        string a = CopyOf("TD-SEQ-1M", "a.bin");
        string b = CopyOf("TD-SEQ-1M", "b.bin");
        using var shifted = new Document(FileByteSource.Open(a), Options());
        shifted.Delete(0x100, 1);
        shifted.Insert(0x80000, [0x42]);
        Assert.Equal(SaveMethod.Safe, SavePlanner.Plan(shifted, null, Settings()).Method);

        using var overwritten = new Document(FileByteSource.Open(b), Options());
        overwritten.Overwrite(0x100, [0x42]);
        Assert.Equal(SaveMethod.InPlace, SavePlanner.Plan(overwritten, null, Settings()).Method);

        // 設定「常に安全な保存」。
        Assert.Equal(SaveMethod.Safe, SavePlanner.Plan(overwritten, null, Settings() with { AlwaysSafeSave = true }).Method);
    }

    [Fact]
    public void UntitledAndReadOnlyDocumentsNeedSaveAs()
    {
        using var untitled = new Document(MemoryByteSource.CreateEmpty("無題 1"), Options());
        Assert.Equal(SaveMethod.SaveAs, SavePlanner.Plan(untitled, null, Settings()).Method);

        string path = CopyOf("TD-SEQ-1M", "ro.bin");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var doc = new Document(FileByteSource.Open(path), Options());
            doc.Overwrite(0, [1]);
            SavePlan plan = SavePlanner.Plan(doc, null, Settings());
            Assert.Equal((SaveMethod.SaveAs, SaveIssue.ReadOnly), (plan.Method, plan.Issue));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-20-05")]
    public void FileSizeLimitIsReportedInsteadOfFreeSpace()
    {
        // ENG-25 の仕様 5: ファイルサイズの上限を空き容量より先に確かめ、上限のエラーだけを出す (TC-ENG-20-05 の判定の部分)。
        var source = new FakeByteSource(5 * TestDataCatalog.GiB, (o, s) => s.Clear(), SourceCapabilities.CanResize | SourceCapabilities.CanWrite);
        using var doc = new Document(source, Options());
        string target = Path.Combine(_dir, "big.bin");
        SavePlan plan = SavePlanner.Plan(doc, target, Settings(new FakeVolumes("FAT32", available: 1024)));
        Assert.Equal(SaveIssue.FileTooLarge, plan.Issue);
        Assert.Equal(VolumeInfo.Fat32MaxFileSize, plan.SizeLimit!.MaxFileSize);
        Assert.Null(plan.Space);
        Assert.Throws<FileSizeLimitException>(() => DocumentSaver.Save(doc.Current, target, volumes: new FakeVolumes("FAT32", null)));
        Assert.Empty(AllFiles(_dir));

        // 4 GiB − 1 バイトは上限内。
        var fits = new FakeByteSource(VolumeInfo.Fat32MaxFileSize, (o, s) => s.Clear(), SourceCapabilities.CanResize | SourceCapabilities.CanWrite);
        using var small = new Document(fits, Options());
        Assert.Equal(SaveIssue.None, SavePlanner.Plan(small, target, Settings(new FakeVolumes("FAT32", 8 * TestDataCatalog.GiB))).Issue);
    }

    [Fact]
    public void InsufficientSpaceIsReportedBeforeWriting()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0, [1]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings(new FakeVolumes("NTFS", available: 1024 * 1024)));
        Assert.Equal((SaveMethod.Safe, SaveIssue.InsufficientSpace), (plan.Method, plan.Issue));
        Assert.Equal(doc.Length + DocumentSaver.FreeSpaceMargin, plan.Space!.Required);
        Assert.Equal(1024 * 1024, plan.Space.Available);
        Assert.Throws<InvalidOperationException>(() => SavePlanner.Execute(plan));

        // 空き容量を取得できない場所 (一部のネットワークドライブ) では確認を省略する。
        Assert.Equal(SaveIssue.None, SavePlanner.Plan(doc, null, Settings(new FakeVolumes("NTFS", available: null))).Issue);
    }

    [Fact]
    public void FreeSpaceIsCheckedOnNetworkPathsToo()
    {
        // ENG-25 の「エラー」: UNC パスでも取得できれば確認する。ローカルの共有 (\\localhost\C$) が使えない環境では省略する。
        string unc = $@"\\localhost\{Path.GetPathRoot(_dir)![0]}$\";
        if (!Directory.Exists(unc))
        {
            return;
        }

        VolumeInfo? volume = SystemVolumeInfoProvider.Instance.GetVolume(unc);
        Assert.NotNull(volume?.AvailableFreeSpace);
        Assert.Throws<InsufficientSpaceException>(() => DocumentSaver.CheckFreeSpace(unc, long.MaxValue / 2));
    }

    [Fact]
    public void JournalLimitNeedsConfirmation()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.OverwritePattern(0, 0x80000, [0x00]);

        SavePlan plan = SavePlanner.Plan(doc, null, Settings(journalLimit: 0x1000));
        Assert.Equal((SaveMethod.InPlace, SaveIssue.JournalTooLarge), (plan.Method, plan.Issue));
        Assert.Equal(0x80000, plan.Journal!.Required);
        Assert.Null(plan.Journal.Space);
        Assert.Equal(original, File.ReadAllBytes(path)); // 確認するまで書かない

        // 「安全な保存を使う」
        SavePlan safe = SavePlanner.UseSafeSave(plan);
        Assert.Equal((SaveMethod.Safe, SaveIssue.None), (safe.Method, safe.Issue));

        // 「保護なしで書き込む」: ジャーナルを書かずに書き込み、Undo 用の旧内容は退避する。
        SavePlan unprotected = SavePlanner.WriteWithoutJournal(plan);
        Save(unprotected);
        Assert.False(Directory.Exists(Journals) && Directory.GetFiles(Journals).Length > 0);
        Assert.Equal(new byte[0x80000], File.ReadAllBytes(path)[..0x80000]);
        doc.Undo();
        Assert.Equal(original, ReadAll(doc.Current));
    }

    [Fact]
    public void JournalLocationWithoutSpaceNeedsConfirmation()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [1]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings(new FakeVolumes("NTFS", available: 1000)));
        Assert.Equal(SaveIssue.JournalTooLarge, plan.Issue);
        Assert.NotNull(plan.Journal!.Space);
    }

    [Fact]
    public void JournalIsRemovedWhenSaveIsCancelledOrFailsBeforeWriting()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.OverwritePattern(0, 0x100000, [0x11]);

        var center = new OperationCenter();
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            center.RunAsync("保存", OperationKind.WritesExternal, doc, plan.TotalBytes, op =>
            {
                op.Cancel();
                return Task.FromResult(SavePlanner.Execute(plan, op));
            }).GetAwaiter().GetResult());
        SavePlanner.Abort(plan);

        Assert.Empty(Directory.GetFiles(Journals));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(doc.IsModified);
        Assert.Equal(FileLockState.Locked, doc.LockState); // 書き込み禁止のハンドルを元に戻した
    }

    [Fact]
    public void WriteFailureRollsBackFromJournal()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        for (int i = 0; i < 4; i++)
        {
            doc.OverwritePattern(i * 0x40000, 0x1000, [0xAB]);
        }

        // 2 つ目の範囲を書いた後に書き込みが失敗した: 自動でジャーナルから戻す (ENG-23 の「エラー」)。
        try
        {
            InPlaceSaver.AfterRangeWritten = i =>
            {
                if (i == 1)
                {
                    throw new IOException("disk error");
                }
            };
            Assert.Throws<InPlaceSaveRolledBackException>(() => InPlaceSaver.Save(doc.Current, Journals, documentId: doc.Id));
        }
        finally
        {
            InPlaceSaver.AfterRangeWritten = null;
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Journals));
    }

    [Fact]
    [Trait(TC, "TC-ENG-23-03")]
    public void InterruptedWriteIsRolledBackFromJournal()
    {
        string path = CopyOf("TD-RANDOM-16M", "r.bin");
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1: 0x100000 ごとに 16 か所、各 4 KiB を 00 で上書きして保存し、3 つ目の範囲を書いた直後に強制終了する。
        for (int i = 0; i < 16; i++)
        {
            doc.OverwritePattern(i * 0x100000L, 0x1000, [0x00]);
        }

        InPlaceSaver.AfterRangeWritten = i =>
        {
            if (i == 2)
            {
                throw new SimulatedCrashException();
            }
        };
        try
        {
            Assert.Throws<SimulatedCrashException>(() => InPlaceSaver.Save(doc.Current, Journals, documentId: doc.Id));
        }
        finally
        {
            InPlaceSaver.AfterRangeWritten = null;
        }

        doc.Dispose(); // プロセスの終了

        // 手順 2: 一部が書き換わっている。
        Assert.NotEqual(originalHash, SHA256.HashData(File.ReadAllBytes(path)));

        // 手順 3〜5: 起動時に見つかったジャーナルで保存前の状態に戻すと、ハッシュが一致し、ジャーナルが消える。
        string journal = Assert.Single(InPlaceSaver.FindJournals(Journals));
        Assert.Equal($"journal-{doc.Id:N}.bin", Path.GetFileName(journal));
        Assert.Equal(path, InPlaceSaver.ReadJournalTarget(journal));
        Assert.Equal(path, InPlaceSaver.Rollback(journal));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Empty(InPlaceSaver.FindJournals(Journals));
    }

    [Fact]
    [Trait(TC, "TC-ENG-23-04")]
    public void JournalIsNotAppliedToChangedFile()
    {
        string path = CopyOf("TD-RANDOM-16M", "r.bin");
        var doc = new Document(FileByteSource.Open(path), Options());
        doc.OverwritePattern(0x100000, 0x1000, [0x00]);
        InPlaceSaver.AfterRangeWritten = _ => throw new SimulatedCrashException();
        try
        {
            Assert.Throws<SimulatedCrashException>(() => InPlaceSaver.Save(doc.Current, Journals, documentId: doc.Id));
        }
        finally
        {
            InPlaceSaver.AfterRangeWritten = null;
        }

        doc.Dispose();

        // 手順 1: 起動する前にファイルの末尾に 1 バイト追記する。
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.WriteByte(0x5A);
        }

        byte[] afterAppend = File.ReadAllBytes(path);

        // 手順 2・3: 書き戻さず、一致しない旨を示す。ファイルは変わらない。ジャーナルは残る (「このままにする」で消す)。
        string journal = Assert.Single(InPlaceSaver.FindJournals(Journals));
        Assert.Throws<JournalMismatchException>(() => InPlaceSaver.Rollback(journal));
        Assert.Equal(afterAppend, File.ReadAllBytes(path));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    [Trait(TC, "TC-ENG-23-05")]
    public void UndoAfterInPlaceSaveAndSaveAgainRestoresFile()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1
        doc.Overwrite(0x1000, Enumerable.Repeat((byte)0xAA, 0x100).ToArray());
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveMethod.InPlace, plan.Method);
        Save(plan);

        // 手順 2
        doc.Undo();
        Assert.Equal(Enumerable.Range(0, 0x100).Select(i => (byte)i).ToArray(), Read(doc.Current, 0x1000, 0x100));

        // 手順 3
        Save(SavePlanner.Plan(doc, null, Settings()));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    [Trait(TC, "TC-ENG-22-03")]
    public void UndoAfterSafeSaveAndReplacedFileIsHiddenAndFreedOnClose()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1・2
        doc.Insert(0, [0x01]);
        Save(SavePlanner.Plan(doc, null, Settings()));
        doc.Undo();
        Assert.Equal(originalHash, Sha256(doc.Current));

        // 手順 3: フォルダにあるのは保存したファイルだけ (置き換え前のファイルはパスから見えない)。
        Assert.Equal([path], AllFiles(_dir));

        // 手順 4: 閉じた後も保存したファイルだけで、置き換え前のファイルのハンドルは閉じている。
        doc.Dispose();
        Assert.Equal([path], AllFiles(_dir));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-21-02")]
    public void CancelledSaveAsLeavesNoFile()
    {
        // 前提の「遅いデータソース」: 64 MiB の内容を計算で返す。進捗が 20% を超えたらキャンセルする。
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        FakeByteSource source = SlowSource();
        using var doc = new Document(source, Options());
        SavePlan plan = SavePlanner.Plan(doc, Path.Combine(folder, "out.bin"), Settings());
        RunAndCancelAt(doc, plan, 0.2);

        Assert.Empty(AllFiles(folder));
        Assert.Same(source, doc.Source);
    }

    [Fact]
    [Trait(TC, "TC-ENG-22-06")]
    public void CancelledSafeSaveLeavesNoTempFile()
    {
        // 遅いデータソースの内容を、既存のファイルに上書き保存する。
        string path = CopyOf("TD-SEQ-1M", "m.bin");
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        using var doc = new Document(SlowSource(), Options());
        doc.Insert(0, [0x01]);
        SavePlan plan = SavePlanner.Plan(doc, path, Settings());
        Assert.Equal(SaveMethod.Safe, plan.Method);
        RunAndCancelAt(doc, plan, 0.3);

        Assert.Equal([path], AllFiles(_dir));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(doc.IsModified);
    }

    /// <summary>1 回の読み込みに 10 ms かかる 64 MiB のデータソース (テスト方針の「遅いデータソース」)。</summary>
    private static FakeByteSource SlowSource() =>
        new(64 * TestDataCatalog.MiB, (o, s) =>
        {
            Thread.Sleep(10);
            TestDataCatalog.Sequence(o, s);
        }, SourceCapabilities.CanResize | SourceCapabilities.CanWrite);

    private static void RunAndCancelAt(Document doc, SavePlan plan, double fraction)
    {
        var center = new OperationCenter();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            center.RunAsync("保存", OperationKind.WritesExternal, doc, plan.TotalBytes, op =>
            {
                op.ProgressChanged += (_, _) =>
                {
                    if (op.Fraction > fraction)
                    {
                        op.Cancel();
                    }
                };
                return Task.FromResult(SavePlanner.Execute(plan, op));
            }).GetAwaiter().GetResult());
        SavePlanner.Abort(plan);
    }

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
}
