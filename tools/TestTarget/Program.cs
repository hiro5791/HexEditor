using System.Globalization;
using System.Runtime.InteropServices;

// TestTarget: プロセスメモリのテスト (ENG-32、ENG-33) に使う小さなプロセス。
//
// 使い方: TestTarget [--value <16 進>] [--regions <N>] [--text <文字列>]
//   --value    既知の値 (既定 4845584544495421 = "HEXEDIT!")。専用のページに置き、そのアドレスを "VALUE 0x..." で出力する。
//   --regions  4 KiB の領域を N 個確保する (保護属性を読み書き・読み取り専用で交互に変え、隣と結合しないよう 1 ページずつ空ける)。
//              "REGIONS <N> 0x<先頭>" を出力する (TC-ENG-33-03)。
//   --text     UTF-16 LE の文字列を専用のページに置き、"TEXT 0x..." を出力する (TC-PKG-04-04 のメモ帳の代わり)。
//   --out      出力をこのファイルにも書く (標準出力を受け取れない起動のしかたのため)。
//   --random   指定したバイト数の領域を確保し、--seed の種の乱数 (テストデータと同じ SplitMix64) で埋める。
//              "RANDOM 0x<先頭> <バイト数> <SHA-256>" を出力する (TC-ENG-35-01〜03)。
//   --seed     --random の種 (10 進。既定 0x35)。
//   --commit   指定した MiB のコミット済みの領域を確保する (中身は書かない)。"COMMIT 0x<先頭> <バイト数>" を出力する (TC-ENG-35-04)。
// 準備ができたら "READY" を出力し、標準入力が閉じられる (または "exit" の行を受け取る) まで待つ。
// 標準入力の "poke <16 進のアドレス> <16 進のバイト列>" で自分のメモリを書き換え、"POKED" を出力する (TC-ENG-35-02 の値の書き換え)。
//   --echo-args  残りの引数を JSON の文字列の配列で標準出力に出してすぐに終わる (TD-AUTO-ECHOARGS。TC-AUTO-18-03 の process.run の確認)。
if (args.Length > 0 && args[0] == "--echo-args")
{
    Console.Out.Write(System.Text.Json.JsonSerializer.Serialize(args[1..]));
    return 0;
}

string valueHex = "4845584544495421";
int regions = 0;
string? text = null;
string? outFile = null;
long randomSize = 0;
ulong seed = 0x35;
long commitMiB = 0;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--value":
            valueHex = args[i + 1];
            break;
        case "--regions":
            regions = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--text":
            text = args[i + 1];
            break;
        case "--out":
            outFile = args[i + 1];
            break;
        case "--random":
            randomSize = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--seed":
            seed = ulong.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--commit":
            commitMiB = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

byte[] value = Convert.FromHexString(valueHex);
nint valuePage = Native.Alloc(4096, Native.PageReadWrite);
Marshal.Copy(value, 0, valuePage, value.Length);
Report($"VALUE 0x{valuePage:X}");

if (text is not null)
{
    byte[] utf16 = System.Text.Encoding.Unicode.GetBytes(text);
    nint textPage = Native.Alloc((nuint)Math.Max(4096, (utf16.Length + 4095) / 4096 * 4096), Native.PageReadWrite);
    Marshal.Copy(utf16, 0, textPage, utf16.Length);
    Report($"TEXT 0x{textPage:X}");
}

if (regions > 0)
{
    // 2 ページずつ予約し、先頭の 1 ページだけを確定する (後ろの予約したページが隣の領域との結合を防ぐ)。保護属性は交互に変える。
    nint first = 0;
    for (int i = 0; i < regions; i++)
    {
        nint reserved = Native.Reserve(8192);
        nint page = Native.Commit(reserved, 4096, i % 2 == 0 ? Native.PageReadWrite : Native.PageReadOnly);
        first = first == 0 ? page : first;
    }

    Report($"REGIONS {regions} 0x{first:X}");
}

if (randomSize > 0)
{
    nint random = Native.Alloc((nuint)randomSize, Native.PageReadWrite);
    byte[] data = new byte[randomSize];
    FillRandom(seed, data);
    Marshal.Copy(data, 0, random, data.Length);
    Report($"RANDOM 0x{random:X} {randomSize} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))}");
}

if (commitMiB > 0)
{
    long bytes = commitMiB * 1024 * 1024;
    nint committed = Native.Alloc((nuint)bytes, Native.PageReadWrite);
    Report($"COMMIT 0x{committed:X} {bytes}");
}

Report("READY");
Console.Out.Flush();
while (Console.ReadLine() is { } line)
{
    string trimmed = line.Trim();
    if (trimmed == "exit")
    {
        break;
    }

    // 自分のメモリを書き換える (テストが値を変えた前後のスナップショットを比べる。TC-ENG-35-02)。
    string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 3 && parts[0] == "poke")
    {
        nint address = (nint)long.Parse(parts[1].StartsWith("0x", StringComparison.Ordinal) ? parts[1][2..] : parts[1], NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        byte[] bytes = Convert.FromHexString(parts[2]);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        Report("POKED");
    }
}

GC.KeepAlive(value);
return 0;

// テストデータ (tools/HexEditor.TestData の TestDataCatalog.Random) と同じ SplitMix64 のカウンタ方式。
static void FillRandom(ulong seed, Span<byte> destination)
{
    long word = -1;
    ulong z = 0;
    for (int i = 0; i < destination.Length; i++)
    {
        long p = i;
        if (p >> 3 != word)
        {
            word = p >> 3;
            z = unchecked(seed + (ulong)word * 0x9E3779B97F4A7C15UL + 0x9E3779B97F4A7C15UL);
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            z ^= z >> 31;
        }

        destination[i] = (byte)(z >> (int)((p & 7) * 8));
    }
}

void Report(string line)
{
    Console.WriteLine(line);
    if (outFile is not null)
    {
        File.AppendAllText(outFile, line + Environment.NewLine);
    }
}

internal static class Native
{
    public const uint PageReadOnly = 0x02;
    public const uint PageReadWrite = 0x04;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;

    public static nint Alloc(nuint size, uint protect) =>
        VirtualAlloc(0, size, MemCommit | MemReserve, protect) is var p and not 0 ? p : throw new OutOfMemoryException("VirtualAlloc");

    public static nint Reserve(nuint size) =>
        VirtualAlloc(0, size, MemReserve, PageReadWrite) is var p and not 0 ? p : throw new OutOfMemoryException("VirtualAlloc (reserve)");

    public static nint Commit(nint address, nuint size, uint protect)
    {
        nint p = VirtualAlloc(address, size, MemCommit, protect);
        if (p == 0)
        {
            throw new OutOfMemoryException("VirtualAlloc (commit)");
        }

        // 確定したページに書いておく (読み取り専用のページは確定の時点で 0 のまま)。
        if (protect == PageReadWrite)
        {
            Marshal.WriteByte(p, 0x5A);
        }

        return p;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
}
