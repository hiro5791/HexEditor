using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>タブ (UI-09)、並べ替えとピン留め (UI-10)。ドラッグはマウスを使わず、ドロップと同じ処理をテスト用の命令で呼ぶ。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed partial class TabTests
{
    /// <summary>TD-UI-FILES-50 の一部: files50\fileNN.bin (4 KiB、すべてのバイトが NN)。</summary>
    internal static string[] Files(UiTestContext ctx, int count, int first = 1)
    {
        string folder = Path.Combine(ctx.Root, "files50");
        Directory.CreateDirectory(folder);
        return [.. Enumerable.Range(first, count).Select(n =>
        {
            string path = Path.Combine(folder, $"file{n:00}.bin");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)n, 4096).ToArray());
            return path;
        })];
    }

    internal static async Task<JsonObject> TabsAsync(AppSession app, int? window = null) =>
        await app.SendAsync("tabs", window is int w ? new JsonObject { ["window"] = w } : null);

    internal static async Task<IReadOnlyList<string>> NamesAsync(AppSession app, int? window = null) =>
        [.. (await TabsAsync(app, window))["tabs"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];

    internal static async Task<string> ActiveAsync(AppSession app, int? window = null)
    {
        JsonObject tabs = await TabsAsync(app, window);
        return tabs["tabs"]![tabs["selectedIndex"]!.GetValue<int>()]!["name"]!.GetValue<string>();
    }

    private static Task TabMenuAsync(AppSession app, int index, string item) =>
        app.SendAsync("tabMenu", new JsonObject { ["index"] = index, ["item"] = item });

    private static async Task OpenAllAsync(AppSession app, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            Assert.True((await app.UiOpenAsync(path))["opened"]!.GetValue<bool>(), path);
        }
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-01")]
    public Task Opening_the_same_file_again_activates_its_tab() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: seq.bin とハードリンク link.bin。seq.bin、TD-BYTES-256 の順に開いた。
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string link = Path.Combine(ctx.Root, "link.bin");
        Assert.True(CreateHardLink(link, seq, 0), "the hard link");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq, ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);

        // 1〜3. 同じパス、表記の違うパス (大文字と "\.\")、ハードリンクで開いても、タブは 2 つのままで seq.bin がアクティブ。
        string other = Path.Combine(ctx.Root.ToUpperInvariant(), ".", "SEQ.BIN");
        foreach (string path in new[] { seq, other, link })
        {
            await EditingTests.SelectTabAsync(app, 1);
            await app.UiOpenAsync(path);
            Assert.Equal(2, (await NamesAsync(app)).Count);
            Assert.Equal("seq.bin", await ActiveAsync(app));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-02")]
    public Task Same_file_names_show_parent_folders() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SAME-NAME: a\x\data.bin と b\x\data.bin。
        string a = Path.Combine(ctx.Root, "a", "x", "data.bin");
        string b = Path.Combine(ctx.Root, "b", "x", "data.bin");
        foreach (string path in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[16]);
        }

        AppSession app = await ctx.StartAsync();
        await OpenAllAsync(app, [a, b]);
        JsonArray tabs = (await TabsAsync(app))["tabs"]!.AsArray();
        Assert.Equal(["data.bin — a\\x", "data.bin — b\\x"], tabs.Select(t => t!["title"]!.GetValue<string>()));

        // 1 つ閉じると親フォルダ名は消える。
        await app.SendAsync("tabCloseRequest", new JsonObject { ["index"] = 0 });
        await app.WaitForTabsAsync(1);
        Assert.Equal("data.bin", (await TabsAsync(app))["tabs"]![0]!["title"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-03")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_tab_switches_to_the_previously_used_tab() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 3);
        AppSession app = await ctx.StartAsync();
        await OpenAllAsync(app, files);
        Assert.Equal("file03.bin", await ActiveAsync(app));

        // 1〜3. file01 をクリックしてから Ctrl+Tab を押して離すと、直前の file03。
        await EditingTests.SelectTabAsync(app, 0);
        Assert.Equal("command:tab.next", (await app.KeyAsync("Tab", ctrl: true))["handledBy"]!.GetValue<string>());
        Assert.Equal("file03.bin", await ActiveAsync(app));

        // Ctrl を離した後は file03 が最近使ったタブの先頭になる。
        await app.WaitUntilAsync(async () => (await TabsAsync(app))["mru"]![0]!.GetValue<string>() == "file03.bin", TimeSpan.FromSeconds(5), "the switch to finish");
        Assert.False((await TabsAsync(app))["switcherOpen"]!.GetValue<bool>());

        // 4〜5. Ctrl+PageDown は並び順で次 (末尾の file03 の次は先頭の file01)。
        await app.KeyAsync("PageDown", ctrl: true);
        Assert.Equal("file01.bin", await ActiveAsync(app));
        await app.KeyAsync("PageUp", ctrl: true);
        Assert.Equal("file03.bin", await ActiveAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-05")]
    public Task Middle_click_closes_the_tab() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);

        // 1〜2. 中ボタンのクリック (TabView の閉じる要求) で TD-SEQ-1M のタブが閉じる。
        await app.SendAsync("tabCloseRequest", new JsonObject { ["index"] = 0 });
        await app.WaitForTabsAsync(1);
        Assert.Equal(["TD-BYTES-256.bin"], await NamesAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-10-01")]
    public Task Pinned_tab_moves_left_and_survives_close_all() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 4);
        AppSession app = await ctx.StartAsync();
        await OpenAllAsync(app, files);

        // 1〜2. file03 をピン留めすると左端に移り、ピンのアイコンが付く。
        await TabMenuAsync(app, 2, "TabMenu_Pin");
        JsonArray tabs = (await TabsAsync(app))["tabs"]!.AsArray();
        Assert.Equal(["file03.bin", "file01.bin", "file02.bin", "file04.bin"], tabs.Select(t => t!["name"]!.GetValue<string>()));
        Assert.True(tabs[0]!["pinned"]!.GetValue<bool>());
        Assert.Equal("", tabs[0]!["icon"]!.GetValue<string>());

        // 3〜4. 「すべて閉じる」で file03 だけが残る。
        await TabMenuAsync(app, 3, "TabMenu_CloseAll");
        await app.WaitForTabsAsync(1);
        Assert.Equal(["file03.bin"], await NamesAsync(app));

        // Ctrl+W (明示的な操作) では閉じる (UI-10 の仕様 3)。
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-10-02")]
    public Task Dragged_order_is_restored_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 3);
        AppSession app = await ctx.StartAsync();
        await OpenAllAsync(app, files);

        // 1〜2. file03 を file01 の左にドロップする。
        await app.SendAsync("dropTab", new JsonObject { ["index"] = 2, ["toIndex"] = 0 });
        string[] expected = ["file03.bin", "file01.bin", "file02.bin"];
        Assert.Equal(expected, await NamesAsync(app));

        // 3〜4. 終了して起動し直しても同じ並び。
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        AppSession again = await ctx.StartAsync();
        await again.WaitForTabsAsync(3);
        Assert.Equal(expected, await NamesAsync(again));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-10-03")]
    public Task Tabs_move_with_the_keyboard_from_the_palette() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 3);
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ja" });
        await OpenAllAsync(app, files);
        await EditingTests.SelectTabAsync(app, 0);

        // 1〜2. コマンドパレットで「タブ: 右へ移動」。
        await app.KeyAsync("P", ctrl: true, shift: true);
        JsonObject palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">タブ: 右へ移動" });
        Assert.Equal("command:tab.moveRight", palette["entries"]![0]!["key"]!.GetValue<string>());
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        Assert.Equal(["file02.bin", "file01.bin", "file03.bin"], await NamesAsync(app));

        // 3〜4. 「タブ: 左へ移動」で戻る。
        await app.KeyAsync("P", ctrl: true, shift: true);
        palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">タブ: 左へ移動" });
        Assert.Equal("command:tab.moveLeft", palette["entries"]![0]!["key"]!.GetValue<string>());
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        Assert.Equal(["file01.bin", "file02.bin", "file03.bin"], await NamesAsync(app));
    });

    [Fact]
    public Task Tab_context_menu_has_the_specified_items() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-09 の仕様 9 の項目と順序。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonArray items = (await app.SendAsync("tabMenuItems", new JsonObject { ["index"] = 0 }))["items"]!.AsArray();
        Assert.Equal(
            ["TabMenu_Close", "TabMenu_CloseOthers", "TabMenu_CloseRight", "TabMenu_CloseSaved", "TabMenu_CloseAll", "TabMenu_Pin",
                "TabMenu_NewView", "TabMenu_ShowToRight",
                "TabMenu_MoveToNewWindow", "TabMenu_MoveToWindow", "TabMenu_CopyPath", "TabMenu_RevealInExplorer", "TabMenu_ReadOnly",
                "TabMenu_CompareSelectLeft", "TabMenu_CompareWithLeft", "TabMenu_CompareSaved"],
            items.Select(i => i!["id"]!.GetValue<string>()));

        // パスをコピー (アプリ内のクリップボードの代わりに入る)。
        await TabMenuAsync(app, 0, "TabMenu_CopyPath");
        Assert.Equal(ctx.TestData("TD-SEQ-1M"), (await app.SendAsync("clipboard"))["text"]?.GetValue<string>());

        // ツールチップに完全なパスとサイズ (UI-09 の仕様 4)。
        string tip = (await TabsAsync(app))["tabs"]![0]!["toolTip"]!.GetValue<string>();
        Assert.StartsWith(ctx.TestData("TD-SEQ-1M"), tip);
        Assert.Contains("Size:", tip);
    });

    [Fact]
    public Task Restored_tabs_open_when_first_shown() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-31 の仕様 6: アクティブなタブだけをすぐに開き、他のタブは見出しだけを出して初めて表示したときに開く。
        string[] files = Files(ctx, 3);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(3);
        await EditingTests.SelectTabAsync(app, 0);
        await app.GoToAsync(0x20);
        await EditingTests.SelectTabAsync(app, 1);
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));

        AppSession again = await ctx.StartAsync();
        await again.WaitForTabsAsync(3);
        JsonArray tabs = (await TabsAsync(again))["tabs"]!.AsArray();
        Assert.Equal([true, false, true], tabs.Select(t => t!["pending"]!.GetValue<bool>()));
        Assert.Equal(files.Select(Path.GetFileName), tabs.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("file02.bin", await ActiveAsync(again));

        // 表示すると開き、カーソルが戻る。
        await EditingTests.SelectTabAsync(again, 0);
        Assert.False((await TabsAsync(again))["tabs"]![0]!["pending"]!.GetValue<bool>());
        Assert.Equal(0x20, (await again.DocumentAsync())["cursor"]!.GetValue<long>());
        Assert.Equal(files.Select(Path.GetFileName), await NamesAsync(again));
    });

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}

/// <summary>タブの切り替えの速さ (UI-09 の「巨大ファイル・長時間処理」)。</summary>
[Trait(UiTest.Category, PerformanceTests.Performance)]
public sealed class TabPerformanceTests(ITestOutputHelper output)
{
    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-UI-09-04")]
    public Task Switching_among_fifty_tabs_takes_under_100ms() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: file01〜file49 と TD-SPARSE-100G の 50 タブ。
        var files = TabTests.Files(ctx, 49).ToList();
        files.Add(ctx.TestData("TD-SPARSE-100G"));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(50);

        // 1. Ctrl+PageDown を 100 回。押してから切り替え先の Hex ビューが描画されるまで。
        var times = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            times.Add((await app.SendAsync("switchTabMeasured", new JsonObject { ["forward"] = true }))["ms"]!.GetValue<double>());
        }

        output.WriteLine($"最大 {times.Max():F1} ms、平均 {times.Average():F1} ms");
        Assert.All(times, t => Assert.True(t <= 100, $"{t:F1} ms"));
    });
}
