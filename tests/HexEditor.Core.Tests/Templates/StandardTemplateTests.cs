using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// 標準テンプレート集 (TPL-34)。同梱のすべてのテンプレートを、StandardTemplateSamples で組み立てた自作のサンプルに適用し、期待する結果の JSON
/// (tests/fixtures/templates/std/&lt;サンプル&gt;.expected.json) と比べる。期待値を作り直すときは HEXEDITOR_UPDATE_TEMPLATE_GOLDEN=1 を付けて
/// テストを実行し、差分を確認してからコミットする。
/// </summary>
[Trait(TC, "TC-TPL-34-01")]
public sealed class StandardTemplateTests
{
    private static readonly TemplateCatalog Catalog = TemplateCatalog.CreateDefault(null);

    public static IEnumerable<object[]> SampleKeys => StandardTemplateSamples.All.Keys.Select(k => new object[] { k });

    private static IReadOnlyList<TemplateCatalogEntry> StandardEntries => [.. Catalog.Entries.Where(e => e.Origin == TemplateOrigin.Standard)];

    private static TemplateResult Apply(string key, TemplateDataSource? source = null)
    {
        TemplateCatalogEntry entry = Catalog.Find(StandardTemplateSamples.TemplateOf(key)) ?? throw new Xunit.Sdk.XunitException($"標準テンプレート {key} がありません。");
        TemplateProgram program = Catalog.Load(entry);
        Assert.DoesNotContain(program.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        return TemplateEngine.ApplyAndWait(program, source ?? TemplateDataSource.FromBytes(StandardTemplateSamples.All[key], key + ".bin"));
    }

    private static string ExpectedPath(string key) => Path.Combine(Golden.Folder("std"), key + ".expected.json");

    // ---- (a) サンプルに適用して、期待する JSON と比べる ----

    [Theory]
    [MemberData(nameof(SampleKeys))]
    public void SampleMatchesTheExpectedJsonWithoutErrorsOrWarnings(string key)
    {
        // 期待値の JSON は日本語の表示名で作ってある。実行する環境の言語 (CI は英語) に左右されないよう、表示言語を ja-JP に固定する。
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        TemplateResult r;
        string json;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ja-JP");
            r = Apply(key);
            Assert.Equal(TemplateRunState.Completed, r.State);
            json = TemplateJsonExporter.ToJson(r);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = saved;
        }

        string[] problems = [.. r.Diagnostics.Where(d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning).Select(d => d.ToString())];
        Assert.True(problems.Length == 0, $"{key}: {string.Join("; ", problems)}");
        Assert.DoesNotContain("\"error\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"warning\":", json, StringComparison.Ordinal);

        json = json.ReplaceLineEndings("\n");
        string path = ExpectedPath(key);
        if (Environment.GetEnvironmentVariable("HEXEDITOR_UPDATE_TEMPLATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return;
        }

        Assert.True(File.Exists(path), $"{path} がありません。HEXEDITOR_UPDATE_TEMPLATE_GOLDEN=1 を付けて作り、内容を確かめてください。");
        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(expected == json, $"{key}: 結果が {path} と一致しません (先頭 {FirstDifference(expected, json)})。");
    }

    private static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        string Around(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(120, s.Length - Math.Max(0, i - 60))).Replace("\n", "\\n", StringComparison.Ordinal);
        return $"位置 {i}: 期待「{Around(a)}」実際「{Around(b)}」";
    }

    [Fact]
    public void EveryStandardTemplateHasASample()
    {
        string[] withSample = [.. StandardTemplateSamples.All.Keys.Select(StandardTemplateSamples.TemplateOf).Distinct()];
        foreach (TemplateCatalogEntry entry in StandardEntries)
        {
            Assert.True(withSample.Contains(entry.Name, StringComparer.Ordinal), $"{entry.Name} のサンプルがありません。");
        }

        foreach (string name in withSample)
        {
            Assert.NotNull(Catalog.Find(name));
        }
    }

    // ---- (b) メタ情報と自動適用 ----

    [Fact]
    public void EveryStandardTemplateHasEnglishAndJapaneseMetadataLicenseAndAutoApplyConditions()
    {
        Assert.True(StandardEntries.Count >= 48, $"標準テンプレートは {StandardEntries.Count} 個しかありません。");
        foreach (TemplateCatalogEntry entry in StandardEntries)
        {
            TemplateMetadata m = entry.Metadata;
            Assert.False(string.IsNullOrWhiteSpace(m.Name), $"{entry.Name}: name");
            Assert.False(string.IsNullOrWhiteSpace(m.Description), $"{entry.Name}: description");
            Assert.True(m.LocalizedNames.TryGetValue("ja", out string? nameJa) && !string.IsNullOrWhiteSpace(nameJa), $"{entry.Name}: name.ja");
            Assert.True(m.LocalizedDescriptions.TryGetValue("ja", out string? descriptionJa) && !string.IsNullOrWhiteSpace(descriptionJa), $"{entry.Name}: description.ja");
            Assert.Equal("MIT", m.License);
            Assert.Equal("HexEditor", m.Author);
            Assert.True(Version.TryParse(m.Version, out _), $"{entry.Name}: version {m.Version}");
            Assert.True(m.Magic.Count + m.MagicAll.Count + m.Mimes.Count > 0, $"{entry.Name}: magic か mime がありません。");
            Assert.True(m.Extensions.Count > 0, $"{entry.Name}: extensions がありません。");
            Assert.True(m.AutoApply, $"{entry.Name}: auto_apply");
            Assert.Equal(entry.Name, entry.Name.ToLowerInvariant());
        }
    }

    [Theory]
    [MemberData(nameof(SampleKeys))]
    public void DetectionRanksTheTemplateFirstForItsOwnSample(string key)
    {
        string name = StandardTemplateSamples.TemplateOf(key);
        byte[] data = StandardTemplateSamples.All[key];
        TemplateCatalogEntry entry = Catalog.Find(name)!;

        // 実際のドキュメントと同じ判定 (先頭と末尾の 64 KB、ファイル形式の判定の MIME、拡張子)
        using var doc = new Document(new MemoryByteSource(data), Support.DocumentAssert.Options());
        string extension = entry.Metadata.Extensions[0];
        IReadOnlyList<TemplateCandidate> candidates = Catalog.Detect(doc.Current, "sample." + extension);
        TemplateCandidate own = candidates.FirstOrDefault(c => c.Entry.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"{key}: 候補にありません。候補: {string.Join(", ", candidates.Select(c => c.Entry.Name))}");
        Assert.True(own.Match.CanAutoApply, $"{key}: マジックか MIME で一致していません。");
        Assert.Equal(name, candidates[0].Entry.Name);

        // マジックだけ (拡張子なし、MIME なし) でも候補になり、自動適用の対象になる
        IReadOnlyList<TemplateCandidate> magicOnly = Catalog.Detect(TemplateMatchInput.FromBytes(data));
        TemplateCandidate ownByMagic = magicOnly.FirstOrDefault(c => c.Entry.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"{key}: マジックだけでは候補にありません。候補: {string.Join(", ", magicOnly.Select(c => c.Entry.Name))}");
        Assert.True(ownByMagic.Match.Magic, $"{key}: マジックで一致していません。");
    }

    // ---- (c) 主要な形式の、独立した手計算の確認 ----

    [Fact]
    public void PeSectionsExportsImportsResourcesAndCliHeader()
    {
        TemplateResult r = Apply("pe");
        Assert.Equal(".text", Value(r, "sections[0].Name"));
        Assert.Equal(".rdata", Value(r, "sections[1].Name"));
        Assert.Equal(".reloc", Value(r, "sections[3].Name"));
        Assert.Equal("SectionHeader", Node(r, "sections[0]").TypeName.Split("::")[^1]);
        Assert.Equal(0x100L, Long(r, "header.e_lfanew"));
        Assert.Equal(0x8664L, Long(r, "nt.FileHeader.Machine"));
        Assert.Equal(0x20BL, Long(r, "nt.OptionalHeader.Magic"));
        Assert.Equal(0x1000L, Long(r, "nt.OptionalHeader.AddressOfEntryPoint"));
        Assert.Equal(0x140000000L, Long(r, "nt.OptionalHeader.ImageBase"));
        Assert.Equal(0x80L, Node(r, "rich").Offset);       // リッチヘッダは DOS スタブの後ろの 0x80 から
        Assert.Equal(2, Node(r, "rich.entries").ChildCount);
        Assert.Equal("hxtest.dll", Value(r, "exports.dll_name"));
        Assert.Equal("KERNEL32.dll", Value(r, "imports[0].dll_name"));
        Assert.Equal("ExitProcess", Value(r, "imports[0].thunks[0].hint_name.Name"));
        Assert.Equal("GetLastError", Value(r, "imports[0].thunks[1].hint_name.Name"));
        Assert.Equal(1L, Long(r, "resources.NumberOfIdEntries"));
        Assert.Equal(0x1000L, Long(r, "relocations[0].PageRva"));
        Assert.Equal("hxtest.pdb", Value(r, "debug[0].codeview.PdbFileName"));
        Assert.Null(r.FindByPath("cli"));

        TemplateResult net = Apply("pe.dotnet");
        Assert.Equal(0x10BL, Long(net, "nt.OptionalHeader.Magic"));
        Assert.Equal(72L, Long(net, "cli.cb"));
        Assert.Equal("v4.0.30319", Value(net, "metadata.Version"));
        Assert.Equal("#~", Value(net, "metadata.StreamHeaders[0].Name"));
        Assert.Equal("#Strings", Value(net, "metadata.StreamHeaders[1].Name"));
        Assert.Equal(3, Node(net, "metadata.StreamHeaders").ChildCount);
    }

    [Fact]
    public void ElfMachineSectionsSymbolsAndDynamicSection()
    {
        TemplateResult r = Apply("elf");
        Assert.Equal(62L, Long(r, "header.e_machine"));
        Assert.Equal(3L, Long(r, "header.e_type"));
        Assert.Equal(3, Node(r, "program_headers").ChildCount);
        Assert.Equal(1L, Long(r, "program_headers[0].p_type"));
        Assert.Equal(8, Node(r, "section_headers").ChildCount);
        Assert.Equal(".text", Value(r, "section_headers[1].name"));
        Assert.Equal(".dynsym", Value(r, "section_headers[2].name"));
        Assert.Equal(".shstrtab", Value(r, "section_headers[7].name"));
        Assert.Equal("hx_add", Value(r, "symtab[2].name"));
        Assert.Equal("hx.c", Value(r, "symtab[1].name"));
        Assert.Equal("hx_add", Value(r, "dynsym[1].name"));
        Assert.Equal(0x100L, Long(r, "dynsym[1].st_value"));
        Assert.Equal(6, Node(r, "dynamic").ChildCount);
        Assert.Equal(1L, Long(r, "dynamic[0].d_tag"));
        Assert.Equal(24L, Long(r, "dynamic[4].d_val"));

        TemplateResult arm = Apply("elf.arm");
        Assert.Equal(40L, Long(arm, "header.e_machine"));
        Assert.Equal(0x8060L, Long(arm, "header.e_entry"));
        Assert.Equal(".shstrtab", Value(arm, "section_headers[2].name"));

        // ビッグエンディアン (MIPS) でも、同じ値が読める
        TemplateResult mips = Apply("elf.mips");
        Assert.Equal(8L, Long(mips, "header.e_machine"));
        Assert.Equal(0x8060L, Long(mips, "header.e_entry"));
        Assert.Equal(0x79L, Long(mips, "program_headers[0].p_filesz"));
        Assert.Equal(".text", Value(mips, "section_headers[1].name"));
    }

    [Fact]
    public void MachoThinAndFatBinaries()
    {
        TemplateResult r = Apply("macho");
        Assert.Equal(7L, Long(r, "macho.header.ncmds"));
        Assert.Equal(0x01000007L, Long(r, "macho.header.cputype"));
        Assert.Equal(7, Node(r, "macho.commands").ChildCount);
        Assert.Equal("__TEXT", Value(r, "macho.commands[1].segment.segname"));
        Assert.Equal("__text", Value(r, "macho.commands[1].segment.sections[0].sectname"));
        Assert.Equal(0x200L, Long(r, "macho.commands[4].entry_point.entryoff"));
        Assert.Equal("/usr/lib/libSystem.B.dylib", Value(r, "macho.commands[5].dylib.name"));

        TemplateResult fat = Apply("macho.fat");
        Assert.Equal(0xCAFEBABEL, Long(fat, "fat.magic"));
        Assert.Equal(2L, Long(fat, "fat.nfat_arch"));
        Assert.Equal(0x200L, Long(fat, "archs[0].offset"));
        Assert.Equal(0x01000007L, Long(fat, "slices[0].header.cputype"));
        Assert.Equal(0x0100000CL, Long(fat, "slices[1].header.cputype"));
        Assert.Equal(0x400L, Node(fat, "slices[1]").Offset);
    }

    [Fact]
    public void ZipEntriesCentralDirectoryAndZip64()
    {
        TemplateResult r = Apply("zip");
        Assert.Equal("hello.txt", Value(r, "local_files[0].header.file_name"));
        Assert.Equal("dir/data.bin", Value(r, "local_files[1].header.file_name"));
        Assert.Equal("stream.txt", Value(r, "central_directory[2].file_name"));
        Assert.Equal(3L, Long(r, "eocd.total_entries"));
        Assert.Equal("HexEditor test archive", Value(r, "eocd.comment"));
        Assert.Equal(8L, Long(r, "central_directory[1].method"));
        Assert.Equal(200L, Long(r, "central_directory[1].uncompressed_size"));
        Assert.Equal(0L, Node(r, "local_files[0]").Offset);
        Assert.NotNull(r.FindByPath("local_files[2].descriptor"));

        TemplateResult z = Apply("zip.zip64");
        Assert.NotNull(z.FindByPath("zip64_eocd"));
        Assert.Equal(3L, Long(z, "zip64_eocd.total_entries"));
        Assert.Equal(0xFFFFFFFFL, Long(z, "central_directory[1].local_header_offset"));
        Assert.Equal(200L, Long(z, "central_directory[1].real_uncompressed_size"));
        Assert.Equal("dir/data.bin", Value(z, "local_files[1].header.file_name"));
    }

    [Fact]
    public void MbrPartitionTypesAndExtendedBootRecords()
    {
        TemplateResult r = Apply("mbr");
        Assert.Equal(0x07L, Long(r, "mbr.partitions[0].type"));
        Assert.Equal(2048L, Long(r, "mbr.partitions[0].lba_first"));
        Assert.Equal(0x0CL, Long(r, "mbr.partitions[1].type"));
        Assert.Equal(0x0FL, Long(r, "mbr.partitions[2].type"));
        Assert.Equal(0x44545848L, Long(r, "mbr.disk_signature"));
        Assert.Equal(0x1000L, Node(r, "ebr[0]").Offset);
        Assert.Equal(0x83L, Long(r, "ebr[0].partitions[0].type"));
        Assert.Equal(0x3000L, Node(r, "ebr[1]").Offset);
        Assert.Equal(0x82L, Long(r, "ebr[1].partitions[0].type"));
    }

    [Fact]
    public void GptHeaderEntriesAndBackupHeader()
    {
        TemplateResult r = Apply("gpt");
        Assert.Equal(0xEEL, Long(r, "protective_mbr.partitions[0].type"));
        Assert.Equal("EFI PART", Value(r, "header.signature"));
        Assert.Equal(128L, Long(r, "header.num_partition_entries"));
        Assert.Equal("HXTEST-DATA", Value(r, "entries[0].name"));
        Assert.Equal("HXTEST-LOG", Value(r, "entries[1].name"));
        Assert.Equal("Microsoft Basic Data", Value(r, "entries[0].type_name"));
        Assert.Equal(2048L, Long(r, "entries[0].first_lba"));
        Assert.Equal(8191L, Long(r, "header.alternate_lba"));
        Assert.Equal(1L, Long(r, "backup_header.alternate_lba"));
        Assert.Equal(8191L * 512, Node(r, "backup_header").Offset);
    }

    [Fact]
    public void PngKeepsItsTopLevelNames()
    {
        TemplateResult r = Apply("png");
        Assert.Equal(["signature", "chunks"], r.Root.Children.Select(c => c.Name));
        Assert.Equal("IHDR", Value(r, "chunks[0].type"));
        Assert.Equal(4L, Long(r, "chunks[0].header.width"));
        Assert.Equal("IEND", Value(r, $"chunks[{Node(r, "chunks").ChildCount - 1}].type"));
        Assert.Equal("PLTE", Value(r, "chunks[3].type"));
    }

    [Fact]
    public void ImageAndMediaFormatsKeyValues()
    {
        Assert.Equal(16L, Long(Apply("webp"), "chunks[0].vp8x.canvas_width"));
        Assert.Equal(8000L, Long(Apply("wav"), "chunks[0].format.samples_per_second"));
        Assert.Equal(["fmt ", "data", "LIST"], Node(Apply("wav"), "chunks").Children.Select(c => (string)c.GetChild(0).Value!));
        Assert.Equal(417L, Long(Apply("mp3"), "frames[0].length"));
        Assert.Equal("HexTitle", Value(Apply("mp3"), "id3v2.frames[0].text"));
        Assert.Equal(44100L, Long(Apply("flac"), "blocks[0].stream_info.parameters.sample_rate"));
        TemplateResult mkv = Apply("mkv");
        Assert.Equal("webm", Value(mkv, "elements[0].children[4].value"));
        Assert.Equal(4L, Long(Apply("tiff"), "ifd.entries[0].number"));
        Assert.Equal(4L, Long(Apply("tiff.be"), "ifd.entries[0].number"));
        Assert.Equal("HexEditor", Value(Apply("jpeg"), "segments[2].body.exif.ifd0.entries[0].text"));
    }

    [Fact]
    public void ArchiveAndDocumentFormatsKeyValues()
    {
        Assert.Equal("hello.txt", Value(Apply("gzip"), "header.file_name"));
        Assert.Equal(18L, Long(Apply("gzip"), "trailer.uncompressed_size_mod_2_32"));
        TemplateResult tar = Apply("tar");
        Assert.Equal("dir/hello.txt", Value(tar, "entries[1].header.name"));
        Assert.Equal(13L, Long(tar, "entries[1].header.size_value"));
        Assert.Equal(0x400L, Node(tar, "entries[1].data").Offset);
        Assert.Equal(10L, Long(Apply("7z"), "header.next_header_size"));
        TemplateResult rar = Apply("rar");
        Assert.Equal("a.txt", Value(rar, "blocks[1].file.name"));
        Assert.Equal(5L, Long(rar, "blocks[1].data_area_size"));
        Assert.Equal(22L, Long(Apply("xz"), "index.records[0].uncompressed_size"));
        TemplateResult cab = Apply("cab");
        Assert.Equal("a.txt", Value(cab, "files[0].name"));
        Assert.Equal("b.txt", Value(cab, "files[1].name"));
        Assert.Equal(0x58L, Node(cab, "first_data_block").Offset);

        TemplateResult pdf = Apply("pdf");
        Assert.Equal("%PDF-", Value(pdf, "header.magic"));
        Assert.Equal(0xC0L, Long(pdf, "startxref.xref_offset"));
        Assert.Equal(4, Node(pdf, "xref_entries").ChildCount);
        Assert.Equal(0x40L, Long(pdf, "xref_entries[2].object_offset"));
        TemplateResult cfb = Apply("cfb");
        Assert.Equal("Root Entry", Value(cfb, "directory[0].name"));
        Assert.Equal("Stream1", Value(cfb, "directory[1].name"));

        Assert.Equal("1", Value(Apply("rtf"), "signature.version"));
    }

    [Fact]
    public void DiskFilesystemAndDatabaseFormatsKeyValues()
    {
        TemplateResult fat = Apply("fat");
        Assert.Equal("HXTESTFAT32", Value(fat, "fat32.volume_label"));
        Assert.Equal(1L, Long(fat, "fat32.fs_info_sector"));
        Assert.Equal(16000L, Long(fat, "fs_info.free_cluster_count"));
        Assert.Equal("HXTESTFAT16", Value(Apply("fat.fat16"), "extended.volume_label"));
        Assert.Equal(4096L, Long(Apply("exfat"), "boot_sector.bytes_per_cluster"));
        TemplateResult ntfs = Apply("ntfs");
        Assert.Equal(0x1000L, Long(ntfs, "boot_sector.mft_offset"));
        Assert.Equal(1024L, Long(ntfs, "boot_sector.record_size"));
        Assert.Equal("$MFT", Value(ntfs, "mft_records[0].attributes[1].file_name.name"));
        Assert.Equal("$Volume", Value(ntfs, "mft_records[3].attributes[1].file_name.name"));
        Assert.Equal(8, Node(ntfs, "mft_records").ChildCount);
        TemplateResult iso = Apply("iso9660");
        Assert.Equal(2, Node(iso, "descriptors").ChildCount);
        Assert.Equal(2048L, Long(iso, "descriptors[0].primary.logical_block_size.little_endian"));
        Assert.Equal(21L, Long(iso, "descriptors[0].primary.root_directory_record.extent_location.big_endian"));
        Assert.Equal(255L, Long(iso, "descriptors[1].type"));
        TemplateResult vhd = Apply("vhd");
        Assert.Equal(4096L, Node(vhd, "footer").Offset);
        Assert.Equal(4096L, Long(vhd, "footer.current_size"));
        TemplateResult dynamic = Apply("vhd.dynamic");
        Assert.Equal(0x600L, Long(dynamic, "dynamic_header.table_offset"));
        Assert.Equal(2, Node(dynamic, "block_allocation_table").ChildCount);
        TemplateResult vhdx = Apply("vhdx");
        Assert.Equal(1L, Long(vhdx, "header1.version"));
        Assert.Equal(2L, Long(vhdx, "header2.sequence_number"));
        Assert.Equal("BAT", Value(vhdx, "region_table1.entries[0].name"));
        Assert.Equal("Metadata", Value(vhdx, "region_table2.entries[1].name"));
        TemplateResult ext = Apply("ext");
        Assert.Equal(0xEF53L, Long(ext, "superblock.magic"));
        Assert.Equal("HXTEST-EXT4", Value(ext, "superblock.volume_name"));
        Assert.Equal(1024L, Long(ext, "superblock.block_size"));

        TemplateResult sqlite = Apply("sqlite");
        Assert.Equal(512L, Long(sqlite, "header.page_size"));
        Assert.Equal(13L, Long(sqlite, "page_header.page_type"));
        Assert.Equal(1L, Long(sqlite, "cells.rowid"));
        Assert.Equal(479L, Node(sqlite, "cells").Offset);
        TemplateResult lnk = Apply("lnk");
        Assert.Equal(@"C:\Test\a.txt", Value(lnk, "link_info.local_base_path"));
        Assert.Equal("HexEditor", Value(lnk, "name_string.text"));
        TemplateResult regf = Apply("regf");
        Assert.Equal("ROOT", Value(regf, "hive_bins[0].cells[0].key_node.name"));
        Assert.Equal("Value1", Value(regf, "hive_bins[0].cells[1].key_value.name"));
        Assert.Equal(2, Node(Apply("pcap"), "packets").ChildCount);
        Assert.Equal(2, Node(Apply("pcap.be"), "packets").ChildCount);
        Assert.Equal(20L, Long(Apply("pcap.be"), "packets[1].captured_length"));
        TemplateResult pcapng = Apply("pcapng");
        Assert.Equal(3, Node(pcapng, "blocks").ChildCount);
        Assert.Equal(20L, Long(pcapng, "blocks[2].enhanced_packet.captured_length"));
        TemplateResult ttf = Apply("ttf");
        Assert.Equal("head", Value(ttf, "tables[0].tag"));
        Assert.Equal(1000L, Long(ttf, "head.units_per_em"));
        Assert.Equal(12L, Long(ttf, "maxp.num_glyphs"));
        TemplateResult der = Apply("der");
        Assert.Equal("1.2.840.113549.1.1.11", Value(der, "elements[0].children[0].children[2].children[0].oid"));
        Assert.Equal("HexEditor Test CA", Value(der, "elements[0].children[0].children[3].children[0].children[0].children[1].utf8_string"));
    }

    [Fact]
    public void ExecutableAndBytecodeFormatsKeyValues()
    {
        TemplateResult java = Apply("javaclass");
        Assert.Equal(52L, Long(java, "major_version"));
        Assert.Equal(11L, Long(java, "constant_pool_count"));
        Assert.Equal(9, Node(java, "constant_pool").ChildCount);
        Assert.Equal(1L, Long(java, "methods_count"));
        Assert.Equal(5L, Long(java, "methods[0].name_index"));
        TemplateResult dex = Apply("dex");
        Assert.Equal(2L, Long(dex, "header.string_ids_size"));
        Assert.Equal(144L, Long(dex, "header.map_offset"));
        Assert.Equal(5L, Long(dex, "map_list.size"));
        TemplateResult wasm = Apply("wasm");
        Assert.Equal(["Type (0x1)", "Function (0x3)", "Export (0x7)", "Code (0xA)"], Node(wasm, "sections").Children.Select(s => Display(wasm, s.Path + ".id")));
        Assert.Equal("add", Value(wasm, "sections[2].export_section.exports[0].name.text"));
    }

    [Fact]
    public void ImageAndMediaFormatsMoreKeyValues()
    {
        TemplateResult gif = Apply("gif");
        Assert.Equal("89a", Value(gif, "header.version"));
        Assert.Equal(4L, Long(gif, "screen.width"));
        Assert.Equal(4, Node(gif, "global_color_table").ChildCount);
        Assert.Equal(5, Node(gif, "blocks").ChildCount);
        Assert.Equal(4L, Long(gif, "blocks[3].image.width"));
        TemplateResult bmp = Apply("bmp");
        Assert.Equal(8L, Long(bmp, "dib_header.bits_per_pixel"));
        Assert.Equal(4, Node(bmp, "color_table").ChildCount);
        Assert.Equal(16L, Node(bmp, "pixel_data").Size);
        TemplateResult ico = Apply("ico");
        Assert.Equal(2L, Long(ico, "directory.count"));
        Assert.Equal(false, Value(ico, "entries[0].is_png"));
        Assert.Equal(true, Value(ico, "entries[1].is_png"));
        TemplateResult tga = Apply("tga");
        Assert.Equal(4L, Long(tga, "header.width"));
        Assert.Equal("HexTGA", Value(tga, "image_id"));
        Assert.Equal("TRUEVISION-XFILE.", Value(tga, "footer.signature"));
        TemplateResult tiff = Apply("tiff");
        Assert.Equal(11L, Long(tiff, "ifd.count"));
        Assert.Equal("HexEditor 1.0", Value(tiff, "ifd.entries[9].text"));
        Assert.Equal(4L, Long(tiff, "ifd.sub_ifd.entries[1].number"));
        Assert.Equal(11L, Long(Apply("tiff.be"), "ifd.count"));
        Assert.Equal(1L, Long(Apply("ogg"), "pages[0].header_type.begin_stream"));
        Assert.Equal("vorbis", Value(Apply("ogg"), "pages[0].codec_signature"));
        Assert.Equal(2L, Long(Apply("midi"), "header.tracks"));
        Assert.Equal("vide", Value(Apply("mp4"), "boxes[1].children[1].children[1].children[1].handler.handler_type"));
        Assert.Equal(1L, Long(Apply("mkv"), "elements[0].children[0].value.number"));
        Assert.Equal("INFO", Value(Apply("wav"), "chunks[2].list.list_type"));
    }

    // ---- 巨大ファイル: 全体を走査せず、先頭か末尾の数セクタだけを読む (TPL-28、TPL-34 の「巨大ファイル」) ----

    private const long HundredGiB = 100L * 1024 * 1024 * 1024;

    private static TemplateDataSource PaddedImage(string key, bool footerAtEnd = false)
    {
        byte[] sample = StandardTemplateSamples.All[key];
        return new TemplateRobustnessTests.Virtual(HundredGiB, (length, offset, destination) =>
        {
            destination.Clear();
            if (footerAtEnd)
            {
                long start = Math.Max(offset, length - sample.Length);
                long end = Math.Min(offset + destination.Length, length);
                if (start < end)
                {
                    sample.AsSpan((int)(start - (length - sample.Length)), (int)(end - start)).CopyTo(destination[(int)(start - offset)..]);
                }

                return;
            }

            if (offset < sample.Length)
            {
                int n = (int)Math.Min(destination.Length, sample.Length - offset);
                sample.AsSpan((int)offset, n).CopyTo(destination);
            }
        });
    }

    [Theory]
    [InlineData("fat")]
    [InlineData("exfat")]
    [InlineData("ntfs")]
    [InlineData("ext")]
    [InlineData("iso9660")]
    [InlineData("vhdx")]
    [InlineData("mbr")]
    public void DiskTemplatesOnHundredGigabyteImagesReadOnlyTheStartOfTheImage(string key)
    {
        var sw = Stopwatch.StartNew();
        TemplateResult r = Apply(key, PaddedImage(key));
        string json = TemplateJsonExporter.ToJson(r);
        sw.Stop();
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning);
        Assert.True(json.Length > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{key}: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void FixedVhdFooterIsReadFromTheEndOfAHundredGigabyteImage()
    {
        TemplateResult r = Apply("vhd", PaddedImage("vhd", footerAtEnd: true));
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.Equal("conectix", Value(r, "footer.cookie"));
        Assert.Equal(HundredGiB - 512, Node(r, "footer").Offset);
        Assert.Equal(2L, Long(r, "footer.disk_type"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning);
    }

    [Fact]
    public void GptAndMbrTemplatesOnHundredGigabyteImagesFinishInAFewSectors()
    {
        var sw = Stopwatch.StartNew();
        TemplateResult gpt = Apply("gpt", new TemplateRobustnessTests.Virtual(HundredGiB, TestDataCatalog.GptImage));
        Assert.Equal(TemplateRunState.Completed, gpt.State);
        Assert.Equal("HXTEST-DATA", Value(gpt, "entries[0].name"));
        Assert.Equal("HXTEST-LOG", Value(gpt, "entries[1].name"));
        Assert.Equal(HundredGiB / 512 - 1, Long(gpt, "header.alternate_lba"));
        Assert.True(gpt.PatternCount < 1000, $"評価済み {gpt.PatternCount}");
        Assert.DoesNotContain(gpt.Diagnostics, d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning);

        TemplateResult mbr = Apply("mbr", new TemplateRobustnessTests.Virtual(HundredGiB, TestDataCatalog.MbrImage));
        Assert.Equal(TemplateRunState.Completed, mbr.State);
        Assert.Equal(7L, Long(mbr, "mbr.partitions[0].type"));
        Assert.Equal(12L, Long(mbr, "mbr.partitions[1].type"));
        Assert.DoesNotContain(mbr.Diagnostics, d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void GptDisksRankAboveMbrAndNtfsAboveMbr()
    {
        // 55 AA を持つ MBR / FAT / NTFS / exFAT / GPT のうち、GPT が最も高く、ファイルシステムが MBR より高い
        int Priority(string name) => Catalog.Find(name)!.Metadata.Priority;
        Assert.True(Priority("gpt") > Priority("fat"));
        Assert.True(Priority("fat") > Priority("mbr"));
        Assert.True(Priority("ntfs") > Priority("mbr"));
        Assert.True(Priority("exfat") > Priority("mbr"));
    }
}
