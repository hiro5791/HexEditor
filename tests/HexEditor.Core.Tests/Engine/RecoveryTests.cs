using HexEditor.Core.Engine;
using HexEditor.Core.Recovery;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-27 復旧用データ、PKG-30 異常終了時のデータ (Core の部分)。</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-recovery").FullName;

    private string Root => Path.Combine(_dir, "recovery");

    private DocumentOptions Options() => new() { TempDirectory = Root, AddBufferMemoryLimit = AddBuffer.ChunkSize };

    /// <summary>プロセスが落ちたのと同じ状態にする: ロック・ハンドルを放すが、復旧用データは消さない。</summary>
    private static void SimulateCrash(Document doc, DocumentRecovery recovery)
    {
        recovery.Release();
        doc.AddBuffer.DisposeKeepingFile();
        doc.Dispose();
    }

    [Fact]
    [Trait(TC, "TC-ENG-27-01")]
    public void RestoresContentAsModifiedWithCursorAndNoUndo()
    {
        string path = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        var doc = new Document(FileByteSource.Open(path), Options());
        var recovery = new DocumentRecovery(Root, doc.Id);
        doc.Insert(0x10, [0xDE, 0xAD, 0xBE, 0xEF]);
        doc.Overwrite(0x2000, [0x77]);
        byte[] expected = Read(doc.Current, 0, (int)doc.Length);
        recovery.Write(DocumentRecovery.Capture(doc, 0x2000, 0x2000, 0)!);
        SimulateCrash(doc, recovery);

        RecoveryEntry entry = Assert.Single(RecoveryStore.Scan(Root));
        Assert.Equal("TD-SEQ-1M.bin", entry.Record.DisplayName);
        Assert.Equal(path, entry.Record.Path);
        Assert.Equal(5, entry.Record.ChangedBytes);

        RestoredDocument restored = RecoveryStore.Restore(entry, Options());
        using (Document again = restored.Document)
        {
            Assert.False(restored.SourceChanged);
            Assert.Equal(expected, Read(again.Current, 0, (int)again.Length));
            Assert.True(again.IsModified);
            Assert.False(again.History.CanUndo);
            Assert.Equal(0x2000, restored.Record.Cursor);

            // 保存すると、そのドキュメントの復旧用データは消える (仕様 5)。
            again.CompleteSave(DocumentSaver.Save(again.Current, path));
            restored.Recovery.Clear();
            Assert.False(File.Exists(restored.Recovery.StatePath));
        }

        restored.Recovery.Dispose();
        Assert.Empty(RecoveryStore.Scan(Root));
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    [Trait(TC, "TC-ENG-27-02")]
    public void RecoveryDataOfOneByteEditInHugeFileIsSmall()
    {
        string path = TestDataCatalog.Generate("TD-SPARSE-100G", _dir);
        using var doc = new Document(FileByteSource.Open(path), Options());
        using var recovery = new DocumentRecovery(Root, doc.Id);
        doc.Overwrite(1L << 32, [0x5A]);
        recovery.Write(DocumentRecovery.Capture(doc, 1L << 32, 1L << 32, 0)!);

        long total = Directory.EnumerateFiles(recovery.Folder).Sum(f => new FileInfo(f).Length);
        Assert.True(total < 1024 * 1024, $"{total} bytes");
    }

    [Fact]
    public void ReportsChangedSourceFile()
    {
        string path = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        var doc = new Document(FileByteSource.Open(path), Options());
        var recovery = new DocumentRecovery(Root, doc.Id);
        doc.Overwrite(0, [0xAB]);
        recovery.Write(DocumentRecovery.Capture(doc, 0, 0, 0)!);
        SimulateCrash(doc, recovery);

        // 起動する前に、元のファイルが書き換えられた。
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 0x80000;
            stream.WriteByte(0xEE);
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        RestoredDocument restored = RecoveryStore.Restore(Assert.Single(RecoveryStore.Scan(Root)), Options());
        Assert.True(restored.SourceChanged);
        restored.Document.Dispose();
        restored.Recovery.Dispose();
    }

    [Fact]
    public void ShortenedSourceIsRecoveredUpToItsNewLength()
    {
        // ENG-27 の仕様 6・受け入れ基準 4: 元のファイルが記録より短くなっていても、拒否せずに範囲内の部分を復旧する (読み取り専用で開く)。
        string path = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        var doc = new Document(FileByteSource.Open(path), Options());
        var recovery = new DocumentRecovery(Root, doc.Id);
        doc.Insert(0x10, [0xDE, 0xAD]);
        doc.Overwrite(0x2000, [0x77]);
        recovery.Write(DocumentRecovery.Capture(doc, 0, 0, 0)!);
        SimulateCrash(doc, recovery);

        // 起動する前に、元のファイルが 0x1000 バイトに切り詰められた。
        byte[] original = File.ReadAllBytes(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(0x1000);
        }

        RestoredDocument restored = RecoveryStore.Restore(Assert.Single(RecoveryStore.Scan(Root)), Options());
        using (Document again = restored.Document)
        {
            Assert.True(restored.SourceChanged);
            // 範囲外になった元データは除き、挿入・上書きしたデータは残す (0x2000 の 77 は切り詰めた元データの後ろに来る)。
            byte[] expected = [.. original[..0x10], 0xDE, 0xAD, .. original[0x10..0x1000], 0x77];
            Assert.Equal(expected, Read(again.Current, 0, (int)again.Length));
            Assert.True(again.IsModified);
        }

        restored.Recovery.Dispose();
    }

    [Fact]
    public void ClampingDropsPiecesBeyondTheSource()
    {
        Piece[] pieces = [Piece.Original(0, 10), Piece.Added(0, 4), Piece.Original(10, 10), Piece.Original(30, 5)];
        Piece[] clamped = [.. RecoveryStore.ClampToSource(pieces, 15)];
        Assert.Equal([Piece.Original(0, 10), Piece.Added(0, 4), Piece.Original(10, 5)], clamped);
    }

    [Fact]
    public void RestoresUntitledDocumentAndLargeSpilledInput()
    {
        // 無題のドキュメントは元データなしで復元する。メモリの上限を超えて一時ファイルに退避した入力も戻る。
        var doc = new Document(MemoryByteSource.CreateEmpty("Untitled 1"), Options());
        var recovery = new DocumentRecovery(Root, doc.Id);
        byte[] big = new byte[3 * AddBuffer.ChunkSize + 123];
        new Random(1).NextBytes(big);
        doc.Insert(0, big);
        doc.Insert(0, [0x01, 0x02, 0x03]);
        doc.InsertPattern(3, 10, [0xAA, 0xBB]);
        byte[] expected = Read(doc.Current, 0, (int)doc.Length);
        recovery.Write(DocumentRecovery.Capture(doc, 0, 0, 0)!);
        SimulateCrash(doc, recovery);

        RestoredDocument restored = RecoveryStore.Restore(Assert.Single(RecoveryStore.Scan(Root)), Options());
        using (restored.Document)
        {
            Assert.Null(restored.Record.Path);
            Assert.Equal(expected, Read(restored.Document.Current, 0, (int)restored.Document.Length));

            // 復旧したドキュメントにも続けて入力できる。
            restored.Document.Insert(0, [0x09]);
            Assert.Equal(0x09, Read(restored.Document.Current, 0, 1)[0]);
        }

        restored.Recovery.Dispose();
    }

    [Fact]
    public void DataInUseByRunningInstanceIsNotListed()
    {
        using var doc = new Document(MemoryByteSource.CreateEmpty("Untitled 1"), Options());
        using var recovery = new DocumentRecovery(Root, doc.Id);
        doc.Insert(0, [0x01]);
        recovery.Write(DocumentRecovery.Capture(doc, 0, 0, 0)!);

        Assert.Empty(RecoveryStore.Scan(Root));
    }

    [Fact]
    public void KeepsPreviousDataWhenCurrentStateIsBeforeSave()
    {
        // 保存前の版に Undo した直後は、置き換え前の元データを指すため参照で記録できない。前回のデータを残す。
        string path = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0xAB]);
        doc.CompleteSave(DocumentSaver.Save(doc.Current, path));
        doc.Undo();
        Assert.Null(DocumentRecovery.Capture(doc, 0, 0, 0));
    }

    [Fact]
    public void DeletesRecoveryDataOlderThan30Days()
    {
        // 30 日以上前の復旧用データは起動時に消す。使用中のもの (他のインスタンス) は消さない (PKG-13 の仕様 5)。
        var old = new Document(MemoryByteSource.CreateEmpty("Untitled 1"), Options());
        var oldRecovery = new DocumentRecovery(Root, old.Id);
        old.Insert(0, [0x01]);
        oldRecovery.Write(DocumentRecovery.Capture(old, 0, 0, 0)!);
        SimulateCrash(old, oldRecovery);

        using var live = new Document(MemoryByteSource.CreateEmpty("Untitled 2"), Options());
        using var liveRecovery = new DocumentRecovery(Root, live.Id);
        live.Insert(0, [0x02]);
        liveRecovery.Write(DocumentRecovery.Capture(live, 0, 0, 0)!);

        Assert.Equal(0, RecoveryStore.DeleteExpired(Root, RecoveryStore.MaxAge, DateTime.UtcNow));
        Assert.Single(RecoveryStore.Scan(Root));

        Assert.Equal(1, RecoveryStore.DeleteExpired(Root, RecoveryStore.MaxAge, DateTime.UtcNow.AddDays(31)));
        Assert.Empty(RecoveryStore.Scan(Root));
        Assert.True(File.Exists(liveRecovery.StatePath));
    }

    [Fact]
    public void ScanRemovesLeftoverFoldersWithoutState()
    {
        string leftover = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leftover);
        File.WriteAllBytes(Path.Combine(leftover, "add.bin"), [1, 2, 3]);

        Assert.Empty(RecoveryStore.Scan(Root));
        Assert.False(Directory.Exists(leftover));
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
