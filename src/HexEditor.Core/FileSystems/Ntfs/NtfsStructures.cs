using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.FileSystems.Ntfs;

/// <summary>NTFS の属性の種類 (FOR-10 の仕様 3)。</summary>
public enum NtfsAttributeType : uint
{
    StandardInformation = 0x10,
    AttributeList = 0x20,
    FileName = 0x30,
    ObjectId = 0x40,
    SecurityDescriptor = 0x50,
    VolumeName = 0x60,
    VolumeInformation = 0x70,
    Data = 0x80,
    IndexRoot = 0x90,
    IndexAllocation = 0xA0,
    Bitmap = 0xB0,
    ReparsePoint = 0xC0,
    EaInformation = 0xD0,
    Ea = 0xE0,
    LoggedUtilityStream = 0x100,
    End = 0xFFFFFFFF,
}

/// <summary>データラン 1 つ (VCN の区間と LCN。<see cref="Lcn"/> が null ならスパース)。</summary>
public readonly record struct NtfsRun(long Vcn, long Length, long? Lcn);

/// <summary>属性 1 つ (常駐なら <see cref="Value"/>、非常駐ならランリスト)。</summary>
public sealed class NtfsAttribute
{
    public NtfsAttributeType Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool NonResident { get; init; }
    public ushort Flags { get; init; }
    public ushort Id { get; init; }

    /// <summary>レコードの中の属性の位置 (レコードの先頭から)。</summary>
    public int RecordOffset { get; init; }

    public int Length { get; init; }

    /// <summary>常駐の値 (非常駐なら空)。</summary>
    public byte[] Value { get; init; } = [];

    /// <summary>常駐の値のレコードの中の位置。</summary>
    public int ValueOffset { get; init; }

    public long StartVcn { get; init; }
    public long LastVcn { get; init; }
    public int CompressionUnit { get; init; }
    public long AllocatedSize { get; init; }
    public long RealSize { get; init; }
    public long InitializedSize { get; init; }
    public IReadOnlyList<NtfsRun> Runs { get; init; } = [];
    public bool RunListValid { get; init; } = true;

    /// <summary>この属性を持つ MFT レコードの番号 ($ATTRIBUTE_LIST の拡張レコードを含む)。</summary>
    public long RecordNumber { get; init; }

    public bool IsCompressed => (Flags & 0x0001) != 0 && CompressionUnit > 0;
    public bool IsEncrypted => (Flags & 0x4000) != 0;
    public bool IsSparse => (Flags & 0x8000) != 0;

    public long DataSize => NonResident ? RealSize : Value.Length;

    public override string ToString() => $"{Type} '{Name}' {(NonResident ? "non-resident" : "resident")}";
}

/// <summary>$FILE_NAME の値。</summary>
public sealed record NtfsFileName(long ParentRecord, ushort ParentSequence, long Created, long Modified, long MftModified, long Accessed,
    long AllocatedSize, long RealSize, uint Attributes, byte Namespace, string Name)
{
    public const byte Posix = 0, Win32 = 1, Dos = 2, Win32AndDos = 3;

    public static NtfsFileName? Parse(ReadOnlySpan<byte> v)
    {
        if (v.Length < 66)
        {
            return null;
        }

        int length = v[64];
        byte ns = v[65];
        if (66 + length * 2 > v.Length || ns > 3)
        {
            return null;
        }

        ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(v);
        return new NtfsFileName((long)(parent & 0xFFFFFFFFFFFF), (ushort)(parent >> 48),
            BinaryPrimitives.ReadInt64LittleEndian(v[8..]), BinaryPrimitives.ReadInt64LittleEndian(v[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(v[24..]), BinaryPrimitives.ReadInt64LittleEndian(v[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(v[40..]), BinaryPrimitives.ReadInt64LittleEndian(v[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(v[56..]), ns, Encoding.Unicode.GetString(v.Slice(66, length * 2)));
    }
}

/// <summary>$STANDARD_INFORMATION の値。</summary>
public sealed record NtfsStandardInformation(long Created, long Modified, long MftModified, long Accessed, uint Attributes, uint OwnerId,
    uint SecurityId, long Usn)
{
    public static NtfsStandardInformation? Parse(ReadOnlySpan<byte> v)
    {
        if (v.Length < 48)
        {
            return null;
        }

        return new NtfsStandardInformation(BinaryPrimitives.ReadInt64LittleEndian(v), BinaryPrimitives.ReadInt64LittleEndian(v[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(v[16..]), BinaryPrimitives.ReadInt64LittleEndian(v[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(v[32..]),
            v.Length >= 72 ? BinaryPrimitives.ReadUInt32LittleEndian(v[48..]) : 0, v.Length >= 72 ? BinaryPrimitives.ReadUInt32LittleEndian(v[52..]) : 0,
            v.Length >= 72 ? BinaryPrimitives.ReadInt64LittleEndian(v[64..]) : 0);
    }
}

/// <summary>MFT レコード (FILE。フィックスアップを適用した内容と、適用前の生データ)。</summary>
public sealed class NtfsRecord
{
    public long Number { get; init; }

    /// <summary>ボリューム上の位置 (MFT が断片化していればそのレコードの位置)。</summary>
    public long VolumeOffset { get; init; }

    /// <summary>フィックスアップを適用する前の生データ (TC-FOR-10-02 の「適用前の生データ」)。</summary>
    public byte[] Raw { get; init; } = [];

    /// <summary>フィックスアップを適用した内容。</summary>
    public byte[] Data { get; init; } = [];

    public bool HasSignature { get; init; }
    public bool FixupOk { get; init; }
    public ushort Sequence { get; init; }
    public ushort HardLinks { get; init; }
    public ushort Flags { get; init; }
    public int BytesInUse { get; init; }
    public long BaseRecord { get; init; }
    public ushort BaseSequence { get; init; }
    public ushort NextAttributeId { get; init; }
    public long LogSequenceNumber { get; init; }
    public List<NtfsAttribute> Attributes { get; } = [];
    public bool AttributesValid { get; set; } = true;

    public bool InUse => (Flags & 0x0001) != 0;
    public bool IsDirectory => (Flags & 0x0002) != 0;
    public bool IsValid => HasSignature && FixupOk;

    public IEnumerable<NtfsAttribute> Of(NtfsAttributeType type) => Attributes.Where(a => a.Type == type);
}

/// <summary>NTFS の構造の読み方 (フィックスアップ、属性、データラン、LZNT1)。</summary>
public static class NtfsFormat
{
    /// <summary>1 レコードの属性の上限 (壊れたレコードへの備え)。</summary>
    public const int MaxAttributes = 512;

    /// <summary>1 つの属性のデータランの上限。</summary>
    public const int MaxRuns = 1 << 20;

    /// <summary>
    /// 更新シーケンス配列 (フィックスアップ) を検証して適用する (FOR-10 の仕様 3)。各セクタの末尾 2 バイトが更新シーケンス番号と一致しなければ
    /// false (適用はできる範囲で行う)。
    /// </summary>
    public static bool ApplyFixup(Span<byte> record, int sectorSize = 512)
    {
        if (record.Length < 8)
        {
            return false;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 1 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * sectorSize > record.Length)
        {
            return false;
        }

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        bool ok = true;
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * sectorSize - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn)
            {
                ok = false;
                continue;
            }

            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }

        return ok;
    }

    /// <summary>MFT レコードを読む (フィックスアップの検証と適用、ヘッダー、属性)。</summary>
    public static NtfsRecord ParseRecord(long number, long volumeOffset, byte[] raw, int sectorSize)
    {
        byte[] data = (byte[])raw.Clone();
        bool signature = raw.Length >= 48 && raw.AsSpan(0, 4).SequenceEqual("FILE"u8);
        bool fixup = signature && ApplyFixup(data, sectorSize);
        var record = new NtfsRecord
        {
            Number = number,
            VolumeOffset = volumeOffset,
            Raw = raw,
            Data = data,
            HasSignature = signature,
            FixupOk = fixup,
            LogSequenceNumber = signature ? BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)) : 0,
            Sequence = signature ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16)) : (ushort)0,
            HardLinks = signature ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18)) : (ushort)0,
            Flags = signature ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22)) : (ushort)0,
            BytesInUse = signature ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)) : 0,
            BaseRecord = signature ? (long)(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)) & 0xFFFFFFFFFFFF) : 0,
            BaseSequence = signature ? (ushort)(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)) >> 48) : (ushort)0,
            NextAttributeId = signature ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(40)) : (ushort)0,
        };
        if (signature)
        {
            record.AttributesValid = ParseAttributes(record);
        }

        return record;
    }

    private static bool ParseAttributes(NtfsRecord record)
    {
        byte[] d = record.Data;
        int at = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(20));
        int limit = Math.Min(d.Length, Math.Max(record.BytesInUse, 0) is var used and > 0 ? used : d.Length);
        for (int n = 0; n < MaxAttributes; n++)
        {
            if (at < 24 || at + 8 > limit)
            {
                return false;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
            if (type == 0xFFFFFFFF)
            {
                return true;
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4));
            // length > limit - at: at + length はあふれうる (長さは信頼できない 32 bit の値)。
            if (length < 24 || length > limit - at || (length & 7) != 0 && length < 16)
            {
                return false;
            }

            NtfsAttribute? attribute = ParseAttribute(d.AsSpan(at, length), at, record.Number);
            if (attribute is null)
            {
                return false;
            }

            record.Attributes.Add(attribute);
            at += length;
        }

        return false;
    }

    /// <summary>属性 1 つ (ヘッダーと値・ランリスト)。長さが不正なら null。</summary>
    public static NtfsAttribute? ParseAttribute(ReadOnlySpan<byte> a, int recordOffset, long recordNumber)
    {
        if (a.Length < 16)
        {
            return null;
        }

        var type = (NtfsAttributeType)BinaryPrimitives.ReadUInt32LittleEndian(a);
        bool nonResident = a[8] != 0;
        int nameLength = a[9];
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[10..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(a[12..]);
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(a[14..]);
        string name = string.Empty;
        if (nameLength > 0)
        {
            if (nameOffset + nameLength * 2 > a.Length)
            {
                return null;
            }

            name = Encoding.Unicode.GetString(a.Slice(nameOffset, nameLength * 2));
        }

        if (!nonResident)
        {
            if (a.Length < 24)
            {
                return null;
            }

            int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[20..]);
            if (valueLength < 0 || valueOffset + (long)valueLength > a.Length)
            {
                return null;
            }

            return new NtfsAttribute
            {
                Type = type, Name = name, NonResident = false, Flags = flags, Id = id, RecordOffset = recordOffset, Length = a.Length,
                Value = a.Slice(valueOffset, valueLength).ToArray(), ValueOffset = recordOffset + valueOffset, RecordNumber = recordNumber,
            };
        }

        if (a.Length < 64)
        {
            return null;
        }

        long startVcn = BinaryPrimitives.ReadInt64LittleEndian(a[16..]);
        long lastVcn = BinaryPrimitives.ReadInt64LittleEndian(a[24..]);
        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[32..]);
        int unit = BinaryPrimitives.ReadUInt16LittleEndian(a[34..]);
        if (runOffset > a.Length || startVcn < 0)
        {
            return null;
        }

        (List<NtfsRun> runs, bool valid) = DecodeRuns(a[runOffset..], startVcn);
        return new NtfsAttribute
        {
            Type = type, Name = name, NonResident = true, Flags = flags, Id = id, RecordOffset = recordOffset, Length = a.Length,
            StartVcn = startVcn, LastVcn = lastVcn, CompressionUnit = unit is > 0 and < 16 ? unit : 0,
            AllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(a[40..]), RealSize = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(a[48..])),
            InitializedSize = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(a[56..])), Runs = runs, RunListValid = valid, RecordNumber = recordNumber,
        };
    }

    /// <summary>ランリストを読む (FOR-10 の仕様 3)。長さ 0・桁あふれは不正として、そこまでのランを返す。</summary>
    public static (List<NtfsRun> Runs, bool Valid) DecodeRuns(ReadOnlySpan<byte> data, long startVcn)
    {
        var runs = new List<NtfsRun>();
        long vcn = startVcn;
        long lcn = 0;
        int at = 0;
        while (at < data.Length && runs.Count < MaxRuns)
        {
            byte header = data[at++];
            if (header == 0)
            {
                return (runs, true);
            }

            int lengthSize = header & 0x0F, offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || at + lengthSize + offsetSize > data.Length)
            {
                return (runs, false);
            }

            long length = ReadSigned(data.Slice(at, lengthSize), signed: false);
            at += lengthSize;
            if (length <= 0)
            {
                return (runs, false);
            }

            long? runLcn = null;
            if (offsetSize > 0)
            {
                long delta = ReadSigned(data.Slice(at, offsetSize), signed: true);
                at += offsetSize;
                try
                {
                    lcn = checked(lcn + delta);
                }
                catch (OverflowException)
                {
                    return (runs, false);
                }

                if (lcn < 0)
                {
                    return (runs, false);
                }

                runLcn = lcn;
            }

            runs.Add(new NtfsRun(vcn, length, runLcn));
            try
            {
                vcn = checked(vcn + length);
            }
            catch (OverflowException)
            {
                return (runs, false);
            }
        }

        return (runs, false);
    }

    private static long ReadSigned(ReadOnlySpan<byte> b, bool signed)
    {
        long v = 0;
        for (int i = b.Length - 1; i >= 0; i--)
        {
            v = (v << 8) | b[i];
        }

        if (signed && b.Length < 8 && (b[^1] & 0x80) != 0)
        {
            v -= 1L << (b.Length * 8);
        }

        return v;
    }

    /// <summary>
    /// LZNT1 の展開 (FOR-10 の仕様 7)。<paramref name="output"/> に書いた長さを返す。壊れたデータは展開できた所までで止める。
    /// </summary>
    public static int DecompressLznt1(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int src = 0, dst = 0;
        while (src + 2 <= input.Length && dst < output.Length)
        {
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(input[src..]);
            src += 2;
            if (header == 0)
            {
                break;
            }

            int chunkSize = (header & 0x0FFF) + 1;
            bool compressed = (header & 0x8000) != 0;
            int chunkEnd = Math.Min(input.Length, src + chunkSize);
            int chunkStart = dst;
            if (!compressed)
            {
                int n = Math.Min(chunkEnd - src, output.Length - dst);
                input.Slice(src, n).CopyTo(output[dst..]);
                dst += n;
                src = chunkEnd;
                continue;
            }

            while (src < chunkEnd && dst < output.Length)
            {
                byte flags = input[src++];
                for (int bit = 0; bit < 8 && src < chunkEnd && dst < output.Length; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output[dst++] = input[src++];
                        continue;
                    }

                    if (src + 2 > chunkEnd)
                    {
                        return dst;
                    }

                    ushort token = BinaryPrimitives.ReadUInt16LittleEndian(input[src..]);
                    src += 2;
                    int position = dst - chunkStart;
                    int lengthBits = 12;
                    for (int p = position - 1; p >= 0x10; p >>= 1)
                    {
                        lengthBits--;
                    }

                    int displacement = (token >> lengthBits) + 1;
                    int length = (token & ((1 << lengthBits) - 1)) + 3;
                    if (displacement > position)
                    {
                        return dst;
                    }

                    for (int k = 0; k < length && dst < output.Length; k++)
                    {
                        output[dst] = output[dst - displacement];
                        dst++;
                    }
                }
            }

            src = chunkEnd;

            // 圧縮されたチャンクは 4096 バイトに満たなくても、次のチャンクは 4096 バイトの境界から始まる。
            if (dst - chunkStart < 4096 && src + 2 <= input.Length && BinaryPrimitives.ReadUInt16LittleEndian(input[src..]) != 0 && dst < output.Length)
            {
                int pad = Math.Min(4096 - (dst - chunkStart), output.Length - dst);
                output.Slice(dst, pad).Clear();
                dst += pad;
            }
        }

        return dst;
    }

    /// <summary>ファイルの属性の文字列 (R・H・S・A・C・E など)。</summary>
    public static string AttributeText(uint a)
    {
        var s = new StringBuilder();
        if ((a & 0x0001) != 0) s.Append('R');
        if ((a & 0x0002) != 0) s.Append('H');
        if ((a & 0x0004) != 0) s.Append('S');
        if ((a & 0x10000000) != 0) s.Append('D');
        if ((a & 0x0020) != 0) s.Append('A');
        if ((a & 0x0200) != 0) s.Append('P');
        if ((a & 0x0400) != 0) s.Append('L');
        if ((a & 0x0800) != 0) s.Append('C');
        if ((a & 0x4000) != 0) s.Append('E');
        return s.ToString();
    }
}
