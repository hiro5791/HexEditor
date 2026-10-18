using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HexEditor.TestData;

// cases/06-analysis.md の表の逆アセンブル (ANA-23〜ANA-30) のテストデータ。コンパイラを使わず、生成ツールがバイト列を組み立てる。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> DisassemblyItems() =>
    [
        new("TD-ANA-PE-ASM", PeAsm().Length, "x64 の PE32+ (インポート表と既知の命令列)", path => WriteAll(path, PeAsm()), dir => Path.Combine(dir, "TD-ANA-PE-ASM.exe")),
        new("TD-ANA-PE-ARM64", PeArm64().Length, "ARM64 の PE32+ (TD-PE-X64 と同じ形)", path => WriteAll(path, PeArm64()), dir => Path.Combine(dir, "TD-ANA-PE-ARM64.exe")),
        new("TD-ANA-ELF-CPP", ElfCpp().Length, "x64 の ELF (C++ のマングル名のシンボルを持つ)", path => WriteAll(path, ElfCpp())),
        new("TD-ANA-NET-HELLO", NetHello().Length, ".NET のコンソールアプリの DLL (Hello World)", path => WriteAll(path, NetHello()), dir => Path.Combine(dir, "TD-ANA-NET-HELLO.dll")),
    ];

    // ───────────── TD-ANA-PE-ASM ─────────────

    /// <summary>TD-ANA-PE-ASM のイメージベース。</summary>
    public const ulong PeAsmImageBase = 0x140000000;

    /// <summary>TD-ANA-PE-ASM の命令表 (仮想アドレスは ImageBase からの相対、命令の長さ、Intel 構文の表記)。</summary>
    public static IReadOnlyList<(uint Rva, int Length, string Text)> PeAsmInstructions { get; } =
    [
        (0x1000, 4, "sub rsp, 0x28"),
        (0x1004, 5, "call 0x140001040"),
        (0x1009, 5, "mov eax, 1"),
        (0x100E, 5, "call 0x140001040"),
        (0x1013, 5, "call 0x140001040"),
        (0x1018, 6, "call qword ptr [rip + 0xfe2]"),
        (0x101E, 6, "call qword ptr [rip + 0xfe4]"),
        (0x1024, 5, "call 0x140001060"),
        (0x1029, 2, "jmp 0x140001030"),
        (0x1030, 4, "add rsp, 0x28"),
        (0x1034, 1, "ret"),
        (0x1040, 1, "push rbp"),
        (0x1041, 3, "mov rbp, rsp"),
        (0x1044, 2, "xor eax, eax"),
        (0x1046, 1, "pop rbp"),
        (0x1047, 1, "ret"),
        (0x1060, 1, "push rbp"),
        (0x1061, 3, "mov rbp, rsp"),
        (0x1064, 5, "mov eax, 2"),
        (0x1069, 1, "pop rbp"),
        (0x106A, 1, "ret"),
    ];

    /// <summary>
    /// TD-ANA-PE-ASM: x64 の PE32+ (2 KiB)。ImageBase 0x140000000、.text (仮想アドレス 0x1000、ファイル位置 0x400)、.rdata (0x2000、0x600) に
    /// KERNEL32.dll の CreateFileW と GetStdHandle のインポート表。エクスポート・シンボル・デバッグ情報なし。命令は <see cref="PeAsmInstructions"/>。
    /// </summary>
    public static byte[] PeAsm()
    {
        byte[] pe = new byte[0x800];
        WritePeHeaders(pe, machine: 0x8664, sections: 2, entryRva: 0x1000, imageSize: 0x3000, headerSize: 0x400);
        int s = 0x80 + 24 + 0xF0;
        WriteSection(pe, s, ".text", 0x1000, 0x200, 0x400, 0x60000020);
        WriteSection(pe, s + 40, ".rdata", 0x2000, 0x200, 0x600, 0x40000040);

        // データディレクトリ: インポート (1) と IAT (12)。
        int dirs = 0x80 + 24 + 112;
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(dirs + 8), 0x2040);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(dirs + 12), 40);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(dirs + 12 * 8), 0x2000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(dirs + 12 * 8 + 4), 24);

        byte[] code =
        [
            0x48, 0x83, 0xEC, 0x28,
            0xE8, 0x37, 0x00, 0x00, 0x00,
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xE8, 0x2D, 0x00, 0x00, 0x00,
            0xE8, 0x28, 0x00, 0x00, 0x00,
            0xFF, 0x15, 0xE2, 0x0F, 0x00, 0x00,
            0xFF, 0x15, 0xE4, 0x0F, 0x00, 0x00,
            0xE8, 0x37, 0x00, 0x00, 0x00,
            0xEB, 0x05,
            0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
            0x48, 0x83, 0xC4, 0x28,
            0xC3,
        ];
        pe.AsSpan(0x400, 0x200).Fill(0xCC);
        code.CopyTo(pe, 0x400);
        byte[] funcA = [0x55, 0x48, 0x89, 0xE5, 0x31, 0xC0, 0x5D, 0xC3];
        byte[] funcB = [0x55, 0x48, 0x89, 0xE5, 0xB8, 0x02, 0x00, 0x00, 0x00, 0x5D, 0xC3];
        funcA.CopyTo(pe, 0x440);
        funcB.CopyTo(pe, 0x460);

        // .rdata (ファイル位置 = RVA − 0x2000 + 0x600)
        static int R(int rva) => rva - 0x2000 + 0x600;
        foreach (int table in new[] { 0x2000, 0x2020 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(R(table)), 0x2090);
            BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(R(table) + 8), 0x20A0);
        }

        int desc = R(0x2040);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(desc), 0x2020);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(desc + 12), 0x2080);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(desc + 16), 0x2000);
        "KERNEL32.dll\0"u8.CopyTo(pe.AsSpan(R(0x2080)));
        "CreateFileW\0"u8.CopyTo(pe.AsSpan(R(0x2092)));
        "GetStdHandle\0"u8.CopyTo(pe.AsSpan(R(0x20A2)));
        return pe;
    }

    /// <summary>TD-ANA-PE-ARM64: TD-PE-X64 と同じ形の ARM64 の PE32+ (Machine 0xAA64)。.text は <c>mov w0, #0</c> / <c>ret</c>。</summary>
    public static byte[] PeArm64()
    {
        byte[] pe = PeX64();
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x80 + 4), 0xAA64);
        byte[] code = [0x00, 0x00, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6];
        code.CopyTo(pe, 0x200);
        return pe;
    }

    private static void WritePeHeaders(byte[] pe, ushort machine, int sections, int entryRva, int imageSize, int headerSize)
    {
        "MZ"u8.CopyTo(pe);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), 0x80);
        int h = 0x80;
        "PE\0\0"u8.CopyTo(pe.AsSpan(h));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 4), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 6), (ushort)sections);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 20), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 22), 0x22);
        int o = h + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o), 0x20B);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 4), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 16), entryRva);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 20), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(pe.AsSpan(o + 24), PeAsmImageBase);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 32), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 36), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 40), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 48), 6);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 56), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 60), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 68), 3);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 72), 0x100000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 80), 0x1000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 88), 0x100000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 96), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 108), 16);
    }

    private static void WriteSection(byte[] pe, int at, string name, int va, int size, int raw, uint flags)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(pe, at);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(at + 8), size);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(at + 12), va);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(at + 16), size);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(at + 20), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at + 36), flags);
    }

    // ───────────── ELF (TD-ELF-X64、TD-ANA-ELF-CPP) ─────────────

    /// <summary>TD-ELF-X64 と TD-ANA-ELF-CPP のシンボル (readelf -s で見える値。生成時に決まる)。</summary>
    public static IReadOnlyDictionary<string, ulong> ElfSymbols { get; } = new Dictionary<string, ulong>
    {
        ["_start"] = 0x401000,
        ["main"] = 0x401020,
        ["_ZN3foo3barEi"] = 0x401040,
        ["_ZNK3foo3Baz3quxEv"] = 0x401050,
        ["puts@plt"] = 0x401110,
        ["exit@plt"] = 0x401120,
    };

    /// <summary>
    /// x64 の ELF (8 KiB、ET_EXEC)。PT_LOAD 1 つ (仮想アドレス 0x400000 にファイル全体)、.text (0x401000: _start と main。cpp なら
    /// foo::bar(int) と foo::Baz::qux() const も)、.plt (puts@plt・exit@plt)、.got.plt、.dynsym・.dynstr、.rela.plt、.symtab・.strtab。
    /// </summary>
    private static byte[] BuildElf(bool cpp)
    {
        byte[] elf = new byte[0x2000];
        const ulong Base = 0x400000;
        elf[0] = 0x7F;
        "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(24), 0x401000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(40), 0x1600);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(58), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(60), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(62), 9);
        int p = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(p), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(p + 4), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 16), Base);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 24), Base);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 32), 0x2000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 40), 0x2000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 48), 0x1000);

        // .text
        elf.AsSpan(0x1000, 0x100).Fill(0xCC);
        byte[] start = [0xE8, 0x1B, 0x00, 0x00, 0x00, 0x89, 0xC7, 0xB8, 0x3C, 0x00, 0x00, 0x00, 0x0F, 0x05];
        start.CopyTo(elf, 0x1000);
        var main = new List<byte> { 0x55, 0x48, 0x89, 0xE5 };
        if (cpp)
        {
            // call foo::bar(int) (0x401040)、call foo::Baz::qux() const (0x401050)
            main.AddRange([0xE8, .. Rel(0x401024 + 5, 0x401040)]);
            main.AddRange([0xE8, .. Rel(0x401029 + 5, 0x401050)]);
            main.AddRange([0xE8, .. Rel(0x40102E + 5, 0x401110)]);
        }
        else
        {
            main.AddRange([0xE8, .. Rel(0x401024 + 5, 0x401110)]);
        }

        main.AddRange([0x31, 0xC0, 0x5D, 0xC3]);
        main.ToArray().CopyTo(elf, 0x1020);
        if (cpp)
        {
            byte[] bar = [0x8D, 0x47, 0x01, 0xC3];
            byte[] qux = [0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3];
            bar.CopyTo(elf, 0x1040);
            qux.CopyTo(elf, 0x1050);
        }

        // .plt (PLT0 と 2 項目) と .got.plt (0x401200: 予約 3 つと puts・exit の項目)
        byte[] plt0 = [0xFF, 0x35, .. Rel(0x401106, 0x401208), 0xFF, 0x25, .. Rel(0x40110C, 0x401210), 0x0F, 0x1F, 0x40, 0x00];
        plt0.CopyTo(elf, 0x1100);
        for (int i = 0; i < 2; i++)
        {
            ulong entry = 0x401110 + (ulong)(i * 16);
            byte[] e = [0xFF, 0x25, .. Rel(entry + 6, 0x401218 + (ulong)(i * 8)), 0x68, (byte)i, 0, 0, 0, 0xE9, .. Rel(entry + 16, 0x401100)];
            e.CopyTo(elf, (int)(entry - Base));
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(0x1218 + i * 8), entry + 6);
        }

        // .dynsym (0x1300) と .dynstr (0x1360)
        "\0puts\0exit\0"u8.CopyTo(elf.AsSpan(0x1360));
        WriteSym(elf, 0x1300 + 24, 1, 0x12, 0, 0);
        WriteSym(elf, 0x1300 + 48, 6, 0x12, 0, 0);

        // .rela.plt (0x1380)
        for (int i = 0; i < 2; i++)
        {
            int r = 0x1380 + i * 24;
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(r), 0x401218 + (ulong)(i * 8));
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(r + 8), ((ulong)(i + 1) << 32) | 7);
        }

        // .symtab (0x1400) と .strtab (0x1500)
        var strtab = new MemoryStream();
        strtab.WriteByte(0);
        var symbols = new List<(string Name, ulong Value, byte Info, ushort Shndx)>
        {
            ("_start", 0x401000, 0x12, 1),
            ("main", 0x401020, 0x12, 1),
        };
        if (cpp)
        {
            symbols.Add(("_ZN3foo3barEi", 0x401040, 0x12, 1));
            symbols.Add(("_ZNK3foo3Baz3quxEv", 0x401050, 0x12, 1));
        }

        symbols.Add(("puts", 0, 0x12, 0));
        for (int i = 0; i < symbols.Count; i++)
        {
            uint name = (uint)strtab.Length;
            strtab.Write(Encoding.ASCII.GetBytes(symbols[i].Name + "\0"));
            WriteSym(elf, 0x1400 + (i + 1) * 24, name, symbols[i].Info, symbols[i].Shndx, symbols[i].Value);
        }

        strtab.ToArray().CopyTo(elf, 0x1500);

        // .shstrtab (0x1580)
        string[] names = ["", ".text", ".plt", ".got.plt", ".dynsym", ".dynstr", ".rela.plt", ".symtab", ".strtab", ".shstrtab"];
        var shstr = new MemoryStream();
        var nameAt = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            nameAt[i] = (int)shstr.Length;
            shstr.Write(Encoding.ASCII.GetBytes(names[i] + "\0"));
        }

        shstr.ToArray().CopyTo(elf, 0x1580);

        // セクションヘッダ (0x1600)
        (uint Type, ulong Flags, long Offset, long Size, uint Link, uint Info, long EntSize)[] sh =
        [
            (0, 0, 0, 0, 0, 0, 0),
            (1, 6, 0x1000, 0x100, 0, 0, 0),
            (1, 6, 0x1100, 0x30, 0, 0, 16),
            (1, 3, 0x1200, 0x28, 0, 0, 8),
            (11, 2, 0x1300, 72, 5, 1, 24),
            (3, 2, 0x1360, 11, 0, 0, 0),
            (4, 0x42, 0x1380, 48, 4, 3, 24),
            (2, 0, 0x1400, (symbols.Count + 1) * 24L, 8, 1, 24),
            (3, 0, 0x1500, strtab.Length, 0, 0, 0),
            (3, 0, 0x1580, shstr.Length, 0, 0, 0),
        ];
        for (int i = 0; i < sh.Length; i++)
        {
            int at = 0x1600 + i * 64;
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(at), (uint)nameAt[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(at + 4), sh[i].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 8), sh[i].Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 16), (sh[i].Flags & 2) != 0 ? Base + (ulong)sh[i].Offset : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 24), (ulong)sh[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 32), (ulong)sh[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(at + 40), sh[i].Link);
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(at + 44), sh[i].Info);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 56), (ulong)sh[i].EntSize);
        }

        return elf;

        static byte[] Rel(ulong next, ulong target)
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, (int)((long)target - (long)next));
            return b;
        }

        static void WriteSym(byte[] elf, int at, uint name, byte info, ushort shndx, ulong value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(at), name);
            elf[at + 4] = info;
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(at + 6), shndx);
            BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(at + 8), value);
        }
    }

    /// <summary>TD-ANA-ELF-CPP: <c>foo::bar(int)</c> と <c>foo::Baz::qux() const</c> を main から呼ぶ x64 の ELF (strip していない)。</summary>
    public static byte[] ElfCpp() => BuildElf(cpp: true);

    // ───────────── TD-ANA-MACHO-FAT ─────────────

    /// <summary>TD-ANA-MACHO-FAT の _main の仮想アドレス (両方のスライスで同じ)。</summary>
    public const ulong MachOMainAddress = 0x100001000;

    /// <summary>
    /// Mach-O の 64 bit の実行ファイル 1 つ (16 KiB)。LC_SEGMENT_64 (__TEXT。__text セクション 1 つ)、LC_MAIN、LC_SYMTAB (<c>_main</c>)。
    /// </summary>
    private static byte[] MachOSlice(uint cpu, uint subtype, byte[] code)
    {
        byte[] b = new byte[0x4000];
        Span<byte> h = b;
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], cpu);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], subtype);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 152 + 24 + 24);
        Span<byte> c = h[32..];
        BinaryPrimitives.WriteUInt32LittleEndian(c, 0x19);
        BinaryPrimitives.WriteUInt32LittleEndian(c[4..], 152);
        "__TEXT"u8.CopyTo(c[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(c[24..], 0x100000000);
        BinaryPrimitives.WriteUInt64LittleEndian(c[32..], 0x4000);
        BinaryPrimitives.WriteUInt64LittleEndian(c[40..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(c[48..], 0x4000);
        BinaryPrimitives.WriteUInt32LittleEndian(c[56..], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(c[60..], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(c[64..], 1);
        Span<byte> sect = c[72..];
        "__text"u8.CopyTo(sect);
        "__TEXT"u8.CopyTo(sect[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(sect[32..], MachOMainAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(sect[40..], (ulong)code.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sect[48..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(sect[52..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(sect[64..], 0x80000400);
        Span<byte> main = c[152..];
        BinaryPrimitives.WriteUInt32LittleEndian(main, 0x80000028);
        BinaryPrimitives.WriteUInt32LittleEndian(main[4..], 24);
        BinaryPrimitives.WriteUInt64LittleEndian(main[8..], 0x1000);
        Span<byte> symtab = c[176..];
        BinaryPrimitives.WriteUInt32LittleEndian(symtab, 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(symtab[4..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(symtab[8..], 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(symtab[12..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(symtab[16..], 0x2010);
        BinaryPrimitives.WriteUInt32LittleEndian(symtab[20..], 16);
        code.CopyTo(b, 0x1000);
        Span<byte> nlist = h[0x2000..];
        BinaryPrimitives.WriteUInt32LittleEndian(nlist, 1);
        nlist[4] = 0x0F;
        nlist[5] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(nlist[8..], MachOMainAddress);
        "\0_main\0"u8.CopyTo(h[0x2010..]);
        return b;
    }

    /// <summary>TD-ANA-MACHO-FAT の x86_64 のスライス (<c>mov eax, 0x2a</c> / <c>ret</c>)。</summary>
    public static byte[] MachOSliceX64() => MachOSlice(0x01000007, 3, [0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3]);

    /// <summary>TD-ANA-MACHO-FAT の arm64 のスライス (<c>mov w0, #0x2a</c> / <c>ret</c>)。</summary>
    public static byte[] MachOSliceArm64() => MachOSlice(0x0100000C, 0, [0x40, 0x05, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6]);

    // ───────────── TD-ANA-NET-HELLO ─────────────

    /// <summary>
    /// TD-ANA-NET-HELLO: .NET のコンソールアプリの DLL (ReadyToRun なし)。メタデータと IL を System.Reflection.Metadata で組み立てる
    /// (同じ内容になるよう、MVID とタイムスタンプは固定)。<c>Program.Main</c> は <c>Console.WriteLine("Hello, World!")</c>、
    /// <c>Program.Check(int x)</c> は <c>if (x &gt; 0)</c> の中で <c>Program.Helper()</c> を呼ぶ。
    /// </summary>
    public static byte[] NetHello()
    {
        var metadata = new MetadataBuilder();
        var il = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(il);
        byte[] token = [0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A];
        metadata.AddModule(0, metadata.GetOrAddString("Hello.dll"), metadata.GetOrAddGuid(new Guid("6d3c0d12-6f8c-4c1e-9a52-1f7a0c3d4e5f")), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Hello"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob(token), 0, default);
        AssemblyReferenceHandle console = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Console"), new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob(token), 0, default);
        TypeReferenceHandle obj = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        TypeReferenceHandle consoleType = metadata.AddTypeReference(console, metadata.GetOrAddString("System"), metadata.GetOrAddString("Console"));

        var writeLineSig = new BlobBuilder();
        new BlobEncoder(writeLineSig).MethodSignature().Parameters(1, r => r.Void(), p => p.AddParameter().Type().String());
        MemberReferenceHandle writeLine = metadata.AddMemberReference(consoleType, metadata.GetOrAddString("WriteLine"), metadata.GetOrAddBlob(writeLineSig));

        var voidSig = new BlobBuilder();
        new BlobEncoder(voidSig).MethodSignature().Parameters(0, r => r.Void(), _ => { });
        var intSig = new BlobBuilder();
        new BlobEncoder(intSig).MethodSignature().Parameters(1, r => r.Void(), p => p.AddParameter().Type().Int32());

        // メソッドの定義の行番号: Main = 1、Check = 2、Helper = 3。
        var helperHandle = MetadataTokens.MethodDefinitionHandle(3);

        var main = new InstructionEncoder(new BlobBuilder());
        main.LoadString(metadata.GetOrAddUserString("Hello, World!"));
        main.Call(writeLine);
        main.OpCode(ILOpCode.Ret);
        int mainBody = encoder.AddMethodBody(main);

        var flow = new ControlFlowBuilder();
        var check = new InstructionEncoder(new BlobBuilder(), flow);
        LabelHandle end = check.DefineLabel();
        check.LoadArgument(0);
        check.LoadConstantI4(0);
        check.OpCode(ILOpCode.Cgt);
        check.Branch(ILOpCode.Brfalse_s, end);
        check.Call(helperHandle);
        check.MarkLabel(end);
        check.OpCode(ILOpCode.Ret);
        int checkBody = encoder.AddMethodBody(check);

        var helper = new InstructionEncoder(new BlobBuilder());
        helper.LoadString(metadata.GetOrAddUserString("Helper"));
        helper.Call(writeLine);
        helper.OpCode(ILOpCode.Ret);
        int helperBody = encoder.AddMethodBody(helper);

        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        const MethodAttributes Static = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
        MethodDefinitionHandle mainHandle = metadata.AddMethodDefinition(Static, MethodImplAttributes.IL, metadata.GetOrAddString("Main"),
            metadata.GetOrAddBlob(voidSig), mainBody, default);
        metadata.AddMethodDefinition(Static, MethodImplAttributes.IL, metadata.GetOrAddString("Check"), metadata.GetOrAddBlob(intSig), checkBody,
            MetadataTokens.ParameterHandle(1));
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("x"), 1);
        metadata.AddMethodDefinition(Static, MethodImplAttributes.IL, metadata.GetOrAddString("Helper"), metadata.GetOrAddBlob(voidSig), helperBody,
            MetadataTokens.ParameterHandle(2));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("Hello"), metadata.GetOrAddString("Program"), obj, MetadataTokens.FieldDefinitionHandle(1), mainHandle);

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            il,
            entryPoint: default,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: _ => new BlobContentId(new Guid("a1b2c3d4-0000-4000-8000-000000000001"), 0x12345678));
        var output = new BlobBuilder();
        pe.Serialize(output);
        return output.ToArray();
    }

    /// <summary>TD-ANA-NET-HELLO の <c>Program.Main</c> の IL (テストが比べる)。</summary>
    public static ImmutableArray<byte> NetHelloMainIl()
    {
        using var reader = new PEReader(new MemoryStream(NetHello()));
        MetadataReader md = reader.GetMetadataReader();
        MethodDefinition main = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1));
        return reader.GetMethodBody(main.RelativeVirtualAddress).GetILContent();
    }
}
