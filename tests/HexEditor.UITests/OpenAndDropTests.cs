using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// サイズを指定して新規作成 (ENG-10)、開く (ENG-11)、ドロップ (ENG-12、UI-34)、通知 (UI-36)。
/// Explorer からの実際のドラッグ & ドロップはマウスを使うため、ドロップの処理 (ドロップされた項目を開く処理) を
/// テスト用の命令の通り道から呼ぶ。標準のファイルのダイアログはフォーカスを奪うため、テスト用の差し替え (--test-hooks の
/// openPicker / savePicker) を使う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class OpenAndDropTests
{
    private const long TiB = 1L << 40;

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-10-02")]
    public Task New_with_size_1T_filled_with_FF() => UiTestContext.RunAsync(async ctx =>
    {
        // コマンドパレット (フェーズ 1) の代わりに、同じコマンドのメニューの項目から開く。
        AppSession app = await ctx.StartAsync();

        // 測る前に、新しいタブの Hex 表示を 1 度作っておく (テスト用のビルド (Debug) の初めての JIT の分を測らない)。
        await app.SendAsync("new");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0
            && (await app.RenderAsync())["firstFrameTime"]!.GetValue<long>() != 0, TimeSpan.FromSeconds(10), "the warm-up view");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);

        await app.CommandAsync("Command_NewWithSize");
        await app.WaitForAsync("NewSize_Size");
        await app.UiaSetValueAsync("NewSize_Size", "1T");
        await app.UiaSetValueAsync("NewSize_Fill", "FF");

        // 2. 「作成」を押してから、タブが開いて表示が終わるまで。
        var create = app.Button("Create")!;
        var watch = Stopwatch.StartNew();
        create.Patterns.Invoke.Pattern.Invoke();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["document"]?["length"]?.GetValue<long>() == TiB, TimeSpan.FromSeconds(10), "the new document");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        watch.Stop();

        // 作り始めてから Hex 表示の最初の描画までを、アプリの中の時刻で判定する (ダイアログの閉じる動きと、UI オートメーション・命令の
        // 通り道の往復は含めない。混んだ CI のランナーでは往復だけで数百 ms かかる)。
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["firstFrameTime"]!.GetValue<long>() != 0, TimeSpan.FromSeconds(10), "the first frame");
        long created = (await app.StateAsync())["newDocumentCreatedAt"]!.GetValue<long>();
        long shown = (await app.RenderAsync())["firstFrameTime"]!.GetValue<long>() - created;
        Assert.True(shown <= 100, $"creating took {shown} ms ({watch.ElapsedMilliseconds} ms after the button with the round trips)");

        // 3. 長さとピースの数 (生成ピース 1 つ)。
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(TiB, doc["length"]!.GetValue<long>());
        Assert.Equal(1, doc["pieceCount"]!.GetValue<int>());

        // 4. 境界と、固定の種で選んだ 1,000 か所。
        var offsets = new List<long> { 0, (1L << 31) - 1, 1L << 32, TiB - 1 };
        var random = new Random(1002);
        offsets.AddRange(Enumerable.Range(0, 1000).Select(_ => random.NextInt64(TiB)));
        foreach (long offset in offsets)
        {
            Assert.Equal(0xFF, (await app.BytesAsync(offset, 1)).Single());
        }

        // 5. 不正な値: 赤枠になり「作成」が無効になる。
        await app.CommandAsync("Command_NewWithSize");
        await app.WaitForAsync("NewSize_Size");
        await app.UiaSetValueAsync("NewSize_Size", "-1");
        await app.UiaSetValueAsync("NewSize_Fill", "F");
        Assert.True((await app.ElementAsync("NewSize_Size"))["errorBorder"]!.GetValue<bool>());
        Assert.True((await app.ElementAsync("NewSize_Fill"))["errorBorder"]!.GetValue<bool>());
        Assert.False(app.Button("Create")!.IsEnabled);
        await app.InvokeDialogButtonAsync("Cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-11-04")]
    public Task File_opened_exclusively_by_another_app() => UiTestContext.RunAsync(async ctx =>
    {
        // TestTarget (tools/TestTarget、未作成) の代わりに、PowerShell にファイルを共有なしで開かせる。
        string path = ctx.CopyTestData("TD-SEQ-1M", "locked.bin");
        using Process holder = LockFile(path);
        try
        {
            AppSession app = await ctx.StartAsync();
            Assert.False((await app.UiOpenAsync(path))["opened"]!.GetValue<bool>());
            JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("locked.bin", StringComparison.Ordinal), "the error");
            string message = notice["message"]!.GetValue<string>();

            // MSIX 版 (CI の ui-distro) では、Restart Manager がパッケージの外のプロセスを返さないことがある。そのときは使っている
            // アプリの名前のない文言 (ENG-11 の「エラー」: 分からなければ名前を出さない)。
            if (await app.DistributionAsync() == "Msix" && message.Contains("another app is using it", StringComparison.Ordinal))
            {
                Assert.Empty(await app.TabNamesAsync());
                return;
            }

            Assert.Contains("being used by", message, StringComparison.Ordinal);
            Assert.Contains("PowerShell", message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await app.TabNamesAsync());
        }
        finally
        {
            holder.Kill();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-11-05")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Open_dialog_with_five_files() => UiTestContext.RunAsync(async ctx =>
    {
        // ダイアログで選ぶ代わりに、ダイアログの差し替えが 5 つのファイルを返す。手順 4 の初期フォルダは Windows の
        // ダイアログが SettingsIdentifier ごとに覚えるもので、差し替えでは確かめられないため、同じ識別子で開くことだけを確かめる。
        string folder = Directory.CreateDirectory(Path.Combine(ctx.Root, "five")).FullName;
        string[] ids = ["TD-EMPTY", "TD-BYTES-256", "TD-SEQ-1M", "TD-ZERO-1M", "TD-FF-1M"];
        string[] paths = [.. ids.Select(id =>
        {
            string target = Path.Combine(folder, id + ".bin");
            File.Copy(ctx.TestData(id), target);
            return target;
        })];
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["openPicker"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]) } });

        Assert.Equal("menu:Command_Open", (await app.KeyAsync("O", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.WaitForTabsAsync(5);
        JsonArray docs = (await app.StateAsync())["documents"]!.AsArray();
        Assert.Equal(ids.Select(id => id + ".bin"), docs.Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(new long[] { 0, 256, 1 << 20, 1 << 20, 1 << 20 }, docs.Select(d => d!["length"]!.GetValue<long>()));

        await app.KeyAsync("O", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(2, (await app.LogAsync()).Count(l => l.Contains("Test hooks: open picker (HexEditor.Open)", StringComparison.Ordinal)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-12-01")]
    public Task Dropping_three_files_opens_three_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        string png = ctx.WriteFile("TD-PNG.png", Png());
        string[] files = [bytes, seq, png];
        AppSession app = await ctx.StartAsync();

        // 1〜2. 3 つのタブが開き、名前・長さ・識別子 (最終パス) が「開く」と同じ。
        await app.DropAsync(files);
        await app.WaitForTabsAsync(3);
        JsonArray docs = (await app.StateAsync())["documents"]!.AsArray();
        Assert.Equal(files.Select(Path.GetFileName), docs.Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(files.Select(f => new FileInfo(f).Length), docs.Select(d => d!["length"]!.GetValue<long>()));
        Assert.Equal(files.Select(Path.GetFullPath), docs.Select(d => d!["path"]!.GetValue<string>()));

        // 3. もう一度ドロップしてもタブは増えず、既存のタブがアクティブになる (ENG-11 と同じ処理)。
        await EditingTests.SelectTabAsync(app, 0);
        await app.DropAsync(files);
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 2, TimeSpan.FromSeconds(10), "the existing tab");
        Assert.Equal(3, (await app.TabNamesAsync()).Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-12-02")]
    public Task Dropping_a_file_inside_a_zip_opens_it_untitled() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-ZIP の 1 つ目のテキストファイルを、パスを持たない項目 (ZIP フォルダと同じ形) としてドロップする。
        (string name, byte[] content) = FirstZipEntry(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["savePicker"] = string.Empty } });
        await app.DropAsync([], virtualItems: [new JsonObject { ["name"] = name, ["base64"] = Convert.ToBase64String(content) }]);
        await app.WaitForTabsAsync(1);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(name, doc["name"]!.GetValue<string>());
        Assert.Equal(content, await app.BytesAsync(0, content.Length));
        Assert.Null(doc["path"]?.GetValue<string>());

        // 3. Ctrl+S で「名前を付けて保存」のダイアログ (の差し替え) が開く。
        await app.TypeAsync("41");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitForLogAsync(l => l.Contains("Test hooks: save picker", StringComparison.Ordinal), "the save as dialog");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-01")]
    public Task Dropping_on_the_tab_strip_inserts_at_the_position() => UiTestContext.RunAsync(async ctx =>
    {
        // ドラッグ中の挿入位置の縦線は実際のドラッグ (マウス) でしか出ないため確かめない。タブ列の file02 と file03 の境目
        // (挿入位置 2) へのドロップの結果を確かめる。
        string[] files = Files50(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files[..3] });
        await app.WaitForTabsAsync(3);
        await app.DropAsync(files[3..6], insertAt: 2);
        await app.WaitForTabsAsync(6);
        Assert.Equal(["file01.bin", "file02.bin", "file04.bin", "file05.bin", "file06.bin", "file03.bin"], await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-02")]
    public Task Dropping_a_folder_does_not_open_it() => UiTestContext.RunAsync(async ctx =>
    {
        // 「複数ファイル検索」ボタンは複数ファイル検索 (フェーズ 2) と同時に付ける。ここではタブが開かず、フォルダを
        // 開けない旨 (1 個をスキップ) の通知が出ることを確かめる。
        string folder = Path.GetDirectoryName(Files50(ctx)[0])!;
        AppSession app = await ctx.StartAsync();
        await app.DropAsync([folder]);
        JsonObject notice = await app.WaitForNotificationAsync(m => m.StartsWith("Folders can't be opened", StringComparison.Ordinal), "the folder notice");
        Assert.Contains("(1 skipped)", notice["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Warning", notice["severity"]!.GetValue<string>());
        Assert.Empty(await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-34-03")]
    public Task Dropping_30_files_asks_first() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files50(ctx)[..30];
        AppSession app = await ctx.StartAsync();
        await app.DropAsync(files);

        var dialog = await app.WaitForDialogAsync("DropConfirmDialog");
        await app.WaitForDialogTextAsync(dialog, "Open 30 files?");
        await app.WaitUntilAsync(() => Task.FromResult(app.Button("Open") is not null && app.Button("Cancel") is not null), TimeSpan.FromSeconds(5),
            "the Open and Cancel buttons");
        await app.InvokeDialogButtonAsync("Open");
        await app.WaitForTabsAsync(30);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-36-01")]
    public Task Same_warning_is_merged() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.GetDirectoryName(Files50(ctx)[0])!;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        for (int i = 0; i < 5; i++)
        {
            await app.DropAsync([folder]);
            await app.IdleAsync();
        }

        await app.WaitUntilAsync(async () => (await app.NotificationsAsync()).Any(n => n["count"]!.GetValue<int>() == 5), TimeSpan.FromSeconds(10), "×5");
        var folderNotices = (await app.NotificationsAsync()).Where(n => n["message"]!.GetValue<string>().StartsWith("Folders can't be opened", StringComparison.Ordinal)).ToList();
        JsonObject notice = Assert.Single(folderNotices);
        Assert.EndsWith("×5", notice["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("×5", app.NotificationsText(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-36-02")]
    public Task Error_notification_does_not_close_by_itself() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "busy.bin");
        using Process holder = LockFile(path);
        try
        {
            AppSession app = await ctx.StartAsync();
            await app.UiOpenAsync(path);
            JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("busy.bin", StringComparison.Ordinal), "the error");
            Assert.Equal("Error", notice["severity"]!.GetValue<string>());

            // 3〜4. マウスはウィンドウの上にない (テストはマウスを使わない)。フォーカスをエディタ領域に置いて 15 秒待つ。
            await app.FocusAsync("Start_Open", keyboard: false);
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.Contains(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>().Contains("busy.bin", StringComparison.Ordinal));
            Assert.Contains("busy.bin", app.NotificationsText(), StringComparison.Ordinal);
        }
        finally
        {
            holder.Kill();
        }
    });

    // ---- 補助 ----

    /// <summary>別のプロセス (PowerShell) にファイルを共有なし (FileShare.None) で開かせ、開いたことを確かめてから返す。</summary>
    internal static Process LockFile(string path)
    {
        string script = $"$f = [System.IO.File]::Open('{path.Replace("'", "''")}', 'Open', 'Read', 'None'); [Console]::Out.WriteLine('locked'); Start-Sleep -Seconds 120";
        var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        })!;
        string? line = process.StandardOutput.ReadLine();
        Assert.Equal("locked", line);
        return process;
    }

    /// <summary>TD-UI-FILES-50: files50\file01.bin〜file50.bin (各 4 KiB、すべてのバイトが番号の値)。</summary>
    internal static string[] Files50(UiTestContext ctx)
    {
        string folder = Directory.CreateDirectory(Path.Combine(ctx.Root, "files50")).FullName;
        return [.. Enumerable.Range(1, 50).Select(n =>
        {
            string path = Path.Combine(folder, $"file{n:D2}.bin");
            byte[] data = new byte[4096];
            Array.Fill(data, (byte)n);
            File.WriteAllBytes(path, data);
            return path;
        })];
    }

    /// <summary>TD-ZIP (テキストファイル 3 つの ZIP) を作り、1 つ目のファイルの名前と内容を返す。</summary>
    private static (string Name, byte[] Content) FirstZipEntry(UiTestContext ctx)
    {
        string zipPath = Path.Combine(ctx.Root, "TD-ZIP.zip");
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (int i = 1; i <= 3; i++)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"text{i}.txt").Open(), new UTF8Encoding(false));
                writer.Write(string.Concat(Enumerable.Repeat($"Line {i} of the zip test data.\n", 20)));
            }
        }

        using ZipArchive read = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry first = read.Entries[0];
        using var stream = new MemoryStream();
        first.Open().CopyTo(stream);
        return (first.Name, stream.ToArray());
    }

    /// <summary>TD-PNG の代わりの小さい PNG (シグネチャと IHDR・IEND)。</summary>
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];
}
