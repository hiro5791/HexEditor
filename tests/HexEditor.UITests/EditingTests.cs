using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>入力・削除・入力モード (EDIT-10〜EDIT-13)、Undo / Redo と「変更あり」(ENG-05、ENG-20、EDIT-19、VIEW-40、UI-02)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class EditingTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-05-05")]
    public Task Undo_to_the_saved_point_clears_modified() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-BYTES-256")] });

        // 1. オフセット 0 に AA を上書きし、Ctrl+S で保存する。
        await app.TypeAsync("AA");
        await SaveAsync(app);
        Assert.False(await ModifiedAsync(app));

        // 2. オフセット 1 に BB を上書きすると「変更あり」。
        await app.TypeAsync("BB");
        Assert.True(await ModifiedAsync(app));

        // 3. Ctrl+Z で保存した時点に戻ると消え、4. もう一度 Ctrl+Z で再び出る。
        await app.KeyAsync("Z", ctrl: true);
        Assert.False(await ModifiedAsync(app));
        await app.KeyAsync("Z", ctrl: true);
        Assert.True(await ModifiedAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-20-04")]
    public Task Undo_and_redo_after_save() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-BYTES-256")] });
        await app.TypeAsync("AA");
        await SaveAsync(app);

        await app.KeyAsync("Z", ctrl: true);
        Assert.True(await ModifiedAsync(app));
        await app.KeyAsync("Y", ctrl: true);
        Assert.False(await ModifiedAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-19-06")]
    public Task Undo_after_save_shows_modified_and_keeps_history() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("FF");
        await SaveAsync(app);
        Assert.False(await ModifiedAsync(app));

        await app.KeyAsync("Z", ctrl: true);
        Assert.True(await ModifiedAsync(app));
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);

        await app.KeyAsync("Y", ctrl: true);
        Assert.False(await ModifiedAsync(app));
        Assert.Equal(0xFF, (await app.BytesAsync(0x10, 1))[0]);

        // 保存しても履歴は消えない (編集 1 件)。
        Assert.Equal(1, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-40-03")]
    public Task Modified_indicator_is_shown_and_cleared_by_save() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        Assert.Equal(string.Empty, app.TextOrEmpty("Status_Modified"));

        await app.TypeAsync("FF");
        Assert.Equal("● Modified", await app.UiaNameAsync("Status_Modified"));
        Assert.StartsWith("● ", (await app.DocumentAsync())["header"]!.GetValue<string>(), StringComparison.Ordinal);

        await SaveAsync(app);
        Assert.Equal(string.Empty, app.TextOrEmpty("Status_Modified"));
        Assert.DoesNotContain("●", (await app.DocumentAsync())["header"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-02-01")]
    public Task Title_shows_unsaved_mark() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.TypeAsync("41");
        // プレビュー版の版ではアプリ名の後に「Preview」が付く (PKG-28)。
        Assert.Matches(@"^● seq\.bin - HexEditor( Preview)?( \(Administrator\))?$", (await app.StateAsync())["title"]!.GetValue<string>());
        await SaveAsync(app);
        Assert.Matches(@"^seq\.bin - HexEditor( Preview)?( \(Administrator\))?$", (await app.StateAsync())["title"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-10-01")]
    public Task Insert_key_switches_mode_and_caret_shape() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await AssertModeAsync(app, "Overwrite", "box");

        await app.KeyAsync("Insert");
        await AssertModeAsync(app, "Insert", "bar");

        await app.KeyAsync("Insert");
        await AssertModeAsync(app, "Overwrite", "box");

        // モードの切り替えは履歴に残らず、元に戻すは無効。
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.False(doc["canUndo"]!.GetValue<bool>());

        static async Task AssertModeAsync(AppSession app, string mode, string shape)
        {
            Assert.Equal(mode, await app.UiaNameAsync("Status_Mode"));
            JsonObject caret = (await app.RenderAsync())["caret"]!.AsObject();
            Assert.Equal(shape, caret["shape"]!.GetValue<string>());
            double width = caret["width"]!.GetValue<double>();
            double cell = (await app.RenderAsync())["cellWidth"]!.GetValue<double>();

            // ブロックはセルの幅の 60% 以上、縦線は 3 px 以下。
            Assert.True(shape == "box" ? width >= cell * 0.6 : width <= 3, $"caret width {width} (cell {cell})");
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-10-03")]
    public Task Each_tab_keeps_its_own_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M"), ctx.CopyTestData("TD-ZERO-1M")] });
        await SelectTabAsync(app, 0);
        await app.KeyAsync("Insert");

        // Ctrl+Tab の代わりに、タブを命令で切り替える (Ctrl+Tab は TabView のキー処理で、注入の対象外)。
        await SelectTabAsync(app, 1);
        Assert.Equal("Overwrite", await app.UiaNameAsync("Status_Mode"));
        await SelectTabAsync(app, 0);
        Assert.Equal("Insert", await app.UiaNameAsync("Status_Mode"));

        await SelectTabAsync(app, 1);
        await app.TypeAsync("12");
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(0x12, (await app.BytesAsync(0, 1))[0]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-11-01")]
    public Task Typing_two_digits_in_overwrite_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);

        await app.TypeAsync("4");
        Assert.Equal(0x40, (await app.BytesAsync(0x10, 1))[0]);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x10, doc["cursor"]!.GetValue<long>());
        Assert.True(doc["lowNibble"]!.GetValue<bool>());

        await app.TypeAsync("1");
        doc = await app.DocumentAsync();
        Assert.Equal(0x41, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0x11, doc["cursor"]!.GetValue<long>());
        Assert.False(doc["lowNibble"]!.GetValue<bool>());
        Assert.Equal(MiB, doc["length"]!.GetValue<long>());

        await app.TypeAsync("ab");
        Assert.Equal(0xAB, (await app.BytesAsync(0x11, 1))[0]);

        // 0x10 と 0x11 が変更されたバイトとして下線付きで描かれる。
        JsonObject row = (await app.RenderAsync())["rows"]!.AsArray()
            .Single(r => r!["offsetText"]!.GetValue<string>() == "00000010")!.AsObject();
        Assert.Contains("Underline", row["cells"]![0]!["decoration"]!.GetValue<string>());
        Assert.Contains("Underline", row["cells"]![1]!["decoration"]!.GetValue<string>());
        Assert.DoesNotContain("Underline", row["cells"]![2]!["decoration"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-11-02")]
    public Task Typing_digits_in_insert_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.KeyAsync("Insert");

        await app.TypeAsync("7");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB + 1, doc["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x70, 0x10 }, await app.BytesAsync(0x10, 2));
        Assert.True(doc["lowNibble"]!.GetValue<bool>());

        await app.TypeAsync("F");
        doc = await app.DocumentAsync();
        Assert.Equal(MiB + 1, doc["length"]!.GetValue<long>());
        Assert.Equal(0x7F, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0x11, doc["cursor"]!.GetValue<long>());
        Assert.False(doc["lowNibble"]!.GetValue<bool>());

        await app.KeyAsync("End", ctrl: true);
        await app.TypeAsync("55");
        doc = await app.DocumentAsync();
        Assert.Equal(MiB + 2, doc["length"]!.GetValue<long>());
        Assert.Equal(0x55, (await app.BytesAsync(MiB + 1, 1))[0]);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(UiTest.TC, "TC-EDIT-11-03")]
    public Task Non_hex_keys_do_not_change_data(bool insertMode) => UiTestContext.RunAsync(async ctx =>
    {
        // 「音を鳴らす呼び出しがない」はアプリが MessageBeep を使っていないため対象外 (データとカーソルだけ確かめる)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        if (insertMode)
        {
            await app.KeyAsync("Insert");
        }

        await app.TypeAsync("G-Z; ");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB, doc["length"]!.GetValue<long>());
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(0x10, doc["cursor"]!.GetValue<long>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
    }, name: $"{nameof(Non_hex_keys_do_not_change_data)}_{insertMode}");

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-13-02")]
    public Task Delete_and_backspace() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);

        // 1〜2. 挿入モードの Backspace は前の 1 バイトを消す。
        await app.KeyAsync("Insert");
        await app.KeyAsync("Back");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB - 1, doc["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x0E, 0x10 }, await app.BytesAsync(0x0E, 2));
        Assert.Equal(0x0F, doc["cursor"]!.GetValue<long>());

        // 3〜4. Delete はカーソル位置の 1 バイトを消す。
        await app.KeyAsync("Delete");
        Assert.Equal(MiB - 2, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(0x11, (await app.BytesAsync(0x0F, 1))[0]);

        // 5〜6. 上書きモードの Backspace はデータを変えず、1 ニブル戻る。
        await app.KeyAsync("Insert");
        byte[] before = await app.BytesAsync(0x1F, 2);
        await app.GoToAsync(0x20);
        await app.KeyAsync("Back");
        doc = await app.DocumentAsync();
        Assert.Equal(MiB - 2, doc["length"]!.GetValue<long>());
        Assert.Equal(before, await app.BytesAsync(0x1F, 2));
        Assert.Equal(0x1F, doc["cursor"]!.GetValue<long>());
        Assert.True(doc["lowNibble"]!.GetValue<bool>());

        // 7〜8. テキスト列の上書きモードの Backspace は 1 バイト戻るだけ。
        await app.KeyAsync("Tab");
        await app.GoToAsync(0x30);
        byte old = (await app.BytesAsync(0x2F, 1))[0];
        await app.KeyAsync("Back");
        Assert.Equal(0x2F, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        Assert.Equal(old, (await app.BytesAsync(0x2F, 1))[0]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-19-04")]
    public Task Continuous_typing_is_undone_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await app.KeyAsync("Tab");
        await app.GoToAsync(0x10);
        foreach (char c in "ABCD")
        {
            await app.TypeAsync(c.ToString());
            await Task.Delay(100);
        }

        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[4], await app.BytesAsync(0x10, 4));

        // 2 秒以上の間が空くと別の操作になる。
        await app.GoToAsync(0x20);
        await app.TypeAsync("A");
        await Task.Delay(2500);
        await app.TypeAsync("B");
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[] { 0x41, 0x00 }, await app.BytesAsync(0x20, 2));
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[2], await app.BytesAsync(0x20, 2));
    });

    internal static async Task SaveAsync(AppSession app)
    {
        JsonObject r = await app.KeyAsync("S", ctrl: true);
        Assert.Equal("menu:Command_Save", r["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>()
            || (await app.StateAsync())["notice"]!["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(30), "the save to finish");
    }

    /// <summary>「変更あり」の表示: タブの見出しの ● とステータスバーの両方を見る。</summary>
    internal static async Task<bool> ModifiedAsync(AppSession app)
    {
        JsonObject doc = await app.DocumentAsync();
        bool header = doc["header"]!.GetValue<string>().StartsWith("● ", StringComparison.Ordinal);
        bool status = app.TextOrEmpty("Status_Modified") == "● Modified";
        Assert.Equal(header, status);
        Assert.Equal(doc["modified"]!.GetValue<bool>(), status);
        return status;
    }

    internal static async Task SelectTabAsync(AppSession app, int index)
    {
        await app.SendAsync("selectTab", new JsonObject { ["index"] = index });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the tab");

        // 選択したタブの Hex ビューが読み込まれるまで待つ (描画内容を読めるようになる)。
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await app.RenderAsync();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10), "the hex view of the tab");
        await app.IdleAsync();
    }
}
