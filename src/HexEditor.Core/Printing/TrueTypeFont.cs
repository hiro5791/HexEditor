using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Printing;

/// <summary>
/// TrueType アウトラインのフォント (ttf、ttc の 1 つの書体)。PDF へのサブセットの埋め込み (TOOL-03 の仕様 2) と、文字幅・グリフ番号の問い合わせに使う。
/// CFF アウトラインの OpenType (otf) は <see cref="Load"/> で null を返す。サブセットはグリフ番号を変えず、使わないグリフの輪郭だけを空にする
/// (PDF の CID をそのままグリフ番号にできる)。
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly int _numberOfHMetrics;
    private readonly int _locaFormat;
    private readonly (int Platform, int Encoding, int Offset)[] _cmaps;
    private readonly Dictionary<int, int> _cmapCache = [];

    private TrueTypeFont(byte[] data, int faceOffset)
    {
        _data = data;
        int numTables = ReadU16(faceOffset + 4);
        for (int i = 0; i < numTables; i++)
        {
            int record = faceOffset + 12 + i * 16;
            string tag = Encoding.ASCII.GetString(data, record, 4);
            _tables[tag] = (ReadI32(record + 8), ReadI32(record + 12));
        }

        (int head, _) = _tables["head"];
        UnitsPerEm = ReadU16(head + 18);
        XMin = ReadI16(head + 36);
        YMin = ReadI16(head + 38);
        XMax = ReadI16(head + 40);
        YMax = ReadI16(head + 42);
        _locaFormat = ReadI16(head + 50);
        (int hhea, _) = _tables["hhea"];
        Ascent = ReadI16(hhea + 4);
        Descent = ReadI16(hhea + 6);
        _numberOfHMetrics = Math.Max(1, ReadU16(hhea + 34));
        NumGlyphs = ReadU16(_tables["maxp"].Offset + 4);
        if (_tables.TryGetValue("post", out var post) && post.Length >= 16)
        {
            IsFixedPitch = ReadI32(post.Offset + 12) != 0;
        }

        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && ReadU16(os2.Offset) >= 2)
        {
            CapHeight = ReadI16(os2.Offset + 88);
        }

        if (CapHeight <= 0)
        {
            CapHeight = Ascent;
        }

        var maps = new List<(int, int, int)>();
        if (_tables.TryGetValue("cmap", out var cmap))
        {
            int count = ReadU16(cmap.Offset + 2);
            for (int i = 0; i < count; i++)
            {
                int record = cmap.Offset + 4 + i * 8;
                maps.Add((ReadU16(record), ReadU16(record + 2), cmap.Offset + ReadI32(record + 4)));
            }
        }

        // 優先順: Unicode フル (3,10) → (0,*) の format 12 → (3,1)。
        _cmaps = [.. maps.OrderBy(m => (m.Item1, m.Item2) switch { (3, 10) => 0, (0, 4) or (0, 6) => 1, (3, 1) => 2, (0, _) => 3, _ => 9 }).Where(m => (m.Item1, m.Item2) is (3, 10) or (3, 1) or (0, _))];
        Names(out string family, out string subfamily);
        FamilyName = family;
        SubfamilyName = subfamily;
    }

    public int UnitsPerEm { get; }

    public int NumGlyphs { get; }

    public int XMin { get; }

    public int YMin { get; }

    public int XMax { get; }

    public int YMax { get; }

    public int Ascent { get; }

    public int Descent { get; }

    public int CapHeight { get; private set; }

    public bool IsFixedPitch { get; }

    public string FamilyName { get; private set; } = string.Empty;

    public string SubfamilyName { get; private set; } = string.Empty;

    /// <summary>書体の読み込み。TrueType アウトラインでなければ (または壊れていれば) null。ttc は <paramref name="faceIndex"/> 番目の書体。</summary>
    public static TrueTypeFont? Load(byte[] data, int faceIndex = 0)
    {
        try
        {
            if (data.Length < 12)
            {
                return null;
            }

            int offset = 0;
            if (data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f')
            {
                int faces = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8));
                if (faceIndex < 0 || faceIndex >= faces)
                {
                    return null;
                }

                offset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12 + faceIndex * 4));
            }
            else if (faceIndex != 0)
            {
                return null;
            }

            uint version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            if (version is not (0x00010000 or 0x74727565))
            {
                return null;
            }

            var font = new TrueTypeFont(data, offset);
            return font.HasTable("glyf") && font.HasTable("loca") && font.HasTable("hmtx") ? font : null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>ttc の書体の数 (ttc でなければ 1)。</summary>
    public static int FaceCount(byte[] data) =>
        data.Length >= 16 && data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f' ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8)) : 1;

    private bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>符号位置のグリフ番号 (なければ 0)。</summary>
    public int GlyphIndex(int codePoint)
    {
        if (_cmapCache.TryGetValue(codePoint, out int cached))
        {
            return cached;
        }

        int gid = 0;
        foreach ((_, _, int offset) in _cmaps)
        {
            gid = Lookup(offset, codePoint);
            if (gid != 0)
            {
                break;
            }
        }

        _cmapCache[codePoint] = gid;
        return gid;
    }

    private int Lookup(int offset, int codePoint)
    {
        int format = ReadU16(offset);
        switch (format)
        {
            case 4:
                {
                    if (codePoint > 0xFFFF)
                    {
                        return 0;
                    }

                    int segCount = ReadU16(offset + 6) / 2;
                    int endCodes = offset + 14;
                    int startCodes = endCodes + segCount * 2 + 2;
                    int deltas = startCodes + segCount * 2;
                    int rangeOffsets = deltas + segCount * 2;
                    int lo = 0;
                    int hi = segCount - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        int end = ReadU16(endCodes + mid * 2);
                        if (codePoint > end)
                        {
                            lo = mid + 1;
                            continue;
                        }

                        int start = ReadU16(startCodes + mid * 2);
                        if (codePoint < start)
                        {
                            hi = mid - 1;
                            continue;
                        }

                        int delta = ReadI16(deltas + mid * 2);
                        int rangeOffset = ReadU16(rangeOffsets + mid * 2);
                        if (rangeOffset == 0)
                        {
                            return (codePoint + delta) & 0xFFFF;
                        }

                        int glyphAt = rangeOffsets + mid * 2 + rangeOffset + (codePoint - start) * 2;
                        int glyph = ReadU16(glyphAt);
                        return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                    }

                    return 0;
                }

            case 12:
                {
                    int groups = ReadI32(offset + 12);
                    int lo = 0;
                    int hi = groups - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        int record = offset + 16 + mid * 12;
                        long start = (uint)ReadI32(record);
                        long end = (uint)ReadI32(record + 4);
                        if (codePoint < start)
                        {
                            hi = mid - 1;
                        }
                        else if (codePoint > end)
                        {
                            lo = mid + 1;
                        }
                        else
                        {
                            return (int)((uint)ReadI32(record + 8) + (codePoint - start));
                        }
                    }

                    return 0;
                }

            case 0:
                return codePoint is >= 0 and < 256 ? _data[offset + 6 + codePoint] : 0;
            default:
                return 0;
        }
    }

    /// <summary>グリフの送り幅 (フォント単位)。</summary>
    public int Advance(int glyph)
    {
        (int hmtx, _) = _tables["hmtx"];
        int index = Math.Min(glyph, _numberOfHMetrics - 1);
        return ReadU16(hmtx + index * 4);
    }

    /// <summary>グリフの送り幅 (em を 1 とした比)。</summary>
    public double AdvanceEm(int glyph) => (double)Advance(glyph) / UnitsPerEm;

    private (int Start, int End) GlyphRange(int glyph)
    {
        (int loca, _) = _tables["loca"];
        (int glyf, _) = _tables["glyf"];
        if (_locaFormat == 0)
        {
            return (glyf + ReadU16(loca + glyph * 2) * 2, glyf + ReadU16(loca + glyph * 2 + 2) * 2);
        }

        return (glyf + ReadI32(loca + glyph * 4), glyf + ReadI32(loca + glyph * 4 + 4));
    }

    /// <summary>
    /// 使うグリフだけの輪郭を持つフォントファイル (TrueType) を作る。グリフ番号は元のまま。合成グリフの部品も含める。
    /// 使わないグリフの輪郭は空にする。
    /// </summary>
    public byte[] Subset(IEnumerable<int> glyphs)
    {
        var keep = new HashSet<int> { 0 };
        var queue = new Queue<int>(glyphs.Where(g => g >= 0 && g < NumGlyphs));
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            if (!keep.Add(g))
            {
                continue;
            }

            foreach (int component in Components(g))
            {
                if (!keep.Contains(component))
                {
                    queue.Enqueue(component);
                }
            }
        }

        // glyf と loca (長い形式)。
        using var glyf = new MemoryStream();
        byte[] loca = new byte[(NumGlyphs + 1) * 4];
        for (int g = 0; g < NumGlyphs; g++)
        {
            BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(g * 4), (int)glyf.Length);
            if (keep.Contains(g))
            {
                (int start, int end) = GlyphRange(g);
                if (end > start)
                {
                    glyf.Write(_data, start, end - start);
                    while (glyf.Length % 4 != 0)
                    {
                        glyf.WriteByte(0);
                    }
                }
            }
        }

        BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(NumGlyphs * 4), (int)glyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string tag in new[] { "head", "hhea", "maxp", "hmtx", "cvt ", "fpgm", "prep" })
        {
            if (_tables.TryGetValue(tag, out var t))
            {
                tables[tag] = _data.AsSpan(t.Offset, t.Length).ToArray();
            }
        }

        tables["glyf"] = glyf.ToArray();
        tables["loca"] = loca;
        byte[] head = tables["head"];
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return Assemble(tables);
    }

    private IEnumerable<int> Components(int glyph)
    {
        (int start, int end) = GlyphRange(glyph);
        if (end - start < 10 || ReadI16(start) >= 0)
        {
            yield break;
        }

        int p = start + 10;
        while (p + 4 <= end)
        {
            int flags = ReadU16(p);
            yield return ReadU16(p + 2);
            p += 4 + ((flags & 0x1) != 0 ? 4 : 2);
            if ((flags & 0x8) != 0)
            {
                p += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                p += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                p += 8;
            }

            if ((flags & 0x20) == 0)
            {
                break;
            }
        }
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int searchRange = 1;
        int entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }

        int headerSize = 12 + count * 16;
        int total = headerSize;
        foreach (byte[] t in tables.Values)
        {
            total += (t.Length + 3) & ~3;
        }

        byte[] file = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(file, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)(searchRange * 16));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)(count * 16 - searchRange * 16));
        int record = 12;
        int offset = headerSize;
        int headOffset = 0;
        foreach ((string tag, byte[] data) in tables)
        {
            Encoding.ASCII.GetBytes(tag, file.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(record + 8), offset);
            BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(record + 12), data.Length);
            data.CopyTo(file, offset);
            if (tag == "head")
            {
                headOffset = offset;
            }

            record += 16;
            offset += (data.Length + 3) & ~3;
        }

        uint adjustment = unchecked(0xB1B0AFBAu - Checksum(file));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 8), adjustment);
        return file;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }

    private void Names(out string family, out string subfamily)
    {
        family = string.Empty;
        subfamily = string.Empty;
        if (!_tables.TryGetValue("name", out var name))
        {
            return;
        }

        int count = ReadU16(name.Offset + 2);
        int stringsAt = name.Offset + ReadU16(name.Offset + 4);
        string? typographicFamily = null;
        string? plainFamily = null;
        string? plainSubfamily = null;
        for (int i = 0; i < count; i++)
        {
            int record = name.Offset + 6 + i * 12;
            int platform = ReadU16(record);
            int nameId = ReadU16(record + 6);
            int length = ReadU16(record + 8);
            int offset = stringsAt + ReadU16(record + 10);
            if (nameId is not (1 or 2 or 16) || offset + length > _data.Length)
            {
                continue;
            }

            string? text = platform is 3 or 0 ? Encoding.BigEndianUnicode.GetString(_data, offset, length)
                : platform == 1 ? Encoding.Latin1.GetString(_data, offset, length) : null;
            if (text is null)
            {
                continue;
            }

            // Windows の英語 (0x409) を優先するが、なければ最初のものを使う。
            bool english = platform != 3 || ReadU16(record + 4) == 0x409;
            switch (nameId)
            {
                case 16 when typographicFamily is null || english:
                    typographicFamily = text;
                    break;
                case 1 when plainFamily is null || english:
                    plainFamily = text;
                    break;
                case 2 when plainSubfamily is null || english:
                    plainSubfamily = text;
                    break;
            }
        }

        family = typographicFamily ?? plainFamily ?? string.Empty;
        subfamily = plainSubfamily ?? string.Empty;
        if (typographicFamily is not null && plainFamily is not null)
        {
            // 太字・斜体の別を判定できるよう、基本の書体名 (name ID 1) も覚えておく。
            AlternativeFamily = plainFamily;
        }
    }

    /// <summary>name ID 1 の書体名 (typographic family と違う場合)。</summary>
    public string? AlternativeFamily { get; private set; }

    /// <summary>Regular (太字でも斜体でもない) か。</summary>
    public bool IsRegular => _tables.TryGetValue("head", out var head) && (ReadU16(head.Offset + 44) & 3) == 0;

    private int ReadU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset));

    private int ReadI16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset));

    private int ReadI32(int offset) => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(offset));
}
