using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>読み取り専用で開く (ENG-14)、再読み込みと変更の破棄 (ENG-18)、外部変更の検知と再読み込み・マージ (ENG-19)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ExternalChangeTests
{
    private static async Task<JsonObject> ExternalNoticeAsync(AppSession app, string text) =>
        await app.WaitForNotificationAsync(m => m.Contains(text, StringComparison.Ordinal), $"the notice '{text}'");

    private static int ExternalNotices(IReadOnlyList<JsonObject> notices) =>
        notices.Count(n => n["message"]!.GetValue<string>().Contains("changed by another app", StringComparison.Ordinal));

    private static async Task PressNoticeButtonAsync(AppSession app, string name)
    {
        AutomationElement? button = null;
        await app.WaitUntilAsync(() => Task.FromResult((button = app.Window.FindAllDescendants(cf => cf.ByAutomationId("Notification_Action"))
            .FirstOrDefault(b => AppSession.NameOf(b) == name)) is not null), TimeSpan.FromSeconds(10), $"the button '{name}'");
        button!.Patterns.Invoke.Pattern.Invoke();
        await app.IdleAsync();
    }

    private static IReadOnlyList<string> NoticeButtons(AppSession app) =>
        [.. app.Window.FindAllDescendants(cf => cf.ByAutomationId("Notification_Action")).Select(AppSession.NameOf)];

    [Fact(Skip = "EDIT-16 (読み取り専用の解除「編集を許可する」と、属性を外す承認) は編集の担当が作る。読み取り専用属性のファイルを開いたときの読み取り専用と InfoBar は phase 0 の TC-ENG-11 で確認している")]
    [Trait(UiTest.TC, "TC-ENG-14-01")]
    public Task Read_only_attribute_can_be_removed_and_saved() => Task.CompletedTask;

    /// <summary>ENG-14 の仕様 1: ファイル > 読み取り専用で開く… で開くと、編集を受け付けない。</summary>
    [Fact]
    public Task Open_read_only_command_opens_a_read_only_document() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["openPicker"] = new JsonArray(path) } });
        await app.CommandAsync("Command_OpenReadOnly");
        await app.WaitForTabsAsync(1);
        Assert.True((await app.DocumentAsync())["readOnly"]!.GetValue<bool>());
        await app.TypeAsync("FF");
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        Assert.Equal(File.ReadAllBytes(path)[0], (await app.BytesAsync(0, 1))[0]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-18-02")]
    public Task Discarding_changes_can_be_undone() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        foreach (long offset in new long[] { 0x10, 0x20, 0x30 })
        {
            await app.GoToAsync(offset);
            await app.TypeAsync("FF");
        }

        // 1〜2. 「変更を破棄して再読み込み」の確認に「3 か所、3 バイト」と Undo で戻せることが書かれ、ボタンは「破棄して再読み込み」「キャンセル」。
        await app.CommandAsync("Command_DiscardReload");
        AutomationElement dialog = await app.WaitForAsync("DiscardDialog");
        string text = AppSession.AllText(dialog);
        Assert.Contains("3 places, 3 bytes", text);
        Assert.Contains("Ctrl+Z", text);
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains("Discard and reload", buttons);
        Assert.Contains("Cancel", buttons);

        // 3. 破棄すると 0x10 が元の値 (ダイアログが閉じてから破棄する)。
        await app.InvokeDialogButtonAsync("Discard and reload");
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the discard");
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());

        // 4. Ctrl+Z で 3 か所が FF に戻る。
        await app.KeyAsync("Z", ctrl: true);
        foreach (long offset in new long[] { 0x10, 0x20, 0x30 })
        {
            Assert.Equal(0xFF, (await app.BytesAsync(offset, 1))[0]);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-19-01")]
    public Task Unmodified_file_is_reloaded_automatically() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x100);

        // 1. 異常を再現する仕組みの「外部変更」で 0x100 からの 16 バイトを CC にする。
        await app.SendAsync("externalWrite", new JsonObject { ["path"] = path, ["offset"] = 0x100, ["hex"] = new string('C', 32) });

        // 2. 「外部で変更されたため再読み込みしました」が出て、0x100 が CC、カーソルは 0x100 のまま。
        await ExternalNoticeAsync(app, "it was reloaded");
        Assert.Equal(Enumerable.Repeat((byte)0xCC, 16), await app.BytesAsync(0x100, 16));
        Assert.Equal(0x100, (await app.DocumentAsync())["cursor"]!.GetValue<long>());

        // 3. 自動で閉じる (8 秒。UI-36 の仕様 4)。
        await app.WaitUntilAsync(async () => !(await app.NotificationsAsync()).Any(n => n["message"]!.GetValue<string>().Contains("it was reloaded", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15), "the notice to close");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-19-02")]
    public Task Replaced_file_can_be_merged() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");

        // 1. 他のエディタの安全な保存の再現: 0x80000 を BB にした内容を一時ファイルに書き、ReplaceFile で置き換える。
        byte[] replaced = (byte[])original.Clone();
        replaced[0x80000] = 0xBB;
        string temp = Path.Combine(ctx.Root, "a.tmp");
        File.WriteAllBytes(temp, replaced);
        File.Replace(temp, path, null);

        // 2. 「a.bin は外部で変更されました」と「再読み込み」「マージ」「無視」(「比較」は ANA-08 の実装後)。
        await ExternalNoticeAsync(app, "a.bin was changed by another app.");
        IReadOnlyList<string> buttons = NoticeButtons(app);
        Assert.Equal(["Reload", "Merge", "Ignore"], buttons);
        Assert.Contains("⚠", (await app.DocumentAsync())["header"]!.GetValue<string>());

        // 3〜4. 「マージ」で 0x10 が AA、0x80000 が BB。
        await PressNoticeButtonAsync(app, "Merge");
        Assert.Equal(0xAA, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0xBB, (await app.BytesAsync(0x80000, 1))[0]);

        // 5. Ctrl+Z を 1 回でマージ前に戻る。
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(0xAA, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(original[0x80000], (await app.BytesAsync(0x80000, 1))[0]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-19-03")]
    public Task Deleted_file_can_be_edited_and_saved_again() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 1〜2. 削除すると「a.bin は削除または移動されました」と「名前を付けて保存」「閉じる」。
        File.Delete(path);
        await ExternalNoticeAsync(app, "a.bin was deleted or moved.");
        IReadOnlyList<string> buttons = NoticeButtons(app);
        Assert.Contains("Save as...", buttons);
        Assert.Contains("Close", buttons);

        // 3. 上書きできる。
        await app.TypeAsync("11");
        Assert.Equal(0x11, (await app.BytesAsync(0, 1))[0]);

        // 4. Ctrl+S で「元の場所にファイルを作り直します」と確かめて保存する。
        await app.KeyAsync("S", ctrl: true);
        AutomationElement dialog = await app.WaitForAsync("SaveDialog");
        Assert.Contains("creates the file again in its original location", AppSession.AllText(dialog));
        await app.InvokeDialogButtonAsync("Save");
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(20), "the save");

        // 5. a.bin が作り直され、オフセット 0 が 11、残りが TD-SEQ-1M と一致する。
        Assert.True(File.Exists(path));
        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(0x11, saved[0]);
        Assert.Equal(original.AsSpan(1).ToArray(), saved.AsSpan(1).ToArray());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-19-04")]
    public Task Own_saves_are_not_reported() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        async Task SaveAndWatchAsync()
        {
            await app.KeyAsync("S", ctrl: true);
            await app.IdleAsync();
            Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
            for (int i = 0; i < 20; i++)
            {
                if (i == 10)
                {
                    // ウィンドウの非アクティブ化とアクティブ化 (前面に出さずに、アクティブ化のときの確認を行う)。
                    await app.SendAsync("activated");
                }

                await Task.Delay(500);
                Assert.Equal(0, ExternalNotices(await app.NotificationsAsync()));
                Assert.DoesNotContain("⚠", (await app.DocumentAsync())["header"]!.GetValue<string>());
            }
        }

        // 1. その場保存。
        await app.TypeAsync("FF");
        await SaveAndWatchAsync();

        // 2. 1 バイト挿入して安全な保存。
        await app.GoToAsync(0);
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await SaveAndWatchAsync();
        Assert.Equal(0x100001, new FileInfo(path).Length);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-19-05")]
    public Task Hundred_writes_in_a_second_show_one_notice() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 前提: 他のアプリが書き込み用に開いている状態でオフセット 0 を上書きする (書き込み禁止のハンドルを持てない)。
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await app.TypeAsync("FF");
        await app.WaitForNotificationAsync(m => m.Contains("write to this file", StringComparison.Ordinal),
            "the lock failure notice");

        // 1. 10 ms 間隔で 100 回、0x1000 からの 4 バイトに連番を書く。
        DateTime start = DateTime.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            writer.Position = 0x1000;
            writer.Write(BitConverter.GetBytes(i));
            writer.Flush(flushToDisk: true);
            await Task.Delay(10);
        }

        // 2. 書き込みの開始から 3 秒間で、外部変更の InfoBar は 1 回だけ。
        int max = 0;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(3))
        {
            IReadOnlyList<JsonObject> notices = await app.NotificationsAsync();
            max = Math.Max(max, ExternalNotices(notices));
            Assert.All(notices.Where(n => n["message"]!.GetValue<string>().Contains("changed by another app", StringComparison.Ordinal)),
                n => Assert.Equal(1, n["count"]!.GetValue<int>()));
            await Task.Delay(100);
        }

        await app.WaitUntilAsync(async () => ExternalNotices(await app.NotificationsAsync()) == 1, TimeSpan.FromSeconds(5), "the notice");
        Assert.Equal(1, ExternalNotices(await app.NotificationsAsync()));
        Assert.Contains("mixed", (await app.NotificationsAsync()).Select(n => n["message"]!.GetValue<string>()).First(m => m.Contains("changed by another app", StringComparison.Ordinal)));
    });
}
