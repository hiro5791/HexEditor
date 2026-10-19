using System.Runtime.InteropServices;
using HexEditor.Core.Elevation;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Devices.Info;

/// <summary>
/// 実機のデバイスから読み取り専用のデバイス情報を取る (12 の FOR-27、FOR-28)。補助プロセス (ENG-28 の <c>QueryDeviceInfo</c>) か、アプリ全体を
/// 管理者として実行しているときの同じプロセスからだけ呼ばれる (物理ディスクを開けるのは管理者だけ)。テスト・開発の PC では呼ばない
/// (偽のデバイスを使う)。
/// <list type="bullet">
/// <item>ATA のコマンドは <c>IOCTL_ATA_PASS_THROUGH</c> で送る。この IOCTL は Windows の定義で読み書きのアクセス権のハンドルを要するため、
/// ここだけで使う読み書きのハンドルを別に開く。送るのは <see cref="AtaCommands.IsAllowed"/> の読み取りだけのコマンドで、データを送る方向
/// (ATA_FLAGS_DATA_OUT) は使わない。書き込み保護モード (FOR-01) の間も、補助プロセスはこの取得を受け付ける (仕様 2)。</item>
/// <item>NVMe と記述子は <c>IOCTL_STORAGE_QUERY_PROPERTY</c> (読み取りのアクセス権で足りる) で取る。</item>
/// </list>
/// </summary>
internal static unsafe class Win32DeviceInfo
{
    private const uint IoctlAtaPassThrough = 0x0004D02C;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    public static int Query(SafeFileHandle readHandle, string path, DeviceInfoKind kind, out byte[] data)
    {
        data = [];
        if (!OperatingSystem.IsWindows())
        {
            return Win32Errors.NotSupported;
        }

        if (DeviceInfoPayloads.IsAta(kind))
        {
            return QueryAta(path, kind, out data);
        }

        return kind switch
        {
            DeviceInfoKind.NvmeSmartHealth => QueryProtocol(readHandle, NvmeStructures.NvmeDataTypeLogPage, NvmeStructures.NvmeLogPageHealthInfo, 0,
                NvmeStructures.HealthLogSize, out data),
            DeviceInfoKind.NvmeIdentifyController => QueryProtocol(readHandle, NvmeStructures.NvmeDataTypeIdentify, NvmeStructures.NvmeIdentifyCnsController, 0,
                NvmeStructures.IdentifySize, out data),
            DeviceInfoKind.NvmeIdentifyNamespace => QueryProtocol(readHandle, NvmeStructures.NvmeDataTypeIdentify, NvmeStructures.NvmeIdentifyCnsNamespace, 1,
                NvmeStructures.IdentifySize, out data),
            DeviceInfoKind.StorageDescriptor => QueryProperty(readHandle, NvmeStructures.StorageDeviceProperty, 1024, out data),
            DeviceInfoKind.TrimSupport => QueryProperty(readHandle, NvmeStructures.StorageDeviceTrimProperty, 12, out data),
            DeviceInfoKind.SeekPenalty => QueryProperty(readHandle, NvmeStructures.StorageDeviceSeekPenaltyProperty, 12, out data),
            _ => Win32Errors.InvalidParameter,
        };
    }

    private static int QueryProperty(SafeFileHandle handle, uint propertyId, int outputSize, out byte[] data)
    {
        byte[] query = NvmeStructures.BuildQuery(propertyId);
        byte[] output = new byte[outputSize];
        data = [];
        fixed (byte* q = query)
        fixed (byte* o = output)
        {
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, q, query.Length, o, output.Length, out int returned, IntPtr.Zero))
            {
                return NonZero(Marshal.GetLastPInvokeError());
            }

            data = output[..Math.Clamp(returned, 0, output.Length)];
            return 0;
        }
    }

    private static int QueryProtocol(SafeFileHandle handle, uint dataType, uint value, uint subValue, int length, out byte[] data)
    {
        byte[] query = NvmeStructures.BuildQuery(NvmeStructures.StorageDeviceProtocolSpecificProperty, dataType, value, subValue, length);
        data = [];
        fixed (byte* q = query)
        {
            // 入力と出力に同じバッファを使う (Windows の NVMe の取得の決まった使い方)。出力は STORAGE_PROTOCOL_DATA_DESCRIPTOR。
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, q, query.Length, q, query.Length, out int returned, IntPtr.Zero))
            {
                return NonZero(Marshal.GetLastPInvokeError());
            }

            byte[]? payload = NvmeStructures.ProtocolData(query.AsSpan(0, Math.Clamp(returned, 0, query.Length)));
            if (payload is null)
            {
                return Win32Errors.NotSupported;
            }

            data = payload;
            return 0;
        }
    }

    private static int QueryAta(string path, DeviceInfoKind kind, out byte[] data)
    {
        data = [];

        // ATA のコマンドは物理ディスクのハンドルにだけ送る。
        if (DevicePath.DiskNumber(path) is null)
        {
            return Win32Errors.NotSupported;
        }

        // READ NATIVE MAX ADDRESS は 48 bit (EXT) を先に試し、通らなければ 28 bit で読む。
        bool extended = true;
        if (AtaCommands.For(kind, extended) is not { } command)
        {
            return Win32Errors.InvalidParameter;
        }

        using SafeFileHandle handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return NonZero(Marshal.GetLastPInvokeError());
        }

        int error = Send(handle, command, out AtaResult? result);
        if (kind == DeviceInfoKind.ReadNativeMaxAddress && (error != 0 || result is { Failed: true }))
        {
            extended = false;
            command = AtaCommands.ReadNativeMaxAddress;
            error = Send(handle, command, out result);
        }

        if (error != 0 || result is null)
        {
            return error != 0 ? error : Win32Errors.NotSupported;
        }

        data = AtaCommands.EncodeResult(result, kind, extended);
        return 0;
    }

    private static int Send(SafeFileHandle handle, AtaTaskFile command, out AtaResult? result)
    {
        result = null;
        bool is64 = IntPtr.Size == 8;
        byte[] buffer = AtaCommands.BuildPassThrough(command, is64);
        fixed (byte* b = buffer)
        {
            if (!DeviceIoControl(handle, IoctlAtaPassThrough, b, buffer.Length, b, buffer.Length, out _, IntPtr.Zero))
            {
                return NonZero(Marshal.GetLastPInvokeError());
            }
        }

        result = AtaCommands.ParsePassThrough(buffer, command, is64);
        return 0;
    }

    private static int NonZero(int error) => error == 0 ? Win32Errors.GenFailure : error;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, int inputSize, void* output, int outputSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
