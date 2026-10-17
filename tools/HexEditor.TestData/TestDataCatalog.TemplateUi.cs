using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData;

// cases/07-templates.md の表のテストデータのうち、テンプレートの画面のテスト (TPL-20〜TPL-35) で使うもの。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> TemplateUiItems() =>
    [
        new("TD-TPL-PNG-4CHUNKS", Png4Chunks().Length, "IHDR・tEXt・IDAT・2 つ目の IHDR・IEND のチャンクを持つ PNG の形のファイル",
            path => WriteAll(path, Png4Chunks()), dir => Path.Combine(dir, "TD-TPL-PNG-4CHUNKS.png")),
        new("TD-TPL-PNG-AS-BIN", Png16().Length, "TD-PNG と同じ内容で拡張子が .bin のファイル", path => WriteAll(path, Png16())),
        new("TD-TPL-MACHO64", MachO64().Length, "x86_64 の Mach-O の実行ファイル (fat ではない)", path => WriteAll(path, MachO64())),
        new("TD-ANA-MACHO-FAT", MachOFat().Length, "x86_64 のスライス 1 つを持つ fat の Mach-O", path => WriteAll(path, MachOFat())),
        new("TD-TPL-1000-TEMPLATES", GeneratedTemplate(999).Length, "テンプレート 1,000 個 (gen-0000.hxt〜gen-0999.hxt。最後のファイルのパスを返す)",
            WriteThousandTemplates, dir => Path.Combine(dir, "TD-TPL-1000-TEMPLATES", "gen-0999.hxt")),
    ];

    /// <summary>
    /// TD-TPL-PNG-4CHUNKS: シグネチャ、IHDR (16×16)、tEXt (<c>Comment</c> と <c>test</c>)、IDAT、2 つ目の IHDR (64×32)、IEND。CRC は正しい値。
    /// </summary>
    public static byte[] Png4Chunks()
    {
        byte[] png = Png16();

        // TD-PNG の IDAT のデータを取り出す (シグネチャ 8 + IHDR 25 の後)
        int idatLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(33));
        byte[] idat = png.AsSpan(41, idatLength).ToArray();
        var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(s, "IHDR", Ihdr(16, 16));
        WriteChunk(s, "tEXt", [.. "Comment"u8, 0, .. "test"u8]);
        WriteChunk(s, "IDAT", idat);
        WriteChunk(s, "IHDR", Ihdr(64, 32));
        WriteChunk(s, "IEND", []);
        return s.ToArray();

        static byte[] Ihdr(uint width, uint height)
        {
            byte[] ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8;
            ihdr[9] = 2;
            return ihdr;
        }
    }

    /// <summary>TD-TPL-MACHO64: mach_header_64 と LC_SEGMENT_64 (__PAGEZERO) 1 つを持つ 16 KiB の実行ファイル。</summary>
    public static byte[] MachO64()
    {
        byte[] b = new byte[16 * 1024];
        Span<byte> h = b;
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 0x01000007); // CPU_TYPE_X86_64
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 2); // MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 1); // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 72); // sizeofcmds
        Span<byte> c = h[32..];
        BinaryPrimitives.WriteUInt32LittleEndian(c, 0x19); // LC_SEGMENT_64
        BinaryPrimitives.WriteUInt32LittleEndian(c[4..], 72);
        "__PAGEZERO"u8.CopyTo(c[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(c[32..], 0x1_0000_0000);
        return b;
    }

    /// <summary>TD-ANA-MACHO-FAT: fat_header (ビッグエンディアン) と x86_64 のスライス 1 つ (TD-TPL-MACHO64 と同じ内容、0x1000 から)。</summary>
    public static byte[] MachOFat()
    {
        byte[] thin = MachO64();
        byte[] b = new byte[0x1000 + thin.Length];
        Span<byte> h = b;
        BinaryPrimitives.WriteUInt32BigEndian(h, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x01000007);
        BinaryPrimitives.WriteUInt32BigEndian(h[12..], 3);
        BinaryPrimitives.WriteUInt32BigEndian(h[16..], 0x1000);
        BinaryPrimitives.WriteUInt32BigEndian(h[20..], (uint)thin.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h[24..], 12);
        thin.CopyTo(b, 0x1000);
        return b;
    }

    /// <summary>TD-TPL-1000-TEMPLATES の 1 つ: 互いに異なり、既知のシグネチャと先頭が一致しない 8 バイトのマジック (先頭は 0xA5)。</summary>
    public static byte[] GeneratedTemplate(int index)
    {
        byte[] magic = new byte[8];
        Random(SeedOf("TD-TPL-1000-TEMPLATES") + (ulong)index, 0, magic);
        magic[0] = 0xA5;
        BinaryPrimitives.WriteUInt16BigEndian(magic.AsSpan(1), (ushort)index);
        string text = $"#pragma name \"Generated {index:D4}\"\n#pragma magic 0 \"{Convert.ToHexString(magic)}\"\n#pragma extensions \"gen{index:D4}\"\nu64 magic;\n";
        return Encoding.UTF8.GetBytes(text);
    }

    private static void WriteThousandTemplates(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        for (int i = 0; i < 999; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"gen-{i:D4}.hxt"), GeneratedTemplate(i));
        }

        File.WriteAllBytes(path, GeneratedTemplate(999));
    }
}
