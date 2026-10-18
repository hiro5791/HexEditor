using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// 分岐する Undo 履歴 (EDIT-21)、パッチの作成・適用 (EDIT-42・EDIT-43)、変換ビュー (EDIT-45)。木・ダイアログの中身はテスト用の命令
/// (historyPanel / historyDialog / patchDialog / transformView) で読み、欄は UI オートメーションの値で入力し、ボタンは dialogButton で押す。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class EditingPhase4Tests
{
    // ---- 共通 ----

    private static async Task TypeAtAsync(AppSession app, long offset, string text)
    {
        await app.GoToAsync(offset);
        await app.TypeAsync(text);
        await app.IdleAsync();
    }

    /// <summary>コマンドパレットからコマンドを実行する (ダイアログを開くコマンドは完了を待たない)。</summary>
    private static async Task PaletteAsync(AppSession app, string name)
    {
        await app.SendAsync("palette", new JsonObject { ["text"] = ">" + name });
        Assert.True((await app.SendAsync("paletteEnter", new JsonObject { ["detach"] = true }))["invoked"]!.GetValue<bool>());
        await app.IdleAsync();
    }

    private static async Task<JsonArray> TreeAsync(AppSession app) => (await app.SendAsync("historyPanel"))["tree"]!.AsArray();

    private static JsonObject Node(JsonArray tree, int id) => tree.Select(n => n!.AsObject()).Single(n => n["id"]!.GetValue<int>() == id);

    private static async Task<string> ValuesAsync(AppSession app) =>
        string.Join(" ", await Task.WhenAll(new long[] { 0x10, 0x20, 0x30 }.Select(async o => Convert.ToHexString(await app.BytesAsync(o, 1)))));

    private static async Task<AppSession> StartBranchingAsync(UiTestContext ctx, string? file = null, string? profile = null)
    {
        profile ??= ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["edit.undo.branching"] = true });
        return await ctx.StartAsync(new AppOptions { Files = [file ?? ctx.CopyTestData("TD-SEQ-1M")], Profile = profile });
    }

    /// <summary>TC-EDIT-21-01 の手順 1〜4: A (0x10)、B (0x20) を入力し、B を元に戻して C (0x30) を入力する。節の番号は A、B、C の順。</summary>
    private static async Task<(int A, int B, int C)> EditAbcAsync(AppSession app)
    {
        await TypeAtAsync(app, 0x10, "AA");
        await TypeAtAsync(app, 0x20, "BB");
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await TypeAtAsync(app, 0x30, "CC");
        JsonArray tree = await TreeAsync(app);
        int[] ids = [.. tree.Select(n => n!["id"]!.GetValue<int>()).Where(id => id != 0).Order()];
        return (ids[0], ids[1], ids[2]);
    }

    /// <summary>閉じるときの確認 (処理の数え方の外のダイアログ) のボタンを、ボタンが表示されたら押す。</summary>
    private static Task PressWhenReadyAsync(AppSession app, string button) => app.WaitUntilAsync(async () =>
    {
        try
        {
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = button });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }, UiTest.Scaled(TimeSpan.FromSeconds(10)), button);

    private static async Task WaitDialogAsync(AppSession app, string command, Func<JsonObject, bool> ready, string what)
    {
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => ready(state = await app.SendAsync(command)), UiTest.Scaled(TimeSpan.FromSeconds(30)), what);
    }

    // ---- EDIT-21 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-21-01")]
    public Task Undo_then_edit_makes_a_branch_and_nodes_can_be_visited() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartBranchingAsync(ctx);
        (int a, int b, int c) = await EditAbcAsync(app);

        // 5. 木で表示: 根の下に A、A の下に B と C。現在の節は C。
        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.history"));
        await app.SendAsync("historyTreeView", new JsonObject { ["on"] = true });
        await app.WaitForAsync("History_Tree");
        JsonObject panel = await app.SendAsync("historyPanel");
        Assert.True(panel["treeView"]!.GetValue<bool>());
        Assert.Equal(4, panel["treeRows"]!.GetValue<int>());
        JsonArray tree = panel["tree"]!.AsArray();
        Assert.Equal(0, Node(tree, a)["parent"]!.GetValue<int>());
        Assert.Equal(a, Node(tree, b)["parent"]!.GetValue<int>());
        Assert.Equal(a, Node(tree, c)["parent"]!.GetValue<int>());
        Assert.True(Node(tree, c)["current"]!.GetValue<bool>());
        Assert.NotEqual(Node(tree, b)["lane"]!.GetValue<int>(), Node(tree, c)["lane"]!.GetValue<int>());

        // 6〜8. B の節、C の節に移る。
        await app.SendAsync("historyGoToNode", new JsonObject { ["id"] = b });
        await app.IdleAsync();
        Assert.Equal("AA BB 30", await ValuesAsync(app));
        await app.SendAsync("historyGoToNode", new JsonObject { ["id"] = c });
        await app.IdleAsync();
        Assert.Equal("AA 20 CC", await ValuesAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-21-02")]
    public Task Named_node_is_reachable_from_the_command_palette() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartBranchingAsync(ctx);
        (_, int b, _) = await EditAbcAsync(app);

        // 1. B の節に名前 before-C を付ける (名前を付けるダイアログ)。
        await app.SendAsync("historyNameNode", new JsonObject { ["id"] = b });
        await app.WaitForAsync("HistoryNameDialog");
        await app.UiaSetValueAsync("HistoryName_Name", "before-C");
        await EditCommandTests.PressAsync(app);
        Assert.Equal("before-C", Node(await TreeAsync(app), b)["name"]!.GetValue<string>());

        // 2〜3. コマンドパレット「履歴の節へ移動」: 候補に before-C があり、選ぶと移る。
        await PaletteAsync(app, "Go to history node");
        await app.WaitForAsync("HistoryGoToDialog");
        JsonObject dialog = await app.SendAsync("historyDialog");
        Assert.Contains("before-C", dialog["candidates"]!.AsArray().Select(n => n!.GetValue<string>()));
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "HistoryGoTo_List", ["index"] = 0 });
        await EditCommandTests.PressAsync(app);

        // 4. 0x20 は BB、0x30 は 30。現在の節は B。
        Assert.Equal(0xBB, (await app.BytesAsync(0x20, 1))[0]);
        Assert.Equal(0x30, (await app.BytesAsync(0x30, 1))[0]);
        Assert.True(Node(await TreeAsync(app), b)["current"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-21-03")]
    [Trait(UiTest.TC, "TC-EDIT-21-04")]
    public Task Saved_history_is_loaded_after_restart_and_a_different_file_warns() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string file = ctx.CopyTestData("TD-SEQ-1M");
        string hist = Path.Combine(ctx.Root, "t.hexhist");
        AppSession app = await StartBranchingAsync(ctx, file, profile);
        (int a, int b, int c) = await EditAbcAsync(app);
        await app.SendAsync("historyNameNode", new JsonObject { ["id"] = b });
        await app.WaitForAsync("HistoryNameDialog");
        await app.UiaSetValueAsync("HistoryName_Name", "before-C");
        await EditCommandTests.PressAsync(app);

        // 1〜2. 「履歴を保存」で t.hexhist に保存する。
        await PaletteAsync(app, "Save history");
        await app.WaitForAsync("HistorySaveDialog");
        await app.UiaSetValueAsync("HistorySave_Path", hist);
        await EditCommandTests.PressAsync(app);
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(hist)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the history file");

        // 3. タブを閉じる (名前付きの節があるので確認が出る。「保存しないで閉じる」)。アプリを終了する。
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForAsync("CloseDialog");
        await PressWhenReadyAsync(app, "SecondaryButton");
        await app.WaitForAsync("HistoryCloseDialog");
        await PressWhenReadyAsync(app, "SecondaryButton");
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the tab to close");
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));

        // 4〜5. 同じ設定フォルダで起動し、同じファイルを開いて読み込む。警告は出ない。
        app = await ctx.StartAsync(new AppOptions { Files = [file], Profile = profile, Hooks = new JsonObject { ["openPicker"] = new JsonArray(hist) } });
        await app.IdleAsync();
        await PaletteAsync(app, "Load history");
        await app.WaitUntilAsync(async () => (await TreeAsync(app)).Count == 4, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the loaded tree");
        Assert.False((await app.SendAsync("historyDialog"))["open"]!.GetValue<bool>());

        // 6. 同じ構造で、現在の節は C、B に before-C。
        JsonArray tree = await TreeAsync(app);
        Assert.Equal(a, Node(tree, b)["parent"]!.GetValue<int>());
        Assert.Equal(a, Node(tree, c)["parent"]!.GetValue<int>());
        Assert.True(Node(tree, c)["current"]!.GetValue<bool>());
        Assert.Equal("before-C", Node(tree, b)["name"]!.GetValue<string>());

        // 7. 根、A、B、C に順に移る。
        foreach ((int id, string expected) in new[] { (0, "10 20 30"), (a, "AA 20 30"), (b, "AA BB 30"), (c, "AA 20 CC") })
        {
            await app.SendAsync("historyGoToNode", new JsonObject { ["id"] = id });
            await app.IdleAsync();
            Assert.Equal(expected, await ValuesAsync(app));
        }

        // TC-EDIT-21-04: 別のファイル (TD-ZERO-1M) を開いた状態で読み込むと警告が出る。キャンセルすると履歴とデータは変わらない。
        await app.OpenAsync(ctx.CopyTestData("TD-ZERO-1M"));
        await app.IdleAsync();
        await PaletteAsync(app, "Load history");
        await app.WaitForAsync("HistoryLoadMismatchDialog");
        JsonObject warning = await app.SendAsync("historyDialog");
        Assert.Equal("mismatch", warning["kind"]!.GetValue<string>());
        Assert.Contains("may have changed", warning["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Load", warning["primary"]!.GetValue<string>());
        Assert.Equal("Cancel", warning["close"]!.GetValue<string>());
        await EditCommandTests.PressAsync(app, "CloseButton");
        Assert.Single(await TreeAsync(app));
        Assert.Equal(new byte[16], await app.BytesAsync(0, 16));
    }, TimeSpan.FromMinutes(4));

    // ---- EDIT-42・EDIT-43 ----

    private static async Task<JsonObject> OpenCreateDialogAsync(AppSession app)
    {
        await app.CommandAsync("Command_PatchCreate");
        await app.WaitForAsync("PatchCreateDialog");
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => !(state = await app.SendAsync("patchDialog"))["computing"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(60)), "the estimates");
        return state;
    }

    private static JsonObject Format(JsonObject state, string id) => state["formats"]!.AsArray().Select(f => f!.AsObject()).Single(f => f["id"]!.GetValue<string>() == id);

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-42-03")]
    public Task Ips_is_disabled_for_changes_at_sixteen_mebibytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-MARKERS-1G")] });
        await TypeAtAsync(app, 0x1000000, "FF");

        // 3. IPS は無効で、範囲 (16 MiB) を超える旨と使える形式が出る。IPS32 は有効。
        JsonObject state = await OpenCreateDialogAsync(app);
        Assert.False(Format(state, "Ips")["enabled"]!.GetValue<bool>());
        Assert.Contains("16 MiB", Format(state, "Ips")["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("IPS32", Format(state, "Ips")["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(Format(state, "Ips32")["enabled"]!.GetValue<bool>());

        // 4. 「自動」は IPS32 を選ぶ。
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "PatchCreate_Format_Auto", ["value"] = true });
        await app.IdleAsync();
        Assert.Equal("Ips32", (await app.SendAsync("patchDialog"))["effective"]!.GetValue<string>());
        await EditCommandTests.PressAsync(app, "CloseButton");

        // 5. 0x1000000 を戻し、0xFFFFFF を書き換えると IPS は有効。
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await TypeAtAsync(app, 0xFFFFFF, "FF");
        state = await OpenCreateDialogAsync(app);
        Assert.True(Format(state, "Ips")["enabled"]!.GetValue<bool>());
        await EditCommandTests.PressAsync(app, "CloseButton");
    });

    private static async Task<JsonObject> OpenApplyDialogAsync(AppSession app, string patch)
    {
        await app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray(patch) } });
        await app.CommandAsync("Command_PatchApply");
        await app.WaitForAsync("PatchApplyDialog");
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => !(state = await app.SendAsync("patchDialog"))["verifying"]!.GetValue<bool>()
            && state["verification"] is not null, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the verification");
        return state;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-43-02")]
    public Task Bps_for_another_file_shows_a_crc_mismatch_warning() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-EDIT-PATCH-OTHER")] });
        JsonObject state = await OpenApplyDialogAsync(app, ctx.TestData("TD-EDIT-PATCH-BPS"));

        // 2. 「不一致」(文字とアイコン)、別のファイル用の可能性の警告、「適用する」「キャンセル」、既定のボタンはキャンセル。
        Assert.Equal("Mismatch", state["verification"]!.GetValue<string>());
        Assert.Contains("Mismatch", state["verificationText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, state["glyph"]!.GetValue<string>());
        Assert.Contains("different file", state["warning"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Apply anyway", state["primary"]!.GetValue<string>());
        Assert.Equal("Close", state["defaultButton"]!.GetValue<string>());

        // 3. キャンセルすると変更なし。
        await EditCommandTests.PressAsync(app, "CloseButton");
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-43-03")]
    public Task Applied_patch_is_undone_with_one_ctrl_z() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-EDIT-PATCH-SRC");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source] });
        JsonObject state = await OpenApplyDialogAsync(app, ctx.TestData("TD-EDIT-PATCH-BPS"));
        Assert.Equal("Match", state["verification"]!.GetValue<string>());
        Assert.Equal(1_048_676, state["resultLength"]!.GetValue<long>());
        await EditCommandTests.PressAsync(app);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["length"]!.GetValue<long>() == 1_048_676, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the patch");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the check");

        // 2. 編集履歴は 1 件 (開いた時点の行と、パッチの適用の行)。
        Assert.Equal(2, (await app.DocumentAsync())["historyCount"]!.GetValue<int>());

        // 3〜4. Ctrl+Z 1 回で長さ 1,048,576 の元の内容に戻り、「変更なし」。
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(1_048_576, doc["length"]!.GetValue<long>());
        Assert.False(doc["modified"]!.GetValue<bool>());
        byte[] expected = File.ReadAllBytes(ctx.TestData("TD-EDIT-PATCH-SRC"));
        foreach (long at in new long[] { 0, 0x7FFF0, 0x80000, 0xFFFF0 })
        {
            Assert.Equal(expected.AsSpan((int)at, 16).ToArray(), await app.BytesAsync(at, 16));
        }
    });

    // ---- EDIT-45 ----

    private static async Task XorViewAsync(AppSession app, string key)
    {
        await app.CommandAsync("Command_TransformViewXor");
        await app.WaitForAsync("TransformXor_Key");
        await app.UiaSetValueAsync("TransformXor_Key", key);
        await app.IdleAsync();
        await app.CommandAsync("TransformXor_Ok");
        await app.WaitUntilAsync(async () => (await app.SendAsync("transformView"))["active"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the transform view");
        await app.IdleAsync();
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-45-01")]
    public Task Xor_view_does_not_change_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-ZERO-1M");
        string output = Path.Combine(ctx.Root, "out.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["savePicker"] = output } });
        await XorViewAsync(app, "FF");

        // 2. 0 と 0xFFFFF はどちらも FF。
        Assert.Equal(0xFF, (await app.BytesAsync(0, 1))[0]);
        Assert.Equal(0xFF, (await app.BytesAsync(0xFFFFF, 1))[0]);

        // 3. バッジとステータスバーに「変換ビュー: XOR FF」、枠は破線。
        JsonObject state = await app.SendAsync("transformView");
        Assert.Equal("Transform view: XOR FF", state["badge"]!.GetValue<string>());
        Assert.Equal("Transform view: XOR FF", state["status"]!.GetValue<string>());
        Assert.True(state["frame"]!.GetValue<bool>());

        // 4. 変更なし。5〜6. 名前を付けて保存した out.bin と元のファイルはすべて 00。
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        await app.CommandAsync("Command_SaveAs");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(output)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the saved file");
        await app.IdleAsync();
        Assert.All(File.ReadAllBytes(output), b => Assert.Equal(0, b));
        Assert.Equal(1024 * 1024, new FileInfo(output).Length);
        Assert.All(File.ReadAllBytes(source), b => Assert.Equal(0, b));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-45-03")]
    public Task Typing_in_the_view_writes_the_inverse_and_remains_after_clearing() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")] });
        await TypeAtAsync(app, 0x10, "1B1B");
        await XorViewAsync(app, "5A");

        // 2. 0x10 は 41 (A)。
        Assert.Equal(0x41, (await app.BytesAsync(0x10, 1))[0]);
        JsonArray cells = Row(await WaitForContentAsync(app, 0x10), 0x10)!["cells"]!.AsArray();
        Assert.Equal("41", cells[0]!["hex"]!.GetValue<string>());
        Assert.Equal("A", cells[0]!["text"]!.GetValue<string>());

        // 3〜4. テキスト列の 0x11 に B を入力すると 42 (B)。
        await app.GoToAsync(0x11);
        await app.KeyAsync("Tab");
        await app.TypeAsync("B");
        await app.IdleAsync();
        Assert.Equal(0x42, (await app.BytesAsync(0x11, 1))[0]);
        await app.WaitUntilAsync(async () =>
        {
            JsonArray c = Row(await WaitForContentAsync(app, 0x10), 0x10)!["cells"]!.AsArray();
            return c[1]!["hex"]!.GetValue<string>() == "42" && c[1]!["text"]!.GetValue<string>() == "B";
        }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "0x11 to show 42 (B)");

        // 5〜6. 変換ビューを解除すると、0x10 は 1B、0x11 は 18 (0x42 XOR 0x5A)。
        await PaletteAsync(app, "Clear transform view");
        Assert.False((await app.SendAsync("transformView"))["active"]!.GetValue<bool>());
        Assert.Equal(new byte[] { 0x1B, 0x18 }, await app.BytesAsync(0x10, 2));

        // 7. 入力の編集が 1 件 (1B1B の入力の後)。変換ビューの設定・解除は履歴にない。
        JsonArray rows = (await app.SendAsync("historyPanel"))["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Skip(1), r => Assert.Equal("Typing", r!["name"]!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-45-04")]
    public Task Search_in_the_view_looks_at_transformed_data() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")] });
        await TypeAtAsync(app, 0x10, "1B");
        await app.GoToAsync(0);
        await XorViewAsync(app, "5A");

        // 2〜3. 種類「Hex」で 41 を検索すると 0x10 が選ばれ、検索バーに変換ビューの表示が出る。
        await OperationsTests.OpenFindAsync(app, "41");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 1, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the match");
        await AssertSelectionAsync(app, 0x10, 1);
        Assert.Equal("Transform view: XOR 5A", (await app.SendAsync("transformView"))["findBar"]!.GetValue<string>());

        // 4〜6. 解除すると同じ検索で見つからず、検索バーに変換ビューの表示はない。
        await PaletteAsync(app, "Clear transform view");
        await app.GoToAsync(0);
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.ElementAsync("Find_Status"))["text"]!.GetValue<string>().Contains("Not found", StringComparison.Ordinal),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "not found");
        Assert.Equal(string.Empty, (await app.SendAsync("transformView"))["findBar"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-45-05")]
    public Task Apply_this_transform_opens_the_data_operation_with_the_same_key() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")] });
        await XorViewAsync(app, "5A 3C");

        // 1〜2. データ演算のダイアログが「ビット演算 > XOR」、鍵 5A 3C、対象はドキュメント全体で開く。
        await PaletteAsync(app, "Apply this transform to the data");
        await app.WaitForAsync("DataOperationDialog");
        // 分類「ビット演算」(2 番目)、演算は AND・OR・XOR の 3 番目、オペランドは「バイト列の鍵」。
        Assert.Equal(1, (await app.ElementAsync("DataOp_Category"))["selectedIndex"]!.GetValue<int>());
        Assert.Equal(2, (await app.ElementAsync("DataOp_Kind"))["selectedIndex"]!.GetValue<int>());
        Assert.Equal(1, (await app.ElementAsync("DataOp_OperandSource"))["selectedIndex"]!.GetValue<int>());
        Assert.Equal("5A 3C", (await app.ElementAsync("Fill_Pattern"))["text"]!.GetValue<string>());

        // 3〜4. 実行すると変換ビューは解除され、データに 5A 3C 5A 3C が書き込まれる。
        await EditCommandTests.PressAsync(app);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the operation");
        Assert.False((await app.SendAsync("transformView"))["active"]!.GetValue<bool>());
        Assert.Equal(new byte[] { 0x5A, 0x3C, 0x5A, 0x3C }, await app.BytesAsync(0, 4));

        // 対象範囲はドキュメント全体 (末尾まで書き込まれた)。
        Assert.Equal(new byte[] { 0x5A, 0x3C }, await app.BytesAsync(0xFFFFE, 2));
    });
}
