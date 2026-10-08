using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 設定画面「外観」の Hex 表示のフォントと配色の区画 (UI-28 の仕様 3〜5 と「エラー」、UI-29 の仕様 8 と「画面」)。配色の計算そのもの
/// (コントラスト比、JSON) は Core の ColorSchemeTests、Hex 表示への反映は ViewMiscTests (TC-UI-28-01〜04)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class AppearanceSettingsTests
{
    private static string LightProfile(UiTestContext ctx, string extra = "")
    {
        string profile = ctx.NewProfile();
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"ui.theme\": \"light\"" + extra + "}");
        return profile;
    }

    private static async Task<JsonObject> OpenAppearanceAsync(AppSession app)
    {
        await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "appearance" });
        JsonObject? state = null;
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                state = await app.SendAsync("schemeEditor");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10), "the color scheme section");
        return state!;
    }

    private static Task<JsonObject> EditorAsync(AppSession app, JsonObject args) => app.SendAsync("schemeEditor", args);

    [Fact]
    public Task Duplicated_scheme_is_edited_with_a_live_preview_and_contrast_warnings() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = LightProfile(ctx), Files = [ctx.TestData("TD-BYTES-256")] });
        await app.SendAsync("colorScheme", new JsonObject { ["action"] = "select", ["name"] = "solarized" });

        // 一覧に同梱の 3 つの配色があり、選んでいるのはソラライズド。同梱の配色は編集できない (複製して編集する。仕様 3)。
        JsonObject state = await OpenAppearanceAsync(app);
        Assert.Equal(["default", "solarized", "contrast"], state["schemes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal("solarized", state["selected"]!.GetValue<string>());
        Assert.False(state["editorVisible"]!.GetValue<bool>());
        Assert.Equal("#FFFDF6E3", state["previewBackground"]!.GetValue<string>());

        // 複製すると、その配色が選ばれて編集の欄が出る。同じ名前ではもう複製できない。
        state = await EditorAsync(app, new JsonObject { ["action"] = "duplicate", ["name"] = "mine" });
        Assert.Equal("mine", state["created"]!.GetValue<string>());
        Assert.Equal("mine", state["selected"]!.GetValue<string>());
        Assert.Equal("mine", state["editing"]!.GetValue<string>());
        Assert.True(state["editorVisible"]!.GetValue<bool>());
        Assert.Null((await EditorAsync(app, new JsonObject { ["action"] = "duplicate", ["name"] = "MINE" }))["created"]);

        // 色を変えるとプレビューに即座に映る。文字と背景のコントラスト比が 4.5:1 未満なら警告が出る (仕様 4)。
        state = await EditorAsync(app, new JsonObject { ["action"] = "set", ["element"] = "Background", ["color"] = "#FFFFFF" });
        Assert.Equal("#FFFFFFFF", state["previewBackground"]!.GetValue<string>());
        state = await EditorAsync(app, new JsonObject { ["action"] = "set", ["element"] = "HexText", ["color"] = "#949494" });
        Assert.True(state["contrastOpen"]!.GetValue<bool>());
        Assert.Contains("Hex text on Background (Light theme): 3.0:1", state["contrastMessage"]!.GetValue<string>());

        // 保存はできる。保存すると開いている Hex 表示に反映される。
        await EditorAsync(app, new JsonObject { ["action"] = "save" });
        string saved = await File.ReadAllTextAsync(Path.Combine(app.Profile, "themes", "mine.json"));
        Assert.Contains("#949494", saved);
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["background"]?.GetValue<string>() == "#FFFFFFFF",
            TimeSpan.FromSeconds(10), "the saved scheme in the hex view");
    });

    [Fact]
    public Task Invalid_colors_in_an_imported_scheme_are_reported() => UiTestContext.RunAsync(async ctx =>
    {
        // 「エラー」: 不正な要素だけ既定の色を使い、設定画面に警告を出す。
        string file = Path.Combine(ctx.Root, "broken-colors.json");
        await File.WriteAllTextAsync(file, """{"name": "broken-colors", "light": {"background": "#102030", "hexText": "red", "unknownThing": "#FFFFFF"}}""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = LightProfile(ctx), Files = [ctx.TestData("TD-BYTES-256")] });
        await OpenAppearanceAsync(app);
        JsonObject state = await EditorAsync(app, new JsonObject { ["action"] = "import", ["path"] = file });
        Assert.Equal("broken-colors", state["imported"]!.GetValue<string>());
        Assert.Equal("broken-colors", state["selected"]!.GetValue<string>());
        Assert.True(state["loadWarningsOpen"]!.GetValue<bool>());
        Assert.Contains("light.hexText", state["loadWarnings"]!.GetValue<string>());
        Assert.Contains("light.unknownThing", state["loadWarnings"]!.GetValue<string>());
        Assert.Equal("#FF102030", state["previewBackground"]!.GetValue<string>());

        // 配色ファイルを手で壊した場合も、選んでいる配色の警告として出る。
        string themes = Path.Combine(app.Profile, "themes");
        await File.WriteAllTextAsync(Path.Combine(themes, "hand-edited.json"), """{"name": "hand-edited", "light": {"background": "#12345"}}""");
        await app.SendAsync("colorScheme", new JsonObject { ["action"] = "select", ["name"] = "hand-edited" });
        await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "keyboard" });
        await OpenAppearanceAsync(app);
        await app.WaitUntilAsync(async () => (await EditorAsync(app, new JsonObject()))["selected"]?.GetValue<string>() == "hand-edited",
            TimeSpan.FromSeconds(10), "the section to be rebuilt");
        state = await EditorAsync(app, new JsonObject());
        Assert.True(state["loadWarningsOpen"]!.GetValue<bool>());
        Assert.Contains("light.background", state["loadWarnings"]!.GetValue<string>());
    });

    [Fact]
    public Task Font_preview_follows_the_font_settings() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-29 の仕様 8: 変更はプレビュー付きで即座に反映する。大きさは 0.5 pt 刻み (仕様 3)。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = LightProfile(ctx), Files = [ctx.TestData("TD-BYTES-256")] });
        JsonObject state = await OpenAppearanceAsync(app);
        Assert.Equal(10, state["previewPoints"]!.GetValue<double>());
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "view.font.size", ["value"] = 12.5 }))["valid"]!.GetValue<bool>());
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "view.font.lineHeight", ["value"] = 1.5 }))["valid"]!.GetValue<bool>());
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "view.font.family", ["value"] = "Consolas" }))["valid"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await EditorAsync(app, new JsonObject()))["previewPoints"]!.GetValue<double>() == 12.5,
            TimeSpan.FromSeconds(5), "the preview to follow the font size");
        state = await EditorAsync(app, new JsonObject());
        Assert.Equal(1.5, state["previewLineHeight"]!.GetValue<double>());
        Assert.StartsWith("Consolas", state["previewFont"]!.GetValue<string>());
    });

    [Fact]
    public Task Proportional_font_draws_each_character_centered_in_its_cell() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-29 の仕様 2: 等幅でないフォントは、文字ごとに同じ幅のセルに中央揃えで描く。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = LightProfile(ctx), Files = [ctx.TestData("TD-BYTES-256")] });
        JsonObject render = await app.RenderAsync();
        Assert.False(render["proportional"]!.GetValue<bool>());
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "view.font.family", ["value"] = "Segoe UI" }))["valid"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["proportional"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the proportional font");
        render = await app.RenderAsync();
        double cell = render["cellWidth"]!.GetValue<double>();

        // 幅の違う文字 (W と i) が、どちらもセルの左端からセル幅の範囲に描かれる。行の文字列 (UI オートメーション) は変わらない。
        foreach (long offset in new[] { 0x57L, 0x69L })
        {
            JsonObject c = ViewOps.CellOf(render, offset)!;
            Assert.Equal(((char)offset).ToString(), c["glyph"]!.GetValue<string>());
            Assert.Equal(c["textLeft"]!.GetValue<double>(), c["glyphLeft"]!.GetValue<double>(), 2);
            Assert.Equal(cell, c["glyphWidth"]!.GetValue<double>(), 2);
        }

        Assert.Contains("57", ViewOps.Row(render, 0x50)!["line"]!.GetValue<string>(), StringComparison.Ordinal);
    });
}
