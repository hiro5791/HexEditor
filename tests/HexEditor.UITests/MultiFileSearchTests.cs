using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>複数ファイル検索 (FIND-30) と複数ファイル置換 (FIND-31) のパネル。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class MultiFileSearchTests
{
    private static readonly byte[] Replaced = [0x11, 0x22, 0x33, 0x44];

    private static Task<JsonObject> PanelAsync(AppSession app, JsonObject? request = null) => app.SendAsync("multiFile", request ?? []);

    private static async Task<JsonObject> SearchAsync(AppSession app, string folder, string mode = "search")
    {
        await PanelAsync(app, new JsonObject
        {
            ["show"] = true,
            ["mode"] = mode,
            ["kind"] = "Hex",
            ["query"] = "CA FE BA BE",
            ["replace"] = "11 22 33 44",
            ["folders"] = new JsonArray(folder),
        });
        await PanelAsync(app, new JsonObject { ["search"] = true });
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await PanelAsync(app))["state"]?.GetValue<string>() is "Completed" && !state["running"]!.GetValue<bool>(),
            UiTest.Scaled(TimeSpan.FromSeconds(60)), "the search");
        return state;
    }

    /// <summary>置換を実行し、確認ダイアログの「置換して保存する」を押して、終わるまで待つ。ダイアログの文言を返す。</summary>
    private static async Task<(string Dialog, JsonObject State)> ReplaceAsync(AppSession app)
    {
        await PanelAsync(app, new JsonObject { ["runReplace"] = true });
        string text = AppSession.AllText(await app.WaitForAsync("MultiFileReplaceConfirm"));
        // ダイアログのボタンは開いた直後にはまだないことがあるため、押せるまで試す。
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the confirmation");
        JsonObject state = [];
        await app.WaitUntilAsync(async () => !(state = await PanelAsync(app))["running"]!.GetValue<bool>() && state["outcomes"]!.AsArray().Count > 0,
            UiTest.Scaled(TimeSpan.FromSeconds(60)), "the replacement");
        return (text, state);
    }

    private static byte[] At(string path, long offset) => File.ReadAllBytes(path).AsSpan((int)offset, 4).ToArray();

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-30-01")]
    public Task Thousand_files_grouped_by_file() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree1000");
        SearchTrees.WriteTree1000(dir);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });

        // 1〜3. Ctrl+Shift+F で開き、`CA FE BA BE` を検索する。処理センターに処理済み / 見つかったファイル数。
        await app.KeyAsync("F", ctrl: true, shift: true);
        await app.IdleAsync();
        Assert.True((await PanelAsync(app))["visible"]!.GetValue<bool>());
        JsonObject state = await SearchAsync(app, dir);
        Assert.Contains(state["operationDetails"]!.AsArray(), d => d!.GetValue<string>().Contains("1,005 / 1,005", StringComparison.Ordinal));

        // 4. 25 ファイル (20 個の .bin と 5 個の .log)。.bin は 2 件、.log は 1 件。
        Assert.Equal(25, state["files"]!.GetValue<int>());
        JsonArray rows = state["rows"]!.AsArray();
        var files = rows.Where(r => r!["file"]!.GetValue<bool>()).Select(r => r!["path"]!.GetValue<string>()).ToList();
        Assert.Equal(20, files.Count(f => f.EndsWith(".bin", StringComparison.Ordinal)));
        Assert.Equal(5, files.Count(f => f.EndsWith(".log", StringComparison.Ordinal)));
        foreach (string f in files)
        {
            int count = rows.Count(r => !r!["file"]!.GetValue<bool>() && r["path"]!.GetValue<string>() == f);
            Assert.Equal(f.EndsWith(".bin", StringComparison.Ordinal) ? 2 : 1, count);
        }

        // 5. 一致がなかったファイルは 980。
        Assert.Equal(980, state["withoutMatches"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-30-05")]
    public Task Opening_a_result_selects_the_match() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await SearchAsync(app, dir);
        int tabs = (await app.TabNamesAsync()).Count;

        // 1〜2. r2.bin の 2 件目: r2.bin のタブが開き、0x2000〜0x2003 が選択される。
        await PanelAsync(app, new JsonObject { ["open"] = new JsonObject { ["file"] = "r2.bin", ["match"] = 1 } });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["name"]?.GetValue<string>() == "r2.bin", UiTest.Scaled(TimeSpan.FromSeconds(15)), "r2.bin");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((0x2000L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 3〜4. 1 件目: タブは増えない。0x1000〜0x1003。
        await PanelAsync(app, new JsonObject { ["open"] = new JsonObject { ["file"] = "r2.bin", ["match"] = 0 } });
        await app.IdleAsync();
        Assert.Equal(tabs + 1, (await app.TabNamesAsync()).Count);
        doc = await app.DocumentAsync();
        Assert.Equal((0x1000L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-31-01")]
    public Task Ten_files_are_replaced_after_confirmation() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });

        // 1〜2. 置換モードで検索: 10 ファイル、20 件。
        JsonObject state = await SearchAsync(app, dir, "replace");
        Assert.Equal((10, 20L), (state["files"]!.GetValue<int>(), state["matches"]!.GetValue<long>()));

        // 3〜4. 確認ダイアログ: ファイル数、件数、直接書き換え、バックアップ、元に戻せない。ボタンは「置換して保存する」「やめる」。
        (string dialog, state) = await ReplaceAsync(app);
        Assert.Contains("20 matches in 10 files", dialog, StringComparison.Ordinal);
        Assert.Contains("saved directly", dialog, StringComparison.Ordinal);
        Assert.Contains("backup", dialog, StringComparison.Ordinal);
        Assert.Contains("can't be undone", dialog, StringComparison.Ordinal);
        Assert.Contains("Replace and save", dialog, StringComparison.Ordinal);
        Assert.Contains("Don't replace", dialog, StringComparison.Ordinal);

        // 5. 10 ファイル、20 件、スキップ 0。
        Assert.Equal("Replaced 20 matches in 10 files. Skipped: 0 files. Not processed: 0 files.", state["summary"]!.GetValue<string>());

        // 6. 各ファイルの 2 か所が置換され、.bak は置換前と同じ。
        byte[] original = SearchTrees.Tree10File();
        for (int i = 0; i < 10; i++)
        {
            string path = Path.Combine(dir, $"r{i}.bin");
            Assert.Equal(Replaced, At(path, 0x1000));
            Assert.Equal(Replaced, At(path, 0x2000));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-31-02")]
    public Task Unchecked_files_are_not_changed() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        File.WriteAllBytes(Path.Combine(dir, "r0.bin.bak"), []);
        string r1 = Path.Combine(dir, "r1.bin");
        DateTime r1Time = File.GetLastWriteTimeUtc(r1);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });

        // 1〜2. 検索し、r1.bin のファイルと r2.bin の 2 件目のチェックを外す。
        await SearchAsync(app, dir, "replace");
        await PanelAsync(app, new JsonObject { ["check"] = new JsonObject { ["file"] = "r1.bin", ["value"] = false } });
        await PanelAsync(app, new JsonObject { ["check"] = new JsonObject { ["file"] = "r2.bin", ["match"] = 1, ["value"] = false } });

        // 3〜4. 置換する。
        await ReplaceAsync(app);
        Assert.Equal(SearchTrees.Tree10File(), File.ReadAllBytes(r1));
        Assert.Equal(r1Time, File.GetLastWriteTimeUtc(r1));
        Assert.False(File.Exists(r1 + ".bak"));
        string r2 = Path.Combine(dir, "r2.bin");
        Assert.Equal(Replaced, At(r2, 0x1000));
        Assert.Equal(SearchTrees.Marker, At(r2, 0x2000));
        Assert.True(File.Exists(Path.Combine(dir, "r0.bin.bak1")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(dir, "r0.bin.bak")));
    });

    /// <summary>状態が <paramref name="expected"/> になり、検索が終わるまで待つ。</summary>
    private static async Task<JsonObject> WaitForStateAsync(AppSession app, string expected)
    {
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await PanelAsync(app))["state"]?.GetValue<string>() == expected && !state["running"]!.GetValue<bool>(),
            UiTest.Scaled(TimeSpan.FromSeconds(60)), expected);
        return state;
    }

    [Fact]
    public Task Many_matches_are_listed_lazily_and_continue_searches_further() => UiTestContext.RunAsync(async ctx =>
    {
        // 60 ファイル × 2,000 件 (0 の並びの中の `00`)。上限 50,000 件ちょうどで止め、「続ける」で上限を 2 倍にして続きを探す
        // (FIND-30 の仕様 8)。メモリ上は 20,000 件まで (残りは一時ファイル)。一覧の行は見えている分だけ作る。
        string dir = Directory.CreateDirectory(Path.Combine(ctx.Root, "zeros")).FullName;
        for (int i = 0; i < 60; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"z{i:D2}.bin"), new byte[2_000]);
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "search.findAll.limit", ["value"] = 50_000 });
        await PanelAsync(app, new JsonObject
        {
            ["show"] = true,
            ["kind"] = "Hex",
            ["query"] = "00",
            ["folders"] = new JsonArray(dir),
            ["memoryLimit"] = 20_000,
        });
        await PanelAsync(app, new JsonObject { ["search"] = true });
        JsonObject state = await WaitForStateAsync(app, "LimitReached");
        Assert.Equal(50_000L, state["matches"]!.GetValue<long>());
        Assert.True(state["canContinue"]!.GetValue<bool>());
        Assert.Contains("Stopped at the limit (50,000 matches)", state["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(state["inMemory"]!.GetValue<int>() <= 20_000);
        long rows = state["rowCount"]!.GetValue<long>();
        Assert.Equal(rows, state["listCount"]!.GetValue<long>());
        Assert.Equal(50_000 + state["files"]!.GetValue<int>(), rows);

        // 続ける: 100,000 件で止まり、もう一度続けると 120,000 件で完了する。重複も取りこぼしもない。
        await PanelAsync(app, new JsonObject { ["continue"] = true });
        state = await WaitForStateAsync(app, "LimitReached");
        Assert.Equal(100_000L, state["matches"]!.GetValue<long>());
        await PanelAsync(app, new JsonObject { ["continue"] = true });
        state = await WaitForStateAsync(app, "Completed");
        Assert.Equal((60, 120_000L), (state["files"]!.GetValue<int>(), state["matches"]!.GetValue<long>()));
        Assert.False(state["canContinue"]!.GetValue<bool>());
        Assert.Equal(120_060L, state["rowCount"]!.GetValue<long>());
        JsonObject last = (await PanelAsync(app, new JsonObject { ["rowsFrom"] = 120_059, ["rowsCount"] = 1 }))["rows"]!.AsArray()[0]!.AsObject();
        Assert.Equal(1_999L, last["offset"]!.GetValue<long>());

        // 12 万行のうち、作った行は一覧が表示した分だけ。
        long created = state["createdRows"]!.GetValue<long>();
        Assert.True(created < 10_000, created.ToString(System.Globalization.CultureInfo.InvariantCulture));
        state = await PanelAsync(app, new JsonObject { ["rowsFrom"] = 60_000, ["rowsCount"] = 10, ["realize"] = true });
        Assert.Equal(created + 10, state["createdRows"]!.GetValue<long>());
    });

    [Fact]
    public Task Files_stopped_at_the_per_file_limit_are_marked() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Directory.CreateDirectory(Path.Combine(ctx.Root, "zeros")).FullName;
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"z{i}.bin"), new byte[2_000]);
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await PanelAsync(app, new JsonObject { ["show"] = true, ["kind"] = "Hex", ["query"] = "00", ["folders"] = new JsonArray(dir), ["perFileLimit"] = 500 });
        await PanelAsync(app, new JsonObject { ["search"] = true });
        JsonObject state = await WaitForStateAsync(app, "Completed");
        Assert.Equal(1_500L, state["matches"]!.GetValue<long>());
        state = await PanelAsync(app, new JsonObject { ["rowsCount"] = 2_000 });
        var fileRows = state["rows"]!.AsArray().Where(r => r!["file"]!.GetValue<bool>()).ToList();
        Assert.Equal(3, fileRows.Count);
        Assert.All(fileRows, r => Assert.Contains("(stopped at the limit of 500 matches per file)", r!["title"]!.GetValue<string>(), StringComparison.Ordinal));
    });

    [Fact]
    public Task Find_bar_conditions_and_localized_errors() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });

        // 誤りは検索バーと同じ文言 (種類の名前ではない)。
        JsonObject state = await PanelAsync(app, new JsonObject { ["show"] = true, ["kind"] = "Hex", ["query"] = "CA F", ["folders"] = new JsonArray(dir) });
        string error = state["error"]!.GetValue<string>();
        Assert.StartsWith("The search term is not valid (", error, StringComparison.Ordinal);
        Assert.DoesNotContain("OddDigits", error, StringComparison.Ordinal);

        // 検索バーの複数の語 (FIND-26) を取り込んで探す: `CA FE` と `BA BE` が各ファイルに 2 件ずつ。
        await app.KeyAsync("F", ctrl: true);
        await app.SendAsync("findBarPhase2", new JsonObject
        {
            ["terms"] = new JsonArray(new JsonObject { ["kind"] = "Hex", ["text"] = "CA FE" }, new JsonObject { ["kind"] = "Hex", ["text"] = "BA BE" }),
        });
        state = await PanelAsync(app, new JsonObject { ["useFindBar"] = true });
        Assert.Equal("Multiple terms from the find bar (2)", state["extras"]!.GetValue<string>());
        Assert.Equal(string.Empty, state["error"]!.GetValue<string>());
        await PanelAsync(app, new JsonObject { ["search"] = true });
        state = await WaitForStateAsync(app, "Completed");
        Assert.Equal((10, 40L), (state["files"]!.GetValue<int>(), state["matches"]!.GetValue<long>()));

        // パネルの欄のオプション: 16 bit ビッグエンディアンの整数 0xCAFE は各ファイルに 2 件。
        state = await PanelAsync(app, new JsonObject
        {
            ["clearExtras"] = true,
            ["kind"] = "Integer",
            ["query"] = "0xCAFE",
            ["options"] = new JsonObject { ["intBits"] = 16, ["endian"] = 1, ["sign"] = 2 },
        });
        Assert.Equal(string.Empty, state["extras"]!.GetValue<string>());
        await PanelAsync(app, new JsonObject { ["search"] = true });
        state = await WaitForStateAsync(app, "Completed");
        Assert.Equal((10, 20L), (state["files"]!.GetValue<int>(), state["matches"]!.GetValue<long>()));
    });

    [Fact]
    public Task Cancelled_replacement_marks_files_not_processed_and_pads_with_the_filler() => UiTestContext.RunAsync(async ctx =>
    {
        // 「埋めて長さを保つ」で埋め草 FF (FIND-24 の仕様 3): 4 バイトの一致を 2 バイトの置換語で置換すると、残りは FF。
        // 3 つ目のファイルの前でキャンセルすると、残りのファイルは「処理していない」と表示される (FIND-31 の「巨大ファイル・長時間処理」)。
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await PanelAsync(app, new JsonObject
        {
            ["show"] = true,
            ["mode"] = "replace",
            ["kind"] = "Hex",
            ["query"] = "CA FE BA BE",
            ["replace"] = "11 22",
            ["folders"] = new JsonArray(dir),
            ["backup"] = false,
            ["options"] = new JsonObject { ["lengthPolicy"] = 1, ["filler"] = 1 },
            ["cancelReplaceAt"] = 2,
        });
        await PanelAsync(app, new JsonObject { ["search"] = true });
        await WaitForStateAsync(app, "Completed");
        (_, JsonObject state) = await ReplaceAsync(app);
        JsonArray outcomes = state["outcomes"]!.AsArray();
        Assert.Equal(10, outcomes.Count);
        Assert.Equal(2, outcomes.Count(o => o!["status"]!.GetValue<string>() == "Replaced"));
        Assert.Equal(8, outcomes.Count(o => o!["status"]!.GetValue<string>() == "NotProcessed"));
        Assert.Equal("Replaced 4 matches in 2 files. Skipped: 0 files. Not processed: 8 files.", state["summary"]!.GetValue<string>());
        var notProcessedRows = state["rows"]!.AsArray().Where(r => r!["file"]!.GetValue<bool>() && r["title"]!.GetValue<string>().EndsWith("— not processed", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, notProcessedRows.Count);

        byte[] padded = [0x11, 0x22, 0xFF, 0xFF];
        int replacedFiles = 0;
        for (int i = 0; i < 10; i++)
        {
            string path = Path.Combine(dir, $"r{i}.bin");
            if (At(path, 0x1000).SequenceEqual(padded))
            {
                replacedFiles++;
                Assert.Equal(padded, At(path, 0x2000));
            }
            else
            {
                Assert.Equal(SearchTrees.Tree10File(), File.ReadAllBytes(path));
            }
        }

        Assert.Equal(2, replacedFiles);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-31-04")]
    public Task Open_files_are_replaced_in_the_editor() => UiTestContext.RunAsync(async ctx =>
    {
        string dir = Path.Combine(ctx.Root, "tree10");
        SearchTrees.WriteTree10(dir);
        string r7 = Path.Combine(dir, "r7.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [r7] });

        // 1〜2. 検索して置換: r7.bin は「エディタで置換 (未保存)」。
        await SearchAsync(app, dir, "replace");
        (_, JsonObject state) = await ReplaceAsync(app);
        JsonObject seven = state["outcomes"]!.AsArray().Single(o => o!["path"]!.GetValue<string>() == r7)!.AsObject();
        Assert.Equal("ReplacedInEditor", seven["status"]!.GetValue<string>());
        Assert.Contains(state["rows"]!.AsArray(), r => r!["path"]!.GetValue<string>() == r7 && r["title"]!.GetValue<string>().Contains("replaced in the editor (unsaved", StringComparison.Ordinal));

        // 3. ディスク上は元のまま。.bak は作らない。
        Assert.Equal(SearchTrees.Marker, At(r7, 0x1000));
        Assert.False(File.Exists(r7 + ".bak"));

        // 4. タブの 0x1000 は置換後で、変更あり。
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["name"]?.GetValue<string>() == "r7.bin", UiTest.Scaled(TimeSpan.FromSeconds(10)), "r7.bin");
        Assert.Equal(Replaced, await app.BytesAsync(0x1000, 4));
        Assert.True((await app.DocumentAsync())["modified"]!.GetValue<bool>());

        // 5. Ctrl+Z 1 回で両方が元に戻る。
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(SearchTrees.Marker, await app.BytesAsync(0x1000, 4));
        Assert.Equal(SearchTrees.Marker, await app.BytesAsync(0x2000, 4));
    });
}
