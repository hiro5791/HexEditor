using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.PackagingTests;

namespace HexEditor.UITests;

/// <summary>
/// 翻訳者向けのツールチップ (09 の UI-41 の仕様 5) のうち、メインウィンドウの XAML のメニュー (TC-UI-41-03。PackagingTests) 以外:
/// 設定画面・コードで作るメニュー・後から開くダイアログ、元からツールチップがある要素 (キーを後ろに足す)、「翻訳者向け」の見出し。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class StringKeyTipsTests
{
    private const string Enabled = "{\"$schemaVersion\": 1, \"i18n.showStringKeys\": true}";

    private static async Task<string?> ToolTipAsync(AppSession app, string id) =>
        (await app.SendAsync("element", new JsonObject { ["id"] = id }))["toolTip"]?.GetValue<string>();

    /// <summary>キーは画面を定期的に見直して付けるので、付くまで待つ。</summary>
    private static async Task<string> WaitForToolTipAsync(AppSession app, string id, string contains)
    {
        string? tip = null;
        await app.WaitUntilAsync(async () => (tip = await ToolTipAsync(app, id))?.Contains(contains, StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(5), $"the key {contains} in the tooltip of {id}");
        return tip!;
    }

    [Fact]
    public Task Existing_tooltips_keep_their_text_and_get_the_key_appended() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = await ProfileWithSettings(ctx, Enabled), Files = [ctx.TestData("TD-SEQ-1M")] });
        string tip = await WaitForToolTipAsync(app, "Status_Notifications", "Status_Notifications.ToolTipService.ToolTip");
        string[] lines = tip.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(UiHelpers.LoadResw("en")["Status_Notifications.ToolTipService.ToolTip"], lines[0]);

        // コードで作るメニューの項目 (表示 > ズーム) は、表示している文字列のキー。
        Assert.Contains("Menu_View_ZoomIn.Text", (await MenuItemAsync(app, "Command_ZoomIn"))["toolTip"]!.GetValue<string>());

        // 設定が無効なら付けない。
        AppSession plain = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-SEQ-1M")] });
        await Task.Delay(1000);
        Assert.DoesNotContain("Status_Notifications.", await ToolTipAsync(plain, "Status_Notifications") ?? string.Empty);
    });

    [Fact]
    public Task Settings_page_and_dialogs_opened_later_get_keys() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = await ProfileWithSettings(ctx, Enabled), Files = [ctx.TestData("TD-SEQ-1M")] });

        // 設定画面 (起動の後に開く): XAML の要素 (検索欄) と、コードで作る項目の名前。「言語」の「翻訳者向け」の見出し。
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "language" });
        await WaitForToolTipAsync(app, "Settings_Search", "Settings_Search.");
        await WaitForToolTipAsync(app, "SettingName_i18n.showStringKeys", "Set_i18n_showStringKeys");
        JsonObject heading = await app.SendAsync("element", new JsonObject { ["id"] = "SettingsGroup_translators" });
        Assert.Equal(UiHelpers.LoadResw("en")["SetGroup_translators"], heading["text"]!.GetValue<string>());

        // 後から開く、コードで作るダイアログ (翻訳の誤りを報告) の検索欄。500 件を超える一覧には、絞り込みの案内を出す。
        await app.CommandAsync("Command_ReportTranslation");
        await app.WaitForAsync("TranslationReport_Search");
        await WaitForToolTipAsync(app, "TranslationReport_Search", "TranslationReport_SearchPlaceholder");
        JsonObject truncated = await app.SendAsync("element", new JsonObject { ["id"] = "TranslationReport_Truncated" });
        Assert.Equal("Visible", truncated["visibility"]!.GetValue<string>());
        Assert.StartsWith("Showing the first 500 of ", truncated["text"]!.GetValue<string>(), StringComparison.Ordinal);
    });
}
