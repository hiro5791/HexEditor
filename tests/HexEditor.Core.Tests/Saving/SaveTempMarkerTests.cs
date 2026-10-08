using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Saving;

/// <summary>
/// ENG-22 の仕様 7・ENG-27 の仕様 7: 安全な保存の一時ファイルの記録と、異常終了で残った一時ファイルの削除の提案。読み取り専用のドキュメントは
/// 元の場所に保存しない (ENG-14 の仕様 4)。
/// </summary>
public sealed class SaveTempMarkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-savetemp").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Recovery => Path.Combine(_dir, "recovery");

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "docs") };

    private SaveSettings Settings() => new() { JournalDirectory = Recovery };

    private static string[] AllFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", new EnumerationOptions { AttributesToSkip = 0 }) : [];

    [Fact]
    public void LeftoverTempFileIsFoundAndDeleted()
    {
        // 異常終了の再現: 記録を書いた後、片付けずに (ハンドルだけ閉じて) 終わった。
        string target = Path.Combine(_dir, "a.bin");
        string temp = Path.Combine(_dir, ".a.bin.~hex0123abcd.tmp");
        File.WriteAllBytes(target, [1]);
        File.WriteAllBytes(temp, [2]);
        SaveTempMarker.Record(Recovery, temp, target).Keep();

        LeftoverTempFile leftover = Assert.Single(SaveTempMarker.Find(Recovery));
        Assert.Equal(temp, leftover.TempPath);
        Assert.Equal(target, leftover.TargetPath);

        SaveTempMarker.Delete(leftover);
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(target));
        Assert.Empty(AllFiles(Recovery));
        Assert.Empty(SaveTempMarker.Find(Recovery));
    }

    [Fact]
    public void MarkersOfSavesInProgressAreSkipped()
    {
        string target = Path.Combine(_dir, "b.bin");
        string temp = Path.Combine(_dir, ".b.bin.~hex0123abcd.tmp");
        File.WriteAllBytes(temp, [2]);
        using (SaveTempMarker marker = SaveTempMarker.Record(Recovery, temp, target))
        {
            // 保存中 (記録を開いている) なら対象にしない。
            Assert.Empty(SaveTempMarker.Find(Recovery));
            Assert.NotNull(marker.Path);
            Assert.True(File.Exists(marker.Path));
        }

        // 片付けたら記録は消える。
        Assert.Empty(AllFiles(Recovery));
    }

    [Fact]
    public void MarkersWithoutTempFileOrWithForeignNamesAreRemoved()
    {
        string target = Path.Combine(_dir, "c.bin");
        File.WriteAllBytes(target, [1]);

        // 一時ファイルがもうない。
        SaveTempMarker.Record(Recovery, Path.Combine(_dir, ".c.bin.~hex0123abcd.tmp"), target).Keep();

        // 安全な保存の一時ファイルの名前でない (書き換えられた記録で関係のないファイルを消さない)。
        SaveTempMarker.Record(Recovery, target, target).Keep();
        Assert.Empty(SaveTempMarker.Find(Recovery));
        Assert.Empty(AllFiles(Recovery));
        Assert.True(File.Exists(target));
        Assert.False(SaveTempMarker.IsSaveTempName(Path.Combine(_dir, "sub", ".c.bin.~hex0123abcd.tmp"), target));
        Assert.True(SaveTempMarker.IsSaveTempName(Path.Combine(_dir, ".c.bin.~hex0123ABCD.tmp"), target));
    }

    [Fact]
    public void SafeSaveRemovesItsMarkerOnSuccessAndOnCancel()
    {
        string path = Path.Combine(_dir, "d.bin");
        File.WriteAllBytes(path, new byte[64]);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0, [0xAB]);

        // キャンセル: 一時ファイルを消し、記録も消す。
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        var center = new Core.Operations.OperationCenter();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            center.RunAsync("save", Core.Operations.OperationKind.WritesExternal, doc, doc.Length, op =>
            {
                op.Cancel();
                SavePlanner.Execute(plan, op);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult());
        SavePlanner.Abort(plan);
        Assert.Empty(AllFiles(Recovery));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", new EnumerationOptions { AttributesToSkip = 0 }));

        // 成功: 置き換えた後に記録を消す。
        plan = SavePlanner.Plan(doc, null, Settings());
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));
        Assert.Equal(65, new FileInfo(path).Length);
        Assert.Empty(AllFiles(Recovery));
    }

    [Fact]
    public void ReadOnlyDocumentCannotBeSavedOverItsFile()
    {
        // ENG-14 の仕様 4: 読み取り専用のドキュメントは元の場所には保存せず、「名前を付けて保存」になる。
        string path = Path.Combine(_dir, "e.bin");
        File.WriteAllBytes(path, new byte[16]);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0x01]);
        doc.SetReadOnly(ReadOnlyReason.User);

        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveIssue.ReadOnly, plan.Issue);
        Assert.Equal(SaveMethod.SaveAs, plan.Method);
        Assert.False(plan.CanExecute);
        Assert.Equal(SaveIssue.ReadOnly, SavePlanner.Plan(doc, path, Settings()).Issue);

        // 別の場所には保存できる。
        string other = Path.Combine(_dir, "e2.bin");
        SavePlan saveAs = SavePlanner.Plan(doc, other, Settings());
        Assert.True(saveAs.CanExecute);
        SavePlanner.Complete(saveAs, SavePlanner.Execute(saveAs));
        Assert.Equal(0x01, File.ReadAllBytes(other)[0]);
        Assert.Equal(0x00, File.ReadAllBytes(path)[0]);
    }
}
