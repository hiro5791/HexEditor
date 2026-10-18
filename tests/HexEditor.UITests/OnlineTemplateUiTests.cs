using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>
/// オンラインのテンプレート (07 の TPL-36)。リポジトリはテスト用のサーバー (<see cref="FakeTemplateRepository"/>。127.0.0.1 で TD-TPL-REPO を
/// 配信し、受け取った要求を記録する) で、テスト用のビルドはその公開鍵を公式の鍵の代わりに使う。インターネットには接続しない。
/// TC-TPL-36-04 の「通信が一切ない」は、テスト用のサーバーが要求を 1 件も受けないことと、アプリの通信の入口 (UI-58 の NetworkClient。
/// アプリの通信はここを通るほかにない: TC-UI-58-04 の静的検査) の記録が 0 件であることで確かめる (ETW の記録は使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class OnlineTemplateUiTests
{
    private static Task<JsonObject> OnlineAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("tplonline", request, TimeSpan.FromSeconds(120));
    }

    private static string RepoFolder(UiTestContext ctx) => Path.GetDirectoryName(ctx.TestData("TD-TPL-REPO"))!;

    /// <summary>管理画面の「オンライン」を開き、読み込みが終わるまで待つ。</summary>
    private static async Task<JsonObject> OpenOnlineAsync(AppSession app)
    {
        try
        {
            await OnlineAsync(app, "open");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message + "\n" + string.Join("\n", (await app.LogAsync()).TakeLast(40)), ex);
        }

        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await OnlineAsync(app, "state"))["open"]!.GetValue<bool>(), Wait, "the online templates");
        await OnlineAsync(app, "wait");
        return await OnlineAsync(app, "state");
    }

    private static JsonObject HxTest(JsonObject state) =>
        state["rows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["id"]!.GetValue<string>() == TestDataCatalog.TplRepo.TemplateId);

    private static async Task<AppSession> StartAsync(UiTestContext ctx, FakeTemplateRepository repo, string profile, string? frozenTime = null,
        bool downloads = true, params string[] files)
    {
        ViewOps.WriteSettings(profile, repo.Settings(downloads));
        var hooks = frozenTime is null ? null : new JsonObject { ["frozenTime"] = frozenTime };
        return await ctx.StartAsync(new AppOptions { Profile = profile, Files = files, Hooks = hooks, WaitForEditor = files.Length > 0 });
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-36-01")]
    public Task Search_and_install_reflects_in_the_list_and_auto_apply() => UiTestContext.RunAsync(async ctx =>
    {
        await using var repo = new FakeTemplateRepository(RepoFolder(ctx));
        string profile = ctx.NewProfile();
        AppSession app = await StartAsync(ctx, repo, profile);

        // 手順 1: 検索すると表示名・説明・版・ライセンスが出る。
        JsonObject state = await OpenOnlineAsync(app);
        await OnlineAsync(app, "search", new JsonObject { ["text"] = "hxtest" });
        state = await OnlineAsync(app, "state");
        JsonObject row = HxTest(state);
        Assert.Equal(TestDataCatalog.TplRepo.DisplayName, row["name"]!.GetValue<string>());
        Assert.Equal("Test format for the online repository", row["description"]!.GetValue<string>());
        Assert.Equal("1.0.0", row["version"]!.GetValue<string>());
        Assert.Equal("MIT", row["license"]!.GetValue<string>());
        Assert.False(row["unsigned"]!.GetValue<bool>());

        // 手順 2〜3: インストールすると、管理画面の一覧に出所「オンライン」で出て、ファイルは templates-online/ にある。
        Assert.True((await OnlineAsync(app, "install", new JsonObject { ["id"] = "hxtest" }))["ok"]!.GetValue<bool>());
        state = await OnlineAsync(app, "state");
        string folder = state["folder"]!.GetValue<string>();
        Assert.Equal(Path.Combine(profile, "templates-online"), folder, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(folder, "hxtest.hxt")));
        JsonObject manager = [];
        await app.WaitUntilAsync(async () => (manager = await TemplateAsync(app, "manager"))["rows"]!.AsArray()
            .Any(r => r!["name"]!.GetValue<string>() == TestDataCatalog.TplRepo.DisplayName), Wait, "the template in the manager list");
        JsonObject listed = manager["rows"]!.AsArray().Select(r => r!.AsObject()).First(r => r["name"]!.GetValue<string>() == TestDataCatalog.TplRepo.DisplayName);
        Assert.Equal("Online", listed["origin"]!.GetValue<string>());

        // 手順 4: TD-TPL-REPO-DATA を開くと、再起動せずに提案の InfoBar が出る。
        await app.OpenAsync(ctx.TestData("TD-TPL-REPO-DATA"));
        await app.WaitForNotificationAsync(m => m.Contains(TestDataCatalog.TplRepo.DisplayName, StringComparison.Ordinal), "the template suggestion");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-36-02")]
    public Task New_version_is_announced_after_a_day_and_can_be_updated() => UiTestContext.RunAsync(async ctx =>
    {
        await using var repo = new FakeTemplateRepository(RepoFolder(ctx));
        string profile = ctx.NewProfile();
        const string Day0 = "2026-10-01T09:00:00Z";
        const string Day1 = "2026-10-02T10:00:00Z"; // 25 時間後
        AppSession app = await StartAsync(ctx, repo, profile, Day0);
        await OpenOnlineAsync(app);
        Assert.True((await OnlineAsync(app, "install", new JsonObject { ["id"] = "hxtest" }))["ok"]!.GetValue<bool>());

        // 起動時の確認 (前回の確認から 1 日たっていない) では知らせない。
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        repo.Map["/index.json"] = "index-v1.1.json";
        repo.Map["/index.json.sig"] = "index-v1.1.json.sig";

        // 手順 1〜2: 25 時間後に起動し直すと、新しい版の InfoBar が出る (自動では更新しない)。
        AppSession again = await StartAsync(ctx, repo, profile, Day1);
        await again.WaitForNotificationAsync(m => m.Contains("New versions", StringComparison.Ordinal) && m.Contains("1.1.0", StringComparison.Ordinal),
            "the update InfoBar");
        Assert.Equal("1.0.0", (await OnlineAsync(again, "state"))["installed"]!.AsArray().Single()!["version"]!.GetValue<string>());
        Assert.True((await again.SendAsync("notificationAction", new JsonObject { ["label"] = "Update" }))["invoked"]!.GetValue<bool>());

        // 手順 3: 版が 1.1.0 になる。
        await again.WaitUntilAsync(async () => (await OnlineAsync(again, "state"))["installed"]!.AsArray().Single()!["version"]!.GetValue<string>() == "1.1.0",
            Wait, "version 1.1.0");
        await OpenOnlineAsync(again);
        JsonObject manager = [];
        await again.WaitUntilAsync(async () => (manager = await TemplateAsync(again, "manager"))["rows"]!.AsArray()
            .Any(r => r!["name"]!.GetValue<string>() == TestDataCatalog.TplRepo.DisplayName && r["version"]?.GetValue<string>() == "1.1.0"), Wait, "1.1.0 in the manager");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-36-04")]
    public Task Turning_downloads_off_stops_all_communication() => UiTestContext.RunAsync(async ctx =>
    {
        await using var repo = new FakeTemplateRepository(RepoFolder(ctx));
        string profile = ctx.NewProfile();

        // 前提: 1.0.0 をインストール済み (同じ手順で入れてから、通信をオフにして起動し直す)。サーバーには 1.1.0 がある。
        AppSession first = await StartAsync(ctx, repo, profile, "2026-10-01T09:00:00Z");
        await OpenOnlineAsync(first);
        Assert.True((await OnlineAsync(first, "install", new JsonObject { ["id"] = "hxtest" }))["ok"]!.GetValue<bool>());
        await first.SendAsync("exit");
        await first.WaitForExitAsync(TimeSpan.FromSeconds(30));
        repo.Map["/index.json"] = "index-v1.1.json";
        repo.Map["/index.json.sig"] = "index-v1.1.json.sig";
        int before = repo.Requests.Count;

        // 手順 1: 25 時間後に起動し、「オンライン」を開く。
        string png = ctx.TestData("TD-PNG");
        AppSession app = await StartAsync(ctx, repo, profile, "2026-10-02T10:00:00Z", downloads: false, png);
        JsonObject state = await OpenOnlineAsync(app);
        Assert.True(state["disabled"]!.GetValue<bool>());
        Assert.Equal("Online templates are turned off.", state["status"]!.GetValue<string>());
        await app.WaitForAsync("TplOnline_OpenSettings");

        // 手順 2: PNG に「PNG image」を、インストール済みの HXTest format も適用できる。
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = "bundled:templates/png.hxt" });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = Path.Combine(profile, "templates-online", "hxtest.hxt") });
        await WaitTemplatesAsync(app, applications: 2);

        // 手順 3: 通信の記録がない (テスト用のサーバーへの要求も、アプリの通信の入口の記録も 0 件)。
        await app.SendAsync("execute", new JsonObject { ["id"] = "templates.online.checkUpdates" });
        await app.IdleAsync();
        Assert.Equal(before, repo.Requests.Count);
        Assert.Equal(0, (await OnlineAsync(app, "requests"))["count"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-36-05")]
    public Task Requests_do_not_contain_the_open_file_name_or_content() => UiTestContext.RunAsync(async ctx =>
    {
        await using var repo = new FakeTemplateRepository(RepoFolder(ctx));
        string profile = ctx.NewProfile();
        string secret = ctx.TestData("TD-TPL-SECRET");
        AppSession app = await StartAsync(ctx, repo, profile, "2026-10-01T09:00:00Z", downloads: true, secret);

        // 手順 1: 検索・インストール・更新の確認。
        await OpenOnlineAsync(app);
        await OnlineAsync(app, "search", new JsonObject { ["text"] = "hxtest" });
        Assert.True((await OnlineAsync(app, "install", new JsonObject { ["id"] = "hxtest" }))["ok"]!.GetValue<bool>());
        await app.SendAsync("execute", new JsonObject { ["id"] = "templates.online.checkUpdates" });
        await app.WaitForNotificationAsync(m => m.Contains("up to date", StringComparison.Ordinal) || m.Contains("New versions", StringComparison.Ordinal),
            "the update check result");

        // 手順 2: どの要求にも、目印・ファイル名・パス・内容の一部 (16 バイト以上) が含まれない。
        Assert.NotEmpty(repo.Requests);
        byte[] content = await File.ReadAllBytesAsync(secret);
        string[] forbidden =
        [
            TestDataCatalog.TplSecretMarker,
            Path.GetFileName(secret),
            secret,
            Path.GetDirectoryName(secret)!,
            Uri.EscapeDataString(Path.GetFileName(secret)),
        ];
        foreach (string request in repo.Requests)
        {
            foreach (string f in forbidden)
            {
                Assert.DoesNotContain(f, request, StringComparison.OrdinalIgnoreCase);
            }

            byte[] bytes = Encoding.Latin1.GetBytes(request);
            for (int i = 0; i + 16 <= content.Length; i += 1)
            {
                Assert.True(bytes.AsSpan().IndexOf(content.AsSpan(i, 16)) < 0, $"the request contains the file content at {i}");
            }
        }
    });
}
