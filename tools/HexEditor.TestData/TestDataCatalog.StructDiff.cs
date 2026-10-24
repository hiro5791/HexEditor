using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData;

// cases/06-analysis.md の表の、構造を理解した比較 (ANA-31・ANA-32) のテストデータ (TD-ANA-STRUCT-*)。テンプレートのファイルは
// <置き場所>/TD-ANA-STRUCT/ に、テストケースの名前 (items.hxt など) で書き出す。1 億要素の TD-ANA-STRUCT-U64-A / B は 800 MB に
// なるため、テストでは同じ内容を計算する仮想のデータソース (U64Value) で代わりにする。
public static partial class TestDataCatalog
{
    /// <summary>TD-ANA-STRUCT-ARR-HXT: <c>items.hxt</c>。</summary>
    public const string StructItemsHxt = "#pragma endian little\nstruct Item { u32 id; u32 value; };\nu32 count;\nItem items[count];\n";

    /// <summary>TD-ANA-STRUCT-ARR-KEY-HXT: <c>items-key.hxt</c> (配列に <c>diff_key("id")</c>)。</summary>
    public const string StructItemsKeyHxt = "#pragma endian little\nstruct Item { u32 id; u32 value; };\nu32 count;\nItem items[count] [[diff_key(\"id\")]];\n";

    /// <summary>TD-ANA-STRUCT-U64-HXT: <c>u64-eof.hxt</c>。</summary>
    public const string StructU64Hxt = "#pragma endian little\nu64 a[eof];\n";

    /// <summary>TD-ANA-STRUCT-MANY-HXT: <c>many.hxt</c>。</summary>
    public const string StructManyHxt = "struct R { u8 a; u8 b; };\nR r[eof];\n";

    /// <summary>TD-ANA-STRUCT-MIXED-HXT: <c>mixed.hxt</c>。</summary>
    public const string StructMixedHxt = "#pragma endian little\nstruct Item { char tag[4]; u32 value; };\nu32 version;\n"
        + "if (version == 1) { u32 extra; } else { u16 extra; u16 extra_pad; }\nu32 count;\nItem items[count];\nu32 tail_len;\nu8 tail[tail_len];\n";

    /// <summary>TD-ANA-STRUCT-U64-A / B の長さ (1 億要素の u64)。</summary>
    public const long StructU64Length = 800_000_000;

    private static IEnumerable<TestDataItem> StructDiffItems() =>
    [
        new("TD-ANA-STRUCT-PE-A", PeX64().Length, "TD-PE-X64 と同じ内容 (a.exe)", path => WriteAll(path, StructPeA()),
            dir => Path.Combine(dir, "TD-ANA-STRUCT", "a.exe")),
        new("TD-ANA-STRUCT-PE-B", PeX64().Length, "TD-ANA-STRUCT-PE-A の nt.FileHeader.TimeDateStamp だけを 0x60000000 にしたもの (b.exe)",
            path => WriteAll(path, StructPeB()), dir => Path.Combine(dir, "TD-ANA-STRUCT", "b.exe")),
        Png("TD-ANA-STRUCT-PNG-A", StructPngA(), "IHDR・tEXt (Author/Alice)・tIME・IDAT・IEND の PNG"),
        Png("TD-ANA-STRUCT-PNG-B", StructPngB(), "幅 32、tEXt (Author/Bob)・tEXt (Comment/hello) を加え tIME を除いた PNG"),
        Png("TD-ANA-STRUCT-PNG-MOVE", StructPngMove(), "tEXt のチャンクを IDAT と IEND の間に移した PNG"),
        Png("TD-ANA-STRUCT-PNG-BROKEN", StructPngBroken(), "IDAT の長さを 0x7FFFFFF0 にした PNG"),
        Png("TD-ANA-STRUCT-TAIL-A", [.. StructPngA(), .. new byte[16]], "TD-ANA-STRUCT-PNG-A の後ろに 00 を 16 バイト"),
        Png("TD-ANA-STRUCT-TAIL-B", [.. StructPngA(), .. Enumerable.Repeat((byte)0xFF, 16)], "TD-ANA-STRUCT-PNG-A の後ろに FF を 16 バイト"),
        Png("TD-ANA-STRUCT-EXT-A", Diff3A(), "TD-ANA-DIFF3-A と同じ内容で拡張子が .png"),
        Png("TD-ANA-STRUCT-EXT-B", Diff3B(), "TD-ANA-DIFF3-B と同じ内容で拡張子が .png"),
        Text("TD-ANA-STRUCT-ARR-HXT", "items.hxt", StructItemsHxt),
        Text("TD-ANA-STRUCT-ARR-KEY-HXT", "items-key.hxt", StructItemsKeyHxt),
        Text("TD-ANA-STRUCT-U64-HXT", "u64-eof.hxt", StructU64Hxt),
        Text("TD-ANA-STRUCT-MANY-HXT", "many.hxt", StructManyHxt),
        Text("TD-ANA-STRUCT-MIXED-HXT", "mixed.hxt", StructMixedHxt),
        new("TD-ANA-STRUCT-ARR-A", 8004, "items.hxt の形 (count 1,000、id = i + 1、value = id × 10)", path => WriteAll(path, StructArrA())),
        new("TD-ANA-STRUCT-ARR-B", 8004, "ARR-A から id 500 を除き、添字 100 に id 2,000 を入れ、id 10 の value を 7 にしたもの",
            path => WriteAll(path, StructArrB())),
        new("TD-ANA-STRUCT-ARR-SWAP", 8004, "ARR-A の id 1 の要素を末尾に移したもの", path => WriteAll(path, StructArrSwap())),
        new("TD-ANA-STRUCT-ARR100K-A", 800_004, "items.hxt の形 (count 100,000)", path => WriteAll(path, StructArr100KA())),
        new("TD-ANA-STRUCT-ARR100K-B", 800_004, "ARR100K-A のすべての value に 1 を足したもの", path => WriteAll(path, StructArr100KB())),
        new("TD-ANA-STRUCT-MANY-A", 2 * MiB, "すべて 00", path => WriteAll(path, new byte[2 * MiB])),
        new("TD-ANA-STRUCT-MANY-B", 2 * MiB, "すべて FF", path => WriteAll(path, [.. Enumerable.Repeat((byte)0xFF, (int)(2 * MiB))])),
        new("TD-ANA-STRUCT-MIXED-A", 84, "mixed.hxt の形 (version 1、tag AAAA〜HHHH)", path => WriteAll(path, StructMixedA())),
        new("TD-ANA-STRUCT-MIXED-B", 80, "mixed.hxt の形 (7 種類の差分を含む)", path => WriteAll(path, StructMixedB())),
    ];

    private static TestDataItem Png(string id, byte[] data, string description) =>
        new(id, data.Length, description, path => WriteAll(path, data), dir => Path.Combine(dir, "TD-ANA-STRUCT", id + ".png"));

    private static TestDataItem Text(string id, string fileName, string text) =>
        new(id, Encoding.UTF8.GetByteCount(text), "テンプレート " + fileName, path => File.WriteAllText(path, text, new UTF8Encoding(false)),
            dir => Path.Combine(dir, "TD-ANA-STRUCT", fileName));

    /// <summary>TD-ANA-STRUCT-PE-A。</summary>
    public static byte[] StructPeA() => PeX64();

    /// <summary>TD-ANA-STRUCT-PE-B (TimeDateStamp は e_lfanew + 8 の u32 LE)。</summary>
    public static byte[] StructPeB()
    {
        byte[] pe = PeX64();
        int at = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C)) + 8;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at)) == 0x60000000 ? 0x60000001u : 0x60000000u;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at), value);
        return pe;
    }

    /// <summary>TD-ANA-STRUCT-PE-* の TimeDateStamp のオフセット (e_lfanew + 8)。</summary>
    public static int StructPeTimeDateStampOffset => BinaryPrimitives.ReadInt32LittleEndian(PeX64().AsSpan(0x3C)) + 8;

    private static byte[] StructIhdr(uint width)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 16);
        ihdr[8] = 8;
        ihdr[9] = 2;
        return ihdr;
    }

    /// <summary>TD-TPL-PNG-IDAT の IDAT のデータ。</summary>
    private static byte[] StructIdatData()
    {
        byte[] png = TplPngIdat();
        int at = 8 + 25; // シグネチャと IHDR のチャンクの後ろ
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
        return png.AsSpan(at + 8, length).ToArray();
    }

    private static byte[] StructTime() => [0x07, 0xEA, 1, 2, 3, 4, 5]; // 2026-01-02 03:04:05

    private static byte[] Chunks(params (string Type, byte[] Data)[] chunks)
    {
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach ((string type, byte[] data) in chunks)
        {
            WriteChunk(png, type, data);
        }

        return png.ToArray();
    }

    /// <summary>TD-ANA-STRUCT-PNG-A。</summary>
    public static byte[] StructPngA() => Chunks(("IHDR", StructIhdr(16)), ("tEXt", "Author\0Alice"u8.ToArray()), ("tIME", StructTime()),
        ("IDAT", StructIdatData()), ("IEND", []));

    /// <summary>TD-ANA-STRUCT-PNG-B。</summary>
    public static byte[] StructPngB() => Chunks(("IHDR", StructIhdr(32)), ("tEXt", "Author\0Bob"u8.ToArray()), ("tEXt", "Comment\0hello"u8.ToArray()),
        ("IDAT", StructIdatData()), ("IEND", []));

    /// <summary>TD-ANA-STRUCT-PNG-MOVE。</summary>
    public static byte[] StructPngMove() => Chunks(("IHDR", StructIhdr(16)), ("tIME", StructTime()), ("IDAT", StructIdatData()),
        ("tEXt", "Author\0Alice"u8.ToArray()), ("IEND", []));

    /// <summary>TD-ANA-STRUCT-PNG-BROKEN (IDAT の長さを 0x7FFFFFF0 に。CRC は直さない)。</summary>
    public static byte[] StructPngBroken()
    {
        byte[] png = StructPngA();
        int at = StructIdatOffset(png);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(at), 0x7FFFFFF0);
        return png;
    }

    /// <summary>PNG の IDAT のチャンクの先頭 (長さのフィールド) のオフセット。</summary>
    public static int StructIdatOffset(byte[] png)
    {
        int at = 8;
        while (at + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT")
            {
                return at;
            }

            at += 12 + length;
        }

        return -1;
    }

    private static byte[] ItemsData(IEnumerable<(uint Id, uint Value)> items)
    {
        var list = items.ToList();
        byte[] data = new byte[4 + (list.Count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 + (i * 8)), list[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + (i * 8)), list[i].Value);
        }

        return data;
    }

    private static List<(uint Id, uint Value)> ArrItems(int count) => [.. Enumerable.Range(1, count).Select(id => ((uint)id, (uint)id * 10))];

    /// <summary>TD-ANA-STRUCT-ARR-A。</summary>
    public static byte[] StructArrA() => ItemsData(ArrItems(1000));

    /// <summary>TD-ANA-STRUCT-ARR-B。</summary>
    public static byte[] StructArrB()
    {
        List<(uint Id, uint Value)> items = ArrItems(1000);
        items.RemoveAll(x => x.Id == 500);
        items.Insert(100, (2000, 20000));
        int ten = items.FindIndex(x => x.Id == 10);
        items[ten] = (10, 7);
        return ItemsData(items);
    }

    /// <summary>TD-ANA-STRUCT-ARR-SWAP。</summary>
    public static byte[] StructArrSwap()
    {
        List<(uint Id, uint Value)> items = ArrItems(1000);
        (uint, uint) first = items[0];
        items.RemoveAt(0);
        items.Add(first);
        return ItemsData(items);
    }

    /// <summary>TD-ANA-STRUCT-ARR100K-A。</summary>
    public static byte[] StructArr100KA() => ItemsData(ArrItems(100_000));

    /// <summary>TD-ANA-STRUCT-ARR100K-B。</summary>
    public static byte[] StructArr100KB() => ItemsData(ArrItems(100_000).Select(x => (x.Id, x.Value + 1)));

    private static byte[] Mixed(uint version, IEnumerable<(string Tag, uint Value)> items, uint tailLength, byte[] tail)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        w.Write(version);
        if (version == 1)
        {
            w.Write(0u);
        }
        else
        {
            w.Write((ushort)0);
            w.Write((ushort)0);
        }

        var list = items.ToList();
        w.Write((uint)list.Count);
        foreach ((string tag, uint value) in list)
        {
            w.Write(Encoding.ASCII.GetBytes(tag));
            w.Write(value);
        }

        w.Write(tailLength);
        w.Write(tail);
        return s.ToArray();
    }

    /// <summary>TD-ANA-STRUCT-MIXED-A。</summary>
    public static byte[] StructMixedA() =>
        Mixed(1, Enumerable.Range(0, 8).Select(i => (new string((char)('A' + i), 4), (uint)i + 1)), 4, new byte[4]);

    /// <summary>TD-ANA-STRUCT-MIXED-B (BBBB の削除、ZZZZ の追加、CCCC の移動、DDDD の移動・変更、EEEE の変更、version の型の変更、tail の解析エラー)。</summary>
    public static byte[] StructMixedB() =>
        Mixed(2, [("AAAA", 1), ("EEEE", 50), ("FFFF", 6), ("GGGG", 7), ("DDDD", 40), ("ZZZZ", 26), ("HHHH", 8), ("CCCC", 3)], 0x7FFFFFF0, []);

    /// <summary>
    /// TD-ANA-STRUCT-U64-A (<paramref name="b"/> が false) / B のオフセット <paramref name="offset"/> のバイト。A は n mod 251、B は
    /// 要素 [1,000,000 … 1,000,099] と [50,000,000 … 50,000,009] のバイトを FF との XOR にしたもの。
    /// </summary>
    public static byte StructU64Value(long offset, bool b)
    {
        byte v = (byte)(offset % 251);
        if (b)
        {
            long element = offset / 8;
            if (element is >= 1_000_000 and <= 1_000_099 or >= 50_000_000 and <= 50_000_009)
            {
                v ^= 0xFF;
            }
        }

        return v;
    }
}
