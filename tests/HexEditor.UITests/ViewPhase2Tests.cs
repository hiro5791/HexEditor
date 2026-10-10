using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;
using static HexEditor.UITests.Infrastructure.ViewSettingsOps;

namespace HexEditor.UITests;

/// <summary>
/// フェーズ 2 の表示: セルの表示形式 (VIEW-10)、エンディアンと逆順表示 (VIEW-11)、バイトテーマと色の重ね順 (VIEW-17)、レコード表示 (VIEW-18)、
/// 文字表 (VIEW-23)、複数のテキスト列 (VIEW-24)、区切り線 (VIEW-33)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewPhase2Tests
{
    private static async Task<AppSession> OpenAsync(UiTestContext ctx, string id, JsonObject? hooks = null, string? profile = null) =>
        await ctx.StartAsync(new AppOptions { Files = [ctx.TestData(id)], Hooks = hooks, Profile = profile });

    private static JsonObject Cell(JsonObject render, long offset) => CellOf(render, offset) ?? throw new InvalidOperationException($"cell {offset:X} is not shown");

    private static async Task ExecuteAsync(AppSession app, string id, string? argument = null)
    {
        await app.SendAsync("execute", new JsonObject { ["id"] = id, ["argument"] = argument, ["fromPalette"] = true });
        await app.IdleAsync();
    }

    private static async Task<JsonObject> GoAndRenderAsync(AppSession app, long offset)
    {
        await GoToAsync(app, "0x" + offset.ToString("X"));
        return await app.RenderAsync();
    }

    // ---- VIEW-10 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-10-02")]
    public Task Binary_int32_and_float_formats() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-PATTERNS");
        await MenuAsync(app, "Command_ViewCellFormatBinary");
        Assert.Equal("01001111", Cell(await app.RenderAsync(), 0x000)["hex"]!.GetValue<string>());

        await MenuAsync(app, "Command_ViewCellFormatInt32SignedDecimal");
        Assert.Equal("-0000000001", Cell(await app.RenderAsync(), 0x010)["hex"]!.GetValue<string>().Trim());

        await MenuAsync(app, "Command_ViewCellFormatFloat");
        Assert.Equal("NaN", Cell(await app.RenderAsync(), 0x020)["hex"]!.GetValue<string>().Trim());
        Assert.Equal("float", await app.UiaNameAsync("Status_Format"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-10-03")]
    public Task Trailing_bytes_are_hex_with_a_dotted_frame() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-LEN10");
        await MenuAsync(app, "Command_ViewCellFormatInt32Hex");
        JsonArray cells = (await app.RenderAsync())["rows"]![0]!["hexCells"]!.AsArray();
        Assert.Equal(["03020100", "07060504", "08", "09"], cells.Select(c => c!["text"]!.GetValue<string>().Trim()));
        Assert.Equal([4, 4, 1, 1], cells.Select(c => c!["bytes"]!.GetValue<int>()));
        Assert.Equal([null, null, "dotted", "dotted"], cells.Select(c => c!["frame"]?.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-10-04")]
    public Task Float_uses_a_period_in_french() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-PATTERNS", new JsonObject { ["culture"] = "fr-FR" });
        await MenuAsync(app, "Command_ViewCellFormatFloat");
        JsonObject render = await GoAndRenderAsync(app, 0x30);
        foreach (long offset in new long[] { 0x30, 0x34 })
        {
            string text = Cell(render, offset)["hex"]!.GetValue<string>();
            Assert.Contains('.', text);
            Assert.DoesNotContain(',', text);
        }
    });

    // ---- VIEW-11 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-11-01")]
    public Task Reversed_groups_do_not_change_the_saved_file() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-VIEW-PATTERNS");
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await MenuAsync(app, "Command_ViewGroup4");
        await MenuAsync(app, "Command_ViewReverseGroups");
        JsonObject render = await GoAndRenderAsync(app, 0x110);
        string line = Row(render, 0x110)!["line"]!.GetValue<string>();
        Assert.Contains("04030201 08070605", line);
        // テキスト列は逆順にしない (オフセット順に並ぶ)。
        Assert.True(Cell(render, 0x110)["textLeft"]!.GetValue<double>() < Cell(render, 0x113)["textLeft"]!.GetValue<double>());
        Assert.Equal("LE (reversed)", await app.UiaNameAsync("Status_Endian"));

        // 0x1F0 を AA にして保存し、元に戻して保存する。
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x1F0 });
        await app.TypeAsync("AA");
        await app.KeyAsync("S", ctrl: true);
        await app.IdleAsync();
        await app.KeyAsync("Z", ctrl: true);
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "saved");
        await ExitAsync(app);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-11-02")]
    public Task Right_arrow_follows_the_screen_order_when_reversed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-PATTERNS");
        await MenuAsync(app, "Command_ViewGroup4");
        await MenuAsync(app, "Command_ViewReverseGroups");
        await GoToAsync(app, "0x113");
        double x1 = (await app.RenderAsync())["caret"]!["left"]!.GetValue<double>();
        await app.KeyAsync("Right");
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        double x2 = render["caret"]!["left"]!.GetValue<double>();
        Assert.Equal(x1 + 2 * render["cellWidth"]!.GetValue<double>(), x2, 1);
        Assert.Equal("Offset: 0x00000112", await StatusOffsetAsync(app));
        await app.KeyAsync("Right", count: 3);
        await app.IdleAsync();
        Assert.Equal("Offset: 0x00000117", await StatusOffsetAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-11-03")]
    public Task Endian_status_toggles_16_bit_cells() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-PATTERNS");
        await MenuAsync(app, "Command_ViewCellFormatInt16Hex");
        Assert.Equal("1234", Cell(await GoAndRenderAsync(app, 0x120), 0x120)["hex"]!.GetValue<string>());
        Assert.Equal("LE", await app.UiaNameAsync("Status_Endian"));
        await app.UiaInvokeAsync("Status_Endian");
        await app.IdleAsync();
        Assert.Equal("3412", Cell(await app.RenderAsync(), 0x120)["hex"]!.GetValue<string>());
        Assert.Equal("BE", await app.UiaNameAsync("Status_Endian"));
        await app.UiaInvokeAsync("Status_Endian");
        await app.IdleAsync();
        Assert.Equal("1234", Cell(await app.RenderAsync(), 0x120)["hex"]!.GetValue<string>());
        Assert.Equal("LE", await app.UiaNameAsync("Status_Endian"));
    });

    // ---- VIEW-17 ----

    [Theory]
    [InlineData("Command_ThemeLight")]
    [InlineData("Command_ThemeDark")]
    [Trait(UiTest.TC, "TC-VIEW-17-01")]
    public Task Category_theme_colors_bytes_by_kind(string theme) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-BYTES-256");
        await MenuAsync(app, theme);
        await app.KeyAsync("End", ctrl: true);
        await MenuAsync(app, "Command_ViewByteThemeCategory");
        JsonObject render = await app.RenderAsync();
        string Fore(long o) => Cell(render, o)["foreground"]!.GetValue<string>();
        string[] samples = [Fore(0x00), Fore(0xFF), Fore(0x41), Fore(0x09), Fore(0x01), Fore(0x80)];
        Assert.Equal(6, samples.Distinct().Count());
        Assert.Single(Enumerable.Range(0x21, 0x7E - 0x21 + 1).Select(o => Fore(o)).Distinct());
        Assert.Single(Enumerable.Range(0x01, 8).Select(o => Fore(o)).Distinct());
        Assert.Single(Enumerable.Range(0x80, 0x7F).Select(o => Fore(o)).Distinct());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-02")]
    public Task Custom_theme_is_loaded_and_bad_json_is_reported() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-BYTES-256");
        await MenuAsync(app, "Command_ThemeLight");
        await app.KeyAsync("End", ctrl: true);
        await app.SendAsync("openPickerPath", new JsonObject { ["path"] = ctx.TestData("TD-VIEW-THEME-OK") });
        await MenuAsync(app, "Command_ViewByteThemeCustom");
        JsonObject render = await app.RenderAsync();
        string background = render["background"]!.GetValue<string>();
        Assert.Equal("#FF808080", Cell(render, 0x00)["foreground"]!.GetValue<string>());
        Assert.Equal("#FFC00000", Cell(render, 0xFF)["foreground"]!.GetValue<string>());
        Assert.Equal("#FF0050A0", Cell(render, 0x41)["foreground"]!.GetValue<string>());
        Assert.Equal("#FF000000", Cell(render, 0x0A)["foreground"]!.GetValue<string>());
        Assert.Equal("#FFFFE080", Cell(render, 0x0A)["hexBackground"]!.GetValue<string>());
        Assert.Equal(background, Cell(render, 0x41)["hexBackground"]!.GetValue<string>());
        Assert.Equal(render["hexTextColor"]!.GetValue<string>(), Cell(render, 0x80)["foreground"]!.GetValue<string>());

        await app.SendAsync("openPickerPath", new JsonObject { ["path"] = ctx.TestData("TD-VIEW-THEME-BAD") });
        await MenuAsync(app, "Command_ViewByteThemeCustom");
        string notice = (await NoticesAsync(app)).Last();
        Assert.Contains("4", notice);
        Assert.Contains("G0", notice);
        Assert.Equal("#FF808080", Cell(await app.RenderAsync(), 0x00)["foreground"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-04")]
    public Task Selected_bookmark_keeps_a_band() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await app.SendAsync("addHighlight", new JsonObject { ["offset"] = 0x20, ["length"] = 8, ["layer"] = 7, ["background"] = "#FFFFC0C0", ["tag"] = "bookmark" });
        await app.SelectAsync(0x10, 0x20);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        Assert.Equal(render["selectionColor"]!.GetValue<string>(), Cell(render, 0x22)["hexBackground"]!.GetValue<string>());
        JsonArray bands = (await app.SendAsync("highlightsView"))["bands"]!.AsArray();
        Assert.Contains(bands, b => b!["column"]!.GetValue<string>() == "hex" && b["first"]!.GetValue<long>() <= 0x22 && b["last"]!.GetValue<long>() >= 0x22
            && b["height"]!.GetValue<double>() == 2 && b["color"]!.GetValue<string>() == "#FFFFC0C0");
        Assert.DoesNotContain(bands, b => b!["first"]!.GetValue<long>() <= 0x12 && b["last"]!.GetValue<long>() >= 0x12);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-05")]
    public Task Change_underline_survives_a_color_rule() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x40 });
        await app.TypeAsync("AB");
        await app.SendAsync("addHighlight", new JsonObject { ["offset"] = 0x40, ["length"] = 1, ["layer"] = 10, ["background"] = "#FFFFFF00", ["border"] = "#FF000000", ["tag"] = "rule" });
        await app.SelectAsync(0x30, 0x20);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        JsonObject cell = Cell(render, 0x40);
        Assert.Equal(render["selectionColor"]!.GetValue<string>(), cell["hexBackground"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());
        JsonObject highlights = await app.SendAsync("highlightsView");
        Assert.Contains(highlights["bands"]!.AsArray(), b => b!["color"]!.GetValue<string>() == "#FFFFFF00" && b["first"]!.GetValue<long>() <= 0x40);
        Assert.Contains(highlights["segments"]!.AsArray(), s => s!["layer"]!.GetValue<int>() == 10 && s["border"]?.GetValue<string>() == "#FF000000");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-06")]
    public Task Hatch_is_drawn_over_the_bookmark_background() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M", new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject
            {
                ["match"] = "TD-SEQ-1M.bin",
                ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x1000", ["length"] = "0x1000" }),
            }),
        });
        await app.SendAsync("addHighlight", new JsonObject { ["offset"] = 0xFF8, ["length"] = 0x10, ["layer"] = 7, ["background"] = "#FFC0FFC0", ["tag"] = "bookmark" });
        await GoToAsync(app, "0xFF0");
        JsonObject render = null!;
        await app.WaitUntilAsync(async () => (CellOf(render = await app.RenderAsync(), 0x1004)?["hatched"]?.GetValue<bool>()) == true,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "unreadable cells");
        JsonObject unreadable = Cell(render, 0x1004);
        Assert.Equal("#FFC0FFC0", unreadable["highlightBackground"]!.GetValue<string>());
        Assert.Equal("??", unreadable["hex"]!.GetValue<string>());
        JsonObject readable = Cell(render, 0xFFC);
        Assert.Equal("#FFC0FFC0", readable["highlightBackground"]!.GetValue<string>());
        Assert.False(readable["hatched"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-07")]
    public Task Byte_theme_is_disabled_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-BYTES-256");
        await MenuAsync(app, "Command_ViewByteThemeCategory");
        await ForceHighContrastAsync(app);
        await app.KeyAsync("End", ctrl: true);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        string text = await SystemColorAsync(app, "SystemColorWindowTextColor");
        string gray = await SystemColorAsync(app, "SystemColorGrayTextColor");
        Assert.Equal(gray, Cell(render, 0)["foreground"]!.GetValue<string>());
        Assert.All(Enumerable.Range(1, 255), o => Assert.Equal(text, Cell(render, o)["foreground"]!.GetValue<string>()));
        await app.SendAsync("refreshMenus");
        Assert.Contains("high contrast", (await MenuItemAsync(app, "Command_ViewByteTheme"))["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-17-08")]
    public Task Low_contrast_text_is_replaced() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-BYTES-256");
        await MenuAsync(app, "Command_ThemeLight");
        await app.KeyAsync("End", ctrl: true);
        await app.SendAsync("openPickerPath", new JsonObject { ["path"] = ctx.TestData("TD-VIEW-THEME-LOWCONTRAST") });
        await MenuAsync(app, "Command_ViewByteThemeCustom");
        JsonObject render = await app.RenderAsync();
        string back41 = Cell(render, 0x41)["hexBackground"]!.GetValue<string>();
        string fore41 = Cell(render, 0x41)["foreground"]!.GetValue<string>();
        Assert.NotEqual("#FFF0F0F0", fore41);
        Assert.True(Contrast(fore41, back41) >= 4.5, $"{fore41} on {back41}");
        Assert.Equal("#FF0050A0", Cell(render, 0x42)["foreground"]!.GetValue<string>());
    });

    /// <summary>WCAG 2 のコントラスト比 (#AARRGGBB)。</summary>
    internal static double Contrast(string a, string b)
    {
        static double L(string c)
        {
            double Ch(int i)
            {
                double s = Convert.ToInt32(c.Substring(i, 2), 16) / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(3) + 0.7152 * Ch(5) + 0.0722 * Ch(7);
        }

        double la = L(a);
        double lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // ---- VIEW-18 ----

    private static async Task SetRecordsAsync(AppSession app, string length, string start, bool perRow = false, bool numbers = false)
    {
        JsonObject state = await app.SendAsync("recordSettings", new JsonObject
        {
            ["open"] = true, ["length"] = length, ["start"] = start, ["perRow"] = perRow, ["numbers"] = numbers, ["commit"] = true,
        });
        Assert.False(state["open"]!.GetValue<bool>(), state.ToJsonString());
        await app.IdleAsync();
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-01")]
    public Task Records_alternate_from_the_start_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await GoToAsync(app, "0x400");
        await app.KeyAsync("Home", ctrl: true);
        await app.KeyAsync("Down", count: 40);
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x400 });
        await app.KeyAsync("Up", ctrl: true, count: 64);
        await SetRecordsAsync(app, "100", "0x40");
        JsonObject render = await app.RenderAsync();
        string normal = render["background"]!.GetValue<string>();
        string alternate = render["recordAlternateColor"]!.GetValue<string>();
        string Back(long o) => Cell(render, o)["hexBackground"]!.GetValue<string>();
        Assert.All(Enumerable.Range(0, 0x40), o => Assert.Equal(normal, Back(o)));
        Assert.All(Enumerable.Range(0x40, 100), o => Assert.Equal(normal, Back(o)));
        Assert.All(Enumerable.Range(0xA4, 100), o => Assert.Equal(alternate, Back(o)));
        Assert.All(Enumerable.Range(0x108, 100), o => Assert.Equal(normal, Back(o)));
        Assert.All(Enumerable.Range(0x16C, 100), o => Assert.Equal(alternate, Back(o)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-02")]
    public Task One_record_per_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await SetRecordsAsync(app, "100", "0x40", perRow: true);
        JsonObject render = await app.RenderAsync();
        Assert.Equal(100, render["bytesPerRow"]!.GetValue<int>());
        JsonArray rows = render["rows"]!.AsArray();
        Assert.Equal(0x40, rows[1]!["rowStart"]!.GetValue<long>());
        Assert.Equal(0xA4, rows[2]!["rowStart"]!.GetValue<long>());
        Assert.Equal(0x108, rows[3]!["rowStart"]!.GetValue<long>());
        await app.SendAsync("refreshMenus");
        Assert.False((await MenuItemAsync(app, "Command_ViewBytesPerRow"))["enabled"]!.GetValue<bool>());
        JsonObject state = await app.SendAsync("recordSettings", new JsonObject { ["open"] = true, ["length"] = "4097" });
        Assert.False(state["perRowEnabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-03")]
    public Task Next_and_previous_record() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await SetRecordsAsync(app, "100", "0x40");
        await GoToAsync(app, "0x45");
        long[] expected = [0xA9, 0x10D];
        foreach (long e in expected)
        {
            await ExecuteAsync(app, "go.nextRecord");
            Assert.Equal(e, await CursorAsync(app));
        }

        foreach (long e in new long[] { 0xA9, 0x45, 0x45 })
        {
            await ExecuteAsync(app, "go.previousRecord");
            Assert.Equal(e, await CursorAsync(app));
        }

        await app.KeyAsync("End", ctrl: true);
        long end = await CursorAsync(app);
        await ExecuteAsync(app, "go.nextRecord");
        Assert.Equal(end, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-04")]
    public Task Record_boundaries_are_lines_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await ForceHighContrastAsync(app);
        await GoToAsync(app, "0x1000");
        await app.KeyAsync("Home", ctrl: true);
        await app.KeyAsync("Down", count: 10);
        await SetRecordsAsync(app, "32", "0");
        JsonObject render = await app.RenderAsync();
        string window = await SystemColorAsync(app, "SystemColorWindowColor");
        for (long row = 0; row < 4; row++)
        {
            Assert.All(Row(render, row * 16)!["cells"]!.AsArray(), c => Assert.Equal(window, c!["hexBackground"]!.GetValue<string>()));
        }

        bool Boundary(long rowStart) => Row(render, rowStart)!["lines"]!.AsArray().Any(l => l!["kind"]!.GetValue<string>() == "recordBoundary");
        Assert.True(Boundary(0x20));
        Assert.True(Boundary(0x40));
        Assert.False(Boundary(0x10));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-05")]
    public Task Status_bar_shows_the_record() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await SetRecordsAsync(app, "100", "0x40");
        await GoToAsync(app, "0x10D");
        Assert.Equal("Record #2 +0x05", await app.UiaNameAsync("Status_Position"));
        await MenuAsync(app, "Command_ViewRecords");
        Assert.False(await app.IsShownAsync("Status_Position"));
    });

    /// <summary>VIEW-18 の「画面」: フライアウトの入力はその場で反映する (OK を押さなくてよい)。仕様 7: 位置は 8 進にも従う。</summary>
    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-18-05")]
    public Task Record_settings_apply_while_typing_and_status_follows_octal() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        JsonObject state = await app.SendAsync("recordSettings", new JsonObject { ["open"] = true, ["length"] = "100", ["start"] = "0x40" });
        Assert.True(state["open"]!.GetValue<bool>());
        await app.IdleAsync();
        JsonObject view = (await app.SendAsync("viewSettings"))["view"]!.AsObject();
        Assert.True(view["recordView"]!.GetValue<bool>());
        Assert.Equal(100, view["recordLength"]!.GetValue<int>());
        Assert.Equal(0x40, view["recordStart"]!.GetValue<long>());

        // 不正な値は反映しない。
        await app.SendAsync("recordSettings", new JsonObject { ["length"] = "0" });
        Assert.Equal(100, (await app.SendAsync("viewSettings"))["view"]!["recordLength"]!.GetValue<int>());
        await app.SendAsync("recordSettings", new JsonObject { ["length"] = "100", ["commit"] = true });

        await GoToAsync(app, "0x10D");
        await ExecuteAsync(app, "view.radixOctal");
        Assert.Equal("Record #2 +0o5", await app.UiaNameAsync("Status_Position"));
    });

    // ---- VIEW-23 ----

    private static async Task LoadTableAsync(AppSession app, UiTestContext ctx)
    {
        await app.SendAsync("openPickerPath", new JsonObject { ["path"] = ctx.TestData("TD-VIEW-TBL") });
        await MenuAsync(app, "Command_ViewLoadTable");
    }

    private static void AssertTableCells(JsonObject render)
    {
        JsonObject first = Cell(render, 0);
        Assert.Equal("あ", first["glyph"]?.GetValue<string>());
        Assert.Equal(2 * render["cellWidth"]!.GetValue<double>(), first["glyphWidth"]!.GetValue<double>(), 1);
        Assert.Equal("A", Cell(render, 2)["text"]!.GetValue<string>());
        Assert.Equal(".", Cell(render, 3)["text"]!.GetValue<string>());
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-23-01")]
    public Task Table_decodes_by_longest_match() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-TBL-DATA");
        await LoadTableAsync(app, ctx);
        AssertTableCells(await app.RenderAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-23-02")]
    public Task Invalid_table_lines_are_listed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-TBL-DATA");
        await LoadTableAsync(app, ctx);
        string notice = (await NoticesAsync(app)).Last();
        Assert.Contains("4 entries", notice);
        Assert.Contains("3 lines ignored", notice);
        Assert.Contains("line 5", notice);
        Assert.Contains("line 6", notice);
        Assert.Contains("line 7", notice);
        Assert.DoesNotContain("line 1 ", notice);
        Assert.DoesNotContain("line 8", notice);
        AssertTableCells(await app.RenderAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-23-03")]
    public Task Loaded_table_can_be_chosen_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string table = ctx.CopyTestData("TD-VIEW-TBL");
        string data = ctx.TestData("TD-VIEW-TBL-DATA");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [data], Profile = profile });
        await app.SendAsync("openPickerPath", new JsonObject { ["path"] = table });
        await MenuAsync(app, "Command_ViewLoadTable");
        await ExitAsync(app);
        File.Delete(table);

        AppSession again = await ctx.StartAsync(new AppOptions { Files = [data], Profile = profile });
        await again.SendAsync("viewSettings");
        JsonObject list = await again.SendAsync("encodingList", new JsonObject { ["open"] = true });
        string id = "tbl:" + Path.GetFileName(table);
        Assert.Contains(list["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == id);
        await again.SendAsync("encodingList", new JsonObject { ["choose"] = id });
        await again.IdleAsync();
        AssertTableCells(await again.RenderAsync());
    });

    // ---- VIEW-24 ----

    private static async Task SetEncodingAsync(AppSession app, string id)
    {
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.encoding.select", ["argument"] = id });
        await app.IdleAsync();
    }

    private static async Task AddTextColumnAsync(AppSession app, string encoding, bool odd = false)
    {
        await MenuAsync(app, "Command_ViewAddTextColumn");
        await app.SendAsync("encodingList", new JsonObject { ["choose"] = encoding });
        await app.IdleAsync();
        if (odd)
        {
            await MenuAsync(app, "Command_ViewUtf16Odd");
        }
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-24-01")]
    public Task Shifted_utf16_columns() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-VIEW-PATTERNS");
        await SetEncodingAsync(app, "utf-16le");
        await AddTextColumnAsync(app, "utf-16le", odd: true);
        JsonObject render = await GoAndRenderAsync(app, 0xE0);
        JsonArray headers = render["ruler"]!["textHeaders"]!.AsArray();
        Assert.Equal(["UTF-16 LE (even)", "UTF-16 LE (odd)"], headers.Select(h => h!["name"]!.GetValue<string>()));
        string Text(int column) => string.Concat(Enumerable.Range(0xE1, 10).Select(o => Cell(render, o)["texts"]![column]!["text"]!.GetValue<string>()))
            .Replace(" ", string.Empty);
        Assert.Equal("Hello", Text(1));
        Assert.NotEqual("Hello", Text(0));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-24-02")]
    public Task Adding_is_disabled_with_five_columns() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        foreach (string encoding in new[] { "cp1252", "cp437", "utf-8", "cp37" })
        {
            await AddTextColumnAsync(app, encoding);
        }

        await app.SendAsync("refreshMenus");
        Assert.False((await MenuItemAsync(app, "Command_ViewAddTextColumn"))["enabled"]!.GetValue<bool>());
        await ExecuteAsync(app, "view.addTextColumn");
        Assert.Equal(5, (await app.RenderAsync())["textColumns"]!.AsArray().Count);
        await ExecuteAsync(app, "view.removeTextColumn");
        await app.SendAsync("refreshMenus");
        Assert.True((await MenuItemAsync(app, "Command_ViewAddTextColumn"))["enabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-24-03")]
    public Task Tab_moves_through_each_text_column() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await AddTextColumnAsync(app, "utf-8");
        await AddTextColumnAsync(app, "cp37");
        await app.SendAsync("click", new JsonObject { ["offset"] = 0, ["column"] = "Hex" });
        string[] expected = ["text1:ASCII", "text2:UTF-8", "text3:037", "hex:037"];
        foreach (string e in expected)
        {
            await app.KeyAsync("Tab");
            await app.IdleAsync();
            string active = (await app.RenderAsync())["activeColumn"]!.GetValue<string>();
            Assert.Equal(e, active + ":" + await app.UiaNameAsync("Status_Encoding"));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-24-04")]
    public Task Text_columns_are_restored() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await SetEncodingAsync(app, "cp1252");
        await AddTextColumnAsync(app, "utf-16be", odd: true);
        await AddTextColumnAsync(app, "cp932");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.OpenAsync(path);
        await app.IdleAsync();
        JsonArray columns = (await app.RenderAsync())["textColumns"]!.AsArray();
        Assert.Equal(["cp1252", "utf-16be", "cp932"], columns.Select(c => c!["encoding"]!.GetValue<string>()));
        Assert.Equal("UTF-16 BE (odd)", columns[1]!["name"]!.GetValue<string>());
    });

    // ---- VIEW-33 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-33-01")]
    public Task Page_separator_lines_and_labels() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await MenuAsync(app, "Command_ViewSeparatorPage");
        JsonObject render = await GoAndRenderAsync(app, 0x2FF0);
        Assert.Equal("Page 3 (0x3000)", Row(render, 0x3000)!["separator"]!.GetValue<string>());
        Assert.Null(Row(render, 0x2FF0)!["separator"]);
        Assert.Contains(Row(render, 0x3000)!["lines"]!.AsArray(), l => l!["kind"]!.GetValue<string>() == "separator");
        Assert.Equal("Page 2 / 256", await app.UiaNameAsync("Status_Position"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-33-02")]
    public Task Page_is_not_available_with_24_bytes_per_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await SetBytesPerRowAsync(app, 24);
        await app.SendAsync("refreshMenus");
        JsonObject page = await MenuItemAsync(app, "Command_ViewSeparatorPage");
        Assert.False(page["enabled"]!.GetValue<bool>());
        Assert.Contains("24", page["toolTip"]!.GetValue<string>());
        await MenuAsync(app, "Command_ViewSeparatorCustom");
        JsonObject state = await InputAsync(app, "4096");
        Assert.True(state["invalid"]!.GetValue<bool>());
        Assert.Contains("24", state["error"]!.GetValue<string>());
        state = await InputAsync(app, "4104");
        Assert.False(state["invalid"]!.GetValue<bool>());
        Assert.False((await InputAsync(app, null, commit: true))["open"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-33-03")]
    public Task Page_down_in_page_view_stays_in_one_page() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenAsync(ctx, "TD-SEQ-1M");
        await MenuAsync(app, "Command_ViewSeparatorPage");
        await MenuAsync(app, "Command_ViewPageView");
        long last = -1;
        for (int i = 0; i < 30; i++)
        {
            await app.KeyAsync("PageDown");
            await app.IdleAsync();
            JsonObject render = await app.RenderAsync();
            long cursor = await CursorAsync(app);
            Assert.True(cursor > last);
            last = cursor;
            long[] starts = [.. render["rows"]!.AsArray().Select(r => r!["rowStart"]!.GetValue<long>())];
            Assert.Single(starts.Select(s => s / 4096).Distinct());
            long top = starts.Min();
            long page = top / 4096 * 4096;
            Assert.InRange(top, page, page + 4096 - render["visibleRows"]!.GetValue<int>() * 16);
        }
    });
}
