using System.Text;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>文字コード変換 (EDIT-38) の部品。</summary>
public sealed class CharsetConversionTests
{
    private const int MiB = 1024 * 1024;

    static CharsetConversionTests()
    {
        // コードページ (Shift_JIS、EBCDIC など) を使えるようにする。
        _ = TextEncoding.Ascii;
    }

    private static Document Doc(byte[] data, bool fixedLength = false) => fixedLength
        ? new(new MemoryByteSource(data, capabilities: SourceCapabilities.CanWrite), Options())
        : new(new MemoryByteSource(data), Options());

    private static RangeReplacement ConvertWhole(Document doc, CharsetConversionOptions options, LongRunningOperation? op = null) =>
        CharsetConverter.Convert(doc.Current, new TargetRange(0, doc.Length), options, () => ContentSink.For(doc), op);

    private static byte[] Convert(byte[] data, CharsetConversionOptions options)
    {
        using Document doc = Doc(data);
        RangeReplacement part = ConvertWhole(doc, options);
        using (part.Content)
        {
            return part.Content.Preview(int.MaxValue);
        }
    }

    private static string TempFolder(Document doc) => Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));

    private static int TransformFiles(Document doc) =>
        Directory.Exists(TempFolder(doc)) ? Directory.GetFiles(TempFolder(doc), "transform-*.bin").Length : 0;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- TC-EDIT-38-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-38-01")]
    public void Shift_jis_to_utf8()
    {
        byte[] result = Convert([0x93, 0xFA, 0x96, 0x7B], new CharsetConversionOptions("cp932", "utf-8"));
        Assert.Equal(new byte[] { 0xE6, 0x97, 0xA5, 0xE6, 0x9C, 0xAC }, result);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-38-01")]
    public void Ascii_to_ebcdic_037()
    {
        Assert.Equal(new byte[] { 0xC1 }, Convert([0x41], new CharsetConversionOptions("ascii", "cp37")));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-38-01")]
    public void Unmappable_character_is_an_error_by_default_and_the_data_does_not_change()
    {
        byte[] data = [0x41, 0xE6, 0x97, 0xA5, 0x42];
        using Document doc = Doc(data);
        var ex = Assert.Throws<CharsetConversionException>(() =>
            CharsetConverter.ConvertAll(doc, SelectionRanges.Single(0, data.Length), new CharsetConversionOptions("utf-8", "ascii")));
        Assert.Equal(CharsetConversionErrorKind.UnmappableTarget, ex.Kind);
        Assert.Equal(1, ex.Offset);
        Assert.Equal("日", ex.Text);
        Assert.Equal(data, ReadAll(doc.Current));
        Assert.Equal(0, doc.History.CurrentIndex);
        Assert.Equal(0, TransformFiles(doc));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-38-01")]
    public void Unmappable_character_replaced_with_question_mark()
    {
        byte[] result = Convert([0x41, 0xE6, 0x97, 0xA5, 0x42],
            new CharsetConversionOptions("utf-8", "ascii") { Unmappable = UnmappableHandling.Question });
        Assert.Equal(new byte[] { 0x41, 0x3F, 0x42 }, result);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-38-01")]
    public void Source_bom_is_removed_and_no_target_bom_by_default()
    {
        Assert.Equal(new byte[] { 0x41, 0x00 }, Convert([0xEF, 0xBB, 0xBF, 0x41], new CharsetConversionOptions("utf-8", "utf-16le")));
    }

    // ---- TC-EDIT-38-02 ----

    /// <summary>
    /// TD-EDIT-UTF8-10M: 10 MiB の UTF-8 のテキスト (決まった内容)。1〜4 バイトの文字を擬似乱数で並べ、1 MiB の境界ごとに、2・3・4 バイトの
    /// 文字が境界をまたぐように置く (またぐ位置も境界ごとに変える)。
    /// </summary>
    private static byte[] TdEditUtf8_10M()
    {
        const int total = 10 * MiB;
        byte[][] chars = [[0x61], [0xC3, 0xA9], [0xE6, 0x97, 0xA5], [0xF0, 0x9F, 0x98, 0x80], [0x0A], [0xD0, 0x96], [0xEF, 0xBD, 0xB1]];
        byte[] data = new byte[total];
        ulong state = 0x9E3779B97F4A7C15UL;
        int p = 0;
        while (p < total)
        {
            long boundary = ((long)p / MiB + 1) * MiB;
            int k = (int)(boundary / MiB);
            int length = 2 + k % 3;
            int split = 1 + k % (length - 1);
            long target = boundary - split;
            byte[] next;
            if (boundary < total && p == target)
            {
                next = length switch { 2 => chars[1], 3 => chars[2], _ => chars[3] };
            }
            else
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                next = chars[(int)(state % (ulong)chars.Length)];
                bool nearBoundary = boundary < total && p < target && p + next.Length > target - 4;
                if (nearBoundary || p + next.Length > total)
                {
                    next = chars[0];
                }
            }

            next.CopyTo(data, p);
            p += next.Length;
        }

        return data;
    }

    [Fact]
    [Trait(TC, "TC-EDIT-38-02")]
    public void Ten_mebibytes_of_utf8_round_trip_through_utf16le()
    {
        byte[] data = TdEditUtf8_10M();
        for (int k = 1; k < 10; k++)
        {
            // 境界の直後が UTF-8 の継続バイト (境界をまたぐ文字がある)。
            Assert.Equal(0x80, data[k * MiB] & 0xC0);
        }

        string text = Encoding.UTF8.GetString(data);
        Assert.DoesNotContain('�', text);
        byte[] expected = Encoding.Unicode.GetBytes(text);

        Document doc = Doc(data);
        try
        {
            RangeReplacement toUtf16 = ConvertWhole(doc, new CharsetConversionOptions("utf-8", "utf-16le"));

            // 1 MiB を超える結果は一時ファイルに書く。
            Assert.True(toUtf16.Content.HasTemporaryFile);
            Assert.Equal(1, TransformFiles(doc));
            Assert.Equal(expected.Length, toUtf16.Content.Length);
            Assert.True(expected.AsSpan().SequenceEqual(toUtf16.Content.Preview(int.MaxValue)));

            TransformApplier.Apply(doc, [toUtf16], "utf-16");
            Assert.Equal(expected.Length, doc.Length);

            RangeReplacement back = ConvertWhole(doc, new CharsetConversionOptions("utf-16le", "utf-8"));
            using (back.Content)
            {
                Assert.Equal(data.Length, back.Content.Length);
                Assert.True(data.AsSpan().SequenceEqual(back.Content.Preview(int.MaxValue)));
            }
        }
        finally
        {
            doc.Dispose();
        }

        Assert.Equal(0, TransformFiles(doc));
    }

    // ---- 扱いの設定 ----

    [Fact]
    public void Undecodable_bytes_are_an_error_by_default_with_the_offset()
    {
        var ex = Assert.Throws<CharsetConversionException>(() =>
            Convert([0x41, 0x42, 0xFF, 0x43], new CharsetConversionOptions("utf-8", "utf-16le")));
        Assert.Equal(CharsetConversionErrorKind.UndecodableSource, ex.Kind);
        Assert.Equal(2, ex.Offset);
        Assert.Equal(new byte[] { 0xFF }, ex.Bytes);
    }

    [Fact]
    public void Undecodable_bytes_replaced_with_fffd()
    {
        byte[] result = Convert([0x41, 0xFF, 0x42], new CharsetConversionOptions("utf-8", "utf-8") { InvalidSource = InvalidSourceHandling.ReplaceWithFffd });
        Assert.Equal(new byte[] { 0x41, 0xEF, 0xBF, 0xBD, 0x42 }, result);
    }

    [Fact]
    public void Keep_bytes_leaves_undecodable_bytes_and_converts_the_text_around_them()
    {
        var options = new CharsetConversionOptions("utf-8", "utf-16le") { InvalidSource = InvalidSourceHandling.KeepBytes };
        Assert.Equal(new byte[] { 0x41, 0x00, 0xFF, 0x42, 0x00 }, Convert([0x41, 0xFF, 0x42], options));

        // 不完全な文字 (E6 97) の後に文字が続く、範囲の末尾が不完全な文字。
        Assert.Equal(new byte[] { 0x41, 0x00, 0xE6, 0x97, 0x42, 0x00, 0xE6 }, Convert([0x41, 0xE6, 0x97, 0x42, 0xE6], options));

        // Shift_JIS の不正な 2 バイト目。
        byte[] sjis = [0x41, 0x81, 0x20, 0x93, 0xFA, 0x42];
        Assert.Equal(new byte[] { 0x41, 0x81, 0x20, 0xE6, 0x97, 0xA5, 0x42 },
            Convert(sjis, new CharsetConversionOptions("cp932", "utf-8") { InvalidSource = InvalidSourceHandling.KeepBytes }));
        var ex = Assert.Throws<CharsetConversionException>(() => Convert(sjis, new CharsetConversionOptions("cp932", "utf-8")));
        Assert.Equal(1, ex.Offset);
    }

    [Fact]
    public void Keep_bytes_across_chunk_boundaries_round_trips_utf8()
    {
        // UTF-8 → UTF-8 で解読できないバイト列をそのまま残すと、元と同じになる。
        byte[] data = new byte[3 * MiB + 100];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)('a' + i % 26);
        }

        void Put(int at, params byte[] bytes) => bytes.CopyTo(data, at);
        Put(MiB - 2, 0xE6, 0x97);                // 境界の手前で切れた不完全な文字 (次は ASCII)
        Put(2 * MiB - 1, 0xE6, 0x97, 0xA5);      // 境界をまたぐ正しい文字
        Put(2 * MiB + 10, 0xFF, 0xFE, 0xC3);     // 連続する不正なバイト
        Put(3 * MiB - 1, 0xF0, 0x9F, 0x98);      // 境界をまたぐ不完全な 4 バイトの文字
        Put(data.Length - 2, 0xC3, 0xA9);

        byte[] result = Convert(data, new CharsetConversionOptions("utf-8", "utf-8") { InvalidSource = InvalidSourceHandling.KeepBytes });
        Assert.True(data.AsSpan().SequenceEqual(result));

        var ex = Assert.Throws<CharsetConversionException>(() => Convert(data, new CharsetConversionOptions("utf-8", "utf-16le")));
        Assert.Equal(MiB - 2, ex.Offset);
        Assert.Equal(new byte[] { 0xE6, 0x97 }, ex.Bytes);
    }

    [Fact]
    public void Utf16_lone_surrogate_across_a_chunk_boundary_has_the_right_offset()
    {
        byte[] data = new byte[2 * MiB];
        for (int i = 0; i < data.Length; i += 2)
        {
            data[i] = (byte)'x';
        }

        // 1 MiB の境界の直前の単位が上位サロゲートだけ (次は 'x')。
        data[MiB - 2] = 0x00;
        data[MiB - 1] = 0xD8;
        var ex = Assert.Throws<CharsetConversionException>(() => Convert(data, new CharsetConversionOptions("utf-16le", "utf-8")));
        Assert.Equal(CharsetConversionErrorKind.UndecodableSource, ex.Kind);
        Assert.Equal(MiB - 2, ex.Offset);

        byte[] kept = Convert(data, new CharsetConversionOptions("utf-16le", "utf-16le") { InvalidSource = InvalidSourceHandling.KeepBytes });
        Assert.True(data.AsSpan().SequenceEqual(kept));
    }

    [Fact]
    public void Unmappable_character_offset_in_a_later_chunk()
    {
        byte[] data = new byte[3 * MiB];
        data.AsSpan().Fill((byte)'a');
        int at = 2 * MiB + MiB / 2;
        Utf8("日").CopyTo(data, at);
        Utf8("é").CopyTo(data, 2 * MiB - 1);
        var ex = Assert.Throws<CharsetConversionException>(() => Convert(data, new CharsetConversionOptions("utf-8", "cp1252")));
        Assert.Equal(at, ex.Offset);
        Assert.Equal("日", ex.Text);

        // 境界をまたぐ文字が表せない場合も、その文字の先頭の位置。
        var ex2 = Assert.Throws<CharsetConversionException>(() => Convert(data, new CharsetConversionOptions("utf-8", "ascii")));
        Assert.Equal(2 * MiB - 1, ex2.Offset);
        Assert.Equal("é", ex2.Text);
    }

    [Fact]
    public void Custom_replacement_for_unmappable_characters()
    {
        var options = new CharsetConversionOptions("utf-8", "ascii") { Unmappable = UnmappableHandling.Custom, CustomReplacement = "*" };
        Assert.Equal(new byte[] { 0x41, 0x2A, 0x42 }, Convert(Utf8("A日B"), options));

        // 変換先で表せない置き換えの文字列は使えない (UI が地域化した文言を出せるよう、誤りの種類と文字列を持つ)。
        var ex = Assert.Throws<CharsetSettingsException>(() => Convert(Utf8("A日B"), options with { CustomReplacement = "日" }));
        Assert.Equal(CharsetSettingsError.InvalidReplacement, ex.Error);
        Assert.Equal("日", ex.Value);
    }

    [Fact]
    public void No_best_fit_replacement()
    {
        // 1252 には Ā がない (best fit なら A になる)。
        var options = new CharsetConversionOptions("utf-8", "cp1252") { Unmappable = UnmappableHandling.Question };
        Assert.Equal(new byte[] { 0x3F, 0xE9 }, Convert(Utf8("Āé"), options));
    }

    [Fact]
    public void Bom_add_and_keep()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x41, 0x00 },
            Convert(Utf8("A"), new CharsetConversionOptions("utf-8", "utf-16le") { AddTargetBom = true }));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 },
            Convert([0xFF, 0xFE, 0x41, 0x00], new CharsetConversionOptions("utf-16le", "utf-8") { AddTargetBom = true }));
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x41, 0x00 },
            Convert([0xEF, 0xBB, 0xBF, 0x41], new CharsetConversionOptions("utf-8", "utf-16le") { StripSourceBom = false }));

        // 先頭以外の U+FEFF は取り除かない。BOM のない文字コードには付けない。
        Assert.Equal(new byte[] { 0x41, 0x00, 0xFF, 0xFE }, Convert([0x41, 0xEF, 0xBB, 0xBF], new CharsetConversionOptions("utf-8", "utf-16le")));
        Assert.Equal(new byte[] { 0xC1 }, Convert([0x41], new CharsetConversionOptions("ascii", "cp37") { AddTargetBom = true }));
    }

    [Theory]
    [InlineData(NewlineConversion.CrLf, "x\r\ny\r\nz\r\nw\r\n")]
    [InlineData(NewlineConversion.Lf, "x\ny\nz\nw\n")]
    [InlineData(NewlineConversion.Cr, "x\ry\rz\rw\r")]
    [InlineData(NewlineConversion.Keep, "x\ry\nz\r\nw\r")]
    public void Newline_conversion(NewlineConversion newline, string expected)
    {
        byte[] result = Convert(Utf8("x\ry\nz\r\nw\r"), new CharsetConversionOptions("utf-8", "utf-8") { Newline = newline });
        Assert.Equal(expected, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Crlf_split_by_a_chunk_boundary_is_one_newline()
    {
        byte[] data = new byte[MiB + 2];
        data.AsSpan().Fill((byte)'a');
        data[MiB - 1] = (byte)'\r';
        data[MiB] = (byte)'\n';
        byte[] result = Convert(data, new CharsetConversionOptions("utf-8", "utf-8") { Newline = NewlineConversion.Lf });
        Assert.Equal(data.Length - 1, result.Length);
        Assert.Equal((byte)'\n', result[MiB - 1]);
        Assert.Equal((byte)'a', result[MiB]);

        byte[] crlf = Convert(data, new CharsetConversionOptions("utf-8", "utf-8") { Newline = NewlineConversion.CrLf });
        Assert.True(data.AsSpan().SequenceEqual(crlf));
    }

    [Theory]
    [InlineData(UnicodeNormalization.Nfc, NormalizationForm.FormC)]
    [InlineData(UnicodeNormalization.Nfd, NormalizationForm.FormD)]
    [InlineData(UnicodeNormalization.Nfkc, NormalizationForm.FormKC)]
    [InlineData(UnicodeNormalization.Nfkd, NormalizationForm.FormKD)]
    public void Normalization_across_chunk_boundaries_matches_whole_text_normalization(UnicodeNormalization normalization, NormalizationForm form)
    {
        var builder = new List<byte>(3 * MiB + 64);
        void Fill(int until)
        {
            while (builder.Count < until)
            {
                builder.Add((byte)('a' + builder.Count % 26));
            }
        }

        // 境界 1: e + 結合アキュート (CC 81) の結合文字が次のチャンクの先頭。
        Fill(MiB - 1);
        builder.AddRange(Utf8("éx"));

        // 境界 2: é (合成済み) が境界をまたぎ、次のチャンクに結合文字が続く。ハングルの字母 (ᄀ + ᅡ) も。
        Fill(2 * MiB - 1);
        builder.AddRange(Utf8("ẹ́́ᄀ"));
        Fill(2 * MiB + 40);
        builder.AddRange(Utf8("각ｶﾞ①ﬁ"));

        // 境界 3: 字母が境界をまたいで続く。半角カナの濁点が次のチャンクの先頭。
        Fill(3 * MiB - 4);
        builder.AddRange(Utf8("ᄀ"));
        builder.AddRange(Utf8("ᅡｶ"));
        Fill(3 * MiB + 2);
        builder.AddRange(Utf8("ﾞA"));
        byte[] data = [.. builder];

        byte[] expected = Utf8(Encoding.UTF8.GetString(data).Normalize(form));
        byte[] result = Convert(data, new CharsetConversionOptions("utf-8", "utf-8") { Normalization = normalization });
        Assert.Equal(expected.Length, result.Length);
        Assert.True(expected.AsSpan().SequenceEqual(result));
    }

    [Fact]
    public void Nfc_composes_a_combining_mark_at_the_start_of_the_next_chunk()
    {
        byte[] data = new byte[MiB + 3];
        data.AsSpan().Fill((byte)'a');
        data[MiB - 1] = (byte)'e';
        data[MiB] = 0xCC;
        data[MiB + 1] = 0x81;
        data[MiB + 2] = (byte)'x';
        byte[] result = Convert(data, new CharsetConversionOptions("utf-8", "utf-8") { Normalization = UnicodeNormalization.Nfc });
        Assert.Equal(MiB + 2, result.Length);
        Assert.Equal(new byte[] { 0xC3, 0xA9, (byte)'x' }, result[(MiB - 1)..]);
    }

    // ---- マルチ選択・固定長・反映 ----

    [Fact]
    public void Multi_range_conversion_is_one_undo_step()
    {
        byte[] data = [0x61, 0x62, 0x63, 0x64, 0xE6, 0x97, 0xA5, 0x65, 0x66];
        using Document doc = Doc(data);
        var ranges = new SelectionRanges([new TargetRange(4, 3), new TargetRange(0, 2)]);
        IReadOnlyList<RangeReplacement> parts = CharsetConverter.ConvertAll(doc, ranges, new CharsetConversionOptions("utf-8", "utf-16le"));
        Assert.Equal(2, parts.Count);
        Assert.True(TextTransforms.ChangesLength(parts));

        TransformApplier.Apply(doc, parts, "文字コード変換");
        Assert.Equal(new byte[] { 0x61, 0x00, 0x62, 0x00, 0x63, 0x64, 0xE5, 0x65, 0x65, 0x66 }, ReadAll(doc.Current));
        Assert.Equal([new TargetRange(0, 4), new TargetRange(6, 2)], TransformApplier.ResultRanges(parts));

        doc.Undo();
        Assert.Equal(data, ReadAll(doc.Current));
        Assert.Equal(0, doc.History.CurrentIndex);
    }

    [Fact]
    public void Fixed_length_document_accepts_same_length_and_refuses_length_changes()
    {
        using Document doc = Doc([0x41, 0x42], fixedLength: true);
        IReadOnlyList<RangeReplacement> same = CharsetConverter.ConvertAll(doc, SelectionRanges.Single(0, 2), new CharsetConversionOptions("ascii", "cp37"));
        Assert.False(TextTransforms.ChangesLength(same));
        TransformApplier.Apply(doc, same, "変換");
        Assert.Equal(new byte[] { 0xC1, 0xC2 }, ReadAll(doc.Current));

        IReadOnlyList<RangeReplacement> longer = CharsetConverter.ConvertAll(doc, SelectionRanges.Single(0, 2), new CharsetConversionOptions("cp37", "utf-16le"));
        Assert.True(TextTransforms.ChangesLength(longer));
        Assert.Throws<FixedLengthException>(() => TransformApplier.Apply(doc, longer, "変換"));
        Assert.Equal(new byte[] { 0xC1, 0xC2 }, ReadAll(doc.Current));
    }

    // ---- キャンセル・読み込みエラー ----

    [Fact]
    public void Cancel_in_the_middle_leaves_no_temp_file_and_nothing_applied()
    {
        byte[] data = new byte[4 * MiB];
        data.AsSpan().Fill((byte)'a');
        LongRunningOperation? op = null;
        using var doc = new Document(new CallbackSource(data, offset =>
        {
            if (offset >= 3 * MiB)
            {
                op!.Cancel();
            }
        }), Options());
        op = new LongRunningOperation("convert", OperationKind.ModifiesDocument, doc, null, TimeProvider.System);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CharsetConverter.ConvertAll(doc, SelectionRanges.Single(0, data.Length), new CharsetConversionOptions("utf-8", "utf-16le"), op));
        Assert.Equal(0, TransformFiles(doc));
        Assert.Equal(0, doc.History.CurrentIndex);
        Assert.Equal(data.Length, doc.Length);
    }

    [Fact]
    public void Cancel_while_converting_the_second_range_disposes_the_first_result()
    {
        byte[] data = new byte[3 * MiB];
        data.AsSpan().Fill((byte)'a');
        using Document doc = Doc(data);
        var op = new LongRunningOperation("convert", OperationKind.ModifiesDocument, doc, null, TimeProvider.System);
        int created = 0;
        ContentSink Create()
        {
            if (++created == 2)
            {
                // 1 つ目の結果 (2 MiB 以上で一時ファイル) ができた後にキャンセルする。
                Assert.Equal(1, TransformFiles(doc));
                op.Cancel();
            }

            return ContentSink.For(doc);
        }

        Assert.ThrowsAny<OperationCanceledException>(() => CharsetConverter.ConvertAll(doc.Current,
            [new TargetRange(0, 2 * MiB), new TargetRange(2 * MiB, MiB)], new CharsetConversionOptions("utf-8", "utf-16le"), Create, op));
        Assert.Equal(0, TransformFiles(doc));
        Assert.Equal(3L * MiB, op.TotalBytes);
    }

    [Fact]
    public void Read_error_is_an_io_exception_with_the_offset()
    {
        var faulty = new FaultyByteSource(new MemoryByteSource(new byte[4096]));
        faulty.AddReadError(0x100, 0x10);
        using var doc = new Document(faulty, Options());
        var ex = Assert.Throws<IOException>(() => ConvertWhole(doc, new CharsetConversionOptions("utf-8", "utf-16le")));
        Assert.Contains("0x100", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, TransformFiles(doc));
    }

    // ---- プレビュー・長さ ----

    [Fact]
    public void Preview_shows_the_first_256_bytes_and_the_exact_length_for_small_ranges()
    {
        string text = string.Concat(Enumerable.Repeat("日", 100));
        using Document doc = Doc(Utf8(text));
        CharsetConversionPreview preview = CharsetConverter.Preview(doc.Current, new TargetRange(0, doc.Length),
            new CharsetConversionOptions("utf-8", "utf-16le"));
        Assert.Equal(256, preview.Before.Length);

        // 256 バイト = 85 文字 + 1 バイト。途中の文字は変換しない。
        Assert.Equal(Encoding.Unicode.GetBytes(text[..85]), preview.After);
        Assert.Equal(200, preview.EstimatedLength);
        Assert.True(preview.LengthIsExact);
        Assert.Null(preview.Error);
    }

    [Fact]
    public void Preview_cuts_long_output_at_a_character_boundary()
    {
        using Document doc = Doc(Utf8(new string('a', 256)));
        CharsetConversionPreview preview = CharsetConverter.Preview(doc.Current, new TargetRange(0, 256),
            new CharsetConversionOptions("utf-8", "utf-32le") { AddTargetBom = true });
        Assert.Equal(256, preview.After.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0, 0, 0 }, preview.After[..8]);
        Assert.Equal(4 + 256 * 4, preview.EstimatedLength);
    }

    [Fact]
    public void Preview_reports_the_first_error()
    {
        using Document doc = Doc(Utf8("A日B"));
        CharsetConversionPreview preview = CharsetConverter.Preview(doc.Current, new TargetRange(0, doc.Length),
            new CharsetConversionOptions("utf-8", "ascii"));
        Assert.NotNull(preview.Error);
        Assert.Equal(1, preview.Error.Offset);
        Assert.Equal("日", preview.Error.Text);
        Assert.False(preview.LengthIsExact);
    }

    [Fact]
    public void Preview_estimates_the_length_of_long_ranges()
    {
        byte[] data = new byte[5 * MiB];
        data.AsSpan().Fill((byte)'a');
        using Document doc = Doc(data);
        CharsetConversionPreview preview = CharsetConverter.Preview(doc.Current, new TargetRange(0, data.Length),
            new CharsetConversionOptions("utf-8", "utf-16le"));
        Assert.False(preview.LengthIsExact);
        Assert.Equal(10L * MiB, preview.EstimatedLength);
        Assert.True(TextTransforms.IsLongRunning(data.Length));
        Assert.False(TextTransforms.IsLongRunning(4 * MiB));
    }

    [Fact]
    public void Unknown_encoding_is_an_argument_error()
    {
        var ex = Assert.Throws<CharsetSettingsException>(() => Convert([0x41], new CharsetConversionOptions("cp99999", "utf-8")));
        Assert.Equal(CharsetSettingsError.UnknownEncoding, ex.Error);
        Assert.Equal("cp99999", ex.Value);
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal(932, TextTransforms.ResolveCodePage("cp932"));
        Assert.Equal(20127, TextTransforms.ResolveCodePage("ascii"));
    }

    /// <summary>読み込みのたびに位置を知らせるデータソース (処理の途中でキャンセルするため)。</summary>
    private sealed class CallbackSource(byte[] data, Action<long> onRead) : ByteSourceBase
    {
        private readonly MemoryByteSource _inner = new(data);

        public override string DisplayName => "callback";

        public override string Identity => "callback:" + GetHashCode();

        public override long Length => data.Length;

        public override SourceCapabilities Capabilities => _inner.Capabilities;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            onRead(offset);
            return _inner.Read(offset, buffer);
        }
    }
}
