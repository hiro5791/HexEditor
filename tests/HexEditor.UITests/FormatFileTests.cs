using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// エンコード形式のファイルを開く (ENG-38)・元の形式で保存する (TOOL-11)、範囲を指定して開く (ENG-13)、選択範囲を新しいタブで開く (ENG-39)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FormatFileTests
{
    private static async Task<JsonObject> DocAsync(AppSession app, int? index = null, bool hash = false)
    {
        var args = new JsonObject { ["hash"] = hash };
        if (index is int i)
        {
            args["index"] = i;
        }

        return await app.SendAsync("formatDoc", args);
    }

    private static async Task<(byte[] Bytes, string[] States)> StatesAsync(AppSession app, long offset, int length)
    {
        JsonObject r = await app.SendAsync("byteStates", new JsonObject { ["offset"] = offset, ["length"] = length });
        return (Convert.FromHexString(r["hex"]!.GetValue<string>()), [.. r["states"]!.AsArray().Select(s => s!.GetValue<string>())]);
    }

    private static async Task<bool> HasNotificationAsync(AppSession app, string text, string? action = null) =>
        (await app.NotificationsAsync()).Any(n => n["message"]!.GetValue<string>().Contains(text, StringComparison.Ordinal)
            && (action is null || n["actions"]!.AsArray().Any(a => a!.GetValue<string>() == action)));

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-38-01")]
    public Task Intel_hex_and_s_record_are_decoded_with_gaps() => UiTestContext.RunAsync(async ctx =>
    {
        string hex = ctx.CopyTestData("TD-IHEX", "fw.hex");
        string s37 = ctx.CopyTestData("TD-SREC", "fw.s37");
        IReadOnlyList<(long Address, byte[] Data)> segments = TestDataCatalog.FirmwareData();
        long min = segments.Min(s => s.Address);
        long end = segments.Max(s => s.Address + s.Data.Length);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [hex] });
        foreach ((string path, string format) in new[] { (hex, "Intel HEX"), (s37, "S-record") })
        {
            if (path == s37)
            {
                await app.SendAsync("uiOpen", new JsonObject { ["path"] = path });
            }

            // 1〜2. InfoBar とステータスバーの形式。
            await app.WaitUntilAsync(() => HasNotificationAsync(app, "decoded", "Open as text"), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the decoded notice");
            JsonObject doc = await DocAsync(app);
            Assert.Contains(format, doc["modeText"]!.GetValue<string>());

            // 3. ベースアドレス・長さ・値と状態。
            Assert.Equal(min, doc["baseAddress"]!.GetValue<long>());
            Assert.Equal(end - min, doc["length"]!.GetValue<long>());
            (byte[] bytes, string[] states) = await StatesAsync(app, 0, (int)(end - min));
            var expected = Enumerable.Repeat((byte)0xFF, (int)(end - min)).ToArray();
            var data = new bool[expected.Length];
            foreach ((long address, byte[] d) in segments)
            {
                d.CopyTo(expected, address - min);
                data.AsSpan((int)(address - min), d.Length).Fill(true);
            }

            Assert.Equal(expected, bytes);
            for (int i = 0; i < states.Length; i++)
            {
                Assert.Equal(data[i] ? "Valid" : "NoData", states[i]);
            }
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-38-02")]
    public Task Checksum_errors_are_listed_and_the_document_is_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-ENG-IHEX-BADSUM", "bad.hex");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.WaitUntilAsync(async () => (await app.SendAsync("formatIssues"))["rows"]!.AsArray().Count > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the error list");
        JsonObject issues = await app.SendAsync("formatIssues");
        JsonNode row = Assert.Single(issues["rows"]!.AsArray())!;
        Assert.Equal(5, row["line"]!.GetValue<int>());
        Assert.Equal("Checksum", row["kind"]!.GetValue<string>());
        Assert.True(issues["shown"]!.GetValue<bool>());
        Assert.True(await HasNotificationAsync(app, "1 line has errors", "Allow editing"));
        Assert.True((await DocAsync(app))["readOnly"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-11-01")]
    public Task Editing_one_byte_keeps_the_other_lines() => UiTestContext.RunAsync(async ctx =>
    {
        foreach ((string id, string name, long offset) in new[] { ("TD-IHEX", "fw.hex", 2L), ("TD-SREC", "fw.s37", 2L), ("TD-BASE64", "data.b64", 0L) })
        {
            string path = ctx.CopyTestData(id, name);
            string[] before = File.ReadAllLines(path);
            AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [path] });
            byte old = (await app.BytesAsync(offset, 1))[0];
            await app.GoToAsync(offset);
            await app.TypeAsync(old == 0x5A ? "A5" : "5A");
            await app.KeyAsync("S", ctrl: true);
            await app.WaitUntilAsync(async () => !(await DocAsync(app))["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the save");
            string[] after = File.ReadAllLines(path);
            Assert.Equal(before.Length, after.Length);
            int changed = Assert.Single(Enumerable.Range(0, before.Length), i => before[i] != after[i]);
            Assert.Equal(before[changed].Length, after[changed].Length);
            if (id == "TD-BASE64")
            {
                Assert.Equal(0, changed);
            }

            Assert.Equal(File.ReadAllBytes(TestDataCatalog.Get(id)).Contains((byte)'\r'), File.ReadAllBytes(path).Contains((byte)'\r'));
            await app.DisposeAsync();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-11-02")]
    public Task Save_as_binary() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-IHEX", "fw.hex");
        string bin = Path.Combine(ctx.Root, "fw.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["savePicker"] = bin } });
        JsonObject decoded = await DocAsync(app, hash: true);
        await app.KeyAsync("S", ctrl: true, shift: true);
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(bin)), UiTest.Scaled(TimeSpan.FromSeconds(30)), "fw.bin");
        await app.IdleAsync();

        // 1. 選択肢 (元の形式・バイナリ・他の形式)。
        string choices = (await app.WaitForLogAsync(l => l.Contains("Save as choices:", StringComparison.Ordinal), "the choices"));
        Assert.Contains("Intel HEX", choices);
        Assert.Contains("Binary", choices);
        Assert.Contains("S-record", choices);

        // 3. 内容はデコードした結果と一致し、fw.hex は変わらない。4. タブはバイナリのドキュメント。
        Assert.Equal(decoded["sha256"]!.GetValue<string>(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bin))));
        Assert.Equal(original, File.ReadAllBytes(path));
        JsonObject doc = await DocAsync(app);
        Assert.Equal("fw.bin", doc["name"]!.GetValue<string>());
        Assert.Equal(string.Empty, doc["formatText"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-11-03")]
    public Task Comment_lines_are_confirmed_on_the_first_save() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-TOOL-IHEX-COMMENT", "c.hex");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 1. コメント行が形式の誤りとして出て、「編集を許可」を押す。
        await app.WaitUntilAsync(async () => (await app.SendAsync("formatIssues"))["rows"]!.AsArray().Count > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the error list");
        JsonNode row = (await app.SendAsync("formatIssues"))["rows"]!.AsArray()[0]!;
        Assert.Equal((1, "Syntax"), (row["line"]!.GetValue<int>(), row["kind"]!.GetValue<string>()));
        Assert.True((await app.SendAsync("notificationAction", new JsonObject { ["label"] = "Allow editing" }))["invoked"]!.GetValue<bool>());

        // 2〜3. 編集して保存すると、失われる旨の確認が出る。
        await app.GoToAsync(0);
        await app.TypeAsync("00");
        await app.KeyAsync("S", ctrl: true);
        var dialog = await app.WaitForDialogAsync("EncodedLossDialog");
        await app.WaitForDialogTextAsync(dialog, "won't be written");
        Assert.NotNull(app.Button("Save"));
        Assert.NotNull(app.Button("Save As"));
        Assert.NotNull(app.Button("Cancel"));
        await app.InvokeDialogButtonAsync("Save");
        await app.WaitUntilAsync(async () => !(await DocAsync(app))["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the save");

        // 4. 2 回目は確認しない。5. コメント行がなく、データが編集を反映している。
        await app.GoToAsync(1);
        await app.TypeAsync("11");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => !(await DocAsync(app))["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the second save");
        string[] lines = File.ReadAllLines(path);
        Assert.DoesNotContain(lines, l => l.StartsWith(';'));
        Assert.Contains(lines, l => l.StartsWith(":1000000000", StringComparison.Ordinal) && l[11..13] == "11");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-13-01")]
    public Task Range_open_shows_the_file_addresses() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.TestData("TD-ENG-SPARSE-10G");
        AppSession app = await ctx.StartAsync();

        // 1〜2. 範囲を指定して開く: 開始 5G、長さ 1M と解釈結果。
        await app.SendAsync("startCommand", new JsonObject { ["id"] = "file.openAdvanced" });
        await app.WaitForDialogAsync("OpenAdvancedDialog");
        JsonObject state = await app.SendAsync("openAdvancedSet", new JsonObject { ["path"] = path, ["range"] = true, ["start"] = "5G", ["length"] = "1M" });
        Assert.Contains("5,368,709,120 (0x140000000)", state["startResult"]!.GetValue<string>());
        Assert.True(state["canOpen"]!.GetValue<bool>());

        // 5. 開始がファイルの長さ、長さ 0 は開けない。
        state = await app.SendAsync("openAdvancedSet", new JsonObject { ["start"] = "10G" });
        Assert.False(state["canOpen"]!.GetValue<bool>());
        Assert.True(state["startInvalid"]!.GetValue<bool>());
        state = await app.SendAsync("openAdvancedSet", new JsonObject { ["start"] = "5G", ["length"] = "0" });
        Assert.False(state["canOpen"]!.GetValue<bool>());
        Assert.True(state["lengthInvalid"]!.GetValue<bool>());
        state = await app.SendAsync("openAdvancedSet", new JsonObject { ["length"] = "1M" });

        // 3〜4. 開くと、アドレスは開始位置から始まる。
        await app.InvokeDialogButtonAsync("Open");
        await app.WaitUntilAsync(async () => (await DocAsync(app))["isRange"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the range tab");
        JsonObject doc = await DocAsync(app);
        Assert.Equal(0x140000000L, doc["baseAddress"]!.GetValue<long>());
        Assert.Equal(1_048_576L, doc["length"]!.GetValue<long>());
        Assert.Contains("[0x140000000–0x1400FFFFF]", doc["title"]!.GetValue<string>());
        Assert.Equal(TestDataCatalog.Marker(0x140000000), await app.BytesAsync(0, 17));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-39-02")]
    public Task Linked_view_edits_go_to_the_parent() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.SelectAsync(0x1000, 0x1000);
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.openSelectionInNewTab" });
        JsonObject child = await DocAsync(app);
        Assert.True(child["linked"]!.GetValue<bool>());
        Assert.Equal(0x1000L, child["baseAddress"]!.GetValue<long>());

        // 1〜2. 子の 0x10 を AA にすると、親の 0x1010 が変わる。
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        Assert.Equal(0xAA, (await app.BytesAsync(0x1010, 1))[0]);

        // 3. 親で Ctrl+Z を押すと、子も戻る。
        await app.KeyAsync("Z", ctrl: true);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);

        // 4〜5. 親で範囲より前に 100 バイト挿入しても、子の内容は変わらない (範囲が追従する)。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.GoToAsync(0x100);
        await app.KeyAsync("Insert");
        await app.TypeAsync(string.Concat(Enumerable.Repeat("EE", 100)));
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        byte[] expected = new byte[0x1000];
        TestDataCatalog.Sequence(0x1000, expected);
        Assert.Equal(expected, await app.BytesAsync(0, 0x1000));
        JsonObject moved = await DocAsync(app);
        Assert.Equal(0x1064L, moved["linkStart"]!.GetValue<long>());
        Assert.Contains("[0x1064–0x2063]", moved["title"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-39-03")]
    public Task Copy_in_a_new_tab_is_independent() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        string parentHash = (await DocAsync(app, hash: true))["sha256"]!.GetValue<string>();
        await app.SelectAsync(0x1000, 0x1000);
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.openSelectionAsCopy" });
        JsonObject copy = await DocAsync(app);
        Assert.Equal((0L, 0x1000L), (copy["baseAddress"]!.GetValue<long>(), copy["length"]!.GetValue<long>()));
        await app.GoToAsync(0);
        await app.KeyAsync("Insert");
        await app.TypeAsync(string.Concat(Enumerable.Repeat("00", 16)));
        Assert.Equal(0x1010L, (await DocAsync(app))["length"]!.GetValue<long>());
        JsonObject parent = await DocAsync(app, 0, hash: true);
        Assert.Equal(parentHash, parent["sha256"]!.GetValue<string>());
        Assert.False(parent["modified"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-39-01")]
    [Trait("Category", "Nightly")]
    public Task Linked_view_of_5_gib_opens_fast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ENG-SPARSE-10G")] });
        JsonObject r = await app.SendAsync("openRangeInTab", new JsonObject { ["offset"] = 2L << 30, ["length"] = 5L << 30 });
        double ms = r["elapsedMs"]!.GetValue<double>();
        Assert.True(ms < 100 || !PerfEnvironmentFactAttribute.IsPerfMachine, $"{ms} ms");
        JsonObject doc = await DocAsync(app);
        Assert.Equal((0x80000000L, 5L << 30), (doc["baseAddress"]!.GetValue<long>(), doc["length"]!.GetValue<long>()));
        Assert.Equal(TestDataCatalog.Marker(0x80000000), await app.BytesAsync(0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x100000000), await app.BytesAsync(0x100000000 - 0x80000000, 17));
    });
}
