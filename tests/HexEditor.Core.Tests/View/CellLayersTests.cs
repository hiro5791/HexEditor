using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>色の重ね順 (VIEW-17 の仕様 5〜7) とバイトテーマ (VIEW-17 の仕様 2〜4・9)。</summary>
public sealed class CellLayersTests
{
    private static SchemeColor C(int layer, int which) => new(0xFF, (byte)layer, (byte)which, 0x40);

    /// <summary>層ごとに、背景・文字色・枠を指定するかを決めた組み合わせ。</summary>
    private static LayerPaint Paint(CellLayer layer, bool background, bool foreground, bool frame) => new(layer,
        background ? C((int)layer, 1) : null,
        foreground ? C((int)layer, 2) : null,
        layer == CellLayer.Modified ? CellDecorationKind.Underline : frame ? CellDecorationKind.Frame : null,
        frame ? C((int)layer, 3) : null);

    private static Arbitrary<(int Mask, int Seed)> Combinations() =>
        (from mask in Gen.Choose(0, (1 << 17) - 1)
         from seed in Gen.Choose(0, int.MaxValue)
         select (mask, seed)).ToArbitrary();

    [Property(MaxTest = 10_000)]
    [Trait(TC, "TC-VIEW-17-03")]
    public Property Front_layer_wins_and_the_change_underline_is_always_drawn() =>
        // 1. 層 1〜17 の任意の組み合わせ (無作為に 10,000 通り)。
        Prop.ForAll(Combinations(), pair =>
        {
            (int mask, int seed) = pair;
            var random = new Random(seed);
            var paints = new List<LayerPaint>();
            for (int i = 0; i < 17; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    paints.Add(Paint((CellLayer)(i + 1), random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0));
                }
            }

            CellAppearance result = CellLayers.Resolve(paints);
            LayerPaint? back = paints.Where(p => p.Background is not null).OrderBy(p => p.Layer).Cast<LayerPaint?>().FirstOrDefault();
            LayerPaint? fore = paints.Where(p => p.Foreground is not null).OrderBy(p => p.Layer).Cast<LayerPaint?>().FirstOrDefault();
            bool ok = result.Background == back?.Background && result.BackgroundLayer == back?.Layer
                && result.Foreground == fore?.Foreground && result.ForegroundLayer == fore?.Layer;
            bool modified = paints.Any(p => p.Layer == CellLayer.Modified);
            ok &= modified == result.Decorations.Any(d => d.Kind == CellDecorationKind.Underline);

            // 枠は同じ種類なら手前の層のものだけ。
            LayerPaint? frontFrame = paints.Where(p => p.Decoration == CellDecorationKind.Frame).OrderBy(p => p.Layer).Cast<LayerPaint?>().FirstOrDefault();
            CellDecoration[] frames = [.. result.Decorations.Where(d => d.Kind == CellDecorationKind.Frame)];
            ok &= frontFrame is null ? frames.Length == 0 : frames.Length == 1 && frames[0].Layer == frontFrame.Value.Layer;
            return ok;
        });

    [Fact]
    [Trait(TC, "TC-VIEW-17-03")]
    public void Hidden_bookmark_leaves_a_band_under_the_selection()
    {
        // 2. 層 2 (選択範囲) と層 7 (ブックマーク)。
        SchemeColor selection = C(2, 1);
        SchemeColor bookmark = C(7, 1);
        CellAppearance result = CellLayers.Resolve([new(CellLayer.Selection, selection, C(2, 2)), new(CellLayer.Bookmark, bookmark)]);
        Assert.Equal(selection, result.Background);
        CellDecoration band = Assert.Single(result.Decorations, d => d.Kind == CellDecorationKind.Band);
        Assert.Equal(bookmark, band.Color);
        Assert.Equal(CellLayer.Bookmark, band.Layer);

        // 選択していなければ帯はいらない (背景がそのまま見える)。
        Assert.DoesNotContain(CellLayers.Resolve([new(CellLayer.Bookmark, bookmark)]).Decorations, d => d.Kind == CellDecorationKind.Band);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-17-03")]
    public void Color_rule_frame_and_change_underline_are_both_drawn()
    {
        // 3. 層 6 (変更) と層 10 (色付けルール、枠付き)。
        CellAppearance result = CellLayers.Resolve(
        [
            new(CellLayer.Modified, Foreground: C(6, 2), Decoration: CellDecorationKind.Underline),
            new(CellLayer.ColoringRule, C(10, 1), C(10, 2), CellDecorationKind.Frame, C(10, 3), ValueDependent: true),
        ]);
        Assert.Contains(result.Decorations, d => d.Kind == CellDecorationKind.Frame && d.Layer == CellLayer.ColoringRule);
        Assert.Contains(result.Decorations, d => d.Kind == CellDecorationKind.Underline);
        Assert.Equal(C(6, 2), result.Foreground);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-17-03")]
    public void Hatch_is_drawn_over_the_bookmark_background_for_unreadable_bytes()
    {
        // 4. 読み取れない状態と層 7。値で決まる層 (バイトテーマ) は使わない (仕様 7)。
        SchemeColor bookmark = C(7, 1);
        CellAppearance result = CellLayers.Resolve(
            [new(CellLayer.Bookmark, bookmark), new(CellLayer.ByteTheme, Foreground: C(12, 2), ValueDependent: true)], unreadable: true);
        Assert.Equal(bookmark, result.Background);
        Assert.Null(result.Foreground);
        Assert.Equal(CellDecorationKind.Hatch, result.Decorations[0].Kind);
    }

    [Fact]
    public void Category_theme_gives_six_distinct_colors()
    {
        byte[] samples = [0x00, 0xFF, 0x41, 0x09, 0x01, 0x80];
        foreach (bool dark in new[] { false, true })
        {
            SchemeColor?[] colors = [.. samples.Select(b => ByteTheme.Category.ColorOf(b, dark).Text)];
            Assert.Equal(6, colors.Distinct().Count());
            Assert.Equal(ByteTheme.Category.ColorOf(0x21, dark), ByteTheme.Category.ColorOf(0x7E, dark));
            Assert.Equal(ByteTheme.Category.ColorOf(0x80, dark), ByteTheme.Category.ColorOf(0xFE, dark));
        }

        Assert.Equal(ByteCategory.Whitespace, ByteTheme.Classify(0x20));
        Assert.Equal(ByteCategory.Control, ByteTheme.Classify(0x7F));
    }

    [Fact]
    public void Custom_theme_json_is_read_and_errors_name_the_line_and_key()
    {
        const string ok = """{"name": "OK", "light": {"00": "#808080", "FF": "#C00000", "20-7E": "#0050A0", "0A": {"text": "#000000", "background": "#FFE080"}}}""";
        Assert.True(ByteTheme.TryParse(ok, "x", out ByteTheme? theme, out _));
        Assert.Equal("OK", theme!.Name);
        Assert.Equal("#808080", theme.ColorOf(0x00, dark: true).Text.ToString());
        Assert.Equal("#0050A0", theme.ColorOf(0x41, false).Text.ToString());
        Assert.Equal("#FFE080", theme.ColorOf(0x0A, false).Background.ToString());
        Assert.Null(theme.ColorOf(0x80, false).Text);

        string bad = "{\n  \"name\": \"Bad\",\n  \"light\": {\n    \"G0\": \"#808080\"\n  }\n}";
        Assert.False(ByteTheme.TryParse(bad, "x", out _, out ByteThemeError? error));
        Assert.Equal(4, error!.Line);
        Assert.Equal("G0", error.Key);

        // 後に書いたものが優先する。
        Assert.True(ByteTheme.TryParse("""{"light": {"00-FF": "#111111", "41": "#222222"}}""", "x", out ByteTheme? later, out _));
        Assert.Equal("#222222", later!.ColorOf(0x41, false).Text.ToString());
        Assert.Equal("#111111", later.ColorOf(0x42, false).Text.ToString());
    }

    [Fact]
    public void Low_contrast_text_is_replaced_by_the_normal_text_color()
    {
        var white = new SchemeColor(0xFF, 0xFF, 0xFF, 0xFF);
        var normal = new SchemeColor(0xFF, 0x1A, 0x1A, 0x1A);
        Assert.Equal(normal, ByteTheme.ReadableText(new SchemeColor(0xFF, 0xF0, 0xF0, 0xF0), white, normal));
        var blue = new SchemeColor(0xFF, 0x00, 0x50, 0xA0);
        Assert.Equal(blue, ByteTheme.ReadableText(blue, white, normal));
    }

    /// <summary>
    /// VIEW-17 の仕様 9 をすべての層に: ブックマーク・色付けルールなどの背景の上でも、3:1 未満の文字色を置き換え、置き換えた色は 4.5:1 以上になる
    /// (通常の文字色で足りなければ黒か白)。
    /// </summary>
    [Fact]
    public void Low_contrast_text_on_any_layer_background_is_replaced_and_reaches_4_5()
    {
        var white = new SchemeColor(0xFF, 0xFF, 0xFF, 0xFF);
        var normal = new SchemeColor(0xFF, 0x1A, 0x1A, 0x1A);
        var blue = new SchemeColor(0xFF, 0x00, 0x50, 0xA0);

        // 色付けルールの濃い青の背景の上の青い文字: 通常の文字色 (濃い灰色) でも 4.5:1 に届かないので白にする。
        SchemeColor back = CellContrast.Flatten(white, lightTheme: true, top: blue);
        SchemeColor replaced = CellContrast.Replacement(blue, back, normal)!.Value;
        Assert.Equal(white, replaced);
        Assert.True(SchemeColor.ContrastRatio(replaced, back) >= CellContrast.ReplacementContrast);

        // 薄いブックマークの背景 (半透明の黄色) の上の薄い灰色: 通常の文字色にする。
        var yellow = new SchemeColor(0x60, 0xFF, 0xE0, 0x00);
        back = CellContrast.Flatten(white, lightTheme: true, top: yellow);
        Assert.Equal(normal, CellContrast.Replacement(new SchemeColor(0xFF, 0xE8, 0xE8, 0xE8), back, normal));
        Assert.True(SchemeColor.ContrastRatio(normal, back) >= CellContrast.ReplacementContrast);

        // 読める組み合わせは変えない。現在行 (行の下の面) とルールの背景を重ねた色で判断する。
        Assert.Null(CellContrast.Replacement(normal, CellContrast.Flatten(white, true, new SchemeColor(0x10, 0, 0, 0), yellow), normal));

        // 半透明の文字色は背景に重ねてから比べる。
        Assert.NotNull(CellContrast.Replacement(new SchemeColor(0x20, 0, 0, 0), white, normal));

        // ダークテーマで透明な通常の背景は黒の上に置く。
        Assert.Equal(new SchemeColor(0xFF, 0, 0, 0), CellContrast.Flatten(default, lightTheme: false));
    }

    [Property]
    public Property Replacement_always_reaches_the_minimum_contrast() => Prop.ForAll(
        Gen.Choose(0, 0xFFFFFF).Select(v => new SchemeColor(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v)).ToArbitrary(),
        Gen.Choose(0, 0xFFFFFF).Select(v => new SchemeColor(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v)).ToArbitrary(),
        (text, background) =>
        {
            var normal = new SchemeColor(0xFF, 0x1A, 0x1A, 0x1A);
            SchemeColor shown = CellContrast.Replacement(text, background, normal) ?? text;
            double ratio = SchemeColor.ContrastRatio(shown, background);
            return shown == text ? ratio >= CellContrast.MinimumContrast : ratio >= CellContrast.ReplacementContrast || ratio >= Math.Max(
                SchemeColor.ContrastRatio(new SchemeColor(0xFF, 0, 0, 0), background), SchemeColor.ContrastRatio(new SchemeColor(0xFF, 0xFF, 0xFF, 0xFF), background)) - 1e-9;
        });
}
