using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 逆アセンブル (ANA-23〜ANA-30) とインスペクタの 1 命令 (INSP-16) の画面。パネルの状態はテスト用の命令 "disasm" で読み、操作する。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DisassemblyUiTests
{
    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));
    private const long Base = (long)TestDataCatalog.PeAsmImageBase;

    private static Task<JsonObject> DisasmAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("disasm", request, TimeSpan.FromSeconds(120));
    }

    /// <summary>パネルを開き、実行形式の解釈とラベルの先読みを待つ。</summary>
    private static async Task<JsonObject> ShowAsync(AppSession app, string imageFormat = "Pe")
    {
        await DisasmAsync(app, "show");
        await app.WaitForAsync("DisassemblyPanel");
        JsonObject state = [];
        await app.WaitUntilAsync(async () =>
        {
            state = await DisasmAsync(app);
            return state["imageFormat"]!.GetValue<string>() == imageFormat && state["rows"]!.AsArray().Count > 0
                && (imageFormat == "None" || state["labelsScanned"]!.GetValue<bool>());
        }, Wait, "the disassembly");
        return state;
    }

    private static JsonObject RowAt(JsonObject state, long address) =>
        state["rows"]!.AsArray().Select(r => r!.AsObject())
            .First(r => r["kind"]!.GetValue<string>() == "Instruction" && Convert.ToInt64(r["address"]!.GetValue<string>(), 16) == address);

    private static int IndexOf(JsonObject state, long address) => RowAt(state, address)["index"]!.GetValue<int>();

    private static async Task<JsonObject> SelectAddressAsync(AppSession app, long address)
    {
        await DisasmAsync(app, "goTo", new JsonObject { ["offset"] = address - Base - 0x1000 + 0x400 });
        JsonObject state = await DisasmAsync(app);
        await DisasmAsync(app, "selectRow", new JsonObject { ["index"] = IndexOf(state, address) });
        return await DisasmAsync(app);
    }

    private static JsonObject Selected(JsonObject state) => state["rows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["selected"]!.GetValue<bool>());

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-23-03")]
    public Task Listing_and_hex_view_selection_follow_each_other() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);

        // 3 番目の命令 (0x1009 の mov eax, 1、5 バイト) の行を選ぶ → Hex ビューでその 5 バイトが選ばれる。
        JsonObject state = await SelectAddressAsync(app, Base + 0x1009);
        Assert.Equal("mov eax, 1", Selected(state)["text"]!.GetValue<string>());
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await DisasmAsync(app);
            return s["selectionStart"]?.GetValue<long>() == 0x409 && s["selectionLength"]!.GetValue<long>() == 5;
        }, Wait, "the 5 bytes selected in the hex view");

        // Hex ビューで 4 番目の命令 (0x100E の call、5 バイト) の 2 バイト目にカーソルを置く → その行が選ばれる。
        await app.GoToAsync(0x40F);
        await app.WaitUntilAsync(async () => Selected(await DisasmAsync(app))["offset"]!.GetValue<long>() == 0x40E, Wait, "the 4th instruction selected");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-23-06")]
    public Task Listing_stays_left_to_right_in_right_to_left_languages() => UiTestContext.RunAsync(async ctx =>
    {
        foreach (string language in new[] { "ar", "fa" })
        {
            AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = language, Files = [ctx.CopyTestData("TD-PE-X64", "TD-PE-X64.exe")] });
            await ShowAsync(app);
            JsonObject layout = await DisasmAsync(app, "layout");
            Assert.Equal("LeftToRight", layout["listingFlowDirection"]!.GetValue<string>());
            Assert.Equal("RightToLeft", layout["toolbarFlowDirection"]!.GetValue<string>());
            Assert.True(layout["addressLeft"]!.GetValue<double>() < layout["mnemonicLeft"]!.GetValue<double>(), layout.ToJsonString());
            await app.DisposeAsync();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-24-05")]
    public Task Virtual_address_shows_the_offset_and_section() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);
        JsonObject resolved = await DisasmAsync(app, "address", new JsonObject { ["text"] = "0x140001010", ["go"] = true });
        Assert.Equal(0x410, resolved["offset"]!.GetValue<long>());
        Assert.Contains(".text", resolved["info"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("0x410", resolved["info"]!.GetValue<string>(), StringComparison.Ordinal);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0x410, Wait, "the cursor at 0x410");

        JsonObject outside = await DisasmAsync(app, "address", new JsonObject { ["text"] = "0x150000000" });
        Assert.Null(outside["offset"]);
        Assert.Contains("section", outside["info"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-25-01")]
    public Task Enter_and_F12_go_to_the_call_target_and_Alt_Left_returns() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);
        await SelectAddressAsync(app, Base + 0x1004);

        // 1. Enter → func_a の先頭 (0x1040)。
        await DisasmAsync(app, "key", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await DisasmAsync(app);
            return Selected(s)["offset"]!.GetValue<long>() == 0x440 && s["selectionStart"]?.GetValue<long>() == 0x440;
        }, Wait, "func_a in the listing and the hex view");

        // 2. Alt+← → 元の call の行。
        await app.KeyAsync("Left", alt: true);
        await app.WaitUntilAsync(async () => Selected(await DisasmAsync(app))["offset"]!.GetValue<long>() == 0x404, Wait, "the call line");

        // 3. F12 (パネルにフォーカス、テンプレートの適用なし) → 手順 1 と同じ位置。
        await SelectAddressAsync(app, Base + 0x1004);
        await DisasmAsync(app, "f12", new JsonObject { ["panel"] = true });
        await app.WaitUntilAsync(async () => Selected(await DisasmAsync(app))["offset"]!.GetValue<long>() == 0x440, Wait, "func_a after F12");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-25-02")]
    public Task Enter_on_an_import_call_shows_the_function_name() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);
        JsonObject before = await SelectAddressAsync(app, Base + 0x1018);
        await DisasmAsync(app, "key", new JsonObject { ["key"] = "Enter" });
        JsonObject after = await DisasmAsync(app);
        Assert.Equal("External function: KERNEL32!CreateFileW", after["lastStatus"]!.GetValue<string>());
        Assert.Equal(Selected(before)["offset"]!.GetValue<long>(), Selected(after)["offset"]!.GetValue<long>());
        Assert.Equal(before["selectionStart"]?.GetValue<long>(), after["selectionStart"]?.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-26-02")]
    public Task Display_settings_survive_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string file = ctx.CopyTestData("TD-PE-X64", "TD-PE-X64.exe");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [file] });
        await ShowAsync(app);
        await DisasmAsync(app, "format", new JsonObject
        {
            ["syntax"] = "att", ["hexStyle"] = "suffix", ["uppercaseHex"] = true, ["maxBytes"] = 12, ["addressColumn"] = "both",
        });
        JsonObject changed = await DisasmAsync(app);
        Assert.Contains(" ", changed["rows"]![0]!["address"]!.GetValue<string>(), StringComparison.Ordinal);
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(UiTest.Scaled(TimeSpan.FromSeconds(30)));

        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [file] });
        JsonObject state = await ShowAsync(again);
        JsonObject format = state["format"]!.AsObject();
        Assert.Equal("att", format["syntax"]!.GetValue<string>());
        Assert.Equal("suffix", format["hexStyle"]!.GetValue<string>());
        Assert.True(format["uppercaseHex"]!.GetValue<bool>());
        Assert.Equal(12, format["maxBytes"]!.GetValue<int>());
        Assert.Equal("both", format["addressColumn"]!.GetValue<string>());

        // 設定画面の値 (設定ファイル) とツールバーの値が同じ。
        JsonObject settings = state["settings"]!.AsObject();
        Assert.Equal("att", settings["disassembly.syntax"]!.GetValue<string>());
        Assert.Equal(12, settings["disassembly.maxBytes"]!.GetValue<int>());
        Assert.Equal(1, state["toolbar"]!["syntax"]!.GetValue<int>());
        Assert.Contains(" ", state["rows"]![0]!["address"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-27-02")]
    public Task Selecting_an_il_line_selects_its_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] dll = TestDataCatalog.NetHello();
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-NET-HELLO", "TD-ANA-NET-HELLO.dll")] });
        JsonObject state = await ShowAsync(app);
        Assert.True(state["isDotNet"]!.GetValue<bool>());
        await DisasmAsync(app, "msil");
        await DisasmAsync(app, "method", new JsonObject { ["type"] = "Hello.Program", ["name"] = "Main" });
        state = await DisasmAsync(app);
        JsonObject ldstr = state["rows"]!.AsArray().Select(r => r!.AsObject()).First(r => r["text"]!.GetValue<string>().StartsWith("ldstr", StringComparison.Ordinal));
        await DisasmAsync(app, "selectRow", new JsonObject { ["index"] = ldstr["index"]!.GetValue<int>() });
        await app.WaitUntilAsync(async () => (await DisasmAsync(app))["selectionLength"]!.GetValue<long>() == 5, Wait, "the ldstr bytes");
        JsonObject after = await DisasmAsync(app);
        long start = after["selectionStart"]!.GetValue<long>();
        Assert.Equal(0x72, dll[start]);
        Assert.Equal(ldstr["offset"]!.GetValue<long>(), start);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-27-03")]
    public Task Il_branches_and_calls_can_be_followed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-NET-HELLO", "TD-ANA-NET-HELLO.dll")] });
        await ShowAsync(app);
        await DisasmAsync(app, "msil");
        await DisasmAsync(app, "method", new JsonObject { ["type"] = "Hello.Program", ["name"] = "Check" });
        JsonObject state = await DisasmAsync(app);
        JsonObject branch = state["rows"]!.AsArray().Select(r => r!.AsObject()).First(r => r["text"]!.GetValue<string>().StartsWith("brfalse.s", StringComparison.Ordinal));
        await DisasmAsync(app, "selectRow", new JsonObject { ["index"] = branch["index"]!.GetValue<int>() });
        await DisasmAsync(app, "key", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => Selected(await DisasmAsync(app))["offset"]!.GetValue<long>() == branch["target"]!.GetValue<long>(), Wait,
            "the branch target line");

        state = await DisasmAsync(app);
        JsonObject call = state["rows"]!.AsArray().Select(r => r!.AsObject()).First(r => r["text"]!.GetValue<string>().StartsWith("call", StringComparison.Ordinal));
        await DisasmAsync(app, "selectRow", new JsonObject { ["index"] = call["index"]!.GetValue<int>() });
        await DisasmAsync(app, "key", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => (await DisasmAsync(app))["method"]?.GetValue<string>() == "Helper", Wait, "the Helper method");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-29-01")]
    public Task Assembling_a_shorter_instruction_fills_with_nop() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);
        JsonObject state = await SelectAddressAsync(app, Base + 0x1009);
        int index = Selected(state)["index"]!.GetValue<int>();
        JsonObject menu = await DisasmAsync(app, "menu", new JsonObject { ["index"] = index });
        Assert.Contains(menu["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == "Disasm_Menu_Assemble" && i["enabled"]!.GetValue<bool>());

        JsonObject preview = await DisasmAsync(app, "assemble", new JsonObject { ["index"] = index, ["text"] = "xor eax, eax" });
        Assert.Equal(2, preview["length"]!.GetValue<int>());
        Assert.Contains(preview["hex"]!.GetValue<string>(), new[] { "31C0", "33C0" });
        await DisasmAsync(app, "assemble", new JsonObject { ["index"] = index, ["text"] = "xor eax, eax", ["write"] = true });
        byte[] bytes = await app.BytesAsync(0x409, 5);
        Assert.True(bytes.SequenceEqual(new byte[] { 0x31, 0xC0, 0x90, 0x90, 0x90 }) || bytes.SequenceEqual(new byte[] { 0x33, 0xC0, 0x90, 0x90, 0x90 }),
            Convert.ToHexString(bytes));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-29-03")]
    public Task Assembling_is_undone_with_ctrl_z() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ANA-PE-ASM", "TD-ANA-PE-ASM.exe")] });
        await ShowAsync(app);
        JsonObject state = await SelectAddressAsync(app, Base + 0x1009);
        await DisasmAsync(app, "assemble", new JsonObject { ["index"] = Selected(state)["index"]!.GetValue<int>(), ["text"] = "xor eax, eax", ["write"] = true });
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 }, await app.BytesAsync(0x409, 5));
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await DisasmAsync(app, "goTo", new JsonObject { ["offset"] = 0x409 });
            return Selected(s)["text"]!.GetValue<string>() == "mov eax, 1";
        }, Wait, "mov eax, 1 again");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-30-02")]
    public Task Demangle_converts_several_lines_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.CommandAsync("Command_Demangle");
        await app.WaitForAsync("DemanglePage");
        JsonObject result = await DisasmAsync(app, "demangle", new JsonObject
        {
            ["text"] = "_ZN3foo3barEi\r?bar@foo@@QEAAXH@Z\r_ZN4core3ptr13drop_in_place17h0123456789abcdefE\r_RNvCs1234_7mycrate3foo\r_D3foo3barFiZv",
        });
        Assert.Equal(
            ["foo::bar(int)", "public: void __cdecl foo::bar(int)", "core::ptr::drop_in_place", "mycrate::foo", "void foo.bar(int)"],
            result["output"]!.GetValue<string>().Split('\r'));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-16-02")]
    public Task Inspector_instruction_row_goes_to_the_branch_target() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-INSP-VALUES")] });
        await app.GoToAsync(0xA0);
        await app.WaitUntilAsync(async () => (await DisasmAsync(app, "inspector"))["value"]?.GetValue<string>()?.StartsWith("jmp", StringComparison.Ordinal) == true,
            Wait, "the jmp in the inspector");
        await DisasmAsync(app, "inspectorBranch");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0xB2, Wait, "the cursor at 0xB2");
        await app.KeyAsync("Left", alt: true);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0xA0, Wait, "the cursor back at 0xA0");
    });
}
