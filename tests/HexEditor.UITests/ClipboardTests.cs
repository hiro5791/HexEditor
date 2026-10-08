using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// コピー・切り取り・貼り付け (EDIT-22〜EDIT-24)。テスト用のビルドを --test-hooks で起動すると、アプリはシステムのクリップボードの
/// 代わりにアプリの中だけのクリップボードを使う (利用者のクリップボードに触れない)。メモ帳などの他のアプリとのやり取りは、
/// その代わりのクリップボードの中身をテスト用の命令の通り道で読み書きして確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ClipboardTests
{
    private const long MiB = 1024 * 1024;

    private static Task<JsonObject> ClipboardAsync(AppSession app) => app.SendAsync("clipboard");

    /// <summary>他のアプリがクリップボードにテキストを入れた状態にする。</summary>
    private static Task SetClipboardTextAsync(AppSession app, string text) =>
        app.SendAsync("setClipboard", new JsonObject { ["text"] = text });

    private static async Task CopyAsync(AppSession app, string key = "C")
    {
        await app.KeyAsync(key, ctrl: true);
        await app.IdleAsync();
    }

    // ---- EDIT-22 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-22-01")]
    public Task Copy_from_hex_column_puts_hex_text() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [SelectionAndInputTests.EditSample(ctx)] });
        await app.SelectAsync(0, 4);
        await CopyAsync(app);

        JsonObject clip = await ClipboardAsync(app);
        var formats = clip["formats"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Contains("HexEditor.Binary", formats);
        Assert.Contains("HexEditor.Meta", formats);
        Assert.Contains("Text", formats);

        // メモ帳に貼るテキスト (CF_UNICODETEXT)。改行なし。
        Assert.Equal("DE AD BE EF", clip["text"]!.GetValue<string>());
        Assert.Equal("DEADBEEF", clip["binary"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-22-02")]
    public Task Copy_from_text_column_puts_decoded_text() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [SelectionAndInputTests.EditSample(ctx)] });
        JsonObject render = await app.RenderAsync();

        // 1. テキスト列の 0x08 をダブルクリックして Hello を選ぶ。
        await ClickAsync(app, CellPoint(render, 0x08, text: true), clicks: 2);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 5,
            TimeSpan.FromSeconds(5), "the selection");
        await CopyAsync(app);
        Assert.Equal("Hello", (await ClipboardAsync(app))["text"]!.GetValue<string>());

        // 4〜5. 00 0A Hello 00 FF: NUL と解釈できないバイトは U+FFFD、0A は LF。
        await app.SelectAsync(0x04, 9);
        await CopyAsync(app);
        Assert.Equal("�\nHello��", (await ClipboardAsync(app))["text"]!.GetValue<string>());
    });

    [Fact(Skip = "アプリの終了後もシステムのクリップボードに実データが残ることを確かめるには、本物のクリップボードを使う必要がある (作業中の利用者のクリップボードを書き換えるため自動テストでは行わない)")]
    [Trait(UiTest.TC, "TC-EDIT-22-05")]
    public void Clipboard_survives_exit()
    {
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-22-06")]
    public Task Undoing_a_cut_keeps_the_clipboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SelectAsync(0x10, 0x10);
        await CopyAsync(app, "X");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB - 0x10, doc["length"]!.GetValue<long>());
        Assert.Equal(0x20, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0x10, doc["cursor"]!.GetValue<long>());

        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        byte[] expected = Enumerable.Range(0x10, 0x10).Select(i => (byte)i).ToArray();
        Assert.Equal(expected, await app.BytesAsync(0x10, 0x10));

        // 6. クリップボードの内容は残っている。
        Assert.Equal(Convert.ToHexString(expected), (await ClipboardAsync(app))["binary"]!.GetValue<string>());

        // 7. 0x100 に挿入モードで貼る。
        await app.GoToAsync(0x100);
        await app.KeyAsync("Insert");
        await CopyAsync(app, "V");
        Assert.Equal(expected, await app.BytesAsync(0x100, 0x10));
    });

    // ---- EDIT-23 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-01")]
    public Task Hex_text_from_another_app_is_pasted_as_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        // 手順 3〜4 (Base64 として判別されて「形式を選択して貼り付け」のダイアログが開く) は EditCommandTests で確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x10);
        await SetClipboardTextAsync(app, "0xDE, 0xAD");

        await CopyAsync(app, "V");
        Assert.Equal(MiB + 2, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0x10 }, await app.BytesAsync(0x10, 3));
        await AssertSelectionAsync(app, 0x10, 2);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-03")]
    public Task Overwrite_paste_in_insert_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await app.SelectAsync(0, 4);
        await CopyAsync(app);

        await app.GoToAsync(0x40);
        await CopyAsync(app, "B");
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x44 }, await app.BytesAsync(0x40, 5));

        // 4〜5. 末尾の 2 バイトを選んで上書き貼り付け: 可変長なので末尾が延びる。
        await app.SelectAsync(MiB - 2, 2);
        await CopyAsync(app, "B");
        Assert.Equal(MiB + 2, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, await app.BytesAsync(MiB - 2, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-04")]
    public Task Paste_beyond_the_end_of_a_fixed_length_document() => UiTestContext.RunAsync(async ctx =>
    {
        // 物理ディスク (TD-VHDX-MBR は管理者権限と仮想ディスクの接続が必要) の代わりに、長さを変えられない仮想データソースを使う。
        const long length = 0x10000;
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = SelectionAndInputTests.FixedLengthSource(length) });
        await app.SendAsync("setClipboard", new JsonObject { ["binary"] = "1122334455667788" });
        await GoToAsync(app, "end-3");
        Assert.Equal(length - 3, await CursorAsync(app));

        // 2. Ctrl+V: 末尾を越える 5 バイトを示す確認ダイアログ (ENG-07 の仕様 5) で「末尾まで貼り付ける」を選ぶ。
        await app.KeyAsync("V", ctrl: true);
        await app.WaitForAsync("PasteTruncateDialog");
        Assert.Contains("5 bytes", AppSession.AllText(app.Find("PasteTruncateDialog")!));
        (await app.WaitForAsync("PrimaryButton")).Patterns.Invoke.Pattern.Invoke();
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.BytesAsync(length - 3, 1))[0] == 0x11, TimeSpan.FromSeconds(5), "the paste");

        // 3. 長さは変わらず、最後の 3 バイトだけが書かれ、InfoBar で知らせる。
        Assert.Equal(length, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, await app.BytesAsync(length - 3, 3));
        Assert.Contains(await NoticesAsync(app), n => n.Contains("5 bytes beyond the end", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-05")]
    public Task Japanese_text_pasted_into_the_text_column() => UiTestContext.RunAsync(async ctx =>
    {
        // UTF-8 (手順 1〜2) はフェーズ 1 の文字コード (VIEW-21) のため、Shift_JIS (ANSI 932) の手順 3〜4 だけを確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-ZERO-1M")],
            Hooks = new JsonObject { ["ansiCodePage"] = 932 },
        });
        await app.KeyAsync("Insert");
        await app.KeyAsync("Tab");
        await app.GoToAsync(0x10);
        await app.CommandAsync("Command_EncodingAnsi");
        await SetClipboardTextAsync(app, "日本");

        await CopyAsync(app, "V");
        Assert.Equal(MiB + 4, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x93, 0xFA, 0x96, 0x7B }, await app.BytesAsync(0x10, 4));
    });

    // ---- EDIT-24 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-24-02")]
    public Task Pasted_range_stays_readable_after_closing_the_source() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-MARKERS-1G をこのテストの一時フォルダに作る (スパースファイル)。一時ファイルの置き場所は設定フォルダの recovery。
        string source = TestDataCatalog.Generate("TD-MARKERS-1G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source] });
        string temp = (await app.StateAsync())["recoveryRoot"]!.GetValue<string>();

        await app.SelectAsync(0, 256 * MiB);
        await CopyAsync(app);

        // 2. 新しいタブに貼り付ける。
        await app.KeyAsync("N", ctrl: true);
        await EditingTests.SelectTabAsync(app, 1);
        await CopyAsync(app, "V");
        Assert.Equal(256 * MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // 3〜4. コピー元のタブを閉じ、実体化の完了を待つ。
        await EditingTests.SelectTabAsync(app, 0);
        await app.CommandAsync("Command_Close");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["documents"]!.AsArray().Count == 1, TimeSpan.FromSeconds(10), "the tab to close");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0,
            TimeSpan.FromSeconds(120), "the materialization");

        // 5. 一時ファイルの置き場所に 256 MiB 以上。
        long total = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(total >= 256 * MiB, $"{total:N0} bytes in {temp}");

        // 6〜7. コピー元のファイルを消しても、貼り付けた内容が読める。
        File.Delete(source);
        await EditingTests.SelectTabAsync(app, 0);
        foreach (long at in new long[] { 0, 0x100000, 0xFF00000 })
        {
            Assert.Equal(TestDataCatalog.Marker(at), await app.BytesAsync(at, TestDataCatalog.MarkerLength));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-24-03")]
    public Task Text_copied_in_another_app_wins() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await app.SelectAsync(0, 0x10);
        await CopyAsync(app);

        // 2. メモ帳で XYZ をコピーした (他のアプリがクリップボードを書き換えた)。
        await SetClipboardTextAsync(app, "XYZ");

        await app.KeyAsync("Tab");
        await app.GoToAsync(0x40);
        await CopyAsync(app, "V");
        Assert.Equal(MiB + 3, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x58, 0x59, 0x5A, 0x40 }, await app.BytesAsync(0x40, 4));
    });
}
