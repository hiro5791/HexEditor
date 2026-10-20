using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Devices.Info;

/// <summary>IDENTIFY DEVICE のデータ (ATA/ATAPI Command Set の語の番号)。読めなかった項目は null。</summary>
public sealed record AtaIdentifyData
{
    public string Model { get; init; } = string.Empty;

    public string Serial { get; init; } = string.Empty;

    public string Firmware { get; init; } = string.Empty;

    /// <summary>28 bit の利用者がアクセスできる最大 LBA (語 60〜61 のセクタ数 − 1)。</summary>
    public long? MaxLba28 { get; init; }

    /// <summary>48 bit の利用者がアクセスできる最大 LBA (語 100〜103 のセクタ数 − 1)。48 bit に対応しなければ null。</summary>
    public long? MaxLba48 { get; init; }

    public bool Supports48Bit { get; init; }

    public bool HpaSupported { get; init; }

    public bool HpaEnabled { get; init; }

    public bool DcoSupported { get; init; }

    public bool SmartSupported { get; init; }

    public bool SmartEnabled { get; init; }

    public int LogicalSectorSize { get; init; } = 512;

    public int PhysicalSectorSize { get; init; } = 512;

    /// <summary>回転数 (rpm)。SSD は 1、不明は null (語 217)。</summary>
    public int? RotationRate { get; init; }

    /// <summary>TRIM (DATA SET MANAGEMENT) に対応 (語 169 の bit 0)。</summary>
    public bool TrimSupported { get; init; }

    /// <summary>SATA の機能の語 (語 76) が有効。</summary>
    public bool IsSata { get; init; }

    /// <summary>語 255 の整合性の値が合わない (署名 A5 があるのに合計が 0 にならない)。</summary>
    public bool ChecksumMismatch { get; init; }

    /// <summary>利用者がアクセスできる最大 LBA (48 bit があればそれ)。</summary>
    public long? UserMaxLba => Supports48Bit && MaxLba48 is { } l48 ? l48 : MaxLba28;
}

/// <summary>DEVICE CONFIGURATION IDENTIFY のデータ。</summary>
public sealed record DcoIdentifyData(int Revision, long MaxLba, bool ChecksumMismatch);

/// <summary>SMART の属性 1 つ (12 バイト)。<see cref="Threshold"/> は READ THRESHOLDS で分かったもの。</summary>
public sealed record SmartAttribute(byte Id, ushort Flags, byte Current, byte Worst, long Raw, byte? Threshold)
{
    /// <summary>生の値 (48 bit) の 16 進表記。</summary>
    public string RawHex => Raw.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>現在値が閾値以下 (ATA の判定と同じ)。閾値 0 は「常に正常」の意味なので注意にしない。</summary>
    public bool BelowThreshold => Threshold is { } t and > 0 && Current <= t;

    /// <summary>事前の故障予測の属性 (flags の bit 0)。</summary>
    public bool PreFailure => (Flags & 1) != 0;
}

/// <summary>SMART READ DATA の結果。</summary>
public sealed record SmartData(ushort Revision, IReadOnlyList<SmartAttribute> Attributes, bool ChecksumMismatch);

/// <summary>
/// ATA の構造体の読み書き。読み込みは信頼できない入力 (デバイスの応答) として扱い、長さが足りない・値がおかしい場合も例外にせず、
/// 読めた項目だけを返す (読めなければ null)。書き込み (<c>Build…</c>) は偽のデバイスとテストが使う。
/// </summary>
public static class AtaStructures
{
    public const int SectorSize = 512;

    /// <summary>主要な SMART の属性の英語名 (書き出しの JSON・テキストに入れる。画面では翻訳した名前を出す)。</summary>
    public static IReadOnlyDictionary<byte, string> AttributeNames { get; } = new Dictionary<byte, string>
    {
        [1] = "Raw Read Error Rate",
        [2] = "Throughput Performance",
        [3] = "Spin-Up Time",
        [4] = "Start/Stop Count",
        [5] = "Reallocated Sectors Count",
        [7] = "Seek Error Rate",
        [8] = "Seek Time Performance",
        [9] = "Power-On Hours",
        [10] = "Spin Retry Count",
        [11] = "Calibration Retry Count",
        [12] = "Power Cycle Count",
        [170] = "Available Reserved Space",
        [171] = "Program Fail Count",
        [172] = "Erase Fail Count",
        [173] = "Wear Leveling Count",
        [174] = "Unexpected Power Loss Count",
        [177] = "Wear Range Delta",
        [179] = "Used Reserved Block Count Total",
        [181] = "Program Fail Count Total",
        [182] = "Erase Fail Count Total",
        [183] = "Runtime Bad Block",
        [184] = "End-to-End Error",
        [187] = "Reported Uncorrectable Errors",
        [188] = "Command Timeout",
        [189] = "High Fly Writes",
        [190] = "Airflow Temperature",
        [191] = "G-Sense Error Rate",
        [192] = "Power-off Retract Count",
        [193] = "Load Cycle Count",
        [194] = "Temperature",
        [195] = "Hardware ECC Recovered",
        [196] = "Reallocation Event Count",
        [197] = "Current Pending Sector Count",
        [198] = "Offline Uncorrectable Sector Count",
        [199] = "UltraDMA CRC Error Count",
        [200] = "Multi-Zone Error Rate",
        [231] = "SSD Life Left",
        [232] = "Endurance Remaining",
        [233] = "Media Wearout Indicator",
        [235] = "Good Block Count",
        [241] = "Total LBAs Written",
        [242] = "Total LBAs Read",
    };

    /// <summary>主要な属性の英語名。不明なら null。</summary>
    public static string? AttributeName(byte id) => AttributeNames.TryGetValue(id, out string? name) ? name : null;

    private static ushort Word(ReadOnlySpan<byte> data, int word) =>
        word * 2 + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[(word * 2)..]) : (ushort)0;

    /// <summary>ATA の文字列 (各語の上位・下位バイトが入れ替わっている) を読む。表示できない文字は落とし、前後の空白を除く。</summary>
    public static string AtaString(ReadOnlySpan<byte> data, int firstWord, int words)
    {
        var sb = new StringBuilder(words * 2);
        for (int w = firstWord; w < firstWord + words; w++)
        {
            ushort value = Word(data, w);
            foreach (byte b in new[] { (byte)(value >> 8), (byte)value })
            {
                if (b is >= 0x20 and < 0x7F)
                {
                    sb.Append((char)b);
                }
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>語 255 の整合性: 下位バイトが A5 なら、512 バイトの合計が 0 (mod 256) になるはず。A5 でなければ確かめない。</summary>
    public static bool ChecksumMismatch(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SectorSize || sector[510] != 0xA5)
        {
            return false;
        }

        byte sum = 0;
        foreach (byte b in sector[..SectorSize])
        {
            sum += b;
        }

        return sum != 0;
    }

    /// <summary>IDENTIFY DEVICE のデータを読む。512 バイトに足りなければ null。</summary>
    public static AtaIdentifyData? ParseIdentify(ReadOnlySpan<byte> data)
    {
        if (data.Length < SectorSize)
        {
            return null;
        }

        ushort w82 = Word(data, 82), w83 = Word(data, 83), w85 = Word(data, 85), w86 = Word(data, 86);
        bool valid83 = (w83 & 0xC000) == 0x4000;
        bool supports48 = valid83 && (w83 & (1 << 10)) != 0;
        long sectors28 = Word(data, 60) | ((long)Word(data, 61) << 16);
        long sectors48 = (long)(BinaryPrimitives.ReadUInt64LittleEndian(data[200..]) & 0x0000_FFFF_FFFF_FFFF);

        // 語 106: セクタの大きさ (bit 15:14 が 01 のとき有効)。
        ushort w106 = Word(data, 106);
        int logical = SectorSize;
        int physical = SectorSize;
        if ((w106 & 0xC000) == 0x4000)
        {
            if ((w106 & (1 << 12)) != 0)
            {
                long words = Word(data, 117) | ((long)Word(data, 118) << 16);
                if (words is >= 256 and <= 32768)
                {
                    logical = (int)(words * 2);
                }
            }

            if ((w106 & (1 << 13)) != 0)
            {
                int shift = w106 & 0x0F;
                physical = logical << Math.Min(shift, 6);
            }
            else
            {
                physical = logical;
            }
        }

        ushort rotation = Word(data, 217);
        ushort sata = Word(data, 76);
        return new AtaIdentifyData
        {
            Serial = AtaString(data, 10, 10),
            Firmware = AtaString(data, 23, 4),
            Model = AtaString(data, 27, 20),
            MaxLba28 = sectors28 > 0 ? sectors28 - 1 : null,
            MaxLba48 = supports48 && sectors48 > 0 ? sectors48 - 1 : null,
            Supports48Bit = supports48,
            HpaSupported = (w82 & 0xC000) != 0xC000 && (w82 & (1 << 10)) != 0,
            HpaEnabled = (w85 & (1 << 10)) != 0,
            DcoSupported = valid83 && (w83 & (1 << 11)) != 0,
            SmartSupported = (w82 & 0xC000) != 0xC000 && (w82 & 1) != 0,
            SmartEnabled = (w85 & 1) != 0,
            LogicalSectorSize = logical,
            PhysicalSectorSize = physical,
            RotationRate = rotation == 1 ? 1 : rotation is >= 0x0401 and <= 0xFFFE ? rotation : null,
            TrimSupported = (Word(data, 169) & 1) != 0,
            IsSata = sata is not (0 or 0xFFFF),
            ChecksumMismatch = ChecksumMismatch(data),
        };
    }

    /// <summary>DEVICE CONFIGURATION IDENTIFY のデータを読む (語 3〜6 が本来の最大 LBA)。</summary>
    public static DcoIdentifyData? ParseDco(ReadOnlySpan<byte> data)
    {
        if (data.Length < SectorSize)
        {
            return null;
        }

        int revision = Word(data, 0);
        long max = (long)(BinaryPrimitives.ReadUInt64LittleEndian(data[6..]) & 0x0000_FFFF_FFFF_FFFF);
        return revision == 0 && max == 0 ? null : new DcoIdentifyData(revision, max, ChecksumMismatch(data));
    }

    /// <summary>SMART READ DATA と READ THRESHOLDS を読む。属性の番号 0 の行は空き。<paramref name="thresholds"/> は無くてもよい。</summary>
    public static SmartData? ParseSmart(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds)
    {
        if (data.Length < SectorSize)
        {
            return null;
        }

        var limits = new Dictionary<byte, byte>();
        if (thresholds.Length >= SectorSize)
        {
            for (int i = 0; i < 30; i++)
            {
                int at = 2 + i * 12;
                if (thresholds[at] != 0)
                {
                    limits.TryAdd(thresholds[at], thresholds[at + 1]);
                }
            }
        }

        var attributes = new List<SmartAttribute>();
        var seen = new HashSet<byte>();
        for (int i = 0; i < 30; i++)
        {
            int at = 2 + i * 12;
            byte id = data[at];
            if (id == 0 || !seen.Add(id))
            {
                continue;
            }

            long raw = 0;
            for (int b = 5; b >= 0; b--)
            {
                raw = (raw << 8) | data[at + 5 + b];
            }

            attributes.Add(new SmartAttribute(id, BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 1)..]), data[at + 3], data[at + 4], raw,
                limits.TryGetValue(id, out byte t) ? t : null));
        }

        return new SmartData(BinaryPrimitives.ReadUInt16LittleEndian(data), attributes, ChecksumMismatchSmart(data));
    }

    /// <summary>SMART のデータの最後のバイトは、512 バイトの合計が 0 になる値。</summary>
    private static bool ChecksumMismatchSmart(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (byte b in data[..SectorSize])
        {
            sum += b;
        }

        return sum != 0;
    }

    // ---- 作る (偽のデバイスとテスト) ----

    private static void PutWord(Span<byte> data, int word, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data[(word * 2)..], value);

    private static void PutString(Span<byte> data, int firstWord, int words, string text)
    {
        string padded = text.PadRight(words * 2)[..(words * 2)];
        for (int w = 0; w < words; w++)
        {
            PutWord(data, firstWord + w, (ushort)((padded[w * 2] << 8) | padded[w * 2 + 1]));
        }
    }

    /// <summary>語 255 の整合性の値を入れる。</summary>
    public static void SealChecksum(Span<byte> sector)
    {
        sector[510] = 0xA5;
        byte sum = 0;
        for (int i = 0; i < 511; i++)
        {
            sum += sector[i];
        }

        sector[511] = (byte)(0x100 - sum);
    }

    /// <summary>IDENTIFY DEVICE のデータを作る。</summary>
    public static byte[] BuildIdentify(AtaIdentifyData info, long userSectors)
    {
        byte[] d = new byte[SectorSize];
        PutString(d, 10, 10, info.Serial);
        PutString(d, 23, 4, info.Firmware);
        PutString(d, 27, 20, info.Model);
        PutWord(d, 49, 1 << 9);
        long sectors28 = Math.Min(userSectors, 0x0FFF_FFFF);
        PutWord(d, 60, (ushort)sectors28);
        PutWord(d, 61, (ushort)(sectors28 >> 16));
        PutWord(d, 76, info.IsSata ? (ushort)0x0006 : (ushort)0);
        PutWord(d, 82, (ushort)(0x4000 | (info.HpaSupported ? 1 << 10 : 0) | (info.SmartSupported ? 1 : 0)));
        PutWord(d, 83, (ushort)(0x4000 | (info.Supports48Bit ? 1 << 10 : 0) | (info.DcoSupported ? 1 << 11 : 0)));
        PutWord(d, 85, (ushort)((info.HpaEnabled ? 1 << 10 : 0) | (info.SmartEnabled ? 1 : 0)));
        PutWord(d, 86, (ushort)((info.Supports48Bit ? 1 << 10 : 0) | (info.DcoSupported ? 1 << 11 : 0)));
        if (info.Supports48Bit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(200), (ulong)userSectors);
        }

        int shift = 0;
        while (info.LogicalSectorSize << shift < info.PhysicalSectorSize && shift < 6)
        {
            shift++;
        }

        ushort w106 = (ushort)(0x4000 | (shift > 0 ? (1 << 13) | shift : 0) | (info.LogicalSectorSize != 512 ? 1 << 12 : 0));
        PutWord(d, 106, w106);
        if (info.LogicalSectorSize != 512)
        {
            PutWord(d, 117, (ushort)(info.LogicalSectorSize / 2));
            PutWord(d, 118, (ushort)((info.LogicalSectorSize / 2) >> 16));
        }

        PutWord(d, 169, info.TrimSupported ? (ushort)1 : (ushort)0);
        PutWord(d, 217, (ushort)(info.RotationRate ?? 0));
        SealChecksum(d);
        return d;
    }

    /// <summary>DEVICE CONFIGURATION IDENTIFY のデータを作る。</summary>
    public static byte[] BuildDco(long maxLba)
    {
        byte[] d = new byte[SectorSize];
        PutWord(d, 0, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(6), (ulong)maxLba);
        SealChecksum(d);
        return d;
    }

    /// <summary>SMART READ DATA のデータを作る (最大 30 個)。</summary>
    public static byte[] BuildSmartData(IEnumerable<SmartAttribute> attributes)
    {
        byte[] d = new byte[SectorSize];
        PutWord(d, 0, 0x0010);
        int i = 0;
        foreach (SmartAttribute a in attributes.Take(30))
        {
            int at = 2 + i++ * 12;
            d[at] = a.Id;
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 1), a.Flags);
            d[at + 3] = a.Current;
            d[at + 4] = a.Worst;
            for (int b = 0; b < 6; b++)
            {
                d[at + 5 + b] = (byte)(a.Raw >> (8 * b));
            }
        }

        SealSmart(d);
        return d;
    }

    /// <summary>SMART READ THRESHOLDS のデータを作る。</summary>
    public static byte[] BuildSmartThresholds(IEnumerable<SmartAttribute> attributes)
    {
        byte[] d = new byte[SectorSize];
        PutWord(d, 0, 0x0010);
        int i = 0;
        foreach (SmartAttribute a in attributes.Take(30))
        {
            int at = 2 + i++ * 12;
            d[at] = a.Id;
            d[at + 1] = a.Threshold ?? 0;
        }

        SealSmart(d);
        return d;
    }

    private static void SealSmart(Span<byte> d)
    {
        byte sum = 0;
        for (int i = 0; i < 511; i++)
        {
            sum += d[i];
        }

        d[511] = (byte)(0x100 - sum);
    }
}
