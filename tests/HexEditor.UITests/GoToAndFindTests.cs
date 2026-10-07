using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 移動バー (VIEW-29) と検索バー (FIND-04〜FIND-09)。入力欄への入力は UI オートメーションの ValuePattern、ボタンは
/// InvokePattern で行う。入力欄の中の Enter / Esc はテキストボックスのキー処理で、命令の通り道からは送れないため、
/// 同じ処理を行うボタン (移動・次を検索・閉じる) で代える。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class GoToAndFindTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-29-01")]
    public Task Go_to_with_three_hex_notations() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        string? previous = null;
        foreach (string input in new[] { "0x1F00", "1F00h", "$1F00" })
        {
            await app.KeyAsync("Home", ctrl: true);
            await OpenGoToAsync(app);
            JsonObject box = await ElementAsync(app, "GoTo_Input");
            Assert.Equal("TextBox:GoTo_Input", (await app.StateAsync())["focused"]!.GetValue<string>());
            if (previous is not null)
            {
                // 2 回目以降は前回の入力が全選択された状態で入っている。
                Assert.Equal(previous, box["text"]!.GetValue<string>());
                Assert.Equal(previous, box["selectedText"]!.GetValue<string>());
            }

            await app.UiaSetValueAsync("GoTo_Input", input);
            Assert.Equal("= 0x1F00 (7,936)", await app.UiaNameAsync("GoTo_Interpretation"));
            await app.UiaInvokeAsync("GoTo_Go");
            await app.IdleAsync();

            await NavigationTests.AssertCursorAsync(app, 0x1F00);
            JsonObject state = await app.StateAsync();
            Assert.False(state["goToBarVisible"]!.GetValue<bool>());
            Assert.StartsWith("HexView", state["focused"]!.GetValue<string>(), StringComparison.Ordinal);
            previous = input;
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-29-02")]
    public Task Go_to_relative_and_from_the_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x100);
        await GoAsync(app, "+0x10");
        await NavigationTests.AssertCursorAsync(app, 0x110);
        await GoAsync(app, "-0x20");
        await NavigationTests.AssertCursorAsync(app, 0xF0);
        await GoAsync(app, "end-0x10");
        await NavigationTests.AssertCursorAsync(app, 0xFFFF0);

        // 4. 基準を「末尾から」(3 番目の項目) にして 10 (接頭辞のない数は既定の基数の 16 進。00-overview 6.1)。
        await OpenGoToAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "GoTo_Base", ["index"] = 3 });
        await app.UiaSetValueAsync("GoTo_Input", "10");
        await app.UiaInvokeAsync("GoTo_Go");
        await NavigationTests.AssertCursorAsync(app, 0xFFFF0);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-29-05")]
    public Task Values_beyond_the_end_are_rejected() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [NavigationTests.Len100(ctx)] });
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0x64");
        Assert.False((await ElementAsync(app, "GoTo_Input"))["errorBorder"]!.GetValue<bool>());
        await app.UiaInvokeAsync("GoTo_Go");
        await NavigationTests.AssertCursorAsync(app, 0x64);

        await app.KeyAsync("Home", ctrl: true);
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "0x65");
        Assert.True((await ElementAsync(app, "GoTo_Input"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("0x64", await app.UiaNameAsync("GoTo_Interpretation"));
        Assert.Contains("end", await app.UiaNameAsync("GoTo_Interpretation"));
        Assert.False((await app.WaitForAsync("GoTo_Go")).IsEnabled);

        // 3. 移動できないので、移動バーは開いたままでカーソルも動かない。
        Assert.True((await app.StateAsync())["goToBarVisible"]!.GetValue<bool>());
        await NavigationTests.AssertCursorAsync(app, 0);

        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "GoTo_Base", ["index"] = 1 });
        await app.UiaSetValueAsync("GoTo_Input", "-1");
        Assert.True((await ElementAsync(app, "GoTo_Input"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("start", await app.UiaNameAsync("GoTo_Interpretation"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-04-01")]
    public Task Ctrl_f_opens_and_close_returns_focus() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenFindAsync(app);
        Assert.True((await app.StateAsync())["findBarVisible"]!.GetValue<bool>());
        Assert.Equal("TextBox:Find_Query", (await app.StateAsync())["focused"]!.GetValue<string>());

        await app.UiaSetValueAsync("Find_Query", "AB");
        await OpenFindAsync(app);
        Assert.Equal("AB", (await ElementAsync(app, "Find_Query"))["selectedText"]!.GetValue<string>());

        // 5. Esc の代わりに閉じるボタン (同じ Close の処理)。
        await app.UiaInvokeAsync("Find_Close");
        await app.IdleAsync();
        JsonObject state = await app.StateAsync();
        Assert.False(state["findBarVisible"]!.GetValue<bool>());
        Assert.StartsWith("HexView", state["focused"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-04-03")]
    public Task Invalid_hex_shows_error_and_disables_buttons() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenFindAsync(app);
        await app.UiaSetValueAsync("Find_Query", "DE AD B");
        Assert.True((await ElementAsync(app, "Find_Query"))["errorBorder"]!.GetValue<bool>());
        string message = await app.UiaNameAsync("Find_Status");
        Assert.Contains("odd", message);
        Assert.Matches(@"\d", message); // 誤りの位置 (何文字目か)
        Assert.False((await app.WaitForAsync("Find_Next")).IsEnabled);
        Assert.False((await app.WaitForAsync("Find_Previous")).IsEnabled);

        await app.UiaSetValueAsync("Find_Query", "DE AD BE");
        Assert.False((await ElementAsync(app, "Find_Query"))["errorBorder"]!.GetValue<bool>());
        Assert.Equal("DE AD BE", await app.UiaNameAsync("Find_Status"));
        Assert.True((await app.WaitForAsync("Find_Next")).IsEnabled);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-05-02")]
    public Task Find_hex_smoke() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-FIND-MARK-1M: すべて 00 の中に、オフセット 0x80000 から CA FE BA BE。
        byte[] data = new byte[1024 * 1024];
        data[0x80000] = 0xCA;
        data[0x80001] = 0xFE;
        data[0x80002] = 0xBA;
        data[0x80003] = 0xBE;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-MARK-1M.bin", data)] });

        await OpenFindAsync(app);
        await app.UiaSetValueAsync("Find_Query", "CAFEBABE");
        await FindNextAsync(app);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x80000, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(4, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(0x80000, doc["cursor"]!.GetValue<long>());
        long row = 0x80000 / 16 - doc["topRow"]!.GetValue<long>();
        int visible = doc["visibleRows"]!.GetValue<int>();
        Assert.InRange(row, visible / 3, visible * 2 / 3);

        await app.UiaSetValueAsync("Find_Query", "0xCA,0xFE,0xBA,0xBE");
        await app.KeyAsync("Home", ctrl: true);
        await FindNextAsync(app);
        doc = await app.DocumentAsync();
        Assert.Equal(0x80000, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(4, doc["selectionLength"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-07-02")]
    public Task Find_text_smoke() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-STRINGS.bin", FindStrings())] });
        await OpenFindAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "ASCII" });
        await app.UiaSetValueAsync("Find_Query", "World");
        Assert.Equal("57 6F 72 6C 64", await app.UiaNameAsync("Find_Status"));
        await FindNextAsync(app);
        await AssertSelectionAsync(app, 0x20, 5);

        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "UTF-16 LE" });
        await app.UiaSetValueAsync("Find_Query", "ファイル");
        await app.KeyAsync("Home", ctrl: true);
        await FindNextAsync(app);
        await AssertSelectionAsync(app, 0x30, 8);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-09-02")]
    public Task Wrap_on_and_off() => UiTestContext.RunAsync(async ctx =>
    {
        // ステータスバーへの表示は未実装のため、検索バーの表示だけを確かめる。
        byte[] data = new byte[64];
        data.AsSpan(0, 4).Fill(0x41);
        data.AsSpan(0x20, 2).Fill(0x41);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-AAAA.bin", data)] });

        await app.SendAsync("click", new JsonObject { ["offset"] = 0x30 });
        await OpenFindAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.UiaSetValueAsync("Find_Query", "AA");
        await FindNextAsync(app);
        await AssertSelectionAsync(app, 0, 2);
        Assert.Contains("continued from the start", await app.UiaNameAsync("Find_Status"));

        // 「折り返す」はオプションの行にある (FIND-04 の仕様 4)。
        (await app.WaitForAsync("Find_Options")).Patterns.Toggle.Pattern.Toggle();
        await app.IdleAsync();
        (await app.WaitForAsync("Find_Wrap")).Patterns.Toggle.Pattern.Toggle();
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x30 });
        Assert.Equal("menu:Command_FindNext", (await app.KeyAsync("F3"))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(0x30, doc["cursor"]!.GetValue<long>());
        Assert.Contains("Not found before the end", await app.UiaNameAsync("Find_Status"));

        (await app.WaitForAsync("Find_Wrap")).Patterns.Toggle.Pattern.Toggle();
        await app.UiaSetValueAsync("Find_Query", "ZZ");
        await app.KeyAsync("F3");
        await app.WaitUntilAsync(async () => (await app.UiaNameAsync("Find_Status")).StartsWith("Not found", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the search result");
        Assert.Equal("Not found.", await app.UiaNameAsync("Find_Status"));
    });

    /// <summary>TD-FIND-STRINGS (64 バイト)。</summary>
    private static byte[] FindStrings()
    {
        byte[] data = new byte[64];
        new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00 }.CopyTo(data, 0x00);
        new byte[] { 0x48, 0x69, 0x00 }.CopyTo(data, 0x10);
        new byte[] { 0x57, 0x6F, 0x72, 0x6C, 0x64, 0xFF }.CopyTo(data, 0x20);
        new byte[] { 0xD5, 0x30, 0xA1, 0x30, 0xA4, 0x30, 0xEB, 0x30, 0x00, 0x00 }.CopyTo(data, 0x30);
        return data;
    }

    private static async Task OpenGoToAsync(AppSession app)
    {
        Assert.Equal("menu:Command_GoTo", (await app.KeyAsync("G", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
    }

    private static async Task OpenFindAsync(AppSession app)
    {
        Assert.Equal("menu:Command_Find", (await app.KeyAsync("F", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
    }

    private static async Task GoAsync(AppSession app, string input)
    {
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", input);
        await app.UiaInvokeAsync("GoTo_Go");
        await app.IdleAsync();
    }

    private static async Task FindNextAsync(AppSession app)
    {
        await app.UiaInvokeAsync("Find_Next");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => await app.UiaNameAsync("Find_Status") != "Searching...", TimeSpan.FromSeconds(10), "the search");
    }

    private static async Task AssertSelectionAsync(AppSession app, long start, long length)
    {
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(start, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(length, doc["selectionLength"]!.GetValue<long>());
    }

    private static Task<JsonObject> ElementAsync(AppSession app, string id) => app.SendAsync("element", new JsonObject { ["id"] = id });
}
