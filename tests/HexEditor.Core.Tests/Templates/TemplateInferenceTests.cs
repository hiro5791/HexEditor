using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Inference;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Tests.I18n;
using HexEditor.Core.Tests.Engine;
using HexEditor.Core.Text;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>テンプレートの推定の補助: テストデータ・リソースの文。</summary>
internal static class InferenceTestSupport
{
    public static Document Doc(byte[] data) => new(new MemoryByteSource(data), Support.DocumentAssert.Options());

    public static Document Doc(IByteSource source) => new(source, Support.DocumentAssert.Options());

    public static byte[] Rec48(bool big = false) => TestDataCatalog.InferRec48.Small(big).ToArray();

    public static TemplateInferenceResult Infer(byte[] data, long start = 0, long? length = null, int? recordLength = null,
        InferenceEndian endian = InferenceEndian.Auto, long? count = null)
    {
        using Document doc = Doc(data);
        return TemplateInference.Run(doc.Current, new TemplateInferenceRequest
        {
            Start = start,
            Length = length ?? (data.Length - start),
            RecordLength = recordLength,
            RecordCount = count,
            Endian = endian,
        });
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Resources = [];

    /// <summary>App の文字列のリソース (en / ja)。</summary>
    public static Dictionary<string, string> Strings(string language)
    {
        lock (Resources)
        {
            if (!Resources.TryGetValue(language, out Dictionary<string, string>? map))
            {
                string app = Path.GetDirectoryName(SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;
                map = ResourceChecker.LoadResw(Path.Combine(app, "Strings", language, "Resources.resw"));
                Resources[language] = map;
            }

            return map;
        }
    }

    /// <summary>リソースから文を作る (App の Loc.Format と同じ書式)。</summary>
    public static Func<InferenceMessage, string> Text(string language = "en", string culture = "en-US") => m =>
        MessageFormat.Format(Strings(language)[m.Key], CultureInfo.GetCultureInfo(culture), language, m.Args);

    public static string Draft(InferenceTable table, string name = "TD-TPL-INFER-REC48.bin", string language = "en")
    {
        TemplateInferenceResult r = table.Result!;
        var info = new TemplateDraftInfo(name, r.Start, r.RecordLength!.Value, r.RecordCount, new DateTime(2026, 10, 21), table.BigEndian);
        return TemplateDraftWriter.Write(table.Rows, info, Text(language, language == "ja" ? "ja-JP" : "en-US"));
    }

    public static string Dump(TemplateInferenceResult r) => string.Join("\n", r.Fields.Select(f =>
        $"0x{f.Offset:X2} {f.Size} {f.Kind} {f.Type.Display(null)} {f.Confidence} be={f.Rule.BigEndian} {f.Name} {f.Rule.Reason} | "
        + string.Join(" / ", f.Candidates.Select(c => c.Kind + ":" + c.Confidence))));
}

/// <summary>テンプレートの推定 (07 の 8 章、TPL-38〜TPL-40) の Core の試験。</summary>
public sealed class TemplateInferenceTests(ITestOutputHelper output)
{
    private static TemplateInferenceResult Rec48(int? k = null, InferenceEndian endian = InferenceEndian.Auto, bool big = false) =>
        InferenceTestSupport.Infer(InferenceTestSupport.Rec48(big), 0, 48_000, k, endian);

    private static TemplateInferenceResult File(string id, int? k = null, InferenceEndian endian = InferenceEndian.Auto) =>
        InferenceTestSupport.Infer(System.IO.File.ReadAllBytes(TestDataCatalog.Get(id)), recordLength: k, endian: endian);

    private static InferredField At(TemplateInferenceResult r, int offset) =>
        r.Fields.SingleOrDefault(f => f.Offset == offset) ?? throw new Xunit.Sdk.XunitException($"0x{offset:X} がありません:\n" + InferenceTestSupport.Dump(r));

    // ───────────── TPL-38 ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-38-01")]
    public void Rec48_record_length_and_count_are_inferred()
    {
        TemplateInferenceResult r = Rec48();
        output.WriteLine(string.Join(", ", r.Candidates.Select(c => $"{c.Length}:{c.Score:0.000}")));
        Assert.Equal(48, r.Candidates[0].Length);
        Assert.Equal(48, r.RecordLength);
        Assert.Equal(1000, r.RecordCount);
        Assert.InRange(r.Candidates.Count, 1, 5);
        Assert.Equal(0, r.Remainder);
        Assert.True(r.LengthEstimated);

        // 得点の高い順 (差が 0.02 以内なら小さい k が上)
        for (int i = 1; i < r.Candidates.Count; i++)
        {
            Assert.True(r.Candidates[i].Score <= r.Candidates[i - 1].Score + RecordLengthEstimator.Tolerance);
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-38-02")]
    public void Rec64_ranks_64_above_its_divisor_and_multiple()
    {
        TemplateInferenceResult r = File("TD-ANA-REC64");
        output.WriteLine(string.Join(", ", r.Candidates.Select(c => $"{c.Length}:{c.Score:0.000}")));
        Assert.Equal(64, r.Candidates[0].Length);
        int rank64 = r.Candidates.ToList().FindIndex(c => c.Length == 64);
        foreach (int other in new[] { 32, 128 })
        {
            int rank = r.Candidates.ToList().FindIndex(c => c.Length == other);
            Assert.True(rank < 0 || rank > rank64);
        }
    }

    [Fact]
    public void Random_data_has_no_fixed_length_records()
    {
        // TC-TPL-38-03 (UI) の Core の部分: 乱数だけでは「固定長のレコードが見つかりませんでした」
        TemplateInferenceResult r = File("TD-TPL-INFER-RANDOM");
        output.WriteLine(string.Join(", ", r.Candidates.Select(c => $"{c.Length}:{c.Score:0.000}")));
        Assert.True(r.NotFound);
        Assert.Empty(r.Fields);
        Assert.Null(r.RecordLength);
    }

    [Fact]
    [Trait(TC, "TC-TPL-38-04")]
    public void A_given_record_length_is_used_without_estimating()
    {
        TemplateInferenceResult r = Rec48(k: 48);
        Assert.False(r.LengthEstimated);
        Assert.Empty(r.Candidates);
        Assert.Equal(48, r.RecordLength);
        Assert.Equal(1000, r.RecordCount);

        TemplateInferenceResult rec64 = File("TD-ANA-REC64", k: 48);
        Assert.False(rec64.LengthEstimated);
        Assert.Equal(48, rec64.RecordLength);
        Assert.Equal(21_845, rec64.RecordCount);
        Assert.Equal(16, rec64.Remainder);
    }

    [Fact]
    [Trait(TC, "TC-TPL-38-06")]
    public void A_remainder_that_does_not_fill_a_record_is_reported()
    {
        TemplateInferenceResult r = InferenceTestSupport.Infer(InferenceTestSupport.Rec48(), 0, 48_020);
        Assert.Equal(48, r.RecordLength);
        Assert.Equal(1000, r.RecordCount);
        Assert.Equal(20, r.Remainder);
    }

    [Fact]
    public void Ranges_that_do_not_hold_four_records_are_blocked()
    {
        var request = new TemplateInferenceRequest { Start = 0, Length = 31 };
        Assert.Equal("TplInfer_Block_MinLength", request.Validate(1000)?.Key);
        Assert.Equal("TplInfer_Block_FourRecords", (request with { Length = 100, RecordLength = 48 }).Validate(1000)?.Key);
        Assert.Null((request with { Length = 192, RecordLength = 48 }).Validate(1000));
        Assert.Equal("TplInfer_Block_RecordLength", (request with { Length = 1000, RecordLength = 70_000 }).Validate(1000)?.Key);

        // レコード長と数を両方指定すると、範囲の長さは「レコード長 × 数」
        var both = new TemplateInferenceRequest { Start = 0, Length = 10, RecordLength = 48, RecordCount = 10 };
        Assert.Equal(480, both.EffectiveLength(100_000));
        TemplateInferenceResult r = InferenceTestSupport.Infer(InferenceTestSupport.Rec48(), 0, 10, 48, count: 10);
        Assert.Equal(10, r.RecordCount);
        Assert.Equal(480, r.RangeLength);
    }

    [Fact]
    public void Samples_take_the_first_1024_records_and_evenly_spaced_ones_up_to_the_last()
    {
        long[] picked = RecordSample.SelectIndices(1_000_000, 48);
        Assert.Equal(4096, picked.Length);
        Assert.Equal(Enumerable.Range(0, 1024).Select(i => (long)i), picked.Take(1024));
        Assert.Equal(999_999, picked[^1]);
        Assert.Equal(picked.Length, picked.Distinct().Count());
        Assert.True(picked.Zip(picked.Skip(1)).All(p => p.First < p.Second));

        // 標本は 16 MiB まで (最小 4 個)
        Assert.Equal(256, RecordSample.SelectIndices(1_000_000, 65_536).Length);
        Assert.Equal(1000, RecordSample.SelectIndices(1000, 48).Length);
    }

    [Fact]
    public void Unreadable_records_are_left_out_of_the_sample()
    {
        byte[] data = InferenceTestSupport.Rec48();
        var faulty = new FaultyByteSource(new MemoryByteSource(data));
        faulty.AddReadError(48 * 10, 48 * 5);
        using Document doc = InferenceTestSupport.Doc(faulty);
        TemplateInferenceResult r = TemplateInference.Run(doc.Current, new TemplateInferenceRequest { Start = 0, Length = 48_000, RecordLength = 48 });
        Assert.Equal(5, r.UnreadableRecords);
        Assert.Equal(995, r.SampleCount);
        Assert.Equal(InferredKind.Counter, At(r, 4).Kind);
    }

    // ───────────── TPL-39 ─────────────

    private static void AssertRec48Fields(TemplateInferenceResult r, ITestOutputHelper output)
    {
        output.WriteLine(InferenceTestSupport.Dump(r));
        (int Offset, int Size, InferredKind Kind, string Type, InferenceConfidence Confidence)[] expected =
        [
            (0x00, 4, InferredKind.Magic, "char[4]", InferenceConfidence.High),
            (0x04, 4, InferredKind.Counter, "u32", InferenceConfidence.High),
            (0x08, 4, InferredKind.Time, "time32_t", InferenceConfidence.High),
            (0x0C, 4, InferredKind.Offset, "u32", InferenceConfidence.High),
            (0x10, 4, InferredKind.Length, "u32", InferenceConfidence.High),
            (0x14, 4, InferredKind.Float, "f32", InferenceConfidence.High),
            (0x18, 2, InferredKind.Enum, "enum : u16", InferenceConfidence.Medium),
            (0x1A, 2, InferredKind.Flags, "flags : u16", InferenceConfidence.Medium),
            (0x1C, 16, InferredKind.AsciiString, "char[16]", InferenceConfidence.High),
            (0x2C, 4, InferredKind.Padding, "padding[4]", InferenceConfidence.High),
        ];
        Assert.Equal(expected, r.Fields.Select(f => (f.Offset, f.Size, f.Kind, f.Type.Display(null), f.Confidence)));
        Assert.Equal((0L, 1L), At(r, 0x04).Rule.Counter);
        Assert.Equal([1UL, 2, 3, 5], At(r, 0x18).Rule.Values);
        Assert.Equal([0x0001UL, 0x0004, 0x0080], At(r, 0x1A).Rule.Values);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-01")]
    public void Rec48_ten_fields_are_inferred()
    {
        TemplateInferenceResult r = Rec48(k: 48);
        AssertRec48Fields(r, output);
        Assert.False(r.BigEndian);
        Assert.Equal(["magic", "counter_04", "time_08", "offset_0C", "length_10", "value_14", "kind_18", "flags_1A", "name_1C", null],
            r.Fields.Select(f => f.Name));
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-02")]
    public void Rec48_big_endian_is_inferred_as_big_endian()
    {
        TemplateInferenceResult r = Rec48(big: true);
        Assert.Equal(48, r.RecordLength);
        AssertRec48Fields(r, output);
        Assert.True(r.BigEndian);
        string draft = InferenceTestSupport.Draft(new InferenceTable(r));
        Assert.Contains("#pragma endian big", draft, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\n\s+(le|be) ", draft);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-03")]
    public void Times_counter_and_utf16_names_are_inferred()
    {
        TemplateInferenceResult r = File("TD-TPL-INFER-TIMES");
        output.WriteLine(InferenceTestSupport.Dump(r));
        Assert.Equal(32, r.RecordLength);
        Assert.Equal((0x00, 8, InferredKind.Time, "filetime", InferenceConfidence.High), Summary(At(r, 0x00)));
        Assert.Equal((0x08, 8, InferredKind.Time, "time64_t", InferenceConfidence.High), Summary(At(r, 0x08)));
        Assert.Equal((0x10, 4, InferredKind.Time, "dosdatetime"), Short(At(r, 0x10)));
        Assert.Equal((0x14, 4, InferredKind.Counter, "u32"), Short(At(r, 0x14)));
        Assert.Equal((100L, 4L), At(r, 0x14).Rule.Counter);
        Assert.Equal((0x18, 8, InferredKind.Utf16String, "char16[4]"), Short(At(r, 0x18)));
        Assert.False(At(r, 0x18).Rule.BigEndian);
        Assert.Equal(5, r.Fields.Count);

        static (int, int, InferredKind, string, InferenceConfidence) Summary(InferredField f) => (f.Offset, f.Size, f.Kind, f.Type.Display(null), f.Confidence);
        static (int, int, InferredKind, string) Short(InferredField f) => (f.Offset, f.Size, f.Kind, f.Type.Display(null));
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-04")]
    public void Floats_integers_unknown_and_enums_are_told_apart()
    {
        TemplateInferenceResult r = File("TD-TPL-INFER-NUMS");
        output.WriteLine(InferenceTestSupport.Dump(r));
        Assert.Equal(24, r.RecordLength);
        Assert.Equal(
            [
                (0x00, 8, InferredKind.Float, "f64"),
                (0x08, 4, InferredKind.Integer, "u32"),
                (0x0C, 4, InferredKind.Unknown, "u8[4]"),
                (0x10, 1, InferredKind.Enum, "enum : u8"),
                (0x11, 7, InferredKind.Padding, "padding[7]"),
            ],
            r.Fields.Select(f => (f.Offset, f.Size, f.Kind, f.Type.Display(null))));
        Assert.Equal(InferenceConfidence.Low, At(r, 0x08).Confidence);
        Assert.Equal(InferenceConfidence.Low, At(r, 0x0C).Confidence);
        Assert.Equal(InferenceConfidence.Medium, At(r, 0x10).Confidence);
        Assert.Equal([7UL, 9, 12], At(r, 0x10).Rule.Values);
        Assert.Equal("unknown_0C", At(r, 0x0C).Name);
        Assert.Equal("Kind_10 : u8", InferenceRow.From(At(r, 0x10)).TypeDisplay);

        // f64 が整数に、整数が浮動小数点に推定されていない (ほかの候補の 1 位でもない)
        Assert.NotEqual(InferredKind.Integer, At(r, 0x00).Candidates[0].Kind);
        Assert.NotEqual(InferredKind.Float, At(r, 0x08).Candidates[0].Kind);
        Assert.DoesNotContain(At(r, 0x08).Candidates, c => c.Kind == InferredKind.Float);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-05")]
    public void Every_field_has_a_confidence_and_a_reason_with_numbers()
    {
        Func<InferenceMessage, string> text = InferenceTestSupport.Text("ja", "ja-JP");
        foreach (TemplateInferenceResult r in new[] { Rec48(k: 48), File("TD-TPL-INFER-TIMES"), File("TD-TPL-INFER-NUMS") })
        {
            Assert.NotEmpty(r.Fields);
            foreach (InferredField f in r.Fields)
            {
                Assert.True(Enum.IsDefined(f.Confidence));
                string reason = text(f.Rule.Reason);
                Assert.False(string.IsNullOrWhiteSpace(reason));
                Assert.Matches("[0-9]", reason);
                var row = InferenceRow.From(f);
                Assert.Equal(f.Confidence == InferenceConfidence.Low, row.IsLowConfidence);
            }
        }

        TemplateInferenceResult rec = Rec48(k: 48);
        string counter = text(At(rec, 0x04).Rule.Reason);
        Assert.Contains("1,000", counter, StringComparison.Ordinal);
        Assert.Contains("1 × レコード番号", counter, StringComparison.Ordinal);
        Assert.Contains("16 の倍数", text(At(rec, 0x0C).Rule.Reason), StringComparison.Ordinal);
        Assert.Contains(File("TD-TPL-INFER-NUMS").Fields, f => f.Confidence == InferenceConfidence.Low);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-06")]
    public void Reasons_follow_the_display_language_and_the_regional_format()
    {
        InferenceMessage reason = At(Rec48(k: 48), 0x04).Rule.Reason;
        string en = InferenceTestSupport.Text("en", "en-US")(reason);
        string ja = InferenceTestSupport.Text("ja", "ja-JP")(reason);
        string de = InferenceTestSupport.Text("en", "de-DE")(reason);
        output.WriteLine(en + "\n" + ja + "\n" + de);

        // どちらもリソースの同じキーから作る
        Assert.Equal("TplInfer_Reason_Counter", reason.Key);
        Assert.Contains("record number", en, StringComparison.Ordinal);
        Assert.Contains("レコード番号", ja, StringComparison.Ordinal);
        Assert.Contains("1,000", en, StringComparison.Ordinal);
        Assert.Contains("1.000", de, StringComparison.Ordinal);
        Assert.DoesNotContain("1,000", de, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-07")]
    public void Columns_with_values_beyond_the_document_are_not_offsets()
    {
        // 1. レコードの部分だけ (48,000 バイト。オフセットの値がドキュメントの長さ以上)
        byte[] records = InferenceTestSupport.Rec48()[..48_000];
        TemplateInferenceResult r = InferenceTestSupport.Infer(records, recordLength: 48);
        output.WriteLine(InferenceTestSupport.Dump(r));
        Assert.NotEqual(InferredKind.Offset, At(r, 0x0C).Kind);
        Assert.DoesNotContain(r.Fields.SelectMany(f => f.Candidates), c => c.Kind == InferredKind.Offset);
        Assert.DoesNotContain(r.Fields.Where(f => f.Offset == 0x10), f => f.Kind == InferredKind.Length);

        // 2. 1 レコードだけ値がドキュメントの長さを超える
        TemplateInferenceResult bad = File("TD-TPL-INFER-BADPTR", k: 16);
        output.WriteLine(InferenceTestSupport.Dump(bad));
        Assert.DoesNotContain(bad.Fields.Where(f => f.Offset <= 4 && f.End > 4), f => f.Kind == InferredKind.Offset);
        Assert.DoesNotContain(bad.Fields.Where(f => f.Offset <= 4 && f.End > 4).SelectMany(f => f.Candidates), c => c.Kind == InferredKind.Offset);
    }

    [Fact]
    [Trait(TC, "TC-TPL-39-08")]
    public void A_given_endianness_is_the_only_one_examined()
    {
        TemplateInferenceResult r = Rec48(k: 48, endian: InferenceEndian.Little, big: true);
        output.WriteLine(InferenceTestSupport.Dump(r));
        Assert.Equal(0, r.BigEndianEvaluations);
        Assert.NotEqual(InferredKind.Counter, At(r, 0x04).Kind);
        string draft = InferenceTestSupport.Draft(new InferenceTable(r));
        Assert.Contains("#pragma endian little", draft, StringComparison.Ordinal);

        // 自動ではビッグエンディアンも調べる
        Assert.True(Rec48(k: 48, big: true).BigEndianEvaluations > 0);
    }

    // ───────────── TPL-40 (Core) ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-40-02")]
    public void The_draft_has_no_syntax_errors_and_applies_to_1000_records()
    {
        byte[] data = InferenceTestSupport.Rec48();
        TemplateInferenceResult r = InferenceTestSupport.Infer(data, 0, 48_000);
        var table = new InferenceTable(r);
        string draft = InferenceTestSupport.Draft(table, language: "ja");
        output.WriteLine(draft);

        TemplateProgram program = TemplateLoader.LoadText(draft, "inferred.hxt");
        Assert.Empty(program.Diagnostics);
        TemplateResult result = Run(program, TemplateDataSource.FromBytes(data), new TemplateApplyOptions { StartOffset = 0 });
        Assert.Equal(TemplateRunState.Completed, result.State);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning);
        TemplateNode records = Node(result, "records");
        Assert.Equal(1000, records.Children.Count);
        Assert.Equal(999, Long(result, "records[999].counter_04"));
        foreach (int i in new[] { 0, 1, 999 })
        {
            TemplateNode rec = Node(result, $"records[{i}]");
            var children = rec.Children.ToList();
            Assert.Equal(table.Rows.Count, children.Count);
            for (int f = 0; f < table.Rows.Count; f++)
            {
                Assert.Equal((48L * i) + table.Rows[f].Offset, children[f].Offset);
                Assert.Equal(table.Rows[f].Size, children[f].Size);
            }
        }
    }

    [Fact]
    public void The_draft_matches_the_spec_example_shape()
    {
        var table = new InferenceTable(Rec48(k: 48));
        string draft = InferenceTestSupport.Draft(table, language: "ja");
        output.WriteLine(draft);
        Assert.StartsWith("// テンプレートの推定 (TPL-38) で作った下書きです。", draft, StringComparison.Ordinal);
        Assert.Contains("// 元: TD-TPL-INFER-REC48.bin、範囲 0x0〜0xBB7F、レコード長 48、レコード 1,000 個、2026-10-21", draft, StringComparison.Ordinal);
        Assert.Contains("#pragma name \"推定したレコード (TD-TPL-INFER-REC48.bin)\"", draft, StringComparison.Ordinal);
        Assert.Contains("enum Kind_18 : u16 {\n    v1 = 1,\n    v2 = 2,\n    v3 = 3,\n    v5 = 5,\n};", draft, StringComparison.Ordinal);
        Assert.Contains("flags Flags_1A : u16 {\n    bit0 = 0x0001,\n    bit2 = 0x0004,\n    bit7 = 0x0080,\n};", draft, StringComparison.Ordinal);
        Assert.Matches(@"u32\s+offset_0C\s+\[\[format\(""hex""\), comment\(""オフセット \(確度 高\): ", draft);
        Assert.Matches(@"char\s+magic\[4\]\s+\[\[comment\(""マジック \(確度 高\): ", draft);
        Assert.Contains("    padding[4];\n", draft, StringComparison.Ordinal);
        Assert.EndsWith("Record records[1000] @ 0x0;\n", draft, StringComparison.Ordinal);
        Assert.True(draft.IndexOf("enum Kind_18", StringComparison.Ordinal) < draft.IndexOf("struct Record", StringComparison.Ordinal));
    }

    [Fact]
    public void A_range_that_starts_later_is_placed_at_its_start()
    {
        byte[] data = [.. new byte[0x100], .. InferenceTestSupport.Rec48()];
        TemplateInferenceResult r = InferenceTestSupport.Infer(data, 0x100, 48_000, 48);
        var table = new InferenceTable(r);
        string draft = InferenceTestSupport.Draft(table);
        Assert.EndsWith("Record records[1000] @ 0x100;\n", draft, StringComparison.Ordinal);
        TemplateResult result = Run(TemplateLoader.LoadText(draft), TemplateDataSource.FromBytes(data), new TemplateApplyOptions { StartOffset = 0x100 });
        Assert.Equal(0x100 + (48 * 5) + 4, Node(result, "records[5].counter_04").Offset);
        Assert.Equal(5, Long(result, "records[5].counter_04"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-40-03")]
    public void Renaming_and_retyping_change_the_draft()
    {
        // TC-TPL-40-03 の表と下書きの部分 (結果ツリーと適用は UI の試験)
        byte[] data = InferenceTestSupport.Rec48();
        var table = new InferenceTable(InferenceTestSupport.Infer(data, 0, 48_000));
        int row = table.RowAt(0x04);
        Assert.True(table.Rename(row, "index"));
        Assert.True(table.SetType(row, "s32"));
        string draft = InferenceTestSupport.Draft(table);
        Assert.Matches(@"s32\s+index\s", draft);
        Assert.DoesNotContain("counter_04", draft, StringComparison.Ordinal);
        TemplateResult result = Run(TemplateLoader.LoadText(draft), TemplateDataSource.FromBytes(data));
        Assert.Equal("s32", Node(result, "records[0].index").TypeName);
        Assert.True(table.Rows[row].IsManual);
    }

    [Fact]
    public void Invalid_and_duplicate_names_are_rejected()
    {
        var table = new InferenceTable(Rec48(k: 48));
        int row = table.RowAt(0x04);
        Assert.Equal("TplInfer_Name_Invalid", table.ValidateName(row, "1abc")?.Key);
        Assert.Equal("TplInfer_Name_Invalid", table.ValidateName(row, "a b")?.Key);
        Assert.Equal("TplInfer_Name_Reserved", table.ValidateName(row, "struct")?.Key);
        Assert.Equal("TplInfer_Name_Reserved", table.ValidateName(row, "u32")?.Key);
        Assert.Equal("TplInfer_Name_Reserved", table.ValidateName(row, "Kind_18")?.Key);
        Assert.Equal("TplInfer_Name_Duplicate", table.ValidateName(row, "magic")?.Key);
        Assert.Equal("TplInfer_Name_Padding", table.ValidateName(table.RowAt(0x2C), "pad")?.Key);
        Assert.False(table.Rename(row, "magic"));
        Assert.Equal("counter_04", table.Rows[row].Name);
        Assert.False(table.CanUndo);
    }

    [Fact]
    [Trait(TC, "TC-TPL-40-04")]
    public void Merge_and_split_change_the_ranges_and_reinfer()
    {
        var table = new InferenceTable(Rec48(k: 48));
        int flags = table.RowAt(0x1A);
        Assert.True(table.MergeWithPrevious(flags));
        InferenceRow merged = table.Rows[table.RowAt(0x18)];
        Assert.Equal((0x18, 4), (merged.Offset, merged.Size));
        Assert.True(merged.IsManual);
        Assert.Equal(9, table.Rows.Count);
        string draft = InferenceTestSupport.Draft(table);
        Assert.Matches(@"\n    u32\s+\w+_18\s", draft);
        Assert.Empty(TemplateLoader.LoadText(draft).Diagnostics);

        Assert.True(table.Split(table.RowAt(0x18), 2));
        Assert.Equal(10, table.Rows.Count);
        InferenceRow a = table.Rows[table.RowAt(0x18)], b = table.Rows[table.RowAt(0x1A)];
        Assert.Equal((0x18, 2, InferredKind.Enum), (a.Offset, a.Size, a.Kind));
        Assert.Equal((0x1A, 2, InferredKind.Flags), (b.Offset, b.Size, b.Kind));
        Assert.Contains("enum Kind_18 : u16", InferenceTestSupport.Draft(table), StringComparison.Ordinal);
        Assert.Contains("flags Flags_1A : u16", InferenceTestSupport.Draft(table), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-TPL-40-05")]
    public void Manual_names_and_types_survive_reinference()
    {
        TemplateInferenceResult first = Rec48(k: 48);
        var table = new InferenceTable(first);
        Assert.True(table.Rename(table.RowAt(0x04), "index"));
        Assert.True(table.SetType(table.RowAt(0x14), "u32"));
        table.Load(Rec48(k: 48), keepManual: true);
        InferenceRow index = table.Rows[table.RowAt(0x04)], value = table.Rows[table.RowAt(0x14)];
        Assert.Equal(("index", true), (index.Name, index.IsManual));
        Assert.Equal(("u32", true), (value.Type.Id, value.IsManual));
        Assert.All(table.Rows.Where(r => r.Offset is not (0x04 or 0x14)), r => Assert.False(r.IsManual));
        Assert.Equal(first.Fields.Where(f => f.Offset is not (0x04 or 0x14)).Select(f => (f.Offset, f.Name, f.Type.Id)),
            table.Rows.Where(r => r.Offset is not (0x04 or 0x14)).Select(r => (r.Offset, r.Name, r.Type.Id)));
    }

    [Fact]
    [Trait(TC, "TC-TPL-40-08")]
    public void Table_changes_can_be_undone_and_redone()
    {
        // TC-TPL-40-08 の表の部分 (Ctrl+Z / Ctrl+Y のキーとドキュメントの履歴は UI の試験)
        var table = new InferenceTable(Rec48(k: 48));
        int row = table.RowAt(0x04);
        Assert.True(table.Rename(row, "index"));
        Assert.True(table.Undo());
        Assert.Equal("counter_04", table.Rows[row].Name);
        Assert.True(table.Redo());
        Assert.Equal("index", table.Rows[row].Name);
        Assert.False(table.CanRedo);
    }

    [Fact]
    public void Type_choices_include_the_candidates_and_types_that_fit_the_size()
    {
        var table = new InferenceTable(Rec48(k: 48));
        IReadOnlyList<InferenceTypeChoice> choices = table.TypeChoices(table.RowAt(0x04));
        Assert.Equal("u32", choices[0].Type.Id);
        Assert.Contains(choices, c => c.Type.Id == "s32");
        Assert.Contains(choices, c => c.Type.Id == "f32");
        Assert.Contains(choices, c => c.Type.Id == "time32_t");
        Assert.Contains(choices, c => c.Type.Id == "padding");
        Assert.DoesNotContain(choices, c => c.Type.Id == "u16");
        Assert.True(table.SetType(table.RowAt(0x04), "padding"));
        Assert.Null(table.Rows[table.RowAt(0x04)].Name);
        Assert.Contains("    padding[4];\n    time32_t", InferenceTestSupport.Draft(table), StringComparison.Ordinal);
    }

    // ───────────── 頑健さ (乱数のデータ) ─────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Random_inputs_never_throw_and_finish_quickly(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 6; round++)
        {
            int length = random.Next(32, 200_000);
            byte[] data = new byte[length];
            random.NextBytes(data);

            // 一部をレコードらしくする (定数・0・連番を混ぜる)
            int k = random.Next(2, 300);
            for (int i = 0; i + k <= data.Length; i += k)
            {
                if (random.Next(3) == 0)
                {
                    data[i] = 0;
                }

                if (k > 4 && random.Next(2) == 0)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(i + 1, 2), (ushort)(i / k));
                }
            }

            int? given = random.Next(3) == 0 ? random.Next(1, Math.Max(2, length / 4)) : null;
            var endian = (InferenceEndian)random.Next(3);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TemplateInferenceResult r = InferenceTestSupport.Infer(data, 0, length, given, endian);
            // 止まらない・桁違いに遅くないこと (目標の 2 秒は警告。テスト方針 6.6)
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed} (length {length}, k {given})");
            Support.TimeBudget.Limit(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed} (length {length}, k {given})");
            if (r.HasFields)
            {
                Assert.Equal(r.RecordLength, r.Fields.Sum(f => f.Size));
                Assert.True(r.Fields.Zip(r.Fields.Skip(1)).All(p => p.First.End == p.Second.Offset));
                var table = new InferenceTable(r);
                string draft = InferenceTestSupport.Draft(table);
                Assert.DoesNotContain(TemplateLoader.LoadText(draft).Diagnostics, d => d.Severity == TemplateSeverity.Error);

                // 結合と分割を試す
                if (table.Rows.Count > 1)
                {
                    Assert.True(table.MergeWithPrevious(table.Rows.Count - 1));
                }

                int split = table.Rows.ToList().FindIndex(x => x.Size >= 2);
                if (split >= 0)
                {
                    Assert.True(table.Split(split, 1));
                }

                Assert.Equal(r.RecordLength, table.Rows.Sum(x => x.Size));
                Assert.DoesNotContain(TemplateLoader.LoadText(InferenceTestSupport.Draft(table)).Diagnostics, d => d.Severity == TemplateSeverity.Error);
            }
        }
    }

    [Fact]
    public void Large_records_read_at_most_17_MiB()
    {
        // レコード長 65,536 の 1 GiB の仮想のデータ: 標本は 16 MiB まで
        var source = new VirtualByteSource(1L << 30, VirtualContent.Offset64);
        using Document doc = InferenceTestSupport.Doc(source);
        TemplateInferenceResult r = TemplateInference.Run(doc.Current, new TemplateInferenceRequest { Start = 0, Length = 1L << 30, RecordLength = 65_536 });
        Assert.True(r.BytesRead <= 17L * 1024 * 1024, r.BytesRead.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(65_536, r.Fields.Sum(f => f.Size));
    }
}
