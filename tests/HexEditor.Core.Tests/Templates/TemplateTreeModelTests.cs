using System.Diagnostics;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>結果ツリーの行のモデル (TPL-21)、開閉状態の復元 (TPL-20 の仕様 5)、色分け (TPL-23)。</summary>
public sealed class TemplateTreeModelTests
{
    private static readonly byte[] Png = File.ReadAllBytes(TestDataCatalog.Get("TD-PNG"));

    private static TemplateResult RunStandard(string name, byte[] data)
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        TemplateCatalogEntry entry = catalog.Find(name)!;
        return Run(catalog.Load(entry), TemplateDataSource.FromBytes(data));
    }

    [Fact]
    [Trait(TC, "TC-TPL-21-01")]
    public async Task HundredMillionElementsAreGroupedInThreeLevels()
    {
        var source = new Core.Sources.VirtualByteSource(1L << 30, Core.Sources.VirtualContent.Zero);
        TemplateResult r = Run(Load("struct P { u32 v; }; P a[100000000];"), TemplateDataSource.FromByteSource(source));
        var model = new TemplateTreeModel();
        TemplateTreeItem app = model.AddApplication(r);
        await model.ExpandAsync(app);
        TemplateTreeItem a = app.MaterializedChildren!.Single();
        var sw = Stopwatch.StartNew();
        await model.ExpandAsync(a);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "開くのに時間がかかりすぎます。");
        IReadOnlyList<TemplateTreeItem> top = a.MaterializedChildren!;
        Assert.Equal(100, top.Count);
        Assert.All(top, g => Assert.Equal(TemplateTreeItemKind.Group, g.Kind));
        Assert.Equal("[0 … 999999]", top[0].Data!.Name);
        Assert.Equal("[99000000 … 99999999]", top[^1].Data!.Name);

        await model.ExpandAsync(top[0]);
        IReadOnlyList<TemplateTreeItem> second = top[0].MaterializedChildren!;
        Assert.Equal(1000, second.Count);
        Assert.Equal("[0 … 999]", second[0].Data!.Name);
        Assert.Equal("[999000 … 999999]", second[^1].Data!.Name);

        await model.ExpandAsync(second[1]);
        IReadOnlyList<TemplateTreeItem> elements = second[1].MaterializedChildren!;
        Assert.Equal(1000, elements.Count);
        Assert.Equal(TemplateTreeItemKind.Node, elements[0].Kind);
        Assert.Equal("a[1000]", elements[0].Node!.Path);

        // 行の位置: 適用、a、100 グループ (1 つ目は開いて 1000 グループ、その 2 つ目は開いて 1000 要素)
        Assert.Equal(1 + 1 + 100 + 1000 + 1000, model.RowCount);
        Assert.Same(elements[0], model.ItemAt(1 + 1 + 1 + 2));
        Assert.Equal(1 + 1 + 1 + 2, model.IndexOf(elements[0]));
        model.Collapse(top[0]);
        Assert.Equal(1 + 1 + 100, model.RowCount);
    }

    [Fact]
    [Trait(TC, "TC-TPL-21-03")]
    public async Task PathRevealsTheNodeAndOpensItsParents()
    {
        TemplateResult r = RunStandard("png", Png);
        var model = new TemplateTreeModel();
        TemplateTreeItem app = model.AddApplication(r);
        TemplateTreeItem? width = await model.RevealPathAsync(app, "chunks[0].header.width");
        Assert.NotNull(width);
        Assert.Equal("chunks[0].header.width", width.Node!.Path);
        Assert.True(width.Parent!.IsExpanded);
        Assert.True(width.Parent.Parent!.IsExpanded);
        Assert.True(model.IndexOf(width) > 0);
        Assert.Null(await model.RevealPathAsync(app, "chunks[0].nothing"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-20-03")]
    public async Task ExpandedNodesAreRestoredByPathAfterReevaluation()
    {
        TemplateResult r1 = RunStandard("png", Png);
        var model = new TemplateTreeModel();
        TemplateTreeItem app = model.AddApplication(r1);
        TemplateTreeItem width = (await model.RevealPathAsync(app, "chunks[0].header.width"))!;
        HashSet<string> keys = TemplateTreeModel.ExpandedKeys(app);
        Assert.Contains("chunks", keys);
        Assert.Contains("chunks[0]", keys);
        Assert.Contains("chunks[0].header", keys);

        byte[] edited = (byte[])Png.Clone();
        Array.Copy(new byte[] { 0, 0, 0, 0x20 }, 0, edited, 0x10, 4);
        TemplateResult r2 = RunStandard("png", edited);
        TemplateTreeItem replaced = await model.ReplaceApplicationAsync(app, r2);
        TemplateTreeItem again = (await model.RevealPathAsync(replaced, width.Node!.Path))!;
        Assert.True(again.Parent!.IsExpanded);
        Assert.Equal(TemplateTreeModel.ExpandedKeys(replaced).Order(), keys.Order());
        await model.LoadRowDataAsync([again]);
        Assert.Equal("32", again.Data!.Value);
    }

    [Fact]
    public async Task FilterFindsEvaluatedNodesByNameValueAndType()
    {
        TemplateResult r = RunStandard("png", Png);
        var model = new TemplateTreeModel();
        TemplateTreeItem app = model.AddApplication(r);
        await model.RevealPathAsync(app, "chunks[0].header.width");
        int found = await model.FilterAsync("width");
        Assert.True(found >= 1);
        Assert.True(model.IsFiltered);
        TemplateTreeItem first = model.ItemAt(0);
        await model.LoadRowDataAsync([first]);
        Assert.Equal("chunks[0].header.width", first.Data!.Name);
        await model.FilterAsync(string.Empty);
        Assert.False(model.IsFiltered);
    }

    [Fact]
    [Trait(TC, "TC-TPL-23-01")]
    public void AdjacentPaintedRangesNeverShareAnAutomaticColor()
    {
        AssertNoAdjacentSameColor(RunStandard("png", Png));
        AssertNoAdjacentSameColor(RunStandard("pe", File.ReadAllBytes(TestDataCatalog.Get("TD-PE-X64"))));

        // 無作為な数・長さのフィールドを持つ構造体の配列 (200 個)
        var random = new Random(23);
        for (int n = 0; n < 200; n++)
        {
            int fields = random.Next(1, 9);
            string[] types = ["u8", "u16", "u32", "u64", "u8[3]", "u16[2]"];
            var body = string.Join(" ", Enumerable.Range(0, fields).Select(i =>
            {
                string t = types[random.Next(types.Length)];
                return t.Contains('[') ? $"{t[..t.IndexOf('[')]} f{i}{t[t.IndexOf('[')..]};" : $"{t} f{i};";
            }));
            int count = random.Next(1, 40);
            TemplateResult r = Run(Load($"struct S {{ {body} }}; S items[{count}]; u8 tail;"), TemplateDataSource.FromBytes(new byte[4096]));
            AssertNoAdjacentSameColor(r);
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-23-01")]
    public void DeepestAndLaterPatternsWinOverlaps()
    {
        TemplateResult r = Run("struct H { u8 a; u8 b; }; H h; u8 x @ 0;", new byte[16]);
        IReadOnlyList<TemplatePaintSegment> s = TemplateColoring.Segments(r, 0, 16);

        // 0 は後に宣言した x、1 は h の子 b
        Assert.Equal("x", s.Single(p => p.Start == 0).Node.Path);
        Assert.Equal("h.b", s.Single(p => p.Start == 1).Node.Path);
    }

    private static void AssertNoAdjacentSameColor(TemplateResult r)
    {
        IReadOnlyList<TemplatePaintSegment> segments = TemplateColoring.Segments(r, 0, r.DataLength);
        Assert.NotEmpty(segments);
        for (int i = 1; i < segments.Count; i++)
        {
            TemplatePaintSegment a = segments[i - 1];
            TemplatePaintSegment b = segments[i];
            Assert.True(a.End <= b.Start, "範囲が重なっています。");
            if (a.End == b.Start && !(a.Color.Explicit && b.Color.Explicit))
            {
                Assert.False(a.Color.SameAs(b.Color), $"{a.Node.Path} と {b.Node.Path} が同じ色 ({a.Color.Name}) です。");
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-13-03")]
    public void ContrastHelpersFollowWcag()
    {
        Assert.Equal(21, TemplateColoring.ContrastRatio(0xFFFFFF, 0x000000), 1);
        Assert.Equal(1, TemplateColoring.ContrastRatio(0x123456, 0x123456), 3);
        Assert.Equal(0x808080u, TemplateColoring.Blend(0xFFFFFF, 0x000000, 0.5));
        Assert.Equal(new TemplateColor(TemplateColorKind.Accent, 2, 0, true), TemplateColoring.Parse("accent3"));
        Assert.Equal(TemplateColorKind.Named, TemplateColoring.Parse("teal")!.Value.Kind);
        Assert.Null(TemplateColoring.Parse("colour"));
    }
}
