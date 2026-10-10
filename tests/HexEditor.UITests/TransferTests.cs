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
        Assert.Contains("Record count (S5/S6) doesn't match", state["summary"]!.GetValue<string>());
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
        string output = Path.Combine(ctx.Root, "part.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["savePicker"] = output } });
        await app.SelectAsync(0x12345, 0x100000);
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.saveSelection" });
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(output)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "part.bin");
        byte[] expected = new byte[0x100000];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0x12345, expected);
        Assert.Equal(expected, File.ReadAllBytes(output));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-33-01")]
    public Task Workspace_survives_moving_the_folder() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "proj");
        Directory.CreateDirectory(folder);
        string[] files = [.. Enumerable.Range(1, 5).Select(i =>
        {
            string f = Path.Combine(folder, $"file{i:D2}.bin");
            File.WriteAllBytes(f, Enumerable.Repeat((byte)i, 4096).ToArray());
            return f;
        })];
        string workspace = Path.Combine(folder, "w.hexworkspace");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = files, Hooks = new JsonObject { ["savePicker"] = workspace } });
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "hash" }); // パネルの配置を既定から変える
        await app.SendAsync("execute", new JsonObject { ["id"] = "workspace.save" });
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(workspace)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the workspace file");
        Assert.True((await app.SendAsync("panels"))["panels"]!["hash"]!["shown"]!.GetValue<bool>());
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        string moved = Path.Combine(ctx.Root, "moved");
        Directory.Move(folder, moved);
        AppSession again = await ctx.StartAsync(new AppOptions
        {
            Profile = ctx.NewProfile(),
            Hooks = new JsonObject { ["openPicker"] = new JsonArray(Path.Combine(moved, "w.hexworkspace")) },
        });
        await again.SendAsync("startCommand", new JsonObject { ["id"] = "workspace.open" });
        await again.WaitForDialogAsync("WorkspaceOpenDialog");
        await again.InvokeDialogButtonAsync("In this window");
        await again.WaitUntilAsync(async () => (await again.TabNamesAsync()).Count(n => n.StartsWith("file", StringComparison.Ordinal)) == 5,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the 5 tabs");
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"file{i:D2}.bin"), (await again.TabNamesAsync()).Where(n => n.StartsWith("file", StringComparison.Ordinal)));
        Assert.True((await again.SendAsync("panels"))["panels"]!["hash"]!["shown"]!.GetValue<bool>());
    });
}
