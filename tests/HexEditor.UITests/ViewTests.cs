using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Patterns;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// Hex ビューの表示・スクロール・移動 (VIEW-01〜VIEW-03、VIEW-25、VIEW-26、VIEW-28、VIEW-29、VIEW-34、VIEW-40、VIEW-41)。
/// マウス・ホイール・タッチ・スクロールバーの操作と Windows の設定の変更は、テスト用の命令の通り道でアプリの中の同じ処理に渡す
/// (実際の入力装置と利用者の PC の設定は使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>TD-SPARSE-100G の長さ (100 GiB)。</summary>
    private const long Sparse100G = 100L * 1024 * 1024 * 1024;

    // ---- VIEW-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-01")]
    public Task Start_middle_and_end_of_a_100_GB_file() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });

        // 2. 1 行目は 0 で、目印 @0000000000000000 の先頭 16 バイト。
        JsonObject render = await WaitForContentAsync(app, 0);
        AssertRowIsMarker(render, 0, 0, 0);

        // 3〜4. 0xC80000000 (50 GiB) へ移動すると、カーソルの行の値が目印 @0000000C80000000 の先頭 16 バイト。
        await GoToAsync(app, "0xC80000000");
        Assert.Equal(0xC80000000, await CursorAsync(app));
        render = await WaitForContentAsync(app, 0xC80000000);
        AssertRowIsMarker(render, 0xC80000000, 0xC80000000, 0);

        // 5. Ctrl+End: データのある最後の行は (L − 1) を 16 で切り捨てた位置で、末尾の目印の後ろの 16 バイト。
        // 長さを変えられるファイルなので、その下に末尾位置の空の行がある (VIEW-01 の仕様 2)。
        await app.KeyAsync("End", ctrl: true);
        long lastRow = (Sparse100G - 1) / 16 * 16;
        render = await WaitForContentAsync(app, lastRow);
        AssertRowIsMarker(render, lastRow, Sparse100G - TestDataCatalog.MarkerLength, (int)(lastRow - (Sparse100G - TestDataCatalog.MarkerLength)));
        JsonObject blank = render["rows"]!.AsArray()[^1]!.AsObject();
        Assert.Equal(Sparse100G.ToString("X10"), blank["offsetText"]!.GetValue<string>());
        Assert.Equal("  ", blank["cells"]![0]!["hex"]!.GetValue<string>());

        static void AssertRowIsMarker(JsonObject render, long rowStart, long marker, int skip)
        {
            JsonObject row = Row(render, rowStart) ?? throw new Xunit.Sdk.XunitException($"Row {rowStart:X} is not shown.");

            // オフセット列は VIEW-19 の書式 (表示しうる最大のアドレスを表せる桁数。100 GiB では 10 桁の 16 進)。
            Assert.Equal(rowStart.ToString("X10"), row["offsetText"]!.GetValue<string>());
            byte[] expected = TestDataCatalog.Marker(marker).Skip(skip).Take(16).ToArray();
            Assert.Equal(Convert.ToHexString(expected), RowHex(row).Replace(" ", string.Empty));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-04")]
    public Task Hex_view_stays_left_to_right_in_arabic() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Files = [ctx.TestData("TD-BYTES-256")] });

        // 2. Hex ビューは左から右。
        Assert.Equal("LeftToRight", (await ElementAsync(app, "HexView"))["flowDirection"]!.GetValue<string>());
        JsonObject render = await app.RenderAsync();
        Assert.Equal("LeftToRight", render["flowDirection"]!.GetValue<string>());

        // 3. オフセット列 < Hex 列 < テキスト列の順に並び、Hex 列の 1 行目のセルが左から 00 01 … 0F。
        // 列見出し (VIEW-05) はフェーズ 1 の機能のため、まだ確かめない。
        double offsetX = render["offsetLeft"]!.GetValue<double>();
        (double hexX, _) = CellPoint(render, 0);
        (double textX, _) = CellPoint(render, 0, text: true);
        Assert.True(offsetX < hexX && hexX < textX, $"offset {offsetX}, hex {hexX}, text {textX}");
        double previous = double.MinValue;
        for (int c = 0; c < 16; c++)
        {
            (double x, _) = CellPoint(render, c);
            Assert.True(x > previous);
            previous = x;
        }

        Assert.Equal("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F", RowHex(Row(render, 0)!));

        // 4. スクリーンショットを成果物に保存する (向きを目で確かめるため)。
        app.Screenshot().Save(Path.Combine(UiTestContext.ArtifactsRoot, "TC-VIEW-01-04", "arabic.bmp"));

        // メニューバーなど Hex ビューの外は右から左。
        Assert.Equal("RightToLeft", (await ElementAsync(app, "MainMenu"))["flowDirection"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-05")]
    public Task Theme_switch_is_applied_immediately() => UiTestContext.RunAsync(async ctx =>
    {
        // 手順 3〜4 (OS のハイコントラストの切り替え) は、利用者の PC の設定を変えるため自動テストでは行わない
        // (ハイコントラストの配色はシステム色だけを使う ThemeDictionaries で定義している)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        int pid = app.Pid;

        await app.CommandAsync("Command_ThemeLight");
        await app.IdleAsync();
        JsonObject light = await app.RenderAsync();

        await app.CommandAsync("Command_ThemeDark");
        await app.IdleAsync();
        JsonObject dark = await app.RenderAsync();

        // Fluent の SolidBackgroundFillColorBase と TextFillColorPrimary の値。
        Assert.Equal("#FFF3F3F3", light["background"]!.GetValue<string>());
        Assert.Equal("#E4000000", light["textColor"]!.GetValue<string>());
        Assert.Equal("#FF202020", dark["background"]!.GetValue<string>());
        Assert.Equal("#FFFFFFFF", dark["textColor"]!.GetValue<string>());

        // 描いたセルの文字色もテーマの色。
        Assert.Equal(light["textColor"]!.GetValue<string>(), light["rows"]![0]!["cells"]![1]!["foreground"]!.GetValue<string>());
        Assert.Equal(dark["textColor"]!.GetValue<string>(), dark["rows"]![0]!["cells"]![1]!["foreground"]!.GetValue<string>());

        // 5. 再起動していない。
        Assert.Equal(pid, (await app.StateAsync())["pid"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-01-06")]
    public Task Scale_change_keeps_cursor_and_top_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x12345");
        await app.KeyAsync("Down", ctrl: true, count: 5);
        long cursor = await CursorAsync(app);
        long top = await TopRowAsync(app);
        JsonObject render = await app.RenderAsync();
        double cell = render["cellWidth"]!.GetValue<double>();
        double height = render["rowHeight"]!.GetValue<double>();

        // 3〜5. 表示倍率の変更の通知 (別の表示倍率のモニターへ移ったとき) を、テスト用の命令で模擬する。
        foreach (double scale in new[] { 1.5, 2.0, 1.0 })
        {
            await app.SendAsync("setSystem", new JsonObject { ["rasterizationScale"] = scale });
            await app.IdleAsync();
            render = await app.RenderAsync();
            Assert.Equal(cursor, await CursorAsync(app));
            Assert.Equal(top, await TopRowAsync(app));
            Assert.Equal(top, render["topRow"]!.GetValue<long>());

            // 物理ピクセルでは倍率に比例する (物理ピクセルへの切り上げの 1 px を除く)。
            Assert.InRange(render["cellWidth"]!.GetValue<double>() * scale, cell * scale - 1, cell * scale + 1);
            Assert.InRange(render["rowHeight"]!.GetValue<double>() * scale, height * scale - 1, height * scale + 1);
        }

        Assert.Equal(cell, render["cellWidth"]!.GetValue<double>(), 3);
        Assert.Equal(height, render["rowHeight"]!.GetValue<double>(), 3);
    });

    // ---- VIEW-02 ----

    /// <summary>TD-VIEW-VIRT-1T: 長さ 2^40 の長さ固定の仮想データソース。</summary>
    private static readonly JsonObject Virt1T = new()
    {
        ["virtualSources"] = new JsonArray(new JsonObject
        {
            ["name"] = "virtual-1T", ["length"] = 1L << 40, ["content"] = "offset64", ["resizable"] = false,
        }),
    };

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-02-01")]
    public Task Scroll_bar_maps_values_to_rows_on_1_TB() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Virt1T });
        IRangeValuePattern bar = (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern;
        long s = (long)bar.Maximum.Value;
        Assert.Equal(1_000_000, s);
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        long m = (1L << 36) - v;

        await SetBarAsync(app, bar, 0);
        Assert.Equal(0, await TopRowAsync(app));

        await SetBarAsync(app, bar, s / 2);
        Assert.Equal((long)(((Int128)(s / 2) * m + s / 2) / s), await TopRowAsync(app));

        await SetBarAsync(app, bar, s);
        Assert.Equal(m, await TopRowAsync(app));
        JsonObject render = await app.RenderAsync();
        JsonArray rows = render["rows"]!.AsArray();
        Assert.Equal(((1L << 40) - 16).ToString("X10"), rows[v - 1]!["offsetText"]!.GetValue<string>());
        Assert.Equal(v, rows.Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-02-04")]
    public Task Arrow_buttons_and_one_unit_on_1_MB() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        IRangeValuePattern bar = (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern;

        // 1〜2. 下向きの矢印ボタン (スクロールバーのテンプレートの部品。UI オートメーションの木に出ないため命令で押す)。
        await ArrowAsync(app, down: true);
        Assert.Equal(1, await TopRowAsync(app));

        // 3〜4. RangeValue の値を 1 増やす。
        await SetBarAsync(app, bar, (long)bar.Value.Value + 1);
        Assert.Equal(2, await TopRowAsync(app));

        // 5. 上向きの矢印ボタン。
        await ArrowAsync(app, down: false);
        Assert.Equal(1, await TopRowAsync(app));

        // 最大値は M = R − V (R は末尾位置の空の行を含めた 65,537 行)。
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        Assert.Equal(65_537 - v, (long)bar.Maximum.Value);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-02-05")]
    public Task Arrow_buttons_on_100_GB_move_one_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await ArrowAsync(app, down: true);
        Assert.Equal(1, await TopRowAsync(app));
        for (int i = 0; i < 9; i++)
        {
            await ArrowAsync(app, down: true);
        }

        Assert.Equal(10, await TopRowAsync(app));

        // 3. 2^31 へ移動してから矢印ボタンを押すと、ちょうど 1 行増える。
        await GoToAsync(app, "0x80000000");
        long top = await TopRowAsync(app);
        await ArrowAsync(app, down: true);
        Assert.Equal(top + 1, await TopRowAsync(app));

        // 4. つまみの位置は round(行番号 × S / M)。表示中の行は変わらない。
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        long m = Sparse100G / 16 + 1 - v;
        long s = 1_000_000;
        IRangeValuePattern bar = (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern;
        Assert.Equal((long)(((Int128)(top + 1) * s + m / 2) / m), (long)bar.Value.Value);
        Assert.Equal(top + 1, await TopRowAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-02-06")]
    public Task Thumb_drag_shows_the_top_address() => UiTestContext.RunAsync(async ctx =>
    {
        // つまみのドラッグ (マウス) の代わりに、スクロールバーの Scroll イベント (ThumbTrack / EndScroll) と同じ処理を命令で行う。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        IRangeValuePattern bar = (await app.WaitForAsync("HexViewVerticalScrollBar")).Patterns.RangeValue.Pattern;
        double half = bar.Maximum.Value / 2;
        await app.SendAsync("scrollBar", new JsonObject { ["type"] = "ThumbTrack", ["value"] = half });
        await app.IdleAsync();

        JsonObject tip = await ElementAsync(app, "HexViewScrollToolTip");
        Assert.True(tip["found"]!.GetValue<bool>());
        long top = await TopRowAsync(app);
        Assert.True(top > 0);
        Assert.Equal("0x" + (top * 16).ToString("X8"), tip["content"]!.GetValue<string>());

        await app.SendAsync("scrollBar", new JsonObject { ["type"] = "EndScroll", ["value"] = half });
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => !(await ElementAsync(app, "HexViewScrollToolTip"))["found"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(5), "the tool tip to close");
    });

    private static async Task SetBarAsync(AppSession app, IRangeValuePattern bar, long value)
    {
        bar.SetValue(value);
        await app.IdleAsync();
    }

    private static async Task ArrowAsync(AppSession app, bool down)
    {
        await app.SendAsync("scrollBar", new JsonObject { ["part"] = down ? "VerticalSmallIncrease" : "VerticalSmallDecrease" });
        await app.IdleAsync();
    }

    // ---- VIEW-03 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-03-03")]
    public Task Reads_under_100_ms_show_no_placeholder() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-VIEW-HOOK-SLOW50: すべての読み込みを 50 ms 遅らせる。
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("open", new JsonObject { ["path"] = ctx.TestData("TD-SEQ-1M"), ["delayMs"] = 50 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the view");
        await WaitForContentAsync(app, 0);

        foreach (long target in new long[] { 0x40000, 0x80000, 0xC0000 })
        {
            await GoToAsync(app, "0x" + target.ToString("X"));
            await Task.Delay(500);

            // 移動先の値が描かれたフレームがある。
            JsonObject row = Row(await WaitForContentAsync(app, target), target)!;
            Assert.StartsWith("00 01 02", RowHex(row), StringComparison.Ordinal);
        }

        // 記録したどのフレームにも仮表示 (··) のセルがない。
        JsonObject render = await app.RenderAsync();
        Assert.True(render["frames"]!.GetValue<int>() > 3);
        Assert.Equal(0, render["placeholderFrames"]!.GetValue<int>());
    });

    [Fact(Skip = "OS のハイコントラストをオンにする必要がある (SPI_SETHIGHCONTRAST は利用者の PC 全体の表示を変えるため、作業中の PC では自動テストで切り替えない)")]
    [Trait(UiTest.TC, "TC-VIEW-03-05")]
    public void Unreadable_range_in_high_contrast()
    {
    }

    // ---- VIEW-25、VIEW-26 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-25-04")]
    public Task Clicking_a_hex_cell_sets_the_nibble() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.statusBar.showNibble"] = true });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();

        // 1〜2. 0x25 の Hex セルの 2 文字目。
        await ClickAsync(app, CellPoint(render, 0x25, charIndex: 1));
        Assert.Equal("Offset: 0x00000025 (low nibble)", await app.UiaNameAsync("Status_Offset"));
        Assert.True((await app.DocumentAsync())["lowNibble"]!.GetValue<bool>());

        // 3. 同じセルの 1 文字目。
        await ClickAsync(app, CellPoint(render, 0x25, charIndex: 0));
        Assert.Equal("Offset: 0x00000025", await app.UiaNameAsync("Status_Offset"));
        Assert.False((await app.DocumentAsync())["lowNibble"]!.GetValue<bool>());

        // 4. オフセット列の行 3 (行全体を選択し、カーソルは行の先頭)。
        await ClickAsync(app, OffsetColumnPoint(render, 3));
        Assert.Equal("Offset: 0x00000030", await app.UiaNameAsync("Status_Offset"));

        // 5. 最終行より下の空白部分 (末尾を表示してから、末尾位置の空の行より下をクリックする)。
        await app.KeyAsync("Home", ctrl: true);
        await app.KeyAsync("End", ctrl: true);
        render = await app.RenderAsync();
        double height = render["rowHeight"]!.GetValue<double>();
        int rows = render["visibleRows"]!.GetValue<int>();
        await ClickAsync(app, (CellPoint(render, 0).X, rows * height + height / 2));
        Assert.Equal("Offset: 0x00100000", await app.UiaNameAsync("Status_Offset"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-26-01")]
    public Task Right_moves_by_nibble_when_enabled() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.cursor.nibbleArrowKeys"] = true, ["view.statusBar.showNibble"] = true });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });

        await app.KeyAsync("Right");
        Assert.Equal("Offset: 0x00000000 (low nibble)", await app.UiaNameAsync("Status_Offset"));
        await app.KeyAsync("Right");
        Assert.Equal("Offset: 0x00000001", await app.UiaNameAsync("Status_Offset"));

        await app.KeyAsync("Left", count: 3);
        Assert.Equal("Offset: 0x00000000", await app.UiaNameAsync("Status_Offset"));
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0, doc["cursor"]!.GetValue<long>());
        Assert.False(doc["lowNibble"]!.GetValue<bool>());
    });

    // ---- VIEW-28 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-28-01")]
    public Task One_wheel_notch_scrolls_three_rows() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setSystem", new JsonObject { ["wheelScrollLines"] = 3 });
        await WheelAsync(app, -120);
        Assert.Equal(3, await TopRowAsync(app));
        Assert.Equal("Offset: 0x00000000", await app.UiaNameAsync("Status_Offset"));

        // 3. 40 単位の入力を 3 回: 蓄積して 3 行。
        await WheelAsync(app, -40, count: 3);
        Assert.Equal(6, await TopRowAsync(app));
        Assert.Equal(0, (await app.RenderAsync())["subRowOffset"]!.GetValue<double>(), 3);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-28-02")]
    public Task Wheel_follows_the_windows_setting() => UiTestContext.RunAsync(async ctx =>
    {
        // Windows のホイールの設定 (SPI_SETWHEELSCROLLLINES) は利用者の PC 全体の設定のため変えず、アプリが読む値をテスト用の命令で差し替える。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setSystem", new JsonObject { ["wheelScrollLines"] = 5 });
        await WheelAsync(app, -120);
        Assert.Equal(5, await TopRowAsync(app));

        // 2. 「1 画面ずつ」(WHEEL_PAGESCROLL) では V − 1 行。
        await app.SendAsync("setSystem", new JsonObject { ["wheelScrollLines"] = "page" });
        await app.KeyAsync("Home", ctrl: true);
        await WheelAsync(app, -120);
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        Assert.Equal(v - 1, await TopRowAsync(app));
        await app.SendAsync("setSystem", new JsonObject { ["wheelScrollLines"] = null });
    });

    /// <summary>配置 (セルの幅・位置) が 2 回続けて同じになるまで待ち、その描画の情報を返す。</summary>
    private static async Task<JsonObject> StableRenderAsync(AppSession app)
    {
        JsonObject render = null!;
        string? previous = null;
        await app.WaitUntilAsync(async () =>
        {
            await Task.Delay(100);
            await app.IdleAsync();
            render = await app.RenderAsync();
            string layout = $"{render["cellWidth"]} {render["offsetLeft"]} {render["contentLeft"]} {CellPoint(render, 0).X}";
            bool stable = layout == previous;
            previous = layout;
            return stable;
        }, TimeSpan.FromSeconds(10), "a stable layout");
        return render;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-28-03")]
    public Task Horizontal_scroll_keeps_the_offset_column() => UiTestContext.RunAsync(async ctx =>
    {
        // 1 行のバイト数の設定 (VIEW-08) はフェーズ 1 のため、1 行 16 バイトのまま、ウィンドウを最小の幅にして文字を大きくし
        // (Windows の文字サイズ 250% を模擬)、Hex 表示部分の幅を表示領域より広くする。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("resize", new JsonObject { ["width"] = 640, ["height"] = 600 });
        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = 2.5 });
        JsonObject render = null!;
        await app.WaitUntilAsync(async () =>
        {
            await app.IdleAsync();
            render = await app.RenderAsync();
            return render["horizontalBarVisible"]!.GetValue<bool>();
        }, TimeSpan.FromSeconds(10), "the horizontal scroll bar");

        // 文字の大きさの変更による配置の変化が落ち着くまで待つ (速いリリースのビルドでは、横のスクロールバーが出た時点ではまだ変わる)。
        render = await StableRenderAsync(app);
        double cell = render["cellWidth"]!.GetValue<double>();
        double offsetX = render["offsetLeft"]!.GetValue<double>();
        double hexX = CellPoint(render, 0).X;

        // 2〜3. Shift+ホイール (手前に回す) で右に 10 ノッチ: Hex 列は 30 セル分左にずれ、オフセット列は動かない。
        await WheelAsync(app, -120, count: 10, shift: true);
        render = await app.RenderAsync();
        Assert.Equal(offsetX, render["offsetLeft"]!.GetValue<double>());
        Assert.Equal(hexX - 30 * cell, CellPoint(render, 0).X, 3);
        Assert.Equal(offsetX + 2 * cell, OffsetColumnPoint(render, 0).X, 3);

        // 4. チルトホイールの右方向で、さらに 3 セル分。
        await WheelAsync(app, 120, horizontal: true);
        render = await app.RenderAsync();
        Assert.Equal(hexX - 33 * cell, CellPoint(render, 0).X, 3);
    });

    [Fact]
    public Task Offset_column_scrolls_when_not_fixed() => UiTestContext.RunAsync(async ctx =>
    {
        // VIEW-28 の仕様 5: 設定「オフセット列を固定」(既定オン) をオフにすると、オフセット列も横にスクロールする。
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.scroll.fixedOffsetColumn"] = false });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("resize", new JsonObject { ["width"] = 640, ["height"] = 600 });
        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = 2.5 });
        JsonObject render = null!;
        await app.WaitUntilAsync(async () =>
        {
            await app.IdleAsync();
            render = await app.RenderAsync();
            return render["horizontalBarVisible"]!.GetValue<bool>();
        }, TimeSpan.FromSeconds(10), "the horizontal scroll bar");
        render = await StableRenderAsync(app);
        double cell = render["cellWidth"]!.GetValue<double>();
        double offsetX = render["offsetLeft"]!.GetValue<double>();
        double hexX = CellPoint(render, 0).X;
        double contentLeft = render["contentLeft"]!.GetValue<double>();

        await WheelAsync(app, -120, count: 10, shift: true);
        await app.IdleAsync();
        render = await app.RenderAsync();
        Assert.Equal(hexX - 30 * cell, CellPoint(render, 0).X, 3);
        Assert.Equal(offsetX - Math.Min(30 * cell, contentLeft), render["offsetLeft"]!.GetValue<double>(), 3);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-28-04")]
    public Task Touch_scrolls_smoothly_with_inertia() => UiTestContext.RunAsync(async ctx =>
    {
        // タッチ入力の注入 (InjectSyntheticPointerInput) はシステム全体の入力になるため、アプリの中のタッチの処理に直接渡す。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();
        double h = render["rowHeight"]!.GetValue<double>();
        double x = render["surfaceWidth"]!.GetValue<double>() / 2;
        double y = render["surfaceHeight"]!.GetValue<double>() / 2;

        await PointerAsync(app, "down", (x, y), "touch");
        await PointerAsync(app, "move", (x, y - h * 0.4), "touch");
        render = await app.RenderAsync();
        Assert.Equal(0, render["topRow"]!.GetValue<long>());
        Assert.InRange(render["subRowOffset"]!.GetValue<double>(), h * 0.4 - 1, h * 0.4 + 1);

        await PointerAsync(app, "move", (x, y - h * 0.8), "touch");
        render = await app.RenderAsync();
        Assert.Equal(0, render["topRow"]!.GetValue<long>());
        Assert.InRange(render["subRowOffset"]!.GetValue<double>(), h * 0.8 - 1, h * 0.8 + 1);

        // 3. 速く上へはじいて離すと、離した後も行番号が増え続け、やがて止まる。
        double fy = y - h * 0.8;
        for (int i = 0; i < 3; i++)
        {
            fy -= 40;
            await PointerAsync(app, "move", (x, fy), "touch");
        }

        await PointerAsync(app, "up", (x, fy), "touch");
        long released = await TopRowAsync(app);
        var samples = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(50);
            samples.Add(await TopRowAsync(app));
        }

        Assert.True(samples[^1] > released, $"released at {released}, then {string.Join(",", samples)}");
        await app.WaitUntilAsync(async () =>
        {
            long a = await TopRowAsync(app);
            await Task.Delay(200);
            return a == await TopRowAsync(app);
        }, TimeSpan.FromSeconds(20), "the inertia to stop");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-28-06")]
    public Task Right_brings_an_offscreen_cursor_back() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setSystem", new JsonObject { ["wheelScrollLines"] = 3 });
        await app.GoToAsync(0x100);
        await WheelAsync(app, -120, count: 100);
        long top = await TopRowAsync(app);
        int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        Assert.Equal(300, top);
        Assert.False((await app.RenderAsync())["caret"]!["visible"]!.GetValue<bool>());

        await app.KeyAsync("Right");
        Assert.Equal("Offset: 0x00000101", await app.UiaNameAsync("Status_Offset"));
        top = await TopRowAsync(app);
        Assert.InRange(16, top, top + v - 1);
    });

    // ---- VIEW-29 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-29-04")]
    public Task Read_function_jumps_to_the_pe_header() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-PE-X64 の代わりに、テスト対象の HexEditor の実行ファイル (x64 の Windows 実行ファイル) の複製を使う。
        string exe = Path.Combine(ctx.Root, "pe-x64.exe");
        File.Copy(AppLocator.ImagePath, exe);
        byte[] head = File.ReadAllBytes(exe).AsSpan(0, 0x40).ToArray();
        long lfanew = BitConverter.ToUInt32(head, 0x3C);
        long length = new FileInfo(exe).Length;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [exe] });

        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "u32le(0x3C)");
        Assert.Equal($"= 0x{lfanew:X} ({lfanew:N0})", await app.UiaNameAsync("GoTo_Interpretation"));
        await app.SendAsync("goToKey", new JsonObject { ["key"] = "Enter" });
        Assert.Equal(lfanew, await CursorAsync(app));
        Assert.Equal(new byte[] { 0x50, 0x45, 0x00, 0x00 }, await app.BytesAsync(lfanew, 4));

        // 3. ファイルの外を読む式は、赤枠と読み取れない位置の説明。
        string outside = "0x" + (length + 0x100000).ToString("X");
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", $"u32le({outside})");
        Assert.True((await ElementAsync(app, "GoTo_Input"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("can't be read", await app.UiaNameAsync("GoTo_Interpretation"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-29-07")]
    public Task Shift_enter_keeps_the_bar_and_escape_closes_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0x400");
        await app.SendAsync("goToKey", new JsonObject { ["key"] = "Enter", ["shift"] = true });
        await app.IdleAsync();
        JsonObject state = await app.StateAsync();
        Assert.True(state["goToBarVisible"]!.GetValue<bool>());
        Assert.Equal("TextBox:GoTo_Input", state["focused"]!.GetValue<string>());
        Assert.Equal("Offset: 0x00000400", await app.UiaNameAsync("Status_Offset"));

        await app.UiaSetValueAsync("GoTo_Input", "0x800");
        await app.SendAsync("goToKey", new JsonObject { ["key"] = "Escape" });
        await app.IdleAsync();
        Assert.False((await app.StateAsync())["goToBarVisible"]!.GetValue<bool>());
        Assert.Equal("Offset: 0x00000400", await app.UiaNameAsync("Status_Offset"));
    });

    [Theory]
    [InlineData(640)]
    [InlineData(1024)]
    [InlineData(1920)]
    [Trait(UiTest.TC, "TC-VIEW-29-10")]
    public Task Go_to_bar_fits_in_german(int width) => UiTestContext.RunAsync(async ctx =>
    {
        // ベースアドレス (アドレス / オフセットの切り替え) と疑似翻訳はまだないため、ドイツ語の表示だけを確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "de", Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("resize", new JsonObject { ["width"] = width, ["height"] = 700 });
        await app.IdleAsync();
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0x1F00");
        await app.IdleAsync();
        app.Screenshot().Save(Path.Combine(UiTestContext.ArtifactsRoot, "TC-VIEW-29-10", $"goto-de-{width}.bmp"));

        JsonObject bar = (await ElementAsync(app, "GoToBar"))["bounds"]!.AsObject();
        var rects = new List<(string Id, double X, double Y, double W, double H)>();
        foreach (string id in new[] { "GoTo_Input", "GoTo_History", "GoTo_Interpretation", "GoTo_Base", "GoTo_Unit", "GoTo_Select", "GoTo_Go", "GoTo_Close" })
        {
            JsonObject e = await ElementAsync(app, id);
            Assert.True(e["found"]!.GetValue<bool>(), id);
            Assert.False(e["isTextTrimmed"]?.GetValue<bool>() ?? false, $"{id} is trimmed.");
            JsonObject b = e["bounds"]!.AsObject();
            (string, double X, double Y, double W, double H) r = (id, b["x"]!.GetValue<double>(), b["y"]!.GetValue<double>(),
                b["width"]!.GetValue<double>(), b["height"]!.GetValue<double>());
            Assert.True(r.W > 0 && r.H > 0, $"{id} has no size.");

            // 移動バーの中に収まる。
            Assert.True(r.X >= bar["x"]!.GetValue<double>() - 0.5 && r.Y >= bar["y"]!.GetValue<double>() - 0.5
                && r.X + r.W <= bar["x"]!.GetValue<double>() + bar["width"]!.GetValue<double>() + 0.5
                && r.Y + r.H <= bar["y"]!.GetValue<double>() + bar["height"]!.GetValue<double>() + 0.5, $"{id} is outside the bar.");
            rects.Add(r);
        }

        // 互いに重ならない。
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                bool overlap = a.X < b.X + b.W - 0.5 && b.X < a.X + a.W - 0.5 && a.Y < b.Y + b.H - 0.5 && b.Y < a.Y + a.H - 0.5;
                Assert.False(overlap, $"{a.Id} overlaps {b.Id}.");
            }
        }

        // 幅が足りないときは選択項目を 2 段目に折り返す (VIEW-29 の「画面」)。
        double inputY = rects.Single(r => r.Id == "GoTo_Input").Y;
        double baseY = rects.Single(r => r.Id == "GoTo_Base").Y;
        if (width <= 640)
        {
            Assert.True(baseY > inputY + 10, $"The options are not wrapped (input {inputY}, base {baseY}).");
        }
        else if (width >= 1920)
        {
            Assert.True(Math.Abs(baseY - inputY) < 10, $"The options are wrapped at {width} (input {inputY}, base {baseY}).");
        }
    }, name: $"{nameof(Go_to_bar_fits_in_german)}_{width}");

    [Fact]
    public Task Go_to_history_is_kept_across_sessions() => UiTestContext.RunAsync(async ctx =>
    {
        // VIEW-29 の仕様 12: 入力の履歴を最大 20 件保存し、↑ / ↓ か一覧から呼び出せる。セッションをまたいで保存する。
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        foreach (string input in new[] { "0x100", "0x200", "0x300" })
        {
            await GoToAsync(app, input);
        }

        await CommandTests.ExitAsync(app);
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenGoToAsync(again);
        Assert.Equal("0x300", (await ElementAsync(again, "GoTo_Input"))["text"]!.GetValue<string>());
        await again.SendAsync("goToKey", new JsonObject { ["key"] = "Up" });
        await again.SendAsync("goToKey", new JsonObject { ["key"] = "Up" });
        Assert.Equal("0x200", (await ElementAsync(again, "GoTo_Input"))["text"]!.GetValue<string>());
        await again.SendAsync("goToKey", new JsonObject { ["key"] = "Up" });
        Assert.Equal("0x100", (await ElementAsync(again, "GoTo_Input"))["text"]!.GetValue<string>());
        await again.SendAsync("goToKey", new JsonObject { ["key"] = "Down" });
        Assert.Equal("0x200", (await ElementAsync(again, "GoTo_Input"))["text"]!.GetValue<string>());
        Assert.True((await ElementAsync(again, "GoTo_History"))["found"]!.GetValue<bool>());
    });

    // ---- VIEW-34 (設定の読み込みと移動バーからのジャンプ。規則そのものは Core の CursorPlacementTests) ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-34-01")]
    public Task Jump_places_the_target_by_the_setting() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        (long row, int v) = await JumpAsync(app, 0x80000);
        Assert.Equal(v / 3, row);

        // 3. 「中央」。設定ファイルを書き換えると、外部の編集として読み込まれる (UI-23 の仕様 7)。
        WriteSettings(profile, new JsonObject { ["view.jump.position"] = "center" });
        await app.WaitUntilAsync(async () =>
        {
            (row, v) = await JumpAsync(app, 0x40000);
            return row == v / 2;
        }, TimeSpan.FromSeconds(10), "the setting to be applied");

        // 4. 「上端」。
        WriteSettings(profile, new JsonObject { ["view.jump.position"] = "top" });
        await app.WaitUntilAsync(async () =>
        {
            (row, v) = await JumpAsync(app, 0x20000);
            return row == 0;
        }, TimeSpan.FromSeconds(10), "the setting to be applied");

        // 先頭から移動バーでジャンプし、カーソルの行が表示領域の上から何行目かと、ジャンプしたときの表示行数を返す
        // (移動バーを開いている間は表示領域が狭くなるため、表示行数は移動バーを開いた状態で読む)。
        static async Task<(long Row, int Visible)> JumpAsync(AppSession app, long target)
        {
            await app.KeyAsync("Home", ctrl: true);
            await OpenGoToAsync(app);
            await app.UiaSetValueAsync("GoTo_Input", "0x" + target.ToString("X"));
            int v = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
            await app.SendAsync("goToKey", new JsonObject { ["key"] = "Enter" });
            await app.IdleAsync();
            Assert.Equal(target, await CursorAsync(app));
            return (target / 16 - await TopRowAsync(app), v);
        }
    });

    // ---- VIEW-40 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-40-02")]
    public Task Selection_item_and_its_tool_tip() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.Equal(string.Empty, app.TextOrEmpty("Status_Selection"));

        // 2. Ctrl+E (範囲を選択。EDIT-04 はフェーズ 1) の代わりに、命令で選択する。
        await app.SelectAsync(0x1F00, 0x100);
        Assert.Equal("Selection: 0x1F00–0x1FFF (length 0x100 = 256)", await app.UiaNameAsync("Status_Selection"));

        // 4. ツールチップ (マウスを合わせる代わりに、項目に設定されたツールチップの文字列を読む)。
        string tip = (await ElementAsync(app, "Status_Selection"))["toolTip"]!.GetValue<string>();
        Assert.Contains("Start: 0x00001F00", tip);
        Assert.Contains("0x00001FFF", tip);
        Assert.Contains("0x100", tip);
        Assert.Contains("256", tip);
    });

    [Theory]
    [InlineData("en", "Size: 1,50 GB (1.610.612.736 bytes)")]
    [InlineData("ja", "1,50 GB")]
    [Trait(UiTest.TC, "TC-VIEW-40-04")]
    public Task File_size_in_german_region(string language, string expected) => UiTestContext.RunAsync(async ctx =>
    {
        // TD-VIEW-SPARSE-1536M: 1,610,612,736 バイトのスパースファイル。
        string path = Path.Combine(ctx.Root, "TD-VIEW-SPARSE-1536M.bin");
        SparseFile.Create(path, 1536 * MiB);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = language,
            Files = [path],
            Hooks = new JsonObject { ["culture"] = "de-DE" },
        });

        string size = await app.UiaNameAsync("Status_Size");
        Assert.Contains(expected, size);
        Assert.Contains("1.610.612.736", size);

        await app.GoToAsync(0x1F00);
        Assert.Contains("0x00001F00", await app.UiaNameAsync("Status_Offset"));
    }, name: $"{nameof(File_size_in_german_region)}_{language}");

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-40-05")]
    public Task Hex_values_stay_left_to_right_in_arabic() => UiTestContext.RunAsync(async ctx =>
    {
        // 手順 2 (画像の文字認識) の代わりに、値の要素が左から右で、文字列がこの順で入っていることを確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x1F00);
        await app.IdleAsync();
        JsonObject offset = await ElementAsync(app, "Status_Offset");
        Assert.Equal("LeftToRight", offset["flowDirection"]!.GetValue<string>());
        Assert.Contains("0x00001F00", offset["content"]!.GetValue<string>());
        Assert.Equal("RightToLeft", (await ElementAsync(app, "StatusBar"))["flowDirection"]!.GetValue<string>());
        app.Screenshot().Save(Path.Combine(UiTestContext.ArtifactsRoot, "TC-VIEW-40-05", "arabic-status.bmp"));
    });

    // ---- VIEW-41 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-41-03")]
    public Task Focus_frame_follows_the_focus() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // フォーカスの枠はフォーカスが来た後の描画で付くため、描かれるまで待つ (負荷の高い CI のランナーで遅れる)。
        JsonObject render = null!;
        await app.WaitUntilAsync(async () =>
        {
            render = await app.RenderAsync();
            return render["focused"]!.GetValue<bool>() && render["focusFrame"]!.GetValue<double>() > 0;
        }, TimeSpan.FromSeconds(10), "the focus frame");

        // システムのフォーカス表示と同じ 2 px (FocusVisualPrimaryThickness の既定)。
        Assert.Equal(2, render["focusFrame"]!.GetValue<double>());

        // 2. F6 で別の領域へ: 枠が消え、カーソルは点滅しない細い枠線になる。
        Assert.Equal("hexView", (await app.KeyAsync("F6"))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        render = await app.RenderAsync();
        Assert.False(render["focused"]!.GetValue<bool>());
        Assert.Equal(0, render["focusFrame"]!.GetValue<double>());
        Assert.False(render["caretBlinking"]!.GetValue<bool>());
        Assert.Equal("box", render["caret"]!["shape"]!.GetValue<string>());
        Assert.Equal(1, render["caret"]!["strokeThickness"]!.GetValue<double>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-41-04")]
    public Task Modified_byte_is_announced() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");
        await app.SendAsync("announcements", new JsonObject { ["clear"] = true });

        await app.KeyAsync("Left");
        string text = await CursorAnnouncementAsync(app);
        Assert.Contains("0x00000010", text.Replace(" ", string.Empty));
        Assert.Contains("modified", text);

        await app.KeyAsync("Right");
        text = await CursorAnnouncementAsync(app);
        Assert.Contains("0x00000011", text.Replace(" ", string.Empty));
        Assert.DoesNotContain("modified", text);

        static async Task<string> CursorAnnouncementAsync(AppSession app)
        {
            string? found = null;
            await app.WaitUntilAsync(async () =>
            {
                JsonArray items = (await app.SendAsync("announcements", new JsonObject { ["clear"] = false }))["items"]!.AsArray();
                found = items.LastOrDefault(i => i!["id"]!.GetValue<string>() == "HexViewCursor")?["text"]?.GetValue<string>();
                return found is not null;
            }, TimeSpan.FromSeconds(5), "the cursor announcement");
            await app.SendAsync("announcements", new JsonObject { ["clear"] = true });
            return found!;
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-41-05")]
    public Task Windows_text_size_is_applied() => UiTestContext.RunAsync(async ctx =>
    {
        // Windows の文字サイズ (TextScaleFactor) は利用者の PC 全体の設定のため変えず、アプリが読む値をテスト用の命令で差し替えて
        // 変更の通知と同じ処理を行う。手順 3 の Ctrl+= (Hex 表示のズーム。UI-08) はまだないため確かめない。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = 1.0 });
        JsonObject before = await app.RenderAsync();
        double font = before["fontSize"]!.GetValue<double>();

        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = 1.5 });
        await app.IdleAsync();
        JsonObject after = await app.RenderAsync();

        // ± 0.5 pt (= 0.67 epx)。
        Assert.InRange(after["fontSize"]!.GetValue<double>(), font * 1.5 - 0.67, font * 1.5 + 0.67);
        Assert.True(after["cellWidth"]!.GetValue<double>() > before["cellWidth"]!.GetValue<double>());
        Assert.True(after["rowHeight"]!.GetValue<double>() > before["rowHeight"]!.GetValue<double>());
        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = null });
    });
}
