using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>テストの仕組みそのものの確認 (起動・命令の通り道・UI オートメーション・画面の取得・前面に出ないこと)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class InfrastructureTests
{
    [Fact]
    public Task Launches_without_taking_the_foreground() => UiTestContext.RunAsync(async ctx =>
    {
        int before = NativeMethods.ForegroundProcessId();
        AppSession app = await ctx.StartAsync();

        // 命令の通り道とUI オートメーションの両方で、同じ画面を見ている。
        var state = await app.StateAsync();
        Assert.Equal(app.Pid, state["pid"]!.GetValue<int>());
        Assert.StartsWith("Offset:", (await app.UiaNameAsync("Status_Offset")));
        Assert.Equal("Untitled 1", (await app.TabNamesAsync()).Single());

        // キー入力の注入と描画内容の読み出し。
        await app.TypeAsync("41");
        var render = await app.RenderAsync();
        Assert.Equal("41", render["rows"]![0]!["cells"]![0]!["hex"]!.GetValue<string>());

        // 画面の取得 (真っ黒・真っ白でない)。
        WindowImage image = app.Screenshot();
        Assert.True(image.Width > 100 && image.Height > 100);
        Assert.True(image.Bgra.Distinct().Count() > 4);

        Assert.False(app.StoleForeground);
        Assert.NotEqual(app.Pid, NativeMethods.ForegroundProcessId());
        _ = before;
    });
}
