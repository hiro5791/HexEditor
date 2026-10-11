using System.Diagnostics;

namespace HexEditor.Core.Compare;

/// <summary>2 つの列の要素の比較 (要素は 1 バイト、または比較の単位の数バイト)。</summary>
internal interface ISymbolComparer
{
    /// <summary>左の <paramref name="a"/> 番目と右の <paramref name="b"/> 番目が等しいか。</summary>
    bool Equal(int a, int b);
}

/// <summary>
/// Myers の O(ND) 差分アルゴリズムの線形空間版 (中央のスネークで分割する。ANA-03 の仕様 2.2)。結果は最短の編集手順の
/// 一致区間の列。分割は再帰ではなく作業用のスタックで行う (深い分割でもスタックがあふれない)。
/// 編集距離 D が <c>maxD</c> を超える場合と、期限を過ぎた場合は打ち切って null を返す (呼び出し側はローリングハッシュでの
/// 再同期に切り替える)。メモリは O(maxD) で、列の長さに比例しない。
/// </summary>
internal static class MyersDiff
{
    /// <summary>一致区間 (左の位置、右の位置、長さ)。</summary>
    internal readonly record struct Match(int A, int B, int Length);

    private readonly record struct Work(int ALo, int AHi, int BLo, int BHi, int EmitA, int EmitB, int EmitLength, bool IsEmit);

    public static List<Match>? Matches<T>(T eq, int n, int m, int maxD, long deadline, CancellationToken cancellationToken)
        where T : struct, ISymbolComparer
    {
        var result = new List<Match>();
        var stack = new Stack<Work>();
        int[] v1 = new int[2 * maxD + 4];
        int[] v2 = new int[2 * maxD + 4];
        stack.Push(new Work(0, n, 0, m, 0, 0, 0, false));
        int steps = 0;
        while (stack.Count > 0)
        {
            Work w = stack.Pop();
            if (w.IsEmit)
            {
                Add(result, w.EmitA, w.EmitB, w.EmitLength);
                continue;
            }

            if ((++steps & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetTimestamp() > deadline)
                {
                    return null;
                }
            }

            int aLo = w.ALo;
            int aHi = w.AHi;
            int bLo = w.BLo;
            int bHi = w.BHi;

            // 先頭の一致 (この範囲より前は出力済みなので、すぐ出せる)。
            int p = 0;
            while (aLo + p < aHi && bLo + p < bHi && eq.Equal(aLo + p, bLo + p))
            {
                p++;
            }

            Add(result, aLo, bLo, p);
            aLo += p;
            bLo += p;

            // 末尾の一致 (中の結果の後に出す)。
            int s = 0;
            while (aHi - s > aLo && bHi - s > bLo && eq.Equal(aHi - s - 1, bHi - s - 1))
            {
                s++;
            }

            aHi -= s;
            bHi -= s;
            if (s > 0)
            {
                stack.Push(new Work(0, 0, 0, 0, aHi, bHi, s, true));
            }

            if (aLo == aHi || bLo == bHi)
            {
                continue;
            }

            int split = Bisect(eq, aLo, aHi, bLo, bHi, maxD, v1, v2, deadline, out int x, out int y);
            if (split < 0)
            {
                return null;
            }

            if (split == 0)
            {
                // 共通部分がない (すべて削除と挿入)。
                continue;
            }

            stack.Push(new Work(x, aHi, y, bHi, 0, 0, 0, false));
            stack.Push(new Work(aLo, x, bLo, y, 0, 0, 0, false));
        }

        return result;
    }

    private static void Add(List<Match> result, int a, int b, int length)
    {
        if (length <= 0)
        {
            return;
        }

        if (result.Count > 0)
        {
            Match last = result[^1];
            if (last.A + last.Length == a && last.B + last.Length == b)
            {
                result[^1] = last with { Length = last.Length + length };
                return;
            }
        }

        result.Add(new Match(a, b, length));
    }

    /// <summary>
    /// 中央のスネークを探し、分割点 (<paramref name="x"/>, <paramref name="y"/>) を返す (diff-match-patch の bisect と同じ手順)。
    /// 戻り値は 1 = 分割点あり、0 = 共通部分なし、-1 = 上限・期限で打ち切り。
    /// </summary>
    private static int Bisect<T>(T eq, int aLo, int aHi, int bLo, int bHi, int maxDCap, int[] v1, int[] v2, long deadline, out int x, out int y)
        where T : struct, ISymbolComparer
    {
        x = y = 0;
        int n = aHi - aLo;
        int m = bHi - bLo;
        int maxD = (n + m + 1) / 2;
        bool capped = false;
        if (maxD > maxDCap)
        {
            maxD = maxDCap;
            capped = true;
        }

        int vOffset = maxD;
        int vLength = 2 * maxD + 2;
        v1.AsSpan(0, vLength).Fill(-1);
        v2.AsSpan(0, vLength).Fill(-1);
        v1[vOffset + 1] = 0;
        v2[vOffset + 1] = 0;
        int delta = n - m;
        bool front = (delta & 1) != 0;
        int k1start = 0;
        int k1end = 0;
        int k2start = 0;
        int k2end = 0;
        for (int d = 0; d < maxD; d++)
        {
            if ((d & 0x3F) == 0x3F && Stopwatch.GetTimestamp() > deadline)
            {
                return -1;
            }

            for (int k1 = -d + k1start; k1 <= d - k1end; k1 += 2)
            {
                int k1Offset = vOffset + k1;
                int x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1]) ? v1[k1Offset + 1] : v1[k1Offset - 1] + 1;
                int y1 = x1 - k1;
                while (x1 < n && y1 < m && eq.Equal(aLo + x1, bLo + y1))
                {
                    x1++;
                    y1++;
                }

                v1[k1Offset] = x1;
                if (x1 > n)
                {
                    k1end += 2;
                }
                else if (y1 > m)
                {
                    k1start += 2;
                }
                else if (front)
                {
                    int k2Offset = vOffset + delta - k1;
                    if (k2Offset >= 0 && k2Offset < vLength && v2[k2Offset] != -1)
                    {
                        int x2 = n - v2[k2Offset];
                        if (x1 >= x2)
                        {
                            x = aLo + x1;
                            y = bLo + y1;
                            return 1;
                        }
                    }
                }
            }

            for (int k2 = -d + k2start; k2 <= d - k2end; k2 += 2)
            {
                int k2Offset = vOffset + k2;
                int x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1]) ? v2[k2Offset + 1] : v2[k2Offset - 1] + 1;
                int y2 = x2 - k2;
                while (x2 < n && y2 < m && eq.Equal(aLo + n - x2 - 1, bLo + m - y2 - 1))
                {
                    x2++;
                    y2++;
                }

                v2[k2Offset] = x2;
                if (x2 > n)
                {
                    k2end += 2;
                }
                else if (y2 > m)
                {
                    k2start += 2;
                }
                else if (!front)
                {
                    int k1Offset = vOffset + delta - k2;
                    if (k1Offset >= 0 && k1Offset < vLength && v1[k1Offset] != -1)
                    {
                        int x1 = v1[k1Offset];
                        int y1 = vOffset + x1 - k1Offset;
                        x2 = n - x2;
                        if (x1 >= x2)
                        {
                            x = aLo + x1;
                            y = bLo + y1;
                            return 1;
                        }
                    }
                }
            }
        }

        return capped ? -1 : 0;
    }
}
