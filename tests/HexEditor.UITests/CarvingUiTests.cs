using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>カービングのシグネチャの管理 (FOR-18)、カービングの実行 (FOR-19)、レポートの作成 (FOR-33)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CarvingUiTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private static JsonObject En => ForensicsCaseTests.En;

    private static async Task ExitAsync(AppSession app)
    {
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(ExitTimeout);
    }

    private static async Task SelectItemAsync(AppSession app, string name)
    {
        AutomationElement item = await app.WaitForAsync("Sig_Item_" + name);
        item.Patterns.SelectionItem.Pattern.Select();
        await app.IdleAsync();
    }

    private static async Task<string> ValueOf(AppSession app, string id) => (await app.WaitForAsync(id)).Patterns.Value.Pattern.Value.Value;

    private static async Task CloseDialogAsync(AppSession app) => await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });

    private static JsonObject Signature(JsonObject state, string name) =>
        state["signatures"]!.AsArray().Select(s => s!.AsObject()).Single(s => s["name"]!.GetValue<string>() == name);

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-18-01")]
    public Task A_new_definition_is_saved_and_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = En, WaitForEditor = false });

        // 1. 新しい定義: 名前 Java class、拡張子 class、ヘッダー CA FE BA BE、最大サイズ 1M。
        await app.CommandAsync("Command_CarvingSignatures");
        await app.WaitForAsync("SignatureManagerDialog");
        await app.UiaInvokeAsync("Sig_New");
        await app.UiaSetValueAsync("Sig_Name", "Java class");
        await app.UiaSetValueAsync("Sig_Extension", "class");
        await app.UiaSetValueAsync("Sig_Headers", "CA FE BA BE");
        await app.UiaSetValueAsync("Sig_MaxSize", "1M");
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("Sig_Save")).IsEnabled, TimeSpan.FromSeconds(10), "the Save button");
        await app.UiaInvokeAsync("Sig_Save");
        await app.IdleAsync();
        await CloseDialogAsync(app);
        Assert.Equal(1_048_576, Signature(await ForensicsCaseTests.Carve(app, "signatures"), "Java class")["maxSize"]!.GetValue<long>());

        // 2. 再起動して、シグネチャの管理を開くと、定義が残っている。
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = En, WaitForEditor = false });
        JsonObject reloaded = await ForensicsCaseTests.Carve(again, "reload");
        JsonObject saved = Signature(reloaded, "Java class");
        Assert.Equal("CA FE BA BE", saved["headers"]![0]!.GetValue<string>());
        Assert.Equal(1_048_576, saved["maxSize"]!.GetValue<long>());
        await again.CommandAsync("Command_CarvingSignatures");
        await again.WaitForAsync("SignatureManagerDialog");
        await SelectItemAsync(again, "Java class");
        Assert.Contains("CA FE BA BE", await ValueOf(again, "Sig_Headers"), StringComparison.Ordinal);
        await CloseDialogAsync(again);

        // 3. carving-signatures.json に同じ定義がある。
        string json = File.ReadAllText(Path.Combine(profile, "carving-signatures.json"));
        Assert.Contains("Java class", json, StringComparison.Ordinal);
        Assert.Contains("CA FE BA BE", json, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-18-02")]
    public Task Built_in_definitions_cannot_be_edited_but_a_copy_can() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = En, WaitForEditor = false });
        long original = Signature(await ForensicsCaseTests.Carve(app, "signatures"), "JPEG")["maxSize"]!.GetValue<long>();
        await app.CommandAsync("Command_CarvingSignatures");
        await app.WaitForAsync("SignatureManagerDialog");

        // 1. 組み込みの JPEG: 入力欄は編集できず、「有効」のチェックだけを切り替えられる。
        await SelectItemAsync(app, "JPEG");
        foreach (string id in new[] { "Sig_Name", "Sig_Extension", "Sig_Headers", "Sig_MaxSize", "Sig_MinSize", "Sig_Footer", "Sig_Structure" })
        {
            Assert.False((await app.WaitForAsync(id)).IsEnabled, id);
        }

        Assert.True((await app.WaitForAsync("Sig_Enabled")).IsEnabled);
        Assert.False((await app.WaitForAsync("Sig_Delete")).IsEnabled);

        // 2. 複製して、複製の最大サイズを 20M に変えて保存する。
        await app.UiaInvokeAsync("Sig_Duplicate");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("Sig_MaxSize")).IsEnabled, TimeSpan.FromSeconds(10), "the copy to be editable");
        await app.UiaSetValueAsync("Sig_MaxSize", "20M");
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("Sig_Save")).IsEnabled, TimeSpan.FromSeconds(10), "the Save button");
        await app.UiaInvokeAsync("Sig_Save");
        await app.IdleAsync();
        await CloseDialogAsync(app);

        // 3. 元の JPEG の最大サイズは変わっていない。
        JsonObject state = await ForensicsCaseTests.Carve(app, "signatures");
        Assert.Equal(original, Signature(state, "JPEG")["maxSize"]!.GetValue<long>());
        Assert.Equal(20L * 1024 * 1024, Signature(state, "JPEG (copy)")["maxSize"]!.GetValue<long>());
        Assert.False(Signature(state, "JPEG (copy)")["builtIn"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-19-01")]
    public Task Carving_dialog_finds_four_files_and_writes_them_with_a_manifest() => UiTestContext.RunAsync(async ctx =>
    {
        string image = ctx.CopyTestData("TD-FOR-CARVE", "carve.dd");
        string folder = Path.Combine(ctx.Root, "case");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [image], Hooks = En });
        await ForensicsCaseTests.Case(app, "create", ("folder", folder));

        // 既定の設定 (全体、有効な定義すべて、書き出す)。書き出し先はケースの exports/carved/。
        await app.CommandAsync("Command_CarvingRun");
        await app.WaitForAsync("CarveDialog");
        Assert.Equal(Path.Combine(folder, "exports", "carved"), await ValueOf(app, "Carve_Output"));
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () =>
        {
            JsonObject carve = await ForensicsCaseTests.Carve(app, "state");
            return !carve["running"]!.GetValue<bool>() && carve["rows"]!.GetValue<long>() == 4;
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the carving results");

        string output = Path.Combine(folder, "exports", "carved");
        byte[][] originals = [TestData.TestDataCatalog.ForJpeg(), TestData.TestDataCatalog.Png16(), TestData.TestDataCatalog.ForPdf(), TestData.TestDataCatalog.ZipSample()];
        string[] names = ["jpeg/0000000000100000.jpg", "png/0000000000200000.png", "pdf/0000000000300000.pdf", "zip/0000000000400000.zip"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originals[i])), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, names[i])))));
        }

        // 結果の行を選ぶと、開始位置へジャンプして範囲を選択する。
        await ForensicsCaseTests.Case(app, "panels");
        await ForensicsCaseTests.Carve(app, "select", ("index", 1));
        JsonObject state = await ForensicsCaseTests.Carve(app, "state");
        Assert.Equal(0x200000, state["selection"]!["start"]!.GetValue<long>());
        Assert.Equal(originals[1].Length, state["selection"]!["length"]!.GetValue<long>());

        // 監査ログに、開始・終了・件数・マニフェストのハッシュが記録される。
        string[] audit = File.ReadAllLines(Path.Combine(folder, "audit.log")).Where(l => l.Contains("carve.run", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, audit.Length);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "carving-manifest.csv")))), audit[1], StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-19-03")]
    public Task Cancelling_a_carving_of_a_huge_image_keeps_the_manifest_and_the_ui_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        string output = Path.Combine(ctx.Root, "out");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")], Hooks = En });

        // 1. 書き出し先 out でカービングを開始する (走査の完了を待たずに戻る)。
        JsonObject start = await ForensicsCaseTests.Case(app, "carveStart", ("output", output));
        Assert.False(start.ContainsKey("problem"));
        await app.WaitUntilAsync(async () => (await ForensicsCaseTests.Carve(app, "state"))["running"]!.GetValue<bool>(), TimeSpan.FromSeconds(30), "the carving to start");

        // 2. 10 秒間、UI スレッドの応答を 50 ms ごとに確かめる (状態の問い合わせの往復が止まらない)。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double worst = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(UiTest.Scaled(TimeSpan.FromSeconds(10)).TotalSeconds))
        {
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            await ForensicsCaseTests.Carve(app, "state");
            worst = Math.Max(worst, System.Diagnostics.Stopwatch.GetElapsedTime(before).TotalMilliseconds);
            await Task.Delay(50);
        }

        Assert.True(worst < UiTest.Scaled(TimeSpan.FromSeconds(2)).TotalMilliseconds, $"the slowest round trip was {worst:F0} ms");

        // 3. 処理センターでキャンセルする。
        await app.UiaInvokeAsync("Status_Operations");
        // 要素ができても、Invoke の実装はポップアップの配置が済むまで使えないことがある。使えるまで試す。
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                (await app.WaitForAsync("Operations_Cancel")).Patterns.Invoke.Pattern.Invoke();
                return true;
            }
            catch (FlaUI.Core.Exceptions.PatternNotSupportedException)
            {
                return false;
            }
        }, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the cancel button of the operations flyout");
        await app.WaitUntilAsync(async () => !(await ForensicsCaseTests.Carve(app, "state"))["running"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the carving to stop");

        // 4. マニフェストが残っていて、ヘッダー行がある。
        string manifest = Path.Combine(output, "carving-manifest.csv");
        Assert.True(File.Exists(manifest));
        Assert.StartsWith("Number,Type,Offset,LBA,Length,LengthMethod,MD5,SHA256,OutputPath", File.ReadAllLines(manifest)[0], StringComparison.Ordinal);
    });

    [CiPrivilegedFact]
    [Trait(UiTest.TC, "TC-FOR-19-04")]
    public Task Carving_does_not_start_when_the_output_is_on_the_investigated_disk() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = En, RealElevation = true, WaitForEditor = false });
        await app.SendAsync("openDisk", new JsonObject { ["path"] = CiPrivileged.VhdDiskPath, ["readOnly"] = true });
        string target = CiPrivileged.VhdVolumeLetter.TrimEnd(':') + @":\carved";

        // 1. 書き出し先に同じ仮想ディスク上のボリュームを指定して、開始を押す。
        await app.CommandAsync("Command_CarvingRun");
        await app.WaitForAsync("CarveDialog");
        await app.UiaSetValueAsync("Carve_Output", target);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        // 2. 対象と同じディスクには出力できない旨のメッセージが出て、処理は始まらない。
        await app.WaitUntilAsync(() => Task.FromResult(app.TextOrEmpty("Carve_Error").Contains("under investigation", StringComparison.Ordinal)), TimeSpan.FromSeconds(10), "the same-disk message");
        Assert.False((await ForensicsCaseTests.Carve(app, "state"))["running"]!.GetValue<bool>());

        // 3. フォルダは作られていない。
        Assert.False(Directory.Exists(target));
        await CloseDialogAsync(app);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-33-03")]
    public Task Report_created_in_the_app_records_its_sha256_in_the_audit_log() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq], Hooks = En });
        await ForensicsCaseTests.Case(app, "create", ("folder", folder));
        await ForensicsCaseTests.Case(app, "addEvidence", ("hashes", new[] { "SHA-256" }));
        JsonObject state = await ForensicsCaseTests.Case(app, "report", ("format", "html"));

        // 完了の通知に出た SHA-256、監査ログの最後のレポート作成の行、ファイルのハッシュが一致する。
        string sha = state["report"]!["sha256"]!.GetValue<string>();
        string path = state["report"]!["path"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(folder, "reports"), path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        string last = File.ReadAllLines(Path.Combine(folder, "audit.log")).Last(l => l.Contains("report.create", StringComparison.Ordinal));
        Assert.Contains(sha, last, StringComparison.Ordinal);
        Assert.Contains(sha, app.NotificationsText(), StringComparison.Ordinal);
    });
}
