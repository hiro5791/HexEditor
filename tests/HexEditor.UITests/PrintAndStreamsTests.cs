using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using UglyToad.PdfPig;

namespace HexEditor.UITests;

/// <summary>
/// 印刷・印刷プレビュー・PDF への出力 (TOOL-01〜03) と、NTFS 代替データストリーム (ENG-37、TOOL-21)。物理的なプリンタには何も送らない
/// (印刷は、テスト用のフック printToFile で同じページの内容を PDF に書く)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PrintAndStreamsTests
{
    private static JsonObject Hooks(Action<JsonObject>? more = null)
    {
        var hooks = new JsonObject { ["culture"] = "en-US", ["timeZone"] = "UTC" };
        more?.Invoke(hooks);
        return hooks;
    }

    private static Task<JsonObject> Print(AppSession app, string action, params (string Key, object? Value)[] values) =>
        app.SendAsync("tcPrint", ToolsCalcTests.Args(action, values));

    private static Task<JsonObject> Ads(AppSession app, string action, params (string Key, object? Value)[] values) =>
        app.SendAsync("tcAds", ToolsCalcTests.Args(action, values));

    private static async Task OpenPrintDialogAsync(AppSession app)
    {
        JsonObject result = await app.KeyAsync("P", ctrl: true);
        Assert.Equal("menu:Command_Print", result["handledBy"]!.GetValue<string>());
        await app.WaitForAsync("PrintDialog");
        await app.WaitUntilAsync(async () => (await Print(app, "state"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the print dialog");
    }

    private static async Task WaitForFileAsync(string path)
    {
        DateTime end = DateTime.UtcNow + UiTest.Scaled(TimeSpan.FromSeconds(60));
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException($"Timed out waiting for {path}.");
            }

            await Task.Delay(50);
        }

        // 書き終えるのを待つ (一時ファイルから置き換えるため、現れた時点で完成している)。
        await Task.Delay(100);
    }

    // ---- TOOL-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-01-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_P_opens_the_print_dialog_and_a_selection_becomes_the_default_range() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")], Hooks = Hooks() });
        await OpenPrintDialogAsync(app);
        JsonObject state = await Print(app, "state");
        Assert.Equal("All", state["range"]!.GetValue<string>());
        Assert.False(state["selectionEnabled"]!.GetValue<bool>());
        Assert.True(state["hasPreview"]!.GetValue<bool>());
        await app.InvokeDialogButtonAsync("Cancel");

        await app.SelectAsync(0x100, 0x200);
        await OpenPrintDialogAsync(app);
        state = await Print(app, "state");
        Assert.Equal("Selection", state["range"]!.GetValue<string>());
        Assert.True(state["selectionEnabled"]!.GetValue<bool>());
        Assert.True(state["hasPreview"]!.GetValue<bool>());
        await app.InvokeDialogButtonAsync("Cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-01-02")]
    public Task Header_and_footer_symbols_are_replaced_in_the_printed_pages() => UiTestContext.RunAsync(async ctx =>
    {
        string copy = ctx.CopyTestData("TD-SEQ-1M", "print.bin");
        string pdf = Path.Combine(ctx.Root, "out.pdf");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [copy],
            Hooks = Hooks(h =>
            {
                h["frozenTime"] = "2026-10-07T14:03:00";
                h["printToFile"] = pdf;
            }),
        });
        await app.GoToAsync(0);
        await app.TypeAsync("FF");

        await OpenPrintDialogAsync(app);
        foreach ((string slot, string text) in new[]
        {
            ("headerLeft", "{name}"), ("headerCenter", "{path}"), ("headerRight", "{range}"),
            ("footerLeft", "{size} {modified}"), ("footerCenter", "{page} / {pages}"), ("footerRight", "{date} {time}"),
        })
        {
            await Print(app, "text", ("slot", slot), ("text", text));
        }

        await Print(app, "range", ("kind", "Offsets"), ("text", "0:0x1000"));
        JsonObject state = await Print(app, "state");
        Assert.Equal("Offsets", state["range"]!.GetValue<string>());
        long pages = state["printedPages"]!.GetValue<long>();
        Assert.True(pages >= 2);
        await app.InvokeDialogButtonAsync("Print", idle: false);
        await WaitForFileAsync(pdf);

        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.Equal(pages, document.NumberOfPages);
        for (int p = 1; p <= document.NumberOfPages; p++)
        {
            string text = document.GetPage(p).Text;
            Assert.Contains("print.bin", text, StringComparison.Ordinal);
            Assert.Contains(copy, text, StringComparison.Ordinal);
            Assert.Contains("0x0-0xFFF", text, StringComparison.Ordinal);
            Assert.Contains("1,048,576 B (modified)", text, StringComparison.Ordinal);
            Assert.Contains($"{p} / {document.NumberOfPages}", text, StringComparison.Ordinal);
            Assert.Contains("10/7/2026 2:03 PM", text, StringComparison.Ordinal);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-01-05")]
    public Task More_than_a_thousand_pages_ask_for_confirmation_first() => UiTestContext.RunAsync(async ctx =>
    {
        string pdf = Path.Combine(ctx.Root, "out.pdf");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")], Hooks = Hooks(h => h["printToFile"] = pdf) });
        await OpenPrintDialogAsync(app);
        JsonObject state = await Print(app, "state");
        long pages = state["printedPages"]!.GetValue<long>();
        Assert.True(pages > 1000);
        Assert.True(state["needsConfirmation"]!.GetValue<bool>());

        await app.InvokeDialogButtonAsync("Print", idle: false);
        var dialog = await app.WaitForDialogAsync("PrintConfirmDialog");
        string text = await app.WaitForDialogTextAsync(dialog, pages.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), "amount of paper");
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains("Print", buttons);
        Assert.Contains("Change range", buttons);
        Assert.Contains("paper", text, StringComparison.Ordinal);

        // 「範囲を変える」: 印刷を始めず、印刷ダイアログに戻る。
        await app.InvokeDialogButtonAsync("Change range", idle: false);
        await app.WaitForAsync("PrintDialog");
        await app.WaitUntilAsync(async () => (await Print(app, "state"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the print dialog again");
        Assert.False(File.Exists(pdf));

        await Print(app, "range", ("kind", "Pages"), ("text", "1-3"));
        await app.InvokeDialogButtonAsync("Print", idle: false);
        await WaitForFileAsync(pdf);
        Assert.Empty(app.Window.FindAllDescendants(cf => cf.ByAutomationId("PrintConfirmDialog")));
        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.Equal(3, document.NumberOfPages);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-01-06")]
    public Task Edits_during_the_print_do_not_change_the_printed_content() => UiTestContext.RunAsync(async ctx =>
    {
        string copy = ctx.CopyTestData("TD-SEQ-1M", "p.bin");
        string pdf = Path.Combine(ctx.Root, "out.pdf");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [copy],
            Hooks = Hooks(h =>
            {
                h["printToFile"] = pdf;
                h["fileSources"] = new JsonArray(new JsonObject { ["match"] = "p.bin", ["delayMs"] = 100 });
            }),
        });
        await OpenPrintDialogAsync(app);
        await Print(app, "range", ("kind", "Offsets"), ("text", "0:0x10000"));
        await app.InvokeDialogButtonAsync("Print", idle: false);

        // 印刷の処理が動いている間に、0xFF00 を書き換える (印刷中も編集できる)。
        await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray()
            .Any(o => o!["name"]!.GetValue<string>().StartsWith("Printing", StringComparison.Ordinal) && o["state"]!.GetValue<string>() == "Running"),
            UiTest.Scaled(TimeSpan.FromSeconds(15)), "the print operation");
        await app.GoToAsync(0xFF00);
        await app.TypeAsync("EE");
        Assert.Equal(0xEE, (await app.BytesAsync(0xFF00, 1))[0]);
        await WaitForFileAsync(pdf);

        using PdfDocument document = PdfDocument.Open(pdf);
        string all = string.Join('\n', document.GetPages().Select(p => p.Text));
        System.Text.RegularExpressions.Match row = System.Text.RegularExpressions.Regex.Match(all, @"FF00\s*((?:[0-9A-F]{2} ?)+)");
        Assert.True(row.Success, all[..Math.Min(400, all.Length)]);
        Assert.StartsWith("00 01 02", row.Groups[1].Value, StringComparison.Ordinal);
    });

    // ---- TOOL-02 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-02-01")]
    public Task Changing_the_font_size_updates_the_preview_and_the_page_count_quickly() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")], Hooks = Hooks() });
        await OpenPrintDialogAsync(app);
        JsonObject before = await Print(app, "state");

        JsonObject after = await Print(app, "fontSize", ("size", 12.0));
        Assert.True(after["printedPages"]!.GetValue<long>() > before["printedPages"]!.GetValue<long>());
        Assert.NotEqual(before["text"]!.GetValue<string>(), after["text"]!.GetValue<string>());
        Assert.Equal(12.0, after["fontSize"]!.GetValue<double>());
        double refresh = after["refreshMs"]!.GetValue<double>();
        // 設定の変更から計画とプレビューが変わるまで (アプリの中で測る)。共有のランナーでは警告にする (性能の判定は性能テストの環境)。
        TimeBudget.Limit(refresh <= 500, $"{refresh} ms");
        Assert.True(refresh < 5000, $"{refresh} ms");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-02-03")]
    public Task Preview_pages_can_be_turned_with_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")], Hooks = Hooks() });
        await OpenPrintDialogAsync(app);
        long last = (await Print(app, "state"))["pageCount"]!.GetValue<long>() - 1;
        await Print(app, "focus");

        async Task<long> KeyAsync(string key) => (await Print(app, "key", ("key", key)))["page"]!.GetValue<long>();
        Assert.Equal(1, await KeyAsync("PageDown"));
        Assert.Equal(2, await KeyAsync("PageDown"));
        Assert.Equal(1, await KeyAsync("PageUp"));
        Assert.Equal(last, await KeyAsync("End"));
        Assert.Equal(0, await KeyAsync("Home"));
        JsonObject typed = await Print(app, "pageNumber", ("text", "5"));
        Assert.Equal(4, typed["page"]!.GetValue<long>());
        Assert.Contains("5 /", typed["info"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    // ---- TOOL-03 ----

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-03-01")]
    public Task Exported_pdf_has_searchable_text_and_document_information() => UiTestContext.RunAsync(async ctx =>
    {
        string pdf = Path.Combine(ctx.Root, "out.pdf");
        string source = ctx.CopyTestData("TD-BYTES-256");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = Hooks(h => h["savePicker"] = pdf) });

        await app.CommandAsync("Command_ExportPdf");
        await app.WaitForAsync("PrintDialog");
        await app.WaitUntilAsync(async () => (await Print(app, "state"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the PDF dialog");
        await app.InvokeDialogButtonAsync("Save as PDF...", idle: false);
        await WaitForFileAsync(pdf);

        using PdfDocument document = PdfDocument.Open(pdf);
        string text = document.GetPage(1).Text;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "7C 7D 7E 7F"));
        Assert.Equal(Path.GetFileName(source), document.Information.Title);
        Assert.Equal("HexEditor", document.Information.Creator);
        Assert.NotNull(document.Information.CreationDate);
        Assert.True(document.TryGetBookmarks(out UglyToad.PdfPig.Outline.Bookmarks? bookmarks));
        Assert.Equal("0x0-0xFF", Assert.Single(bookmarks!.GetNodes()).Title);
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.Contains("/FontFile2", raw, StringComparison.Ordinal);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+", raw);
    });

    // ---- ENG-37・TOOL-21 ----

    private static bool Ntfs(UiTestContext ctx) => new DriveInfo(Path.GetPathRoot(ctx.Root)!).DriveFormat is "NTFS" or "ReFS";

    private static string DirR(string path)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c dir /r \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Replace("\r", string.Empty);
    }

    private static async Task ShowAdsPanelAsync(AppSession app)
    {
        await app.CommandAsync("Command_FileAds");
        await app.WaitUntilAsync(async () => (await Ads(app, "state"))["file"]?.GetValue<string>() is not null, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the stream list");
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-21-01")]
    [Trait(UiTest.TC, "TC-ENG-37-01")]
    public Task Zone_identifier_is_listed_with_its_description_and_can_be_opened() => UiTestContext.RunAsync(async ctx =>
    {
        if (!Ntfs(ctx))
        {
            return;
        }

        string path = TestDataCatalog.Generate("TD-ENG-ADS", Path.Combine(ctx.Root, "ads"));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Hooks() });
        await ShowAdsPanelAsync(app);
        JsonObject state = await Ads(app, "state");
        JsonArray rows = state["rows"]!.AsArray();
        Assert.Equal(["::$DATA", "Zone.Identifier", "secret"], rows.Select(r => r!["display"]!.GetValue<string>()).OrderBy(n => n == "::$DATA" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal));
        JsonObject zone = rows.Single(r => r!["name"]!.GetValue<string>() == "Zone.Identifier")!.AsObject();
        Assert.Equal(TestDataCatalog.AdsStreams["Zone.Identifier"].Length, zone["size"]!.GetValue<long>());
        Assert.StartsWith("5B 5A 6F 6E", zone["head"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Internet", zone["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // 開く: タブの名前は ファイル名:ストリーム名。内容は生成した内容と一致する。
        await Ads(app, "open", ("name", "Zone.Identifier"));
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Contains("ads.bin:Zone.Identifier"), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the stream tab");
        byte[] expected = TestDataCatalog.AdsStreams["Zone.Identifier"];
        Assert.Equal(expected, await app.BytesAsync(0, expected.Length));
        Assert.StartsWith("[ZoneTransfer]", Encoding.ASCII.GetString(expected), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-21-02")]
    public Task A_stream_can_be_created_edited_and_deleted_and_matches_dir_r() => UiTestContext.RunAsync(async ctx =>
    {
        if (!Ntfs(ctx))
        {
            return;
        }

        string path = ctx.CopyTestData("TD-SEQ-1M", "ads.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Hooks() });
        await ShowAdsPanelAsync(app);

        // 使えない名前は入力欄を赤枠にして確定を無効にする。
        await app.UiaInvokeAsync("Ads_Create");
        await app.WaitForAsync("AdsNameDialog");
        await app.UiaSetValueAsync("Ads_NameBox", "a:b");
        Assert.Contains("cannot contain", await app.UiaNameAsync("Ads_NameError"), StringComparison.Ordinal);
        Assert.False(app.Button("Create")!.IsEnabled);
        await app.UiaSetValueAsync("Ads_NameBox", "note");
        Assert.True(app.Button("Create")!.IsEnabled);
        await app.InvokeDialogButtonAsync("Create", idle: false);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Contains("ads.bin:note"), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the new stream tab");

        // 開いたタブに 41 42 を入力して保存する (長さが 0 から 2 に変わる保存)。
        await app.TypeAsync("4142");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => DirR(path).Contains(" 2 ads.bin:note:$DATA", StringComparison.Ordinal), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the saved stream");
        await app.CommandAsync("Command_Close");
        await app.WaitUntilAsync(async () => !(await app.TabNamesAsync()).Contains("ads.bin:note"), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the stream tab to close");
        JsonObject state = await Ads(app, "load", ("path", path));
        JsonObject note = state["rows"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "note")!.AsObject();
        Assert.Equal(2, note["size"]!.GetValue<long>());

        // 削除: 確認ダイアログにストリーム名・サイズ・取り消せないことを出す。
        await Ads(app, "select", ("name", "note"));
        await app.UiaInvokeAsync("Ads_Delete");
        await app.WaitForAsync("AdsDeleteDialog");
        string body = await app.UiaNameAsync("Ads_DeleteBody");
        Assert.Contains("note", body, StringComparison.Ordinal);
        Assert.Contains("2 bytes", body, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", await app.UiaNameAsync("Ads_DeleteIrreversible"), StringComparison.Ordinal);
        await app.InvokeDialogButtonAsync("Delete", idle: false);
        await app.WaitUntilAsync(async () => !DirR(path).Contains(":note:", StringComparison.Ordinal), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the stream to disappear");
        Assert.DoesNotContain("note", string.Join(',', (await Ads(app, "state"))["rows"]!.AsArray().Select(r => r!["name"]!.GetValue<string>())), StringComparison.Ordinal);
    });

    [Fact]
    public Task A_file_with_streams_can_be_announced_when_opened_if_the_setting_is_on() => UiTestContext.RunAsync(async ctx =>
    {
        if (!Ntfs(ctx))
        {
            return;
        }

        string path = TestDataCatalog.Generate("TD-ENG-ADS", Path.Combine(ctx.Root, "ads"));

        // 既定 (オフ) では知らせない。
        AppSession plain = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Hooks() });
        await plain.IdleAsync();
        Assert.DoesNotContain("alternate data stream", plain.NotificationsText(), StringComparison.OrdinalIgnoreCase);
        await plain.DisposeAsync();

        string profile = ctx.NewProfile();
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"files.adsNotice\": true}");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Profile = profile, Hooks = Hooks() });
        await app.WaitUntilAsync(() => Task.FromResult(app.NotificationsText().Contains("2 alternate data streams", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), "the notice");
        Assert.Equal("Show list", await app.UiaNameAsync("Notification_Action"));
        await app.CommandAsync("Notification_Action");
        await app.WaitUntilAsync(async () => (await Ads(app, "state"))["rows"]!.AsArray().Count == 3, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the stream list");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-21-03")]
    [Trait(UiTest.TC, "TC-ENG-37-04")]
    public Task Volumes_without_named_streams_show_the_unsupported_message() => UiTestContext.RunAsync(async ctx =>
    {
        // FAT32 のボリュームの代わりに、テスト用のフック noNamedStreams でボリュームを非対応にする (仮想ディスクは管理者権限が要るため CI の別のテスト)。
        string path = ctx.CopyTestData("TD-BYTES-256", "plain.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Hooks(h => h["noNamedStreams"] = true) });

        await ShowAdsPanelAsync(app);
        JsonObject state = await Ads(app, "state");
        Assert.False(state["supported"]!.GetValue<bool>());
        Assert.Contains("does not support alternate data streams", state["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(state["buttons"]!["Create"]!.GetValue<bool>());
        Assert.False(state["buttons"]!["Open"]!.GetValue<bool>());

        // コマンド「ファイル: 代替データストリームを開く」は無効で、理由が出る。
        JsonObject? entry = null;
        await app.WaitUntilAsync(async () =>
        {
            JsonObject palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">open alternate data stream" });
            entry = palette["entries"]!.AsArray().Select(e => e!.AsObject())
                .FirstOrDefault(e => e["title"]!.GetValue<string>().Contains("alternate data stream", StringComparison.OrdinalIgnoreCase));
            return entry is not null;
        }, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the palette entry");
        Assert.Contains("does not support alternate data streams", entry!["reason"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        JsonObject executed = await app.SendAsync("execute", new JsonObject { ["id"] = "file.ads.open", ["fromPalette"] = true });
        Assert.False(executed["executed"]!.GetValue<bool>());
    });
}
