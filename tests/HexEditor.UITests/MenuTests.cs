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
    [Trait(UiTest.TC, "TC-UI-03-01")]
    public Task Changed_shortcut_appears_in_the_menu_without_restart() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1. 設定画面の「キーボード」で「ファイル: 開く」に Ctrl+Shift+F12 を追加する (設定画面を開いたまま)。
        await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "keyboard" });
        JsonObject added = await app.SendAsync("assignKey", new JsonObject { ["command"] = "file.open", ["key"] = "Ctrl+Shift+F12" });
        Assert.True(added["added"]!.GetValue<bool>());

        // 2〜3. メニューの「開く」のショートカットの表示。
        await app.WaitUntilAsync(async () => (await MenuItemAsync(app, "Command_Open"))["shortcut"]?.GetValue<string>()?.Contains("F12", StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(5), "the new shortcut in the menu");
        string shortcut = (await MenuItemAsync(app, "Command_Open"))["shortcut"]!.GetValue<string>();
        Assert.Contains("Ctrl+O", shortcut, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+F12", shortcut, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-03-03")]
    public Task Every_menu_item_is_found_in_the_command_palette() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1. すべてのメニュー・サブメニューの項目の表示名 (コマンドを実行する項目。最近使ったファイルなどの一覧の項目は除く)。
        var items = (await app.SendAsync("menuTexts"))["items"]!.AsArray().Select(i => i!.AsObject())
            .Where(i => i["command"]?.GetValue<string>() is { Length: > 0 }).ToList();
        Assert.True(items.Count > 40, $"only {items.Count} menu items");

        // 2〜3. 項目ごとに、> のモードで表示名を入れ、候補に同じコマンドがあるか。
        var missing = new List<string>();
        foreach (JsonObject item in items)
        {
            string name = MenuName(item["text"]!.GetValue<string>());
            JsonObject palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">" + name });
            string command = item["command"]!.GetValue<string>();
            if (!palette["entries"]!.AsArray().Any(e => e!["key"]?.GetValue<string>() is { } key && (key == "command:" + command || key == "argument:" + command)))
            {
                missing.Add($"{item["path"]} ({command})");
            }
        }

        await app.SendAsync("paletteClose");
        Assert.True(missing.Count == 0, "not found in the command palette:\n" + string.Join("\n", missing));
    });

    /// <summary>メニューの表示名から、日本語のアクセスキーの「(X)」と末尾の「…」を除く。</summary>
    private static string MenuName(string text)
    {
        // 末尾の括弧 (日本語のアクセスキー「(F)」、文字コードの「(932)」などの補足) は名前に含めない。
        string name = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\([^()]*\)$", string.Empty);
        return name.TrimEnd('…', '.').Trim();
    }

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
