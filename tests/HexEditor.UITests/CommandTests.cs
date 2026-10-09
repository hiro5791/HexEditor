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
    [Trait(UiTest.Priority, UiTest.High)]
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
        // 書き出しは負荷の高いときに遅れることがあるため、空になるまで最大 10 秒待つ。
        await Task.Delay(1000);
        JsonObject file = null!;
        await app.WaitUntilAsync(async () =>
        {
            file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "keybindings.json")))!.AsObject();
            return file["bindings"]!.AsArray().Count == 0;
        }, TimeSpan.FromSeconds(10), "keybindings.json to be reset");
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

    // ---- UI-02 タイトルバーの入口、UI-17 / UI-18 の呼び出し ----

    [Fact]
    public Task Title_bar_entry_shows_the_shortcut_and_opens_command_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)(1280 * scale), (int)(800 * scale));
        JsonObject entry = await app.SendAsync("titleBarPalette");
        Assert.Equal("Search commands (Ctrl+Shift+P)", entry["text"]!.GetValue<string>());
        Assert.False(entry["compact"]!.GetValue<bool>());
        double width = entry["width"]!.GetValue<double>();
        Assert.InRange(width, 240, 400);

        // ウィンドウ幅が 900 px 未満では虫眼鏡のアイコンだけ。
        await app.ResizeAsync((int)(800 * scale), (int)(600 * scale));
        await app.WaitUntilAsync(async () => (await app.SendAsync("titleBarPalette"))["compact"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the compact entry");
        Assert.True((await app.SendAsync("titleBarPalette"))["width"]!.GetValue<double>() < 100);

        // 押すとコマンドモードで開く。
        entry = await app.SendAsync("titleBarPalette", new JsonObject { ["invoke"] = true });
        Assert.True(entry["paletteOpen"]!.GetValue<bool>());
        Assert.Equal(">", entry["paletteText"]!.GetValue<string>());
    });

    [Fact]
    public Task Palette_entry_menu_changes_the_shortcut_of_the_command() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.KeyAsync("P", ctrl: true, shift: true);
        await app.SendAsync("palette", new JsonObject { ["text"] = ">Go to offset" });
        JsonObject r = await app.SendAsync("paletteChangeShortcut", new JsonObject { ["id"] = "go.goTo" });
        Assert.False(r["paletteOpen"]!.GetValue<bool>());
        await app.IdleAsync();
        JsonObject page = await app.SendAsync("settingsPage");
        Assert.Equal(("settings", "keyboard"), (page["active"]!.GetValue<string>(), page["category"]!.GetValue<string>()));
        JsonObject section = await app.SendAsync("keyboardSection");
        Assert.Equal("go.goTo", section["search"]!.GetValue<string>());
        Assert.Equal(["go.goTo"], section["rows"]!.AsArray().Select(x => x!["command"]!.GetValue<string>()));
    });

    // ---- UI-18 ショートカットの変更 (表、キーで検索、既定に戻す) ----

    [Fact]
    public Task Search_by_key_filters_instead_of_running_the_command() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject r = await app.SendAsync("keyboardSection", new JsonObject { ["searchByKey"] = "G", ["ctrl"] = true });
        Assert.True(r["capturing"]!.GetValue<bool>());
        Assert.True(r["handled"]!.GetValue<bool>());
        Assert.Null(r["command"]);
        Assert.False(r["capturingAfter"]!.GetValue<bool>());
        Assert.Equal("Ctrl+G", r["search"]!.GetValue<string>());
        Assert.False(r["goToBarVisible"]!.GetValue<bool>());
        Assert.Contains(r["rows"]!.AsArray(), x => x!["command"]!.GetValue<string>() == "go.goTo");
        Assert.DoesNotContain(r["rows"]!.AsArray(), x => x!["command"]!.GetValue<string>() == "search.find");

        // 修飾キーだけの打鍵は待ち続ける。
        r = await app.SendAsync("keyboardSection", new JsonObject { ["searchByKey"] = "Control" });
        Assert.True(r["capturingAfter"]!.GetValue<bool>());
    });

    [Fact]
    public Task Keyboard_table_is_a_virtualized_list_with_an_english_name_column() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ja" });
        JsonObject r = await app.SendAsync("keyboardSection");
        JsonObject open = r["rows"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["command"]!.GetValue<string>() == "file.open");
        Assert.Equal(("開く", "Open"), (open["name"]!.GetValue<string>(), open["english"]!.GetValue<string>()));
        int rows = r["rows"]!.AsArray().Count;
        int realized = r["realized"]!.GetValue<int>();
        Assert.InRange(realized, 1, rows - 1);

        // 絞り込みは行のデータだけを作り直す。
        r = await app.SendAsync("keyboardSection", new JsonObject { ["search"] = "ctrl+g" });
        Assert.Contains(r["rows"]!.AsArray(), x => x!["command"]!.GetValue<string>() == "go.goTo");
        Assert.True(r["rows"]!.AsArray().Count < rows);
    });

    [Fact]
    public Task Resetting_a_row_does_not_take_back_a_key_moved_to_another_command() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        Assert.True((await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.goTo", ["key"] = "Ctrl+F", ["replace"] = true }))["added"]!.GetValue<bool>());
        JsonObject r = await app.SendAsync("keyboardSection", new JsonObject { ["reset"] = "search.find" });
        JsonObject skipped = Assert.Single(r["skipped"]!.AsArray())!.AsObject();
        Assert.Equal(("Ctrl+F", "go.goTo"), (skipped["key"]!.GetValue<string>(), skipped["other"]!.GetValue<string>()));
        Assert.Empty(await KeysAsync(app, "search.find"));
        Assert.Equal(["Ctrl+G (global)", "Ctrl+F (global)"], await KeysAsync(app, "go.goTo"));
        await app.WaitForNotificationAsync(m => m.Contains("was not restored", StringComparison.Ordinal), "the notice");
    });

    [Fact]
    public Task Hex_view_menu_shows_the_current_shortcuts() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.True((await app.SendAsync("assignKey", new JsonObject { ["command"] = "edit.copy", ["key"] = "Ctrl+Shift+F7", ["scope"] = "editor" }))["added"]!.GetValue<bool>());
        JsonObject render = await app.RenderAsync();
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(render, 0));
        await app.IdleAsync();
        JsonObject menu = await app.SendAsync("hexViewMenuShortcuts");
        await app.SendAsync("hideContextMenu");
        Assert.Equal((await CommandAsync(app, "edit.copy"))["shortcut"]!.GetValue<string>(), menu["HexViewMenu_Copy"]!.GetValue<string>());
        Assert.Contains("F7", menu["HexViewMenu_Copy"]!.GetValue<string>());
        Assert.Equal((await CommandAsync(app, "edit.cut"))["shortcut"]!.GetValue<string>(), menu["HexViewMenu_Cut"]!.GetValue<string>());
    });

    // ---- UI-21 インポートの差分の一覧 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-21-02")]
    public Task Import_preview_lists_added_changed_and_removed_items_and_bad_lines() => UiTestContext.RunAsync(async ctx =>
    {
        string file = Path.Combine(ctx.Root, "keys.json");
        await File.WriteAllTextAsync(file, """
            {
              "$schemaVersion": 1,
              "preset": "default",
              "bindings": [
                { "command": "go.goTo", "key": "Ctrl+J" },
                { "command": "go.start", "key": "Ctrl+Shift+F7" },
                { "command": "-file.reload", "key": "Ctrl+R" },
                { "command": "go.end", "key": "Hyper+Q" }
              ]
            }
            """);
        AppSession app = await ctx.StartAsync();
        foreach (string mode in new[] { "merge", "replace" })
        {
            JsonObject preview = await app.SendAsync("importKeys", new JsonObject { ["path"] = file, ["mode"] = mode });
            Assert.Equal(["Start of file: Ctrl+Shift+F7"], preview["added"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(["Go to offset: Ctrl+G → Ctrl+G, Ctrl+J"], preview["changed"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(["Reload: Ctrl+R"], preview["removed"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(["Line 8: Invalid \"key\": Hyper+Q"], preview["errors"]!.AsArray().Select(x => x!.GetValue<string>()));
        }
    });

    // ---- UI-43 の呼び出し、UI-39 の画面 ----

    [Fact]
    public Task Change_language_command_opens_the_language_setting() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        Assert.True((await app.SendAsync("execute", new JsonObject { ["id"] = "settings.changeLanguage", ["fromPalette"] = true }))["executed"]!.GetValue<bool>());
        await app.IdleAsync();
        JsonObject page = await app.SendAsync("settingsPage");
        Assert.Equal(("settings", "language"), (page["active"]!.GetValue<string>(), page["category"]!.GetValue<string>()));
    });

    [Fact]
    public Task Shortcut_list_is_one_tab_for_the_whole_app() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("execute", new JsonObject { ["id"] = "help.shortcuts" });
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.SendAsync("execute", new JsonObject { ["window"] = 1, ["id"] = "help.shortcuts" });
        Assert.Empty((await TabTests.TabsAsync(app, 1))["toolTabs"]!.AsArray());
        Assert.Equal(["shortcuts"], (await TabTests.TabsAsync(app, 0))["toolTabs"]!.AsArray().Select(p => p!.GetValue<string>()));
    });

    // ---- UI-17 の仕様 7 引数を尋ねるコマンド ----

    [Fact]
    public Task Palette_asks_for_arguments() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 一覧から選ぶ引数 (文字コード)。
        await app.KeyAsync("P", ctrl: true, shift: true);
        await app.SendAsync("palette", new JsonObject { ["text"] = ">view.encoding.select" });
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        JsonObject state = await app.SendAsync("palette");
        Assert.True(state["open"]!.GetValue<bool>());
        Assert.StartsWith("Choose an encoding", state["message"]!.GetValue<string>());
        state = await app.SendAsync("palette", new JsonObject { ["text"] = "utf-16 be" });
        Assert.Equal("argument:view.encoding.select:utf-16be", state["entries"]!.AsArray()[0]!["key"]!.GetValue<string>());
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        Assert.Contains("16", (await app.RenderAsync())["encoding"]!.GetValue<string>());

        // 入力を確かめる引数 (1 行のバイト数)。誤りは一覧の上に出し、Enter を無効にする。
        await app.KeyAsync("P", ctrl: true, shift: true);
        await app.SendAsync("palette", new JsonObject { ["text"] = ">view.bytesPerRowCustom" });
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        state = await app.SendAsync("palette", new JsonObject { ["text"] = "0" });
        Assert.False(string.IsNullOrEmpty(state["message"]?.GetValue<string>()));
        Assert.False((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        await app.SendAsync("palette", new JsonObject { ["text"] = "24" });
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        Assert.Equal(24, (await app.DocumentAsync())["bytesPerRow"]!.GetValue<int>());
    });

    // ---- UI-18 の「エラー」 keybindings.json の誤り ----

    [Fact]
    public Task Invalid_keybindings_file_is_kept_before_it_is_rewritten() => UiTestContext.RunAsync(async ctx =>
    {
        const string invalid = """
            {
              "$schemaVersion": 1,
              "bindings": [
                { "command": "go.goTo", "key": "Ctrl+J" },
                { "command": "search.find", "key": "Hyper+Q" }
              ]
            }
            """;
        string profile = Profile(ctx, keybindings: invalid);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.WaitForNotificationAsync(m => m.Contains("line 5", StringComparison.Ordinal), "the line number of the error");
        Assert.Equal(["Ctrl+G (global)", "Ctrl+J (global)"], await KeysAsync(app, "go.goTo"));

        // 割り当てを変えて保存すると、前のファイルを keybindings.json.broken-<日時> として残す。
        await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.end", ["key"] = "Ctrl+Shift+F8" });
        await app.SendAsync("flushSettings");
        string kept = Assert.Single(Directory.GetFiles(profile, "keybindings.json.broken-*"));
        Assert.Equal(invalid, await File.ReadAllTextAsync(kept));
        Assert.Contains("Ctrl+Shift+F8", await File.ReadAllTextAsync(Path.Combine(profile, "keybindings.json")));
    });

    [Fact]
    public Task Too_new_keybindings_file_is_read_but_not_written() => UiTestContext.RunAsync(async ctx =>
    {
        const string newer = """{ "$schemaVersion": 2, "preset": "default", "bindings": [ { "command": "go.goTo", "key": "Ctrl+J" } ] }""";
        string profile = Profile(ctx, keybindings: newer);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.WaitForNotificationAsync(m => m.Contains("newer version", StringComparison.Ordinal), "the newer file notice");
        Assert.Equal(["Ctrl+G (global)", "Ctrl+J (global)"], await KeysAsync(app, "go.goTo"));
        await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.end", ["key"] = "Ctrl+Shift+F8" });
        await Task.Delay(1000);
        await app.SendAsync("flushSettings");
        Assert.Equal(newer, await File.ReadAllTextAsync(Path.Combine(profile, "keybindings.json")));
    });

    // ---- UI-20 の仕様 3 押せないキーの表示 ----

    [Fact]
    public Task Shortcut_list_and_keyboard_table_show_the_same_layout_warnings() => UiTestContext.RunAsync(async ctx =>
    {
        // 今の配列で押せるかは配列による (作業中の PC の配列は変えない)。表と一覧が同じ判定を表示することを確かめる。
        AppSession app = await ctx.StartAsync();
        Assert.True((await app.SendAsync("assignKey", new JsonObject { ["command"] = "go.end", ["key"] = "Ctrl+Oem8" }))["added"]!.GetValue<bool>());
        JsonObject list = await app.SendAsync("shortcutWarnings");
        JsonObject table = await app.SendAsync("keyboardSection", new JsonObject { ["search"] = "go.end" });
        string tableWarning = table["rows"]!.AsArray().Single(r => r!["command"]!.GetValue<string>() == "go.end")!["warnings"]!.GetValue<string>();
        Assert.Equal(tableWarning, list["go.end global"]?.GetValue<string>() ?? string.Empty);
        if (tableWarning.Length > 0)
        {
            Assert.Contains("cannot be pressed", tableWarning);
        }
    });
}
