using HexEditor.Core.Devices;

namespace HexEditor.Core.Forensics;

/// <summary>出力先の検査の結果 (12 の「出力先の検査」)。</summary>
public enum OutputProblem
{
    None,

    /// <summary>出力先のボリュームが、調査対象 (証拠・処理の入力元) と同じ物理ディスクにある。</summary>
    SameDisk,

    /// <summary>空き容量が足りない。</summary>
    NotEnoughSpace,

    /// <summary>出力先のファイルシステムの 1 ファイルの上限 (FAT32 の 4 GiB - 1 など) を超える。分割が必要。</summary>
    FileTooLarge,

    /// <summary>出力先に書けない。</summary>
    CannotWrite,
}

/// <summary>検査の結果。</summary>
public sealed record OutputCheck(OutputProblem Problem, string Detail = "", long? RequiredBytes = null, long? FreeBytes = null, int? DiskNumber = null, long? FileLimit = null)
{
    public bool Ok => Problem == OutputProblem.None;

    public static OutputCheck Pass { get; } = new(OutputProblem.None);
}

/// <summary>パスや入力元が載っている物理ディスクを調べる (出力先の検査用)。実機では <see cref="CatalogDiskLocator"/>、テストでは偽のカタログを渡す。</summary>
public interface IDiskLocator
{
    /// <summary>パス (ファイル・フォルダ) があるボリュームの物理ディスクの番号。分からなければ null (検査できない)。</summary>
    IReadOnlyList<int>? DisksOfPath(string path);

    /// <summary>デバイスのパス (<c>\\.\PhysicalDriveN</c>、<c>\\.\X:</c>) が属する物理ディスクの番号。</summary>
    IReadOnlyList<int>? DisksOfDevice(string devicePath);

    /// <summary>パスのあるボリュームの空き容量。</summary>
    long? FreeBytes(string path);

    /// <summary>パスのあるボリュームのファイルシステム名 (<c>NTFS</c>、<c>FAT32</c>)。</summary>
    string? FileSystemName(string path);
}

/// <summary>デバイスの一覧 (<see cref="DeviceCatalog"/>) から物理ディスクを調べる。空き容量とファイルシステム名は <see cref="DriveInfo"/> から。</summary>
public sealed class CatalogDiskLocator(Func<DeviceCatalog> catalog) : IDiskLocator
{
    public IReadOnlyList<int>? DisksOfPath(string path)
    {
        // ジャンクション・シンボリックリンクをたどった本当のパスで調べる (C:\out が調査対象のボリュームへのジャンクションのとき、
        // ドライブ文字だけで判定すると C: のディスクと誤る)。
        if (!Files.CanonicalPath.TryNormalize(path, out string full))
        {
            return null;
        }

        string? root = Path.GetPathRoot(full);
        if (root is not { Length: >= 2 } || root[1] != ':')
        {
            return null;
        }

        // フォルダのマウントポイント (C:\mnt\evidence\ に別のボリューム) は、いちばん長く一致するものを選ぶ。
        string withSlash = full.EndsWith('\\') ? full : full + "\\";
        VolumeDeviceInfo? best = null;
        int bestLength = -1;
        foreach (VolumeDeviceInfo v in catalog().Volumes)
        {
            IEnumerable<string> roots = v.MountPoints;
            if (v.DriveLetter is { Length: > 0 } d)
            {
                roots = roots.Append(char.ToUpperInvariant(d[0]) + @":\");
            }

            foreach (string mount in roots)
            {
                string m = mount.EndsWith('\\') ? mount : mount + "\\";
                if (m.Length > bestLength && withSlash.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                {
                    best = v;
                    bestLength = m.Length;
                }
            }
        }

        return best?.DiskNumber is { } n ? [n] : null;
    }

    public IReadOnlyList<int>? DisksOfDevice(string devicePath)
    {
        if (DevicePath.DiskNumber(devicePath) is { } disk)
        {
            return [disk];
        }

        VolumeDeviceInfo? volume = EvidenceOverlap.FindVolume(catalog(), devicePath);
        return volume?.DiskNumber is { } n ? [n] : null;
    }

    public long? FreeBytes(string path) => Drive(path)?.AvailableFreeSpace;

    public string? FileSystemName(string path) => Drive(path)?.DriveFormat;

    private static DriveInfo? Drive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// 出力先の検査 (12 の共通の前提「出力先の検査」)。イメージ・切り出したファイル・レポートなどの出力先は、調査対象のディスク (証拠一覧に登録された
/// デバイスと処理の入力元) 上のボリュームであってはならず、空き容量が足り、1 ファイルの上限を超えない必要がある。
/// イメージファイルやファイルが入力元の場合、そのファイルのあるディスクは調査対象ではない (ファイルは読み取り専用で読むだけ)。
/// </summary>
public static class OutputGuard
{
    /// <summary>FAT32 の 1 ファイルの上限 (4 GiB - 1)。</summary>
    public const long Fat32FileLimit = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>
    /// 検査する。<paramref name="inputDevices"/> は処理の入力元と証拠一覧のデバイス (ディスク・ボリューム) のパス。
    /// <paramref name="requiredBytes"/> が分からなければ 0。<paramref name="largestFileBytes"/> は 1 ファイルの最大の大きさ (分からなければ 0)。
    /// </summary>
    public static OutputCheck Check(string outputFolder, IEnumerable<string> inputDevices, long requiredBytes, long largestFileBytes, IDiskLocator locator)
    {
        string[] devices = [.. inputDevices.Where(d => !string.IsNullOrWhiteSpace(d))];
        if (devices.Length > 0)
        {
            IReadOnlyList<int>? outputDisks = locator.DisksOfPath(outputFolder);
            if (outputDisks is null)
            {
                // 出力先のディスクを特定できないときは、調査対象でないと言い切れないので実行しない。
                return new OutputCheck(OutputProblem.SameDisk, "The disk that holds the output folder could not be identified.");
            }

            var inputDisks = new HashSet<int>();
            foreach (string device in devices)
            {
                if (locator.DisksOfDevice(device) is { } list)
                {
                    inputDisks.UnionWith(list);
                }
            }

            int? conflict = outputDisks.Cast<int?>().FirstOrDefault(d => d is { } n && inputDisks.Contains(n));
            if (conflict is { } disk)
            {
                return new OutputCheck(OutputProblem.SameDisk, $"The output folder is on disk {disk}, which is under investigation.", DiskNumber: disk);
            }
        }

        if (requiredBytes > 0 && locator.FreeBytes(outputFolder) is { } free && free < requiredBytes)
        {
            return new OutputCheck(OutputProblem.NotEnoughSpace, "Not enough free space.", requiredBytes, free);
        }

        if (largestFileBytes > 0 && string.Equals(locator.FileSystemName(outputFolder), "FAT32", StringComparison.OrdinalIgnoreCase) && largestFileBytes > Fat32FileLimit)
        {
            return new OutputCheck(OutputProblem.FileTooLarge, "The file system cannot hold a file this large.", largestFileBytes, FileLimit: Fat32FileLimit);
        }

        return OutputCheck.Pass;
    }
}
