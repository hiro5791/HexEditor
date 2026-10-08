using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ツールバー (UI-04)、コマンドパレット (UI-17)、ショートカットの変更・プリセット・インポート / エクスポート (UI-18、UI-19、UI-21)、
/// ショートカット一覧 (UI-39)。設定画面の操作は、画面の部品と同じ処理を呼ぶテスト用の命令 (MainWindow.FrameworkTestCommands.cs) を使う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CommandTests
{
    /// <summary>TD-UI-KEYS-CUSTOM。</summary>
    internal const string KeysCustom = """
        {
          "$schemaVersion": 1,
          "preset": "default",
          "bindings": [
            { "command": "edit.fill", "key": "Ctrl+K Ctrl+F", "when": "editor" },
            { "command": "-file.print", "key": "Ctrl+P" }
          ]
        }
        """;

    /// <summary>設定の書き出しを待ってから終了する。</summary>
    internal static async Task ExitAsync(AppSession app)
    {
        await app.SendAsync("flushSettings");
        try
        {
            await app.SendAsync("exit");
        }
        catch (IOException)
        {
        }

        await app.WaitForExitAsync(TimeSpan.FromSeconds(20));
    }

    internal static string Profile(UiTestContext ctx, string? settings = null, string? keybindings = null)
    {
        string profile = ctx.NewProfile();
        if (settings is not null)
        {
            File.WriteAllText(Path.Combine(profile, "settings.json"), settings);
        }

        if (keybindings is not null)
        {
            File.WriteAllText(Path.Combine(profile, "keybindings.json"), keybindings);
        }

        return profile;
    }

    private static async Task<JsonObject> CommandAsync(AppSession app, string id) =>
        (await app.SendAsync("commands"))["items"]!.AsArray().Select(n => n!.AsObject()).Single(c => c["id"]!.GetValue<string>() == id);

    private static async Task<List<string>> KeysAsync(AppSession app, string command) =>
        [.. (await app.SendAsync("keyBindings", new JsonObject { ["command"] = command }))["bindings"]!.AsArray().Select(b => $"{b!["key"]} ({b["scope"]})")];

    // ---- UI-04 ツールバー ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-04-01")]
    public Task Toolbar_is_hidden_at_first_and_customization_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = Profile(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 1. 初回起動ではツールバーがない。
        Assert.False(await app.IsShownAsync("Toolbar"));

        // 2. 表示 > パネルの表示切り替え > ツールバー。
        await app.CommandAsync("Command_Toolbar");
        await app.IdleAsync();
        Assert.True(await app.IsShownAsync("Toolbar"));

        // 3. カスタマイズで末尾に追加する (全画面表示 UI-07 は後半で作るので、代わりに「ショートカット一覧」)。
        await app.SendAsync("toolbarAdd", new JsonObject { ["id"] = "help.shortcuts" });
        await ExitAsync(app);

        // 4〜5. 同じ設定フォルダで起動し直す。
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject toolbar = await again.SendAsync("toolbar");
        Assert.True(toolbar["visible"]!.GetValue<bool>());
        Assert.Equal("help.shortcuts", toolbar["buttons"]!.AsArray().Last()!["id"]!.GetValue<string>());

        // ツールチップは「表示名 (ショートカット)」(UI-04 の仕様 3)。
        Assert.Contains(toolbar["buttons"]!.AsArray(), b => b!["id"]!.GetValue<string>() == "file.open" && b["toolTip"]!.GetValue<string>() == "Open (Ctrl+O)");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-04-02")]
    public Task Buttons_that_do_not_fit_go_to_the_overflow_menu() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SET-TOOLBAR。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = Profile(ctx, """{ "$schemaVersion": 1, "ui.toolbar.visible": true }""") });
        await app.ResizeAsync(1280, 800);
        await app.IdleAsync();
        JsonArray wide = (await app.SendAsync("toolbar"))["buttons"]!.AsArray();
        Assert.All(wide, b => Assert.False(b!["inOverflow"]!.GetValue<bool>(), b["id"]!.GetValue<string>()));

        await app.ResizeAsync(640, 600);
        JsonArray narrow = [];
        await app.WaitUntilAsync(async () =>
        {
            narrow = (await app.SendAsync("toolbar"))["buttons"]!.AsArray();
            return narrow.Any(b => b!["inOverflow"]!.GetValue<bool>());
        }, TimeSpan.FromSeconds(5), "buttons move to the overflow menu");

        // 表示されていないボタンは、すべて「…」メニューに入っている (なくなったボタンはない)。
        Assert.Equal(wide.Select(b => b!["id"]!.GetValue<string>()), narrow.Select(b => b!["id"]!.GetValue<string>()));
    });

    // ---- UI-17 コマンドパレット ----

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-UI-17-01")]
    public Task Palette_opens_within_100ms() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        var times = new List<double>();
        for (int i = 0; i < 50; i++)
        {
            var sw = Stopwatch.StartNew();
            await app.KeyAsync("P", ctrl: true, shift: true);
            JsonObject state = await app.SendAsync("palette");
            sw.Stop();
            Assert.True(state["open"]!.GetValue<bool>());
            times.Add(sw.Elapsed.TotalMilliseconds);
            await app.SendAsync("paletteClose");
        }

        Assert.True(times.Max() < 100, $"最大 {times.Max():F1} ms (命令の通り道の往復を含む)");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-17-02")]
    public Task Japanese_ui_finds_open_by_english_name_and_reading() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ja" });

        // 1. Ctrl+Shift+P で開き、open と入力する。
        Assert.Equal("menu:Command_CommandPalette", (await app.KeyAsync("P", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        JsonObject state = await app.SendAsync("palette", new JsonObject { ["text"] = ">open" });
        JsonObject open = state["entries"]!.AsArray().Select(e => e!.AsObject()).First(e => e["key"]!.GetValue<string>() == "command:file.open");
        Assert.Equal("ファイル: 開く", open["title"]!.GetValue<string>());
        Assert.Equal("Open", open["secondary"]!.GetValue<string>());

        // 3. 読みで探す。
        state = await app.SendAsync("palette", new JsonObject { ["text"] = ">ひらく" });
        Assert.Contains(state["entries"]!.AsArray(), e => e!["key"]!.GetValue<string>() == "command:file.open");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-17-04")]
    public Task Offset_mode_moves_to_the_expression() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("P", ctrl: true, shift: true);
        JsonObject state = await app.SendAsync("palette", new JsonObject { ["text"] = ":end-0x10" });
        Assert.Contains("0xFFFF0", state["entries"]!.AsArray()[0]!["title"]!.GetValue<string>());
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        Assert.Equal(0xFFFF0, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        Assert.False((await app.SendAsync("palette"))["open"]!.GetValue<bool>());

        // 不正な入力は理由を出し、Enter を無効にする。
        await app.KeyAsync("P", ctrl: true, shift: true);
        state = await app.SendAsync("palette", new JsonObject { ["text"] = ":end+(" });
        Assert.False(string.IsNullOrEmpty(state["message"]?.GetValue<string>()));
        Assert.False((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-17-05")]
    public Task Recently_used_command_comes_first_after_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = Profile(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 1. コマンドパレットで「表示: ツールバーの表示切り替え」を実行する。
        await app.KeyAsync("P", ctrl: true, shift: true);
        JsonObject state = await app.SendAsync("palette", new JsonObject { ["text"] = ">Toggle toolbar" });
        Assert.Equal("command:view.toolbar", state["entries"]!.AsArray()[0]!["key"]!.GetValue<string>());
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        await ExitAsync(app);

        // 2〜3. 起動し直し、入力が空の候補の一覧を見る。
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        await again.KeyAsync("P", ctrl: true, shift: true);
        JsonArray entries = (await again.SendAsync("palette"))["entries"]!.AsArray();
        Assert.True(entries[0]!["header"]!.GetValue<bool>());
        Assert.Equal("Recently used", entries[0]!["title"]!.GetValue<string>());
        Assert.Equal("command:view.toolbar", entries[1]!["key"]!.GetValue<string>());
        Assert.Contains("view.toolbar", await File.ReadAllTextAsync(Path.Combine(profile, "state.json")));
    });

    // ---- UI-18 ショートカットの変更 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-18-01")]
    public Task Added_key_shows_in_the_menu_and_works() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject r = await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+J" });
        Assert.True(r["added"]!.GetValue<bool>());

        // 2. メニューの「オフセットへ移動」に Ctrl+G と Ctrl+J が並ぶ。
        Assert.Equal("Ctrl+G, Ctrl+J", (await CommandAsync(app, "go.goTo"))["menuShortcut"]!.GetValue<string>());

        // 3. Hex ビューで Ctrl+J。
        Assert.Equal("menu:Command_GoTo", (await app.KeyAsync("J", ctrl: true))["handledBy"]!.GetValue<string>());
        Assert.True((await app.StateAsync())["goToBarVisible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-18-02")]
    public Task Conflict_is_listed_and_replace_moves_the_key() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        JsonObject r = await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+F" });
        Assert.False(r["added"]!.GetValue<bool>());
        Assert.True(r["needsReplace"]!.GetValue<bool>());
        JsonObject conflict = Assert.Single(r["conflicts"]!.AsArray())!.AsObject();
        Assert.Equal(("search.find", "global"), (conflict["command"]!.GetValue<string>(), conflict["scope"]!.GetValue<string>()));

        // 3. 「置き換える」。
        Assert.True((await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+F", ["replace"] = true }))["added"]!.GetValue<bool>());
        Assert.Empty(await KeysAsync(app, "search.find"));
        Assert.Equal(["Ctrl+G (global)", "Ctrl+F (global)"], await KeysAsync(app, "go.goTo"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-18-03")]
    public Task Unmodified_letter_is_rejected_with_a_reason() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        JsonObject r = await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "A" });
        Assert.False(r["added"]!.GetValue<bool>());
        Assert.Contains("typing", r["rejection"]!.GetValue<string>());
        Assert.Equal(["Ctrl+G (global)"], await KeysAsync(app, "go.goTo"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-18-04")]
    public Task Reset_all_empties_the_bindings_in_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = Profile(ctx, keybindings: KeysCustom);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 1〜2. 設定画面の「キーボード」>「すべて既定に戻す」> 確認ダイアログの「既定に戻す」。
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "keyboard" });
        await app.IdleAsync();
        await app.UiaInvokeAsync("Keyboard_ResetAll");
        await app.WaitForDialogAsync("KeysResetAllDialog");
        await app.InvokeDialogButtonAsync("Reset to default");

        // 3. 500 ms 以上待ってから keybindings.json を読む。
        await Task.Delay(1000);
        JsonObject file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "keybindings.json")))!.AsObject();
        Assert.Empty(file["bindings"]!.AsArray());
        Assert.Equal("default", file["preset"]!.GetValue<string>());
    });

    // ---- UI-19 プリセット ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-19-02")]
    public Task Switching_preset_updates_the_menu_and_keeps_user_bindings() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = Profile(ctx, keybindings: KeysCustom);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "keyboard" });

        // 1. プリセットを「HxD 風」に (コマンド X = 「やり直し」: hxd.json で Ctrl+Shift+Z を外す)。
        Assert.Equal("Ctrl+Y, Ctrl+Shift+Z", (await CommandAsync(app, "edit.redo"))["menuShortcut"]!.GetValue<string>());
        await app.SendAsync("setPreset", new JsonObject { ["preset"] = "hxd", ["preferUser"] = true });

        // 3. 設定画面を開いたまま、メニューの表示が変わる。
        Assert.Equal("Ctrl+Y", (await CommandAsync(app, "edit.redo"))["menuShortcut"]!.GetValue<string>());
        Assert.Equal("settings", (await app.SendAsync("settingsPage"))["active"]!.GetValue<string>());

        // 4. 利用者の割り当て (edit.fill は編集の機能が登録するコマンドで、登録前でもファイルに残る)。
        await app.SendAsync("flushSettings");
        JsonObject file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "keybindings.json")))!.AsObject();
        Assert.Equal("hxd", file["preset"]!.GetValue<string>());
        Assert.Contains(file["bindings"]!.AsArray(), b => b!["command"]!.GetValue<string>() == "edit.fill" && b["key"]!.GetValue<string>() == "Ctrl+K Ctrl+F");
    });

    // ---- UI-20 キーボード配列 ----
    // 入力の配列 (ドイツ語・フランス語) や IME を切り替えるのは OS の設定の変更で、作業中の PC では行わない (テスト方針 5.2 の
    // 専用のランナーで行う)。キーの表示名・押せないキー・AltGr の判定は、今の配列で Win32 の API から求める。

    private const string LayoutSkip = "入力の配列・IME の切り替え (OS の設定の変更) が必要なため、専用のランナーで行う。";

    [Fact(Skip = LayoutSkip)]
    [Trait(UiTest.TC, "TC-UI-20-01")]
    public void German_layout_key_names()
    {
    }

    [Fact(Skip = LayoutSkip)]
    [Trait(UiTest.TC, "TC-UI-20-02")]
    public void German_layout_altgr_types_in_the_text_column()
    {
    }

    [Fact(Skip = LayoutSkip)]
    [Trait(UiTest.TC, "TC-UI-20-03")]
    public void Azerty_ctrl_z_undoes()
    {
    }

    [Fact(Skip = LayoutSkip)]
    [Trait(UiTest.TC, "TC-UI-20-04")]
    public void Ime_composition_does_not_run_shortcuts()
    {
    }

    // ---- UI-21 インポート / エクスポート ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-21-01")]
    public Task Exported_shortcuts_imported_in_another_profile_are_the_same() => UiTestContext.RunAsync(async ctx =>
    {
        string a = Profile(ctx, keybindings: KeysCustom);
        string exported = Path.Combine(ctx.Root, "hexeditor-keybindings.json");
        AppSession first = await ctx.StartAsync(new AppOptions { Profile = a });
        await first.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+J" });
        await first.SendAsync("exportKeys", new JsonObject { ["path"] = exported });
        JsonArray before = (await first.SendAsync("commands"))["items"]!.AsArray();
        await ExitAsync(first);

        string b = Profile(ctx);
        AppSession second = await ctx.StartAsync(new AppOptions { Profile = b });
        await second.SendAsync("importKeys", new JsonObject { ["path"] = exported, ["mode"] = "replace", ["apply"] = true });
        JsonArray after = (await second.SendAsync("commands"))["items"]!.AsArray();
        Assert.Equal(before.Select(c => $"{c!["id"]} {c["shortcut"]}"), after.Select(c => $"{c!["id"]} {c["shortcut"]}"));
        Assert.True(File.Exists(Path.Combine(b, "keybindings.json.bak")));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-21-02")]
    public Task Importing_a_file_with_a_conflict_shows_it_before_applying() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-KEYS-DUP。
        string dup = Path.Combine(ctx.Root, "dup.json");
        await File.WriteAllTextAsync(dup, """
            { "$schemaVersion": 1, "preset": "default", "bindings": [
              { "command": "search.find", "key": "Ctrl+G", "when": "global" },
              { "command": "no.such.command", "key": "Ctrl+Shift+F9" } ] }
            """);
        AppSession app = await ctx.StartAsync();
        JsonObject preview = await app.SendAsync("importKeys", new JsonObject { ["path"] = dup, ["mode"] = "merge" });
        JsonObject conflict = Assert.Single(preview["conflicts"]!.AsArray())!.AsObject();
        Assert.Equal(("Ctrl+G", "search.find", "go.goTo"), (conflict["key"]!.GetValue<string>(), conflict["first"]!.GetValue<string>(), conflict["second"]!.GetValue<string>()));
        Assert.Equal("Unknown commands: 1", preview["unknownText"]!.GetValue<string>());

        // キャンセル (確定しない) なら割り当ては変わらない。
        Assert.Equal(["Ctrl+G (global)"], await KeysAsync(app, "go.goTo"));
        Assert.Equal(["Ctrl+F (global)"], await KeysAsync(app, "search.find"));
    });

    // ---- UI-39 ショートカット一覧 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-39-01")]
    public Task Shortcut_list_reflects_changes_immediately() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.CommandAsync("Command_Shortcuts");
        await app.IdleAsync();
        await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+J" });
        JsonObject page = await app.SendAsync("shortcutsPage");
        JsonObject row = page["rows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["command"]!.GetValue<string>() == "go.goTo");
        Assert.Equal("Ctrl+G, Ctrl+J", row["keys"]!.GetValue<string>());
        Assert.Contains("ShortcutRow_go.goTo", page["shown"]!.AsArray().Select(n => n!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-39-02")]
    public Task Saved_html_has_every_category_heading() => UiTestContext.RunAsync(async ctx =>
    {
        // ブラウザの代わりに、保存した HTML を XML として読んで見出しを数える (Edge のヘッドレス起動はこの PC では行わない)。
        string html = Path.Combine(ctx.Root, "shortcuts.html");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["savePicker"] = html } });
        await app.SendAsync("saveShortcutsHtml");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(html)), TimeSpan.FromSeconds(10), "the HTML file is saved");
        XDocument doc = XDocument.Parse((await File.ReadAllTextAsync(html)).Replace("<!DOCTYPE html>", string.Empty));
        var headings = doc.Descendants("h2").Select(h => h.Value).ToList();
        JsonArray rows = (await app.SendAsync("shortcutsPage"))["rows"]!.AsArray();
        Assert.Equal(rows.Select(r => r!["category"]!.GetValue<string>()).Distinct(), headings);
        Assert.Contains("File", headings);
        Assert.Contains("Help", headings);
    });
}
