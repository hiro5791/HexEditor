using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>インポート / エクスポート (TOOL-04〜07)、範囲の切り出しと保存 (TOOL-16)、ワークスペース (UI-33)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TransferTests
{
    private static byte[] RandomHead(int length)
    {
        byte[] data = new byte[length];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, data);
        return data;
    }

    private static async Task OpenImportAsync(AppSession app)
    {
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "file.import" });
        await app.WaitForDialogAsync("ImportDialog");
        string last = string.Empty;
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await TransferAsync(app, []);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                last = ex.Message;
                return false;
            }
        }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the import dialog " + last);
    }

    private static async Task<long> LengthAsync(AppSession app) => (await app.SendAsync("formatDoc"))["length"]!.GetValue<long>();

    private static Task<JsonObject> TransferAsync(AppSession app, JsonObject args) => app.SendAsync("transfer", args);

    private static async Task<int> TabsAsync(AppSession app) => (await app.SendAsync("formatDoc"))["tabs"]!.GetValue<int>();

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-04-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Import_detects_the_format_from_the_content() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        foreach ((string id, string format) in new[]
        {
            ("TD-IHEX", "ihex"), ("TD-SREC", "srec"), ("TD-BASE64", "base64"), ("TD-TOOL-HEXTEXT", "hextext"),
        })
        {
            string path = ctx.CopyTestData(id, id + ".txt");
            await OpenImportAsync(app);
            JsonObject state = await TransferAsync(app, new JsonObject { ["path"] = path });
            await app.WaitUntilAsync(async () => (state = await TransferAsync(app, [])) ["summary"]!.GetValue<string>().Length > 0,
                UiTest.Scaled(TimeSpan.FromSeconds(10)), "the preview");
            Assert.Equal(format, state["format"]!.GetValue<string>());
            string summary = state["summary"]!.GetValue<string>();
            Assert.Contains("Size after conversion", summary);
            Assert.Contains("Warnings: 0, errors: 0", summary);
            if (id is "TD-IHEX" or "TD-SREC")
            {
                Assert.Contains("Address range: 0x08000000", summary);
            }

            if (id == "TD-BASE64")
            {
                Assert.StartsWith(Convert.ToHexString(RandomHead(16)), state["preview"]!.GetValue<string>().Replace(" ", string.Empty));
            }

            await TransferAsync(app, new JsonObject { ["close"] = true });
            await app.IdleAsync();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-04-03")]
    public Task Import_inserted_at_the_cursor_undoes_in_one_step() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string b64 = ctx.CopyTestData("TD-BASE64", "data.b64");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x100);
        await OpenImportAsync(app);
        await TransferAsync(app, new JsonObject { ["path"] = b64, ["target"] = 1 });
        await app.InvokeDialogButtonAsync("Import");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["length"]!.GetValue<long>() == 1_048_576 + 1024,
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the import");
        Assert.Equal(RandomHead(16), await app.BytesAsync(0x100, 16));
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(hash, (await app.SendAsync("formatDoc", new JsonObject { ["hash"] = true }))["sha256"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-04-04")]
    public Task Cancelled_export_leaves_no_file() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-RANDOM-16M");
        string folder = Path.Combine(ctx.Root, "out");
        Directory.CreateDirectory(folder);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [path],
            Hooks = new JsonObject { ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-RANDOM-16M.bin", ["delayMs"] = 500 }) },
        });
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "file.export" });
        await app.WaitForDialogAsync("ExportDialog");
        await TransferAsync(app, new JsonObject { ["format"] = "ihex", ["target"] = 0, ["path"] = Path.Combine(folder, "out.hex") });
        await app.InvokeDialogButtonAsync("Export", idle: false);
        await app.WaitUntilAsync(async () =>
        {
            JsonArray ops = (await app.StateAsync())["operations"]!.AsArray();
            return ops.Select(o => o!.AsObject()).Any(o => o["state"]!.GetValue<string>() == "Running"
                && o["total"]?.GetValue<long>() is long total && total > 0 && o["processed"]!.GetValue<long>() > total / 5);
        }, UiTest.Scaled(TimeSpan.FromMinutes(2)), "20% progress");
        await app.UiaInvokeAsync("Status_Operations");
        await app.UiaInvokeAsync("Operations_Cancel");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromMinutes(1)),
            "the cancel");
        Assert.Empty(Directory.GetFiles(folder));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-05-01")]
    public Task Checksum_errors_ask_before_importing() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-TOOL-IHEX-BADSUM", "bad.hex");
        AppSession app = await ctx.StartAsync();
        int tabs = await TabsAsync(app);
        await OpenImportAsync(app);
        await TransferAsync(app, new JsonObject { ["path"] = path, ["format"] = "ihex" });
        await app.InvokeDialogButtonAsync("Import");
        var dialog = await app.WaitForDialogAsync("ImportErrorsDialog");
        string text = await app.WaitForDialogTextAsync(dialog, "Line 5", "Checksum mismatch", "expected");
        Assert.Contains("1 error was found.", text);
        Assert.NotNull(app.Button("Ignore errors and import"));
        Assert.NotNull(app.Button("Cancel"));
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.Equal(tabs, await TabsAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-06-02")]
    public Task Record_count_mismatch_is_a_warning() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-TOOL-SREC-BADCOUNT", "bad.s19");
        AppSession app = await ctx.StartAsync();
        await OpenImportAsync(app);
        JsonObject state = await TransferAsync(app, new JsonObject { ["path"] = path, ["format"] = "srec" });
        await app.WaitUntilAsync(async () => (state = await TransferAsync(app, []))["summary"]!.GetValue<string>().Length > 0,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the preview");
        Assert.Contains("Warnings: 1, errors: 0", state["summary"]!.GetValue<string>());

        // 警告だけの場合も、プレビューの下の一覧に行・列・内容が出る (TOOL-04 の「エラー」)。
        string warning = Assert.Single(state["issues"]!.AsArray())!.GetValue<string>();
        Assert.Contains("Record count (S5/S6) doesn't match", warning);
        Assert.StartsWith("Warning:", warning);
        await app.InvokeDialogButtonAsync("Import");
        await app.WaitUntilAsync(async () => await LengthAsync(app) == 4096, UiTest.Scaled(TimeSpan.FromSeconds(30)),
            "the new document");
        Assert.Equal(RandomHead(4096), await app.BytesAsync(0, 4096));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-07-03")]
    public Task Pem_certificate_imports_as_base64() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-TOOL-PEM", "cert.pem");
        AppSession app = await ctx.StartAsync();
        await OpenImportAsync(app);
        JsonObject state = await TransferAsync(app, new JsonObject { ["path"] = path });
        await app.WaitUntilAsync(async () => (state = await TransferAsync(app, []))["summary"]!.GetValue<string>().Length > 0,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the preview");
        Assert.Equal("base64", state["format"]!.GetValue<string>());
        Assert.StartsWith("30 82", state["preview"]!.GetValue<string>());
        Assert.Contains("Warnings: 0, errors: 0", state["summary"]!.GetValue<string>());
        await app.InvokeDialogButtonAsync("Import");
        byte[] der = TestDataCatalog.PemDer();
        await app.WaitUntilAsync(async () => await LengthAsync(app) == der.Length, UiTest.Scaled(TimeSpan.FromSeconds(30)),
            "the new document");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(der)),
            (await app.SendAsync("formatDoc", new JsonObject { ["hash"] = true }))["sha256"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-16-01")]
    public Task Saved_selection_matches_the_selection() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-RANDOM-16M");
        string output = Path.Combine(ctx.Root, "out");
        Directory.CreateDirectory(output);
        string single = Path.Combine(output, "sel.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["savePicker"] = single } });
        static byte[] Expected(long offset, int length)
        {
            byte[] bytes = new byte[length];
            TestDataCatalog.Random(TestDataCatalog.RandomSeed, offset, bytes);
            return bytes;
        }

        // 1〜2. 0x12345 から長さ 0x10000 を選択し、右クリックメニューの「選択範囲をファイルに保存...」で sel.bin に保存する。
        await app.SelectAsync(0x12345, 0x10000);
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(await app.RenderAsync(), 0x12345));
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("HexViewMenu_file.saveSelection")).IsEnabled, UiTest.Scaled(TimeSpan.FromSeconds(5)),
            "the menu item");
        await app.UiaInvokeAsync("HexViewMenu_file.saveSelection");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(single)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "sel.bin");
        await app.IdleAsync();
        Assert.Equal(Expected(0x12345, 0x10000), File.ReadAllBytes(single));

        // 3〜4. 0x200000〜0x2000FF を加えてマルチ選択にし、同じメニューで「範囲ごとに別ファイル」、形式 {start}.bin で保存する。
        await app.SendAsync("multiSelection", new JsonObject { ["ranges"] = new JsonArray(new JsonArray(0x12345, 0x10000), new JsonArray(0x200000, 0x100)) });
        await app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["savePicker"] = Path.Combine(output, "ignored.bin") } });
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(await app.RenderAsync(), 0x12345));
        await app.UiaInvokeAsync("HexViewMenu_file.saveSelection");
        await app.WaitForDialogAsync("SaveRangeDialog");
        JsonObject dialog = await app.SendAsync("saveRange", new JsonObject { ["mode"] = 0, ["pattern"] = "{start}.bin" });
        Assert.True(dialog["modeShown"]!.GetValue<bool>());
        Assert.True(dialog["canRun"]!.GetValue<bool>());
        await app.InvokeDialogButtonAsync("Continue");

        // 5. 2 つのファイル (名前に各範囲の開始オフセット) の内容がそれぞれの範囲と一致する。
        string first = Path.Combine(output, "12345.bin");
        string second = Path.Combine(output, "200000.bin");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(first) && File.Exists(second)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the files per range");
        await app.IdleAsync();
        Assert.Equal(Expected(0x12345, 0x10000), File.ReadAllBytes(first));
        Assert.Equal(Expected(0x200000, 0x100), File.ReadAllBytes(second));
        Assert.False(File.Exists(Path.Combine(output, "ignored.bin")));

        // 同じ名前のファイルがあれば、範囲ごとの保存でも上書きを確かめる (TOOL-16 の仕様 2)。キャンセルすれば書かない。
        File.WriteAllBytes(second, [1, 2, 3]);
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "file.saveSelection" });
        await app.WaitForDialogAsync("SaveRangeDialog");
        await app.SendAsync("saveRange", new JsonObject { ["mode"] = 0, ["pattern"] = "{start}.bin" });
        await app.InvokeDialogButtonAsync("Continue");
        var confirm = await app.WaitForDialogAsync("ExportOverwriteDialog");
        await app.WaitForDialogTextAsync(confirm, "200000.bin");
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(second));
    });

    /// <summary>TOOL-16 の仕様 1: 選択がなくても、オフセットの範囲 (入力式) を指定して保存できる。</summary>
    [Fact]
    public Task Offset_range_can_be_saved_without_a_selection() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-RANDOM-16M");
        string output = Path.Combine(ctx.Root, "range.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["savePicker"] = output } });
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "file.saveSelection" });
        await app.WaitForDialogAsync("SaveRangeDialog");
        JsonObject dialog = await app.SendAsync("saveRange", new JsonObject { ["target"] = "range", ["start"] = "0x1000", ["length"] = "0x20" });
        Assert.False(dialog["modeShown"]!.GetValue<bool>());
        Assert.True(dialog["canRun"]!.GetValue<bool>());
        await app.InvokeDialogButtonAsync("Continue");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(output)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "range.bin");
        await app.IdleAsync();
        byte[] expected = new byte[0x20];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0x1000, expected);
        Assert.Equal(expected, File.ReadAllBytes(output));
    });

    /// <summary>TOOL-04 の「エラー」・TOOL-09 の仕様 4: 配列のインポートで、推定した要素のサイズと、解釈できない部分すべての位置が見える。</summary>
    [Fact]
    public Task Array_import_shows_the_inferred_size_and_every_error() => UiTestContext.RunAsync(async ctx =>
    {
        string path = Path.Combine(ctx.Root, "data.c");
        File.WriteAllText(path, "uint16_t a[] = {\r\n  0x0102, @,\r\n  0x0304, 0xG1\r\n};\r\n");
        AppSession app = await ctx.StartAsync();
        await OpenImportAsync(app);
        await TransferAsync(app, new JsonObject { ["path"] = path });
        JsonObject state = await TransferAsync(app, new JsonObject { ["format"] = "c" });
        await app.WaitUntilAsync(async () => (state = await TransferAsync(app, []))["summary"]!.GetValue<string>().Contains("Bytes per element"),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the preview");
        Assert.Contains("Bytes per element: 2", state["summary"]!.GetValue<string>());
        Assert.Equal(2, state["issues"]!.AsArray().Count);

        // 要素のサイズを変えると、プレビューが作り直される。
        state = await TransferAsync(app, new JsonObject { ["fields"] = new JsonObject { ["elementSize"] = "1" } });
        Assert.Contains("Bytes per element: 1", state["summary"]!.GetValue<string>());
        await TransferAsync(app, new JsonObject { ["close"] = true });
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-33-01")]
    public Task Workspace_survives_moving_the_folder() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: proj に file01〜05.bin を置き、5 つを開いてデータインスペクタを下パネル、ブックマークを右パネルに置く。
        string folder = Path.Combine(ctx.Root, "ws1");
        Directory.CreateDirectory(folder);
        string[] files = [.. Enumerable.Range(1, 5).Select(i =>
        {
            string f = Path.Combine(folder, $"file{i:D2}.bin");
            File.WriteAllBytes(f, Enumerable.Repeat((byte)i, 4096).ToArray());
            return f;
        })];
        string workspace = Path.Combine(folder, "test.hexworkspace");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = files, Hooks = new JsonObject { ["savePicker"] = workspace } });
        foreach ((string id, string dock) in new[] { ("inspector", "bottom"), ("bookmarks", "right") })
        {
            await app.SendAsync("panelShow", new JsonObject { ["id"] = id });
            await app.SendAsync("panelMove", new JsonObject { ["id"] = id, ["dock"] = dock });
        }

        JsonObject saved = (await app.SendAsync("panels"))["panels"]!.AsObject();
        Assert.Equal("bottom", saved["inspector"]!["location"]!.GetValue<string>());
        Assert.Equal("right", saved["bookmarks"]!["location"]!.GetValue<string>());

        // 1. ワークスペースを保存する。
        await app.SendAsync("execute", new JsonObject { ["id"] = "workspace.save" });
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(workspace)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the workspace file");
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        // 2〜3. 別のプロファイル (既定のパネル配置) で、フォルダを丸ごと移してから開く。
        string moved = Path.Combine(ctx.Root, "moved", "ws2");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(folder, moved);
        AppSession again = await ctx.StartAsync(new AppOptions
        {
            Profile = ctx.NewProfile(),
            Hooks = new JsonObject { ["openPicker"] = new JsonArray(Path.Combine(moved, "test.hexworkspace")) },
        });
        Assert.NotEqual("bottom", (await again.SendAsync("panels"))["panels"]!["inspector"]!["location"]?.GetValue<string>());

        // 4. 既定の「新しいウィンドウ」で開く。
        await again.SendAsync("startCommand", new JsonObject { ["id"] = "workspace.open" });
        await again.WaitForDialogAsync("WorkspaceOpenDialog");
        await again.InvokeDialogButtonAsync("In a new window");

        // 5. 新しいウィンドウのタブとパスとパネルの配置。
        JsonObject Window(JsonObject args)
        {
            args["window"] = 1;
            return args;
        }

        await again.WaitUntilAsync(async () =>
        {
            try
            {
                return (await again.SendAsync("formatDoc", Window([])))["tabs"]!.GetValue<int>() == 5;
            }
            catch (InvalidOperationException)
            {
                return false; // 新しいウィンドウがまだない
            }
        }, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the 5 tabs in the new window");
        var paths = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            JsonObject doc = await again.SendAsync("formatDoc", Window(new JsonObject { ["index"] = i }));
            paths.Add((doc["path"] ?? doc["pendingPath"])!.GetValue<string>()); // 選ばれていないタブは遅延して開く (UI-31)
        }

        Assert.Equal(Enumerable.Range(1, 5).Select(i => Path.Combine(moved, $"file{i:D2}.bin")), paths);
        JsonObject panels = (await again.SendAsync("panels", Window([])))["panels"]!.AsObject();
        Assert.Equal("bottom", panels["inspector"]!["location"]!.GetValue<string>());
        Assert.Equal("right", panels["bookmarks"]!["location"]!.GetValue<string>());
    });
}
