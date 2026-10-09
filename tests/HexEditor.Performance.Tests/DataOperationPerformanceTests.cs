using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// ビット単位の挿入と削除 (EDIT-37) の性能。ダイアログの実行と同じ <see cref="BitShifter"/> を直接呼び、表示中の行を読み終えるまでを計る。
/// </summary>
[Trait("Category", "Performance")]
public sealed class DataOperationPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    [Fact]
    [Trait(TC, "TC-EDIT-37-02")]
    public void InsertingEightBitsIntoOneHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        long length = doc.Length;
        var spec = new DataOperationSpec
        {
            Kind = DataOperationKind.InsertBits,
            BitOffset = 0x10,
            BitIndex = 7,
            BitCount = 8,
            FillWithOne = true,
            BitScope = BitShiftScope.ToEnd,
        };
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            times.Add(Time(() =>
            {
                // ピース操作で行うため確認ダイアログは出ない (書き直すバイトが 0)。
                Assert.Equal(0, BitShifter.Analyze(doc.Length, doc.CanResize, null, spec).RewriteBytes);
                using BitEditPlan plan = BitShifter.For(doc).Build(doc.Current, doc.CanResize, null, spec);
                plan.Apply(doc, "ビットの挿入");
                ReadVisible(editor);
            }));
            Assert.Equal(length + 1, doc.Length);
            Assert.Equal(0xFF, Read(doc, 0x10, 1)[0]);
            Assert.Equal(TestDataCatalog.Marker(0x40000000), Read(doc, 0x40000001, 17));
            editor.Undo();
            Assert.Equal(length, doc.Length);
        }

        output.Report($"100 GB への 8 ビットの挿入: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= Limit, Summary(times));
    }
}
