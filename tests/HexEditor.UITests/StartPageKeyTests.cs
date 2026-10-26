using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// スタートページ (文書が 1 つもない状態) でのショートカットキー (UI-18) の回帰テスト。キーはテスト用の命令 (<c>key</c>) ではなく、
/// 実際のキー入力と同じ道筋 (<see cref="WindowKeys"/>) で届ける。
/// </summary>
/// <remarks>
/// 不具合: キーの振り分けを Root (ウィンドウの中身) の PreviewKeyDown に付けていたため、WinUI がフォーカスを Root の外のウィンドウの根元の
/// 要素に置いたとき (ウィンドウのアクティブ化・フォーカスを受けない場所のクリックの後) に、Ctrl+Shift+D などがまったく効かなかった
/// (手動テスト TC-ENG-28-01 の手順 1 で見つかった)。
/// </remarks>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class StartPageKeyTests
{
    private const int KeyD = 'D';

    [Fact]
    public Task Ctrl_Shift_D_on_the_start_page_right_after_launch_opens_the_disk_dialog() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = DeviceTests.Hooks(ctx), WaitForEditor = false });
        Assert.True(await app.IsShownAsync("StartPage"), "the start page is not shown");

        WindowKeys.Press(app, KeyD, ctrl: true, shift: true);
        await app.WaitForDialogAsync("OpenDiskDialog");
    });

    [Fact]
    public Task Ctrl_Shift_D_opens_the_disk_dialog_when_focus_is_on_the_window_root_outside_the_content() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = DeviceTests.Hooks(ctx), WaitForEditor = false });
        Assert.True(await app.IsShownAsync("StartPage"), "the start page is not shown");

        // ウィンドウのアクティブ化・空いた場所のクリックの後と同じく、フォーカスを Root の外 (ウィンドウの根元。枠は出ない) に置く。
        JsonObject focus = await app.SendAsync("focus", new JsonObject { ["target"] = "windowRoot" });
        Assert.True(focus["moved"]!.GetValue<bool>(), "focus did not move to the window root");
        Assert.Equal("ScrollViewer:", focus["focused"]?.GetValue<string>());
        Assert.Equal(string.Empty, focus["where"]?.GetValue<string>());

        WindowKeys.Press(app, KeyD, ctrl: true, shift: true);
        await app.WaitForDialogAsync("OpenDiskDialog");
    });
}
