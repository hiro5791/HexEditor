using System.Globalization;
using System.Text;
using HexEditor.Core.Devices;

namespace HexEditor.Core.Forensics.Images;

/// <summary>出力先のボリュームの情報 (空き容量、ファイルシステム、所属する物理ディスク)。分からない項目は null。</summary>
public sealed record OutputVolumeInfo(long? FreeBytes, string? FileSystem, int? DiskNumber);

/// <summary>出力先のボリュームを調べる口 (テストでは偽物に置き換える)。</summary>
public interface IOutputVolumeProbe
{
    OutputVolumeInfo Describe(string directory);
}

/// <summary>
/// 既定の実装: 空き容量とファイルシステムは <see cref="DriveInfo"/>、物理ディスクの番号はデバイスの一覧 (ENG-29 の列挙。ボリュームを開かない)
/// のドライブ文字から求める。
/// </summary>
public sealed class DefaultOutputVolumeProbe(Func<DeviceCatalog>? catalog = null) : IOutputVolumeProbe
{
    public OutputVolumeInfo Describe(string directory)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory;
            var drive = new DriveInfo(root);
            long? free = drive.IsReady ? drive.AvailableFreeSpace : null;
            string? fs = drive.IsReady ? drive.DriveFormat : null;
            int? disk = null;
            if (catalog is not null && root.Length >= 2 && root[1] == ':')
            {
                string letter = root[..2];
                try
                {
                    disk = catalog().Volumes.FirstOrDefault(v => string.Equals(v.DriveLetter, letter, StringComparison.OrdinalIgnoreCase))?.DiskNumber;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                }
            }

            return new OutputVolumeInfo(free, fs, disk);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new OutputVolumeInfo(null, null, null);
        }
    }
}

/// <summary>出力先の検査の結果 (共通の前提「出力先の検査」)。</summary>
public enum OutputProblem
{
    None,

    /// <summary>入力元と同じ物理ディスク上のボリューム。</summary>
    SameDiskAsSource,

    /// <summary>証拠一覧に登録されたディスク上のボリューム。</summary>
    OnEvidenceDisk,

    /// <summary>空き容量が足りない。</summary>
    InsufficientSpace,

    /// <summary>1 ファイルの上限 (FAT32 の 4 GiB − 1 など) を超える。分割が必要。</summary>
    FileTooLarge,
}

public sealed record OutputCheckResult(OutputProblem Problem, long Required, long? Free, long? FileLimit, string? FileSystem)
{
    public bool Ok => Problem == OutputProblem.None;
}

/// <summary>出力先の検査 (共通の前提「出力先の検査」1〜3)。</summary>
public static class ImageOutputCheck
{
    /// <summary>ファイルシステムの 1 ファイルの上限 (なければ null)。</summary>
    public static long? FileSizeLimit(string? fileSystem) => fileSystem?.ToUpperInvariant() switch
    {
        "FAT32" or "FAT" or "FAT16" or "FAT12" => 4L * 1024 * 1024 * 1024 - 1,
        _ => null,
    };

    /// <summary>
    /// 出力先を調べる。<paramref name="required"/> は必要な容量の目安、<paramref name="largestFile"/> は出力する 1 ファイルの最大の大きさ
    /// (分割しない raw はイメージ全体)。<paramref name="spaceIsEstimate"/> なら空き容量の不足は警告にとどめる (E01 の圧縮。FOR-20 の仕様 6)。
    /// 空き容量の不足を警告にとどめた場合も <see cref="OutputProblem.InsufficientSpace"/> を返す (呼び出し側が警告として扱う)。
    /// </summary>
    public static OutputCheckResult Check(string directory, long required, long largestFile, int? sourceDisk, IReadOnlyList<int> evidenceDisks,
        IOutputVolumeProbe probe)
    {
        OutputVolumeInfo info = probe.Describe(directory);
        long? limit = FileSizeLimit(info.FileSystem);
        if (info.DiskNumber is int disk)
        {
            if (sourceDisk == disk)
            {
                return new OutputCheckResult(OutputProblem.SameDiskAsSource, required, info.FreeBytes, limit, info.FileSystem);
            }

            if (evidenceDisks.Contains(disk))
            {
                return new OutputCheckResult(OutputProblem.OnEvidenceDisk, required, info.FreeBytes, limit, info.FileSystem);
            }
        }

        if (limit is long l && largestFile > l)
        {
            return new OutputCheckResult(OutputProblem.FileTooLarge, required, info.FreeBytes, limit, info.FileSystem);
        }

        if (info.FreeBytes is long free && free < required)
        {
            return new OutputCheckResult(OutputProblem.InsufficientSpace, required, free, limit, info.FileSystem);
        }

        return new OutputCheckResult(OutputProblem.None, required, info.FreeBytes, limit, info.FileSystem);
    }
}

/// <summary>含めた範囲の一覧 <c>&lt;名前&gt;.ranges.csv</c> (FOR-23 の仕様 3。開始 LBA とセクタ数)。</summary>
public static class RangesCsv
{
    public const string Header = "start_lba,sector_count";

    public static string PathFor(string logPath) => Path.ChangeExtension(logPath, null) + ".ranges.csv";

    public static void Write(string path, IEnumerable<SectorRange> ranges)
    {
        HexEditor.Core.Forensics.WriteBlock.WriteBlockGate.Current.EnsureFileWrite(path);
        var s = new StringBuilder(Header).Append("\r\n");
        foreach (SectorRange r in ranges)
        {
            s.Append(r.StartSector.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.SectorCount.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }

    /// <summary>読む。形の違う行は飛ばす (行数の上限 1,000 万)。</summary>
    public static IReadOnlyList<SectorRange> Parse(string text)
    {
        var result = new List<SectorRange>();
        foreach (string raw in text.Split('\n').Take(10_000_000))
        {
            string line = raw.Trim();
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long count) && count > 0 && start <= long.MaxValue - count)
            {
                result.Add(new SectorRange(start, count));
            }
        }

        return result;
    }

    /// <summary>イメージの隣の <c>.ranges.csv</c> (取得ログと同じ名前) を探す。なければ null。</summary>
    public static string? FindFor(string imageFile, ImageFormat format)
    {
        string path = PathFor(AcquisitionLog.PathFor(imageFile, format));
        return File.Exists(path) ? path : null;
    }

    /// <summary>含めなかった範囲 (バイト。[0, length) の中で、含めた範囲の補集合)。</summary>
    public static IReadOnlyList<(long Offset, long Length)> NotIncluded(IReadOnlyList<SectorRange> included, int sectorSize, long length)
    {
        var result = new List<(long, long)>();
        long at = 0;
        foreach (SectorRange r in included.OrderBy(r => r.StartSector))
        {
            long start = Math.Min(length, r.StartSector * sectorSize);
            if (start > at)
            {
                result.Add((at, start - at));
            }

            at = Math.Max(at, Math.Min(length, r.EndSector * sectorSize));
        }

        if (at < length)
        {
            result.Add((at, length - at));
        }

        return result;
    }
}
