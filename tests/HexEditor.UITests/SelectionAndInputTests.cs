using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// マウス・キーボードによる選択 (EDIT-01〜EDIT-03)、入力モードと入力 (EDIT-10〜EDIT-12)、削除 (EDIT-13)。
/// マウスの操作はテスト用の命令の通り道で、Hex ビューのポインタの処理に描画面の座標を渡して行う (実際のマウスは使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SelectionAndInputTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>TD-VIEW-VIRT-FIXED: 長さを変えられない仮想データソース (物理ディスクの代わり)。オフセット n の値は n の下位 8 bit。</summary>
    internal static JsonObject FixedLengthSource(long length) => new()
    {
        ["virtualSources"] = new JsonArray(new JsonObject
        {
            ["name"] = "virtual-fixed", ["length"] = length, ["content"] = "offsetLowByte", ["resizable"] = false,
        }),
    };

    /// <summary>TD-EDIT-SAMPLE (32 バイト): DE AD BE EF 00 0A "Hello" 00 FF "Hi" 00…</summary>
    internal static string EditSample(UiTestContext ctx)
    {
        byte[] data = new byte[32];
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x0A, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0xFF, 0x48, 0x69 }.CopyTo(data, 0);
        return ctx.WriteFile("TD-EDIT-SAMPLE.bin", data);
    }

    // ---- EDIT-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-01-01")]
    public Task Shift_click_keeps_the_anchor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();
        await ClickAsync(app, CellPoint(render, 0x10));

        await ClickAsync(app, CellPoint(render, 0x30), shift: true);
        Assert.Equal("Selection: 0x10–0x30 (length 0x21 = 33)", await app.UiaNameAsync("Status_Selection"));

        await ClickAsync(app, CellPoint(render, 0x20), shift: true);
        Assert.Equal("Selection: 0x10–0x20 (length 0x11 = 17)", await app.UiaNameAsync("Status_Selection"));

        await ClickAsync(app, CellPoint(render, 0x08), shift: true);
        Assert.Equal("Selection: 0x8–0x10 (length 0x9 = 9)", await app.UiaNameAsync("Status_Selection"));
    });

    [Fact(Skip = "バイトのグループ化 (VIEW-09。フェーズ 1) が未実装のため、グループ化 2・4 バイトでのダブルクリックを確かめられない。グループ化 1 バイトの手順 7〜8 は Double_click_in_hex_selects_one_byte で確かめる")]
    [Trait(UiTest.TC, "TC-EDIT-01-02")]
    public void Double_click_in_hex_selects_the_group()
    {
    }

    /// <summary>TC-EDIT-01-02 の手順 7〜8 (グループ化 1 バイト。既定の表示)。</summary>
    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-01-02")]
    public Task Double_click_in_hex_selects_one_byte() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();
        await ClickAsync(app, CellPoint(render, 0x23, charIndex: 1), clicks: 2);
        await AssertSelectionAsync(app, 0x23, 1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-01-03")]
    public Task Double_click_in_text_selects_printable_run() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [EditSample(ctx)] });
        JsonObject render = await app.RenderAsync();
        foreach (long offset in new long[] { 0x08, 0x06, 0x0A })
        {
            await app.KeyAsync("Escape");
            await ClickAsync(app, CellPoint(render, offset, text: true), clicks: 2);

            // テキスト列の連続は別のスレッドで読んでから選ぶ。
            await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() > 0,
                TimeSpan.FromSeconds(5), "the selection");
            await AssertSelectionAsync(app, 0x06, 5);
            Assert.Equal("Text", (await app.DocumentAsync())["activeColumn"]!.GetValue<string>());

            // 次のダブルクリックがトリプルクリックと数えられないよう、ダブルクリックの時間をあける。
            await Task.Delay(600);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-01-04")]
    public Task Right_click_inside_the_selection_keeps_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();

        // 1. 0x10 から 0x1F までをドラッグする。
        await PointerAsync(app, "down", CellPoint(render, 0x10));
        await PointerAsync(app, "move", CellPoint(render, 0x18));
        await PointerAsync(app, "move", CellPoint(render, 0x1F));
        await PointerAsync(app, "up", CellPoint(render, 0x1F));
        await AssertSelectionAsync(app, 0x10, 0x10);

        // 2〜3. 選択範囲の中を右クリック: メニューが開き、選択はそのまま。
        await RightClickAsync(app, CellPoint(render, 0x15));
        await app.IdleAsync();
        Assert.True((await app.RenderAsync())["contextMenuOpen"]!.GetValue<bool>());
        await AssertSelectionAsync(app, 0x10, 0x10);

        // 4. メニューを閉じる (Esc と同じ)。
        await app.SendAsync("hideContextMenu");
        await app.IdleAsync();
        Assert.False((await app.RenderAsync())["contextMenuOpen"]!.GetValue<bool>());

        // 5〜6. 選択範囲の外を右クリック: 選択が解除されてカーソルが移り、メニューが開く。
        await RightClickAsync(app, CellPoint(render, 0x40));
        await app.IdleAsync();
        await AssertSelectionAsync(app, 0, 0);
        Assert.Equal(0x40, await CursorAsync(app));
        Assert.True((await app.RenderAsync())["contextMenuOpen"]!.GetValue<bool>());
        await app.SendAsync("hideContextMenu");
    });

    // ---- EDIT-02 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-02-03")]
    public Task Selection_change_is_announced_once() => UiTestContext.RunAsync(async ctx =>
    {
        // UI オートメーションの通知イベントの代わりに、アプリが通知イベントで送った読み上げ文の記録 (送った時刻付き) を読む。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await Task.Delay(300);
        await app.SendAsync("announcements", new JsonObject { ["clear"] = true });

        // 40 回の入力を 1 つの命令で送る (1 回ずつ送ると、混んだ CI のランナーでは命令の往復が読み上げの待ち時間 0.5 秒を超え、
        // 途中でも読み上げてしまう。入力の間隔ではなく、続けて押したときに 1 件にまとめることを確かめる)。
        long sent = Environment.TickCount64;
        await app.KeyAsync("Right", shift: true, count: 40);
        long lastKey = Environment.TickCount64;

        await Task.Delay(1000);
        var selection = (await app.SendAsync("announcements"))["items"]!.AsArray()
            .Where(a => a!["id"]!.GetValue<string>() == "HexViewSelection").ToList();
        JsonNode only = Assert.Single(selection)!;
        long after = only["time"]!.GetValue<long>() - lastKey;
        // 最後の入力は命令を送ってから答えが返るまでの間 (その往復の分だけ前でもよい)。
        Assert.InRange(after, 450 - (lastKey - sent), 700);
        string text = only["text"]!.GetValue<string>();
        Assert.Contains("0x00000010", text.Replace(" ", string.Empty));
        Assert.Contains("40 bytes", text);
    });

    // ---- EDIT-03 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-03-01")]
    public Task Select_all_keeps_the_scroll_position() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-EMPTY")] });
        await EditingTests.SelectTabAsync(app, 0);
        await GoToAsync(app, "0x80000");
        long top = await TopRowAsync(app);

        await app.KeyAsync("A", ctrl: true);
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(top, doc["topRow"]!.GetValue<long>());
        Assert.Equal(top, (await app.RenderAsync())["topRow"]!.GetValue<long>());
        await AssertSelectionAsync(app, 0, MiB);
        Assert.Equal(MiB, doc["cursor"]!.GetValue<long>());

        // 5〜6. 長さ 0 のタブでは何も選ばれず、エラーや InfoBar も出ない。
        await EditingTests.SelectTabAsync(app, 1);
        await app.KeyAsync("A", ctrl: true);
        await app.IdleAsync();
        doc = await app.DocumentAsync();
        Assert.Equal(0, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.Empty(await NoticesAsync(app));
        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains(" ERR ", StringComparison.Ordinal));
    });

    // ---- EDIT-10〜EDIT-13 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-10-02")]
    public Task Insert_mode_is_refused_on_a_fixed_length_document() => UiTestContext.RunAsync(async ctx =>
    {
        // 物理ディスク (TD-VHDX-MBR は管理者権限と仮想ディスクの接続が必要) の代わりに、長さを変えられない仮想データソースを使う。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = FixedLengthSource(0x10000) });
        string mode = await app.UiaNameAsync("Status_Mode");
        Assert.StartsWith("Overwrite", mode, StringComparison.Ordinal);

        await app.KeyAsync("Insert");
        await app.IdleAsync();
        Assert.Equal(mode, await app.UiaNameAsync("Status_Mode"));
        Assert.False((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());
        string notice = Assert.Single(await NoticesAsync(app));
        Assert.Contains("can't change length", notice);

        // 3〜4. もう一度押しても上書きのまま。同じ通知はまとめて 1 つ。
        await app.KeyAsync("Insert");
        await app.IdleAsync();
        Assert.Equal(mode, await app.UiaNameAsync("Status_Mode"));
        Assert.Single(await NoticesAsync(app));
    });

    [Fact(Skip = "日本語 IME (Microsoft IME) の変換中の状態と確定が必要。IME への入力はシステム全体のキーボード入力になるため、作業中の PC では自動テストで行わない")]
    [Trait(UiTest.TC, "TC-EDIT-11-04")]
    public void Full_width_letters_from_japanese_ime()
    {
    }

    [Fact(Skip = "日本語 IME の変換中の文字列と確定が必要 (システム全体のキーボード入力)。また UTF-8 の文字コード (VIEW-21) はフェーズ 1")]
    [Trait(UiTest.TC, "TC-EDIT-12-01")]
    public void Japanese_ime_in_utf8_insert_mode()
    {
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-12-02")]
    public Task Kanji_in_shift_jis_overwrite_mode() => UiTestContext.RunAsync(async ctx =>
    {
        // Shift_JIS はフェーズ 0 では ANSI (コードページ 932) として選ぶ。どの PC でも同じになるよう、ANSI のコードページを 932 に固定する。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-SEQ-1M")],
            Hooks = new JsonObject { ["ansiCodePage"] = 932 },
        });
        await app.CommandAsync("Command_EncodingAnsi");
        await app.KeyAsync("Tab");
        await app.GoToAsync(0x10);

        await app.TypeAsync("日");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB, doc["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x93, 0xFA, 0x12 }, await app.BytesAsync(0x10, 3));
        Assert.Equal(0x12, doc["cursor"]!.GetValue<long>());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(UiTest.TC, "TC-EDIT-12-03")]
    public Task Unencodable_character_writes_nothing(bool insertMode) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-SEQ-1M")],
            Hooks = new JsonObject { ["ansiCodePage"] = 932 },
        });
        await app.KeyAsync("Tab");
        await app.GoToAsync(0x10);
        if (insertMode)
        {
            await app.KeyAsync("Insert");
        }

        // 1〜2. ASCII で é。
        await app.TypeAsync("é");
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB, doc["length"]!.GetValue<long>());
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.Contains(await NoticesAsync(app), n => n.Contains("“é”", StringComparison.Ordinal) && n.Contains("ASCII", StringComparison.Ordinal));

        // 3〜4. Shift_JIS (ANSI 932) で ß。近似文字 (? など) も書き込まない。
        await app.CommandAsync("Command_EncodingAnsi");
        await app.TypeAsync("ß");
        await app.IdleAsync();
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());
        Assert.Contains(await NoticesAsync(app), n => n.Contains("“ß”", StringComparison.Ordinal) && n.Contains("932", StringComparison.Ordinal));
    }, name: $"{nameof(Unencodable_character_writes_nothing)}_{insertMode}");

    [Fact(Skip = "フランス語のキーボード配列への切り替えとデッドキーの入力が必要 (システム全体のキーボード配列とキーボード入力を変えるため、作業中の PC では行わない)")]
    [Trait(UiTest.TC, "TC-EDIT-12-04")]
    public void Dead_keys_on_french_layout()
    {
    }

    [Fact(Skip = "UTF-16 LE / BE の文字コード (VIEW-21) はフェーズ 1 の機能で、フェーズ 0 では ASCII と ANSI だけを選べる")]
    [Trait(UiTest.TC, "TC-EDIT-12-05")]
    public void Emoji_in_utf16()
    {
    }

    [Fact(Skip = "中国語・韓国語の IME、タイ語・ベトナム語の入力方式の追加と切り替えが必要 (システム全体の入力方式)。また UTF-8 の文字コード (VIEW-21) はフェーズ 1")]
    [Trait(UiTest.TC, "TC-EDIT-12-06")]
    public void Chinese_korean_thai_and_vietnamese_input_methods()
    {
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-13-03")]
    public Task Delete_on_a_fixed_length_document_offers_fill_with_zero() => UiTestContext.RunAsync(async ctx =>
    {
        // 物理ディスク (TD-VHDX-MBR は管理者権限と仮想ディスクの接続が必要) の代わりに、長さを変えられない仮想データソースを使う。
        // そのため手順 7 (保存しないで閉じたあとのディスクの内容) は確かめない。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = FixedLengthSource(0x10000) });

        // 1. Ctrl+E (EDIT-04 はフェーズ 1) の代わりに命令で選択する。
        await app.SelectAsync(0x200, 0x10);
        byte[] before = await app.BytesAsync(0x200, 0x10);

        await app.KeyAsync("Delete");
        await app.IdleAsync();
        Assert.Equal(0x10000, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(before, await app.BytesAsync(0x200, 0x10));
        string notice = Assert.Single(await NoticesAsync(app));
        Assert.Contains("can't change length", notice);
        JsonObject action = null!;
        await app.WaitUntilAsync(async () => (action = await ElementAsync(app, "Notification_Action"))["found"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(10), "the button of the notice");
        Assert.Equal("Fill with 00", action["content"]!.GetValue<string>());

        // 5〜6. 「00 で塗りつぶす」。
        await app.CommandAsync("Notification_Action");
        await app.IdleAsync();
        Assert.Equal(new byte[0x10], await app.BytesAsync(0x200, 0x10));
        Assert.Equal(0x10000, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0xFF, 0x00 }, await app.BytesAsync(0x1FF, 2));
        Assert.Equal(new byte[] { 0x00, 0x10 }, await app.BytesAsync(0x20F, 2));
    });
}
