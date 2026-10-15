using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData;

/// <summary>コマンドライン (AUTO-36〜AUTO-44) のテストデータ (cases/08-automation.md の「データファイル」の表)。</summary>
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> CliItems() =>
    [
        new("TD-AUTO-SEQ-1M-DIFF1", MiB, "TD-SEQ-1M のオフセット 0x80000 の値を 00 から FF に変えたもの", path => WriteGenerated(path, MiB, (o, s) =>
        {
            Sequence(o, s);
            if (0x80000 >= o && 0x80000 < o + s.Length)
            {
                s[(int)(0x80000 - o)] = 0xFF;
            }
        })),
        new("TD-AUTO-FIND", MiB, "すべて 00。0x100 に DE AD 00 EF、0x2000 に DE AD 7F EF、0x3000 に DE AD BE EE、0xFFFFC に DE AD FF EF",
            path => WriteAll(path, AutoFind())),
        new("TD-AUTO-CMP-A", MiB, "TD-RANDOM-16M の先頭 1 MiB", path => WriteAll(path, AutoCompareA())),
        new("TD-AUTO-CMP-B", MiB - 4, "TD-AUTO-CMP-A の 0x100 を XOR FF、0x8000〜0x800F を XOR FF、0x20000 の前に 11 22 33 44 を挿入、0x40000 から 8 バイトを削除",
            path => WriteAll(path, AutoCompareB())),
        new("TD-AUTO-SQUEEZE", 1104, "00〜3F の 64 バイト、00 が 1,024 バイト、FF が 16 バイト", path => WriteAll(path, AutoSqueeze())),
        new("TD-AUTO-IPS", AutoIps().Length, "TD-SEQ-1M に対する IPS パッチ (RLE と切り詰めの長さを含む)", path => WriteAll(path, AutoIps())),
        new("TD-AUTO-SPARSE-100G-DIFF0", 100 * GiB, "TD-SPARSE-100G のオフセット 0 の @ (0x40) を # (0x23) に変えたもの (スパース)", path =>
        {
            WriteMarkers(path, 100 * GiB, MarkersEvery(100 * GiB, GiB).Concat(Around(1L << 31)).Concat(Around(1L << 32)));
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.None);
            RandomAccess.Write(handle, [0x23], 0);
        }),
        new("TD-AUTO-SUMS", AutoSums().Length, "f1.bin (TD-BYTES-256)、f2.bin (TD-SEQ-1M の先頭 64 KiB)、f3.bin (TD-ZERO-1M の先頭 4 KiB) と SHA256SUMS",
            WriteAutoSums, dir => Path.Combine(dir, "TD-AUTO-SUMS", "SHA256SUMS")),
    ];

    /// <summary>TD-AUTO-FIND の内容。</summary>
    public static byte[] AutoFind()
    {
        byte[] data = new byte[MiB];
        Plant(data, 0x100, 0xDE, 0xAD, 0x00, 0xEF);
        Plant(data, 0x2000, 0xDE, 0xAD, 0x7F, 0xEF);
        Plant(data, 0x3000, 0xDE, 0xAD, 0xBE, 0xEE);
        Plant(data, 0xFFFFC, 0xDE, 0xAD, 0xFF, 0xEF);
        return data;
    }

    private static void Plant(byte[] data, int offset, params byte[] bytes) => bytes.CopyTo(data, offset);

    /// <summary>TD-AUTO-CMP-A の内容 (TD-RANDOM-16M の先頭 1 MiB)。</summary>
    public static byte[] AutoCompareA()
    {
        byte[] data = new byte[MiB];
        Random(RandomSeed, 0, data);
        return data;
    }

    /// <summary>TD-AUTO-CMP-B の内容。</summary>
    public static byte[] AutoCompareB()
    {
        List<byte> data = [.. AutoCompareA()];
        data[0x100] ^= 0xFF;
        for (int i = 0x8000; i < 0x8010; i++)
        {
            data[i] ^= 0xFF;
        }

        data.InsertRange(0x20000, [0x11, 0x22, 0x33, 0x44]);
        data.RemoveRange(0x40000, 8);
        return [.. data];
    }

    /// <summary>TD-AUTO-SQUEEZE の内容。</summary>
    public static byte[] AutoSqueeze() =>
        [.. Enumerable.Range(0, 64).Select(i => (byte)i), .. new byte[1024], .. Enumerable.Repeat((byte)0xFF, 16)];

    /// <summary>TD-AUTO-IPS の内容: 0x10 に 48 45 58、RLE で 0x1000 から AA を 0x100 回、0x80000 に 00 FF、切り詰めの長さ 0x0FFF00。</summary>
    public static byte[] AutoIps() =>
    [
        .. "PATCH"u8.ToArray(),
        0x00, 0x00, 0x10, 0x00, 0x03, 0x48, 0x45, 0x58,
        0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0xAA,
        0x08, 0x00, 0x00, 0x00, 0x02, 0x00, 0xFF,
        .. "EOF"u8.ToArray(),
        0x0F, 0xFF, 0x00,
    ];

    /// <summary>TD-AUTO-SUMS の 3 つのファイルの内容 (名前, 内容)。</summary>
    public static IReadOnlyList<(string Name, byte[] Data)> AutoSumsFiles()
    {
        byte[] f1 = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        byte[] f2 = new byte[64 * KiB];
        Sequence(0, f2);
        return [("f1.bin", f1), ("f2.bin", f2), ("f3.bin", new byte[4 * KiB])];
    }

    /// <summary>TD-AUTO-SUMS の SHA256SUMS (GNU 形式、LF、パスの区切りは /)。</summary>
    public static byte[] AutoSums()
    {
        var sb = new StringBuilder();
        foreach ((string name, byte[] data) in AutoSumsFiles())
        {
            sb.Append(Convert.ToHexStringLower(SHA256.HashData(data))).Append("  ").Append(name).Append('\n');
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void WriteAutoSums(string sumsPath)
    {
        string folder = Path.GetDirectoryName(sumsPath)!;
        foreach ((string name, byte[] data) in AutoSumsFiles())
        {
            File.WriteAllBytes(Path.Combine(folder, name), data);
        }

        File.WriteAllBytes(sumsPath, AutoSums());
    }
}
