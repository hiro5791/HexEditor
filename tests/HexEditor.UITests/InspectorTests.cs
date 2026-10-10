using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// データインスペクタ (INSP-01〜INSP-19)。キーは、パネルにフォーカスがあるときはパネルの要素に渡す (<c>panelKey</c>)。
/// 入力欄への入力は UI オートメーションの ValuePattern、ボタンは InvokePattern で行う (マウス・キーボードは使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class InspectorTests
{
    internal static async Task<AppSession> StartAsync(UiTestContext ctx, bool show = true, string data = "TD-INSP-VALUES", JsonObject? hooks = null,
        string? profile = null, string? file = null)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file ?? ctx.CopyTestData(data)], Hooks = hooks, Profile = profile });
        if (show)
        {
            await ShowAsync(app);
        }

        return app;
    }

    /// <summary>Ctrl+Shift+I で表示する (表示されていなければ)。</summary>
    internal static async Task ShowAsync(AppSession app)
    {
        if (!(await StateAsync(app))["visible"]!.GetValue<bool>())
        {
            Assert.Equal("menu:Command_ToggleInspector", (await app.KeyAsync("I", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        }

        await WaitForRowsAsync(app);
    }

    internal static Task<JsonObject> StateAsync(AppSession app) => app.SendAsync("inspector");

    /// <summary>ツールバーのエンディアンの選択を開いて項目を選ぶ (UI オートメーションの ExpandCollapse と Invoke)。</summary>
    internal static async Task ChooseEndianAsync(AppSession app, string item)
    {
        (await app.WaitForAsync("Inspector_Endian")).Patterns.ExpandCollapse.Pattern.Expand();
        await app.WaitForAsync(item);
        await app.CommandAsync(item);
        await WaitForRowsAsync(app);
    }

    /// <summary>行が読み込み済みの値になるまで待つ。</summary>
    internal static async Task WaitForRowsAsync(AppSession app)
    {
        await app.IdleAsync();
        await app.WaitUntilAsync(async () =>
        {
            JsonArray items = (await StateAsync(app))["items"]!.AsArray();
            return items.Count > 0 && items.All(i => i!["status"]!.GetValue<string>() != "Loading");
        }, TimeSpan.FromSeconds(15), "the inspector rows to load");
    }

    internal static async Task<JsonObject> RowAsync(AppSession app, string type, bool opposite = false)
    {
        string id = "Inspector_Row_" + type + (opposite ? "_Opposite" : string.Empty);
        return (await StateAsync(app))["items"]!.AsArray().Select(i => i!.AsObject()).FirstOrDefault(i => i["id"]!.GetValue<string>() == id)
            ?? throw new Xunit.Sdk.XunitException($"No inspector row {id}");
    }

    internal static async Task<string> ValueAsync(AppSession app, string type) => (await RowAsync(app, type))["value"]!.GetValue<string>();

    internal static async Task GoAsync(AppSession app, long offset)
    {
        await app.GoToAsync(offset);
        await WaitForRowsAsync(app);
    }

    internal static Task<JsonObject> PanelKeyAsync(AppSession app, string key, bool shift = false, bool ctrl = false) =>
        app.SendAsync("panelKey", new JsonObject { ["key"] = key, ["shift"] = shift, ["ctrl"] = ctrl });

    /// <summary>行を選び、Enter で入力欄を開き、値を入れる (確定はしない)。</summary>
    internal static async Task BeginEditAsync(AppSession app, string type, string text)
    {
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = type });
        await PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        Assert.True((await RowAsync(app, type))["editing"]!.GetValue<bool>(), "the input box did not open");

        // 入力欄は開いた後に今の値を入れて全選択する (リリースのビルドでは少し後になる)。その前に入れると今の値で上書きされるので、
        // 全選択が済んでから入れ、入ったことを確かめる。
        await app.WaitUntilAsync(async () => await app.ElementAsync("Inspector_Edit") is var box && box["found"]!.GetValue<bool>()
            && box["selectedText"]?.GetValue<string>() == box["text"]?.GetValue<string>(), TimeSpan.FromSeconds(5), "the input box to be ready");
        await app.UiaSetValueAsync("Inspector_Edit", text);
        await app.WaitUntilAsync(async () => (await app.ElementAsync("Inspector_Edit"))["text"]?.GetValue<string>() == text, TimeSpan.FromSeconds(5),
            "the typed value in the input box");
    }

    /// <summary>値を書き込む (入力して Enter)。</summary>
    internal static async Task WriteAsync(AppSession app, string type, string text)
    {
        await BeginEditAsync(app, type, text);
        await PanelKeyAsync(app, "Enter");
        await WaitForRowsAsync(app);
    }

    /// <summary>描画モデルの強調 (層・列ごとに、強調の付いたバイトの集合)。</summary>
    internal static async Task<HashSet<long>> HighlightedAsync(AppSession app, int layer, string column = "hex", string? tagPrefix = null)
    {
        await app.IdleAsync();
        JsonObject h = await app.SendAsync("highlights");
        var bytes = new HashSet<long>();
        foreach (JsonObject s in h["segments"]!.AsArray().Select(n => n!.AsObject()))
        {
            if (s["layer"]!.GetValue<int>() == layer && s["column"]!.GetValue<string>() == column
                && (tagPrefix is null || s["tag"]!.GetValue<string>().StartsWith(tagPrefix, StringComparison.Ordinal)))
            {
                for (long b = s["first"]!.GetValue<long>(); b <= s["last"]!.GetValue<long>(); b++)
                {
                    bytes.Add(b);
                }
            }
        }

        return bytes;
    }

    private static HashSet<long> Range(long start, long length) => [.. Enumerable.Range(0, (int)length).Select(i => start + i)];

    // ---- INSP-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-01-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_shift_i_toggles_the_panel() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, show: false);
        Assert.False((await StateAsync(app))["visible"]!.GetValue<bool>());
        Assert.False(await app.IsShownAsync("InspectorPanel"));

        // 1・2: 表示され (既定は右パネル)、起点が 0x00000000、int32 が 12,345。
        await app.KeyAsync("I", ctrl: true, shift: true);
        await WaitForRowsAsync(app);
        Assert.True(await app.IsShownAsync("InspectorPanel"));
        JsonObject panel = await app.ElementAsync("InspectorPanel");
        JsonObject view = await app.ElementAsync("HexView");
        Assert.True(panel["left"]!.GetValue<double>() >= view["right"]!.GetValue<double>() - 1, "the inspector is not on the right");
        Assert.Equal("From 0x00000000", (await StateAsync(app))["origin"]!.GetValue<string>());
        Assert.Equal("12,345", await ValueAsync(app, "int32"));

        // 3: もう一度で非表示。
        await app.KeyAsync("I", ctrl: true, shift: true);
        await app.IdleAsync();
        Assert.False(await app.IsShownAsync("InspectorPanel"));

        // 4: コマンド「データインスペクタの表示切り替え」(コマンドパレット F1-02 は UI の土台の担当。同じコマンドをメニューから実行する)。
        await app.CommandAsync("Command_ToggleInspector");
        await app.IdleAsync();
        await app.WaitUntilAsync(() => app.IsShownAsync("InspectorPanel"), TimeSpan.FromSeconds(10), "the inspector panel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-01-03")]
    public Task Rows_without_enough_data_show_a_dash() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ViewOps.GoToAsync(app, "end-2");
        await WaitForRowsAsync(app);
        Assert.Matches(@"^-?\d", await ValueAsync(app, "int8"));
        Assert.Matches(@"^-?\d", await ValueAsync(app, "int16"));
        foreach (string type in new[] { "int32", "int64", "double" })
        {
            Assert.Equal("—", await ValueAsync(app, type));
        }

        // 3: ツールチップで、4 バイト必要で残りが 2 バイトであることを示す。
        string tip = (await RowAsync(app, "int32"))["toolTip"]!.GetValue<string>();
        Assert.Contains("4", tip);
        Assert.Contains("2", tip);

        await ViewOps.GoToAsync(app, "end-4");
        await WaitForRowsAsync(app);
        Assert.Equal("0", await ValueAsync(app, "int32"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-01-05")]
    public Task Keyboard_and_row_names_for_screen_readers() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.FocusAsync("editor");

        // 1: F6 を、インスペクタの一覧にフォーカスが移るまで押す。
        int presses = 0;
        while (!(await StateAsync(app))["hasFocus"]!.GetValue<bool>())
        {
            Assert.True(++presses <= 5, "F6 did not reach the inspector");
            await PanelKeyAsync(app, "F6");
            await app.IdleAsync();
        }

        // 2・3: ↓ で int32 の行を選び、選ばれた行の名前が「int32, 12,345」。
        while ((await StateAsync(app))["selected"]?.GetValue<string>() != "Inspector_Row_int32")
        {
            await PanelKeyAsync(app, "Down");
        }

        await app.IdleAsync();
        Assert.Equal("int32, 12,345", (await RowAsync(app, "int32"))["automationName"]!.GetValue<string>());
        string? focused = await app.FocusedAsync();
        Assert.Equal("ListViewItem:Inspector_Row_int32", focused);
        FlaUI.Core.AutomationElements.AutomationElement? item = app.Find("Inspector_Row_int32");
        Assert.NotNull(item);
        Assert.Equal("int32, 12,345", AppSession.NameOf(item!));

        // 4: ↑ で 1 つ上の行 (uint16)。
        await PanelKeyAsync(app, "Up");
        await app.IdleAsync();
        Assert.Equal("ListViewItem:Inspector_Row_uint16", await app.FocusedAsync());
        Assert.Equal("uint16, 12,345", AppSession.NameOf(app.Find("Inspector_Row_uint16")!));

        // 5: Shift+F6 を同じ回数押すと Hex ビューに戻る。
        for (int i = 0; i < presses; i++)
        {
            await PanelKeyAsync(app, "F6", shift: true);
            await app.IdleAsync();
        }

        Assert.StartsWith("HexView", await app.FocusedAsync(), StringComparison.Ordinal);
    });

    // ---- INSP-02 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-02-04")]
    public Task Hex_format_shows_int16_minus_one_as_ffff() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_Base", ["index"] = 1 });
        await GoAsync(app, 0x60);
        Assert.Equal("0xFFFF", await ValueAsync(app, "int16"));
        Assert.Equal("0xFFFF", await ValueAsync(app, "uint16"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-02-05")]
    public Task Endianness_choice_is_kept_per_document() => UiTestContext.RunAsync(async ctx =>
    {
        string values = ctx.CopyTestData("TD-INSP-VALUES");
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await StartAsync(ctx, file: values);
        await ChooseEndianAsync(app, "Inspector_EndianBig");
        await WaitForRowsAsync(app);
        Assert.Equal("BE (inspector only)", (await StateAsync(app))["endian"]!.GetValue<string>());
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 2・3: 同じ設定フォルダで起動して開き直すと、BE (インスペクタのみ) のまま。
        AppSession again = await StartAsync(ctx, file: values, profile: ctx.DefaultProfile);
        Assert.Equal("BE (inspector only)", (await StateAsync(again))["endian"]!.GetValue<string>());

        // 4: 別のドキュメントは「ドキュメントに従う」(LE)。
        await again.OpenAsync(seq);
        await again.WaitForTabsAsync(2);
        await WaitForRowsAsync(again);
        Assert.Equal("LE", (await StateAsync(again))["endian"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-02-02")]
    public Task Following_the_document_switches_with_the_status_bar() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x10);

        // 1. ステータスバー・インスペクタとも LE、uint32 は 67,305,985 (01 02 03 04)。
        Assert.Equal("LE", await app.UiaNameAsync("Status_Endian"));
        Assert.Equal("LE", (await StateAsync(app))["endian"]!.GetValue<string>());
        Assert.Equal("67,305,985", await ValueAsync(app, "uint32"));

        // 2〜3. ステータスバーのエンディアン表示を押すと、ドキュメントに従うインスペクタも BE になる。
        await app.UiaInvokeAsync("Status_Endian");
        await app.IdleAsync();
        await WaitForRowsAsync(app);
        Assert.Equal("BE", await app.UiaNameAsync("Status_Endian"));
        Assert.Equal("BE", (await StateAsync(app))["endian"]!.GetValue<string>());
        Assert.Equal("16,909,060", await ValueAsync(app, "uint32"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-02-03")]
    public Task Inspector_only_big_endian_ignores_the_document_endianness() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.CommandAsync("Command_ViewCellFormatInt16Hex");
        await app.IdleAsync();
        await GoAsync(app, 0x10);

        // 1〜2. インスペクタだけ BE: ステータスバーは LE、uint32 は BE の値、セルは LE のまま (0201)。
        await ChooseEndianAsync(app, "Inspector_EndianBig");
        Assert.Equal("LE", await app.UiaNameAsync("Status_Endian"));
        Assert.Equal("BE (inspector only)", (await StateAsync(app))["endian"]!.GetValue<string>());
        Assert.Equal("16,909,060", await ValueAsync(app, "uint32"));
        Assert.Equal("0201", ViewOps.CellOf(await app.RenderAsync(), 0x10)!["hex"]!.GetValue<string>());

        // 3〜4. ステータスバーを押すと、セルは BE (0102) になり、インスペクタは BE (インスペクタのみ) のまま。
        await app.UiaInvokeAsync("Status_Endian");
        await app.IdleAsync();
        await WaitForRowsAsync(app);
        Assert.Equal("BE", await app.UiaNameAsync("Status_Endian"));
        Assert.Equal("0102", ViewOps.CellOf(await app.RenderAsync(), 0x10)!["hex"]!.GetValue<string>());
        Assert.Equal("BE (inspector only)", (await StateAsync(app))["endian"]!.GetValue<string>());
        Assert.Equal("16,909,060", await ValueAsync(app, "uint32"));
    });

    // ---- INSP-03 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-03-02")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Integer_rows() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ViewOps.GoToAsync(app, "0x60");
        await WaitForRowsAsync(app);
        Assert.Equal("-1", await ValueAsync(app, "int8"));
        Assert.Equal("255", await ValueAsync(app, "uint8"));
        Assert.Equal("-1", await ValueAsync(app, "int64"));
        Assert.Equal("18,446,744,073,709,551,615", await ValueAsync(app, "uint64"));
    });

    // ---- INSP-05 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-05-02")]
    public Task Writing_point_one_to_the_float_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x80);
        await BeginEditAsync(app, "float", "0.1");
        Assert.Equal("CD CC CC 3D", await app.UiaNameAsync("Inspector_EditPreview"));
        await PanelKeyAsync(app, "Enter");
        await WaitForRowsAsync(app);
        Assert.Equal(Convert.FromHexString("CDCCCC3D"), await app.BytesAsync(0x80, 4));
        Assert.Equal("Stored value: 0.100000001490116", (await StateAsync(app))["stored"]!.GetValue<string>());
    });

    // ---- INSP-08 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-08-01")]
    public Task Binary_row_shows_nibbles() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ViewOps.GoToAsync(app, "0x20");
        await WaitForRowsAsync(app);
        Assert.Equal("0100 0001", await ValueAsync(app, "binary8"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-08-02")]
    public Task Clicking_a_bit_flips_it_and_undo_restores() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x20);

        // 1: 最下位ビットのボタンのツールチップがビット番号 0 を示し、Invoke で押す (行を表示させてから探す)。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "binary8" });

        // 行は一覧の下の方にあり、スクロールして作られるまで待つ。
        JsonObject bit = new();
        await app.WaitUntilAsync(async () => (bit = await app.ElementAsync("Inspector_Bit_binary8_0"))["found"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(10), "the bit buttons");
        Assert.Equal("Bit 0", bit["toolTip"]!.GetValue<string>());
        await app.UiaInvokeAsync("Inspector_Bit_binary8_0");
        await WaitForRowsAsync(app);

        // 2: 40 と 0100 0000。
        Assert.Equal([0x40], await app.BytesAsync(0x20, 1));
        Assert.Equal("0100 0000", await ValueAsync(app, "binary8"));

        // 3: Hex ビューで Ctrl+Z を 1 回押すと 41 に戻る。
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal([0x41], await app.BytesAsync(0x20, 1));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-08-03")]
    public Task Bits_can_be_flipped_with_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x20);
        await app.FocusAsync("editor");
        while (!(await StateAsync(app))["hasFocus"]!.GetValue<bool>())
        {
            await PanelKeyAsync(app, "F6");
        }

        while ((await StateAsync(app))["selected"]?.GetValue<string>() != "Inspector_Row_binary8")
        {
            await PanelKeyAsync(app, "Down");
        }

        // 2: Tab でビットのボタンへ (最上位 = 7)。3: → で 6。
        // 選んだ行のビットのボタンは、行を選んだ少し後に XAML の木に入る。入る前に Tab を押すと、移る先がない。
        await app.WaitUntilAsync(async () => (await app.ElementAsync("Inspector_Bit_binary8_7"))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(5),
            "the bit buttons");
        await app.IdleAsync();
        await PanelKeyAsync(app, "Tab");
        await app.WaitUntilAsync(async () => (await StateAsync(app))["focusedBit"]?.GetValue<int>() == 7, TimeSpan.FromSeconds(5), "bit 7 focused");

        await PanelKeyAsync(app, "Right");
        await app.WaitUntilAsync(async () => (await StateAsync(app))["focusedBit"]?.GetValue<int>() == 6, TimeSpan.FromSeconds(5), "bit 6 focused");

        // 4: Space で反転 (41 → 01)。
        await PanelKeyAsync(app, "Space");
        await WaitForRowsAsync(app);
        Assert.Equal([0x01], await app.BytesAsync(0x20, 1));
    });

    // ---- INSP-09 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-09-02")]
    public Task Character_rows() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x18);
        Assert.Equal("あ  U+3042  (3 bytes)", await ValueAsync(app, "utf8"));
    });

    // ---- INSP-15 ----

    private static JsonObject Tokyo => new() { ["timeZone"] = "Tokyo Standard Time" };

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-15-01")]
    public Task Local_time_and_utc() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, hooks: Tokyo);
        await GoAsync(app, 0x80);
        string local = await ValueAsync(app, "unix32");
        Assert.Contains("09:00:00", local);
        Assert.EndsWith("(UTC+09:00)", local);
        Assert.Contains("1970", local);

        await app.CommandAsync("Inspector_Utc");
        await WaitForRowsAsync(app);
        string utc = await ValueAsync(app, "unix32");
        Assert.Contains("00:00:00", utc);
        Assert.EndsWith("(UTC)", utc);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-15-02")]
    public Task Formats_without_a_time_zone_do_not_change() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, hooks: Tokyo);
        await GoAsync(app, 0x90);
        string local = await ValueAsync(app, "dosdatetime");
        Assert.Contains("1980", local);
        Assert.Contains("00:00:00", local);
        Assert.EndsWith("(no time zone)", local);
        await app.CommandAsync("Inspector_Utc");
        await WaitForRowsAsync(app);
        Assert.Equal(local, await ValueAsync(app, "dosdatetime"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-15-03")]
    public Task Iso_8601_input_for_filetime() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, hooks: Tokyo);
        await GoAsync(app, 0x80);
        byte[] expected = Convert.FromHexString("0040C1CBEE55DD01");
        await WriteAsync(app, "filetime", "2026-10-07T00:00:00Z");
        Assert.Equal(expected, await app.BytesAsync(0x80, 8));

        // 3: Undo の後、ローカル時刻 (東京) でタイムゾーンのない入力。
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.GoToAsync(0x80);
        await WaitForRowsAsync(app);
        await WriteAsync(app, "filetime", "2026-10-07 09:00:00");
        Assert.Equal(expected, await app.BytesAsync(0x80, 8));

        // 4: 地域設定の書式は受け付けず、説明文が出て確定できない。
        await BeginEditAsync(app, "filetime", "2026/10/07");
        JsonObject row = await RowAsync(app, "filetime");
        Assert.Contains("2026-10-07 12:34:56", row["error"]!.GetValue<string>());
        Assert.True((await app.ElementAsync("Inspector_Edit"))["errorBorder"]!.GetValue<bool>());
        await PanelKeyAsync(app, "Enter");
        Assert.True((await RowAsync(app, "filetime"))["editing"]!.GetValue<bool>());
        Assert.Equal(expected, await app.BytesAsync(0x80, 8));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-15-04")]
    public Task German_regional_format_does_not_change_date_input() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, hooks: new JsonObject { ["culture"] = "de-DE", ["timeZone"] = "UTC" });
        await GoAsync(app, 0x80);
        await WriteAsync(app, "unix32", "2026-10-07 12:34:56");
        Assert.Equal(Convert.FromHexString("703CC66A"), await app.BytesAsync(0x80, 4));
        string shown = await ValueAsync(app, "unix32");
        Assert.StartsWith("07.10.2026 12:34:56", shown);

        await BeginEditAsync(app, "unix32", "07.10.2026 12:34:56");
        Assert.NotEqual(string.Empty, (await RowAsync(app, "unix32"))["error"]!.GetValue<string>());
        Assert.True((await app.ElementAsync("Inspector_Edit"))["errorBorder"]!.GetValue<bool>());
    });

    // ---- INSP-17 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-17-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Writing_0x1234_to_uint16() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x80);

        // 1: 入力欄に現在の値 0 が全選択で入る。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "uint16" });
        await PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        // 全選択は入力欄にフォーカスが移った後に行われる (リリースのビルドでは少し後になる) ので、待つ。
        JsonObject box = null!;
        await app.WaitUntilAsync(async () => (box = await app.ElementAsync("Inspector_Edit"))["selectedText"]?.GetValue<string>() == "0",
            TimeSpan.FromSeconds(5), "the whole value selected in the input box");
        Assert.Equal("0", box["text"]!.GetValue<string>());
        Assert.Equal("0", box["selectedText"]!.GetValue<string>());

        // 2: 書き込まれるバイト列と、書き込む範囲の強調。
        await app.UiaSetValueAsync("Inspector_Edit", "0x1234");
        Assert.Equal("34 12", await app.UiaNameAsync("Inspector_EditPreview"));
        Assert.Equal(Range(0x80, 2), await HighlightedAsync(app, 3));

        // 3・4: Enter で書き込み、カーソルは動かない。
        await PanelKeyAsync(app, "Enter");
        await WaitForRowsAsync(app);
        Assert.Equal(Convert.FromHexString("3412"), await app.BytesAsync(0x80, 2));
        Assert.Equal(0x80, await ViewOps.CursorAsync(app));

        // 5: 入力式 cur+2 (= 0x82)。
        await WriteAsync(app, "uint16", "cur+2");
        Assert.Equal(Convert.FromHexString("8200"), await app.BytesAsync(0x80, 2));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-17-02")]
    public Task Insert_mode_does_not_change_the_length() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x80);
        await app.KeyAsync("Insert");
        Assert.True((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());
        await WriteAsync(app, "uint32", "0xAABBCCDD");
        Assert.Equal(256, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(Convert.FromHexString("DDCCBBAA00"), await app.BytesAsync(0x80, 5));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-17-03")]
    public Task One_undo_reverts_a_write() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x80);
        await WriteAsync(app, "uint64", "0x1122334455667788");
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(new byte[8], await app.BytesAsync(0x80, 8));
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        await app.KeyAsync("Y", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(Convert.FromHexString("8877665544332211"), await app.BytesAsync(0x80, 8));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-17-04")]
    public Task Writing_past_the_end_is_rejected() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ViewOps.GoToAsync(app, "end-2");
        await WaitForRowsAsync(app);
        byte[] before = await app.BytesAsync(0xFE, 2);
        await BeginEditAsync(app, "int32", "1");
        Assert.Contains("end", (await RowAsync(app, "int32"))["error"]!.GetValue<string>());
        await PanelKeyAsync(app, "Enter");
        Assert.True((await RowAsync(app, "int32"))["editing"]!.GetValue<bool>());
        Assert.Equal(before, await app.BytesAsync(0xFE, 2));
        Assert.Equal(256, (await app.DocumentAsync())["length"]!.GetValue<long>());
        await PanelKeyAsync(app, "Escape");

        // 5: 末尾ちょうどまでは書き込める。
        await ViewOps.GoToAsync(app, "end-4");
        await WaitForRowsAsync(app);
        await WriteAsync(app, "int32", "1");
        Assert.Equal(Convert.FromHexString("01000000"), await app.BytesAsync(0xFC, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-17-05")]
    public Task Escape_and_losing_focus_cancel() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x80);
        await BeginEditAsync(app, "uint16", "0xFFFF");
        await PanelKeyAsync(app, "Escape");
        await app.IdleAsync();
        Assert.Equal(new byte[2], await app.BytesAsync(0x80, 2));
        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());

        // 3・4: フォーカスを Hex ビューに移すと取り消す。
        await BeginEditAsync(app, "uint16", "0xFFFF");
        await app.FocusAsync("editor");
        await app.IdleAsync();
        Assert.False((await RowAsync(app, "uint16"))["editing"]!.GetValue<bool>());
        Assert.Equal(new byte[2], await app.BytesAsync(0x80, 2));
        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());
    });

    // ---- INSP-18 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-18-01")]
    public Task Hovering_a_row_highlights_its_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await GoAsync(app, 0x10);
        await app.SendAsync("inspectorHover", new JsonObject { ["id"] = "int32" });
        Assert.Equal(Range(0x10, 4), await HighlightedAsync(app, 3, "hex"));
        Assert.Equal(Range(0x10, 4), await HighlightedAsync(app, 3, "text"));
        JsonObject first = (await app.SendAsync("highlights"))["segments"]!.AsArray().Select(s => s!.AsObject()).First(s => s["layer"]!.GetValue<int>() == 3);
        Assert.NotNull(first["background"]);
        Assert.NotNull(first["border"]);

        // 3: マウスを外すと強調がない (選んでいる行もない)。
        await app.SendAsync("inspectorHover", new JsonObject { ["id"] = null });
        Assert.Empty(await HighlightedAsync(app, 3));

        // 4: キーボードで int64 の行を選ぶ。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "int64" });
        Assert.Equal(Range(0x10, 8), await HighlightedAsync(app, 3));

        // 5: 設定「インスペクタの対象を強調する」をオフにする (設定ファイルの外部の編集として反映する)。
        ViewOps.WriteSettings(ctx.DefaultProfile!, new JsonObject { ["inspector.highlightTarget"] = false });
        await app.WaitUntilAsync(async () => (await HighlightedAsync(app, 3)).Count == 0, TimeSpan.FromSeconds(10), "the highlight to disappear");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-18-03")]
    public Task Highlighting_does_not_change_the_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.SelectAsync(0x10, 2);
        await WaitForRowsAsync(app);
        await app.SendAsync("inspectorHover", new JsonObject { ["id"] = "int64" });
        await ViewOps.AssertSelectionAsync(app, 0x10, 2);
        JsonObject render = await app.RenderAsync();
        JsonObject row = ViewOps.Row(render, 0x10)!;
        var selected = row["cells"]!.AsArray().Select((c, i) => (c, i)).Where(p => p.c!["selected"]!.GetValue<bool>()).Select(p => 0x10L + p.i).ToHashSet();
        Assert.Equal(Range(0x10, 2), selected);
        Assert.Equal(Range(0x10, 8), await HighlightedAsync(app, 3));
    });

    // ---- INSP-19 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-19-01")]
    public Task Hide_a_row_and_show_it_again() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        List<string> before = await RowIdsAsync(app);
        int index = before.IndexOf("int16");

        // 1: 右クリックメニュー「この行を非表示」。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = "int16" });
        await PanelKeyAsync(app, "Application");
        await app.UiaInvokeAsync("Inspector_MenuHide");
        await WaitForRowsAsync(app);
        Assert.DoesNotContain("int16", await RowIdsAsync(app));

        // 2・3: 行の設定で int16 のチェックボックスをオンにする。
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Setting_int16");
        await app.CommandAsync("Inspector_Setting_int16");
        await app.SendAsync("panelKey", new JsonObject { ["key"] = "Escape" });
        await WaitForRowsAsync(app);
        List<string> after = await RowIdsAsync(app);
        Assert.Equal(index, after.IndexOf("int16"));
        Assert.Equal(before, after);
    });

    internal static async Task<List<string>> RowIdsAsync(AppSession app) =>
        [.. (await StateAsync(app))["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).Select(id => id.StartsWith("Inspector_Row_", StringComparison.Ordinal) ? id["Inspector_Row_".Length..] : id)];

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-19-02")]
    public Task Row_order_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Setting_uint32");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_SettingsList", ["text"] = "uint32" });
        for (int i = 0; i < 3; i++)
        {
            await app.UiaInvokeAsync("Inspector_MoveUp");
        }

        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_SettingsList", ["text"] = "Floating point" });
        await app.UiaInvokeAsync("Inspector_MoveUp");
        await app.SendAsync("panelKey", new JsonObject { ["key"] = "Escape" });
        await WaitForRowsAsync(app);
        List<string> order = await RowIdsAsync(app);
        Assert.True(order.IndexOf("uint32") < order.IndexOf("uint16"), string.Join(",", order));
        Assert.True(order.IndexOf("Inspector_Group_Float") < order.IndexOf("Inspector_Group_Integer"), string.Join(",", order));

        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        AppSession again = await StartAsync(ctx, data: "TD-SEQ-1M", profile: ctx.DefaultProfile);
        Assert.Equal(order, await RowIdsAsync(again));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-19-04")]
    public Task Rows_can_be_reordered_with_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        List<string> before = await RowIdsAsync(app);
        await app.FocusAsync("editor");
        while (!(await StateAsync(app))["hasFocus"]!.GetValue<bool>())
        {
            await PanelKeyAsync(app, "F6");
        }

        // 1: Tab でツールバーの「行の設定」に移り、Enter。
        for (int i = 0; i < 8 && await app.FocusedAsync() != "Button:Inspector_Rows"; i++)
        {
            await PanelKeyAsync(app, "Tab");
        }

        Assert.Equal("Button:Inspector_Rows", await app.FocusedAsync());
        await PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        Assert.True((await StateAsync(app))["rowSettingsOpen"]!.GetValue<bool>());

        // 2: ↓ で int64 の行に移り、Tab で「上へ」に移って Enter を 2 回。
        for (int i = 0; i < 12 && await app.FocusedAsync() != "ListViewItem:Inspector_SettingItem_int64"; i++)
        {
            await PanelKeyAsync(app, "Down");
            await app.IdleAsync();
        }

        Assert.Equal("ListViewItem:Inspector_SettingItem_int64", await app.FocusedAsync());
        await PanelKeyAsync(app, "Tab");
        Assert.Equal("Button:Inspector_MoveUp", await app.FocusedAsync());
        await PanelKeyAsync(app, "Enter");
        await PanelKeyAsync(app, "Enter");

        // 3: Esc で閉じる。
        await PanelKeyAsync(app, "Escape");
        await WaitForRowsAsync(app);
        List<string> after = await RowIdsAsync(app);
        Assert.Equal(before.IndexOf("int64") - 2, after.IndexOf("int64"));
    });

    /// <summary>INSP-19 の仕様 2: 行の設定の一覧で、行とグループをドラッグして並べ替える (ドラッグを落としたのと同じ処理を呼ぶ)。</summary>
    [Fact]
    public Task Rows_and_groups_can_be_reordered_by_dragging() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        List<string> before = await RowIdsAsync(app);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Setting_int64");

        // 整数のグループの先頭 (見出しの次、一覧の 1 番目) に int64 を落とす。
        Assert.True((await app.SendAsync("inspectorRowDrag", new JsonObject { ["item"] = "int64", ["to"] = 1 }))["moved"]!.GetValue<bool>());

        // 浮動小数点のグループを一覧の先頭に落とす。
        Assert.True((await app.SendAsync("inspectorRowDrag", new JsonObject { ["item"] = "Float", ["to"] = 0 }))["moved"]!.GetValue<bool>());
        await app.SendAsync("panelKey", new JsonObject { ["key"] = "Escape" });
        await WaitForRowsAsync(app);
        List<string> after = await RowIdsAsync(app);
        Assert.True(after.IndexOf("Inspector_Group_Float") < after.IndexOf("Inspector_Group_Integer"), string.Join(",", after));
        string firstInteger = before.SkipWhile(id => id != "Inspector_Group_Integer").Skip(1).First();
        Assert.Equal(after.IndexOf("Inspector_Group_Integer") + 1, after.IndexOf("int64"));
        Assert.NotEqual("int64", firstInteger);
    });
}
