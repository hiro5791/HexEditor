using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Compare;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Compare.CompareTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-07 差分のマージ。</summary>
public sealed class DiffMergeTests
{
    /// <summary>TD-ANA-DIFF3-A (TD-SEQ-1M の先頭 4 KiB)。</summary>
    internal static byte[] Diff3A() => [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];

    /// <summary>TD-ANA-DIFF3-B: 0x100〜0x103 を FF で上書き、0x800 の前に EE を 16 バイト挿入、0xC00〜0xC1F を削除。</summary>
    internal static byte[] Diff3B()
    {
        byte[] a = Diff3A();
        byte[] b = (byte[])a.Clone();
        b.AsSpan(0x100, 4).Fill(0xFF);
        return [.. b[..0x800], .. Enumerable.Repeat((byte)0xEE, 16), .. b[0x800..0xC00], .. b[0xC20..]];
    }

    private static Document Doc(byte[] data, SourceCapabilities capabilities = SourceCapabilities.CanWrite | SourceCapabilities.CanResize) =>
        new(new MemoryByteSource(data, "doc", capabilities), Options());

    private static CompareResult Run(Document left, Document right, CompareMethod method)
    {
        var options = new CompareOptions { Method = method };
        var result = new CompareResult(method, CompareRange.Whole(CompareData.FromSnapshot(left.Current)),
            CompareRange.Whole(CompareData.FromSnapshot(right.Current)));
        DataComparer.Run(options, result);
        return result;
    }

    [Fact]
    public void Diff3HasTheThreeDocumentedDiffs()
    {
        using Document a = Doc(Diff3A());
        using Document b = Doc(Diff3B());
        using CompareResult result = Run(a, b, CompareMethod.InsertDelete);
        Assert.Equal(
        [
            new DiffRange(DiffKind.Changed, 0x100, 4, 0x100, 4),
            new DiffRange(DiffKind.Inserted, 0x800, 0, 0x800, 16),
            new DiffRange(DiffKind.Deleted, 0xC00, 32, 0xC10, 0),
        ], All(result));
    }

    [Fact]
    [Trait(TC, "TC-ANA-07-01")]
    public async Task ChangedAndDeletedDiffsAreCopiedToTheRight()
    {
        using Document a = Doc(Diff3A());
        using Document b = Doc(Diff3B());
        using CompareResult result = Run(a, b, CompareMethod.InsertDelete);

        // 1 件目 (変更) を右へコピーする。
        MergeOutcome first = await DiffMerger.CopyAsync(result, a, b, [0], MergeDirection.ToRight);
        Assert.Equal(new MergeOutcome(1, 0), first);
        Assert.Equal([0x00, 0x01, 0x02, 0x03], Read(b.Current, 0x100, 4));
        Assert.Equal(2, result.Diffs.Count);
        Assert.Equal(DiffKind.Inserted, result.Diffs[0].Kind);

        // 3 件目 (いまは 2 件目。削除、左 0xC00 長さ 32) を右へコピーする。
        long length = b.Length;
        await DiffMerger.CopyAsync(result, a, b, [1], MergeDirection.ToRight);
        Assert.Equal(length + 32, b.Length);
        Assert.Equal(Read(a.Current, 0xC00, 32), Read(b.Current, 0xC10, 32));
        Assert.Equal([new DiffRange(DiffKind.Inserted, 0x800, 0, 0x800, 16)], All(result));

        // 自動で保存しない (変更済みになる)。
        Assert.True(b.IsModified);
        Assert.False(a.IsModified);
    }

    private sealed record MergeCase(int Length, int Edits, int Seed, bool InsertDelete);

    private static Arbitrary<MergeCase> MergeCases() =>
        (from length in Gen.Choose(0, 8192)
         from edits in Gen.Choose(0, 20)
         from seed in Gen.Choose(0, int.MaxValue)
         from method in Gen.Elements(false, true)
         select new MergeCase(length, edits, seed, method)).ToArbitrary();

    [Property(MaxTest = 500)]
    [Trait(TC, "TC-ANA-07-02")]
    public Property CopyingAllDiffsToTheRightLeavesNoDiffs() => Prop.ForAll(MergeCases(), c =>
    {
        (byte[] left, byte[] right) = InsertDeleteCompareTests.RandomEdits(c.Length, c.Edits, c.Seed);
        CompareMethod method = c.InsertDelete ? CompareMethod.InsertDelete : CompareMethod.Simple;
        using Document a = Doc(left);
        using Document b = Doc(right);
        using CompareResult result = Run(a, b, method);
        DiffMerger.CopyAllAsync(result, a, b, MergeDirection.ToRight).GetAwaiter().GetResult();
        Assert.Equal(0, result.Diffs.Count);

        using CompareResult again = Run(a, b, method);
        Assert.Equal(0, again.Diffs.Count);
        Assert.Equal(left, ReadAll(b.Current));
    });

    [Fact]
    [Trait(TC, "TC-ANA-07-03")]
    public async Task CopyingSeveralDiffsIsOneUndo()
    {
        using Document a = Doc(Diff3A());
        using Document b = Doc(Diff3B());
        byte[] before = ReadAll(b.Current);
        using CompareResult result = Run(a, b, CompareMethod.InsertDelete);

        await DiffMerger.CopyAllAsync(result, a, b, MergeDirection.ToRight);
        Assert.Equal(0, result.Diffs.Count);
        Assert.Equal(Diff3A(), ReadAll(b.Current));

        b.Undo();
        Assert.Equal(before, ReadAll(b.Current));
        Assert.False(b.History.CanUndo);
    }

    [Fact]
    [Trait(TC, "TC-ANA-07-04")]
    public async Task LengthChangingDiffsAreSkippedForAFixedLengthTarget()
    {
        // 物理ディスク (長さを変えられないデータソース) の代わりに、書き込めて長さを変えられないデータソースを使う
        // (実際のディスクへの書き込みはこの PC では行わない。ディスクの書き込みの経路はデバイスの担当のテストで確かめる)。
        using Document file = Doc(Diff3B());
        using Document disk = Doc(Diff3A(), SourceCapabilities.CanWrite);
        using CompareResult result = Run(file, disk, CompareMethod.InsertDelete);
        Assert.Equal(3, result.Diffs.Count);

        MergeOutcome outcome = await DiffMerger.CopyAllAsync(result, file, disk, MergeDirection.ToRight);

        Assert.Equal(new MergeOutcome(1, 2), outcome);
        Assert.Equal(4096, disk.Length);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], Read(disk.Current, 0x100, 4));
        Assert.Equal(2, result.Diffs.Count);
    }

    [Fact]
    public async Task AReadOnlyTargetIsRefused()
    {
        using Document a = Doc(Diff3A());
        using Document b = Doc(Diff3B());
        b.SetReadOnly(ReadOnlyReason.User);
        using CompareResult result = Run(a, b, CompareMethod.InsertDelete);
        await Assert.ThrowsAsync<DocumentReadOnlyException>(() => DiffMerger.CopyAllAsync(result, a, b, MergeDirection.ToRight));
        Assert.Equal(3, result.Diffs.Count);
    }

    [Fact]
    public async Task CancellingUndoesEverything()
    {
        const int Length = 30_000;
        byte[] zeros = new byte[Length];
        byte[] alternating = new byte[Length];
        SimpleCompareTests.Alternating(0, alternating);
        using Document a = Doc(zeros);
        using Document b = Doc(alternating);
        using CompareResult result = Run(a, b, CompareMethod.Simple);
        Assert.Equal(Length / 2, result.Diffs.Count);
        using var cancel = new CancellationTokenSource();
        int pauses = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiffMerger.CopyAllAsync(result, a, b, MergeDirection.ToRight, cancel.Token, () =>
        {
            if (++pauses == 3)
            {
                cancel.Cancel();
            }

            return Task.CompletedTask;
        }));

        Assert.Equal(alternating, ReadAll(b.Current));
        Assert.Equal(Length / 2, result.Diffs.Count);
    }

    [Fact]
    public async Task CopyingToTheLeftShiftsTheLaterLeftOffsets()
    {
        using Document a = Doc(Diff3A());
        using Document b = Doc(Diff3B());
        using CompareResult result = Run(a, b, CompareMethod.InsertDelete);

        // 挿入 (右のみ) を左へコピーすると、左に 16 バイト挿入され、後ろの削除の左の位置が 16 ずれる。
        await DiffMerger.CopyAsync(result, a, b, [1], MergeDirection.ToLeft);
        Assert.Equal(4096 + 16, a.Length);
        Assert.Equal(
        [
            new DiffRange(DiffKind.Changed, 0x100, 4, 0x100, 4),
            new DiffRange(DiffKind.Deleted, 0xC10, 32, 0xC10, 0),
        ], All(result));
        Assert.Equal(Read(a.Current, 0xC10, 32), Read(a.Current, 0xC10, 32));
    }
}
