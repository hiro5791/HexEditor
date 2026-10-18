using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search.Yara;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>FIND-33 YARA スキャン (マネージドの YARA の実装: 構文、文字列の照合、条件、モジュール、ブロック分割、時間の上限)。</summary>
public sealed class YaraTests
{
    private static YaraRuleSet Compile(string source, Action<YaraCompiler>? setup = null)
    {
        var compiler = new YaraCompiler();
        setup?.Invoke(compiler);
        compiler.AddSource(source, "test.yar");
        return compiler.Build();
    }

    private static YaraScanResult Scan(string source, byte[] data, YaraScanOptions? options = null) =>
        YaraScanner.Scan(Compile(source), new YaraBytes(data), options);

    private static bool Matches(string condition, byte[] data, string strings = "") =>
        Scan($"import \"math\"\nimport \"hash\"\nimport \"string\"\nrule t {{ {(strings.Length > 0 ? "strings: " + strings : string.Empty)} condition: ({condition}){(strings.Length > 0 ? " or (false and any of them)" : string.Empty)} }}", data)
            .Rules.Any(r => r.Rule.Name == "t");

    private static string RulesDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "yara-rules");
        return YaraRuleFiles.Write(dir);
    }

    [Fact]
    [Trait(TC, "TC-FIND-33-01")]
    public void Mz_rule_matches_the_exe_at_offset_zero()
    {
        var compiler = new YaraCompiler();
        compiler.AddFile(Path.Combine(RulesDir(), "mz.yar"));
        YaraRuleSet rules = compiler.Build();
        byte[] exe = File.ReadAllBytes(TestDataCatalog.Get("TD-PE-X64"));
        YaraScanResult result = YaraScanner.Scan(rules, new YaraBytes(exe));
        YaraRuleResult mz = Assert.Single(result.Rules);
        Assert.Equal("mz", mz.Rule.Name);
        YaraStringResult s = Assert.Single(mz.Strings);
        Assert.Equal(new YaraStringResult("$a", 0, 2), s);
        Assert.Equal(new byte[] { 0x4D, 0x5A }, exe[..2]);
    }

    [Fact]
    public void Syntax_error_reports_the_file_and_line()
    {
        // TC-FIND-33-02 の Core の部分: bad.yar の 3 行目 (at の後に式がない)。
        var compiler = new YaraCompiler();
        compiler.AddFile(Path.Combine(RulesDir(), "bad.yar"));
        YaraCompileError error = Assert.Single(compiler.Errors);
        Assert.Equal("bad.yar", Path.GetFileName(error.File));
        Assert.Equal(3, error.Line);
        Assert.Contains("syntax error", error.Message, StringComparison.Ordinal);
        Assert.Throws<YaraCompileErrorsException>(() => compiler.Build());
    }

    [Fact]
    [Trait(TC, "TC-FIND-33-03")]
    public void String_across_a_block_boundary_is_found_once()
    {
        // 128 MiB の仮想のデータ (00 の中の 0x3FFFFF0 に 32 バイトの文字列)。
        var data = new SparseString(128L * 1024 * 1024, TestDataCatalog.YaraBoundaryOffset, TestDataCatalog.YaraBoundaryString);
        YaraRuleSet rules = Compile(YaraRuleFiles.Boundary);
        foreach (int block in new[] { YaraScanOptions.DefaultBlockSize, 16 * 1024 * 1024 })
        {
            YaraScanResult result = YaraScanner.Scan(rules, data, new YaraScanOptions { BlockSize = block });
            YaraStringResult s = Assert.Single(Assert.Single(result.Rules).Strings);
            Assert.Equal(0x3FFFFF0, s.Offset);
            Assert.Equal(32, s.Length);
        }

        // 重なり幅を超える文字列はブロックをまたぐと見つからない (制限。画面に表示する)。
        Assert.True(rules.MaxMatchLength <= YaraScanOptions.DefaultOverlap);
    }

    /// <summary>00 の中の 1 か所に文字列を置いた仮想のデータ。</summary>
    private sealed class SparseString(long length, long at, byte[] text) : IYaraData
    {
        public long Length => length;

        public void Read(long offset, Span<byte> destination)
        {
            destination.Clear();
            long from = Math.Max(offset, at), to = Math.Min(offset + destination.Length, at + text.Length);
            if (from < to)
            {
                text.AsSpan((int)(from - at), (int)(to - from)).CopyTo(destination[(int)(from - offset)..]);
            }
        }
    }

    [Fact]
    public void Unsaved_edits_are_scanned_through_the_snapshot()
    {
        // TC-FIND-33-05 の Core の部分: 未保存の編集を含む現在の状態をスキャンする。
        using var doc = new Document(new MemoryByteSource(new byte[1024 * 1024]), Options());
        doc.Overwrite(0x1000, Encoding.ASCII.GetBytes("ADDED-BY-EDIT"));
        YaraScanResult result = YaraScanner.Scan(Compile(YaraRuleFiles.Added), new YaraSnapshotData(doc.Current, 0, doc.Length));
        Assert.Equal(new YaraStringResult("$s", 0x1000, 13), Assert.Single(Assert.Single(result.Rules).Strings));
    }

    [Fact]
    public void Text_string_modifiers()
    {
        byte[] data = Encoding.ASCII.GetBytes("xx Hello world; H\0e\0l\0l\0o\0 helloworld");
        Assert.True(Matches("#a == 1", data, "$a = \"hello\""));
        Assert.True(Matches("#a == 2", data, "$a = \"hello\" nocase"));
        Assert.True(Matches("#a == 1 and @a[1] == 16", data, "$a = \"Hello\" wide"));
        Assert.True(Matches("#a == 2", data, "$a = \"Hello\" wide ascii"));
        Assert.True(Matches("#a == 1 and @a[1] == 3", data, "$a = \"hello\" nocase fullword"));
        Assert.True(Matches("#a == 0", data, "$a = \"world\" fullword") == false);
        byte[] xored = [.. Encoding.ASCII.GetBytes("Hello").Select(b => (byte)(b ^ 0x5A))];
        Assert.True(Matches("$a", xored, "$a = \"Hello\" xor"));
        Assert.True(Matches("$a", xored, "$a = \"Hello\" xor(0x50-0x5F)"));
        Assert.False(Matches("$a", xored, "$a = \"Hello\" xor(1-2)"));
        byte[] b64 = Encoding.ASCII.GetBytes("prefix " + Convert.ToBase64String(Encoding.ASCII.GetBytes("This program cannot")));
        Assert.True(Matches("$a", b64, "$a = \"This program\" base64"));
    }

    [Fact]
    public void Hex_strings_with_wildcards_jumps_and_alternatives()
    {
        byte[] data = [0x00, 0x4D, 0x5A, 0x90, 0x00, 0x03, 0xAA, 0xBB, 0x11, 0x22, 0xCC, 0x7F];
        Assert.True(Matches("$a at 1", data, "$a = { 4D 5A ?? 00 }"));
        Assert.True(Matches("$a at 1", data, "$a = { 4D 5? 9? }"));
        Assert.True(Matches("$a at 1", data, "$a = { 4D [2-3] 00 03 }"));
        Assert.True(Matches("$a at 6", data, "$a = { AA ( BB | CC ) 11 }"));
        Assert.True(Matches("$a at 1", data, "$a = { 4D ~00 }"));
        Assert.True(Matches("!a[1] == 5", data, "$a = { AA BB [1-] CC }"));
        Assert.True(Matches("$a at 0", data, "$a = { ?? 4D }"));
    }

    [Fact]
    public void Regular_expressions()
    {
        byte[] data = Encoding.ASCII.GetBytes("id=12345; ID=777\nnext line\tTAB");
        Assert.True(Matches("#a == 1 and !a[1] == 8", data, "$a = /id=[0-9]+/"));
        Assert.True(Matches("#a == 2", data, "$a = /id=\\d{3}/i"));
        Assert.True(Matches("$a", data, "$a = /777.next/s"));
        Assert.False(Matches("$a", data, "$a = /777.next/"));
        Assert.True(Matches("$a at 0", data, "$a = /^id/"));
        Assert.True(Matches("$a", data, "$a = /TAB$/"));
        byte[] wide = Encoding.Unicode.GetBytes("abcABC123");
        Assert.True(Matches("$a at 6", wide, "$a = /[A-Z]{3}/ wide"));
    }

    [Fact]
    public void Conditions_and_quantifiers()
    {
        byte[] data = Encoding.ASCII.GetBytes("AAxBBxAAxCC");
        string strings = "$a = \"AA\" $b = \"BB\" $c = \"CC\" $d = \"DD\"";
        Assert.True(Matches("#a == 2 and @a[2] == 6 and !a[1] == 2", data, strings + " "));
        Assert.True(Matches("3 of them", data, strings));
        Assert.True(Matches("any of ($a, $b) and none of ($d)", data, strings));
        Assert.True(Matches("all of ($a, $b, $c) and not all of them", data, strings));
        Assert.True(Matches("75% of them", data, strings));
        Assert.True(Matches("$a in (5..7) and $b at 3", data, strings));
        Assert.True(Matches("#a in (0..3) == 1", data, strings));
        Assert.True(Matches("for any i in (1..#a) : (@a[i] == 6)", data, strings));
        Assert.True(Matches("for all of ($a, $b) : (# >= 1 and @ < 10)", data, strings));
        Assert.True(Matches("for 2 of ($a, $b, $c) : ($ in (0..5))", data, strings));
        Assert.True(Matches("for any x in (1, 2, 3) : (x * 2 == 6)", data, strings + " "));
        Assert.True(Matches("filesize == 11 and uint8(2) == 0x78 and uint16be(0) == 0x4141 and int32(0) == 0x42784141", data, strings));
        Assert.True(Matches("(7 \\ 2) == 3 and 7 % 4 == 3 and (1 << 4) == 16 and (0xF0 | 0x0F) == 0xFF and ~0 == -1 and -3 + 1 == -2", data, strings));
        Assert.True(Matches("\"abc\" contains \"b\" and \"ABC\" iequals \"abc\" and \"abc\" startswith \"a\" and \"abc\" matches /B/i", data, strings));
        Assert.True(Matches("not defined @d[1] and defined @a[1]", data, strings));
        Assert.True(Matches("1.5 * 2 == 3", data, strings));
    }

    [Fact]
    public void Modules_math_hash_and_string()
    {
        byte[] data = Encoding.ASCII.GetBytes("abcabcabc");
        Assert.True(Matches("hash.md5(0, filesize) == \"" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data)) + "\"", data));
        Assert.True(Matches("hash.sha256(\"abc\") == \"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\"", data));
        Assert.True(Matches("hash.crc32(\"123456789\") == 0xCBF43926", data));
        Assert.True(Matches("math.entropy(0, filesize) > 1.58 and math.entropy(0, filesize) < 1.59", data));
        Assert.True(Matches("math.count(0x61) == 3 and math.mode() == 0x61 and math.max(3, 5) == 5", data));
        Assert.True(Matches("string.to_int(\"0x10\") == 16 and string.length(\"abc\") == 3", data));
    }

    [Fact]
    public void Pe_module_reads_the_test_executable()
    {
        byte[] exe = File.ReadAllBytes(TestDataCatalog.Get("TD-PE-X64"));
        string rule = "import \"pe\"\nrule p { condition: pe.is_pe == 1 and pe.machine == pe.MACHINE_AMD64 and pe.is_64bit() and pe.number_of_sections > 0 "
            + "and for any s in pe.sections : (s.raw_data_size > 0) and entrypoint == pe.entry_point }";
        Assert.Single(YaraScanner.Scan(Compile(rule), new YaraBytes(exe)).Rules);

        // PE でないデータでは未定義 (偽)。
        Assert.Empty(YaraScanner.Scan(Compile(rule), new YaraBytes(new byte[100])).Rules);
    }

    [Fact]
    public void Rule_references_private_global_tags_meta_and_externals()
    {
        string source = """
            private rule hasA : t1 { strings: $a = "A" condition: $a }
            rule usesA : tag1 tag2 { meta: author = "me" level = 3 strings: $b = "B" condition: hasA and $b and ext_n > 2 and ext_s == "x" }
            rule notReported { strings: $_unused = "Z" condition: true }
            """;
        YaraRuleSet rules = Compile(source, c =>
        {
            c.DefineExternal("ext_n", 1L);
            c.DefineExternal("ext_s", "x");
        });
        YaraScanResult result = YaraScanner.Scan(rules, new YaraBytes("AB"u8.ToArray()),
            new YaraScanOptions { Externals = new Dictionary<string, string> { ["ext_n"] = "5" } });
        Assert.Equal(["usesA", "notReported"], result.Rules.Select(r => r.Rule.Name));
        YaraRule uses = result.Rules[0].Rule;
        Assert.Equal(["tag1", "tag2"], uses.Tags);
        Assert.Equal(new KeyValuePair<string, string>("author", "me"), uses.Meta[0]);
        Assert.Equal("3", uses.Meta[1].Value);

        // 条件だけのルール (文字列の行がない) は、文字列の一致なしの結果になる。
        Assert.Empty(result.Rules[1].Strings);

        // 大域のルールが成り立たないと、名前空間のルールはすべて一致しない。
        YaraScanResult global = Scan("global rule g { condition: filesize > 100 } rule r { condition: true }", "AB"u8.ToArray());
        Assert.Empty(global.Rules);
    }

    [Theory]
    [InlineData("rule a { condition: $x }", 1, "undefined string")]
    [InlineData("rule a { strings: $x = \"a\" condition: true }", 1, "unreferenced string")]
    [InlineData("rule a { condition: true }\nrule a { condition: true }", 2, "duplicated identifier")]
    [InlineData("import \"cuckoo\"\nrule a { condition: true }", 1, "unknown module")]
    [InlineData("rule a { condition: foo }", 1, "undefined identifier")]
    [InlineData("import \"pe\"\nrule a { condition: pe.no_such_field }", 2, "invalid field")]
    [InlineData("rule a { strings: $x = { 4D [-] } condition: $x }", 1, "jump")]
    [InlineData("rule a { strings: $x = /a{1,99999}/ condition: $x }", 1, "repeat")]
    [InlineData("rule a {\n strings:\n  $x = \"abc\n condition: $x }", 3, "unterminated string")]
    [InlineData("rule a { strings: $x = \"a\" xor nocase condition: $x }", 1, "xor nocase")]
    public void Compile_errors_have_lines_and_messages(string source, int line, string message)
    {
        var compiler = new YaraCompiler();
        compiler.AddSource(source, "e.yar");
        YaraCompileError error = Assert.Single(compiler.Errors);
        Assert.Equal(line, error.Line);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Includes_are_resolved_relative_to_the_including_file_and_compiled_rules_are_rejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "yara-include-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "sub", "inner.yar"), "rule inner { condition: true }");
            File.WriteAllText(Path.Combine(dir, "main.yar"), "include \"sub/inner.yar\"\nrule outer { condition: inner }");
            File.WriteAllText(Path.Combine(dir, "loop.yar"), "include \"loop.yar\"");
            File.WriteAllBytes(Path.Combine(dir, "compiled.yarc"), [.. "YARA"u8, 1, 2, 3, 4]);
            var compiler = new YaraCompiler();
            compiler.AddFile(Path.Combine(dir, "main.yar"));
            Assert.Equal(["inner", "outer"], YaraScanner.Scan(compiler.Build(), new YaraBytes([1])).Rules.Select(r => r.Rule.Name));

            var bad = new YaraCompiler();
            bad.AddFile(Path.Combine(dir, "loop.yar"));
            bad.AddFile(Path.Combine(dir, "compiled.yarc"));
            Assert.Equal(2, bad.Errors.Count);
            Assert.Contains("include", bad.Errors[0].Message, StringComparison.Ordinal);
            Assert.Contains("compiled", bad.Errors[1].Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Time_limit_keeps_the_results_found_so_far()
    {
        var time = new Support.ManualTimeProvider();
        var data = new TickingData(64L * 1024 * 1024, time);
        YaraRuleSet rules = Compile("rule z { strings: $a = { 00 00 00 01 } condition: $a or true } rule early { condition: true }");
        YaraScanResult result = YaraScanner.Scan(rules, data, new YaraScanOptions { BlockSize = 1024 * 1024, TimeLimit = TimeSpan.FromSeconds(5), Time = time });
        Assert.True(result.TimedOut);
        Assert.InRange(result.BytesScanned, 1, 63L * 1024 * 1024);
    }

    /// <summary>読むたびに時計を 1 秒進める 00 のデータ。</summary>
    private sealed class TickingData(long length, Support.ManualTimeProvider time) : IYaraData
    {
        public long Length => length;

        public void Read(long offset, Span<byte> destination)
        {
            destination.Clear();
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Too_many_matches_stop_at_the_limit()
    {
        YaraScanResult result = YaraScanner.Scan(Compile("rule z { strings: $a = { 00 } condition: $a }"), new YaraBytes(new byte[5000]),
            new YaraScanOptions { MaxMatchesPerString = 1000 });
        Assert.Equal(1000, Assert.Single(result.Rules).Strings.Count);
        Assert.Equal(["z:$a"], result.TooManyMatches);
    }
}
