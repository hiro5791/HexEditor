using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-26 バックアップファイルの作成。</summary>
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
