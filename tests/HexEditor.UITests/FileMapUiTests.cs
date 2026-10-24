using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>ファイルマップの作成 (ANA-33) とパネルの表示・操作 (ANA-34)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FileMapUiTests
{
    internal static JsonObject En => new() { ["culture"] = "en-US", ["timeZone"] = "UTC" };

    private static readonly TimeSpan BuildTimeout = UiTest.Scaled(TimeSpan.FromMinutes(2));

    internal static Task<JsonObject> MapAsync(AppSession app, string action, JsonObject? args = null, bool regions = true)
    {
        JsonObject request = args ?? new JsonObject();
        request["action"] = action;
        request["regions"] = regions;
        return app.SendAsync("fileMap", request, BuildTimeout);
    }

    /// <summary>TD-ANA-MAP-MIX を開き、ファイルマップを作り終えた状態にする。</summary>
    internal static async Task<(AppSession App, JsonObject State)> MixAsync(UiTestContext ctx, Func<AppSession, Task>? before = null)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-MAP-MIX")], Hooks = En });
        if (before is not null)
        {
            await before(app);
        }

        JsonObject state = await MapAsync(app, "build");
        Assert.True(state["complete"]!.GetValue<bool>(), state.ToJsonString());
        return (app, state);
    }

    internal static IEnumerable<JsonObject> Regions(JsonObject state) => state["regions"]!.AsArray().Select(r => r!.AsObject());

    internal static JsonObject Region(JsonObject state, string kind, long? offset = null) =>
        Regions(state).First(r => r["kind"]!.GetValue<string>() == kind && (offset is null || r["offset"]!.GetValue<long>() == offset));

    private static int Index(JsonObject region) => region["index"]!.GetValue<int>();

    private static long L(JsonNode? n) => n!.GetValue<long>();

    // ---- ANA-33 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-33-08")]
    public Task Editing_after_the_map_offers_a_rebuild() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await MixAsync(ctx);

        // 1. 0x10000 に 41 を上書きで入力する。
        await app.GoToAsync(0x10000);
        await app.TypeAsync("41");
        await app.IdleAsync();

        // 2. 「結果は編集前の内容のものです」と「作り直す」。
        JsonObject state = await MapAsync(app, "state", regions: false);
        Assert.True(state["staleShown"]!.GetValue<bool>());
        Assert.True(state["rebuildShown"]!.GetValue<bool>());
        Assert.Equal("The results are for the content before editing.", await app.UiaNameAsync("FileMap_Stale"));

        // 3. 「作り直す」を押し、終わるのを待つ: 表示が消える。
        await app.UiaInvokeAsync("FileMap_Rebuild");
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await MapAsync(app, "state", regions: false);
            return !s["building"]!.GetValue<bool>() && s["complete"]!.GetValue<bool>() && !s["staleShown"]!.GetValue<bool>();
        }, BuildTimeout, "the rebuilt file map");
    });

    // ---- ANA-34 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Clicking_the_band_moves_the_cursor_and_double_click_selects_the_region() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        await app.GoToAsync(0);
        JsonObject random = Region(state, "encrypted");

        // 1〜2. 暗号化・乱数の領域の中央をクリックする: カーソルが 0x160000〜0x25FFFF の中。
        await MapAsync(app, "bandClick", new JsonObject { ["region"] = Index(random) }, regions: false);
        long cursor = L((await app.DocumentAsync())["cursor"]);
        Assert.InRange(cursor, 0x160000, 0x25FFFF);

        // 3. Alt+←: オフセット 0 に戻る。
        await app.KeyAsync("Left", alt: true);
        Assert.Equal(0, L((await app.DocumentAsync())["cursor"]));

        // 4. ダブルクリック: 選択範囲がその領域になる。
        await MapAsync(app, "bandDoubleClick", new JsonObject { ["region"] = Index(random) }, regions: false);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(L(random["offset"]), L(doc["selectionStart"]));
        Assert.Equal(L(random["length"]), L(doc["selectionLength"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-02")]
    public Task The_band_can_be_used_with_the_keyboard_only() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        await app.GoToAsync(0);
        JsonObject[] content = [.. Regions(state).Where(r => r["lane"]!.GetValue<string>() == "content")];

        // 1. Tab で帯にフォーカスを移す (カーソル位置を含む内容のレーンの領域を選ぶ)。
        JsonObject s = await MapAsync(app, "focusBand", regions: false);
        Assert.True(s["bandFocused"]!.GetValue<bool>());
        Assert.Equal(Index(content[0]), s["bandSelected"]!.GetValue<int>());

        // 2. → を 3 回: 内容のレーンの 4 番目の領域。
        s = await MapAsync(app, "bandKey", new JsonObject { ["key"] = "Right", ["count"] = 3 }, regions: false);
        Assert.Equal(Index(content[3]), s["bandSelected"]!.GetValue<int>());

        // 3. ↑: 構造のレーンの領域。
        s = await MapAsync(app, "bandKey", new JsonObject { ["key"] = "Up" });
        Assert.Equal("structure", Regions(s).Single(r => Index(r) == s["bandSelected"]!.GetValue<int>())["lane"]!.GetValue<string>());

        // 4. ↓、Enter: カーソルが内容のレーンで選んだ領域の先頭に移り、その範囲が選択される。
        s = await MapAsync(app, "bandKey", new JsonObject { ["key"] = "Down" }, regions: false);
        int chosen = s["bandSelected"]!.GetValue<int>();
        JsonObject target = Regions(state).Single(r => Index(r) == chosen);
        Assert.Equal("content", target["lane"]!.GetValue<string>());
        await MapAsync(app, "bandKey", new JsonObject { ["key"] = "Enter" }, regions: false);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(L(target["offset"]), L(doc["cursor"]));
        Assert.Equal(L(target["offset"]), L(doc["selectionStart"]));
        Assert.Equal(L(target["length"]), L(doc["selectionLength"]));

        // 5. Shift+F10: その領域の右クリックメニューが開く。
        await MapAsync(app, "bandKey", new JsonObject { ["key"] = "ContextMenu" }, regions: false);
        await app.WaitUntilAsync(async () => (await MapAsync(app, "state", regions: false))["menuOpen"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the region menu");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-03")]
    public Task Applying_the_png_template_to_the_embedded_png() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx, a => a.SendAsync("settingSet", new JsonObject { ["key"] = "fileMap.applyTemplatesToEmbedded", ["value"] = "none" }));
        JsonObject png = Region(state, "embedded", 0x50000);
        Assert.Null(state["suggestion"]);

        // 1〜2. 「テンプレート "PNG image" を適用」(適用の設定画面は出ない)。
        JsonObject menu = await MapAsync(app, "menu", new JsonObject { ["region"] = Index(png) }, regions: false);
        JsonObject apply = menu["items"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["id"]!.GetValue<string>() == "ApplyTemplate");
        Assert.Equal("Apply template \"PNG image\"", apply["label"]!.GetValue<string>());
        await MapAsync(app, "run", new JsonObject { ["region"] = Index(png), ["id"] = "ApplyTemplate" }, regions: false);

        // 3. 結果ツリー: 0x50000 の PNG のテンプレートの固定の適用、chunks[0].type が IHDR。
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await MapAsync(app, "state", regions: false);
            return s["templates"]!.AsArray().Any(t => L(t!["start"]) == 0x50000 && t["chunk0Type"]?.GetValue<string>() == "IHDR");
        }, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the PNG application with IHDR");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-04")]
    public Task Applying_templates_to_found_files_stops_at_thirty_two() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);

        // 1〜2. InfoBar: テンプレートの決まった埋め込まれたファイル 2 個 (PNG と ZIP) と「適用」「一覧を表示」。
        JsonObject suggestion = state["suggestion"]!.AsObject();
        Assert.Equal("2 embedded files were found. Apply templates?", suggestion["message"]!.GetValue<string>());
        Assert.Equal(["Apply", "Show list"], suggestion["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        await MapAsync(app, "suggestion", new JsonObject { ["invoke"] = 0 }, regions: false);

        // 3. 0x50000 (PNG) と 0x360000 (ZIP) の固定の適用。
        JsonObject s = await MapAsync(app, "state", regions: false);
        long[] starts = [.. s["templates"]!.AsArray().Select(t => L(t!["start"]))];
        Assert.Contains(0x50000L, starts);
        Assert.Contains(0x360000L, starts);

        // 4〜5. TD-ANA-MAP-PNG40 (先頭の PNG は形式の構造、残りの 39 個が埋め込まれたファイル): 32 個で止まり、適用しなかった 7 個を知らせる。
        await app.OpenAsync(ctx.TestData("TD-ANA-MAP-PNG40"));
        await app.IdleAsync();
        await MapAsync(app, "build", regions: false);
        await app.SendAsync("execute", new JsonObject { ["id"] = "analysis.fileMap.applyTemplates", ["fromPalette"] = true });
        s = await MapAsync(app, "state", regions: false);
        Assert.Equal(32, s["templates"]!.AsArray().Count);
        Assert.Contains("7 files were not applied", s["lastFileMapNotice"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-05")]
    public Task Inferring_a_template_from_repeating_records() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);

        // テンプレートの推定 (TPL-38) がつながっていないビルドでは、コマンドの代わりに要求を記録する。
        await MapAsync(app, "installInferProbe", regions: false);
        JsonObject records = Region(state, "records");
        await MapAsync(app, "run", new JsonObject { ["region"] = Index(records), ["id"] = "InferTemplate" }, regions: false);
        await app.WaitUntilAsync(async () => (await MapAsync(app, "state", regions: false))["lastInfer"] is JsonObject, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the inference request");
        JsonObject s = await MapAsync(app, "state", regions: false);
        Assert.Equal(0x260000, L(s["lastInfer"]!["start"]));
        Assert.Equal(L(records["length"]), L(s["lastInfer"]!["length"]));
        Assert.Equal(64, L(s["lastInfer"]!["recordLength"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-06")]
    public Task Decompressing_from_a_compressed_region_opens_the_dialog() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        JsonObject zlib = Region(state, "compressed", 0x60000);
        Assert.Equal("zlib", zlib["label"]!.GetValue<string>());
        await MapAsync(app, "run", new JsonObject { ["region"] = Index(zlib), ["id"] = "Decompress" }, regions: false);
        await app.WaitForAsync("DecompressDialog");
        JsonObject dialog = await app.SendAsync("codecDialog");
        Assert.Equal(0x60000, L(dialog["start"]));
        Assert.Equal("Zlib", dialog["format"]!.GetValue<string>());
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-07")]
    public Task Region_menus_show_only_usable_items() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        await MapAsync(app, "installInferProbe", regions: false);
        var expected = new Dictionary<string, string[]>
        {
            ["structure"] = ["Select", "Bookmark", "ApplyTemplate"],
            ["embedded"] = ["Select", "Bookmark", "ApplyTemplate", "OpenTab", "SaveAs"],
            ["compressed"] = ["Select", "Bookmark", "OpenTab", "Decompress"],
            ["encrypted"] = ["Select", "Bookmark"],
            ["text"] = ["Select", "Bookmark", "OpenTab", "Strings"],
            ["zero"] = ["Select", "Bookmark"],
            ["records"] = ["Select", "Bookmark", "OpenTab", "InferTemplate"],
        };
        foreach ((string kind, string[] ids) in expected)
        {
            JsonObject region = kind == "embedded" ? Region(state, kind, 0x50000) : kind == "compressed" ? Region(state, kind, 0x60000) : Region(state, kind);
            JsonObject menu = await MapAsync(app, "menu", new JsonObject { ["region"] = Index(region) }, regions: false);
            JsonObject[] items = [.. menu["items"]!.AsArray().Select(i => i!.AsObject())];
            Assert.Equal(ids, items.Select(i => i["id"]!.GetValue<string>()));
            Assert.All(items, i => Assert.True(i["enabled"]!.GetValue<bool>(), kind + ": " + i["id"]));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-10")]
    public Task Legend_patterns_differ_for_every_kind() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        await MapAsync(app, "show", regions: false);
        static string Part(string status, string key) => status.Split(';').Single(p => p.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

        // 1. ライトテーマ: 名前に種類、模様が種類ごとにすべて違う (「標本による推定」を含む)。
        JsonObject[] legend = [.. (await MapAsync(app, "state", regions: false))["legend"]!.AsArray().Select(i => i!.AsObject())];
        Assert.Equal(11, legend.Length);
        Assert.Contains(legend, i => i["name"]!.GetValue<string>().StartsWith("Embedded file", StringComparison.Ordinal));
        Assert.Contains(legend, i => i["name"]!.GetValue<string>() == "Estimated from samples");
        string[] patterns = [.. legend.Select(i => Part(i["status"]!.GetValue<string>(), "pattern"))];
        Assert.Equal(patterns.Length, patterns.Distinct().Count());

        // 2. ハイコントラスト: 色のリソース名がすべてシステム色。
        await MapAsync(app, "highContrast", new JsonObject { ["on"] = true }, regions: false);
        legend = [.. (await MapAsync(app, "state", regions: false))["legend"]!.AsArray().Select(i => i!.AsObject())];
        Assert.All(legend, i =>
        {
            string status = i["status"]!.GetValue<string>();
            Assert.StartsWith("SystemColor", Part(status, "color"));
            Assert.StartsWith("SystemColor", Part(status, "stroke"));
        });
        await MapAsync(app, "highContrast", new JsonObject { ["on"] = false }, regions: false);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-34-11")]
    public Task Band_regions_have_automation_names() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await MixAsync(ctx);
        await MapAsync(app, "show", regions: false);
        await app.IdleAsync();

        // 1. 名前のない操作可能な要素がない (Axe.Windows の規則の 1 つ。Axe.Windows は同梱していない)。
        Assert.DoesNotContain(ViewMiscTests.UnnamedInteractiveElements(app, "file map"), m => m.Contains("FileMap", StringComparison.Ordinal));

        // 2. 帯の領域の要素の名前: 種類、ラベル、開始オフセット、長さ、確度。
        AutomationElement band = await app.WaitForAsync("FileMap_Band");
        AutomationElement[] items = band.FindAllChildren();
        Assert.NotEmpty(items);
        foreach (AutomationElement item in items)
        {
            string id = item.Properties.AutomationId.Value;
            JsonObject region = Regions(state).Single(r => "FileMap_Region_" + Index(r) == id);
            Assert.Equal(region["name"]!.GetValue<string>(), item.Properties.Name.Value);
            Assert.Contains("0x" + L(region["offset"]).ToString("X"), item.Properties.Name.Value);
            Assert.Contains("confidence", item.Properties.Name.Value);
        }

        // 3. 帯にフォーカスを移して →: 選んだ領域の要素へのフォーカスの移動 (または選択) のイベント。
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var focus = AppSession.Automation.RegisterFocusChangedEvent(e =>
        {
            try
            {
                events.Enqueue(e.Properties.AutomationId.ValueOrDefault ?? string.Empty);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
            {
            }
        });
        using var selected = band.RegisterAutomationEvent(band.Automation.EventLibrary.SelectionItem.ElementSelectedEvent, FlaUI.Core.Definitions.TreeScope.Descendants,
            (e, _) =>
            {
                try
                {
                    events.Enqueue(e.Properties.AutomationId.ValueOrDefault ?? string.Empty);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
                {
                }
            });
        await MapAsync(app, "focusBand", regions: false);
        JsonObject s = await MapAsync(app, "bandKey", new JsonObject { ["key"] = "Right" }, regions: false);
        string expectedId = "FileMap_Region_" + s["bandSelected"]!.GetValue<int>();
        // イベントが届かない環境 (ウィンドウが前面にない) では、選択のパターンで選んだ領域の要素が分かることを確かめる。
        await app.WaitUntilAsync(() => Task.FromResult(events.Contains(expectedId)
            || band.Patterns.Selection.Pattern.Selection.Value.Any(e => e.Properties.AutomationId.ValueOrDefault == expectedId)),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the region focus event");
    });

    // ---- 性能 (性能テスト用の固定の環境だけで判定する) ----

    /// <summary>TC-ANA-33-03: 100 GB のファイルで概観が 5 秒以内に出て、全体の走査の間も Hex ビューのスクロールが 60 fps を保つ。</summary>
    [PerfEnvironmentFact]
    [Trait(UiTest.Category, PerformanceTests.Performance)]
    [Trait(UiTest.TC, "TC-ANA-33-03")]
    public Task Overview_of_a_hundred_gigabytes_is_fast_and_scrolling_stays_smooth() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")], Hooks = En });

        // 1. 作り始め、概観の段階が終わるまでの時間 (アプリの中で計る)。
        await MapAsync(app, "start", regions: false);
        await app.WaitUntilAsync(async () => (await MapAsync(app, "state", regions: false))["overviewMs"] is JsonValue, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the overview");
        double overview = (await MapAsync(app, "state", regions: false))["overviewMs"]!.GetValue<double>();
        Assert.True(overview <= 5000, $"overview {overview:F0} ms");

        // 2. 全体の走査の間、Hex ビューを 10 秒間スクロールし、フレームの間隔を記録する。
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; watch.Elapsed < TimeSpan.FromSeconds(10); i++)
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = "PageDown" });
            int wait = (int)((i + 1) * 30 - watch.ElapsedMilliseconds);
            if (wait > 0)
            {
                await Task.Delay(wait);
            }
        }

        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });

        // 3. キャンセルする。
        await MapAsync(app, "cancel", regions: false);
        FrameLog frames = FrameLog.Read(log);
        IReadOnlyList<double> intervals = [.. frames.Frames.Select(f => f.Interval)];
        Assert.NotEmpty(intervals);
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= 16.7 && intervals.Max() <= 50, FrameLog.Describe(intervals));
    }, TimeSpan.FromMinutes(5));

    /// <summary>TC-ANA-34-13: 領域が 100 万個 (TD-ANA-MAP-ALT-4G) でも、帯の拡大・縮小とスクロールが 60 fps を保つ。</summary>
    [PerfEnvironmentFact]
    [Trait(UiTest.Category, PerformanceTests.Performance)]
    [Trait(UiTest.TC, "TC-ANA-34-13")]
    public Task A_million_regions_keep_the_band_at_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = En, WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject
        {
            ["name"] = "TD-ANA-MAP-ALT-4G", ["length"] = TestDataCatalog.MapAlternatingLength, ["content"] = "AlternatingBlocks", ["seed"] = 0xA170000,
        });
        await app.IdleAsync();
        JsonObject state = await app.SendAsync("fileMap", new JsonObject { ["action"] = "build", ["regions"] = false }, TimeSpan.FromMinutes(20));
        Assert.True(state["regionCount"]!.GetValue<int>() >= 1_000_000, state["regionCount"]!.ToJsonString());

        // 1〜2. 最小から最大まで 20 段階拡大し、最小まで縮小し、最大の拡大で端から端までスクロールする (描画の時間はアプリの中で計る)。
        var times = new List<double>();
        async Task Step(string action, JsonObject args)
        {
            args["action"] = action;
            args["regions"] = false;
            times.Add((await app.SendAsync("fileMap", args))["lastDrawMs"]!.GetValue<double>());
        }

        for (int i = 0; i < 20; i++)
        {
            await Step("zoom", new JsonObject { ["factor"] = 2.0 });
        }

        for (int i = 0; i < 20; i++)
        {
            await Step("zoom", new JsonObject { ["factor"] = 0.5 });
        }

        for (int i = 0; i < 20; i++)
        {
            await Step("zoom", new JsonObject { ["factor"] = 2.0 });
        }

        for (int i = 0; i <= 100; i++)
        {
            await Step("scroll", new JsonObject { ["offset"] = TestDataCatalog.MapAlternatingLength / 100 * i });
        }

        double p99 = times.Order().ElementAt((int)Math.Floor(times.Count * 0.99) - 1);
        Assert.True(p99 <= 16.7 && times.Max() <= 50, $"draw p99 {p99:F1} ms, max {times.Max():F1} ms");
    }, TimeSpan.FromMinutes(30));
}
