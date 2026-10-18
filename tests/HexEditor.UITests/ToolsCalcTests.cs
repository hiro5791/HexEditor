using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 電卓 (TOOL-22)、プログラマモード (TOOL-23)、基数・データ型の変換 (TOOL-24)、浮動小数点のビジュアライザ (TOOL-25)、文字コード表 (TOOL-26)、
/// カラーピッカー (TOOL-27)、日時の変換 (TOOL-28)。入力欄に入れる・ボタンを押すのと同じ処理をテスト用の命令で呼び (マウスは使わない)、
/// 見えている内容を読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ToolsCalcTests
{
    internal static readonly JsonObject EnUs = new() { ["culture"] = "en-US", ["timeZone"] = "UTC" };

    internal static JsonObject Args(string action, params (string Key, object? Value)[] values)
    {
        var args = new JsonObject { ["action"] = action };
        foreach ((string key, object? value) in values)
        {
            args[key] = JsonSerializer.SerializeToNode(value);
        }

        return args;
    }

    private static Task<JsonObject> Calc(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcCalc", Args(action, values));

    private static Task<JsonObject> Conv(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcConv", Args(action, values));

    private static Task<JsonObject> Float(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcFloat", Args(action, values));

    private static Task<JsonObject> Chars(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcChars", Args(action, values));

    private static Task<JsonObject> Color(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcColor", Args(action, values));

    private static Task<JsonObject> Time(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcTime", Args(action, values));

    private static Task<AppSession> StartWithoutDocumentAsync(UiTestContext ctx, JsonObject? hooks = null) =>
        ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = hooks ?? EnUs });

    private static async Task<AppSession> StartWithAsync(UiTestContext ctx, string path, JsonObject? hooks = null)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks ?? EnUs });
        return app;
    }

    private static string Text(JsonObject state, string key) => state[key]!.GetValue<string>();

    // ---- TOOL-22 電卓 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Adds_and_multiplies_in_every_radix() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_Calculator");

        JsonObject state = await Calc(app, "set", ("text", "0x1F00 + 10h * 2"));
        Assert.True(state["visible"]!.GetValue<bool>());
        Assert.Equal("0x1F20", Text(state, "hex"));
        Assert.Equal("7,968", Text(state, "dec"));
        Assert.Equal("17440", Text(state, "oct"));
        Assert.Equal("0001 1111 0010 0000", Text(state, "bin"));
        Assert.Equal("7.78 KiB", Text(state, "size"));

        state = await Calc(app, "commit");
        JsonObject top = state["history"]!.AsArray()[0]!.AsObject();
        Assert.Equal("0x1F00 + 10h * 2", Text(top, "expression"));
        Assert.Equal("0x1F20", Text(top, "result"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-02")]
    public Task End_minus_cursor_uses_the_active_document() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")], Hooks = EnUs });
        await app.WaitForTabsAsync(2);
        await app.CommandAsync("Command_Calculator");

        // 1. TD-SEQ-1M (カーソル 0x100) では 0xFFF00。
        await EditingTests.SelectTabAsync(app, 0);
        await app.GoToAsync(0x100);
        JsonObject state = await Calc(app, "set", ("text", "end - cur"));
        Assert.Equal("0xFFF00", Text(state, "hex"));

        // 2. TD-BYTES-256 (カーソル 0x10) に切り替えると 0xF0。式の欄は同じ。
        await EditingTests.SelectTabAsync(app, 1);
        await app.GoToAsync(0x10);
        await app.WaitUntilAsync(async () => Text(await Calc(app, "state"), "hex") == "0xF0", TimeSpan.FromSeconds(10), "end - cur for the second tab");
        state = await Calc(app, "commit");
        Assert.Equal("0xF0", Text(state, "hex"));

        // 3. すべてのタブを閉じると、ドキュメントがないため使えない旨のエラー。
        await app.KeyAsync("W", ctrl: true);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.WaitUntilAsync(async () => (await Calc(app, "state"))["errorVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the no-document error");
        state = await Calc(app, "state");
        Assert.NotEqual(string.Empty, Text(state, "error"));
        Assert.True(state["inputInvalid"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-03")]
    public Task Read_functions_follow_the_document_endianness() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-BYTES-256"));
        await app.CommandAsync("Command_Calculator");

        Assert.Equal("0x10203", Text(await Calc(app, "set", ("text", "u32be(0)")), "hex"));
        Assert.Equal("0x3020100", Text(await Calc(app, "set", ("text", "u32(0)")), "hex"));

        // ドキュメントのエンディアンをビッグにすると、u32(0) が u32be(0) と同じ値になる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.endianBig" });
        await Calc(app, "set", ("text", "1"));
        Assert.Equal("0x10203", Text(await Calc(app, "set", ("text", "u32(0)")), "hex"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-04")]
    public Task Calculator_only_notations_are_errors_in_other_input_fields() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-SEQ-1M"));
        await app.CommandAsync("Command_Calculator");
        await Calc(app, "set", ("text", "0x10"));
        await Calc(app, "commit");

        // 1〜2. ジャンプ欄で ans / popcnt(7) は赤枠で、確定ボタンが無効。
        await app.KeyAsync("G", ctrl: true);
        await app.WaitForAsync("GoTo_Input");
        foreach (string input in new[] { "ans", "popcnt(7)" })
        {
            await app.UiaSetValueAsync("GoTo_Input", input);
            Assert.True((await app.ElementAsync("GoTo_Input"))["errorBorder"]!.GetValue<bool>(), input);
            Assert.False((await app.WaitForAsync("GoTo_Go")).IsEnabled, input);
        }

        await app.KeyAsync("Escape");

        // 3. 範囲の選択のダイアログの長さ欄で align(5, 4) も同じ。
        await app.KeyAsync("E", ctrl: true);
        await app.WaitForAsync("SelectRangeDialog");
        await app.UiaSetValueAsync("SelectRange_Length", "align(5, 4)");
        Assert.True((await app.ElementAsync("SelectRange_Length"))["errorBorder"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("PrimaryButton")).IsEnabled);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });

        // 4. 電卓の中では使える。
        Assert.Equal("0x13", Text(await Calc(app, "set", ("text", "ans + popcnt(7)")), "hex"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-06")]
    public Task History_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_Calculator");
        await Calc(app, "set", ("text", "1 + 2"));
        await Calc(app, "commit");
        await Calc(app, "set", ("text", "0x100 * 3"));
        await Calc(app, "commit");
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));

        AppSession again = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs });
        await again.CommandAsync("Command_Calculator");
        JsonArray history = (await Calc(again, "state"))["history"]!.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Equal(("0x100 * 3", "0x300"), (Text(history[0]!.AsObject(), "expression"), Text(history[0]!.AsObject(), "result")));
        Assert.Equal("1 + 2", Text(history[1]!.AsObject(), "expression"));

        // 履歴の `1 + 2` をクリックすると式の欄に戻る。
        JsonObject state = await Calc(again, "history", ("index", 1));
        Assert.Equal("1 + 2", Text(state, "input"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-07")]
    public Task Writing_at_the_cursor_can_be_undone() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await StartWithAsync(ctx, path);
        await app.GoToAsync(0x40);
        await app.CommandAsync("Command_Calculator");
        await Calc(app, "set", ("text", "0x11223344"));

        await Calc(app, "write", ("size", 4), ("big", true));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, await app.BytesAsync(0x40, 4));

        // Hex ビューで Ctrl+Z を 1 回押すと元に戻る。
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[] { 0x40, 0x41, 0x42, 0x43 }, await app.BytesAsync(0x40, 4));
        Assert.Equal(1048576, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-22-08")]
    public Task Decimal_point_is_a_dot_even_in_german_regional_settings() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx, new JsonObject { ["culture"] = "de-DE", ["timeZone"] = "UTC" });
        await app.CommandAsync("Command_Calculator");

        Assert.Equal("3", Text(await Calc(app, "set", ("text", "1.5 * 2")), "dec"));
        Assert.True((await Calc(app, "set", ("text", "1,5 * 2")))["errorVisible"]!.GetValue<bool>());

        JsonObject state = await Calc(app, "set", ("text", "1.5K"));
        Assert.Equal("1.536", Text(state, "dec"));
        await Calc(app, "copy", ("id", "Calc_Copy_Dec"));
        Assert.Equal("1536", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>());
    });

    // ---- TOOL-23 プログラマモード ----

    private static async Task<AppSession> StartProgrammerAsync(UiTestContext ctx)
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_Calculator");
        JsonObject state = await Calc(app, "programmer", ("on", true));
        Assert.True(state["programmer"]!.GetValue<bool>());
        Assert.Equal(32, state["width"]!.GetValue<int>());
        Assert.False(state["signed"]!.GetValue<bool>());
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-23-02")]
    public Task Clicking_a_bit_changes_the_value_and_other_radixes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartProgrammerAsync(ctx);
        await Calc(app, "set", ("text", "0"));

        JsonObject state = await Calc(app, "toggleBit", ("index", 4));
        Assert.Equal("0x10", Text(state, "hex"));
        Assert.Equal("16", Text(state, "dec"));
        Assert.Equal("0000 0000 0000 0000 0000 0000 0001 0000", Text(state, "bin"));

        state = await Calc(app, "toggleBit", ("index", 31));
        Assert.Equal("0x80000010", Text(state, "hex"));
        Assert.Equal("2,147,483,664", Text(state, "dec"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-23-03")]
    public Task Selecting_bits_shows_the_field_value_and_the_mask_shift_expression() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartProgrammerAsync(ctx);
        await Calc(app, "set", ("text", "0xABCD"));

        JsonObject state = await Calc(app, "selectBits", ("from", 4), ("to", 7));
        Assert.Contains("0xC (12)", Text(state, "bitField"), StringComparison.Ordinal);
        Assert.Equal("(x >> 4) & 0xF", Text(state, "bitExpression"));

        // ビット 8 にフォーカスを置き、Shift+← を 3 回押して選び直す。
        await Calc(app, "focusBit", ("index", 8));
        for (int i = 0; i < 3; i++)
        {
            state = await Calc(app, "bitKey", ("key", "Left"), ("shift", true));
        }

        JsonObject selection = state["selection"]!.AsObject();
        Assert.Equal((8, 11), (selection["low"]!.GetValue<int>(), selection["high"]!.GetValue<int>()));
        Assert.Contains("0xB (11)", Text(state, "bitField"), StringComparison.Ordinal);
        Assert.Equal("(x >> 8) & 0xF", Text(state, "bitExpression"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-23-04")]
    public Task Bit_table_works_with_the_keyboard_and_every_cell_has_a_name() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartProgrammerAsync(ctx);
        await Calc(app, "set", ("text", "0x80000001"));

        // フォーカスをビットの表に移し、→ で隣のビットへ移って Space で反転する。
        await Calc(app, "focusBit", ("index", 2));
        JsonObject state = await Calc(app, "bitKey", ("key", "Right"));
        Assert.Equal(1, state["focusedBit"]!.GetValue<int>());
        state = await Calc(app, "toggleBit", ("index", 1));
        Assert.Equal("0x80000003", Text(state, "hex"));

        // 32 個のセルの名前に、ビット番号と値が入る。
        JsonArray bits = state["bits"]!.AsArray();
        Assert.Equal(32, bits.Count);
        JsonObject top = bits.Select(b => b!.AsObject()).First(b => b["index"]!.GetValue<int>() == 31);
        Assert.Equal("Bit 31, value 1", Text(top, "name"));
        Assert.All(bits.Select(b => b!.AsObject()), b => Assert.Contains($"Bit {b["index"]!.GetValue<int>()},", Text(b, "name"), StringComparison.Ordinal));
    });

    // ---- TOOL-24 基数・データ型の変換 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-24-01")]
    public Task Number_and_text_fields_convert_into_each_other() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_Converter");

        await Conv(app, "endian", ("big", true));
        JsonObject fields = (await Conv(app, "type", ("field", "Conv_UDec"), ("text", "1078530011")))["fields"]!.AsObject();
        Assert.Equal("40 49 0F DB", Text(fields, "Conv_Hex"));
        Assert.Equal("3.1415927", Text(fields, "Conv_Float"));

        fields = (await Conv(app, "type", ("field", "Conv_Utf8"), ("text", "あ")))["fields"]!.AsObject();
        Assert.Equal("E3 81 82", Text(fields, "Conv_Hex"));
        fields = (await Conv(app, "type", ("field", "Conv_Utf16Le"), ("text", "あ")))["fields"]!.AsObject();
        Assert.Equal("42 30", Text(fields, "Conv_Hex"));

        await Conv(app, "codePage", ("id", "cp37"));
        fields = (await Conv(app, "type", ("field", "Conv_Hex"), ("text", "C1 C2 C3")))["fields"]!.AsObject();
        Assert.Equal("ABC", Text(fields, "Conv_CodePage"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-24-02")]
    public Task Read_from_cursor_takes_the_selection_up_to_64_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-BYTES-256"));
        await app.CommandAsync("Command_Converter");

        await app.SelectAsync(0x41, 3);
        JsonObject fields = (await Conv(app, "read"))["fields"]!.AsObject();
        Assert.Equal("41 42 43", Text(fields, "Conv_Hex"));
        Assert.Equal("ABC", Text(fields, "Conv_Ascii"));

        await app.SelectAsync(0, 0x50);
        fields = (await Conv(app, "read"))["fields"]!.AsObject();
        string[] bytes = Text(fields, "Conv_Hex").Split(' ');
        Assert.Equal(64, bytes.Length);
        Assert.Equal(("00", "3F"), (bytes[0], bytes[^1]));
    });

    // ---- TOOL-25 浮動小数点のビジュアライザ ----

    private static async Task<JsonObject> ToggleToAsync(AppSession app, IEnumerable<int> bits, bool value)
    {
        JsonObject state = await Float(app, "state");
        foreach (int bit in bits)
        {
            JsonObject cell = state["cells"]!.AsArray().Select(c => c!.AsObject()).First(c => c["index"]!.GetValue<int>() == bit);
            if ((Text(cell, "value") == "1") != value)
            {
                state = await Float(app, "toggleBit", ("index", bit));
            }
        }

        return state;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-25-01")]
    public Task Float_input_is_rounded_exactly_and_classified() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_FloatVisualizer");
        await Float(app, "format", ("id", "float"));

        // 1. 0.1 を float で入力する。
        JsonObject values = (await Float(app, "decimal", ("text", "0.1")))["values"]!.AsObject();
        Assert.Equal("0x3DCCCCCD", Text(values, "Float_BitsHex"));
        Assert.Equal("0.100000001490116119384765625", Text(values, "Float_Exact"));
        Assert.Equal("1.490116119384765625E-9", Text(values, "Float_Error"));

        // 2. 00000001 は非正規化数。
        JsonObject state = await Float(app, "hex", ("text", "00000001"));
        Assert.Equal("Subnormal", Text(state, "class"));

        // 3〜4. 3FC00000 の指数部をすべて 1 にすると quiet NaN (0x7FC00000)、仮数部をすべて 0 にすると +∞ (0x7F800000)。
        await Float(app, "hex", ("text", "3FC00000"));
        state = await ToggleToAsync(app, Enumerable.Range(23, 8), value: true);
        Assert.Equal(("NaN", "0x7FC00000"), (Text(state, "class"), Text(state, "bits")));
        state = await ToggleToAsync(app, Enumerable.Range(0, 23), value: false);
        Assert.Equal(("Infinity", "0x7F800000"), (Text(state, "class"), Text(state, "bits")));

        // 区間のラベル(色以外でも区別できる)。
        string[] labels = [.. state["labels"]!.AsArray().Select(l => l!.GetValue<string>())];
        Assert.Equal(["Sign", "Exponent (8 bits)", "Mantissa (23 bits)"], labels);
        JsonObject cell = state["cells"]!.AsArray().Select(c => c!.AsObject()).First(c => c["index"]!.GetValue<int>() == 30);
        Assert.Equal("Exponent bit 7, value 1", Text(cell, "name"));
    });

    // ---- TOOL-26 文字コード表 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-26-01")]
    public Task Code_page_437_shows_the_light_shade() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_CharTable");
        await Chars(app, "encoding", ("id", "cp437"));

        Assert.Equal("░", Text(await Chars(app, "cell", ("value", 0xB0)), "label"));
        JsonObject detail = (await Chars(app, "select", ("value", 0xB0)))["detail"]!.AsObject();
        Assert.Equal(("U+2591", "LIGHT SHADE"), (Text(detail, "Chars_Detail_CodePoint"), Text(detail, "Chars_Detail_Name")));
        Assert.Equal("NUL", Text(await Chars(app, "cell", ("value", 0)), "label"));
        Assert.Equal("LF", Text(await Chars(app, "cell", ("value", 0x0A)), "label"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-26-02")]
    public Task Shift_jis_lead_byte_opens_the_second_byte_table() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_CharTable");
        await Chars(app, "encoding", ("id", "cp932"));

        JsonObject state = await Chars(app, "select", ("value", 0x82));
        Assert.True(state["trailVisible"]!.GetValue<bool>());
        Assert.Equal("ぁ", Text(await Chars(app, "cell", ("value", 0x9F), ("trail", true)), "label"));
        Assert.Equal("あ", Text(await Chars(app, "cell", ("value", 0xA0), ("trail", true)), "label"));

        JsonObject detail = (await Chars(app, "select", ("value", 0xA0), ("lead", 0x82)))["detail"]!.AsObject();
        Assert.Equal(("U+3042", "HIRAGANA LETTER A", "E3 81 82", "42 30"),
            (Text(detail, "Chars_Detail_CodePoint"), Text(detail, "Chars_Detail_Name"), Text(detail, "Chars_Detail_Utf8"), Text(detail, "Chars_Detail_Utf16")));
        detail = (await Chars(app, "utf16", ("big", true)))["detail"]!.AsObject();
        Assert.Equal("30 42", Text(detail, "Chars_Detail_Utf16"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-26-03")]
    public Task Enter_inputs_the_byte_at_the_cursor_and_can_be_undone() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await StartWithAsync(ctx, path);
        await app.GoToAsync(0x10);
        await app.CommandAsync("Command_CharTable");
        await Chars(app, "encoding", ("id", "cp437"));

        // 上書きモード: 0x10 が 41 になり、長さは変わらない。
        await Chars(app, "select", ("value", 0x41));
        await Chars(app, "activate");
        Assert.Equal(0x41, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(1048576, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // Ctrl+Z で戻る。
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);

        // 挿入モード: 長さが 1 増える。
        await app.GoToAsync(0x10);
        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.toggleInsert" });
        await Chars(app, "select", ("value", 0x41));
        await Chars(app, "activate");
        Assert.Equal(1048577, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-26-04")]
    public Task Arrow_keys_move_between_cells_and_each_cell_has_a_spoken_name() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_CharTable");
        await Chars(app, "encoding", ("id", "cp437"));
        await Chars(app, "focus", ("value", 0x40));

        // → でセル 0x41 に移り、名前に 0x41、A、文字名が入る。
        await Chars(app, "move", ("delta", 1));
        JsonObject cell = await Chars(app, "cell", ("value", 0x41));
        Assert.Equal("0x41, A, LATIN CAPITAL LETTER A", Text(cell, "name"));
        Assert.Equal("0x41, A, LATIN CAPITAL LETTER A", Text(cell, "focusedName"));

        // ↓ でセル 0x51。
        await Chars(app, "move", ("delta", 16));
        cell = await Chars(app, "cell", ("value", 0x51));
        Assert.Equal("0x51, Q, LATIN CAPITAL LETTER Q", Text(cell, "focusedName"));
    });

    // ---- TOOL-27 カラーピッカー ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-27-01")]
    public Task Rgb565_of_orange_is_00_FC() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_ColorPicker");

        JsonObject state = await Color(app, "hex", ("text", "#FF8000"));
        JsonObject bytes = state["bytes"]!.AsObject();
        await Color(app, "endian", ("id", "rgb565"), ("big", false));
        Assert.Equal("00 FC", Text((await Color(app, "state"))["bytes"]!.AsObject(), "rgb565"));
        Assert.Equal("FF 80 00", Text(bytes, "rgb888"));
        Assert.Equal("00 80 FF", Text(bytes, "bgr888"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-27-02")]
    public Task Four_bytes_read_as_bgra8888_give_the_color() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-TOOL-COLOR"));
        await app.GoToAsync(0);
        await app.CommandAsync("Command_ColorPicker");
        await Color(app, "format", ("id", "bgra8888"));

        JsonObject state = await Color(app, "read");
        Assert.Equal("#FFFF8000", Text(state, "swatchText"));
        JsonObject swatch = state["swatch"]!.AsObject();
        Assert.Equal((255, 128, 0), (swatch["r"]!.GetValue<int>(), swatch["g"]!.GetValue<int>(), swatch["b"]!.GetValue<int>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-27-03")]
    public Task Find_this_color_sends_the_bytes_to_the_find_bar() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-TOOL-COLOR"));
        await app.GoToAsync(0);
        await app.CommandAsync("Command_ColorPicker");
        await Color(app, "hex", ("text", "#FF8000"));
        await Color(app, "format", ("id", "bgra8888"));

        await Color(app, "search");
        Assert.True((await app.StateAsync())["findBarVisible"]!.GetValue<bool>());
        JsonObject query = await app.ElementAsync("Find_Query");
        Assert.Equal("00 80 FF FF", Text(query, "text"));
        Assert.Equal(0, (await app.ElementAsync("Find_Kind"))["selectedIndex"]!.GetValue<int>());

        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 4, TimeSpan.FromSeconds(10), "the match");
        Assert.Equal(0, (await app.DocumentAsync())["selectionStart"]!.GetValue<long>());
    });

    // ---- TOOL-28 日時の変換 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-28-01")]
    public Task Filetime_unix_and_dos_conversions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithoutDocumentAsync(ctx);
        await app.CommandAsync("Command_DateTime");
        await Time(app, "zone", ("id", "utc"));

        JsonObject rows = (await Time(app, "date", ("text", "2000-01-01T00:00:00Z")))["rows"]!.AsObject();
        Assert.Equal("0x01BF53EB256D4000", Text(rows["filetime"]!.AsObject(), "hex"));

        JsonObject state = await Time(app, "field", ("format", "unix32"), ("field", "dec"), ("text", "0"));
        Assert.Equal("1970-01-01T00:00:00Z", Text(state, "iso"));

        rows = (await Time(app, "date", ("text", "1979-12-31T23:59:59Z")))["rows"]!.AsObject();
        Assert.Equal(UiHelpers.LoadResw("en")["Time_NotRepresentable"], Text(rows["dos"]!.AsObject(), "status"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-28-02")]
    public Task Reading_the_same_bytes_shows_the_same_text_as_the_inspector() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithAsync(ctx, ctx.TestData("TD-TOOL-TIMESTAMPS"));
        await app.KeyAsync("I", ctrl: true, shift: true);
        await InspectorTests.WaitForRowsAsync(app);
        await app.CommandAsync("Command_DateTime");
        await Time(app, "zone", ("id", "local"));
        await Time(app, "endian", ("big", false));

        // データインスペクタに既定で出る日時の型 (slot: TD-TOOL-TIMESTAMPS の枠の番号)。他の形式は Core のテストで同じことを確かめる。
        foreach ((string typeId, string formatId, int slot) in new[] { ("unix32", "unix32", 0), ("filetime", "filetime", 6), ("dosdatetime", "dos", 7) })
        {
            await app.GoToAsync(slot * 16);
            await InspectorTests.WaitForRowsAsync(app);
            await app.WaitUntilAsync(async () => (await InspectorTests.StateAsync(app))["origin"]!.GetValue<string>().Contains((slot * 16).ToString("X"), StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "the inspector origin");
            JsonObject row = (await InspectorTests.StateAsync(app))["items"]!.AsArray().Select(i => i!.AsObject()).First(i => i["type"]?.GetValue<string>() == typeId);
            await Time(app, "format", ("id", formatId));
            JsonObject state = await Time(app, "read");
            Assert.Equal(Text(row, "value"), Text(state, "regional"));
        }
    });
}
