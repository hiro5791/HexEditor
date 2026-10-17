using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// セーフモード (UI-30 の仕様 4)、オフラインモードでの C# スクリプト コンポーネントの追加 (UI-58 の受け入れ基準 3)、.hexplugin のドロップ (UI-34 の
/// 仕様 5)。Shift キーは押さず、テスト用のフック <c>safeModeShift</c> で「押されている」とみなす (キーボードの入力は使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SafeModeAndComponentTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-UI-30-03")]
    public Task Shift_at_start_up_starts_in_safe_mode() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-UI-HEXPLUGIN のプラグインを入れて有効にした設定フォルダ。
        string profile = ctx.NewProfile();
        TestPlugins.Trust(profile, TestPlugins.Install(Path.Combine(profile, "plugins"), TestPlugins.HexPlugin), TestPlugins.HexPlugin.Id);

        // 1・2: Shift を押した状態で起動する (テスト用のフック)。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = new JsonObject { ["safeModeShift"] = true } });

        // 3: タイトルに「(セーフモード)」、アプリ全体の InfoBar に文言と「通常どおり再起動」。
        Assert.Contains("(Safe Mode)", (await app.StateAsync())["title"]!.GetValue<string>());
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("safe mode", StringComparison.OrdinalIgnoreCase), "the safe mode notice");
        Assert.Contains("Plug-ins and automatically run scripts are not loaded", notice["message"]!.GetValue<string>());
        Assert.Contains("Restart normally", notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));

        // プラグインのコマンド plugin.test.hello はない (プラグインを読み込んでいない)。
        await app.SendAsync("plugins", new JsonObject { ["action"] = "waitLoaded" }, TimeSpan.FromSeconds(120));
        Assert.False(await HasHelloAsync(app));
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // フックがなければセーフモードにならず (実際のキーボードの状態に左右されない)、同じ設定フォルダでプラグインのコマンドがある。
        AppSession normal = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = new JsonObject { ["safeModeShift"] = false } });
        Assert.DoesNotContain("(Safe Mode)", (await normal.StateAsync())["title"]!.GetValue<string>());
        await normal.SendAsync("plugins", new JsonObject { ["action"] = "waitLoaded" }, TimeSpan.FromSeconds(120));
        await normal.WaitUntilAsync(() => HasHelloAsync(normal), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the plugin command plugin.test.hello");
    });

    /// <summary>コマンドの一覧に TD-UI-HEXPLUGIN のコマンド plugin.test.hello があるか。</summary>
    private static async Task<bool> HasHelloAsync(AppSession app) =>
        (await app.SendAsync("commands"))["items"]!.AsArray().Any(i => i!["id"]?.GetValue<string>() == "plugin.test.hello");

    [Fact]
    [Trait(UiTest.TC, "TC-UI-58-03")]
    public Task Offline_mode_shows_the_manual_steps_for_the_csharp_component() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = await PackagingTests.ProfileWithSettings(ctx, "{\"$schemaVersion\": 1, \"network.offline\": true}"),
        });
        JsonObject before = await app.SendAsync("csharpComponent");
        Assert.False(before["installed"]!.GetValue<bool>(), "the component is already installed in the test profile");

        // 1: C# スクリプト コンポーネントの追加 (ツール > スクリプト)。
        await app.CommandAsync("Command_ScriptAddCSharp");

        // 2: ダウンロードの確認ではなく、手動で入れる手順 (HexEditor-<版>-<アーキテクチャ>-csharp.zip) が出る。
        FlaUI.Core.AutomationElements.AutomationElement dialog = await app.WaitForDialogAsync("CSharpComponentOfflineDialog");
        string package = before["package"]!.GetValue<string>();
        Assert.Matches(@"^HexEditor-.+-(x64|arm64|x86)-csharp\.zip$", package);
        await app.WaitForDialogTextAsync(dialog, package, "Offline mode is on");
        Assert.Null(app.Button("Download"));
        await app.InvokeDialogButtonAsync("Close");

        // ダウンロードは行われない (アプリの通信の記録に外部への接続がない)。
        JsonArray requests = (await app.SendAsync("networkLog"))["requests"]!.AsArray();
        Assert.Empty(requests);
        Assert.False((await app.SendAsync("csharpComponent"))["installed"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-05")]
    public Task Dropping_a_hexplugin_asks_to_add_the_plugin() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-HEXPLUGIN の test.hexplugin を作る (テスト用のプラグインを .hexplugin にまとめる)。
        string staging = Path.Combine(ctx.Root, "hexplugin-src");
        TestPlugins.WriteFolder(TestPlugins.HexPlugin, staging);
        string package = TestPlugins.Package(staging, Path.Combine(ctx.Root, "test.hexplugin"));
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("plugins", new JsonObject { ["action"] = "waitLoaded" }, TimeSpan.FromSeconds(120));
        int tabs = (await app.StateAsync())["documents"]!.AsArray().Count;

        // 1: Hex ビューにドロップする (ドロップの処理をテスト用の命令で呼ぶ。OLE のドロップは UI オートメーションで送れない)。
        await app.SendAsync("drop", new JsonObject { ["paths"] = new JsonArray(package) });

        // 2: プラグインの追加の確認ダイアログ (08 の AUTO-32) が出て、タブは増えていない。
        await app.WaitForDialogAsync("PluginTrustDialog");
        Assert.Equal(tabs, (await app.StateAsync())["documents"]!.AsArray().Count);
        await app.InvokeDialogButtonAsync("Don't enable");
    });
}
