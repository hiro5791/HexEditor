using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Processes;

/// <summary>ENG-33 メモリ領域・モジュールの一覧 (Core レベル: 領域マップと移動)。</summary>
public sealed class MemoryMapTests
{
    private static ProcessMemoryByteSource Open(FakeProcessSpec spec)
    {
        var access = new FakeProcessAccess(new FakeProcessListSpec { Elevated = true, Processes = [spec] });
        IProcessMemory memory = access.Open(spec.Pid, writable: false);
        return new ProcessMemoryByteSource(memory, access, new ProcessOpenInfo { DisplayName = $"{spec.Name} (PID {spec.Pid})" });
    }

    [Fact]
    [Trait("TC", "TC-ENG-33-02")]
    public void Next_region_skips_a_reserved_range()
    {
        // 領域 A (コミット 64 KiB)、予約のみ 1 MiB、領域 B (コミット 64 KiB)。
        long a = 0x100000, reserved = a + 0x10000, b = reserved + 0x100000;
        ProcessMemoryByteSource source = Open(new FakeProcessSpec
        {
            Pid = 10,
            AddressLimit = b + 0x10000,
            Regions =
            [
                new FakeRegionSpec { Base = a, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.ReadWrite },
                new FakeRegionSpec { Base = reserved, Size = 0x100000, State = RegionState.Reserve, Protect = 0 },
                new FakeRegionSpec { Base = b, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.ReadWrite },
            ],
        });
        using (source)
        {
            var editor = new EditorState(new Document(source)) { VisibleRows = 10 };
            editor.GoTo(a);
            Assert.True(editor.MoveNextRegion());
            Assert.Equal(b, editor.Cursor);
            Assert.True(editor.MovePreviousRegion());
            Assert.Equal(a, editor.Cursor);
        }
    }

    [Fact]
    public void The_region_map_marks_unreadable_ranges_without_reading_them()
    {
        long a = 0x100000;
        ProcessMemoryByteSource source = Open(new FakeProcessSpec
        {
            Pid = 11,
            AddressLimit = a + 0x30000,
            Regions =
            [
                new FakeRegionSpec { Base = a, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.ReadWrite, Data = "AABBCCDD" },
                new FakeRegionSpec { Base = a + 0x10000, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.NoAccess },
                new FakeRegionSpec { Base = a + 0x20000, Size = 0x10000, State = RegionState.Reserve, Protect = 0 },
            ],
        });
        using (source)
        {
            // アドレス空間全体を開くと、先頭 (アドレス 0) から最初の領域までは未割り当て。
            Assert.Equal(RegionAccess.Unallocated, RegionMaps.At(source.Regions, 0)!.Access);
            Assert.Equal(RegionAccess.Readable, RegionMaps.At(source.Regions, a)!.Access);
            Assert.Equal(RegionAccess.NoAccess, RegionMaps.At(source.Regions, a + 0x10000)!.Access);
            Assert.Equal(RegionAccess.Unallocated, RegionMaps.At(source.Regions, a + 0x20000)!.Access);

            // 読める領域は内容を返し、アクセス不可の領域は読まずに「アクセス不可」を返す。
            byte[] buffer = new byte[8];
            ReadResult ok = source.Read(a, buffer);
            Assert.True(ok.IsComplete);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, buffer[..4]);

            ReadResult blocked = source.Read(a + 0x10000, new byte[8]);
            Assert.False(blocked.IsComplete);
            Assert.Equal(UnreadableReason.AccessDenied, blocked.Unreadable[0].Reason);
        }
    }

    [Fact]
    public void Describe_address_names_the_module_and_protection()
    {
        long moduleBase = 0x7FF000000000;
        ProcessMemoryByteSource source = Open(new FakeProcessSpec
        {
            Pid = 12,
            AddressLimit = moduleBase + 0x200000,
            Regions = [new FakeRegionSpec { Base = moduleBase, Size = 0x100000, State = RegionState.Commit, Protect = PageProtection.ExecuteRead, Type = RegionType.Image }],
            Modules = [new ProcessModule("kernel32.dll", moduleBase, 0x100000, @"C:\Windows\System32\kernel32.dll")],
        });
        using (source)
        {
            string? text = source.DescribeAddress(moduleBase + 0x1A2B0);
            Assert.Equal("kernel32.dll+0x1A2B0 (RX)", text);
        }
    }

    [Fact]
    public void A_guard_page_is_reported_as_no_access_and_never_read()
    {
        long guard = 0x200000;
        var spec = new FakeProcessSpec
        {
            Pid = 13,
            AddressLimit = guard + 0x10000,
            Regions = [new FakeRegionSpec { Base = guard, Size = 0x1000, State = RegionState.Commit, Protect = PageProtection.ReadWrite | PageProtection.Guard }],
        };
        var access = new FakeProcessAccess(new FakeProcessListSpec { Elevated = true, Processes = [spec] });
        using var source = new ProcessMemoryByteSource(access.Open(13, false), access, new ProcessOpenInfo { DisplayName = "t" });
        ReadResult result = source.Read(guard, new byte[16]);
        Assert.False(result.IsComplete);
        Assert.Equal(UnreadableReason.AccessDenied, result.Unreadable[0].Reason);

        // ガードページを一度も読まない (読むと対象プロセスで例外が起きる。ENG-32 の仕様 10)。
        Assert.Equal(0, access.Process(13).GuardViolations);
    }
}
