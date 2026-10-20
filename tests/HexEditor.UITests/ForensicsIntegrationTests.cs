using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// フォレンジックの領域をまたぐ確認 (12): ファイルシステムブラウザの日時 (FOR-05 と FOR-09)、イメージ作成の監査ログ (FOR-04 と FOR-20)。
/// テスト用のイメージはテストデータの生成ツールが作ったもの (スパースファイル)。実機のディスクには触れない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ForensicsIntegrationTests
{
    private static async Task<AppSession> OpenFileSystemAsync(UiTestContext ctx, string id, string caseFolder, string timeZone)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = ForensicsCaseTests.En });
        await ForensicsCaseTests.Case(app, "create", ("folder", caseFolder), ("number", "C-0005"), ("name", "time"), ("investigator", "tester"), ("timeZone", timeZone));
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData(id), ["sectorSize"] = 512, ["readOnly"] = true });
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.True(shown["opened"]!.GetValue<bool>(), shown.ToJsonString());
        return app;
    }

    private static Task<JsonObject> BrowserAsync(AppSession app, JsonObject? args = null) =>
        app.SendAsync("fsBrowser", args, UiTest.Scaled(TimeSpan.FromSeconds(60)));

    private static string Row(JsonObject browser, string name) =>
        browser["rows"]!.AsArray().Select(r => r!.GetValue<string>()).First(r => r.Contains(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-05-02")]
    public Task Fat_times_in_the_browser_follow_the_case_time_zone_and_carry_the_estimate_mark() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-FAT32", Path.Combine(ctx.Root, "case"), "Tokyo Standard Time");

        // 1. readme.txt (2026-01-15 10:30:00 として記録) の更新日時: UTC 01:30Z、ローカル 10:30+09:00、「タイムゾーン推定」の印 (~)。
        JsonObject tokyo = await BrowserAsync(app);
        Assert.Contains("Modified (UTC)", tokyo["header"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Modified (local)", tokyo["header"]!.GetValue<string>(), StringComparison.Ordinal);
        string row = Row(tokyo, "readme.txt");
        Assert.Contains("~2026-01-15T01:30:00Z", row, StringComparison.Ordinal);
        Assert.Contains("~2026-01-15T10:30:00+09:00", row, StringComparison.Ordinal);

        // 2・3. ケースのタイムゾーンを UTC にすると、UTC が 10:30Z、ローカルが 10:30+00:00 に変わる。
        await ForensicsCaseTests.Case(app, "setZone", ("timeZone", "UTC"));
        JsonObject utc = null!;
        await app.WaitUntilAsync(async () => Row(utc = await BrowserAsync(app), "readme.txt").Contains("2026-01-15T10:30:00Z", StringComparison.Ordinal),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the browser to follow the case time zone");
        Assert.Contains("~2026-01-15T10:30:00+00:00", Row(utc, "readme.txt"), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-05-03")]
    public Task Utc_display_mode_removes_the_local_columns_from_the_browser_and_the_deleted_files() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS", Path.Combine(ctx.Root, "case"), "Tokyo Standard Time");
        Assert.Contains("(local)", (await BrowserAsync(app))["header"]!.GetValue<string>(), StringComparison.Ordinal);

        // 1. 設定「日時の表示」を UTC にする。2. ファイル一覧と削除されたファイルの一覧にローカルの列がなく、日時の値がすべて Z で終わる。
        await ForensicsCaseTests.Case(app, "setTimeMode", ("mode", "utc"));
        await app.WaitUntilAsync(async () => !(await BrowserAsync(app))["header"]!.GetValue<string>().Contains("(local)", StringComparison.Ordinal),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the browser columns to update");
        foreach (JsonObject list in new[] { await BrowserAsync(app), await BrowserAsync(app, new JsonObject { ["folder"] = "Deleted" }) })
        {
            Assert.DoesNotContain("(local)", list["header"]!.GetValue<string>(), StringComparison.Ordinal);
            string[] times = [.. list["rows"]!.AsArray().SelectMany(r => System.Text.RegularExpressions.Regex.Matches(r!.GetValue<string>(), @"\d{4}-\d\d-\d\dT[0-9:.]+(Z|[+-]\d\d:\d\d)")
                .Select(m => m.Value))];
            Assert.NotEmpty(times);
            Assert.All(times, t => Assert.EndsWith("Z", t, StringComparison.Ordinal));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-06")]
    public Task Creating_partition_entries_is_refused_while_write_protection_is_on() => UiTestContext.RunAsync(async ctx =>
    {
        // パーティション表のエントリの作成 (FOR-08 の仕様 4) は、書き込み保護モードの間は断る (FOR-01 の仕様 2: パーティションの書き込み)。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = ForensicsCaseTests.En });
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.CopyTestData("TD-FOR-LOST-PART"), ["sectorSize"] = 512, ["readOnly"] = false });
        JsonObject scan = await app.SendAsync("fsScan", null, UiTest.Scaled(TimeSpan.FromSeconds(120)));
        Assert.NotEmpty(scan["rows"]!.AsArray());
        await app.SendAsync("tcWriteBlock", ToolsCalcTests.Args("enable"));

        JsonObject refused = await app.SendAsync("fsResults", new JsonObject { ["select"] = new JsonArray(0), ["action"] = "createMbr" });
        Assert.False(refused["created"]!.GetValue<bool>());
        Assert.False(refused["modified"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-04-01")]
    public Task Creating_an_image_writes_start_and_end_lines_with_the_hashes_to_the_case_audit_log() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-SEQ-1M をディスクイメージとして開く (ケースあり)。
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = ForensicsCaseTests.En });
        await ForensicsCaseTests.Case(app, "create", ("folder", folder), ("number", "C-0004"), ("name", "image"), ("investigator", "tester"), ("timeZone", "UTC"));
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = seq, ["sectorSize"] = 512, ["readOnly"] = true });

        // 1. FOR-20 のイメージ作成: raw、MD5 と SHA-256、検証なし、images/seq.dd。
        string output = Path.Combine(folder, "images", "seq.dd");
        JsonObject created = await app.SendAsync("imgCreate", new JsonObject
        {
            ["output"] = output, ["hashes"] = new JsonArray("MD5", "SHA-256"), ["verify"] = false, ["wait"] = true,
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        Assert.Equal("Completed", created["acquisition"]!["status"]!.GetValue<string>());

        // 2. audit.log の行: 開始と終了の 2 行 (image.create)。終了の行に TD-SEQ-1M の MD5 と SHA-256。
        byte[] data = File.ReadAllBytes(seq);
        string md5 = Convert.ToHexStringLower(MD5.HashData(data)), sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        string[] lines = [.. File.ReadAllLines(Path.Combine(folder, "audit.log")).Where(l => l.Contains("\"image.create\"", StringComparison.Ordinal))];
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"phase\":\"start\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"end\"", lines[1], StringComparison.Ordinal);
        Assert.Contains(md5, lines[1], StringComparison.Ordinal);
        Assert.Contains(sha256, lines[1], StringComparison.Ordinal);

        // 取得したイメージは証拠一覧に取得時のハッシュ付きで載る (FOR-20 の仕様 5)。
        JsonObject state = await ForensicsCaseTests.Case(app, "state");
        Assert.Contains(state["evidence"]!.AsArray(), e => e!.ToJsonString().Contains("seq.dd", StringComparison.Ordinal));
    });
}
