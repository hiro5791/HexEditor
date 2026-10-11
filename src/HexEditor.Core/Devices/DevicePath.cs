using System.Text.RegularExpressions;

namespace HexEditor.Core.Devices;

/// <summary>デバイスのパスの種類。</summary>
public enum DevicePathKind
{
    /// <summary><c>\\.\PhysicalDriveN</c></summary>
    PhysicalDrive,

    /// <summary><c>\\.\X:</c></summary>
    DriveLetter,

    /// <summary><c>\\?\Volume{GUID}</c></summary>
    VolumeGuid,

    /// <summary><c>\\.\CdRomN</c></summary>
    CdRom,
}

/// <summary>
/// 開けるデバイスのパス (ENG-28 の仕様 4 の <c>OpenDevice</c>)。受け付けるのは決まった 4 つの形式だけで、それ以外 (ファイルのパス、
/// <c>..</c> を含むもの、末尾に何か付いたもの) はすべて拒否する。補助プロセスと UI のプロセスの両方がこの判定を使う。
/// </summary>
public static partial class DevicePath
{
    [GeneratedRegex(@"^\\\\\.\\PhysicalDrive(?<n>[0-9]{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDrivePattern();

    [GeneratedRegex(@"^\\\\\.\\(?<l>[A-Za-z]):$", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetterPattern();

    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeGuidPattern();

    [GeneratedRegex(@"^\\\\\.\\CdRom(?<n>[0-9]{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CdRomPattern();

    /// <summary><c>\\.\PhysicalDriveN</c>。</summary>
    public static string PhysicalDrive(int number) => $@"\\.\PhysicalDrive{number}";

    /// <summary><c>\\.\X:</c>。</summary>
    public static string Drive(char letter) => $@"\\.\{char.ToUpperInvariant(letter)}:";

    /// <summary>決まった形式のパスなら true。</summary>
    public static bool TryParse(string? path, out DevicePathKind kind)
    {
        kind = default;
        if (string.IsNullOrEmpty(path) || path.Length > 64 || path.Contains('\0'))
        {
            return false;
        }

        if (PhysicalDrivePattern().IsMatch(path))
        {
            kind = DevicePathKind.PhysicalDrive;
            return true;
        }

        if (DriveLetterPattern().IsMatch(path))
        {
            kind = DevicePathKind.DriveLetter;
            return true;
        }

        if (VolumeGuidPattern().IsMatch(path))
        {
            kind = DevicePathKind.VolumeGuid;
            return true;
        }

        if (CdRomPattern().IsMatch(path))
        {
            kind = DevicePathKind.CdRom;
            return true;
        }

        return false;
    }

    public static bool IsValid(string? path) => TryParse(path, out _);

    /// <summary>ボリュームのパス (ドライブ文字か GUID) か。</summary>
    public static bool IsVolume(string path) => TryParse(path, out DevicePathKind kind) && kind is DevicePathKind.DriveLetter or DevicePathKind.VolumeGuid;

    /// <summary>物理ディスクの番号。物理ディスクのパスでなければ null。</summary>
    public static int? DiskNumber(string path) =>
        PhysicalDrivePattern().Match(path) is { Success: true } m ? int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
}
