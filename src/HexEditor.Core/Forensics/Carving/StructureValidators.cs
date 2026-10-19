using System.Text;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>
/// 構造の検証 (FOR-18 の仕様 2)。候補の位置から、ファイル形式の構造をたどってファイルの長さを求める。構造として正しくなければ null
/// (結果に含めない)。信頼できないデータを読むので、ループの回数と読む範囲は必ず有限 (<see cref="ByteWindow"/> の上限と各検証の回数の上限)。
/// </summary>
public interface IStructureValidator
{
    string Name { get; }

    /// <summary><paramref name="start"/> から始まる構造の長さ (バイト)。<paramref name="window"/> の <see cref="ByteWindow.Limit"/> を越えない。</summary>
    long? Measure(ByteWindow window, long start);
}

/// <summary>組み込みの構造の検証 (名前は定義の「構造の検証」に書く)。構造体テンプレート (07) の名前は <see cref="TemplateStructureValidator"/> が受け持つ。</summary>
public static class StructureValidators
{
    private static readonly Dictionary<string, IStructureValidator> Validators = new(StringComparer.OrdinalIgnoreCase);

    static StructureValidators()
    {
        foreach (IStructureValidator v in new IStructureValidator[]
        {
            new Native("png", Png), new Native("jpeg", Jpeg), new Native("gif", Gif), new Native("bmp", Bmp), new Native("tiff", Tiff), new Native("pdf", Pdf),
            new Native("zip", Zip), new Native("rar", Rar), new Native("7z", SevenZip), new Native("gzip", Gzip), new Native("bzip2", Bzip2), new Native("mp3", Mp3),
            new Native("mp4", Mp4), new Native("sqlite", Sqlite), new Native("pe", Pe), new Native("elf", Elf), new Native("ole", Ole), new Native("evtx", Evtx),
            new Native("lnk", Lnk), new Native("rtf", Rtf), new Native("html", Html), new Native("xml", Xml),
        })
        {
            Validators[v.Name] = v;
        }
    }

    private sealed class Native(string name, Func<ByteWindow, long, long?> measure) : IStructureValidator
    {
        public string Name { get; } = name;

        public long? Measure(ByteWindow window, long start)
        {
            try
            {
                return measure(window, start) is { } end && !window.Failed && end > start && end <= window.Limit ? end - start : null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>組み込みの検証の名前。</summary>
    public static IReadOnlyCollection<string> Names => Validators.Keys;

    public static IStructureValidator? Find(string name) => Validators.GetValueOrDefault(name);

    // 以下の検証は「終わりの絶対位置」を返す。

    private const int MaxLoop = 1 << 20;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static long? Png(ByteWindow w, long start)
    {
        if (!w.Matches(start, PngSignature))
        {
            return null;
        }

        long pos = start + 8;
        byte[] type = new byte[4];
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU32(pos, true, out uint length) || length > 0x7FFFFFFF || !w.TryRead(pos + 4, type) || type.Any(c => !char.IsAsciiLetter((char)c)))
            {
                return null;
            }

            long total = 12L + length;
            if (pos + total > w.Limit)
            {
                return null;
            }

            bool isHeader = type.AsSpan().SequenceEqual("IHDR"u8);
            if (i == 0 && (!isHeader || length != 13))
            {
                return null;
            }

            if (type.AsSpan().SequenceEqual("IEND"u8))
            {
                return length == 0 ? pos + total : null;
            }

            pos += total;
        }

        return null;
    }

    private static long? Jpeg(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0xFF, 0xD8, 0xFF]))
        {
            return null;
        }

        long pos = start + 2;
        bool frame = false;
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU8(pos, out byte ff) || ff != 0xFF)
            {
                return null;
            }

            byte marker;
            do
            {
                pos++;
                if (!w.TryU8(pos, out marker))
                {
                    return null;
                }
            }
            while (marker == 0xFF);

            pos++;
            if (marker == 0xD9)
            {
                return frame ? pos : null;
            }

            if (marker == 0x00 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker == 0xD8 || !w.TryU16(pos, true, out ushort length) || length < 2)
            {
                return null;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                frame = true;
            }

            pos += length;
            if (marker == 0xDA)
            {
                // エントロピー符号化データ: 次のマーカー (FF の後ろが 00 でも RSTn でもないもの) まで読み飛ばす。
                while (true)
                {
                    if (!w.TryU8(pos, out byte b))
                    {
                        return null;
                    }

                    if (b == 0xFF)
                    {
                        if (!w.TryU8(pos + 1, out byte next))
                        {
                            return null;
                        }

                        if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                        {
                            pos += 2;
                            continue;
                        }

                        if (next == 0xFF)
                        {
                            pos++;
                            continue;
                        }

                        break;
                    }

                    pos++;
                }
            }
        }

        return null;
    }

    private static long? Gif(ByteWindow w, long start)
    {
        if (!w.Matches(start, "GIF8"u8) || !w.TryU8(start + 4, out byte v) || v is not ((byte)'7' or (byte)'9') || !w.TryU8(start + 5, out byte a) || a != (byte)'a')
        {
            return null;
        }

        if (!w.TryU8(start + 10, out byte packed))
        {
            return null;
        }

        long pos = start + 13;
        if ((packed & 0x80) != 0)
        {
            pos += 3L << ((packed & 7) + 1);
        }

        int images = 0;
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU8(pos, out byte block))
            {
                return null;
            }

            pos++;
            switch (block)
            {
                case 0x3B:
                    return images > 0 ? pos : null;
                case 0x21:
                    pos++;
                    pos = SubBlocks(w, pos);
                    if (pos < 0)
                    {
                        return null;
                    }

                    break;
                case 0x2C:
                    {
                        if (!w.TryU8(pos + 8, out byte flags))
                        {
                            return null;
                        }

                        pos += 9;
                        if ((flags & 0x80) != 0)
                        {
                            pos += 3L << ((flags & 7) + 1);
                        }

                        pos++;
                        pos = SubBlocks(w, pos);
                        if (pos < 0)
                        {
                            return null;
                        }

                        images++;
                        break;
                    }

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>GIF のサブブロックの列 (長さ 0 のブロックで終わる)。終わりの位置、壊れていれば -1。</summary>
    private static long SubBlocks(ByteWindow w, long pos)
    {
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU8(pos, out byte size))
            {
                return -1;
            }

            pos += 1 + size;
            if (size == 0)
            {
                return pos;
            }
        }

        return -1;
    }

    private static long? Bmp(ByteWindow w, long start)
    {
        if (!w.Matches(start, "BM"u8) || !w.TryU32(start + 2, false, out uint size) || !w.TryU32(start + 10, false, out uint pixels) || !w.TryU32(start + 14, false, out uint dib))
        {
            return null;
        }

        if (dib is not (12 or 40 or 52 or 56 or 64 or 108 or 124) || size < 14 + dib || pixels < 14 + dib || pixels > size)
        {
            return null;
        }

        if (dib >= 40)
        {
            if (!w.TryU32(start + 18, false, out uint width) || !w.TryU32(start + 22, false, out uint height) || !w.TryU16(start + 26, false, out ushort planes)
                || !w.TryU16(start + 28, false, out ushort bpp))
            {
                return null;
            }

            int h = (int)height;
            if (width is 0 or > 65535 || h == 0 || Math.Abs((long)h) > 65535 || planes != 1 || bpp is not (1 or 4 or 8 or 16 or 24 or 32))
            {
                return null;
            }
        }

        return start + size;
    }

    private static long? Tiff(ByteWindow w, long start)
    {
        bool big;
        if (w.Matches(start, [0x49, 0x49, 0x2A, 0x00]))
        {
            big = false;
        }
        else if (w.Matches(start, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            big = true;
        }
        else
        {
            return null;
        }

        if (!w.TryU32(start + 4, big, out uint firstIfd) || firstIfd < 8)
        {
            return null;
        }

        int[] typeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];
        long end = 8;
        var queue = new Queue<long>();
        var seen = new HashSet<long>();
        queue.Enqueue(firstIfd);
        int ifds = 0;
        while (queue.Count > 0 && ifds < 64)
        {
            long ifd = queue.Dequeue();
            if (!seen.Add(ifd) || start + ifd + 2 > w.Limit)
            {
                continue;
            }

            if (!w.TryU16(start + ifd, big, out ushort count) || count == 0 || count > 4096)
            {
                return null;
            }

            ifds++;
            end = Math.Max(end, ifd + 2 + 12L * count + 4);
            var offsets = new List<(long Offset, int Count, int Type)>();
            var counts = new List<(long Offset, int Count, int Type)>();
            for (int i = 0; i < count; i++)
            {
                long entry = start + ifd + 2 + 12L * i;
                if (!w.TryU16(entry, big, out ushort tag) || !w.TryU16(entry + 2, big, out ushort type) || !w.TryU32(entry + 4, big, out uint n))
                {
                    return null;
                }

                if (type is 0 or > 12)
                {
                    return null;
                }

                long bytes = (long)typeSize[type] * n;
                long valueAt = entry + 8;
                if (bytes > 4)
                {
                    if (!w.TryU32(entry + 8, big, out uint off) || start + off + bytes > w.Limit)
                    {
                        return null;
                    }

                    end = Math.Max(end, off + bytes);
                    valueAt = start + off;
                }

                switch (tag)
                {
                    case 273 or 324:
                        offsets.Add((valueAt, (int)Math.Min(n, 100_000), type));
                        break;
                    case 279 or 325:
                        counts.Add((valueAt, (int)Math.Min(n, 100_000), type));
                        break;
                    case 34665 or 34853 or 330 when type == 4 && n == 1:
                        if (w.TryU32(entry + 8, big, out uint sub))
                        {
                            queue.Enqueue(sub);
                        }

                        break;
                }
            }

            if (offsets.Count > 0 && counts.Count > 0)
            {
                (long Offset, int Count, int Type) o = offsets[0], c = counts[0];
                for (int i = 0; i < Math.Min(o.Count, c.Count); i++)
                {
                    if (!ReadValue(w, o.Offset, i, o.Type, big, out long so) || !ReadValue(w, c.Offset, i, c.Type, big, out long sc))
                    {
                        return null;
                    }

                    end = Math.Max(end, so + sc);
                }
            }

            if (!w.TryU32(start + ifd + 2 + 12L * count, big, out uint next))
            {
                return null;
            }

            if (next != 0)
            {
                queue.Enqueue(next);
            }
        }

        return ifds > 0 && start + end <= w.Limit ? start + end : null;
    }

    private static bool ReadValue(ByteWindow w, long at, int index, int type, bool big, out long value)
    {
        value = 0;
        if (type == 3)
        {
            bool ok = w.TryU16(at + 2L * index, big, out ushort v);
            value = v;
            return ok;
        }

        if (type == 4)
        {
            bool ok = w.TryU32(at + 4L * index, big, out uint v);
            value = v;
            return ok;
        }

        return false;
    }

    private static long? Pdf(ByteWindow w, long start)
    {
        if (!w.Matches(start, "%PDF-"u8))
        {
            return null;
        }

        // 線形化された PDF は /L にファイル全体の長さを持つ (最初の %%EOF は先頭の部分のものなので、そのまま使えない)。
        Span<byte> head = stackalloc byte[1024];
        int headLength = 0;
        for (; headLength < head.Length && w.TryU8(start + headLength, out head[headLength]); headLength++)
        {
        }

        bool linearized = false;
        if (headLength > 0)
        {
            string text = Encoding.Latin1.GetString(head[..headLength]);
            int lin = text.IndexOf("/Linearized", StringComparison.Ordinal);
            if (lin >= 0)
            {
                linearized = true;
                int l = text.IndexOf("/L ", lin, StringComparison.Ordinal);
                if (l >= 0)
                {
                    int p = l + 3;
                    long value = 0;
                    int digits = 0;
                    while (p < text.Length && text[p] is >= '0' and <= '9' && digits < 12)
                    {
                        value = value * 10 + (text[p] - '0');
                        p++;
                        digits++;
                    }

                    if (digits > 0 && value >= 20 && start + value <= w.Limit)
                    {
                        return start + value;
                    }
                }
            }
        }

        long pos = start + 5;
        int validEofs = 0;
        for (int i = 0; i < 1000; i++)
        {
            long q = w.Find("%%EOF"u8, pos, w.Limit);
            if (q < 0)
            {
                return null;
            }

            // 直前に startxref があること。
            long from = Math.Max(start, q - 1100);
            int span = (int)(q - from);
            byte[] before = new byte[span];
            if (!w.TryRead(from, before))
            {
                return null;
            }

            pos = q + 5;
            if (Encoding.Latin1.GetString(before).Contains("startxref", StringComparison.Ordinal))
            {
                validEofs++;
                if (linearized && validEofs < 2)
                {
                    continue;
                }

                long end = q + 5;
                if (w.TryU8(end, out byte c1) && c1 == '\r')
                {
                    end++;
                    if (w.TryU8(end, out byte c2) && c2 == '\n')
                    {
                        end++;
                    }
                }
                else if (c1 == '\n')
                {
                    end++;
                }

                return end;
            }
        }

        return null;
    }

    private static long? Zip(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0x50, 0x4B, 0x03, 0x04]))
        {
            return null;
        }

        long pos = start + 30;
        for (int i = 0; i < 64; i++)
        {
            long q = w.Find([0x50, 0x4B, 0x05, 0x06], pos, w.Limit);
            if (q < 0)
            {
                return null;
            }

            pos = q + 4;
            if (!w.TryU16(q + 4, false, out ushort disk) || !w.TryU16(q + 10, false, out ushort entries) || !w.TryU32(q + 12, false, out uint cdSize)
                || !w.TryU32(q + 16, false, out uint cdOffset) || !w.TryU16(q + 20, false, out ushort comment))
            {
                continue;
            }

            long cdEnd = q;
            ulong size = cdSize, offset = cdOffset;
            if (entries == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
            {
                // ZIP64: EOCD の直前のロケーターからたどる。
                if (!w.Matches(q - 20, [0x50, 0x4B, 0x06, 0x07]) || !w.TryU64(q - 12, false, out ulong eocd64) || eocd64 > (ulong)(w.Limit - start))
                {
                    continue;
                }

                long z = start + (long)eocd64;
                if (!w.Matches(z, [0x50, 0x4B, 0x06, 0x06]) || !w.TryU64(z + 40, false, out size) || !w.TryU64(z + 48, false, out offset))
                {
                    continue;
                }

                cdEnd = z;
            }

            if (disk != 0 || offset > (ulong)(w.Limit - start) || size > (ulong)(w.Limit - start) || start + (long)offset + (long)size > cdEnd)
            {
                continue;
            }

            if (size == 0 || !w.Matches(start + (long)offset, [0x50, 0x4B, 0x01, 0x02]))
            {
                continue;
            }

            long end = q + 22 + comment;
            return end <= w.Limit ? end : null;
        }

        return null;
    }

    private static long? Rar(ByteWindow w, long start)
    {
        if (w.Matches(start, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]))
        {
            return Rar4(w, start);
        }

        return w.Matches(start, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]) ? Rar5(w, start) : null;
    }

    private static long? Rar4(ByteWindow w, long start)
    {
        long pos = start + 7;
        long lastGood = 0;
        int blocks = 0;
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU8(pos + 2, out byte type) || !w.TryU16(pos + 3, false, out ushort flags) || !w.TryU16(pos + 5, false, out ushort size) || type is < 0x72 or > 0x7B || size < 7)
            {
                break;
            }

            long add = 0;
            if ((flags & 0x8000) != 0)
            {
                if (!w.TryU32(pos + 7, false, out uint a))
                {
                    break;
                }

                add = a;
            }

            long next = pos + size + add;
            if (next > w.Limit)
            {
                break;
            }

            blocks++;
            lastGood = next;
            pos = next;
            if (type == 0x7B)
            {
                return pos;
            }
        }

        return blocks >= 2 ? lastGood : null;
    }

    private static bool Vint(ByteWindow w, ref long pos, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (!w.TryU8(pos++, out byte b))
            {
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static long? Rar5(ByteWindow w, long start)
    {
        long pos = start + 8;
        long lastGood = 0;
        int blocks = 0;
        for (int i = 0; i < MaxLoop; i++)
        {
            long p = pos + 4;
            if (!Vint(w, ref p, out ulong headSize) || headSize is 0 or > 1 << 20)
            {
                break;
            }

            long headerStart = p;
            if (!Vint(w, ref p, out ulong type) || !Vint(w, ref p, out ulong flags))
            {
                break;
            }

            if ((flags & 1) != 0 && !Vint(w, ref p, out _))
            {
                break;
            }

            ulong data = 0;
            if ((flags & 2) != 0 && !Vint(w, ref p, out data))
            {
                break;
            }

            long next = headerStart + (long)headSize + (long)Math.Min(data, (ulong)w.Limit);
            if (type > 5 || next > w.Limit)
            {
                break;
            }

            blocks++;
            lastGood = next;
            pos = next;
            if (type == 5)
            {
                return pos;
            }
        }

        return blocks >= 2 ? lastGood : null;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static readonly uint[] Crc32Table = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static long? SevenZip(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) || !w.TryU32(start + 8, false, out uint headerCrc) || !w.TryU64(start + 12, false, out ulong nextOffset)
            || !w.TryU64(start + 20, false, out ulong nextSize))
        {
            return null;
        }

        Span<byte> startHeader = stackalloc byte[20];
        if (!w.TryRead(start + 12, startHeader) || Crc32(startHeader) != headerCrc)
        {
            return null;
        }

        ulong total = 32UL + nextOffset + nextSize;
        if (nextOffset > (ulong)w.Limit || nextSize > (ulong)w.Limit || total > (ulong)(w.Limit - start))
        {
            return null;
        }

        return start + (long)total;
    }

    private static long? Gzip(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0x1F, 0x8B, 0x08]) || !w.TryU8(start + 3, out byte flags) || (flags & 0xE0) != 0)
        {
            return null;
        }

        long pos = start + 10;
        if ((flags & 4) != 0)
        {
            if (!w.TryU16(pos, false, out ushort extra))
            {
                return null;
            }

            pos += 2 + extra;
        }

        foreach (int bit in new[] { 8, 16 })
        {
            if ((flags & bit) == 0)
            {
                continue;
            }

            for (int i = 0; ; i++)
            {
                if (i > 4096 || !w.TryU8(pos++, out byte c))
                {
                    return null;
                }

                if (c == 0)
                {
                    break;
                }
            }
        }

        if ((flags & 2) != 0)
        {
            pos += 2;
        }

        long? end = DeflateSkipper.FindEnd(w, pos);
        return end is { } e && e + 8 <= w.Limit ? e + 8 : null;
    }

    private static long? Bzip2(ByteWindow w, long start)
    {
        if (!w.Matches(start, "BZh"u8) || !w.TryU8(start + 3, out byte level) || level is < (byte)'1' or > (byte)'9' || !w.Matches(start + 4, [0x31, 0x41, 0x59, 0x26, 0x53, 0x59]))
        {
            return null;
        }

        // ブロックの境界はビット単位なので、終端のマジック (0x177245385090) をビットごとに探す。
        ulong window = 0;
        long bitIndex = 0;
        for (long pos = start + 4; pos < w.Limit; pos++)
        {
            if (!w.TryU8(pos, out byte b))
            {
                return null;
            }

            for (int bit = 7; bit >= 0; bit--)
            {
                window = (window << 1) | (uint)((b >> bit) & 1);
                bitIndex++;
                if (bitIndex >= 48 && (window & 0xFFFFFFFFFFFFUL) == 0x177245385090UL)
                {
                    long end = start + 4 + (bitIndex + 32 + 7) / 8;
                    return end <= w.Limit ? end : null;
                }
            }
        }

        return null;
    }

    private static long? Mp3(ByteWindow w, long start)
    {
        if (!w.Matches(start, "ID3"u8) || !w.TryU8(start + 3, out byte major) || major is 0 or > 4 || !w.TryU8(start + 5, out byte flags)
            || !w.TryU8(start + 6, out byte s0) || !w.TryU8(start + 7, out byte s1) || !w.TryU8(start + 8, out byte s2) || !w.TryU8(start + 9, out byte s3)
            || ((s0 | s1 | s2 | s3) & 0x80) != 0)
        {
            return null;
        }

        long size = ((long)s0 << 21) | ((long)s1 << 14) | ((long)s2 << 7) | s3;
        long pos = start + 10 + size + ((flags & 0x10) != 0 ? 10 : 0);
        int frames = 0;
        int[,] bitrates =
        {
            { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 },
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 },
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 },
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 },
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },
        };
        int[][] rates = [[44100, 48000, 32000], [22050, 24000, 16000], [11025, 12000, 8000]];
        while (frames < MaxLoop)
        {
            if (!w.TryU32(pos, true, out uint h) || (h & 0xFFE00000) != 0xFFE00000)
            {
                break;
            }

            int version = (int)((h >> 19) & 3), layer = (int)((h >> 17) & 3), bitrateIndex = (int)((h >> 12) & 15), rateIndex = (int)((h >> 10) & 3), padding = (int)((h >> 9) & 1);
            if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            {
                break;
            }

            bool mpeg1 = version == 3;
            int row = mpeg1 ? 3 - layer : layer == 3 ? 3 : 4;
            int rate = rates[version == 3 ? 0 : version == 2 ? 1 : 2][rateIndex];
            int bitrate = bitrates[row, bitrateIndex] * 1000;
            if (bitrate == 0)
            {
                break;
            }

            long length = layer == 3 ? (12L * bitrate / rate + padding) * 4 : (mpeg1 || layer == 2 ? 144L : 72L) * bitrate / rate + padding;
            if (pos + length > w.Limit)
            {
                break;
            }

            pos += length;
            frames++;
        }

        if (frames < 8)
        {
            return null;
        }

        if (w.Matches(pos, "TAG"u8) && pos + 128 <= w.Limit)
        {
            pos += 128;
        }

        return pos;
    }

    private static readonly HashSet<string> Mp4Boxes = new(StringComparer.Ordinal)
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "pnot", "moof", "mfra", "styp", "sidx", "ssix", "prft", "meta", "pdin", "mvex", "emsg",
    };

    private static long? Mp4(ByteWindow w, long start)
    {
        long pos = start;
        long lastGood = 0;
        bool sawData = false;
        for (int i = 0; i < MaxLoop; i++)
        {
            if (!w.TryU32(pos, true, out uint size) || !w.TryU32(pos + 4, true, out uint typeValue))
            {
                break;
            }

            string type = new([(char)(typeValue >> 24), (char)((typeValue >> 16) & 0xFF), (char)((typeValue >> 8) & 0xFF), (char)(typeValue & 0xFF)]);
            if (i == 0 ? type != "ftyp" : !Mp4Boxes.Contains(type))
            {
                break;
            }

            long length = size;
            if (size == 1)
            {
                if (!w.TryU64(pos + 8, true, out ulong large) || large > (ulong)w.Limit)
                {
                    break;
                }

                length = (long)large;
            }
            else if (size == 0)
            {
                length = w.Limit - pos;
            }

            if (length < 8 || pos + length > w.Limit)
            {
                break;
            }

            pos += length;
            lastGood = pos;
            sawData |= type is "moov" or "mdat" or "moof";
        }

        return sawData ? lastGood : null;
    }

    private static long? Sqlite(ByteWindow w, long start)
    {
        if (!w.Matches(start, "SQLite format 3\0"u8) || !w.TryU16(start + 16, true, out ushort pageSizeField) || !w.TryU32(start + 28, true, out uint pages)
            || !w.TryU32(start + 24, true, out uint changeCounter) || !w.TryU32(start + 92, true, out uint validFor))
        {
            return null;
        }

        long pageSize = pageSizeField == 1 ? 65536 : pageSizeField;
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0 || pages == 0 || changeCounter != validFor)
        {
            return null;
        }

        return start + pages * pageSize;
    }

    private static long? Pe(ByteWindow w, long start)
    {
        if (!w.Matches(start, "MZ"u8) || !w.TryU32(start + 0x3C, false, out uint pe) || pe < 0x40 || pe > 0x100000 || !w.Matches(start + pe, [0x50, 0x45, 0, 0])
            || !w.TryU16(start + pe + 6, false, out ushort sections) || sections is 0 or > 96 || !w.TryU16(start + pe + 20, false, out ushort optionalSize)
            || !w.TryU16(start + pe + 24, false, out ushort magic) || magic is not (0x10B or 0x20B))
        {
            return null;
        }

        long end = 0;
        if (w.TryU32(start + pe + 24 + 60, false, out uint headers))
        {
            end = headers;
        }

        long table = start + pe + 24 + optionalSize;
        for (int i = 0; i < sections; i++)
        {
            if (!w.TryU32(table + 40L * i + 16, false, out uint rawSize) || !w.TryU32(table + 40L * i + 20, false, out uint rawPointer))
            {
                return null;
            }

            if (rawSize > 0)
            {
                end = Math.Max(end, (long)rawPointer + rawSize);
            }
        }

        // 証明書テーブル (データディレクトリの 4 番目。ファイルのオフセットで指定される)。
        long directories = start + pe + 24 + (magic == 0x20B ? 112 : 96);
        if (w.TryU32(directories + 4 * 8, false, out uint certOffset) && w.TryU32(directories + 4 * 8 + 4, false, out uint certSize) && certOffset > 0 && certSize > 0)
        {
            end = Math.Max(end, (long)certOffset + certSize);
        }

        return end > 0x40 && start + end <= w.Limit ? start + end : null;
    }

    private static long? Elf(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0x7F, 0x45, 0x4C, 0x46]) || !w.TryU8(start + 4, out byte cls) || cls is not (1 or 2) || !w.TryU8(start + 5, out byte data) || data is not (1 or 2))
        {
            return null;
        }

        bool big = data == 2, is64 = cls == 2;
        int phoffAt = is64 ? 0x20 : 0x1C, shoffAt = is64 ? 0x28 : 0x20, phentAt = is64 ? 0x36 : 0x2A;
        if (!UIntN(w, start + phoffAt, is64, big, out ulong phoff) || !UIntN(w, start + shoffAt, is64, big, out ulong shoff)
            || !w.TryU16(start + phentAt, big, out ushort phentsize) || !w.TryU16(start + phentAt + 2, big, out ushort phnum)
            || !w.TryU16(start + phentAt + 4, big, out ushort shentsize) || !w.TryU16(start + phentAt + 6, big, out ushort shnum))
        {
            return null;
        }

        if ((phnum > 0 && phentsize < (is64 ? 56 : 32)) || (shnum > 0 && shentsize < (is64 ? 64 : 40)))
        {
            return null;
        }

        ulong end = 0;
        for (int i = 0; i < Math.Min(shnum, (ushort)4096); i++)
        {
            long header = start + (long)shoff + (long)i * shentsize;
            if (!w.TryU32(header + 4, big, out uint type) || !UIntN(w, header + (is64 ? 0x18 : 0x10), is64, big, out ulong offset)
                || !UIntN(w, header + (is64 ? 0x20 : 0x14), is64, big, out ulong size))
            {
                return null;
            }

            if (type != 8 && offset + size > end && offset + size < (ulong)w.Limit)
            {
                end = offset + size;
            }
        }

        for (int i = 0; i < Math.Min(phnum, (ushort)4096); i++)
        {
            long header = start + (long)phoff + (long)i * phentsize;
            if (!UIntN(w, header + (is64 ? 8 : 4), is64, big, out ulong offset) || !UIntN(w, header + (is64 ? 0x20 : 0x10), is64, big, out ulong size))
            {
                return null;
            }

            if (offset + size > end && offset + size < (ulong)w.Limit)
            {
                end = offset + size;
            }
        }

        if (shnum > 0)
        {
            end = Math.Max(end, shoff + (ulong)shnum * shentsize);
        }

        if (phnum > 0)
        {
            end = Math.Max(end, phoff + (ulong)phnum * phentsize);
        }

        return end > 0x34 && start + (long)end <= w.Limit ? start + (long)end : null;
    }

    private static bool UIntN(ByteWindow w, long at, bool is64, bool big, out ulong value)
    {
        if (is64)
        {
            return w.TryU64(at, big, out value);
        }

        bool ok = w.TryU32(at, big, out uint v);
        value = v;
        return ok;
    }

    private static long? Ole(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) || !w.TryU16(start + 0x1A, false, out ushort major) || !w.TryU16(start + 0x1E, false, out ushort shift)
            || !w.TryU32(start + 0x2C, false, out uint fatCount) || !w.TryU32(start + 0x44, false, out uint difat) || !w.TryU32(start + 0x48, false, out uint difatCount))
        {
            return null;
        }

        if ((major == 3 && shift != 9) || (major == 4 && shift != 12) || major is not (3 or 4) || fatCount is 0 or > 8192 || difatCount > 64)
        {
            return null;
        }

        long sector = 1L << shift;
        var fat = new List<uint>();
        for (int i = 0; i < Math.Min(fatCount, 109u); i++)
        {
            if (!w.TryU32(start + 0x4C + 4L * i, false, out uint id))
            {
                return null;
            }

            fat.Add(id);
        }

        uint next = difat;
        for (uint d = 0; d < difatCount && fat.Count < fatCount; d++)
        {
            if (next >= 0xFFFFFFFA)
            {
                break;
            }

            long at = start + (next + 1L) * sector;
            for (int i = 0; i < sector / 4 - 1 && fat.Count < fatCount; i++)
            {
                if (!w.TryU32(at + 4L * i, false, out uint id))
                {
                    return null;
                }

                fat.Add(id);
            }

            if (!w.TryU32(at + sector - 4, false, out next))
            {
                return null;
            }
        }

        long highest = 0;
        for (int f = 0; f < fat.Count; f++)
        {
            long fatAt = start + (fat[f] + 1L) * sector;
            if (fat[f] >= 0xFFFFFFFA || fatAt + sector > w.Limit)
            {
                return null;
            }

            for (int i = 0; i < sector / 4; i++)
            {
                if (!w.TryU32(fatAt + 4L * i, false, out uint entry))
                {
                    return null;
                }

                if (entry != 0xFFFFFFFF)
                {
                    highest = Math.Max(highest, f * (sector / 4) + i);
                }
            }
        }

        long end = start + (highest + 2) * sector;
        return end <= w.Limit ? end : null;
    }

    private static long? Evtx(ByteWindow w, long start)
    {
        if (!w.Matches(start, "ElfFile\0"u8) || !w.TryU32(start + 32, false, out uint headerSize) || headerSize != 128 || !w.TryU16(start + 38, false, out ushort major) || major != 3
            || !w.TryU16(start + 40, false, out ushort block) || block != 4096 || !w.TryU16(start + 42, false, out ushort chunks) || chunks == 0)
        {
            return null;
        }

        long end = start + 4096 + 65536L * chunks;
        return end <= w.Limit && w.Matches(start + 4096, "ElfChnk\0"u8) ? end : null;
    }

    private static long? Lnk(ByteWindow w, long start)
    {
        if (!w.Matches(start, [0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46]) || !w.TryU32(start + 0x14, false, out uint flags))
        {
            return null;
        }

        long pos = start + 0x4C;
        if ((flags & 1) != 0)
        {
            if (!w.TryU16(pos, false, out ushort idList))
            {
                return null;
            }

            pos += 2 + idList;
        }

        if ((flags & 2) != 0)
        {
            if (!w.TryU32(pos, false, out uint info) || info < 0x1C || info > 0x10000)
            {
                return null;
            }

            pos += info;
        }

        bool unicode = (flags & 0x80) != 0;
        foreach (int bit in new[] { 4, 8, 16, 32, 64 })
        {
            if ((flags & bit) == 0)
            {
                continue;
            }

            if (!w.TryU16(pos, false, out ushort chars))
            {
                return null;
            }

            pos += 2 + chars * (unicode ? 2L : 1L);
        }

        for (int i = 0; i < 64; i++)
        {
            if (!w.TryU32(pos, false, out uint block))
            {
                return pos <= w.Limit ? pos : null;
            }

            if (block < 4)
            {
                pos += 4;
                return pos <= w.Limit ? pos : null;
            }

            pos += block;
        }

        return null;
    }

    private static long? Rtf(ByteWindow w, long start)
    {
        if (!w.Matches(start, "{\\rtf"u8))
        {
            return null;
        }

        int depth = 0;
        for (long pos = start; pos < w.Limit; pos++)
        {
            if (!w.TryU8(pos, out byte b))
            {
                return null;
            }

            if (b == '\\')
            {
                pos++;
            }
            else if (b == '{')
            {
                depth++;
            }
            else if (b == '}' && --depth == 0)
            {
                return pos + 1;
            }
        }

        return null;
    }

    private static long? Html(ByteWindow w, long start)
    {
        // 先頭が <html または <!DOCTYPE html (大文字小文字は区別しない)。
        Span<byte> head = stackalloc byte[15];
        int n = 0;
        for (; n < head.Length && w.TryU8(start + n, out head[n]); n++)
        {
        }

        string text = Encoding.ASCII.GetString(head[..n]).ToLowerInvariant();
        if (!text.StartsWith("<html", StringComparison.Ordinal) && !text.StartsWith("<!doctype html", StringComparison.Ordinal))
        {
            return null;
        }

        // 終了タグは小文字か大文字の 2 通りを探し、先に見つかった方を使う。
        long lower = w.Find("</html>"u8, start, w.Limit), upper = w.Find("</HTML>"u8, start, w.Limit);
        long lt = lower < 0 ? upper : upper < 0 ? lower : Math.Min(lower, upper);
        if (lt < 0)
        {
            return null;
        }

        long end = lt + 7;
        if (w.TryU8(end, out byte c) && c == '\r')
        {
            end++;
            if (w.TryU8(end, out c) && c == '\n')
            {
                end++;
            }
        }
        else if (c == '\n')
        {
            end++;
        }

        return end;
    }

    private static long ScanByte(ByteWindow w, byte value, long from)
    {
        for (long p = from; p < w.Limit; p++)
        {
            if (!w.TryU8(p, out byte b))
            {
                return -1;
            }

            if (b == value)
            {
                return p;
            }
        }

        return -1;
    }

    private static long ScanSeq(ByteWindow w, ReadOnlySpan<byte> sequence, long from)
    {
        for (long p = from; p + sequence.Length <= w.Limit; p++)
        {
            if (!w.TryU8(p, out byte b))
            {
                return -1;
            }

            if (b == sequence[0] && w.Matches(p, sequence))
            {
                return p;
            }
        }

        return -1;
    }

    private static long? Xml(ByteWindow w, long start)
    {
        if (!w.Matches(start, "<?xml"u8))
        {
            return null;
        }

        long pos = start;
        int depth = 0;
        bool rootSeen = false;
        for (int i = 0; i < 4 * MaxLoop; i++)
        {
            long lt = ScanByte(w, (byte)'<', pos);
            if (lt < 0 || !w.TryU8(lt + 1, out byte c))
            {
                return null;
            }

            if (c == '?')
            {
                long e = ScanSeq(w, "?>"u8, lt + 2);
                if (e < 0)
                {
                    return null;
                }

                pos = e + 2;
            }
            else if (c == '!')
            {
                if (w.Matches(lt, "<!--"u8))
                {
                    long e = ScanSeq(w, "-->"u8, lt + 4);
                    if (e < 0)
                    {
                        return null;
                    }

                    pos = e + 3;
                }
                else if (w.Matches(lt, "<![CDATA["u8))
                {
                    long e = ScanSeq(w, "]]>"u8, lt + 9);
                    if (e < 0)
                    {
                        return null;
                    }

                    pos = e + 3;
                }
                else
                {
                    // DOCTYPE (内部サブセットの [ ] を含む)。
                    long p = lt + 2;
                    int bracket = 0;
                    while (true)
                    {
                        if (!w.TryU8(p, out byte b))
                        {
                            return null;
                        }

                        p++;
                        if (b == '[')
                        {
                            bracket++;
                        }
                        else if (b == ']')
                        {
                            bracket--;
                        }
                        else if (b == '>' && bracket <= 0)
                        {
                            break;
                        }
                    }

                    pos = p;
                }
            }
            else if (c == '/')
            {
                long e = ScanByte(w, (byte)'>', lt + 2);
                if (e < 0 || --depth < 0)
                {
                    return null;
                }

                pos = e + 1;
                if (depth == 0 && rootSeen)
                {
                    return pos;
                }
            }
            else if (char.IsLetter((char)c) || c == '_' || c == ':')
            {
                long p = lt + 1;
                byte quote = 0;
                byte prev = 0;
                while (true)
                {
                    if (!w.TryU8(p, out byte b))
                    {
                        return null;
                    }

                    p++;
                    if (quote != 0)
                    {
                        if (b == quote)
                        {
                            quote = 0;
                        }
                    }
                    else if (b is (byte)'"' or (byte)'\'')
                    {
                        quote = b;
                    }
                    else if (b == '>')
                    {
                        break;
                    }

                    prev = b;
                }

                rootSeen = true;
                if (prev != '/')
                {
                    depth++;
                }
                else if (depth == 0)
                {
                    return p;
                }

                pos = p;
            }
            else
            {
                return null;
            }
        }

        return null;
    }
}
