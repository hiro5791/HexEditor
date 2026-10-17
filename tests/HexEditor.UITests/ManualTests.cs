using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ヘルプ > ドキュメントで開くマニュアルのページ (09 の UI-40 の仕様 1、UI-59)。ブラウザは開かず、開こうとした URL をログで確かめる
/// (テスト方針 7.2「外部の起動の記録」)。ネットワークの状態は --test-hooks の networkAvailable で決める (実際の通信はしない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ManualTests
{
    private const string Online = "https://hiro5791.github.io/HexEditor/";

    private static async Task<string> LaunchedAsync(AppSession app) =>
        (await app.WaitForLogAsync(l => l.Contains("Test hooks: launch ", StringComparison.Ordinal), "the manual page"))
            .Split("Test hooks: launch ", 2)[1].Trim();

    [Theory]
    [Trait(UiTest.TC, "TC-UI-59-06")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("de", "en")]
    public Task Documentation_opens_the_online_page_of_the_display_language(string uiLanguage, string manualLanguage) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = uiLanguage, Hooks = new JsonObject { ["networkAvailable"] = true } });
        await app.SendAsync("execute", new JsonObject { ["id"] = "help.documentation" });
        Assert.Equal($"{Online}{manualLanguage}/index.html", await LaunchedAsync(app));
        Assert.False(await app.IsShownAsync("OpenUrlDialog"));
    });

    [Theory]
    [Trait(UiTest.TC, "TC-UI-59-06")]
    [InlineData(true, true, "en", "en")]
    [InlineData(false, false, "ja", "ja")]
    [InlineData(false, false, "de", "en")]
    public Task Documentation_opens_the_bundled_page_offline(bool offlineMode, bool networkAvailable, string uiLanguage, string manualLanguage) =>
        UiTestContext.RunAsync(async ctx =>
        {
            string profile = await PackagingTests.ProfileWithSettings(ctx, $"{{\"$schemaVersion\": 1, \"network.offline\": {(offlineMode ? "true" : "false")}}}");
            AppSession app = await ctx.StartAsync(new AppOptions
            {
                Profile = profile,
                UiLanguage = uiLanguage,
                Hooks = new JsonObject { ["networkAvailable"] = networkAvailable },
            });
            await app.CommandAsync("Command_Documentation");
            var page = new Uri(await LaunchedAsync(app));
            Assert.True(page.IsFile, page.AbsoluteUri);
            Assert.EndsWith($"/{manualLanguage}/index.html", page.AbsolutePath, StringComparison.Ordinal);
            Assert.True(File.Exists(page.LocalPath), page.LocalPath);
            Assert.Contains("<html lang=\"" + manualLanguage + "\"", File.ReadAllText(page.LocalPath), StringComparison.Ordinal);

            // 同梱のページを開くときは URL の確認 (UI-58 の仕様 3) を出さない (ネットワークを使わないため)。
            Assert.False(await app.IsShownAsync("OpenUrlDialog"));
        });
}
