using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>表示プリセット (VIEW-42 の仕様 6・7): 拡張子による自動適用、エクスポートとインポート。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewPresetTests
{
    private static async Task ExecuteAsync(AppSession app, string id, string? argument = null)
    {
        await app.SendAsync("execute", new JsonObject { ["id"] = id, ["argument"] = argument, ["fromPalette"] = true });
        await app.IdleAsync();
    }

    private static async Task<JsonObject> ViewAsync(AppSession app) => (await app.SendAsync("viewSettings"))["view"]!.AsObject();

    /// <summary>「現在の設定をプリセットとして保存…」のダイアログで名前と拡張子を入れて保存する。</summary>
    private static async Task SavePresetAsync(AppSession app, string name, string extensions)
    {
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "view.presetSave" });
        await app.WaitForDialogAsync("PresetSaveDialog");
        await app.UiaSetValueAsync("Preset_Name", name);
        await app.UiaSetValueAsync("Preset_Extensions", extensions);
        await app.InvokeDialogButtonAsync("Save");
        await app.WaitUntilAsync(async () => (await app.SendAsync("viewPresets"))["presets"]!.AsArray().Any(p => p!.GetValue<string>() == name),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the preset to be saved");
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-42-03")]
    public Task A_preset_is_applied_to_matching_files_the_first_time_only() => UiTestContext.RunAsync(async ctx =>
    {
        string nes = ctx.CopyTestData("TD-VIEW-NES", "game.nes");
        string bin = ctx.CopyTestData("TD-VIEW-NES", "game.bin");
        AppSession app = await ctx.StartAsync();

        // 1. 新しいドキュメントで 1 行 8 バイト・小文字にし、名前 NES・条件 .nes で保存する。
        await ExecuteAsync(app, "file.new");
        await ExecuteAsync(app, "view.bytesPerRow8");
        await ExecuteAsync(app, "view.lowercase");
        await SavePresetAsync(app, "NES", ".nes");

        // 2. game.nes は 1 行 8 バイト・小文字。
        await app.OpenAsync(nes);
        await app.WaitForTabsAsync(2);
        JsonObject view = await ViewAsync(app);
        Assert.Equal(8, view["bytesPerRow"]!.GetValue<int>());
        Assert.True(view["lowercaseHex"]!.GetValue<bool>());

        // 3. game.bin には適用しない。
        await app.OpenAsync(bin);
        await app.WaitForTabsAsync(3);
        view = await ViewAsync(app);
        Assert.Equal(16, view["bytesPerRow"]!.GetValue<int>());
        Assert.False(view["lowercaseHex"]!.GetValue<bool>());

        // 4. game.nes で 1 行 16 バイトにして閉じ、開き直すと 16 (初めて開いたときだけ適用する)。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await ExecuteAsync(app, "view.bytesPerRow16");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(2);
        await app.OpenAsync(nes);
        await app.WaitForTabsAsync(3);
        Assert.Equal(16, (await ViewAsync(app))["bytesPerRow"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-42-04")]
    public Task Presets_are_exported_and_imported() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        string exported = Path.Combine(ctx.Root, "presets.json");

        // 前提: 設定フォルダ A で P1 (1 行 24 バイト、グループ化 8、種類別) を保存する。1. エクスポートして終了する。
        AppSession a = await ctx.StartAsync(new AppOptions { Files = [seq] });
        await ExecuteAsync(a, "view.bytesPerRowCustom", "24");
        await ExecuteAsync(a, "view.group8");
        await ExecuteAsync(a, "view.byteThemeCategory");
        await SavePresetAsync(a, "P1", string.Empty);
        await a.SendAsync("viewPresets", new JsonObject { ["export"] = exported });
        Assert.True(File.Exists(exported));
        await a.SendAsync("exit");
        await a.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 2〜3. 新しい設定フォルダ B でインポートし、TD-SEQ-1M に P1 を適用する。
        AppSession b = await ctx.StartAsync(new AppOptions { Files = [seq], Profile = ctx.NewProfile() });
        Assert.Contains("P1", (await b.SendAsync("viewPresets", new JsonObject { ["import"] = exported }))["presets"]!.AsArray().Select(p => p!.GetValue<string>()));
        await ExecuteAsync(b, "view.byteThemeNone");
        await ExecuteAsync(b, "view.presetApply", "P1");
        JsonObject view = await ViewAsync(b);
        Assert.Equal(24, view["bytesPerRow"]!.GetValue<int>());
        Assert.Equal(8, view["groupSize"]!.GetValue<int>());
        Assert.True((await b.SendAsync("menuItem", new JsonObject { ["id"] = "Command_ViewByteThemeCategory" }))["checked"]!.GetValue<bool>());

        // 4. 知らない項目を含む JSON: エラーにならず Future が加わる。
        JsonObject state = await b.SendAsync("viewPresets", new JsonObject { ["import"] = ctx.TestData("TD-VIEW-PRESET-FUTURE") });
        Assert.Null(state["error"]);
        string[] afterFuture = [.. state["presets"]!.AsArray().Select(p => p!.GetValue<string>())];
        Assert.Contains("Future", afterFuture);

        // 5. 不正な JSON: 理由を示し、一覧は変わらない。
        string broken = Path.Combine(ctx.Root, "broken.json");
        File.WriteAllText(broken, "{ \"presets\": [ { \"name\": \"Bad\" ");
        state = await b.SendAsync("viewPresets", new JsonObject { ["import"] = broken });
        Assert.NotNull(state["error"]);
        Assert.Equal(afterFuture, state["presets"]!.AsArray().Select(p => p!.GetValue<string>()));
    });
}
