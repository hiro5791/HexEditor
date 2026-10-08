using HexEditor.Core.Panels;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Panels;

/// <summary>パネルの配置 (UI-05)。画面での確かめは UI テスト (PanelTests)。</summary>
public sealed class PanelLayoutTests
{
    private static readonly Dictionary<string, PanelDock> Defaults = new() { ["inspector"] = PanelDock.Right, ["bookmarks"] = PanelDock.Left };

    [Fact]
    [Trait(TC, "TC-UI-05-01")]
    public void Moved_panel_stays_after_saving_and_loading()
    {
        var layout = new PanelLayout();
        layout.Show("inspector", PanelDock.Right);
        Assert.Equal(PanelDock.Right, layout.Find("inspector")!.Dock);
        layout.Move("inspector", PanelDock.Bottom, PanelDock.Right);

        PanelLayout restored = PanelLayout.FromJson(layout.ToJson());
        Assert.Equal(PanelDock.Bottom, restored.Find("inspector")!.Dock);
        Assert.True(restored.Find("inspector")!.Visible);
        Assert.Equal("inspector", restored.ActiveTab[PanelDock.Bottom]);
        Assert.Empty(restored.VisibleIn(PanelDock.Right));
    }

    [Fact]
    [Trait(TC, "TC-UI-05-02")]
    public void Floating_panel_redocks_to_where_it_came_from()
    {
        var layout = new PanelLayout();
        layout.Show("inspector", PanelDock.Right);
        layout.Move("inspector", PanelDock.Floating, PanelDock.Right);
        layout.Find("inspector")!.FloatingBounds = new PanelBounds(100, 100, 320, 480);
        PanelLayout restored = PanelLayout.FromJson(layout.ToJson());
        Assert.Equal(PanelDock.Floating, restored.Find("inspector")!.Dock);
        Assert.Equal(new PanelBounds(100, 100, 320, 480), restored.Find("inspector")!.FloatingBounds);

        restored.Redock("inspector");
        Assert.Equal(PanelDock.Right, restored.Find("inspector")!.Dock);
        Assert.Empty(restored.VisibleIn(PanelDock.Floating));

        // 画面外の浮動パネルはメインウィンドウの右上に置く (UI-05 の「エラー」)。
        PanelBounds main = new(0, 0, 1280, 800);
        PanelBounds placed = PanelLayout.EnsureOnScreen(new PanelBounds(5000, 100, 320, 480), [new PanelBounds(0, 0, 1920, 1040)], main);
        Assert.Equal(1280 - 320 - 16, placed.X);
        Assert.Equal(new PanelBounds(10, 10, 320, 480), PanelLayout.EnsureOnScreen(new PanelBounds(10, 10, 320, 480), [new PanelBounds(0, 0, 1920, 1040)], main));
    }

    [Fact]
    [Trait(TC, "TC-UI-05-03")]
    public void Panels_in_the_same_place_share_tabs_and_reset_restores_defaults()
    {
        var layout = new PanelLayout();
        layout.Show("inspector", PanelDock.Right);
        layout.Show("bookmarks", PanelDock.Left);
        layout.Move("bookmarks", PanelDock.Right, PanelDock.Left);
        Assert.Equal(["inspector", "bookmarks"], layout.VisibleIn(PanelDock.Right).Select(p => p.Id));
        Assert.Equal("bookmarks", layout.ActiveTab[PanelDock.Right]);
        layout.RightWidth = 500;
        layout.HiddenDocks.Add(PanelDock.Left);

        layout.ResetToDefault(Defaults);
        Assert.Equal(PanelDock.Right, layout.Find("inspector")!.Dock);
        Assert.Equal(PanelDock.Left, layout.Find("bookmarks")!.Dock);
        Assert.True(layout.Find("bookmarks")!.Visible);
        Assert.Equal(PanelLayout.DefaultSideWidth, layout.RightWidth);
        Assert.Empty(layout.HiddenDocks);
    }

    [Fact]
    public void Hiding_and_sizes()
    {
        var layout = new PanelLayout();
        layout.Show("inspector", PanelDock.Right);
        layout.Hide("inspector");
        Assert.False(layout.IsDockShown(PanelDock.Right));
        Assert.False(layout.ActiveTab.ContainsKey(PanelDock.Right));
        Assert.Equal(PanelLayout.MinSize, PanelLayout.Clamp(10, 1000));
        Assert.Equal(400, PanelLayout.Clamp(500, 400));
        Assert.Equal(PanelLayout.DefaultBottomHeight, PanelLayout.FromJson(null).BottomHeight);
        Assert.Equal(PanelLayout.MinSize, PanelLayout.FromJson(new System.Text.Json.Nodes.JsonObject { ["leftWidth"] = 5 }).LeftWidth);
    }
}
