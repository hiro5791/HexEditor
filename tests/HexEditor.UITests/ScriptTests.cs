using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// スクリプトの実行 (AUTO-01) と信頼の確認 (AUTO-34) を、アプリにつないだ状態で確かめる。
/// 振る舞いの細部は Core のテスト (HexEditor.Core.Tests/Scripting) が偽物の UI で確かめる。ここではダイアログと Undo のつながりだけを見る。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ScriptTests
{
    private static async Task<JsonArray> OutputAsync(AppSession app) =>
        (await app.SendAsync("script", new JsonObject { ["action"] = "output" }))["lines"]!.AsArray();

    private static async Task WaitForNoRunAsync(AppSession app) =>
        await app.WaitUntilAsync(
            async () => (await app.SendAsync("script", new JsonObject { ["action"] = "running" }))["count"]!.GetValue<int>() == 0,
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the end of the script");

    private static async Task<byte[]> BytesAsync(AppSession app, long offset, int length) =>
        Convert.FromHexString((await app.SendAsync("byteStates", new JsonObject { ["offset"] = offset, ["length"] = length }))["hex"]!.GetValue<string>());

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-34-01")]
    public Task First_run_asks_for_trust_and_lists_the_permissions() => UiTestContext.RunAsync(async ctx =>
    {
        string script = ctx.WriteFile("perms.js", System.Text.Encoding.UTF8.GetBytes("""
            // ==HexEditorScript==
            // @name         perms
            // @author       Example
            // @permission   file-read
            // @permission   network
            // ==/HexEditorScript==
            console.log('ran');
            """));
        AppSession app = await ctx.StartAsync();

        // 1〜2. 確認ダイアログに名前・作者・説明・許可の一覧と 3 つのボタンがある。
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        var dialog = await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.WaitForDialogTextAsync(dialog, "Name: perms", "Author: Example", "read and write the open documents", "file-read", "network");
        Assert.NotNull(app.Button("Run once"));
        Assert.NotNull(app.Button("Always allow"));
        Assert.NotNull(app.Button("Don't run"));

        // 3. 「実行しない」を押すと、スクリプトは実行されない。
        await app.InvokeDialogButtonAsync("Don't run");
        await WaitForNoRunAsync(app);
        Assert.DoesNotContain(await OutputAsync(app), l => l!["text"]!.GetValue<string>() == "ran");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-01-03")]
    public Task One_run_is_undone_by_one_ctrl_z() => UiTestContext.RunAsync(async ctx =>
    {
        string data = ctx.WriteFile("data.bin", [.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
        string script = ctx.WriteFile("write3.js", System.Text.Encoding.UTF8.GetBytes("""
            doc.writeBytes(0, 'AA');
            doc.writeBytes(1, 'BB');
            doc.writeBytes(2, 'CC');
            console.log('done');
            """));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [data] });

        // 信頼の確認で「1 回だけ実行する」を選び、3 回の書き込みを終える。
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Run once");
        await WaitForNoRunAsync(app);
        Assert.Contains(await OutputAsync(app), l => l!["text"]!.GetValue<string>() == "done");
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0x03 }, await BytesAsync(app, 0, 4));

        // 1 回の Ctrl+Z で 3 か所とも戻る。
        await app.KeyAsync("Z", ctrl: true);
        await app.WaitUntilAsync(async () => (await BytesAsync(app, 0, 4)).SequenceEqual(new byte[] { 0, 1, 2, 3 }),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the undo");
    });
}
