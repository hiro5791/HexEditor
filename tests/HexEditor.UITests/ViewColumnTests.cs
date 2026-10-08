using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;
using static HexEditor.UITests.Infrastructure.ViewSettingsOps;

namespace HexEditor.UITests;

/// <summary>
/// 列の構成 (VIEW-05 列見出し、VIEW-06 現在行・対応位置、VIEW-08 1 行のバイト数、VIEW-09 グループ化、VIEW-12〜VIEW-16)。
/// ハイコントラストの手順は、OS の設定を変えずにハイコントラストの描き方 (システム色だけ) を模擬して確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewColumnTests
{
    private static JsonArray HexLabels(JsonObject render) =>
        new([.. render["ruler"]!["labels"]!.AsArray().Where(l => l!["column"]!.GetValue<string>() == "hex").Select(l => l!.DeepClone())]);

    private static JsonObject FirstRow(JsonObject render) => render["rows"]!.AsArray().First(r => r!["rowStart"]!.GetValue<long>() <= 0)!.AsObject();

    // ---- VIEW-05 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-05-01")]
    public Task Ruler_labels_sit_above_the_cells() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        JsonObject render = await app.RenderAsync();
        JsonArray labels = HexLabels(render);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => i.ToString("X2")), labels.Select(l => l!["text"]!.GetValue<string>()));
        JsonArray cells = FirstRow(render)["cells"]!.AsArray();
        for (int c = 0; c < 16; c++)
        {
            double left = cells[c]!["hexLeft"]!.GetValue<double>();
            Assert.Equal(left, labels[c]!["left"]!.GetValue<double>(), 3);
            Assert.Equal(left + 2 * render["cellWidth"]!.GetValue<double>(), labels[c]!["right"]!.GetValue<double>(), 3);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-05-02")]
    public Task Ruler_shows_group_starts_only() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await MenuAsync(app, "Command_ViewGroup4");
        JsonObject render = await app.RenderAsync();
        JsonArray labels = HexLabels(render);
        Assert.Equal(["00", "04", "08", "0C"], labels.Select(l => l!["text"]!.GetValue<string>()));
        JsonArray cells = FirstRow(render)["cells"]!.AsArray();
        for (int g = 0; g < 4; g++)
        {
            Assert.Equal(cells[g * 4]!["hexLeft"]!.GetValue<double>(), labels[g]!["left"]!.GetValue<double>(), 3);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-05-03")]
    public Task Ruler_stays_on_vertical_scroll_and_follows_horizontal_scroll() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.ResizeAsync(1024, 768);
        await SetBytesPerRowAsync(app, 256);
        JsonObject before = await app.RenderAsync();
        double top = before["ruler"]!["top"]!.GetValue<double>();
        double cell = before["cellWidth"]!.GetValue<double>();
        string firstLabels = string.Join(' ', HexLabels(before).Select(l => l!["text"]!.GetValue<string>()));

        // 2〜3. 縦スクロールしても見出しは動かない。
        await app.KeyAsync("Down", ctrl: true, count: 20);
        await app.IdleAsync();
        JsonObject scrolled = await app.RenderAsync();
        Assert.True(scrolled["topRow"]!.GetValue<long>() > 0);
        Assert.Equal(top, scrolled["ruler"]!["top"]!.GetValue<double>(), 3);
        Assert.Equal(firstLabels, string.Join(' ', HexLabels(scrolled).Select(l => l!["text"]!.GetValue<string>())));

        // 4〜5. 横スクロールには追従する: 3 ノッチ × 3 セル = 9 セル分。見出しの 00 とセルの位置が一致する。
        await WheelAsync(app, -120, count: 3, shift: true);
        await app.IdleAsync();
        JsonObject shifted = await app.RenderAsync();
        Assert.Equal(-9 * cell, shifted["ruler"]!["shift"]!.GetValue<double>(), 1);
        Assert.Equal(9 * cell, shifted["horizontalOffset"]!.GetValue<double>(), 1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-05-04")]
    public Task Offset_header_switches_the_radix() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();
        Assert.Equal("Offset (h)", render["ruler"]!["offsetHeader"]!.GetValue<string>());

        // 2. 見出しを Invoke パターンで押すと、基数を選ぶメニューが出る。
        await app.UiaInvokeAsync("HexView_OffsetHeader");
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["ruler"]!["radixMenuOpen"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the radix menu");

        // 3〜4. 10 進を選ぶ。
        await app.SendAsync("chooseRadix", new JsonObject { ["radix"] = "Decimal" });
        await app.IdleAsync();
        render = await app.RenderAsync();
        Assert.Equal("Offset (d)", render["ruler"]!["offsetHeader"]!.GetValue<string>());
        string second = render["rows"]![1]!["offsetText"]!.GetValue<string>();
        Assert.Equal("16", second.Trim());
        Assert.True(second.Length > 2 && second.StartsWith(' '), $"right aligned: '{second}'");
    });

    // ---- VIEW-06 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-06-01")]
    public Task Current_row_and_ruler_highlight_follow_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await AssertCurrentAsync(app, 0, "00");
        await app.KeyAsync("Down", count: 2);
        await app.KeyAsync("Right", count: 3);
        await app.IdleAsync();
        Assert.Equal(0x23, await CursorAsync(app));
        await AssertCurrentAsync(app, 0x20, "03");

        static async Task AssertCurrentAsync(AppSession app, long rowStart, string label)
        {
            JsonObject render = await app.RenderAsync();
            long[] current = [.. render["rows"]!.AsArray().Where(r => r!["currentRow"]!.GetValue<bool>()).Select(r => r!["rowStart"]!.GetValue<long>())];
            Assert.Equal([rowStart], current);
            JsonObject row = Row(render, rowStart)!;
            Assert.Equal("currentRow", row["cells"]![8]!["hexLayer"]!.GetValue<string>());
            string[] highlighted = [.. HexLabels(render).Where(l => l!["highlighted"]!.GetValue<bool>()).Select(l => l!["text"]!.GetValue<string>())];
            Assert.Equal([label], highlighted);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-06-02")]
    public Task Other_column_shows_a_frame_and_tab_swaps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.KeyAsync("Right", count: 5);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        JsonObject cell = CellOf(render, 5)!;

        // 2. Hex 列に帯 (上位ニブルの 1 文字目)、テキスト列に細い枠線。
        Assert.Equal(cell["hexLeft"]!.GetValue<double>(), render["caret"]!["left"]!.GetValue<double>(), 1);
        Assert.Equal(render["cellWidth"]!.GetValue<double>(), render["caret"]!["width"]!.GetValue<double>(), 1);
        Assert.Equal(cell["textLeft"]!.GetValue<double>(), render["secondaryCaret"]!["left"]!.GetValue<double>(), 1);
        Assert.Equal(1, render["secondaryCaret"]!["strokeThickness"]!.GetValue<double>());

        // 3〜4. Tab でテキスト列へ: テキスト列に帯、Hex 列のセル全体 (2 文字) に枠線。
        await app.KeyAsync("Tab");
        await app.IdleAsync();
        render = await app.RenderAsync();
        Assert.Equal(cell["textLeft"]!.GetValue<double>(), render["caret"]!["left"]!.GetValue<double>(), 1);
        Assert.Equal(cell["hexLeft"]!.GetValue<double>(), render["secondaryCaret"]!["left"]!.GetValue<double>(), 1);
        Assert.Equal(2 * render["cellWidth"]!.GetValue<double>(), render["secondaryCaret"]!["width"]!.GetValue<double>(), 1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-06-03")]
    public Task Current_row_is_lines_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await ForceHighContrastAsync(app);
        await app.KeyAsync("Down", count: 3);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        string window = await SystemColorAsync(app, "SystemColorWindowColor");
        JsonObject row = Row(render, 0x30)!;

        // 背景は他の行と同じ Window、上端と下端に 1 px の線。
        Assert.All(row["cells"]!.AsArray(), c => Assert.Equal(window, c!["hexBackground"]!.GetValue<string>()));
        string[] lines = [.. row["lines"]!.AsArray().Select(l => l!["kind"]!.GetValue<string>())];
        Assert.Contains("currentRowTop", lines);
        Assert.Contains("currentRowBottom", lines);
        Assert.Empty(Row(render, 0x20)!["lines"]!.AsArray());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-06-04")]
    public Task Caret_shape_follows_the_input_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.FocusAsync("HexView");
        await app.IdleAsync();
        JsonObject caret = (await app.RenderAsync())["caret"]!.AsObject();
        Assert.Equal("box", caret["shape"]!.GetValue<string>());
        Assert.True(caret["filled"]!.GetValue<bool>(), "overwrite mode: filled band");

        await app.KeyAsync("Insert");
        await app.IdleAsync();
        Assert.Equal("bar", (await app.RenderAsync())["caret"]!["shape"]!.GetValue<string>());
        Assert.Equal("Insert", await app.UiaNameAsync("Status_Mode"));

        await app.KeyAsync("Tab");
        await app.IdleAsync();
        Assert.Equal("bar", (await app.RenderAsync())["caret"]!["shape"]!.GetValue<string>());
    });

    // ---- VIEW-08 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-08-01")]
    public Task Bytes_per_row_accepts_1_to_4096() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        foreach (int n in new[] { 1, 16, 4096 })
        {
            await SetBytesPerRowAsync(app, n);
            JsonObject render = await app.RenderAsync();
            Assert.Equal(n, render["bytesPerRow"]!.GetValue<int>());
            JsonArray rows = render["rows"]!.AsArray();
            Assert.Equal(n, rows[1]!["rowStart"]!.GetValue<long>());
            JsonArray cells = rows[0]!["cells"]!.AsArray();
            Assert.Equal(n, cells.Count);
            for (int c = 0; c < Math.Min(n, 300); c++)
            {
                Assert.Equal((c % 256).ToString("X2"), cells[c]!["hex"]!.GetValue<string>());
            }
        }

        // 4〜5. 4097 と 0 は確定できない (赤枠・説明文・確定ボタンが無効)。
        foreach (string bad in new[] { "4097", "0" })
        {
            await MenuAsync(app, "Command_ViewBytesPerRowCustom");
            JsonObject state = await InputAsync(app, bad);
            Assert.True(state["invalid"]!.GetValue<bool>());
            Assert.False(state["okEnabled"]!.GetValue<bool>());
            Assert.Contains("4,096", state["error"]!.GetValue<string>());
            state = await InputAsync(app, null, commit: true);
            Assert.True(state["open"]!.GetValue<bool>());
            Assert.Equal(4096, (await app.RenderAsync())["bytesPerRow"]!.GetValue<int>());

            // 入力欄を閉じる (正しい値で確定し直す)。
            Assert.False((await InputAsync(app, "4096", commit: true))["open"]!.GetValue<bool>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-08-02")]
    public Task Auto_bytes_per_row_follows_the_window_width() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewGroup4");
        await MenuAsync(app, "Command_ViewBytesPerRowAuto");
        int previous = 0;
        foreach (int width in new[] { 1024, 1400, 1920 })
        {
            await app.ResizeAsync(width, 800);
            await Task.Delay(300);
            await app.IdleAsync();
            JsonObject render = await app.RenderAsync();
            int b = render["bytesPerRow"]!.GetValue<int>();
            Assert.Equal(0, b % 4);
            Assert.True(b >= previous, $"{width}: {b} < {previous}");
            previous = b;

            // 1 行のバイト数 + 4 では Hex 表示部分に収まらない (グループ化 4・中央区切りの文字数で計算する)。
            double cell = render["cellWidth"]!.GetValue<double>();
            double available = render["surfaceWidth"]!.GetValue<double>() - render["contentLeft"]!.GetValue<double>();
            int n = b + 4;
            int chars = 2 * n + (n - 1) / 4 + (n >= 16 ? (n - 1) / 8 : 0) + 2 + 2 + n;
            Assert.True((chars + 1) * cell > available, $"{width}: {n} bytes would fit ({chars} chars, {available} px)");
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-08-03")]
    public Task Changing_bytes_per_row_keeps_the_cursor_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x12345");
        double y = (await app.RenderAsync())["caret"]!["top"]!.GetValue<double>();
        foreach (int n in new[] { 32, 8, 64 })
        {
            await MenuAsync(app, "Command_ViewBytesPerRow" + n);
            Assert.Equal(0x12345, await CursorAsync(app));
            Assert.Equal(y, (await app.RenderAsync())["caret"]!["top"]!.GetValue<double>(), 1);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-08-04")]
    public Task Bytes_per_row_must_be_a_multiple_of_the_group() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewGroup8");
        await MenuAsync(app, "Command_ViewBytesPerRowCustom");
        JsonObject state = await InputAsync(app, "12");
        Assert.True(state["invalid"]!.GetValue<bool>());
        Assert.False(state["okEnabled"]!.GetValue<bool>());
        Assert.Contains("8", state["error"]!.GetValue<string>());
        await InputAsync(app, null, commit: true);
        Assert.Equal(16, (await app.RenderAsync())["bytesPerRow"]!.GetValue<int>());
    });

    // ---- VIEW-09 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-09-01")]
    public Task Grouping_by_four_packs_the_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-VIEW-PATTERNS")] });
        await MenuAsync(app, "Command_ViewGroup4");
        await GoToAsync(app, "0x100");
        JsonObject render = await app.RenderAsync();
        string line = Row(render, 0x100)!["line"]!.GetValue<string>();
        Assert.Contains("00000100  DEADBEEF 01020304", line);

        // Text パターンの文字列も画面と同じ書式。
        string text = app.Find("HexView")!.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        Assert.Contains("DEADBEEF 01020304", text);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-09-02")]
    public Task Grouping_does_not_change_the_text_column() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-TEXT-ASCII")] });
        (string Text, double[] Gaps) Read(JsonObject render) =>
        (
            string.Join('\n', render["rows"]!.AsArray().Select(r => string.Concat(r!["cells"]!.AsArray().Select(c => c!["text"]!.GetValue<string>())))),
            [.. render["rows"]![0]!["cells"]!.AsArray().Zip(render["rows"]![0]!["cells"]!.AsArray().Skip(1))
                .Select(p => Math.Round(p.Second!["textLeft"]!.GetValue<double>() - p.First!["textLeft"]!.GetValue<double>(), 2))]
        );
        (string text, double[] gaps) = Read(await app.RenderAsync());
        foreach (int g in new[] { 2, 4, 8, 16 })
        {
            await MenuAsync(app, "Command_ViewGroup" + g);
            (string t, double[] gp) = Read(await app.RenderAsync());
            Assert.Equal(text, t);
            Assert.Equal(gaps, gp);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-09-03")]
    public Task Larger_group_rounds_bytes_per_row_up() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewGroup4");
        await SetBytesPerRowAsync(app, 12);
        await MenuAsync(app, "Command_ViewGroup8");
        Assert.Equal(16, (await app.RenderAsync())["bytesPerRow"]!.GetValue<int>());
        Assert.Contains(await NoticesAsync(app), n => n.Contains("16", StringComparison.Ordinal) && n.Contains("row", StringComparison.OrdinalIgnoreCase));
    });

    // ---- VIEW-12 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-12-01")]
    public Task Lowercase_hex_keeps_0x() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0xABCDE");
        await MenuAsync(app, "Command_ViewLowercase");
        JsonObject render = await app.RenderAsync();
        JsonObject row = RowOf(render, 0xABCD0)!;
        Assert.Equal("000abcd0", row["offsetText"]!.GetValue<string>());
        Assert.Equal("de", row["cells"]![14]!["hex"]!.GetValue<string>());
        Assert.Contains("0x000abcde", await StatusOffsetAsync(app));
        Assert.DoesNotContain("0X", await StatusOffsetAsync(app));
        Assert.Contains("de", await app.UiaNameAsync("Status_Value"));

        // 4. 移動バーの解釈結果も小文字 (0x の x は変わらない)。
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0xFF");
        await app.IdleAsync();
        Assert.Contains("0xff", await app.UiaNameAsync("GoTo_Interpretation"));
    });

    // ---- VIEW-13 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-13-01")]
    public Task Zero_bytes_are_dimmed_and_the_setting_turns_it_off() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.ResizeAsync(1280, 800);
        await GoToAsync(app, "0x10");
        JsonObject render = await app.RenderAsync();
        string dim = render["dimColor"]!.GetValue<string>();
        JsonObject zero = CellOf(render, 0)!;
        JsonObject one = CellOf(render, 1)!;
        Assert.Equal(dim, zero["foreground"]!.GetValue<string>());
        Assert.Equal(dim, zero["textForeground"]!.GetValue<string>());
        Assert.NotEqual(dim, one["foreground"]!.GetValue<string>());

        await MenuAsync(app, "Command_ViewDimZeros");
        render = await app.RenderAsync();
        Assert.Equal(CellOf(render, 1)!["foreground"]!.GetValue<string>(), CellOf(render, 0)!["foreground"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-13-02")]
    public Task Modified_zero_uses_the_modified_color() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await GoToAsync(app, "0x5");
        await app.TypeAsync("00");
        await GoToAsync(app, "0x20");
        JsonObject render = await app.RenderAsync();
        JsonObject cell = CellOf(render, 5)!;
        Assert.Equal(render["modifiedColor"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());
        Assert.NotEqual(render["dimColor"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-13-03")]
    public Task Zero_is_gray_text_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await ForceHighContrastAsync(app);
        await GoToAsync(app, "0x10");
        JsonObject render = await app.RenderAsync();
        Assert.Equal(await SystemColorAsync(app, "SystemColorGrayTextColor"), CellOf(render, 0)!["foreground"]!.GetValue<string>());
        Assert.Equal(await SystemColorAsync(app, "SystemColorWindowTextColor"), CellOf(render, 1)!["foreground"]!.GetValue<string>());
    });

    // ---- VIEW-14 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-14-01")]
    public Task Alternate_background_per_group_of_four() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewGroup4");
        await app.GoToAsync(0x100);
        await MenuAsync(app, "Command_ViewAlternate");
        JsonObject render = await app.RenderAsync();
        string alternate = render["alternateColor"]!.GetValue<string>();
        string normal = render["background"]!.GetValue<string>();
        foreach (long rowStart in new[] { 0x10L, 0x20L })
        {
            JsonArray cells = Row(render, rowStart)!["cells"]!.AsArray();
            for (int c = 0; c < 16; c++)
            {
                bool odd = c / 4 % 2 == 1;
                Assert.Equal(odd ? alternate : normal, cells[c]!["hexBackground"]!.GetValue<string>());
                Assert.Equal(normal, cells[c]!["textBackground"]!.GetValue<string>());
            }
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-14-02")]
    public Task Alternate_background_is_dotted_lines_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await ForceHighContrastAsync(app);
        await MenuAsync(app, "Command_ViewGroup4");
        await MenuAsync(app, "Command_ViewAlternate");
        JsonObject render = await app.RenderAsync();
        string window = await SystemColorAsync(app, "SystemColorWindowColor");
        JsonObject row = Row(render, 0x10)!;
        Assert.All(row["cells"]!.AsArray(), c => Assert.Equal(window, c!["hexBackground"]!.GetValue<string>()));
        double cell = render["cellWidth"]!.GetValue<double>();
        double[] xs = [.. row["lines"]!.AsArray().Where(l => l!["kind"]!.GetValue<string>() == "groupSeparator").Select(l => l!["x1"]!.GetValue<double>())];
        JsonArray cells = row["cells"]!.AsArray();
        Assert.Equal(3, xs.Length);
        for (int i = 0; i < 3; i++)
        {
            int c = (i + 1) * 4;
            double between = (cells[c - 1]!["hexLeft"]!.GetValue<double>() + 2 * cell + cells[c]!["hexLeft"]!.GetValue<double>()) / 2;
            Assert.Equal(between, xs[i], 0);
        }
    });

    // ---- VIEW-15 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-15-01")]
    public Task Overwritten_byte_is_highlighted_until_undo() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x40");
        await app.TypeAsync("AB");
        await GoToAsync(app, "0x80");
        JsonObject render = await app.RenderAsync();
        string modified = render["modifiedColor"]!.GetValue<string>();
        JsonObject cell = CellOf(render, 0x40)!;
        Assert.Equal(modified, cell["foreground"]!.GetValue<string>());
        Assert.Equal(modified, cell["textForeground"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        cell = CellOf(await app.RenderAsync(), 0x40)!;
        Assert.NotEqual(modified, cell["foreground"]!.GetValue<string>());
        Assert.Equal("none", cell["underline"]!.GetValue<string>());

        // 5. 元と同じ値で上書きしても変更として示す。
        await GoToAsync(app, "0x41");
        await app.TypeAsync("41");
        await GoToAsync(app, "0x80");
        cell = CellOf(await app.RenderAsync(), 0x41)!;
        Assert.Equal(modified, cell["foreground"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-15-02")]
    public Task Inserted_bytes_have_a_double_underline() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await GoToAsync(app, "0x40");
        await app.TypeAsync("ABCD");
        await GoToAsync(app, "0x80");
        JsonObject render = await app.RenderAsync();
        foreach (long offset in new[] { 0x40L, 0x41L })
        {
            JsonObject cell = CellOf(render, offset)!;
            Assert.Equal(render["insertedColor"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());
            Assert.Equal("double", cell["underline"]!.GetValue<string>());
        }

        JsonObject shifted = CellOf(render, 0x42)!;
        Assert.Equal("40", shifted["hex"]!.GetValue<string>());
        Assert.Equal("none", shifted["underline"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-15-03")]
    public Task Modified_bytes_are_underlined_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await ForceHighContrastAsync(app);
        await GoToAsync(app, "0x40");
        await app.TypeAsync("AB");
        await app.KeyAsync("Insert");
        await GoToAsync(app, "0x50");
        await app.TypeAsync("CD");
        await GoToAsync(app, "0x100");
        JsonObject render = await app.RenderAsync();
        string text = await SystemColorAsync(app, "SystemColorWindowTextColor");
        foreach ((long offset, string underline) in new[] { (0x40L, "solid"), (0x50L, "double"), (0x60L, "none") })
        {
            JsonObject cell = CellOf(render, offset)!;
            Assert.Equal(text, cell["foreground"]!.GetValue<string>());
            Assert.Equal(underline, cell["underline"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-15-04")]
    public Task Saving_clears_the_highlight_unless_kept() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await GoToAsync(app, "0x40");
        await app.TypeAsync("AB");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(15), "the save");
        await GoToAsync(app, "0x80");
        JsonObject cell = CellOf(await app.RenderAsync(), 0x40)!;
        Assert.Equal("none", cell["underline"]!.GetValue<string>());
        Assert.Equal((await app.RenderAsync())["hexTextColor"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());

        // 4〜5. 「保存後も閉じるまで強調を残す」: 点線の下線と「保存済みの変更」色。
        WriteSettings(app.Profile, new JsonObject { ["view.modified.keepAfterSave"] = true });
        await app.WaitUntilAsync(async () => (await app.SendAsync("viewSettings")) is not null, TimeSpan.FromSeconds(2), "settings");
        await Task.Delay(1500);
        await GoToAsync(app, "0x41");
        await app.TypeAsync("CD");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(15), "the save");
        await GoToAsync(app, "0x80");
        JsonObject render = await app.RenderAsync();
        cell = CellOf(render, 0x41)!;
        Assert.Equal("dotted", cell["underline"]!.GetValue<string>());
        Assert.Equal(render["savedChangeColor"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());
    });

    // ---- VIEW-16 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-16-01")]
    public Task Hex_only_and_all_columns() => UiTestContext.RunAsync(async ctx =>
    {
        // コマンドパレット (UI-17) は別の機能のため、同じコマンドを表示メニューから実行する。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewHexOnly");
        Assert.Equal(["offset", "hex"], (await app.RenderAsync())["columns"]!.AsArray().Select(c => c!.GetValue<string>()));
        await MenuAsync(app, "Command_ViewAllColumns");
        Assert.Equal(["offset", "hex", "text"], (await app.RenderAsync())["columns"]!.AsArray().Select(c => c!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-16-02")]
    public Task Last_data_column_cannot_be_hidden() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewHexColumn");
        JsonObject text = await MenuItemAsync(app, "Command_ViewTextColumn");
        Assert.False(text["enabled"]!.GetValue<bool>());
        Assert.True(text["checked"]!.GetValue<bool>());
        Assert.True((await MenuItemAsync(app, "Command_ViewOffsetColumn"))["enabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-16-03")]
    public Task Hiding_the_active_column_moves_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x123");
        await app.KeyAsync("Tab");
        await MenuAsync(app, "Command_ViewTextColumn");
        Assert.Contains("0x00000123", await StatusOffsetAsync(app));
        Assert.Equal("Hex", await app.UiaNameAsync("Status_Column"));
    });
}
