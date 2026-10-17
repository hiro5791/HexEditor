using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// インスペクタの拡張 (フェーズ 3): ポインタとして移動 (INSP-20)、配列として前後の要素に移動 (INSP-21)。右クリックメニューは Shift+F10 と
/// 同じキー (アプリケーションキー) で開き、項目は UI オートメーションで選ぶ (マウス・キーボードは使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class InspectorPhase3Tests
{
    /// <summary>行の右クリックメニュー「ポインタとして移動 &gt; (方法)」を選ぶ。</summary>
    private static async Task FollowPointerAsync(AppSession app, string type, string mode)
    {
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = type });
        await InspectorTests.PanelKeyAsync(app, "Application");
        (await app.WaitForAsync("Inspector_MenuPointer")).Patterns.ExpandCollapse.Pattern.Expand();
        await app.UiaInvokeAsync("Inspector_MenuPointer_" + mode);
        await app.IdleAsync();
    }

    private static async Task GoAsync(AppSession app, long offset)
    {
        await InspectorTests.GoAsync(app, offset);
        Assert.Equal(offset, await ViewOps.CursorAsync(app));
    }

    // ---- INSP-20 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-20-01")]
    public Task Follow_as_absolute_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-INSP-PTR");
        await GoAsync(app, 0x10);

        // 1・2: uint32 の 0x100 → オフセット 0x100。
        await FollowPointerAsync(app, "uint32", "AbsoluteOffset");
        Assert.Equal(0x100, await ViewOps.CursorAsync(app));

        // 3: 0x40 の 0x10000000 は範囲外 (長さ 0x2000): 移動せず InfoBar で知らせる。
        await GoAsync(app, 0x40);
        await FollowPointerAsync(app, "uint32", "AbsoluteOffset");
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("0x10000000", StringComparison.Ordinal), "the out-of-range notice");
        Assert.Contains("0x2000", notice["message"]!.GetValue<string>());
        Assert.Equal(0x40, await ViewOps.CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-20-02")]
    public Task Follow_as_absolute_address() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-INSP-PTR");
        await ViewSettingsOps.SetBaseAddressAsync(app, "0x400000");
        await GoAsync(app, 0x20);

        // uint32 の 0x401000 を表示上のアドレスとして、ベースアドレスを引いた 0x1000 へ。
        await FollowPointerAsync(app, "uint32", "AbsoluteAddress");
        Assert.Equal(0x1000, await ViewOps.CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-20-03")]
    public Task Relative_pointers_with_a_negative_value() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-INSP-PTR");

        // 1: int8 の -16 で「値の直後からの相対」: 0x30 + 1 − 16。
        await GoAsync(app, 0x30);
        await FollowPointerAsync(app, "int8", "RelativeToNext");
        Assert.Equal(0x21, await ViewOps.CursorAsync(app));

        // 2: 「カーソルからの相対」: 0x30 − 16。
        await GoAsync(app, 0x30);
        await FollowPointerAsync(app, "int8", "RelativeToCursor");
        Assert.Equal(0x20, await ViewOps.CursorAsync(app));

        // 3: 「基準を指定して相対」で基準 0x100: 0x100 − 16。
        await GoAsync(app, 0x30);
        await FollowPointerAsync(app, "int8", "RelativeToBase");
        await app.WaitForAsync("Inspector_PointerBase");
        Assert.True((await app.SendAsync("inspectorPointerBase", new JsonObject { ["text"] = "0x100" }))["submitted"]!.GetValue<bool>());
        await app.IdleAsync();
        Assert.Equal(0xF0, await ViewOps.CursorAsync(app));
        Assert.Equal("RelativeToBase", (await app.SendAsync("inspectorNav"))["pointerMode"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-20-04")]
    public Task Alt_left_returns_after_following_a_pointer() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-INSP-PTR");
        await GoAsync(app, 0x10);
        await FollowPointerAsync(app, "uint32", "AbsoluteOffset");
        Assert.Equal(0x100, await ViewOps.CursorAsync(app));

        // 2: Hex ビューにフォーカスを移して Alt+←。
        await app.FocusAsync("editor");
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Equal(0x10, await ViewOps.CursorAsync(app));
    });

    // ---- INSP-21 ----

    /// <summary>TD-SEQ-1M を開き、0 から Ctrl+G で 0x100 に移動し (履歴に 0 を記録)、uint32 の行で配列モードに入る。</summary>
    private static async Task<AppSession> StartArrayAsync(UiTestContext ctx)
    {
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-SEQ-1M");
        await ViewOps.GoToAsync(app, "0x100");
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal(0x100, await ViewOps.CursorAsync(app));
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "uint32" });
        await InspectorTests.PanelKeyAsync(app, "Application");
        await app.UiaInvokeAsync("Inspector_MenuArray");
        await app.IdleAsync();
        Assert.True((await NavAsync(app))["barShown"]!.GetValue<bool>(), "the array bar is not shown");
        return app;
    }

    private static Task<JsonObject> NavAsync(AppSession app) => app.SendAsync("inspectorNav");

    private static async Task StepAsync(AppSession app, string button, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            await app.UiaInvokeAsync(button);
            await InspectorTests.WaitForRowsAsync(app);
        }
    }

    private static async Task<HashSet<long>> BoundariesAsync(AppSession app)
    {
        await app.IdleAsync();
        JsonObject h = await app.SendAsync("highlights");
        return [.. h["segments"]!.AsArray().Select(s => s!.AsObject())
            .Where(s => s["tag"]!.GetValue<string>() == "inspectorArray" && s["column"]!.GetValue<string>() == "hex")
            .Select(s => s["first"]!.GetValue<long>())];
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-21-01")]
    public Task Next_element_in_uint32_array_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartArrayAsync(ctx);

        // 2・3: 「次の要素」を 3 回 → 0x10C、要素番号 3、0x100 から 4 バイトごとの境目に線。
        await StepAsync(app, "Inspector_ArrayNext", 3);
        Assert.Equal(0x10C, await ViewOps.CursorAsync(app));
        Assert.Equal("3", (await NavAsync(app))["index"]!.GetValue<string>());
        HashSet<long> lines = await BoundariesAsync(app);
        foreach (long at in new long[] { 0x100, 0x104, 0x108, 0x10C, 0x110 })
        {
            Assert.Contains(at, lines);
        }

        Assert.DoesNotContain(0x102L, lines);
        Assert.All(lines, at => Assert.Equal(0, (at - 0x100) % 4));

        // 4: Alt+← は配列の移動を飛ばして、Ctrl+G の前の 0 に戻る。
        await app.FocusAsync("editor");
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Equal(0, await ViewOps.CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-21-02")]
    public Task Stride_0x28_moves_40_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartArrayAsync(ctx);

        // 1・2: 間隔 0x28 → 「次の要素」2 回で 0x150。
        await app.UiaSetValueAsync("Inspector_ArrayStride", "0x28");
        Assert.True((await app.SendAsync("inspectorArrayInput", new JsonObject { ["field"] = "stride", ["text"] = "0x28" }))["ok"]!.GetValue<bool>());
        await StepAsync(app, "Inspector_ArrayNext", 2);
        Assert.Equal(0x150, await ViewOps.CursorAsync(app));

        // 3: 「前の要素」で 0x128、要素番号 1。
        await StepAsync(app, "Inspector_ArrayPrevious");
        Assert.Equal(0x128, await ViewOps.CursorAsync(app));
        Assert.Equal("1", (await NavAsync(app))["index"]!.GetValue<string>());

        // 4: Hex ビューで → を 1 回: 要素の境界ではない。
        await app.FocusAsync("editor");
        await app.KeyAsync("Right");
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal("(not on an element boundary)", (await NavAsync(app))["index"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-21-03")]
    public Task Element_number_input_moves_and_esc_exits() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartArrayAsync(ctx);

        // 1: 要素番号 10 → 0x100 + 4 × 10。2: −4 → 0xF0。
        Assert.True((await app.SendAsync("inspectorArrayInput", new JsonObject { ["field"] = "index", ["text"] = "10" }))["ok"]!.GetValue<bool>());
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal(0x128, await ViewOps.CursorAsync(app));
        Assert.True((await app.SendAsync("inspectorArrayInput", new JsonObject { ["field"] = "index", ["text"] = "-4" }))["ok"]!.GetValue<bool>());
        await InspectorTests.WaitForRowsAsync(app);
        Assert.Equal(0xF0, await ViewOps.CursorAsync(app));

        // 3: Ctrl+End の後は「次の要素」が無効 (範囲外)。
        await app.FocusAsync("editor");
        await app.KeyAsync("End", ctrl: true);
        await InspectorTests.WaitForRowsAsync(app);
        Assert.False((await NavAsync(app))["canNext"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("Inspector_ArrayNext")).IsEnabled);

        // 4: インスペクタにフォーカスを移して Esc → 配列モードの操作部が消える。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "uint32" });
        await InspectorTests.PanelKeyAsync(app, "Escape");
        await app.IdleAsync();
        JsonObject nav = await NavAsync(app);
        Assert.False(nav["arrayMode"]!.GetValue<bool>());
        Assert.False(nav["barShown"]!.GetValue<bool>());
        Assert.False(await app.IsShownAsync("Inspector_ArrayBar"));
    });
}
