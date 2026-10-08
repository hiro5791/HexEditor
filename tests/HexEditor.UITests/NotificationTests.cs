using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FlaUI.Core.Definitions;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>通知 (UI-36) の読み上げとトースト通知。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class NotificationTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-UI-36-03")]
    public Task InfoBar_is_announced_once() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-READONLY: TD-SEQ-1M の先頭 1 KiB に読み取り専用属性を付けたもの。
        byte[] data = new byte[1024];
        TestDataCatalog.Expected("TD-SEQ-1M", 0, data);
        string path = ctx.WriteFile("TD-READONLY.bin", data);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
            await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "FileAttribute" });
            var received = new ConcurrentQueue<string>();
            using var notifications = app.Window.RegisterNotificationEvent(TreeScope.Subtree,
                (_, _, _, text, activityId) => received.Enqueue($"{activityId}|{text}"));

            // 1. Hex ビューで 0 を入力すると、読み取り専用の InfoBar が出る。
            await app.TypeAsync("0");
            JsonObject notice = await app.WaitForNotificationAsync(_ => true, "the read-only notice");
            string message = notice["message"]!.GetValue<string>();

            // 2. 1 秒以内に、InfoBar の文言を含む通知イベントが 1 回。
            await Task.Delay(1000);
            Assert.Single(received, r => r.Contains(message, StringComparison.Ordinal));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-36-05")]
    public Task Long_operation_finished_while_inactive_shows_a_toast() => UiTestContext.RunAsync(async ctx =>
    {
        // 他のアプリを前面にする代わりに、ウィンドウが非アクティブになったときと同じ扱いにする (テスト用の命令)。テスト用のビルドは
        // 利用者の画面にトーストを出さず、ログに書く (ToastNotifier)。TD-UI-SPARSE-10G の代わりに遅い仮想のデータソース。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Profile = ctx.NewProfile() });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-24G", ["length"] = 24L << 30, ["content"] = "zero", ["delayMs"] = 8 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        // 1〜2. すべて検索を始め、別のアプリを使う (非アクティブ)。
        await SearchResultsTests.OpenFindAsync(app, 0, "DE AD BE EF");
        await SearchResultsTests.FindAllAsync(app);
        await app.SendAsync("setActive", new JsonObject { ["active"] = false });

        // 3. 検索が終わると、完了のトースト通知が 1 件。
        await SearchResultsTests.WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results", 120);
        await Task.Delay(1000);
        IReadOnlyList<string> log = await app.LogAsync();
        Assert.Single(log, l => l.Contains("Toast (not shown in test builds): Find all", StringComparison.Ordinal));
    });
}
