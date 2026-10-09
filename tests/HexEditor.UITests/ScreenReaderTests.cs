using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// スクリーンリーダー対応の細部 (09 の UI-50、UI-51): 列の切り替え・長時間処理の完了の読み上げ、読み上げ文の桁の区切り、
/// 挿入モードの語の重複、読み取り専用の切り替えでの ControlType、文書名の追従、読み込み中のバイトの Text パターンでの表し方。
/// 読み上げは、アプリが通知イベントで送った文の記録 (テスト用の命令 announcements / windowAnnouncements) で確かめる。英語表示。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ScreenReaderTests
{
    private static async Task<JsonArray> AnnouncementsAsync(AppSession app, bool clear = true) =>
        (await app.SendAsync("announcements", new JsonObject { ["clear"] = clear }))["items"]!.AsArray();

    private static async Task<string> WaitForAnnouncementAsync(AppSession app, string id)
    {
        string? found = null;
        await app.WaitUntilAsync(async () =>
        {
            found = (await AnnouncementsAsync(app, clear: false)).LastOrDefault(i => i!["id"]!.GetValue<string>() == id)?["text"]?.GetValue<string>();
            return found is not null;
        }, TimeSpan.FromSeconds(5), $"the {id} announcement");
        await AnnouncementsAsync(app);
        return found!;
    }

    [Fact]
    public Task Column_switch_is_announced_and_digits_are_spelled() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.FocusAsync("HexView");
        await GoToAsync(app, "0x1F");
        await app.FocusAsync("HexView");
        await Task.Delay(300);
        await AnnouncementsAsync(app);

        // Tab で Hex 列 → テキスト列: 列の名前だけを読む (UI-51 の仕様 5)。
        await app.KeyAsync("Tab");
        Assert.Equal("Text column", await WaitForAnnouncementAsync(app, "HexViewColumn"));
        await app.KeyAsync("Tab");
        Assert.Equal("Hex column", await WaitForAnnouncementAsync(app, "HexViewColumn"));

        // 既定 (full) のカーソルの読み上げは、オフセットと値の 16 進の桁の間に区切りを入れる (UI-51 の仕様 2)。Value には入れない。
        await app.KeyAsync("Right");
        string spoken = await WaitForAnnouncementAsync(app, "HexViewCursor");
        Assert.Contains("0 x 0 0 0 0 0 0 2 0", spoken);
        Assert.Contains("value 2 0", spoken);
        AutomationElement view = await app.WaitForAsync("HexView");
        Assert.Contains("0x00000020", view.Patterns.Value.Pattern.Value.Value);
    });

    [Fact]
    public Task Insert_mode_and_inserted_byte_are_not_read_as_the_same_word() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await app.FocusAsync("HexView");
        await GoToAsync(app, "0x10");
        await app.FocusAsync("HexView");
        await app.KeyAsync("Insert");
        await app.TypeAsync("41");
        await app.IdleAsync();
        await Task.Delay(300);
        await AnnouncementsAsync(app);

        // 入力の後のカーソルは 0x11。← で挿入したバイト (0x10) に戻る。
        await app.KeyAsync("Left");
        string spoken = await WaitForAnnouncementAsync(app, "HexViewCursor");
        Assert.Equal(1, Regex.Count(spoken, @"\binsert mode\b"));
        Assert.Equal(1, Regex.Count(spoken, @"\binserted\b"));
    });

    [Fact]
    public Task Read_only_switch_changes_the_control_type_and_is_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        AutomationElement view = await app.WaitForAsync("HexView");
        Assert.Equal(ControlType.Edit, view.Properties.ControlType.Value);
        Assert.False(view.Patterns.Value.Pattern.IsReadOnly.Value);

        await app.CommandAsync("Command_ReadOnly");
        await app.IdleAsync();
        view = await app.WaitForAsync("HexView");
        Assert.Equal(ControlType.Document, view.Properties.ControlType.Value);
        Assert.True(view.Patterns.Value.Pattern.IsReadOnly.Value);
        Assert.Contains(await AnnouncementsAsync(app), i => i!["id"]!.GetValue<string>() == "HexViewMode" && i["text"]!.GetValue<string>() == "Read-only");
    });

    [Fact]
    public Task Name_follows_the_document_name_after_save_as() => UiTestContext.RunAsync(async ctx =>
    {
        string source = ctx.CopyTestData("TD-BYTES-256");
        string target = Path.Combine(Path.GetDirectoryName(source)!, "renamed-for-a11y.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["savePicker"] = target } });
        AutomationElement view = await app.WaitForAsync("HexView");
        Assert.Equal(Path.GetFileName(source), view.Properties.Name.Value);

        await app.CommandAsync("Command_SaveAs");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(target)), TimeSpan.FromSeconds(10), "the saved file");
        await app.WaitUntilAsync(async () => AppSession.NameOf(await app.WaitForAsync("HexView")) == "renamed-for-a11y.bin",
            TimeSpan.FromSeconds(5), "the new name of the hex view");
    });

    [Fact]
    public Task Text_pattern_shows_loading_bytes_as_question_marks() => UiTestContext.RunAsync(async ctx =>
    {
        // 0x10000 から後の読み込みを 1 回ごとに 5 秒遅らせる。画面の仮表示 (·· や空白) の代わりに ?? / ? を返す (UI-50 の「エラー」)。
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-SEQ-1M.bin", ["delayMs"] = 5000, ["delayFromOffset"] = 65536 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")], Hooks = hooks });
        await app.GoToAsync(0x20000);
        AutomationElement view = await app.WaitForAsync("HexView");
        await app.EventuallyAsync(() =>
        {
            string text = view.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            Assert.Contains("?? ?? ??", text);
            Assert.DoesNotContain("··", text);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Completion_of_a_long_operation_is_announced() => UiTestContext.RunAsync(async ctx =>
    {
        // 遅いデータソース (読み込み 1 回ごとに 150 ms) の検索は 0.5 秒を超える長時間処理になる。終わったら読み上げる (UI-51 の仕様 5)。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-16M", ["length"] = 16 * 1024 * 1024, ["content"] = "zero", ["delayMs"] = 150 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await app.SendAsync("windowAnnouncements", new JsonObject { ["clear"] = true });
        await OperationsTests.OpenFindAsync(app, "DE AD BE EF 01");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.SendAsync("windowAnnouncements"))["items"]!.AsArray()
            .Any(i => i!["id"]!.GetValue<string>() == "Operation" && i["text"]!.GetValue<string>() == "Find completed"),
            TimeSpan.FromSeconds(30), "the completion announcement");
    });
}
