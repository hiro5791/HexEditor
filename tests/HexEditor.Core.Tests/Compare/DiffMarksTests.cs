using HexEditor.Core.Compare;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ミニマップの差分の印 (VIEW-35 の仕様 6「差分 (ANA)」) の片側から見た位置と形。</summary>
public sealed class DiffMarksTests
{
    [Fact]
    public void Each_side_sees_changes_filled_its_own_bytes_hatched_and_missing_bytes_as_a_point()
    {
        DiffRange[] diffs =
        [
            new(DiffKind.Changed, 0x10, 4, 0x10, 4),
            new(DiffKind.Inserted, 0x100, 0, 0x100, 8),
            new(DiffKind.Deleted, 0x200, 16, 0x208, 0),
            new(DiffKind.Unreadable, 0x300, 4, 0x308, 4),
        ];

        Assert.Equal(
            [(0x10L, 4L, DiffKind.Changed), (0x100L, 0L, DiffKind.Deleted), (0x200L, 16L, DiffKind.Inserted)],
            DiffMarks.Build(diffs, right: false, sideLength: 0x400, maxMarks: 0x400));
        Assert.Equal(
            [(0x10L, 4L, DiffKind.Changed), (0x100L, 8L, DiffKind.Inserted), (0x208L, 0L, DiffKind.Deleted)],
            DiffMarks.Build(diffs, right: true, sideLength: 0x400, maxMarks: 0x400));
    }

    [Fact]
    public void Many_close_differences_are_merged_to_about_the_mark_limit()
    {
        // 1 MiB に 2 バイトおきの変更 (262,144 件) → 間が 1 MiB / 100 以下の同じ形はまとめて 1 件。
        IEnumerable<DiffRange> Many()
        {
            for (long o = 0; o < (1 << 20); o += 4)
            {
                yield return new DiffRange(DiffKind.Changed, o, 2, o, 2);
            }
        }

        IReadOnlyList<(long Offset, long Length, DiffKind Kind)> marks = DiffMarks.Build(Many(), right: false, sideLength: 1 << 20, maxMarks: 100);
        Assert.Equal([(0L, (1L << 20) - 2, DiffKind.Changed)], marks);

        // 離れた差分はまとめない。
        DiffRange[] apart = [new(DiffKind.Changed, 0, 1, 0, 1), new(DiffKind.Changed, 1 << 19, 1, 1 << 19, 1)];
        Assert.Equal(2, DiffMarks.Build(apart, right: false, sideLength: 1 << 20, maxMarks: 100).Count);
    }
}
