using System.Globalization;
using System.Text;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>大文字・小文字の変換 (EDIT-39) の部品。</summary>
public sealed class CaseConversionTests
{
    private const int MiB = 1024 * 1024;

    static CaseConversionTests()
    {
        _ = TextEncoding.Ascii;
    }

    private static Document Doc(byte[] data, bool fixedLength = false) => fixedLength
        ? new(new MemoryByteSource(data, capabilities: SourceCapabilities.CanWrite), Options())
        : new(new MemoryByteSource(data), Options());

    private static byte[] Convert(byte[] data, CaseOperation operation, CaseConversionMode mode, string encoding = "utf-8")
    {
        using Document doc = Doc(data);
        RangeReplacement part = CaseConversion.Convert(doc.Current, new TargetRange(0, data.Length), operation, mode, encoding,
            () => ContentSink.For(doc));
        using (part.Content)
        {
            return part.Content.Preview(int.MaxValue);
        }
    }

    // ---- TC-EDIT-39-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-39-01")]
    public void Ascii_only_upper_changes_only_ascii_letters()
    {
        Assert.Equal(new byte[] { 0x41, 0xE9, 0x5A }, Convert([0x61, 0xE9, 0x7A], CaseOperation.Upper, CaseConversionMode.AsciiOnly));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-39-01")]
    public void Encoding_aware_upper_of_utf8_e_acute()
    {
        Assert.Equal(new byte[] { 0xC3, 0x89 }, Convert([0xC3, 0xA9], CaseOperation.Upper, CaseConversionMode.EncodingAware));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-39-01")]
    public void Encoding_aware_swap_keeps_undecodable_bytes()
    {
        Assert.Equal(new byte[] { 0x61, 0x42, 0xFF, 0x43 },
            Convert([0x41, 0x62, 0xFF, 0x63], CaseOperation.Swap, CaseConversionMode.EncodingAware));
    }

    // ---- TC-EDIT-39-02 ----

    [Theory]
    [Trait(TC, "TC-EDIT-39-02")]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    public void Dotted_i_is_culture_independent(string culture)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        CultureInfo beforeUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.Equal(new byte[] { 0x49 }, Convert([0x69], CaseOperation.Upper, CaseConversionMode.EncodingAware));
            Assert.Equal(new byte[] { 0x69 }, Convert([0x49], CaseOperation.Lower, CaseConversionMode.EncodingAware));
            Assert.Equal(new byte[] { 0x49, 0x69 }, Convert([0x69, 0x49], CaseOperation.Swap, CaseConversionMode.EncodingAware));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
            CultureInfo.CurrentUICulture = beforeUi;
        }
    }

    // ---- ASCII の英字だけ ----

    [Fact]
    public void Ascii_only_vectorised_code_matches_the_byte_rule_for_every_length()
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        foreach (CaseOperation operation in Enum.GetValues<CaseOperation>())
        {
            for (int length = 0; length <= 300; length++)
            {
                byte[] data = Enumerable.Range(0, length).Select(i => all[(i * 7 + length) % 256]).ToArray();
                byte[] expected = data.Select(b => CaseConversion.Map(b, operation)).ToArray();
                CaseConversion.ConvertAscii(data, operation);
                Assert.Equal(expected, data);
            }
        }

        Assert.Equal((byte)'A', CaseConversion.Map((byte)'a', CaseOperation.Upper));
        Assert.Equal((byte)'@', CaseConversion.Map((byte)'@', CaseOperation.Swap));
        Assert.Equal((byte)'[', CaseConversion.Map((byte)'[', CaseOperation.Lower));
        Assert.Equal((byte)'z', CaseConversion.Map((byte)'Z', CaseOperation.Swap));
        Assert.Equal((byte)0xE9, CaseConversion.Map((byte)0xE9, CaseOperation.Upper));
    }

    [Fact]
    public void Ascii_only_large_range_uses_a_temp_file_and_keeps_the_length()
    {
        byte[] data = new byte[3 * MiB + 7];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 128);
        }

        Document doc = Doc(data, fixedLength: true);
        string folder = Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));
        try
        {
            IReadOnlyList<RangeReplacement> parts = CaseConversion.ConvertAll(doc, SelectionRanges.Single(0, data.Length), CaseOperation.Swap,
                CaseConversionMode.AsciiOnly, "utf-8");
            Assert.True(parts[0].Content.HasTemporaryFile);
            Assert.False(TextTransforms.ChangesLength(parts));
            Assert.False(CaseConversion.MayChangeLength(CaseConversionMode.AsciiOnly));
            TransformApplier.Apply(doc, parts, "大文字と小文字を入れ替える");
            byte[] expected = data.Select(b => CaseConversion.Map(b, CaseOperation.Swap)).ToArray();
            Assert.True(expected.AsSpan().SequenceEqual(ReadAll(doc.Current)));
            doc.Undo();
            Assert.True(data.AsSpan().SequenceEqual(ReadAll(doc.Current)));
        }
        finally
        {
            doc.Dispose();
        }

        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder, "transform-*.bin").Length == 0);
    }

    [Fact]
    public void Cancel_leaves_no_temp_file()
    {
        byte[] data = new byte[3 * MiB];
        using Document doc = Doc(data);
        string folder = Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));
        var op = new LongRunningOperation("case", OperationKind.ModifiesDocument, doc, null, TimeProvider.System);
        int created = 0;
        ContentSink Create()
        {
            if (++created == 2)
            {
                op.Cancel();
            }

            return ContentSink.For(doc);
        }

        Assert.ThrowsAny<OperationCanceledException>(() => CaseConversion.ConvertAll(doc.Current,
            [new TargetRange(0, 2 * MiB), new TargetRange(2 * MiB, MiB)], CaseOperation.Upper, CaseConversionMode.EncodingAware, "utf-8", Create, op));
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder, "transform-*.bin").Length == 0);
        Assert.Equal(0, doc.History.CurrentIndex);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-39-01")]
    public void Ascii_only_conversion_reports_whether_anything_changed()
    {
        // 英字のない範囲・すでに大文字の範囲は変わらない (反映せず、元に戻す操作を作らない)。ベクトルの幅を超える長さも調べる。
        byte[] digits = Encoding.ASCII.GetBytes(new string('7', 100));
        Assert.False(CaseConversion.ConvertAscii(digits.ToArray(), CaseOperation.Upper));
        Assert.False(CaseConversion.ConvertAscii(Encoding.ASCII.GetBytes(new string('A', 70)), CaseOperation.Upper));
        byte[] tail = Encoding.ASCII.GetBytes(new string('A', 70) + "z");
        Assert.True(CaseConversion.ConvertAscii(tail, CaseOperation.Upper));
        Assert.Equal((byte)'Z', tail[^1]);

        using Document doc = Doc([.. digits, 0xE9]);
        var unchanged = new CaseConversionStats();
        TransformApplier.DisposeAll(CaseConversion.ConvertAll(doc, SelectionRanges.Single(0, doc.Length), CaseOperation.Lower,
            CaseConversionMode.AsciiOnly, "utf-8", stats: unchanged));
        Assert.False(unchanged.Changed);

        var changed = new CaseConversionStats();
        TransformApplier.DisposeAll(CaseConversion.ConvertAll(doc, SelectionRanges.Single(0, doc.Length), CaseOperation.Swap,
            CaseConversionMode.AsciiOnly, "utf-8", stats: changed));
        Assert.False(changed.Changed);
        using Document letters = Doc(Encoding.ASCII.GetBytes("abc"));
        TransformApplier.DisposeAll(CaseConversion.ConvertAll(letters, SelectionRanges.Single(0, 3), CaseOperation.Swap,
            CaseConversionMode.AsciiOnly, "utf-8", stats: changed));
        Assert.True(changed.Changed);
    }

    // ---- 文字コードに従う ----

    [Fact]
    public void Encoding_aware_length_change_is_refused_on_fixed_length_documents()
    {
        // ı (C4 B1) の大文字は I (1 バイト)。
        byte[] data = [0x61, 0xC4, 0xB1];
        using Document doc = Doc(data, fixedLength: true);
        IReadOnlyList<RangeReplacement> parts = CaseConversion.ConvertAll(doc, SelectionRanges.Single(0, 3), CaseOperation.Upper,
            CaseConversionMode.EncodingAware, "utf-8");
        Assert.Equal(new byte[] { 0x41, 0x49 }, parts[0].Content.Preview(16));
        Assert.True(TextTransforms.ChangesLength(parts));
        Assert.True(CaseConversion.MayChangeLength(CaseConversionMode.EncodingAware));
        Assert.Throws<FixedLengthException>(() => TransformApplier.Apply(doc, parts, "大文字に"));
        Assert.Equal(data, ReadAll(doc.Current));

        using Document resizable = Doc(data);
        TransformApplier.Apply(resizable, CaseConversion.ConvertAll(resizable, SelectionRanges.Single(0, 3), CaseOperation.Upper,
            CaseConversionMode.EncodingAware, "utf-8"), "大文字に");
        Assert.Equal(new byte[] { 0x41, 0x49 }, ReadAll(resizable.Current));
    }

    [Fact]
    public void Encoding_aware_keeps_characters_whose_converted_form_is_not_in_the_encoding()
    {
        // Latin-1 (28591) に Ÿ (U+0178) はないので ÿ は変えない。1252 では 9F になる。
        Assert.Equal(new byte[] { 0x41, 0xFF, 0xC9 }, Convert([0x61, 0xFF, 0xE9], CaseOperation.Upper, CaseConversionMode.EncodingAware, "cp28591"));
        Assert.Equal(new byte[] { 0x41, 0x9F, 0xC9 }, Convert([0x61, 0xFF, 0xE9], CaseOperation.Upper, CaseConversionMode.EncodingAware, "cp1252"));
    }

    [Fact]
    public void Encoding_aware_handles_surrogate_pairs_and_shift_jis()
    {
        // 𐐨 (U+10428、Deseret の小文字) の大文字は 𐐀 (U+10400)。
        Assert.Equal(Encoding.UTF8.GetBytes("\U00010400A"), Convert(Encoding.UTF8.GetBytes("\U00010428a"), CaseOperation.Upper,
            CaseConversionMode.EncodingAware));
        Assert.Equal(Encoding.Unicode.GetBytes("\U00010400A"), Convert(Encoding.Unicode.GetBytes("\U00010428a"), CaseOperation.Upper,
            CaseConversionMode.EncodingAware, "utf-16le"));

        // Shift_JIS の全角英字 (ａ = 82 81) は Ａ (82 60) になる。2 バイト目が 'a' (61) などでも変えない。
        Assert.Equal(new byte[] { 0x82, 0x60, 0x41, 0x83, 0x61 },
            Convert([0x82, 0x81, 0x61, 0x83, 0x61], CaseOperation.Upper, CaseConversionMode.EncodingAware, "cp932"));
    }

    [Fact]
    public void Encoding_aware_across_chunk_boundaries()
    {
        // é (C3 A9) が 1 MiB の境界をまたぐよう、先頭に 1 バイトずらして並べる。
        var builder = new StringBuilder("x");
        while (builder.Length < 2 * MiB / 2 + 10)
        {
            builder.Append("éa");
        }

        string text = builder.ToString();
        byte[] data = Encoding.UTF8.GetBytes(text);
        byte[] expected = Encoding.UTF8.GetBytes(text.ToUpperInvariant());
        byte[] result = Convert(data, CaseOperation.Upper, CaseConversionMode.EncodingAware);
        Assert.True(expected.AsSpan().SequenceEqual(result));
    }
}
