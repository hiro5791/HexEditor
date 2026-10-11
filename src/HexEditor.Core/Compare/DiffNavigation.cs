namespace HexEditor.Core.Compare;

/// <summary>次 / 前の差分への移動の行き先 (ANA-05)。<see cref="Wrapped"/> は末尾から先頭 (または先頭から末尾) に戻ったか。</summary>
public readonly record struct DiffStep(long Index, bool Wrapped);

/// <summary>
/// 差分の間の移動 (ANA-05) と、左右の位置の対応付け (ANA-04 の仕様 4)。どちらも差分の一覧の二分探索で求めるので、件数に関係なく
/// 速い (1,000 万件でも 100 ms 以内)。
/// </summary>
public static class DiffNavigation
{
    /// <summary>
    /// 「次の差分」(仕様 1・3): <paramref name="cursor"/> より後ろで最初に始まる差分。カーソルの位置から始まる差分も、それが今選んでいる
    /// 差分 (<paramref name="currentIndex"/>) でなければ行き先にする。末尾を越えたら先頭に戻る。差分がなければ null。
    /// </summary>
    /// <param name="right">フォーカスのある側が右か。</param>
    public static DiffStep? Next(DiffStore diffs, bool right, long cursor, long currentIndex = -1)
    {
        long count = diffs.Count;
        if (count == 0)
        {
            return null;
        }

        long index = diffs.FirstStartingAtOrAfter(right, cursor);
        while (index < count && diffs[index].Start(right) == cursor && index <= currentIndex)
        {
            index++;
        }

        return index < count ? new DiffStep(index, false) : new DiffStep(0, true);
    }

    /// <summary>
    /// 「前の差分」(仕様 2・3): カーソルが差分の中にあればその差分、なければカーソルより前で始まる最後の差分。先頭を越えたら末尾に戻る。
    /// </summary>
    public static DiffStep? Previous(DiffStore diffs, bool right, long cursor)
    {
        long count = diffs.Count;
        if (count == 0)
        {
            return null;
        }

        long inside = diffs.IndexContaining(right, cursor);
        if (inside >= 0 && diffs[inside].Start(right) < cursor)
        {
            return new DiffStep(inside, false);
        }

        long index = diffs.FirstStartingAtOrAfter(right, cursor) - 1;
        return index >= 0 ? new DiffStep(index, false) : new DiffStep(count - 1, true);
    }

    /// <summary>
    /// 片側の位置に対応するもう片側の位置 (同期スクロール。ANA-04 の仕様 4)。差分のない区間は同じだけ進み、差分の中は対応する位置
    /// (変更は同じ相対位置、相手側の長さを越えたら相手側の末尾)、挿入・削除の中は相手側の区間の先頭にする。
    /// </summary>
    /// <param name="fromRight">右の位置から左の位置を求めるか。</param>
    public static long Map(CompareResult result, bool fromRight, long offset)
    {
        CompareRange from = fromRight ? result.Right : result.Left;
        CompareRange to = fromRight ? result.Left : result.Right;
        DiffStore diffs = result.Diffs;
        long index = diffs.Count == 0 ? -1 : diffs.FirstStartingAtOrAfter(fromRight, offset + 1) - 1;
        if (index < 0)
        {
            return to.Start + (offset - from.Start);
        }

        DiffRange d = diffs[index];
        long start = d.Start(fromRight);
        long end = start + d.Length(fromRight);
        long otherStart = d.Start(!fromRight);
        long otherLength = d.Length(!fromRight);
        if (offset < end)
        {
            return d.Kind is DiffKind.Changed or DiffKind.Unreadable && otherLength > 0
                ? otherStart + Math.Min(offset - start, otherLength - 1)
                : otherStart;
        }

        return otherStart + otherLength + (offset - end);
    }

    /// <summary>片側の位置にある差分 (なければ null)。スクリーンリーダーの読み上げ (仕様 8) とステータスバーに使う。</summary>
    public static (long Index, DiffRange Diff)? At(DiffStore diffs, bool right, long offset)
    {
        long index = diffs.IndexContaining(right, offset);
        return index < 0 ? null : (index, diffs[index]);
    }
}
