using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;
using static HexEditor.UITests.Infrastructure.ViewSettingsOps;

namespace HexEditor.UITests;

/// <summary>オフセットの基数・ベースアドレス・基準点 (VIEW-19、VIEW-20) と、マルチバイト文字の表示規則 (VIEW-22)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewOffsetAndTextTests
{
    private const long Sparse100G = 100L * 1024 * 1024 * 1024;

    // ---- VIEW-19 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-19-01")]
    public Task Decimal_radix_in_the_offset_column_and_status_bar() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x1F00");
        await MenuAsync(app, "Command_ViewRadixDecimal");
        JsonObject row = RowOf(await app.RenderAsync(), 0x1F00)!;
        string text = row["offsetText"]!.GetValue<string>();
        Assert.Equal("7936", text.Trim());
        Assert.StartsWith(" ", text);
        Assert.Contains("7,936", await StatusOffsetAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-19-03")]
    public Task Offset_column_width_does_not_change_on_100_GB() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        foreach ((string radix, int width) in new[] { ("Hex", 10), ("Decimal", 12) })
        {
            await MenuAsync(app, "Command_ViewRadix" + radix);
            await app.KeyAsync("Home", ctrl: true);
            var widths = new List<(int Chars, double Left)>();
            foreach (string target in new[] { "0x7FFFFFF0", "0xFFFFFFF0", "0x100000000", "end" })
            {
                if (target == "end")
                {
                    await app.KeyAsync("End", ctrl: true);
                    await app.IdleAsync();
                }
                else
                {
                    await GoToAsync(app, target);
                }

                JsonObject render = await app.RenderAsync();
                foreach (JsonNode? row in render["rows"]!.AsArray())
                {
                    widths.Add((row!["offsetText"]!.GetValue<string>().Length, render["contentLeft"]!.GetValue<double>()));
                }
            }

            Assert.All(widths, w => Assert.Equal(width, w.Chars));
            Assert.Single(widths.Select(w => w.Left).Distinct());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-19-04")]
    public Task Hex_prefix_works_in_decimal_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewRadixDecimal");
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0x1F00");
        await app.IdleAsync();
        string interpretation = await app.UiaNameAsync("GoTo_Interpretation");
        Assert.Contains("7,936", interpretation);
        Assert.Contains("0x1F00", interpretation);
        await app.SendAsync("goToKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
        Assert.Contains("7,936", await StatusOffsetAsync(app));

        // 3. 接頭辞のない 1F00 は 10 進として解釈できない。
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "1F00");
        await app.IdleAsync();
        JsonObject input = await ElementAsync(app, "GoTo_Input");
        Assert.NotNull(input["borderBrush"]);
        Assert.DoesNotContain("=", await app.UiaNameAsync("GoTo_Interpretation"));
    });

    // ---- VIEW-20 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-20-01")]
    public Task Base_address_and_its_upper_limit() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await SetBaseAddressAsync(app, "0x400000");
        JsonObject render = await app.RenderAsync();
        Assert.Equal("00400000", render["rows"]![0]!["offsetText"]!.GetValue<string>());
        Assert.Contains("@00400000", await StatusOffsetAsync(app));

        // 3. 末尾の次の位置のアドレスがちょうど 2^64 − 1 になるベースアドレス。
        await SetBaseAddressAsync(app, "0xFFFFFFFFFFFFFEFF");
        await app.KeyAsync("End", ctrl: true);
        await app.IdleAsync();
        render = await app.RenderAsync();
        Assert.Equal("FFFFFFFFFFFFFFF0", render["rows"]!.AsArray()[^1]!["offsetText"]!.GetValue<string>());
        Assert.Contains("@FFFFFFFFFFFFFFFF", await StatusOffsetAsync(app));

        // 4. 1 つ大きい値は確定できない。
        await MenuAsync(app, "Command_ViewBaseAddress");
        JsonObject state = await InputAsync(app, "0xFFFFFFFFFFFFFF00");
        Assert.True(state["invalid"]!.GetValue<bool>());
        Assert.False(state["okEnabled"]!.GetValue<bool>());
        Assert.Contains("0xFFFFFFFFFFFFFFFF", state["error"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-20-02")]
    public Task Rows_align_to_the_base_address() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await SetBaseAddressAsync(app, "0x401004");
        JsonObject render = await app.RenderAsync();
        JsonArray first = render["rows"]![0]!["cells"]!.AsArray();
        for (int c = 0; c < 4; c++)
        {
            Assert.Equal("  ", first[c]!["hex"]!.GetValue<string>());
        }

        Assert.Equal("00", first[4]!["hex"]!.GetValue<string>());
        JsonObject second = render["rows"]![1]!.AsObject();
        Assert.Equal("00401010", second["offsetText"]!.GetValue<string>());
        Assert.Equal("0C", second["cells"]![0]!["hex"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-20-03")]
    public Task Reference_point_shows_relative_offsets() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await GoToAsync(app, "0x40");
        await MenuAsync(app, "Command_ViewSetReference");
        JsonObject render = await app.RenderAsync();
        Assert.Equal("-00000010", RowOf(render, 0x30)!["offsetText"]!.GetValue<string>());
        Assert.Equal("+00000000", RowOf(render, 0x40)!["offsetText"]!.GetValue<string>());
        Assert.Equal("+00000020", RowOf(render, 0x60)!["offsetText"]!.GetValue<string>());
        string status = await StatusOffsetAsync(app);
        Assert.Contains("relative", status);
        Assert.Contains("+00000000", status);

        await MenuAsync(app, "Command_ViewClearReference");
        render = await app.RenderAsync();
        Assert.Equal("00000030", RowOf(render, 0x30)!["offsetText"]!.GetValue<string>());
        Assert.DoesNotContain("relative", await StatusOffsetAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-20-04")]
    public Task Reference_point_follows_an_insertion() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await GoToAsync(app, "0x40");
        await MenuAsync(app, "Command_ViewSetReference");
        await app.KeyAsync("Insert");
        await GoToAsync(app, "0x10");
        await app.TypeAsync("00000000000000000000000000000000");
        await app.IdleAsync();
        Assert.Equal("+00000000", RowOf(await app.RenderAsync(), 0x50)!["offsetText"]!.GetValue<string>());

        // 3〜4. 基準点を含む範囲を削除すると、基準点は削除範囲の先頭へ。
        await app.SelectAsync(0, 0x60);
        await app.KeyAsync("Delete");
        await app.IdleAsync();
        Assert.Equal("+00000000", RowOf(await app.RenderAsync(), 0)!["offsetText"]!.GetValue<string>());
    });

    // ---- VIEW-22 ----

    private static async Task<AppSession> PatternsAsync(UiTestContext ctx, string encodingCommand)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-VIEW-PATTERNS")] });
        await MenuAsync(app, encodingCommand);
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-22-01")]
    public Task Utf8_three_byte_character_spans_two_cells() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await PatternsAsync(ctx, "Command_Encoding_utf-8");
        JsonObject render = await app.RenderAsync();
        double cell = render["cellWidth"]!.GetValue<double>();
        JsonObject first = CellOf(render, 0x50)!;
        Assert.Equal("あ", first["glyph"]!.GetValue<string>());
        Assert.Equal(first["textLeft"]!.GetValue<double>(), first["glyphLeft"]!.GetValue<double>(), 3);
        Assert.Equal(2 * cell, first["glyphWidth"]!.GetValue<double>(), 3);
        Assert.Null(CellOf(render, 0x52)!["glyph"]);
        Assert.Equal(" ", CellOf(render, 0x52)!["text"]!.GetValue<string>());
    });

    public static TheoryData<string, string> TextFiles() => new()
    {
        { "TD-TEXT-UTF8", "Command_Encoding_utf-8" },
        { "TD-TEXT-SJIS", "Command_Encoding_cp932" },
        { "TD-TEXT-UTF16LE", "Command_Encoding_utf-16le" },
    };

    [Theory]
    [Trait(UiTest.TC, "TC-VIEW-22-02")]
    [MemberData(nameof(TextFiles))]
    public Task Characters_do_not_change_with_the_scroll_position(string id, string encoding) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData(id)] });
        await MenuAsync(app, encoding);
        var seen = new Dictionary<long, string>();
        for (int step = 0; step <= 64; step++)
        {
            JsonObject render = await app.RenderAsync();
            foreach (JsonNode? row in render["rows"]!.AsArray())
            {
                long start = row!["rowStart"]!.GetValue<long>();
                JsonArray cells = row["cells"]!.AsArray();
                for (int c = 0; c < cells.Count; c++)
                {
                    string shown = cells[c]!["glyph"]?.GetValue<string>() ?? cells[c]!["text"]!.GetValue<string>();
                    if (seen.TryGetValue(start + c, out string? before))
                    {
                        Assert.True(before == shown, $"{id} offset 0x{start + c:X} at step {step}: '{before}' != '{shown}'");
                    }
                    else
                    {
                        seen[start + c] = shown;
                    }
                }
            }

            await app.KeyAsync("Down", ctrl: true);
            await app.IdleAsync();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-22-04")]
    public Task Utf16_odd_start_shifts_the_units() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await PatternsAsync(ctx, "Command_Encoding_utf-16le");
        await GoToAsync(app, "0xE0");
        string Glyph(JsonObject render, long offset) => CellOf(render, offset)!["glyph"]?.GetValue<string>() ?? string.Empty;
        JsonObject render = await app.RenderAsync();
        Assert.DoesNotContain("H", Enumerable.Range(0xE0, 12).Select(o => Glyph(render, o)));

        await MenuAsync(app, "Command_ViewUtf16Odd");
        render = await app.RenderAsync();
        Assert.Equal(["H", "e", "l", "l", "o"], new long[] { 0xE1, 0xE3, 0xE5, 0xE7, 0xE9 }.Select(o => Glyph(render, o)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-22-05")]
    public Task Invalid_utf8_and_surrogate_pairs() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await PatternsAsync(ctx, "Command_Encoding_utf-8");
        JsonObject render = await app.RenderAsync();
        JsonObject bad = CellOf(render, 0x60)!;
        Assert.Equal(".", bad["text"]!.GetValue<string>());
        Assert.Equal("Invalid", bad["textKind"]!.GetValue<string>());
        Assert.Equal(render["invalidColor"]!.GetValue<string>(), bad["textForeground"]!.GetValue<string>());
        JsonObject paren = CellOf(render, 0x61)!;
        Assert.Equal("(", paren["text"]!.GetValue<string>());
        Assert.Equal(render["hexTextColor"]!.GetValue<string>(), paren["textForeground"]!.GetValue<string>());

        await MenuAsync(app, "Command_Encoding_utf-16le");
        render = await app.RenderAsync();
        Assert.Equal(char.ConvertFromUtf32(0x1F600), CellOf(render, 0x70)!["glyph"]!.GetValue<string>());
        foreach (long o in new long[] { 0x71, 0x72, 0x73 })
        {
            Assert.Equal(" ", CellOf(render, o)!["text"]!.GetValue<string>());
            Assert.Equal("Continuation", CellOf(render, o)!["textKind"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-22-06")]
    public Task Character_across_the_row_end_is_squeezed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await PatternsAsync(ctx, "Command_Encoding_utf-8");
        JsonObject render = await app.RenderAsync();
        double cell = render["cellWidth"]!.GetValue<double>();
        JsonObject last = CellOf(render, 0xBF)!;
        Assert.Equal("あ", last["glyph"]!.GetValue<string>());
        Assert.True(last["glyphScaleX"]!.GetValue<double>() < 1, $"scale {last["glyphScaleX"]}");
        Assert.Equal(last["textLeft"]!.GetValue<double>(), last["glyphLeft"]!.GetValue<double>(), 3);
        Assert.Equal(cell, last["glyphWidth"]!.GetValue<double>(), 3);
        Assert.Equal(" ", CellOf(render, 0xC0)!["text"]!.GetValue<string>());
        Assert.Equal(" ", CellOf(render, 0xC1)!["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-22-07")]
    public Task Combining_mark_is_drawn_alone() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await PatternsAsync(ctx, "Command_Encoding_utf-8");
        JsonObject render = await app.RenderAsync();
        Assert.Equal("A", CellOf(render, 0x80)!["text"]!.GetValue<string>());
        Assert.Equal("◌́", CellOf(render, 0x81)!["glyph"]!.GetValue<string>());
        Assert.Equal(" ", CellOf(render, 0x82)!["text"]!.GetValue<string>());
    });
}
