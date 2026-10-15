using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// プラグイン (08 の AUTO-30〜AUTO-35) を、アプリにつないだ状態で確かめる。読み込み・信頼・署名の細部は Core のテスト
/// (HexEditor.Core.Tests/Plugins) が確かめる。ここではプラグインマネージャ・メニュー・コマンド・パネル・インスペクタ・InfoBar のつながりを見る。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PluginUiTests
{
    private static string PluginsFolder(string profile) => Path.Combine(profile, "plugins");

    /// <summary>テスト用のプラグインを設定フォルダに入れ、信頼済みにする。</summary>
    private static string InstallTrusted(string profile, TestPlugins.Definition d)
    {
        string folder = TestPlugins.Install(PluginsFolder(profile), d);
        TestPlugins.Trust(profile, folder, d.Id);
        return folder;
    }

    private static Task<JsonObject> PluginsAsync(AppSession app, string action, JsonObject? args = null)
    {
        args ??= [];
        args["action"] = action;
        return app.SendAsync("plugins", args, TimeSpan.FromSeconds(120));
    }

    private static async Task<JsonObject> PluginAsync(AppSession app, string id) =>
        (await PluginsAsync(app, "list"))["plugins"]!.AsArray().Select(p => p!.AsObject()).Single(p => p["id"]!.GetValue<string>() == id);

    private static async Task<IReadOnlyList<string>> CommandTitlesAsync(AppSession app) =>
        [.. (await app.SendAsync("commands"))["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>())];

    private static async Task<List<string>> OutputAsync(AppSession app) =>
        [.. (await app.SendAsync("scriptUi", new JsonObject { ["action"] = "outputRows" }))["rows"]!.AsArray().Select(r => r!["text"]!.GetValue<string>())];

    private static string SamplePackage(UiTestContext ctx, string? name = null)
    {
        string staging = Path.Combine(ctx.Root, "sample-src-" + Guid.NewGuid().ToString("N")[..6]);
        TestPlugins.WriteFolder(TestPlugins.Sample, staging);
        return TestPlugins.Package(staging, Path.Combine(ctx.Root, name ?? "sample.hexplugin"));
    }

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-32-01")]
    public Task A_dropped_hexplugin_is_confirmed_extracted_and_enabled_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string package = SamplePackage(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        await PluginsAsync(app, "openManager");
        await app.WaitForAsync("PluginManager_List");

        // 1. 一覧へのドロップと同じ処理で追加する (OLE のドロップは UI オートメーションで送れないため、ドロップの処理を呼ぶ)。
        Task<JsonObject> install = PluginsAsync(app, "install", new JsonObject { ["path"] = package });

        // 2. 信頼の確認で「有効にする」。展開は確認の後。
        await app.WaitForDialogAsync("PluginTrustDialog");
        Assert.False(Directory.Exists(Path.Combine(PluginsFolder(profile), "test.sample")));
        await app.InvokeDialogButtonAsync("Enable");
        JsonObject result = await install;
        Assert.False(result["error"]!.GetValue<bool>(), result["message"]?.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(PluginsFolder(profile), "test.sample", "1.0.0", "Plugin.Sample.dll")));

        // 3. 「今すぐ再起動」が出る (再起動はアプリを閉じて同じ設定フォルダで起動し直すのと同じ)。
        Assert.True((await PluginsAsync(app, "manager"))["restartVisible"]!.GetValue<bool>());
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 4. 再起動後: 有効で、候補「sample: hello」がある。
        app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        Assert.Equal("Enabled", (await PluginAsync(app, "test.sample"))["state"]!.GetValue<string>());
        await app.WaitUntilAsync(async () => (await CommandTitlesAsync(app)).Any(t => t.EndsWith("sample: hello", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the command 'sample: hello'");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-32-02")]
    public Task A_disabled_plugin_is_not_loaded_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Sample);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        Assert.Equal("Enabled", (await PluginAsync(app, "test.sample"))["state"]!.GetValue<string>());

        // 1. プラグインマネージャで「無効」に切り替える (UI オートメーションの切り替え)。2. 「今すぐ再起動」が出る。
        await PluginsAsync(app, "select", new JsonObject { ["index"] = 0 });
        (await app.WaitForAsync("PluginManager_Enabled")).Patterns.Toggle.Pattern.Toggle();
        await app.IdleAsync();
        JsonObject manager = await PluginsAsync(app, "manager");
        Assert.False(manager["selectedEnabled"]!.GetValue<bool>());
        Assert.True(manager["restartVisible"]!.GetValue<bool>());
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 3. 再起動後: 無効で、候補がなく、DLL を読み込んでいない。
        app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        JsonObject p = await PluginAsync(app, "test.sample");
        Assert.Equal("Disabled", p["state"]!.GetValue<string>());
        Assert.False(p["assemblyLoaded"]!.GetValue<bool>());
        Assert.DoesNotContain(await CommandTitlesAsync(app), t => t.EndsWith("sample: hello", StringComparison.Ordinal));
        app.Process.Refresh();
        Assert.DoesNotContain(app.Process.Modules.Cast<System.Diagnostics.ProcessModule>(), m => m.ModuleName.Equals("Plugin.Sample.dll", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-32-03")]
    public Task Unloadable_plugins_show_the_reason_and_a_broken_package_is_not_added() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        TestPlugins.Install(PluginsFolder(profile), TestPlugins.NoEntry);
        string broken = TestPlugins.Truncated(SamplePackage(ctx), Path.Combine(ctx.Root, "broken.hexplugin"));
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");

        // 1. 「読み込めません」と、見つからない DLL の名前。
        await PluginsAsync(app, "openManager");
        JsonObject item = (await PluginsAsync(app, "manager"))["items"]!.AsArray().Single()!.AsObject();
        Assert.StartsWith("Cannot be loaded", item["state"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Missing.Entry.dll", item["state"]!.GetValue<string>(), StringComparison.Ordinal);

        // 2〜3. 途中で切れた zip: 追加できない旨と理由。plugins に何も増えない。
        string[] before = [.. Directory.EnumerateFileSystemEntries(PluginsFolder(profile), "*", SearchOption.AllDirectories)];
        JsonObject result = await PluginsAsync(app, "install", new JsonObject { ["path"] = broken });
        Assert.True(result["error"]!.GetValue<bool>());
        Assert.Contains("zip", result["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zip", (await PluginsAsync(app, "manager"))["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        string[] after = [.. Directory.EnumerateFileSystemEntries(PluginsFolder(profile), "*", SearchOption.AllDirectories)
            .Where(e => !e.Contains(".staging", StringComparison.Ordinal))];
        Assert.Equal(before.Order(), after.Order());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-32-04")]
    public Task The_plugin_manager_is_operable_from_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Sample);
        InstallTrusted(profile, TestPlugins.DepA);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        var focus = new List<string>();

        // 1. 「プラグイン: 管理」(コマンドパレットと同じコマンド)。一覧にフォーカスが移る。
        await app.SendAsync("execute", new JsonObject { ["id"] = "plugins.manage", ["fromPalette"] = true });
        await app.WaitForAsync("PluginManager_List");
        await app.WaitUntilAsync(async () => (await app.FocusedAsync() ?? "").Length > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the focus in the manager");
        focus.Add(await app.FocusedAsync() ?? "");

        // キーの入力は、システムのキーボードを使わずに同じ操作で行う: Tab はフォーカスの移動 (Tab と同じ順)、↓ は一覧の項目の選択、
        // Space は切り替え、Enter はボタンの実行 (UI オートメーションのパターン。キーボードの操作と同じ処理を通る)。
        // 2. ↓ で 2 番目の項目 (一覧は ID の順: dep-a, test.sample)。
        var list = await app.WaitForAsync("PluginManager_List");
        FlaUI.Core.AutomationElements.AutomationElement second = list.FindAllChildren()[1];
        second.Patterns.SelectionItem.Pattern.Select();
        await app.IdleAsync();
        focus.Add(await app.FocusedAsync() ?? "");
        Assert.Equal("test.sample", (await PluginsAsync(app, "manager"))["selected"]?.GetValue<string>());

        // 3. Tab で「有効」の切り替えに移り、Space で無効にする。「今すぐ再起動」が出る。
        await app.FocusAsync("PluginManager_List");
        string? moved = (await app.SendAsync("focus", new JsonObject { ["target"] = "next" }))["focused"]?.GetValue<string>();
        focus.Add(moved ?? "");
        Assert.EndsWith(":PluginManager_Enabled", moved, StringComparison.Ordinal);
        (await app.WaitForAsync("PluginManager_Enabled")).Patterns.Toggle.Pattern.Toggle();
        await app.IdleAsync();
        JsonObject manager = await PluginsAsync(app, "manager");
        Assert.False(manager["selectedEnabled"]!.GetValue<bool>());
        Assert.True(manager["restartVisible"]!.GetValue<bool>());

        // 4. Tab で「プラグインのフォルダを開く」に移り、Enter (テスト用のビルドは Explorer を開かず、開こうとしたフォルダを記録する)。
        await app.FocusAsync("PluginManager_Enabled");
        moved = (await app.SendAsync("focus", new JsonObject { ["target"] = "next" }))["focused"]?.GetValue<string>();
        focus.Add(moved ?? "");
        Assert.EndsWith(":PluginManager_OpenFolder", moved, StringComparison.Ordinal);
        await app.UiaInvokeAsync("PluginManager_OpenFolder");
        await app.IdleAsync();
        Assert.Equal(Path.GetFullPath(PluginsFolder(profile)).TrimEnd('\\'), (await PluginsAsync(app, "list"))["lastOpenedFolder"]?.GetValue<string>()?.TrimEnd('\\'));

        // 5. どの手順でもフォーカスのある要素が取れる。
        Assert.All(focus, f => Assert.False(string.IsNullOrEmpty(f)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-31-01")]
    public Task Sample_plugin_extensions_appear_with_the_built_in_features() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Sample);
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [bytes] });
        await PluginsAsync(app, "waitLoaded");

        // 1. データインスペクタの行 (本体の型と同じ一覧)。ツールチップにプラグイン名。
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
        await app.GoToAsync(0x12);
        JsonObject row = null!;
        await app.WaitUntilAsync(async () => (row = (await app.SendAsync("inspector"))["items"]!.AsArray().Select(i => i!.AsObject())
            .FirstOrDefault(i => i["name"]?.GetValue<string>() == "nibble-swap8")!) is not null, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the plugin row");
        Assert.Equal("21", row["value"]!.GetValue<string>());
        Assert.Contains("Sample Plugin", row["toolTip"]!.GetValue<string>(), StringComparison.Ordinal);

        // 2. ハッシュパネルで sum8 (Sample) を計算する: 全体の和の下位 8 bit は 80。
        await app.SendAsync("hash", new JsonObject { ["action"] = "show" });
        await app.SendAsync("hash", new JsonObject { ["action"] = "algorithms", ["ids"] = new JsonArray("test.sample.sum8") });
        await app.SendAsync("hash", new JsonObject { ["action"] = "target", ["kind"] = "WholeDocument" });
        JsonObject hash = await app.SendAsync("hash", new JsonObject { ["action"] = "compute" });
        Assert.Contains("80", hash["rows"]!.AsArray().Single()!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        // 3〜4・6. メニュー: ツール > プラグイン、ファイル > プラグインのデータソースを開く、データ > プラグインのデータ演算。ツールチップにプラグイン名。
        JsonObject menus = await PluginsAsync(app, "menus");
        JsonObject tool = menus["tools"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["text"]!.GetValue<string>() == "sample: hello");
        Assert.Contains("Sample Plugin", tool["toolTip"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("sample-pattern", menus["sources"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("sample-not", menus["data"]!.AsArray().Select(n => n!.GetValue<string>()));

        // 4. データソース: 長さ 256 で開く。
        Task<JsonObject> open = app.SendAsync("execute", new JsonObject { ["id"] = "plugin.test.sample.pattern.open" });
        // 命令の通り道は "execute" の完了 (ダイアログを閉じるまで) を待っているため、ここでは UI オートメーションだけで操作する。
        (await app.WaitForAsync("PluginSource_Argument")).Patterns.Value.Pattern.SetValue("256");
        await app.InvokeDialogButtonAsync("OK", idle: false);
        await open;
        await app.WaitForTabsAsync(2);
        Assert.Equal(256, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // 6. コマンド「sample: hello」: 出力パネルに hello。パネル「Sample Panel」を開く。
        await app.SendAsync("execute", new JsonObject { ["id"] = "plugin.test.sample.hello" });
        await app.WaitUntilAsync(async () => (await OutputAsync(app)).Contains("hello"), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the output 'hello'");
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.pluginTestSamplePanel" });
        await app.WaitForAsync("SamplePanelText");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-30-03")]
    public Task A_plugin_that_keeps_throwing_is_stopped_with_an_infobar_and_the_app_keeps_working() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Throw);
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });
        await PluginsAsync(app, "waitLoaded");
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });

        // 1. コマンド「throw: run」。
        await app.SendAsync("execute", new JsonObject { ["id"] = "plugin.throw.run" });

        // 2. → を 12 回 (インスペクタの型が毎回呼ばれる)。
        await app.FocusAsync("editor");
        for (int i = 0; i < 12; i++)
        {
            await app.KeyAsync("Right");
            await app.IdleAsync();
        }

        // 3. 「プラグイン: throw」の例外の記録が 10 行以上。4. InfoBar と 2 つのボタン。
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("\"throw\" was stopped", StringComparison.Ordinal), "the plugin infobar");
        Assert.Equal(["Disable the plugin", "Try again at restart"], notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        List<JsonObject> rows = [.. (await app.SendAsync("scriptUi", new JsonObject { ["action"] = "outputRows" }))["rows"]!.AsArray().Select(r => r!.AsObject())];
        Assert.True(rows.Count(r => r["channel"]!.GetValue<string>() == "Plugin: throw" && r["kind"]!.GetValue<string>() == "Error") >= 10);
        Assert.Equal("Suspended", (await PluginAsync(app, "throw"))["state"]!.GetValue<string>());

        // 5. アプリは動き続け、オフセット 0 に FF を入力できる。
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        Assert.Equal([0xFF], await app.BytesAsync(0, 1));
        Assert.False(app.Process.HasExited);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-30-04")]
    [Trait(UiTest.TC, "TC-UI-30-03")]
    public Task Safe_mode_loads_no_plugins_and_a_normal_start_loads_them_again() => UiTestContext.RunAsync(async ctx =>
    {
        // Shift キーを押したままの起動は、テスト用のビルドの設定 shiftHeld で模擬する (システムのキーボードの入力は送らない)。
        // --safe-mode の起動は Core のテスト (TC-AUTO-30-04) とコマンドラインのテストが確かめる。
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Sample);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = new JsonObject { ["shiftHeld"] = true } });
        await PluginsAsync(app, "waitLoaded");

        // 1・3. 「(セーフモード)」の表示、InfoBar、プラグインは読み込まれない (候補なし、モジュールなし)。
        Assert.Contains("(Safe Mode)", (await app.StateAsync())["title"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject notice = await app.WaitForNotificationAsync(m => m.StartsWith("Started in safe mode", StringComparison.Ordinal), "the safe mode infobar");
        Assert.Contains("Restart normally", notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        JsonObject p = await PluginAsync(app, "test.sample");
        Assert.Equal("NotLoaded", p["state"]!.GetValue<string>());
        Assert.False(p["assemblyLoaded"]!.GetValue<bool>());
        Assert.DoesNotContain(await CommandTitlesAsync(app), t => t.StartsWith("Plugin: sample:", StringComparison.Ordinal) || t.EndsWith("sample: hello", StringComparison.Ordinal));
        app.Process.Refresh();
        Assert.DoesNotContain(app.Process.Modules.Cast<System.Diagnostics.ProcessModule>(), m => m.ModuleName.Equals("Plugin.Sample.dll", StringComparison.OrdinalIgnoreCase));
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 4. 通常の起動: 有効で候補が出る (一覧と設定は変わっていない)。
        app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await PluginsAsync(app, "waitLoaded");
        Assert.Equal("Enabled", (await PluginAsync(app, "test.sample"))["state"]!.GetValue<string>());
        Assert.Contains(await CommandTitlesAsync(app), t => t.EndsWith("sample: hello", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-34-08")]
    public Task As_administrator_one_confirmation_lists_the_plugins_and_dont_load_loads_none() => UiTestContext.RunAsync(async ctx =>
    {
        // 管理者として実行している扱いは、テスト用のビルドの設定 elevated で模擬する (UAC で昇格しない)。
        string profile = ctx.NewProfile();
        InstallTrusted(profile, TestPlugins.Sample);
        InstallTrusted(profile, TestPlugins.DepA);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = new JsonObject { ["elevated"] = true } });

        // 2. 確認は 1 つだけで、2 つのプラグインの名前が一覧されている。
        var dialog = await app.WaitForDialogAsync("PluginElevatedDialog");
        string text = await app.WaitForDialogTextAsync(dialog, "Sample Plugin", "dep-a", "administrator");
        Assert.Contains("Don't load", UiHelpers.DialogButtons(dialog));

        // 3. 「読み込まない」。
        await app.InvokeDialogButtonAsync("Don't load");
        await PluginsAsync(app, "waitLoaded");
        Assert.Empty(app.Window.FindAllDescendants(cf => cf.ByAutomationId("PluginElevatedDialog")));
        Assert.Empty(app.Window.FindAllDescendants(cf => cf.ByAutomationId("PluginTrustDialog")));

        // 4. どちらも読み込まれていない (モジュールの一覧にない)。
        foreach (string id in new[] { "test.sample", "dep-a" })
        {
            JsonObject p = await PluginAsync(app, id);
            Assert.NotEqual("Enabled", p["state"]!.GetValue<string>());
            Assert.False(p["assemblyLoaded"]!.GetValue<bool>());
        }

        app.Process.Refresh();
        Assert.DoesNotContain(app.Process.Modules.Cast<System.Diagnostics.ProcessModule>(),
            m => m.ModuleName is "Plugin.Sample.dll" or "Plugin.DepA.dll");
        Assert.False(string.IsNullOrEmpty(text));
    });
}
