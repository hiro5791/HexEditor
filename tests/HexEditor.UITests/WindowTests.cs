using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// メインウィンドウの構成 (UI-01)、タイトルバー (UI-02)、メニューバー (UI-03)、ステータスバー (UI-06)、外観 (UI-26、UI-27)、
/// バージョン情報と問題の報告 (UI-40、PKG-28)、キーボードでの操作 (UI-52、UI-53)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed partial class WindowTests
{
    private const long GiB = 1L << 30;

    [Fact]
    [Trait(UiTest.TC, "TC-UI-01-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task First_launch_shows_title_bar_tabs_start_page_and_status_bar() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1〜2. 各領域の表示状態と位置 (ウィンドウの中のエピクセル)。
        var areas = new List<(string Id, double Top, double Bottom)>();
        foreach (string id in new[] { "TitleBar", "DocumentTabs", "StartPage", "StatusBar" })
        {
            JsonObject e = await app.ElementAsync(id);
            Assert.True(e["found"]!.GetValue<bool>(), $"{id} is missing");
            Assert.True(e["effectivelyVisible"]!.GetValue<bool>(), $"{id} is not visible");
            Assert.True(e["height"]!.GetValue<double>() > 0, $"{id} has no height");
            areas.Add((id, e["top"]!.GetValue<double>(), e["bottom"]!.GetValue<double>()));
        }

        // 上から順に並ぶ。タブ列 (TabView) はエディタ領域を含むので、スタートページはタブ列の見出しの帯の下に重なる。
        for (int i = 1; i < areas.Count; i++)
        {
            Assert.True(areas[i].Top > areas[i - 1].Top, $"{areas[i].Id} ({areas[i].Top}) is not below {areas[i - 1].Id} ({areas[i - 1].Top})");
        }

        Assert.True(areas[0].Bottom <= areas[1].Top + 1, "the tab strip overlaps the title bar");
        Assert.True(areas[2].Top - areas[1].Top >= 24, "the tab strip has no height above the start page");
        Assert.True(areas[2].Bottom <= areas[3].Top + 1, "the start page overlaps the status bar");

        // ツールバー・左右と下のパネルは表示されていない (フェーズ 0 では要素自体がない)。
        foreach (string id in new[] { "Toolbar", "LeftPanel", "RightPanel", "BottomPanel" })
        {
            Assert.False(await app.IsShownAsync(id), $"{id} is shown");
        }

        Assert.Empty(await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-02-04")]
    public Task Title_bar_blank_area_is_a_caption_drag_region() => UiTestContext.RunAsync(async ctx =>
    {
        // 実際のマウスのドラッグ・ダブルクリックは作業中の PC の入力を奪うため行わない。代わりに、ドラッグと
        // ダブルクリックでの最大化を Windows に任せる根拠である判定を確かめる: ウィンドウへの WM_NCHITTEST が
        // キャプション (HTCAPTION) を返し、その点が入力を通す領域 (InputNonClientPointerSource の Passthrough) に
        // 含まれない (メッセージを送るだけで、入力もフォーカスも動かさない)。
        // 既定のウィンドウの大きさは画面で変わり (CI のランナーの画面は 1024 px 幅)、狭いとタイトルの右に余白がない。幅 1000 px にそろえる。
        AppSession app = await ctx.StartAsync();
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)(1000 * scale), (int)(700 * scale));
        JsonObject state = await app.StateAsync();
        JsonObject title = await app.ElementAsync("WindowTitle");
        JsonObject menu = await app.ElementAsync("MainMenu");

        // 1. タイトルの文字列より右の余白 (メニューバー・右側の入力を通す要素・操作ボタンに重ならない点)。
        // 余白の幅はウィンドウの幅で変わる (CI のランナーの画面は小さく、既定のウィンドウも狭い) ので、タイトルの右端と、
        // その右にある次の入力を通す領域 (なければウィンドウ操作ボタンの左端) の中央を使う。
        JsonObject regions = await app.SendAsync("nonClientRegions");
        int buttonsLeft = regions["caption"]!.AsArray().Max(r => r!["right"]!.GetValue<int>()) - state["titleBarRightInset"]!.GetValue<int>();
        double y = (title["top"]!.GetValue<double>() + title["bottom"]!.GetValue<double>()) / 2;
        double titleRight = title["right"]!.GetValue<double>() * scale;
        double next = regions["passthrough"]!.AsArray().Concat(regions["caption"]!.AsArray())
            .Where(r => r!["top"]!.GetValue<int>() <= y * scale && y * scale < r["bottom"]!.GetValue<int>())
            .SelectMany(r => new[] { r!["left"]!.GetValue<int>(), r["right"]!.GetValue<int>() })
            .Append(buttonsLeft)
            .Where(edge => edge > titleRight + 1)
            .DefaultIfEmpty((int)titleRight)
            .Min();
        Assert.True(next - titleRight >= 8, $"no blank title area right of the title ({titleRight}..{next})");
        double x = (titleRight + next) / 2 / scale;
        Assert.True(x > menu["right"]!.GetValue<double>(), "the point is on the menu bar");
        Assert.Equal(WindowHitTest.Caption, WindowHitTest.At(app.Hwnd, x, y, scale));
        Assert.False(InAny(regions["passthrough"]!.AsArray(), x * scale, y * scale), "the blank title area is a passthrough region");
        Assert.True(InAny(regions["caption"]!.AsArray(), x * scale, y * scale), "the blank title area is not a caption region");

        // メニューバーの上は入力を通す領域 (クリックがメニューに届く。TC-UI-02-05 でも確かめる)。
        Assert.True(InAny(regions["passthrough"]!.AsArray(), (menu["left"]!.GetValue<double>() + 10) * scale, y * scale), "the menu bar is in the drag region");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-02-05")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Menu_bar_is_not_swallowed_by_the_drag_region() => UiTestContext.RunAsync(async ctx =>
    {
        // 実際のマウスのクリックは行わない (作業中の PC の入力を奪うため)。メニューバーの各メニューの中央が
        // キャプション (ドラッグ領域) ではなくクライアント領域と判定されることを WM_NCHITTEST で確かめ、メニューが
        // UI オートメーションで開けることを確かめる。コマンドパレットの入口も同じく確かめ、UI オートメーションの Invoke で押す。
        AppSession app = await ctx.StartAsync();
        var menus = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu"))!.FindAllChildren()
            .Where(m => AppSession.NameOf(m) != "Test").ToList();
        Assert.NotEmpty(menus);
        JsonArray passthrough = (await app.SendAsync("nonClientRegions"))["passthrough"]!.AsArray();
        WindowHitTest.ClientOrigin(app.Hwnd, out int originX, out int originY);
        foreach (var m in menus)
        {
            // メニューの中央 (クライアント領域の物理ピクセル) が入力を通す領域に入っている。
            var r = m.BoundingRectangle;
            double cx = r.Left + (r.Width / 2.0) - originX;
            double cy = r.Top + (r.Height / 2.0) - originY;
            Assert.True(InAny(passthrough, cx, cy), $"{AppSession.NameOf(m)} is in the drag region");
            Assert.True(m.Patterns.ExpandCollapse.IsSupported, $"{AppSession.NameOf(m)} cannot be expanded");
        }

        // 3〜4. コマンドパレットの入口 (UI-02 の仕様 4): 中央が入力を通す領域にあり、押すとコマンドパレットが開く。
        JsonObject entry = await app.ElementAsync("TitleBar_Palette");
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        double ex = (entry["left"]!.GetValue<double>() + entry["right"]!.GetValue<double>()) / 2 * scale;
        double ey = (entry["top"]!.GetValue<double>() + entry["bottom"]!.GetValue<double>()) / 2 * scale;
        Assert.True(InAny(passthrough, ex, ey), "the command palette entry is in the drag region");
        await app.UiaInvokeAsync("TitleBar_Palette");
        await app.WaitUntilAsync(async () => (await app.SendAsync("palette"))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the command palette");
    });

    [Theory]
    [InlineData("en", "File")]
    [InlineData("ja", "ファイル(F)")]
    [Trait(UiTest.TC, "TC-UI-03-02")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Access_keys_open_the_file_dialog(string language, string fileTitle) => UiTestContext.RunAsync(async ctx =>
    {
        // Alt・F・O の実際のキー入力はシステムのキーボードを使うため送らない。アクセスキーの割り当て (Alt → F → O の
        // 経路) と表示名を確かめ、「開く」の項目を押すとファイルを開くダイアログが出ることを、ダイアログの差し替え
        // (テスト用の仕組み) のログで確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = language,
            Hooks = new JsonObject { ["openPicker"] = new JsonArray() },
        });
        JsonArray items = (await app.SendAsync("menuTexts"))["items"]!.AsArray();
        JsonObject file = items.Select(i => i!.AsObject()).First(i => i["menu"]?.GetValue<bool>() == true);
        Assert.Equal(fileTitle, file["text"]!.GetValue<string>());
        Assert.Equal("F", file["accessKey"]!.GetValue<string>());
        JsonObject open = items.Select(i => i!.AsObject()).Single(i => i["id"]!.GetValue<string>() == "Command_Open");
        Assert.Equal("O", open["accessKey"]!.GetValue<string>());

        await app.CommandAsync("Command_Open");
        await app.WaitForLogAsync(l => l.Contains("Test hooks: open picker (HexEditor.Open)", StringComparison.Ordinal), "the open dialog");
        Assert.Empty(await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-06-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Status_bar_shows_the_last_offset_of_a_100_GB_source() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-SPARSE-100G の代わりに同じ長さの仮想のデータソース (ディスクを使わない)。
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "sparse-100G", ["length"] = 100 * GiB, ["content"] = "zero" });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        await app.KeyAsync("G", ctrl: true);
        await app.IdleAsync();
        await app.UiaSetValueAsync("GoTo_Input", "end-1");
        await app.UiaInvokeAsync("GoTo_Go");
        await app.IdleAsync();
        Assert.Equal(100 * GiB - 1, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
        Assert.Matches("^Offset: 0x0*18FFFFFFFF$", await app.UiaNameAsync("Status_Offset"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-06-02")]
    public Task Clicking_the_mode_item_toggles_insert_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-BYTES-256")] });
        Assert.Equal("Overwrite", await app.UiaNameAsync("Status_Mode"));

        await app.UiaInvokeAsync("Status_Mode");
        await app.IdleAsync();
        Assert.Equal("Insert", await app.UiaNameAsync("Status_Mode"));

        await app.TypeAsync("AA");
        Assert.Equal(257, (await app.DocumentAsync())["length"]!.GetValue<long>());

        await app.KeyAsync("Insert");
        await app.IdleAsync();
        Assert.Equal("Overwrite", await app.UiaNameAsync("Status_Mode"));
        Assert.False((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-06-03")]
    public Task Hidden_status_items_stay_hidden_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq] });
        Assert.True(await app.IsShownAsync("Status_Encoding"));

        // 1. 右クリックメニューの「文字コード」のチェックを外す (メニューを開かずに項目を押す)。
        await app.CommandAsync("StatusMenu_encoding");
        await app.IdleAsync();
        Assert.False(await app.IsShownAsync("Status_Encoding"));

        // 2. 終了し (設定の書き出しを待つ)、同じ設定フォルダで起動し直す。
        await Task.Delay(1000);
        try
        {
            await app.SendAsync("exit");
        }
        catch (IOException)
        {
        }

        await app.WaitForExitAsync(TimeSpan.FromSeconds(20));
        AppSession again = await ctx.StartAsync(new AppOptions { Files = [seq] });

        // 3. 文字コードの項目は表示されず、ほかの項目は表示されている。
        Assert.False(await again.IsShownAsync("Status_Encoding"));
        Assert.True(await again.IsShownAsync("Status_Offset"));
        Assert.True(await again.IsShownAsync("Status_Mode"));
        JsonObject settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(ctx.DefaultProfile!, "settings.json")))!.AsObject();
        string items = settings["ui.statusBar.items"]!.ToJsonString();
        Assert.DoesNotContain("encoding", items, StringComparison.Ordinal);
        Assert.Contains("cursor", items, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-06-04")]
    public Task Narrow_window_keeps_the_essential_status_items() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SelectAsync(0x10, 0x10);

        // 1. 640 × 400 (表示倍率 100% 換算) にする。
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)Math.Round(640 * scale), (int)Math.Round(400 * scale));
        await app.WaitUntilAsync(async () => (await app.StateAsync())["rootSize"]!.GetValue<string>().StartsWith("6", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the window to shrink");
        await Task.Delay(300);
        await app.IdleAsync();

        // 2. カーソル位置・選択範囲・入力モードは表示され、「…」に入った項目は省略の順序の先頭側。
        foreach (string id in new[] { "Status_Offset", "Status_Selection", "Status_Mode" })
        {
            Assert.True(await app.IsShownAsync(id), $"{id} is hidden at 640 px");
        }

        JsonObject state = await app.StateAsync();
        var overflow = state["statusOverflow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.NotEmpty(overflow);
        string[] order = ["column", "value", "encoding", "size", "notifications"];
        Assert.Equal(order.Take(overflow.Count), overflow);
        Assert.True(await app.IsShownAsync("Status_More"), "the … button is not shown");
        foreach (string id in overflow)
        {
            Assert.False(await app.IsShownAsync("Status_" + char.ToUpperInvariant(id[0]) + id[1..]), $"{id} is both shown and in …");
        }
    });

    [Fact(Skip = "Windows のアプリモード (AppsUseLightTheme) を切り替える必要があり、作業中の PC の設定を変えるため自動では実行しない (CI の専用の環境が必要)")]
    [Trait(UiTest.TC, "TC-UI-26-01")]
    public Task Theme_follows_windows_app_mode() => Task.CompletedTask;

    [Fact(Skip = "Windows のコントラストテーマを有効にする必要があり、作業中の PC の設定を変えるため自動では実行しない (CI の専用の環境が必要)")]
    [Trait(UiTest.TC, "TC-UI-26-02")]
    public Task Contrast_theme_wins_over_light() => Task.CompletedTask;

    [Fact]
    [Trait(UiTest.TC, "TC-UI-27-01")]
    public Task Mica_backdrop_is_applied() => UiTestContext.RunAsync(async ctx =>
    {
        // 手順 2 のデスクトップの背景の変更は OS の設定を変えるため行わない。背景素材の種類と、Hex ビューの背景が
        // 不透明 (画面の取得で同じ色が 2 回とも取れる) であることを確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.Equal("Mica", (await app.StateAsync())["backdrop"]!.GetValue<string>());
        Assert.Contains(await app.LogAsync(), l => l.Contains("Backdrop: Mica", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-27-03")]
    public Task Accent_color_setting_is_used_by_accent_controls() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SET-ACCENT。設定画面 (UI-24) はフェーズ 1 のため、アクセントの色を使う部品として、スタートページの
        // 「開く」ボタン (AccentButtonStyle) の塗りの色を画面から取る。
        string profile = ctx.NewProfile();
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"ui.accentColor\": \"#E81123\", \"ui.theme\": \"light\"}");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.WaitForAsync("Start_Open");
        await Task.Delay(500);

        uint color = SampleBackground(app, "Start_Open");
        var allowed = AccentShades(0xE8, 0x11, 0x23).ToList();
        Assert.True(allowed.Any(a => Close(a, color)), $"the accent button is #{color & 0xFFFFFF:X6}, not #E81123 or its shades");
    });

    [Fact(Skip = "Windows の「透明効果」(EnableTransparency) を切り替える必要があり、作業中の PC の設定を変えるため自動では実行しない (CI の専用の環境が必要)")]
    [Trait(UiTest.TC, "TC-UI-27-04")]
    public Task Transparency_off_uses_a_solid_background() => Task.CompletedTask;

    [Fact]
    [Trait(UiTest.TC, "TC-UI-40-02")]
    public Task Report_problem_url_does_not_contain_paths() => UiTestContext.RunAsync(async ctx =>
    {
        // ブラウザは起動しない (テスト用のビルドは URL の起動をログに書く)。C:\secret-folder の代わりにテストの一時フォルダの
        // secret-folder (同じく C:\ のドライブ) を使う。
        string folder = Directory.CreateDirectory(Path.Combine(ctx.Root, "secret-folder")).FullName;
        string path = Path.Combine(folder, "seq.bin");
        File.Copy(ctx.TestData("TD-SEQ-1M"), path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        await app.CommandAsync("Command_ReportProblem");
        string line = await app.WaitForLogAsync(l => l.Contains("Test hooks: launch ", StringComparison.Ordinal), "the issue URL");
        var url = new Uri(line[(line.IndexOf("Test hooks: launch ", StringComparison.Ordinal) + "Test hooks: launch ".Length)..].Trim());
        Assert.Equal("github.com", url.Host);
        Assert.EndsWith("/issues/new", url.AbsolutePath, StringComparison.Ordinal);
        string body = Uri.UnescapeDataString(url.Query);
        Assert.Contains("Version: ", body, StringComparison.Ordinal);
        Assert.Contains("Distribution: ", body, StringComparison.Ordinal);
        foreach (string secret in new[] { "secret-folder", "seq.bin", @"C:\", Environment.UserName })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-PKG-28-03")]
    public Task About_shows_the_version_with_the_commit_hash() => UiTestContext.RunAsync(async ctx =>
    {
        // リリースのワークフローのポータブル版の代わりに、手元のビルドで同じ形式 (SemVer + 短いハッシュ) を確かめる。
        // ハッシュはビルドしたコミット (このリポジトリにあるコミット) であること。
        AppSession app = await ctx.StartAsync();
        await app.CommandAsync("Command_About");
        string version = await app.UiaNameAsync("About_Version");
        Match m = VersionPattern().Match(version);
        Assert.True(m.Success, $"'{version}' is not <SemVer>+<commit>");
        string hash = m.Groups["commit"].Value;
        Assert.True(GitCommitExists(hash), $"{hash} is not a commit of this repository");
        await app.InvokeDialogButtonAsync("Close");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-52-03")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task F6_and_shift_f6_move_between_regions() => UiTestContext.RunAsync(async ctx =>
    {
        // データインスペクタ (右パネル) を表示した状態。仕様 1 の順 (タブ列 → エディタ → 右パネル → …) で、F6 はエディタの次の
        // 右パネルに移る。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("I", ctrl: true, shift: true);
        await app.WaitUntilAsync(() => app.IsShownAsync("RightPanel"), TimeSpan.FromSeconds(10), "the right panel");
        Assert.StartsWith("HexView", await app.FocusAsync("editor"), StringComparison.Ordinal);

        await app.KeyAsync("F6");
        await app.IdleAsync();
        JsonObject focus = await app.SendAsync("focus", new JsonObject { ["target"] = "none" });
        Assert.Contains("RightPanel", focus["where"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);

        Assert.StartsWith("HexView", await app.FocusAsync("editor"), StringComparison.Ordinal);
        await app.KeyAsync("F6", shift: true);
        await app.IdleAsync();
        Assert.StartsWith("TabViewItem", await app.FocusedAsync(), StringComparison.Ordinal);
    });

    [Fact(Skip = "Windows の文字の大きさ (TextScaleFactor) を 225% にする必要があり、作業中の PC の設定を変えるため自動では実行しない (CI の専用の環境が必要)")]
    [Trait(UiTest.TC, "TC-UI-53-02")]
    public Task Text_scale_225_does_not_clip_text() => Task.CompletedTask;

    // ---- 補助 ----

    /// <summary>点 (クライアント領域の物理ピクセル) が矩形のどれかに入っているか。</summary>
    private static bool InAny(JsonArray rects, double x, double y) => rects.Any(r =>
        x >= r!["left"]!.GetValue<int>() && x < r["right"]!.GetValue<int>() && y >= r["top"]!.GetValue<int>() && y < r["bottom"]!.GetValue<int>());

    [GeneratedRegex(@"^(?<semver>\d+\.\d+\.\d+(-preview\.\d+|-local)?)\+(?<commit>[0-9a-f]{7,40})$")]
    private static partial Regex VersionPattern();

    private static bool GitCommitExists(string hash)
    {
        using var git = Process.Start(new ProcessStartInfo("git", ["-C", AppLocator.RepositoryRoot, "cat-file", "-e", hash + "^{commit}"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        git.WaitForExit(10_000);
        return git.ExitCode == 0;
    }

    /// <summary>要素の左上の内側の点の色 (文字に重ならない位置)。</summary>
    internal static uint SampleBackground(AppSession app, string automationId)
    {
        var r = app.Find(automationId)!.BoundingRectangle;
        NativeMethods.GetWindowRect(app.Hwnd, out NativeMethods.Rect window);
        WindowImage image = app.Screenshot();
        return image.Pixel(r.Left - window.Left + 6, r.Top - window.Top + (r.Height / 2));
    }

    /// <summary>アクセントカラーとアプリが計算する派生色 (Light1〜3、Dark1〜3。Appearance.ApplyAccent と同じ計算)。</summary>
    private static IEnumerable<uint> AccentShades(byte r, byte g, byte b)
    {
        yield return Rgb(r, g, b);
        foreach (double t in new[] { 0.25, 0.5, 0.75 })
        {
            yield return Rgb((byte)Math.Round(r + ((255 - r) * t)), (byte)Math.Round(g + ((255 - g) * t)), (byte)Math.Round(b + ((255 - b) * t)));
            yield return Rgb((byte)Math.Round(r * (1 - t)), (byte)Math.Round(g * (1 - t)), (byte)Math.Round(b * (1 - t)));
        }
    }

    private static uint Rgb(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    private static bool Close(uint a, uint b) =>
        Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= 4
        && Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= 4
        && Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= 4;
}
