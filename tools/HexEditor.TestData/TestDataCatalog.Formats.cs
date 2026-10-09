using System.Globalization;
using System.Text;

namespace HexEditor.TestData;

/// <summary>
/// エンコード形式のテストデータ (test-data.md の TD-IHEX・TD-SREC・TD-BASE64、cases/11-tools.md の TD-TOOL-*、cases/01 の TD-ENG-IHEX-BADSUM など)。
/// アプリの変換処理とは別に、ここで形式どおりに作る (テストの期待値が同じ実装に頼らないため)。
/// </summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-IHEX・TD-SREC のデータ: (アドレス, TD-RANDOM-16M の位置, 長さ)。アドレスの飛びと 64 KB の境界をまたぐ範囲を含む。</summary>
    public static IReadOnlyList<(long Address, long Source, int Length)> FirmwareSegments =>
    [
        (0x0800_0000, 0, 512),
        (0x0800_0400, 512, 256),
        (0x0800_FF80, 768, 256),
    ];

    /// <summary>TD-IHEX・TD-SREC の各範囲のバイト列。</summary>
    public static IReadOnlyList<(long Address, byte[] Data)> FirmwareData()
    {
        var list = new List<(long, byte[])>();
        foreach ((long address, long source, int length) in FirmwareSegments)
        {
            byte[] data = new byte[length];
            Random(RandomSeed, source, data);
            list.Add((address, data));
        }

        return list;
    }

    /// <summary>TD-TOOL-FW-128K の乱数の種。</summary>
    public const ulong FirmwareSeed = 128;

    /// <summary>TD-TOOL-IHEX-1G の乱数の種と長さ。</summary>
    public const ulong IhexLargeSeed = 1;

    public const long IhexLargeData = 384 * MiB;

    /// <summary>TD-ENG-BASE64-1G の乱数の種と長さ。</summary>
    public const ulong Base64LargeSeed = 0xB64;

    public const long Base64LargeData = 768 * MiB;

    /// <summary>TD-TOOL-PEM の証明書 (テスト用の自己署名証明書。RSA 2048、CN=HexEditor Test。OpenSSL 3.5 で作ったものを固定で置く)。</summary>
    public const string PemCertificate =
        "-----BEGIN CERTIFICATE-----\r\n"
        + "MIIDATCCAemgAwIBAgICEjQwDQYJKoZIhvcNAQELBQAwGTEXMBUGA1UEAwwOSGV4\r\n"
        + "RWRpdG9yIFRlc3QwHhcNMjYxMDA5MTA0NTEzWhcNMzYxMDA2MTA0NTEzWjAZMRcw\r\n"
        + "FQYDVQQDDA5IZXhFZGl0b3IgVGVzdDCCASIwDQYJKoZIhvcNAQEBBQADggEPADCC\r\n"
        + "AQoCggEBAM8kouUAvX/AGhSgmKzwDsGEsGQp7jA2q0ecvtidmyDONSvEan+h5fml\r\n"
        + "4Fcn+XahplEnhlmawCHSE6Hx+jWgGKqry+sR9d8UX97KVjhMbjEUnIq7iCJCQpYf\r\n"
        + "jheeHc8yIiXyBw7I189hk4pHyFbFFKLYnreIy5pCJ8wiivFWoPpDXe4OtoOdNZPs\r\n"
        + "W9IYdKmB63ZefzHT6AjBk7fKVIaXfKzS3l4WdQJtSvtyzvBL/yEEitAj3Cb4Cj4X\r\n"
        + "YF7KAUITJzVcHFuI62tPD8YyNLoEOMOg2gPhCUiaFxUnvA70P8cm2IsCvpAnwBWU\r\n"
        + "iwOzQ+/xCZeWE3LXPzWsxnztd2xqEFMCAwEAAaNTMFEwHQYDVR0OBBYEFHPQFKf8\r\n"
        + "6J/l7Q+//8qeCLxqWqCHMB8GA1UdIwQYMBaAFHPQFKf86J/l7Q+//8qeCLxqWqCH\r\n"
        + "MA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQELBQADggEBABh4Y8L9Y8mdvSDe\r\n"
        + "L+jgeVk1B4AzxaWo9Vh2hFNqvSf82MW3FjObgo1vbwkDPtmcBnBNgUKx1R3gfqyS\r\n"
        + "iThzSwt9ZUmrx7vXUn0k8FiqsFtY0HBTUPPjWEEFykDDGEFH400NO05cmhP7gmej\r\n"
        + "oBSbVzHSet8Kgf8HLTIh0yyLyJSSzcqRjzjLI8L1gTzCnJuh+nGEfz2cPwnxfuDF\r\n"
        + "kR35AN2KIc61Q9GhvgecYbCus1oyuDLKrMLioYCNT2azhUqZsFsrOfHHdc93CfMs\r\n"
        + "2oyY43a2eXn842BizCadluDk1fLxG+jiXdaIT3RFZZlq1AKeeEh120qj+iqDSjP7\r\n"
        + "HE9eU7s=\r\n"
        + "-----END CERTIFICATE-----\r\n";

    /// <summary>TD-TOOL-PEM の証明書の DER。</summary>
    public static byte[] PemDer()
    {
        string body = string.Concat(PemCertificate.Split("\r\n").Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        return Convert.FromBase64String(body);
    }

    /// <summary>TD-TOOL-SPECIAL の内容。</summary>
    public static byte[] SpecialText()
    {
        byte[] specials = Encoding.ASCII.GetBytes("<>&\"\\{}$%#_^~`|");
        var list = new List<byte>();
        for (int i = 0; i < 4; i++)
        {
            list.AddRange(specials);
        }

        list.AddRange("AAAA"u8.ToArray());
        return [.. list];
    }

    private static IEnumerable<TestDataItem> FormatItems()
    {
        static TestDataItem Text(string id, string description, Func<string> content, Func<string, string>? pathIn = null)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(content());
            return new TestDataItem(id, bytes.Length, description, path => File.WriteAllBytes(path, bytes), pathIn);
        }

        yield return Text("TD-IHEX", "Intel HEX (32 bit のアドレス拡張とアドレスの飛び、64 KB の境界を含む)", IhexText);
        yield return Text("TD-SREC", "Motorola S-record (S3、アドレスの飛びを含む)", SrecText);
        yield return Text("TD-BASE64", "TD-RANDOM-16M の先頭 1 KiB の Base64 (1 行 76 文字、CRLF)", () =>
        {
            byte[] data = new byte[1024];
            Random(RandomSeed, 0, data);
            return Wrap(Convert.ToBase64String(data), 76);
        });
        yield return Text("TD-TOOL-HEXTEXT", "TD-BYTES-256 の Hex テキスト (1 行 16 バイト、大文字、空白区切り、CRLF)", () =>
        {
            var sb = new StringBuilder();
            for (int row = 0; row < 16; row++)
            {
                sb.Append(string.Join(' ', Enumerable.Range(row * 16, 16).Select(i => i.ToString("X2", CultureInfo.InvariantCulture)))).Append("\r\n");
            }

            return sb.ToString();
        });
        yield return new TestDataItem("TD-TOOL-FW-128K", 128 * KiB, "種 128 の乱数", path => WriteGenerated(path, 128 * KiB, (o, s) => Random(FirmwareSeed, o, s)));
        yield return new TestDataItem("TD-TOOL-IHEX-1G", IhexLargeLength, "種 1 の乱数 384 MiB の Intel HEX (I32HEX、レコード長 16、CRLF)", WriteIhexLarge);
        yield return Text("TD-TOOL-IHEX-BADSUM", "TD-IHEX の 5 行目のチェックサムを +1 したもの", () => BadChecksum(IhexText(), 5));
        yield return Text("TD-ENG-IHEX-BADSUM", "TD-IHEX の 5 行目 (データレコード) のチェックサムを +1 したもの", () => BadChecksum(IhexText(), 5));
        yield return Text("TD-TOOL-IHEX-SPARSE", "0x0000 と 0xFFFF0000 にデータがある I32HEX", () =>
        {
            var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
            WriteIhex(w, [(0, [.. Enumerable.Range(0, 16).Select(i => (byte)i)]), (0xFFFF_0000, [.. Enumerable.Range(0xF0, 16).Select(i => (byte)i)])],
                16, leadingExtended: true);
            return w.ToString();
        });
        yield return Text("TD-TOOL-IHEX-OBJCOPY", "TD-TOOL-FW-128K を 0x08000000 から objcopy -O ihex と同じ形 (レコード長 16、I32HEX、大文字、CRLF) にしたもの", () =>
        {
            byte[] data = new byte[128 * KiB];
            Random(FirmwareSeed, 0, data);
            var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
            WriteIhex(w, [(0x0800_0000, data)], 16, leadingExtended: true);
            return w.ToString();
        });
        yield return Text("TD-TOOL-IHEX-COMMENT", "TD-IHEX の前にコメント行 ; build 1.0 を加えたもの", () => "; build 1.0\r\n" + IhexText());
        foreach ((string id, long address, int bytes) in new[] { ("S19", 0x1000L, 2), ("S28", 0x010000L, 3), ("S37", 0x0800_0000L, 4) })
        {
            yield return Text("TD-TOOL-SREC-" + id, $"TD-RANDOM-16M の先頭 4 KiB の S-record ({id})", () => SrecSmall(address, bytes, countDelta: 0));
        }

        yield return Text("TD-TOOL-SREC-BADCOUNT", "TD-TOOL-SREC-S19 の S5 のレコード数を +1 したもの", () => SrecSmall(0x1000, 2, countDelta: 1));
        yield return Text("TD-TOOL-SREC-SRECCAT", "TD-TOOL-FW-128K を 0x08000000 から srec_cat と同じ形 (S3、レコード長 32、ヘッダ HEXEDTEST、S5、S7、CRLF) にしたもの", () =>
        {
            byte[] data = new byte[128 * KiB];
            Random(FirmwareSeed, 0, data);
            var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
            WriteSrec(w, [(0x0800_0000, data)], 4, 32, "HEXEDTEST", writeCount: true, start: 0x0800_0000);
            return w.ToString();
        });
        yield return Text("TD-TOOL-PEM", "テスト用の自己署名証明書 (PEM)", () => PemCertificate);
        yield return new TestDataItem("TD-TOOL-DUMPS", DumpTexts()["xxd.txt"].Length, "TD-BYTES-256 の 5 種類のダンプ (xxd・hexdump -C・od・certutil・このアプリ)",
            WriteDumps, dir => Path.Combine(dir, "TD-TOOL-DUMPS", "xxd.txt"));
        yield return new TestDataItem("TD-TOOL-SPECIAL", 64, "特殊文字を含むテキスト", path => File.WriteAllBytes(path, SpecialText()));
        yield return new TestDataItem("TD-TOOL-SEQ-8M", 8 * MiB, "オフセット n の値は n mod 256 (8 MiB)", path => WriteGenerated(path, 8 * MiB, (o, s) => Sequence(o, s)));
        yield return new TestDataItem("TD-TOOL-ZERO-32M", 32 * MiB, "すべて 00 (スパース)", path => WriteMarkers(path, 32 * MiB, []));
        yield return new TestDataItem("TD-ENG-BASE64-1G", Base64LargeData / 3 * 4, "種 0xB64 の乱数 768 MiB の Base64 (改行なし、パディングなし)", WriteBase64Large);
    }

    /// <summary>76 文字ごとに CRLF で改行する (最後の行の後にも改行)。</summary>
    private static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i += width)
        {
            sb.Append(text.AsSpan(i, Math.Min(width, text.Length - i))).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>TD-IHEX の内容。</summary>
    public static string IhexText()
    {
        var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        WriteIhex(w, FirmwareData(), 16, leadingExtended: true);
        return w.ToString();
    }

    /// <summary>TD-SREC の内容。</summary>
    public static string SrecText()
    {
        var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        WriteSrec(w, FirmwareData(), 4, 16, "HEXEDTEST", writeCount: true, start: 0x0800_0000);
        return w.ToString();
    }

    private static string SrecSmall(long address, int addressBytes, int countDelta)
    {
        byte[] data = new byte[4 * KiB];
        Random(RandomSeed, 0, data);
        var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        WriteSrec(w, [(address, data)], addressBytes, 16, "HEXEDTEST", writeCount: true, start: address, countDelta);
        return w.ToString();
    }

    /// <summary><paramref name="line"/> 行目 (1 から) のチェックサムに 1 を足す。</summary>
    private static string BadChecksum(string text, int line)
    {
        string[] lines = text.Split("\r\n");
        string target = lines[line - 1];
        int sum = Convert.ToInt32(target[^2..], 16);
        lines[line - 1] = target[..^2] + ((sum + 1) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Intel HEX を書く (I32HEX)。レコードは 64 KB の境界をまたがず、上位のアドレスが変わるときに 04 レコードを出す
    /// (<paramref name="leadingExtended"/> なら最初にも出す)。最後に 01 レコード。
    /// </summary>
    public static void WriteIhex(TextWriter w, IEnumerable<(long Address, byte[] Data)> segments, int recordLength, bool leadingExtended)
    {
        long upper = leadingExtended ? -1 : 0;
        foreach ((long address, byte[] data) in segments)
        {
            int at = 0;
            while (at < data.Length)
            {
                long a = address + at;
                int n = (int)Math.Min(Math.Min(recordLength, data.Length - at), 0x10000 - (a & 0xFFFF));
                if (a >> 16 != upper)
                {
                    upper = a >> 16;
                    IhexLine(w, 0, 4, [(byte)(upper >> 8), (byte)upper]);
                }

                IhexLine(w, (int)(a & 0xFFFF), 0, data.AsSpan(at, n));
                at += n;
            }
        }

        w.Write(":00000001FF");
        w.WriteLine();
    }

    private static void IhexLine(TextWriter w, int address, byte type, ReadOnlySpan<byte> data)
    {
        int sum = data.Length + (address >> 8) + (address & 0xFF) + type;
        var sb = new StringBuilder(":");
        sb.Append(data.Length.ToString("X2", CultureInfo.InvariantCulture)).Append(address.ToString("X4", CultureInfo.InvariantCulture))
            .Append(type.ToString("X2", CultureInfo.InvariantCulture));
        foreach (byte b in data)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sum += b;
        }

        sb.Append(((byte)(-sum)).ToString("X2", CultureInfo.InvariantCulture));
        w.Write(sb);
        w.WriteLine();
    }

    /// <summary>S-record を書く (S0 ヘッダ、データ、S5 のレコード数、終わりのレコード)。</summary>
    public static void WriteSrec(TextWriter w, IEnumerable<(long Address, byte[] Data)> segments, int addressBytes, int recordLength, string header,
        bool writeCount, long start, int countDelta = 0)
    {
        SrecLine(w, 0, 0, Encoding.ASCII.GetBytes(header), 2);
        int count = 0;
        foreach ((long address, byte[] data) in segments)
        {
            for (int at = 0; at < data.Length; at += recordLength)
            {
                SrecLine(w, addressBytes - 1, address + at, data.AsSpan(at, Math.Min(recordLength, data.Length - at)), addressBytes);
                count++;
            }
        }

        if (writeCount)
        {
            SrecLine(w, 5, count + countDelta, [], 2);
        }

        SrecLine(w, 11 - addressBytes, start, [], addressBytes);
    }

    private static void SrecLine(TextWriter w, int type, long address, ReadOnlySpan<byte> data, int addressBytes)
    {
        int count = addressBytes + data.Length + 1;
        int sum = count;
        var sb = new StringBuilder("S");
        sb.Append(type.ToString(CultureInfo.InvariantCulture)).Append(count.ToString("X2", CultureInfo.InvariantCulture));
        for (int i = addressBytes - 1; i >= 0; i--)
        {
            int b = (int)((address >> (i * 8)) & 0xFF);
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sum += b;
        }

        foreach (byte b in data)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sum += b;
        }

        sb.Append(((byte)~sum).ToString("X2", CultureInfo.InvariantCulture));
        w.Write(sb);
        w.WriteLine();
    }

    /// <summary>TD-TOOL-IHEX-1G の長さ: データ行 45 文字 × 25,165,824、04 レコード 17 文字 × 6,144、01 レコード 13 文字。</summary>
    private const long IhexLargeLength = IhexLargeData / 16 * 45 + IhexLargeData / 0x10000 * 17 + 13;

    private static void WriteIhexLarge(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new StreamWriter(stream, new UTF8Encoding(false), 1 << 20) { NewLine = "\r\n" };
        byte[] chunk = new byte[(int)MiB];
        long upper = -1;
        for (long offset = 0; offset < IhexLargeData; offset += chunk.Length)
        {
            Random(IhexLargeSeed, offset, chunk);
            for (int at = 0; at < chunk.Length; at += 16)
            {
                long a = offset + at;
                if (a >> 16 != upper)
                {
                    upper = a >> 16;
                    IhexLine(w, 0, 4, [(byte)(upper >> 8), (byte)upper]);
                }

                IhexLine(w, (int)(a & 0xFFFF), 0, chunk.AsSpan(at, 16));
            }
        }

        w.Write(":00000001FF");
        w.WriteLine();
    }

    private static void WriteBase64Large(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        byte[] chunk = new byte[3 * 256 * 1024];
        for (long offset = 0; offset < Base64LargeData; offset += chunk.Length)
        {
            Random(Base64LargeSeed, offset, chunk);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(chunk)));
        }
    }

    /// <summary>
    /// TD-TOOL-DUMPS の 5 つのダンプ。xxd (vim 9.0)・<c>hexdump -C</c>・<c>od -A x -t x1</c>・<c>certutil -encodehex</c>・このアプリのダンプ
    /// (TOOL-10 のテキスト、既定の設定) の出力の形をここで再現する (CI に各ツールがないため。形は各ツールの実際の出力で確かめた)。
    /// </summary>
    public static IReadOnlyDictionary<string, string> DumpTexts()
    {
        byte[] data = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        static char Show(byte b) => b is >= 0x20 and <= 0x7E ? (char)b : '.';
        var xxd = new StringBuilder();
        var hexdump = new StringBuilder();
        var od = new StringBuilder();
        var certutil = new StringBuilder();
        var app = new StringBuilder();
        for (int row = 0; row < 16; row++)
        {
            byte[] r = data[(row * 16)..(row * 16 + 16)];
            string text = new([.. r.Select(Show)]);
            string lower(int i) => r[i].ToString("x2", CultureInfo.InvariantCulture);
            string upperHex(int i) => r[i].ToString("X2", CultureInfo.InvariantCulture);
            xxd.Append((row * 16).ToString("x8", CultureInfo.InvariantCulture)).Append(": ")
                .Append(string.Join(' ', Enumerable.Range(0, 8).Select(g => lower(g * 2) + lower(g * 2 + 1)))).Append("  ").Append(text).Append('\n');
            hexdump.Append((row * 16).ToString("x8", CultureInfo.InvariantCulture)).Append("  ")
                .Append(string.Join(' ', Enumerable.Range(0, 8).Select(lower))).Append("  ")
                .Append(string.Join(' ', Enumerable.Range(8, 8).Select(lower))).Append("  |").Append(text).Append("|\n");
            od.Append((row * 16).ToString("x6", CultureInfo.InvariantCulture)).Append(' ')
                .Append(string.Join(' ', Enumerable.Range(0, 16).Select(lower))).Append('\n');
            certutil.Append((row * 16).ToString("x4", CultureInfo.InvariantCulture)).Append('\t')
                .Append(string.Join(' ', Enumerable.Range(0, 8).Select(lower))).Append("  ")
                .Append(string.Join(' ', Enumerable.Range(8, 8).Select(lower))).Append("   ").Append(text).Append("\r\n");
            app.Append((row * 16).ToString("X8", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(string.Join(' ', Enumerable.Range(0, 8).Select(upperHex))).Append("  ")
                .Append(string.Join(' ', Enumerable.Range(8, 8).Select(upperHex))).Append(" | ").Append(text).Append("\r\n");
        }

        hexdump.Append("00000100\n");
        od.Append("000100\n");
        return new Dictionary<string, string>
        {
            ["xxd.txt"] = xxd.ToString(),
            ["hexdump-C.txt"] = hexdump.ToString(),
            ["od.txt"] = od.ToString(),
            ["certutil.txt"] = certutil.ToString(),
            ["app.txt"] = app.ToString(),
        };
    }

    private static void WriteDumps(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        foreach ((string name, string text) in DumpTexts())
        {
            File.WriteAllBytes(name == "xxd.txt" ? path : Path.Combine(dir, name), Encoding.ASCII.GetBytes(text));
        }
    }
}
