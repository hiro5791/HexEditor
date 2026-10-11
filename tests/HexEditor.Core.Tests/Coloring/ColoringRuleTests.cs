using System.Text;
using HexEditor.Core.Coloring;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Coloring;

/// <summary>色付けルール (INSP-33) と優先順位 (INSP-34) の評価。</summary>
public sealed class ColoringRuleTests
{
    private static byte[] Bytes256 => [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static (ColoringCell[] Hex, ColoringCell[] Text) Evaluate(ColoringRuleSet set, byte[] data, long start = 0, int? count = null)
    {
        int n = count ?? data.Length;
        var hex = new ColoringCell[n];
        var text = new ColoringCell[n];
        set.Evaluate(data, [], 0, start, hex, text);
        return (hex, text);
    }

    private static ColoringRuleSet Compile(params ColoringRule[] rules) => ColoringRuleSet.Compile(rules, [], null);

    [Fact]
    public void Byte_values_and_presets()
    {
        ColoringRule dim = ColoringPresets.All(id => id)[0] with { Enabled = true };
        (ColoringCell[] hex, _) = Evaluate(Compile(dim), Bytes256);
        Assert.Equal(0, hex[0].Foreground);
        Assert.Equal(-1, hex[1].Foreground);

        ColoringRule control = ColoringPresets.All(id => id)[2] with { Enabled = true };
        (hex, _) = Evaluate(Compile(control), Bytes256);
        Assert.Equal(33, hex.Count(c => c.Foreground == 0));
        Assert.Throws<ColoringRuleException>(() => CompiledColoringRule.ParseByteValues("20-1F"));
        Assert.Throws<ColoringRuleException>(() => CompiledColoringRule.ParseByteValues("GG"));

        // 無効なルールと、色を指定しないルールは適用しない。
        Assert.True(Compile(dim with { Enabled = false }).IsEmpty);
    }

    [Fact]
    [Trait(TC, "TC-INSP-33-02")]
    public void Hex_pattern_marks_each_zip_local_header()
    {
        byte[] data = new byte[2048];
        foreach (int at in new[] { 0, 700, 1500 })
        {
            Array.Copy(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, data, at, 4);
        }

        ColoringRule rule = new() { Kind = ColoringConditionKind.HexPattern, Pattern = "50 4B 03 04", Background = 0xFFD700 };
        (ColoringCell[] hex, ColoringCell[] text) = Evaluate(Compile(rule), data);
        int[] marked = [.. Enumerable.Range(0, data.Length).Where(i => hex[i].Background == 0)];
        Assert.Equal([0, 1, 2, 3, 700, 701, 702, 703, 1500, 1501, 1502, 1503], marked);
        Assert.Equal(marked, Enumerable.Range(0, data.Length).Where(i => text[i].Background == 0));
    }

    [Fact]
    [Trait(TC, "TC-INSP-33-03")]
    public void Patterns_crossing_the_evaluated_range_are_colored()
    {
        // TD-INSP-EDGE: 0xFFC〜0x1003 が DE AD BE EF CA FE BA BE。範囲の境界をまたいでも、範囲の中のバイトすべてに色が付く。
        byte[] data = new byte[0x2000];
        Array.Copy(Convert.FromHexString("DEADBEEFCAFEBABE"), 0, data, 0xFFC, 8);
        ColoringRuleSet set = Compile(new ColoringRule { Kind = ColoringConditionKind.HexPattern, Pattern = "DE AD BE EF CA FE BA BE", Background = 0x00FF00 });
        foreach ((long start, int count) in new[] { (0x1000L, 0x100), (0xFF0L, 0x100), (0xF04L, 0xFC) })
        {
            var hex = new ColoringCell[count];
            var text = new ColoringCell[count];
            set.Evaluate(data, [], 0, start, hex, text);
            for (long o = Math.Max(start, 0xFFC); o < Math.Min(start + count, 0x1004); o++)
            {
                Assert.Equal(0, hex[o - start].Background);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-INSP-34-01")]
    public void The_upper_rule_wins_for_each_kind_of_color()
    {
        ColoringRule red = new() { Name = "R", Pattern = "00", Foreground = 0xFF0000 };
        ColoringRule blue = new() { Name = "B", Pattern = "00-1F", Foreground = 0x0000FF };
        ColoringRuleSet set = Compile(red, blue);
        (ColoringCell[] hex, _) = Evaluate(set, Bytes256);
        Assert.Equal(0xFF0000u, set.Rules[hex[0].Foreground].Rule.Foreground);
        Assert.Equal(0x0000FFu, set.Rules[hex[1].Foreground].Rule.Foreground);

        set = Compile(blue, red);
        (hex, _) = Evaluate(set, Bytes256);
        Assert.Equal(0x0000FFu, set.Rules[hex[0].Foreground].Rule.Foreground);

        // 背景色・文字色・枠線は種類ごとに、それを指定している最も上のルールのもの。
        ColoringRule back = new() { Pattern = "00-FF", Background = 0x112233, Border = ColoringBorder.Dashed };
        set = Compile(red, back);
        (hex, _) = Evaluate(set, Bytes256);
        Assert.Equal((0, 1, 1), ((int)hex[0].Foreground, (int)hex[0].Background, (int)hex[0].Border));

        // ドキュメントのルールは全体のルールより優先する。
        ColoringRuleSet both = ColoringRuleSet.Compile([blue], [red], null);
        (hex, _) = Evaluate(both, Bytes256);
        Assert.Equal(0x0000FFu, both.Rules[hex[0].Foreground].Rule.Foreground);

        // 適用先 (Hex 列だけ)。
        set = Compile(red with { Target = ColoringTarget.Hex });
        (hex, ColoringCell[] text) = Evaluate(set, Bytes256);
        Assert.Equal((0, -1), ((int)hex[0].Foreground, (int)text[0].Foreground));
    }

    [Fact]
    public void Other_condition_kinds()
    {
        byte[] data = new byte[1024];
        Encoding.ASCII.GetBytes("xxPNGxx\x01\x02\x03\x04\x05\x06\x07\x08\x09zzMZ").CopyTo(data, 0x100);
        BitConverter.GetBytes(0x7Fu).CopyTo(data, 0x200);
        BitConverter.GetBytes(0x1000u).CopyTo(data, 0x204);

        // テキスト (大文字・小文字の区別なし)。
        ColoringRuleSet text = Compile(new ColoringRule { Kind = ColoringConditionKind.Text, Pattern = "png", CaseSensitive = false, CodePage = 20127, Background = 1 });
        Assert.Equal([0x102, 0x103, 0x104], Marked(text, data));

        // 正規表現 (バイト列)。
        ColoringRuleSet regex = Compile(new ColoringRule { Kind = ColoringConditionKind.Regex, Pattern = @"[\x01-\x1F]{8,}", Background = 1 });
        Assert.Equal(Enumerable.Range(0x107, 9), Marked(regex, data));

        // 正規表現 (テキスト)。
        ColoringRuleSet regexText = Compile(new ColoringRule { Kind = ColoringConditionKind.Regex, Pattern = "MZ", RegexOnText = true, CodePage = 65001, Background = 1 });
        Assert.Equal([0x112, 0x113], Marked(regexText, data));

        // 数値 (uint32 LE で 0..0xFF、4 の倍数の位置)。
        ColoringRuleSet number = Compile(new ColoringRule
        {
            Kind = ColoringConditionKind.Number, NumberType = "uint32", Pattern = "1..0xFF", Modulus = 4, Remainder = 0, Background = 1,
        });
        Assert.Equal([0x200, 0x201, 0x202, 0x203], Marked(number, data));

        // 周期 (512 バイトごとの先頭 2 バイト)。
        ColoringRuleSet period = Compile(new ColoringRule { Kind = ColoringConditionKind.Period, Modulus = 512, Remainder = 0, PeriodLength = 2, Background = 1 });
        Assert.Equal([0, 1, 512, 513], Marked(period, data));

        // オフセット範囲 (終了はこのバイトを含む) と、適用する範囲。
        ColoringRuleSet range = Compile(new ColoringRule { Kind = ColoringConditionKind.OffsetRange, Pattern = "0x10", EndExpression = "0x13", Background = 1 });
        Assert.Equal([0x10, 0x11, 0x12, 0x13], Marked(range, data));
        ColoringRuleSet scoped = Compile(new ColoringRule { Pattern = "00", Background = 1, RangeStart = "0x20", RangeEnd = "0x21" });
        Assert.Equal([0x20, 0x21], Marked(scoped, data));

        // 構文の誤りは Errors に入り、適用しない。
        ColoringRule bad = new() { Kind = ColoringConditionKind.Regex, Pattern = "(", Background = 1 };
        ColoringRuleSet errors = Compile(bad);
        Assert.True(errors.IsEmpty);
        Assert.Equal(ColoringRuleErrorKind.InvalidRegex, errors.Errors[bad.Id].Kind);
    }

    private static List<int> Marked(ColoringRuleSet set, byte[] data)
    {
        (ColoringCell[] hex, _) = Evaluate(set, data);
        return [.. Enumerable.Range(0, data.Length).Where(i => hex[i].Background >= 0)];
    }

    [Fact]
    public void Rules_round_trip_through_json()
    {
        ColoringRule rule = new()
        {
            Name = "r", Kind = ColoringConditionKind.Number, NumberType = "double", BigEndian = true, Pattern = "0.5..1.5", Modulus = 8, Remainder = 4,
            Foreground = 0x123456, Background = 0xABCDEF, Border = ColoringBorder.Dotted, Target = ColoringTarget.Text, RangeStart = "0x10", RangeEnd = "end-1",
        };
        IReadOnlyList<ColoringRule> back = ColoringRule.Parse(ColoringRule.Serialize([rule]))!;
        Assert.Equal(rule, Assert.Single(back));
        Assert.Null(ColoringRule.Parse("{ not json"));
    }

    [Fact]
    public void The_engine_caches_chunks_and_counts_and_finds_matches()
    {
        byte[] bytes = new byte[3 * ColoringEngine.ChunkSize];
        foreach (int at in new[] { 10, 5000, 9000 })
        {
            Array.Copy(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, bytes, at, 4);
        }

        using var doc = new Document(new MemoryByteSource(bytes), Options());
        var rule = new ColoringRule { Kind = ColoringConditionKind.HexPattern, Pattern = "50 4B 03 04", Background = 0xFFD700 };
        var engine = new ColoringEngine { Schedule = work => work() };
        engine.Rules = ColoringRuleSet.Compile([rule], [], null);
        var hex = new ColoringCell[16];
        var text = new ColoringCell[16];
        Assert.False(engine.TryGetCells(doc.Current, 4992, hex, text));
        Assert.True(engine.TryGetCells(doc.Current, 4992, hex, text));
        Assert.Equal(0, hex[8].Background);

        // 編集で版が変わると評価し直す。
        doc.Overwrite(5000, [0, 0, 0, 0], "test");
        Assert.False(engine.TryGetCells(doc.Current, 4992, hex, text));
        Assert.True(engine.TryGetCells(doc.Current, 4992, hex, text));
        Assert.Equal(-1, hex[8].Background);
        doc.Undo();

        CompiledColoringRule compiled = engine.Rules.Rules[0];
        Assert.Equal(3, ColoringEngine.CountAll(doc.Current, compiled, null, CancellationToken.None));
        ReadForDisplayWhenLoaded(doc.Current, 4096, 4096 + 3);
        Assert.Equal(1, ColoringEngine.CountVisible(doc.Current, compiled, 4096, 8192));
        Assert.Equal(5000, ColoringEngine.FindNext(doc.Current, compiled, 10, forward: true));
        Assert.Equal(5000, ColoringEngine.FindNext(doc.Current, compiled, 9000, forward: false));
        Assert.Null(ColoringEngine.FindNext(doc.Current, compiled, 9000, forward: true));

        // 検索エンジンを使わない条件 (バイト値) の次 / 前。
        CompiledColoringRule byteRule = ColoringRuleSet.Compile([new ColoringRule { Pattern = "4B", Background = 1 }], [], null).Rules[0];
        Assert.Equal(5001, ColoringEngine.FindNext(doc.Current, byteRule, 11, forward: true));
        Assert.Equal(11, ColoringEngine.FindNext(doc.Current, byteRule, 5001, forward: false));
    }

    [Fact]
    [Trait(TC, "TC-INSP-34-05")]
    public void High_contrast_shapes_are_solid_dashed_dotted_and_double_in_rule_order()
    {
        Assert.Equal(
            [ColoringShape.Solid, ColoringShape.Dashed, ColoringShape.Dotted, ColoringShape.Double, ColoringShape.Solid],
            Enumerable.Range(0, 5).Select(ColoringShapes.HighContrast));
        Assert.Equal(ColoringShape.Dashed, ColoringShapes.Of(ColoringBorder.Dashed));
        Assert.Equal(ColoringShape.Dotted, ColoringShapes.Of(ColoringBorder.Dotted));
        Assert.Equal(ColoringShape.Solid, ColoringShapes.Of(ColoringBorder.Solid));
    }
}
