using System.Text.Json.Nodes;
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
        // 1・2: Shift を押した状態で起動する (テスト用のフック)。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["safeModeShift"] = true } });

        // 3: タイトルに「(セーフモード)」、アプリ全体の InfoBar に文言と「通常どおり再起動」。
        Assert.Contains("(Safe Mode)", (await app.StateAsync())["title"]!.GetValue<string>());
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("safe mode", StringComparison.OrdinalIgnoreCase), "the safe mode notice");
        Assert.Contains("Plug-ins and automatically run scripts are not loaded", notice["message"]!.GetValue<string>());
        Assert.Contains("Restart normally", notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));

        // プラグインのコマンド plugin.test.hello はない (コマンドパレットに出ない)。プラグインの仕組み (AUTO-29〜AUTO-31) ができたら
        // TD-UI-HEXPLUGIN を入れた設定フォルダで確かめる (今は通常の起動でも出ない)。
        JsonObject palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">plugin.test.hello" });
        Assert.DoesNotContain(palette["entries"]!.AsArray(), e => e!["key"]?.GetValue<string>() == "plugin.test.hello");
        await app.KeyAsync("Escape");

        // フックがなければセーフモードにならない (実際のキーボードの状態に左右されない)。
        AppSession normal = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["safeModeShift"] = false } });
        Assert.DoesNotContain("(Safe Mode)", (await normal.StateAsync())["title"]!.GetValue<string>());
    });

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

    [Fact(Skip = "プラグインマネージャ (08 の AUTO-32) とプラグインの追加の確認ダイアログができていない (フェーズ 3 のプラグインの担当)。TD-UI-HEXPLUGIN もまだない。")]
    [Trait(UiTest.TC, "TC-UI-34-05")]
    public Task Dropping_a_hexplugin_asks_to_add_the_plugin() => Task.CompletedTask;
}
