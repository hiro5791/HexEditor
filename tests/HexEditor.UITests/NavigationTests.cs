using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>表示とカーソルの移動 (VIEW-01、VIEW-02、VIEW-25〜VIEW-27、VIEW-30)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class NavigationTests
{
    /// <summary>TD-VIEW-VIRT-FIXED256: 長さ 256 の長さ固定の仮想データソース。オフセット n の値が n。</summary>
    private static readonly JsonObject VirtFixed256 = new()
    {
        ["virtualSources"] = new JsonArray(new JsonObject
        {
            ["name"] = "virtual-256", ["length"] = 256, ["content"] = "offsetLowByte", ["resizable"] = false,
        }),
    };

    /// <summary>TD-VIEW-VIRT-MAX: 長さ 2^63 − 1 の長さ固定の仮想データソース。8 バイトごとにオフセットを 64 bit LE で書いた内容。</summary>
    private static readonly JsonObject VirtMax = new()
    {
        ["virtualSources"] = new JsonArray(new JsonObject
        {
            ["name"] = "virtual-max", ["length"] = "0x7FFFFFFFFFFFFFFF", ["content"] = "offset64", ["resizable"] = false,
        }),
    };

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-02")]
    public Task Empty_file_is_shown_as_one_blank_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-EMPTY")] });
        JsonObject render = await app.RenderAsync();
        JsonArray rows = render["rows"]!.AsArray();
        Assert.Single(rows);
        Assert.Equal("00000000", rows[0]!["offsetText"]!.GetValue<string>());
        Assert.Equal("  ", rows[0]!["cells"]![0]!["hex"]!.GetValue<string>());

        foreach ((string key, bool ctrl) in new[] { ("Right", false), ("Down", false), ("End", false), ("End", true), ("PageDown", false) })
        {
            await app.KeyAsync(key, ctrl: ctrl);
            Assert.Equal(0, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        }

        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains(" ERR ", StringComparison.Ordinal));
        Assert.False(app.Process.HasExited);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-03")]
    public Task Blank_last_row_only_for_resizable_sources() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.KeyAsync("End", ctrl: true);
        JsonArray rows = (await app.RenderAsync())["rows"]!.AsArray();
        Assert.Equal(17, rows.Count);
        Assert.Equal("00000100", rows[^1]!["offsetText"]!.GetValue<string>());
        Assert.Equal("  ", rows[^1]!["cells"]![0]!["hex"]!.GetValue<string>());
        Assert.Equal(0x100, (await app.DocumentAsync())["cursor"]!.GetValue<long>());

        AppSession fixedApp = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Hooks = VirtFixed256 });
        await fixedApp.KeyAsync("End", ctrl: true);
        rows = (await fixedApp.RenderAsync())["rows"]!.AsArray();
        Assert.Equal(16, rows.Count);
        Assert.Equal("000000F0", rows[^1]!["offsetText"]!.GetValue<string>());
        Assert.Equal(0xFF, (await fixedApp.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [Fact(Skip = "VIEW-02 の UI オートメーション対応が未実装: 縦スクロールバーの RangeValue を変えても Hex ビューがスクロールしない (ScrollBar.Scroll だけを処理している)")]
    [Trait(UiTest.TC, "TC-VIEW-02-03")]
    public Task Last_byte_of_a_2_pow_63_source() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = VirtMax });
        Assert.Equal(long.MaxValue, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // 1. 縦スクロールバーの RangeValue を最大値にする (UI オートメーション。マウスは使わない)。
        var bar = (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern;
        bar.SetValue(bar.Maximum.Value);
        await app.IdleAsync();
        await AssertLastRowOfMaxSourceAsync(app);

        // 3. Ctrl+End でカーソルが最後のバイトに移る。
        await app.KeyAsync("End", ctrl: true);
        Assert.Equal(long.MaxValue - 1, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        Assert.Contains("0x7FFFFFFFFFFFFFFE", await app.UiaNameAsync("Status_Offset"));

        // 4. Ctrl+Home でスクロールバーの値と一番上の行がどちらも 0。
        await app.KeyAsync("Home", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(0, (await app.DocumentAsync())["topRow"]!.GetValue<long>());
        Assert.Equal(0, (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern.Value.Value);
        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains(" ERR ", StringComparison.Ordinal));
    });

    /// <summary>TC-VIEW-02-03 の手順 2〜4 を、スクロールバーの代わりに Ctrl+End で確かめる (仮想のデータソースの確認を兼ねる)。</summary>
    [Fact]
    public Task Last_row_of_a_2_pow_63_source_after_ctrl_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = VirtMax });
        await app.KeyAsync("End", ctrl: true);
        await app.IdleAsync();
        await AssertLastRowOfMaxSourceAsync(app);
        Assert.Contains("0x7FFFFFFFFFFFFFFE", await app.UiaNameAsync("Status_Offset"));
        await app.KeyAsync("Home", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(0, (await app.DocumentAsync())["topRow"]!.GetValue<long>());
        Assert.Equal(0, (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern.Value.Value);
    });

    private static async Task AssertLastRowOfMaxSourceAsync(AppSession app)
    {

        // 2. 最終行の先頭オフセットが 0x7FFFFFFFFFFFFFF0 で、15 バイトが規則どおりの値。
        JsonArray rows = (await app.RenderAsync())["rows"]!.AsArray();
        JsonObject last = rows[^1]!.AsObject();
        Assert.Equal("7FFFFFFFFFFFFFF0", last["offsetText"]!.GetValue<string>());
        JsonArray cells = last["cells"]!.AsArray();
        byte[] word = BitConverter.GetBytes(0x7FFFFFFFFFFFFFF8L);
        for (int c = 0; c < 15; c++)
        {
            long offset = 0x7FFFFFFFFFFFFFF0L + c;
            byte expected = offset < 0x7FFFFFFFFFFFFFF8L ? BitConverter.GetBytes(0x7FFFFFFFFFFFFFF0L)[c] : word[c - 8];
            Assert.Equal(expected.ToString("X2"), cells[c]!["hex"]!.GetValue<string>());
        }

        Assert.Equal("  ", cells[15]!["hex"]!.GetValue<string>());
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-25-01")]
    public Task Down_near_the_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [Len100(ctx)] });
        await app.GoToAsync(0x5A);
        await app.KeyAsync("Down");
        await AssertCursorAsync(app, 0x64);
        await app.KeyAsync("Down");
        await AssertCursorAsync(app, 0x64);
        await app.GoToAsync(0x50);
        await app.KeyAsync("Down");
        await AssertCursorAsync(app, 0x60);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-25-02")]
    public Task Ctrl_end_on_a_fixed_length_source() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = VirtFixed256 });
        await app.KeyAsync("End", ctrl: true);
        await AssertCursorAsync(app, 0xFF);
        await app.KeyAsync("Right");
        await app.KeyAsync("Down");
        await AssertCursorAsync(app, 0xFF);

        // 3. 最終行より下の空白部分のクリック: 当たり判定の後と同じく、末尾より後ろの位置でクリックの処理を呼ぶ。
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x1000 });
        await AssertCursorAsync(app, 0xFF);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-25-03")]
    public Task Page_down_keeps_the_caret_height() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        long page = 16L * (v - 1);
        await app.KeyAsync("Down", count: 5);
        (long cursor, int row) = await CaretAsync(app);
        Assert.Equal(0x50, cursor);

        for (int i = 1; i <= 10; i++)
        {
            await app.KeyAsync("PageDown");
            (long c, int r) = await CaretAsync(app);
            Assert.Equal(row, r);
            Assert.Equal(cursor + (page * i), c);
        }

        await app.KeyAsync("End", ctrl: true);
        (cursor, row) = await CaretAsync(app);
        for (int i = 1; i <= 3; i++)
        {
            await app.KeyAsync("PageUp");
            (long c, int r) = await CaretAsync(app);
            Assert.Equal(row, r);
            Assert.Equal(cursor - (page * i), c);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-26-02")]
    public Task Right_from_the_low_nibble_moves_to_the_next_byte() => UiTestContext.RunAsync(async ctx =>
    {
        // 「ニブルの位置を表示」の設定はまだないため、ニブルは描画モデル (状態) で確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x10, ["lowNibble"] = true });
        await app.KeyAsync("Right");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x11, doc["cursor"]!.GetValue<long>());
        Assert.False(doc["lowNibble"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-26-03")]
    public Task Down_keeps_the_nibble() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x10, ["lowNibble"] = true });
        await app.KeyAsync("Down");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x20, doc["cursor"]!.GetValue<long>());
        Assert.True(doc["lowNibble"]!.GetValue<bool>());
        await app.KeyAsync("PageDown");
        Assert.True((await app.DocumentAsync())["lowNibble"]!.GetValue<bool>());
        await app.KeyAsync("Home");
        Assert.False((await app.DocumentAsync())["lowNibble"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-27-01")]
    public Task Tab_switches_to_the_text_column() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x1234);
        await app.KeyAsync("Tab");
        await AssertCursorAsync(app, 0x1234);
        Assert.Equal("Text", await app.UiaNameAsync("Status_Column"));

        await app.KeyAsync("Tab", shift: true);
        await AssertCursorAsync(app, 0x1234);
        Assert.Equal("Hex", await app.UiaNameAsync("Status_Column"));
        Assert.StartsWith("HexView", (await app.StateAsync())["focused"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-30-01")]
    public Task Ctrl_end_shows_the_last_row_at_the_bottom() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), Len100(ctx)] });
        await EditingTests.SelectTabAsync(app, 0);

        // 1. 0x10〜0x1F を選択する (Ctrl+E の範囲の選択は未実装のため、命令で選ぶ)。
        await app.SelectAsync(0x10, 0x10);
        await app.KeyAsync("End", ctrl: true);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x100000, doc["cursor"]!.GetValue<long>());
        Assert.Equal(0, doc["selectionLength"]!.GetValue<long>());
        JsonObject render = await app.RenderAsync();
        int visible = render["visibleRows"]!.GetValue<int>();
        Assert.Equal(65536 - (visible - 1), doc["topRow"]!.GetValue<long>());
        JsonObject lastRow = render["rows"]!.AsArray().Single(r => r!["offsetText"]!.GetValue<string>() == "00100000")!.AsObject();
        Assert.Equal(visible - 1, lastRow["index"]!.GetValue<int>());

        await app.KeyAsync("Home", ctrl: true);
        doc = await app.DocumentAsync();
        Assert.Equal(0, doc["cursor"]!.GetValue<long>());
        Assert.Equal(0, doc["topRow"]!.GetValue<long>());

        await EditingTests.SelectTabAsync(app, 1);
        await app.KeyAsync("End", ctrl: true);
        Assert.Equal(0, (await app.DocumentAsync())["topRow"]!.GetValue<long>());
    });

    /// <summary>TD-VIEW-LEN100: 100 バイト、オフセット n の値が n。</summary>
    internal static string Len100(UiTestContext ctx) =>
        ctx.WriteFile("TD-VIEW-LEN100.bin", Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());

    /// <summary>カーソル位置を状態とステータスバーの両方で確かめる (表示の桁数は問わない)。</summary>
    internal static async Task AssertCursorAsync(AppSession app, long expected)
    {
        Assert.Equal(expected, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        // 表示は 8 桁以上に 0 で埋める (VIEW-40)。
        Assert.Matches($"^Offset: 0x0*{expected:X}$", await app.UiaNameAsync("Status_Offset"));
        Assert.True((await app.UiaNameAsync("Status_Offset")).Length >= "Offset: 0x".Length + 8);
    }

    private static async Task<(long Cursor, int Row)> CaretAsync(AppSession app)
    {
        JsonObject render = await app.RenderAsync();
        return ((await app.DocumentAsync())["cursor"]!.GetValue<long>(), render["caret"]!["row"]!.GetValue<int>());
    }
}
