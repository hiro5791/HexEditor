using System.Buffers.Binary;
using HexEditor.Core.Elevation;

namespace HexEditor.Core.Devices.Info;

/// <summary>
/// ATA のコマンドのレジスタの組 (タスクファイル)。<see cref="DataIn"/> はデバイスからデータを受け取るコマンド。デバイスへデータを送るコマンド
/// (書き込み・設定の変更) はこの型では表さない (<see cref="AtaCommands.IsAllowed"/> が許すのは読み取りだけのコマンド)。
/// </summary>
public readonly record struct AtaTaskFile(byte Features, byte SectorCount, byte LbaLow, byte LbaMid, byte LbaHigh, byte Device, byte Command,
    bool Extended, bool DataIn, int DataLength);

/// <summary>ATA のコマンドの結果: ステータス・エラーのレジスタと、受け取ったデータ (データのないコマンドは返ってきたレジスタ)。</summary>
public sealed record AtaResult(byte Status, byte Error, byte[] Data, byte[] CurrentTaskFile, byte[] PreviousTaskFile)
{
    /// <summary>ステータスの ERR ビット (bit 0)。</summary>
    public bool Failed => (Status & 0x01) != 0;
}

/// <summary>
/// FOR-27・FOR-28 で送る ATA のコマンド (12 の FOR-27 の仕様 1、FOR-28 の仕様 1)。すべて読み取りだけのコマンドで、デバイスの設定を変える
/// SET MAX ADDRESS・DEVICE CONFIGURATION RESTORE・SMART のセルフテストなどは作らない (FOR-27 の仕様 3、FOR-28 の仕様 5)。
/// 送る前に <see cref="IsAllowed"/> で許可リストと照合し、違えば送らない (補助プロセスでも同じ確認をする)。
/// </summary>
public static class AtaCommands
{
    public const byte IdentifyDeviceCommand = 0xEC;
    public const byte ReadNativeMaxAddressCommand = 0xF8;
    public const byte ReadNativeMaxAddressExtCommand = 0x27;
    public const byte DeviceConfigurationCommand = 0xB1;
    public const byte DeviceConfigurationIdentifyFeature = 0xC2;
    public const byte SmartCommand = 0xB0;
    public const byte SmartReadDataFeature = 0xD0;
    public const byte SmartReadThresholdsFeature = 0xD1;

    /// <summary>IDENTIFY DEVICE (512 バイトを受け取る)。</summary>
    public static AtaTaskFile IdentifyDevice { get; } = new(0, 1, 0, 0, 0, 0xA0, IdentifyDeviceCommand, false, true, 512);

    /// <summary>READ NATIVE MAX ADDRESS (28 bit。結果はレジスタ)。</summary>
    public static AtaTaskFile ReadNativeMaxAddress { get; } = new(0, 0, 0, 0, 0, 0x40, ReadNativeMaxAddressCommand, false, false, 0);

    /// <summary>READ NATIVE MAX ADDRESS EXT (48 bit。結果はレジスタ)。</summary>
    public static AtaTaskFile ReadNativeMaxAddressExt { get; } = new(0, 0, 0, 0, 0, 0x40, ReadNativeMaxAddressExtCommand, true, false, 0);

    /// <summary>DEVICE CONFIGURATION IDENTIFY (512 バイトを受け取る)。</summary>
    public static AtaTaskFile DeviceConfigurationIdentify { get; } =
        new(DeviceConfigurationIdentifyFeature, 1, 0, 0, 0, 0xA0, DeviceConfigurationCommand, false, true, 512);

    /// <summary>SMART READ DATA (512 バイト)。LBA Mid / High は SMART の決まった値 4F / C2。</summary>
    public static AtaTaskFile SmartReadData { get; } = new(SmartReadDataFeature, 1, 1, 0x4F, 0xC2, 0xA0, SmartCommand, false, true, 512);

    /// <summary>SMART READ THRESHOLDS (512 バイト)。</summary>
    public static AtaTaskFile SmartReadThresholds { get; } = new(SmartReadThresholdsFeature, 1, 1, 0x4F, 0xC2, 0xA0, SmartCommand, false, true, 512);

    private static readonly AtaTaskFile[] Allowed =
        [IdentifyDevice, ReadNativeMaxAddress, ReadNativeMaxAddressExt, DeviceConfigurationIdentify, SmartReadData, SmartReadThresholds];

    /// <summary>許可リストのコマンド (レジスタの組がすべて一致するもの) か。</summary>
    public static bool IsAllowed(AtaTaskFile command) => Array.IndexOf(Allowed, command) >= 0;

    /// <summary>情報の種類に対応する ATA のコマンド。ATA のコマンドでない種類は null。</summary>
    public static AtaTaskFile? For(DeviceInfoKind kind, bool extended = true) => kind switch
    {
        DeviceInfoKind.IdentifyDevice => IdentifyDevice,
        DeviceInfoKind.ReadNativeMaxAddress => extended ? ReadNativeMaxAddressExt : ReadNativeMaxAddress,
        DeviceInfoKind.DeviceConfigurationIdentify => DeviceConfigurationIdentify,
        DeviceInfoKind.SmartReadData => SmartReadData,
        DeviceInfoKind.SmartReadThresholds => SmartReadThresholds,
        _ => null,
    };

    // ---- ATA_PASS_THROUGH_EX (IOCTL_ATA_PASS_THROUGH) ----

    public const ushort FlagDrdyRequired = 0x01;
    public const ushort FlagDataIn = 0x02;
    public const ushort Flag48Bit = 0x08;

    /// <summary>構造体の大きさ (64 bit のプロセス: DataBufferOffset が 8 バイトに揃う)。</summary>
    public static int PassThroughSize(bool is64Bit) => is64Bit ? 48 : 40;

    /// <summary>
    /// <c>ATA_PASS_THROUGH_EX</c> とデータの領域を 1 つのバッファに組み立てる (データは構造体の直後)。許可リストにないコマンドは
    /// <see cref="ArgumentException"/> (送らない)。
    /// </summary>
    public static byte[] BuildPassThrough(AtaTaskFile command, bool is64Bit, uint timeoutSeconds = 10)
    {
        if (!IsAllowed(command))
        {
            throw new ArgumentException("The ATA command is not on the allow list of read-only commands.", nameof(command));
        }

        int header = PassThroughSize(is64Bit);
        byte[] buffer = new byte[header + command.DataLength];
        Span<byte> s = buffer;
        BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)header);
        ushort flags = (ushort)(FlagDrdyRequired | (command.DataIn ? FlagDataIn : 0) | (command.Extended ? Flag48Bit : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)command.DataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], timeoutSeconds);
        int offsetField = is64Bit ? 24 : 20;
        if (is64Bit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s[offsetField..], (ulong)header);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[offsetField..], (uint)header);
        }

        int previous = is64Bit ? 32 : 24;
        int current = previous + 8;
        s[current + 0] = command.Features;
        s[current + 1] = command.SectorCount;
        s[current + 2] = command.LbaLow;
        s[current + 3] = command.LbaMid;
        s[current + 4] = command.LbaHigh;
        s[current + 5] = command.Device;
        s[current + 6] = command.Command;
        return buffer;
    }

    /// <summary>返ってきたバッファから結果を読む (レジスタとデータ)。短すぎれば null。</summary>
    public static AtaResult? ParsePassThrough(ReadOnlySpan<byte> buffer, AtaTaskFile command, bool is64Bit)
    {
        int header = PassThroughSize(is64Bit);
        if (buffer.Length < header)
        {
            return null;
        }

        int previous = is64Bit ? 32 : 24;
        byte[] prev = buffer.Slice(previous, 8).ToArray();
        byte[] cur = buffer.Slice(previous + 8, 8).ToArray();
        byte[] data = command.DataIn ? buffer.Slice(header, Math.Min(command.DataLength, buffer.Length - header)).ToArray() : [];
        return new AtaResult(cur[6], cur[0], data, cur, prev);
    }

    /// <summary>READ NATIVE MAX ADDRESS (EXT) の結果のレジスタから最大 LBA を読む。</summary>
    public static long NativeMaxFromRegisters(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, bool extended)
    {
        if (current.Length < 6)
        {
            return -1;
        }

        long low = current[2] | ((long)current[3] << 8) | ((long)current[4] << 16);
        if (!extended)
        {
            return low | ((long)(current[5] & 0x0F) << 24);
        }

        if (previous.Length < 5)
        {
            return -1;
        }

        return low | ((long)previous[2] << 24) | ((long)previous[3] << 32) | ((long)previous[4] << 40);
    }

    // ---- 補助プロセスとの間の形 (QueryDeviceInfo の応答の本体) ----

    /// <summary>ATA の種類の応答: [ステータス u8][エラー u8][予約 6][データ]。データのないコマンド (最大 LBA) は [LBA i64]。</summary>
    public static byte[] EncodeResult(AtaResult result, DeviceInfoKind kind, bool extended)
    {
        byte[] payload;
        if (kind == DeviceInfoKind.ReadNativeMaxAddress)
        {
            payload = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(payload, NativeMaxFromRegisters(result.CurrentTaskFile, result.PreviousTaskFile, extended));
        }
        else
        {
            payload = result.Data;
        }

        byte[] body = new byte[8 + payload.Length];
        body[0] = result.Status;
        body[1] = result.Error;
        payload.CopyTo(body, 8);
        return body;
    }

    /// <summary><see cref="EncodeResult"/> の形を読む。短すぎれば null。</summary>
    public static (byte Status, byte Error, byte[] Payload)? DecodeResult(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8)
        {
            return null;
        }

        return (body[0], body[1], body[8..].ToArray());
    }

    /// <summary>最大 LBA の応答を作る (偽のデバイス・テスト用)。</summary>
    public static byte[] EncodeNativeMax(long maxLba, byte status = 0x50, byte error = 0)
    {
        byte[] body = new byte[16];
        body[0] = status;
        body[1] = error;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), maxLba);
        return body;
    }

    /// <summary>データのある ATA のコマンドの応答を作る (偽のデバイス・テスト用)。</summary>
    public static byte[] EncodeData(ReadOnlySpan<byte> data, byte status = 0x50, byte error = 0)
    {
        byte[] body = new byte[8 + data.Length];
        body[0] = status;
        body[1] = error;
        data.CopyTo(body.AsSpan(8));
        return body;
    }
}
