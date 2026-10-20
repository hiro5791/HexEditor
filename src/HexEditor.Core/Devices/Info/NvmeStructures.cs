using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace HexEditor.Core.Devices.Info;

/// <summary>NVMe の SMART / Health Information (ログページ 02h、512 バイト)。量の単位は NVMe の定義のまま (データ量は 512,000 バイト単位)。</summary>
public sealed record NvmeHealthData
{
    public byte CriticalWarning { get; init; }

    /// <summary>温度 (ケルビン)。</summary>
    public int TemperatureKelvin { get; init; }

    public int AvailableSparePercent { get; init; }

    public int AvailableSpareThresholdPercent { get; init; }

    public int PercentageUsed { get; init; }

    public UInt128 DataUnitsRead { get; init; }

    public UInt128 DataUnitsWritten { get; init; }

    public UInt128 HostReadCommands { get; init; }

    public UInt128 HostWriteCommands { get; init; }

    public UInt128 ControllerBusyMinutes { get; init; }

    public UInt128 PowerCycles { get; init; }

    public UInt128 PowerOnHours { get; init; }

    public UInt128 UnsafeShutdowns { get; init; }

    public UInt128 MediaErrors { get; init; }

    public UInt128 ErrorLogEntries { get; init; }

    /// <summary>温度 (℃)。</summary>
    public int TemperatureCelsius => TemperatureKelvin - 273;

    /// <summary>読んだデータ量 (バイト)。</summary>
    public UInt128 BytesRead => DataUnitsRead * 512_000;

    public UInt128 BytesWritten => DataUnitsWritten * 512_000;
}

/// <summary>NVMe の Identify Controller (CNS 01h) の必要な項目。</summary>
public sealed record NvmeControllerData(string Serial, string Model, string Firmware, UInt128 TotalCapacity, bool DatasetManagement);

/// <summary>NVMe の Identify Namespace (CNS 00h) の必要な項目。サイズ・容量はブロック数。</summary>
public sealed record NvmeNamespaceData(ulong Size, ulong Capacity, ulong Utilization, int BlockSize)
{
    public UInt128 SizeBytes => (UInt128)Size * (ulong)BlockSize;

    public UInt128 CapacityBytes => (UInt128)Capacity * (ulong)BlockSize;
}

/// <summary><c>STORAGE_DEVICE_DESCRIPTOR</c> の文字列と接続の種類 (USB など、ATA パススルーが通らないことがあるものを含む)。</summary>
public sealed record StorageDescriptorData(string Vendor, string Product, string Revision, string Serial, int BusType, bool Removable);

/// <summary>NVMe と <c>IOCTL_STORAGE_QUERY_PROPERTY</c> の構造体の読み書き。読み込みは信頼できない入力として扱い、足りなければ null。</summary>
public static class NvmeStructures
{
    public const int HealthLogSize = 512;
    public const int IdentifySize = 4096;

    // STORAGE_PROPERTY_ID と STORAGE_PROTOCOL_SPECIFIC_DATA の値。
    public const uint StorageDeviceProperty = 0;
    public const uint StorageDeviceSeekPenaltyProperty = 7;
    public const uint StorageDeviceTrimProperty = 8;
    public const uint StorageDeviceProtocolSpecificProperty = 50;
    public const uint ProtocolTypeNvme = 3;
    public const uint NvmeDataTypeIdentify = 1;
    public const uint NvmeDataTypeLogPage = 2;
    public const uint NvmeIdentifyCnsNamespace = 0;
    public const uint NvmeIdentifyCnsController = 1;
    public const uint NvmeLogPageHealthInfo = 2;
    public const int ProtocolSpecificDataSize = 40;

    private static UInt128 U128(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt128LittleEndian(s);

    /// <summary>SMART / Health Information を読む。</summary>
    public static NvmeHealthData? ParseHealth(ReadOnlySpan<byte> d)
    {
        if (d.Length < HealthLogSize)
        {
            return null;
        }

        return new NvmeHealthData
        {
            CriticalWarning = d[0],
            TemperatureKelvin = BinaryPrimitives.ReadUInt16LittleEndian(d[1..]),
            AvailableSparePercent = d[3],
            AvailableSpareThresholdPercent = d[4],
            PercentageUsed = d[5],
            DataUnitsRead = U128(d[32..]),
            DataUnitsWritten = U128(d[48..]),
            HostReadCommands = U128(d[64..]),
            HostWriteCommands = U128(d[80..]),
            ControllerBusyMinutes = U128(d[96..]),
            PowerCycles = U128(d[112..]),
            PowerOnHours = U128(d[128..]),
            UnsafeShutdowns = U128(d[144..]),
            MediaErrors = U128(d[160..]),
            ErrorLogEntries = U128(d[176..]),
        };
    }

    private static string Ascii(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (byte b in s)
        {
            if (b == 0)
            {
                break;
            }

            if (b is >= 0x20 and < 0x7F)
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Identify Controller を読む (SN 4〜23、MN 24〜63、FR 64〜71、TNVMCAP 280、ONCS 520)。</summary>
    public static NvmeControllerData? ParseController(ReadOnlySpan<byte> d)
    {
        if (d.Length < 1024)
        {
            return null;
        }

        return new NvmeControllerData(Ascii(d[4..24]), Ascii(d[24..64]), Ascii(d[64..72]), U128(d[280..]),
            (BinaryPrimitives.ReadUInt16LittleEndian(d[520..]) & (1 << 2)) != 0);
    }

    /// <summary>Identify Namespace を読む (NSZE 0、NCAP 8、NUSE 16、FLBAS 26、LBA の形式の表 128)。</summary>
    public static NvmeNamespaceData? ParseNamespace(ReadOnlySpan<byte> d)
    {
        if (d.Length < 192)
        {
            return null;
        }

        int format = d[26] & 0x0F;
        int lbads = d[128 + format * 4 + 2];
        int blockSize = lbads is >= 9 and <= 16 ? 1 << lbads : 512;
        return new NvmeNamespaceData(BinaryPrimitives.ReadUInt64LittleEndian(d), BinaryPrimitives.ReadUInt64LittleEndian(d[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(d[16..]), blockSize);
    }

    /// <summary><c>STORAGE_DEVICE_DESCRIPTOR</c> を読む。文字列の位置は記述子の中に収まるものだけを読む。</summary>
    public static StorageDescriptorData? ParseStorageDescriptor(ReadOnlySpan<byte> d)
    {
        if (d.Length < 36)
        {
            return null;
        }

        int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), (uint)d.Length);
        ReadOnlySpan<byte> all = d[..Math.Max(size, 36)];
        return new StorageDescriptorData(At(all, 12), At(all, 16), At(all, 20), At(all, 24), (int)BinaryPrimitives.ReadUInt32LittleEndian(all[28..]), all[10] != 0);
    }

    private static string At(ReadOnlySpan<byte> all, int field)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(all[field..]);
        return offset == 0 || offset >= all.Length ? string.Empty : Ascii(all[(int)offset..]);
    }

    /// <summary>TRIM・シーク時間の記述子 (<c>DEVICE_TRIM_DESCRIPTOR</c>、<c>DEVICE_SEEK_PENALTY_DESCRIPTOR</c>) の真偽の値 (オフセット 8)。</summary>
    public static bool? ParseFlagDescriptor(ReadOnlySpan<byte> d) => d.Length >= 9 ? d[8] != 0 : null;

    /// <summary>
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> の入力を作る。NVMe のプロトコル固有のデータ (<paramref name="dataType"/> が 0 でないとき) は
    /// <c>STORAGE_PROPERTY_QUERY</c> の後ろに <c>STORAGE_PROTOCOL_SPECIFIC_DATA</c> とデータの領域を付ける。
    /// </summary>
    public static byte[] BuildQuery(uint propertyId, uint dataType = 0, uint requestValue = 0, uint requestSubValue = 0, int dataLength = 0)
    {
        if (dataType == 0)
        {
            byte[] simple = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(simple, propertyId);
            return simple;
        }

        byte[] q = new byte[8 + ProtocolSpecificDataSize + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(q, propertyId);
        Span<byte> p = q.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(p, ProtocolTypeNvme);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], dataType);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], requestValue);
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], requestSubValue);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], ProtocolSpecificDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], (uint)dataLength);
        return q;
    }

    /// <summary>
    /// <c>STORAGE_PROTOCOL_DATA_DESCRIPTOR</c> (Version、Size、STORAGE_PROTOCOL_SPECIFIC_DATA) からデータの部分を取り出す。範囲外なら null。
    /// </summary>
    public static byte[]? ProtocolData(ReadOnlySpan<byte> output)
    {
        if (output.Length < 8 + ProtocolSpecificDataSize)
        {
            return null;
        }

        ReadOnlySpan<byte> specific = output[8..];
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(specific[16..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(specific[20..]);
        if (offset < ProtocolSpecificDataSize || (ulong)offset + length > (ulong)specific.Length)
        {
            return null;
        }

        return specific.Slice((int)offset, (int)length).ToArray();
    }

    // ---- 作る (偽のデバイスとテスト) ----

    private static void PutU128(Span<byte> s, UInt128 v) => BinaryPrimitives.WriteUInt128LittleEndian(s, v);

    public static byte[] BuildHealth(NvmeHealthData h)
    {
        byte[] d = new byte[HealthLogSize];
        d[0] = h.CriticalWarning;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(1), (ushort)h.TemperatureKelvin);
        d[3] = (byte)h.AvailableSparePercent;
        d[4] = (byte)h.AvailableSpareThresholdPercent;
        d[5] = (byte)h.PercentageUsed;
        PutU128(d.AsSpan(32), h.DataUnitsRead);
        PutU128(d.AsSpan(48), h.DataUnitsWritten);
        PutU128(d.AsSpan(64), h.HostReadCommands);
        PutU128(d.AsSpan(80), h.HostWriteCommands);
        PutU128(d.AsSpan(96), h.ControllerBusyMinutes);
        PutU128(d.AsSpan(112), h.PowerCycles);
        PutU128(d.AsSpan(128), h.PowerOnHours);
        PutU128(d.AsSpan(144), h.UnsafeShutdowns);
        PutU128(d.AsSpan(160), h.MediaErrors);
        PutU128(d.AsSpan(176), h.ErrorLogEntries);
        return d;
    }

    private static void PutAscii(Span<byte> s, string text)
    {
        s.Fill((byte)' ');
        Encoding.ASCII.GetBytes(text.Length > s.Length ? text[..s.Length] : text, s);
    }

    public static byte[] BuildController(string serial, string model, string firmware, UInt128 capacity, bool dsm)
    {
        byte[] d = new byte[IdentifySize];
        PutAscii(d.AsSpan(4, 20), serial);
        PutAscii(d.AsSpan(24, 40), model);
        PutAscii(d.AsSpan(64, 8), firmware);
        PutU128(d.AsSpan(280), capacity);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(520), dsm ? (ushort)(1 << 2) : (ushort)0);
        return d;
    }

    public static byte[] BuildNamespace(ulong size, ulong capacity, int blockSize)
    {
        byte[] d = new byte[IdentifySize];
        BinaryPrimitives.WriteUInt64LittleEndian(d, size);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), capacity);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), capacity);
        d[26] = 0;
        d[128 + 2] = (byte)BitOperations.Log2((uint)blockSize);
        return d;
    }

    public static byte[] BuildStorageDescriptor(string vendor, string product, string revision, string serial, int busType, bool removable)
    {
        var strings = new List<byte>();
        int Add(string s)
        {
            if (s.Length == 0)
            {
                return 0;
            }

            int at = 40 + strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(s));
            strings.Add(0);
            return at;
        }

        int v = Add(vendor), p = Add(product), r = Add(revision), sn = Add(serial);
        byte[] d = new byte[40 + strings.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)d.Length);
        d[10] = removable ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)v);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)p);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), (uint)r);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(24), (uint)sn);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), (uint)busType);
        strings.CopyTo(d, 40);
        return d;
    }

    public static byte[] BuildFlagDescriptor(bool value)
    {
        byte[] d = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 12);
        d[8] = value ? (byte)1 : (byte)0;
        return d;
    }
}
