namespace HexEditor.ManualTests;

/// <summary>この PC の物理ディスク 1 台 (Get-PhysicalDisk の DeviceId、BusType、MediaType、SerialNumber)。</summary>
public sealed record LocalDisk(int Number, string Bus, string Media, string Serial);

/// <summary>
/// 実機のディスクを使う手動テスト: 準備の「この PC の … を用意します」を人にさせず、ツールがディスクを探して手順の番号を埋める。
/// </summary>
public static class LocalDisks
{
    /// <summary>Get-PhysicalDisk の結果を 1 台 1 行 (タブ区切り) で出す PowerShell の命令。</summary>
    public const string ListCommand = "Get-PhysicalDisk | ForEach-Object { \"$($_.DeviceId)`t$($_.BusType)`t$($_.MediaType)`t$($_.SerialNumber)\" }";

    private const string SataDisk = "この PC の SATA ディスク";
    private const string SataSsd = "この PC の SATA SSD";
    private const string NvmeSsd = "この PC の NVMe SSD";

    /// <summary>ツールが探せる「必要なもの」か。</summary>
    public static bool Handles(string need) => need is SataDisk or SataSsd or NvmeSsd;

    public static IReadOnlyList<LocalDisk> Parse(string output)
    {
        var disks = new List<LocalDisk>();
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 4 && int.TryParse(parts[0], out int number))
            {
                disks.Add(new LocalDisk(number, parts[1], parts[2], parts[3].Trim()));
            }
        }

        return disks;
    }

    /// <summary>「必要なもの」に合う最初のディスク。なければ null。</summary>
    public static LocalDisk? Find(IEnumerable<LocalDisk> disks, string need) => need switch
    {
        SataDisk => disks.FirstOrDefault(d => d.Bus == "SATA"),
        SataSsd => disks.FirstOrDefault(d => d.Bus == "SATA" && d.Media == "SSD"),
        NvmeSsd => disks.FirstOrDefault(d => d.Bus == "NVMe"),
        _ => null,
    };

    /// <summary>手順の「ディスク N」と「シリアル番号 S」を、実際の値にする。</summary>
    public static string Fill(string text, LocalDisk disk) =>
        text.Replace("ディスク N", $"ディスク {disk.Number}", StringComparison.Ordinal)
            .Replace("シリアル番号 S", $"シリアル番号 {disk.Serial}", StringComparison.Ordinal);
}
