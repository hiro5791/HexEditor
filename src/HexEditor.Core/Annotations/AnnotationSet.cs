namespace HexEditor.Core.Annotations;

/// <summary>
/// 注釈を持つ出どころ (INSP-32)。注釈は開始位置の順の配列と、ブロックごとの終了位置の最大値で索引を作る (区間の索引)。索引は
/// 変更のたびに作り直した不変のもので、問い合わせ (UI スレッド) と変更 (別のスレッドでもよい) が同時に起きても壊れない。
/// まとめて加えるときは <see cref="AddRange"/> を使う (1 回の作り直しで済む)。
/// </summary>
public sealed class AnnotationSet(string id, AnnotationOrigin origin, string displayName = "") : IAnnotationSource
{
    private readonly object _lock = new();
    private volatile Index _index = Index.Empty;

    public string Id { get; } = id;

    public AnnotationOrigin Origin { get; } = origin;

    public string DisplayName { get; } = displayName;

    /// <summary>ドキュメントの付随データに保存する (スクリプトが「保存する」と指定した注釈。INSP-32 の仕様 3)。</summary>
    public bool Persistent { get; set; }

    public event EventHandler? Changed;

    public int Count => _index.Count;

    /// <summary>すべての注釈 (開始位置の順)。</summary>
    public IReadOnlyList<Annotation> Items => _index.Items;

    public void Add(Annotation annotation) => AddRange([annotation]);

    public void AddRange(IEnumerable<Annotation> annotations)
    {
        lock (_lock)
        {
            _index = Index.Build([.. _index.Items, .. annotations.Select(a => a.Normalized())]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>条件に合う注釈を除く。除いた数を返す。</summary>
    public int RemoveWhere(Func<Annotation, bool> predicate)
    {
        int removed;
        lock (_lock)
        {
            Annotation[] kept = [.. _index.Items.Where(a => !predicate(a))];
            removed = _index.Count - kept.Length;
            if (removed > 0)
            {
                _index = Index.Build(kept);
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _index = Index.Empty;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Query(long start, long end, List<Annotation> output) => _index.Query(start, end, output);

    /// <summary>
    /// 開始位置の順の配列と、64 件ごとのブロックの終了位置の最大値・それまでのブロックの最大値の累積。[start, end) の問い合わせは、
    /// 開始位置が end より前の部分を後ろのブロックから調べ、累積の最大値が start 以下になったら止める。
    /// </summary>
    private sealed class Index
    {
        private const int BlockShift = 6;
        private const int BlockSize = 1 << BlockShift;

        public static readonly Index Empty = new([], [], [], []);

        private readonly long[] _starts;
        private readonly long[] _blockMaxEnd;
        private readonly long[] _prefixMaxEnd;

        private Index(Annotation[] items, long[] starts, long[] blockMaxEnd, long[] prefixMaxEnd)
        {
            Items = items;
            _starts = starts;
            _blockMaxEnd = blockMaxEnd;
            _prefixMaxEnd = prefixMaxEnd;
        }

        public Annotation[] Items { get; }

        public int Count => Items.Length;

        /// <summary>長さ 0 の注釈は位置の 1 バイトを占めるものとして扱う。</summary>
        private static long EndOf(Annotation a) => a.Length == 0 ? a.Start + 1 : a.End;

        public static Index Build(Annotation[] items)
        {
            if (items.Length == 0)
            {
                return Empty;
            }

            long[] starts = new long[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                starts[i] = items[i].Start;
            }

            Array.Sort(starts, items);
            int blocks = (items.Length + BlockSize - 1) >> BlockShift;
            long[] blockMax = new long[blocks];
            long[] prefix = new long[blocks];
            long running = long.MinValue;
            for (int b = 0; b < blocks; b++)
            {
                long max = long.MinValue;
                int to = Math.Min(items.Length, (b + 1) << BlockShift);
                for (int i = b << BlockShift; i < to; i++)
                {
                    max = Math.Max(max, EndOf(items[i]));
                }

                blockMax[b] = max;
                running = Math.Max(running, max);
                prefix[b] = running;
            }

            return new Index(items, starts, blockMax, prefix);
        }

        public void Query(long start, long end, List<Annotation> output)
        {
            if (Items.Length == 0 || end <= start)
            {
                return;
            }

            // 開始位置が end 未満のものの数。
            int count = UpperBound(_starts, end - 1);
            if (count == 0)
            {
                return;
            }

            int first = output.Count;
            for (int b = (count - 1) >> BlockShift; b >= 0; b--)
            {
                if (_prefixMaxEnd[b] <= start)
                {
                    break;
                }

                if (_blockMaxEnd[b] <= start)
                {
                    continue;
                }

                int to = Math.Min(count, (b + 1) << BlockShift);
                for (int i = b << BlockShift; i < to; i++)
                {
                    if (EndOf(Items[i]) > start)
                    {
                        output.Add(Items[i]);
                    }
                }
            }

            // 後ろのブロックから集めたので、開始位置の順に直す。
            output.Sort(first, output.Count - first, StartComparer.Instance);
        }

        /// <summary>value 以下の要素の数。</summary>
        private static int UpperBound(long[] sorted, long value)
        {
            int lo = 0;
            int hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (sorted[mid] <= value)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }

    private sealed class StartComparer : IComparer<Annotation>
    {
        public static readonly StartComparer Instance = new();

        public int Compare(Annotation? x, Annotation? y) => x!.Start.CompareTo(y!.Start);
    }
}
