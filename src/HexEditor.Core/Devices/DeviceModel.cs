using System.Text.Json.Serialization;

namespace HexEditor.Core.Devices;

/// <summary>物理ディスクの接続方式 (ENG-29 の仕様 1)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiskBusType>))]
public enum DiskBusType
{
    Unknown,
    Sata,
    Ata,
    Nvme,
    Usb,
    Scsi,
    Sas,
    Sd,
    Mmc,
    Virtual,
    Other,
}

/// <summary>パーティションの形式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiskPartitionStyle>))]
public enum DiskPartitionStyle
{
    Unknown,
    Mbr,
    Gpt,

    /// <summary>パーティションなし。</summary>
    Raw,
}

/// <summary>BitLocker の状態 (ENG-29 の仕様 1・10)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BitLockerState>))]
public enum BitLockerState
{
    Unknown,
    NotEncrypted,

    /// <summary>暗号化されていて、ロックを解除してある (復号された内容を読める)。</summary>
    Unlocked,

    /// <summary>ロック中 (読めない)。</summary>
    Locked,
}

/// <summary>
/// 物理ディスク 1 つの情報 (ENG-29 の仕様 1)。取得できない項目は null (画面では「不明」)。
/// </summary>
public sealed record DiskInfo
{
    /// <summary>ディスク番号 (<c>\\.\PhysicalDriveN</c> の N)。</summary>
    public required int Number { get; init; }

    public string Path => DevicePath.PhysicalDrive(Number);

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public long? Size { get; init; }

    public DiskBusType Bus { get; init; }

    public DiskPartitionStyle PartitionStyle { get; init; }

    public int? LogicalSectorSize { get; init; }

    public int? PhysicalSectorSize { get; init; }

    public bool? Removable { get; init; }

    /// <summary>Windows が起動しているボリュームを含む。</summary>
    public bool? IsSystem { get; init; }
}

/// <summary>ボリューム 1 つの情報 (ENG-29 の仕様 1)。</summary>
public sealed record VolumeDeviceInfo
{
    /// <summary>開くときのパス (<c>\\.\C:</c> または <c>\\?\Volume{GUID}</c>)。</summary>
    public required string Path { get; init; }

    /// <summary>ドライブ文字 (<c>C:</c>)。なければ null。</summary>
    public string? DriveLetter { get; init; }

    /// <summary>マウントポイント (フォルダ)。</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public long? Size { get; init; }

    /// <summary>所属する物理ディスクの番号。複数のディスクにまたがるボリュームは最初のもの。</summary>
    public int? DiskNumber { get; init; }

    /// <summary>物理ディスクの中の開始位置 (バイト)。</summary>
    public long? DiskOffset { get; init; }

    /// <summary>取り外し可能な USB ストレージのボリューム (管理者権限なしで開ける。ENG-28 の概要の表)。</summary>
    public bool IsRemovableUsb { get; init; }

    public BitLockerState BitLocker { get; init; }

    /// <summary>Windows のシステムボリューム (ロックもディスマウントもできない。ENG-30 の仕様 3 の 3)。</summary>
    public bool IsSystem { get; init; }

    /// <summary>ページファイルがある。</summary>
    public bool HasPageFile { get; init; }

    /// <summary>このアプリが動いているボリューム。</summary>
    public bool HostsApplication { get; init; }

    /// <summary>Windows が使用中でロックできない (<see cref="IsSystem"/>・<see cref="HasPageFile"/>・<see cref="HostsApplication"/>)。</summary>
    [JsonIgnore]
    public bool IsInUseByWindows => IsSystem || HasPageFile || HostsApplication;

    /// <summary>画面に出す名前 (<c>C:</c>、マウントポイント、GUID の順に使えるもの)。</summary>
    [JsonIgnore]
    public string Name => DriveLetter ?? MountPoints.FirstOrDefault() ?? Path;
}

/// <summary>光学ドライブ (ENG-29 の仕様 1)。</summary>
public sealed record OpticalDriveInfo
{
    /// <summary><c>\\.\CdRomN</c> または <c>\\.\D:</c>。</summary>
    public required string Path { get; init; }

    public string? DriveLetter { get; init; }

    /// <summary>メディアが入っているか。不明なら null。</summary>
    public bool? HasMedia { get; init; }
}

/// <summary>「ディスクを開く」の一覧の内容。</summary>
public sealed record DeviceCatalog
{
    public static DeviceCatalog Empty { get; } = new();

    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];

    public IReadOnlyList<VolumeDeviceInfo> Volumes { get; init; } = [];

    public IReadOnlyList<OpticalDriveInfo> OpticalDrives { get; init; } = [];

    /// <summary>ディスクのパスまたはボリュームのパスの情報を探す。</summary>
    public DiskInfo? FindDisk(string path) => Disks.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    public VolumeDeviceInfo? FindVolume(string path) =>
        Volumes.FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>物理ディスクの範囲 [offset, offset + length) に重なるボリューム (ENG-30 の仕様 2)。</summary>
    public IReadOnlyList<VolumeDeviceInfo> VolumesOverlapping(int diskNumber, long offset, long length) =>
    [
        .. Volumes.Where(v => v.DiskNumber == diskNumber && v.DiskOffset is { } start && v.Size is { } size
            && start < offset + length && offset < start + size),
    ];
}

/// <summary>開いたデバイスの長さとセクタサイズ (ENG-28 の <c>GetGeometry</c>)。</summary>
public sealed record DeviceGeometry(long Length, int LogicalSectorSize, int PhysicalSectorSize, int Alignment = 1);

/// <summary>デバイスの操作の失敗。<see cref="ErrorCode"/> は Win32 のエラーコード。</summary>
public class DeviceException(int errorCode, string message) : IOException(message)
{
    public int ErrorCode { get; } = errorCode;

    /// <summary>エラーコードから例外を作る (メッセージは OS の文言)。</summary>
    public static DeviceException FromError(int errorCode, string? what = null)
    {
        string text = new System.ComponentModel.Win32Exception(errorCode).Message;
        return new DeviceException(errorCode, what is null ? text : $"{what}: {text}");
    }
}

/// <summary>よく使う Win32 のエラーコード。</summary>
public static class Win32Errors
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int AccessDenied = 5;
    public const int InvalidHandle = 6;
    public const int NotReady = 21;
    public const int WriteProtect = 19;
    public const int SectorNotFound = 27;
    public const int GenFailure = 31;
    public const int SharingViolation = 32;
    public const int LockViolation = 33;
    public const int NotSupported = 50;
    public const int InvalidParameter = 87;
    public const int BrokenPipe = 109;
    public const int NotLocked = 158;
    public const int PartialCopy = 299;
    public const int NoSuchDevice = 433;
    public const int NoAccess = 998;
    public const int DeviceNotConnected = 1167;
    public const int Cancelled = 1223;
    public const int Timeout = 1460;
    public const int ProcessAborted = 1067;
    public const int InvalidAddress = 487;
    public const int FveLocked = unchecked((int)0x80310000);

    /// <summary>デバイスがなくなったことを示すエラー (取り外し、補助プロセスの終了、プロセスの終了)。</summary>
    public static bool IsDisconnect(int error) =>
        error is DeviceNotConnected or NoSuchDevice or InvalidHandle or BrokenPipe or ProcessAborted or FileNotFound;
}
