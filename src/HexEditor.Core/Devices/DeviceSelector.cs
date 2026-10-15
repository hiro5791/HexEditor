using HexEditor.Core.Processes;

namespace HexEditor.Core.Devices;

/// <summary>プロセスの指定 (<c>--process PID または名前</c>) に当たったもの。</summary>
public sealed record ProcessSelection(ProcessEntry? Match, IReadOnlyList<ProcessEntry> Candidates)
{
    /// <summary>名前が複数のプロセスに当たった (PID で指定し直してもらう)。</summary>
    public bool IsAmbiguous => Match is null && Candidates.Count > 1;
}

/// <summary>
/// コマンドラインのデバイス・プロセスの指定 (08 の AUTO-37: <c>--disk</c>、<c>--volume</c>、<c>--process</c>) を、一覧の項目に直す。
/// 指定の解釈と一覧からの選択だけで、デバイスは開かない (開く手順は GUI の通常の手順: 管理者権限の確認を含む。ENG-28、ENG-29、ENG-32)。
/// </summary>
public static class DeviceSelector
{
    /// <summary>ディスクの指定: <c>0</c>、<c>PhysicalDrive0</c>、<c>\\.\PhysicalDrive1</c>。</summary>
    public static bool TryParseDisk(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (DevicePath.TryParse(text, out DevicePathKind kind) && kind == DevicePathKind.PhysicalDrive)
        {
            return int.TryParse(text[(text.LastIndexOf('e') + 1)..], out number);
        }

        if (text.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            text = text["PhysicalDrive".Length..];
        }

        return text.Length is >= 1 and <= 3 && text.All(char.IsAsciiDigit) && int.TryParse(text, out number);
    }

    /// <summary>ボリュームの指定: <c>C:</c>、<c>C</c>、<c>C:\</c>、<c>\\.\C:</c>。</summary>
    public static bool TryParseVolume(string? text, out char letter)
    {
        letter = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (DevicePath.TryParse(text, out DevicePathKind kind) && kind == DevicePathKind.DriveLetter)
        {
            letter = char.ToUpperInvariant(text[4]);
            return true;
        }

        text = text.TrimEnd('\\', '/');
        if (text.Length == 2 && text[1] == ':')
        {
            text = text[..1];
        }

        if (text.Length == 1 && char.IsAsciiLetter(text[0]))
        {
            letter = char.ToUpperInvariant(text[0]);
            return true;
        }

        return false;
    }

    public static DiskInfo? FindDisk(DeviceCatalog catalog, int number) => catalog.Disks.FirstOrDefault(d => d.Number == number);

    public static VolumeDeviceInfo? FindVolume(DeviceCatalog catalog, char letter) =>
        catalog.Volumes.FirstOrDefault(v => v.DriveLetter is { Length: > 0 } d && char.ToUpperInvariant(d[0]) == letter);

    /// <summary>
    /// プロセスを選ぶ: 数字だけなら PID、それ以外は名前 (大文字・小文字を区別せず、<c>.exe</c> は省略できる)。名前が複数に当たれば
    /// <see cref="ProcessSelection.IsAmbiguous"/>。
    /// </summary>
    public static ProcessSelection SelectProcess(IReadOnlyList<ProcessEntry> processes, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return new ProcessSelection(null, []);
        }

        spec = spec.Trim();
        if (spec.All(char.IsAsciiDigit) && int.TryParse(spec, out int pid))
        {
            ProcessEntry? byPid = processes.FirstOrDefault(p => p.Pid == pid);
            return new ProcessSelection(byPid, byPid is null ? [] : [byPid]);
        }

        string bare = spec.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? spec[..^4] : spec;
        ProcessEntry[] named =
        [
            .. processes.Where(p => string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(p.Name), bare, StringComparison.OrdinalIgnoreCase)),
        ];
        return named.Length == 1 ? new ProcessSelection(named[0], named) : new ProcessSelection(null, named);
    }
}
