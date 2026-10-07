using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 編集の性能テスト (03-editing の「巨大ファイル・長時間処理」)。Hex ビューと同じ <see cref="EditorState"/> を直接操作し、
/// 操作から表示用の読み込み (<see cref="DocumentSnapshot.ReadForDisplay"/>) の完了までを計る。描画のフレームの計測は UI の性能テスト。
/// </summary>
[Trait("Category", "Performance")]
public sealed class EditingPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InputLimit = TimeSpan.FromMilliseconds(50);
    private static readonly long Hundred = 100 * GiB;

    /// <summary>操作のあと、状態の変化の通知 (ステータスバーの更新のきっかけ) を受け、表示中の行を読み終えるまでの時間。</summary>
    private static TimeSpan TimeUntilShown(EditorState editor, Action action) => Time(() =>
    {
        action();
        ReadVisible(editor);
    });

    [Fact]
    [Trait(TC, "TC-EDIT-02-02")]
    public void CtrlShiftEndOnHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            editor.MoveToStart();
            Assert.False(editor.HasSelection);
            times.Add(TimeUntilShown(editor, () => editor.MoveToEnd(extend: true)));
            Assert.Equal(0, editor.SelectionStart);
            Assert.Equal(107_374_182_400, editor.SelectionLength);
            Assert.Equal(107_374_182_400, editor.Cursor);
        }

        output.Report($"Ctrl+Shift+End: {Summary(times)}");
        Assert.True(MaxExceptFirst(times) <= Limit, Summary(times));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-03-02")]
    public void SelectAllOnHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            editor.ClearSelection();
            times.Add(TimeUntilShown(editor, editor.SelectAll));
            Assert.Equal(0, editor.SelectionStart);
            Assert.Equal(107_374_182_400, editor.SelectionLength);
        }

        output.Report($"Ctrl+A: {Summary(times)}");
        Assert.True(MaxExceptFirst(times) <= Limit, Summary(times));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-11-06")]
    public void InsertModeTypingInTheMiddleOfHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        Assert.Equal(EditResult.Done, editor.ToggleInsertMode());
        editor.GoTo(0xC80000000);
        var times = new List<TimeSpan>();
        for (int i = 0; i < 101; i++)
        {
            long at = editor.Cursor;

            // 手順 2: 上位ニブル。そのセルの表示が 10 になるまで。
            times.Add(Time(() =>
            {
                Assert.Equal(EditResult.Done, editor.TypeHexDigit('1'));
                byte[] shown = ReadVisible(editor);
                Assert.Equal(0x10, shown[at - editor.TopRow * editor.BytesPerRow]);
            }));

            // 手順 3: 下位ニブルを確定させる。
            Assert.Equal(EditResult.Done, editor.TypeHexDigit('1'));
        }

        output.Report($"挿入モードの入力 (50 GiB): {Summary(times)}");
        Assert.True(MaxExceptFirst(times) <= InputLimit, Summary(times));
        Assert.Equal(107_374_182_400 + 101, doc.Length);
        Assert.Equal(Enumerable.Repeat((byte)0x11, 101).ToArray(), Read(doc, 0xC80000000, 101));
        Assert.Equal(TestDataCatalog.Marker(0xC80000000), Read(doc, 0xC80000000 + 101, 17));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-13-01")]
    public void DeletingFiftyGigabytesAndUndoAreFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var deletes = new List<TimeSpan>();
        var undos = new List<TimeSpan>();

        // 手順 1〜5 (1 回目は選択して Delete、2 回目以降は Ctrl+Y で同じ削除をやり直す)。
        for (int i = 0; i < 12; i++)
        {
            if (i == 0)
            {
                editor.Select(0, 50 * GiB);
                deletes.Add(TimeUntilShown(editor, () => Assert.Equal(EditResult.Done, editor.Delete())));
            }
            else
            {
                deletes.Add(TimeUntilShown(editor, editor.Redo));
            }

            Assert.Equal(53_687_091_200, doc.Length);
            Assert.Equal(TestDataCatalog.Marker(0xC80000000), Read(doc, 0, 17));

            undos.Add(TimeUntilShown(editor, editor.Undo));
            Assert.Equal(107_374_182_400, doc.Length);
            Assert.Equal(TestDataCatalog.Marker(0), Read(doc, 0, 17));
            Assert.Equal(TestDataCatalog.Marker(0xC80000000), Read(doc, 0xC80000000, 17));
        }

        output.Report($"50 GiB の削除: {Summary(deletes)} / 元に戻す: {Summary(undos)}");
        Assert.True(MaxExceptFirst(deletes) <= Limit, "削除 " + Summary(deletes));
        Assert.True(MaxExceptFirst(undos) <= Limit, "元に戻す " + Summary(undos));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-19-02")]
    public void UndoingAndRedoingLargeEditsIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var times = new List<(string Step, TimeSpan Time)>();
        void Step(string name, Action action) => times.Add((name, TimeUntilShown(editor, action)));

        // 手順 1: 1G から 10G を削除する。
        editor.Select(GiB, 10 * GiB);
        Step("削除", () => Assert.Equal(EditResult.Done, editor.Delete()));
        Assert.Equal(96_636_764_160, doc.Length);

        // 手順 2: 編集 > 挿入… で 2G の位置に 00 を 1T 挿入する。
        Step("1T の挿入", () => doc.InsertPattern(2 * GiB, TiB, [0x00]));
        Assert.Equal(1_196_148_391_936, doc.Length);

        // 手順 3: 0x80000000 の上位ニブルに上書きモードで AA を入力する (2 桁を 1 回の操作として測る)。
        editor.GoTo(0x80000000);
        Assert.False(editor.InsertMode);
        Step("AA の入力", () =>
        {
            Assert.Equal(EditResult.Done, editor.TypeHexDigit('A'));
            Assert.Equal(EditResult.Done, editor.TypeHexDigit('A'));
        });
        Assert.Equal(0xAA, Read(doc, 0x80000000, 1)[0]);

        // 手順 4: Ctrl+Z を 3 回。
        for (int i = 0; i < 3; i++)
        {
            Step($"元に戻す {i + 1}", editor.Undo);
        }

        Assert.Equal(107_374_182_400, doc.Length);
        foreach (long marker in new[] { 0x40000000L, 0x80000000L, 0x100000000L, 10 * GiB, Hundred - 17 })
        {
            Assert.Equal(TestDataCatalog.Marker(marker), Read(doc, marker, 17));
        }

        // 手順 5: Ctrl+Y を 3 回。
        for (int i = 0; i < 3; i++)
        {
            Step($"やり直す {i + 1}", editor.Redo);
        }

        Assert.Equal(1_196_148_391_936, doc.Length);
        Assert.Equal(0xAA, Read(doc, 0x80000000, 1)[0]);

        output.Report(string.Join(Environment.NewLine, times.Select(t => $"{t.Step}: {Ms(t.Time)}")));
        Assert.All(times, t => Assert.True(t.Time <= Limit, $"{t.Step}: {Ms(t.Time)}"));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-19-03")]
    public void TenThousandSingleByteEditsUseLittleMemory()
    {
        using Document doc = Open("TD-SEQ-1M");
        EditorState editor = Editor(doc);
        Assert.False(editor.InsertMode);
        int entriesBefore = doc.History.CurrentIndex;
        long before = PrivateBytesAfterGc();

        // 手順 1: k × 97 mod 1,048,576 に移って FF を入力する (毎回カーソルを動かすので別々の編集グループになる)。
        for (int k = 0; k < 10_000; k++)
        {
            editor.GoTo(k * 97L % 1_048_576);
            editor.TypeHexDigit('F');
            editor.TypeHexDigit('F');
        }

        // 手順 2・3
        Assert.Equal(10_000, doc.History.CurrentIndex - entriesBefore);
        long after = PrivateBytesAfterGc();
        output.Report($"1 万回の 1 バイト書き換え: 履歴 {doc.History.CurrentIndex - entriesBefore} 件、プライベートバイトの増加 {(after - before) / (double)MiB:F1} MiB");
        Assert.True(after - before <= 100 * MiB, $"増加 {(after - before) / MiB} MiB");
        Assert.Equal(0xFF, Read(doc, 9_999L * 97 % 1_048_576, 1)[0]);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-22-03")]
    public void CopyingTenGigabytesAndPastingIntoAnotherTab()
    {
        using var clipboard = new InAppClipboard();
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);

        // 手順 1・2: 開始 0、長さ 10G を選択してコピーする (アプリと同じく、アプリ内クリップボードへの記録と形式の判定)。
        editor.Select(0, 10 * GiB);
        var times = new List<TimeSpan>();
        ClipboardPlan? plan = null;
        for (int i = 0; i < 12; i++)
        {
            times.Add(Time(() =>
            {
                clipboard.Copy(doc, editor.SelectionStart, editor.SelectionLength);
                plan = ClipboardPlan.For(editor.SelectionLength, () => ClipboardPlan.HexTextLength(editor.SelectionLength));
            }));
        }

        output.Report($"10 GiB のコピー: {Summary(times)}");
        Assert.True(MaxExceptFirst(times) <= Limit, Summary(times));

        // 手順 3・4: 上限を超えるので、システムのクリップボードには Meta と 1 行のテキストだけ (InfoBar で知らせる)。
        Assert.True(plan!.InAppOnly);
        Assert.False(plan.Binary);
        Assert.Equal(ClipboardTextKind.TooLargeLine, plan.Text);

        // 手順 5・6: 新しいタブ (無題のドキュメント) に貼り付ける。
        using var target = new Document(MemoryByteSource.CreateEmpty("無題 1"), Options());
        EditorState targetEditor = Editor(target);
        Assert.Equal(EditResult.Done, targetEditor.Paste(clipboard.Current!.Range, overwrite: false));
        Assert.Equal(10_737_418_240, target.Length);
        Assert.Equal(TestDataCatalog.Marker(0), Read(target, 0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x40000000), Read(target, 0x40000000, 17));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-24-01")]
    public void CopyingWholeHundredGigabytesIntoNewTabIsFastAndUsesNoMemory()
    {
        using var clipboard = new InAppClipboard();
        using Document doc = Open("TD-SPARSE-100G");
        string path = TestDataCatalog.Get("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        long before = PrivateBytesAfterGc();

        // 手順 1: Ctrl+A、Ctrl+C。
        editor.SelectAll();
        InAppClip clip = clipboard.Copy(doc, editor.SelectionStart, editor.SelectionLength);

        // 手順 2: Ctrl+N で新しいタブを作り、Ctrl+V。
        using var target = new Document(MemoryByteSource.CreateEmpty("無題 1"), Options());
        EditorState targetEditor = Editor(target);
        TimeSpan paste = TimeUntilShown(targetEditor, () => Assert.Equal(EditResult.Done, targetEditor.Paste(clip.Range, overwrite: false)));

        // 手順 3
        Assert.Equal(107_374_182_400, target.Length);
        foreach (long offset in new[] { 0L, 0x7FFFFFF8L, 0x100000000L, 107_374_182_383L })
        {
            Assert.Equal(ReadFile(path, offset, 17), Read(target, offset, 17));
        }

        // 手順 4
        long after = PrivateBytesAfterGc();
        output.Report($"100 GiB の貼り付け: {Ms(paste)}、プライベートバイトの増加 {(after - before) / (double)MiB:F1} MiB");
        Assert.True(paste <= TimeSpan.FromSeconds(1), Ms(paste));
        Assert.True(after - before <= 50 * MiB, $"増加 {(after - before) / MiB} MiB");
    }
}
