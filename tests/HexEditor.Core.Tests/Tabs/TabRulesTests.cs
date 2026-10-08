using HexEditor.Core.Tabs;

namespace HexEditor.Core.Tests.Tabs;

/// <summary>タブ (UI-09)、並べ替えとピン留め (UI-10)、切り離し (UI-11) の Core の規則。</summary>
public sealed class TabRulesTests
{
    [Fact]
    public void Same_file_names_get_the_smallest_distinguishing_parent_folders()
    {
        string[] suffixes = TabNames.Suffixes(
        [
            ("data.bin", @"C:\td\a\x\data.bin"),
            ("data.bin", @"C:\td\b\x\data.bin"),
            ("other.bin", @"C:\td\a\x\other.bin"),
            ("Untitled 1", null),
        ]);
        Assert.Equal([@" — a\x", @" — b\x", string.Empty, string.Empty], suffixes);

        // 1 つ上で区別できればそれだけ付ける。
        Assert.Equal([" — a", " — b"], TabNames.Suffixes([("data.bin", @"C:\a\data.bin"), ("DATA.BIN", @"C:\b\data.bin")]));

        // 同じファイル名が 1 つなら付けない。
        Assert.Equal([string.Empty], TabNames.Suffixes([("data.bin", @"C:\a\data.bin")]));
    }

    [Fact]
    public void Pinned_tabs_stay_left_and_survive_bulk_close()
    {
        bool[] pinned = [true, false, false, false];
        bool[] modified = [false, true, false, false];

        // ピン留めすると、ピン留めの並びの末尾 (位置 1) に移る。外すと、ピン留めしていない並びの先頭に移る (UI-10 の仕様 2)。
        Assert.Equal(1, TabStripRules.PinTarget(pinned, 3, pin: true));
        Assert.Equal(0, TabStripRules.PinTarget(pinned, 0, pin: false));
        Assert.Equal(1, TabStripRules.PinTarget([true, true, false], 0, pin: false));

        // ピン留めしたタブは、ピン留めしていないタブより右に移せない (仕様 4)。逆も同じ。
        Assert.Equal(0, TabStripRules.ClampMove(pinned, 0, 3));
        Assert.Equal(1, TabStripRules.ClampMove(pinned, 3, 0));
        Assert.Null(TabStripRules.Step(pinned, 0, right: true));
        Assert.Null(TabStripRules.Step(pinned, 1, right: false));
        Assert.Equal(2, TabStripRules.Step(pinned, 1, right: true));
        Assert.Null(TabStripRules.Step(pinned, 3, right: true));

        // 一括で閉じる操作ではピン留めしたタブを閉じない (仕様 3)。
        Assert.Equal([1, 2, 3], TabStripRules.CloseTargets(TabCloseSet.All, pinned, modified, 2));
        Assert.Equal([1, 3], TabStripRules.CloseTargets(TabCloseSet.Others, pinned, modified, 2));
        Assert.Equal([3], TabStripRules.CloseTargets(TabCloseSet.ToRight, pinned, modified, 2));
        Assert.Equal([2, 3], TabStripRules.CloseTargets(TabCloseSet.Saved, pinned, modified, 2));
        Assert.Equal([0], TabStripRules.CloseTargets(TabCloseSet.This, pinned, modified, 0));

        Assert.Equal("abcdefgh", TabStripRules.PinnedTitle("abcdefghij.bin"));
        Assert.Equal("a.bin", TabStripRules.PinnedTitle("a.bin"));
    }

    [Fact]
    public void Ctrl_tab_switches_in_most_recently_used_order()
    {
        var mru = new TabMru<string>();
        string[] open = ["file01", "file02", "file03"];
        foreach (string t in open)
        {
            mru.Touch(t);
        }

        // file01 をクリックした後の Ctrl+Tab は直前の file03 (UI-09 の仕様 6)。
        mru.Touch("file01");
        Assert.Equal("file03", mru.Step(open, forward: true));

        // Ctrl を押したまま、もう一度押すと次の候補。離すと決まる。
        Assert.Equal("file02", mru.Step(open, forward: true));
        Assert.Equal("file03", mru.Step(open, forward: false));
        Assert.Equal("file03", mru.Commit());
        Assert.Equal(["file03", "file01", "file02"], mru.Order);

        // 閉じたタブは候補から外れる。
        mru.Remove("file01");
        Assert.Equal("file02", mru.Step(["file02", "file03"], forward: true));
        mru.Commit();
        Assert.Null(new TabMru<string>().Step(["only"], forward: true));

        // 並び順の切り替え (Ctrl+PageDown) は末尾から先頭に戻る。
        Assert.Equal(0, TabStripRules.Cycle(3, 2, forward: true));
        Assert.Equal(2, TabStripRules.Cycle(3, 0, forward: false));
    }

    [Fact]
    public void Dropping_far_from_the_tab_strip_detaches_the_tab()
    {
        // タブ列 (y = 40〜76) から 32 px 以上離れていれば外 (UI-11 の仕様 1)。
        Assert.Equal(TabDropAction.None, TabDragRules.Decide(40, 76, 100, 1.0, 2));
        Assert.Equal(TabDropAction.NewWindow, TabDragRules.Decide(40, 76, 276, 1.0, 2));
        Assert.Equal(TabDropAction.None, TabDragRules.Decide(40, 76, 130, 2.0, 2));

        // 最後のタブはウィンドウ自体を動かす (仕様 4)。
        Assert.Equal(TabDropAction.MoveWindow, TabDragRules.Decide(40, 76, 276, 1.0, 1));
        Assert.Equal((400, 300), TabDragRules.Place(100, 100, 50, 60, 350, 260));
    }
}
