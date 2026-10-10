using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.SearchResultsTests;

namespace HexEditor.UITests;

/// <summary>
/// フェーズ 2 の検索の UI: 複数の文字コード (FIND-08)、範囲 (FIND-15)、位置の条件 (FIND-17)、正規表現 (FIND-18)、一致しない箇所 (FIND-25)、
/// 複数語 (FIND-26)、文字列の抽出 (FIND-32)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SearchPhase2Tests
{
    private static Task<JsonObject> Phase2Async(AppSession app, JsonObject? request = null) => app.SendAsync("findBarPhase2", request ?? []);

    private static async Task<string> FindStatusAsync(AppSession app) =>
        (await app.ElementAsync("Find_Status"))["text"]?.GetValue<string>() ?? string.Empty;

    private static async Task<(long Start, long Length)> SelectionAsync(AppSession app)
    {
        JsonObject d = await app.DocumentAsync();
        return (d["selectionStart"]!.GetValue<long>(), d["selectionLength"]!.GetValue<long>());
    }

    /// <summary>検索バーで Enter (Shift なら前を検索) を押し、選択範囲が変わるまで待つ。</summary>
    private static async Task<(long Start, long Length)> EnterAsync(AppSession app, bool shift = false)
    {
        (long, long) before = await SelectionAsync(app);
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter", ["shift"] = shift });
        (long, long) after = before;
        await app.WaitUntilAsync(async () => (after = await SelectionAsync(app)) != before, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the next match");
        return after;
    }

    private static Task ShowOptionsAsync(AppSession app) => app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_Options", ["value"] = true });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-08-01")]
    public Task Three_encodings_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1 << 20];
        Encoding.ASCII.GetBytes("Test").CopyTo(data, 0x100);
        Encoding.Unicode.GetBytes("Test").CopyTo(data, 0x200);
        Encoding.BigEndianUnicode.GetBytes("Test").CopyTo(data, 0x300);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-ZERO-1M.bin", data)] });

        // 1〜2. テキスト、文字コード「複数」で ASCII・UTF-16LE・UTF-16BE。00 の中の UTF-16 は 1 バイトずれても読めるため文字の境界に揃える。
        await OpenFindAsync(app, 1, "Test", incremental: false);
        await ShowOptionsAsync(app);
        await Phase2Async(app, new JsonObject { ["multiEncodings"] = new JsonArray("ascii", "utf-16le", "utf-16be") });
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_Align", ["value"] = true });
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");

        // 3. 0x100 (ASCII)、0x200 (UTF-16LE)、0x300 (UTF-16BE) の 3 件。文字コードの列がある。
        JsonArray rows = (await ResultsAsync(app, 0, 10))["rows"]!.AsArray();
        Assert.Equal([0x100L, 0x200, 0x300], rows.Select(r => r!["offset"]!.GetValue<long>()));
        Assert.Contains("ASCII", rows[0]!["endian"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("UTF-16 LE", rows[1]!["endian"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("UTF-16 BE", rows[2]!["endian"]!.GetValue<string>(), StringComparison.Ordinal);

        // 4. 先頭から次を検索を 3 回: 0x100、0x200、0x300 の順で、一致した文字コードが出る。
        await app.GoToAsync(0);
        string[] names = ["ASCII", "UTF-16 LE", "UTF-16 BE"];
        long[] offsets = [0x100, 0x200, 0x300];
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(offsets[k], (await EnterAsync(app)).Start);
            Assert.Contains(names[k], await FindStatusAsync(app), StringComparison.Ordinal);
            Assert.Contains(names[k], (await app.SendAsync("findBar"))["statusMessage"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // 5. UTF-8 も選ぶ: 3 件のまま。0x100 の行は「ASCII / UTF-8」。
        await Phase2Async(app, new JsonObject { ["multiEncodings"] = new JsonArray("ascii", "utf-16le", "utf-16be", "utf-8") });
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>() && r["count"]!.GetValue<long>() == 3, "the results");
        string first = (await ResultsAsync(app, 0, 1))["rows"]![0]!["endian"]!.GetValue<string>();
        Assert.Matches("ASCII.* / .*UTF-8", first);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-15-02")]
    public Task Range_search_defaults_to_aligned_positions() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1 << 20];
        new byte[] { 0x96, 0, 0, 0 }.CopyTo(data, 0x101);
        new byte[] { 0x96, 0, 0, 0 }.CopyTo(data, 0x200);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-ZERO-1M.bin", data)] });

        // 1〜2. 整数、32 bit、符号あり、リトルエンディアンで 100..200。
        await OpenFindAsync(app, 2, string.Empty, incremental: false);
        await ShowOptionsAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Sign", ["index"] = 1 });
        await app.UiaSetValueAsync("Find_Query", "100..200");

        // 3. 位置の条件は「4 の倍数」、範囲の表示。
        Assert.Equal("Position: multiples of 4", (await Phase2Async(app))["positionText"]!.GetValue<string>());
        Assert.Contains("Range: 100 to 200 (32 bit signed, LE)", await FindStatusAsync(app), StringComparison.Ordinal);

        // 4. すべて検索: 0x200 の 1 件。
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        Assert.Equal([0x200L], (await ResultsAsync(app, 0, 10))["rows"]!.AsArray().Select(r => r!["offset"]!.GetValue<long>()));

        // 5. 位置の条件を「条件なし」にすると 0x101 と 0x200。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Position", ["index"] = 0 });
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>() && r["count"]!.GetValue<long>() == 2, "the results");
        Assert.Equal([0x101L, 0x200], (await ResultsAsync(app, 0, 10))["rows"]!.AsArray().Select(r => r!["offset"]!.GetValue<long>()));
    });

    /// <summary>TD-FIND-ALIGN。</summary>
    private static byte[] Align()
    {
        byte[] data = new byte[4096];
        foreach (int at in new[] { 0x000, 0x001, 0x100, 0x200, 0x201, 0x400, 0x500, 0x5FF, 0x600 })
        {
            data[at] = 0xEB;
        }

        return data;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-17-02")]
    public Task Position_condition_display_and_errors() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-ALIGN.bin", Align())] });

        // 1〜2. Hex `EB`、位置の条件「指定する」で x = 512、y = 0 → 「位置: mod 512 = 0」。
        await OpenFindAsync(app, 0, "EB", incremental: false);
        await ShowOptionsAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Position", ["index"] = 1 });
        await app.UiaSetValueAsync("Find_PositionModulus", "512");
        await app.UiaSetValueAsync("Find_PositionRemainder", "0");
        Assert.Equal("Position: mod 512 = 0", (await Phase2Async(app))["positionText"]!.GetValue<string>());

        // 3. y = 512: y の入力欄が赤枠で、検索ボタンは無効。
        await app.UiaSetValueAsync("Find_PositionRemainder", "512");
        Assert.True((await app.ElementAsync("Find_PositionRemainder"))["errorBorder"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("Find_Next")).IsEnabled);

        // 4. 「セクタの先頭」: x は 512、y は 0。
        await app.UiaInvokeAsync("Find_PositionPresetSector");
        JsonObject state = await Phase2Async(app);
        Assert.Equal(("512", "0"), (state["positionX"]!.GetValue<string>(), state["positionY"]!.GetValue<string>()));
        Assert.True(state["positionValid"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-18-02")]
    public Task Catastrophic_backtracking_does_not_freeze_the_ui() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1 << 20];
        Array.Fill(data, (byte)'a', 0x100, 40);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-ZERO-1M.bin", data)] });

        // 1. (a+)+b は後戻りしない方式で照合され、すぐに「見つかりませんでした」。
        await OpenFindAsync(app, 4, "(a+)+b", incremental: false, encoding: "ASCII (7 bit)");
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => (await FindStatusAsync(app)).StartsWith("Not found", StringComparison.Ordinal),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "not found");

        // 2〜4. 先読みを含む (a+)+(?=b) は後戻りする方式。時間の上限で InfoBar が出る。待つ間も UI は応答する。
        await app.UiaSetValueAsync("Find_Query", "(a+)+(?=b)");
        _ = app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        for (int i = 0; i < 5; i++)
        {
            await app.ElementAsync("Find_Status");
        }

        await app.WaitForNotificationAsync(m => m.Contains("took too long", StringComparison.Ordinal), "the time limit notice");
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Stop searching" });
        await app.WaitUntilAsync(async () => (await FindStatusAsync(app)).Contains("time limit", StringComparison.Ordinal),
            UiTest.Scaled(TimeSpan.FromSeconds(15)), "the search to stop");
    });

    [Fact]
    public Task Byte_regex_turns_the_s_flag_on_by_default() => UiTestContext.RunAsync(async ctx =>
    {
        // FIND-19 の仕様 3: 種類「正規表現 (バイト列)」では `s` フラグを既定でオンにする (テキストは既定オフ。FIND-18 の仕様 2)。
        // 利用者が切り替えた値は、種類ごとにこのウィンドウの間は覚えておく。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("small.bin", new byte[256])] });
        await OpenFindAsync(app, 4, "a", incremental: false);
        await ShowOptionsAsync(app);
        async Task<bool> SinglelineAsync() => (await Phase2Async(app))["regexSingleline"]!.GetValue<bool>();
        Task KindAsync(int kind) => app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = kind });
        Task CheckAsync(bool value) => app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_RegexSingleline", ["value"] = value });

        Assert.False(await SinglelineAsync());
        await KindAsync(5);
        Assert.True(await SinglelineAsync());

        // バイト列でオフにし、テキストでオンにする。種類を戻すとそれぞれの値に戻る。
        await CheckAsync(false);
        await KindAsync(4);
        Assert.False(await SinglelineAsync());
        await CheckAsync(true);
        await KindAsync(0);
        await KindAsync(5);
        Assert.False(await SinglelineAsync());
        await KindAsync(4);
        Assert.True(await SinglelineAsync());
    });

    /// <summary>TD-FIND-GAPS。</summary>
    private static byte[] Gaps()
    {
        byte[] data = new byte[0x100200];
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(data, 0x100000);
        Array.Fill(data, (byte)0xAA, 0x100100, 32);
        return data;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-25-01")]
    public Task Mismatch_moves_to_the_next_data() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-GAPS.bin", Gaps())] });

        // 1〜2. 種類「Hex」で「一致しない箇所を探す」: 検索欄の見出しは「繰り返しのパターン」。
        await OpenFindAsync(app, 0, string.Empty, incremental: false);
        await ShowOptionsAsync(app);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_Mismatch", ["value"] = true });
        Assert.Equal("Repeated pattern", (await Phase2Async(app))["queryLabel"]!.GetValue<string>());

        // 3〜4. `00` で Enter: 0x100000 の 1 バイト。
        await app.UiaSetValueAsync("Find_Query", "00");
        Assert.Equal((0x100000L, 1L), await EnterAsync(app));
        Assert.Equal(0x100000, (await app.DocumentAsync())["cursor"]!.GetValue<long>());

        // 5. もう一度: 0x100001 (`34`)。
        Assert.Equal((0x100001L, 1L), await EnterAsync(app));

        // 6. 末尾から Shift+Enter: 0x10011F。
        await app.GoToAsync(0x100200);
        Assert.Equal((0x10011FL, 1L), await EnterAsync(app, shift: true));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-25-03")]
    public Task Mismatch_find_all_lists_ranges() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-GAPS.bin", Gaps())] });
        await OpenFindAsync(app, 0, string.Empty, incremental: false);
        await ShowOptionsAsync(app);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_Mismatch", ["value"] = true });
        await app.UiaSetValueAsync("Find_Query", "00");

        // 1〜2. 0x100000 から 4 バイトと、0x100100 から 32 バイト。
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        JsonArray rows = (await ResultsAsync(app, 0, 10))["rows"]!.AsArray();
        Assert.Equal([(0x100000L, 4L), (0x100100L, 32L)], rows.Select(r => (r!["offset"]!.GetValue<long>(), r["length"]!.GetValue<long>())));

        // 3. 2 行目をクリック: 0x100100〜0x10011F。
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        Assert.Equal((0x100100L, 32L), await SelectionAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-26-01")]
    public Task Three_terms_of_different_kinds() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1 << 20];
        new byte[] { 0x4D, 0x5A }.CopyTo(data, 0x100);
        new byte[] { 0x50, 0x45 }.CopyTo(data, 0x200);
        new byte[] { 0x50, 0x00, 0x45, 0x00 }.CopyTo(data, 0x300);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-ZERO-1M.bin", data)] });

        // 1〜2. 「複数語」: Hex `4D 5A`、テキスト (ASCII) `PE`、テキスト (UTF-16LE) `PE`。
        await OpenFindAsync(app, 0, string.Empty, incremental: false);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "Find_MultiTerm", ["value"] = true });
        await Phase2Async(app, new JsonObject
        {
            ["terms"] = new JsonArray(
                new JsonObject { ["kind"] = "Hex", ["text"] = "4D 5A" },
                new JsonObject { ["kind"] = "Text", ["text"] = "PE", ["encoding"] = "ascii" },
                new JsonObject { ["kind"] = "Text", ["text"] = "PE", ["encoding"] = "utf-16le" }),
        });
        Assert.True((await Phase2Async(app))["multiTerm"]!.GetValue<bool>());

        // 3〜4. Alt+Enter: 3 件。検索語の列と、語ごとの件数 (各 1 件)。
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        JsonArray rows = (await ResultsAsync(app, 0, 10))["rows"]!.AsArray();
        Assert.Equal([0x100L, 0x200, 0x300], rows.Select(r => r!["offset"]!.GetValue<long>()));
        Assert.Equal("4D 5A", rows[0]!["endian"]!.GetValue<string>());
        Assert.StartsWith("PE (", rows[1]!["endian"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("UTF-16 LE", rows[2]!["endian"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonArray counts = (await Phase2Async(app))["variantCounts"]!.AsArray();
        Assert.Equal(3, counts.Count);
        Assert.All(counts, c => Assert.Equal(1, c!["count"]!.GetValue<long>()));

        // 5. 先頭から Enter: 0x100〜0x101。検索バーに `4D 5A` が一致した旨。
        await app.GoToAsync(0);
        Assert.Equal((0x100L, 2L), await EnterAsync(app));
        Assert.Contains("4D 5A", await FindStatusAsync(app), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-32-02")]
    public Task Strings_stream_and_can_be_cancelled() => UiTestContext.RunAsync(async ctx =>
    {
        string path = TestDataCatalog.Generate("TD-MARKERS-1G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [path],
            Hooks = new JsonObject { ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-MARKERS-1G.bin", ["delayMs"] = 2 }) },
        });

        // 1〜2. 既定の設定で「抽出」: 完了前に 1 件以上の結果が出ている。
        await app.SendAsync("stringsPanel", new JsonObject { ["show"] = true, ["extract"] = true });
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("stringsPanel"))["count"]!.GetValue<long>() >= 1,
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the first strings");
        Assert.True(state["running"]!.GetValue<bool>());

        // 3. 1 行目をクリック: 目印の 17 バイトが選択される。
        await app.SendAsync("stringsPanel", new JsonObject { ["click"] = 0 });
        await app.IdleAsync();
        Assert.Equal((0L, (long)TestDataCatalog.MarkerLength), await SelectionAsync(app));

        // 4〜5. キャンセル: 状態は「中断」、件数は 1,025 件未満で残る。
        await app.SendAsync("stringsPanel", new JsonObject { ["cancel"] = true });
        await app.WaitUntilAsync(async () => !(state = await app.SendAsync("stringsPanel"))["running"]!.GetValue<bool>(),
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the extraction to stop");
        Assert.Equal("Cancelled", state["state"]!.GetValue<string>());
        long count = state["count"]!.GetValue<long>();
        Assert.InRange(count, 1, 1024);
    });
}
