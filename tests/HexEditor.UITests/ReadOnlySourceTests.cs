using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 書き込めないファイル (ENG-14 の仕様 1・3・4)、読み取り専用のドキュメントの外部変更 (ENG-19 の仕様 12)、閉じた InfoBar の再表示 (ENG-18 の
/// 仕様 1)、保存の途中で残った一時ファイル (ENG-22 の仕様 7、ENG-27 の仕様 7)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ReadOnlySourceTests
{
    private static bool ReadOnly(JsonObject doc) => doc["readOnly"]!.GetValue<bool>();

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

    [Fact]
    public Task Read_only_document_is_reloaded_after_an_external_change() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-14 の受け入れ基準 4 の続き: 「読み取り専用で開く」で開いたファイルを他のアプリが書き換えると、自動で再読み込みする
        // (EDIT-16 の仕様 2: 再読み込みは読み取り専用でもできる)。以前はここで例外になり、アプリが落ちていた。
        string path = ctx.CopyTestData("TD-SEQ-1M", "ro.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["openPicker"] = new JsonArray(path) } });
        await app.CommandAsync("Command_OpenReadOnly");
        await app.WaitForTabsAsync(1);
        await EditingTests.SelectTabAsync(app, 0);
        Assert.True(ReadOnly(await app.DocumentAsync()));

        await app.SendAsync("externalWrite", new JsonObject { ["path"] = path, ["offset"] = 0x100, ["hex"] = new string('C', 32) });
        await app.WaitForNotificationAsync(m => m.Contains("it was reloaded", StringComparison.Ordinal), "the reload notice");
        Assert.Equal(Enumerable.Repeat((byte)0xCC, 16), await app.BytesAsync(0x100, 16));
        Assert.True(ReadOnly(await app.DocumentAsync()));
    });

    [Fact]
    public Task Read_only_modified_document_offers_reload_but_not_merge() => UiTestContext.RunAsync(async ctx =>
    {
        // 編集してから読み取り専用にしたドキュメント (EDIT-16 の仕様 5) に外部変更: マージは出さず、再読み込みはできる。
        string path = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");
        await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "User" });

        byte[] replaced = (byte[])original.Clone();
        replaced[0x80000] = 0xBB;
        string temp = Path.Combine(ctx.Root, "a.tmp");
        File.WriteAllBytes(temp, replaced);
        File.Replace(temp, path, null);

        await app.WaitForNotificationAsync(m => m.Contains("a.bin was changed by another app.", StringComparison.Ordinal), "the notice");
        IReadOnlyList<string> buttons = NoticeButtons(app);
        Assert.Contains("Reload", buttons);
        Assert.DoesNotContain("Merge", buttons);

        // 「再読み込み」: 変更を破棄する確認の後、新しい内容になる (読み取り専用のまま)。
        await PressNoticeButtonAsync(app, "Reload");
        await app.WaitForAsync("DiscardDialog");
        await app.InvokeDialogButtonAsync("Discard and reload");
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the reload");
        Assert.Equal(0xBB, (await app.BytesAsync(0x80000, 1))[0]);
        Assert.Equal(original[0x10], (await app.BytesAsync(0x10, 1))[0]);
        Assert.True(ReadOnly(await app.DocumentAsync()));
    });

    [Fact]
    public Task File_held_by_another_writer_opens_read_only_with_the_reason() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-14 の仕様 1: 他のアプリが書き込みのために開いていて (書き込みを共有しない) 書き込み用に開けない → 読み取り専用で開き、
        // InfoBar で理由を示す。他のアプリが閉じた後は「編集を許可する」で解除できる。
        string path = ctx.CopyTestData("TD-SEQ-1M", "held.bin");
        var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        AppSession app;
        try
        {
            app = await ctx.StartAsync(new AppOptions { Files = [path] });
            await app.WaitForNotificationAsync(m => m.Contains("open for writing, so it opened as read-only", StringComparison.Ordinal), "the reason");
            Assert.True(ReadOnly(await app.DocumentAsync()));
            Assert.Contains("Allow editing", NoticeButtons(app));
        }
        finally
        {
            await other.DisposeAsync();
        }

        await PressNoticeButtonAsync(app, "Allow editing");
        await app.WaitForAsync("ReadOnlyConfirmDialog");
        await EditCommandTests.PressAsync(app);
        Assert.False(ReadOnly(await app.DocumentAsync()));
    });

    [Fact]
    public Task Access_denied_and_read_only_media_are_shown_at_open() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-14 の仕様 1: 書き込み権限がない・読み取り専用のメディア (異常を再現する仕組みで書き込みの確認を失敗させる)。
        string denied = ctx.CopyTestData("TD-SEQ-1M", "denied.bin");
        string media = ctx.CopyTestData("TD-SEQ-1M", "media.bin");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [denied, media],
            Hooks = new JsonObject
            {
                ["writeErrors"] = new JsonArray(
                    new JsonObject { ["match"] = "denied.bin", ["error"] = "accessDenied" },
                    new JsonObject { ["match"] = "media.bin", ["error"] = "writeProtect" }),
            },
        });

        await app.WaitForTabsAsync(2);
        await EditingTests.SelectTabAsync(app, 0);
        await app.WaitForNotificationAsync(m => m.Contains("permission to write to this file", StringComparison.Ordinal), "the access denied reason");
        Assert.True(ReadOnly(await app.DocumentAsync()));
        Assert.Contains("Allow editing", NoticeButtons(app));

        // 権限がないまま「編集を許可する」: 理由を示して読み取り専用のまま (EDIT-16 の仕様 4)。
        await PressNoticeButtonAsync(app, "Allow editing");
        await app.WaitForAsync("ReadOnlyConfirmDialog");
        await EditCommandTests.PressAsync(app);
        await app.WaitForNotificationAsync(m => m.Contains("so it stays read-only", StringComparison.Ordinal), "the release failure");
        Assert.True(ReadOnly(await app.DocumentAsync()));

        // 読み取り専用のメディアは解除できないため「編集を許可する」を出さない。
        await EditingTests.SelectTabAsync(app, 1);
        await app.WaitForNotificationAsync(m => m.Contains("on read-only media, so it opened as read-only", StringComparison.Ordinal), "the media reason");
        Assert.True(ReadOnly(await app.DocumentAsync()));
        Assert.DoesNotContain("Allow editing", NoticeButtons(app));
    });

    [Fact]
    public Task Opened_read_only_file_with_the_attribute_can_be_released_and_saved() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-14 の仕様 3: 「読み取り専用で開く」で開いた読み取り専用属性のファイルも、承認すれば保存時に属性を外す (権限がないとは示さない)。
        string path = ctx.CopyTestData("TD-SEQ-1M", "attr.bin");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["openPicker"] = new JsonArray(path) } });
            await app.CommandAsync("Command_OpenReadOnly");
            await app.WaitForTabsAsync(1);
            await EditingTests.SelectTabAsync(app, 0);

            // 入力すると「読み取り専用です」と「編集を許可する」。
            await app.TypeAsync("FF");
            await PressNoticeButtonAsync(app, "Allow editing");
            await app.WaitForAsync("ReadOnlyConfirmDialog");
            await EditCommandTests.PressAsync(app);
            Assert.False(ReadOnly(await app.DocumentAsync()));
            Assert.DoesNotContain(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>().Contains("permission", StringComparison.Ordinal));

            await app.GoToAsync(0);
            await app.TypeAsync("FF");
            await app.KeyAsync("S", ctrl: true);
            await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(15), "the save");
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(0xFF, File.ReadAllBytes(path)[0]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    public Task Save_all_does_not_overwrite_a_read_only_document() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-14 の仕様 4・EDIT-16 の仕様 5: 読み取り専用のドキュメントは「すべて保存」でも元のファイルに書かず、「名前を付けて保存」になる。
        string path = ctx.CopyTestData("TD-SEQ-1M", "keep.bin");
        byte[] original = File.ReadAllBytes(path);
        string other = Path.Combine(ctx.Root, "copy.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["savePicker"] = other } });
        await app.TypeAsync("FF");
        await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "User" });

        await app.SendAsync("execute", new JsonObject { ["id"] = "file.saveAll" });
        await app.IdleAsync();
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(other)), TimeSpan.FromSeconds(15), "the save as");
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0xFF, File.ReadAllBytes(other)[0]);
    });

    [Fact]
    public Task Dismissed_external_change_notice_comes_back_on_reload() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-18 の仕様 1: 外部で変更されたファイルの「再読み込み」(Ctrl+R) は ENG-19 の扱い。InfoBar を閉じた後でも、もう一度出す。
        string path = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");

        byte[] replaced = (byte[])original.Clone();
        replaced[0x80000] = 0xBB;
        string temp = Path.Combine(ctx.Root, "a.tmp");
        File.WriteAllBytes(temp, replaced);
        File.Replace(temp, path, null);
        const string message = "a.bin was changed by another app.";
        await app.WaitForNotificationAsync(m => m.Contains(message, StringComparison.Ordinal), "the notice");

        // InfoBar の閉じるボタンで閉じる。
        AutomationElement bar = app.Window.FindAllDescendants(cf => cf.ByAutomationId("Notification"))
            .First(b => AppSession.AllText(b).Contains(message, StringComparison.Ordinal));
        bar.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Patterns.Invoke.Pattern.Invoke();
        await app.WaitUntilAsync(async () => !(await app.NotificationsAsync()).Any(n => n["message"]!.GetValue<string>().Contains(message, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10), "the notice to close");

        await app.KeyAsync("R", ctrl: true);
        await app.WaitForNotificationAsync(m => m.Contains(message, StringComparison.Ordinal), "the notice again");

        // ボタンは通知の後に UI オートメーションに現れる。
        await app.WaitUntilAsync(() => Task.FromResult(NoticeButtons(app).Contains("Merge")), TimeSpan.FromSeconds(5), "the Merge button");
        Assert.Equal(0xAA, (await app.BytesAsync(0x10, 1))[0]);
    });

    [Fact]
    public Task Leftover_save_temp_file_is_offered_for_deletion() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-22 の仕様 7: 安全な保存の途中で落ちて残った一時ファイルは、次回起動時に復旧の画面で削除を提案する。
        string path = ctx.CopyTestData("TD-SEQ-1M", "left.bin");
        byte[] original = File.ReadAllBytes(path);
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [path], Hooks = new JsonObject { ["killAt"] = "saveBeforeReplace" } });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        try
        {
            await app.KeyAsync("S", ctrl: true);
            await app.IdleAsync();
        }
        catch (IOException)
        {
            // 保存の途中でプロセスが終わり、命令の通り道が閉じた。
        }

        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        string temp = Assert.Single(Directory.GetFiles(ctx.Root, ".left.bin.~hex*.tmp", new EnumerationOptions { AttributesToSkip = 0 }));
        Assert.Equal(original, File.ReadAllBytes(path));

        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        AutomationElement delete = await again.WaitForAsync("Recovery_DeleteTemp", TimeSpan.FromSeconds(20));
        Assert.Contains(temp, AppSession.AllText(again.Find("RecoveryDialog") ?? again.Window));
        delete.Patterns.Invoke.Pattern.Invoke();
        await again.WaitUntilAsync(() => Task.FromResult(!File.Exists(temp)), TimeSpan.FromSeconds(10), "the temp file to be deleted");
        Assert.Equal(original, File.ReadAllBytes(path));
    });
}
