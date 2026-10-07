using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData;

/// <summary>テストデータ 1 件の定義 (docs/test/test-data.md)。</summary>
public sealed record TestDataItem(string Id, long Length, string Description, Action<string> Generate);

/// <summary>
/// テストデータを生成する (テスト方針 7.1)。同じ ID からは常に同じ内容を作る。生成したファイルはキャッシュし、
/// 2 回目以降はそのまま使う。
/// </summary>
public static class TestDataCatalog
{
    public const long KiB = 1024;
    public const long MiB = 1024 * KiB;
    public const long GiB = 1024 * MiB;
    public const long TiB = 1024 * GiB;

    /// <summary>目印の長さ: '@' と 16 桁の Hex。</summary>
    public const int MarkerLength = 17;

    private static readonly Dictionary<string, TestDataItem> Items = new List<TestDataItem>
    {
        new("TD-EMPTY", 0, "空のファイル", path => WriteAll(path, [])),
        new("TD-BYTES-256", 256, "00〜FF を 1 回ずつ", path => WriteAll(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())),
        new("TD-SEQ-1M", MiB, "オフセット n の値は n mod 256", path => WriteGenerated(path, MiB, (o, s) => Sequence(o, s))),
        new("TD-ZERO-1M", MiB, "すべて 00", path => WriteGenerated(path, MiB, (_, s) => s.Clear())),
        new("TD-FF-1M", MiB, "すべて FF", path => WriteGenerated(path, MiB, (_, s) => s.Fill(0xFF))),
        new("TD-RANDOM-16M", 16 * MiB, "固定の種の乱数", path => WriteGenerated(path, 16 * MiB, (o, s) => Random(RandomSeed, o, s))),
        new("TD-MARKERS-1G", GiB, "先頭・末尾・2^20 ごとの目印 (スパース)", path => WriteMarkers(path, GiB, MarkersEvery(GiB, MiB))),
        new("TD-SPARSE-100G", 100 * GiB, "先頭・末尾・2^31・2^32 の前後・1 GiB ごとの目印 (スパース)",
            path => WriteMarkers(path, 100 * GiB, MarkersEvery(100 * GiB, GiB).Concat(Around(1L << 31)).Concat(Around(1L << 32)))),
        new("TD-SPARSE-2T", 2 * TiB, "先頭・末尾・2^31・2^32・2^40 の前後の目印 (スパース)",
            path => WriteMarkers(path, 2 * TiB, new[] { 0L, 2 * TiB - MarkerLength }.Concat(Around(1L << 31)).Concat(Around(1L << 32)).Concat(Around(1L << 40)))),
    }.ToDictionary(i => i.Id);

    /// <summary>TD-RANDOM-16M の乱数の種。</summary>
    public const ulong RandomSeed = 0x5EED_0000_0016_0001UL;

    public static IReadOnlyCollection<TestDataItem> All => Items.Values;

    /// <summary>テストデータの置き場所。環境変数 HEXEDITOR_TESTDATA で変えられる。</summary>
    public static string CacheDirectory =>
        Environment.GetEnvironmentVariable("HEXEDITOR_TESTDATA") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "HexEditorTestData");

    /// <summary>テストデータのパスを返す。なければ生成する。</summary>
    public static string Get(string id) => Generate(id, CacheDirectory);

    /// <summary>テストデータを <paramref name="directory"/> に生成し、パスを返す。同じ長さのファイルがあれば作り直さない。</summary>
    public static string Generate(string id, string directory)
    {
        if (!Items.TryGetValue(id, out TestDataItem? item))
        {
            throw new ArgumentException($"未定義のテストデータです: {id}", nameof(id));
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".bin");
        lock (Items)
        {
            if (File.Exists(path) && new FileInfo(path).Length == item.Length)
            {
                return path;
            }

            string temp = path + ".tmp";
            item.Generate(temp);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    /// <summary>テストデータの内容を計算で求める (ファイルを読まずに期待値を作るため)。</summary>
    public static void Expected(string id, long offset, Span<byte> destination)
    {
        switch (id)
        {
            case "TD-SEQ-1M":
                Sequence(offset, destination);
                break;
            case "TD-RANDOM-16M":
                Random(RandomSeed, offset, destination);
                break;
            default:
                throw new NotSupportedException(id);
        }
    }

    /// <summary>オフセット <paramref name="position"/> に置く目印のバイト列 (`@` と 16 桁の大文字の Hex)。</summary>
    public static byte[] Marker(long position) => Encoding.ASCII.GetBytes("@" + position.ToString("X16"));

    /// <summary>オフセット n の値を n mod 256 にする。</summary>
    public static void Sequence(long offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(offset + i);
        }
    }

    /// <summary>SplitMix64 のカウンタ方式の乱数 (エンジンの生成ピースと同じ方式)。</summary>
    public static void Random(ulong seed, long offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            long p = offset + i;
            ulong z = unchecked(seed + (ulong)(p >> 3) * 0x9E3779B97F4A7C15UL + 0x9E3779B97F4A7C15UL);
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            z ^= z >> 31;
            destination[i] = (byte)(z >> (int)((p & 7) * 8));
        }
    }

    private static IEnumerable<long> MarkersEvery(long length, long step)
    {
        for (long p = 0; p < length; p += step)
        {
            yield return p;
        }

        yield return length - MarkerLength;
    }

    /// <summary><paramref name="p"/> の直前と <paramref name="p"/> に置く目印の位置。</summary>
    private static IEnumerable<long> Around(long p) => [p - MarkerLength, p];

    private static void WriteAll(string path, byte[] data) => File.WriteAllBytes(path, data);

    private static void WriteGenerated(string path, long length, Action<long, Span<byte>> fill)
    {
        using FileStream stream = File.Create(path);
        byte[] buffer = new byte[MiB];
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - offset);
            fill(offset, buffer.AsSpan(0, n));
            stream.Write(buffer, 0, n);
        }
    }

    /// <summary>スパースファイルを作り、指定の位置に目印を書く。それ以外は 00 (実際のディスク使用量はほぼ 0)。</summary>
    private static void WriteMarkers(string path, long length, IEnumerable<long> positions)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, length);
        foreach (long p in positions.Distinct().Order())
        {
            RandomAccess.Write(handle, Marker(p), p);
        }
    }

    private static void MakeSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows 以外のファイルシステムは書き込みのない範囲を自動で疎にする。
        }

        const uint FsctlSetSparse = 0x000900C4;
        if (!DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("スパースファイルにできません。NTFS のドライブが必要です。", Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
