using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// バイトの挿入 (EDIT-14)・ファイルサイズの変更 (EDIT-15)・塗りつぶし (EDIT-29) の性能。ダイアログの実行と同じ
/// <see cref="ContentBuilder"/> と <see cref="EditCommands"/> を直接呼び、表示中の行を読み終えるまでを計る。
/// </summary>
[Trait("Category", "Performance")]
public sealed class ContentEditPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";
    private const long KiB = TestDataCatalog.KiB;
    private const long MaxMemoryGrowth = 50L * 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    private static TimeSpan TimeUntilShown(EditorState editor, Action action) => Time(() =>
    {
        action();
        ReadVisible(editor);
    });

    [Fact]
    [Trait(TC, "TC-EDIT-14-01")]
    public void InsertingOneTebibyteOfZerosIsFast()
    {
        using Document doc = Open("TD-SEQ-1M");
        EditorState editor = Editor(doc);
        long before = PrivateBytesAfterGc();
        var inserts = new List<TimeSpan>();
        var undos = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            inserts.Add(TimeUntilShown(editor, () =>
            {
                if (i == 0)
                {
                    EditContent content = ContentBuilder.For(doc).Build(FillSpec.Zero, 0x10, TiB);
                    EditCommands.Insert(editor, 0x10, content, selectInserted: true);
                }
                else
                {
                    editor.Redo();
                }
            }));
            Assert.Equal(MiB + TiB, doc.Length);
            Assert.Equal((0x10L, TiB), (editor.SelectionStart, editor.SelectionLength));
            Assert.Equal(new byte[] { 0x0F, 0x00 }, Read(doc, 0x0F, 2));
            Assert.Equal(new byte[] { 0x00, 0x10 }, Read(doc, 0x1000000000F, 2));
            if (i == 0)
            {
                Assert.True(PrivateBytesAfterGc() - before <= MaxMemoryGrowth, "メモリ使用量");
            }

            undos.Add(TimeUntilShown(editor, editor.Undo));
            Assert.Equal(MiB, doc.Length);
            Assert.Equal(0x10, Read(doc, 0x10, 1)[0]);
        }

        output.Report($"1 TiB の挿入: {Summary(inserts)} / 元に戻す: {Summary(undos)}");
        TimeLimit(MaxExceptFirst(inserts) <= Limit, "挿入 " + Summary(inserts));
        TimeLimit(MaxExceptFirst(undos) <= Limit, "元に戻す " + Summary(undos));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-15-01")]
    public void ResizingOneKibibyteToFourGibibytesIsFast()
    {
        using Document doc = Open("TD-EDIT-SEQ-1K");
        EditorState editor = Editor(doc);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            times.Add(TimeUntilShown(editor, () =>
            {
                if (i == 0)
                {
                    EditCommands.Resize(editor, 4 * GiB, ContentBuilder.For(doc).Build(FillSpec.Zero, KiB, 4 * GiB - KiB));
                }
                else
                {
                    editor.Redo();
                }
            }));
            Assert.Equal(4 * GiB, doc.Length);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, Read(doc, 0x3FF, 2));
            Assert.Equal(0x00, Read(doc, 0xFFFFFFFF, 1)[0]);

            // 末尾の行 (Ctrl+End) はすべて 00。
            editor.MoveToEnd();
            Assert.All(ReadVisible(editor).TakeLast(16), b => Assert.Equal(0, b));

            if (i == 0)
            {
                // 5. 現在と同じ長さでは編集履歴が増えない。
                int count = doc.History.Count;
                Assert.Equal(0, EditCommands.Resize(editor, 4 * GiB));
                Assert.Equal(count, doc.History.Count);
            }

            editor.Undo();
            Assert.Equal(KiB, doc.Length);
        }

        output.Report($"4 GiB への変更: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= Limit, Summary(times));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-29-01")]
    public void FillingOneHundredGigabytesWithZerosIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        long length = doc.Length;
        long before = PrivateBytesAfterGc();
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            editor.SelectAll();
            times.Add(TimeUntilShown(editor, () =>
                EditCommands.Overwrite(editor, 0, ContentBuilder.For(doc).Build(FillSpec.Zero, 0, length))));
            Assert.Equal(length, doc.Length);
            Assert.All(Read(doc, 0, 17), b => Assert.Equal(0, b));
            Assert.All(Read(doc, 0x80000000, 17), b => Assert.Equal(0, b));
            Assert.All(Read(doc, length - 17, 17), b => Assert.Equal(0, b));
            if (i == 0)
            {
                Assert.True(PrivateBytesAfterGc() - before <= MaxMemoryGrowth, "メモリ使用量");
            }

            editor.Undo();
            Assert.Equal(TestDataCatalog.Marker(0), Read(doc, 0, 17));
        }

        output.Report($"100 GB の塗りつぶし: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= Limit, Summary(times));
    }
}
