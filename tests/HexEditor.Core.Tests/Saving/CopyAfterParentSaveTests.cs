using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>
/// ENG-39 の仕様 2: コピーとして開いたドキュメントは、親のデータソースのファイルが保存で変わっても、開いたときの内容のまま
/// (親が参照していた元データを保持する)。保存の方式 (その場・安全・ずらしながら) ごとに確かめる。
/// </summary>
[Collection("InPlaceSaver")]
public sealed class CopyAfterParentSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-copysave").FullName;

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

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "temp") };

    private SaveSettings Settings() => new()
    {
        JournalDirectory = Path.Combine(_dir, "recovery"),
        SpillDirectory = Path.Combine(_dir, "spill"),
    };

    private static byte[] Expected(long offset, int length)
    {
        byte[] b = new byte[length];
        TestDataCatalog.Sequence(offset, b);
        return b;
    }

    public enum Method
    {
        InPlace,
        Safe,
        Shift,
    }

    [Theory]
    [InlineData(Method.InPlace)]
    [InlineData(Method.Safe)]
    [InlineData(Method.Shift)]
    public void Copy_keeps_its_content_after_the_parent_is_saved(Method method)
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var parent = new Document(FileByteSource.Open(path), Options());
        using Document copy = Document.CreateCopy(parent.Current, 0x1000, 0x1000, "copy", Options());

        // 親で範囲の中身を変えて (長さが変わる保存では前に挿入もして) 保存する。
        parent.Overwrite(0x1010, [0xAA, 0xBB, 0xCC]);
        if (method != Method.InPlace)
        {
            parent.Insert(0x100, [1, 2, 3, 4]);
        }

        SavePlan plan = SavePlanner.Plan(parent, null, Settings());
        plan = method switch
        {
            Method.Shift => SavePlanner.ConfirmShift(SavePlanner.UseShiftInPlace(plan)),
            _ => plan,
        };
        Assert.Equal(method switch { Method.InPlace => SaveMethod.InPlace, Method.Safe => SaveMethod.Safe, _ => SaveMethod.ShiftInPlace }, plan.Method);
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));

        // ファイルは変わったが、コピーは開いたときの内容のまま。
        Assert.False(parent.IsModified);
        Assert.NotEqual(Expected(0, 0x3000), File.ReadAllBytes(path)[..0x3000]);
        Assert.Equal(Expected(0x1000, 0x1000), ReadAll(copy.Current));

        // コピーへの編集もできる (親とは独立)。
        copy.Insert(0, [0xEE]);
        Assert.Equal([0xEE, .. Expected(0x1000, 0x1000)], ReadAll(copy.Current));
    }
}
