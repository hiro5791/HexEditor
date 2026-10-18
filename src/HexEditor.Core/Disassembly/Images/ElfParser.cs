namespace HexEditor.Core.Disassembly.Images;

/// <summary>
/// ELF (32 / 64 bit、リトル / ビッグエンディアン) の解釈 (ANA-24 の仕様 1・6): セクション (なければプログラムヘッダの PT_LOAD)、
/// エントリポイント、<c>.symtab</c>・<c>.dynsym</c>、PLT の項目 (<c>関数名@plt</c>) と GOT の項目。
/// </summary>
internal static class ElfParser
{
    private const int MaxSections = 4096;
    private const int MaxSymbols = 10_000_000;

    public static bool IsElf(ImageData d) => d.Has(0, 0x34) && d.U8(0) == 0x7F && d.U8(1) == (byte)'E' && d.U8(2) == (byte)'L' && d.U8(3) == (byte)'F'
        && d.U8(4) is 1 or 2 && d.U8(5) is 1 or 2;

    private sealed record Section(string Name, uint Type, ulong Flags, ulong Addr, long Offset, long Size, uint Link, uint Info, long EntSize);

    public static ExecutableImage Parse(ImageData d)
    {
        bool wide = d.U8(4) == 2;
        d.BigEndian = d.U8(5) == 2;
        var issues = new List<ImageIssue>();
        ushort machine = d.U16(18);
        ulong entry = d.Word(24, wide);
        long phoff = (long)d.Word(wide ? 32 : 28, wide);
        long shoff = (long)d.Word(wide ? 40 : 32, wide);
        uint flags = d.U32(wide ? 48 : 36);
        int phentsize = d.U16(wide ? 54 : 42);
        int phnum = d.U16(wide ? 56 : 44);
        int shentsize = d.U16(wide ? 58 : 46);
        int shnum = d.U16(wide ? 60 : 48);
        int shstrndx = d.U16(wide ? 62 : 50);
        string? arch = machine switch
        {
            3 => DisasmArchitectures.X86_32,
            62 => DisasmArchitectures.X86_64,
            40 => (entry & 1) != 0 ? DisasmArchitectures.Thumb : DisasmArchitectures.Arm,
            183 => DisasmArchitectures.Arm64,
            8 or 10 => wide ? DisasmArchitectures.Mips64 : DisasmArchitectures.Mips32,
            20 => DisasmArchitectures.Ppc32,
            21 => DisasmArchitectures.Ppc64,
            2 => DisasmArchitectures.Sparc,
            43 => DisasmArchitectures.SparcV9,
            22 => DisasmArchitectures.SystemZ,
            243 => wide
                ? (flags & 1) != 0 ? DisasmArchitectures.RiscV64C : DisasmArchitectures.RiscV64
                : (flags & 1) != 0 ? DisasmArchitectures.RiscV32C : DisasmArchitectures.RiscV32,
            247 => DisasmArchitectures.Ebpf,
            4 => "m68k-020",
            203 => DisasmArchitectures.XCore,
            140 => DisasmArchitectures.Tms320C64x,
            _ => null,
        };

        // プログラムヘッダ (イメージベースと、セクションがない場合の範囲)。
        var loads = new List<ImageSection>();
        ulong? imageBase = null;
        for (int i = 0; i < Math.Min(phnum, 4096); i++)
        {
            long p = phoff + (long)i * phentsize;
            if (!d.Has(p, wide ? 56 : 32))
            {
                issues.Add(new ImageIssue("ProgramHeadersOutside"));
                break;
            }

            uint type = d.U32(p);
            if (type != 1)
            {
                continue;
            }

            uint pflags = d.U32(wide ? p + 4 : p + 24);
            long offset = (long)d.Word(wide ? p + 8 : p + 4, wide);
            ulong vaddr = d.Word(wide ? p + 16 : p + 8, wide);
            long filesz = (long)d.Word(wide ? p + 32 : p + 16, wide);
            ulong memsz = d.Word(wide ? p + 40 : p + 20, wide);
            imageBase = imageBase is null ? vaddr - (ulong)offset : Math.Min(imageBase.Value, vaddr - (ulong)offset);
            filesz = Math.Clamp(filesz, 0, Math.Max(0, d.Length - offset));
            loads.Add(new ImageSection("LOAD" + loads.Count, vaddr, memsz, offset, filesz, (pflags & 1) != 0, (pflags & 4) != 0, (pflags & 2) != 0));
        }

        var sections = new List<Section>();
        if (shoff > 0 && shnum > 0)
        {
            if (shnum > MaxSections || !d.Has(shoff, (long)shnum * shentsize))
            {
                issues.Add(new ImageIssue("SectionTableOutside"));
            }
            else
            {
                for (int i = 0; i < shnum; i++)
                {
                    long s = shoff + (long)i * shentsize;
                    sections.Add(new Section(
                        string.Empty,
                        d.U32(s + 4),
                        d.Word(s + 8, wide),
                        d.Word(wide ? s + 16 : s + 12, wide),
                        (long)d.Word(wide ? s + 24 : s + 16, wide),
                        (long)d.Word(wide ? s + 32 : s + 20, wide),
                        d.U32(wide ? s + 40 : s + 24),
                        d.U32(wide ? s + 44 : s + 28),
                        (long)d.Word(wide ? s + 56 : s + 36, wide)));
                }

                if (shstrndx < sections.Count)
                {
                    Section strings = sections[shstrndx];
                    for (int i = 0; i < sections.Count; i++)
                    {
                        try
                        {
                            sections[i] = sections[i] with { Name = d.CString(strings.Offset + d.U32(shoff + (long)i * shentsize), 256) };
                        }
                        catch (ImageFormatException)
                        {
                            issues.Add(new ImageIssue("SectionNamesBroken"));
                            break;
                        }
                    }
                }
            }
        }

        var imageSections = new List<ImageSection>();
        foreach (Section s in sections)
        {
            if ((s.Flags & 2) == 0 || s.Addr == 0 && s.Type != 8)
            {
                continue;
            }

            long fileSize = s.Type == 8 ? 0 : Math.Clamp(s.Size, 0, Math.Max(0, d.Length - s.Offset));
            if (s.Type != 8 && s.Offset + s.Size > d.Length)
            {
                issues.Add(new ImageIssue("SectionOutside", s.Name));
            }

            imageSections.Add(new ImageSection(s.Name, s.Addr, (ulong)s.Size, s.Offset, fileSize, (s.Flags & 4) != 0, true, (s.Flags & 1) != 0));
        }

        if (imageSections.Count == 0)
        {
            imageSections = loads;
        }

        var symbols = new List<ImageSymbol>();
        var imports = new Dictionary<ulong, string>();
        List<string>? dynNames = null;
        foreach (Section s in sections.Where(s => s.Type is 2 or 11))
        {
            try
            {
                List<string> names = Symbols(d, s, sections, wide, symbols);
                if (s.Type == 11)
                {
                    dynNames = names;
                }
            }
            catch (ImageFormatException ex)
            {
                issues.Add(new ImageIssue("SymbolTableBroken", s.Name + " " + ex.Detail));
            }
        }

        if (dynNames is not null)
        {
            try
            {
                Plt(d, sections, wide, machine, dynNames, imports, symbols);
            }
            catch (ImageFormatException ex)
            {
                issues.Add(new ImageIssue("PltBroken", ex.Detail));
            }
        }

        return new ExecutableImage(ImageFormat.Elf, arch, d.BigEndian, imageBase ?? 0, entry == 0 ? null : entry, imageSections, symbols, imports, [],
            issues, false, []);
    }

    /// <summary>シンボル表を読む。添字の順の名前の一覧を返す (再配置の解決に使う)。</summary>
    private static List<string> Symbols(ImageData d, Section table, List<Section> sections, bool wide, List<ImageSymbol> symbols)
    {
        long entSize = table.EntSize > 0 ? table.EntSize : wide ? 24 : 16;
        long count = Math.Min(table.Size / entSize, MaxSymbols);
        Section strings = table.Link < sections.Count ? sections[(int)table.Link] : throw new ImageFormatException("NoStringTable");
        var names = new List<string>((int)Math.Min(count, 1_000_000));
        for (long i = 0; i < count; i++)
        {
            long e = table.Offset + i * entSize;
            uint nameIndex = d.U32(e);
            byte info = d.U8(wide ? e + 4 : e + 12);
            ushort shndx = d.U16(wide ? e + 6 : e + 14);
            ulong value = d.Word(wide ? e + 8 : e + 4, wide);
            string name = nameIndex == 0 ? string.Empty : d.CString(strings.Offset + nameIndex, 4096);
            names.Add(name);
            int type = info & 0xF;
            if (name.Length == 0 || shndx == 0 || value == 0 || type is not (1 or 2 or 0) || type == 0 && (info >> 4) == 0)
            {
                continue;
            }

            symbols.Add(new ImageSymbol(name, name, value, type == 2 ? ImageSymbolKind.Function : ImageSymbolKind.Data));
        }

        return names;
    }

    /// <summary>PLT と GOT の項目に名前を付ける (x86 / x64 / ARM / ARM64 の標準的な PLT の形)。</summary>
    private static void Plt(ImageData d, List<Section> sections, bool wide, ushort machine, List<string> dynNames,
        Dictionary<ulong, string> imports, List<ImageSymbol> symbols)
    {
        Section? relPlt = sections.FirstOrDefault(s => s.Name is ".rela.plt" or ".rel.plt");
        if (relPlt is null)
        {
            return;
        }

        bool rela = relPlt.Type == 4;
        long entSize = relPlt.EntSize > 0 ? relPlt.EntSize : wide ? rela ? 24 : 16 : rela ? 12 : 8;
        Section? pltSec = sections.FirstOrDefault(s => s.Name == ".plt.sec");
        Section? plt = sections.FirstOrDefault(s => s.Name == ".plt");
        (int header, int stride) = machine switch
        {
            62 or 3 => pltSec is not null ? (0, 16) : (16, 16),
            183 => (32, 16),
            40 => (20, 12),
            _ => (-1, 0),
        };
        Section? stubs = pltSec ?? plt;
        long count = Math.Min(relPlt.Size / entSize, MaxSymbols);
        for (long i = 0; i < count; i++)
        {
            long e = relPlt.Offset + i * entSize;
            ulong offset = d.Word(e, wide);
            ulong info = d.Word(e + (wide ? 8 : 4), wide);
            long symIndex = wide ? (long)(info >> 32) : (long)(info >> 8);
            if (symIndex <= 0 || symIndex >= dynNames.Count || dynNames[(int)symIndex].Length == 0)
            {
                continue;
            }

            string name = dynNames[(int)symIndex];
            imports[offset] = name;
            symbols.Add(new ImageSymbol(name, name, offset, ImageSymbolKind.Import));
            if (stubs is not null && header >= 0)
            {
                ulong stub = stubs.Addr + (ulong)(header + i * stride);
                string pltName = name + "@plt";
                imports[stub] = pltName;
                symbols.Add(new ImageSymbol(pltName, pltName, stub, ImageSymbolKind.Import));
            }
        }
    }
}
