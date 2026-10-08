using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.Definitions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 国際化: 未翻訳の文字列 (UI-42)、右から左に書く言語 (UI-02、UI-44)、地域設定 (UI-45)、疑似翻訳 (UI-46)、切れの検出 (UI-47)。
/// 地域設定は OS を変えずに、異常を再現する仕組みの「地域設定の上書き」(--test-hooks の culture) で指定する。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class LocalizationTests
{
    private const long MiB = 1L << 20;

    [Fact]
    [Trait(UiTest.TC, "TC-UI-02-08")]
    public Task Arabic_title_bar_is_mirrored() => UiTestContext.RunAsync(async ctx =>
    {
        // コマンドパレットの入口 (UI-17) はフェーズ 1 のため、アイコン・メニューバー・タイトル・操作ボタンの並びを確かめる。
        AppSession en = await ctx.StartAsync(new AppOptions { UiLanguage = "en", Profile = ctx.NewProfile() });
        TitleBarLayout ltr = await TitleBarAsync(en);
        Assert.True(ltr.Icon < ltr.Menu && ltr.Menu < ltr.Title, $"en: {ltr}");
        Assert.True(ltr.RightInset > 0 && ltr.LeftInset == 0, $"en: {ltr}");

        AppSession ar = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Profile = ctx.NewProfile() });
        TitleBarLayout rtl = await TitleBarAsync(ar);

        // 右から順に: アイコン、メニューバー、タイトル (コマンドパレットの入口はフェーズ 1)。
        Assert.True(rtl.Icon > rtl.Menu && rtl.Menu > rtl.Title, $"ar: {rtl}");

        // 操作ボタン (最小化・最大化・閉じる) は Windows が描く。ExtendsContentIntoTitleBar のウィンドウでは、ウィンドウを
        // WS_EX_LAYOUTRTL にしないと左端に移らず、WS_EX_LAYOUTRTL にすると XAML の内容まで鏡像になる (2026-10-07 に確認)。
        // そのため操作ボタンは右端のままで、内容が操作ボタンに重ならないこと (右の余白を空けていること) を確かめる。
        Assert.True(rtl.RightInset > 0, $"ar: {rtl}");
        Assert.True(rtl.IconRight <= rtl.WindowRight - rtl.RightInset + 1, $"ar: the icon is under the caption buttons ({rtl})");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-42-02")]
    public Task Strings_without_a_translation_are_shown_in_english() => UiTestContext.RunAsync(async ctx =>
    {
        // 「de の .resw からキーを消した確認用のビルド」は作らず、今のビルドで同じ規則を確かめる: メニューの各項目は、
        // de の .resw にキーがあればドイツ語の訳、なければ英語の原文で表示される (MRT のフォールバック)。
        Dictionary<string, string> de = UiHelpers.LoadResw("de");
        Dictionary<string, string> en = UiHelpers.LoadResw("en");
        Dictionary<string, string> uidById = MenuUids();
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "de" });
        JsonArray items = (await app.SendAsync("menuTexts"))["items"]!.AsArray();

        int translated = 0;
        foreach (JsonObject item in items.Select(i => i!.AsObject()))
        {
            string text = item["text"]!.GetValue<string>();
            string key = item["menu"]?.GetValue<bool>() == true
                ? MenuTitleKey(items, item)
                : uidById.TryGetValue(item["id"]!.GetValue<string>(), out string? uid) ? uid + ".Text" : string.Empty;
            if (key.Length == 0 || !en.ContainsKey(key))
            {
                continue; // コードで文字列を入れる項目 (書式付きの文字列) は対象外。
            }

            string expected = de.TryGetValue(key, out string? german) ? german : en[key];
            Assert.True(expected == text, $"{key}: '{text}' (expected '{expected}')");
            translated += de.ContainsKey(key) ? 1 : 0;
        }

        Assert.True(translated > 10, "the German menus are not translated");

        // 「編集」はドイツ語の訳で表示される。
        Assert.Equal(de["Menu_Edit.Title"], items.Select(i => i!.AsObject()).First(i => i["menu"]?.GetValue<bool>() == true && i["text"]!.GetValue<string>() == de["Menu_Edit.Title"])["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-44-01")]
    public Task Arabic_menus_run_right_to_left_and_hex_view_stays_left_to_right() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1. 最初のメニュー (ファイル) が一番右にあり、左へ 00-overview.md 7 章の順に並ぶ。
        var menus = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu"))!.FindAllChildren()
            .Where(m => AppSession.NameOf(m) != "Test").ToList();
        Dictionary<string, string> ar = UiHelpers.LoadResw("ar"), en = UiHelpers.LoadResw("en");
        string[] order = ["Menu_File", "Menu_Edit", "Menu_Search", "Menu_Go", "Menu_View", "Menu_Analysis", "Menu_Tools", "Menu_Help"];

        // 未翻訳のメニュー (機械翻訳の前の新しいもの) は英語で表示される。
        Assert.Equal(order.Select(k => ar.GetValueOrDefault(k + ".Title") ?? en[k + ".Title"]), menus.Select(AppSession.NameOf));
        for (int i = 1; i < menus.Count; i++)
        {
            Assert.True(menus[i].BoundingRectangle.Right <= menus[i - 1].BoundingRectangle.Left + 1, $"{AppSession.NameOf(menus[i])} is not left of {AppSession.NameOf(menus[i - 1])}");
        }

        // 2. Hex ビューは左から順にオフセット列・Hex 列・テキスト列 (Hex ビューの中の位置)。
        JsonObject render = await app.RenderAsync();
        Assert.Equal("LeftToRight", render["flowDirection"]!.GetValue<string>());
        Assert.True(render["offsetVisualLeft"]!.GetValue<double>() < render["contentVisualLeft"]!.GetValue<double>(), "the offset column is not on the left");
        string line = render["rows"]![0]!["line"]!.GetValue<string>();
        Assert.Matches("^0+  00 01 02 03", line);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-44-02")]
    public Task Arabic_offset_input_is_left_to_right() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("G", ctrl: true);
        await app.IdleAsync();
        await app.UiaSetValueAsync("GoTo_Input", "0x1F00");
        Assert.Equal("LeftToRight", (await app.ElementAsync("GoTo_Input"))["flowDirection"]!.GetValue<string>());

        // 2. Text パターンで先頭の 0 と末尾の 0 の表示位置を取る。
        var box = await app.WaitForAsync("GoTo_Input");
        var document = box.Patterns.Text.Pattern.DocumentRange;
        var first = document.Clone();
        first.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -5);
        var last = document.Clone();
        last.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 5);
        Assert.Equal("0", first.GetText(-1));
        Assert.Equal("0", last.GetText(-1));
        double firstLeft = first.GetBoundingRectangles().Single().Left;
        double lastLeft = last.GetBoundingRectangles().Single().Left;
        Assert.True(firstLeft < lastLeft, $"the first character ({firstLeft}) is not left of the last ({lastLeft})");

        // 入力欄の左端に寄っている (右端より左端に近い)。
        var r = box.BoundingRectangle;
        Assert.True(firstLeft - r.Left < r.Right - last.GetBoundingRectangles().Single().Right, "the text is not aligned to the left");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-44-03")]
    public Task Arabic_right_arrow_moves_to_the_next_byte() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar", Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.KeyAsync("Right");
        Assert.Equal(0x11, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-44-04")]
    public Task Arabic_and_persian_screens_are_mirrored() => UiTestContext.RunAsync(async ctx =>
    {
        // UI-47 の主要な画面のうち、メインウィンドウ (タイトルバー・メニュー・タブ列・ステータスバー)、検索バー、移動バーを、
        // 英語と比べる (パネルはフェーズ 1)。
        string seq = ctx.TestData("TD-SEQ-1M");
        Layout en = await LayoutAsync(await ctx.StartAsync(new AppOptions { UiLanguage = "en", Files = [seq], Profile = ctx.NewProfile() }));
        Assert.Equal("LeftToRight", en.FlowDirection);
        foreach (string language in new[] { "ar", "fa" })
        {
            Layout rtl = await LayoutAsync(await ctx.StartAsync(new AppOptions { UiLanguage = language, Files = [seq], Profile = ctx.NewProfile() }));
            Assert.Equal("RightToLeft", rtl.FlowDirection);

            // 1. 要素の左右の並びが英語の反転になっている (左にあったものが右に)。
            foreach ((string a, string b) in Layout.Pairs)
            {
                Assert.True(en.LeftOf(a, b), $"en: {a} is not left of {b}");
                Assert.True(rtl.LeftOf(b, a), $"{language}: {b} {rtl.Bounds[b]} is not left of {a} {rtl.Bounds[a]} (not mirrored)");
            }

            // 2. Hex 表示・オフセットの入力欄・Hex の検索欄・オフセットの表示は左から右のまま。
            foreach ((string id, string flow) in rtl.LeftToRightElements)
            {
                Assert.True(flow == "LeftToRight", $"{language}: {id} is {flow}");
            }
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-45-01")]
    public Task German_region_formats_the_file_size() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = "en",
            Files = [ctx.TestData("TD-UI-SPARSE-1536M")],
            Hooks = new JsonObject { ["culture"] = "de-DE" },
        });
        string size = await app.UiaNameAsync("Status_Size");

        // 数値は地域設定 (桁区切り . と小数点 ,)、語は表示言語 (英語の bytes)。
        Assert.Contains("1.610.612.736", size, StringComparison.Ordinal);
        Assert.Contains("1,50 GB", size, StringComparison.Ordinal);
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo("de-DE"), UiHelpers.LoadResw("en")["Status_Size"], "1,50 GB", "1.610.612.736"), size);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-45-02")]
    public Task German_region_does_not_change_offset_input() => UiTestContext.RunAsync(async ctx =>
    {
        // オフセットの基数の設定 (F1-05) はフェーズ 1。小数を含む `1.5K` は既定のままでも 10 進として読む (00-overview 6 章)。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-SEQ-1M")],
            Hooks = new JsonObject { ["culture"] = "de-DE" },
        });
        await app.KeyAsync("G", ctrl: true);
        await app.IdleAsync();
        await app.UiaSetValueAsync("GoTo_Input", "1.5K");
        Assert.Equal("= 0x600 (1.536)", await app.UiaNameAsync("GoTo_Interpretation"));
        await app.UiaInvokeAsync("GoTo_Go");
        await app.IdleAsync();
        Assert.Equal(0x600, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-46-01")]
    public Task Pseudo_locale_wraps_every_menu_item() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = null,
            ExtraArgs = ["--pseudo-locale", "qps-ploc"],
            Files = [ctx.TestData("TD-SEQ-1M")],
        });
        JsonArray items = (await app.SendAsync("menuTexts"))["items"]!.AsArray();
        Assert.True(items.Count > 20);
        var bad = items.Select(i => i!.AsObject())
            .Where(i => !(i["text"]!.GetValue<string>() is var t && t.StartsWith('[') && t.EndsWith(']')))
            .Select(i => $"{i["path"]} ({i["id"]})")
            .ToList();
        Assert.True(bad.Count == 0, "not pseudo-localized: " + string.Join(", ", bad));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-46-02")]
    public Task Pseudo_locale_screens_have_no_clipped_or_overlapping_text() => UiTestContext.RunAsync(async ctx =>
    {
        var problems = new List<string>();
        foreach ((int width, int height) in new[] { (1024, 768), (1920, 1080) })
        {
            AppSession app = await ctx.StartAsync(new AppOptions
            {
                UiLanguage = null,
                ExtraArgs = ["--pseudo-locale", "qps-ploc"],
                Profile = ctx.NewProfile(),
            });
            await foreach (string screen in MainScreens.ShowAsync(app, ctx, width, height))
            {
                problems.AddRange(TrimReport.Describe(await app.TextCheckAsync(), screen, $"{width}x{height}", TrimReport.PseudoKeys()));
            }
        }

        Assert.True(problems.Count == 0, "clipped or overlapping text:\n" + string.Join("\n", problems));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-47-02")]
    public Task Fixed_width_button_is_reported_as_clipped() => UiTestContext.RunAsync(async ctx =>
    {
        // 確認用のビルドの代わりに、テスト用の命令で幅 60 px に固定したボタン (原文 `Export settings`) をスタートページに置く。
        // 設定画面 (UI-24) はフェーズ 1。
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = null, ExtraArgs = ["--pseudo-locale", "qps-ploc"] });
        JsonObject before = await app.TextCheckAsync();
        await app.SendAsync("addProbe", new JsonObject { ["key"] = "Startup_ExportSettings", ["width"] = 60 });
        await app.WaitUntilAsync(async () => await app.IsShownAsync("TestProbe"), TimeSpan.FromSeconds(10), "the probe button");
        await Task.Delay(300);
        await app.IdleAsync();
        JsonObject after = await app.TextCheckAsync();

        // 2. 切れた文字列の一覧に、リソースキー付きで載る。
        var report = TrimReport.Describe(after, "start", "1280x800", TrimReport.PseudoKeys()).ToList();
        Assert.True(report.Any(line => line.Contains("Startup_ExportSettings", StringComparison.Ordinal) && line.Contains("TestProbe", StringComparison.Ordinal)),
            "the probe is not reported:\n" + string.Join("\n", report));
        Assert.Contains("Text clipped", string.Join("\n", await app.LogAsync()), StringComparison.Ordinal);

        // 3. 切れた文字列が増えたときの判定は警告 (失敗ではない)。
        Assert.Equal(TrimVerdict.Warning, TrimReport.Compare(TrimReport.Describe(before, "start", "1280x800", TrimReport.PseudoKeys()).ToList(), report));
    });

    // ---- 補助 ----

    private sealed record TitleBarLayout(double Icon, double Menu, double Title, int LeftInset, int RightInset, double IconRight, double WindowRight);

    /// <summary>タイトルバーの要素の中央の x 座標 (画面の物理ピクセル) と、操作ボタンの左右の余白。</summary>
    private static async Task<TitleBarLayout> TitleBarAsync(AppSession app)
    {
        JsonObject state = await app.StateAsync();
        double Center(string id)
        {
            var r = app.Window.FindFirstDescendant(cf => cf.ByAutomationId(id))?.BoundingRectangle;
            return r is { } rect ? rect.Left + (rect.Width / 2.0) : double.NaN;
        }

        JsonObject icon = await app.ElementAsync("TitleBar_Icon");
        WindowHitTest.ClientOrigin(app.Hwnd, out int originX, out _);
        double scale = state["scale"]!.GetValue<double>();
        double iconCenter = originX + ((icon["left"]!.GetValue<double>() + icon["right"]!.GetValue<double>()) / 2 * scale);
        double iconRight = originX + (icon["right"]!.GetValue<double>() * scale);
        double windowRight = originX + (double.Parse(state["rootSize"]!.GetValue<string>().Split('x')[0], System.Globalization.CultureInfo.InvariantCulture) * scale);
        return new TitleBarLayout(iconCenter, Center("MainMenu"), Center("WindowTitle"),
            state["titleBarLeftInset"]!.GetValue<int>(), state["titleBarRightInset"]!.GetValue<int>(), iconRight, windowRight);
    }

    /// <summary>メニューバーの項目の AutomationId → x:Uid (MainWindow.xaml から読む)。</summary>
    private static Dictionary<string, string> MenuUids()
    {
        string xaml = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "MainWindow.xaml"));
        return Regex.Matches(xaml, "x:Uid=\"(?<uid>Menu_[^\"]+)\"[^>]*?AutomationProperties.AutomationId=\"(?<id>[^\"]+)\"")
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["uid"].Value);
    }

    /// <summary>メニューの見出しのキー (MainWindow.xaml の MenuBarItem の順)。</summary>
    private static string MenuTitleKey(JsonArray items, JsonObject menu)
    {
        string xaml = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "MainWindow.xaml"));
        var uids = Regex.Matches(xaml, "<MenuBarItem x:Uid=\"(?<uid>[^\"]+)\"").Select(m => m.Groups["uid"].Value).ToList();
        int index = items.Select(i => i!.AsObject()).Where(i => i["menu"]?.GetValue<bool>() == true).ToList().IndexOf(menu);
        return index >= 0 && index < uids.Count ? uids[index] + ".Title" : string.Empty;
    }

    /// <summary>主要な画面の要素の位置 (ウィンドウの中のエピクセル) と書字方向。</summary>
    private sealed class Layout
    {
        /// <summary>英語で左 → 右の順に並ぶ要素の組。</summary>
        public static readonly (string, string)[] Pairs =
        [
            ("TitleBar_Icon", "MainMenu"),
            ("MainMenu", "WindowTitle"),
            ("Status_Offset", "Status_Mode"),
            ("Status_Mode", "Status_Size"),
            ("Find_Kind", "Find_Query"),
            ("Find_Query", "Find_Close"),
            ("GoTo_Input", "GoTo_Go"),
        ];

        public string FlowDirection { get; set; } = string.Empty;

        public Dictionary<string, (double Left, double Right)> Bounds { get; } = [];

        public List<(string Id, string Flow)> LeftToRightElements { get; } = [];

        public bool LeftOf(string a, string b) => Bounds[a].Right <= Bounds[b].Left + 1;
    }

    private static async Task<Layout> LayoutAsync(AppSession app)
    {
        var layout = new Layout { FlowDirection = (await app.StateAsync())["flowDirection"]!.GetValue<string>() };
        WindowHitTest.ClientOrigin(app.Hwnd, out int originX, out _);
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();

        async Task Record(params string[] ids)
        {
            foreach (string id in ids)
            {
                // 物理的な左右の位置は UI オートメーション (画面の座標) で取る。アイコンは UI オートメーションに出さないため、
                // アプリの中の位置を使う。
                if (app.Window.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e && id != "TitleBar_Icon")
                {
                    layout.Bounds[id] = (e.BoundingRectangle.Left, e.BoundingRectangle.Right);
                }
                else
                {
                    JsonObject element = await app.ElementAsync(id);
                    layout.Bounds[id] = (originX + (element["left"]!.GetValue<double>() * scale), originX + (element["right"]!.GetValue<double>() * scale));
                }
            }
        }

        await Record("TitleBar_Icon", "MainMenu", "WindowTitle", "Status_Offset", "Status_Mode", "Status_Size");
        layout.LeftToRightElements.Add(("HexView", (await app.RenderAsync())["flowDirection"]!.GetValue<string>()));
        layout.LeftToRightElements.Add(("Status_Offset", (await app.ElementAsync("Status_Offset"))["flowDirection"]!.GetValue<string>()));

        await OperationsTests.OpenFindAsync(app, "41 42");
        await Task.Delay(300);
        await Record("Find_Kind", "Find_Query", "Find_Close");
        layout.LeftToRightElements.Add(("Find_Query", (await app.ElementAsync("Find_Query"))["flowDirection"]!.GetValue<string>()));
        await app.UiaInvokeAsync("Find_Close");

        await app.KeyAsync("G", ctrl: true);
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["goToBarVisible"]!.GetValue<bool>() && app.Find("GoTo_Go") is { } go && go.BoundingRectangle.Width > 0,
            TimeSpan.FromSeconds(10), "the go-to bar");
        await Task.Delay(300);
        await Record("GoTo_Input", "GoTo_Go");
        layout.LeftToRightElements.Add(("GoTo_Input", (await app.ElementAsync("GoTo_Input"))["flowDirection"]!.GetValue<string>()));
        return layout;
    }
}
