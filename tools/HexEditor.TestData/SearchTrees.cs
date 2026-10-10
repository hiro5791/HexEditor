namespace HexEditor.TestData;

/// <summary>
/// 複数ファイル検索のテストデータ (docs/test/cases/04-search.md の表の TD-FIND-TREE-1000 と TD-FIND-TREE-10)。フォルダごと作る。
/// </summary>
public static class SearchTrees
{
    /// <summary>目印の並び `CA FE BA BE`。</summary>
    public static readonly byte[] Marker = [0xCA, 0xFE, 0xBA, 0xBE];

    /// <summary>
    /// TD-FIND-TREE-1000: `d0`〜`d9` の各々に `f00.bin`〜`f99.bin` (4,096 バイト、種 = フォルダ番号 × 100 + ファイル番号の乱数)。通し番号が 50 の
    /// 倍数の 20 ファイルは 0x100 と 0x200 に目印を置き、それ以外の位置の目印は先頭のバイトを 00 にする。`d0`〜`d4` に `x.log`
    /// (4,096 バイトの 00 で、0x10 に目印) を置く。
    /// </summary>
    public static void WriteTree1000(string directory)
    {
        for (int d = 0; d < 10; d++)
        {
            string folder = Path.Combine(directory, $"d{d}");
            Directory.CreateDirectory(folder);
            for (int f = 0; f < 100; f++)
            {
                int serial = (d * 100) + f;
                byte[] data = new byte[4096];
                TestDataCatalog.Random((ulong)serial, 0, data);
                RemoveMarkers(data);
                if (serial % 50 == 0)
                {
                    Marker.CopyTo(data, 0x100);
                    Marker.CopyTo(data, 0x200);
                }

                File.WriteAllBytes(Path.Combine(folder, $"f{f:D2}.bin"), data);
            }

            if (d < 5)
            {
                byte[] log = new byte[4096];
                Marker.CopyTo(log, 0x10);
                File.WriteAllBytes(Path.Combine(folder, "x.log"), log);
            }
        }
    }

    /// <summary>TD-FIND-TREE-10: `r0.bin`〜`r9.bin` (65,536 バイトの 00 で、0x1000 と 0x2000 に目印)。</summary>
    public static void WriteTree10(string directory)
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"r{i}.bin"), Tree10File());
        }
    }

    /// <summary>TD-FIND-TREE-10 の 1 ファイルの内容。</summary>
    public static byte[] Tree10File()
    {
        byte[] data = new byte[65536];
        Marker.CopyTo(data, 0x1000);
        Marker.CopyTo(data, 0x2000);
        return data;
    }

    private static void RemoveMarkers(byte[] data)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data.AsSpan(i, 4).SequenceEqual(Marker))
            {
                data[i] = 0x00;
            }
        }
    }
}
