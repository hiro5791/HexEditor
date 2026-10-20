using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Volumes;

/// <summary>LVM2 の物理ボリューム (メタデータの physical_volumes の 1 つ)。位置はセクタ数。</summary>
public sealed record LvmPhysicalVolume(string Name, string Id, long PeStart, long PeCount, long DeviceSize);

/// <summary>LVM2 のセグメント (論理ボリュームの区間)。<see cref="Stripes"/> は (物理ボリュームの名前、その中の開始エクステント)。</summary>
public sealed record LvmSegment(long StartExtent, long ExtentCount, string Type, int StripeCount, long StripeSize, IReadOnlyList<(string Pv, long Extent)> Stripes);

/// <summary>LVM2 の論理ボリューム。</summary>
public sealed record LvmLogicalVolume(string Name, string Id, IReadOnlyList<string> Status, IReadOnlyList<LvmSegment> Segments)
{
    /// <summary>LVM が内部で使う LV (VISIBLE のないもの。thin pool のメタデータなど)。</summary>
    public bool Hidden => !Status.Contains("VISIBLE");
}

/// <summary>LVM2 のボリュームグループ (テキストのメタデータ。FOR-07 の仕様 2)。エクステントの大きさはセクタ数。</summary>
public sealed record LvmVolumeGroup(string Name, string Id, long Sequence, long ExtentSize, IReadOnlyList<LvmPhysicalVolume> PhysicalVolumes,
    IReadOnlyList<LvmLogicalVolume> LogicalVolumes);

/// <summary>
/// LVM2 の物理ボリュームのラベル (<c>LABELONE</c>) と PV ヘッダー、メタデータ領域 (<c> LVM2 x[5A%r0N*&gt;</c>) の最新のテキストのメタデータを読む
/// (FOR-07 の仕様 2)。チェックサム (LVM2 の calc_crc) が合わないものは警告にして続ける。読むのはメタデータ領域 (1 MiB まで) だけ。
/// </summary>
public sealed class Lvm2PhysicalVolumeLabel
{
    public const uint InitialCrc = 0xF597A6CF;
    public const int MaxMetadata = 1 << 20;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    /// <summary>LVM2 の calc_crc (反射の CRC32、最後の反転なし)。</summary>
    public static uint Crc(ReadOnlySpan<byte> data, uint seed = InitialCrc)
    {
        uint c = seed;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    private Lvm2PhysicalVolumeLabel()
    {
    }

    /// <summary>PV の UUID (ダッシュを除いた 32 文字)。</summary>
    public string PvUuid { get; private init; } = string.Empty;
    public long DeviceSize { get; private init; }
    public long LabelOffset { get; private init; }
    public IReadOnlyList<(long Offset, long Size)> DataAreas { get; private init; } = [];
    public IReadOnlyList<(long Offset, long Size)> MetadataAreas { get; private init; } = [];
    public string? MetadataText { get; private init; }
    public LvmVolumeGroup? VolumeGroup { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    /// <summary>ダッシュを除いて大文字小文字をそろえた UUID (比較用)。</summary>
    public static string Normalize(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>LVM2 の書式の UUID (6-4-4-4-4-4-6)。</summary>
    public static string Format(string uuid)
    {
        string u = Normalize(uuid);
        if (u.Length != 32)
        {
            return u;
        }

        return string.Join('-', u[..6], u[6..10], u[10..14], u[14..18], u[18..22], u[22..26], u[26..]);
    }

    /// <summary>先頭 4 セクタからラベルを探して読む。なければ null。</summary>
    public static Lvm2PhysicalVolumeLabel? Read(IVolumeReader device)
    {
        for (int s = 0; s < 4; s++)
        {
            byte[] label = device.ReadBytes(s * 512L, 512, out bool complete);
            if (complete && label.AsSpan(0, 8).SequenceEqual("LABELONE"u8))
            {
                return Parse(device, label, s * 512L);
            }
        }

        return null;
    }

    private static Lvm2PhysicalVolumeLabel? Parse(IVolumeReader device, byte[] label, long labelOffset)
    {
        var warnings = new List<string>();
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(label.AsSpan(16));
        if (Crc(label.AsSpan(20)) != crc)
        {
            warnings.Add("label CRC");
        }

        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(label.AsSpan(20));
        if (offset is < 32 or > 400 || !label.AsSpan(24, 8).SequenceEqual("LVM2 001"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> pv = label.AsSpan(offset);
        string uuid = Encoding.ASCII.GetString(pv[..32]);
        long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(pv[32..]), long.MaxValue);
        int at = 40;
        var data = new List<(long, long)>();
        var meta = new List<(long, long)>();
        foreach (List<(long, long)> list in new[] { data, meta })
        {
            for (int guard = 0; guard < 16 && at + 16 <= pv.Length; guard++)
            {
                long o = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(pv[at..]), long.MaxValue / 2);
                long l = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(pv[(at + 8)..]), long.MaxValue / 2);
                at += 16;
                if (o == 0)
                {
                    break;
                }

                list.Add((o, l));
            }
        }

        string? text = null;
        long bestSeq = -1;
        LvmVolumeGroup? vg = null;
        foreach ((long mdaOffset, long mdaSize) in meta)
        {
            if (ReadMetadata(device, mdaOffset, mdaSize, warnings) is not { } t)
            {
                continue;
            }

            LvmVolumeGroup? parsed = LvmText.ParseVolumeGroup(t, warnings);
            if (parsed is not null && parsed.Sequence > bestSeq)
            {
                (text, vg, bestSeq) = (t, parsed, parsed.Sequence);
            }
        }

        return new Lvm2PhysicalVolumeLabel
        {
            PvUuid = uuid,
            DeviceSize = size,
            LabelOffset = labelOffset,
            DataAreas = data,
            MetadataAreas = meta,
            MetadataText = text,
            VolumeGroup = vg,
            Warnings = warnings,
        };
    }

    private static string? ReadMetadata(IVolumeReader device, long mdaOffset, long mdaSize, List<string> warnings)
    {
        byte[] header = device.ReadBytes(mdaOffset, 512, out bool complete);
        if (!complete || !header.AsSpan(4, 16).SequenceEqual(" LVM2 x[5A%r0N*>"u8))
        {
            warnings.Add("metadata area header");
            return null;
        }

        if (Crc(header.AsSpan(4, 508)) != BinaryPrimitives.ReadUInt32LittleEndian(header))
        {
            warnings.Add("metadata area header CRC");
        }

        long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)), (ulong)mdaSize);
        long textOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40)), long.MaxValue / 2);
        long textSize = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48)), MaxMetadata);
        uint textCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56));
        if (textOffset <= 0 || textSize <= 0 || size <= 512 || textOffset >= size)
        {
            return null;
        }

        // 循環するバッファ: 末尾を越えた分はヘッダーの直後から続く。
        byte[] text = new byte[textSize];
        long first = Math.Min(textSize, size - textOffset);
        device.Read(mdaOffset + textOffset, text.AsSpan(0, (int)first));
        if (first < textSize)
        {
            device.Read(mdaOffset + 512, text.AsSpan((int)first));
        }

        if (Crc(text) != textCrc)
        {
            warnings.Add("metadata text CRC");
        }

        int nul = Array.IndexOf(text, (byte)0);
        return Encoding.UTF8.GetString(text, 0, nul >= 0 ? nul : text.Length);
    }
}

/// <summary>LVM2 のテキストのメタデータ (<c>名前 = 値</c> と <c>名前 { … }</c> の入れ子) の読み方。入れ子の深さと字句の数に上限を設ける。</summary>
public static class LvmText
{
    public const int MaxDepth = 32;
    public const int MaxTokens = 1_000_000;

    /// <summary>節 (入れ子の名前付きの値)。値は string・long・List&lt;object&gt;・Section。</summary>
    public sealed class Section
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        public List<(string Name, Section Section)> Children { get; } = [];

        public string? String(string key) => Values.GetValueOrDefault(key) as string;

        public long? Number(string key) => Values.GetValueOrDefault(key) is long l ? l : null;

        public List<object> List(string key) => Values.GetValueOrDefault(key) as List<object> ?? [];

        public Section? Child(string name) => Children.FirstOrDefault(c => c.Name == name).Section;
    }

    public static Section Parse(string text)
    {
        var tokens = Tokenize(text);
        int pos = 0;
        Section root = ParseBody(tokens, ref pos, 0);
        return root;
    }

    private enum Kind
    {
        Ident,
        String,
        Number,
        Symbol,
    }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < s.Length && list.Count < MaxTokens)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '"')
            {
                var b = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                    }

                    b.Append(s[i++]);
                }

                i++;
                list.Add(new Token(Kind.String, b.ToString()));
            }
            else if (c is '{' or '}' or '[' or ']' or '=' or ',')
            {
                list.Add(new Token(Kind.Symbol, c.ToString()));
                i++;
            }
            else if (char.IsAsciiDigit(c) || c == '-' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
            {
                int start = i++;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
                {
                    i++;
                }

                list.Add(new Token(Kind.Number, s[start..i]));
            }
            else
            {
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '[' or ']' or '=' or ',' or '"' or '#'))
                {
                    i++;
                }

                if (i == start)
                {
                    i++;
                    continue;
                }

                list.Add(new Token(Kind.Ident, s[start..i]));
            }
        }

        return list;
    }

    private static Section ParseBody(List<Token> t, ref int pos, int depth)
    {
        var section = new Section();
        while (pos < t.Count)
        {
            Token name = t[pos];
            if (name.Kind == Kind.Symbol && name.Text == "}")
            {
                pos++;
                return section;
            }

            if (name.Kind is not (Kind.Ident or Kind.String) || pos + 1 >= t.Count)
            {
                pos++;
                continue;
            }

            Token next = t[pos + 1];
            if (next.Text == "{" && next.Kind == Kind.Symbol)
            {
                pos += 2;
                if (depth >= MaxDepth)
                {
                    Skip(t, ref pos);
                    continue;
                }

                section.Children.Add((name.Text, ParseBody(t, ref pos, depth + 1)));
            }
            else if (next.Text == "=" && next.Kind == Kind.Symbol)
            {
                pos += 2;
                if (pos < t.Count)
                {
                    section.Values[name.Text] = ParseValue(t, ref pos);
                }
            }
            else
            {
                pos++;
            }
        }

        return section;
    }

    private static void Skip(List<Token> t, ref int pos)
    {
        int level = 1;
        while (pos < t.Count && level > 0)
        {
            if (t[pos].Kind == Kind.Symbol)
            {
                level += t[pos].Text == "{" ? 1 : t[pos].Text == "}" ? -1 : 0;
            }

            pos++;
        }
    }

    private static object ParseValue(List<Token> t, ref int pos)
    {
        Token v = t[pos++];
        if (v.Kind == Kind.Symbol && v.Text == "[")
        {
            var list = new List<object>();
            while (pos < t.Count && !(t[pos].Kind == Kind.Symbol && t[pos].Text == "]"))
            {
                if (t[pos].Kind == Kind.Symbol && t[pos].Text == ",")
                {
                    pos++;
                    continue;
                }

                Token item = t[pos++];
                list.Add(item.Kind == Kind.Number && long.TryParse(item.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : item.Text);
            }

            pos++;
            return list;
        }

        return v.Kind == Kind.Number && long.TryParse(v.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) ? number : v.Text;
    }

    /// <summary>ボリュームグループのメタデータ (最上位の節) を読む。成り立たなければ null。</summary>
    public static LvmVolumeGroup? ParseVolumeGroup(string text, List<string>? warnings = null)
    {
        Section root;
        try
        {
            root = Parse(text);
        }
        catch (ArgumentException)
        {
            warnings?.Add("metadata syntax");
            return null;
        }

        foreach ((string name, Section vg) in root.Children)
        {
            if (vg.String("id") is not { } id || vg.Child("physical_volumes") is not { } pvs)
            {
                continue;
            }

            var physical = new List<LvmPhysicalVolume>();
            foreach ((string pvName, Section pv) in pvs.Children)
            {
                physical.Add(new LvmPhysicalVolume(pvName, pv.String("id") ?? string.Empty, pv.Number("pe_start") ?? 0, pv.Number("pe_count") ?? 0,
                    pv.Number("dev_size") ?? 0));
            }

            var logical = new List<LvmLogicalVolume>();
            foreach ((string lvName, Section lv) in vg.Child("logical_volumes")?.Children ?? [])
            {
                var segments = new List<LvmSegment>();
                foreach ((string segName, Section seg) in lv.Children)
                {
                    if (!segName.StartsWith("segment", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    List<object> stripes = seg.List("stripes");
                    var pairs = new List<(string, long)>();
                    for (int i = 0; i + 1 < stripes.Count; i += 2)
                    {
                        if (stripes[i] is string pvRef && stripes[i + 1] is long extent)
                        {
                            pairs.Add((pvRef, extent));
                        }
                    }

                    segments.Add(new LvmSegment(seg.Number("start_extent") ?? 0, seg.Number("extent_count") ?? 0, seg.String("type") ?? string.Empty,
                        (int)(seg.Number("stripe_count") ?? pairs.Count), seg.Number("stripe_size") ?? 0, pairs));
                }

                segments.Sort((a, b) => a.StartExtent.CompareTo(b.StartExtent));
                logical.Add(new LvmLogicalVolume(lvName, lv.String("id") ?? string.Empty, [.. lv.List("status").OfType<string>()], segments));
            }

            return new LvmVolumeGroup(name, id, vg.Number("seqno") ?? 0, vg.Number("extent_size") ?? 0, physical, logical);
        }

        return null;
    }
}
