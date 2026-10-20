using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ディスクイメージの UI (12 の FOR-20〜FOR-26)。入力元はテストデータのファイル、出力はテストの一時フォルダ。ディスクには触れない
/// (クローン・復元の実際の仮想ディスクでの確認は <see cref="ForensicImagePrivilegedTests"/>。CI の特権のジョブだけ)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ForensicImageTests
{
    private static readonly Lazy<Dictionary<string, string>> En = new(() => UiHelpers.LoadResw("en"));

    private static string R(string key) => En.Value[key];

    private static async Task<JsonObject> ImgStateAsync(AppSession app) => await app.SendAsync("imgState");

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-20-01")]
    public Task Completion_notice_shows_the_hashes_and_an_open_log_button() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source] });
        string output = Path.Combine(ctx.Root, "images", "m.dd");
        await app.SendAsync("imgCreate", new JsonObject { ["output"] = output, ["wait"] = true });

        JsonObject state = await ImgStateAsync(app);
        Assert.Equal("Completed", state["acquisition"]!["status"]!.GetValue<string>());
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Equal(sha, state["acquisition"]!["hashes"]!["SHA-256"]!.GetValue<string>());
        await app.WaitUntilAsync(() => Task.FromResult(app.NotificationsText().Contains(sha, StringComparison.Ordinal)), UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the completion notice with the hash");
        Assert.NotNull(app.Button(R("Img_OpenLog")));
        Assert.True(File.Exists(Path.Combine(ctx.Root, "images", "m.txt")));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-20-05")]
    public Task Cancelling_an_acquisition_asks_to_keep_or_delete_and_the_log_says_interrupted() => UiTestContext.RunAsync(async ctx =>
    {
        string markers = TestDataCatalog.Get("TD-MARKERS-1G");
        var hooks = new JsonObject { ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-MARKERS-1G.bin", ["delayMs"] = 10 }) };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [markers], Hooks = hooks });
        string output = Path.Combine(ctx.Root, "c.dd");
        await app.SendAsync("imgCreate", new JsonObject { ["output"] = output, ["verify"] = false });

        // 進捗が 20% を超えたらキャンセルする。
        await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray()
                .Any(o => o!["state"]!.GetValue<string>() == "Running" && o["total"]?.GetValue<long>() is long total && o["processed"]!.GetValue<long>() > total / 5),
            UiTest.Scaled(TimeSpan.FromSeconds(120)), "the acquisition to pass 20%");
        await app.SendAsync("imgCancel", new JsonObject { ["text"] = "c.dd" });

        // 残す・削除するの選択肢が出る。「残す」を選ぶ。
        AutomationElement dialog = await app.WaitForDialogAsync("ImgKeepPartialDialog");
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains(R("Img_Keep"), buttons);
        Assert.Contains(R("Img_Delete"), buttons);
        await app.InvokeDialogButtonAsync(R("Img_Keep"), idle: false);
        await app.WaitUntilAsync(async () => (await ImgStateAsync(app))["acquisition"]?["status"]?.GetValue<string>() == "Cancelled",
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the acquisition to end");

        Assert.True(File.Exists(output));
        Assert.Contains(R("ImgLog_Cancelled"), File.ReadAllText(Path.Combine(ctx.Root, "c.txt")));
    });

    /// <summary>「イメージを検証」をファイルで始め、比べる値を入れて検証し、結果の本文を返す。</summary>
    private static async Task<string> VerifyAsync(AppSession app, string path, string? expected)
    {
        await app.SendAsync("imgVerifyStart", new JsonObject { ["path"] = path });
        await app.WaitForDialogAsync("ImgVerifyDialog");
        if (expected is not null)
        {
            await app.UiaSetValueAsync("ImgVerify_Expected", expected);
        }

        await app.InvokeDialogButtonAsync(R("Img_VerifyStart"), idle: false);
        AutomationElement result = await app.WaitForDialogAsync("ImgVerifyResultDialog");
        string text = await app.UiaNameAsync("ImgVerifyResult_Body");
        _ = result;
        await app.InvokeDialogButtonAsync(R("Common_Close"));
        return text;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-22-03")]
    public Task A_raw_image_without_a_log_has_no_reference_until_a_value_is_entered() => UiTestContext.RunAsync(async ctx =>
    {
        string plain = ctx.CopyTestData("TD-SEQ-1M", "plain.dd");
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plain)));
        AppSession app = await ctx.StartAsync(new AppOptions());

        string none = await VerifyAsync(app, plain, null);
        Assert.Contains("SHA-256: " + R("Img_Verdict_NoReference"), none);
        Assert.Equal("NoReference", (await ImgStateAsync(app))["verification"]!["SHA-256"]!.GetValue<string>());

        await VerifyAsync(app, plain, sha);
        Assert.Equal("Match", (await ImgStateAsync(app))["verification"]!["SHA-256"]!.GetValue<string>());

        string changed = sha[..^1] + (sha[^1] == '0' ? '1' : '0');
        await VerifyAsync(app, plain, changed);
        Assert.Equal("Mismatch", (await ImgStateAsync(app))["verification"]!["SHA-256"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-24-03")]
    public Task Write_protect_mode_disables_clone_and_restore_with_the_reason() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-SEQ-1M", "src.img");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], ExtraArgs = ["--write-block"] });
        JsonObject state = await ImgStateAsync(app);
        Assert.True(state["writeBlocked"]!.GetValue<bool>());
        JsonObject clone = state["commands"]!["forensics.disk.clone"]!.AsObject();
        Assert.False(clone["enabled"]!.GetValue<bool>());
        Assert.Equal(R("WriteBlock_Enabled"), clone["reason"]!.GetValue<string>());
        Assert.False(state["commands"]!["forensics.sectors.restore"]!["enabled"]!.GetValue<bool>());

        // コマンドパレットの「フォレンジック: ディスクをクローン」も無効。
        JsonObject commands = await app.SendAsync("commands");
        JsonObject item = commands["items"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["id"]!.GetValue<string>() == "forensics.disk.clone");
        Assert.False(item["enabled"]!.GetValue<bool>());
        Assert.Equal("Command_DiskClone", item["menu"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-26-02")]
    public Task Swapping_raid0_members_changes_the_detection_from_gpt_to_no_partition_table() => UiTestContext.RunAsync(async ctx =>
    {
        string m0 = TestDataCatalog.Get("TD-FOR-RAID0-M0");
        string m1 = TestDataCatalog.Get("TD-FOR-RAID0-M1");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [m0, m1] });
        await app.SendAsync("imgRun", new JsonObject { ["id"] = "forensics.raid.reconstruct" });
        await app.WaitForDialogAsync("ImgRaidDialog");

        JsonObject r0 = await app.SendAsync("imgRaid", new JsonObject
        {
            ["keep"] = new JsonArray("TD-FOR-RAID0-M0.bin", "TD-FOR-RAID0-M1.bin"),
            ["level"] = 1,
            ["stripe"] = 64 * 1024,
        });
        Assert.Equal(new[] { "TD-FOR-RAID0-M0.bin", "TD-FOR-RAID0-M1.bin" }, r0["members"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Contains(R("ImgRaid_TableDetected").Replace("{0}", "GPT", StringComparison.Ordinal).Split('(')[0].Trim(), r0["detection"]!.GetValue<string>());

        JsonObject r1 = await app.SendAsync("imgRaid", new JsonObject { ["move"] = new JsonArray(0, 1) });
        Assert.Equal(new[] { "TD-FOR-RAID0-M1.bin", "TD-FOR-RAID0-M0.bin" }, r1["members"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Contains(R("ImgRaid_NoTable"), r1["detection"]!.GetValue<string>());
        await app.InvokeDialogButtonAsync(R("Common_Cancel"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-21-02")]
    public Task Opening_a_split_raw_with_a_missing_segment_names_it_and_offers_to_open_what_exists() => UiTestContext.RunAsync(async ctx =>
    {
        // 3 つのセグメント (m.001〜m.003) のうち m.002 を欠いた分割 raw。
        byte[] data = File.ReadAllBytes(TestDataCatalog.Get("TD-SEQ-1M"));
        string first = Path.Combine(ctx.Root, "m.001");
        File.WriteAllBytes(first, data[..(512 * 1024)]);
        File.WriteAllBytes(Path.Combine(ctx.Root, "m.003"), data[(512 * 1024)..]);
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("imgOpen", new JsonObject { ["path"] = first, ["detach"] = true });

        AutomationElement dialog = await app.WaitForDialogAsync("ImgMissingSegmentsDialog");
        await app.WaitForDialogTextAsync(dialog, "m.002");
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains(R("Img_OpenAvailable"), buttons);
        Assert.Contains(R("Common_Cancel"), buttons);
        await app.InvokeDialogButtonAsync(R("Img_OpenAvailable"));
        await app.WaitForTabsAsync(1);
        JsonObject doc = await app.DocumentAsync();
        Assert.True(doc["readOnly"]!.GetValue<bool>());
    });
}
