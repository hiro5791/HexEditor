using System.Text;

namespace HexEditor.TestData;

/// <summary>
/// フォレンジックのケース・カービング・レポートのテストデータ (cases/12-forensics.md の末尾の表の TD-FOR-JPEG / TD-FOR-PDF / TD-FOR-CARVE / TD-FOR-SIG-MIXED)。
/// TD-FOR-CARVE-FS (NTFS のボリューム) はファイルシステムの生成部品 (FOR-10) が作る。
/// </summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-FOR-CARVE に埋め込むファイルのオフセット (JPEG、PNG、PDF、ZIP の順)。</summary>
    public static readonly long[] ForCarveOffsets = [0x100000, 0x200000, 0x300000, 0x400000];

    private static IEnumerable<TestDataItem> ForCaseItems() =>
    [
        new("TD-FOR-JPEG", ForJpeg().Length, "32×32 のグレースケールの横方向のグラデーションを、固定の符号化 (品質 75、ベースライン) で JPEG にしたもの", path => WriteAll(path, ForJpeg())),
        new("TD-FOR-PDF", ForPdf().Length, "PDF 1.4 (1 ページ、テキスト HexEditor、xref と %%EOF を持つ)", path => WriteAll(path, ForPdf())),
        new("TD-FOR-CARVE", 8 * MiB, "すべて 00 のデータの、0x100000 に TD-FOR-JPEG、0x200000 に TD-PNG、0x300000 に TD-FOR-PDF、0x400000 に TD-ZIP を置いたもの",
            path => WriteAll(path, ForCarve())),
        new("TD-FOR-SIG-MIXED", ForSigMixed().Length, "カービングの定義の JSON (good1、good2、bad-empty、bad-hex、bad-size)", path => WriteAll(path, ForSigMixed())),
    ];

    /// <summary>TD-FOR-CARVE: 8 MiB の 00 に 4 つのファイルを置く。</summary>
    public static byte[] ForCarve()
    {
        byte[] data = new byte[8 * MiB];
        byte[][] files = [ForJpeg(), Png16(), ForPdf(), ZipSample()];
        for (int i = 0; i < files.Length; i++)
        {
            files[i].CopyTo(data, ForCarveOffsets[i]);
        }

        return data;
    }

    /// <summary>TD-FOR-SIG-MIXED: 良い定義 2 つと悪い定義 3 つ。</summary>
    public static byte[] ForSigMixed() => Encoding.UTF8.GetBytes("""
        {
          "version": 1,
          "signatures": [
            { "name": "good1", "extension": "class", "headers": ["CA FE BA BE"], "maxSize": 1048576 },
            { "name": "good2", "extension": "pdf", "headers": ["25 50 44 46"], "footer": "25 25 45 4F 46" },
            { "name": "bad-empty", "extension": "bin", "headers": [] },
            { "name": "bad-hex", "extension": "bin", "headers": ["ZZ 01"] },
            { "name": "bad-size", "extension": "bin", "headers": ["AA BB"], "maxSize": 1024, "minSize": 2048 }
          ]
        }
        """);

    /// <summary>TD-FOR-PDF: 手で組んだ PDF 1.4。xref のオフセットを計算して埋める。</summary>
    public static byte[] ForPdf()
    {
        string content = "BT /F1 18 Tf 72 700 Td (HexEditor) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append($"{o:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---- TD-FOR-JPEG: 小さなベースライン JPEG の符号化器 (グレースケール 1 成分、固定の符号表) ----

    private static int[] JpegZigzag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static int[] JpegLuminanceQuant =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>
    /// 32×32 の横方向のグラデーションを JPEG にする。量子化表は標準の輝度表の品質 75 (50% 倍)。ハフマン表は DC を 12 記号の 4 ビット、
    /// AC を 162 記号の 8 ビットとする固定の表 (どの記号も同じ長さなので、符号は記号の順番そのもの)。
    /// </summary>
    public static byte[] ForJpeg()
    {
        const int Size = 32;
        int[] zigzag = JpegZigzag;
        int[] quant = [.. JpegLuminanceQuant.Select(q => Math.Clamp((q * 50 + 50) / 100, 1, 255))];
        var jpg = new List<byte> { 0xFF, 0xD8 };
        jpg.AddRange([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        jpg.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        jpg.AddRange(zigzag.Select(z => (byte)quant[z]));
        jpg.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, Size, 0x00, Size, 0x01, 0x01, 0x11, 0x00]);

        // DC: 長さ 4 の符号が 12 個 (カテゴリー 0〜11)。
        jpg.AddRange([0xFF, 0xC4, 0x00, 0x1F, 0x00]);
        for (int len = 1; len <= 16; len++)
        {
            jpg.Add((byte)(len == 4 ? 12 : 0));
        }

        for (int i = 0; i < 12; i++)
        {
            jpg.Add((byte)i);
        }

        // AC: 長さ 8 の符号が 162 個 (EOB、ZRL、run 0〜15 × size 1〜10)。符号 0xFF は予約なので 162 個で足りる。
        byte[] acSymbols = [0x00, 0xF0, .. Enumerable.Range(0, 16).SelectMany(run => Enumerable.Range(1, 10).Select(size => (byte)((run << 4) | size)))];
        jpg.AddRange([0xFF, 0xC4, 0x00, (byte)(19 + acSymbols.Length), 0x10]);
        for (int len = 1; len <= 16; len++)
        {
            jpg.Add((byte)(len == 8 ? acSymbols.Length : 0));
        }

        jpg.AddRange(acSymbols);
        jpg.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);

        var bits = new List<byte>();
        int acc = 0, nbits = 0;
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                acc = (acc << 1) | ((value >> i) & 1);
                if (++nbits == 8)
                {
                    bits.Add((byte)acc);
                    if (acc == 0xFF)
                    {
                        bits.Add(0);
                    }

                    acc = 0;
                    nbits = 0;
                }
            }
        }

        int Category(int v)
        {
            v = Math.Abs(v);
            int c = 0;
            while (v != 0)
            {
                c++;
                v >>= 1;
            }

            return c;
        }

        void PutValue(int v, int category)
        {
            if (category > 0)
            {
                Put(v < 0 ? v + (1 << category) - 1 : v, category);
            }
        }

        int previousDc = 0;
        for (int by = 0; by < Size / 8; by++)
        {
            for (int bx = 0; bx < Size / 8; bx++)
            {
                double[] block = new double[64];
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        block[y * 8 + x] = (bx * 8 + x) * 8 - 128;
                    }
                }

                int[] q = new int[64];
                for (int v = 0; v < 8; v++)
                {
                    for (int u = 0; u < 8; u++)
                    {
                        double sum = 0;
                        for (int y = 0; y < 8; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                sum += block[y * 8 + x] * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                            }
                        }

                        double cu = u == 0 ? Math.Sqrt(0.5) : 1, cv = v == 0 ? Math.Sqrt(0.5) : 1;
                        q[v * 8 + u] = (int)Math.Round(sum * cu * cv / 4 / quant[v * 8 + u]);
                    }
                }

                int dc = q[0] - previousDc;
                previousDc = q[0];
                int category = Category(dc);
                Put(category, 4);
                PutValue(dc, category);
                int run = 0;
                int last = 63;
                while (last > 0 && q[zigzag[last]] == 0)
                {
                    last--;
                }

                for (int k = 1; k <= last; k++)
                {
                    int v = q[zigzag[k]];
                    if (v == 0)
                    {
                        run++;
                        continue;
                    }

                    while (run > 15)
                    {
                        Put(1, 8);
                        run -= 16;
                    }

                    int size = Category(v);
                    Put(2 + run * 10 + (size - 1), 8);
                    PutValue(v, size);
                    run = 0;
                }

                if (last < 63)
                {
                    Put(0, 8);
                }
            }
        }

        if (nbits > 0)
        {
            Put((1 << (8 - nbits)) - 1, 8 - nbits);
        }

        jpg.AddRange(bits);
        jpg.AddRange([0xFF, 0xD9]);
        return [.. jpg];
    }
}
