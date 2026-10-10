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
// 準備ができたら "READY" を出力し、標準入力が閉じられる (または "exit" の行を受け取る) まで待つ。
string valueHex = "4845584544495421";
int regions = 0;
string? text = null;
string? outFile = null;
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

Report("READY");
Console.Out.Flush();
while (Console.ReadLine() is { } line)
{
    if (line.Trim() == "exit")
    {
        break;
    }
}

GC.KeepAlive(value);
return 0;

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
