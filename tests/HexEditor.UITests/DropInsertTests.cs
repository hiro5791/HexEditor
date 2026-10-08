using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>Ctrl を押しながらのファイルのドロップ (UI-34 の仕様 1・2)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DropInsertTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-04")]
    public Task Ctrl_drop_inserts_the_file_content_at_the_drop_position() => UiTestContext.RunAsync(async ctx =>
    {
        // 実際のマウスのドラッグは使わない。Ctrl を押しながら Hex ビューのオフセット 0x100 のセルにドロップしたときの処理を
        // テスト用の命令で呼ぶ (ドラッグ中の表示の文字も返す)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")] });
        await app.CommandAsync("Command_ToggleInsert");
        await app.IdleAsync();
        string bytes256 = ctx.TestData("TD-BYTES-256");

        // 1. ドラッグ中のカーソルの横に「挿入」。EDIT-30 のダイアログ (全体を挿入) で決める。
        JsonObject drop = await app.SendAsync("drop", new JsonObject { ["paths"] = new JsonArray(bytes256), ["ctrlOffset"] = 0x100 });
        Assert.Equal("Insert", drop["caption"]!.GetValue<string>());
        await app.WaitForAsync("InsertFileDialog");
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["length"]!.GetValue<long>() == 1_048_832, TimeSpan.FromSeconds(10), "the inserted content");

        // 2. タブは 1 つのまま。0x100〜0x1FF が 00〜FF。
        Assert.Single(await app.TabNamesAsync());
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), await app.BytesAsync(0x100, 256));
        Assert.Equal(new byte[16], await app.BytesAsync(0xF0, 16));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-04")]
    public Task Ctrl_drop_is_refused_on_a_read_only_document() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-34 の仕様 2: 読み取り専用の文書には挿入のドロップを受け付けない。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")] });
        await app.CommandAsync("Command_ReadOnly");
        await app.IdleAsync();
        JsonObject drop = await app.SendAsync("drop", new JsonObject { ["paths"] = new JsonArray(ctx.TestData("TD-BYTES-256")), ["ctrlOffset"] = 0x100 });
        Assert.False(drop["accepted"]!.GetValue<bool>());
        await Task.Delay(300);
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Single(await app.TabNamesAsync());
    });
}
