using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// ENG-15 ファイルのロックと共有の方針。「他のアプリ」の書き込み用のオープンは、同じプロセスの別のハンドルで行う
/// (共有モードの判定はハンドルごとで、プロセスに依存しない)。
/// </summary>
[Collection("InPlaceSaver")]
public sealed class FileLockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-lock").FullName;

    private string CopyOf(string id)
    {
        string path = Path.Combine(_dir, id + ".bin");
        File.Copy(TestDataCatalog.Get(id), path);
        return path;
    }

    /// <summary>他のアプリが書き込み用に開いて 1 バイト書く。開けなければ false。</summary>
    private static bool OtherAppWrites(string path, long offset, byte value)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = offset;
            stream.WriteByte(value);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-15-01")]
    public void OpenedFileCanBeWrittenByOtherApps()
    {
        string path = CopyOf("TD-SEQ-1M");
        using var doc = new Document(FileByteSource.Open(path), Options());
        Assert.Equal(FileLockState.Unlocked, doc.LockState);
        Assert.True(OtherAppWrites(path, 0x10, 0x99));
        Assert.Equal(0x99, File.ReadAllBytes(path)[0x10]);
    }

    [Fact]
    [Trait(TC, "TC-ENG-15-02")]
    public void ModifiedFileDeniesOtherWritersUntilUndoOrSave()
    {
        string path = CopyOf("TD-SEQ-1M");
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1・2: 変更した後は、他のアプリが書き込み用に開けない。
        doc.Overwrite(0, [0xFF]);
        Assert.Equal(FileLockState.Locked, doc.LockState);
        Assert.False(OtherAppWrites(path, 0x10, 0x99));

        // 手順 3・4: 取り消すと開ける。
        doc.Undo();
        Assert.Equal(FileLockState.Unlocked, doc.LockState);
        Assert.True(OtherAppWrites(path, 0x10, 0x10));

        // 手順 5: もう一度上書きして保存すると、保存後は開ける。
        doc.Redo();
        Assert.False(OtherAppWrites(path, 0x10, 0x99));
        SavePlan plan = SavePlanner.Plan(doc, null, new SaveSettings { JournalDirectory = Path.Combine(_dir, "recovery") });
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));
        Assert.Equal(0xFF, File.ReadAllBytes(path)[0]);
        Assert.Equal(FileLockState.Unlocked, doc.LockState);
        Assert.True(OtherAppWrites(path, 0x10, 0x10));
    }

    [Fact]
    public void LockFailsWhenOtherAppAlreadyWritesAndPoliciesCanBeChanged()
    {
        string path = CopyOf("TD-SEQ-1M");
        using var doc = new Document(FileByteSource.Open(path), Options());
        int notified = 0;
        doc.LockStateChanged += (_, _) => notified++;
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            // 他のアプリが書き込み用に開いている: 禁止できない (UI は InfoBar を出す)。続けて編集しても通知は 1 回だけ。
            doc.Overwrite(0, [1]);
            doc.Overwrite(1, [1]);
            Assert.Equal(FileLockState.Failed, doc.LockState);
            Assert.Equal(1, notified);
        }

        // 「常に禁止」(仕様 3・4) と「ロックしない」。
        doc.Undo();
        doc.Undo();
        doc.LockPolicy = FileLockPolicy.Always;
        Assert.Equal(FileLockState.Locked, doc.LockState);
        Assert.False(OtherAppWrites(path, 0, 0));
        doc.LockPolicy = FileLockPolicy.None;
        doc.Overwrite(0, [2]);
        Assert.Equal(FileLockState.Unlocked, doc.LockState);
        Assert.True(OtherAppWrites(path, 0x20, 0x20));
    }

    [Fact]
    [Trait(TC, "TC-ENG-15-03")]
    public void DeletedFileRemainsReadable()
    {
        string path = CopyOf("TD-RANDOM-16M");
        byte[] expected = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1: 削除できる。
        File.Delete(path);
        Assert.False(File.Exists(path));

        // 手順 2: キャッシュにないブロックを読む。
        foreach (long offset in new long[] { 0, 0x7FFFF0, 0xFFFFF0 })
        {
            Assert.Equal(expected.AsSpan((int)offset, 16).ToArray(), Read(doc.Current, offset, 16));
        }
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
