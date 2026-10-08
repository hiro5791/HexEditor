using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>UI-01 の「エラー」: 保存した位置が画面外のときの位置 (UI テストの TC-UI-01-03 の計算部分)。</summary>
public sealed class WindowPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);

    [Fact]
    public void Offscreen_position_is_centered_on_the_primary_monitor()
    {
        // 外したモニター (x = 3840) にあった位置。
        PixelRect r = WindowPlacement.Restore(new PixelRect(3900, 100, 1280, 800), [Primary], Primary, 1.0);
        Assert.Equal(new PixelRect(320, 120, 1280, 800), r);
        Assert.True(r.IsInside(Primary));
    }

    [Fact]
    public void Visible_position_is_kept_including_the_invisible_frame()
    {
        var saved = new PixelRect(-7, 0, 1000, 700);
        Assert.Equal(saved, WindowPlacement.Restore(saved, [Primary], Primary, 1.0));
    }

    [Fact]
    public void Partly_visible_position_is_kept()
    {
        // 右下が画面の端からはみ出しているだけ (タイトルバーはつかめる)。
        var saved = new PixelRect(1500, 600, 1280, 800);
        Assert.Equal(saved, WindowPlacement.Restore(saved, [Primary], Primary, 1.0));

        // タイトルバーが作業領域の下にある・横に 100 px 未満しか重ならないときは中央に出す。
        Assert.NotEqual(new PixelRect(100, 1030, 1280, 800).X, WindowPlacement.Restore(new PixelRect(100, 1030, 1280, 800), [Primary], Primary, 1.0).X);
        Assert.Equal(320, WindowPlacement.Restore(new PixelRect(1850, 100, 1280, 800), [Primary], Primary, 1.0).X);
    }

    [Fact]
    public void Small_work_area_uses_ninety_percent()
    {
        var small = new PixelRect(0, 0, 1366, 728);
        Assert.Equal(new PixelRect(68, 36, 1229, 655), WindowPlacement.Centered(small, 1.0));
        Assert.Equal(new PixelRect(0, 0, 2560, 1600), WindowPlacement.Centered(new PixelRect(0, 0, 2560, 1600), 2.0));
    }
}
