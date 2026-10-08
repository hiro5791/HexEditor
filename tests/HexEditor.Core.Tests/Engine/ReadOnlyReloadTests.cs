using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// 読み取り専用・処理中のドキュメントの再読み込み (EDIT-16 の仕様 2、ENG-18、ENG-19 の仕様 11・12): 再読み込みと変更の破棄は読み取り専用でも
/// でき、マージはできない。処理中はどれもできない (呼び出し側が処理の後に回す)。
/// </summary>
public sealed class ReadOnlyReloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-roreload").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "recovery"), LockPolicy = FileLockPolicy.None };

    private string File16(string name, byte fill)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 16).ToArray());
        return path;
    }

    [Fact]
    public void ReadOnlyDocumentCanBeReloadedFromAChangedFile()
    {
        // 「読み取り専用で開く」で開いたファイルを他のアプリが書き換えた (ENG-14 の受け入れ基準 4) → 自動の再読み込み。
        string path = File16("a.bin", 0x11);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.SetReadOnly(ReadOnlyReason.OpenedReadOnly);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x22, 32).ToArray());

        doc.ReplaceSource(FileByteSource.Open(path));
        Assert.True(doc.IsReadOnly);
        Assert.Equal(32, doc.Length);
        Assert.Equal(0x22, Read(doc.Current, 31, 1)[0]);
    }

    [Fact]
    public void ReadOnlyDocumentCanDiscardItsChanges()
    {
        // 編集してから読み取り専用にした (EDIT-16 の仕様 5) ドキュメントの「変更を破棄して再読み込み」。
        string path = File16("b.bin", 0x11);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0xFF]);
        doc.SetReadOnly(ReadOnlyReason.User);

        doc.DiscardChanges();
        Assert.False(doc.IsModified);
        Assert.Equal(0x11, Read(doc.Current, 0, 1)[0]);
        Assert.True(doc.IsReadOnly);
    }

    [Fact]
    public void ReadOnlyDocumentCannotMerge()
    {
        string path = File16("c.bin", 0x11);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0xFF]);
        doc.SetReadOnly(ReadOnlyReason.User);
        using FileByteSource changed = FileByteSource.Open(File16("c2.bin", 0x22));
        Assert.Throws<DocumentReadOnlyException>(() => doc.MergeOnto(changed));
        Assert.Equal(0xFF, Read(doc.Current, 0, 1)[0]);
    }

    [Fact]
    public void EditLockedDocumentRefusesReloadDiscardAndMerge()
    {
        // ドキュメントを変える長時間処理の実行中 (ENG-09 の仕様 7) は、再読み込み・破棄・マージのどれもできない。
        string path = File16("d.bin", 0x11);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0xFF]);
        doc.SetEditLock(true);
        using FileByteSource other = FileByteSource.Open(File16("d2.bin", 0x22));
        Assert.Throws<DocumentLockedException>(() => doc.ReplaceSource(other));
        Assert.Throws<DocumentLockedException>(() => doc.DiscardChanges());
        Assert.Throws<DocumentLockedException>(() => doc.MergeOnto(other));
        Assert.Equal(0xFF, Read(doc.Current, 0, 1)[0]);

        // 処理が終われば再読み込みできる。
        doc.SetEditLock(false);
        doc.ReplaceSource(FileByteSource.Open(path));
        Assert.False(doc.IsModified);
        Assert.Equal(0x11, Read(doc.Current, 0, 1)[0]);
    }

    [Fact]
    public void MergeIsNotOfferedForReadOnlyDocuments()
    {
        Assert.False(ExternalChangeRules.ActionsFor(ExternalChangePrompt.Changed, modified: true, readOnly: true).HasFlag(ExternalChangeActions.Merge));
        Assert.True(ExternalChangeRules.ActionsFor(ExternalChangePrompt.Changed, modified: true, readOnly: true).HasFlag(ExternalChangeActions.Reload));
        Assert.True(ExternalChangeRules.ActionsFor(ExternalChangePrompt.Changed, modified: true, readOnly: false).HasFlag(ExternalChangeActions.Merge));
    }
}
