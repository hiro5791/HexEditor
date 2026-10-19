using HexEditor.Core.Devices;
using HexEditor.Core.Files;

namespace HexEditor.Core.Forensics;

/// <summary>
/// 書き込みの対象が証拠 (FOR-03) に当たるかの判定 (FOR-30 の仕様 8 など、「証拠一覧のデータソースが対象の場合は実行しない」)。
/// 文字列の一致だけでは、証拠のボリューム (<c>\\.\E:</c>) を含む物理ディスク (<c>\\.\PhysicalDrive1</c>) への書き込みや、同じボリュームの
/// 別の書き方 (<c>\\?\Volume{…}</c>)、ファイルの別名のパスを見逃すため、次のように比べる。
/// <list type="bullet">
/// <item>ファイル同士: <see cref="CanonicalPath"/> でそろえて比べる。</item>
/// <item>物理ディスクとボリューム: ボリュームがそのディスクにあれば重なる (どちらが証拠でも)。</item>
/// <item>ボリューム同士: 同じボリューム (デバイスの一覧で同じもの) なら重なる。同じディスクの別のボリュームは重ならない。</item>
/// </list>
/// デバイスの一覧が要るのは、両方がデバイスのパスで文字列が違うときだけ (<paramref name="catalog"/> は遅延して呼ぶ)。
/// </summary>
public static class EvidenceOverlap
{
    public static bool Overlaps(string evidencePath, string targetPath, Func<DeviceCatalog?>? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(evidencePath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        string a = evidencePath.Trim().TrimEnd('\\'), b = targetPath.Trim().TrimEnd('\\');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool aDevice = DevicePath.IsValid(a), bDevice = DevicePath.IsValid(b);
        if (aDevice != bDevice)
        {
            return false;
        }

        if (!aDevice)
        {
            return string.Equals(CanonicalPath.Normalize(evidencePath), CanonicalPath.Normalize(targetPath), StringComparison.OrdinalIgnoreCase);
        }

        int? diskA = DevicePath.DiskNumber(a), diskB = DevicePath.DiskNumber(b);
        if (diskA is not null && diskB is not null)
        {
            return diskA == diskB;
        }

        DeviceCatalog? devices = catalog?.Invoke();
        if (devices is null)
        {
            return false;
        }

        VolumeDeviceInfo? volumeA = diskA is null ? FindVolume(devices, a) : null, volumeB = diskB is null ? FindVolume(devices, b) : null;
        if (diskA is { } da)
        {
            return volumeB?.DiskNumber == da;
        }

        if (diskB is { } db)
        {
            return volumeA?.DiskNumber == db;
        }

        return volumeA is not null && ReferenceEquals(volumeA, volumeB);
    }

    /// <summary>ボリュームのパス (<c>\\.\E:</c>、<c>\\?\Volume{…}</c>) に当たる一覧のボリューム。</summary>
    public static VolumeDeviceInfo? FindVolume(DeviceCatalog catalog, string path)
    {
        if (catalog.FindVolume(path) is { } v)
        {
            return v;
        }

        // \\.\E: はドライブ文字で探す。
        if (path.Length == 6 && path.StartsWith(@"\\.\", StringComparison.Ordinal) && path[5] == ':')
        {
            char letter = char.ToUpperInvariant(path[4]);
            return catalog.Volumes.FirstOrDefault(x => x.DriveLetter is { Length: > 0 } d && char.ToUpperInvariant(d[0]) == letter);
        }

        return null;
    }
}
