using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// チェックサムファイルの作成・検証 (TOOL-18・TOOL-19) とフォルダの比較 (TOOL-20)。ファイルの選択ダイアログは、テスト用の代わりの結果
/// (openPicker) で置き換える。結果は結果一覧の内容 (テスト用の命令 tcSums / tcFolder) で読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ToolsFilesTests
{
    private static string CopyFolder(UiTestContext ctx, string id)
    {
        string source = TestDataCatalog.FolderOf(id);
        string target = Path.Combine(ctx.Root, id);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private static Task<JsonObject> Sums(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcSums", ToolsCalcTests.Args(action, values));

    private static Task<JsonObject> Folder(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcFolder", ToolsCalcTests.Args(action, values));

    private static Task SetPickerAsync(AppSession app, string path) =>
        app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray(path) } });

    private static async Task<JsonObject> WaitForVerificationAsync(AppSession app, int rows)
    {
        JsonObject state = new();
        await app.WaitUntilAsync(async () =>
        {
            state = await Sums(app, "state");
            return !state["running"]!.GetValue<bool>() && state["checksumRows"] is JsonArray found && found.Count == rows;
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the checksum verification");
        return state;
    }

    private static IEnumerable<JsonObject> Cells(JsonObject state) =>
        state["rows"]!.AsArray().Select(r => r!.AsArray().Select(c => c!.AsObject()).ToArray()).Select(cells => new JsonObject
        {
            ["state"] = cells[0]["text"]!.DeepClone(),
            ["glyph"] = cells[0]["glyph"]?.DeepClone(),
            ["path"] = cells[1]["text"]!.DeepClone(),
        });

    [Fact]
    public Task Create_dialog_writes_the_checksum_file_and_asks_before_replacing() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = CopyFolder(ctx, "TD-TOOL-TREE");
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });

        // ダイアログに対象のフォルダを入れて「作成」を押すと、SHA256SUMS ができる。
        await app.CommandAsync("Command_ChecksumCreate");
        await app.WaitForAsync("ChecksumCreateDialog");
        await app.UiaSetValueAsync("Sums_Targets", folder);
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("PrimaryButton")).IsEnabled, TimeSpan.FromSeconds(10), "the Create button");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        string sums = Path.Combine(folder, "SHA256SUMS");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(sums)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "SHA256SUMS");
        Assert.Equal(5, File.ReadAllLines(sums).Length);

        // もう一度作ると、上書きの確認が出る。キャンセルすると、ファイルは変わらない。
        File.AppendAllText(sums, "# marker\n");
        await app.CommandAsync("Command_ChecksumCreate");
        await app.WaitForAsync("ChecksumCreateDialog");
        await app.UiaSetValueAsync("Sums_Targets", folder);
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("PrimaryButton")).IsEnabled, TimeSpan.FromSeconds(10), "the Create button");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitForAsync("ChecksumOverwriteDialog");
        // ダイアログの要素が出ても、ボタンはテンプレートが当たる次の配置まで作られない。ボタンが見つかるまで押し直す。
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Dialog button not found", StringComparison.Ordinal))
            {
                return false;
            }
        }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the Cancel button of the overwrite dialog");
        await app.IdleAsync();
        Assert.Contains("# marker", File.ReadAllText(sums), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-19-01")]
    public Task Only_the_tampered_file_is_a_mismatch() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = CopyFolder(ctx, "TD-TOOL-TREE");
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });
        JsonObject created = await Sums(app, "create", ("targets", new[] { folder }));
        Assert.Equal(5, created["files"]!.GetValue<int>());

        // a.bin のオフセット 10 の 1 バイトを書き換える。
        string a = Path.Combine(folder, "a.bin");
        byte[] data = File.ReadAllBytes(a);
        data[10] ^= 0xFF;
        File.WriteAllBytes(a, data);

        await SetPickerAsync(app, Path.Combine(folder, "SHA256SUMS"));
        await app.CommandAsync("Command_ChecksumVerify");
        JsonObject state = await WaitForVerificationAsync(app, 5);

        var rows = state["checksumRows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(["a.bin"], rows.Where(r => r["status"]!.GetValue<string>() == "Mismatch").Select(r => r["path"]!.GetValue<string>()));
        Assert.Equal(4, rows.Count(r => r["status"]!.GetValue<string>() == "Match"));
        Assert.Equal("Match 4, Mismatch 1", state["summary"]!.GetValue<string>());
        Assert.All(rows, r => Assert.NotNull(r["actual"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-19-03")]
    public Task Opening_a_md5_file_offers_to_verify_it() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = CopyFolder(ctx, "TD-TOOL-MD5");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [Path.Combine(folder, "x.bin.md5")], Hooks = ToolsCalcTests.EnUs });

        await app.WaitUntilAsync(() => Task.FromResult(app.NotificationsText().Contains("checksum file", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(20), "the InfoBar");
        await app.WaitForAsync("Notification_Action");
        Assert.Equal("Verify", await app.UiaNameAsync("Notification_Action"));

        await app.CommandAsync("Notification_Action");
        JsonObject state = await WaitForVerificationAsync(app, 1);
        JsonObject row = state["checksumRows"]![0]!.AsObject();
        Assert.Equal(("x.bin", "Match", "md5"), (row["path"]!.GetValue<string>(), row["status"]!.GetValue<string>(), row["algorithm"]!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-19-04")]
    public Task Statuses_have_distinct_icons_and_words() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = CopyFolder(ctx, "TD-TOOL-SUMS-MIXED");
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });

        // locked.bin は共有なしで開いたままにして、読み取りエラーにする。
        using var locked = new FileStream(Path.Combine(folder, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        await Sums(app, "verify", ("path", Path.Combine(folder, "SHA256SUMS")));
        JsonObject state = await WaitForVerificationAsync(app, 4);

        var cells = Cells(state).ToList();
        string[] texts = [.. cells.Select(c => c["state"]!.GetValue<string>())];
        string[] glyphs = [.. cells.Select(c => c["glyph"]!.GetValue<string>())];
        Assert.Equal(["Match", "Mismatch", "Read error", "Missing"], texts);
        Assert.Equal(4, glyphs.Distinct().Count());
        Assert.All(glyphs, g => Assert.NotEqual(string.Empty, g));
        Assert.Equal("Match 1, Mismatch 1, Missing 1, Read error 1", state["summary"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-20-01")]
    public Task Different_and_one_sided_files_are_listed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });
        string left = TestDataCatalog.FolderOf("TD-TOOL-FOLDER-L"), right = TestDataCatalog.FolderOf("TD-TOOL-FOLDER-R");

        // メニュー「ツール > ファイルツール > フォルダの比較...」のダイアログで 2 つのフォルダを入れて実行する。
        await app.CommandAsync("Command_FolderCompare");
        await app.WaitForAsync("FolderCompareDialog");
        await app.UiaSetValueAsync("FolderCmp_Left", left);
        await app.UiaSetValueAsync("FolderCmp_Right", right);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        JsonObject state = new();
        await app.WaitUntilAsync(async () =>
        {
            state = await Folder(app, "state");
            return !state["running"]!.GetValue<bool>() && state["allRows"] is JsonArray all && all.Count == 4;
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the folder comparison");

        // 既定の表示は 3 行 (同じファイルは隠す)。
        var visible = state["rows"]!.AsArray().Select(r => r!.AsArray().Select(c => c!["text"]!.GetValue<string>()).ToArray()).ToList();
        Assert.Equal(3, visible.Count);
        string[] different = visible.Single(r => r[1] == "sub/diff.bin");
        Assert.Equal(("Different", "0x1234"), (different[0], different[4]));
        Assert.Equal("Left only", visible.Single(r => r[1] == "left-only.bin")[0]);
        Assert.Equal("Right only", visible.Single(r => r[1] == "right-only.bin")[0]);

        // SAME.BIN と same.bin は対応付けられ、絞り込みで「すべて」にすると「同じ」で出る。
        state = await Folder(app, "filter", ("index", 1));
        Assert.Equal(4, state["rows"]!.AsArray().Count);
        JsonObject same = state["allRows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["status"]!.GetValue<string>() == "Same");
        Assert.Equal("SAME.BIN", same["path"]!.GetValue<string>());
        Assert.Equal(0x1234, state["allRows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["status"]!.GetValue<string>() == "Different")["firstDifference"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-20-02")]
    public Task Opening_a_different_row_opens_the_file_comparison() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });
        await Folder(app, "compare", ("left", TestDataCatalog.FolderOf("TD-TOOL-FOLDER-L")), ("right", TestDataCatalog.FolderOf("TD-TOOL-FOLDER-R")));

        await Folder(app, "activate", ("path", "sub/diff.bin"));
        JsonObject compare = new();
        await app.WaitUntilAsync(async () =>
        {
            compare = await app.SendAsync("compareState");
            return compare["count"]!.GetValue<int>() == 1 && (compare["diffCount"]?.GetValue<int>() ?? 0) > 0;
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the file comparison");
        Assert.Equal("diff.bin", compare["left"]!["name"]!.GetValue<string>());
        Assert.Equal("diff.bin", compare["right"]!["name"]!.GetValue<string>());
        Assert.Equal(0x1234, compare["diffs"]![0]!["leftOffset"]!.GetValue<long>());
    });
}
