using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// 範囲を選択 (EDIT-04)、ファイルサイズの変更と切り詰め (EDIT-15)、読み取り専用 (EDIT-16)、形式を選択してコピー・貼り付け (EDIT-25・26)、
/// 塗りつぶし (EDIT-29)、ファイルの内容の挿入 (EDIT-30) のダイアログ。ダイアログの欄は UI オートメーションの値で入力し、ボタンは Invoke で押す
/// (マウス・キーボード・フォーカスを使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class EditCommandTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private static async Task<JsonObject> SelectionAsync(AppSession app) => await app.DocumentAsync();

    private static async Task<bool> PrimaryEnabledAsync(AppSession app) => (await app.WaitForAsync("PrimaryButton")).IsEnabled;

    /// <summary>
    /// ダイアログのボタンを押し、処理が終わるまで待つ。PrimaryButton などのダイアログのボタン以外は AutomationId で押す。
    /// <paramref name="waitForOperations"/> が偽なら、始まった長時間処理の終わりを待たない (進捗を見てキャンセルするテスト用。
    /// idle は長時間処理が終わるまで最大 10 秒待つため、10 秒前後で終わる処理では進捗を見る前に終わってしまう)。
    /// </summary>
    internal static async Task PressAsync(AppSession app, string button = "PrimaryButton", bool waitForOperations = true)
    {
        await app.WaitForAsync(button);
        if (button is "PrimaryButton" or "SecondaryButton" or "CloseButton")
        {
            int closed = (await app.SendAsync("openDialogs"))["closed"]!.GetValue<int>();
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = button });

            // 閉じる動きが終わって ShowAsync の後の処理が動くまで待つ。
            await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["closed"]!.GetValue<int>() > closed,
                TimeSpan.FromSeconds(10), "the dialog to close");
        }
        else
        {
            await app.CommandAsync(button);
        }

        if (waitForOperations)
        {
            await app.IdleAsync();
            await app.IdleAsync();
        }
    }

    private static async Task<string> TextAsync(AppSession app, string id)
    {
        // ダイアログの中身は開いた直後にはまだないことがあるため、見つかるまで待つ。
        JsonObject e = null!;
        await app.WaitUntilAsync(async () => (e = await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        return e["text"]?.GetValue<string>() ?? e["content"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>
    /// ダイアログの中のトグルを切り替える。ダイアログ自体は UI オートメーションに見えていても、中身が XAML の木に入るのは少し後のことが
    /// ある (リリースのビルド) ので、見つかるまで待つ。
    /// </summary>
    private static async Task SetCheckedAsync(AppSession app, string id, bool value = true)
    {
        await app.WaitUntilAsync(async () => (await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = id, ["value"] = value });
    }

    /// <summary>一覧の項目 (ダイアログの中身ができるまで待つ)。</summary>
    private static async Task<JsonObject> ListItemsAsync(AppSession app, string id)
    {
        await app.WaitUntilAsync(async () => (await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        await app.IdleAsync();
        return await app.SendAsync("listItems", new JsonObject { ["id"] = id });
    }

    /// <summary>一覧の項目を表示の文字列で選ぶ (ダイアログの中身ができるまで待つ)。</summary>
    internal static async Task SelectItemAsync(AppSession app, string id, string text)
    {
        await app.WaitUntilAsync(async () => (await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = id, ["index"] = 0, ["text"] = text });
        await app.IdleAsync();
    }

    private static async Task OpenSelectRangeAsync(AppSession app)
    {
        Assert.Equal("menu:Command_SelectRange", (await app.KeyAsync("E", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.WaitForAsync("SelectRangeDialog");
        await app.IdleAsync();
    }

    /// <summary>Ctrl+E で開始と長さを入れて選択する。</summary>
    internal static async Task SelectRangeAsync(AppSession app, string start, string length)
    {
        await OpenSelectRangeAsync(app);
        await app.UiaSetValueAsync("SelectRange_Start", start);
        await app.UiaSetValueAsync("SelectRange_Length", length);
        await PressAsync(app);
    }

    // ---- EDIT-04 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-04-01")]
    public Task Start_and_length_compute_the_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x40);
        await OpenSelectRangeAsync(app);
        Assert.Equal("0x40", await TextAsync(app, "SelectRange_Start"));
        Assert.Equal(string.Empty, await TextAsync(app, "SelectRange_Length"));

        await app.UiaSetValueAsync("SelectRange_Start", "0x100");
        await app.UiaSetValueAsync("SelectRange_Length", "0x20");
        JsonObject end = await ElementAsync(app, "SelectRange_End");
        Assert.Equal("0x11F", end["text"]!.GetValue<string>());
        Assert.Equal("Italic", end["fontStyle"]!.GetValue<string>());

        await app.UiaSetValueAsync("SelectRange_Length", "0");
        Assert.False(await PrimaryEnabledAsync(app));
        await app.UiaSetValueAsync("SelectRange_Length", "0x20");
        Assert.True(await PrimaryEnabledAsync(app));
        await PressAsync(app);

        JsonObject doc = await SelectionAsync(app);
        Assert.Equal(0x100, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(0x20, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(0x120, doc["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-04-02")]
    public Task Expressions_select_the_last_sixteen_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenSelectRangeAsync(app);
        await app.UiaSetValueAsync("SelectRange_Start", "end-0x10");
        await app.UiaSetValueAsync("SelectRange_End", "end-1");
        Assert.Contains("1,048,560 (0xFFFF0)", await TextAsync(app, "SelectRange_StartResult"));
        Assert.Contains("1,048,575 (0xFFFFF)", await TextAsync(app, "SelectRange_EndResult"));
        Assert.Equal("0x10", await TextAsync(app, "SelectRange_Length"));
        Assert.Contains("16 (0x10)", await TextAsync(app, "SelectRange_LengthResult"));
        await PressAsync(app);

        JsonObject doc = await SelectionAsync(app);
        Assert.Equal(0xFFFF0, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(16, doc["selectionLength"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-04-03")]
    public Task End_beyond_the_file_is_fixed_with_up_to_the_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenSelectRangeAsync(app);
        await app.UiaSetValueAsync("SelectRange_Start", "0xFFF00");
        await app.UiaSetValueAsync("SelectRange_End", "0xFFFFF");
        Assert.False((await ElementAsync(app, "SelectRange_End"))["errorBorder"]!.GetValue<bool>());
        Assert.True(await PrimaryEnabledAsync(app));

        await app.UiaSetValueAsync("SelectRange_End", "0x100000");
        Assert.True((await ElementAsync(app, "SelectRange_End"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("past the end", await TextAsync(app, "SelectRange_EndResult"));
        Assert.Equal("Visible", (await ElementAsync(app, "SelectRange_ClampEnd"))["visibility"]!.GetValue<string>());
        Assert.False(await PrimaryEnabledAsync(app));

        await PressAsync(app, "SelectRange_ClampEnd");
        Assert.Equal("0xFFFFF", await TextAsync(app, "SelectRange_End"));
        Assert.False((await ElementAsync(app, "SelectRange_End"))["errorBorder"]!.GetValue<bool>());
        Assert.True(await PrimaryEnabledAsync(app));

        await app.UiaSetValueAsync("SelectRange_Start", "0x100000");
        Assert.True((await ElementAsync(app, "SelectRange_Start"))["errorBorder"]!.GetValue<bool>());
        string message = await TextAsync(app, "SelectRange_StartResult");
        Assert.Contains("past the end", message);
        Assert.Contains("0x100000", message);
        await PressAsync(app, "CloseButton");
    });

    // ---- EDIT-15 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-15-02")]
    public Task Truncate_at_the_cursor_and_undo_from_the_notice() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-EDIT-SEQ-1K")] });
        await app.GoToAsync(0x100);
        await app.CommandAsync("Command_TruncateAtCursor");
        await app.IdleAsync();
        Assert.Equal(0x100, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Null(app.Find("ContentDialog"));
        Assert.Contains(await NoticesAsync(app), n => n.Contains("Cut off 768 bytes", StringComparison.Ordinal));

        await app.CommandAsync("Notification_Action");
        await app.IdleAsync();
        Assert.Equal(1024, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x00 }, await app.BytesAsync(0x100, 1));
        Assert.Equal(new byte[] { 0xFF }, await app.BytesAsync(0x3FF, 1));

        await app.KeyAsync("End", ctrl: true);
        Assert.False((await app.SendAsync("menuItem", new JsonObject { ["id"] = "Command_TruncateAtCursor" }))["enabled"]!.GetValue<bool>());
    });

    // ---- EDIT-16 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-16-01")]
    public Task Typing_in_a_read_only_tab_shows_one_notice() => UiTestContext.RunAsync(async ctx =>
    {
        // 「読み取り専用で開く」(ENG-14) の代わりに、開いた後に同じ理由で読み取り専用にする。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "OpenedReadOnly" });
        await app.GoToAsync(0x10);
        foreach (string digit in new[] { "4", "1", "4", "2" })
        {
            await app.TypeAsync(digit);
        }

        await app.SendAsync("setClipboard", new JsonObject { ["binary"] = "FF" });
        await app.KeyAsync("V", ctrl: true);
        await app.IdleAsync();

        Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, await app.BytesAsync(0x10, 4));
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        string notice = Assert.Single(await NoticesAsync(app));
        Assert.Contains("read-only", notice);
        JsonObject action = await ElementAsync(app, "Notification_Action");
        Assert.Equal("Allow editing", action["content"]!.GetValue<string>());

        // 4. ステータスバーとタブの見出しの鍵。
        Assert.Contains("Read-only", await app.UiaNameAsync("Status_Mode"));
        Assert.Contains("\U0001F512", await app.UiaNameAsync("Status_Mode"));
        Assert.StartsWith("\U0001F512", (await app.DocumentAsync())["header"]!.GetValue<string>());

        // 5. データを変えるコマンドは無効。
        foreach (string id in new[] { "Command_Paste", "Command_Fill", "Command_Undo", "Command_Save" })
        {
            Assert.False((await app.SendAsync("menuItem", new JsonObject { ["id"] = id }))["enabled"]!.GetValue<bool>(), id);
        }

        Assert.True((await app.SendAsync("menuItem", new JsonObject { ["id"] = "Command_ReadOnly" }))["checked"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-16-02")]
    public Task Copy_and_find_work_in_a_read_only_tab() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "OpenedReadOnly" });
        await SelectRangeAsync(app, "0x10", "4");
        await app.KeyAsync("C", ctrl: true);
        await app.IdleAsync();

        // コピーは実データを別のスレッドで読んでから入れるため、入るまで待つ。
        await app.WaitUntilAsync(async () => (await app.SendAsync("clipboard"))["binary"] is not null, TimeSpan.FromSeconds(10), "the copy");
        Assert.Equal("10111213", (await app.SendAsync("clipboard"))["binary"]!.GetValue<string>());

        await OperationsTests.OpenFindAsync(app, "20 21 22");
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionStart"]!.GetValue<long>() == 0x20, TimeSpan.FromSeconds(10), "the match");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x20, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(3, doc["selectionLength"]!.GetValue<long>());
        Assert.DoesNotContain(await NoticesAsync(app), n => n.Contains("read-only", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-16-03")]
    public Task Allow_editing_a_file_with_the_read_only_attribute() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-READONLY: TD-SEQ-1M の先頭 1 KiB に読み取り専用属性を付けたもの。読み取り専用属性での読み取り専用 (ENG-14) は、開いた後に
        // 同じ理由を設定して再現する。
        byte[] data = new byte[1024];
        TestDataCatalog.Expected("TD-SEQ-1M", 0, data);
        string path = ctx.WriteFile("TD-READONLY.bin", data);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
            await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "FileAttribute" });
            await app.GoToAsync(0x10);
            await app.TypeAsync("4");
            await app.WaitUntilAsync(async () => (await ElementAsync(app, "Notification_Action"))["found"]!.GetValue<bool>(),
                TimeSpan.FromSeconds(10), "the notice");
            await app.CommandAsync("Notification_Action");
            await app.WaitForAsync("ReadOnlyConfirmDialog");
            string body = AppSession.AllText(app.Find("ReadOnlyConfirmDialog")!);
            Assert.Contains("read-only attribute", body);
            await PressAsync(app);

            await app.GoToAsync(0x10);
            await app.TypeAsync("41");
            Assert.Equal(0x41, (await app.BytesAsync(0x10, 1))[0]);
            Assert.DoesNotContain("Read-only", await app.UiaNameAsync("Status_Mode"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    public Task Write_protection_hides_allow_editing() => UiTestContext.RunAsync(async ctx =>
    {
        // TC-EDIT-16-04 (フェーズ 5) の部品: 解除できない理由では「編集を許可する」を出さず、鍵のクリックでも解除しない。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("setReadOnly", new JsonObject { ["reason"] = "WriteProtectionMode" });
        await app.GoToAsync(0x10);
        await app.TypeAsync("4");
        await app.IdleAsync();
        Assert.Contains(await NoticesAsync(app), n => n.Contains("read-only", StringComparison.Ordinal));
        Assert.False((await ElementAsync(app, "Notification_Action"))["found"]!.GetValue<bool>());
        await app.CommandAsync("Status_Mode");
        await app.IdleAsync();
        Assert.True((await app.DocumentAsync())["readOnly"]!.GetValue<bool>());
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
    });

    // ---- EDIT-26 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-26-03")]
    public Task Paste_special_shows_candidates_and_pastes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x10);
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = "{ 0xDE, 0xAD, 0xBE, 0xEF }" });
        Assert.Equal("menu:Command_PasteSpecial", (await app.KeyAsync("V", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();

        JsonObject list = await ListItemsAsync(app, "PasteSpecial_Formats");
        int selected = list["selectedIndex"]!.GetValue<int>();
        Assert.StartsWith("Array notation", list["items"]![selected]!["text"]!.GetValue<string>());
        Assert.Contains("DE AD BE EF", await TextAsync(app, "PasteSpecial_Preview"));
        Assert.Contains("4 bytes", await TextAsync(app, "PasteSpecial_Length"));
        Assert.True(app.Find("PasteSpecial_Insert")!.Patterns.SelectionItem.Pattern.IsSelected.Value);
        await PressAsync(app);

        Assert.Equal(MiB + 4, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, await app.BytesAsync(0x10, 4));

        await app.SendAsync("setClipboard", new JsonObject { ["text"] = "DEADBEE" });
        await app.KeyAsync("V", ctrl: true, shift: true);
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();
        list = await ListItemsAsync(app, "PasteSpecial_Formats");
        JsonObject hex = list["items"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["text"]!.GetValue<string>().StartsWith("Hex string", StringComparison.Ordinal));
        Assert.Contains("Error: line 1, character 7", hex["text"]!.GetValue<string>());
        Assert.False(hex["enabled"]!.GetValue<bool>());
        await PressAsync(app, "CloseButton");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-02")]
    public Task Base64_pasted_into_the_hex_column_opens_paste_special() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await app.GoToAsync(0x10);
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = "SGVsbG8=" });
        await app.KeyAsync("V", ctrl: true);
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();
        JsonObject list = await ListItemsAsync(app, "PasteSpecial_Formats");
        Assert.StartsWith("Base64", list["items"]![list["selectedIndex"]!.GetValue<int>()]!["text"]!.GetValue<string>());
        Assert.Contains("48 65 6C 6C 6F", await TextAsync(app, "PasteSpecial_Preview"));
        Assert.Contains("5 bytes", await TextAsync(app, "PasteSpecial_Length"));
        await PressAsync(app);
        Assert.Equal("Hello"u8.ToArray(), await app.BytesAsync(0x10, 5));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-23-01")]
    public Task Odd_hex_text_opens_paste_special_with_base64() => UiTestContext.RunAsync(async ctx =>
    {
        // TC-EDIT-23-01 の手順 3〜4 (手順 1〜2 は ClipboardTests)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = "DEADBEE" });
        await app.KeyAsync("V", ctrl: true);
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        JsonObject list = await ListItemsAsync(app, "PasteSpecial_Formats");
        Assert.StartsWith("Base64", list["items"]![list["selectedIndex"]!.GetValue<int>()]!["text"]!.GetValue<string>());
        Assert.Contains(list["items"]!.AsArray(), i => i!["text"]!.GetValue<string>().StartsWith("Hex string — Error", StringComparison.Ordinal)
            && !i["enabled"]!.GetValue<bool>());
        await PressAsync(app, "CloseButton");
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-26-04")]
    public Task Intel_hex_is_written_at_the_record_addresses() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-FF-1M")] });
        await app.GoToAsync(0x80000);
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = TestDataCatalog.IhexSmall });
        await app.KeyAsync("V", ctrl: true, shift: true);
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();
        JsonObject list = await ListItemsAsync(app, "PasteSpecial_Formats");
        Assert.StartsWith("Intel HEX", list["items"]![list["selectedIndex"]!.GetValue<int>()]!["text"]!.GetValue<string>());
        await SetCheckedAsync(app, "PasteSpecial_AtAddress");
        await app.IdleAsync();
        await PressAsync(app);

        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(0xFF, (await app.BytesAsync(0x0F, 1))[0]);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), await app.BytesAsync(0x10, 16));
        Assert.Equal(0xFF, (await app.BytesAsync(0x20, 1))[0]);
        Assert.Equal(0xFF, (await app.BytesAsync(0xFF, 1))[0]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xFF }, await app.BytesAsync(0x100, 5));
        Assert.Equal(0xFF, (await app.BytesAsync(0x80000, 1))[0]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-26-05")]
    public Task Intel_hex_with_a_gap_is_pasted_at_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await app.GoToAsync(0x80000);
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = TestDataCatalog.IhexSmall });
        await app.KeyAsync("V", ctrl: true, shift: true);
        await app.WaitForAsync("PasteSpecialDialog");
        await app.IdleAsync();
        await SetCheckedAsync(app, "PasteSpecial_AtCursor");
        await SetCheckedAsync(app, "PasteSpecial_Overwrite");
        await app.IdleAsync();
        Assert.Contains("244 bytes", await TextAsync(app, "PasteSpecial_Length"));
        await PressAsync(app);

        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(0x00, (await app.BytesAsync(0x7FFFF, 1))[0]);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), await app.BytesAsync(0x80000, 16));
        Assert.Equal(0xFF, (await app.BytesAsync(0x80010, 1))[0]);
        Assert.Equal(0xFF, (await app.BytesAsync(0x800EF, 1))[0]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00 }, await app.BytesAsync(0x800F0, 5));
        Assert.Equal(new byte[16], await app.BytesAsync(0x10, 16));
    });

    // ---- EDIT-25 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-25-04")]
    [Trait(UiTest.Category, "Nightly")]
    public Task A_100_MiB_c_array_is_saved_to_a_file() => UiTestContext.RunAsync(async ctx =>
    {
        string source = TestDataCatalog.Generate("TD-MARKERS-1G", ctx.Root);
        string target = Path.Combine(ctx.Root, "data.c");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["savePicker"] = target } });
        await SelectRangeAsync(app, "0", "0x6400000");
        Assert.Equal("menu:Command_CopyAs", (await app.KeyAsync("C", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        await app.WaitForAsync("CopyAsDialog");
        await SelectItemAsync(app, "CopyAs_Formats", "Array: C");
        await app.IdleAsync();
        // 推定サイズは 64 MiB を超える (「(N bytes)」の N で比べる)。
        string estimate = await TextAsync(app, "CopyAs_Estimate");
        long estimated = long.Parse(System.Text.RegularExpressions.Regex.Match(estimate, @"\(([\d,]+) bytes\)").Groups[1].Value.Replace(",", string.Empty));
        Assert.True(estimated > 64 * MiB, estimate);
        Assert.False(await PrimaryEnabledAsync(app));
        Assert.True((await app.WaitForAsync("SecondaryButton")).IsEnabled);
        await PressAsync(app, "SecondaryButton");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(target)), TimeSpan.FromMinutes(5), "the saved file");

        using var reader = new StreamReader(target);
        Assert.StartsWith("unsigned char data[104857600] = {", reader.ReadLine());
        Assert.StartsWith("    0x40, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,", reader.ReadLine());
        long elements = 16;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            elements += line.Count(c => c == 'x');
        }

        Assert.Equal(104_857_600, elements);
    });

    // ---- EDIT-29 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-29-03")]
    public Task Fill_with_a_pattern_from_the_dialog() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SelectRangeAsync(app, "0x10", "5");
        await app.CommandAsync("Command_Fill");
        await app.WaitForAsync("FillDialog");
        await app.IdleAsync();
        await SelectItemAsync(app, "Fill_Kind", "Hex pattern");
        await app.UiaSetValueAsync("Fill_Pattern", "DE AD");
        Assert.Contains("10 11 12 13 14", await TextAsync(app, "Fill_PreviewBefore"));
        Assert.Contains("DE AD DE AD DE", await TextAsync(app, "Fill_PreviewAfter"));

        await app.UiaSetValueAsync("Fill_Pattern", "DE A");
        Assert.True((await ElementAsync(app, "Fill_Pattern"))["errorBorder"]!.GetValue<bool>());
        Assert.NotEqual(string.Empty, await TextAsync(app, "Fill_Error"));
        Assert.False(await PrimaryEnabledAsync(app));

        await app.UiaSetValueAsync("Fill_Pattern", "DE AD");
        await PressAsync(app);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xDE, 0xAD, 0xDE, 0x15 }, await app.BytesAsync(0x10, 6));
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-29-04")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Cancelling_a_cryptographic_random_fill_changes_nothing() => UiTestContext.RunAsync(async ctx =>
    {
        string source = TestDataCatalog.Generate("TD-SPARSE-100G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["freeSpace"] = 1L << 40 } });
        await SelectRangeAsync(app, "0", "0x280000000");
        await app.CommandAsync("Command_Fill");
        await app.WaitForAsync("FillDialog");
        await app.IdleAsync();
        await SelectItemAsync(app, "Fill_Kind", "Cryptographic random numbers");
        await app.IdleAsync();
        await PressAsync(app, waitForOperations: false);

        await CancelWhenProgressAsync(app, 0.10);
        Assert.Equal(TestDataCatalog.Marker(0), await app.BytesAsync(0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x40000000), await app.BytesAsync(0x40000000, 17));
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(107_374_182_400, doc["length"]!.GetValue<long>());
        Assert.False(doc["modified"]!.GetValue<bool>());
        Assert.DoesNotContain((await app.SendAsync("documentTempFiles"))["files"]!.AsArray(), f => f!.GetValue<string>().StartsWith("fill-", StringComparison.Ordinal));
    });

    /// <summary>処理センターの進捗が <paramref name="fraction"/> 以上になったら処理をキャンセルし、終わるまで待つ。</summary>
    private static async Task CancelWhenProgressAsync(AppSession app, double fraction)
    {
        await app.WaitUntilAsync(async () =>
        {
            JsonArray ops = (await app.StateAsync())["operations"]!.AsArray();
            return ops.Select(o => o!.AsObject()).Any(o => o["state"]!.GetValue<string>() == "Running"
                && o["total"]?.GetValue<long>() is long total && total > 0 && o["processed"]!.GetValue<long>() >= total * fraction);
        }, TimeSpan.FromMinutes(5), "the progress");
        await app.UiaInvokeAsync("Status_Operations");
        await app.UiaInvokeAsync("Operations_Cancel");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, TimeSpan.FromMinutes(2), "the cancel");
        await app.IdleAsync();
    }

    // ---- EDIT-30 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-30-01")]
    public Task Insert_part_of_a_file_at_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        string file = TestDataCatalog.Generate("TD-EDIT-SEQ-1K", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-ZERO-1M")],
            Hooks = new JsonObject { ["openPicker"] = new JsonArray(file) },
        });
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x10);
        await app.CommandAsync("Command_InsertFile");
        await app.WaitForAsync("InsertFileDialog");
        await app.IdleAsync();
        Assert.Contains("1,024 bytes", await TextAsync(app, "InsertFile_Size"));
        Assert.Contains("00 01 02 03", await TextAsync(app, "InsertFile_Preview"));
        await app.UiaSetValueAsync("InsertFile_Offset", "0x100");
        await app.UiaSetValueAsync("InsertFile_Length", "0x80");
        await SetCheckedAsync(app, "InsertFile_Insert");
        await PressAsync(app);

        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(1_048_704, doc["length"]!.GetValue<long>());
        Assert.Equal(0x00, (await app.BytesAsync(0x0F, 1))[0]);
        Assert.Equal(Enumerable.Range(0, 0x80).Select(i => (byte)i).ToArray(), await app.BytesAsync(0x10, 0x80));
        Assert.Equal(0x00, (await app.BytesAsync(0x90, 1))[0]);
        Assert.Equal(0x10, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(0x80, doc["selectionLength"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-30-02")]
    public Task Inserted_content_does_not_follow_later_changes_to_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] seq = new byte[1024];
        TestDataCatalog.Expected("TD-SEQ-1M", 0, seq);
        string src = ctx.WriteFile("src.bin", seq);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-ZERO-1M")],
            Hooks = new JsonObject { ["openPicker"] = new JsonArray(src) },
        });
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x10);
        await app.CommandAsync("Command_InsertFile");
        await app.WaitForAsync("InsertFileDialog");
        await app.IdleAsync();
        await SetCheckedAsync(app, "InsertFile_Insert");
        await PressAsync(app);
        await app.IdleAsync();

        File.WriteAllBytes(src, Enumerable.Repeat((byte)0xFF, 1024).ToArray());
        using (var stream = new FileStream(src, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(16);
        }

        Assert.Equal(seq, await app.BytesAsync(0x10, 1024));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-30-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Cancelling_the_insertion_of_a_large_file_changes_nothing() => UiTestContext.RunAsync(async ctx =>
    {
        string big = TestDataCatalog.Generate("TD-EDIT-SPARSE-10G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-SEQ-1M")],
            Hooks = new JsonObject
            {
                ["openPicker"] = new JsonArray(big),
                ["freeSpace"] = 1L << 40,
                ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-EDIT-SPARSE-10G.bin", ["delayMs"] = 2 }),
            },
        });
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x10);
        await app.CommandAsync("Command_InsertFile");
        await app.WaitForAsync("InsertFileDialog");
        await app.IdleAsync();
        await SetCheckedAsync(app, "InsertFile_Insert");
        await PressAsync(app, waitForOperations: false);

        await CancelWhenProgressAsync(app, 0.05);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(MiB, doc["length"]!.GetValue<long>());
        Assert.Equal(0x10, (await app.BytesAsync(0x10, 1))[0]);
        Assert.False(doc["modified"]!.GetValue<bool>());
        Assert.DoesNotContain((await app.SendAsync("documentTempFiles"))["files"]!.AsArray(), f => f!.GetValue<string>().StartsWith("fill-", StringComparison.Ordinal));
    });
}
