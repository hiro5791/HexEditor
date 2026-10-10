using HexEditor.Core.Engine;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Tests.Processes;

/// <summary>ENG-34 プロセスメモリへの書き込み (Core レベル。偽のプロセスを使う)。</summary>
public sealed class ProcessWriteTests
{
    private const long Base = 0x10000;

    private static (FakeProcessAccess Access, ProcessMemoryByteSource Source, Document Doc) Open(params FakeRegionSpec[] regions)
    {
        var spec = new FakeProcessListSpec
        {
            Elevated = true,
            Processes = [new FakeProcessSpec { Pid = 1234, Name = "TestTarget.exe", Regions = [.. regions] }],
        };
        var access = new FakeProcessAccess(spec);
        IProcessMemory memory = access.Open(1234, writable: true);
        var source = new ProcessMemoryByteSource(memory, access, new ProcessOpenInfo { DisplayName = "TestTarget.exe (PID 1234)" });
        return (access, source, new Document(source));
    }

    [Fact]
    [Trait("TC", "TC-ENG-34-01")]
    public void Writing_a_variable_reaches_the_target_process()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource source, Document doc) = Open(
            new FakeRegionSpec { Base = Base, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadWrite, Data = "01000000" });
        using (source)
        {
            doc.Overwrite(Base, new byte[] { 0x78, 0x56, 0x34, 0x12 });
            ProcessWriteResult result = ProcessWrite.Execute(doc, source);
            Assert.True(result.AllWritten);
            doc.CompleteProcessWrite(source, result.Written, result.AllWritten);

            byte[] actual = access.Process(1234).ReadRaw(Base, 4);
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, actual);
            Assert.False(doc.IsModified);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-34-02")]
    public void A_read_only_page_is_written_after_changing_protection_and_restored()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource source, Document doc) = Open(
            new FakeRegionSpec { Base = Base, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadOnly, Data = "1111111111111111" });
        using (source)
        {
            doc.Overwrite(Base, Enumerable.Repeat((byte)0x22, 8).ToArray());
            bool prompted = false;
            ProcessWriteResult result = ProcessWrite.Execute(doc, source, confirmProtectChange: protect =>
            {
                prompted = true;
                Assert.Equal(Core.Processes.PageProtection.ReadOnly, protect);
                return true;
            });

            Assert.True(prompted);
            Assert.True(result.AllWritten);
            Assert.Equal(Enumerable.Repeat((byte)0x22, 8), access.Process(1234).ReadRaw(Base, 8));

            // 保護属性は元に戻っている (ENG-34 の仕様 3)。
            Assert.Equal(Core.Processes.PageProtection.ReadOnly, access.Process(1234).Regions[0].Protect);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-34-03")]
    public void Immediate_write_reaches_the_process_and_undo_restores_it()
    {
        // 即時書き込みモードは「編集のたびに書く」。ここでは Core レベルで、編集→即時書き込み→Undo→即時書き込みを再現する。
        (FakeProcessAccess access, ProcessMemoryByteSource source, Document doc) = Open(
            new FakeRegionSpec { Base = Base, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadWrite, Data = "01000000" });
        using (source)
        {
            doc.Overwrite(Base, new byte[] { 0xFF, 0, 0, 0 });
            ProcessWriteResult r1 = ProcessWrite.Execute(doc, source);
            doc.CompleteProcessWrite(source, r1.Written, r1.AllWritten);
            Assert.Equal(0xFF, access.Process(1234).ReadRaw(Base, 1)[0]);

            doc.Undo();
            ProcessWriteResult r2 = ProcessWrite.Execute(doc, source);
            doc.CompleteProcessWrite(source, r2.Written, r2.AllWritten);
            Assert.Equal(0x01, access.Process(1234).ReadRaw(Base, 1)[0]);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-34-04")]
    public void A_save_with_an_unwritable_range_leaves_that_range_modified()
    {
        const long pageB = Base + 0x2000;
        (FakeProcessAccess access, ProcessMemoryByteSource source, Document doc) = Open(
            new FakeRegionSpec { Base = Base, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadWrite, Data = "AAAAAAAA" },
            new FakeRegionSpec { Base = pageB, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadOnly, Data = "BBBBBBBB" });
        using (source)
        {
            doc.Overwrite(Base, new byte[] { 1, 2, 3, 4 });
            doc.Overwrite(pageB, new byte[] { 5, 6, 7, 8 });

            // ページ B の保護変更を「書き込まない」にする。
            ProcessWriteResult result = ProcessWrite.Execute(doc, source, confirmProtectChange: _ => false);
            doc.CompleteProcessWrite(source, result.Written, result.AllWritten);

            ProcessWriteFailure failure = Assert.Single(result.Failed);
            Assert.Equal(pageB - source.BaseAddress, failure.Offset);
            Assert.Equal(ProcessWriteReason.Protected, failure.Reason);

            // ページ A は新しい値、ページ B は元の値。
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, access.Process(1234).ReadRaw(Base, 4));
            Assert.Equal(0xBB, access.Process(1234).ReadRaw(pageB, 1)[0]);

            // ページ B の範囲だけ「変更あり」で残る。
            Assert.True(doc.IsModified);
            var modified = doc.Current.EnumerateModifiedRanges().ToList();
            Assert.Single(modified);
            Assert.Equal(pageB - source.BaseAddress, modified[0].Offset);
        }
    }

    [Fact]
    public void Unallocated_ranges_cannot_be_edited_or_written()
    {
        (FakeProcessAccess _, ProcessMemoryByteSource source, Document doc) = Open(
            new FakeRegionSpec { Base = Base, Size = 0x1000, Protect = Core.Processes.PageProtection.ReadWrite });
        using (source)
        {
            Assert.False(source.IsAllocated(0x5000, 4));
            Assert.True(source.IsAllocated(Base, 4));
        }
    }
}
