using HexEditor.Core.Devices;
using HexEditor.Core.Expressions;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Tests.Expressions;

/// <summary>コマンドラインの範囲 (AUTO-36 の仕様 4) と、デバイス・プロセスの指定 (AUTO-37)。</summary>
public sealed class RangeExpressionTests
{
    private sealed class Context(long length) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    [Theory]
    [InlineData("0x10:0x20", DefaultRadix.Decimal, 16, 32)]
    [InlineData("10:20", DefaultRadix.Decimal, 10, 20)]
    [InlineData("10:20", DefaultRadix.Hexadecimal, 16, 32)]
    [InlineData("0x10..0x20", DefaultRadix.Decimal, 16, 16)]
    [InlineData("end-32:32", DefaultRadix.Decimal, 224, 32)]
    [InlineData("end-16..end", DefaultRadix.Decimal, 240, 16)]
    [InlineData("100", DefaultRadix.Decimal, 100, 156)]
    [InlineData("0:0", DefaultRadix.Decimal, 0, 0)]
    public void Ranges_are_start_length_or_start_end(string text, DefaultRadix radix, long start, long length)
    {
        Assert.True(RangeExpression.TryEvaluate(text, new Context(256), radix, out long s, out long l, out ExpressionException? error), error?.Message);
        Assert.Equal((start, length), (s, l));
    }

    [Fact]
    public void Expression_errors_are_reported_with_the_position()
    {
        Assert.False(RangeExpression.TryEvaluate("0x10+", new Context(256), DefaultRadix.Decimal, out _, out _, out ExpressionException? error));
        Assert.NotNull(error);
        Assert.Equal(5, error.Position);
        Assert.False(RangeExpression.TryEvaluate("1:", new Context(256), DefaultRadix.Decimal, out _, out _, out _));
    }

    [Theory]
    [InlineData(0, 256, 256, true)]
    [InlineData(224, 32, 256, true)]
    [InlineData(240, 32, 256, false)]
    [InlineData(-1, 1, 256, false)]
    [InlineData(257, 0, 256, false)]
    [InlineData(10, -1, 256, false)]
    public void Range_must_be_inside_the_data(long start, long length, long data, bool inside) =>
        Assert.Equal(inside, RangeExpression.IsInside(start, length, data));

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("12", true, 12)]
    [InlineData("PhysicalDrive3", true, 3)]
    [InlineData(@"\\.\PhysicalDrive1", true, 1)]
    [InlineData(@"\\.\PhysicalDrive12", true, 12)]
    [InlineData("", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData(@"\\.\C:", false, 0)]
    [InlineData("1234", false, 0)]
    public void Disk_specifications(string text, bool ok, int number)
    {
        Assert.Equal(ok, DeviceSelector.TryParseDisk(text, out int n));
        if (ok)
        {
            Assert.Equal(number, n);
        }
    }

    [Theory]
    [InlineData("C:", true, 'C')]
    [InlineData("d", true, 'D')]
    [InlineData(@"E:\", true, 'E')]
    [InlineData(@"\\.\f:", true, 'F')]
    [InlineData(@"\\.\F:", true, 'F')]
    [InlineData("CD", false, ' ')]
    [InlineData("1:", false, ' ')]
    public void Volume_specifications(string text, bool ok, char letter)
    {
        Assert.Equal(ok, DeviceSelector.TryParseVolume(text, out char l));
        if (ok)
        {
            Assert.Equal(letter, l);
        }
    }

    [Fact]
    public void Disks_and_volumes_are_found_in_the_catalog()
    {
        var catalog = new DeviceCatalog
        {
            Disks = [new DiskInfo { Number = 0 }, new DiskInfo { Number = 2 }],
            Volumes = [new VolumeDeviceInfo { Path = @"\\.\C:", DriveLetter = "C:" }, new VolumeDeviceInfo { Path = @"\\?\Volume{00000000-0000-0000-0000-000000000001}" }],
        };
        Assert.Equal(@"\\.\PhysicalDrive2", DeviceSelector.FindDisk(catalog, 2)?.Path);
        Assert.Null(DeviceSelector.FindDisk(catalog, 1));
        Assert.Equal(@"\\.\C:", DeviceSelector.FindVolume(catalog, 'C')?.Path);
        Assert.Null(DeviceSelector.FindVolume(catalog, 'D'));
    }

    [Fact]
    public void Processes_are_selected_by_pid_or_name()
    {
        ProcessEntry[] processes =
        [
            new() { Pid = 10, Name = "notepad.exe" },
            new() { Pid = 20, Name = "calc.exe" },
            new() { Pid = 30, Name = "svchost.exe" },
            new() { Pid = 31, Name = "svchost.exe" },
        ];
        Assert.Equal(20, DeviceSelector.SelectProcess(processes, "20").Match?.Pid);
        Assert.Null(DeviceSelector.SelectProcess(processes, "99").Match);
        Assert.Equal(10, DeviceSelector.SelectProcess(processes, "notepad").Match?.Pid);
        Assert.Equal(10, DeviceSelector.SelectProcess(processes, "NOTEPAD.EXE").Match?.Pid);
        ProcessSelection many = DeviceSelector.SelectProcess(processes, "svchost");
        Assert.True(many.IsAmbiguous);
        Assert.Equal(2, many.Candidates.Count);
        Assert.Null(DeviceSelector.SelectProcess(processes, "nothing").Match);
        Assert.Empty(DeviceSelector.SelectProcess(processes, "").Candidates);
    }
}
