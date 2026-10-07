using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.Core.Saving;

/// <summary>
/// 保存先のボリュームの情報 (ENG-25)。<see cref="AvailableFreeSpace"/> は呼び出し元のユーザーが使える容量 (クォータを考慮。
/// 仕様 2)。取得できない場合 (一部のネットワークドライブ) は null で、確認を省略する。
/// </summary>
public sealed record VolumeInfo(string Name, string? FileSystem, long? AvailableFreeSpace)
{
    /// <summary>FAT32 の 1 ファイルの最大サイズ (4 GiB − 1 バイト)。</summary>
    public const long Fat32MaxFileSize = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>ファイルシステムの 1 ファイルの最大サイズ (ENG-25 の仕様 5)。上限がない (または分からない) 場合は null。</summary>
    public long? MaxFileSize => FileSystem?.ToUpperInvariant() switch
    {
        "FAT32" or "FAT" or "FAT16" or "FAT12" => Fat32MaxFileSize,
        _ => null,
    };
}

/// <summary>ボリュームの情報を取る (テストでは差し替える)。</summary>
public interface IVolumeInfoProvider
{
    /// <summary><paramref name="folder"/> を含むボリュームの情報。取得できなければ null。</summary>
    VolumeInfo? GetVolume(string folder);
}

/// <summary>
/// OS からボリュームの情報を取る。ボリュームのマウントポイントのフォルダや UNC パスでも、そのフォルダを含むボリュームの値を返す
/// (<c>GetVolumePathName</c> と <c>GetDiskFreeSpaceEx</c>)。
/// </summary>
public sealed class SystemVolumeInfoProvider : IVolumeInfoProvider
{
    public static SystemVolumeInfoProvider Instance { get; } = new();

    public VolumeInfo? GetVolume(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FromDriveInfo(folder);
        }

        string full = Path.GetFullPath(folder);
        var root = new StringBuilder(1024);
        string volume = GetVolumePathName(full, root, root.Capacity) ? root.ToString() : Path.GetPathRoot(full) ?? full;
        if (!volume.EndsWith('\\'))
        {
            volume += '\\';
        }

        long? available = GetDiskFreeSpaceEx(volume, out ulong freeForCaller, out _, out _) ? (long)Math.Min(freeForCaller, long.MaxValue) : null;
        var fsName = new StringBuilder(64);
        string? fileSystem = GetVolumeInformation(volume, null, 0, out _, out _, out _, fsName, fsName.Capacity) ? fsName.ToString() : null;
        return new VolumeInfo(volume.TrimEnd('\\'), fileSystem, available);
    }

    private static VolumeInfo? FromDriveInfo(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return new VolumeInfo(drive.Name, drive.DriveFormat, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int nFileSystemNameSize);
}
