using System.Globalization;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Devices;

/// <summary>「範囲を指定して開く」の入力の問題 (ENG-29 の仕様 2、ENG-32 の仕様 2)。</summary>
public enum DeviceRangeError
{
    None,

    /// <summary>開始がセクタ境界でない (バイトで指定した場合)。</summary>
    StartNotAligned,

    /// <summary>開始がデバイス・アドレス空間の外。</summary>
    StartOutside,

    /// <summary>長さが 0 以下。</summary>
    LengthInvalid,
}

/// <summary>
/// ディスク・プロセスを範囲を指定して開くときの、入力値からバイトの範囲への変換 (ENG-13 と同じ入力。単位はセクタ数またはバイト)。
/// 長さが末尾を越える場合は末尾までに切り詰める (ENG-13 の仕様 3 と同じ)。
/// </summary>
public static class DeviceRange
{
    /// <summary>
    /// 開始・長さを (単位が <paramref name="inSectors"/> ならセクタ数として) バイトの範囲にする。<paramref name="sectorSize"/> が 1 なら揃えない
    /// (プロセスメモリ)。長さが null なら末尾まで。
    /// </summary>
    public static (long Start, long Length, DeviceRangeError Error, bool Truncated) Resolve(long start, long? length, bool inSectors, int sectorSize,
        long totalLength)
    {
        int unit = inSectors ? sectorSize : 1;
        long startBytes;
        long? lengthBytes;
        try
        {
            startBytes = checked(start * unit);
            lengthBytes = length is { } l ? checked(l * unit) : null;
        }
        catch (OverflowException)
        {
            return (0, 0, DeviceRangeError.StartOutside, false);
        }

        if (startBytes < 0 || startBytes >= totalLength)
        {
            return (0, 0, DeviceRangeError.StartOutside, false);
        }

        if (sectorSize > 1 && startBytes % sectorSize != 0)
        {
            return (0, 0, DeviceRangeError.StartNotAligned, false);
        }

        if (lengthBytes is <= 0)
        {
            return (0, 0, DeviceRangeError.LengthInvalid, false);
        }

        long available = totalLength - startBytes;
        long len = Math.Min(lengthBytes ?? available, available);
        return (startBytes, len, DeviceRangeError.None, lengthBytes is { } want && want > available);
    }
}

/// <summary>「プロセスを開く」の一覧の並べ替えの列 (ENG-32 の仕様 1)。</summary>
public enum ProcessSort
{
    Name,
    Pid,
    Memory,
    User,
}

/// <summary>「プロセスを開く」の一覧の絞り込みと並べ替え (ENG-32 の仕様 1)。</summary>
public static class ProcessListModel
{
    /// <summary>
    /// 名前・PID・ウィンドウのタイトルの部分一致で絞り込み (大文字小文字を区別しない)、<paramref name="ownOnly"/> なら自分 (同じユーザー) の
    /// プロセスだけにする。
    /// </summary>
    public static List<ProcessEntry> Filter(IEnumerable<ProcessEntry> entries, string? text, bool ownOnly, ProcessSort sort = ProcessSort.Name,
        bool descending = false)
    {
        IEnumerable<ProcessEntry> result = entries;
        if (ownOnly)
        {
            result = result.Where(p => p.IsCurrentUser);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            string t = text.Trim();
            result = result.Where(p => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                || p.Pid.ToString(CultureInfo.InvariantCulture).Contains(t, StringComparison.Ordinal)
                || (p.WindowTitle?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        IOrderedEnumerable<ProcessEntry> ordered = sort switch
        {
            ProcessSort.Pid => descending ? result.OrderByDescending(p => p.Pid) : result.OrderBy(p => p.Pid),
            ProcessSort.Memory => descending ? result.OrderByDescending(p => p.CommitBytes ?? -1) : result.OrderBy(p => p.CommitBytes ?? -1),
            ProcessSort.User => descending
                ? result.OrderByDescending(p => p.User ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                : result.OrderBy(p => p.User ?? string.Empty, StringComparer.CurrentCultureIgnoreCase),
            _ => descending
                ? result.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                : result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return [.. ordered.ThenBy(p => p.Pid)];
    }
}
