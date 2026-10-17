using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData;

/// <summary>
/// 標準テンプレート (TPL-34) のサンプルファイル。すべてこのコードで組み立てた自作のバイト列であり、外部のファイルや第三者のデータは使わない
/// (tests/fixtures/templates/std/SOURCES.md)。サンプルはテンプレートの名前 (ファイル名から拡張子を除いたもの) をキーにする。
/// 同じテンプレートの別のサンプルは「名前.接尾辞」(例: <c>pe.dotnet</c>) をキーにする。同じ入力からは常に同じ内容を作る。
/// </summary>
public static class StandardTemplateSamples
{
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> AllSamples = new(Build);

    /// <summary>すべてのサンプル (キーはテンプレートの名前、または「名前.接尾辞」)。</summary>
    public static IReadOnlyDictionary<string, byte[]> All => AllSamples.Value;

    /// <summary>キーに対応するテンプレートの名前 (「.」より前)。</summary>
    public static string TemplateOf(string key) => key.Contains('.', StringComparison.Ordinal) ? key[..key.IndexOf('.', StringComparison.Ordinal)] : key;

    /// <summary>すべてのサンプルを <paramref name="directory"/> に <c>&lt;キー&gt;.bin</c> として書き出す。</summary>
    public static void WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (KeyValuePair<string, byte[]> sample in All)
        {
            File.WriteAllBytes(Path.Combine(directory, sample.Key + ".bin"), sample.Value);
        }
    }

    /// <summary>サンプルの一覧 (<c>&lt;キー&gt;.bin</c> と大きさを 1 行ずつ。LF、ASCII)。TD-TPL-STD-SAMPLES の一覧のファイルの内容。</summary>
    public static string Manifest() =>
        string.Concat(All.Select(s => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s.Key}.bin\t{s.Value.Length}\n")));

    private static IReadOnlyDictionary<string, byte[]> Build()
    {
        var d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pe"] = PeNative(),
            ["pe.dotnet"] = PeDotNet(),
            ["elf"] = ElfX64(),
            ["elf.arm"] = ElfSmall32(false, 40),
            ["elf.mips"] = ElfSmall32(true, 8),
            ["macho"] = MachoX64(),
            ["macho.fat"] = MachoFat(),
            ["zip"] = Zip(false),
            ["zip.zip64"] = Zip(true),
            ["mbr"] = MbrDisk(),
            ["gpt"] = GptDisk(),
            ["png"] = Png(),
            ["jpeg"] = Jpeg(),
            ["tiff"] = Tiff(false),
            ["tiff.be"] = Tiff(true),
            ["gif"] = Gif(),
            ["bmp"] = Bmp(),
            ["ico"] = Ico(),
            ["tga"] = Tga(),
            ["webp"] = Webp(),
            ["wav"] = Wav(),
            ["avi"] = Avi(),
            ["mp3"] = Mp3(),
            ["flac"] = Flac(),
            ["ogg"] = Ogg(),
            ["midi"] = Midi(),
            ["mp4"] = Mp4(),
            ["mkv"] = Webm(),
            ["gzip"] = Gzip(),
            ["tar"] = Tar(),
            ["7z"] = SevenZip(),
            ["rar"] = Rar(),
            ["xz"] = Xz(),
            ["cab"] = Cab(),
            ["pdf"] = Pdf(),
            ["sqlite"] = Sqlite(),
            ["lnk"] = Lnk(),
            ["regf"] = Regf(),
            ["pcap"] = Pcap(false),
            ["pcap.be"] = Pcap(true),
            ["pcapng"] = Pcapng(),
            ["ttf"] = Otf(),
            ["der"] = Der(),
            ["javaclass"] = JavaClass(),
            ["dex"] = Dex(),
            ["wasm"] = Wasm(),
            ["cfb"] = Cfb(),
            ["rtf"] = Rtf(),
            ["fat"] = Fat32(),
            ["fat.fat16"] = Fat16(),
            ["exfat"] = ExFat(),
            ["ntfs"] = Ntfs(),
            ["iso9660"] = Iso9660(),
            ["vhd"] = VhdFixed(),
            ["vhd.dynamic"] = VhdDynamic(),
            ["vhdx"] = Vhdx(),
            ["ext"] = Ext4(),
            // @@SAMPLES
        };
        return d;
    }

    /// <summary>バイト列の組み立て用の書き込み器。位置を指定した書き込みと、末尾への追記ができる。</summary>
    private sealed class Buf
    {
        private byte[] _a = new byte[256];

        public int Length { get; private set; }

        public Buf Size(int length)
        {
            Ensure(length);
            Length = Math.Max(Length, length);
            return this;
        }

        private void Ensure(int length)
        {
            if (length > _a.Length)
            {
                Array.Resize(ref _a, Math.Max(length, _a.Length * 2));
            }
        }

        public Buf At(int pos, params byte[] bytes)
        {
            Ensure(pos + bytes.Length);
            bytes.CopyTo(_a, pos);
            Length = Math.Max(Length, pos + bytes.Length);
            return this;
        }

        public Buf Add(params byte[] bytes) => At(Length, bytes);

        public Buf Zero(int count) => Size(Length + count);

        public Buf U8(int pos, int v) => At(pos, (byte)v);

        public Buf U16(int pos, int v) => At(pos, (byte)v, (byte)(v >> 8));

        public Buf U32(int pos, long v) => At(pos, (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));

        public Buf U64(int pos, long v)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, v);
            return At(pos, b);
        }

        public Buf B16(int pos, int v) => At(pos, (byte)(v >> 8), (byte)v);

        public Buf B24(int pos, int v) => At(pos, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        public Buf B32(int pos, long v) => At(pos, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        public Buf B64(int pos, long v)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            return At(pos, b);
        }

        /// <summary>ASCII の文字列を書く (<paramref name="length"/> を指定すると 0 で埋める。0 なら文字列の長さ)。</summary>
        public Buf Str(int pos, string s, int length = 0)
        {
            byte[] b = Encoding.Latin1.GetBytes(s);
            if (length > 0)
            {
                Array.Resize(ref b, length);
            }

            return At(pos, b);
        }

        public Buf Utf16(int pos, string s, int length = 0)
        {
            byte[] b = Encoding.Unicode.GetBytes(s);
            if (length > 0)
            {
                Array.Resize(ref b, length);
            }

            return At(pos, b);
        }

        public Buf Hex(int pos, string hex) => At(pos, Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal)));

        public Buf Pad(int length) => Size(length);

        public byte[] ToArray() => _a.AsSpan(0, Length).ToArray();

        public Span<byte> Span(int pos, int count) => _a.AsSpan(pos, count);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in data)
        {
            crc ^= x;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    // ---- 実行形式 ----

    /// <summary>PE32+ (x64)。DOS ヘッダ、リッチヘッダ、4 つのセクション、エクスポート・インポート・リソース・再配置・デバッグのディレクトリ。</summary>
    private static byte[] PeNative()
    {
        var b = new Buf().Size(0xE00);
        b.Str(0, "MZ").U16(2, 0x90).U16(4, 3).U16(8, 4).U16(0xA, 0).U16(0xC, 0xFFFF).U16(0x10, 0xB8).U16(0x18, 0x40).U32(0x3C, 0x100);
        b.Str(0x4E, "This program cannot be run in DOS mode.\r\n$");
        // リッチヘッダ (DanS と値は鍵で XOR する)
        const uint key = 0x1234ABCD;
        int rich = 0x80;
        b.U32(rich, 0x536E6144 ^ key).U32(rich + 4, key).U32(rich + 8, key).U32(rich + 12, key);
        b.U32(rich + 16, 0x00DE7809 ^ key).U32(rich + 20, 3 ^ key);
        b.U32(rich + 24, 0x010A7809 ^ key).U32(rich + 28, 10 ^ key);
        b.Str(rich + 32, "Rich").U32(rich + 36, key);
        // NT ヘッダ
        int nt = 0x100;
        b.Str(nt, "PE\0\0").U16(nt + 4, 0x8664).U16(nt + 6, 4).U32(nt + 8, 0x5F000000).U16(nt + 20, 240).U16(nt + 22, 0x0022);
        int opt = nt + 24;
        b.U16(opt, 0x20B).U8(opt + 2, 14).U8(opt + 3, 29).U32(opt + 4, 0x200).U32(opt + 8, 0x800).U32(opt + 16, 0x1000).U32(opt + 20, 0x1000);
        b.U64(opt + 24, 0x140000000).U32(opt + 32, 0x1000).U32(opt + 36, 0x200).U16(opt + 40, 6).U16(opt + 48, 6);
        b.U32(opt + 56, 0x5000).U32(opt + 60, 0x400).U16(opt + 68, 3).U16(opt + 70, 0x8160);
        b.U64(opt + 72, 0x100000).U64(opt + 80, 0x1000).U64(opt + 88, 0x100000).U64(opt + 96, 0x1000).U32(opt + 108, 16);
        int dd = opt + 112;
        b.U32(dd + 0, 0x20C0).U32(dd + 4, 0x40);        // エクスポート
        b.U32(dd + 8, 0x2000).U32(dd + 12, 0x3C);       // インポート
        b.U32(dd + 16, 0x3000).U32(dd + 20, 0x68);      // リソース
        b.U32(dd + 40, 0x4000).U32(dd + 44, 0xC);       // 再配置
        b.U32(dd + 48, 0x2140).U32(dd + 52, 0x1C);      // デバッグ
        b.U32(dd + 96, 0x2060).U32(dd + 100, 0x18);     // IAT (12 番)
        // セクション表
        int sh = opt + 240;
        Section(b, sh, ".text", 0x200, 0x1000, 0x200, 0x400, 0x60000020);
        Section(b, sh + 40, ".rdata", 0x180, 0x2000, 0x200, 0x600, 0x40000040);
        Section(b, sh + 80, ".rsrc", 0x68, 0x3000, 0x200, 0xA00, 0x40000040);
        Section(b, sh + 120, ".reloc", 0xC, 0x4000, 0x200, 0xC00, 0x42000040);
        // .text
        b.Hex(0x400, "48 83 EC 28 B9 00 00 00 00 FF 15 00 00 00 00 48 83 C4 28 C3");
        b.Hex(0x410, "8D 04 37 C3").Hex(0x420, "29 F7 89 F8 C3");
        // .rdata (file 0x600, RVA 0x2000)
        int r = 0x600;
        b.U32(r, 0x2040).U32(r + 12, 0x20A0).U32(r + 16, 0x2060);              // インポート記述子 (KERNEL32.dll)
        b.U64(r + 0x40, 0x2080).U64(r + 0x48, 0x2090);                          // ILT
        b.U64(r + 0x60, 0x2080).U64(r + 0x68, 0x2090);                          // IAT
        b.U16(r + 0x80, 1).Str(r + 0x82, "ExitProcess");
        b.U16(r + 0x90, 2).Str(r + 0x92, "GetLastError");
        b.Str(r + 0xA0, "KERNEL32.dll");
        // エクスポート
        int ex = r + 0xC0;
        b.U32(ex + 4, 0x5F000000).U16(ex + 8, 1).U16(ex + 10, 0).U32(ex + 12, 0x2110).U32(ex + 16, 1).U32(ex + 20, 2).U32(ex + 24, 2)
            .U32(ex + 28, 0x20F0).U32(ex + 32, 0x20F8).U32(ex + 36, 0x2100);
        b.U32(r + 0xF0, 0x1010).U32(r + 0xF4, 0x1020).U32(r + 0xF8, 0x2120).U32(r + 0xFC, 0x2130).U16(r + 0x100, 0).U16(r + 0x102, 1);
        b.Str(r + 0x110, "hxtest.dll").Str(r + 0x120, "HxAdd").Str(r + 0x130, "HxSub");
        // デバッグ + CodeView
        int dbg = r + 0x140;
        b.U32(dbg + 4, 0x5F000000).U16(dbg + 8, 0).U16(dbg + 10, 0).U32(dbg + 12, 2).U32(dbg + 16, 35).U32(dbg + 20, 0x2160).U32(dbg + 24, r + 0x160);
        b.Str(r + 0x160, "RSDS");
        for (int i = 0; i < 16; i++)
        {
            b.U8(r + 0x164 + i, 0xA0 + i);
        }

        b.U32(r + 0x174, 1).Str(r + 0x178, "hxtest.pdb");
        // .rsrc (file 0xA00, RVA 0x3000): 種類 → 名前 → 言語 → データ
        int rs = 0xA00;
        b.U16(rs + 14, 1).U32(rs + 16, 16).U32(rs + 20, 0x80000018);
        b.U16(rs + 0x18 + 14, 1).U32(rs + 0x18 + 16, 1).U32(rs + 0x18 + 20, 0x80000030);
        b.U16(rs + 0x30 + 14, 1).U32(rs + 0x30 + 16, 0x409).U32(rs + 0x30 + 20, 0x48);
        b.U32(rs + 0x48, 0x3060).U32(rs + 0x4C, 8).U32(rs + 0x50, 1252);
        b.Str(rs + 0x60, "HXRSRC01");
        // .reloc
        b.U32(0xC00, 0x1000).U32(0xC04, 12).U16(0xC08, 0xA00B).U16(0xC0A, 0);
        return b.ToArray();
    }

    private static void Section(Buf b, int at, string name, int vsize, int va, int rawSize, int rawPtr, uint characteristics) =>
        b.Str(at, name, 8).U32(at + 8, vsize).U32(at + 12, va).U32(at + 16, rawSize).U32(at + 20, rawPtr).U32(at + 36, characteristics);

    /// <summary>PE32 (x86) の .NET アセンブリ (CLI ヘッダとメタデータのストリームだけを持つ)。</summary>
    private static byte[] PeDotNet()
    {
        var b = new Buf().Size(0x600);
        b.Str(0, "MZ").U16(2, 0x90).U16(4, 3).U32(0x3C, 0x80);
        int nt = 0x80;
        b.Str(nt, "PE\0\0").U16(nt + 4, 0x14C).U16(nt + 6, 1).U32(nt + 8, 0x5F000000).U16(nt + 20, 224).U16(nt + 22, 0x2102);
        int opt = nt + 24;
        b.U16(opt, 0x10B).U8(opt + 2, 11).U32(opt + 4, 0x200).U32(opt + 20, 0x2000).U32(opt + 24, 0x4000).U32(opt + 28, 0x400000);
        b.U32(opt + 32, 0x2000).U32(opt + 36, 0x200).U16(opt + 40, 4).U16(opt + 48, 4).U32(opt + 56, 0x4000).U32(opt + 60, 0x200).U16(opt + 68, 3);
        b.U16(opt + 70, 0x8540).U32(opt + 72, 0x100000).U32(opt + 76, 0x1000).U32(opt + 80, 0x100000).U32(opt + 84, 0x1000).U32(opt + 92, 16);
        b.U32(opt + 96 + 14 * 8, 0x2000).U32(opt + 96 + 14 * 8 + 4, 0x48);
        int sh = opt + 224;
        Section(b, sh, ".text", 0x400, 0x2000, 0x400, 0x200, 0x60000020);
        // CLI ヘッダ (file 0x200)
        int c = 0x200;
        b.U32(c, 72).U16(c + 4, 2).U16(c + 6, 5).U32(c + 8, 0x2048).U32(c + 12, 0x70).U32(c + 16, 1);
        // メタデータ (file 0x248)
        int m = 0x248;
        b.U32(m, 0x424A5342).U16(m + 4, 1).U16(m + 6, 1).U32(m + 12, 12).Str(m + 16, "v4.0.30319");
        b.U16(m + 28, 0).U16(m + 30, 3);
        int s = m + 32;
        b.U32(s, 0x60).U32(s + 4, 0x10).Str(s + 8, "#~");
        s += 12;
        b.U32(s, 0x70).U32(s + 4, 0x10).Str(s + 8, "#Strings");
        s += 20;
        b.U32(s, 0x80).U32(s + 4, 0x10).Str(s + 8, "#GUID");
        b.Str(m + 0x70, "\0<Module>\0Main\0");
        return b.ToArray();
    }

    /// <summary>ELF64 (x86-64, リトルエンディアン) の共有ライブラリ。プログラムヘッダ 3、セクション 8 (.dynsym、.dynamic、.symtab を含む)。</summary>
    private static byte[] ElfX64()
    {
        var b = new Buf().Size(0x480);
        b.Hex(0, "7F 45 4C 46 02 01 01 00").U16(16, 3).U16(18, 62).U32(20, 1).U64(24, 0x100).U64(32, 0x40).U64(40, 0x280).U16(52, 64).U16(54, 56)
            .U16(56, 3).U16(58, 64).U16(60, 8).U16(62, 7);
        // プログラムヘッダ: PT_LOAD、PT_DYNAMIC、PT_GNU_STACK
        b.U32(0x40, 1).U32(0x44, 5).U64(0x50, 0).U64(0x58, 0).U64(0x60, 0x480).U64(0x68, 0x480).U64(0x70, 0x1000);
        b.U32(0x78, 2).U32(0x7C, 6).U64(0x80, 0x160).U64(0x88, 0x160).U64(0x90, 0x160).U64(0x98, 96).U64(0xA0, 96).U64(0xA8, 8);
        b.U32(0xB0, 0x6474E551).U32(0xB4, 6).U64(0xE0, 16);
        b.Hex(0x100, "31 C0 C3 90 90 90 90 90 90 90 90 90 90 90 90 90");
        b.Str(0x110, "\0libc.so.6\0hx_add\0");
        // .dynsym: 0 番目は空、1 番目が hx_add
        b.U32(0x148, 11).U8(0x14C, 0x12).U16(0x14E, 1).U64(0x150, 0x100).U64(0x158, 3);
        // .dynamic
        b.U64(0x160, 1).U64(0x168, 1).U64(0x170, 5).U64(0x178, 0x110).U64(0x180, 6).U64(0x188, 0x130).U64(0x190, 10).U64(0x198, 0x14)
            .U64(0x1A0, 11).U64(0x1A8, 24);
        // .symtab: 空、ファイル、hx_add
        b.U32(0x1D8, 1).U8(0x1DC, 0x04).U16(0x1DE, 0xFFF1);
        b.U32(0x1F0, 6).U8(0x1F4, 0x12).U16(0x1F6, 1).U64(0x1F8, 0x100).U64(0x200, 3);
        b.Str(0x208, "\0hx.c\0hx_add\0");
        b.Str(0x220, "\0.text\0.dynsym\0.dynstr\0.dynamic\0.symtab\0.strtab\0.shstrtab\0");
        ElfSection64(b, 0x280 + 64 * 1, 1, 1, 6, 0x100, 0x100, 16, 0, 0, 16, 0);
        ElfSection64(b, 0x280 + 64 * 2, 7, 11, 2, 0x130, 0x130, 48, 3, 1, 8, 24);
        ElfSection64(b, 0x280 + 64 * 3, 15, 3, 2, 0x110, 0x110, 0x12, 0, 0, 1, 0);
        ElfSection64(b, 0x280 + 64 * 4, 23, 6, 3, 0x160, 0x160, 96, 3, 0, 8, 16);
        ElfSection64(b, 0x280 + 64 * 5, 32, 2, 0, 0, 0x1C0, 72, 6, 2, 8, 24);
        ElfSection64(b, 0x280 + 64 * 6, 40, 3, 0, 0, 0x208, 13, 0, 0, 1, 0);
        ElfSection64(b, 0x280 + 64 * 7, 48, 3, 0, 0, 0x220, 58, 0, 0, 1, 0);
        return b.ToArray();
    }

    private static void ElfSection64(Buf b, int at, int name, int type, long flags, long addr, long offset, long size, int link, int info, long align, long entsize) =>
        b.U32(at, name).U32(at + 4, type).U64(at + 8, flags).U64(at + 16, addr).U64(at + 24, offset).U64(at + 32, size).U32(at + 40, link).U32(at + 44, info)
            .U64(at + 48, align).U64(at + 56, entsize);

    /// <summary>ELF32 の小さな実行ファイル。<paramref name="big"/> でビッグエンディアン (MIPS)、そうでなければリトルエンディアン (ARM)。</summary>
    private static byte[] ElfSmall32(bool big, int machine)
    {
        var b = new Buf().Size(0xF8);
        void W16(int at, int v) { if (big) { b.B16(at, v); } else { b.U16(at, v); } }
        void W32(int at, long v) { if (big) { b.B32(at, v); } else { b.U32(at, v); } }
        b.Hex(0, "7F 45 4C 46 01 " + (big ? "02" : "01") + " 01 00");
        W16(16, 2);
        W16(18, machine);
        W32(20, 1);
        W32(24, 0x8060);
        W32(28, 0x34);
        W32(32, 0x80);
        W32(36, big ? 0x70001001 : 0x05000200);
        W16(40, 52);
        W16(42, 32);
        W16(44, 1);
        W16(46, 40);
        W16(48, 3);
        W16(50, 2);
        // PT_LOAD
        W32(0x34, 1);
        W32(0x38, 0);
        W32(0x3C, 0x8000);
        W32(0x40, 0x8000);
        W32(0x44, 0x79);
        W32(0x48, 0x79);
        W32(0x4C, 5);
        W32(0x50, 0x1000);
        b.Hex(0x60, "01 00 A0 E3 1E FF 2F E1");
        b.Str(0x68, "\0.text\0.shstrtab\0");
        // .text
        W32(0x80 + 40 + 0, 1);
        W32(0x80 + 40 + 4, 1);
        W32(0x80 + 40 + 8, 6);
        W32(0x80 + 40 + 12, 0x8060);
        W32(0x80 + 40 + 16, 0x60);
        W32(0x80 + 40 + 20, 8);
        W32(0x80 + 40 + 32, 4);
        // .shstrtab
        W32(0x80 + 80 + 0, 7);
        W32(0x80 + 80 + 4, 3);
        W32(0x80 + 80 + 16, 0x68);
        W32(0x80 + 80 + 20, 17);
        W32(0x80 + 80 + 32, 1);
        return b.ToArray();
    }

    /// <summary>Mach-O 64 bit (x86-64) の実行ファイル: __PAGEZERO、__TEXT (__text)、LC_SYMTAB、LC_UUID、LC_MAIN、LC_LOAD_DYLIB、LC_BUILD_VERSION。</summary>
    private static byte[] MachoX64()
    {
        var b = new Buf().Size(0x228);
        b.Hex(0, "CF FA ED FE").U32(4, 0x01000007).U32(8, 3).U32(12, 2).U32(16, 7).U32(20, 376).U32(24, 0x200085);
        int p = 32;
        // LC_SEGMENT_64 __PAGEZERO
        b.U32(p, 0x19).U32(p + 4, 72).Str(p + 8, "__PAGEZERO", 16).U64(p + 32, 0x100000000);
        p += 72;
        // LC_SEGMENT_64 __TEXT + __text
        b.U32(p, 0x19).U32(p + 4, 152).Str(p + 8, "__TEXT", 16).U64(p + 24, 0x100000000).U64(p + 32, 0x1000).U64(p + 40, 0).U64(p + 48, 0x210)
            .U32(p + 56, 7).U32(p + 60, 5).U32(p + 64, 1);
        int sec = p + 72;
        b.Str(sec, "__text", 16).Str(sec + 16, "__TEXT", 16).U64(sec + 32, 0x100000200).U64(sec + 40, 8).U32(sec + 48, 0x200).U32(sec + 52, 4)
            .U32(sec + 64, 0x80000400);
        p += 152;
        // LC_SYMTAB
        b.U32(p, 2).U32(p + 4, 24).U32(p + 8, 0x210).U32(p + 12, 1).U32(p + 16, 0x220).U32(p + 20, 8);
        p += 24;
        // LC_UUID
        b.U32(p, 0x1B).U32(p + 4, 24);
        for (int i = 0; i < 16; i++)
        {
            b.U8(p + 8 + i, 0x10 + i);
        }

        p += 24;
        // LC_MAIN
        b.U32(p, 0x80000028).U32(p + 4, 24).U64(p + 8, 0x200).U64(p + 16, 0);
        p += 24;
        // LC_LOAD_DYLIB
        b.U32(p, 0xC).U32(p + 4, 56).U32(p + 8, 24).U32(p + 12, 2).U32(p + 16, 0x050C6400).U32(p + 20, 0x00010000).Str(p + 24, "/usr/lib/libSystem.B.dylib");
        p += 56;
        // LC_BUILD_VERSION (macOS 13.0)
        b.U32(p, 0x32).U32(p + 4, 24).U32(p + 8, 1).U32(p + 12, 0x000D0000).U32(p + 16, 0x000D0100).U32(p + 20, 0);
        // __text と nlist_64 と文字列表
        b.Hex(0x200, "31 C0 C3 90 90 90 90 90");
        b.U32(0x210, 1).U8(0x214, 0x0F).U8(0x215, 1).U16(0x216, 0).U64(0x218, 0x100000200);
        b.Str(0x220, "\0_main\0");
        return b.ToArray();
    }

    /// <summary>薄い Mach-O の最小のイメージ (ヘッダ + LC_UUID + LC_BUILD_VERSION)。fat のスライス用。</summary>
    private static byte[] MachoThinMin(long cpuType, long cpuSubType)
    {
        var b = new Buf().Size(0x100);
        b.Hex(0, "CF FA ED FE").U32(4, cpuType).U32(8, cpuSubType).U32(12, 2).U32(16, 2).U32(20, 48).U32(24, 0x200085);
        b.U32(32, 0x1B).U32(36, 24);
        for (int i = 0; i < 16; i++)
        {
            b.U8(40 + i, 0xA0 + i);
        }

        b.U32(56, 0x32).U32(60, 24).U32(64, 1).U32(68, 0x000D0000).U32(72, 0x000D0100);
        return b.ToArray();
    }

    /// <summary>fat (ユニバーサル) バイナリ: x86-64 と arm64 の 2 つのスライス。</summary>
    private static byte[] MachoFat()
    {
        var b = new Buf().Size(0x500);
        b.B32(0, 0xCAFEBABE).B32(4, 2);
        b.B32(8, 0x01000007).B32(12, 3).B32(16, 0x200).B32(20, 0x100).B32(24, 9);
        b.B32(28, 0x0100000C).B32(32, 0).B32(36, 0x400).B32(40, 0x100).B32(44, 9);
        b.At(0x200, MachoThinMin(0x01000007, 3));
        b.At(0x400, MachoThinMin(0x0100000C, 0));
        return b.ToArray();
    }

    // ---- アーカイブ ----

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    /// <summary>ZIP: 3 つの項目 (無圧縮、Deflate、データ記述子付き)。<paramref name="zip64"/> で ZIP64 の拡張フィールドと ZIP64 の EOCD を使う。</summary>
    private static byte[] Zip(bool zip64)
    {
        var files = new (string Name, byte[] Data, int Method, bool Descriptor)[]
        {
            ("hello.txt", Encoding.ASCII.GetBytes("Hello, HexEditor!\n"), 0, false),
            ("dir/data.bin", Deflate(Enumerable.Range(0, 200).Select(i => (byte)(i % 4)).ToArray()), 8, false),
            ("stream.txt", Encoding.ASCII.GetBytes("streamed entry"), 0, true),
        };
        byte[][] raw =
        [
            Encoding.ASCII.GetBytes("Hello, HexEditor!\n"),
            Enumerable.Range(0, 200).Select(i => (byte)(i % 4)).ToArray(),
            Encoding.ASCII.GetBytes("streamed entry"),
        ];
        var b = new Buf();
        var offsets = new int[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            offsets[i] = b.Length;
            byte[] name = Encoding.ASCII.GetBytes(f.Name);
            int extraLength = zip64 ? 20 : 0;
            int start = b.Length;
            b.U32(start, 0x04034B50).U16(start + 4, zip64 ? 45 : 20).U16(start + 6, 0x0800 | (f.Descriptor ? 8 : 0)).U16(start + 8, f.Method)
                .U16(start + 10, 0x645C).U16(start + 12, 0x5D50);
            uint crc = Crc32(raw[i]);
            if (!f.Descriptor)
            {
                b.U32(start + 14, crc).U32(start + 18, zip64 ? 0xFFFFFFFF : f.Data.Length).U32(start + 22, zip64 ? 0xFFFFFFFF : raw[i].Length);
            }
            else
            {
                b.U32(start + 14, 0).U32(start + 18, 0).U32(start + 22, 0);
            }

            b.U16(start + 26, name.Length).U16(start + 28, extraLength).At(start + 30, name);
            int p = start + 30 + name.Length;
            if (zip64)
            {
                b.U16(p, 1).U16(p + 2, 16).U64(p + 4, f.Descriptor ? 0 : raw[i].Length).U64(p + 12, f.Descriptor ? 0 : f.Data.Length);
                p += 20;
            }

            b.At(p, f.Data);
            if (f.Descriptor)
            {
                int d = b.Length;
                b.U32(d, 0x08074B50).U32(d + 4, crc);
                if (zip64)
                {
                    b.U64(d + 8, f.Data.Length).U64(d + 16, raw[i].Length);
                }
                else
                {
                    b.U32(d + 8, f.Data.Length).U32(d + 12, raw[i].Length);
                }
            }
        }

        int cd = b.Length;
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            byte[] name = Encoding.ASCII.GetBytes(f.Name);
            int start = b.Length;
            int extraLength = zip64 ? 28 : 0;
            b.U32(start, 0x02014B50).U16(start + 4, 0x031E).U16(start + 6, zip64 ? 45 : 20).U16(start + 8, 0x0800 | (f.Descriptor ? 8 : 0)).U16(start + 10, f.Method)
                .U16(start + 12, 0x645C).U16(start + 14, 0x5D50).U32(start + 16, Crc32(raw[i]))
                .U32(start + 20, zip64 ? 0xFFFFFFFF : f.Data.Length).U32(start + 24, zip64 ? 0xFFFFFFFF : raw[i].Length)
                .U16(start + 28, name.Length).U16(start + 30, extraLength).U16(start + 32, 0).U16(start + 34, 0).U16(start + 36, 0)
                .U32(start + 38, 0x81A40000).U32(start + 42, zip64 ? 0xFFFFFFFF : offsets[i]).At(start + 46, name);
            if (zip64)
            {
                int p = start + 46 + name.Length;
                b.U16(p, 1).U16(p + 2, 24).U64(p + 4, raw[i].Length).U64(p + 12, f.Data.Length).U64(p + 20, offsets[i]);
            }
        }

        int cdSize = b.Length - cd;
        if (zip64)
        {
            int z = b.Length;
            b.U32(z, 0x06064B50).U64(z + 4, 44).U16(z + 12, 45).U16(z + 14, 45).U32(z + 16, 0).U32(z + 20, 0).U64(z + 24, files.Length).U64(z + 32, files.Length)
                .U64(z + 40, cdSize).U64(z + 48, cd);
            b.U32(z + 56, 0x07064B50).U32(z + 60, 0).U64(z + 64, z).U32(z + 72, 1);
        }

        int e = b.Length;
        string comment = "HexEditor test archive";
        b.U32(e, 0x06054B50).U16(e + 4, 0).U16(e + 6, 0).U16(e + 8, zip64 ? 0xFFFF : files.Length).U16(e + 10, zip64 ? 0xFFFF : files.Length)
            .U32(e + 12, zip64 ? 0xFFFFFFFF : cdSize).U32(e + 16, zip64 ? 0xFFFFFFFF : cd).U16(e + 20, comment.Length).Str(e + 22, comment);
        return b.ToArray();
    }

    // ---- ディスク ----

    private static void MbrEntry(Buf b, int at, int status, int type, long lba, long sectors) =>
        b.U8(at, status).Hex(at + 1, "20 21 00").U8(at + 4, type).Hex(at + 5, "FE FF FF").U32(at + 8, lba).U32(at + 12, sectors);

    /// <summary>MBR のディスク (16 KiB): NTFS / FAT32 / 拡張パーティションの 3 つと、拡張ブートレコード 2 つの連鎖。</summary>
    private static byte[] MbrDisk()
    {
        var b = new Buf().Size(32 * 512);
        b.Hex(0, "FA 31 C0 8E D8 8E C0 FB EB FE");
        b.U32(440, 0x44545848);
        MbrEntry(b, 446, 0x80, 0x07, 2048, 6144);
        MbrEntry(b, 462, 0x00, 0x0C, 8192, 4096);
        MbrEntry(b, 478, 0x00, 0x0F, 8, 24);
        b.Hex(510, "55 AA");
        // EBR 1 (LBA 8)
        int e1 = 8 * 512;
        MbrEntry(b, e1 + 446, 0, 0x83, 2, 8);
        MbrEntry(b, e1 + 462, 0, 0x05, 16, 8);
        b.Hex(e1 + 510, "55 AA");
        // EBR 2 (LBA 24)
        int e2 = 24 * 512;
        MbrEntry(b, e2 + 446, 0, 0x82, 2, 4);
        b.Hex(e2 + 510, "55 AA");
        return b.ToArray();
    }

    /// <summary>GPT のディスク (4 MiB、セクタ 512 バイト): 保護 MBR、ヘッダ、パーティションエントリ (HXTEST-DATA、HXTEST-LOG)、予備の GPT。</summary>
    private static byte[] GptDisk()
    {
        byte[] data = new byte[4 * 1024 * 1024];
        TestDataCatalog.GptImage(data.Length, 0, data);
        return data;
    }

    // ---- 画像 ----

    /// <summary>エンディアンを選んで書く補助 (TIFF など)。</summary>
    private sealed class Ew(Buf b, bool big)
    {
        public Ew W16(int at, int v) { if (big) { b.B16(at, v); } else { b.U16(at, v); } return this; }

        public Ew W32(int at, long v) { if (big) { b.B32(at, v); } else { b.U32(at, v); } return this; }

        public Ew W64(int at, long v) { if (big) { b.B64(at, v); } else { b.U64(at, v); } return this; }
    }

    private static void TiffEntry(Ew w, int at, int tag, int type, long count, long value)
    {
        w.W16(at, tag).W16(at + 2, type).W32(at + 4, count);
        if (type == 3 && count == 1)
        {
            w.W16(at + 8, (int)value);
        }
        else
        {
            w.W32(at + 8, value);
        }
    }

    /// <summary>TIFF (4×4、RGB、無圧縮、1 ストリップ)。Exif の IFD も持つ。</summary>
    private static byte[] Tiff(bool big)
    {
        var b = new Buf().Size(254);
        var w = new Ew(b, big);
        b.Str(0, big ? "MM" : "II");
        w.W16(2, 42).W32(4, 8);
        w.W16(8, 11);
        int e = 10;
        TiffEntry(w, e + 12 * 0, 0x0100, 3, 1, 4);
        TiffEntry(w, e + 12 * 1, 0x0101, 3, 1, 4);
        TiffEntry(w, e + 12 * 2, 0x0102, 3, 3, 146);
        TiffEntry(w, e + 12 * 3, 0x0103, 3, 1, 1);
        TiffEntry(w, e + 12 * 4, 0x0106, 3, 1, 2);
        TiffEntry(w, e + 12 * 5, 0x0111, 4, 1, 206);
        TiffEntry(w, e + 12 * 6, 0x0115, 3, 1, 3);
        TiffEntry(w, e + 12 * 7, 0x0116, 3, 1, 4);
        TiffEntry(w, e + 12 * 8, 0x0117, 4, 1, 48);
        TiffEntry(w, e + 12 * 9, 0x0131, 2, 14, 152);
        TiffEntry(w, e + 12 * 10, 0x8769, 4, 1, 168);
        w.W32(142, 0);
        w.W16(146, 8).W16(148, 8).W16(150, 8);
        b.Str(152, "HexEditor 1.0");
        // Exif IFD
        w.W16(168, 2);
        TiffEntry(w, 170, 0x829A, 5, 1, 198);
        TiffEntry(w, 182, 0xA002, 3, 1, 4);
        w.W32(194, 0);
        w.W32(198, 1).W32(202, 250);
        for (int i = 0; i < 48; i++)
        {
            b.U8(206 + i, i * 5);
        }

        return b.ToArray();
    }

    /// <summary>JPEG: JFIF、Exif、DQT、SOF0、DHT、SOS、圧縮データ、EOI。</summary>
    private static byte[] Jpeg()
    {
        var b = new Buf();
        b.Hex(0, "FF D8");
        // JFIF
        b.Hex(2, "FF E0 00 10 4A 46 49 46 00 01 01 01 00 48 00 48 00 00");
        // Exif (リトルエンディアンの TIFF)
        var t = new Buf().Size(78);
        var w = new Ew(t, false);
        t.Str(0, "II");
        w.W16(2, 42).W32(4, 8).W16(8, 3);
        TiffEntry(w, 10, 0x010F, 2, 10, 50);
        TiffEntry(w, 22, 0x0112, 3, 1, 1);
        TiffEntry(w, 34, 0x8769, 4, 1, 60);
        w.W32(46, 0);
        t.Str(50, "HexEditor");
        w.W16(60, 1);
        TiffEntry(w, 62, 0xA002, 3, 1, 8);
        w.W32(74, 0);
        b.Add(0xFF, 0xE1, 0x00, 86).Str(b.Length, "Exif\0\0").At(b.Length, t.ToArray());
        // DQT
        b.Hex(b.Length, "FF DB 00 43 00");
        for (int i = 0; i < 64; i++)
        {
            b.Add((byte)(8 + (i / 4)));
        }

        // SOF0 (8×8、3 成分)
        b.Hex(b.Length, "FF C0 00 11 08 00 08 00 08 03 01 22 00 02 11 01 03 11 01");
        // DHT (DC テーブル 0: 符号長 1 が 1 個、符号長 2 が 2 個)
        b.Hex(b.Length, "FF C4 00 16 00 01 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 02");
        // SOS
        b.Hex(b.Length, "FF DA 00 0C 03 01 00 02 11 03 11 00 3F 00");
        b.Hex(b.Length, "12 34 56 78 9A BC DE F0");
        b.Hex(b.Length, "FF D9");
        return b.ToArray();
    }

    private static void PngChunk(Buf b, string type, byte[] data)
    {
        int at = b.Length;
        b.B32(at, data.Length).Str(at + 4, type).At(at + 8, data);
        byte[] crcInput = b.Span(at + 4, 4 + data.Length).ToArray();
        b.B32(b.Length, Crc32(crcInput));
    }

    /// <summary>PNG (4×4、パレット、PLTE・gAMA・sRGB・pHYs・tIME・tEXt・IDAT・IEND)。</summary>
    private static byte[] Png()
    {
        var b = new Buf();
        b.Hex(0, "89 50 4E 47 0D 0A 1A 0A");
        var ihdr = new Buf().Size(13);
        ihdr.B32(0, 4).B32(4, 4).U8(8, 8).U8(9, 3);
        PngChunk(b, "IHDR", ihdr.ToArray());
        PngChunk(b, "gAMA", [0x00, 0x00, 0xB1, 0x8F]);
        PngChunk(b, "sRGB", [0]);
        PngChunk(b, "PLTE", [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]);
        PngChunk(b, "pHYs", [0, 0, 0x0B, 0x13, 0, 0, 0x0B, 0x13, 1]);
        PngChunk(b, "tIME", [0x07, 0xEA, 10, 16, 12, 34, 56]);
        PngChunk(b, "tEXt", Encoding.Latin1.GetBytes("Comment\0Hello from HexEditor"));
        byte[] raw = new byte[4 * 5];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                raw[(y * 5) + 1 + x] = (byte)((x + y) % 4);
            }
        }

        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zs.Write(raw);
        }

        PngChunk(b, "IDAT", z.ToArray());
        PngChunk(b, "IEND", []);
        return b.ToArray();
    }

    /// <summary>GIF89a (4×4、グローバルカラーテーブル 4 色、拡張 3 種類、1 枚の画像)。</summary>
    private static byte[] Gif()
    {
        var b = new Buf();
        b.Str(0, "GIF89a").U16(6, 4).U16(8, 4).U8(10, 0xF1).U8(11, 0).U8(12, 0);
        b.Hex(13, "FF 00 00 00 FF 00 00 00 FF FF FF FF");
        // グラフィックコントロール拡張
        b.Hex(b.Length, "21 F9 04 05 0A 00 01 00");
        // コメント拡張
        b.Hex(b.Length, "21 FE 0D").Str(b.Length, "HexEditor GIF").Add(0);
        // アプリケーション拡張 (NETSCAPE2.0)
        b.Hex(b.Length, "21 FF 0B").Str(b.Length, "NETSCAPE2.0").Hex(b.Length, "03 01 00 00 00");
        // 画像記述子
        b.Hex(b.Length, "2C 00 00 00 00 04 00 04 00 00");
        // LZW (最小コードサイズ 2)
        b.Hex(b.Length, "02 05 84 1D 81 7A 50 00");
        b.Add(0x3B);
        return b.ToArray();
    }

    /// <summary>BMP (4×4、8 bit、パレット 4 色)。</summary>
    private static byte[] Bmp()
    {
        var b = new Buf().Size(14 + 40 + 16 + 16);
        b.Str(0, "BM").U32(2, 86).U32(10, 70);
        b.U32(14, 40).U32(18, 4).U32(22, 4).U16(26, 1).U16(28, 8).U32(34, 16).U32(38, 2835).U32(42, 2835).U32(46, 4).U32(50, 4);
        b.Hex(54, "00 00 FF 00 00 FF 00 00 FF 00 00 00 FF FF FF 00");
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                b.U8(70 + (y * 4) + x, (x + y) % 4);
            }
        }

        return b.ToArray();
    }

    /// <summary>ICO: 4×4 の DIB と 4×4 の PNG の 2 つの画像。</summary>
    private static byte[] Ico()
    {
        byte[] png = Png();
        var dib = new Buf().Size(40 + 64 + 16);
        dib.U32(0, 40).U32(4, 4).U32(8, 8).U16(12, 1).U16(14, 32).U32(20, 80);
        for (int i = 0; i < 16; i++)
        {
            dib.U32(40 + (i * 4), 0xFF000000 | (uint)(i * 0x101010));
        }

        var b = new Buf().Size(6 + 32);
        b.U16(2, 1).U16(4, 2);
        b.U8(6, 4).U8(7, 4).U16(10, 1).U16(12, 32).U32(14, dib.Length).U32(18, 38);
        b.U8(22, 4).U8(23, 4).U16(26, 1).U16(28, 8).U32(30, png.Length).U32(34, 38 + dib.Length);
        b.At(38, dib.ToArray());
        b.At(38 + dib.Length, png);
        return b.ToArray();
    }

    /// <summary>TGA 2.0 (4×4、無圧縮のトゥルーカラー 24 bit、画像 ID あり、フッターあり)。</summary>
    private static byte[] Tga()
    {
        var b = new Buf().Size(18);
        b.U8(0, 6).U8(1, 0).U8(2, 2).U16(12, 4).U16(14, 4).U8(16, 24).U8(17, 0x20);
        b.Str(18, "HexTGA");
        for (int i = 0; i < 48; i++)
        {
            b.Add((byte)(i * 5));
        }

        // フッター: 拡張領域のオフセット、開発者ディレクトリのオフセット、署名
        b.U32(b.Length, 0).U32(b.Length, 0).Str(b.Length, "TRUEVISION-XFILE.\0");
        return b.ToArray();
    }

    // ---- 音声・動画 ----

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] RiffChunk(string id, byte[] data)
    {
        var b = new Buf().Size(8);
        b.Str(0, id).U32(4, data.Length).At(8, data);
        if ((data.Length & 1) != 0)
        {
            b.Zero(1);
        }

        return b.ToArray();
    }

    private static byte[] RiffList(string type, params byte[][] children) => RiffChunk("LIST", Cat(Encoding.ASCII.GetBytes(type), Cat(children)));

    private static byte[] Riff(string form, params byte[][] chunks)
    {
        byte[] body = Cat(chunks);
        var b = new Buf().Size(12);
        b.Str(0, "RIFF").U32(4, 4 + body.Length).Str(8, form).At(12, body);
        return b.ToArray();
    }

    /// <summary>WebP: VP8X (アルファ + Exif、16×16)、VP8L、EXIF。</summary>
    private static byte[] Webp()
    {
        var vp8x = new Buf().Size(10);
        vp8x.U8(0, 0x18).At(4, 15, 0, 0, 15, 0, 0);
        var vp8l = new Buf().Size(13);
        vp8l.U8(0, 0x2F).U32(1, 15 | (15 << 14) | (1 << 28)).Hex(5, "00 10 07 10 11 11 88 88");
        byte[] exif = Cat(Encoding.ASCII.GetBytes("Exif\0\0"), [0x49, 0x49]);
        return Riff("WEBP", RiffChunk("VP8X", vp8x.ToArray()), RiffChunk("VP8L", vp8l.ToArray()), RiffChunk("EXIF", exif));
    }

    /// <summary>WAV: PCM 16 bit、モノラル、8000 Hz、16 サンプル、LIST INFO。</summary>
    private static byte[] Wav()
    {
        var fmt = new Buf().Size(16);
        fmt.U16(0, 1).U16(2, 1).U32(4, 8000).U32(8, 16000).U16(12, 2).U16(14, 16);
        var data = new Buf().Size(32);
        for (int i = 0; i < 16; i++)
        {
            data.U16(i * 2, (i * 2000) & 0x7FFF);
        }

        byte[] info = RiffChunk("ISFT", Encoding.ASCII.GetBytes("HexEditor\0"));
        return Riff("WAVE", RiffChunk("fmt ", fmt.ToArray()), RiffChunk("data", data.ToArray()), RiffList("INFO", info));
    }

    /// <summary>AVI: メインヘッダ、映像ストリーム 1 つ、movi (フレーム 2 つ)、idx1。</summary>
    private static byte[] Avi()
    {
        var avih = new Buf().Size(56);
        avih.U32(0, 66667).U32(4, 100000).U32(12, 0x110).U32(16, 2).U32(24, 1).U32(28, 4096).U32(32, 16).U32(36, 16);
        var strh = new Buf().Size(56);
        strh.Str(0, "vids").Str(4, "MJPG").U32(20, 1).U32(24, 15).U32(32, 2).U32(36, 4096).U16(52, 16).U16(54, 16);
        var strf = new Buf().Size(40);
        strf.U32(0, 40).U32(4, 16).U32(8, 16).U16(12, 1).U16(14, 24).Str(16, "MJPG").U32(20, 768);
        byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] movi = RiffList("movi", RiffChunk("00dc", frame), RiffChunk("00dc", frame));
        var idx = new Buf();
        idx.Str(0, "00dc").U32(4, 0x10).U32(8, 4).U32(12, 8).Str(16, "00dc").U32(20, 0x10).U32(24, 20).U32(28, 8);
        return Riff("AVI ",
            RiffList("hdrl", RiffChunk("avih", avih.ToArray()), RiffList("strl", RiffChunk("strh", strh.ToArray()), RiffChunk("strf", strf.ToArray()))),
            movi,
            RiffChunk("idx1", idx.ToArray()));
    }

    /// <summary>MP3: ID3v2.3 (TIT2、TPE1、パディング)、MPEG-1 Layer III 128 kbps 44.1 kHz のフレーム 3 つ、ID3v1。</summary>
    private static byte[] Mp3()
    {
        var b = new Buf();
        b.Str(0, "ID3").Hex(3, "03 00 00 00 00 00 31");
        b.Str(10, "TIT2").B32(14, 9).U16(18, 0).U8(20, 0).Str(21, "HexTitle");
        b.Str(29, "TPE1").B32(33, 10).U16(37, 0).U8(39, 0).Str(40, "HexArtist");
        b.Zero(10);
        for (int i = 0; i < 3; i++)
        {
            int at = b.Length;
            b.Size(at + 417);
            b.Hex(at, "FF FB 90 00");
            b.U8(at + 4, i + 1);
        }

        int v1 = b.Length;
        b.Size(v1 + 128).Str(v1, "TAG").Str(v1 + 3, "HexTitle").Str(v1 + 33, "HexArtist").Str(v1 + 63, "HexAlbum").Str(v1 + 93, "2026").Str(v1 + 97, "test").U8(v1 + 127, 12);
        return b.ToArray();
    }

    /// <summary>FLAC: STREAMINFO と VORBIS_COMMENT のメタデータブロックと、音声フレーム (ダミー)。</summary>
    private static byte[] Flac()
    {
        var b = new Buf();
        b.Str(0, "fLaC");
        b.Hex(4, "00 00 00 22");
        b.B16(8, 4096).B16(10, 4096).B24(12, 0).B24(15, 0);
        long parameters = (44100L << 44) | (1L << 41) | (15L << 36) | 44100;
        b.B64(18, parameters);
        for (int i = 0; i < 16; i++)
        {
            b.U8(26 + i, 0xD0 + i);
        }

        b.Hex(42, "84 00 00 1E");
        b.U32(46, 9).Str(50, "HexEditor").U32(59, 1).U32(63, 9).Str(67, "TITLE=Hex");
        b.Hex(76, "FF F8 69 18 00 00 00 00 00 00 00 00 00 00 00 00");
        return b.ToArray();
    }

    private static uint OggCrc(byte[] data)
    {
        uint crc = 0;
        foreach (byte x in data)
        {
            crc ^= (uint)x << 24;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        return crc;
    }

    private static byte[] OggPage(int type, long granule, uint serial, uint sequence, byte[] payload)
    {
        var b = new Buf().Size(27);
        b.Str(0, "OggS").U8(5, type).U64(6, granule).U32(14, serial).U32(18, sequence).U8(26, 1).Add((byte)payload.Length).At(28, payload);
        b.U32(22, OggCrc(b.ToArray()));
        return b.ToArray();
    }

    /// <summary>Ogg Vorbis: 先頭の識別ヘッダのページと、最後のページ。</summary>
    private static byte[] Ogg()
    {
        var id = new Buf().Size(30);
        id.U8(0, 1).Str(1, "vorbis").U32(7, 0).U8(11, 2).U32(12, 44100).U32(16, 0).U32(20, 128000).U32(24, 0).U8(28, 0xB8).U8(29, 1);
        byte[] last = Encoding.ASCII.GetBytes("OggEnd0123");
        return Cat(OggPage(2, 0, 0x1234, 0, id.ToArray()), OggPage(4, 44100, 0x1234, 1, last));
    }

    /// <summary>標準 MIDI ファイル (形式 1、2 トラック)。</summary>
    private static byte[] Midi()
    {
        var b = new Buf();
        b.Str(0, "MThd").B32(4, 6).B16(8, 1).B16(10, 2).B16(12, 480);
        b.Str(14, "MTrk").B32(18, 19).Hex(22, "00 FF 51 03 07 A1 20 00 FF 58 04 04 02 18 08 00 FF 2F 00");
        b.Str(41, "MTrk").B32(45, 15).Hex(49, "00 C0 00 00 90 3C 40 60 80 3C 40 00 FF 2F 00");
        return b.ToArray();
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] body = Cat(parts);
        var b = new Buf().Size(8);
        b.B32(0, 8 + body.Length).Str(4, type).At(8, body);
        return b.ToArray();
    }

    /// <summary>MP4: ftyp、moov (mvhd、trak (tkhd、mdia (mdhd、hdlr)))、mdat。</summary>
    private static byte[] Mp4()
    {
        var ftyp = new Buf().Size(20);
        ftyp.Str(0, "isom").B32(4, 512).Str(8, "isom").Str(12, "iso2").Str(16, "mp41");
        var mvhd = new Buf().Size(100);
        mvhd.B32(4, 0xE0000000).B32(8, 0xE0000000).B32(12, 1000).B32(16, 2000).B32(20, 0x00010000).B16(24, 0x0100).B32(36, 0x00010000).B32(52, 0x00010000)
            .B32(68, 0x40000000).B32(96, 2);
        var tkhd = new Buf().Size(84);
        tkhd.B32(0, 3).B32(4, 0xE0000000).B32(8, 0xE0000000).B32(12, 1).B32(20, 2000).B32(40, 0x00010000).B32(56, 0x00010000).B32(72, 0x40000000)
            .B32(76, 16 << 16).B32(80, 16 << 16);
        var mdhd = new Buf().Size(24);
        mdhd.B32(4, 0xE0000000).B32(8, 0xE0000000).B32(12, 12800).B32(16, 25600).B16(20, 0x55C4);
        var hdlr = new Buf().Size(24);
        hdlr.Str(8, "vide");
        byte[] name = Encoding.ASCII.GetBytes("VideoHandler\0");
        byte[] mdat = [1, 2, 3, 4, 5, 6, 7, 8];
        return Cat(
            Box("ftyp", ftyp.ToArray()),
            Box("moov", Box("mvhd", mvhd.ToArray()), Box("trak", Box("tkhd", tkhd.ToArray()), Box("mdia", Box("mdhd", mdhd.ToArray()), Box("hdlr", hdlr.ToArray(), name)))),
            Box("mdat", mdat));
    }

    private static byte[] Ebml(long id, params byte[][] parts) => EbmlSized(id, 0, Cat(parts));

    /// <summary>EBML の要素。<paramref name="sizeLength"/> が 0 なら最短のサイズ表現。</summary>
    private static byte[] EbmlSized(long id, int sizeLength, byte[] body)
    {
        var b = new Buf();
        int idLength = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        for (int i = idLength - 1; i >= 0; i--)
        {
            b.Add((byte)(id >> (8 * i)));
        }

        int n = sizeLength > 0 ? sizeLength : 1;
        while (sizeLength == 0 && body.Length >= (1L << (7 * n)) - 1)
        {
            n++;
        }

        // n バイトのサイズは、長さを表すマーカーのビット (1 << 7n) を立てた n バイトの値
        long encoded = (1L << (7 * n)) | (long)(uint)body.Length;
        for (int i = n - 1; i >= 0; i--)
        {
            b.Add((byte)(encoded >> (8 * i)));
        }

        b.Add(body);
        return b.ToArray();
    }

    private static byte[] EbmlUInt(long id, long value)
    {
        int n = value > 0xFFFFFF ? 4 : value > 0xFFFF ? 3 : value > 0xFF ? 2 : 1;
        byte[] data = new byte[n];
        for (int i = 0; i < n; i++)
        {
            data[i] = (byte)(value >> (8 * (n - 1 - i)));
        }

        return Ebml(id, data);
    }

    private static byte[] EbmlString(long id, string s) => Ebml(id, Encoding.ASCII.GetBytes(s));

    private static byte[] EbmlFloat32(long id, float v)
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(data, v);
        return Ebml(id, data);
    }

    /// <summary>WebM: EBML ヘッダ、Segment (Info、Tracks、Cluster、Cues)。Segment のサイズは 8 バイトの表現にする。</summary>
    private static byte[] Webm()
    {
        byte[] header = Ebml(0x1A45DFA3, EbmlUInt(0x4286, 1), EbmlUInt(0x42F7, 1), EbmlUInt(0x42F2, 4), EbmlUInt(0x42F3, 8), EbmlString(0x4282, "webm"),
            EbmlUInt(0x4287, 4), EbmlUInt(0x4285, 2));
        byte[] segment = Cat(
            Ebml(0x1549A966, EbmlUInt(0x2AD7B1, 1000000), EbmlString(0x4D80, "HexEditor"), EbmlString(0x5741, "HexEditor"), EbmlFloat32(0x4489, 1000f)),
            Ebml(0x1654AE6B, Ebml(0xAE, EbmlUInt(0xD7, 1), EbmlUInt(0x73C5, 0x1234), EbmlUInt(0x83, 1), EbmlString(0x86, "V_VP8"),
                Ebml(0xE0, EbmlUInt(0xB0, 16), EbmlUInt(0xBA, 16)))),
            Ebml(0x1F43B675, EbmlUInt(0xE7, 0), Ebml(0xA3, new byte[] { 0x81, 0x00, 0x00, 0x80, 1, 2, 3, 4 })),
            Ebml(0x1C53BB6B, Ebml(0xBB, EbmlUInt(0xB3, 0), Ebml(0xB7, EbmlUInt(0xF7, 1)))));
        return Cat(header, EbmlSized(0x18538067, 8, segment));
    }

    // ---- アーカイブ (続き) ----

    /// <summary>GZIP: ファイル名とコメントを持つ 1 メンバー。</summary>
    private static byte[] Gzip()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Hello, HexEditor!\n");
        var b = new Buf();
        b.Hex(0, "1F 8B 08 18").U32(4, 0x5F000000).Hex(8, "00 03");
        b.Str(10, "hello.txt\0").Str(b.Length, "HexEditor\0");
        b.At(b.Length, Deflate(raw));
        b.U32(b.Length, Crc32(raw)).U32(b.Length, raw.Length);
        return b.ToArray();
    }

    private static void TarHeader(Buf b, string name, int type, int size, string content = "")
    {
        int at = b.Length;
        b.Size(at + 512);
        b.Str(at, name).Str(at + 100, "0000644\0").Str(at + 108, "0001750\0").Str(at + 116, "0001750\0");
        b.Str(at + 124, Convert.ToString(size, 8).PadLeft(11, '0') + "\0").Str(at + 136, "14500000000\0");
        b.Str(at + 148, "        ").U8(at + 156, type).Str(at + 257, "ustar\0").Str(at + 263, "00").Str(at + 265, "hex").Str(at + 297, "hex");
        b.Str(at + 329, "0000000\0").Str(at + 337, "0000000\0");
        int sum = 0;
        for (int i = 0; i < 512; i++)
        {
            sum += b.Span(at, 512)[i];
        }

        b.Str(at + 148, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        if (content.Length > 0)
        {
            int d = b.Length;
            b.Size(d + 512).Str(d, content);
        }
    }

    /// <summary>TAR (ustar): ディレクトリ 1 つ、ファイル 1 つ、終端の 0 のブロック 2 つ。</summary>
    private static byte[] Tar()
    {
        var b = new Buf();
        TarHeader(b, "dir/", '5', 0);
        TarHeader(b, "dir/hello.txt", '0', 13, "Hello, tar!\n\0");
        b.Size(b.Length + 1024);
        return b.ToArray();
    }

    /// <summary>7z: シグネチャヘッダ、パックストリーム 16 バイト、次のヘッダ 10 バイト (CRC は正しい値)。</summary>
    private static byte[] SevenZip()
    {
        var b = new Buf().Size(32);
        b.Hex(0, "37 7A BC AF 27 1C 00 04");
        byte[] pack = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        byte[] next = [0x01, 0x04, 0x06, 0x00, 0x01, 0x09, 0x10, 0x00, 0x0A, 0x00];
        b.At(32, pack).At(48, next);
        b.U64(12, pack.Length).U64(20, next.Length).U32(28, Crc32(next));
        b.U32(8, Crc32(b.Span(12, 20)));
        return b.ToArray();
    }

    private static byte[] RarBlock(byte[] header, byte[] data)
    {
        // ヘッダの CRC の対象は、ヘッダのサイズの欄から先
        var content = new Buf();
        content.Add((byte)header.Length).Add(header);
        var b = new Buf();
        b.U32(0, Crc32(content.ToArray())).At(4, content.ToArray()).At(b.Length, data);
        return b.ToArray();
    }

    /// <summary>RAR 5: シグネチャ、メインブロック、ファイルブロック (a.txt)、終端ブロック。</summary>
    private static byte[] Rar()
    {
        byte[] main = RarBlock([1, 0, 0], []);
        byte[] file = RarBlock([2, 2, 5, 0, 5, 0x20, 0, 1, 5, (byte)'a', (byte)'.', (byte)'t', (byte)'x', (byte)'t'], Encoding.ASCII.GetBytes("hello"));
        byte[] end = RarBlock([5, 0, 0], []);
        return Cat([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], main, file, end);
    }

    /// <summary>XZ: ストリームヘッダ (CRC32)、ブロック 16 バイト、索引 (1 レコード)、ストリームフッター。</summary>
    private static byte[] Xz()
    {
        var b = new Buf();
        b.Hex(0, "FD 37 7A 58 5A 00 00 01");
        b.U32(8, Crc32([0x00, 0x01]));
        b.At(12, Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray());
        int idx = b.Length;
        b.Hex(idx, "00 01 20 16");
        b.U32(idx + 4, Crc32(b.Span(idx, 4)));
        int f = b.Length;
        b.U32(f + 4, 1).Hex(f + 8, "00 01").Hex(f + 10, "59 5A");
        b.U32(f, Crc32(b.Span(f + 4, 6)));
        return b.ToArray();
    }

    /// <summary>CAB: 1 フォルダ (無圧縮)、2 ファイル、CFDATA 1 つ。</summary>
    private static byte[] Cab()
    {
        var b = new Buf().Size(108);
        b.Str(0, "MSCF").U32(8, 108).U32(16, 44).U8(24, 3).U8(25, 1).U16(26, 1).U16(28, 2).U16(30, 0).U16(32, 0x1234).U16(34, 0);
        b.U32(36, 88).U16(40, 1).U16(42, 0);
        b.U32(44, 6).U32(48, 0).U16(52, 0).U16(54, 0x5D50).U16(56, 0x645C).U16(58, 0x20).Str(60, "a.txt\0");
        b.U32(66, 6).U32(70, 6).U16(74, 0).U16(76, 0x5D50).U16(78, 0x645C).U16(80, 0x20).Str(82, "b.txt\0");
        b.U32(88, 0).U16(92, 12).U16(94, 12).Str(96, "HexEditorCAB");
        return b.ToArray();
    }

    // ---- 文書 ----

    /// <summary>PDF 1.4: 3 つのオブジェクト、xref 表、トレーラ、startxref。</summary>
    private static byte[] Pdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        string[] objects =
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n",
        ];
        foreach (string o in objects)
        {
            offsets.Add(sb.Length);
            sb.Append(o);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int off in offsets)
        {
            sb.Append(off.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>OLE 複合文書 (セクタ 512 バイト): ヘッダ、FAT のセクタ 1 つ、ディレクトリのセクタ 1 つ (Root Entry と Stream1)。</summary>
    private static byte[] Cfb()
    {
        var b = new Buf().Size(1536);
        b.Hex(0, "D0 CF 11 E0 A1 B1 1A E1");
        b.U16(24, 0x3E).U16(26, 3).U16(28, 0xFFFE).U16(30, 9).U16(32, 6).U32(44, 1).U32(48, 1).U32(56, 4096).U32(60, 0xFFFFFFFE)
            .U32(68, 0xFFFFFFFE);
        for (int i = 0; i < 109; i++)
        {
            b.U32(76 + (i * 4), i == 0 ? 0 : 0xFFFFFFFF);
        }

        // FAT (セクタ 0)
        for (int i = 0; i < 128; i++)
        {
            b.U32(512 + (i * 4), i == 0 ? 0xFFFFFFFD : i == 1 ? 0xFFFFFFFE : 0xFFFFFFFF);
        }

        // ディレクトリ (セクタ 1)
        int d = 1024;
        DirEntry(b, d, "Root Entry", 5, 1, 0xFFFFFFFF, 0xFFFFFFFF, 1, 0xFFFFFFFE, 0);
        DirEntry(b, d + 128, "Stream1", 2, 1, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFE, 0);
        DirEntry(b, d + 256, "", 0, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0);
        DirEntry(b, d + 384, "", 0, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0);
        return b.ToArray();
    }

    private static void DirEntry(Buf b, int at, string name, int type, int color, long left, long right, long child, long start, long size)
    {
        if (name.Length > 0)
        {
            b.Utf16(at, name, 64).U16(at + 64, (name.Length + 1) * 2);
        }

        b.U8(at + 66, type).U8(at + 67, color).U32(at + 68, left).U32(at + 72, right).U32(at + 76, child).U32(at + 116, start).U64(at + 120, size);
    }

    /// <summary>RTF (ヘッダと短い本文)。</summary>
    private static byte[] Rtf() =>
        Encoding.ASCII.GetBytes("{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Arial;}}\n\\f0 Hello, HexEditor!\\par\n}\n");

    // ---- ファイルシステム ----

    /// <summary>FAT32 のボリュームの先頭 2 セクタ (ブートセクタと FSInfo)。</summary>
    private static byte[] Fat32()
    {
        var b = new Buf().Size(1024);
        b.Hex(0, "EB 58 90").Str(3, "MSWIN4.1").U16(11, 512).U8(13, 8).U16(14, 32).U8(16, 2).U8(21, 0xF8).U16(24, 63).U16(26, 255).U32(28, 2048).U32(32, 0x20000);
        b.U32(36, 512).U32(44, 2).U16(48, 1).U16(50, 6).U8(64, 0x80).U8(66, 0x29).U32(67, 0x1234ABCD).Str(71, "HXTESTFAT32").Str(82, "FAT32   ");
        b.Hex(510, "55 AA");
        b.U32(512, 0x41615252).U32(512 + 484, 0x61417272).U32(512 + 488, 16000).U32(512 + 492, 3).U32(512 + 508, 0xAA550000);
        return b.ToArray();
    }

    /// <summary>FAT16 のボリュームのブートセクタ。</summary>
    private static byte[] Fat16()
    {
        var b = new Buf().Size(512);
        b.Hex(0, "EB 3C 90").Str(3, "MSDOS5.0").U16(11, 512).U8(13, 4).U16(14, 1).U8(16, 2).U16(17, 512).U8(21, 0xF8).U16(22, 64).U16(24, 32).U16(26, 64).U32(32, 0x10000);
        b.U8(36, 0x80).U8(38, 0x29).U32(39, 0x5678CDEF).Str(43, "HXTESTFAT16").Str(54, "FAT16   ");
        b.Hex(510, "55 AA");
        return b.ToArray();
    }

    /// <summary>exFAT のメインブートセクタ。</summary>
    private static byte[] ExFat()
    {
        var b = new Buf().Size(512);
        b.Hex(0, "EB 76 90").Str(3, "EXFAT   ").U64(64, 2048).U64(72, 0x20000).U32(80, 2048).U32(84, 1024).U32(88, 4096).U32(92, 15000).U32(96, 4).U32(100, 0xCAFE0001)
            .U8(105, 1).U8(108, 9).U8(109, 3).U8(110, 1).U8(111, 0x80);
        b.Hex(510, "55 AA");
        return b.ToArray();
    }

    private static void NtfsAttributeHeader(Buf b, int at, int type, int length, int valueLength, int valueOffset)
    {
        b.U32(at, type).U32(at + 4, length).U16(at + 10, 0x18).U32(at + 16, valueLength).U16(at + 20, valueOffset);
    }

    private static void NtfsRecord(Buf b, int at, int number, string name)
    {
        b.Str(at, "FILE").U16(at + 4, 0x30).U16(at + 6, 3).U16(at + 16, 1).U16(at + 18, 1).U16(at + 20, 0x38).U16(at + 22, 1).U32(at + 28, 1024).U16(at + 40, 3)
            .U32(at + 44, number);
        int p = at + 0x38;
        // $STANDARD_INFORMATION (常駐、48 バイト)
        NtfsAttributeHeader(b, p, 0x10, 72, 48, 24);
        b.U64(p + 24, 0x01D9_0000_0000_0000).U64(p + 32, 0x01D9_0000_0000_0000).U64(p + 40, 0x01D9_0000_0000_0000).U64(p + 48, 0x01D9_0000_0000_0000).U32(p + 56, 0x6);
        p += 72;
        // $FILE_NAME (常駐、66 + 名前)
        int valueLength = 66 + (name.Length * 2);
        int length = (24 + valueLength + 7) / 8 * 8;
        NtfsAttributeHeader(b, p, 0x30, length, valueLength, 24);
        b.U64(p + 24, 5 | (5L << 48)).U32(p + 24 + 56, 0x6).U8(p + 24 + 64, name.Length).U8(p + 24 + 65, 3).Utf16(p + 24 + 66, name);
        p += length;
        if (number == 0)
        {
            // $DATA (非常駐)
            b.U32(p, 0x80).U32(p + 4, 72).U8(p + 8, 1).U16(p + 10, 0x40);
            b.U64(p + 16, 0).U64(p + 24, 31).U16(p + 32, 0x40).U64(p + 40, 0x20000).U64(p + 48, 0x20000).U64(p + 56, 0x20000);
            b.Hex(p + 64, "21 20 01 00 00 00 00 00");
            p += 72;
        }

        b.U32(p, 0xFFFFFFFF);
        b.U32(at + 24, p + 8 - at);
    }

    /// <summary>NTFS: ブートセクタと、MFT の先頭 32 レコード (先頭の 4 つが FILE レコード)。</summary>
    private static byte[] Ntfs()
    {
        var b = new Buf().Size(4096 + (8 * 1024));
        b.Hex(0, "EB 52 90").Str(3, "NTFS    ").U16(11, 512).U8(13, 8).U8(21, 0xF8).U16(24, 63).U16(26, 255).U32(28, 2048).U32(36, 0x80008000).U64(40, 0x2000).U64(48, 1).U64(56, 0x100)
            .U8(64, 0xF6).U8(68, 1).U64(72, 0x1122334455667788);
        b.Hex(510, "55 AA");
        string[] names = ["$MFT", "$MFTMirr", "$LogFile", "$Volume"];
        for (int i = 0; i < names.Length; i++)
        {
            NtfsRecord(b, 4096 + (i * 1024), i, names[i]);
        }

        return b.ToArray();
    }

    private static void IsoDate(Buf b, int at, string digits) => b.Str(at, digits).U8(at + 16, 0);

    private static void Both32At(Buf b, int at, long v) => b.U32(at, v).B32(at + 4, v);

    private static void Both16At(Buf b, int at, int v) => b.U16(at, v).B16(at + 2, v);

    /// <summary>ISO 9660: システム領域 32 KiB、プライマリボリューム記述子、終端。</summary>
    private static byte[] Iso9660()
    {
        var b = new Buf().Size(0x8000 + 4096);
        int p = 0x8000;
        b.U8(p, 1).Str(p + 1, "CD001").U8(p + 6, 1);
        b.Str(p + 8, "HEXEDITOR".PadRight(32)).Str(p + 40, "HXTEST_ISO".PadRight(32));
        Both32At(b, p + 80, 18);
        Both16At(b, p + 120, 1);
        Both16At(b, p + 124, 1);
        Both16At(b, p + 128, 2048);
        Both32At(b, p + 132, 10);
        b.U32(p + 140, 19).B32(p + 148, 20);
        int r = p + 156;
        b.U8(r, 34);
        Both32At(b, r + 2, 21);
        Both32At(b, r + 10, 2048);
        b.Hex(r + 18, "7E 0A 10 0C 22 38 00").U8(r + 25, 2);
        Both16At(b, r + 28, 1);
        b.U8(r + 32, 1).U8(r + 33, 0);
        b.Str(p + 190, "".PadRight(128)).Str(p + 318, "HexEditor".PadRight(128)).Str(p + 446, "".PadRight(128)).Str(p + 574, "HexEditor".PadRight(128));
        b.Str(p + 702, "".PadRight(37)).Str(p + 739, "".PadRight(37)).Str(p + 776, "".PadRight(37));
        IsoDate(b, p + 813, "2026101612345600");
        IsoDate(b, p + 830, "2026101612345600");
        IsoDate(b, p + 847, "0000000000000000");
        IsoDate(b, p + 864, "2026101612345600");
        b.U8(p + 881, 1);
        int t = p + 2048;
        b.U8(t, 255).Str(t + 1, "CD001").U8(t + 6, 1);
        return b.ToArray();
    }

    private static void VhdFooter(Buf b, int at, int diskType, long dataOffset)
    {
        b.Str(at, "conectix").B32(at + 8, 2).B32(at + 12, 0x00010000).B64(at + 16, dataOffset).B32(at + 24, 0x30000000).Str(at + 28, "hex ").B32(at + 32, 0x00010000)
            .Str(at + 36, "Wi2k").B64(at + 40, 4096).B64(at + 48, 4096).B16(at + 56, 1).U8(at + 58, 1).U8(at + 59, 8).B32(at + 60, diskType);
        for (int i = 0; i < 16; i++)
        {
            b.U8(at + 68 + i, 0x30 + i);
        }

        uint sum = 0;
        foreach (byte x in b.Span(at, 512))
        {
            sum += x;
        }

        b.B32(at + 64, ~sum);
    }

    /// <summary>VHD (固定): データ 4 KiB とフッター。</summary>
    private static byte[] VhdFixed()
    {
        var b = new Buf().Size(4096 + 512);
        VhdFooter(b, 4096, 2, -1);
        return b.ToArray();
    }

    /// <summary>VHD (可変): 先頭のフッターのコピー、動的ディスクヘッダ、BAT、末尾のフッター。</summary>
    private static byte[] VhdDynamic()
    {
        var b = new Buf().Size(2560);
        VhdFooter(b, 0, 3, 512);
        b.Str(512, "cxsparse").B64(520, -1).B64(528, 1536).B32(536, 0x00010000).B32(540, 2).B32(544, 0x200000);
        b.B32(1536, 0xFFFFFFFF).B32(1540, 0xFFFFFFFF);
        VhdFooter(b, 2048, 3, 512);
        return b.ToArray();
    }

    private static void VhdxHeader(Buf b, int at, long sequence)
    {
        b.Str(at, "head").U64(at + 8, sequence).At(at + 16, new Guid("11111111-2222-3333-4444-555555555555").ToByteArray())
            .At(at + 32, new Guid("66666666-7777-8888-9999-AAAAAAAAAAAA").ToByteArray()).U16(at + 64, 0).U16(at + 66, 1).U32(at + 68, 0x100000).U64(at + 72, 0x100000);
    }

    private static void VhdxRegionTable(Buf b, int at)
    {
        b.Str(at, "regi").U32(at + 8, 2);
        b.At(at + 16, new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08").ToByteArray()).U64(at + 32, 0x300000).U32(at + 40, 0x100000).U32(at + 44, 1);
        b.At(at + 48, new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E").ToByteArray()).U64(at + 64, 0x200000).U32(at + 72, 0x100000).U32(at + 76, 1);
    }

    /// <summary>VHDX: ファイルタイプ識別子、2 つのヘッダ、2 つのリージョンテーブル (先頭 260 KiB だけ)。</summary>
    private static byte[] Vhdx()
    {
        var b = new Buf().Size(0x40000 + 0x1000);
        b.Str(0, "vhdxfile").Utf16(8, "HexEditor");
        VhdxHeader(b, 0x10000, 1);
        VhdxHeader(b, 0x20000, 2);
        VhdxRegionTable(b, 0x30000);
        VhdxRegionTable(b, 0x40000);
        return b.ToArray();
    }

    /// <summary>ext4: 先頭 2 KiB (ブートブロックとスーパーブロック)。</summary>
    private static byte[] Ext4()
    {
        var b = new Buf().Size(2048);
        int s = 1024;
        b.U32(s, 128).U32(s + 4, 256).U32(s + 8, 12).U32(s + 12, 200).U32(s + 16, 100).U32(s + 20, 1).U32(s + 32, 8192).U32(s + 36, 8192).U32(s + 40, 128);
        b.U32(s + 44, 0x5F000000).U32(s + 48, 0x5F000100).U16(s + 52, 3).U16(s + 54, 0xFFFF).U16(s + 56, 0xEF53).U16(s + 58, 1).U16(s + 60, 1);
        b.U32(s + 64, 0x5F000000).U32(s + 76, 1).U32(s + 84, 11).U16(s + 88, 256).U32(s + 92, 0x3C).U32(s + 96, 0x2C2).U32(s + 100, 0x6B);
        for (int i = 0; i < 16; i++)
        {
            b.U8(s + 104 + i, 0x40 + i);
        }

        b.Str(s + 120, "HXTEST-EXT4").Str(s + 136, "/mnt").U16(s + 254, 64).U32(s + 264, 0x5F000200);
        return b.ToArray();
    }

    // ---- データベース・その他 ----

    /// <summary>SQLite 3: 1 ページ (512 バイト) のデータベース。sqlite_schema に表 t が 1 つ。</summary>
    private static byte[] Sqlite()
    {
        var b = new Buf().Size(512);
        b.Str(0, "SQLite format 3\0").B16(16, 512).U8(18, 1).U8(19, 1).U8(21, 64).U8(22, 32).U8(23, 32).B32(24, 1).B32(28, 1).B32(40, 1).B32(44, 4).B32(56, 1).B32(92, 1)
            .B32(96, 3045000);
        // ページヘッダ (オフセット 100): テーブルのリーフ、セル 1 つ、セル領域は 479 から
        b.U8(100, 0x0D).B16(103, 1).B16(105, 479).B16(108, 479);
        // セル: ペイロード 31 バイト、rowid 1、レコード (type, name, tbl_name, rootpage, sql)
        b.Hex(479, "1F 01 06 17 0F 0F 01 2F").Str(487, "table").Str(492, "t").Str(493, "t").U8(494, 2).Str(495, "CREATE TABLE t(a)");
        return b.ToArray();
    }

    /// <summary>LNK: 日時、ID リスト、LinkInfo (ローカルパス)、名前と相対パス、拡張データの終端。</summary>
    private static byte[] Lnk()
    {
        var b = new Buf().Size(76);
        b.U32(0, 0x4C).At(4, new Guid("00021401-0000-0000-C000-000000000046").ToByteArray()).U32(20, 0x8F).U32(24, 0x20)
            .U64(28, 0x01D9000000000000).U64(36, 0x01D9000000000000).U64(44, 0x01D9000000000000).U32(52, 14).U32(56, 0).U32(60, 1);
        // ID リスト: 項目 1 つ (20 バイト) + 終端
        b.U16(76, 22).U16(78, 20);
        b.Size(80 + 18).At(80, Enumerable.Range(0, 18).Select(i => (byte)(0x10 + i)).ToArray());
        b.U16(b.Length, 0);
        // LinkInfo (60 バイト)
        int li = b.Length;
        b.Size(li + 60);
        b.U32(li, 60).U32(li + 4, 28).U32(li + 8, 1).U32(li + 12, 28).U32(li + 16, 45).U32(li + 20, 0).U32(li + 24, 59);
        b.U32(li + 28, 17).U32(li + 32, 3).U32(li + 36, 0xA1B2C3D4).U32(li + 40, 16);
        b.Str(li + 45, "C:\\Test\\a.txt\0");
        // 文字列データ (UTF-16)
        int sd = b.Length;
        b.U16(sd, 9).Utf16(sd + 2, "HexEditor");
        sd = b.Length;
        b.U16(sd, 7).Utf16(sd + 2, ".\\a.txt");
        b.U32(b.Length, 0);
        return b.ToArray();
    }

    /// <summary>レジストリハイブ: ベースブロックと hbin 1 つ (キーノード ROOT、値 Value1、空き)。</summary>
    private static byte[] Regf()
    {
        var b = new Buf().Size(8192);
        b.Str(0, "regf").U32(4, 1).U32(8, 1).U64(12, 0x01D9000000000000).U32(20, 1).U32(24, 5).U32(28, 0).U32(32, 1).U32(36, 0x20).U32(40, 4096).U32(44, 1)
            .Utf16(48, "ntuser.dat", 64);
        uint x = 0;
        for (int i = 0; i < 127; i++)
        {
            x ^= BinaryPrimitives.ReadUInt32LittleEndian(b.Span(i * 4, 4));
        }

        b.U32(508, x);
        int h = 4096;
        b.Str(h, "hbin").U32(h + 4, 0).U32(h + 8, 4096).U64(h + 20, 0x01D9000000000000);
        int c = h + 0x20;
        b.U32(c, unchecked((uint)-88)).Str(c + 4, "nk").U16(c + 6, 0x2C).U64(c + 8, 0x01D9000000000000).U32(c + 20, 0xFFFFFFFF).U32(c + 24, 0).U32(c + 32, 0xFFFFFFFF)
            .U32(c + 40, 1).U32(c + 44, 0x100).U32(c + 48, 0xFFFFFFFF).U32(c + 52, 0xFFFFFFFF).U32(c + 80, 0).U16(c + 76, 4).Str(c + 80, "ROOT");
        c += 88;
        b.U32(c, unchecked((uint)-32)).Str(c + 4, "vk").U16(c + 6, 6).U32(c + 8, 0x80000004).U32(c + 12, 0x1234).U32(c + 16, 4).U16(c + 20, 1).Str(c + 24, "Value1");
        c += 32;
        b.U32(c, 4096 - 0x20 - 88 - 32);
        return b.ToArray();
    }

    /// <summary>PCAP (Ethernet、2 パケット)。<paramref name="big"/> でビッグエンディアン。</summary>
    private static byte[] Pcap(bool big)
    {
        var b = new Buf().Size(24);
        var w = new Ew(b, big);
        b.Hex(0, big ? "A1 B2 C3 D4" : "D4 C3 B2 A1");
        w.W16(4, 2).W16(6, 4).W32(16, 65535).W32(20, 1);
        for (int i = 0; i < 2; i++)
        {
            int at = b.Length;
            b.Size(at + 16 + 20);
            w.W32(at, 0x5F000000 + i).W32(at + 4, 123456).W32(at + 8, 20).W32(at + 12, 60);
            b.Hex(at + 16, "FF FF FF FF FF FF 00 11 22 33 44 55 08 00 45 00 00 14 00 00");
        }

        return b.ToArray();
    }

    /// <summary>PCAPNG: SHB (オプション付き)、IDB、EPB。</summary>
    private static byte[] Pcapng()
    {
        var b = new Buf().Size(48);
        b.U32(0, 0x0A0D0D0A).U32(4, 48).U32(8, 0x1A2B3C4D).U16(12, 1).U16(14, 0).U64(16, -1);
        b.U16(24, 4).U16(26, 9).Str(28, "HexEditor").U32(44, 48);
        // IDB
        int i = b.Length;
        b.Size(i + 20).U32(i, 1).U32(i + 4, 20).U16(i + 8, 1).U32(i + 12, 65535).U32(i + 16, 20);
        // EPB
        int e = b.Length;
        b.Size(e + 52).U32(e, 6).U32(e + 4, 52).U32(e + 8, 0).U32(e + 12, 0x0005F000).U32(e + 16, 0x12345678).U32(e + 20, 20).U32(e + 24, 60);
        b.Hex(e + 28, "FF FF FF FF FF FF 00 11 22 33 44 55 08 00 45 00 00 14 00 00").U32(e + 48, 52);
        return b.ToArray();
    }

    private static uint TableChecksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++)
            {
                v = (v << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            }

            sum += v;
        }

        return sum;
    }

    /// <summary>OpenType (CFF): テーブルディレクトリと head、maxp。</summary>
    private static byte[] Otf()
    {
        var head = new Buf().Size(54);
        head.B16(0, 1).B32(4, 0x00010000).B32(12, 0x5F0F3CF5).B16(16, 0x000B).B16(18, 1000).B64(20, 3_700_000_000).B64(28, 3_700_000_000).B16(36, -50 & 0xFFFF)
            .B16(38, -200 & 0xFFFF).B16(40, 1000).B16(42, 900).B16(46, 8).B16(48, 2);
        var maxp = new Buf().Size(6);
        maxp.B32(0, 0x00005000).B16(4, 12);
        var b = new Buf().Size(112);
        b.Str(0, "OTTO").B16(4, 2).B16(6, 32).B16(8, 1).B16(10, 0);
        b.Str(12, "head").B32(16, TableChecksum(head.ToArray())).B32(20, 48).B32(24, 54);
        b.Str(28, "maxp").B32(32, TableChecksum(maxp.ToArray())).B32(36, 104).B32(40, 6);
        b.At(48, head.ToArray()).At(104, maxp.ToArray());
        return b.ToArray();
    }

    private static byte[] Tlv(int tag, params byte[][] parts)
    {
        byte[] body = Cat(parts);
        var b = new Buf();
        b.Add((byte)tag);
        if (body.Length < 128)
        {
            b.Add((byte)body.Length);
        }
        else if (body.Length < 256)
        {
            b.Add(0x81, (byte)body.Length);
        }
        else
        {
            b.Add(0x82, (byte)(body.Length >> 8), (byte)body.Length);
        }

        b.Add(body);
        return b.ToArray();
    }

    private static byte[] Oid(string dotted)
    {
        int[] parts = dotted.Split('.').Select(int.Parse).ToArray();
        var bytes = new List<byte> { (byte)((parts[0] * 40) + parts[1]) };
        foreach (int p in parts.Skip(2))
        {
            var tmp = new List<byte>();
            int v = p;
            tmp.Add((byte)(v & 0x7F));
            v >>= 7;
            while (v > 0)
            {
                tmp.Add((byte)(0x80 | (v & 0x7F)));
                v >>= 7;
            }

            tmp.Reverse();
            bytes.AddRange(tmp);
        }

        return Tlv(0x06, bytes.ToArray());
    }

    private static byte[] Asn1Name(string commonName) =>
        Tlv(0x30, Tlv(0x31, Tlv(0x30, Oid("2.5.4.3"), Tlv(0x0C, Encoding.UTF8.GetBytes(commonName)))));

    /// <summary>DER: X.509 証明書の形をした ASN.1 (自作の構造で、本物の証明書ではない。鍵と署名は意味のないバイト列)。</summary>
    private static byte[] Der()
    {
        byte[] algorithm = Tlv(0x30, Oid("1.2.840.113549.1.1.11"), Tlv(0x05));
        byte[] extensions = Tlv(0xA3, Tlv(0x30, Tlv(0x30, Oid("2.5.29.17"), Tlv(0x04, Enumerable.Range(0, 260).Select(i => (byte)(0x61 + (i % 26))).ToArray()))));
        byte[] tbs = Tlv(0x30,
            Tlv(0xA0, Tlv(0x02, new byte[] { 2 })),
            Tlv(0x02, new byte[] { 0x01, 0x23 }),
            algorithm,
            Asn1Name("HexEditor Test CA"),
            Tlv(0x30, Tlv(0x17, Encoding.ASCII.GetBytes("260101000000Z")), Tlv(0x17, Encoding.ASCII.GetBytes("360101000000Z"))),
            Asn1Name("HexEditor Test"),
            Tlv(0x30, Tlv(0x30, Oid("1.2.840.113549.1.1.1"), Tlv(0x05)), Tlv(0x03, Cat(new byte[] { 0 }, Enumerable.Range(0, 16).Select(i => (byte)(0x80 + i)).ToArray()))),
            extensions);
        return Tlv(0x30, tbs, algorithm, Tlv(0x03, Cat(new byte[] { 0 }, Enumerable.Range(0, 32).Select(i => (byte)(0xE0 + i)).ToArray())));
    }

    // ---- 実行形式 (続き) ----

    /// <summary>Java クラスファイル: 定数プール 9 項目 (long を含む)、メソッド 1 つ、SourceFile 属性。</summary>
    private static byte[] JavaClass()
    {
        var b = new Buf();
        b.Hex(0, "CA FE BA BE 00 00 00 34");
        b.B16(8, 11);
        void Utf8(string s) { b.Add(1, (byte)(s.Length >> 8), (byte)s.Length).Add(Encoding.ASCII.GetBytes(s)); }
        Utf8("HexTest");
        b.Add(7, 0, 1);
        Utf8("java/lang/Object");
        b.Add(7, 0, 3);
        Utf8("main");
        Utf8("()V");
        Utf8("SourceFile");
        Utf8("HexTest.java");
        b.Add(5, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
        b.Add(0x00, 0x21, 0, 2, 0, 4);   // アクセスフラグ、this、super
        b.Add(0, 0);                       // インターフェース
        b.Add(0, 0);                       // フィールド
        b.Add(0, 1, 0, 9, 0, 5, 0, 6, 0, 0);   // メソッド 1 つ (public static main、属性なし)
        b.Add(0, 1, 0, 7, 0, 0, 0, 2, 0, 8);   // クラスの属性 SourceFile
        return b.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, s = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            s = (s + a) % 65521;
        }

        return (s << 16) | a;
    }

    /// <summary>DEX: 文字列 2、型 1 のヘッダと ID の表、マップリスト。</summary>
    private static byte[] Dex()
    {
        var b = new Buf().Size(208);
        b.Str(0, "dex\n035\0").U32(32, 208).U32(36, 0x70).U32(40, 0x12345678).U32(52, 144).U32(56, 2).U32(60, 112).U32(64, 1).U32(68, 120).U32(100, 0).U32(104, 0)
            .U32(108, 0);
        b.U32(104, 208 - 124).U32(108, 124);
        b.U32(112, 124).U32(116, 135).U32(120, 0);
        b.U8(124, 9).Str(125, "LHexTest;\0").U8(135, 5).Str(136, "Hello\0");
        // マップリスト (144): 5 項目
        b.U32(144, 5);
        (int Type, int Size, int Offset)[] map = [(0x0000, 1, 0), (0x0001, 2, 112), (0x0002, 1, 120), (0x2002, 2, 124), (0x1000, 1, 144)];
        for (int i = 0; i < map.Length; i++)
        {
            b.U16(148 + (i * 12), map[i].Type).U32(148 + (i * 12) + 4, map[i].Size).U32(148 + (i * 12) + 8, map[i].Offset);
        }

        byte[] hash = System.Security.Cryptography.SHA1.HashData(b.Span(32, b.Length - 32));
        b.At(12, hash);
        b.U32(8, Adler32(b.Span(12, b.Length - 12)));
        return b.ToArray();
    }

    /// <summary>WebAssembly: add 関数を 1 つ持つ最小のモジュール。</summary>
    private static byte[] Wasm() =>
        Convert.FromHexString("0061736D01000000" + "01070160027F7F017F" + "03020100" + "070701036164640000" + "0A09010700200020016A0B");

    // @@END
}
