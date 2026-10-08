namespace HexEditor.Core.Tabs;

/// <summary>タブの右クリックメニューの「閉じる」の範囲 (UI-09 の仕様 9)。</summary>
public enum TabCloseSet
{
    /// <summary>そのタブだけ (ピン留めしていても閉じる。UI-10 の仕様 3)。</summary>
    This,

    /// <summary>他のタブを閉じる。</summary>
    Others,

    /// <summary>右側のタブを閉じる。</summary>
    ToRight,

    /// <summary>保存済みのタブ (未保存の変更がないタブ) を閉じる。</summary>
    Saved,

    /// <summary>すべて閉じる。</summary>
    All,
}

/// <summary>
/// タブ列の並びの規則 (UI-09、UI-10)。ピン留めしたタブは左端にまとめ (仕様 2)、ピン留めしていないタブより右には移さない (仕様 4)。
/// 並びは「ピン留めしているか」の一覧で表す (先頭から連続してピン留めしたタブが並んでいる前提)。
/// </summary>
public static class TabStripRules
{
    /// <summary>ピン留めしたタブの数 (先頭から連続する分)。</summary>
    public static int PinnedCount(IReadOnlyList<bool> pinned)
    {
        int count = 0;
        while (count < pinned.Count && pinned[count])
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="from"/> のタブを <paramref name="to"/> (移した後の位置) へ動かすとき、ピン留めの規則に収めた位置を返す
    /// (UI-10 の仕様 4)。ピン留めしたタブはピン留めの範囲、していないタブはその右の範囲に収める。
    /// </summary>
    public static int ClampMove(IReadOnlyList<bool> pinned, int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, pinned.Count);
        int count = PinnedCount(pinned);
        to = Math.Clamp(to, 0, pinned.Count - 1);
        return pinned[from] ? Math.Min(to, count - 1) : Math.Max(to, count);
    }

    /// <summary>
    /// ピン留めを切り替えた後の位置 (UI-10 の仕様 2)。ピン留めすると、ピン留めしたタブの並びの末尾に移る。外すと、ピン留めしていない
    /// タブの並びの先頭に移る。
    /// </summary>
    public static int PinTarget(IReadOnlyList<bool> pinned, int index, bool pin)
    {
        int count = PinnedCount(pinned);
        if (pin)
        {
            return pinned[index] ? index : count;
        }

        return pinned[index] ? count - 1 : index;
    }

    /// <summary>
    /// 「左へ移動」「右へ移動」(UI-10 の仕様 1) の移動先。端、またはピン留めの境界を越える場合は null (動かさない)。
    /// </summary>
    public static int? Step(IReadOnlyList<bool> pinned, int index, bool right)
    {
        int target = index + (right ? 1 : -1);
        if (target < 0 || target >= pinned.Count)
        {
            return null;
        }

        int clamped = ClampMove(pinned, index, target);
        return clamped == index ? null : clamped;
    }

    /// <summary>
    /// 右クリックメニューの「閉じる」の対象 (UI-09 の仕様 9)。ピン留めしたタブは「他のタブを閉じる」「右側のタブを閉じる」
    /// 「保存済みのタブを閉じる」「すべて閉じる」では閉じない (UI-10 の仕様 3)。返すのはタブの位置 (並び順)。
    /// </summary>
    public static IReadOnlyList<int> CloseTargets(TabCloseSet set, IReadOnlyList<bool> pinned, IReadOnlyList<bool> modified, int target)
    {
        if (set == TabCloseSet.This)
        {
            return target >= 0 && target < pinned.Count ? [target] : [];
        }

        var result = new List<int>();
        for (int i = 0; i < pinned.Count; i++)
        {
            if (pinned[i])
            {
                continue;
            }

            bool include = set switch
            {
                TabCloseSet.Others => i != target,
                TabCloseSet.ToRight => i > target,
                TabCloseSet.Saved => !modified[i],
                _ => true,
            };
            if (include)
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>見出しの並び順で次 / 前のタブ (Ctrl+PageDown / Ctrl+PageUp。UI-09 の仕様 6)。端では反対の端に戻る。</summary>
    public static int Cycle(int count, int index, bool forward)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (index < 0)
        {
            return forward ? 0 : count - 1;
        }

        return ((index + (forward ? 1 : -1)) % count + count) % count;
    }

    /// <summary>ピン留めしたタブの見出しの文字数 (アイコンと先頭 8 文字。UI-10 の仕様 2)。</summary>
    public const int PinnedTitleLength = 8;

    /// <summary>ピン留めしたタブの見出し: 先頭 8 文字 (サロゲートペアを切らない)。</summary>
    public static string PinnedTitle(string name)
    {
        var info = new System.Globalization.StringInfo(name);
        return info.LengthInTextElements <= PinnedTitleLength ? name : info.SubstringByTextElements(0, PinnedTitleLength);
    }
}
