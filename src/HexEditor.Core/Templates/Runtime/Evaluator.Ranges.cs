namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    /// <summary>
    /// 範囲に重なるパターン (TPL-23 の色分けと、カーソル位置のパターンの検索)。重なる部分木だけをたどり、固定長の要素の配列は
    /// 添字を計算して重なる要素だけを作る (表示範囲の大きさに比例する時間で終わる)。
    /// </summary>
    internal List<(TemplateNode Node, int Depth)> NodesInRange(long offset, long length, TemplateSection? section, int maxDepth, int maxNodes)
    {
        var result = new List<(TemplateNode, int)>();
        long end = offset + Math.Max(1, length);
        var stack = new Stack<(TemplateNode Node, int Depth)>();
        foreach (TemplateNode top in DisplayChildren(_result.Root).Reverse())
        {
            stack.Push((top, 0));
        }

        while (stack.Count > 0 && result.Count < maxNodes)
        {
            (TemplateNode node, int depth) = stack.Pop();
            if (node.Kind == TemplateNodeKind.Section)
            {
                if (section is not null && ReferenceEquals(node.Section, section))
                {
                    foreach (TemplateNode c in DisplayChildren(node).Reverse())
                    {
                        stack.Push((c, depth));
                    }
                }

                continue;
            }

            if (!ReferenceEquals(node.Section, section) || node.Kind == TemplateNodeKind.Computed)
            {
                continue;
            }

            bool overlaps = node.Size == 0 ? node.Offset >= offset && node.Offset < end : node.Offset < end && node.End > offset;
            if (!overlaps)
            {
                continue;
            }

            result.Add((node, depth));
            if (depth >= maxDepth || node.Children0 is null || node.Kind == TemplateNodeKind.Pointer)
            {
                continue;
            }

            switch (node.Children0)
            {
                case OnDemandChildren od:
                {
                    long count = od.Count;
                    if (count == 0)
                    {
                        break;
                    }

                    long size = node.Size / count;
                    long first = size == 0 ? 0 : Math.Max(0, (offset - node.Offset) / size);
                    long last = size == 0 ? count - 1 : Math.Min(count - 1, (end - 1 - node.Offset) / size);
                    for (long i = Math.Min(last, first + maxNodes); i >= first; i--)
                    {
                        stack.Push((od.Get(i), depth + 1));
                    }

                    break;
                }

                case SequenceChildren seq:
                {
                    List<TemplateNode> items;
                    lock (_result.SyncRoot)
                    {
                        items = seq.Items;
                    }

                    // 要素は位置の順に並んでいるので、最初に重なる要素を二分探索で探す
                    int lo = 0;
                    int hi = items.Count;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (items[mid].End <= offset)
                        {
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }

                    int stop = lo;
                    while (stop < items.Count && items[stop].Offset < end && stop - lo < maxNodes)
                    {
                        stop++;
                    }

                    for (int i = stop - 1; i >= lo; i--)
                    {
                        stack.Push((items[i], depth + 1));
                    }

                    break;
                }

                default:
                    foreach (TemplateNode c in DisplayChildren(node).Reverse())
                    {
                        stack.Push((c, depth + 1));
                    }

                    break;
            }
        }

        return result;
    }
}
