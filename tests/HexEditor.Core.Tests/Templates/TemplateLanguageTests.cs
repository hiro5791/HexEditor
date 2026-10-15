using HexEditor.Core.Templates;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>テンプレート言語の基本 (TPL-01〜TPL-11)。</summary>
public sealed class TemplateLanguageTests
{
    [Fact]
    [Trait(TC, "TC-TPL-01-02")]
    public void FunctionsAndTypesCanBeUsedBeforeTheirDefinition()
    {
        TemplateResult r = RunOk(
            "u8 a [[comment(std::format(\"{}\", twice(3)))]]; Item it; fn twice(u8 v) -> u8 { return v * 2; } struct Item { u8 x; };",
            Hex("01 02"));
        Assert.Equal("6", Node(r, "a").Comment);
        Assert.Equal(2L, Long(r, "it.x"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-02-01")]
    public void NumberLiteralsAndPrecedence()
    {
        const string t = "local auto v1 = 0x10 + 1Fh + 0b1 [[show]]; local auto v2 = 10 [[show]]; local auto v3 = 0o17 + 1_000 [[show]];"
            + " local auto v4 = 2 + 3 * 4 << 1 [[show]]; local auto v5 = 1 | 2 ^ 3 & 4 [[show]]; local auto v6 = !0 && 0 || 1 ? 7 : 8 [[show]];";
        TemplateResult r = RunOk(t, []);
        Assert.Equal(48L, Long(r, "v1"));
        Assert.Equal(10L, Long(r, "v2"));
        Assert.Equal(1015L, Long(r, "v3"));
        Assert.Equal(28L, Long(r, "v4"));
        Assert.Equal(3L, Long(r, "v5"));
        Assert.Equal(7L, Long(r, "v6"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-02-02")]
    public void DivisionByZeroIsAnErrorWithTheLine()
    {
        TemplateResult r = Run("u8 n;\nu8 a;\nlocal u32 q = 100 / (n - 1);\n", Hex("01 05"));
        Assert.Equal(TemplateRunState.Failed, r.State);
        TemplateDiagnostic e = Assert.Single(r.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.DivisionByZero, e.Code);
        Assert.Equal(3, e.Location!.Value.Line);
        Assert.Equal(1L, Long(r, "n"));
        Assert.Equal(5L, Long(r, "a"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-03-01")]
    public void U24EndianAndStringTerminator()
    {
        TemplateResult r = RunOk("le u24 a; be u24 b; string s; u8 after;", Hex("01 02 03 01 02 03 41 42 00 FF"));
        Assert.Equal(0x030201L, Long(r, "a"));
        Assert.Equal((0L, 3L), (Node(r, "a").Offset, Node(r, "a").Size));
        Assert.Equal(0x010203L, Long(r, "b"));
        Assert.Equal(3L, Node(r, "b").Offset);
        Assert.Equal("AB", Value(r, "s"));
        Assert.Equal((6L, 3L), (Node(r, "s").Offset, Node(r, "s").Size));
        Assert.Equal(9L, Node(r, "after").Offset);
        Assert.Equal(0xFFL, Long(r, "after"));

        TemplateResult r2 = Run("string s;", Hex("41 42 43"));
        Assert.Contains(r2.Diagnostics, d => d.Code == TemplateMessages.StringNotTerminated);
        Assert.Equal("ABC", Value(r2, "s"));
        Assert.Equal(3L, Node(r2, "s").Size);
    }

    [Fact]
    [Trait(TC, "TC-TPL-03-03")]
    public void GuidUuidAndRgb565()
    {
        TemplateResult r = RunOk("guid g; uuid u; le rgb565 c1; le rgb565 c2;",
            Hex("33 22 11 00 55 44 77 66 88 99 AA BB CC DD EE FF 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 F8 E0 07"));
        Assert.Equal("{00112233-4455-6677-8899-AABBCCDDEEFF}", Display(r, "g"), ignoreCase: true);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", Display(r, "u"));
        Assert.Equal("#FF0000", Display(r, "c1"));
        Assert.Equal("#00FF00", Display(r, "c2"));
    }

    private const string Tpl04 = """
        struct Header {
            u32 magic;
            u16 version;
            u16 count;
        };

        struct Record(u16 version) {
            u32 id;
            if (version >= 2) {
                u64 timestamp;
            }
            string name;
        };

        union Value {
            u32 as_int;
            f32 as_float;
            u8  bytes[4];
        };

        Header hdr;
        Record(hdr.version) records[hdr.count];
        """;

    [Fact]
    [Trait(TC, "TC-TPL-04-01")]
    public void StructWithParametersAndConditionalPattern()
    {
        TemplateResult r1 = RunOk(Tpl04, Hex("4D 41 47 43 01 00 02 00 01 00 00 00 41 00 02 00 00 00 42 00"));
        Assert.Equal(["id", "name"], Node(r1, "records[0]").Children.Select(c => c.Name));
        Assert.Equal(["id", "name"], Node(r1, "records[1]").Children.Select(c => c.Name));
        Assert.Equal("B", Value(r1, "records[1].name"));

        TemplateResult r2 = RunOk(Tpl04, Hex("4D 41 47 43 02 00 01 00 01 00 00 00 00 E1 F5 05 00 00 00 00 41 00"));
        Assert.Equal(["id", "timestamp", "name"], Node(r2, "records[0]").Children.Select(c => c.Name));
        Assert.Equal(100_000_000L, Long(r2, "records[0].timestamp"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-04-02")]
    public void UnionOffsetsAndSizeAttribute()
    {
        TemplateResult r = RunOk("u8 pad; union Value { u32 as_int; f32 as_float; u8 bytes[4]; }; Value v;", Hex("FF 00 00 80 3F"));
        Assert.Equal(1L, Node(r, "v.as_int").Offset);
        Assert.Equal(1L, Node(r, "v.as_float").Offset);
        Assert.Equal(1L, Node(r, "v.bytes").Offset);
        Assert.Equal(1.0, Value(r, "v.as_float"));
        Assert.Equal(4L, Node(r, "v").Size);

        TemplateResult r2 = Run("struct S { u8 a; u16 b; } [[size(32)]]; struct L { u8 data[40]; } [[size(32)]]; S s; L l; u8 after;", new byte[80]);
        Assert.Equal(32L, Node(r2, "s").Size);
        Assert.Equal(32L, Node(r2, "l").Size);
        Assert.Equal(64L, Node(r2, "after").Offset);
        _ = Node(r2, "l").Children.Count;
        Assert.Contains(r2.Diagnostics, d => d.Code == TemplateMessages.StructTooLarge && d.Arguments[0] == "40" && d.Arguments[1] == "32");
    }

    [Fact]
    [Trait(TC, "TC-TPL-04-03")]
    public void RecursiveLinkedListAndDepthLimit()
    {
        const string t = "struct Node { u8 has_next; u8 value; if (has_next != 0) { Node next; } }; Node head;";
        static byte[] Nodes(int n)
        {
            byte[] d = new byte[n * 2];
            for (int i = 0; i < n - 1; i++)
            {
                d[i * 2] = 1;
                d[(i * 2) + 1] = (byte)i;
            }

            return d;
        }

        TemplateResult r1 = RunOk(t, Nodes(10));
        Assert.NotNull(r1.FindByPath("head" + string.Concat(Enumerable.Repeat(".next", 9))));
        Assert.Null(r1.FindByPath("head" + string.Concat(Enumerable.Repeat(".next", 10))));

        TemplateResult r2 = Run(t, Nodes(300));
        TemplateDiagnostic e = Assert.Single(r2.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.RecursionTooDeep, e.Code);
        Assert.Equal("256", e.Arguments[0]);
        Assert.Contains("Node", e.CallStack!);
        Assert.NotNull(r2.FindByPath("head" + string.Concat(Enumerable.Repeat(".next", 200))));

        TemplateResult r3 = RunOk("#pragma limit.recursion 512\n" + t, Nodes(300));
        Assert.NotNull(r3.FindByPath("head" + string.Concat(Enumerable.Repeat(".next", 299))));
    }

    private const string Tpl05 = """
        enum Machine : u16 {
            Unknown = 0x0000,
            I386    = 0x014C,
            AMD64   = 0x8664,
            ARM64   = 0xAA64,
        };

        flags Characteristics : u16 {
            RelocsStripped    = 0x0001,
            Executable        = 0x0002,
            LargeAddressAware = 0x0020,
            Dll               = 0x2000,
        };

        enum Kind : u8 { A = 1, Reserved = 0x10 ... 0x1F, B };
        """;

    [Fact]
    [Trait(TC, "TC-TPL-05-01")]
    public void EnumsFlagsAndRanges()
    {
        TemplateResult r = RunOk(Tpl05 + "le Machine m; le Characteristics c; Kind k1; Kind k2; Kind k3;", Hex("64 86 22 20 14 20 01"));
        Assert.Equal("AMD64 (0x8664)", Display(r, "m"));
        Assert.Equal("Executable | LargeAddressAware | Dll", Display(r, "c"));
        Assert.Equal("Reserved (0x14)", Display(r, "k1"));
        Assert.Equal("B (0x20)", Display(r, "k2"));
        Assert.Equal("A (0x1)", Display(r, "k3"));
    }

    private const string TcpFlags = """
        bitfield TcpFlags : u16 {
            data_offset : 4;
            reserved    : 3;
            ns  : 1; cwr : 1; ece : 1; urg : 1;
            ack : 1; psh : 1; rst : 1; syn : 1; fin : 1;
        };
        be TcpFlags f;
        """;

    [Fact]
    [Trait(TC, "TC-TPL-06-01")]
    public void TcpFlagsAndBitOrder()
    {
        TemplateResult r = RunOk("#pragma bitfield_order msb_first\n" + TcpFlags, Hex("50 18"));
        Assert.Equal(5L, Long(r, "f.data_offset"));
        Assert.Equal(0L, Long(r, "f.reserved"));
        Assert.Equal(1L, Long(r, "f.ack"));
        Assert.Equal(1L, Long(r, "f.psh"));
        foreach (string n in new[] { "ns", "cwr", "ece", "urg", "rst", "syn", "fin" })
        {
            Assert.Equal(0L, Long(r, "f." + n));
        }

        TemplateResult r2 = RunOk("#pragma bitfield_order lsb_first\n" + TcpFlags, Hex("50 18"));
        Assert.Equal(8L, Long(r2, "f.data_offset"));
        Assert.NotEqual((Long(r, "f.ack"), Long(r, "f.psh"), Long(r, "f.fin")), (Long(r2, "f.ack"), Long(r2, "f.psh"), Long(r2, "f.fin")));
    }

    [Fact]
    [Trait(TC, "TC-TPL-07-02")]
    public void UntilTerminatedAndEofArrays()
    {
        TemplateResult r = RunOk("struct E { u8 type; u8 v; }; E list[until(last.type == 0)]; u8 rest;", Hex("01 0A 02 0B 00 0C 03 0D"));
        Assert.Equal(3, Node(r, "list").Children.Count);
        Assert.Equal(0L, Long(r, "list[2].type"));
        Assert.Equal(3L, Long(r, "rest"));

        TemplateResult r2 = RunOk("char name[]; u8 rest;", Hex("41 42 00 43"));
        Assert.Equal(3, Node(r2, "name").Children.Count);
        Assert.Equal("AB", Display(r2, "name"));
        Assert.Equal(0x43L, Long(r2, "rest"));

        TemplateResult r3 = Run("u16 w[eof];", Hex("01 00 02 00 03"));
        Assert.Equal(2, Node(r3, "w").Children.Count);
        Assert.Contains(r3.Diagnostics, d => d.Code == TemplateMessages.PartialElement);
    }

    [Fact]
    [Trait(TC, "TC-TPL-07-03")]
    public void ZeroSizeElementsStopWithAnError()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TemplateResult r = Run("struct Z { u8 x [[no_advance]]; }; Z z[while(true)];", Hex("00"), timeout: TimeSpan.FromSeconds(10));
        Assert.Contains(r.Diagnostics, d => d.Code == TemplateMessages.ZeroSizeElements);
        Assert.True(Node(r, "z").KnownChildCount <= 1000);

        TemplateResult r2 = Run("struct Z { if (false) { u8 x; } }; Z z[eof];", Hex("00"), timeout: TimeSpan.FromSeconds(10));
        Assert.Contains(r2.Diagnostics, d => d.Code == TemplateMessages.ZeroSizeElements);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait(TC, "TC-TPL-08-01")]
    public void NullRelativeAndLazyPointers()
    {
        TemplateResult r = RunOk("struct T { u8 v; }; le T *p0 : u32; le T *p1 : u32 [[relative]]; le T *p2 : u32;",
            Hex("00 00 00 00 08 00 00 00 0C 00 00 00 AA BB CC DD EE"));
        Assert.Equal("null", Display(r, "p0"));
        Assert.False(Node(r, "p0").HasChildren);
        Assert.Equal(0, r.EvaluationCounts.GetValueOrDefault("T"));
        TemplateNode p1 = Node(r, "p1");
        Assert.Single(p1.Children);
        Assert.Equal(1, r.EvaluationCounts.GetValueOrDefault("T"));
        Assert.Equal(0x0CL, p1.Pointer!.TargetOffset);
        Assert.Equal(0xAAL, Long(r, "p1.v"));
        Assert.Equal("→ 0xC (T)", Display(r, "p1"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-08-02")]
    public void SelfReferencingListsTerminate()
    {
        const string t = "struct Node { u32 value; le Node *next : u32 [[allow_null]]; }; Node head;";
        TemplateResult r = RunOk(t, Hex("01 00 00 00 00 00 00 00"));
        r.EvaluateAll();
        TemplateNode next = Node(r, "head.next");
        Assert.Equal("head", next.Pointer!.DuplicateOf);

        TemplateResult r2 = RunOk(t, Hex("01 00 00 00 08 00 00 00 02 00 00 00 00 00 00 00"));
        r2.EvaluateAll();
        Assert.Equal("head", Node(r2, "head.next.next").Pointer!.DuplicateOf);
        Assert.Equal(2, r2.EvaluationCounts["Node"]);
    }

    [Fact]
    [Trait(TC, "TC-TPL-09-01")]
    public void PlacementAlignAndOverlap()
    {
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        TemplateResult r = RunOk("u8 a; u32 far @ 0x80; u8 b; u8 c [[align(16)]]; $ = 0; u16 overlap;", data);
        Assert.Equal(0x80L, Node(r, "far").Offset);
        Assert.Equal(1L, Node(r, "b").Offset);
        Assert.Equal(0x10L, Node(r, "c").Offset);
        Assert.Equal((0L, 1L), (Node(r, "a").Offset, Node(r, "a").Size));
        Assert.Equal((0L, 2L), (Node(r, "overlap").Offset, Node(r, "overlap").Size));
    }

    [Fact]
    [Trait(TC, "TC-TPL-09-02")]
    public void StartOffsetAndBaseOffset()
    {
        byte[] data = new byte[1024 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        TemplateResult r = RunOk("u8 first; local u64 base = std::core::base_offset() [[show]]; u8 abs @ 0x10; u8 rel @ std::core::base_offset() + 0x10;",
            data, new TemplateApplyOptions { StartOffset = 0x1000 });
        Assert.Equal(0x1000L, Node(r, "first").Offset);
        Assert.Equal(0L, Long(r, "first"));
        Assert.Equal(4096L, Long(r, "base"));
        Assert.Equal(0x10L, Node(r, "abs").Offset);
        Assert.Equal(0x1010L, Node(r, "rel").Offset);
    }

    [Fact]
    [Trait(TC, "TC-TPL-10-01")]
    public void EndianPriority()
    {
        const string t = "#pragma endian little\nbe u32 x; u32 y; struct S { std::core::set_endian(big); u16 in1; } ; S s; u16 out1;"
            + " struct T { u16 a; } [[endian(big)]]; T t; le u16 kw [[endian(big)]];";
        TemplateResult r = RunOk(t, Hex("00 00 00 01 01 00 00 00 00 01 01 00 00 01 01 00"));
        Assert.Equal(1L, Long(r, "x"));
        Assert.Equal(1L, Long(r, "y"));
        Assert.Equal(1L, Long(r, "s.in1"));
        Assert.Equal(1L, Long(r, "out1"));
        Assert.Equal(1L, Long(r, "t.a"));
        Assert.Equal(1L, Long(r, "kw"));

        TemplateResult r2 = RunOk("u32 y;", Hex("00 00 00 01"), new TemplateApplyOptions { BigEndian = true });
        Assert.Equal(1L, Long(r2, "y"));
    }

    private const string Tiff = """
        struct TiffHeader {
            char order[2];
            if (order == "MM") std::core::set_endian(big);
            else               std::core::set_endian(little);
            u16 magic;          // 42
            u32 ifd_offset;
        };
        TiffHeader h;
        """;

    [Fact]
    [Trait(TC, "TC-TPL-10-02")]
    public void TiffHeaderInBothByteOrders()
    {
        foreach (string data in new[] { "49 49 2A 00 08 00 00 00", "4D 4D 00 2A 00 00 00 08" })
        {
            TemplateResult r = RunOk(Tiff, Hex(data));
            Assert.Equal(42L, Long(r, "h.magic"));
            Assert.Equal(8L, Long(r, "h.ifd_offset"));
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-11-02")]
    public void InfiniteLoopStopsAtTheStepLimit()
    {
        TemplateResult r = Run("#pragma limit.steps 1000\nu8 a; while (true) {}", Hex("01"), timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(TemplateRunState.LimitReached, r.State);
        Assert.Equal(TemplateLimitKind.Steps, r.LimitReached);
        Assert.Equal(1L, Long(r, "a"));
    }
}
