using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HexEditor.TestData;

// cases/06-analysis.md の表の統計・ファイル形式の判定 (ANA-10〜ANA-17) のテストデータ。大きいもの (TD-ANA-U32-100M、TD-ANA-HALF-2G、
// TD-ANA-NGRAM-1G、TD-ANA-10G-A) は、テストが内容を計算する仮想のデータソースで代わりにする (ここでは作らない)。
public static partial class TestDataCatalog
{
    private static readonly Lazy<byte[]> ClassesData = new(() => BuildClasses().Data);
    private static readonly Lazy<byte[]> ZlibEmbeddedData = new(BuildZlibEmbedded);

    private static IEnumerable<TestDataItem> AnalysisItems() =>
    [
        new("TD-ANA-10G-A", 10 * GiB, "固定の種の乱数 (スパースにしない。性能計測用)", path => WriteGenerated(path, 10 * GiB, (o, s) => Random(Ana10GSeed, o, s))),
        new("TD-ANA-41-1M", MiB, "すべて 41", path => WriteGenerated(path, MiB, (_, s) => s.Fill(0x41))),
        new("TD-ANA-CRYPT-10M", 10 * MiB, "AES-CTR の鍵ストリーム", path => WriteGenerated(path, 10 * MiB, (o, s) => AesCtr(CryptKey, o, s))),
        new("TD-ANA-DEADBEEF", 16_000, "DE AD BE EF と乱数 12 バイトのレコード 1,000 個", path => WriteAll(path, DeadBeef())),
        new("TD-ANA-REC64", MiB, "64 バイトのレコード 16,384 個", path => WriteAll(path, Rec64())),
        new("TD-ANA-CLASSES", 0, "ゼロ埋め・英文・gzip・AES-256-CBC を連結したもの", path => WriteAll(path, ClassesData.Value))
        {
            ComputeLength = () => ClassesData.Value.Length,
        },
        new("TD-ANA-ZLIB-EMB", 0, "乱数 64 KiB・zlib (0x10000)・乱数 64 KiB", path => WriteAll(path, ZlibEmbeddedData.Value))
        {
            ComputeLength = () => ZlibEmbeddedData.Value.Length,
        },
        new("TD-PNG", Png16().Length, "16×16 の PNG 画像", path => WriteAll(path, Png16())),
        new("TD-ANA-PNG-AS-JPG", Png16().Length, "TD-PNG と同じ内容で拡張子が .jpg", path => WriteAll(path, Png16()),
            dir => Path.Combine(dir, "TD-ANA-PNG-AS-JPG.jpg")),
        new("TD-ANA-JPEG", Jpeg16().Length, "16×16 のベースライン JPEG (JFIF)", path => WriteAll(path, Jpeg16())),
        new("TD-ANA-PDF", Pdf().Length, "1 ページの PDF 1.7", path => WriteAll(path, Pdf())),
        new("TD-PE-X64", PeX64().Length, "小さな Windows 実行ファイル (x64)", path => WriteAll(path, PeX64())),
        new("TD-ELF-X64", ElfX64().Length, "小さな Linux 実行ファイル (x64)", path => WriteAll(path, ElfX64())),
        new("TD-ANA-PE-PNG", PePng().Length, "TD-PE-X64 の 0x2000 から TD-PNG を埋め込んだもの", path => WriteAll(path, PePng())),
        new("TD-ANA-MAGIC-FILE", 64, "HXTESTMAGIC の後に 00 を 53 バイト", path => WriteAll(path, MagicFile())),
        new("TD-ANA-MAGIC-JSON", Encoding.UTF8.GetByteCount(MagicJson), "利用者のシグネチャデータベース (hexed-test.json)",
            path => File.WriteAllText(path, MagicJson, new UTF8Encoding(false)), dir => Path.Combine(dir, "TD-ANA-MAGIC", "hexed-test.json")),
        new("TD-ANA-MAGIC-BAD", Encoding.UTF8.GetByteCount(MagicBadJson), "3 行目に構文エラーがある JSON (broken.json)",
            path => File.WriteAllText(path, MagicBadJson, new UTF8Encoding(false)), dir => Path.Combine(dir, "TD-ANA-MAGIC", "broken.json")),
    ];

    /// <summary>TD-ANA-10G-A の乱数の種。</summary>
    public const ulong Ana10GSeed = 0x10A;

    /// <summary>TD-ANA-CRYPT-10M の AES の鍵 (SHA-256("TD-ANA-CRYPT-10M"))。</summary>
    public static readonly byte[] CryptKey = SHA256.HashData("TD-ANA-CRYPT-10M"u8);

    /// <summary>
    /// AES-256-CTR の鍵ストリーム: 16 バイトのブロック i は、カウンタ i (128 bit のビッグエンディアン) を AES-ECB で暗号化したもの。
    /// </summary>
    public static void AesCtr(byte[] key, long offset, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.Key = key;
        long first = offset / 16;
        long last = (offset + destination.Length - 1) / 16;
        int blocks = (int)(last - first + 1);
        byte[] counters = new byte[blocks * 16];
        for (int i = 0; i < blocks; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(counters.AsSpan((i * 16) + 8, 8), first + i);
        }

        byte[] stream = aes.EncryptEcb(counters, PaddingMode.None);
        stream.AsSpan((int)(offset - (first * 16)), destination.Length).CopyTo(destination);
    }

    /// <summary>TD-ANA-DEADBEEF: 16 バイトのレコード (DE AD BE EF + 乱数 12 バイト) を 1,000 個。乱数の中に DE AD BE EF が現れないようにする。</summary>
    public static byte[] DeadBeef()
    {
        byte[] marker = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] data = new byte[16_000];
        ulong seed = 0xDEADBEEF;
        for (int r = 0; r < 1000; r++)
        {
            marker.CopyTo(data, r * 16);
        }

        while (true)
        {
            for (int r = 0; r < 1000; r++)
            {
                Random(seed, r * 12L, data.AsSpan((r * 16) + 4, 12));
            }

            bool clean = true;
            for (int i = 0; i + 4 <= data.Length; i++)
            {
                if (i % 16 != 0 && data.AsSpan(i, 4).SequenceEqual(marker))
                {
                    clean = false;
                    break;
                }
            }

            if (clean)
            {
                return data;
            }

            seed++;
        }
    }

    /// <summary>TD-ANA-REC64 のレコードの先頭の固定の 16 バイト。</summary>
    public static readonly byte[] Rec64Header = [0x52, 0x45, 0x43, 0x4F, 0x52, 0x44, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>TD-ANA-REC64: 64 バイトのレコード (固定の 16 バイト、レコード番号 (u32 LE)、乱数 44 バイト) を 16,384 個。</summary>
    public static byte[] Rec64()
    {
        byte[] data = new byte[(int)MiB];
        for (int r = 0; r < 16_384; r++)
        {
            Span<byte> rec = data.AsSpan(r * 64, 64);
            Rec64Header.CopyTo(rec);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], (uint)r);
            Random(0x6464, r * 44L, rec[20..]);
        }

        return data;
    }

    /// <summary>TD-ANA-CLASSES の各部分の開始オフセット (ゼロ埋め、テキスト、gzip、AES-256-CBC)。</summary>
    public static IReadOnlyList<long> ClassesStarts => BuildClasses().Starts;

    private static (byte[] Data, long[] Starts) BuildClasses()
    {
        var parts = new List<byte[]>
        {
            new byte[MiB],
            RepeatBytes(Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false), 256),
            GzipEnglish(),
            AesCbc(),
        };
        var result = new List<byte>();
        long[] starts = new long[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            // 開始オフセットを 4 KiB の倍数にそろえる。隙間は直前の部分と同じ種類のデータで埋める。
            int gap = (int)((4 * KiB - (result.Count % (4 * KiB))) % (4 * KiB));
            if (i > 0 && gap > 0)
            {
                byte[] previous = parts[i - 1];
                for (int k = 0; k < gap; k++)
                {
                    result.Add(previous[k % previous.Length]);
                }
            }

            starts[i] = result.Count;
            result.AddRange(parts[i]);
        }

        return ([.. result], starts);
    }

    private static byte[] RepeatBytes(byte[] bytes, int times)
    {
        byte[] data = new byte[bytes.Length * times];
        for (int i = 0; i < times; i++)
        {
            bytes.CopyTo(data, i * bytes.Length);
        }

        return data;
    }

    /// <summary>固定の種で単語表から作った 4 MiB の英文を gzip で圧縮したもの。</summary>
    private static byte[] GzipEnglish()
    {
        string[] words = EnglishWords;
        var text = new StringBuilder((int)(4 * MiB) + 64);
        ulong state = 0x0E5E;
        int sentence = 0;
        while (text.Length < 4 * MiB)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            string w = words[(int)((state >> 33) % (ulong)words.Length)];
            text.Append(sentence == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w);
            sentence++;
            if (sentence > 6 + (int)((state >> 20) % 10))
            {
                text.Append(". ");
                sentence = 0;
            }
            else
            {
                text.Append(' ');
            }
        }

        byte[] plain = Encoding.ASCII.GetBytes(text.ToString(0, (int)(4 * MiB)));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        return output.ToArray();
    }

    /// <summary>TD-ANA-CRYPT-10M の先頭 1 MiB を固定の鍵で AES-256-CBC (PKCS#7) で暗号化したもの。</summary>
    private static byte[] AesCbc()
    {
        byte[] plain = new byte[MiB];
        AesCtr(CryptKey, 0, plain);
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData("TD-ANA-CLASSES-KEY"u8);
        byte[] iv = MD5.HashData("TD-ANA-CLASSES-IV"u8);
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    /// <summary>
    /// TD-ANA-ZLIB-EMB: 乱数 64 KiB、TD-TEXT-ASCII を zlib (ヘッダ 78 DA) で圧縮したもの (0x10000 から)、乱数 64 KiB。
    /// 乱数の部分にはヘッダのチェック値が正しい zlib のヘッダの並び (78 01 / 78 5E / 78 9C / 78 DA) が現れないようにする。
    /// </summary>
    private static byte[] BuildZlibEmbedded()
    {
        byte[] before = CleanRandom(0x2B1, 64 * (int)KiB);
        byte[] after = CleanRandom(0x2B2, 64 * (int)KiB);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false));
        }

        byte[] compressed = output.ToArray();

        // .NET の ZLibStream はレベルによってヘッダの 2 バイト目が変わる。仕様どおり 78 DA にそろえる (FLEVEL だけの違いで、展開には影響しない)。
        compressed[1] = 0xDA;
        return [.. before, .. compressed, .. after];
    }

    private static byte[] CleanRandom(ulong seed, int length)
    {
        byte[] data = new byte[length];
        Random(seed, 0, data);
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0x78 && data[i + 1] is 0x01 or 0x5E or 0x9C or 0xDA)
            {
                data[i + 1] ^= 0x40;
            }
        }

        return data;
    }

    private static string[] EnglishWords =>
    [
        "the", "of", "and", "to", "in", "is", "you", "that", "it", "he", "was", "for", "on", "are", "as", "with", "his", "they", "at", "be",
        "this", "have", "from", "or", "one", "had", "by", "word", "but", "not", "what", "all", "were", "we", "when", "your", "can", "said",
        "there", "use", "an", "each", "which", "she", "do", "how", "their", "if", "will", "up", "other", "about", "out", "many", "then",
        "them", "these", "so", "some", "her", "would", "make", "like", "him", "into", "time", "has", "look", "two", "more", "write", "go",
        "see", "number", "no", "way", "could", "people", "my", "than", "first", "water", "been", "call", "who", "oil", "its", "now", "find",
        "long", "down", "day", "did", "get", "come", "made", "may", "part", "over", "new", "sound", "take", "only", "little", "work", "know",
        "place", "year", "live", "me", "back", "give", "most", "very", "after", "thing", "our", "just", "name", "good", "sentence", "man",
        "think", "say", "great", "where", "help", "through", "much", "before", "line", "right", "too", "mean", "old", "any", "same", "tell",
        "boy", "follow", "came", "want", "show", "also", "around", "form", "three", "small", "set", "put", "end", "does", "another", "well",
        "large", "must", "big", "even", "such", "because", "turn", "here", "why", "ask", "went", "men", "read", "need", "land", "different",
        "home", "us", "move", "try", "kind", "hand", "picture", "again", "change", "off", "play", "spell", "air", "away", "animal", "house",
        "point", "page", "letter", "mother", "answer", "found", "study", "still", "learn", "should", "america", "world", "binary", "editor",
    ];

    // ---- 画像・文書・実行形式 (ファイル形式の判定) ----

    /// <summary>16×16 の RGB の PNG (左上から右下への色のグラデーション)。</summary>
    public static byte[] Png16()
    {
        var raw = new MemoryStream();
        for (int y = 0; y < 16; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < 16; x++)
            {
                raw.WriteByte((byte)(x * 16));
                raw.WriteByte((byte)(y * 16));
                raw.WriteByte((byte)((x + y) * 8));
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 16);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 16);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32([.. typeBytes, .. data]));
        s.Write(len);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// 16×16 のグレースケールのベースライン JPEG (JFIF)。すべての係数が 0 の 4 ブロックを、記号 1 つずつのハフマン表で符号化する
    /// (生成ツールに内蔵した固定の符号化表)。
    /// </summary>
    public static byte[] Jpeg16()
    {
        var j = new List<byte> { 0xFF, 0xD8 };
        j.AddRange([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        j.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        j.AddRange(Enumerable.Repeat((byte)1, 64));
        j.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
        foreach (byte table in new byte[] { 0x00, 0x10 })
        {
            j.AddRange([0xFF, 0xC4, 0x00, 0x14, table, 0x01]);
            j.AddRange(new byte[15]);
            j.Add(0x00);
        }

        j.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);

        // 4 ブロック × (DC の差 0 の符号 "0" + EOB の符号 "0") = 8 ビット。
        j.Add(0x00);
        j.AddRange([0xFF, 0xD9]);
        return [.. j];
    }

    /// <summary>1 ページの PDF 1.7 (テキスト HexEditor test を 1 行表示)。xref のオフセットを計算して埋める。</summary>
    public static byte[] Pdf()
    {
        string content = "BT /F1 24 Tf 72 720 Td (HexEditor test) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append($"{o:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>テキストファイル 3 つを入れた ZIP (日時は固定)。</summary>
    public static byte[] Zip3()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            foreach ((string name, int lines) in new[] { ("a.txt", 10), ("b.txt", 20), ("c.txt", 30) })
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = time;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                for (int i = 0; i < lines; i++)
                {
                    writer.Write(AsciiLines[i % AsciiLines.Length] + "\r\n");
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// 小さな Windows 実行ファイル (x64、8 KiB)。DOS ヘッダ (e_lfanew = 0x80)、PE シグネチャ、COFF ヘッダ (AMD64)、PE32+ の
    /// オプションヘッダ、.text セクション 1 つ (ret だけ) を持つ。
    /// </summary>
    public static byte[] PeX64()
    {
        byte[] pe = new byte[0x2000];
        "MZ"u8.CopyTo(pe);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(2), 0x90);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x18), 0x40);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), 0x80);
        "This program cannot be run in DOS mode.\r\r\n$"u8.CopyTo(pe.AsSpan(0x4E));
        int h = 0x80;
        "PE\0\0"u8.CopyTo(pe.AsSpan(h));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 4), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 20), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(h + 22), 0x22);
        int o = h + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o), 0x20B);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 4), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 16), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 20), 0x1000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 24), 0x140000000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 32), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 36), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 40), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 48), 6);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 56), 0x2000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 60), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(o + 68), 3);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 72), 0x100000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 80), 0x1000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 88), 0x100000);
        BinaryPrimitives.WriteInt64LittleEndian(pe.AsSpan(o + 96), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(o + 108), 16);
        int s = o + 0xF0;
        ".text\0\0\0"u8.CopyTo(pe.AsSpan(s));
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(s + 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(s + 12), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(s + 16), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(s + 20), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(s + 36), 0x60000020);
        pe[0x200] = 0xC3;
        return pe;
    }

    /// <summary>小さな Linux 実行ファイル (x64、ELF64、8 KiB)。ELF ヘッダとプログラムヘッダ (PT_LOAD) 1 つ、コードは exit(0)。</summary>
    public static byte[] ElfX64()
    {
        byte[] elf = new byte[0x2000];
        elf[0] = 0x7F;
        "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(24), 0x401000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56), 1);
        int p = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(p), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(p + 4), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 8), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 16), 0x401000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 24), 0x401000);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 32), 12);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 40), 12);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(p + 48), 0x1000);
        byte[] code = [0xB8, 0x3C, 0x00, 0x00, 0x00, 0x31, 0xFF, 0x0F, 0x05, 0x90, 0x90, 0x90];
        code.CopyTo(elf, 0x1000);
        return elf;
    }

    /// <summary>TD-ANA-PE-PNG: TD-PE-X64 の後ろ (0x2000) に TD-PNG を置いたもの。</summary>
    public static byte[] PePng() => [.. PeX64(), .. Png16()];

    public static byte[] MagicFile() => [.. "HXTESTMAGIC"u8, .. new byte[53]];

    /// <summary>TD-ANA-MAGIC-JSON (tests/fixtures/magic/hexed-test.json と同じ内容)。</summary>
    public const string MagicJson = """
        {
          "formats": [
            {
              "id": "hexed-test",
              "name": "HexEditor Test Format",
              "mime": "application/x-hexed-test",
              "extensions": ["hxtest"],
              "match": { "offset": 0, "bytes": "48 58 54 45 53 54 4D 41 47 49 43" }
            }
          ]
        }

        """;

    /// <summary>TD-ANA-MAGIC-BAD (tests/fixtures/magic/broken.json と同じ内容)。3 行目のオブジェクトの閉じ括弧がない。</summary>
    public const string MagicBadJson = """
        {
          "formats": [
            { "id": "broken", "name": "Broken", "match": { "offset": 0, "bytes": "00" } ]
        }

        """;
}
