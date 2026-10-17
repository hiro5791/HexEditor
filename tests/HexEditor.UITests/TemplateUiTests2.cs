using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>構造体テンプレートの画面のつづき: 値の編集 (TPL-22)、表示 (TPL-02〜TPL-15)、色分け・連動 (TPL-23)、右クリックメニュー (TPL-24)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateEditingUiTests
{
    private static async Task<JsonObject> EditAsync(AppSession app, string path) => await TemplateAsync(app, "edit", new JsonObject { ["path"] = path });

    private static async Task<JsonObject> CommitAsync(AppSession app)
    {
        JsonObject r = await TemplateAsync(app, "editKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
        return r;
    }

    private static async Task<AppSession> OpenWithAsync(UiTestContext ctx, byte[] data, string template)
    {
        string file = ctx.WriteFile("data.bin", data);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, template);
        return app;
    }

    // ───────────── 値の編集 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-22-01")]
    public Task Enum_drop_down_and_flag_checkboxes_write_the_values() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenWithAsync(ctx, [0x64, 0x86, 0x22, 0x20], "import std.pe;\nle std::pe::Machine m; le std::pe::Characteristics c;");
        JsonObject m = await EditAsync(app, "m");
        Assert.Equal("Enum", m["kind"]!.GetValue<string>());
        await TemplateAsync(app, "editSet", new JsonObject { ["enum"] = "ARM64" });
        Assert.True((await CommitAsync(app))["committed"]!.GetValue<bool>());
        await WaitTemplatesAsync(app);
        JsonObject c = await EditAsync(app, "c");
        Assert.Equal("Flags", c["kind"]!.GetValue<string>());
        var checkedNames = c["checked"]!.AsArray().Select(n => n!.GetValue<string>()).Where(n => n != "LargeAddressAware");
        await TemplateAsync(app, "editSet", new JsonObject { ["flags"] = new JsonArray([.. checkedNames.Select(n => (JsonNode?)n)]) });
        await CommitAsync(app);
        Assert.Equal(new byte[] { 0x64, 0xAA, 0x02, 0x20 }, await app.BytesAsync(0, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-22-02")]
    public Task Big_endian_u32_is_overwritten_even_in_insert_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await TemplateAsync(app, "removeAll");
        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.toggleInsert" });
        await ApplyTextAsync(ctx, app, "u8 pad[4]; be u32 v; u8 tail;");
        await EditAsync(app, "v");
        await TemplateAsync(app, "editSet", new JsonObject { ["text"] = "0x12345678" });
        await CommitAsync(app);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, await app.BytesAsync(4, 4));
        Assert.Equal(1048576, (await app.DocumentAsync())["length"]!.GetValue<long>());

        await WaitTemplatesAsync(app);
        await EditAsync(app, "v");
        JsonObject bad = await TemplateAsync(app, "editSet", new JsonObject { ["text"] = "0x1_0000_0000" });
        Assert.False(bad["canCommit"]!.GetValue<bool>());
        Assert.NotEqual(string.Empty, bad["error"]!.GetValue<string>());
        Assert.False((await CommitAsync(app))["committed"]!.GetValue<bool>());
        await TemplateAsync(app, "editKey", new JsonObject { ["key"] = "Escape" });
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-22-03")]
    public Task String_edits_are_undone_with_ctrl_z_and_too_long_strings_are_rejected() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "char name[8]; u8 after;");
        await EditAsync(app, "name");
        await TemplateAsync(app, "editSet", new JsonObject { ["text"] = "ABC" });
        await CommitAsync(app);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0, 0, 0, 0, 0, 0 }, await app.BytesAsync(0, 9));
        await app.SendAsync("focus", new JsonObject { ["target"] = "hexView" });
        await app.KeyAsync("Z", ctrl: true);
        Assert.Equal(new byte[9], await app.BytesAsync(0, 9));

        await WaitTemplatesAsync(app);
        await EditAsync(app, "name");
        JsonObject bad = await TemplateAsync(app, "editSet", new JsonObject { ["text"] = "ABCDEFGHI" });
        Assert.False(bad["canCommit"]!.GetValue<bool>());
        Assert.Contains("8", bad["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False((await CommitAsync(app))["committed"]!.GetValue<bool>());
        await TemplateAsync(app, "editKey", new JsonObject { ["key"] = "Escape" });
        Assert.Equal(new byte[9], await app.BytesAsync(0, 9));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-14-02")]
    public Task Read_only_conversion_edits_the_raw_value() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenWithAsync(ctx, [0xEB, 0x00], "le s16 temp [[read(fn(v) => v / 10.0)]];");
        JsonObject state = await EditAsync(app, "temp");
        Assert.True(state["raw"]!.GetValue<bool>());
        Assert.Equal("235", state["initial"]!.GetValue<string>());
        await TemplateAsync(app, "editSet", new JsonObject { ["text"] = "100" });
        await CommitAsync(app);
        Assert.Equal(new byte[] { 0x64, 0x00 }, await app.BytesAsync(0, 2));
        await app.WaitUntilAsync(async () => (await RowAsync(app, "temp"))["value"]!.GetValue<string>() == "10", Wait, "the converted value");
    });

    // ───────────── セクション ─────────────

    private static async Task<string> PathContainingAsync(AppSession app, string part)
    {
        string? path = null;
        await app.WaitUntilAsync(async () =>
        {
            await TemplateAsync(app, "filter", new JsonObject { ["text"] = part.Split('.')[^1], ["all"] = true });
            JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
            path = state["rows"]!.AsArray().Select(r => r!["path"]!.GetValue<string>()).FirstOrDefault(p => p.Contains(part, StringComparison.Ordinal));
            return path is not null;
        }, Wait, "the path containing " + part);
        await TemplateAsync(app, "filter", new JsonObject { ["text"] = string.Empty });
        return path!;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-15-04")]
    public Task Patterns_inside_sections_cannot_be_edited() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-TPL-PNG-IDAT")], ExtraArgs = ["--template", Fixture("png-section.hxt")] });
        await WaitTemplatesAsync(app);
        string path = await PathContainingAsync(app, "rows[1].filter");
        JsonObject edit = await TemplateAsync(app, "edit", new JsonObject { ["path"] = path });
        Assert.False(edit["open"]!.GetValue<bool>());
        JsonObject menu = await TemplateAsync(app, "menu", new JsonObject { ["path"] = path });
        JsonObject item = menu["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "TplMenu_EditValue")!.AsObject();
        Assert.False(item["enabled"]!.GetValue<bool>());
        Assert.Contains("section", item["tooltip"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-15-02")]
    public Task Opening_a_section_in_a_new_tab_is_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-TPL-PNG-IDAT")], ExtraArgs = ["--template", Fixture("png-section.hxt")] });
        await WaitTemplatesAsync(app);
        string path = await PathContainingAsync(app, "rows[0].filter");
        string section = path[..path.IndexOf(".rows", StringComparison.Ordinal)];
        await TemplateAsync(app, "menuInvoke", new JsonObject { ["path"] = section, ["id"] = "TplMenu_OpenSection" });
        await app.WaitForTabsAsync(2);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(784, doc["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0 }, await app.BytesAsync(0, 1));
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        Assert.Equal(new byte[] { 0 }, await app.BytesAsync(0, 1));
    });

    // ───────────── 表示 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-02-03")]
    public Task Doc_comments_appear_in_the_tooltip() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "/// 先頭のマジック\nu32 magic;\n// 普通のコメント\nu8 next;\n");
        Assert.Contains("先頭のマジック", (await RowAsync(app, "magic"))["tooltip"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("普通のコメント", (await RowAsync(app, "next"))["tooltip"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-03-02")]
    public Task Filetime_shows_a_date_and_both_times_in_the_tooltip() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.WriteFile("ft.bin", [0x00, 0x80, 0x3E, 0xD5, 0xDE, 0xB1, 0x9D, 0x01]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file], Hooks = new JsonObject { ["timeZone"] = "Tokyo Standard Time" } });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "filetime t;");
        JsonObject row = await RowAsync(app, "t");
        Assert.Contains("1970", row["value"]!.GetValue<string>(), StringComparison.Ordinal);
        string tip = row["tooltip"]!.GetValue<string>();
        Assert.Contains("1970-01-01 00:00:00", tip, StringComparison.Ordinal);

        // 地域の時刻 (テスト用のタイムゾーン UTC+9)
        Assert.Contains("1970-01-01 09:00:00", tip, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-03-04")]
    public Task Rgb565_shows_a_color_swatch() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenWithAsync(ctx, [0x00, 0xF8], "le rgb565 c;");
        JsonObject row = await RowAsync(app, "c");
        Assert.Equal("#FF0000", row["value"]!.GetValue<string>());
        Assert.Equal("#FF0000", row["valueSwatch"]!.GetValue<string>());
        var tree = await app.WaitForAsync("Tpl_Tree");
        FlaUI.Core.AutomationElements.AutomationElement? swatch = null;
        await app.WaitUntilAsync(() => Task.FromResult((swatch = tree.FindFirstDescendant(cf => cf.ByAutomationId("Tpl_ValueSwatch"))) is not null), Wait, "the swatch");
        Assert.Equal("#FF0000", AppSession.NameOf(swatch!));
        Assert.True(swatch!.BoundingRectangle.Width > 0);

        // 描画されている色見本の文字の色 (前面に出さないテストでは画面の取得に XAML の描画が載らないので、要素の色を読む)
        JsonObject drawn = (await TemplateAsync(app, "swatches"))["swatches"]!.AsArray().Single()!.AsObject();
        Assert.Equal("#FF0000", drawn["color"]!.GetValue<string>());
        Assert.True(drawn["width"]!.GetValue<double>() > 0);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-05-02")]
    public Task Undefined_enum_values_get_a_warning_icon_but_no_warning() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenWithAsync(ctx, [0x34, 0x12], "import std.pe;\nle std::pe::Machine m;");
        JsonObject row = await RowAsync(app, "m");
        Assert.Equal("(0x1234)", row["value"]!.GetValue<string>());
        Assert.Equal("", row["glyph"]!.GetValue<string>());
        Assert.Contains("Undefined value", row["tooltip"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject state = await TemplateAsync(app, "state");
        Assert.Equal(0, state["applications"]![0]!["warnings"]!.GetValue<int>());
        Assert.Contains("0 warning", state["counts"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-06-02")]
    public Task Bitfield_members_show_bit_offsets_and_highlight_the_whole_byte() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "u8 pad[16]; bitfield B { a : 3; b : 5; }; B bf;");
        JsonObject row = await RowAsync(app, "bf.b");
        Assert.Equal("0x10.3", row["offset"]!.GetValue<string>());
        Assert.Equal("5 bit", row["size"]!.GetValue<string>());
        Assert.StartsWith("2", row["value"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Bits 3–7", row["tooltip"]!.GetValue<string>(), StringComparison.Ordinal);
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "bf.b" });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 1, Wait, "the byte selection");
        Assert.Equal(0x10, (await app.DocumentAsync())["selectionStart"]!.GetValue<long>());
        string json = Path.Combine(ctx.Root, "bits.json");
        await TemplateAsync(app, "export", new JsonObject { ["file"] = json });
        JsonNode node = Find(JsonNode.Parse(File.ReadAllText(json))!, "bf.b")!;
        Assert.Equal(3, (int)node["bit_offset"]!);
        Assert.Equal(5, (int)node["bit_size"]!);
    });

    internal static JsonNode? Find(JsonNode root, string path)
    {
        if ((string?)root["path"] == path)
        {
            return root;
        }

        foreach (JsonNode? c in root["children"]?.AsArray() ?? [])
        {
            if (Find(c!, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ───────────── 色分けと連動 (TPL-23)・コントラスト (TPL-13) ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-23-03")]
    public Task High_contrast_draws_boundary_lines_instead_of_fills() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", "PNG image"] });
        await WaitTemplatesAsync(app);
        await app.SendAsync("forceHighContrast", new JsonObject { ["on"] = true });
        JsonArray template = await TemplateHighlightsAsync(app);
        Assert.All(template, h => Assert.Null(h!["background"]));
        Assert.Contains(template, h => h!["first"]!.GetValue<long>() == 0 && h["last"]!.GetValue<long>() == 7 && h["border"] is not null);
        Assert.Contains(template, h => h!["first"]!.GetValue<long>() == 8 && h["last"]!.GetValue<long>() == 0xB);
        Assert.Contains(template, h => h!["first"]!.GetValue<long>() == 0xC && h["last"]!.GetValue<long>() == 0xF);
    });

    internal static async Task<JsonArray> TemplateHighlightsAsync(AppSession app)
    {
        JsonArray template = [];
        await app.WaitUntilAsync(async () =>
        {
            await app.RenderAsync();
            template = new JsonArray([.. (await app.SendAsync("highlights"))["segments"]!.AsArray()
                .Where(h => h!["layer"]!.GetValue<int>() == 9 && h["column"]!.GetValue<string>() == "hex").Select(h => h!.DeepClone())]);
            return template.Count > 0;
        }, Wait, "the template highlights");
        return template;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-13-03")]
    public Task Theme_colors_keep_text_readable_and_high_contrast_uses_lines() => UiTestContext.RunAsync(async ctx =>
    {
        // 1. テーマのリソースの値からコントラスト比を計算する (ライト: 背景 #F3F3F3・文字 #1B1B1B、ダーク: 背景 #202020・文字 #FFFFFF)
        string xaml = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "Themes", "AnnotationPalette.xaml"));
        foreach ((string theme, uint background, uint text) in new[] { ("Light", 0xF3F3F3u, 0x1B1B1Bu), ("Dark", 0x202020u, 0xFFFFFFu) })
        {
            string section = xaml[xaml.IndexOf($"x:Key=\"{theme}\"", StringComparison.Ordinal)..];
            section = section[..section.IndexOf("</ResourceDictionary>", StringComparison.Ordinal)];
            var colors = System.Text.RegularExpressions.Regex.Matches(section, "<Color x:Key=\"Template(\\w+)Color\">#FF([0-9A-F]{6})</Color>");
            Assert.Equal(21, colors.Count);
            foreach (System.Text.RegularExpressions.Match m in colors)
            {
                uint rgb = Convert.ToUInt32(m.Groups[2].Value, 16);
                double ratio = Contrast(Blend(rgb, background, 0.35), text);
                Assert.True(ratio >= 4.5, $"{theme} {m.Groups[1].Value}: {ratio:F2}");
            }
        }

        // 2. ハイコントラストでは 0x0 のセルに背景の塗りがなく、境目の線がある
        string data = ctx.TestData("TD-TEXT-ASCII");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [data], ExtraArgs = ["--template", Fixture("color3.hxt")] });
        await WaitTemplatesAsync(app);
        await app.SendAsync("forceHighContrast", new JsonObject { ["on"] = true });
        JsonArray template = await TemplateHighlightsAsync(app);
        Assert.All(template, h => Assert.Null(h!["background"]));
        Assert.Contains(template, h => h!["first"]!.GetValue<long>() == 0 && h["border"] is not null);
    });

    private static uint Blend(uint fg, uint bg, double a)
    {
        uint Mix(int shift) => (uint)Math.Round((((fg >> shift) & 0xFF) * a) + (((bg >> shift) & 0xFF) * (1 - a)));
        return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    private static double Contrast(uint a, uint b)
    {
        static double C(uint c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double L(uint rgb) => (0.2126 * C((rgb >> 16) & 0xFF)) + (0.7152 * C((rgb >> 8) & 0xFF)) + (0.0722 * C(rgb & 0xFF));
        double l1 = L(a);
        double l2 = L(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-23-04")]
    public Task Cursor_and_tree_selection_follow_each_other() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", "PNG image"] });
        await WaitTemplatesAsync(app);
        await app.GoToAsync(0x13);
        await app.WaitUntilAsync(async () => (await TemplateAsync(app, "state"))["selectedPath"]?.GetValue<string>() == "chunks[0].header.width", Wait, "the width node");
        Assert.True(Row(await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 }), "chunks[0].header")["expanded"]!.GetValue<bool>());
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "chunks[1]" });
        byte[] png = File.ReadAllBytes(ctx.TestData("TD-PNG"));
        long length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(0x21));
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionStart"]!.GetValue<long>() == 0x21, Wait, "the chunk selection");
        await ViewOps.AssertSelectionAsync(app, 0x21, length + 12);
    });

    // ───────────── 右クリックメニュー (TPL-24) ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-24-01")]
    public Task Pointer_target_jump_is_recorded_in_the_history() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "struct T { u8 v; }; u8 skip[16]; T *p : u8;");
        await app.GoToAsync(0);
        await TemplateAsync(app, "menuInvoke", new JsonObject { ["path"] = "p", ["id"] = "TplMenu_GoToTarget" });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0x10, Wait, "the target");
        await ViewOps.AssertSelectionAsync(app, 0x10, 1);
        await app.SendAsync("focus", new JsonObject { ["target"] = "hexView" });
        await app.KeyAsync("Left", alt: true);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == 0, Wait, "back to 0");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-24-03")]
    public Task Copy_path_and_add_bookmark() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-TPL-PNG-4CHUNKS")], ExtraArgs = ["--template", Fixture("png-example.hxt")] });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "menuInvoke", new JsonObject { ["path"] = "chunks[3].header.width", ["id"] = "TplMenu_CopyPath" });
        Assert.Equal("chunks[3].header.width", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>());
        await TemplateAsync(app, "menuInvoke", new JsonObject { ["path"] = "chunks[3].header.width", ["id"] = "TplMenu_AddBookmark" });
        JsonArray bookmarks = (await app.SendAsync("bookmarks"))["bookmarks"]!.AsArray();
        JsonObject last = bookmarks.Last()!.AsObject();
        JsonObject row = await RowAsync(app, "chunks[3].header.width");
        Assert.Equal(Convert.ToInt64(row["offset"]!.GetValue<string>(), 16), last["start"]!.GetValue<long>());
        Assert.Equal(4, last["length"]!.GetValue<long>());
        Assert.Contains("width", last["comment"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("chunks[3].header.width", last["comment"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-26-01")]
    public Task Follow_and_fixed_applications_and_convert_to_fixed() => UiTestContext.RunAsync(async ctx =>
    {
        string pe = ctx.TestData("TD-PE-X64");
        byte[] bytes = File.ReadAllBytes(pe);
        int lfanew = BitConverter.ToInt32(bytes, 0x3C);
        int optional = BitConverter.ToUInt16(bytes, lfanew + 20);
        long s0 = lfanew + 24 + optional;
        long s1 = s0 + 40;
        string Name(long at) => Encoding.ASCII.GetString(bytes, (int)at, 8).TrimEnd('\0');
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [pe] });
        await TemplateAsync(app, "removeAll");
        await TemplateAsync(app, "apply", new JsonObject { ["template"] = "pe" });
        await TemplateAsync(app, "apply", new JsonObject { ["template"] = "pe", ["follow"] = true, ["type"] = "std::pe::SectionHeader", ["offset"] = s0 });
        await WaitTemplatesAsync(app, 2);

        // 選択せずに (カーソルを動かさずに) 行の値を読む
        async Task<string> PeekAsync(string path, int index)
        {
            await TemplateAsync(app, "reveal", new JsonObject { ["path"] = path, ["app"] = index, ["select"] = false });
            JsonArray rows = (await TemplateAsync(app, "state", new JsonObject { ["rows"] = 3000 }))["rows"]!.AsArray();
            return rows.FirstOrDefault(r => r!["path"]!.GetValue<string>() == path)?["value"]?.GetValue<string>() ?? string.Empty;
        }

        Task<string> FollowNameAsync() => PeekAsync("SectionHeader.Name", 1);

        await app.GoToAsync(s0);
        await app.WaitUntilAsync(async () => (await FollowNameAsync()).Contains(Name(s0), StringComparison.Ordinal), Wait, "the first section name");
        string fixedName = await PeekAsync("sections[0].Name", 0);
        await app.GoToAsync(s1);
        await app.WaitUntilAsync(async () => (await FollowNameAsync()).Contains(Name(s1), StringComparison.Ordinal), Wait, "the second section name");
        Assert.Equal(fixedName, await PeekAsync("sections[0].Name", 0));

        await TemplateAsync(app, "reveal", new JsonObject { ["path"] = "@app", ["app"] = 1, ["select"] = false });
        JsonObject s = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 3000 });
        Assert.Equal("Follow", s["rows"]!.AsArray().Where(r => r!["kind"]!.GetValue<string>() == "Application").ElementAt(1)!["status"]!.GetValue<string>());
        JsonArray focus = (await app.SendAsync("highlights"))["segments"]!.AsArray();
        Assert.Contains(focus, h => h!["tag"]!.GetValue<string>() == "templateFollow" && h["background"] is null);

        await TemplateAsync(app, "menuInvoke", new JsonObject { ["path"] = "@app", ["app"] = 1, ["id"] = "TplMenu_ToFixed" });
        await app.WaitUntilAsync(async () => (await TemplateAsync(app, "state"))["applications"]!.AsArray().Count == 3, Wait, "the new fixed application");
        JsonObject added = (await TemplateAsync(app, "state"))["applications"]!.AsArray().Last()!.AsObject();
        Assert.Equal(s1, added["offset"]!.GetValue<long>());
        Assert.Equal("Fixed", added["kind"]!.GetValue<string>());

        // 4: カーソルを 0x0 に移しても、新しい固定の適用は 2 つ目のセクションの名前のまま
        await WaitTemplatesAsync(app, 3);
        await app.GoToAsync(0);
        await app.WaitUntilAsync(async () => (await TemplateAsync(app, "state"))["applications"]![1]!["followOffset"]?.GetValue<long>() == 0
            && (await TemplateAsync(app, "state"))["applications"]![1]!["completed"]!.GetValue<bool>(), Wait, "the follow application at 0x0");
        Assert.Contains(Name(s1), await PeekAsync("SectionHeader.Name", 2), StringComparison.Ordinal);
    });
}
