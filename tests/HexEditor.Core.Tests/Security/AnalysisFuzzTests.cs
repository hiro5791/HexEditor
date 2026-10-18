using System.Text;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Demangling;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Disassembly.Msil;
using HexEditor.Core.Engine;
using HexEditor.Core.Search.Yara;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// 解析の自前の解釈 (ANA-23〜30 の実行形式・.NET メタデータ・逆アセンブラ・デマングラ、FIND-33 の YARA) のファジング (フェーズ 4 の監査)。
/// 壊れた入力でも例外・無限ループ・際限のないメモリにならないこと。
/// </summary>
public sealed class AnalysisFuzzTests
{
    private static byte[][] Executables() =>
    [
        TestDataCatalog.PeX64(), TestDataCatalog.PeAsm(), TestDataCatalog.PeArm64(), TestDataCatalog.ElfX64(), TestDataCatalog.ElfCpp(),
        TestDataCatalog.MachO64(), TestDataCatalog.MachOFat(), TestDataCatalog.NetHello(),
    ];

    [Fact]
    public void Executable_parsers_survive_mutated_images()
    {
        Fuzz.Run(Fuzz.Cases(2401, Executables(), mutations: 3000, randoms: 200, maxRandomLength: 4096), data =>
        {
            ExecutableImage image = ExecutableParser.Parse(data);
            foreach (ImageSection s in image.Sections)
            {
                _ = s.MappedEnd;
                _ = s.ContainsAddress(image.ImageBase);
            }

            if (image.EntryPoint is { } entry)
            {
                _ = image.ToOffset(entry);
            }

            foreach (ImageSlice slice in image.Slices)
            {
                _ = ExecutableParser.Parse(data, slice);
            }
        });
    }

    [Fact]
    public void Disassembly_listing_survives_mutated_images()
    {
        Fuzz.Run(Fuzz.Cases(2402, Executables(), mutations: 600), data =>
        {
            using var doc = new Document(new MemoryByteSource(data), Options());
            ExecutableImage image = ExecutableParser.Parse(doc.Current);
            var options = new DisasmViewOptions { ArchitectureId = image.ArchitectureId ?? DisasmArchitectures.Default };
            using var listing = new DisassemblyListing(() => doc.Current, image, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            listing.ScanLabels(cts.Token);
            _ = listing.Lines(0, 64);
            _ = listing.Lines(Math.Max(0, doc.Length - 16), 16);
        }, allowed: ex => ex is DisassemblerUnavailableException or OperationCanceledException);
    }

    [Fact]
    public void Msil_reader_survives_mutated_assemblies()
    {
        byte[] hello = TestDataCatalog.NetHello();
        Fuzz.Run(Fuzz.Cases(2403, [hello], mutations: 1500), data =>
        {
            using var doc = new Document(new MemoryByteSource(data), Options());
            ExecutableImage image = ExecutableParser.Parse(doc.Current);
            using MsilModule? module = MsilModule.TryOpen(doc.Current, image);
            if (module is null)
            {
                return;
            }

            foreach (MsilType type in module.Types)
            {
                foreach (MsilMethod method in type.Methods)
                {
                    _ = module.Disassemble(method);
                    _ = module.IlBytes(method);
                    _ = module.NativeCodeOf(method);
                }
            }
        });
    }

    /// <summary>入れ子の型の宣言が輪になったメタデータでも止まる (NestedClass の表は検証されない)。</summary>
    [Fact]
    public void Msil_reader_stops_on_nested_type_cycles()
    {
        byte[] data = NestedCycleAssembly();
        Fuzz.Run([data], d =>
        {
            using var doc = new Document(new MemoryByteSource(d), Options());
            ExecutableImage image = ExecutableParser.Parse(doc.Current);
            using MsilModule? module = MsilModule.TryOpen(doc.Current, image);
            Assert.NotNull(module);
            Assert.NotEmpty(module.Types);
        }, timeoutMs: 10_000);
    }

    /// <summary>型 A・B が互いを宣言する型として持つ (NestedClass の表で輪になる) DLL。</summary>
    private static byte[] NestedCycleAssembly()
    {
        var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Cycle.dll"), metadata.GetOrAddGuid(new Guid("6d3c0d12-6f8c-4c1e-9a52-1f7a0c3d4e60")), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Cycle"), new Version(1, 0, 0, 0), default, default, 0, System.Reflection.AssemblyHashAlgorithm.Sha1);
        var fields = System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1);
        var methods = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(1);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, fields, methods);
        var a = metadata.AddTypeDefinition(System.Reflection.TypeAttributes.NestedPublic, metadata.GetOrAddString("N"), metadata.GetOrAddString("A"), default, fields, methods);
        var b = metadata.AddTypeDefinition(System.Reflection.TypeAttributes.NestedPublic, metadata.GetOrAddString("N"), metadata.GetOrAddString("B"), default, fields, methods);
        metadata.AddNestedType(a, b);
        metadata.AddNestedType(b, a);
        var pe = new System.Reflection.PortableExecutable.ManagedPEBuilder(
            new System.Reflection.PortableExecutable.PEHeaderBuilder(imageCharacteristics: System.Reflection.PortableExecutable.Characteristics.ExecutableImage
                | System.Reflection.PortableExecutable.Characteristics.Dll),
            new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata, suppressValidation: true),
            new System.Reflection.Metadata.BlobBuilder(),
            flags: System.Reflection.PortableExecutable.CorFlags.ILOnly,
            deterministicIdProvider: _ => new System.Reflection.Metadata.BlobContentId(new Guid("a1b2c3d4-0000-4000-8000-000000000002"), 1));
        var output = new System.Reflection.Metadata.BlobBuilder();
        pe.Serialize(output);
        return output.ToArray();
    }

    [Fact]
    public void Own_decoders_never_throw_or_overrun()
    {
        var random = new Random(2404);
        foreach (DisasmArchitecture arch in DisasmArchitectures.BuiltIn)
        {
            IInstructionDecoder decoder;
            try
            {
                decoder = Disassembler.Create(arch, DisasmFormat.Default);
            }
            catch (DisassemblerUnavailableException)
            {
                continue;
            }

            using (decoder)
            {
                var cases = new List<byte[]>();
                for (int i = 0; i < (arch.Engine == DisasmEngine.Managed ? 20_000 : 2_000); i++)
                {
                    byte[] code = new byte[random.Next(0, 40)];
                    random.NextBytes(code);
                    if (code.Length > 1 && random.Next(4) == 0)
                    {
                        // LEB128 や接頭辞の続きの形 (上位ビットを立てる)。
                        for (int j = 1; j < code.Length; j++)
                        {
                            code[j] |= 0x80;
                        }
                    }

                    cases.Add(code);
                }

                Fuzz.Run(cases, code =>
                {
                    DisasmInstruction? ins = decoder.Decode(code, 0x1000, detail: true);
                    if (ins is not null)
                    {
                        Assert.InRange(ins.Length, 0, Math.Max(code.Length, 0));
                        _ = ins.Text;
                    }

                    if (code.Length > 0)
                    {
                        _ = Disassembler.Sweep(decoder, code, 0x1000, DisasmFormat.Default, 64);
                    }
                }, describe: c => arch.Id + " " + Convert.ToHexString(c));
            }
        }
    }

    /// <summary>
    /// 置換 (後方参照) を重ねて出力が指数的に大きくなる名前も、短い時間で失敗として返る (出力の長さと深さの上限)。
    /// </summary>
    [Fact]
    public void Demanglers_bound_exponential_back_references()
    {
        var names = new List<string>();

        // Itanium: テンプレート引数の置換を重ねる (S_、S0_、…)。
        var it = new StringBuilder("_Z1fI");
        for (int i = 0; i < 40; i++)
        {
            it.Append("St4pairI").Append(i == 0 ? "ii" : $"S{(i - 1).ToString("X", System.Globalization.CultureInfo.InvariantCulture)}_S{(i - 1).ToString("X", System.Globalization.CultureInfo.InvariantCulture)}_").Append('E');
        }

        names.Add(it.Append("EvT_").ToString());

        // Rust v0: 型の後方参照 (B<base-62>_) で同じタプルを 2 回ずつ参照する。
        var rust = new StringBuilder("_RINvC5crate1fT");
        rust.Append("ll");
        for (int i = 0; i < 60; i++)
        {
            rust.Append("TB").Append(i == 0 ? "_" : (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "_").Append("B_E");
        }

        names.Add(rust.Append("EE").ToString());
        names.Add("_RINvC5crate1f" + string.Concat(Enumerable.Repeat("TB_B_", 200)) + "E");

        // Microsoft: 型の後方参照 (0〜9) を重ねたテンプレート。
        var ms = new StringBuilder("?f@@YAXV?$A@");
        for (int i = 0; i < 30; i++)
        {
            ms.Append("V?$B@").Append("H00@");
        }

        names.Add(ms.Append("@@Z").ToString());

        // D: 後方参照 (Q) を重ねる。
        names.Add("_D1a" + string.Concat(Enumerable.Repeat("S1aQaQa", 300)) + "FZv");
        names.Add("_Z" + string.Concat(Enumerable.Repeat("N", 5000)) + "1aE");
        names.Add("?" + string.Concat(Enumerable.Repeat("?$A@", 5000)));
        names.Add("_R" + string.Concat(Enumerable.Repeat("N", 5000)));
        Fuzz.Run(names, n => Demangler.Demangle(n), timeoutMs: 10_000, maxAllocatedBytes: 1L << 30);
    }

    // ---- YARA ----

    private static readonly string[] Rules =
    [
        "rule mz { strings: $a = { 4D 5A } condition: $a at 0 }",
        "rule t { strings: $a = \"hello\" nocase wide ascii fullword $b = \"x\" xor(1-200) $c = \"This program\" base64 condition: any of them }",
        "rule h { strings: $a = { 4D [2-3] 00 03 } $b = { AA ( BB | CC ) 11 } $c = { 4D ~00 ?? 5? } $d = { AA BB [1-] CC } condition: #a > 0 or $b or $c or !d[1] == 5 }",
        "rule r { strings: $a = /id=[0-9]+/ $b = /id=\\d{3}/i $c = /777.next/s $d = /^id/ $e = /[A-Z]{3}/ wide condition: any of them }",
        "rule c { strings: $a = \"AA\" $b = \"BB\" condition: for any i in (1..#a) : (@a[i] == 6) and for all of ($a, $b) : (# >= 1 and @ < 10) and 50% of them }",
        "rule n { condition: filesize > 2 and uint8(2) == 0x78 and uint16be(0) == 0x4141 and int32(0) == 0x42784141 and (7 \\ 2) == 3 and 7 % 4 == 3 and (1 << 4) == 16 }",
        "import \"math\"\nimport \"hash\"\nimport \"string\"\nrule m { condition: hash.md5(0, filesize) == \"x\" or math.entropy(0, filesize) > 1.5 or math.count(0x61) == 3 or string.to_int(\"0x10\") == 16 or hash.crc32(0, 100) == 1 }",
        "import \"pe\"\nrule p { condition: pe.is_pe == 1 and pe.machine == pe.MACHINE_AMD64 and pe.is_64bit() and for any s in pe.sections : (s.raw_data_size > 0) and entrypoint == pe.entry_point and pe.imports(\"kernel32.dll\") and pe.exports(\"x\") and pe.number_of_exports >= 0 }",
        "import \"elf\"\nrule e { condition: elf.type == elf.ET_EXEC and for any s in elf.sections : (s.name == \".text\") and elf.number_of_segments > 0 and elf.entry_point > 0 }",
        "global private rule g : tag { meta: a = 1 b = \"s\" condition: filesize > 0 } rule after { condition: g and true }",
        "import \"console\"\nrule k { condition: console.log(\"x\") and console.hex(filesize) }",
    ];

    private const string RuleAlphabet = "abcdefghijklmnopqrstuvwxyz$#@!()[]{}|?~\"/\\=<>.,:;-+*%&^0123456789 \n\tAFxXwideasciinocasefullwordprivateglobalruleconditionstringsof them all any for in at ";

    [Fact]
    public void Yara_compiler_and_scanner_survive_mutated_rules()
    {
        var random = new Random(3301);
        var sources = new List<string>(Rules);
        for (int i = 0; i < 6000; i++)
        {
            sources.Add(Fuzz.MutateText(random, Rules[random.Next(Rules.Length)], RuleAlphabet));
        }

        byte[] pe = TestDataCatalog.PeX64(), elf = TestDataCatalog.ElfX64();
        byte[] text = Encoding.ASCII.GetBytes("xx Hello world; id=12345 ID=777\nnext AAxBBxAAxCC");
        Fuzz.Run(sources, source =>
        {
            var compiler = new YaraCompiler();
            compiler.AddSource(source, "fuzz.yar");
            if (compiler.Errors.Count > 0)
            {
                Assert.Throws<YaraCompileErrorsException>(() => compiler.Build());
                return;
            }

            YaraRuleSet rules = compiler.Build();
            var options = new YaraScanOptions { TimeLimit = TimeSpan.FromSeconds(3) };
            foreach (byte[] data in new[] { pe, elf, text, [] })
            {
                _ = YaraScanner.Scan(rules, new YaraBytes(data), options);
            }
        }, describe: s => s);
    }

    [Fact]
    public void Yara_modules_survive_mutated_executables()
    {
        var compiler = new YaraCompiler();
        foreach (string rule in Rules.Where(r => r.Contains("import \"pe\"") || r.Contains("import \"elf\"")))
        {
            compiler.AddSource(rule);
        }

        compiler.AddSource("import \"pe\"\nimport \"elf\"\nrule every { condition: for any i in (0..pe.number_of_sections - 1) : (pe.sections[i].name == \"x\") or "
            + "for any i in (0..pe.number_of_imports - 1) : (pe.imports(\"kernel32.dll\", \"ExitProcess\")) or pe.overlay.size > 0 or "
            + "for any i in (0..elf.number_of_sections - 1) : (elf.sections[i].size > 0) or pe.imphash() == \"x\" or pe.section_index(\".text\") == 0 or "
            + "for any i in (0..elf.number_of_segments - 1) : (elf.segments[i].type == 1) or pe.is_dll() or pe.timestamp > 0 or "
            + "pe.rva_to_offset(pe.entry_point) > 0 or pe.data_directories[1].virtual_address > 0 or pe.linker_version.major > 0 }");
        YaraRuleSet rules = compiler.Build();
        Fuzz.Run(Fuzz.Cases(3302, [TestDataCatalog.PeX64(), TestDataCatalog.PeAsm(), TestDataCatalog.NetHello(), TestDataCatalog.ElfX64(), TestDataCatalog.ElfCpp()],
            mutations: 3000), data => YaraScanner.Scan(rules, new YaraBytes(data), new YaraScanOptions { TimeLimit = TimeSpan.FromSeconds(3) }));
    }

    /// <summary>病的なルール (深い入れ子、巨大な範囲のループ、壊滅的なバックトラックを起こす正規表現) も時間の上限で止まる。</summary>
    [Fact]
    public void Yara_pathological_rules_are_bounded()
    {
        string deep = "rule d { condition: " + new string('(', 5000) + "true" + new string(')', 5000) + " }";
        string deepNot = "rule d { condition: " + string.Concat(Enumerable.Repeat("not ", 20000)) + "true }";
        string deepHex = "rule d { strings: $a = { " + string.Concat(Enumerable.Repeat("( ", 3000)) + "41" + string.Concat(Enumerable.Repeat(" )", 3000)) + " } condition: $a }";
        string deepRe = "rule d { strings: $a = /" + new string('(', 3000) + "a" + new string(')', 3000) + "/ condition: $a }";
        string loop = "rule l { condition: for all i in (0..0x7FFFFFFFFFFFFFFF) : (i >= 0) }";
        string nested = "rule l { condition: for all i in (0..100000) : (for all j in (0..100000) : (i + j >= 0)) }";
        string backtrack = "rule b { strings: $a = /(a+)+b/ $b = /(a|aa)*c/ $c = /a{0,30}a{0,30}a{0,30}x/ condition: any of them }";
        string manyJumps = "rule j { strings: $a = { 61 [0-] 61 [0-] 61 [0-] 61 [0-] 62 } condition: $a }";
        string longString = "rule s { strings: $a = \"" + new string('a', 100_000) + "\" condition: $a }";
        string many = string.Concat(Enumerable.Range(0, 3000).Select(i => $"rule r{i} {{ strings: $a = \"a{i}\" condition: $a }}\n"));
        string recursion = "rule a { condition: b } rule b { condition: a }";
        string plusChain = "rule c { condition: " + string.Join(" + ", Enumerable.Repeat("1", 100_000)) + " > 0 }";
        string andChain = "rule c { condition: " + string.Join(" and ", Enumerable.Repeat("true", 100_000)) + " }";
        string memberChain = "import \"pe\"\nrule c { condition: pe" + string.Concat(Enumerable.Repeat(".sections[0]", 50_000)) + " == 0 }";
        string manyHexJumps = "rule j { strings: $a = { 61 " + string.Concat(Enumerable.Repeat("[0-1] 61 ", 5000)) + "} condition: $a }";
        byte[] data = Encoding.ASCII.GetBytes(new string('a', 64 * 1024));
        Fuzz.Run(new[] { deep, deepNot, deepHex, deepRe, loop, nested, backtrack, manyJumps, longString, many, recursion, plusChain, andChain, memberChain, manyHexJumps }, source =>
        {
            var compiler = new YaraCompiler();
            compiler.AddSource(source, "p.yar");
            if (compiler.Errors.Count > 0)
            {
                return;
            }

            YaraScanResult result = YaraScanner.Scan(compiler.Build(), new YaraBytes(data), new YaraScanOptions { TimeLimit = TimeSpan.FromSeconds(2) });
            _ = result.Rules.Count;
        }, timeoutMs: 30_000, maxAllocatedBytes: 2L << 30, describe: s => s.Length > 120 ? s[..120] + "…" : s);
    }
}
