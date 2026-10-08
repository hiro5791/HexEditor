using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-18 再読み込みと変更の破棄、ENG-19 のマージ、ENG-14 の読み取り専用で開く (Core の部分)。</summary>
public sealed class ReloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-reload").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "recovery") };

    private string Seq(string name = "seq.bin")
    {
        string path = Path.Combine(_dir, name);
        File.Copy(TestDataCatalog.Generate("TD-SEQ-1M", _dir), path, overwrite: true);
        return path;
    }

    [Fact]
    public void DiscardingUnchangedSourceCanBeUndone()
    {
        string path = Seq();
        using var doc = new Document(FileByteSource.Open(path), Options());
        foreach (long offset in new long[] { 0x10, 0x20, 0x30 })
        {
            doc.Overwrite(offset, [0xFF]);
        }

        ChangeSummary summary = ChangeSummary.Of(doc.Current);
        Assert.Equal((3, 3L), (summary.Places, summary.Bytes));

        doc.DiscardChanges();
        Assert.False(doc.IsModified);
        Assert.Equal(0x10, Read(doc.Current, 0x10, 1)[0]);

        // 破棄は 1 つの Undo 単位 (ENG-18 の仕様 3)。
        doc.Undo();
        Assert.True(doc.IsModified);
        Assert.Equal([0xFF], Read(doc.Current, 0x10, 1));
        Assert.Equal([0xFF], Read(doc.Current, 0x20, 1));
        Assert.Equal([0xFF], Read(doc.Current, 0x30, 1));
    }

    [Fact]
    public void ReplacingTheSourceClearsTheHistory()
    {
        string path = Seq();

        // 書き込み禁止のハンドルを持たない (他のアプリが同じファイルに書ける) 状態。
        using var doc = new Document(FileByteSource.Open(path), Options() with { LockPolicy = FileLockPolicy.None });
        doc.Overwrite(0, [0xAA]);

        byte[] changed = File.ReadAllBytes(path);
        changed[5] = 0x55;
        Array.Resize(ref changed, 0x80000);
        File.WriteAllBytes(path, changed);

        doc.ReplaceSource(FileByteSource.Open(path));
        Assert.False(doc.IsModified);
        Assert.False(doc.History.CanUndo);
        Assert.Equal(0x80000, doc.Length);
        Assert.Equal(0x55, Read(doc.Current, 5, 1)[0]);
    }

    [Fact]
    public void MergeAppliesOwnChangesOntoTheNewContentAsOneUndoUnit()
    {
        string path = Seq("a.bin");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0x10, [0xAA]);
        doc.Insert(0x100, [0x01, 0x02]);

        // 他のエディタの安全な保存: 0x80000 を BB にした内容で置き換える。
        byte[] replaced = File.ReadAllBytes(path);
        replaced[0x80000] = 0xBB;
        string temp = Path.Combine(_dir, "a.tmp");
        File.WriteAllBytes(temp, replaced);
        File.Replace(temp, path, null);

        MergeResult result = doc.MergeOnto(FileByteSource.Open(path));
        Assert.False(result.LengthChanged);
        Assert.Equal(0xAA, Read(doc.Current, 0x10, 1)[0]);
        Assert.Equal([0x01, 0x02], Read(doc.Current, 0x100, 2));
        Assert.Equal(0xBB, Read(doc.Current, 0x80002, 1)[0]);
        Assert.True(doc.IsModified);

        doc.Undo();
        Assert.Equal(0xAA, Read(doc.Current, 0x10, 1)[0]);
        Assert.Equal(0x00, Read(doc.Current, 0x80002, 1)[0]);
    }

    [Fact]
    public void MergeOntoLongerOrShorterFiles()
    {
        string path = Seq("b.bin");
        using var doc = new Document(FileByteSource.Open(path), Options() with { LockPolicy = FileLockPolicy.None });
        doc.Overwrite(0, [0xEE]);

        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x33, 0x100010).ToArray());
        MergeResult longer = doc.MergeOnto(FileByteSource.Open(path));
        Assert.True(longer.LengthChanged);
        Assert.Equal(0x100010, doc.Length);
        Assert.Equal([0xEE, 0x33], Read(doc.Current, 0, 2));

        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x44, 0x20).ToArray());
        doc.MergeOnto(FileByteSource.Open(path));
        Assert.Equal(0x20, doc.Length);
        Assert.Equal([0xEE, 0x44], Read(doc.Current, 0, 2));
    }

    [Fact]
    public void RefreshKeepsChangedBytes()
    {
        string path = Seq();
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0x1000, [0xDE, 0xAD, 0xBE, 0xEF]);
        Read(doc.Current, 0x2000, 4);
        doc.RefreshFromSource();
        Assert.True(doc.IsModified);
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], Read(doc.Current, 0x1000, 4));
    }

    [Fact]
    [Trait(TC, "TC-ENG-14-04")]
    public void ReadOnlyOpenLetsOtherAppsWrite()
    {
        string path = Seq();

        // 「読み取り専用で開く」: 読み取りのアクセス権だけで開き、編集を受け付けない (ENG-14 の仕様 1、EDIT-16)。
        using var doc = new Document(FileByteSource.Open(path), Options());
        var editor = new EditorState(doc) { ReadOnly = true };
        Assert.NotEqual(EditResult.Done, editor.TypeHexDigit('F'));
        Assert.False(doc.IsModified);

        // 1. アプリのハンドルが読み取りのアクセス権だけを持つ: 他のアプリの書き込みを禁止する共有モードで開けるのは、
        //    書き込みのアクセス権を持つハンドルがないときだけ。
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
        }

        // 2. 他のアプリが書き込み用 (FileAccess.Write、FileShare.ReadWrite) に開き、オフセット 0 に 77 を書ける。
        using (var other = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            other.WriteByte(0x77);
        }

        Assert.Equal(0x77, File.ReadAllBytes(path)[0]);
        Assert.Equal(FileLockState.Unlocked, doc.LockState);
    }
}
