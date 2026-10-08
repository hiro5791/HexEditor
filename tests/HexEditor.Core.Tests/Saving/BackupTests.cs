using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-26 バックアップファイルの作成。その場保存の異常の再現 (静的な差し替え) と同時に走らないよう、同じコレクションにする。</summary>
[Collection("InPlaceSaver")]
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-backup").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "recovery") };

    private SaveSettings Settings(BackupSettings? backup) => new() { JournalDirectory = Path.Combine(_dir, "journal"), Backup = backup };

    /// <summary>アプリの保存と同じ順 (計画・実行・完了) で保存する。</summary>
    private static SaveResult Save(Document doc, SaveSettings settings)
    {
        SavePlan plan = SavePlanner.Plan(doc, null, settings);
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SaveResult result = SavePlanner.Execute(plan);
        SavePlanner.Complete(plan, result);
        return result;
    }

    [Fact]
    public async Task BackupCopyReportsItsProgressAndRestoresTheTotal()
    {
        // ENG-26 の「巨大ファイル・長時間処理」: その場保存のバックアップのコピーは長時間処理として進捗を示す。
        string path = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(path, new byte[10 * 1024 * 1024]);
        var center = new Core.Operations.OperationCenter();
        var seen = new List<(long Processed, long? Total)>();
        await center.RunAsync("save", Core.Operations.OperationKind.WritesExternal, null, 123, op =>
        {
            op.ProgressChanged += (_, _) => seen.Add((op.ProcessedBytes, op.TotalBytes));
            Backup.Copy(path, path + ".bak", op);
            Assert.Equal(123, op.TotalBytes);
            return Task.CompletedTask;
        });

        // 進捗の通知は 100 ms ごとにまとめるため、コピーの開始 (全体がファイルの大きさ) の通知を確かめる。
        Assert.Contains(seen, s => s.Total == 10 * 1024 * 1024);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    [Trait(TC, "TC-ENG-26-01")]
    public void SameFolderBackupsKeepTheConfiguredGenerations()
    {
        string path = Path.Combine(_dir, "a.bin");
        File.Copy(TestDataCatalog.Generate("TD-BYTES-256", _dir), path);
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 1. 世代数 1 でオフセット 0 を 01 にして保存すると、a.bin.bak が保存前の内容。
        doc.Overwrite(0, [0x01]);
        SaveResult first = Save(doc, Settings(new BackupSettings { Generations = 1 }));
        Assert.Equal(path + ".bak", first.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(0x01, File.ReadAllBytes(path)[0]);

        // 2. 世代数 3 で 02〜06 を順に保存する。
        foreach (byte value in new byte[] { 0x02, 0x03, 0x04, 0x05, 0x06 })
        {
            doc.Overwrite(0, [value]);
            Save(doc, Settings(new BackupSettings { Generations = 3 }));
        }

        // 3. バックアップは .bak、.bak2、.bak3 の 3 つだけで、オフセット 0 は新しい順に 05、04、03。
        string[] backups = [.. Directory.GetFiles(_dir, "a.bin.bak*").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(["a.bin.bak", "a.bin.bak2", "a.bin.bak3"], backups);
        Assert.Equal(0x05, File.ReadAllBytes(path + ".bak")[0]);
        Assert.Equal(0x04, File.ReadAllBytes(path + ".bak2")[0]);
        Assert.Equal(0x03, File.ReadAllBytes(path + ".bak3")[0]);
        Assert.Equal(0x06, File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void SafeSaveRenamesTheOriginalAndUndoStillReadsIt()
    {
        string path = Path.Combine(_dir, "b.bin");
        File.Copy(TestDataCatalog.Generate("TD-BYTES-256", _dir), path);
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 挿入は長さが変わるため安全な保存。置き換え前のファイルの名前を変えてバックアップにする (仕様 4)。
        doc.Insert(0, [0xAB]);
        SaveResult result = Save(doc, Settings(new BackupSettings()));
        Assert.NotNull(result.BackupTime);
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(257, new FileInfo(path).Length);

        // Undo 用に開いているハンドルはバックアップのファイルを指す。
        doc.Undo();
        Assert.Equal(original, ReadAll(doc.Current));
    }

    [Fact]
    public void FolderBackupsUseThePathHash()
    {
        string folder = Path.Combine(_dir, "backups");
        string path = Path.Combine(_dir, "c.bin");
        File.WriteAllBytes(path, new byte[16]);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0x01]);
        SaveResult result = Save(doc, Settings(new BackupSettings { Folder = folder }));

        string expected = Path.Combine(folder, $"c.bin.{Backup.PathHash(path)}.bak");
        Assert.Equal(expected, result.BackupPath);
        Assert.Matches("^[0-9a-f]{8}$", Backup.PathHash(path));
        Assert.Equal(new byte[16], File.ReadAllBytes(expected));
    }

    [Fact]
    public void LargeInPlaceBackupsAskBeforeCopying()
    {
        // 1 GiB を超えるファイルのその場保存では、コピーの前に確かめる (仕様 5)。スパースなので実際には書き込まない。
        string path = Path.Combine(_dir, "big.bin");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            SparseFiles.TryMakeSparse(stream.SafeFileHandle);
            stream.SetLength(BackupSettings.CopyConfirmBytes + 1);
        }

        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0x01]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings(new BackupSettings()));
        Assert.Equal(SaveIssue.BackupCopy, plan.Issue);
        Assert.Equal(BackupSettings.CopyConfirmBytes + 1, plan.BackupCopyBytes);
        Assert.False(plan.CanExecute);

        SavePlan without = SavePlanner.WithoutBackup(plan);
        Assert.True(without.CanExecute);
        Assert.Null(without.Backup);
        Assert.True(SavePlanner.CopyBackup(plan).CanExecute);
    }

    /// <summary>計画を作り、取り消した長時間処理として実行する (書き出しの途中のキャンセル)。</summary>
    private static void SaveCancelled(Document doc, SaveSettings settings)
    {
        SavePlan plan = SavePlanner.Plan(doc, null, settings);
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        var center = new Core.Operations.OperationCenter();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            center.RunAsync("save", Core.Operations.OperationKind.WritesExternal, doc, doc.Length, op =>
            {
                op.Cancel();
                SavePlanner.Execute(plan, op);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult());
        SavePlanner.Abort(plan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelledSaveKeepsThePreviousBackup(bool safeSave)
    {
        // 世代数 1 でも、書き出し (安全な保存) やバックアップのコピー (その場保存) を取り消したら、前のバックアップは残る (ENG-26 の仕様 3)。
        string path = Path.Combine(_dir, safeSave ? "safe.bin" : "inplace.bin");
        File.WriteAllBytes(path, new byte[64]);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0x01]);
        Save(doc, Settings(new BackupSettings()));
        byte[] previousBackup = File.ReadAllBytes(path + ".bak");
        byte[] saved = File.ReadAllBytes(path);

        if (safeSave)
        {
            doc.Insert(0, [0x02]);
        }
        else
        {
            doc.Overwrite(1, [0x02]);
        }

        SaveCancelled(doc, Settings(new BackupSettings()));
        Assert.Equal(previousBackup, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(saved, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    private sealed class FakeVolumes(long available) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("X:", "NTFS", available);
    }

    [Fact]
    public void InPlaceBackupWithoutSpaceStopsTheSaveBeforeWriting()
    {
        // バックアップのコピーの置き場所の空き容量が足りなければ、保存を始めない (ENG-26 の「エラー」)。
        string path = Path.Combine(_dir, "space.bin");
        File.WriteAllBytes(path, new byte[64]);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0x01]);
        SaveSettings settings = Settings(new BackupSettings()) with { Volumes = new FakeVolumes(DocumentSaver.FreeSpaceMargin + 10) };
        SavePlan plan = SavePlanner.Plan(doc, null, settings);
        Assert.Equal(SaveMethod.InPlace, plan.Method);
        BackupFailedException error = Assert.Throws<BackupFailedException>(() => SavePlanner.Execute(plan));
        Assert.IsType<InsufficientSpaceException>(error.InnerException);
        SavePlanner.Abort(plan);
        Assert.Equal(new byte[64], File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void CopiedBackupChecksTheBackupFolderSpace()
    {
        // コピーで作る場合 (別のボリュームの置き場所・その場保存) は、置き場所の空き容量を書き出しの前に確かめる。既存のバックアップには触れない。
        string path = Path.Combine(_dir, "cross.bin");
        File.WriteAllBytes(path, new byte[64]);
        string target = Path.GetFullPath(path);
        string folder = Path.Combine(_dir, "bak");
        var local = new BackupSettings { Folder = folder };
        BackupFailedException error = Assert.Throws<BackupFailedException>(() =>
            Backup.Prepare(target, local, new FakeVolumes(DocumentSaver.FreeSpaceMargin), copyBytes: 64));
        Assert.IsType<InsufficientSpaceException>(error.InnerException);
        Assert.Equal(Backup.PathFor(target, local), Backup.Prepare(target, local, new FakeVolumes(long.MaxValue), copyBytes: 64));
        Assert.Empty(Directory.GetFiles(folder, "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public void UnusableBackupFolderStopsTheSaveBeforeWriting()
    {
        string path = Path.Combine(_dir, "d.bin");
        File.WriteAllBytes(path, new byte[16]);

        // 置き場所に同じ名前のファイルがあり、フォルダを作れない。
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllBytes(blocker, []);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0, [0x01]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings(new BackupSettings { Folder = Path.Combine(blocker, "sub") }));
        Assert.Throws<BackupFailedException>(() => SavePlanner.Execute(plan));
        SavePlanner.Abort(plan);
        Assert.Equal(16, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
