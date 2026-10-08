using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>メニューバー (UI-03) と、メニューの項目名に操作名を入れる「元に戻す」「やり直し」(EDIT-19 の仕様 11)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class MenuTests
{
    private static async Task<JsonObject> MenuItemAsync(AppSession app, string id)
    {
        await app.IdleAsync();
        return (await app.SendAsync("menuTexts"))["items"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["id"]!.GetValue<string>() == id);
    }

    private static async Task<string> MenuTextAsync(AppSession app, string id) => (await MenuItemAsync(app, id))["text"]!.GetValue<string>();

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-19-07")]
    public Task Undo_and_redo_items_show_the_operation_name() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["ui.toolbar.visible"] = true });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.CopyTestData("TD-SEQ-1M")] });

        // 1. 開始 0x10・長さ 0x10 を選び、塗りつぶし (00)。
        await EditCommandTests.SelectRangeAsync(app, "0x10", "0x10");
        await app.CommandAsync("Command_Fill");
        await app.WaitForAsync("FillDialog");
        await app.IdleAsync();
        await EditCommandTests.SelectItemAsync(app, "Fill_Kind", "Hex pattern");
        await app.UiaSetValueAsync("Fill_Pattern", "00");
        await EditCommandTests.PressAsync(app);
        Assert.Equal(new byte[16], await app.BytesAsync(0x10, 16));

        // 2. 「元に戻す」の項目名とツールバーのボタンのツールチップ。
        Assert.Equal("Undo: Fill", await MenuTextAsync(app, "Command_Undo"));
        string? tip = (await app.ElementAsync("ToolbarButton_edit.undo"))["toolTip"]?.GetValue<string>();
        Assert.StartsWith("Undo: Fill", tip ?? string.Empty, StringComparison.Ordinal);

        // 3. Ctrl+Z の後は「やり直し: 塗りつぶし」。
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal("Redo: Fill", await MenuTextAsync(app, "Command_Redo"));
        Assert.Equal("Undo", await MenuTextAsync(app, "Command_Undo"));

        // 4. オフセット 0x40 で 1 2 を入力すると「元に戻す: 入力」。
        await app.GoToAsync(0x40);
        await app.TypeAsync("12");
        await app.IdleAsync();
        Assert.Equal("Undo: Typing", await MenuTextAsync(app, "Command_Undo"));
    });
}
