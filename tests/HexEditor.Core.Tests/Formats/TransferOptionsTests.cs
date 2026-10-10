using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Saving;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;

namespace HexEditor.Core.Tests.Formats;

/// <summary>
/// インポート / エクスポートのダイアログの欄の規則 (TOOL-04〜TOOL-10、TOOL-16): ドキュメントごとの既定値 (インポート時の値)、記憶する値、
/// 実行開始アドレスのレコード型、配列の修飾、配列のインポートの誤りの一覧、ダンプの色と TeX の文書、安全な書き出し。
/// </summary>
public sealed class TransferOptionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "transfer-" + Guid.NewGuid().ToString("N"));

    public TransferOptionsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static long? Eval(string text) => text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt64(text.Trim()[2..], 16)
        : long.TryParse(text, out long v) ? v : null;

    private static Dictionary<string, string> Values(string format, ExportDefaults defaults, string? saved = null) =>
        TransferOptions.Load(saved, TransferOptions.ExportFields(format, null, defaults));

    // ---- TOOL-05・TOOL-06: インポート時の値をエクスポートの既定値にする ----

    [Fact]
    public void S0_header_defaults_to_the_imported_value_then_to_the_file_name_and_is_not_carried_over()
    {
        var imported = new EncodedFileSettings { Format = FormatIds.SRecord, Header = "BOOT", StartAddress = 0x8000 };
        Dictionary<string, string> withImport = Values(FormatIds.SRecord, ExportDefaults.From("fw.bin", imported));
        Assert.Equal("BOOT", withImport["header"]);
        Assert.Equal("0x8000", withImport["execAddress"]);

        // インポートした値がなければファイル名。前回 (別のファイル) のエクスポートで記憶した値は持ち越さない。
        string saved = TransferOptions.Serialize(withImport, TransferOptions.ExportFields(FormatIds.SRecord, null, ExportDefaults.From("fw.bin", imported)));
        Assert.DoesNotContain("BOOT", saved);
        Dictionary<string, string> other = Values(FormatIds.SRecord, ExportDefaults.From("other.bin", null),
            """{"header":"BOOT","execAddress":"0x8000","recordLength":"32"}""");
        Assert.Equal("other.bin", other["header"]);
        Assert.Equal("0", other["execAddress"]); // S-record は「なければ 0」
        Assert.Equal("32", other["recordLength"]); // 形式ごとの設定は記憶する (TOOL-04 の仕様 4)
    }

    [Fact]
    public void Intel_hex_exec_address_defaults_to_the_imported_value_and_its_record_type()
    {
        var imported = new EncodedFileSettings { Format = FormatIds.IntelHex, StartAddress = 0x12345678, StartIsSegment = true };
        Dictionary<string, string> v = Values(FormatIds.IntelHex, ExportDefaults.From("fw.hex", imported));
        Assert.Equal("0x12345678", v["execAddress"]);
        Assert.Equal("segment", v["execType"]);
        ExportOptions options = TransferOptions.ToExportOptions(FormatIds.IntelHex, v, Eval, new DumpOptions());
        Assert.Equal(0x12345678, options.Records.ExecAddress);
        Assert.True(options.Records.ExecIsSegment);
        string text = Export([1, 2, 3], options);
        Assert.Contains(":0400000312345678", text); // 03 開始セグメントアドレス

        v["execType"] = "linear";
        Assert.Contains(":0400000512345678", Export([1, 2, 3], TransferOptions.ToExportOptions(FormatIds.IntelHex, v, Eval, new DumpOptions())));

        // インポートした値がなければ「なし」(空欄は 03 / 05 を出さない)。
        Dictionary<string, string> none = Values(FormatIds.IntelHex, ExportDefaults.From("fw.bin", null));
        Assert.Equal(string.Empty, none["execAddress"]);
        Assert.Null(TransferOptions.ToExportOptions(FormatIds.IntelHex, none, Eval, new DumpOptions()).Records.ExecAddress);
    }

    [Fact]
    public void Record_length_offers_16_by_default_and_32_as_a_choice()
    {
        TransferField field = TransferOptions.ExportFields(FormatIds.IntelHex).Single(f => f.Key == "recordLength");
        Assert.Equal(TransferFieldKind.Suggest, field.Kind);
        Assert.Equal("16", field.Default);
        Assert.Equal(["16", "32"], field.Choices);
        Assert.Empty(TransferOptions.Validate(FormatIds.IntelHex, new Dictionary<string, string> { ["recordLength"] = "200" }, Eval));
        Assert.Contains("recordLength", TransferOptions.Validate(FormatIds.IntelHex, new Dictionary<string, string> { ["recordLength"] = "256" }, Eval));
    }

    [Fact]
    public void S0_header_must_be_ascii_of_at_most_64_characters()
    {
        Assert.Contains("header", TransferOptions.Validate(FormatIds.SRecord, new Dictionary<string, string> { ["header"] = new string('a', 65) }, Eval));
        Assert.Contains("header", TransferOptions.Validate(FormatIds.SRecord, new Dictionary<string, string> { ["header"] = "日本" }, Eval));
        Assert.Empty(TransferOptions.Validate(FormatIds.SRecord, new Dictionary<string, string> { ["header"] = "module-1" }, Eval));
    }

    [Fact]
    public void Imported_start_address_and_header_are_kept_by_the_import_result()
    {
        string srec = Export([1, 2, 3, 4], new ExportOptions
        {
            Format = FormatIds.SRecord,
            Records = new RecordExportOptions { Header = "MOD", ExecAddress = 0x100 },
        }, 0x1000);
        using ImportResult result = Import(FormatIds.SRecord, srec);
        ExportDefaults defaults = ExportDefaults.From("x.bin", result.Settings);
        Assert.Equal("MOD", defaults.Header);
        Assert.Equal(0x100, defaults.ExecAddress);
    }

    // ---- TOOL-09: 修飾・誤りの一覧 ----

    [Theory]
    [InlineData(FormatIds.C, "static const", "static const unsigned char data[3] = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.C, "const", "const unsigned char data[3] = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.C, "default", "unsigned char data[3] = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.Cpp, "static const", "static constexpr std::array<std::uint8_t, 3> data = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.Cpp, "default", "constexpr std::array<std::uint8_t, 3> data = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.CSharp, "static const", "static readonly byte[] data = { 0x01, 0x02, 0x03 };")]
    [InlineData(FormatIds.Java, "static const", "static final byte[] data = { (byte) 0x01, (byte) 0x02, (byte) 0x03 };")]
    [InlineData(FormatIds.Rust, "static", "static data: [u8; 3] = [0x01, 0x02, 0x03];")]
    [InlineData(FormatIds.JavaScript, "none", "let data = new Uint8Array([0x01, 0x02, 0x03]);")]
    [InlineData(FormatIds.Go, "static", "var data = []byte{0x01, 0x02, 0x03}")]
    [InlineData(FormatIds.Pascal, "none", "var data: array[0..2] of Byte = ($01, $02, $03);")]
    public void Source_array_modifier_is_applied(string format, string modifier, string expected)
    {
        var v = TransferOptions.Load(null, TransferOptions.ExportFields(format));
        v["modifier"] = modifier switch { "static const" => "staticConst", _ => modifier };
        v["comment"] = "false";
        ExportOptions options = TransferOptions.ToExportOptions(format, v, Eval, new DumpOptions());
        Assert.Equal(expected, Export([1, 2, 3], options).Trim());
    }

    [Fact]
    public void Python_has_no_modifier_field()
    {
        Assert.DoesNotContain(TransferOptions.ExportFields(FormatIds.Python), f => f.Key == "modifier");
        Assert.Contains(TransferOptions.ExportFields(FormatIds.C), f => f.Key == "modifier");
    }

    [Fact]
    public void Source_array_import_lists_every_unparsable_position()
    {
        const string Source = "unsigned char a[] = {\n  0x01, 0xZZ, 2,\n  @, 300, 3\n};";
        using ImportResult result = Import(FormatIds.C, Source);
        Assert.Equal(3, result.Issues.ErrorCount);
        Assert.Equal([(2, 9), (3, 3), (3, 6)], result.Issues.Items.Select(i => (i.Line, i.Column)));
        Assert.Equal(ImportIssueKind.InvalidCharacter, result.Issues.Items[1].Kind);
        Assert.Equal(ImportIssueKind.OutOfRange, result.Issues.Items[2].Kind);
        Assert.Equal("  @, 300, 3", result.Issues.Items[1].Content); // 内容はその行
        Assert.Equal(1, result.InferredValueSize);

        // 「誤りを無視して読み込む」では解釈できた値だけを読む。
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, Bytes(result));
    }

    [Fact]
    public void Inferred_element_size_is_reported_with_errors()
    {
        using ImportResult result = Import(FormatIds.C, "uint16_t a[] = {\r\n  0x0102, @,\r\n  0x0304, 0xG1\r\n};\r\n", new ImportOptions { ValueSize = 0 });
        Assert.Equal(2, result.InferredValueSize);
        Assert.Equal([(2, 11), (3, 11)], result.Issues.Items.Select(i => (i.Line, i.Column)));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x04, 0x03 }, Bytes(result));
    }

    [Fact]
    public void Paste_still_stops_at_the_first_array_error()
    {
        PasteCandidate parsed = PasteDetector.Parse(PasteFormat.Array, "{ 0x01, @, 0x02 }");
        Assert.NotNull(parsed.Error);
        Assert.Empty(parsed.SkippedErrors);
    }

    // ---- TOOL-10: 文字コード・色・TeX の文書 ----

    [Fact]
    public void Dump_text_encoding_is_an_option_defaulting_to_the_screen()
    {
        Dictionary<string, string> v = Values(FormatIds.DumpText, new ExportDefaults { TextEncodingId = "utf-8" });
        Assert.Equal("utf-8", v["textEncoding"]);
        Assert.Equal("utf-8", TransferOptions.ToExportOptions(FormatIds.DumpText, v, Eval, new DumpOptions()).Dump.Encoding.Id);
        v["textEncoding"] = "cp932";
        Assert.Equal(932, TransferOptions.ToExportOptions(FormatIds.DumpText, v, Eval, new DumpOptions()).Dump.Encoding.CodePage);
        v["textEncoding"] = "no-such-encoding";
        Assert.Contains("textEncoding", TransferOptions.Validate(FormatIds.DumpText, v, Eval));
    }

    [Fact]
    public void Html_and_rtf_use_bookmark_and_rule_colors()
    {
        byte[] data = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        var options = new DumpOptions
        {
            Highlights = [new DumpHighlight(0, 2, DumpHighlightKind.Bookmark, "hdr", "#107C10")],
            RowHighlights = (offset, count) => offset == 16 ? [new DumpHighlight(16, 1, DumpHighlightKind.Rule, "magic", "#0063B1")] : [],
            RuleColors = ["#0063B1"],
        };
        string html = DumpExporter.Format(FormatIds.Html, options, data);
        Assert.Contains("<span class=\"mark\" title=\"hdr\" style=\"background-color: #107C1055\">00</span>", html);
        Assert.Contains("<span class=\"rule\" title=\"magic\" style=\"background-color: #0063B155\">10</span>", html);

        string rtf = DumpExporter.Format(FormatIds.Rtf, options, data);
        Assert.Contains("\\red16\\green124\\blue16;\\red0\\green99\\blue177;}", rtf);
        Assert.Contains("{\\cf3\\uld 00}", rtf);
        Assert.Contains("{\\cf4\\uld 10}", rtf);

        string tex = DumpExporter.Format(FormatIds.Tex, options with { Tex = TexEnvironment.AlltColor }, data);
        Assert.Contains("\\textcolor[HTML]{107C10}{\\underline{00}}", tex);
        Assert.Contains("\\textcolor[HTML]{0063B1}{\\underline{10}}", tex);
    }

    [Fact]
    public void Modified_bytes_win_over_rule_colors()
    {
        var options = new DumpOptions
        {
            Highlights = [new DumpHighlight(0, 1, DumpHighlightKind.Modified)],
            RowHighlights = (_, _) => [new DumpHighlight(0, 2, DumpHighlightKind.Rule, "r", "#FF0000")],
        };
        string html = DumpExporter.Format(FormatIds.Html, options, [0xAA, 0xBB]);
        Assert.Contains("<span class=\"mod\">AA</span>", html);
        Assert.Contains("<span class=\"rule\" title=\"r\" style=\"background-color: #FF000055\">BB</span>", html);
    }

    [Fact]
    public void Tex_can_be_a_complete_document()
    {
        foreach (TexEnvironment env in Enum.GetValues<TexEnvironment>())
        {
            string fragment = DumpExporter.Format(FormatIds.Tex, new DumpOptions { Tex = env }, [0x41]);
            Assert.DoesNotContain("\\documentclass", fragment);
            string document = DumpExporter.Format(FormatIds.Tex, new DumpOptions { Tex = env, TexDocument = true }, [0x41]);
            Assert.StartsWith("\\documentclass{article}", document);
            Assert.Contains("\\usepackage{xcolor}", document);
            Assert.Contains("\\begin{document}", document);
            Assert.EndsWith("\\end{document}\r\n", document);
            Assert.Contains(env == TexEnvironment.Verbatim ? "\\end{verbatim}" : "\\end{alltt}", document);
        }

        Dictionary<string, string> v = TransferOptions.Load(null, TransferOptions.ExportFields(FormatIds.Tex));
        Assert.Equal("false", v["texDocument"]);
        v["texDocument"] = "true";
        Assert.True(TransferOptions.ToExportOptions(FormatIds.Tex, v, Eval, new DumpOptions()).Dump.TexDocument);
    }

    // ---- TOOL-16: ファイル名の記号は元のドキュメントの名前 ----

    [Fact]
    [Trait("TC", "TC-TOOL-16-01")]
    public void Per_range_names_use_the_document_name_and_existing_files_are_found()
    {
        IReadOnlyList<(long, long)> ranges = [(0x12345, 0x10000), (0x200000, 0x100)];
        Assert.Equal(["firmware_12345.img", "firmware_200000.img"], RangeFileNames.ExpandAll(RangeFileNames.DefaultPattern, "firmware.img", ranges));
        Assert.Equal(["12345.bin", "200000.bin"], RangeFileNames.ExpandAll("{start}.bin", "firmware.img", ranges));
        Assert.Equal(["firmware.img.1", "firmware.img.2"], RangeFileNames.ExpandAll("{name}.{index}", "firmware.img", ranges));

        File.WriteAllBytes(Path.Combine(_folder, "200000.bin"), [1]);
        Assert.Equal(["200000.bin"], RangeFileNames.Existing(_folder, ["12345.bin", "200000.bin"]));
    }

    // ---- 安全な書き出し (ENG-22 と同じ手順。TOOL-04・TOOL-11) ----

    [Fact]
    public void Safe_writer_keeps_the_creation_time_and_attributes_and_makes_a_backup()
    {
        string target = Path.Combine(_folder, "out.hex");
        File.WriteAllText(target, "old");
        var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(target, created);
        File.SetAttributes(target, FileAttributes.Archive | FileAttributes.NotContentIndexed);
        string markers = Path.Combine(_folder, "markers");
        Directory.CreateDirectory(markers);

        BackupOutcome? backup = SafeFileWriter.Write(target, s => s.Write("new"u8), new BackupSettings(), markers);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(created, File.GetCreationTimeUtc(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.NotContentIndexed));
        Assert.NotNull(backup);
        Assert.Equal("old", File.ReadAllText(backup.Path));
        Assert.Empty(Directory.GetFiles(markers)); // 終わったら一時ファイルの記録を消す
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp", SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(_folder, ".*.tmp")));
    }

    [Fact]
    public void Safe_writer_leaves_the_target_and_no_temp_file_when_writing_fails()
    {
        string target = Path.Combine(_folder, "keep.bin");
        File.WriteAllText(target, "old");
        Assert.Throws<InvalidOperationException>(() => SafeFileWriter.Write(target, s =>
        {
            s.Write("partial"u8);
            throw new InvalidOperationException();
        }));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Saving_an_encoded_file_uses_the_safe_save_with_a_backup()
    {
        string path = Path.Combine(_folder, "fw.hex");
        string original = Export([1, 2, 3, 4], new ExportOptions { Format = FormatIds.IntelHex });
        File.WriteAllText(path, original);
        File.SetCreationTimeUtc(path, new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using ImportResult result = ImportFile(FormatIds.IntelHex, path, EncodedFile.OpenOptions(FormatIds.IntelHex));
        EncodedFileSettings settings = result.Settings!;
        using Document doc = EncodedFile.CreateDocument(result, FormatIds.IntelHex);
        doc.Overwrite(0, [0xAA]);
        BackupOutcome? backup = EncodedFile.Save(doc.Current, 0, settings, path, null, new BackupSettings());
        Assert.NotNull(backup);
        Assert.Equal(original, File.ReadAllText(backup.Path));
        Assert.Equal(new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetCreationTimeUtc(path));
        using ImportResult again = ImportFile(FormatIds.IntelHex, path);
        Assert.Equal(new byte[] { 0xAA, 2, 3, 4 }, Bytes(again));
    }

    [Fact]
    public void Remembered_values_exclude_document_specific_fields()
    {
        IReadOnlyList<TransferField> fields = TransferOptions.ExportFields(FormatIds.UUEncode, null, new ExportDefaults { FileName = "a.bin" });
        Dictionary<string, string> v = TransferOptions.Load(null, fields);
        v["uuMode"] = "600";
        string json = TransferOptions.Serialize(v, fields);
        Assert.Contains("600", json);
        Assert.DoesNotContain("a.bin", json);
        Assert.Equal("b.bin", TransferOptions.Load(json, TransferOptions.ExportFields(FormatIds.UUEncode, null, new ExportDefaults { FileName = "b.bin" }))["uuName"]);
        Assert.Equal("1", TransferOptions.Load("""{"uuIndex":"3"}""", TransferOptions.ImportFields(FormatIds.UUEncode))["uuIndex"]);
    }
}
