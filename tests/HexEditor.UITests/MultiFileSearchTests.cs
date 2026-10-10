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
