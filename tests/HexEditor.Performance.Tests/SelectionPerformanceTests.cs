using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>選択の性能テスト (EDIT-08、EDIT-18 の「巨大ファイル・長時間処理」)。Hex ビューと同じ <see cref="EditorState"/> を直接操作する。</summary>
[Trait("Category", "Performance")]
public sealed class SelectionPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    [Fact]
    [Trait(TC, "TC-EDIT-18-03")]
    public void MovingTenGigabytesWithinTheFileIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 11; i++)
        {
            // 0x1000 から 10 GiB を選び、0x100 (選択範囲の前) へドロップして移動する。表示中の行を読み終えるまでを計る。
            editor.Select(0x1000, 10L << 30);
            times.Add(Time(() =>
            {
                Assert.Equal(EditResult.Done, editor.DropSelection(0x100, SelectionDropKind.Move));
                ReadVisible(editor);
            }));
            Assert.Equal(100L << 30, doc.Length);
            Assert.Equal("@0000000040000000"u8.ToArray(), Read(doc, 0x3FFFF100, 17));
            editor.Undo();
        }

        output.Report($"move 10 GiB: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= TimeSpan.FromMilliseconds(100), Summary(times));
    }

    /// <summary>EDIT-08 の「巨大ファイル・長時間処理」: カーソル 10,000 個への 1 回の入力が 50 ms 以内。</summary>
    [Fact]
    public void TypingIntoTenThousandCaretsIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        editor.Click(0, ActiveColumn.Hex, false, false);
        for (int i = 1; i < EditorState.MaxCarets; i++)
        {
            editor.AddCaret(i * 4096L);
        }

        Assert.Equal(EditorState.MaxCarets, editor.CaretCount);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 11; i++)
        {
            times.Add(Time(() =>
            {
                editor.TypeHexDigit('A');
                ReadVisible(editor);
            }));
        }

        output.Report($"typing into 10,000 carets: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= TimeSpan.FromMilliseconds(50), Summary(times));
    }
}
