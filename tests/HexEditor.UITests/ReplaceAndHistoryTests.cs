using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.SearchResultsTests;

namespace HexEditor.UITests;

/// <summary>置換 (FIND-22〜FIND-24)、インクリメンタルサーチ (FIND-27)、検索履歴 (FIND-28)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ReplaceAndHistoryTests
{
    private static readonly byte[] Original = [0x12, 0x34, 0x56, 0x78];
    private static readonly byte[] Replaced = [0x87, 0x65, 0x43, 0x21];

    /// <summary>設定ファイルは変更から 500 ms 後にまとめて書く (UI-23)。テスト用の終了の命令は書き込みを待たないので、その分待つ。</summary>
    private static readonly TimeSpan SettingsWriteDelay = TimeSpan.FromMilliseconds(1500);

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-22-01")]
    public Task Ctrl_h_replaces_and_moves_to_the_next_match() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });

        // 1〜2. Ctrl+H: 検索欄の下に置換欄があり、検索欄にフォーカスがある。
        Assert.Equal("menu:Command_Replace", (await app.KeyAsync("H", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        Assert.True((await FindBarAsync(app))["replaceMode"]!.GetValue<bool>());
        Assert.True(await app.IsShownAsync("Find_Replace"));
        Assert.Equal("TextBox:Find_Query", await app.FocusedAsync());

        // 3. `12 34 56 78` → `87 65 43 21`。Enter で最初の一致 (0x0)。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });
        await app.UiaSetValueAsync("Find_Query", "12 34 56 78");
        await app.UiaSetValueAsync("Find_Replace", "87 65 43 21");
        await EnterAsync(app, 0);

        // 4〜5. 「置換」: 0x0〜0x3 は `87 65 43 21`、選択は次の一致 (0x400)。
        await app.UiaInvokeAsync("Find_ReplaceOne");
        await WaitForSelectionAsync(app, 0x400);
        Assert.Equal(Replaced, await app.BytesAsync(0, 4));

        // 6. 「スキップ」: 選択は 0x800。0x400 はそのまま。
        await app.UiaInvokeAsync("Find_Skip");
        await WaitForSelectionAsync(app, 0x800);
        Assert.Equal(Original, await app.BytesAsync(0x400, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-22-02")]
    public Task One_replacement_is_one_undo() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });
        await OpenReplaceAsync(app, "12 34 56 78", "87 65 43 21");
        await EnterAsync(app, 0);

        // 1. 「置換」を 2 回 (0x0 と 0x400)。
        await app.UiaInvokeAsync("Find_ReplaceOne");
        await WaitForSelectionAsync(app, 0x400);
        await app.UiaInvokeAsync("Find_ReplaceOne");
        await WaitForSelectionAsync(app, 0x800);

        // 2〜3. Hex ビューで Ctrl+Z を 1 回: 0x0 はそのまま、0x400 は元に戻る。
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(Replaced, await app.BytesAsync(0, 4));
        Assert.Equal(Original, await app.BytesAsync(0x400, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-22-04")]
    public Task Read_only_documents_cannot_be_replaced() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "readonly.bin");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

            // 1〜2. 置換欄とボタンは無効で、理由が出る。
            await OpenReplaceAsync(app, "10 11", null);
            Assert.False((await app.ElementAsync("Find_Replace"))["isEnabled"]!.GetValue<bool>());
            Assert.False((await app.ElementAsync("Find_ReplaceOne"))["isEnabled"]!.GetValue<bool>());
            Assert.False((await app.ElementAsync("Find_ReplaceAll"))["isEnabled"]!.GetValue<bool>());
            Assert.Contains("read-only", await app.UiaNameAsync("Find_ReplaceReason"), StringComparison.Ordinal);

            // 3. 検索はできる: 0x10〜0x11。
            await EnterAsync(app, 0x10);
            Assert.Equal(2, (await app.DocumentAsync())["selectionLength"]!.GetValue<long>());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-23-02")]
    public Task Cancelling_replace_all_while_searching_changes_nothing() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000());
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Slow(path, 4000) });
        await OpenReplaceAsync(app, "12 34 56 78", "87 65 43 21");

        // 1〜2. 「すべて置換」。進捗バーが出たらキャンセルする。
        await app.UiaInvokeAsync("Find_ReplaceAll");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["findBarProgress"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the progress bar");
        await app.UiaInvokeAsync("Find_Cancel");
        await app.WaitUntilAsync(async () => !(await app.StateAsync())["findBarProgress"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the end");
        await app.WaitForLogAsync(l => l.Contains("Replace all: cancelled", StringComparison.Ordinal), "the cancellation");

        // 3. `87 65 43 21` は 0 件、編集履歴は 0 件、変更なし。
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(1, doc["historyCount"]!.GetValue<long>());
        Assert.False(doc["modified"]!.GetValue<bool>());
        Assert.Equal(Original, await app.BytesAsync(0, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-23-03")]
    public Task Replacing_over_a_million_matches_asks_first() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1500K.bin", Hits1500K())] });
        await OpenReplaceAsync(app, "AB CD", "00 00");

        // 1〜2. 確認ダイアログ: 件数と Undo の情報の容量。ボタンは「置換する」「やめる」。
        await app.UiaInvokeAsync("Find_ReplaceAll");
        var dialog = await app.WaitForDialogAsync("ReplaceAllConfirm");
        string text = await app.WaitForDialogTextAsync(dialog, "1,500,000");
        Assert.Contains("undo", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Replace", UiHelpers.DialogButtons(dialog));
        Assert.Contains("Don't replace", UiHelpers.DialogButtons(dialog));

        // 3. 「やめる」: 編集履歴は 0 件、0〜1 は `AB CD`。
        await app.InvokeDialogButtonAsync("Don't replace");
        await app.WaitForLogAsync(l => l.Contains("Replace all: end (-1)", StringComparison.Ordinal), "the end");
        Assert.Equal(1, (await app.DocumentAsync())["historyCount"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0xAB, 0xCD }, await app.BytesAsync(0, 2));

        // 4〜5. もう一度「すべて置換」→「置換する」: 「1,500,000 件置換しました」。5,999,996〜5,999,997 は `00 00`。
        await app.UiaInvokeAsync("Find_ReplaceAll");
        await app.WaitForDialogAsync("ReplaceAllConfirm");
        await app.InvokeDialogButtonAsync("Replace");
        await app.WaitForNotificationAsync(m => m.Contains("Replaced 1,500,000 matches", StringComparison.Ordinal), "the completion");
        Assert.Equal(new byte[] { 0x00, 0x00 }, await app.BytesAsync(5_999_996, 2));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-23-04")]
    public Task Undo_in_the_completion_infobar_restores_everything() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });
        await OpenReplaceAsync(app, "12 34 56 78", "87 65 43 21");

        // 1. 「すべて置換」。
        await app.UiaInvokeAsync("Find_ReplaceAll");
        await app.WaitForNotificationAsync(m => m.Contains("Replaced 1,000 matches", StringComparison.Ordinal), "the completion");
        Assert.Equal(Replaced, await app.BytesAsync(0x400 * 999, 4));

        // 2. InfoBar の「元に戻す」。
        await app.InvokeDialogButtonAsync("Undo");

        // 3. `12 34 56 78` は 1,000 件、変更なし。
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the undo");
        await FindAllAsync(app);
        JsonObject results = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");
        Assert.Equal(1000, results["count"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-24-02")]
    public Task Replacing_with_a_different_length() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-MARK-1M.bin", Mark1M())] });

        // 1〜2. `CA FE BA BE` → `01 02 03 04 05 06`: 既定は「長さを変える」で、長さが違うため強調されている。
        await OpenReplaceAsync(app, "CA FE BA BE", "01 02 03 04 05 06");
        JsonObject bar = await FindBarAsync(app);
        Assert.Equal(0, bar["policyIndex"]!.GetValue<int>());
        Assert.True(bar["policyHighlighted"]!.GetValue<bool>());

        // 3〜4. 「すべて置換」: 長さは 1,048,578。0x80000〜0x80006 は `01 02 03 04 05 06 00`。
        await app.UiaInvokeAsync("Find_ReplaceAll");
        await app.WaitForNotificationAsync(m => m.Contains("Replaced 1 match.", StringComparison.Ordinal), "the replacement");
        Assert.Equal(1_048_578, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 0 }, await app.BytesAsync(0x80000, 7));

        // 5〜6. Ctrl+Z で戻し、「埋めて長さを保つ」、指定バイト `CC`、置換語 `01 02`: 長さは 1,048,576。`01 02 CC CC 00`。
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_LengthPolicy", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Filler", ["index"] = 3 });
        await app.UiaSetValueAsync("Find_FillerCustom", "CC");
        await app.UiaSetValueAsync("Find_Replace", "01 02");
        await app.UiaInvokeAsync("Find_ReplaceAll");
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0x80000, 5))[0] == 1, TimeSpan.FromSeconds(10), "the padded replacement");
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 1, 2, 0xCC, 0xCC, 0 }, await app.BytesAsync(0x80000, 5));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-24-03")]
    public Task Fixed_length_documents_cannot_change_the_length() => UiTestContext.RunAsync(async ctx =>
    {
        // 物理ディスク (TD-VHDX-MBR) の代わりに、長さを変えられない書き込み可能なデータソース (テスト用の仮想のデータソース) を開く。
        // 物理ディスクを開くには管理者権限と VHDX の接続が必要で、この PC では行わない。「長さを変えられない」ことだけが条件なので同じ扱いになる。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "fixed-disk", ["length"] = 1024 * 1024, ["content"] = "fill", ["fill"] = 0x55 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["document"] is not null, TimeSpan.FromSeconds(10), "the virtual source");

        // 1〜2. `55 AA` → `55`: 既定は「埋めて長さを保つ」。「長さを変える」は選べない。
        await OpenReplaceAsync(app, "55 AA", "55");
        JsonObject bar = await FindBarAsync(app);
        Assert.Equal(1, bar["policyIndex"]!.GetValue<int>());
        Assert.False(bar["policyChangeEnabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-27-01")]
    public Task Searches_as_you_type_but_not_on_odd_digits() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1024 * 1024];
        data[0x100] = 0xDE;
        data[0x200] = 0xDE;
        data[0x201] = 0xAD;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("zero-dead.bin", data)] });

        // 1. Ctrl+F で種類「Hex」。
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });

        // 2. `D`: 選択はなく、カーソルは 0。
        await TypeQueryAsync(app, "D");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((0L, 0L), (doc["cursor"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 3. `DE`: 0x100 (長さ 1)。
        await TypeQueryAsync(app, "DE");
        doc = await app.DocumentAsync();
        Assert.Equal((0x100L, 1L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 4. `DEA` (奇数桁): 手順 3 のまま。
        await TypeQueryAsync(app, "DEA");
        doc = await app.DocumentAsync();
        Assert.Equal((0x100L, 1L, 0x100L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>(), doc["cursor"]!.GetValue<long>()));

        // 5. `DEAD`: 起点 0 から探し直して 0x200〜0x201。
        await TypeQueryAsync(app, "DEAD");
        doc = await app.DocumentAsync();
        Assert.Equal((0x200L, 2L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 6. Esc: 検索バーが閉じ、カーソルは 0x200 に留まる。
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Escape" });
        await app.IdleAsync();
        Assert.False((await FindBarAsync(app))["open"]!.GetValue<bool>());
        Assert.Equal(0x200, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-27-02")]
    public Task Each_input_cancels_the_previous_search() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.TestData("TD-SPARSE-100G");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Slow(path, 8) });
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });

        // 1〜3. `11`、`11 22`、`11 22 33`、`11 22 33 44` を 200 ms 間隔で入れ、2 秒待つ。
        foreach (string text in new[] { "1", "11", "11 2", "11 22", "11 22 3", "11 22 33", "11 22 33 4", "11 22 33 44" })
        {
            await app.UiaSetValueAsync("Find_Query", text);
            await Task.Delay(200);
        }

        await Task.Delay(2000);

        // 4. 偶数桁になるたびに検索が始まり、新しい検索の前に前の検索が終わっている。実行中の検索はどの時点でも 1 つ以下。
        List<string> log = [.. (await app.LogAsync()).Where(l => l.Contains("Incremental search:", StringComparison.Ordinal))];
        int starts = log.Count(l => l.Contains(": start #", StringComparison.Ordinal));
        Assert.InRange(starts, 4, 4);
        Assert.All(log.Where(l => l.Contains(": start #", StringComparison.Ordinal)), l => Assert.Contains("(running 1)", l, StringComparison.Ordinal));
        int running = 0;
        foreach (string line in log)
        {
            if (line.Contains(": start #", StringComparison.Ordinal))
            {
                Assert.Equal(0, running);
                running++;
            }
            else if (line.Contains(": end #", StringComparison.Ordinal))
            {
                running--;
            }
        }

        Assert.True(log.Count(l => l.Contains(": cancel #", StringComparison.Ordinal)) >= 3, string.Join("\n", log));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-28-01")]
    public Task Up_recalls_earlier_queries_and_conditions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1. Hex `10 11` で Enter。
        await OpenFindAsync(app, 0, "10 11");
        await EnterAsync(app, 0x10);

        // 2. テキスト (UTF-16LE)、大文字と小文字を区別する、`ab` で Enter。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "UTF-16 LE" });
        await app.CommandAsync("Find_CaseSensitive");
        await app.UiaSetValueAsync("Find_Query", "ab");
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();

        // 3. 整数、16 bit、`0x1234` で Enter。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 2 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_IntBits", ["text"] = "16 bit" });
        await app.UiaSetValueAsync("Find_Query", "0x1234");
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();

        // 4〜5. ↑ を 2 回: `ab`、テキスト、UTF-16LE、区別する。
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Up" });
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Up" });
        await app.IdleAsync();
        JsonObject c = (await FindBarAsync(app))["conditions"]!.AsObject();
        Assert.Equal("ab", (await app.ElementAsync("Find_Query"))["text"]!.GetValue<string>());
        Assert.Equal(("Text", "utf-16le", true), (c["kind"]!.GetValue<string>(), c["encoding"]!.GetValue<string>(), c["caseSensitive"]!.GetValue<bool>()));

        // 6. ↓ を 1 回: `0x1234`、整数、16 bit。
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Down" });
        await app.IdleAsync();
        c = (await FindBarAsync(app))["conditions"]!.AsObject();
        Assert.Equal("0x1234", (await app.ElementAsync("Find_Query"))["text"]!.GetValue<string>());
        Assert.Equal(("Integer", 16), (c["kind"]!.GetValue<string>(), c["bits"]!.GetValue<int>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-28-02")]
    public Task History_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string data = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [data] });

        // 1. `10 11`、`20 21`、`30 31` を順に検索する。
        await OpenFindAsync(app, 0, "10 11");
        await EnterAsync(app, 0x10);
        await app.UiaSetValueAsync("Find_Query", "20 21");
        await EnterAsync(app, 0x20);
        await app.UiaSetValueAsync("Find_Query", "30 31");
        await EnterAsync(app, 0x30);

        // 2. Ctrl+H で置換欄に `FF FF` を入れて「置換」。
        await app.KeyAsync("H", ctrl: true);
        await app.IdleAsync();
        await app.UiaSetValueAsync("Find_Replace", "FF FF");
        await app.UiaInvokeAsync("Find_ReplaceOne");
        await app.IdleAsync();

        // 3. 終了して、同じ設定フォルダで起動し直す。
        await Task.Delay(SettingsWriteDelay);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [data] });

        // 4. 履歴は新しい順に `30 31`、`20 21`、`10 11`。
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        JsonObject bar = await FindBarAsync(app);
        Assert.Equal(["30 31", "20 21", "10 11"], bar["history"]!.AsArray().Select(n => n!.GetValue<string>()));

        // 5. 置換欄で ↑: `FF FF` (検索欄とは別の履歴)。
        await app.KeyAsync("H", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("replaceKey", new JsonObject { ["key"] = "Up" });
        await app.IdleAsync();
        Assert.Equal("FF FF", (await app.ElementAsync("Find_Replace"))["text"]!.GetValue<string>());

        // 6. 4 KiB を超える検索語 (4,097 文字) は履歴に入らない。
        string longQuery = string.Concat(Enumerable.Repeat("AB ", 1366)).TrimEnd() + "  ";
        Assert.True(longQuery.Length > 4096);
        await app.UiaSetValueAsync("Find_Query", longQuery);
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
        bar = await FindBarAsync(app);
        Assert.DoesNotContain(bar["history"]!.AsArray(), n => n!.GetValue<string>() == longQuery);
        Assert.Equal(3, bar["history"]!.AsArray().Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-28-03")]
    public Task Removing_and_clearing_the_history() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenFindAsync(app, 0, "10 11");
        await EnterAsync(app, 0x10);
        await app.UiaSetValueAsync("Find_Query", "20 21");
        await EnterAsync(app, 0x20);
        await app.UiaSetValueAsync("Find_Query", "30 31");
        await EnterAsync(app, 0x30);

        // 1〜2. ドロップダウンで `20 21` を削除: `30 31`、`10 11`。
        JsonObject bar = await app.SendAsync("findHistory", new JsonObject { ["action"] = "delete", ["index"] = 1 });
        Assert.Equal(["30 31", "10 11"], bar["history"]!.AsArray().Select(n => n!.GetValue<string>()));

        // 3〜4. 「検索履歴を消去」: 一覧は空。↑ を押しても検索欄は変わらない。
        await app.CommandAsync("Command_ClearSearchHistory");
        Assert.Empty((await FindBarAsync(app))["history"]!.AsArray());
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Up" });
        await app.IdleAsync();
        Assert.Equal("30 31", (await app.ElementAsync("Find_Query"))["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-28-04")]
    public Task Limit_zero_saves_no_history() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"search.history.limit\": 0}");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1〜2. `10 11` を検索しても、一覧は空。
        await OpenFindAsync(app, 0, "10 11");
        await EnterAsync(app, 0x10);
        Assert.Empty((await FindBarAsync(app))["history"]!.AsArray());

        // 3. 終了後、設定フォルダのファイルに `10 11` がない。
        await Task.Delay(SettingsWriteDelay);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        foreach (string file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).Length < 16 * 1024 * 1024)
            {
                Assert.DoesNotContain("10 11", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
            }
        }
    });

    // ---- 補助 ----

    private static Task<JsonObject> FindBarAsync(AppSession app) => app.SendAsync("findBar");

    private static async Task OpenReplaceAsync(AppSession app, string find, string? replace)
    {
        await OpenFindAsync(app, 0, find, replace: true);
        if (replace is not null)
        {
            await app.UiaSetValueAsync("Find_Replace", replace);
        }
    }

    /// <summary>検索欄で Enter を押し、<paramref name="expected"/> が選ばれるまで待つ。</summary>
    private static async Task EnterAsync(AppSession app, long expected)
    {
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await WaitForSelectionAsync(app, expected);
    }

    private static Task WaitForSelectionAsync(AppSession app, long start) =>
        app.WaitUntilAsync(async () => (await app.DocumentAsync()) is var d && d["selectionStart"]!.GetValue<long>() == start && d["selectionLength"]!.GetValue<long>() > 0,
            TimeSpan.FromSeconds(10), $"the selection at 0x{start:X}");

    /// <summary>検索欄に入れて 300 ms 待つ (インクリメンタルサーチの 150 ms の待ちと検索の時間)。</summary>
    private static async Task TypeQueryAsync(AppSession app, string text)
    {
        await app.UiaSetValueAsync("Find_Query", text);
        await Task.Delay(300);
        await app.IdleAsync();
    }
}
