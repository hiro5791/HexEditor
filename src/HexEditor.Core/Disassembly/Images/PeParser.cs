namespace HexEditor.Core.Disassembly.Images;

/// <summary>
/// PE (PE32 / PE32+) の解釈 (ANA-24 の仕様 1・6): セクション、エントリポイント、エクスポート表、インポート表 (IAT の各項目に
/// <c>DLL名!関数名</c>)、遅延読み込みインポート、例外表 (関数の範囲)、CLI ヘッダの有無。PDB は読まない。
/// </summary>
internal static class PeParser
{
    private const int MaxSections = 96;
    private const int MaxImports = 1_000_000;

    public static bool IsPe(ImageData d) =>
        d.Has(0, 0x40) && d.U16(0) == 0x5A4D && d.U32(0x3C) is var lfanew && lfanew < d.Length - 4 && d.U32(lfanew) == 0x00004550;

    public static ExecutableImage Parse(ImageData d)
    {
        var issues = new List<ImageIssue>();
        var sections = new List<ImageSection>();
        var symbols = new List<ImageSymbol>();
        var imports = new Dictionary<ulong, string>();
        var functions = new List<ulong>();
        long pe = d.U32(0x3C);
        ushort machine = d.U16(pe + 4);
        int count = d.U16(pe + 6);
        int optSize = d.U16(pe + 20);
        long opt = pe + 24;
        ushort magic = d.U16(opt);
        bool plus = magic == 0x20B;
        uint entryRva = d.U32(opt + 16);
        ulong imageBase = plus ? d.U64(opt + 24) : d.U32(opt + 28);
        long headerSize = d.U32(opt + 60);
        uint dirCount = d.Has(opt + (plus ? 108 : 92), 4) ? d.U32(opt + (plus ? 108 : 92)) : 0;
        long dirs = opt + (plus ? 112 : 96);
        string? arch = machine switch
        {
            0x014C => DisasmArchitectures.X86_32,
            0x8664 => DisasmArchitectures.X86_64,
            0x01C0 or 0x01C2 => DisasmArchitectures.Arm,
            0x01C4 => DisasmArchitectures.Thumb,
            0xAA64 or 0xA641 or 0xA64E => DisasmArchitectures.Arm64,
            0x5032 => DisasmArchitectures.RiscV32C,
            0x5064 => DisasmArchitectures.RiscV64C,
            _ => null,
        };

        long sectionTable = opt + optSize;
        if (count > MaxSections)
        {
            issues.Add(new ImageIssue("TooManySections", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            count = MaxSections;
        }

        for (int i = 0; i < count; i++)
        {
            long s = sectionTable + i * 40L;
            if (!d.Has(s, 40))
            {
                issues.Add(new ImageIssue("SectionTableOutside"));
                break;
            }

            string name = d.FixedName(s, 8);
            uint vsize = d.U32(s + 8);
            uint va = d.U32(s + 12);
            uint rawSize = d.U32(s + 16);
            uint rawPtr = d.U32(s + 20);
            uint flags = d.U32(s + 36);
            long fileSize = rawSize;
            if (rawPtr >= d.Length)
            {
                fileSize = 0;
                if (rawSize > 0)
                {
                    issues.Add(new ImageIssue("SectionOutside", name));
                }
            }
            else if (rawPtr + fileSize > d.Length)
            {
                fileSize = d.Length - rawPtr;
                issues.Add(new ImageIssue("SectionOutside", name));
            }

            // ファイル上のサイズが仮想サイズより大きい場合 (ファイルの整列の詰め物) は、仮想サイズまでを表示する。
            if (vsize > 0 && fileSize > vsize)
            {
                fileSize = vsize;
            }

            sections.Add(new ImageSection(name, imageBase + va, vsize == 0 ? rawSize : vsize, rawPtr, fileSize,
                Execute: (flags & 0x20000000) != 0 || (flags & 0x20) != 0, Read: (flags & 0x40000000) != 0, Write: (flags & 0x80000000) != 0));
        }

        var image = new Mapper(sections, imageBase, headerSize);

        (uint Rva, uint Size) Dir(int index) =>
            index < dirCount && d.Has(dirs + index * 8L, 8) ? (d.U32(dirs + index * 8L), d.U32(dirs + index * 8L + 4)) : (0, 0);

        Try(issues, "ExportTableBroken", () => Exports(d, image, Dir(0), symbols));
        Try(issues, "ImportTableBroken", () => Imports(d, image, Dir(1).Rva, plus, imports, symbols));
        Try(issues, "DelayImportTableBroken", () => DelayImports(d, image, Dir(13).Rva, plus, imports, symbols));
        if (machine is 0x8664 or 0xAA64 or 0xA641 or 0xA64E)
        {
            Try(issues, "ExceptionTableBroken", () => Exceptions(d, image, Dir(3), machine == 0x8664 ? 12 : 8, functions));
        }

        bool dotnet = Dir(14).Rva != 0;
        ulong? entry = entryRva != 0 ? imageBase + entryRva : null;
        return new ExecutableImage(ImageFormat.Pe, arch, false, imageBase, entry, sections, symbols, imports, functions, issues, dotnet, [],
            headerSize);
    }

    private static void Try(List<ImageIssue> issues, string code, Action action)
    {
        try
        {
            action();
        }
        catch (ImageFormatException ex)
        {
            issues.Add(new ImageIssue(code, ex.Detail));
        }
    }

    /// <summary>RVA とファイル位置の変換 (解釈の途中で使う)。</summary>
    private sealed class Mapper(List<ImageSection> sections, ulong imageBase, long headerSize)
    {
        public ulong ImageBase => imageBase;

        public long Offset(uint rva)
        {
            ulong va = imageBase + rva;
            foreach (ImageSection s in sections)
            {
                if (va >= s.VirtualAddress && va < s.VirtualAddress + (ulong)s.FileSize)
                {
                    return s.FileOffset + (long)(va - s.VirtualAddress);
                }
            }

            if (rva < headerSize)
            {
                return rva;
            }

            throw new ImageFormatException("RvaOutside", $"0x{rva:X}");
        }
    }

    private static string DllBase(string dll)
    {
        // 表示はインポート表の大文字・小文字のまま、拡張子だけを外す (KERNEL32.dll → KERNEL32)。
        int dot = dll.LastIndexOf('.');
        return dot > 0 ? dll[..dot] : dll;
    }

    private static void Exports(ImageData d, Mapper m, (uint Rva, uint Size) dir, List<ImageSymbol> symbols)
    {
        if (dir.Rva == 0)
        {
            return;
        }

        long e = m.Offset(dir.Rva);
        uint functionCount = d.U32(e + 20);
        uint nameCount = d.U32(e + 24);
        long functions = m.Offset(d.U32(e + 28));
        long names = nameCount > 0 ? m.Offset(d.U32(e + 32)) : 0;
        long ordinals = nameCount > 0 ? m.Offset(d.U32(e + 36)) : 0;
        uint ordinalBase = d.U32(e + 16);
        var named = new Dictionary<uint, string>();
        for (uint i = 0; i < Math.Min(nameCount, MaxImports); i++)
        {
            uint index = d.U16(ordinals + i * 2L);
            named.TryAdd(index, d.CString(m.Offset(d.U32(names + i * 4L))));
        }

        for (uint i = 0; i < Math.Min(functionCount, MaxImports); i++)
        {
            uint rva = d.U32(functions + i * 4L);
            if (rva == 0 || rva >= dir.Rva && rva < dir.Rva + dir.Size)
            {
                // フォワーダ (別の DLL の関数) は位置を持たない。
                continue;
            }

            string name = named.TryGetValue(i, out string? n) ? n : "#" + (ordinalBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            symbols.Add(new ImageSymbol(name, name, m.ImageBase + rva, ImageSymbolKind.Export));
        }
    }

    private static void Imports(ImageData d, Mapper m, uint rva, bool plus, Dictionary<ulong, string> imports, List<ImageSymbol> symbols)
    {
        if (rva == 0)
        {
            return;
        }

        long desc = m.Offset(rva);
        for (int n = 0; n < 4096; n++, desc += 20)
        {
            uint oft = d.U32(desc);
            uint nameRva = d.U32(desc + 12);
            uint ft = d.U32(desc + 16);
            if (oft == 0 && nameRva == 0 && ft == 0)
            {
                break;
            }

            string dll = DllBase(d.CString(m.Offset(nameRva)));
            Thunks(d, m, oft != 0 ? oft : ft, ft, plus, dll, imports, symbols, rvaNames: true);
        }
    }

    private static void Thunks(ImageData d, Mapper m, uint lookupRva, uint iatRva, bool plus, string dll, Dictionary<ulong, string> imports,
        List<ImageSymbol> symbols, bool rvaNames)
    {
        int size = plus ? 8 : 4;
        long lookup = m.Offset(lookupRva);
        for (int i = 0; i < MaxImports; i++)
        {
            ulong thunk = d.Word(lookup + (long)i * size, plus);
            if (thunk == 0)
            {
                break;
            }

            bool ordinal = plus ? (thunk & 0x8000000000000000) != 0 : (thunk & 0x80000000) != 0;
            string func;
            if (ordinal)
            {
                func = "#" + (thunk & 0xFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                uint hint = (uint)(thunk & 0x7FFFFFFF);
                if (!rvaNames)
                {
                    hint = (uint)(thunk - m.ImageBase);
                }

                func = d.CString(m.Offset(hint) + 2);
            }

            ulong slot = m.ImageBase + iatRva + (ulong)(i * size);
            string name = dll + "!" + func;
            imports[slot] = name;
            symbols.Add(new ImageSymbol(name, name, slot, ImageSymbolKind.Import));
        }
    }

    private static void DelayImports(ImageData d, Mapper m, uint rva, bool plus, Dictionary<ulong, string> imports, List<ImageSymbol> symbols)
    {
        if (rva == 0)
        {
            return;
        }

        long desc = m.Offset(rva);
        for (int n = 0; n < 4096; n++, desc += 32)
        {
            uint attributes = d.U32(desc);
            uint nameRva = d.U32(desc + 4);
            uint iat = d.U32(desc + 12);
            uint int_ = d.U32(desc + 16);
            if (nameRva == 0 && iat == 0)
            {
                break;
            }

            // 属性の bit 0 が 0 の古い形式では、値が RVA ではなく仮想アドレス。
            bool isRva = (attributes & 1) != 0;
            uint Fix(uint v) => isRva ? v : (uint)(v - m.ImageBase);
            string dll = DllBase(d.CString(m.Offset(Fix(nameRva))));
            Thunks(d, m, Fix(int_), Fix(iat), plus, dll, imports, symbols, rvaNames: isRva);
        }
    }

    private static void Exceptions(ImageData d, Mapper m, (uint Rva, uint Size) dir, int entrySize, List<ulong> functions)
    {
        if (dir.Rva == 0 || dir.Size == 0)
        {
            return;
        }

        long table = m.Offset(dir.Rva);
        long count = Math.Min(dir.Size / entrySize, 4_000_000);
        for (long i = 0; i < count; i++)
        {
            uint begin = d.U32(table + i * entrySize);
            if (begin != 0)
            {
                functions.Add(m.ImageBase + begin);
            }
        }
    }
}
