using System.Security.Cryptography;
using System.Text;

namespace HexEditor.TestData;

/// <summary>
/// ツールのテストデータ (cases/11-tools.md の TOOL-18〜TOOL-28 の表): チェックサムファイルの作成・検証、フォルダの比較、カラーピッカー、
/// 日時の変換。フォルダのデータは、先頭のファイルのパスを返す (フォルダは <see cref="FolderOf"/>)。テストは書き換える前にコピーして使うこと。
/// </summary>
public static partial class TestDataCatalog
{
    /// <summary>フォルダのテストデータのフォルダ (<see cref="Get"/> が返す先頭のファイルのあるフォルダ)。</summary>
    public static string FolderOf(string id)
    {
        string primary = Get(id);
        string folder = Path.GetDirectoryName(primary)!;

        // 生成は先頭のファイルの長さしか見ないので、足りないファイルがあれば作り直す。
        return folder;
    }

    /// <summary>TD-TOOL-TREE の相対パス(フォルダの中。区切りは /)。</summary>
    public static readonly IReadOnlyList<string> ToolTreeFiles = ["a.bin", "b.bin", "empty.bin", "sub/c.bin", "日本語/テスト ファイル①.bin"];

    private static IEnumerable<TestDataItem> ToolsCalcItems()
    {
        yield return Folder("TD-TOOL-TREE", "a.bin", 256, "5 ファイルのフォルダ (a.bin、b.bin、empty.bin、sub/c.bin、日本語/テスト ファイル①.bin)", WriteToolTree, "a.bin", "b.bin", "empty.bin", "sub/c.bin", "日本語/テスト ファイル①.bin");
        yield return Folder("TD-TOOL-SUMS-STAR", "a.bin", 256, "sha256sum の出力 (b.bin は *b.bin、c.bin はハッシュ値が大文字) と 3 ファイル", WriteSumsStar, "a.bin", "b.bin", "c.bin", "SHA256SUMS");
        yield return Folder("TD-TOOL-SUMS-MIXED", "ok.bin", 256, "一致・不一致・見つからない・(テストが開いたままにする) の 4 状態の SHA256SUMS と 3 ファイル", WriteSumsMixed, "ok.bin", "bad.bin", "locked.bin", "SHA256SUMS");
        yield return Folder("TD-TOOL-MD5", "x.bin", 256, "x.bin (TD-BYTES-256) とその MD5 の x.bin.md5", WriteMd5, "x.bin", "x.bin.md5");
        yield return Folder("TD-TOOL-FOLDER-L", "SAME.BIN", 256, "SAME.BIN、sub/diff.bin、left-only.bin", WriteFolderLeft, "SAME.BIN", "sub/diff.bin", "left-only.bin");
        yield return Folder("TD-TOOL-FOLDER-R", "same.bin", 256, "same.bin、sub/diff.bin (0x1234 を XOR FF)、right-only.bin", WriteFolderRight, "same.bin", "sub/diff.bin", "right-only.bin");
        yield return new TestDataItem("TD-TOOL-COLOR", 4, "00 80 FF FF", path => WriteAll(path, [0x00, 0x80, 0xFF, 0xFF]));
        yield return new TestDataItem("TD-TOOL-TIMESTAMPS", 256, "2024-02-29T12:34:56.789Z を各形式で表した値 (形式ごとに 16 バイトの枠、リトルエンディアン)",
            path => WriteAll(path, ToolTimestamps()));
    }

    private static TestDataItem Folder(string id, string primary, long primaryLength, string description, Action<string> write, params string[] files) =>
        new(id, primaryLength, description, path => write(Path.GetDirectoryName(path)!), dir => Path.Combine(dir, id, primary))
        {
            IsComplete = dir => files.All(f => File.Exists(Path.Combine(dir, id, f.Replace('/', Path.DirectorySeparatorChar)))),
        };

    // ---- TD-TOOL-TREE ----

    private static byte[] Bytes256() => [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static byte[] SequenceBytes(int length)
    {
        byte[] data = new byte[length];
        Sequence(0, data);
        return data;
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] data = new byte[length];
        Random(RandomSeed, 0, data);
        return data;
    }

    private static void Put(string folder, string relative, byte[] content)
    {
        string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void WriteToolTree(string folder)
    {
        Put(folder, "a.bin", Bytes256());
        Put(folder, "b.bin", SequenceBytes(64 * 1024));
        Put(folder, "empty.bin", []);
        Put(folder, "sub/c.bin", new byte[4096]);
        Put(folder, "日本語/テスト ファイル①.bin", RandomBytes(1024));
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void WriteSumsStar(string folder)
    {
        byte[] a = Bytes256(), b = SequenceBytes(64 * 1024), c = new byte[4096];
        Put(folder, "a.bin", a);
        Put(folder, "b.bin", b);
        Put(folder, "c.bin", c);

        // GNU coreutils の sha256sum の出力: 通常は "ハッシュ値  名前"、-b では "ハッシュ値 *名前"。c.bin の行はハッシュ値を大文字にする。
        string sums = $"{Sha256Hex(a)}  a.bin\n{Sha256Hex(b)} *b.bin\n{Sha256Hex(c).ToUpperInvariant()}  c.bin\n";
        File.WriteAllBytes(Path.Combine(folder, "SHA256SUMS"), new UTF8Encoding(false).GetBytes(sums));
    }

    private static void WriteSumsMixed(string folder)
    {
        byte[] ok = Bytes256(), bad = SequenceBytes(1024), locked = RandomBytes(2048);
        Put(folder, "ok.bin", ok);

        // bad.bin はハッシュを計算した後に 1 バイト変える。
        string badHash = Sha256Hex(bad);
        byte[] changed = [.. bad];
        changed[10] ^= 0xFF;
        Put(folder, "bad.bin", changed);
        Put(folder, "locked.bin", locked);
        string sums = $"{Sha256Hex(ok)}  ok.bin\n{badHash}  bad.bin\n{Sha256Hex(locked)}  locked.bin\n{Sha256Hex([1, 2, 3])}  missing.bin\n";
        File.WriteAllBytes(Path.Combine(folder, "SHA256SUMS"), new UTF8Encoding(false).GetBytes(sums));
    }

    private static void WriteMd5(string folder)
    {
        byte[] x = Bytes256();
        Put(folder, "x.bin", x);
        File.WriteAllBytes(Path.Combine(folder, "x.bin.md5"), new UTF8Encoding(false).GetBytes($"{Convert.ToHexStringLower(MD5.HashData(x))}  x.bin\n"));
    }

    // ---- フォルダの比較 ----

    private static void WriteFolderLeft(string folder)
    {
        Put(folder, "SAME.BIN", Bytes256());
        Put(folder, "sub/diff.bin", SequenceBytes(64 * 1024));
        Put(folder, "left-only.bin", new byte[16]);
    }

    private static void WriteFolderRight(string folder)
    {
        Put(folder, "same.bin", Bytes256());
        byte[] diff = SequenceBytes(64 * 1024);
        diff[0x1234] ^= 0xFF;
        Put(folder, "sub/diff.bin", diff);
        Put(folder, "right-only.bin", [.. Enumerable.Repeat((byte)0xFF, 16)]);
    }

    // ---- TD-TOOL-TIMESTAMPS ----

    /// <summary>TD-TOOL-TIMESTAMPS の枠の並び (TOOL-28 の仕様 1 の形式。枠の番号 × 16 がオフセット)。</summary>
    public static readonly IReadOnlyList<string> ToolTimestampSlots =
        ["unix32", "unix32u", "unix64", "unixms", "unixus", "unixns", "filetime", "dos", "ole", "hfsplus", "apfs", "java", "dotnet", "gps", "cocoa"];

    private static byte[] ToolTimestamps()
    {
        var instant = new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Utc).AddMilliseconds(789);
        long unixSeconds = new DateTimeOffset(instant).ToUnixTimeSeconds();
        long unixMs = new DateTimeOffset(instant).ToUnixTimeMilliseconds();
        long ticksSinceEpoch = instant.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long fileTime = instant.ToFileTimeUtc();
        int dosDate = ((instant.Year - 1980) << 9) | (instant.Month << 5) | instant.Day;
        int dosTime = (instant.Hour << 11) | (instant.Minute << 5) | (instant.Second / 2);
        double ole = instant.ToOADate();
        long hfs = unixSeconds + 2082844800L;
        long gps = unixSeconds - 315964800L + 18;
        double cocoa = (instant.Ticks - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / (double)TimeSpan.TicksPerSecond;

        byte[] data = new byte[256];
        void Put64(int slot, ulong value, int size)
        {
            for (int i = 0; i < size; i++)
            {
                data[slot * 16 + i] = (byte)(value >> (8 * i));
            }
        }

        Put64(0, (ulong)unixSeconds, 4);
        Put64(1, (ulong)unixSeconds, 4);
        Put64(2, (ulong)unixSeconds, 8);
        Put64(3, (ulong)unixMs, 8);
        Put64(4, (ulong)(ticksSinceEpoch / 10), 8);
        Put64(5, (ulong)(ticksSinceEpoch * 100), 8);
        Put64(6, (ulong)fileTime, 8);
        Put64(7, (uint)dosTime | ((ulong)(uint)dosDate << 16), 4);
        Put64(8, (ulong)BitConverter.DoubleToInt64Bits(ole), 8);
        Put64(9, (ulong)hfs, 4);
        Put64(10, (ulong)(ticksSinceEpoch * 100), 8);
        Put64(11, (ulong)unixMs, 8);
        Put64(12, (ulong)instant.Ticks, 8);
        Put64(13, (ulong)gps, 4);
        Put64(14, (ulong)BitConverter.DoubleToInt64Bits(cocoa), 8);
        return data;
    }
}
