using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.Forensics.Images.Ewf;

// Expert Witness 形式 (EWF-E01、EnCase 6 互換) の構造。公開されている形式の説明 (Joachim Metz の "Expert Witness Compression Format"
// 仕様書) に従って自前で実装する (FOR-20 の仕様 8。libewf (LGPL) は使わない)。値はすべてリトルエンディアン。

/// <summary>EWF の定数とセクションの記述子。</summary>
public static class EwfFormat
{
    /// <summary>セグメントファイルの先頭 8 バイト "EVF\t\r\n\xFF\0"。</summary>
    public static ReadOnlySpan<byte> Signature => [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00];

    public const int FileHeaderSize = 13;

    public const int SectionDescriptorSize = 76;

    public const int VolumeSize = 1052;

    public const int TableHeaderSize = 24;

    /// <summary>1 つの table セクションの最大のエントリ数 (EnCase と同じ)。</summary>
    public const int MaxTableEntries = 16375;

    /// <summary>チャンクのセクタ数 (FOR-20 の仕様 4: 64 セクタ固定)。</summary>
    public const int SectorsPerChunk = 64;

    /// <summary>セグメントの最大番号 (.E01〜.E99、.EAA〜.ZZZ)。</summary>
    public const int MaxSegmentNumber = 99 + 22 * 26 * 26;

    /// <summary>セグメントの拡張子 (1 から)。.E01〜.E99、続けて .EAA〜.ZZZ (FOR-20 の仕様 2)。</summary>
    public static string SegmentExtension(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxSegmentNumber);
        if (number <= 99)
        {
            return ".E" + number.ToString("D2", CultureInfo.InvariantCulture);
        }

        int k = number - 100;
        char a = (char)('E' + k / 676);
        char b = (char)('A' + k / 26 % 26);
        char c = (char)('A' + k % 26);
        return "." + a + b + c;
    }

    /// <summary>拡張子からセグメント番号 (違えば 0)。</summary>
    public static int SegmentNumber(string extension)
    {
        if (extension.Length != 4 || extension[0] != '.')
        {
            return 0;
        }

        string e = extension[1..].ToUpperInvariant();
        if (e[0] == 'E' && char.IsAsciiDigit(e[1]) && char.IsAsciiDigit(e[2]))
        {
            int n = (e[1] - '0') * 10 + (e[2] - '0');
            return n >= 1 ? n : 0;
        }

        if (e[0] is >= 'E' and <= 'Z' && char.IsAsciiLetterUpper(e[1]) && char.IsAsciiLetterUpper(e[2]))
        {
            return 100 + (e[0] - 'E') * 676 + (e[1] - 'A') * 26 + (e[2] - 'A');
        }

        return 0;
    }

    /// <summary><paramref name="first"/> (<c>.E01</c>) と同じ名前の <paramref name="number"/> 番目のセグメントのパス。</summary>
    public static string SegmentPath(string first, int number) => Path.ChangeExtension(first, SegmentExtension(number));

    /// <summary>E01 の最初のセグメントらしいか (拡張子 .E01)。</summary>
    public static bool IsFirstSegment(string path) => SegmentNumber(Path.GetExtension(path)) == 1;

    /// <summary>セグメントのファイルヘッダー (13 バイト)。</summary>
    public static byte[] FileHeader(int segment)
    {
        byte[] header = new byte[FileHeaderSize];
        Signature.CopyTo(header);
        header[8] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(9), checked((ushort)segment));
        return header;
    }

    /// <summary>セクションの記述子 (76 バイト。末尾 4 バイトは先頭 72 バイトの Adler-32)。</summary>
    public static byte[] SectionDescriptor(string type, long nextOffset, long size)
    {
        byte[] d = new byte[SectionDescriptorSize];
        Encoding.ASCII.GetBytes(type, d);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), nextOffset);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), size);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(72), ImageChecksums.Adler32(d.AsSpan(0, 72)));
        return d;
    }

    /// <summary>記述子の種類の文字列 (末尾の 0 を除く)。</summary>
    public static string SectionType(ReadOnlySpan<byte> descriptor)
    {
        ReadOnlySpan<byte> type = descriptor[..16];
        int end = type.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? type : type[..end]);
    }

    /// <summary>
    /// volume / data セクションの本体 (1052 バイト。EnCase 5 以降の形)。
    /// </summary>
    public static byte[] Volume(long chunkCount, int sectorsPerChunk, int bytesPerSector, long sectorCount, EwfCompression compression,
        Guid setIdentifier, bool physical, int errorGranularity = 64)
    {
        byte[] v = new byte[VolumeSize];
        v[0] = 0x01; // 固定ディスク
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(4), checked((uint)chunkCount));
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(8), (uint)sectorsPerChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(12), (uint)bytesPerSector);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(16), sectorCount);
        v[36] = (byte)(0x01 | (physical ? 0x02 : 0)); // イメージ、物理ディスク
        v[52] = compression switch { EwfCompression.Fast => 1, EwfCompression.Best => 2, _ => 0 };
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(56), (uint)errorGranularity);
        setIdentifier.TryWriteBytes(v.AsSpan(64, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(1048), ImageChecksums.Adler32(v.AsSpan(0, 1048)));
        return v;
    }
}

/// <summary>header / header2 セクションの値 (タブ区切りのテキスト)。</summary>
public static class EwfHeaderText
{
    /// <summary>EnCase 6 の header (ASCII)。日時は "yyyy M d H m s" の形。</summary>
    public static string Header(EwfCaseInfo info, DateTimeOffset acquired, DateTimeOffset system, string version, string os, EwfCompression compression)
    {
        string r = compression switch { EwfCompression.Best => "b", EwfCompression.Fast => "f", _ => "n" };
        string[] values =
        [
            Clean(info.CaseNumber), Clean(info.EvidenceNumber), Clean(info.Description), Clean(info.Examiner), Clean(info.Notes),
            Clean(version), Clean(os), HeaderDate(acquired), HeaderDate(system), "0", r,
        ];
        return "1\nmain\nc\tn\ta\te\tt\tav\tov\tm\tu\tp\tr\n" + string.Join('\t', values) + "\n\n";
    }

    /// <summary>EnCase 6 の header2 (UTF-16 LE)。日時は UNIX 時刻。</summary>
    public static string Header2(EwfCaseInfo info, DateTimeOffset acquired, DateTimeOffset system, string version, string os, string? model, string? serial)
    {
        string[] values =
        [
            Clean(info.Description), Clean(info.CaseNumber), Clean(info.EvidenceNumber), Clean(info.Examiner), Clean(info.Notes),
            Clean(model ?? string.Empty), Clean(serial ?? string.Empty), Clean(version), Clean(os),
            acquired.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), system.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "0", string.Empty,
        ];
        return "3\nmain\na\tc\tn\te\tt\tmd\tsn\tav\tov\tm\tu\tp\tdc\n" + string.Join('\t', values) + "\n\n"
            + "srce\n0\t1\np\tn\tid\tev\ttb\tlo\tpo\tah\tgu\taq\n0\t0\n\t\t\t\t\t-1\t-1\t\t\t\n\n"
            + "sub\n0\t1\np\tn\tid\tnu\tco\tgu\n0\t0\n\t\t\t\t1\t\n\n";
    }

    /// <summary>header / header2 の main の値を、項目名 → 値で返す。壊れていれば空。</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i + 2 < lines.Length && i < 64; i++)
        {
            if (lines[i] == "main")
            {
                string[] names = lines[i + 1].Split('\t');
                string[] values = lines[i + 2].Split('\t');
                for (int k = 0; k < names.Length && k < values.Length; k++)
                {
                    result.TryAdd(names[k], values[k]);
                }

                break;
            }
        }

        return result;
    }

    /// <summary>header の日時 ("2026 10 7 3 15 42") または header2 の UNIX 時刻を UTC として解釈する。</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long unix))
        {
            return unix is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        }

        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 6 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            int[] n = [.. parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture))];
            try
            {
                return new DateTimeOffset(n[0], n[1], n[2], n[3], n[4], n[5], TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static string HeaderDate(DateTimeOffset t)
    {
        DateTime u = t.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{u.Year} {u.Month} {u.Day} {u.Hour} {u.Minute} {u.Second}");
    }

    /// <summary>タブ・改行は区切りなので空白にする。</summary>
    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
