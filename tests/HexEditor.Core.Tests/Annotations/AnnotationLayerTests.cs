using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Annotations;

/// <summary>共通の注釈レイヤー (INSP-32)。</summary>
public sealed class AnnotationLayerTests
{
    /// <summary>TD-INSP-SCRIPT-ANNOT3 と同じ 3 つの注釈 (出どころ「スクリプト」)。</summary>
    private static AnnotationSet Three()
    {
        var set = new AnnotationSet("script:annot3", AnnotationOrigin.Script, "annot3.js");
        set.AddRange(
        [
            new Annotation(0x00, 0x40, "outer", null, "outer desc"),
            new Annotation(0x08, 0x18, "middle", null, "middle desc"),
            new Annotation(0x10, 1, "inner", null, "inner desc"),
        ]);
        return set;
    }

    [Fact]
    public void Overlapping_annotations_are_nested_with_the_inner_one_on_top()
    {
        var layer = new AnnotationLayer(new AnnotationDisplay());
        layer.Register(Three());
        IReadOnlyList<PlacedAnnotation> placed = layer.QueryVisible(0x10, 0x11);
        Assert.Equal(["outer", "middle", "inner"], placed.Select(p => p.Annotation.Label));
        Assert.Equal([0, 1, 2], placed.Select(p => p.Level));

        // ツールチップ (そのバイトを含むすべての注釈。内側の範囲ほど先)。
        Assert.Equal(["inner", "middle", "outer"], layer.At(0x10).Select(a => a.Annotation.Label));

        // 注釈の列: 行 0 で始まるのは outer (と middle)、行 1 は inner。
        Assert.Equal(new AnnotationRowLabel("outer", 1, layer.Sources[0]), layer.RowLabel(0x00, 0x10));
        Assert.Equal("inner", layer.RowLabel(0x10, 0x20)!.Value.Label);
        Assert.Null(layer.RowLabel(0x40, 0x50));
    }

    [Fact]
    public void Only_four_levels_are_drawn_but_the_tooltip_has_all()
    {
        var layer = new AnnotationLayer(new AnnotationDisplay());
        var set = new AnnotationSet("s", AnnotationOrigin.Script);
        for (int i = 0; i < 6; i++)
        {
            set.Add(new Annotation(i, 20 - 2 * i, "a" + i));
        }

        layer.Register(set);
        Assert.Equal(AnnotationLayer.MaxLevels, layer.QueryVisible(8, 9).Count);
        Assert.Equal(6, layer.At(8).Count);
    }

    [Fact]
    public void Hiding_an_origin_hides_only_its_annotations()
    {
        var display = new AnnotationDisplay();
        var layer = new AnnotationLayer(display);
        var yara = new AnnotationSet("yara", AnnotationOrigin.Yara, "rules.yar");
        yara.Add(new Annotation(0, 4, "zip_local_header"));
        layer.Register(yara);
        layer.Register(Three());
        int changes = 0;
        layer.Changed += (_, _) => changes++;
        display.SetVisible(AnnotationOrigin.Yara, false);
        Assert.Equal(1, changes);
        Assert.DoesNotContain(layer.At(1), a => a.Source.Origin == AnnotationOrigin.Yara);
        Assert.Contains(layer.At(1), a => a.Source.Origin == AnnotationOrigin.Script);
        Assert.Equal(AnnotationStyle.Border, display.StyleOf(AnnotationOrigin.Yara));
        Assert.Equal(AnnotationStyle.Background, display.StyleOf(AnnotationOrigin.Bookmark));

        // 出どころの機能が結果を閉じたら注釈も消える (INSP-32 の仕様 3)。
        Assert.True(layer.Unregister("yara"));
        Assert.Single(layer.Sources);
    }

    [Fact]
    public void Bookmarks_are_an_annotation_source()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x1000]), Options());
        BookmarkCollection bookmarks = BookmarkCollection.Attach(doc);
        Bookmark b = bookmarks.Add(0x100, 16, "hdr");
        bookmarks.SetComment(b, "# Title");
        AnnotationLayer layer = AnnotationLayer.For(doc);
        Assert.Same(layer, AnnotationLayer.For(doc));
        layer.Register(new BookmarkAnnotationSource(bookmarks));
        (Annotation a, IAnnotationSource s) = Assert.Single(layer.At(0x104));
        Assert.Equal(("hdr", "# Title", AnnotationOrigin.Bookmark), (a.Label, a.Description, s.Origin));
        bookmarks.SetGroup(b, "G");
        bookmarks.SetGroupVisible(bookmarks.FindGroup("G")!, false);
        Assert.Empty(layer.At(0x104));
    }

    [Fact]
    public void A_million_annotations_are_queried_quickly()
    {
        // 表示範囲の取り出しは件数によらない (INSP-32 の「巨大ファイル・長時間処理」: 1,000 万件で 1 フレームあたり 2 ms 以内)。
        var set = new AnnotationSet("many", AnnotationOrigin.Script);
        set.AddRange(Enumerable.Range(0, 1_000_000).Select(i => new Annotation(i * 16L, 8, string.Empty)));
        var output = new List<Annotation>();
        set.Query(500_000L * 16, 500_000L * 16 + 4096, output);
        Assert.Equal(256, output.Count);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            output.Clear();
            set.Query(i * 100_000L, i * 100_000L + 4096, output);
        }

        Assert.True(watch.Elapsed.TotalMilliseconds / 100 < 2 * 10, $"{watch.Elapsed.TotalMilliseconds / 100} ms");
    }

    [Fact]
    public void Style_settings_set_the_drawing_style_per_origin()
    {
        foreach (AnnotationOrigin origin in Enum.GetValues<AnnotationOrigin>())
        {
            // 設定の定義があり、既定値は既定の描き方 (ブックマークとテンプレートが背景色、それ以外は枠線)。
            string key = AnnotationDisplay.StyleSettingKey(origin);
            Assert.Equal(AnnotationDisplay.DefaultStyle(origin), AnnotationDisplay.ParseStyle(Core.Settings.BuiltInSettings.DefaultOf(key)?.GetValue<string>()));
        }

        Assert.Equal("annotations.style.searchResults", AnnotationDisplay.StyleSettingKey(AnnotationOrigin.SearchResults));
        var display = new AnnotationDisplay();
        int changes = 0;
        display.Changed += (_, _) => changes++;
        var values = new Dictionary<string, string>
        {
            ["annotations.style.yara"] = "underline",
            ["annotations.style.bookmark"] = "border",
            ["annotations.style.template"] = "nonsense",
        };
        display.ApplyStyleSettings(values.GetValueOrDefault);
        Assert.Equal(AnnotationStyle.Underline, display.StyleOf(AnnotationOrigin.Yara));
        Assert.Equal(AnnotationStyle.Border, display.StyleOf(AnnotationOrigin.Bookmark));
        Assert.Equal(AnnotationStyle.Background, display.StyleOf(AnnotationOrigin.Template));
        Assert.Equal(AnnotationStyle.Border, display.StyleOf(AnnotationOrigin.Analysis));
        Assert.Equal(2, changes);

        // 設定を消すと既定の描き方に戻る。
        display.ApplyStyleSettings(_ => null);
        Assert.Equal(AnnotationStyle.Border, display.StyleOf(AnnotationOrigin.Yara));
        Assert.Equal(AnnotationStyle.Background, display.StyleOf(AnnotationOrigin.Bookmark));
    }

    [Fact]
    public void Find_all_results_are_queried_only_for_the_visible_range_and_follow_the_origin_toggle()
    {
        // 1 TB のドキュメントの 16 バイトごとの一致 (実際の件数は 600 億を超える): 注釈の配列は作らず、表示範囲だけを問い合わせる。
        var asked = new List<(long, long)>();
        IEnumerable<(long, long)> Matches(long start, long end)
        {
            asked.Add((start, end));
            for (long at = (start + 15) / 16 * 16; at < end; at += 16)
            {
                yield return (at, 4);
            }
        }

        var display = new AnnotationDisplay();
        var layer = new AnnotationLayer(display);
        var source = new RangeAnnotationSource("searchResults", AnnotationOrigin.SearchResults, Matches, () => "DE AD BE EF");
        layer.Register(source);
        IReadOnlyList<PlacedAnnotation> placed = layer.QueryVisible(1L << 39, (1L << 39) + 64);
        Assert.Equal(4, placed.Count);
        Assert.All(placed, p => Assert.Equal("DE AD BE EF", p.Annotation.Label));
        Assert.Equal([(1L << 39, (1L << 39) + 64)], asked);
        Assert.Equal(AnnotationStyle.Border, display.StyleOf(AnnotationOrigin.SearchResults));

        // 表示 > 注釈 > すべて検索の結果 をオフにすると、問い合わせもしない。
        int changes = 0;
        layer.Changed += (_, _) => changes++;
        display.SetVisible(AnnotationOrigin.SearchResults, false);
        asked.Clear();
        Assert.Empty(layer.QueryVisible(0, 64));
        Assert.Empty(layer.At(0));
        Assert.Empty(asked);
        source.RaiseChanged();
        Assert.Equal(2, changes);
    }
}
