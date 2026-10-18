using System.Diagnostics;
using System.Globalization;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Disassembly.Msil;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Disassembly;

/// <summary>ANA-23〜ANA-29 の逆アセンブル (実行形式の解釈、リスティング、相互参照、命令パターン検索、アセンブル) と ANA-27 の MSIL。</summary>
public sealed class DisassemblyFeatureTests
{
    private static Document Doc(byte[] data) => new(new MemoryByteSource(data), Options());

    internal static DisassemblyListing Listing(Document doc, DisasmViewOptions? options = null, ImageSlice? slice = null)
    {
        ExecutableImage image = ExecutableParser.Parse(doc.Current, slice);
        options ??= new DisasmViewOptions { ArchitectureId = image.ArchitectureId ?? DisasmArchitectures.Default };
        return new DisassemblyListing(() => doc.Current, image, options);
    }

    private const ulong Base = TestDataCatalog.PeAsmImageBase;

    // ───────────── ANA-24 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-24-01")]
    public void PeAsmArchitectureImageBaseAndEntryPoint()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Equal(ImageFormat.Pe, image.Format);
        Assert.Equal(DisasmArchitectures.X86_64, image.ArchitectureId);
        Assert.Equal(Base, image.ImageBase);
        Assert.Equal(Base + 0x1000, image.EntryPoint);
        Assert.Empty(image.Issues);
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(image.ToOffset(image.EntryPoint!.Value)!.Value, 3);
        Assert.Equal(ListingLineKind.Heading, lines[0].Kind);
        Assert.Equal("sub rsp, 0x28", lines[1].Instruction!.Text);
        Assert.Equal(Base + 0x1000, lines[1].Address);
    }

    [Fact]
    [Trait(TC, "TC-ANA-24-01")]
    public void Arm64PeIsDetected()
    {
        using Document doc = Doc(TestDataCatalog.PeArm64());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Equal(DisasmArchitectures.Arm64, image.ArchitectureId);
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(image.ToOffset(image.EntryPoint!.Value)!.Value, 3);
        // Capstone 4 は mov の別名を使わず movz と表示する。
        Assert.Equal(["movz w0, #0", "ret"], lines.Where(l => l.Kind == ListingLineKind.Instruction).Take(2).Select(l => l.Instruction!.Text));
    }

    /// <summary>CI のランナーの notepad.exe (コピーを読み取りで開く。TC-ANA-24-01 の手順 1・2)。</summary>
    [Fact]
    [Trait(TC, "TC-ANA-24-01")]
    public void SystemNotepadIsDetected()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!OperatingSystem.IsWindows() || !System.IO.File.Exists(path))
        {
            return;
        }

        byte[] data = System.IO.File.ReadAllBytes(path);
        using Document doc = Doc(data);
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Equal(ImageFormat.Pe, image.Format);
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? DisasmArchitectures.Arm64 : DisasmArchitectures.X86_64, image.ArchitectureId);
        uint lfanew = BitConverter.ToUInt32(data, 0x3C);
        ulong imageBase = BitConverter.ToUInt64(data, (int)lfanew + 24 + 24);
        uint entry = BitConverter.ToUInt32(data, (int)lfanew + 24 + 16);
        Assert.Equal(imageBase, image.ImageBase);
        Assert.Equal(imageBase + entry, image.EntryPoint);
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(image.ToOffset(image.EntryPoint!.Value)!.Value, 2);
        Assert.Equal(ListingLineKind.Heading, lines[0].Kind);
        Assert.Equal(imageBase + entry, lines[1].Address);
    }

    [Fact]
    [Trait(TC, "TC-ANA-24-03")]
    public void ElfSymbolsDynsymAndPlt()
    {
        using Document doc = Doc(TestDataCatalog.ElfX64());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Equal(ImageFormat.Elf, image.Format);
        ImageSymbol main = Assert.Single(image.Symbols, s => s.Name == "main");
        Assert.Equal(ImageSymbolKind.Function, main.Kind);
        Assert.Equal(TestDataCatalog.ElfSymbols["main"], main.Address);
        Assert.Contains(image.Symbols, s => s.Name == "puts" && s.Kind == ImageSymbolKind.Import);
        Assert.Contains(image.Symbols, s => s.Name == "puts@plt" && s.Address == TestDataCatalog.ElfSymbols["puts@plt"]);
        Assert.Contains(image.Symbols, s => s.Name == "exit@plt" && s.Address == TestDataCatalog.ElfSymbols["exit@plt"]);

        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(image.ToOffset(main.Address)!.Value, 1);
        Assert.Equal("main", lines[0].Label);

        // main の call puts@plt のコメント
        ListingLine call = listing.Lines(image.ToOffset(main.Address)!.Value, 6).First(l => l.Instruction?.Mnemonic == "call");
        Assert.Equal("puts@plt", call.Comment);
    }

    [Fact]
    [Trait(TC, "TC-ANA-30-03")]
    public void CppSymbolsAreDemangledInTheSymbolList()
    {
        using Document doc = Doc(TestDataCatalog.ElfCpp());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Contains(image.Symbols, s => s.Name == "foo::bar(int)" && s.RawName == "_ZN3foo3barEi");
        Assert.Contains(image.Symbols, s => s.Name == "foo::Baz::qux() const");
        Assert.DoesNotContain(image.Symbols, s => s.Name.StartsWith("_Z", StringComparison.Ordinal));
        using DisassemblyListing listing = Listing(doc);
        ulong bar = TestDataCatalog.ElfSymbols["_ZN3foo3barEi"];
        Assert.Equal("foo::bar(int)", listing.Lines(image.ToOffset(bar)!.Value, 1)[0].Label);

        // main の call のコメントもデマングルした名前 (ANA-30 の仕様 7: ANA-23 のコメント列)。
        IReadOnlyList<ListingLine> main = listing.Lines(image.ToOffset(TestDataCatalog.ElfSymbols["main"])!.Value, 6);
        Assert.Contains(main, l => l.Comment == "foo::bar(int)");
    }

    /// <summary>
    /// TC-ANA-23-01: dumpbin /disasm の命令列と比べる。dumpbin は Visual Studio のあるランナーにしかないため、見つからなければ何もしない
    /// (CI の windows ランナーで動く)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-23-01")]
    public void EntryPointMatchesDumpbin()
    {
        string? dumpbin = FindDumpbin();
        if (dumpbin is null)
        {
            return;
        }

        string dir = Directory.CreateTempSubdirectory("hexed-dumpbin").FullName;
        try
        {
            string exe = Path.Combine(dir, "TD-ANA-PE-ASM.exe");
            System.IO.File.WriteAllBytes(exe, TestDataCatalog.PeAsm());
            var start = new ProcessStartInfo(dumpbin, $"/nologo /disasm \"{exe}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using Process p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var expected = new List<(ulong Address, int Length, string Text)>();
            foreach (string line in output.Split('\n'))
            {
                // 例: "  0000000140001000: 48 83 EC 28        sub         rsp,28h"
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line,
                    @"^\s+([0-9A-F]{16}): ((?:[0-9A-F]{2} )+)\s*(\S+)\s*(.*)$");
                if (m.Success)
                {
                    expected.Add((Convert.ToUInt64(m.Groups[1].Value, 16), m.Groups[2].Value.Trim().Split(' ').Length,
                        Normalize((m.Groups[3].Value + " " + m.Groups[4].Value).Trim())));
                }
            }

            using Document doc = Doc(TestDataCatalog.PeAsm());
            using DisassemblyListing listing = Listing(doc, new DisasmViewOptions { ArchitectureId = DisasmArchitectures.X86_64, Format = new DisasmFormat { HexStyle = HexStyle.Suffix, UppercaseHex = true } });
            var actual = listing.Lines(0x400, expected.Count + 16).Where(l => l.Kind == ListingLineKind.Instruction).Take(expected.Count)
                .Select(l => (l.Address, l.Length, Normalize(l.Instruction!.Text))).ToList();
            Assert.True(expected.Count >= 11, output);
            for (int i = 0; i < 21 && i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Address, actual[i].Address);
                Assert.Equal(expected[i].Length, actual[i].Length);

                // RIP 相対のメモリ参照は dumpbin が絶対アドレスで書くので、ニーモニックだけを比べる。
                if (expected[i].Text.Contains('[') || actual[i].Item3.Contains('['))
                {
                    Assert.Equal(expected[i].Text.Split('[')[0].Split(' ')[0][..4], actual[i].Item3[..4]);
                }
                else
                {
                    Assert.Equal(expected[i].Text, actual[i].Item3);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        // 構文の差 (ptr の表記、空白、大文字・小文字、先頭の 0) をそろえる。
        static string Normalize(string text)
        {
            string t = text.ToLowerInvariant().Replace("ptr ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\b0*([0-9a-f]+)h\b", "$1");
            return System.Text.RegularExpressions.Regex.Replace(t, @"\b0+([0-9a-f]+)\b", "$1");
        }
    }

    private static string? FindDumpbin()
    {
        string vs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio");
        if (!Directory.Exists(vs))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(vs, "dumpbin.exe", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("Hostx64\\x64", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-24-04")]
    public void MachOFatSlices()
    {
        using Document doc = Doc(TestDataCatalog.MachOFat());
        ExecutableImage fat = ExecutableParser.Parse(doc.Current);
        Assert.Equal(["x86_64", "arm64"], fat.Slices.Select(s => s.Name));
        foreach ((ImageSlice slice, string[] expected) in new[]
        {
            (fat.Slices[0], new[] { "mov eax, 0x2a", "ret" }),
            (fat.Slices[1], new[] { "movz w0, #0x2a", "ret" }),
        })
        {
            using DisassemblyListing listing = Listing(doc, slice: slice);
            Assert.Equal(TestDataCatalog.MachOMainAddress, listing.Image.EntryPoint);
            IReadOnlyList<ListingLine> lines = listing.Lines(listing.Image.ToOffset(listing.Image.EntryPoint!.Value)!.Value, 3);
            Assert.Equal("_main", lines[0].Label);
            Assert.Equal(expected, lines.Skip(1).Select(l => l.Instruction!.Text));
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-24-05")]
    public void VirtualAddressToOffset()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Equal(0x410, image.ToOffset(0x140001010));
        Assert.Equal(".text", image.SectionOfAddress(0x140001010)!.Name);
        Assert.Null(image.ToOffset(0x150000000));
        Assert.Null(image.SectionOfAddress(0x150000000));
    }

    [Fact]
    [Trait(TC, "TC-ANA-24-06")]
    public void ImportCallsGetDllAndFunctionComments()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(0x400, 40);
        Assert.Equal("KERNEL32!CreateFileW", lines.Single(l => l.Address == Base + 0x1018 && l.Kind == ListingLineKind.Instruction).Comment);
        Assert.Equal("KERNEL32!GetStdHandle", lines.Single(l => l.Address == Base + 0x101E && l.Kind == ListingLineKind.Instruction).Comment);
    }

    [Fact]
    public void BrokenHeadersAreReported()
    {
        byte[] pe = TestDataCatalog.PeAsm();
        // セクション表の 2 つ目のファイル位置をファイルの外にする。
        BitConverter.GetBytes(0x7FFF0000).CopyTo(pe, 0x80 + 24 + 0xF0 + 40 + 20);
        using Document doc = Doc(pe);
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.Contains(image.Issues, i => i.Code == "SectionOutside");
        Assert.Equal(".text", image.SectionOfOffset(0x400)!.Name);
    }

    // ───────────── ANA-23 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-23-03")]
    public void InstructionContainingOffset()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);

        // 4 番目の命令 (0x100E の call、5 バイト) の 2 バイト目
        Assert.Equal(0x40E, listing.InstructionStart(0x40F));
        Assert.Equal(0x409, listing.InstructionStart(0x40D));
    }

    [Fact]
    [Trait(TC, "TC-ANA-23-04")]
    public void JumpNearEndOf100GBFile()
    {
        const long Size = 100L * 1024 * 1024 * 1024;
        using var doc = new Document(new VirtualByteSource(Size, VirtualContent.Random, seed: 7), Options());
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
        foreach (long target in new[] { Size - 0x1000, 0x80000001, Size - 1, 0x1000000003 })
        {
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<ListingLine> lines = listing.Lines(target, 48);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            ListingLine first = lines.First(l => l.Kind == ListingLineKind.Instruction);
            Assert.True(first.Offset <= target && target < first.Offset + first.Length, $"0x{target:X}");
            TimeBudget.Limit(elapsed < TimeSpan.FromMilliseconds(100), $"0x{target:X}: {elapsed.TotalMilliseconds} ms");
            Assert.True(elapsed < TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void ScrollingUpAndDownIsConsistent()
    {
        byte[] data = new byte[64 * 1024];
        new Random(1).NextBytes(data);
        using Document doc = Doc(data);
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
        IReadOnlyList<ListingLine> lines = listing.Lines(20_000, 30);
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.Equal(lines[i - 1].Offset + lines[i - 1].Length, lines[i].Offset);
        }

        Assert.Equal(lines[0].Offset, listing.PreviousInstruction(lines[1].Offset));
    }

    [Fact]
    public void EditsInvalidateCheckpoints()
    {
        byte[] data = new byte[0x3000];
        data.AsSpan().Fill(0x90);
        using Document doc = Doc(data);
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
        Assert.Equal("nop", listing.Lines(0x2000, 1)[0].Instruction!.Text);
        doc.Overwrite(0x1FFF, [0xB8]);
        listing.Invalidate(0x1FFF);
        ListingLine line = listing.Lines(0x2000, 1)[0];
        Assert.Equal(0x1FFF, line.Offset);
        Assert.Equal("mov eax, 0x90909090", line.Instruction!.Text);
    }

    [Fact]
    public void MostlyDataWarning()
    {
        byte[] data = new byte[256];
        data.AsSpan().Fill(0x06);
        using Document doc = Doc(data);
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
        Assert.True(DisassemblyListing.MostlyData(listing.Lines(0, 32)));
    }

    // ───────────── ANA-25 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-25-03")]
    public void AutomaticLabels()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);
        listing.ScanLabels(CancellationToken.None);
        IReadOnlyList<ListingLine> lines = listing.Lines(0x400, 60);
        var headings = lines.Where(l => l.Kind == ListingLineKind.Heading).Select(l => (l.Address, l.Label)).ToList();
        Assert.Contains((Base + 0x1040, "sub_140001040"), headings);
        Assert.Contains((Base + 0x1060, "sub_140001060"), headings);
        Assert.Contains((Base + 0x1030, "loc_140001030"), headings);
    }

    [Fact]
    [Trait(TC, "TC-ANA-25-04")]
    public void FindReferencesToFunction()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);
        CrossReference[] refs = [.. DisassemblySearch.FindReferences(listing, doc.Current, Base + 0x1040, DisassemblySearch.DefaultRanges(listing)!,
            CancellationToken.None)];
        Assert.Equal([Base + 0x1004, Base + 0x100E, Base + 0x1013], refs.Select(r => r.Address));
        Assert.All(refs, r => Assert.Equal(ReferenceKind.Call, r.Kind));
    }

    [Fact]
    [Trait(TC, "TC-ANA-25-01")]
    public void BranchTargetOffsetsAndImports()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<ListingLine> lines = listing.Lines(0x404, 1);
        Assert.Equal(0x440, lines[0].TargetOffset);
        ListingLine import = listing.Lines(0x418, 1)[0];
        Assert.True(listing.Image.IsImport(import.Instruction!.Target!.Value));
    }

    // ───────────── ANA-28 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-28-01")]
    public void PatternFindsFunctionPrologues()
    {
        using Document doc = Doc(TestDataCatalog.PeAsm());
        using DisassemblyListing listing = Listing(doc);
        InstructionPattern pattern = InstructionPattern.Parse("push rbp; mov rbp, rsp", regex: false);
        PatternMatch[] matches = [.. DisassemblySearch.FindPattern(listing, doc.Current, pattern, false, DisassemblySearch.DefaultRanges(listing)!,
            CancellationToken.None)];
        Assert.Equal([(Base + 0x1040, 0x440L), (Base + 0x1060, 0x460L)], matches.Select(m => (m.Address, m.Offset)));
    }

    [Theory]
    [Trait(TC, "TC-ANA-28-02")]
    [InlineData("TD-ANA-PE-ASM")]
    [InlineData("TD-PE-X64")]
    public void CallWildcardFindsEveryCall(string id)
    {
        byte[] data = id == "TD-ANA-PE-ASM" ? TestDataCatalog.PeAsm() : TestDataCatalog.PeX64();
        using Document doc = Doc(data);
        using DisassemblyListing listing = Listing(doc);
        IReadOnlyList<(long Offset, long Length)> ranges = DisassemblySearch.DefaultRanges(listing)!;
        PatternMatch[] matches = [.. DisassemblySearch.FindPattern(listing, doc.Current, InstructionPattern.Parse("call *", false), false, ranges,
            CancellationToken.None)];

        // 同じエンジンの線形スイープで call を数える。
        var expected = new List<ulong>();
        using IInstructionDecoder decoder = Disassembler.Create(DisasmArchitectures.X86_64, DisasmFormat.Default);
        foreach ((long offset, long length) in ranges)
        {
            foreach (DisasmInstruction i in Disassembler.Sweep(decoder, data.AsSpan((int)offset, (int)length), listing.AddressOf(offset), DisasmFormat.Default))
            {
                if (i.Mnemonic == "call")
                {
                    expected.Add(i.Address);
                }
            }
        }

        Assert.Equal(expected, matches.Select(m => m.Address));
        if (id == "TD-ANA-PE-ASM")
        {
            Assert.Equal(6, matches.Length);
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-28-03")]
    public void PatternIgnoresCaseAndSpacesAndReportsErrors()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            using Document doc = Doc(DecoderTests.Hex("55 48 89 E5 E8 00 00 00 00 C3"));
            using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
            foreach (string text in new[] { "PUSH RBP; MOV RBP, RSP", "push   rbp;mov rbp,rsp" })
            {
                PatternMatch m = Assert.Single(DisassemblySearch.FindPattern(listing, doc.Current, InstructionPattern.Parse(text, false), false,
                    [(0, 10)], CancellationToken.None));
                Assert.Equal(0, m.Offset);
            }

            InstructionPatternException ex = Assert.Throws<InstructionPatternException>(() => InstructionPattern.Parse("cmp *; {0-x}; jz *", false));
            Assert.Equal("Range", ex.Reason);
            Assert.Equal(7, ex.Position);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void PatternGapsAndRegex()
    {
        using Document doc = Doc(DecoderTests.Hex("48 39 C8 90 90 74 02 C3 C3"));
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions());
        PatternMatch m = Assert.Single(DisassemblySearch.FindPattern(listing, doc.Current, InstructionPattern.Parse("cmp *; {0-3}; je *", false), false,
            [(0, 9)], CancellationToken.None));
        Assert.Equal(7, m.Length);
        Assert.Single(DisassemblySearch.FindPattern(listing, doc.Current, InstructionPattern.Parse(@"^je \w+$", true), true, [(0, 9)],
            CancellationToken.None));
    }

    // ───────────── ANA-29 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-29-02")]
    public void RelativeBranchesUseTheLineAddress()
    {
        AssembleResult jmp = X86Assembler.Assemble("jmp 0x401000", 0x401010, 32);
        Assert.True(jmp.Bytes.SequenceEqual(DecoderTests.Hex("EB EE")) || jmp.Bytes.SequenceEqual(DecoderTests.Hex("E9 EB FF FF FF")),
            Convert.ToHexString(jmp.Bytes));
        Assert.Equal(0x401000UL, jmp.Instructions[0].BranchTarget);

        AssembleResult call = X86Assembler.Assemble("call 0x140002000", 0x140001000, 64);
        Assert.Equal(DecoderTests.Hex("E8 FB 0F 00 00"), call.Bytes);
        Assert.Equal(0x140002000UL, call.Instructions[0].BranchTarget);
    }

    [Theory]
    [InlineData("xor eax, eax", 64, "31C0|33C0")]
    [InlineData("mov eax, 1", 64, "B801000000")]
    [InlineData("mov eax, dword ptr [rbx + rcx*4 + 8]", 64, "8B448B08")]
    [InlineData("push rbp; mov rbp, rsp", 64, "554889E5")]
    [InlineData("nop", 32, "90")]
    [InlineData("mov rax, qword ptr [rip + 0x10]", 64, "488B0510000000")]
    public void AssemblesInstructions(string text, int bitness, string expected)
    {
        AssembleResult r = X86Assembler.Assemble(text, 0x1000, bitness);
        Assert.Contains(Convert.ToHexString(r.Bytes), expected.Split('|'));
    }

    [Theory]
    [InlineData("movx eax, 1", "UnknownMnemonic")]
    [InlineData("mov eax, [rbx", "Syntax")]
    [InlineData("mov eax, qword ptr", "Operands")]
    [InlineData("mov rax, ebx", "Operands")]
    public void AssembleErrors(string text, string reason)
    {
        AssembleException ex = Assert.Throws<AssembleException>(() => X86Assembler.Assemble(text, 0x1000, 64));
        Assert.Equal(reason, ex.Reason);
    }

    // ───────────── ANA-27 ─────────────

    [Fact]
    [Trait(TC, "TC-ANA-27-01")]
    public void MsilTokensAreResolved()
    {
        using Document doc = Doc(TestDataCatalog.NetHello());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        Assert.True(image.IsDotNet);
        using MsilModule module = MsilModule.TryOpen(doc.Current, image)!;
        MsilMethod main = module.FindMethod("Hello.Program", "Main")!;
        IReadOnlyList<MsilLine> lines = module.Disassemble(main);
        Assert.Contains(lines, l => l.Mnemonic == "ldstr" && l.Operand == "\"Hello, World!\"");
        Assert.Contains(lines, l => l.Mnemonic == "call" && l.Operand == "void [System.Console]System.Console::WriteLine(string)");
        Assert.Equal(TestDataCatalog.NetHelloMainIl().ToArray(), module.IlBytes(main));
    }

    [Fact]
    [Trait(TC, "TC-ANA-27-02")]
    public void MsilLinesMapToFileOffsets()
    {
        byte[] data = TestDataCatalog.NetHello();
        using Document doc = Doc(data);
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        using MsilModule module = MsilModule.TryOpen(doc.Current, image)!;
        MsilMethod main = module.FindMethod("Hello.Program", "Main")!;
        MsilLine ldstr = module.Disassemble(main).Single(l => l.Mnemonic == "ldstr");
        Assert.Equal(5, ldstr.Length);
        Assert.Equal(0x72, data[ldstr.Offset]);
        Assert.Equal(main.BodyOffset + 1 + ldstr.IlOffset, ldstr.Offset);
    }

    [Fact]
    [Trait(TC, "TC-ANA-27-03")]
    public void MsilBranchesAndCalls()
    {
        using Document doc = Doc(TestDataCatalog.NetHello());
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        using MsilModule module = MsilModule.TryOpen(doc.Current, image)!;
        MsilMethod check = module.FindMethod("Hello.Program", "Check")!;
        IReadOnlyList<MsilLine> lines = module.Disassemble(check);
        MsilLine branch = lines.Single(l => l.Mnemonic == "brfalse.s");
        Assert.Contains(lines, l => l.Kind == MsilLineKind.Instruction && l.IlOffset == branch.BranchTarget);
        MsilLine call = lines.Single(l => l.Mnemonic == "call");
        Assert.Equal("Helper", module.MethodByToken(call.MethodToken!.Value)!.Name);
    }

    [Fact]
    public void ReadyToRunNativeCode()
    {
        // フレームワークのアセンブリ (ReadyToRun でコンパイル済み) を読み取りだけで開く。
        string path = typeof(Console).Assembly.Location;
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            return;
        }

        using Document doc = Doc(System.IO.File.ReadAllBytes(path));
        ExecutableImage image = ExecutableParser.Parse(doc.Current);
        using MsilModule module = MsilModule.TryOpen(doc.Current, image)!;
        if (!module.IsReadyToRun)
        {
            return;
        }

        NativeCode? code = module.Types.SelectMany(t => t.Methods).Select(module.NativeCodeOf).FirstOrDefault(c => c is not null);
        Assert.NotNull(code);
        Assert.True(code.Length > 0 && code.Offset > 0);
    }
}
