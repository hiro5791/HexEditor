using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Inspector.InspectorDecoderTests;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>行の構成 (INSP-19)、パネルの解釈 (INSP-01)、コメントの Markdown (INSP-24)。</summary>
public sealed class InspectorLayoutTests
{
    [Fact]
    public void Basic_preset_shows_the_default_rows()
    {
        InspectorLayout layout = InspectorLayout.Default;
        var visible = layout.VisibleGroups().SelectMany(g => g.Rows.Select(r => r.TypeId)).ToList();
        Assert.Equal(
            [
                "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double", "ansi", "utf8", "utf16",
                "unix32", "filetime", "dosdatetime", "binary8", "guid",
            ],
            visible);
        Assert.Equal(InspectorTypes.All.Count, InspectorLayout.FromPreset(InspectorPreset.All).VisibleGroups().Sum(g => g.Rows.Count));
        Assert.All(InspectorLayout.FromPreset(InspectorPreset.DateTime).VisibleGroups(), g => Assert.Equal(InspectorGroup.DateTime, g.Group));
    }

    [Fact]
    public void Hide_show_and_reorder_survive_serialization()
    {
        InspectorLayout layout = InspectorLayout.Default
            .WithVisible("int16", false)
            .MoveRow("uint32", -3)
            .MoveGroup(InspectorGroup.Float, -1)
            .WithOpposite("int32", true);
        var integers = layout.VisibleGroups().First(g => g.Group == InspectorGroup.Integer).Rows.Select(r => r.TypeId).ToList();
        // 行の設定には非表示の行も並ぶので、非表示の int16 を含めて 3 つ上へ動く。
        Assert.Equal(["int8", "uint8", "uint32", "uint16", "int32", "int64", "uint64"], integers);
        Assert.Equal(InspectorGroup.Float, layout.Groups[0]);

        InspectorLayout parsed = InspectorLayout.Parse(layout.Serialize());
        Assert.Equal(layout, parsed);
        Assert.True(parsed.Row("int32")!.Opposite);

        // 非表示にした行を表示し直すと元の位置に戻る (順序は非表示の行も保つ)。
        var restored = parsed.WithVisible("int16", true).VisibleGroups().First(g => g.Group == InspectorGroup.Integer).Rows.Select(r => r.TypeId).ToList();
        Assert.Equal(["int8", "uint8", "uint32", "int16", "uint16", "int32", "int64", "uint64"], restored);

        Assert.Equal(InspectorLayout.Default, InspectorLayout.Parse("not json"));
        Assert.Equal(InspectorLayout.Default, InspectorLayout.Parse(null));
    }

    [Fact]
    public void Evaluate_reads_from_the_snapshot_and_adds_opposite_rows()
    {
        byte[] data = new byte[0x100];
        data[0] = 0x39;
        data[1] = 0x30;
        using var doc = new Document(new MemoryByteSource(data), Support.DocumentAssert.Options());
        InspectorLayout layout = InspectorLayout.Default.WithOpposite("int32", true);
        Support.DocumentAssert.ReadForDisplayWhenLoaded(doc.Current, 0, 0x100);
        IReadOnlyList<InspectorGroupResult> result = DataInspector.Evaluate(doc.Current, 0, layout, Endianness.Little, Opts());
        InspectorRowResult[] int32 = [.. result.SelectMany(g => g.Rows).Where(r => r.TypeId == "int32")];
        Assert.Equal("12,345", int32[0].Value.Text);
        Assert.True(int32[1].Opposite);
        Assert.Equal("959,447,040", int32[1].Value.Text);

        // 末尾の 2 バイト手前では int32 が「—」。
        var atEnd = DataInspector.Evaluate(doc.Current, 0xFE, layout, Endianness.Little, Opts()).SelectMany(g => g.Rows).ToList();
        Assert.Equal("—", atEnd.First(r => r.TypeId == "int32").Value.Text);
        Assert.Equal("0", atEnd.First(r => r.TypeId == "int16").Value.Text);
    }

    [Fact]
    public void Markdown_subset()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownLite.Parse("# Table\n**太字** and *em* `code`\n<b>html</b>\n- item\n1. one\n\n| a | b |\n|---|---|\n| 1 | 2 |\n```\nraw *x*\n```");
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        MarkdownBlock paragraph = blocks[1];
        Assert.Equal(MarkdownInlineKind.Strong, paragraph.Inlines[0].Kind);
        Assert.Equal("太字", paragraph.Inlines[0].PlainText);
        Assert.Contains(paragraph.Inlines, i => i.Kind == MarkdownInlineKind.Emphasis && i.PlainText == "em");
        Assert.Contains(paragraph.Inlines, i => i.Kind == MarkdownInlineKind.Code && i.Text == "code");

        // HTML は文字列のまま。
        Assert.Contains("<b>html</b>", string.Concat(paragraph.Inlines.Select(i => i.PlainText)));
        Assert.Equal(MarkdownBlockKind.BulletItem, blocks[2].Kind);
        Assert.Equal(MarkdownBlockKind.NumberedItem, blocks[3].Kind);
        Assert.Equal(MarkdownBlockKind.Table, blocks[4].Kind);
        Assert.Equal(2, blocks[4].Rows!.Count);
        Assert.Equal("raw *x*", blocks[5].Code);

        MarkdownInline link = MarkdownLite.ParseInlines("[ヘッダ](#0x100) ![img](a.png)")[0];
        Assert.Equal(("ヘッダ", "#0x100"), (link.PlainText, link.Url));
        Assert.Equal(MarkdownLite.LinkKind.Document, MarkdownLite.Classify(link.Url));
        Assert.Equal(MarkdownLite.LinkKind.Web, MarkdownLite.Classify("https://example.com"));
        Assert.Equal(MarkdownLite.LinkKind.None, MarkdownLite.Classify("file:///C:/Windows/notepad.exe"));
        Assert.DoesNotContain(MarkdownLite.ParseInlines("![img](a.png)"), i => i.Kind == MarkdownInlineKind.Link);
        Assert.Equal("bm_name", MarkdownLite.ParseInlines("bm_name")[0].PlainText);
    }
}
