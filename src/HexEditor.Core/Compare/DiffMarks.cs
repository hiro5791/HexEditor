namespace HexEditor.Core.Compare;

/// <summary>
/// ミニマップの差分の印 (VIEW-35 の仕様 6「差分 (ANA)」): 比較の片側から見た差分の位置・長さ・形。変更は塗りつぶし、この側にバイトがある
/// 挿入・削除は斜線 (<see cref="DiffKind.Inserted"/>)、この側にバイトがない挿入・削除は長さ 0 の位置の点 (<see cref="DiffKind.Deleted"/>)。
/// 読み込み不可は印にしない。差分が多いときは、近い (間が <c>長さ / maxMarks</c> 以下の) 同じ形の差分をまとめて、印の数を
/// <paramref name="maxMarks"/> 程度に抑える (ミニマップのピクセル行より細かい印は見分けられない)。
/// </summary>
public static class DiffMarks
{
    public const int DefaultMaxMarks = 2000;

    public static IReadOnlyList<(long Offset, long Length, DiffKind Kind)> Build(IEnumerable<DiffRange> diffs, bool right, long sideLength,
        int maxMarks = DefaultMaxMarks)
    {
        long gap = Math.Max(0, sideLength / Math.Max(1, maxMarks));
        var marks = new List<(long Offset, long Length, DiffKind Kind)>();
        foreach (DiffRange d in diffs)
        {
            if (d.Kind == DiffKind.Unreadable)
            {
                continue;
            }

            long offset = d.Start(right);
            long length = d.Length(right);
            DiffKind shape = d.Kind == DiffKind.Changed && length > 0 ? DiffKind.Changed : length > 0 ? DiffKind.Inserted : DiffKind.Deleted;
            if (marks.Count > 0 && marks[^1] is var last && last.Kind == shape && offset >= last.Offset
                && offset - (last.Offset + last.Length) <= gap)
            {
                long end = Math.Max(last.Offset + last.Length, offset + length);
                marks[^1] = (last.Offset, end - last.Offset, shape);
                continue;
            }

            marks.Add((offset, length, shape));
        }

        return marks;
    }
}
