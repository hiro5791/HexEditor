namespace HexEditor.Core.Disassembly.Images;

/// <summary>
/// Mach-O (32 / 64 bit) とユニバーサル (fat) バイナリの解釈 (ANA-24 の仕様 1・6): セグメントのセクション、LC_MAIN のエントリポイント、
/// シンボル表 (LC_SYMTAB)、遅延バインドのスタブ (__stubs と間接シンボル表)。
/// </summary>
internal static class MachOParser
{
    private const uint Magic32 = 0xFEEDFACE;
    private const uint Magic64 = 0xFEEDFACF;
    private const uint Cigam32 = 0xCEFAEDFE;
    private const uint Cigam64 = 0xCFFAEDFE;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;

    public static bool IsFat(ImageData d)
    {
        if (!d.Has(0, 8))
        {
            return false;
        }

        bool saved = d.BigEndian;
        d.BigEndian = true;
        uint magic = d.U32(0);
        uint count = d.U32(4);
        d.BigEndian = saved;

        // Java のクラスファイル (同じ CAFEBABE で始まる) と区別するため、スライスの数を 1〜30 に限る。
        return magic is FatMagic or FatMagic64 && count is > 0 and <= 30;
    }

    public static bool IsMachO(ImageData d, long at) => d.Has(at, 28) && Magic(d, at) is Magic32 or Magic64 or Cigam32 or Cigam64;

    private static uint Magic(ImageData d, long at)
    {
        bool saved = d.BigEndian;
        d.BigEndian = false;
        uint m = d.U32(at);
        d.BigEndian = saved;
        return m;
    }

    public static string? ArchitectureOf(uint cpu) => cpu switch
    {
        7 => DisasmArchitectures.X86_32,
        0x01000007 => DisasmArchitectures.X86_64,
        12 => DisasmArchitectures.Arm,
        0x0100000C or 0x0200000C => DisasmArchitectures.Arm64,
        18 => DisasmArchitectures.Ppc32,
        0x01000012 => DisasmArchitectures.Ppc64,
        _ => null,
    };

    public static string CpuName(uint cpu) => cpu switch
    {
        7 => "i386",
        0x01000007 => "x86_64",
        12 => "arm",
        0x0100000C => "arm64",
        0x0200000C => "arm64_32",
        18 => "ppc",
        0x01000012 => "ppc64",
        _ => $"cpu 0x{cpu:X}",
    };

    /// <summary>fat バイナリのスライスの一覧 (ヘッダだけの結果。スライスを選んで <see cref="Parse"/> する)。</summary>
    public static ExecutableImage ParseFat(ImageData d)
    {
        d.BigEndian = true;
        bool wide = d.U32(0) == FatMagic64;
        uint count = d.U32(4);
        var slices = new List<ImageSlice>();
        var issues = new List<ImageIssue>();
        for (int i = 0; i < count; i++)
        {
            long e = 8 + i * (wide ? 32L : 20L);
            if (!d.Has(e, wide ? 32 : 20))
            {
                issues.Add(new ImageIssue("FatHeaderBroken"));
                break;
            }

            uint cpu = d.U32(e);
            long offset = wide ? (long)d.U64(e + 8) : d.U32(e + 8);
            long size = wide ? (long)d.U64(e + 16) : d.U32(e + 12);
            if (offset >= d.Length)
            {
                issues.Add(new ImageIssue("SliceOutside", CpuName(cpu)));
                continue;
            }

            slices.Add(new ImageSlice(CpuName(cpu), offset, Math.Min(size, d.Length - offset), ArchitectureOf(cpu)));
        }

        return new ExecutableImage(ImageFormat.MachOFat, slices.FirstOrDefault()?.ArchitectureId, false, 0, null, [], [], [], [], issues, false, slices);
    }

    /// <summary><paramref name="at"/> (fat のスライスの位置。fat でなければ 0) から Mach-O を解釈する。</summary>
    public static ExecutableImage Parse(ImageData d, long at, IReadOnlyList<ImageSlice> slices)
    {
        uint magic = Magic(d, at);
        d.BigEndian = magic is Cigam32 or Cigam64;
        bool wide = magic is Magic64 or Cigam64;
        var issues = new List<ImageIssue>();
        uint cpu = d.U32(at + 4);
        uint ncmds = d.U32(at + 16);
        long cmd = at + (wide ? 32 : 28);
        var sections = new List<ImageSection>();
        var symbols = new List<ImageSymbol>();
        var imports = new Dictionary<ulong, string>();
        ulong textBase = 0;
        ulong? entryOff = null;
        (long Symoff, long Nsyms, long Stroff, long Strsize)? symtab = null;
        (long Offset, long Count)? indirect = null;
        var stubSections = new List<(ulong Addr, ulong Size, uint Index, uint StubSize)>();
        for (uint i = 0; i < Math.Min(ncmds, 65536u); i++)
        {
            if (!d.Has(cmd, 8))
            {
                issues.Add(new ImageIssue("LoadCommandsBroken"));
                break;
            }

            uint type = d.U32(cmd);
            uint size = d.U32(cmd + 4);
            if (size < 8 || !d.Has(cmd, size))
            {
                issues.Add(new ImageIssue("LoadCommandsBroken"));
                break;
            }

            try
            {
                switch (type)
                {
                    case 0x1 or 0x19:
                    {
                        bool w = type == 0x19;
                        string segname = d.FixedName(cmd + 8, 16);
                        ulong vmaddr = d.Word(cmd + 24, w);
                        long fileoff = (long)d.Word(cmd + (w ? 40 : 32), w);
                        uint nsects = d.U32(cmd + (w ? 64 : 48));
                        if (segname == "__TEXT")
                        {
                            textBase = vmaddr - (ulong)fileoff;
                        }

                        long sect = cmd + (w ? 72 : 56);
                        for (uint s = 0; s < Math.Min(nsects, 1024u); s++, sect += w ? 80 : 68)
                        {
                            string name = d.FixedName(sect, 16);
                            ulong addr = d.Word(sect + 32, w);
                            ulong ssize = d.Word(sect + (w ? 40 : 36), w);
                            long offset = d.U32(sect + (w ? 48 : 40));
                            uint flags = d.U32(sect + (w ? 64 : 56));
                            uint reserved1 = d.U32(sect + (w ? 68 : 60));
                            uint reserved2 = d.U32(sect + (w ? 72 : 64));
                            bool zerofill = (flags & 0xFF) is 1 or 12;
                            bool code = (flags & 0x80000000) != 0 || (flags & 0x400) != 0;
                            long fileSize = zerofill || offset == 0 ? 0 : Math.Clamp((long)ssize, 0, Math.Max(0, d.Length - (at + offset)));
                            sections.Add(new ImageSection(segname + "," + name, addr, ssize, at + offset, fileSize, code, true, segname != "__TEXT"));
                            if ((flags & 0xFF) == 8)
                            {
                                stubSections.Add((addr, ssize, reserved1, reserved2));
                            }
                        }

                        break;
                    }

                    case 0x80000028:
                        entryOff = d.U64(cmd + 8);
                        break;
                    case 0x2:
                        symtab = (d.U32(cmd + 8), d.U32(cmd + 12), d.U32(cmd + 16), d.U32(cmd + 20));
                        break;
                    case 0xB:
                        indirect = (d.U32(cmd + 56), d.U32(cmd + 60));
                        break;
                }
            }
            catch (ImageFormatException ex)
            {
                issues.Add(new ImageIssue("LoadCommandsBroken", ex.Detail));
            }

            cmd += size;
        }

        var names = new List<string>();
        if (symtab is { } st)
        {
            try
            {
                long entry = wide ? 16 : 12;
                for (long n = 0; n < Math.Min(st.Nsyms, 10_000_000); n++)
                {
                    long e = at + st.Symoff + n * entry;
                    uint strx = d.U32(e);
                    byte ntype = d.U8(e + 4);
                    ulong value = d.Word(e + 8, wide);
                    string name = strx == 0 || strx >= st.Strsize ? string.Empty : d.CString(at + st.Stroff + strx, 4096);
                    names.Add(name);
                    if ((ntype & 0xE0) != 0 || (ntype & 0x0E) != 0x0E || name.Length == 0)
                    {
                        continue;
                    }

                    ImageSection? section = sections.FirstOrDefault(s => s.ContainsAddress(value));
                    symbols.Add(new ImageSymbol(name, name, value, section?.Execute == true ? ImageSymbolKind.Function : ImageSymbolKind.Data));
                }
            }
            catch (ImageFormatException ex)
            {
                issues.Add(new ImageIssue("SymbolTableBroken", ex.Detail));
            }
        }

        if (indirect is { } ind && stubSections.Count > 0)
        {
            try
            {
                foreach ((ulong addr, ulong size, uint index, uint stubSize) in stubSections)
                {
                    if (stubSize == 0)
                    {
                        continue;
                    }

                    for (ulong k = 0; k < size / stubSize && k < 1_000_000; k++)
                    {
                        uint symIndex = d.U32(at + ind.Offset + (index + (long)k) * 4);
                        if (symIndex < names.Count && names[(int)symIndex].Length > 0)
                        {
                            string name = names[(int)symIndex];
                            ulong stub = addr + k * stubSize;
                            imports[stub] = name;
                            symbols.Add(new ImageSymbol(name, name, stub, ImageSymbolKind.Import));
                        }
                    }
                }
            }
            catch (ImageFormatException ex)
            {
                issues.Add(new ImageIssue("StubsBroken", ex.Detail));
            }
        }

        ulong? entryPoint = entryOff is { } off ? textBase + off : null;
        return new ExecutableImage(ImageFormat.MachO, ArchitectureOf(cpu), d.BigEndian, textBase, entryPoint, sections, symbols, imports, [], issues,
            false, slices) { BaseOffset = at };
    }
}
