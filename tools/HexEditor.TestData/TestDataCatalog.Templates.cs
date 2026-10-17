using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HexEditor.TestData;

// cases/07-templates.md の表のテンプレートのテストデータ。テンプレートのファイル (TD-TPL-*.hxt / .ksy) は
// tests/fixtures/templates/data/ に置き、ここではデータのファイルを作る。100 GiB のディスクイメージ (TD-TPL-GPT-100G、
// TD-TPL-MBR-100G) は、テストでは同じ内容を計算する仮想のデータソースで代わりにする (GptImage / MbrImage)。TD-TPL-ZBOMB-10G は、
// テストでは同じ zlib ストリームを読まれた分だけ作る仮想のデータソースで代わりにする (先頭の 256 MB 分しか読まれないため)。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> TemplateItems() =>
    [
        new("TD-TPL-PNG-IDAT", TplPngIdat().Length, "16×16 Truecolor の PNG。行 i のフィルタの種類は i mod 5", path => WriteAll(path, TplPngIdat()),
            dir => Path.Combine(dir, "TD-TPL-PNG-IDAT.png")),
        new("TD-TPL-TIFF-II", 26, "リトルエンディアンの TIFF の先頭と IFD (項目 1 つ)", path => WriteAll(path, TiffII())),
        new("TD-TPL-TIFF-MM", 26, "ビッグエンディアンの TIFF の先頭と IFD (項目 1 つ)", path => WriteAll(path, TiffMM())),
        new("TD-TPL-GIF", TplGif().Length, "4×4 の GIF89a (グローバルカラーテーブル 4 色、画像 1 つ)", path => WriteAll(path, TplGif())),
        new("TD-TPL-SLOW-DATA", 16, "SLOW の後に 00 を 12 バイト", path => WriteAll(path, [.. "SLOW"u8, .. new byte[12]])),
        new("TD-TPL-EXT-DATA", 8, "HXEX と 2A 00 00 00", path => WriteAll(path, [.. "HXEX"u8, 0x2A, 0, 0, 0])),
        new("TD-TPL-RGB565-DATA", 8, "00 F8 E0 07 1F 00 FF FF (赤、緑、青、白)", path => WriteAll(path, [0x00, 0xF8, 0xE0, 0x07, 0x1F, 0x00, 0xFF, 0xFF])),
        new("TD-TPL-REPO-DATA", 16, "HXTS の後に 00 を 12 バイト", path => WriteAll(path, [.. "HXTS"u8, .. new byte[12]])),
        // フォルダ: <キー>.bin のサンプルと、一覧の _samples.txt (Generate の戻り値は一覧のパス。サンプルは同じフォルダにある)
        new("TD-TPL-STD-SAMPLES", 0,
            "TPL-34 の標準テンプレートのサンプルファイル (フォルダ。すべて StandardTemplateSamples で組み立てた自作のもの)。期待する結果の JSON は tests/fixtures/templates/std/",
            path =>
            {
                StandardTemplateSamples.WriteAll(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, StandardTemplateSamples.Manifest());
            },
            dir => Path.Combine(dir, "TD-TPL-STD-SAMPLES", "_samples.txt")) { ComputeLength = () => StandardTemplateSamples.Manifest().Length },
    ];

    /// <summary>TD-TPL-GIF: 4×4 の GIF89a (標準テンプレートのサンプル gif と同じ。TC-TPL-33-01 で kaitai_struct_formats の gif.ksy に適用する)。</summary>
    public static byte[] TplGif() => [.. StandardTemplateSamples.All["gif"]];

    /// <summary>TD-TPL-PNG-IDAT: 16×16、ビット深度 8、Truecolor。行 i のフィルタの種類は i mod 5、画素は固定の種の乱数。</summary>
    public static byte[] TplPngIdat()
    {
        var raw = new MemoryStream();
        byte[] pixels = new byte[16 * 16 * 3];
        Random(SeedOf("TD-TPL-PNG-IDAT"), 0, pixels);
        for (int y = 0; y < 16; y++)
        {
            raw.WriteByte((byte)(y % 5));
            raw.Write(pixels, y * 48, 48);
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 16);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 16);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>TD-TPL-PNG-IDAT の展開後の画素データ (行ごとにフィルタの種類 1 バイトと 48 バイト)。</summary>
    public static byte[] TplPngIdatRaw()
    {
        var raw = new MemoryStream();
        byte[] pixels = new byte[16 * 16 * 3];
        Random(SeedOf("TD-TPL-PNG-IDAT"), 0, pixels);
        for (int y = 0; y < 16; y++)
        {
            raw.WriteByte((byte)(y % 5));
            raw.Write(pixels, y * 48, 48);
        }

        return raw.ToArray();
    }

    public static byte[] TiffII() =>
        Convert.FromHexString("4949" + "2A00" + "08000000" + "0100" + "0001" + "0300" + "01000000" + "10000000" + "00000000");

    public static byte[] TiffMM() =>
        Convert.FromHexString("4D4D" + "002A" + "00000008" + "0001" + "0100" + "0003" + "00000001" + "00100000" + "00000000");

    /// <summary>ID の文字列の SHA-256 の先頭 8 バイトを種にする (cases/07-templates.md の「このファイルで使うテストデータ」)。</summary>
    public static ulong SeedOf(string id) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    /// <summary>
    /// TD-TPL-GPT-100G の内容 (セクタ 512 バイト)。LBA 0 に保護 MBR、LBA 1 に GPT のヘッダ、LBA 2〜33 にパーティションエントリ 128 個
    /// (名前 HXTEST-DATA (LBA 2048〜) と HXTEST-LOG)、末尾に予備の GPT。CRC32 は正しい値にする。それ以外は 00。
    /// テストはこの関数で必要な範囲だけを作る仮想のデータソースを使う。
    /// </summary>
    public static void GptImage(long length, long offset, Span<byte> destination)
    {
        destination.Clear();
        long sectors = length / 512;
        byte[] entries = new byte[128 * 128];
        WriteGptEntry(entries.AsSpan(0, 128), 2048, (sectors / 2) - 1, "HXTEST-DATA", 1);
        WriteGptEntry(entries.AsSpan(128, 128), sectors / 2, sectors - 34, "HXTEST-LOG", 2);
        uint entriesCrc = Crc32(entries);
        byte[] mbr = new byte[512];
        mbr[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)Math.Min(sectors - 1, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        byte[] primary = GptHeader(1, sectors - 1, 2, sectors, entriesCrc);
        byte[] backup = GptHeader(sectors - 1, 1, sectors - 33, sectors, entriesCrc);
        Place(mbr, 0, offset, destination);
        Place(primary, 512, offset, destination);
        Place(entries, 2 * 512, offset, destination);
        Place(entries, (sectors - 33) * 512, offset, destination);
        Place(backup, (sectors - 1) * 512, offset, destination);
    }

    private static void Place(byte[] data, long at, long offset, Span<byte> destination)
    {
        long start = Math.Max(at, offset);
        long end = Math.Min(at + data.Length, offset + destination.Length);
        if (start < end)
        {
            data.AsSpan((int)(start - at), (int)(end - start)).CopyTo(destination[(int)(start - offset)..]);
        }
    }

    private static void WriteGptEntry(Span<byte> e, long first, long last, string name, byte uniqueSeed)
    {
        // Microsoft Basic Data の種類の GUID
        new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").TryWriteBytes(e);
        e.Slice(16, 16).Fill(uniqueSeed);
        BinaryPrimitives.WriteInt64LittleEndian(e[32..], first);
        BinaryPrimitives.WriteInt64LittleEndian(e[40..], last);
        Encoding.Unicode.GetBytes(name).CopyTo(e[56..]);
    }

    private static byte[] GptHeader(long current, long backup, long entriesLba, long sectors, uint entriesCrc)
    {
        byte[] h = new byte[512];
        "EFI PART"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), current);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), sectors - 34);
        h.AsSpan(56, 16).Fill(0x5A);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32(h.AsSpan(0, 92)));
        return h;
    }

    /// <summary>TD-TPL-MBR-100G の内容。LBA 0 に MBR (パーティション 2 つ: 種類 0x07 (LBA 2048〜) と 0x0C)、ブートシグネチャ 55 AA。</summary>
    public static void MbrImage(long length, long offset, Span<byte> destination)
    {
        destination.Clear();
        if (offset >= 512)
        {
            return;
        }

        long sectors = length / 512;
        byte[] mbr = new byte[512];
        mbr[446 + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)Math.Min((sectors / 2) - 2048, uint.MaxValue));
        mbr[462 + 4] = 0x0C;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(462 + 8), (uint)Math.Min(sectors / 2, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(462 + 12), (uint)Math.Min(sectors / 2, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        int n = (int)Math.Min(destination.Length, 512 - offset);
        mbr.AsSpan((int)offset, n).CopyTo(destination);
    }
}
