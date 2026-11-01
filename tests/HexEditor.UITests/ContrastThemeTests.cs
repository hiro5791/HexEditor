using HexEditor.ManualTests;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>Windows のコントラストテーマを実際に切り替えて、4 種類すべてで主な画面の色を画像で確かめる (UI-02、UI-26)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ContrastThemeTests
{
    [Theory]
    [InlineData("hc1")]
    [InlineData("hc2")]
    [InlineData("hcwhite")]
    [InlineData("hcblack")]
    [Trait(UiTest.TC, "TC-UI-02-06")]
    [Trait(UiTest.TC, "TC-UI-26-03")]
    public Task Main_screens_use_the_theme_colors_and_keep_the_caption_buttons_readable(string theme) => UiTestContext.RunAsync(async ctx =>
    {
        using SystemContrastTheme contrast = await SystemContrastTheme.ApplyAsync(theme);
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.WaitForAsync("Start_New");

        async Task CheckAsync(string screen)
        {
            await app.IdleAsync();
            await Task.Delay(500);
            IReadOnlyList<string> problems = ScreenCheck.Run(app.Hwnd, highContrast: true);
            Assert.True(problems.Count == 0, $"{theme}, {screen}: {string.Join(" / ", problems)}");
        }

        await CheckAsync("start page");

        await app.OpenAsync(ctx.TestData("TD-SEQ-1M"));
        await app.WaitForAsync("HexView");
        await CheckAsync("hex view");

        await app.KeyAsync("P", ctrl: true, shift: true);
        await CheckAsync("command palette");
        await app.KeyAsync("Escape");

        await app.KeyAsync("F", ctrl: true);
        await app.WaitForAsync("Find_Close");
        await CheckAsync("find bar");
        await app.UiaInvokeAsync("Find_Close");

        await app.CommandAsync("Command_ToggleInspector");
        await app.WaitUntilAsync(() => app.IsShownAsync("InspectorPanel"), TimeSpan.FromSeconds(10), "the inspector panel");
        await CheckAsync("inspector");

        await app.CommandAsync("Command_About");
        await app.WaitForAsync("About_Version");
        await CheckAsync("about");
        await app.InvokeDialogButtonAsync("Close");
    });
}
