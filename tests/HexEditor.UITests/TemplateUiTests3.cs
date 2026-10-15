using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>
/// 構造体テンプレートの画面のつづき: 定義へ移動 (TPL-24)、出力パネル (TPL-27)、自動適用 (TPL-30)、エディタとデバッガ (TPL-31/32)、
/// 標準テンプレートの提案 (TPL-34)、表示名の翻訳 (TPL-17)、性能 (TPL-21/23/26/28)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateToolUiTests(ITestOutputHelper output)
{
    private static Task<JsonObject> EditorAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("templateEditor", request, TimeSpan.FromSeconds(120));
    }

    private static Task<JsonObject> EditorKeyAsync(AppSession app, string key, bool shift = false) =>
        EditorAsync(app, "key", new JsonObject { ["key"] = key, ["shift"] = shift });

    // ───────────── TPL-24 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-24-02")]
    public Task F12_opens_the_template_editor_at_the_definition() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", Fixture("png-example.hxt")] });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "chunks[0].header.width" });
        await TemplateAsync(app, "focusTree");
        await app.KeyAsync("F12");
        JsonObject editor = [];
        await app.WaitUntilAsync(async () => (editor = await EditorAsync(app, "state"))["open"]!.GetValue<bool>(), Wait, "the template editor");
        Assert.EndsWith("png-example.hxt", editor["path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(19, editor["caretLine"]!.GetValue<int>());

        // Hex ビューで height (0x14) に置いて F12
        await app.SendAsync("closeToolPage", new JsonObject { ["id"] = "template:" + editor["path"]!.GetValue<string>() });
        await app.FocusAsync("HexView");
        await app.GoToAsync(0x14);
        await app.KeyAsync("F12");
        await app.WaitUntilAsync(async () => (editor = await EditorAsync(app, "state"))["open"]!.GetValue<bool>() && editor["caretLine"]!.GetValue<int>() == 20,
            Wait, "the height line");
    });

    // ───────────── TPL-27 ─────────────

    private static async Task<(AppSession App, int Index, JsonObject Row)> RuntimeErrorAsync(UiTestContext ctx)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")], ExtraArgs = ["--template", Fixture("runtime-err.hxt")] });
        await WaitTemplatesAsync(app);
        JsonArray rows = [];
        await app.WaitUntilAsync(async () => (rows = (await TemplateAsync(app, "output"))["rows"]!.AsArray()).Any(r => r!["severity"]!.GetValue<string>() == "Error"),
            Wait, "the error in the output panel");
        int index = rows.Select((r, i) => (r, i)).First(x => x.r!["severity"]!.GetValue<string>() == "Error").i;
        return (app, index, rows[index]!.AsObject());
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-27-03")]
    public Task Output_errors_go_to_the_source_line_and_the_data() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, int index, JsonObject row) = await RuntimeErrorAsync(ctx);
        Assert.Equal("Error", row["severityText"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(row["glyph"]!.GetValue<string>()));

        await TemplateAsync(app, "outputInvoke", new JsonObject { ["index"] = index, ["id"] = "TplOut_ToSource" });
        JsonObject editor = [];
        await app.WaitUntilAsync(async () => (editor = await EditorAsync(app, "state"))["open"]!.GetValue<bool>(), Wait, "the template editor");
        Assert.EndsWith("runtime-err.hxt", editor["path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, editor["caretLine"]!.GetValue<int>());

        await TemplateAsync(app, "outputInvoke", new JsonObject { ["index"] = index, ["id"] = "TplOut_ToData" });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0x40, Wait, "the cursor at 0x40");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-27-04")]
    public Task Error_codes_open_their_help_page_and_every_code_has_one() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, int index, JsonObject row) = await RuntimeErrorAsync(ctx);
        string code = row["code"]!.GetValue<string>();
        Assert.Matches("^HXT[0-9]{4}$", code);
        Assert.Contains(code, row["text"]!.GetValue<string>(), StringComparison.Ordinal);

        await TemplateAsync(app, "outputInvoke", new JsonObject { ["index"] = index, ["id"] = "TplOut_HelpItem" });
        string url = (await app.WaitForLogAsync(l => l.Contains("Test hooks: launch ", StringComparison.Ordinal), "the help page")).Split("Test hooks: launch ", 2)[1].Trim();
        Assert.Contains("template-errors.md#" + code.ToLowerInvariant(), url, StringComparison.Ordinal);

        // 3. すべてのコード (Core の TemplateMessages) に説明の節がある
        string source = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.Core", "Templates", "TemplateDiagnostics.cs"));
        string[] codes = [.. System.Text.RegularExpressions.Regex.Matches(source, "= \"(HXT[0-9]{4})\";").Select(m => m.Groups[1].Value).Distinct()];
        Assert.True(codes.Length > 50);
        string help = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "docs", "help", "template-errors.md"));
        string[] missing = [.. codes.Where(c => !help.Contains("\n### " + c + "\n", StringComparison.Ordinal))];
        Assert.True(missing.Length == 0, "No help section: " + string.Join(", ", missing));
    });

    // ───────────── TPL-30 ─────────────

    private const string SuggestNever = "Don't show for this format";

    private static Task<JsonObject> PngSuggestionAsync(AppSession app) =>
        app.WaitForNotificationAsync(m => m.Contains("PNG image", StringComparison.Ordinal), "the PNG suggestion");

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-30-03")]
    public Task Dont_show_for_this_format_is_remembered_and_can_be_undone_in_settings() => UiTestContext.RunAsync(async ctx =>
    {
        string asBin = ctx.TestData("TD-TPL-PNG-AS-BIN");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-PNG")] });
        await PngSuggestionAsync(app);
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = SuggestNever });
        await app.IdleAsync();

        await app.KeyAsync("W", ctrl: true);
        await app.OpenAsync(asBin);
        await app.WaitForTabsAsync(1);
        await Task.Delay(2000);
        Assert.DoesNotContain(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>().Contains("PNG image", StringComparison.Ordinal));

        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "templates" });
        await app.UiaInvokeAsync("TplMgr_Unsuppress_PNGimage");
        await app.IdleAsync();
        await app.SendAsync("closeToolPage", new JsonObject { ["id"] = "settings" });

        await app.KeyAsync("W", ctrl: true);
        await app.OpenAsync(asBin);
        await PngSuggestionAsync(app);
    });

    // ───────────── TPL-31 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-31-03")]
    public Task Try_run_updates_the_tree_without_saving() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.WriteFile("try.hxt", "u8 a;"u8.ToArray());
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await TemplateAsync(app, "removeAll");
        await EditorAsync(app, "open", new JsonObject { ["path"] = file });
        await EditorAsync(app, "setText", new JsonObject { ["text"] = "u16 a; u8 b;" });
        await EditorAsync(app, "target", new JsonObject { ["index"] = 0 });
        await EditorAsync(app, "tryRun");
        JsonObject state = await WaitTemplatesAsync(app);
        Assert.True(state["applications"]![0]!["unsaved"]!.GetValue<bool>());
        Assert.Equal("u16", (await RowAsync(app, "a"))["type"]!.GetValue<string>());
        await RowAsync(app, "b");
        state = await TemplateAsync(app, "state");
        Assert.Contains("(unsaved)", state["rows"]![0]!["name"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal("u8 a;", File.ReadAllText(file));
        Assert.True((await EditorAsync(app, "state"))["dirty"]!.GetValue<bool>());
    });

    // ───────────── TPL-32 ─────────────

    private static async Task<AppSession> DebugSessionAsync(UiTestContext ctx)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await TemplateAsync(app, "removeAll");
        await EditorAsync(app, "open", new JsonObject { ["path"] = Fixture("debug.hxt") });
        await EditorAsync(app, "target", new JsonObject { ["index"] = 0 });
        await EditorAsync(app, "focus");
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-32-05")]
    public Task Documents_cannot_be_edited_while_debugging() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await DebugSessionAsync(ctx);
        await EditorAsync(app, "breakpoint", new JsonObject { ["line"] = 7 });
        await EditorKeyAsync(app, "F5");
        JsonObject paused = await EditorAsync(app, "waitPaused");
        Assert.Equal(7, paused["debugLine"]!.GetValue<int>());

        await EditorAsync(app, "showDocument");
        await app.FocusAsync("HexView");
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        Assert.Equal(new byte[] { 0x00 }, await app.BytesAsync(0, 1));
        Assert.True((await app.DocumentAsync())["readOnly"]!.GetValue<bool>());

        await EditorAsync(app, "focus");
        await EditorKeyAsync(app, "F5", shift: true);
        await EditorAsync(app, "waitEnded");
        await EditorAsync(app, "showDocument");
        await app.FocusAsync("HexView");
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        Assert.Equal(new byte[] { 0xFF }, await app.BytesAsync(0, 1));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-32-06")]
    public Task Debugging_works_from_the_keyboard_and_function_keys_win_over_global_ones() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await DebugSessionAsync(ctx);

        // 1〜2. 8 行目に F9、F5
        await EditorAsync(app, "caret", new JsonObject { ["line"] = 8, ["column"] = 1 });
        Assert.Equal("codeEditor.toggleBreakpoint", (await EditorKeyAsync(app, "F9"))["handledBy"]!.GetValue<string>());
        await EditorKeyAsync(app, "F5");
        Assert.Equal(8, (await EditorAsync(app, "waitPaused"))["debugLine"]!.GetValue<int>());

        // 3. F11 は全画面表示ではなくステップイン
        JsonObject step = await EditorKeyAsync(app, "F11");
        Assert.Equal("codeEditor.stepInto", step["handledBy"]!.GetValue<string>());
        step = await EditorAsync(app, "waitPaused");
        Assert.False(step["fullScreen"]!.GetValue<bool>());
        Assert.Equal(2, step["debugLine"]!.GetValue<int>());

        // 4. Shift+F11 でトップレベルへ、5. F10 で次の文へ
        Assert.Equal("codeEditor.stepOut", (await EditorKeyAsync(app, "F11", shift: true))["handledBy"]!.GetValue<string>());
        int afterOut = (await EditorAsync(app, "waitPaused"))["debugLine"]!.GetValue<int>();
        Assert.InRange(afterOut, 8, 9);
        Assert.Equal("codeEditor.stepOver", (await EditorKeyAsync(app, "F10"))["handledBy"]!.GetValue<string>());
        int afterOver = (await EditorAsync(app, "waitPaused"))["debugLine"]!.GetValue<int>();
        Assert.True(afterOver > afterOut, $"{afterOut} -> {afterOver}");

        // 6. コマンドパレットの「ウォッチ式を追加」
        await TemplateAsync(app, "run", new JsonObject { ["id"] = "codeEditor.addWatch" });
        await app.WaitForDialogAsync("TemplateWatchDialog");
        await app.UiaSetValueAsync("TplEd_WatchDialogInput", "c * 3");
        await app.InvokeDialogButtonAsync("Add");
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["watches"]!.AsArray().Any(w => w!.GetValue<string>().Contains("c * 3", StringComparison.Ordinal)
            && w.GetValue<string>().Contains('6', StringComparison.Ordinal)), Wait, "the watch c * 3 = 6");

        // 7. 12 行目で「ここまで実行」、8. Shift+F5
        await EditorAsync(app, "caret", new JsonObject { ["line"] = 12, ["column"] = 1 });
        await TemplateAsync(app, "run", new JsonObject { ["id"] = "codeEditor.runToCursor" });
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["debugLine"]?.GetValue<int>() == 12, Wait, "the stop at line 12");
        Assert.Equal("codeEditor.stop", (await EditorKeyAsync(app, "F5", shift: true))["handledBy"]!.GetValue<string>());
        Assert.False((await EditorAsync(app, "waitEnded"))["debugging"]!.GetValue<bool>());
    });

    // ───────────── TPL-34 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-34-02")]
    public Task Executables_get_the_matching_standard_template_suggested() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-PE-X64")] });
        foreach ((string data, string name) in new[]
        {
            ("TD-PE-X64", "PE executable"), ("TD-ELF-X64", "ELF executable"), ("TD-ANA-MACHO-FAT", "Mach-O executable"), ("TD-TPL-MACHO64", "Mach-O executable"),
        })
        {
            if (data != "TD-PE-X64")
            {
                await app.OpenAsync(ctx.TestData(data));
            }

            await app.WaitUntilAsync(async () => (await TemplateAsync(app, "suggestion"))["top"]?.GetValue<string>() == name, Wait, name + " for " + data);
            await app.WaitForNotificationAsync(m => m.Contains(name, StringComparison.Ordinal), "the suggestion of " + name);
            await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Apply" });
        }
    });

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-TPL-34-03")]
    public Task Physical_disks_get_the_partition_table_template_suggested() => UiTestContext.RunAsync(async ctx =>
    {
        // CI で接続する仮想ディスクは TD-VHDX-MBR だけ。TD-VHDX-GPT は HEXEDITOR_TEST_VHD_GPT_DISK があるときだけ確かめる
        // (GPT が保護 MBR より上位になることは Core の TC-TPL-34-03 の検査で確かめている)。
        foreach ((string? disk, string name) in new[]
        {
            (CiPrivileged.VhdDisk.ToString(System.Globalization.CultureInfo.InvariantCulture), "MBR partition table"),
            (Environment.GetEnvironmentVariable("HEXEDITOR_TEST_VHD_GPT_DISK"), "GPT partition table"),
        })
        {
            if (string.IsNullOrEmpty(disk))
            {
                continue;
            }

            AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), ExtraArgs = ["--disk", disk] });
            await app.WaitUntilAsync(async () => (await TemplateAsync(app, "suggestion"))["top"]?.GetValue<string>() == name, Wait, name);
            await app.DisposeAsync();
        }
    });

    // ───────────── TPL-17 ─────────────

    [Theory]
    [InlineData("ja", "テスト用の画像形式")]
    [InlineData("en", "Test image format")]
    [InlineData("de", "Test image format")]
    [Trait(UiTest.TC, "TC-TPL-17-01")]
    public Task Template_names_follow_the_ui_language(string language, string expected) => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        Directory.CreateDirectory(Path.Combine(profile, "templates"));
        File.Copy(Fixture("name-ja.hxt"), Path.Combine(profile, "templates", "name-ja.hxt"));
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, UiLanguage = language, Files = [ctx.TestData("TD-BYTES-256")] });
        await TemplateAsync(app, "run", new JsonObject { ["id"] = "templates.apply" });
        await app.WaitForDialogAsync("TemplateApplyDialog");
        JsonArray items = [];
        await app.WaitUntilAsync(async () => (items = (await TemplateAsync(app, "applyDialog"))["items"]!.AsArray()).Count > 0, Wait, "the template list");
        Assert.Contains(items, i => i!.GetValue<string>() == expected);
        Assert.DoesNotContain(items, i => i!.GetValue<string>() == (language == "ja" ? "Test image format" : "テスト用の画像形式"));
        await app.KeyAsync("Escape");
    });

    // ───────────── 性能 ─────────────

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-TPL-21-02")]
    public Task Scrolling_a_million_open_rows_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "struct R { u8 a; u8 b; u8 c; }; R r[333334];");
        JsonObject expanded = await TemplateAsync(app, "expandAll", new JsonObject { ["path"] = "r" });
        Assert.True(expanded["rowCount"]!.GetValue<int>() > 1_000_000);
        JsonObject measured = await app.SendAsync("template", new JsonObject { ["action"] = "treeScrollMeasured", ["count"] = 200 }, TimeSpan.FromMinutes(5));
        IReadOnlyList<double> intervals = [.. measured["intervals"]!.AsArray().Select(v => v!.GetValue<double>())];
        output.WriteLine(FrameLog.Describe(intervals));
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= 16.7, FrameLog.Describe(intervals));
        Assert.All(measured["jumps"]!.AsArray(), j => Assert.True(j!.GetValue<double>() <= 100, $"{j} ms"));
    }, TimeSpan.FromMinutes(20));

    private static async Task<FrameLog> MeasureKeysAsync(UiTestContext ctx, AppSession app, Func<Task> keys)
    {
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        await Task.Delay(500);
        await keys();
        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        return FrameLog.Read(log);
    }

    private static Task KeyMeasuredAsync(AppSession app, string key, bool ctrl = false) =>
        app.SendAsync("keyMeasured", new JsonObject { ["key"] = key, ["ctrl"] = ctrl });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-TPL-23-05")]
    public Task Scrolling_with_ten_million_colored_patterns_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")], ExtraArgs = ["--template", Fixture("ten-million.hxt")] });
        await WaitTemplatesAsync(app);
        await app.FocusAsync("HexView");
        FrameLog log = await MeasureKeysAsync(ctx, app, async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                await KeyMeasuredAsync(app, "PageDown");
            }
        });
        IReadOnlyList<double> intervals = [.. log.Frames.Select(f => f.Interval)];
        output.WriteLine(FrameLog.Describe(intervals));
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= 16.7, FrameLog.Describe(intervals));

        FrameLog jumps = await MeasureKeysAsync(ctx, app, async () =>
        {
            await KeyMeasuredAsync(app, "End", ctrl: true);
            await Task.Delay(300);
            await KeyMeasuredAsync(app, "Home", ctrl: true);
        });
        Assert.All(jumps.KeyToFrame(), l => Assert.True(l <= 100, $"{l} ms"));
    }, TimeSpan.FromMinutes(10));

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-TPL-26-02")]
    public Task Cursor_moves_stay_fast_with_a_heavy_follow_type() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        await TemplateAsync(app, "removeAll");
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = Fixture("heavy.hxt"), ["follow"] = true, ["type"] = "Heavy", ["offset"] = 0 });
        await app.FocusAsync("HexView");
        FrameLog log = await MeasureKeysAsync(ctx, app, async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                await KeyMeasuredAsync(app, "Right");
                await Task.Delay(20);
            }
        });
        IReadOnlyList<double> latencies = log.KeyToFrame();
        Assert.Equal(100, latencies.Count);
        Assert.All(latencies, l => Assert.True(l <= 50, $"{l} ms"));

        var watch = Stopwatch.StartNew();
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await TemplateAsync(app, "state"))["applications"]![0]!["followOffset"]!.GetValue<long>() == 100
            && state["applications"]![0]!["completed"]!.GetValue<bool>(), Wait, "the follow result");
        output.WriteLine($"追従の結果まで {watch.ElapsedMilliseconds} ms");
        Assert.Contains("limit", state["status"]!.GetValue<string>(), StringComparison.Ordinal);
    }, TimeSpan.FromMinutes(5));

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-TPL-28-05")]
    public Task Hex_view_scrolling_keeps_sixty_fps_during_evaluation() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        await TemplateAsync(app, "removeAll");
        string file = ctx.WriteFile("heavy-all.hxt", System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Fixture("heavy.hxt")) + "\nHeavy h[while(!std::mem::eof())];\n"));
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = file });
        await app.FocusAsync("HexView");
        FrameLog log = await MeasureKeysAsync(ctx, app, async () =>
        {
            for (int i = 0; i < 300; i++)
            {
                await KeyMeasuredAsync(app, "PageDown");
            }
        });
        IReadOnlyList<double> intervals = [.. log.Frames.Select(f => f.Interval)];
        output.WriteLine(FrameLog.Describe(intervals));
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= 16.7, FrameLog.Describe(intervals));
        Assert.False((await TemplateAsync(app, "state"))["applications"]![0]!["completed"]!.GetValue<bool>());
    }, TimeSpan.FromMinutes(5));
}
