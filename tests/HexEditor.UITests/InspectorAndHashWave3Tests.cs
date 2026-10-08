using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// データインスペクタ・ブックマーク・ハッシュパネルのフェーズ 1 の残り (INSP-02 のコマンド、INSP-19 の保存したプリセット、INSP-23 の既定の色と
/// ツールチップ、ANA-18 の「終了」の指定と履歴、ANA-21・ANA-22 のコマンド) と、ウィンドウを閉じたときのアプリ全体のイベントの解除。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class InspectorAndHashWave3Tests
{
    private static Task<JsonObject> HashAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("hash", request, TimeSpan.FromSeconds(60));
    }

    private static Task<JsonObject> ExecuteAsync(AppSession app, string id) => app.SendAsync("execute", new JsonObject { ["id"] = id });

    private static async Task<JsonArray> WaitForRowsAsync(AppSession app, int count)
    {
        JsonArray rows = [];
        await app.WaitUntilAsync(async () =>
        {
            JsonObject state = await HashAsync(app);
            rows = state["rows"]!.AsArray();
            return !state["computing"]!.GetValue<bool>() && rows.Count == count;
        }, TimeSpan.FromSeconds(30), $"{count} hash results");
        return rows;
    }

    private static string ValueOf(JsonArray rows, string id) =>
        rows.First(r => r!["id"]!.GetValue<string>() == id)!["value"]!.GetValue<string>();

    // ---- ANA-18 ----

    [Fact]
    public Task Custom_range_accepts_an_inclusive_end() => UiTestContext.RunAsync(async ctx =>
    {
        // 06 の 0.1: 範囲を指定は、開始と長さ、または開始と「終了 (このバイトを含む)」。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        await HashAsync(app, "show");
        await HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray("crc32") });

        await HashAsync(app, "custom", new JsonObject { ["start"] = "0", ["end"] = "3" });
        Assert.Equal("0x0–0x3 (4 bytes)", (await HashAsync(app))["range"]!.GetValue<string>());
        await HashAsync(app, "compute");
        string byEnd = ValueOf(await WaitForRowsAsync(app, 1), "crc32");

        // 同じ範囲を長さで指定すると同じ値 (「1234」の CRC-32)。
        await HashAsync(app, "custom", new JsonObject { ["start"] = "0", ["length"] = "4" });
        Assert.Equal("0x0–0x3 (4 bytes)", (await HashAsync(app))["range"]!.GetValue<string>());
        await HashAsync(app, "compute");
        Assert.Equal(byEnd, ValueOf(await WaitForRowsAsync(app, 1), "crc32"));
        Assert.Equal("9BE3E0A3", byEnd);

        // 終了は末尾のバイトまで。末尾を超えると範囲の誤り。
        await HashAsync(app, "custom", new JsonObject { ["start"] = "1", ["end"] = "8" });
        Assert.Equal("0x1–0x8 (8 bytes)", (await HashAsync(app))["range"]!.GetValue<string>());
        await HashAsync(app, "custom", new JsonObject { ["start"] = "0", ["end"] = "9" });
        Assert.Equal("The range is outside the document.", (await HashAsync(app))["range"]!.GetValue<string>());
    });

    [Fact]
    public Task Results_are_kept_per_document_until_the_panel_closes() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-18 の仕様 9: 結果はパネルを閉じるまで履歴として持つ。タブを切り替えて戻っても消えない (自動で再計算しない設定でも残る)。
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["hash.autoRecompute"] = false });
        string first = ctx.CopyTestData("TD-ANA-CHECK9", "first.bin");
        string second = ctx.CopyTestData("TD-SEQ-1M", "second.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [first, second], Profile = profile });
        await app.WaitForTabsAsync(2);
        await EditingTests.SelectTabAsync(app, 0);
        await HashAsync(app, "show");
        await HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray("crc32") });
        await HashAsync(app, "compute");
        Assert.Equal("CBF43926", ValueOf(await WaitForRowsAsync(app, 1), "crc32"));

        await EditingTests.SelectTabAsync(app, 1);
        await app.IdleAsync();
        Assert.Empty((await HashAsync(app))["rows"]!.AsArray());

        await EditingTests.SelectTabAsync(app, 0);
        await app.IdleAsync();
        Assert.Equal("CBF43926", ValueOf((await HashAsync(app))["rows"]!.AsArray(), "crc32"));

        // パネルを閉じると履歴は消える。
        await HashAsync(app, "hide");
        await HashAsync(app, "show");
        Assert.Empty((await HashAsync(app))["rows"]!.AsArray());
    });

    // ---- ANA-21・ANA-22 のコマンド ----

    [Fact]
    public Task Palette_commands_verify_and_copy_hash_values() => UiTestContext.RunAsync(async ctx =>
    {
        string data = ctx.TestData("TD-ANA-CHECK9");
        string sums = ctx.WriteFile("check9.sha256", Encoding.UTF8.GetBytes(
            "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225  " + Path.GetFileName(data) + "\n"));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [data], Hooks = new JsonObject { ["openPicker"] = new JsonArray(sums) } });

        // 結果がない間は「ハッシュ値をコピー」は使えない。
        JsonObject commands = await app.SendAsync("commands");
        Assert.False(commands["items"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "analysis.hash.copy")!["enabled"]!.GetValue<bool>());

        // 「解析: ハッシュ値を照合」: パネルを開き、期待値の欄にフォーカスを移す。
        await ExecuteAsync(app, "analysis.hash.verify");
        await app.WaitUntilAsync(async () => await app.FocusedAsync() == "TextBox:Hash_Expected", TimeSpan.FromSeconds(10), "focus on the expected value");

        // 「解析: チェックサムファイルで検証」: ファイルの行のアルゴリズム (SHA-256) で計算して照合する。
        await ExecuteAsync(app, "analysis.hash.verifyFile");
        await app.WaitUntilAsync(async () => (await HashAsync(app))["verifyMessage"]!.GetValue<string>().Length > 0, TimeSpan.FromSeconds(30), "the verification");
        JsonObject state = await HashAsync(app);
        Assert.Equal("SHA-256: Match", state["verifyMessage"]!.GetValue<string>());

        // 「解析: ハッシュ値をコピー」: 複数行なら「名前: 値」をすべての行。
        await HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray("crc32", "md5") });
        await HashAsync(app, "compute");
        await WaitForRowsAsync(app, 2);
        await ExecuteAsync(app, "analysis.hash.copy");
        string text = (await app.SendAsync("clipboard"))["text"]!.GetValue<string>();
        Assert.Contains("CRC-32: CBF43926", text);
        Assert.Contains("MD5: 25F9E794323B453885F5181F1B624D0B", text);

        // 1 行なら値だけ。
        await HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray("crc32") });
        await HashAsync(app, "compute");
        await WaitForRowsAsync(app, 1);
        await ExecuteAsync(app, "analysis.hash.copy");
        Assert.Equal("CBF43926", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>());
    });

    // ---- INSP-02 ----

    [Fact]
    public Task Palette_command_toggles_the_inspector_endianness() => UiTestContext.RunAsync(async ctx =>
    {
        // 「インスペクタ: エンディアンの切り替え」: 反対のエンディアンに固定し、もう一度で「ドキュメントに従う」(LE) に戻る。
        AppSession app = await InspectorTests.StartAsync(ctx);
        Assert.Equal("LE", (await InspectorTests.StateAsync(app))["endian"]!.GetValue<string>());
        string le = await InspectorTests.ValueAsync(app, "uint32");

        await ExecuteAsync(app, "inspector.toggleEndian");
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal("BE (inspector only)", (await InspectorTests.StateAsync(app))["endian"]!.GetValue<string>());
        Assert.NotEqual(le, await InspectorTests.ValueAsync(app, "uint32"));

        await ExecuteAsync(app, "inspector.toggleEndian");
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal("LE", (await InspectorTests.StateAsync(app))["endian"]!.GetValue<string>());
        Assert.Equal(le, await InspectorTests.ValueAsync(app, "uint32"));
    });

    // ---- INSP-09 ----

    [Fact]
    public Task Characters_without_a_glyph_show_only_the_code_point() => UiTestContext.RunAsync(async ctx =>
    {
        // 仕様 3: 代替フォントでも表示できない文字 (U+0378 は未割り当てで、どのフォントにもない) はコードポイントだけ。
        string file = ctx.WriteFile("glyphs.bin", [0xCD, 0xB8, 0xE3, 0x81, 0x82]);
        AppSession app = await InspectorTests.StartAsync(ctx, file: file);
        Assert.Equal("U+0378  (2 bytes)", await InspectorTests.ValueAsync(app, "utf8"));
        await InspectorTests.GoAsync(app, 2);
        Assert.Equal("あ  U+3042  (3 bytes)", await InspectorTests.ValueAsync(app, "utf8"));
    });

    // ---- INSP-19 ----

    [Fact]
    public Task Applying_a_saved_preset_shows_it_as_selected() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Setting_int16");
        Assert.Equal("Basic", (await app.SendAsync("inspectorPreset"))["selected"]?.GetValue<string>());

        // int16 を隠した構成を「mine」として保存する。
        await app.CommandAsync("Inspector_Setting_int16");
        await app.UiaSetValueAsync("Inspector_PresetName", "mine");
        await app.UiaInvokeAsync("Inspector_SavePreset");
        await app.IdleAsync();
        Assert.Equal("mine", (await app.SendAsync("inspectorPreset"))["selected"]?.GetValue<string>());

        // 「基本」に戻してから「mine」を選ぶと、適用した後も「mine」を選んだ状態のまま。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_Preset", ["text"] = "Basic" });
        await app.IdleAsync();
        Assert.Contains("int16", await InspectorTests.RowIdsAsync(app));
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_Preset", ["text"] = "mine" });
        await app.IdleAsync();
        Assert.DoesNotContain("int16", await InspectorTests.RowIdsAsync(app));
        Assert.Equal("mine", (await app.SendAsync("inspectorPreset"))["selected"]?.GetValue<string>());
    });

    // ---- INSP-23 ----

    [Fact]
    public Task New_bookmarks_use_the_default_color_and_the_tool_tip_shows_the_comment() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });

        // 仕様 2: 色は設定「ブックマークの既定の色」(既定は 1 番目)。
        await app.GoToAsync(0x100);
        await app.KeyAsync("F2", ctrl: true);
        await app.IdleAsync();
        Assert.True((await app.SendAsync("settingSet", new JsonObject { ["key"] = "bookmarks.defaultColor", ["value"] = "3" }))["valid"]!.GetValue<bool>());
        await app.GoToAsync(0x200);
        await app.KeyAsync("F2", ctrl: true);
        await app.IdleAsync();
        JsonArray bookmarks = (await app.SendAsync("bookmarks"))["bookmarks"]!.AsArray();
        Assert.Equal(["1", "3"], bookmarks.Select(b => b!["color"]!.GetValue<string>()));

        // 仕様 8: ツールチップに名前とコメントの冒頭。コメントがなければ名前だけ。
        Assert.Contains("Bookmark 1", (await app.SendAsync("bookmarkToolTip", new JsonObject { ["offset"] = 0x100 }))["text"]!.GetValue<string>());
        await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = 0x200 });
        await app.WaitForAsync("Bookmark_Name");
        await app.UiaSetValueAsync("Bookmark_Name", "header");
        await app.UiaSetValueAsync("Bookmark_Comment", "\n**File header** of the sample, followed by a long explanation that goes on and on beyond eighty characters\nsecond line");
        await app.IdleAsync();
        string tip = (await app.SendAsync("bookmarkToolTip", new JsonObject { ["offset"] = 0x200 }))["text"]!.GetValue<string>();
        Assert.Contains("header: **File header** of the sample, followed by a long explanation that goes on and o…", tip);
        Assert.DoesNotContain("second line", tip);
    });

    // ---- ウィンドウを閉じたときのイベントの解除 ----

    [Fact]
    public Task Closing_a_window_detaches_its_app_wide_subscriptions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await app.CommandAsync("Command_ToggleBookmarks");
        await app.IdleAsync();
        JsonObject before = await app.SendAsync("appSubscriptions");

        // 新しいウィンドウでインスペクタ・ブックマーク一覧・ハッシュパネルを開いてから閉じる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "window.new" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("appSubscriptions"))["windows"]!.GetValue<int>() == 2, TimeSpan.FromSeconds(30), "the second window");
        foreach (string id in new[] { "view.panel.inspector", "view.panel.bookmarks", "view.panel.hash" })
        {
            await app.SendAsync("execute", new JsonObject { ["id"] = id, ["window"] = 1 });
        }

        await app.SendAsync("idle", new JsonObject { ["window"] = 1 });
        JsonObject opened = await app.SendAsync("appSubscriptions");
        Assert.True(opened["settings"]!.GetValue<int>() > before["settings"]!.GetValue<int>());
        Assert.True(opened["bookmarkColumns"]!.GetValue<int>() > before["bookmarkColumns"]!.GetValue<int>());

        await app.SendAsync("closeWindow", new JsonObject { ["window"] = 1 });
        await app.WaitUntilAsync(async () => (await app.SendAsync("appSubscriptions"))["windows"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(30), "the window to close");
        JsonObject after = await app.SendAsync("appSubscriptions");
        Assert.Equal(before["settings"]!.GetValue<int>(), after["settings"]!.GetValue<int>());
        Assert.Equal(before["bookmarkColumns"]!.GetValue<int>(), after["bookmarkColumns"]!.GetValue<int>());
    });
}
