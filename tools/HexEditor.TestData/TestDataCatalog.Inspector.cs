using System.Text;

namespace HexEditor.TestData;

/// <summary>フェーズ 3 のインスペクタ・画像表示のテストデータ (cases/05-inspector-and-annotations.md と cases/02-view-and-navigation.md の末尾の表)。</summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-INSP-TPL-TYPE の内容 (構造体 Header6 に「インスペクタに表示する」属性を付けたテンプレート)。</summary>
    public const string InspectorTemplateType = """
        // TD-INSP-TPL-TYPE: インスペクタのユーザー定義型 (INSP-22)
        struct Header6 {
            le u16 id;
            le u32 size;
        } [[inspector("Header6")]];

        """;

    private static IEnumerable<TestDataItem> InspectorPhase3Items() =>
    [
        new("TD-INSP-PTR", 8 * KiB, "ポインタとして移動の確認用 (0x10: 00 01 00 00、0x20: 00 10 40 00、0x30: F0、0x40: 00 00 00 10)",
            path => WriteAll(path, InspectorPointers())),
        new("TD-INSP-TPL-TYPE", Encoding.UTF8.GetByteCount(InspectorTemplateType), "インスペクタのユーザー定義型 Header6 のテンプレート (header6.hxt)",
            path => File.WriteAllText(path, InspectorTemplateType, new UTF8Encoding(false)), dir => Path.Combine(dir, "TD-INSP-TPL-TYPE", "header6.hxt")),
        new("TD-VIEW-BMP24", 54 + (64 * 3 * 32), "非圧縮の 24 bit BMP (幅 64、高さ 32、下から上)", path => WriteAll(path, Bmp24())),
    ];

    private static byte[] InspectorPointers()
    {
        byte[] data = new byte[8 * KiB];
        data[0x11] = 0x01;
        data[0x21] = 0x10;
        data[0x22] = 0x40;
        data[0x30] = 0xF0;
        data[0x43] = 0x10;
        return data;
    }

    /// <summary>画素 (x, y) (上から y 行目) の色。R = x × 4、G = y × 8、B = 255 − x × 4。</summary>
    public static (byte R, byte G, byte B) Bmp24Pixel(int x, int y) => ((byte)(x * 4), (byte)(y * 8), (byte)(255 - x * 4));

    /// <summary>TD-VIEW-BMP24: BITMAPFILEHEADER + BITMAPINFOHEADER の後に、下の行から順に 1 行 192 バイトの BGR。</summary>
    private static byte[] Bmp24()
    {
        const int Width = 64, Height = 32, Row = Width * 3;
        byte[] data = new byte[54 + Row * Height];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BitConverter.TryWriteBytes(data.AsSpan(2), data.Length);
        BitConverter.TryWriteBytes(data.AsSpan(10), 54);
        BitConverter.TryWriteBytes(data.AsSpan(14), 40);
        BitConverter.TryWriteBytes(data.AsSpan(18), Width);
        BitConverter.TryWriteBytes(data.AsSpan(22), Height);
        BitConverter.TryWriteBytes(data.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(data.AsSpan(28), (short)24);
        BitConverter.TryWriteBytes(data.AsSpan(34), Row * Height);
        BitConverter.TryWriteBytes(data.AsSpan(38), 2835);
        BitConverter.TryWriteBytes(data.AsSpan(42), 2835);
        for (int y = 0; y < Height; y++)
        {
            int dataRow = Height - 1 - y;
            for (int x = 0; x < Width; x++)
            {
                (byte r, byte g, byte b) = Bmp24Pixel(x, y);
                int o = 54 + dataRow * Row + x * 3;
                data[o] = b;
                data[o + 1] = g;
                data[o + 2] = r;
            }
        }

        return data;
    }
}
