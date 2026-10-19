using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 空き領域とスラックの消去の実機の確認 (12 の TC-FOR-31-01〜04)。CI の管理者権限のランナーでだけ、Windows でフォーマットした固定サイズの VHD
/// (TD-FOR-VHDX-DELETED。build/tests/New-WipeTestVhd.ps1) を作り、OS 経由の方法は接続したボリューム (ラベル HEXWIPE) に、直接書き込みは
/// 切り離した VHD のファイルをディスクイメージとして開いて実行する。この PC では仮想ディスクを作らず、ボリュームにも書かない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PrivilegedWipeTests
{
    private static readonly byte[] Keep = TestDataCatalog.TextAscii4K();

    private static async Task<JsonObject?> VhdAsync(string vhd, string action, string? keep = null)
    {
        CiPrivileged.EnsureEnabled();
        string script = Path.Combine(AppLocator.RepositoryRoot, "build", "tests", "New-WipeTestVhd.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Path", vhd, "-Action", action },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (keep is not null)
        {
            start.ArgumentList.Add("-KeepFile");
            start.ArgumentList.Add(keep);
        }

        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output + error);
        string? last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        return last is null ? null : JsonNode.Parse(last)!.AsObject();
    }

    private static async Task<string> CreateAsync(UiTestContext ctx, string vhd) =>
        (await VhdAsync(vhd, "Create", ctx.WriteFile("keep-source.txt", Keep)))!["letter"]!.GetValue<string>() + @":\";

    private static async Task<string> AttachAsync(string vhd) => (await VhdAsync(vhd, "Attach"))!["letter"]!.GetValue<string>() + @":\";

    private static bool RawContains(string path, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[(1 << 20) + needle.Length];
        int carry = 0;
        while (true)
        {
            int n = stream.Read(buffer, carry, buffer.Length - carry);
            if (n <= 0)
            {
                return false;
            }

            int total = carry + n;
            if (buffer.AsSpan(0, total).IndexOf(needle) >= 0)
            {
                return true;
            }

            carry = Math.Min(needle.Length - 1, total);
            buffer.AsSpan(total - carry, carry).CopyTo(buffer);
        }
    }

    private static string FileSha(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string[] Entries(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true })
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static async Task ConfirmAsync(AppSession app)
    {
        await app.WaitUntilAsync(async () => (await app.SendAsync("wipeConfirm"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(60)), "the confirmation");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
    }

    private static async Task<JsonObject> ResultAsync(AppSession app, TimeSpan timeout)
    {
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("wipeState"))["result"] is not null, timeout, "the wipe result");
        return state;
    }

    private static async Task<AppSession> OpenImageFileSystemAsync(UiTestContext ctx, string vhd)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["culture"] = "en-US" } });
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = vhd, ["sectorSize"] = 512, ["readOnly"] = false });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsPartitions"))["done"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "partitions");
        await app.SendAsync("fsPartitionAction", new JsonObject { ["number"] = 1, ["action"] = "showFileSystem" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        await app.SendAsync("fsBrowser", new JsonObject { ["entries"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        return app;
    }

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-31-01")]
    [Trait(UiTest.TC, "TC-FOR-31-03")]
    public Task The_os_method_fills_and_cleans_up_a_mounted_volume_and_cancelling_leaves_nothing_behind() => UiTestContext.RunAsync(async ctx =>
    {
        // TC-FOR-31-01 (A): 未割り当て領域に WIPE-ME-0123456789 が残っている。OS 経由で消去すると見つからず、keep.txt は同じ。
        string a = Path.Combine(ctx.Root, "A.vhd");
        await CreateAsync(ctx, a);
        await VhdAsync(a, "Detach");
        Assert.True(RawContains(a, "WIPE-ME-0123456789"));
        string rootA = await AttachAsync(a);
        string[] before = Entries(rootA);
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["culture"] = "en-US" } });
        Assert.True((await app.SendAsync("wipeFreeSpaceOsCi", new JsonObject { ["root"] = rootA }))["started"]?.GetValue<bool>() == true);
        await ConfirmAsync(app);
        JsonObject state = await ResultAsync(app, UiTest.Scaled(TimeSpan.FromMinutes(5)));
        Assert.False(state["result"]!["cancelled"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(0, state["result"]!["leftovers"]!.GetValue<int>());
        Assert.True(state["result"]!["overwritten"]!.GetValue<long>() > 0);
        Assert.Equal(before, Entries(rootA));
        string log = await app.WaitForLogAsync(l => l.Contains("Audit write.wipe Succeeded", StringComparison.Ordinal), "the audit record");
        Assert.Contains("kind=FreeSpaceOs", log, StringComparison.Ordinal);
        await VhdAsync(a, "Detach");
        Assert.False(RawContains(a, "WIPE-ME-0123456789"));
        rootA = await AttachAsync(a);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Keep)), FileSha(Path.Combine(rootA, "keep.txt")));
        await VhdAsync(a, "Detach");

        // TC-FOR-31-03 (D): 30% を超えたらキャンセルする。一時フォルダ・一時ファイルが残らず、空き容量もほぼ同じ。
        string dPath = Path.Combine(ctx.Root, "D.vhd");
        string rootD = await CreateAsync(ctx, dPath);
        string[] listed = Entries(rootD);
        long free = new DriveInfo(rootD).AvailableFreeSpace;
        await app.SendAsync("wipeDelay", new JsonObject { ["ms"] = 30 });
        Assert.True((await app.SendAsync("wipeFreeSpaceOsCi", new JsonObject { ["root"] = rootD, ["method"] = "dod" }))["started"]?.GetValue<bool>() == true);
        await ConfirmAsync(app);
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await app.SendAsync("wipeState");
            return s["operation"]?["total"]?.GetValue<long?>() is long total && total > 0
                && s["operation"]!["processed"]!.GetValue<long>() > total * 3 / 10 && s["operation"]!["state"]!.GetValue<string>() == "Running";
        }, UiTest.Scaled(TimeSpan.FromMinutes(5)), "30% of the wipe");
        await app.SendAsync("wipeCancel");
        state = await ResultAsync(app, UiTest.Scaled(TimeSpan.FromMinutes(2)));
        Assert.True(state["result"]!["cancelled"]!.GetValue<bool>(), state.ToJsonString());
        Assert.Equal(listed, Entries(rootD));
        Assert.True(Math.Abs(new DriveInfo(rootD).AvailableFreeSpace - free) < 1024 * 1024, $"free {free} -> {new DriveInfo(rootD).AvailableFreeSpace}");
        await VhdAsync(dPath, "Detach");
    }, TimeSpan.FromMinutes(15));

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-31-01")]
    [Trait(UiTest.TC, "TC-FOR-31-02")]
    [Trait(UiTest.TC, "TC-FOR-31-04")]
    public Task Direct_writing_wipes_free_space_and_slack_of_a_windows_ntfs_and_refuses_a_dirty_one() => UiTestContext.RunAsync(async ctx =>
    {
        // TC-FOR-31-01 (B): 直接書き込み。
        string b = Path.Combine(ctx.Root, "B.vhd");
        await CreateAsync(ctx, b);
        await VhdAsync(b, "Detach");
        Assert.True(RawContains(b, "WIPE-ME-0123456789"));
        await using (AppSession app = await OpenImageFileSystemAsync(ctx, b))
        {
            Assert.True((await app.SendAsync("wipeDirect", new JsonObject { ["slack"] = false }))["started"]?.GetValue<bool>() == true);
            await ConfirmAsync(app);
            JsonObject state = await ResultAsync(app, UiTest.Scaled(TimeSpan.FromMinutes(5)));
            Assert.True(state["result"]!["succeeded"]!.GetValue<bool>(), state.ToJsonString());
            Assert.False(RawContains(b, "WIPE-ME-0123456789"));

            // TC-FOR-31-02: スラック。slack.txt のファイル末尾以降が 00、常駐の tiny.txt は対象外に数える。
            await app.SendAsync("fsBrowser", new JsonObject { ["select"] = "slack.txt", ["action"] = "showClusters", ["entries"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
            long offset = (await app.SendAsync("fsFragments"))["fragments"]!.AsArray()[0]!["offset"]!.GetValue<long>();
            Assert.True(RawContainsAt(b, offset + 100, 4096 - 100, "SLACK-REMNANT"));
            await app.SendAsync("wipeState");
            Assert.True((await app.SendAsync("wipeDirect", new JsonObject { ["slack"] = true }))["started"]?.GetValue<bool>() == true);
            await ConfirmAsync(app);
            state = await ResultAsync(app, UiTest.Scaled(TimeSpan.FromMinutes(5)));
            Assert.True(state["result"]!["succeeded"]!.GetValue<bool>(), state.ToJsonString());
            Assert.True(state["slackSkipped"]!["resident"]!.GetValue<int>() >= 1, state.ToJsonString());
            Assert.All(ReadAt(b, offset + 100, 4096 - 100), x => Assert.Equal(0, x));
        }

        string rootB = await AttachAsync(b);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Keep)), FileSha(Path.Combine(rootB, "keep.txt")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("SLACK-REMNANT", 8)))[..100])),
            FileSha(Path.Combine(rootB, "slack.txt")));
        Assert.Equal("tiny-file!", File.ReadAllText(Path.Combine(rootB, "tiny.txt")));
        await VhdAsync(b, "Detach");

        // TC-FOR-31-04 (E): dirty フラグの立った NTFS では直接書き込みを実行しない。
        string e = Path.Combine(ctx.Root, "E.vhd");
        await CreateAsync(ctx, e);
        await VhdAsync(e, "Dirty");
        await VhdAsync(e, "Detach");
        string sha = FileSha(e);
        await using (AppSession app = await OpenImageFileSystemAsync(ctx, e))
        {
            await app.SendAsync("wipeDirect", new JsonObject { ["slack"] = false });
            FlaUI.Core.AutomationElements.AutomationElement dialog = await app.WaitForDialogAsync("WipeRefusedDialog");
            await app.WaitForDialogTextAsync(dialog, "chkdsk");
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
            Assert.Null((await app.SendAsync("wipeState"))["result"]);
        }

        Assert.Equal(sha, FileSha(e));
    }, TimeSpan.FromMinutes(15));

    private static byte[] ReadAt(string path, long offset, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool RawContainsAt(string path, long offset, int length, string text) =>
        ReadAt(path, offset, length).AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
}
