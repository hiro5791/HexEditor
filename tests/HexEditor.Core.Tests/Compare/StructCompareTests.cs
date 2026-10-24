using System.Buffers.Binary;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Compare;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-31 構造を理解した比較 (フィールドの対応付け)。</summary>
public sealed class StructCompareTests
{
    private const string TC = "TC";

    internal static readonly TemplateCatalog Catalog = TemplateCatalog.CreateDefault(null);

    internal static TemplateProgram Standard(string name) => Catalog.Load(Catalog.Find(name)!);

    internal static TemplateProgram FromText(string text, string fileName) => TemplateLoader.LoadText(text, fileName);

    internal static StructCompareSide Side(byte[] data, string name = "data") => new(TemplateDataSource.FromBytes(data), 0, 0, data.Length, name, null);

    internal static StructCompareResult Compare(TemplateProgram program, byte[] a, byte[] b, StructCompareOptions? options = null) =>
        StructComparer.Run(program, new StructTemplateChoice(program.Path, 0, 0, StructTemplateReason.UserChoice), Side(a, "a"), Side(b, "b"), options);

    /// <summary>構造の比較と単純比較を行い、構造の外の差分を求める (比較タブと同じ流れ)。</summary>
    internal static StructCompareResult CompareWithBytes(TemplateProgram program, byte[] a, byte[] b)
    {
        StructCompareResult r = Compare(program, a, b);
        using var bytes = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromBytes(a)), CompareRange.Whole(CompareData.FromBytes(b)));
        DataComparer.Run(new CompareOptions(), bytes);
        r.ComputeOutside(bytes.Diffs.Enumerate());
        return r;
    }

    private static StructDiffNode Single(StructCompareResult r, StructDiffKind kind, string path) =>
        Assert.Single(r.Diffs(), d => d.Kind == kind && d.DisplayPath == path);

    [Fact]
    [Trait(TC, "TC-ANA-31-01")]
    public void PeFilesDifferingOnlyInTimeDateStampGiveOneChange()
    {
        byte[] a = TestDataCatalog.StructPeA();
        byte[] b = TestDataCatalog.StructPeB();
        StructTemplateChoice choice = StructTemplateResolver.Resolve(null,
            new StructResolveSide(TemplateDataSource.FromBytes(a), "a.exe", 0, []), new StructResolveSide(TemplateDataSource.FromBytes(b), "b.exe", 0, []), Catalog);
        Assert.EndsWith("pe.hxt", choice.TemplatePath, StringComparison.Ordinal);
        Assert.Equal((0L, 0L), (choice.LeftStart, choice.RightStart));
        Assert.Equal(StructTemplateReason.MagicMatch, choice.Reason);

        StructCompareResult r = StructComparer.Run(Catalog.Load(choice.TemplatePath), choice, Side(a), Side(b));
        using var bytes = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromBytes(a)), CompareRange.Whole(CompareData.FromBytes(b)));
        DataComparer.Run(new CompareOptions(), bytes);
        r.ComputeOutside(bytes.Diffs.Enumerate());

        Assert.Equal(StructCompareState.Completed, r.State);
        StructDiffNode d = Assert.Single(r.Diffs());
        Assert.Equal(StructDiffKind.Changed, d.Kind);
        Assert.Equal("nt.FileHeader.TimeDateStamp", d.DisplayPath);
        int at = TestDataCatalog.StructPeTimeDateStampOffset;
        Assert.Equal((long)at, d.LeftOffset);
        Assert.Equal((long)at, d.RightOffset);
        Assert.Equal(Convert.ToUInt64(BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(at))), Convert.ToUInt64(r.ValueOf(d, right: false)));
        Assert.Equal(0x60000000UL, Convert.ToUInt64(r.ValueOf(d, right: true)));
        Assert.False(string.IsNullOrEmpty(d.LeftDisplay));
        Assert.Equal(new StructOutsideDiffs(0, 0), r.Outside);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-02")]
    public void TemplateIsChosenOnlyWhenBothMagicsMatch()
    {
        StructResolveSide S(byte[] data, string name) => new(TemplateDataSource.FromBytes(data), name, 0, []);
        byte[] pngA = TestDataCatalog.StructPngA();
        byte[] pngB = TestDataCatalog.StructPngB();
        byte[] extA = TestDataCatalog.Diff3A();
        byte[] extB = TestDataCatalog.Diff3B();

        StructTemplateChoice png = StructTemplateResolver.Resolve(null, S(pngA, "a.png"), S(pngB, "b.png"), Catalog);
        Assert.EndsWith("png.hxt", png.TemplatePath, StringComparison.Ordinal);
        Assert.Equal(StructTemplateReason.MagicMatch, png.Reason);
        Assert.Equal((0L, 0L), (png.LeftStart, png.RightStart));

        Assert.True(StructTemplateResolver.Resolve(null, S(extA, "a.png"), S(extB, "b.png"), Catalog).IsNone);
        Assert.True(StructTemplateResolver.Resolve(null, S(pngA, "a.png"), S(extB, "b.png"), Catalog).IsNone);
    }

    [Fact]
    public void FixedApplicationsAtTheSameRelativeOffsetAreUsedFirst()
    {
        byte[] data = TestDataCatalog.StructPngA();
        string path = Catalog.Find("png")!.Path;
        var left = new StructResolveSide(TemplateDataSource.FromBytes(data), "a.bin", 0x10,
            [new TemplateApplicationInfo { TemplatePath = path, StartOffset = 0x18 }, new TemplateApplicationInfo { TemplatePath = path, StartOffset = 0x40 }]);
        var right = new StructResolveSide(TemplateDataSource.FromBytes(data), "b.bin", 0x20,
            [new TemplateApplicationInfo { TemplatePath = path, StartOffset = 0x28 }, new TemplateApplicationInfo { TemplatePath = path, StartOffset = 0x50 }]);
        StructTemplateChoice c = StructTemplateResolver.Resolve(null, left, right, Catalog);
        Assert.Equal(StructTemplateReason.SameApplication, c.Reason);
        Assert.Equal((0x18L, 0x28L), (c.LeftStart, c.RightStart));

        StructTemplateChoice user = StructTemplateResolver.Resolve(new StructTemplateChoice("x.hxt", 1, 2, StructTemplateReason.UserChoice), left, right, Catalog);
        Assert.Equal(("x.hxt", 1L, 2L), (user.TemplatePath, user.LeftStart, user.RightStart));
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-03")]
    public void PngChunksAreReportedAsFieldChanges()
    {
        StructCompareResult r = Compare(Standard("png"), TestDataCatalog.StructPngA(), TestDataCatalog.StructPngB());
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        Assert.Equal(7, diffs.Count);

        StructDiffNode width = Single(r, StructDiffKind.Changed, "chunks[0] (type = \"IHDR\").header.width");
        Assert.Equal(("16", "32"), (width.LeftDisplay, width.RightDisplay));
        Single(r, StructDiffKind.Changed, "chunks[0] (type = \"IHDR\").crc");
        Single(r, StructDiffKind.Changed, "chunks[1] (type = \"tEXt\").length");
        StructDiffNode text = Single(r, StructDiffKind.Changed, "chunks[1] (type = \"tEXt\").text");
        Assert.Equal((12L, 10L), (text.LeftSize, text.RightSize));
        Single(r, StructDiffKind.Changed, "chunks[1] (type = \"tEXt\").crc");

        StructDiffNode added = Single(r, StructDiffKind.Added, "chunks[2] (type = \"tEXt\")");
        StructDiffNode author = r.Root.FirstChild!.NextSibling!.Children.First(n => n.LeftIndex == 1);
        Assert.Equal(author.LeftOffset + author.LeftSize, added.LeftOffset);
        Assert.Equal(0, added.LeftSize);
        Single(r, StructDiffKind.Removed, "chunks[2] (type = \"tIME\")");

        // IDAT と IEND (左右でオフセットが違う) は差分にならない
        Assert.DoesNotContain(diffs, d => d.DisplayPath.Contains("IDAT", StringComparison.Ordinal) || d.DisplayPath.Contains("IEND", StringComparison.Ordinal));
        StructDiffNode chunks = r.Root.Children.Single(n => n.Name == "chunks");
        Assert.Equal("key:type", chunks.Array!.MatchText);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-04")]
    public void ReorderedChunkIsOneMove()
    {
        StructCompareResult r = Compare(Standard("png"), TestDataCatalog.StructPngA(), TestDataCatalog.StructPngMove());
        StructDiffNode d = Assert.Single(r.Diffs());
        Assert.Equal(StructDiffKind.Moved, d.Kind);
        Assert.Equal((1L, 3L), (d.LeftIndex, d.RightIndex));
        Assert.Equal("chunks[1] (type = \"tEXt\")", d.DisplayPath);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-05")]
    public void ArrayWithoutKeyIsMatchedByContentAndThenByChosenKey()
    {
        TemplateProgram items = FromText(TestDataCatalog.StructItemsHxt, "items.hxt");
        byte[] a = TestDataCatalog.StructArrA();
        byte[] b = TestDataCatalog.StructArrB();
        StructCompareResult r = Compare(items, a, b);
        AssertArrDiffs(r, keyed: false);
        Assert.Equal(StructMatchMethod.Content, ItemsInfo(r).Method);

        var rules = new StructArrayRules();
        rules.Set(items.Path, ItemsInfo(r).RulePath, StructMatchMethod.Key, "id");
        StructCompareResult keyed = Compare(items, a, b, new StructCompareOptions { Rules = rules });
        AssertArrDiffs(keyed, keyed: true);
        Assert.Equal("key:id", ItemsInfo(keyed).MatchText);
        Assert.True(ItemsInfo(keyed).FromRule);
        Assert.Equal(["id", "value"], ItemsInfo(keyed).KeyCandidates);

        // 記録した設定は JSON を通しても同じ
        StructArrayRules restored = StructArrayRules.FromJson(rules.ToJson());
        Assert.Equal(rules.Items, restored.Items);
    }

    private static StructArrayInfo ItemsInfo(StructCompareResult r) => r.Root.Children.Single(n => n.Name == "items").Array!;

    private static void AssertArrDiffs(StructCompareResult r, bool keyed)
    {
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        Assert.Equal(3, diffs.Count);
        StructDiffNode changed = Assert.Single(diffs, d => d.Kind == StructDiffKind.Changed);
        Assert.Equal(keyed ? "items[9] (id = 10).value" : "items[9].value", changed.DisplayPath);
        Assert.Equal(("100", "7"), (changed.LeftDisplay, changed.RightDisplay));
        StructDiffNode added = Assert.Single(diffs, d => d.Kind == StructDiffKind.Added);
        Assert.Equal(keyed ? "items[100] (id = 2000)" : "items[100]", added.DisplayPath);
        StructDiffNode removed = Assert.Single(diffs, d => d.Kind == StructDiffKind.Removed);
        Assert.Equal(keyed ? "items[499] (id = 500)" : "items[499]", removed.DisplayPath);
        Assert.Equal(2000U, IdAt(r.RightResult!, added.RightOffset));
        Assert.Equal(500U, IdAt(r.LeftResult!, removed.LeftOffset));
    }

    private static uint IdAt(TemplateResult r, long offset)
    {
        byte[] id = new byte[4];
        r.Data.Read(offset, id, out _);
        return BinaryPrimitives.ReadUInt32LittleEndian(id);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-07")]
    public void DiffKeyAttributeMatchesByThatField()
    {
        StructCompareResult r = Compare(FromText(TestDataCatalog.StructItemsKeyHxt, "items-key.hxt"), TestDataCatalog.StructArrA(), TestDataCatalog.StructArrSwap());
        Assert.Equal("key:id", ItemsInfo(r).MatchText);
        Assert.False(ItemsInfo(r).FromRule);
        StructDiffNode d = Assert.Single(r.Diffs());
        Assert.Equal(StructDiffKind.Moved, d.Kind);
        Assert.Equal("items[0] (id = 1)", d.DisplayPath);
        Assert.Equal((0L, 999L), (d.LeftIndex, d.RightIndex));
    }

    [Fact]
    public void DiffKeyIsAKnownAttributeAndChangesNothingElse()
    {
        TemplateProgram p = FromText(TestDataCatalog.StructItemsKeyHxt, "items-key.hxt");
        TemplateResult r = TemplateEngine.ApplyAndWait(p, TemplateDataSource.FromBytes(TestDataCatalog.StructArrA()));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == TemplateMessages.UnknownAttribute);
        Assert.Equal(1000, r.FindByPath("items")!.ChildCount);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-08")]
    public void PartAfterAParseErrorIsOneUnparsedRow()
    {
        StructCompareResult r = Compare(Standard("png"), TestDataCatalog.StructPngA(), TestDataCatalog.StructPngBroken());
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        StructDiffNode unparsed = Assert.Single(diffs, d => d.Kind == StructDiffKind.Unparsed);
        Assert.Equal("chunks[3]", unparsed.RightPath);
        Assert.True(unparsed.ReasonRight);
        Assert.StartsWith("HXT2", unparsed.Diagnostic!.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(diffs, d => d.Kind is StructDiffKind.Added or StructDiffKind.Removed);
        Assert.True(r.RightErrors >= 1);
        Assert.Equal(TestDataCatalog.StructIdatOffset(TestDataCatalog.StructPngA()), unparsed.LeftOffset);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-09")]
    public void BytesOutsideTheTemplateAreCountedAsOutsideDiffs()
    {
        byte[] a = TestDataCatalog.StructPngA();
        StructCompareResult r = CompareWithBytes(Standard("png"), [.. a, .. new byte[16]], [.. a, .. Enumerable.Repeat((byte)0xFF, 16)]);
        Assert.Empty(r.Diffs());
        Assert.Equal(new StructOutsideDiffs(1, 16), r.Outside);
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-11")]
    public void FieldLimitStopsAndRaisingItComparesEverything()
    {
        TemplateProgram many = FromText(TestDataCatalog.StructManyHxt, "many.hxt");
        byte[] a = new byte[2 * 1024 * 1024];
        byte[] b = [.. Enumerable.Repeat((byte)0xFF, a.Length)];
        StructCompareResult limited = Compare(many, a, b);
        Assert.Equal(StructCompareState.FieldLimit, limited.State);
        Assert.Equal(StructCompareOptions.DefaultMaxFields, limited.LeftFields);
        Assert.InRange(limited.DiffCount, 1, StructCompareOptions.DefaultMaxFields);
        limited.Dispose();

        // 「上限を上げて再比較」: 設定の最大値まで上げる
        StructCompareResult all = Compare(many, a, b, new StructCompareOptions { MaxFields = StructCompareOptions.MaxMaxFields, MaxSameRows = 0 });
        Assert.Equal(StructCompareState.Completed, all.State);
        Assert.Equal(2_097_152, all.LeftFields);
        Assert.Equal(2_097_152, all.RightFields);
        Assert.Equal(2_097_152, all.DiffCount);
        StructArrayInfo r = all.Root.Children.Single(n => n.Name == "r").Array!;
        Assert.True(r.TooManyForContent);
        Assert.Equal(StructMatchMethod.Index, r.Method);
        all.Dispose();
    }

    [Fact]
    [Trait(TC, "TC-ANA-31-12")]
    public async Task CancellationStopsWithinOneHundredMilliseconds()
    {
        TemplateProgram many = FromText(TestDataCatalog.StructManyHxt, "many.hxt");
        byte[] a = new byte[2 * 1024 * 1024];
        byte[] b = [.. Enumerable.Repeat((byte)0xFF, a.Length)];
        StructCompareResult r = StructComparer.Start(many, new StructTemplateChoice(many.Path, 0, 0, StructTemplateReason.UserChoice), Side(a), Side(b),
            new StructCompareOptions { MaxFields = StructCompareOptions.MaxMaxFields });
        while (r.DiffCount < 10_000 && !r.Completion.IsCompleted)
        {
            await Task.Delay(5);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        r.Cancel();
        await r.Completion;
        watch.Stop();
        Assert.Equal(StructCompareState.Cancelled, r.State);
        Support.TimeBudget.Limit(watch.ElapsedMilliseconds <= 100, $"キャンセルに {watch.ElapsedMilliseconds} ms");
        Assert.True(r.DiffCount >= 10_000);
        r.Dispose();
    }

    [Fact]
    public void ComparingDataWithItselfGivesNoDiffs()
    {
        foreach (byte[] data in new[] { TestDataCatalog.StructPngA(), TestDataCatalog.StructPngBroken() })
        {
            Assert.Empty(Compare(Standard("png"), data, data).Diffs());
        }

        Assert.DoesNotContain(Compare(FromText(TestDataCatalog.StructMixedHxt, "mixed.hxt"), TestDataCatalog.StructMixedB(), TestDataCatalog.StructMixedB()).Diffs(),
            d => d.Kind != StructDiffKind.Unparsed);
    }

    [Fact]
    public void MixedDataGivesEveryKind()
    {
        StructCompareResult r = Compare(FromText(TestDataCatalog.StructMixedHxt, "mixed.hxt"), TestDataCatalog.StructMixedA(), TestDataCatalog.StructMixedB());
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.TypeChanged && d.DisplayPath == "extra");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.Removed && d.DisplayPath == "items[1] (tag = \"BBBB\")");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.Added && d.DisplayPath == "items[5] (tag = \"ZZZZ\")");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.Moved && d.DisplayPath == "items[2] (tag = \"CCCC\")");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.MovedChanged && d.DisplayPath == "items[3] (tag = \"DDDD\")");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.Changed && d.DisplayPath == "items[4] (tag = \"EEEE\").value");
        Assert.Contains(diffs, d => d.Kind == StructDiffKind.Unparsed && d.DisplayPath == "tail");
    }

    [Fact]
    public void FixedLeafArrayIsComparedInRuns()
    {
        TemplateProgram p = FromText("#pragma endian little\nu32 head;\nu16 a[64];\n", "runs.hxt");
        byte[] a = new byte[4 + 128];
        byte[] b = (byte[])a.Clone();
        for (int i = 10; i <= 12; i++)
        {
            b[4 + (i * 2)] = 1;
        }

        b[4 + (40 * 2)] = 2;
        StructCompareResult r = Compare(p, a, b);
        Assert.Equal(["a[10 … 12]", "a[40]"], r.Diffs().Select(d => d.DisplayPath));
        Assert.Equal(4 + 20, r.Diffs()[0].LeftOffset);
        Assert.Equal(6, r.Diffs()[0].LeftSize);
    }

    [Fact]
    public void HiddenAndExportFalsePatternsAreNotCompared()
    {
        TemplateProgram p = FromText("u8 a;\nu8 h [[hidden]];\nu8 e [[export(false)]];\nu8 z;\n", "hidden.hxt");
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 9, 9, 4];
        Assert.Empty(Compare(p, a, b).Diffs());
        Assert.Equal(["h"], Compare(p, a, b, new StructCompareOptions { IncludeHidden = true }).Diffs().Select(d => d.DisplayPath));
    }

    [Fact]
    public void ArrayCutByTheLimitBecomesALimitRow()
    {
        // 右は 6 要素のうち 4 要素で打ち切られる。左にしかない EE・FF は「削除」にせず「上限のため比べていません」にまとめる
        TemplateProgram p = FromText("#pragma limit.array 4\nu8 n;\nstruct E { char t[2]; };\nE e[n];\n", "limit.hxt");
        StructCompareResult r = Compare(p, [4, .. "AABBEEFF"u8], [6, .. "AABBCCDDEEFF"u8]);
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        StructDiffNode limit = Assert.Single(diffs, d => d.Kind == StructDiffKind.Unparsed);
        Assert.Equal(StructUnparsedReason.Limit, limit.Reason);
        Assert.Equal(2, limit.FoldedCount);
        Assert.DoesNotContain(diffs, d => d.Kind == StructDiffKind.Removed);
        Assert.Equal(2, diffs.Count(d => d.Kind == StructDiffKind.Added));
    }

    // ───────────── TC-ANA-31-14: 無作為な木 ─────────────

    private const string TagTemplate = "#pragma endian little\nstruct Item { char tag[4]; u32 value; };\nu32 count;\nItem items[count];\n";

    public sealed record TreeCase(int Length, int Edits, int Seed);

    private static Arbitrary<TreeCase> TreeCases() =>
        (from length in Gen.Choose(0, 30)
         from edits in Gen.Choose(0, 20)
         from seed in Gen.Choose(0, int.MaxValue)
         select new TreeCase(length, edits, seed)).ToArbitrary();

    private static readonly TemplateProgram TagProgram = FromText(TagTemplate, "tags.hxt");

    private static byte[] Encode(List<(string Tag, uint Value)> items)
    {
        byte[] data = new byte[4 + (items.Count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Encoding.ASCII.GetBytes(items[i].Tag).CopyTo(data, 4 + (i * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + (i * 8)), items[i].Value);
        }

        return data;
    }

    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-ANA-31-14")]
    public Property RandomTreesMatchTheReferenceRules() => Prop.ForAll(TreeCases(), c =>
    {
        var random = new Random(c.Seed);
        var used = new HashSet<string>();
        string NewTag()
        {
            while (true)
            {
                string t = new([.. Enumerable.Range(0, 4).Select(_ => (char)random.Next('A', 'Z' + 1))]);
                if (used.Add(t))
                {
                    return t;
                }
            }
        }

        List<(string Tag, uint Value)> left = [.. Enumerable.Range(0, c.Length).Select(_ => (NewTag(), (uint)random.Next(0, 1000)))];
        List<(string Tag, uint Value)> right = [.. left];
        for (int e = 0; e < c.Edits; e++)
        {
            int op = random.Next(4);
            if (op == 0 || right.Count == 0)
            {
                right.Insert(random.Next(right.Count + 1), (NewTag(), (uint)random.Next(0, 1000)));
            }
            else if (op == 1)
            {
                right.RemoveAt(random.Next(right.Count));
            }
            else if (op == 2)
            {
                int from = random.Next(right.Count);
                (string, uint) item = right[from];
                right.RemoveAt(from);
                right.Insert(random.Next(right.Count + 1), item);
            }
            else
            {
                int at = random.Next(right.Count);
                right[at] = (right[at].Tag, right[at].Value + 1 + (uint)random.Next(10));
            }
        }

        var rules = new StructArrayRules();
        rules.Set(TagProgram.Path, "items", StructMatchMethod.Key, "tag");
        var options = new StructCompareOptions { Rules = rules };
        StructCompareResult r = Compare(TagProgram, Encode(left), Encode(right), options);
        IReadOnlyList<StructDiffNode> diffs = r.Diffs();
        string TagOf(StructDiffNode n) => n.KeyText!["tag = \"".Length..^1];
        StructDiffNode ElementOf(StructDiffNode n) => n.Parent!.Array is null ? ElementOf(n.Parent) : n;

        // 基準: タグの集合の差と、左の順に並べた右の位置の最長増加部分列
        var leftTags = left.Select(x => x.Tag).ToList();
        var rightTags = right.Select(x => x.Tag).ToList();
        Assert.Equal(leftTags.Except(rightTags).Order(), diffs.Where(d => d.Kind == StructDiffKind.Removed).Select(TagOf).Order());
        Assert.Equal(rightTags.Except(leftTags).Order(), diffs.Where(d => d.Kind == StructDiffKind.Added).Select(TagOf).Order());
        Assert.Equal(left.Count != right.Count, diffs.Any(d => d.DisplayPath == "count"));
        var common = leftTags.Where(rightTags.Contains).ToList();
        var moved = diffs.Where(d => d.Kind is StructDiffKind.Moved or StructDiffKind.MovedChanged).Select(TagOf).ToHashSet();
        int[] positions = [.. common.Select(t => rightTags.IndexOf(t))];
        Assert.Equal(common.Count - LisLength(positions), moved.Count);
        int[] stay = [.. common.Where(t => !moved.Contains(t)).Select(t => rightTags.IndexOf(t))];
        Assert.True(stay.Zip(stay.Skip(1)).All(p => p.First < p.Second));
        foreach (string tag in common)
        {
            bool valueChanged = left.First(x => x.Tag == tag).Value != right.First(x => x.Tag == tag).Value;
            if (moved.Contains(tag))
            {
                Assert.Equal(valueChanged ? StructDiffKind.MovedChanged : StructDiffKind.Moved,
                    diffs.Single(d => d.Kind is StructDiffKind.Moved or StructDiffKind.MovedChanged && TagOf(d) == tag).Kind);
            }

            Assert.Equal(valueChanged, diffs.Any(d => d.Kind == StructDiffKind.Changed && d.Name == "value" && TagOf(ElementOf(d)) == tag));
        }

        int expected = leftTags.Except(rightTags).Count() + rightTags.Except(leftTags).Count() + moved.Count
            + common.Count(t => left.First(x => x.Tag == t).Value != right.First(x => x.Tag == t).Value) + (left.Count != right.Count ? 1 : 0);
        Assert.Equal(expected, diffs.Count);

        // それ自身との比較では差分が 0 件
        Assert.Empty(Compare(TagProgram, Encode(right), Encode(right), options).Diffs());
        r.Dispose();
    });

    private static int LisLength(int[] values)
    {
        var tails = new List<int>();
        foreach (int v in values)
        {
            int i = tails.BinarySearch(v);
            i = i < 0 ? ~i : i;
            if (i == tails.Count)
            {
                tails.Add(v);
            }
            else
            {
                tails[i] = v;
            }
        }

        return tails.Count;
    }
}
