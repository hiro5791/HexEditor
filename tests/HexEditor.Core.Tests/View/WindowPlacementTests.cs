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
    public void Missing_monitor_centers_on_the_primary_even_if_the_position_overlaps_another()
    {
        // UI-31 の仕様 1: 記録したモニター (DISPLAY2) が今はない。位置は主モニターにかかっているが、主モニターの中央に出す。
        (string?, PixelRect)[] monitors = [(@"\\.\DISPLAY1", Primary)];
        var saved = new PixelRect(1500, 100, 1280, 800);
        Assert.Equal(new PixelRect(320, 120, 1280, 800), WindowPlacement.Restore(saved, monitors, @"\\.\DISPLAY2", Primary, 1.0));

        // モニターがある・記録がない・名前が取れない場合は、位置の規則だけで決める。
        Assert.Equal(saved, WindowPlacement.Restore(saved, monitors, @"\\.\display1", Primary, 1.0));
        Assert.Equal(saved, WindowPlacement.Restore(saved, monitors, null, Primary, 1.0));
        Assert.Equal(saved, WindowPlacement.Restore(saved, [(null, Primary)], @"\\.\DISPLAY2", Primary, 1.0));
    }

    [Fact]
    public void Maximized_or_full_screen_window_records_its_normal_bounds()
    {
        // UI-31 の仕様 1: 最大化・全画面の間は、その前の通常の表示の位置と大きさを書く。
        var maximized = new PixelRect(-8, -8, 1936, 1056);
        var normal = new PixelRect(200, 100, 1000, 700);
        Assert.Equal(normal, WindowPlacement.NormalBounds(maximized, normal, isNormalState: false));
        Assert.Equal(maximized, WindowPlacement.NormalBounds(maximized, null, isNormalState: false));
        Assert.Equal(normal, WindowPlacement.NormalBounds(normal, new PixelRect(0, 0, 10, 10), isNormalState: true));
    }

    [Fact]
    public void Small_work_area_uses_ninety_percent()
    {
        var small = new PixelRect(0, 0, 1366, 728);
        Assert.Equal(new PixelRect(68, 36, 1229, 655), WindowPlacement.Centered(small, 1.0));
        Assert.Equal(new PixelRect(0, 0, 2560, 1600), WindowPlacement.Centered(new PixelRect(0, 0, 2560, 1600), 2.0));
    }
}
