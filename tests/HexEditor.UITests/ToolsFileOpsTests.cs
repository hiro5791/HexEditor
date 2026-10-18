using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ファイルツールの分割 (TOOL-13)・結合 (TOOL-14)・デインターリーブ (TOOL-15)・安全な削除 (TOOL-17)。ダイアログは UI オートメーションで操作し、
/// ファイルの選択ダイアログはテスト用の代わりの結果 (openPicker) で置き換える。安全な削除は、テストの一時フォルダのファイルだけを対象にする。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ToolsFileOpsTests
{
    private static readonly JsonObject EnUs = new() { ["culture"] = "en-US", ["timeZone"] = "UTC" };

    private static Task SetPickerAsync(AppSession app, params string[] paths) =>
        app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]) } });

    private static async Task SetCheckedAsync(AppSession app, string id, bool value = true) =>
        await app.SendAsync("setChecked", new JsonObject { ["id"] = id, ["value"] = value });

    private static async Task<string> WaitForTextAsync(AppSession app, string id, Func<string, bool> ready, string what)
    {
        string text = string.Empty;
        await app.WaitUntilAsync(async () => ready(text = await app.UiaNameAsync(id)), UiTest.Scaled(TimeSpan.FromSeconds(15)), what);
        return text;
    }

    private static async Task WaitForFilesAsync(string folder, string pattern, int count) =>
        await WaitAsync(() => Directory.Exists(folder) && Directory.GetFiles(folder, pattern).Length == count, $"{count} files {pattern}");

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow + UiTest.Scaled(TimeSpan.FromSeconds(60));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }

    // ---- TOOL-13 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-13-02")]
    public Task Split_at_the_bookmark_positions() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-SEQ-1M", "s.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = EnUs });
        await app.GoToAsync(0x1000);
        await app.KeyAsync("F2", ctrl: true);
        await app.GoToAsync(0x80000);
        await app.KeyAsync("F2", ctrl: true);

        await app.CommandAsync("Command_FileSplit");
        await app.WaitForAsync("SplitDialog");
        await SetCheckedAsync(app, "Split_ByPositions");
        await SetCheckedAsync(app, "Split_UseBookmarks");
        string summary = await WaitForTextAsync(app, "Split_Summary", t => t.Contains("s.bin.003", StringComparison.Ordinal), "the split summary");
        Assert.Contains("3 files", summary, StringComparison.Ordinal);
        Assert.Contains("s.bin.001", summary, StringComparison.Ordinal);
        Assert.Contains("1,048,576", summary, StringComparison.Ordinal);

        await app.InvokeDialogButtonAsync("Split", idle: false);
        await WaitForFilesAsync(ctx.Root, "s.bin.0*", 3);
        Assert.Equal([0x1000L, 0x7F000L, 0x80000L], new[] { 1, 2, 3 }.Select(i => new FileInfo(Path.Combine(ctx.Root, $"s.bin.{i:000}")).Length));
        byte[] second = File.ReadAllBytes(Path.Combine(ctx.Root, "s.bin.002"));
        byte[] third = File.ReadAllBytes(Path.Combine(ctx.Root, "s.bin.003"));
        Assert.Equal(((byte)0x00, (byte)0x01, (byte)0x00), (second[0], second[1], third[0]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-13-04")]
    public Task Unsaved_changes_are_split_with_their_new_content() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-SEQ-1M", "u.bin");
        string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = EnUs });
        await app.GoToAsync(0);
        await app.TypeAsync("EE");
        await app.GoToAsync(0x10);
        await app.KeyAsync("Insert");
        await app.TypeAsync("1122");

        await app.CommandAsync("Command_FileSplit");
        await app.WaitForAsync("SplitDialog");
        Assert.Contains("u.bin", await app.UiaNameAsync("Split_InputDocument"), StringComparison.Ordinal);
        await SetCheckedAsync(app, "Split_ByCount");
        await app.UiaSetValueAsync("Split_Count", "2");
        await WaitForTextAsync(app, "Split_Summary", t => t.Contains("2 files", StringComparison.Ordinal), "the split summary");
        await app.InvokeDialogButtonAsync("Split", idle: false);
        await WaitForFilesAsync(ctx.Root, "u.bin.0*", 2);

        string first = Path.Combine(ctx.Root, "u.bin.001");
        string second = Path.Combine(ctx.Root, "u.bin.002");
        Assert.Equal(1_048_578L, new FileInfo(first).Length + new FileInfo(second).Length);
        Assert.Equal(524_289L, new FileInfo(first).Length);
        byte[] head = File.ReadAllBytes(first)[..0x14];
        Assert.Equal([0xEE, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0x11, 0x22, 0x10, 0x11], head);
        Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-13-03")]
    public Task Split_does_not_run_when_the_output_volume_has_no_room() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-RANDOM-16M", "r.bin");
        string output = Path.Combine(ctx.Root, "out");
        Directory.CreateDirectory(output);
        // 仮想ディスクの代わりに、出力先の空き容量をテスト用のフックで 8 MiB にする (管理者権限が要らない)。
        var hooks = new JsonObject { ["culture"] = "en-US", ["timeZone"] = "UTC", ["freeSpace"] = 8L * 1024 * 1024 };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = hooks });

        await app.CommandAsync("Command_FileSplit");
        await app.WaitForAsync("SplitDialog");
        await SetCheckedAsync(app, "Split_ByCount");
        await app.UiaSetValueAsync("Split_Count", "2");
        await app.UiaSetValueAsync("Split_OutputFolder", output);
        await WaitForTextAsync(app, "Split_Summary", t => t.Contains("2 files", StringComparison.Ordinal), "the split summary");
        await app.InvokeDialogButtonAsync("Split", idle: false);

        var dialog = await app.WaitForDialogAsync("FileOpsNoSpaceDialog");
        string text = await app.WaitForDialogTextAsync(dialog, "8,388,608", "bytes");
        Assert.Contains("Not enough free space", text, StringComparison.Ordinal);
        await app.InvokeDialogButtonAsync("Close");
        Assert.Empty(Directory.GetFiles(output));
    });

    // ---- TOOL-14 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-14-02")]
    public Task Adding_the_first_numbered_file_offers_the_rest() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "parts");
        Directory.CreateDirectory(folder);
        long sum = 0;
        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"p.bin.{i:000}"), new byte[i * 10]);
            sum += i * 10;
        }

        File.WriteAllBytes(Path.Combine(folder, "q.bin.001"), [1]);
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs });
        await SetPickerAsync(app, Path.Combine(folder, "p.bin.001"));

        await app.CommandAsync("Command_FileJoin");
        await app.WaitForAsync("JoinDialog");
        await app.UiaInvokeAsync("Join_Add");
        string question = await WaitForTextAsync(app, "Join_SequenceText", t => t.Length > 0, "the question");
        Assert.Contains("p.bin.002", question, StringComparison.Ordinal);
        Assert.Contains("11", question, StringComparison.Ordinal);
        Assert.NotNull(app.Button("Add all numbered files"));
        Assert.NotNull(app.Button("Add only this file"));
        await app.UiaInvokeAsync("Join_SequenceAll");

        await app.WaitUntilAsync(async () => (await app.SendAsync("listItems", new JsonObject { ["id"] = "Join_List" }))["items"]!.AsArray().Count == 12,
            UiTest.Scaled(TimeSpan.FromSeconds(15)), "12 files in the list");
        JsonArray items = (await app.SendAsync("listItems", new JsonObject { ["id"] = "Join_List" }))["items"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"p.bin.{i:000}"), items.Select(i => Path.GetFileName(i!["text"]!.GetValue<string>())));
        string total = await app.UiaNameAsync("Join_Total");
        Assert.Contains("12 files", total, StringComparison.Ordinal);
        Assert.Contains(sum.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), total, StringComparison.Ordinal);
    });

    // ---- TOOL-15 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-15-01")]
    public Task Deinterleave_into_even_and_odd_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        string rom = ctx.WriteFile("rom.bin", [0x00, 0x11, 0x22, 0x33]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [rom], Hooks = EnUs });

        await app.CommandAsync("Command_FileDeinterleave");
        await app.WaitForAsync("DeinterleaveDialog");
        string summary = await WaitForTextAsync(app, "Deint_Summary", t => t.Contains("2 outputs", StringComparison.Ordinal), "the summary");
        Assert.Contains("2, 2", summary, StringComparison.Ordinal);
        await app.InvokeDialogButtonAsync("Deinterleave", idle: false);
        await WaitAsync(() => File.Exists(Path.Combine(ctx.Root, "rom.1.bin")) && File.Exists(Path.Combine(ctx.Root, "rom.2.bin")), "the outputs");
        Assert.Equal([0x00, 0x22], File.ReadAllBytes(Path.Combine(ctx.Root, "rom.1.bin")));
        Assert.Equal([0x11, 0x33], File.ReadAllBytes(Path.Combine(ctx.Root, "rom.2.bin")));
    });

    // ---- TOOL-17 ----

    private static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-17-02")]
    public Task The_confirmation_says_it_cannot_be_undone_and_names_the_limits() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string b = ctx.CopyTestData("TD-ZERO-1M", "zero.bin");
        string shaA = Sha(a);
        string shaB = Sha(b);
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs });

        await app.CommandAsync("Command_FileShred");
        await app.WaitForAsync("ShredDialog");
        await SetPickerAsync(app, a, b);
        await app.UiaInvokeAsync("Shred_Add");
        await app.WaitUntilAsync(async () => (await app.SendAsync("listItems", new JsonObject { ["id"] = "Shred_List" }))["items"]!.AsArray().Count == 2,
            UiTest.Scaled(TimeSpan.FromSeconds(15)), "two files in the list");
        await SetCheckedAsync(app, "Shred_Dod");
        await app.InvokeDialogButtonAsync("Delete...", idle: false);

        var dialog = await app.WaitForDialogAsync("ShredConfirmDialog");
        string text = await app.WaitForDialogTextAsync(dialog, "2 files", "2,097,152", "DoD 5220.22-M", "cannot be undone", "wear leveling", "shadow copies", "slack");
        Assert.Contains("Recycle Bin", text, StringComparison.Ordinal);
        Assert.Contains("OneDrive", text, StringComparison.Ordinal);
        Assert.Contains("copy-on-write", text, StringComparison.Ordinal);
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains("Delete permanently", buttons);
        Assert.Contains("Cancel", buttons);
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.Equal((shaA, shaB), (Sha(a), Sha(b)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-17-03")]
    public Task A_file_used_by_another_program_is_listed_as_a_failure() => UiTestContext.RunAsync(async ctx =>
    {
        string locked = ctx.CopyTestData("TD-SEQ-1M", "locked.bin");
        string free = ctx.CopyTestData("TD-ZERO-1M", "free.bin");
        string shaLocked = Sha(locked);
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs });
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        await app.CommandAsync("Command_FileShred");
        await app.WaitForAsync("ShredDialog");
        await SetPickerAsync(app, locked, free);
        await app.UiaInvokeAsync("Shred_Add");
        await app.WaitUntilAsync(async () => (await app.SendAsync("listItems", new JsonObject { ["id"] = "Shred_List" }))["items"]!.AsArray().Count == 2,
            UiTest.Scaled(TimeSpan.FromSeconds(15)), "two files in the list");
        await app.InvokeDialogButtonAsync("Delete...", idle: false);
        await app.WaitForDialogAsync("ShredConfirmDialog");
        await app.InvokeDialogButtonAsync("Delete permanently", idle: false);

        await app.WaitForDialogAsync("ShredResultDialog");
        string failures = await app.UiaNameAsync("Shred_FailureList");
        Assert.Contains("locked.bin", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("free.bin", failures, StringComparison.Ordinal);
        Assert.Contains("In use by another program", failures, StringComparison.Ordinal);
        await app.InvokeDialogButtonAsync("Close");
        hold.Dispose();
        Assert.False(File.Exists(free));
        Assert.Equal(shaLocked, Sha(locked));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-17-04")]
    public Task A_file_open_in_the_app_cannot_be_added() => UiTestContext.RunAsync(async ctx =>
    {
        string open = ctx.CopyTestData("TD-SEQ-1M", "open.bin");
        string sha = Sha(open);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [open], Hooks = EnUs });

        await app.CommandAsync("Command_FileShred");
        await app.WaitForAsync("ShredDialog");
        await SetPickerAsync(app, open);
        await app.UiaInvokeAsync("Shred_Add");
        string message = await WaitForTextAsync(app, "Shred_Message", t => t.Contains("open.bin", StringComparison.Ordinal), "the message");
        Assert.Contains("Close it first", message, StringComparison.Ordinal);
        Assert.Empty((await app.SendAsync("listItems", new JsonObject { ["id"] = "Shred_List" }))["items"]!.AsArray());
        Assert.Equal(sha, Sha(open));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-17-05")]
    public Task System_folders_and_drive_roots_cannot_be_added() => UiTestContext.RunAsync(async ctx =>
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string winIni = Path.Combine(windows, "win.ini");
        DateTime modified = File.Exists(winIni) ? File.GetLastWriteTimeUtc(winIni) : default;
        string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files");
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs });

        await app.CommandAsync("Command_FileShred");
        await app.WaitForAsync("ShredDialog");
        foreach ((string path, string expected) in new[] { (winIni, "System folders"), (common, "System folders"), (Path.GetPathRoot(windows)!, "drive root") })
        {
            await SetPickerAsync(app, path);
            await app.UiaInvokeAsync("Shred_Add");
            await WaitForTextAsync(app, "Shred_Message", t => t.Contains(expected, StringComparison.OrdinalIgnoreCase), $"the message for {path}");
            Assert.Empty((await app.SendAsync("listItems", new JsonObject { ["id"] = "Shred_List" }))["items"]!.AsArray());
        }

        if (File.Exists(winIni))
        {
            Assert.Equal(modified, File.GetLastWriteTimeUtc(winIni));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-17-06")]
    public Task Write_protect_mode_disables_the_secure_delete_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = EnUs, ExtraArgs = ["--write-block"] });
        JsonObject item = await app.SendAsync("menuItem", new JsonObject { ["id"] = "Command_FileShred" });
        Assert.True(item["found"]!.GetValue<bool>());
        Assert.False(item["enabled"]!.GetValue<bool>());
        string reason = (item["reason"]?.GetValue<string>() ?? string.Empty) + (item["toolTip"]?.GetValue<string>() ?? string.Empty);
        Assert.Contains("Write-protect mode is on", reason, StringComparison.Ordinal);

        JsonObject executed = await app.SendAsync("execute", new JsonObject { ["id"] = "tools.files.shred", ["fromPalette"] = true });
        Assert.False(executed["executed"]!.GetValue<bool>());
    });
}
