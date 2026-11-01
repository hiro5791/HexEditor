using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>実機のディスクを使う手動テストで、ツールがディスクを探して手順の番号を埋める。</summary>
public sealed class LocalDisksTests
{
    private const string Output = "0\tSATA\tHDD\tWD-1 \r\n1\tNVMe\tSSD\tACE4_2E00.\r\n2\tUSB\tUnspecified\tX\r\n";

    [Fact]
    public void The_disk_list_is_read_line_by_line()
    {
        IReadOnlyList<LocalDisk> disks = LocalDisks.Parse(Output);
        Assert.Equal(3, disks.Count);
        Assert.Equal(new LocalDisk(1, "NVMe", "SSD", "ACE4_2E00."), disks[1]);
    }

    [Theory]
    [InlineData("この PC の SATA ディスク", 0)]
    [InlineData("この PC の SATA SSD", null)]
    [InlineData("この PC の NVMe SSD", 1)]
    [InlineData("別のもの", null)]
    public void The_right_disk_is_found_for_a_need(string need, int? number) =>
        Assert.Equal(number, LocalDisks.Find(LocalDisks.Parse(Output), need)?.Number);

    [Fact]
    public void Steps_show_the_real_number_and_serial()
    {
        var disk = new LocalDisk(1, "NVMe", "SSD", "ABC");
        Assert.Equal("「ディスク 1」のタブ、シリアル番号 ABC", LocalDisks.Fill("「ディスク N」のタブ、シリアル番号 S", disk));
    }
}
