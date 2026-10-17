using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 利用者マニュアルを書くときに見つかった、仕様書とアプリの食い違いの修正: 編集 > 削除・選択の解除 (EDIT-13、EDIT-03)、
/// 表示 > パネルの表示切り替え > メモリマップ (ENG-33)、設定「信頼と許可」(AUTO-34 の仕様 5・9)、「ファイルのロック」(ENG-15 の仕様 3)、
/// アクセントカラーの設定 (UI-27)、使用中で保存できないときの「再試行」(ENG-20 の「エラー」)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ManualAlignmentTests
{
    private static async Task<JsonObject> MenuItemAsync(AppSession app, string id) =>
        (await app.SendAsync("menuTexts"))["items"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["id"]?.GetValue<string>() == id);

    private static async Task RunAsync(AppSession app, string path) =>
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = path });

    private static async Task WaitForNoRunAsync(AppSession app) =>
        await app.WaitUntilAsync(
            async () => (await app.SendAsync("script", new JsonObject { ["action"] = "running" }))["count"]!.GetValue<int>() == 0,
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the end of the script");

    [Fact]
    public Task Edit_menu_deletes_and_clears_the_selection() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.WriteFile("data.bin", [.. Enumerable.Range(0, 64).Select(i => (byte)i)]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });

        // メニューには Hex ビューが処理する固定のキーを表示する。
        Assert.Equal("edit.delete", (await MenuItemAsync(app, "Command_Delete"))["command"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty((await MenuItemAsync(app, "Command_Delete"))["shortcut"]?.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty((await MenuItemAsync(app, "Command_ClearSelection"))["shortcut"]?.GetValue<string>()));

        // 編集 > 削除: 選択範囲を削除する (Delete キーと同じ)。
        await app.SelectAsync(0x10, 4);
        await app.CommandAsync("Command_Delete");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["length"]!.GetValue<long>() == 60,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the delete");
        Assert.Equal(new byte[] { 0x0F, 0x14 }, await app.BytesAsync(0x0F, 2));

        // 編集 > 選択の解除。
        await app.SelectAsync(0x20, 8);
        Assert.Equal(8, (await app.DocumentAsync())["selectionLength"]!.GetValue<long>());
        await app.CommandAsync("Command_ClearSelection");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 0,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the selection to be cleared");
        Assert.Equal(60, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    public Task Memory_map_is_in_the_panels_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        JsonObject item = await MenuItemAsync(app, "Command_ToggleMemoryMap");
        Assert.Equal("view.panel.memoryMap", item["command"]!.GetValue<string>());
        Assert.Contains("Memory Map", item["text"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-34-10")]
    public Task Revoking_an_always_allowed_script_asks_again() => UiTestContext.RunAsync(async ctx =>
    {
        string script = ctx.WriteFile("hello.js", System.Text.Encoding.UTF8.GetBytes("console.log('hello from js');"));
        AppSession app = await ctx.StartAsync();
        await RunAsync(app, script);
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Always allow");
        await WaitForNoRunAsync(app);

        // 1. 「自動化 > 信頼と許可」の一覧にこのスクリプトがある。
        await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "automation" });
        Assert.Equal("hello.js", await app.UiaNameAsync("ScriptTrust_Name_0"));

        // 2. 取り消すと一覧から消える。
        await app.UiaInvokeAsync("ScriptTrust_Revoke_0");
        await app.WaitForAsync("ScriptTrust_None");
        Assert.Null(app.Find("ScriptTrust_Name_0"));

        // 3. 次の実行で再び確認が出る。
        await app.SendAsync("showDocument");
        await RunAsync(app, script);
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Don't run");
        await WaitForNoRunAsync(app);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-34-09")]
    public Task Organization_policy_is_shown_in_trust_and_permissions() => UiTestContext.RunAsync(async ctx =>
    {
        // 4. 組織の設定は設定画面の一覧に出さず、「信頼と許可」に内容を示す (ここでは変えられない)。
        string profile = CommandTests.Profile(ctx, """{"$schemaVersion": 1, "scripts.policy": "disabled"}""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject page = await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "automation" });
        Assert.DoesNotContain(page["cards"]!.AsArray(), c => c!["key"]!.GetValue<string>() == "scripts.policy");
        Assert.Contains("Disable all scripts and plugins", await app.UiaNameAsync("ScriptTrust_Policy"), StringComparison.Ordinal);
    });

    [Fact]
    public Task CSharp_component_section_shows_the_state() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "automation" });
        string status = await app.UiaNameAsync("CSharpComponent_Status");
        Assert.True(status.StartsWith("Installed version: ", StringComparison.Ordinal) || status.StartsWith("Not installed", StringComparison.Ordinal)
            || status.StartsWith("A different version", StringComparison.Ordinal), status);
        Assert.NotNull(app.Find("CSharpComponent_ClearCache"));
    });

    [Fact]
    public Task File_lock_setting_none_lets_other_apps_write_while_modified() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.WriteFile("lock.bin", new byte[256]);
        AppSession app = await ctx.StartAsync();
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "files.lock", ["value"] = "none" }))["valid"]!.GetValue<bool>());

        // 設定の後に開いたファイルは、編集しても他のアプリの書き込みを禁止しない。
        await app.OpenAsync(file);
        await app.GoToAsync(0);
        await app.TypeAsync("41");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the edit");
        using (var other = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            other.Position = 0x80;
            other.WriteByte(0x7F);
        }

        Assert.Equal(0x7F, File.ReadAllBytes(file)[0x80]);
    });

    [Fact]
    public Task Accent_color_is_validated_and_needs_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        Assert.False((await app.SendAsync("settingSet", new JsonObject { ["key"] = "ui.accentColor", ["value"] = "red" }))["valid"]!.GetValue<bool>());
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "ui.accentColor", ["value"] = "#E81123" }))["valid"]!.GetValue<bool>());
        JsonObject page = await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = "appearance" });
        JsonObject card = page["cards"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["key"]!.GetValue<string>() == "ui.accentColor");
        Assert.True(card["modified"]!.GetValue<bool>());
        Assert.True(card["restartNote"]!.GetValue<bool>());
        Assert.Equal("#E81123", (await app.WaitForAsync("SettingControl_ui.accentColor")).Properties.HelpText.ValueOrDefault);
    });

    [Fact]
    public Task Saving_a_file_in_use_offers_retry() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.WriteFile("inuse.bin", new byte[256]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await app.GoToAsync(0);
        await app.TypeAsync("41");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the edit");

        // 他のアプリ (このテストのプロセス) が書き込み・削除を許さずに開いている間は保存できず、「再試行」が出る。
        JsonObject notice;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await EditingTests.SaveAsync(app);
            notice = await app.WaitForNotificationAsync(m => m.Contains("inuse.bin", StringComparison.Ordinal) && m.Contains("can't be saved", StringComparison.Ordinal),
                "the in-use notice");
            Assert.Contains("Retry", notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.True((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        }

        // 閉じた後の「再試行」で保存できる。
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Retry" });
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the retried save");
        Assert.Equal(0x41, File.ReadAllBytes(file)[0]);
    });
}
