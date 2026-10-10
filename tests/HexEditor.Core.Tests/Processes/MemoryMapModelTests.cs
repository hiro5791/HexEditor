using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Processes;

/// <summary>メモリマップの一覧 (ENG-33 の仕様 1・2・4)、プロセスの一覧の絞り込み (ENG-32 の仕様 1)、範囲の入力 (ENG-29 の仕様 2)。</summary>
public sealed class MemoryMapModelTests
{
    private static readonly MemoryRegion[] Regions =
    [
        new(0x10000, 0x1000, RegionState.Commit, PageProtection.ExecuteRead, RegionType.Image),
        new(0x11000, 0x2000, RegionState.Commit, PageProtection.ReadWrite, RegionType.Image),
        new(0x13000, 0x5000, RegionState.Free, 0, RegionType.None),
        new(0x18000, 0x1000, RegionState.Reserve, 0, RegionType.Private),
        new(0x19000, 0x1000, RegionState.Commit, PageProtection.ReadWrite | PageProtection.Guard, RegionType.Private),
        new(0x1A000, 0x4000, RegionState.Commit, PageProtection.ReadOnly, RegionType.Mapped, @"\Device\HarddiskVolume3\Windows\data.nls"),
    ];

    private static readonly ProcessModule[] Modules = [new("app.exe", 0x10000, 0x3000, @"C:\app.exe")];

    [Fact]
    public void Rows_carry_state_protection_type_and_module_or_file_name()
    {
        List<MemoryMapRow> rows = MemoryMapModel.Build(Regions, Modules, 0, 0x100000);
        Assert.Equal(6, rows.Count);
        Assert.Equal("app.exe", rows[0].Name);
        Assert.Equal("RX", rows[0].ProtectText);
        Assert.Equal("RW+G", rows[4].ProtectText);
        Assert.False(rows[4].IsReadable);
        Assert.Equal("data.nls", rows[5].Name);
        Assert.Equal(string.Empty, rows[2].ProtectText);
        Assert.Equal(0x1A000 + 0x4000, rows[5].End);
    }

    [Fact]
    public void Free_regions_are_hidden_by_default_and_filters_match_name_protection_and_state()
    {
        List<MemoryMapRow> rows = MemoryMapModel.Build(Regions, Modules, 0, 0x100000);
        Assert.DoesNotContain(MemoryMapModel.Filter(rows, null, showFree: false), r => r.State == RegionState.Free);
        Assert.Contains(MemoryMapModel.Filter(rows, null, showFree: true), r => r.State == RegionState.Free);

        Assert.Equal(2, MemoryMapModel.Filter(rows, "app.exe", false).Count);
        Assert.Single(MemoryMapModel.Filter(rows, "rx", false));
        Assert.Single(MemoryMapModel.Filter(rows, "reserve", false));
        Assert.Single(MemoryMapModel.Filter(rows, "予約", false, stateName: s => s == RegionState.Reserve ? "予約" : s.ToString()));
    }

    [Fact]
    public void Rows_sort_by_any_column_and_the_cursor_row_is_found()
    {
        List<MemoryMapRow> rows = MemoryMapModel.Build(Regions, Modules, 0, 0x100000);
        List<MemoryMapRow> bySize = MemoryMapModel.Filter(rows, null, true, MemoryMapSort.Size, descending: true);
        Assert.Equal(0x5000, bySize[0].Size);
        List<MemoryMapRow> byName = MemoryMapModel.Filter(rows, null, false, MemoryMapSort.Name);
        Assert.Equal("app.exe", byName[^2].Name);

        // カーソルのある領域 (仕様 4)。
        Assert.Equal(1, MemoryMapModel.IndexOfOffset(rows, 0x12345));
        Assert.Equal(-1, MemoryMapModel.IndexOfOffset(rows, 0x5));
    }

    [Fact]
    public void A_module_range_is_clipped_and_offsets_start_at_zero()
    {
        // モジュールだけを開いた場合 (ENG-32 の仕様 6): オフセット 0 が開始アドレス。
        List<MemoryMapRow> rows = MemoryMapModel.Build(Regions, Modules, 0x10800, 0x1000);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].Offset);
        Assert.Equal(0x10800, rows[0].Start);
        Assert.Equal(0x800, rows[0].Size);
        Assert.Equal(0x800, rows[1].Offset);
    }

    [Fact]
    public void Process_list_filters_by_name_pid_and_title_and_own_processes()
    {
        ProcessEntry[] entries =
        [
            new() { Pid = 100, Name = "notepad.exe", WindowTitle = "memo.txt - Notepad", IsCurrentUser = true, CommitBytes = 10 },
            new() { Pid = 2345, Name = "svchost.exe", IsCurrentUser = false, CommitBytes = 50 },
            new() { Pid = 777, Name = "Calc.exe", IsCurrentUser = true, CommitBytes = 30 },
        ];
        Assert.Equal(["Calc.exe", "notepad.exe", "svchost.exe"], ProcessListModel.Filter(entries, null, false).Select(p => p.Name));
        Assert.Equal(["notepad.exe"], ProcessListModel.Filter(entries, "memo", false).Select(p => p.Name));
        Assert.Equal(["svchost.exe"], ProcessListModel.Filter(entries, "234", false).Select(p => p.Name));
        Assert.Equal(["Calc.exe", "notepad.exe"], ProcessListModel.Filter(entries, null, ownOnly: true).Select(p => p.Name));
        Assert.Equal([2345, 777, 100], ProcessListModel.Filter(entries, null, false, ProcessSort.Memory, descending: true).Select(p => p.Pid));
    }

    [Fact]
    public void Device_ranges_in_sectors_or_bytes_are_resolved_and_validated()
    {
        const long total = 1024 * 1024;
        Assert.Equal((4096L, 1024L, DeviceRangeError.None, false), DeviceRange.Resolve(8, 2, inSectors: true, 512, total));
        Assert.Equal((4096L, total - 4096, DeviceRangeError.None, false), DeviceRange.Resolve(4096, null, inSectors: false, 512, total));
        Assert.Equal(DeviceRangeError.StartNotAligned, DeviceRange.Resolve(100, 10, inSectors: false, 512, total).Error);
        Assert.Equal(DeviceRangeError.StartOutside, DeviceRange.Resolve(total, 10, inSectors: false, 512, total).Error);
        Assert.Equal(DeviceRangeError.LengthInvalid, DeviceRange.Resolve(0, 0, inSectors: false, 512, total).Error);

        // 末尾を越える長さは切り詰める。
        (long start, long length, DeviceRangeError error, bool truncated) = DeviceRange.Resolve(total - 512, 4096, inSectors: false, 512, total);
        Assert.Equal((total - 512, 512L, DeviceRangeError.None, true), (start, length, error, truncated));

        // プロセスメモリ (揃えない)。
        Assert.Equal(DeviceRangeError.None, DeviceRange.Resolve(0x1001, 3, inSectors: false, 1, 0x10000).Error);
    }

    [Fact]
    public void Typing_into_an_unallocated_process_range_is_refused()
    {
        // ENG-34 の仕様 7: 未割り当ての範囲は編集できない (「この範囲は割り当てられていないため編集できません」)。
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Processes =
            [
                new FakeProcessSpec
                {
                    Pid = 42, Name = "t.exe", Regions = [new FakeRegionSpec { Base = 0x10000, Size = 0x1000, Protect = PageProtection.ReadWrite }],
                },
            ],
        });
        var source = new ProcessMemoryByteSource(access.Open(42, writable: true), access, new ProcessOpenInfo { DisplayName = "t" });
        using var doc = new Document(source);
        var editor = new EditorState(doc);
        editor.GoTo(0x10000);
        Assert.Equal(EditResult.Done, editor.TypeHexDigit('A'));
        editor.GoTo(0x20000);
        Assert.Equal(EditResult.Unallocated, editor.TypeHexDigit('A'));
        Assert.Equal(EditResult.Unallocated, editor.TypeText("x"));
    }
}
