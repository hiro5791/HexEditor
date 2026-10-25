using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// フォレンジックのケース (FOR-03)、監査ログ (FOR-04)、日時の表示 (FOR-05)。ダイアログは UI オートメーションで操作し、結果は命令 tcCase の状態
/// (証拠パネル・監査ログパネルの行を含む) で読む。ファイルシステムブラウザは別の担当なので、日時の表示は証拠パネルと監査ログパネルで確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ForensicsCaseTests
{

    /// <summary>監査ログはアプリが追記している最中でも読む (共有して開く。File.ReadAllText は書き込み中に共有違反になる)。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    internal static JsonObject En => new() { ["culture"] = "en-US", ["timeZone"] = "UTC" };

    internal static Task<JsonObject> Case(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcCase", ToolsCalcTests.Args(action, values), TimeSpan.FromMinutes(2));

    internal static Task<JsonObject> Carve(AppSession app, string action, params (string Key, object? Value)[] values) => app.SendAsync("tcCarve", ToolsCalcTests.Args(action, values));

    private static Task<AppSession> StartAsync(UiTestContext ctx, params string[] files) =>
        ctx.StartAsync(new AppOptions { Files = files, Hooks = En, WaitForEditor = files.Length > 0 });

    internal static Task SetPickerAsync(AppSession app, string path) =>
        app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray(path) } });

    private static async Task ExitAsync(AppSession app)
    {
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(ExitTimeout);
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task CreateCaseAsync(AppSession app, string folder, string timeZone = "Tokyo Standard Time") =>
        await Case(app, "create", ("folder", folder), ("number", "C-0001"), ("name", "テストケース"), ("investigator", "tester"), ("timeZone", timeZone));

    /// <summary>連続して読んだ 2 回の内容が同じになるまで読み直す (最大 60 秒)。</summary>
    private static async Task<byte[]> ReadStableDeviceAsync(string path, int length)
    {
        byte[] previous = CiPrivileged.ReadDevice(path, length);
        DateTime limit = DateTime.UtcNow + UiTest.Scaled(TimeSpan.FromSeconds(60));
        while (true)
        {
            await Task.Delay(500);
            byte[] current = CiPrivileged.ReadDevice(path, length);
            if (current.AsSpan().SequenceEqual(previous))
            {
                return current;
            }

            Assert.True(DateTime.UtcNow < limit, "The virtual disk content kept changing.");
            previous = current;
        }
    }

    private static async Task WaitForHashesAsync(AppSession app, int evidenceCount)
    {
        await app.WaitUntilAsync(async () =>
        {
            JsonObject state = await Case(app, "state");
            return state["evidence"] is JsonArray list && list.Count == evidenceCount && list.All(e => e!["hashState"]!.GetValue<string>() == "Computed");
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the hashes of the evidence");
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-03-01")]
    public Task New_case_dialog_creates_case_json_audit_log_and_folders() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case1");
        Directory.CreateDirectory(folder);
        AppSession app = await StartAsync(ctx);

        // 1. 「フォレンジック > ケース > 新しいケース」。2. ケース番号が空のままでは作成ボタンが無効。
        await app.CommandAsync("Command_CaseNew");
        await app.WaitForAsync("CaseNewDialog");
        await app.UiaSetValueAsync("Case_Name", "テストケース");
        await app.UiaSetValueAsync("Case_Folder", folder);
        Assert.False((await app.WaitForAsync("PrimaryButton")).IsEnabled);

        // 3. ケース番号、ケース名、調査者名、フォルダを入れて作成する。
        await app.UiaSetValueAsync("Case_Number", "C-0001");
        await app.UiaSetValueAsync("Case_Investigator", "tester");
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("PrimaryButton")).IsEnabled, TimeSpan.FromSeconds(10), "the Create button");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () => (await Case(app, "state"))["caseOpen"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)), "the case to open");

        // 4. case1 の中身と case.json。
        Assert.True(File.Exists(Path.Combine(folder, "case.json")));
        Assert.True(File.Exists(Path.Combine(folder, "audit.log")));
        Assert.True(Directory.Exists(Path.Combine(folder, "exports")));
        Assert.True(Directory.Exists(Path.Combine(folder, "reports")));
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "case.json")));
        System.Text.Json.JsonElement caseInfo = json.RootElement.GetProperty("case");
        Assert.Equal("C-0001", caseInfo.GetProperty("number").GetString());
        Assert.Equal("テストケース", caseInfo.GetProperty("name").GetString());
        Assert.Equal("tester", caseInfo.GetProperty("investigator").GetString());
        Assert.False(string.IsNullOrEmpty(caseInfo.GetProperty("timeZoneId").GetString()));

        // 5. ウィンドウのタイトルとステータスバーにケース名。
        JsonObject state = await Case(app, "state");
        Assert.Contains("テストケース", state["title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(state["statusCaseVisible"]!.GetValue<bool>());
        Assert.Contains("テストケース", state["statusCase"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("テストケース", await app.UiaNameAsync("Status_Case"), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-03-03")]
    public Task Reopening_the_case_restores_the_evidence_list_exactly() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string random = ctx.CopyTestData("TD-RANDOM-16M", "random.bin");
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq, random], Hooks = En });
        await CreateCaseAsync(app, folder);
        for (int i = 0; i < 2; i++)
        {
            await app.SendAsync("selectTab", new JsonObject { ["index"] = i });
            await Case(app, "addEvidence", ("description", i == 0 ? "証拠 1" : "証拠 2"), ("hashes", new[] { "SHA-256", "MD5" }));
        }

        await WaitForHashesAsync(app, 2);
        await Case(app, "panels");
        JsonObject before = await Case(app, "state");
        JsonArray rowsBefore = before["evidenceRows"]!.AsArray();
        Assert.Equal(2, rowsBefore.Count);

        // 1. ケースを閉じる。2. アプリを再起動し、「ケースを開く」で同じフォルダを開く。
        await app.CommandAsync("Command_CaseClose");
        Assert.False((await Case(app, "state"))["caseOpen"]!.GetValue<bool>());
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = En, WaitForEditor = false });
        await SetPickerAsync(again, folder);
        await again.CommandAsync("Command_CaseOpen");
        await again.WaitUntilAsync(async () => (await Case(again, "state"))["caseOpen"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)), "the case to open");
        await Case(again, "panels");

        // 3. 証拠パネルの全行が同じ (証拠番号、説明、種類、パス、容量、ハッシュ、登録日時)。
        JsonObject after = await Case(again, "state");
        Assert.Equal(rowsBefore.ToJsonString(), after["evidenceRows"]!.AsArray().ToJsonString());
        Assert.Equal("証拠 1", after["evidenceRows"]![0]![1]!.GetValue<string>());
        Assert.Equal(before["evidence"]!.ToJsonString(), after["evidence"]!.ToJsonString());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-03-04")]
    public Task Removing_evidence_keeps_it_in_case_json_as_excluded_and_logs_it() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string before = Sha256(seq);
        AppSession app = await StartAsync(ctx, seq);
        await CreateCaseAsync(app, folder);
        await Case(app, "addEvidence", ("hashes", Array.Empty<string>()));
        await Case(app, "panels");
        JsonObject added = await Case(app, "state");
        Assert.Equal("E001", added["evidence"]![0]!["id"]!.GetValue<string>());

        // 1. 証拠パネルで E001 を選び、Delete (一覧から外す)。
        await Case(app, "select", ("index", 0));
        JsonObject state = await Case(app, "removeSelected");

        // 2. 一覧から消え、case.json に除外として残り、監査ログに記録される。
        Assert.Empty(state["evidence"]!.AsArray());
        Assert.Empty(state["evidenceRows"]!.AsArray());
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "case.json")));
        System.Text.Json.JsonElement e = json.RootElement.GetProperty("evidence")[0];
        Assert.Equal("E001", e.GetProperty("id").GetString());
        Assert.True(e.GetProperty("excluded").GetBoolean());
        string last = File.ReadAllLines(Path.Combine(folder, "audit.log"))[^1];
        Assert.Contains("evidence.exclude", last, StringComparison.Ordinal);
        Assert.Contains("E001", last, StringComparison.Ordinal);

        // 3. ファイルのハッシュは変わらない。
        Assert.Equal(before, Sha256(seq));
    });

    [CiPrivilegedFact]
    [Trait(UiTest.TC, "TC-FOR-03-02")]
    public Task Adding_a_disk_shows_model_serial_capacity_and_hashes_and_refuses_a_duplicate() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = En, RealElevation = true, WaitForEditor = false });
        await CreateCaseAsync(app, folder);
        JsonObject disk = (await app.SendAsync("enumDevices"))["disks"]!.AsArray().Select(d => d!.AsObject()).Single(d => d["number"]!.GetValue<int>() == CiPrivileged.VhdDisk);
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = CiPrivileged.VhdDiskPath, ["readOnly"] = true });
        long length = opened["length"]!.GetValue<long>();
        // 直前のテスト (イメージの複製) で書いたディスクは、Windows が遅れてメタデータを書き戻すことがある。内容が止まるまで待ってから期待値を作る。
        byte[] all = await ReadStableDeviceAsync(CiPrivileged.VhdDiskPath, (int)length);

        // 1. 「証拠を追加」(SHA-256 と MD5)。2. ハッシュ計算の完了を待つ。3. 行の各列。
        await Case(app, "addEvidence", ("hashes", new[] { "SHA-256", "MD5" }));
        await WaitForHashesAsync(app, 1);
        JsonObject evidence = (await Case(app, "state"))["evidence"]![0]!.AsObject();
        Assert.Equal("E001", evidence["id"]!.GetValue<string>());
        Assert.Equal(disk["model"]!.GetValue<string>(), evidence["model"]!.GetValue<string>());
        Assert.Equal(length, evidence["capacity"]!.GetValue<long>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(all)), evidence["hashes"]!.AsArray().Single(h => h!["algorithm"]!.GetValue<string>() == "SHA-256")!["value"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(all)), evidence["hashes"]!.AsArray().Single(h => h!["algorithm"]!.GetValue<string>() == "MD5")!["value"]!.GetValue<string>());

        // 4. もう一度追加しようとすると、既存の証拠番号 E001 を示して止まる。
        await Case(app, "addEvidence", ("hashes", Array.Empty<string>()));
        Assert.Single((await Case(app, "state"))["evidence"]!.AsArray());
        Assert.Contains("E001", app.NotificationsText(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-04-04")]
    public Task Audit_log_write_failure_shows_a_persistent_bar_and_disables_write_commands() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string other = ctx.CopyTestData("TD-BYTES-256", "bytes.bin");
        AppSession app = await StartAsync(ctx, seq);
        await Case(app, "auditFailing", ("on", false));
        await CreateCaseAsync(app, folder);
        Assert.True((await Case(app, "state"))["writesAllowed"]!.GetValue<bool>());

        // 前提: 次の監査ログへの書き込みを「空き容量不足」にして、ドキュメントを 1 つ開く (監査ログへの記録が発生する)。
        await Case(app, "auditFailing", ("on", true));
        await app.OpenAsync(other);
        await app.WaitUntilAsync(async () => !(await Case(app, "state"))["writesAllowed"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)), "the audit fault");

        // 1〜2. エディタ上部の InfoBar は閉じるボタンがない。「監査ログに記録できません」。
        JsonObject state = await Case(app, "state");
        Assert.True(state["auditFaultOpen"]!.GetValue<bool>());
        Assert.False(state["auditFaultClosable"]!.GetValue<bool>());
        Assert.Equal("Cannot write to the audit log", state["auditFaultTitle"]!.GetValue<string>());
        Assert.NotNull(app.Find("AuditFaultBar"));

        // 3. 書き込みを伴うコマンド (保存、安全な削除、切り出し) が無効になる。
        JsonArray commands = (await app.SendAsync("commands"))["items"]!.AsArray();
        foreach (string id in new[] { "file.save", "file.saveAs", "tools.files.shred", "forensics.carving.run", "forensics.report.create" })
        {
            JsonObject command = commands.Select(c => c!.AsObject()).Single(c => c["id"]!.GetValue<string>() == id);
            Assert.False(command["enabled"]!.GetValue<bool>(), id);
        }

        // 容量が戻って再試行すると、溜めた記録が書かれ、コマンドが戻る。
        await Case(app, "recover");
        await app.WaitUntilAsync(async () => (await Case(app, "state"))["writesAllowed"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)), "the recovery");
        Assert.False((await Case(app, "state"))["auditFaultOpen"]!.GetValue<bool>());
        Assert.Contains("document.open", File.ReadAllText(Path.Combine(folder, "audit.log")), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-05-03")]
    public Task Utc_display_mode_removes_the_local_columns_from_the_lists() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await StartAsync(ctx, seq);
        await CreateCaseAsync(app, folder);
        await Case(app, "addEvidence", ("hashes", Array.Empty<string>()));
        await Case(app, "panels");

        // 既定は UTC とローカルの両方 (列が並ぶ)。
        JsonObject both = await Case(app, "state");
        Assert.Contains(both["evidenceColumns"]!.AsArray(), c => c!.GetValue<string>().Contains("local", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(both["auditColumns"]!.AsArray(), c => c!.GetValue<string>().Contains("local", StringComparison.OrdinalIgnoreCase));

        // 1. 設定「日時の表示」を UTC にする。2. どの一覧にもローカルの列がなく、日時の値がすべて Z で終わる。
        JsonObject utc = await Case(app, "setTimeMode", ("mode", "utc"));
        await app.WaitUntilAsync(async () => !(await Case(app, "state"))["evidenceColumns"]!.AsArray().Any(c => c!.GetValue<string>().Contains("local", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10), "the evidence columns to update");
        utc = await Case(app, "state");
        Assert.DoesNotContain(utc["evidenceColumns"]!.AsArray(), c => c!.GetValue<string>().Contains("local", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(utc["auditColumns"]!.AsArray(), c => c!.GetValue<string>().Contains("local", StringComparison.OrdinalIgnoreCase));
        int evidenceTime = utc["evidenceColumns"]!.AsArray().ToList().FindIndex(c => c!.GetValue<string>().Contains("UTC", StringComparison.Ordinal));
        Assert.EndsWith("Z", utc["evidenceRows"]![0]![evidenceTime]!.GetValue<string>(), StringComparison.Ordinal);
        int auditTime = utc["auditColumns"]!.AsArray().ToList().FindIndex(c => c!.GetValue<string>().Contains("UTC", StringComparison.Ordinal));
        Assert.All(utc["auditRows"]!.AsArray(), row => Assert.EndsWith("Z", row![auditTime]!.GetValue<string>(), StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-05-02")]
    public Task Changing_the_case_time_zone_changes_the_local_times_but_not_utc() => UiTestContext.RunAsync(async ctx =>
    {
        // FAT の日時の「タイムゾーン推定」の計算は Core のテストで確かめる。ここでは、ケースのタイムゾーンを変えると、
        // ローカル時刻の列が変わることを証拠パネルで確かめる。
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await StartAsync(ctx, seq);
        await CreateCaseAsync(app, folder, "Tokyo Standard Time");
        await Case(app, "addEvidence", ("hashes", Array.Empty<string>()));
        await Case(app, "panels");
        JsonObject tokyo = await Case(app, "state");
        string[] columns = [.. tokyo["evidenceColumns"]!.AsArray().Select(c => c!.GetValue<string>())];
        int utcIndex = Array.FindIndex(columns, c => c.Contains("UTC", StringComparison.Ordinal)), localIndex = Array.FindIndex(columns, c => c.Contains("local", StringComparison.OrdinalIgnoreCase));
        string utcBefore = tokyo["evidenceRows"]![0]![utcIndex]!.GetValue<string>();
        string localTokyo = tokyo["evidenceRows"]![0]![localIndex]!.GetValue<string>();
        Assert.EndsWith("+09:00", localTokyo, StringComparison.Ordinal);

        JsonObject utc = await Case(app, "setZone", ("timeZone", "UTC"));
        await app.WaitUntilAsync(async () => (await Case(app, "state"))["evidenceRows"]![0]![localIndex]!.GetValue<string>().EndsWith("+00:00", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the local time to follow the case time zone");
        utc = await Case(app, "state");
        Assert.Equal(utcBefore, utc["evidenceRows"]![0]![utcIndex]!.GetValue<string>());
        Assert.Equal("UTC", utc["timeZoneId"]!.GetValue<string>());
        Assert.Contains("timezone", File.ReadAllText(Path.Combine(folder, "audit.log")), StringComparison.Ordinal);
    });
    /// <summary>
    /// FOR-04 の仕様 3・4: 保存とフォレンジックの設定の変更は常に、ジャンプ・ブックマークの追加は設定「閲覧操作も記録する」のときだけ記録される。
    /// </summary>
    [Fact]
    public Task Saves_settings_and_browsing_are_recorded_in_the_audit_log() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Directory.CreateDirectory(Path.Combine(ctx.Root, "case")).FullName;
        string memo = ctx.CopyTestData("TD-BYTES-256", "memo.bin");
        AppSession app = await StartAsync(ctx, memo);

        // メモのファイルに保存するため、ケースを開いても書き込み保護モードを自動で有効にしない。
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "forensics.case.autoWriteBlock", ["value"] = false });
        await CreateCaseAsync(app, folder);
        string log = Path.Combine(folder, "audit.log");

        // 閲覧操作は既定では記録しない。
        await app.CommandAsync("Command_GoEnd");
        await app.IdleAsync();
        Assert.DoesNotContain("browse.jump", ReadShared(log), StringComparison.Ordinal);

        // 設定を変えると記録され (settings.change)、ジャンプとブックマークの追加が記録される。
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "forensics.audit.logBrowsing", ["value"] = true });
        await app.WaitUntilAsync(() => Task.FromResult(ReadShared(log).Contains("forensics.audit.logBrowsing", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the setting change record");
        await app.CommandAsync("Command_GoStart");
        await app.CommandAsync("Command_ToggleBookmark");
        await app.WaitUntilAsync(() => Task.FromResult(ReadShared(log) is var text && text.Contains("browse.jump", StringComparison.Ordinal)
            && text.Contains("browse.bookmark", StringComparison.Ordinal)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the browsing records");

        // 保存は結果とともに記録される。
        await app.TypeAsync("AA");
        await app.CommandAsync("Command_Save");
        await app.WaitUntilAsync(() => Task.FromResult(ReadShared(log).Contains("write.save", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), "the save record");
        string save = ReadShared(log).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => l.Contains("write.save", StringComparison.Ordinal));
        Assert.Contains("memo.bin", save, StringComparison.Ordinal);
        Assert.Contains("success", save, StringComparison.OrdinalIgnoreCase);
    });
}
