namespace HexEditor.Core.Templates.Runtime;

/// <summary>ノードの子の持ち方 (TPL-28 の仕様 1・2)。</summary>
internal abstract class ChildSource
{
    /// <summary>評価せずに分かる子の数。</summary>
    public abstract long? KnownCount { get; }

    public abstract bool IsMaterialized { get; }
}

/// <summary>評価済みの子の一覧 (構造体の本体を評価しながら追加する)。</summary>
internal sealed class ListChildren : ChildSource
{
    public List<TemplateNode> Items { get; } = [];

    public override long? KnownCount => Items.Count;

    public override bool IsMaterialized => true;
}

/// <summary>評価を始めた時点の状態 (遅延評価と、捨てた部分木の再評価に使う。TPL-28 の仕様 2)。</summary>
internal sealed class StructThunk
{
    public required RtType Type { get; init; }

    public required Val[] Args { get; init; }

    public required long Offset { get; init; }

    public required Space Space { get; init; }

    public required Frame Parent { get; init; }

    public required bool BigEndian { get; init; }

    public required bool MsbFirst { get; init; }

    public string? Encoding { get; init; }

    public long ArrayIndex { get; init; } = -1;

    public int Depth { get; init; }

    public long WindowEnd { get; init; } = long.MaxValue;

    public long IoBase { get; init; }
}

/// <summary>構造体・共用体・ビットフィールドの子 (開いたとき、または値を参照したときに評価する)。</summary>
internal sealed class LazyChildren(StructThunk thunk) : ChildSource
{
    public StructThunk Thunk { get; } = thunk;

    public List<TemplateNode>? Items { get; set; }

    /// <summary>評価中 (捨てない)。</summary>
    public bool Pinned { get; set; }

    /// <summary>評価に失敗した場合の診断 (部分木だけのエラー)。</summary>
    public TemplateDiagnostic? Error { get; set; }

    /// <summary>メモリの見積もりに入れた量。</summary>
    public long Cost { get; set; }

    /// <summary>捨てられる (再評価できる) 部分木の一覧での位置。</summary>
    public LinkedListNode<TemplateNode>? LruEntry { get; set; }

    public override long? KnownCount => Items?.Count;

    public override bool IsMaterialized => Items is not null;
}

/// <summary>
/// 要素を必要になったときに作る配列 (オンデマンド配列。TPL-07 の仕様 3)。固定長の要素の配列は、要素 i のオフセットを
/// <c>開始 + i × サイズ</c> で求める。可変長の要素の配列は、評価済みの要素の開始位置をそのまま持つ。
/// </summary>
internal sealed class OnDemandChildren : ChildSource
{
    private const int CacheLimit = 4096;
    private readonly Dictionary<long, TemplateNode> _cache = [];

    public required long Count { get; init; }

    public required Func<long, TemplateNode> Factory { get; init; }

    /// <summary>キャッシュから捨てた要素 (評価済みのパターンの数とメモリの見積もりから引く)。</summary>
    public Action<IEnumerable<TemplateNode>>? Evicted { get; init; }

    public override long? KnownCount => Count;

    public override bool IsMaterialized => true;

    public TemplateNode Get(long index)
    {
        if (_cache.TryGetValue(index, out TemplateNode? n))
        {
            return n;
        }

        if (_cache.Count >= CacheLimit)
        {
            Evicted?.Invoke(_cache.Values);
            _cache.Clear(); // 要素の数に比例してメモリを使わない
        }

        n = Factory(index);
        _cache[index] = n;
        return n;
    }

    public int CachedCount => _cache.Count;

    /// <summary>作ってある (評価済みの) 要素 (添字の順。結果ツリーの絞り込み。TPL-21 の仕様 5)。</summary>
    public IReadOnlyList<TemplateNode> Cached => [.. _cache.OrderBy(p => p.Key).Select(p => p.Value)];
}

/// <summary>
/// 順に評価した要素 (可変長の要素の配列、while / until の配列)。要素の開始位置を 1,024 要素ごとに記録し (チェックポイント)、作った要素は
/// 4,096 個までだけ持つ。持っていない要素は、直前のチェックポイントから順に作り直す (TPL-07 の仕様 4)。要素の数に比例してメモリを使わない。
/// </summary>
internal sealed class SequenceChildren : ChildSource
{
    public const int CheckpointInterval = 1024;
    private const int CacheLimit = 4096;

    private readonly object _lock = new();
    private readonly List<long> _starts = [];
    private readonly Dictionary<long, TemplateNode> _cache = [];
    private long _count;

    /// <summary>要素を作り直す (添字、評価を始める位置)。null なら作った要素をすべて持つ (作り直せない配列: ビット単位の要素)。</summary>
    public Func<long, long, TemplateNode>? Recreate { get; init; }

    /// <summary>キャッシュから捨てた要素 (評価済みのパターンの数とメモリの見積もりから引く)。</summary>
    public Action<IEnumerable<TemplateNode>>? Evicted { get; init; }

    public long Count => Interlocked.Read(ref _count);

    public override long? KnownCount => Count;

    public override bool IsMaterialized => true;

    /// <summary>要素を加える (<paramref name="start"/> はその要素の評価を始めた位置)。</summary>
    public void Add(TemplateNode element, long start)
    {
        lock (_lock)
        {
            long index = _count;
            if (index % CheckpointInterval == 0)
            {
                _starts.Add(start);
            }

            Put(index, element);
            Interlocked.Increment(ref _count);
        }
    }

    public TemplateNode Get(long index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        long from;
        long pos;
        lock (_lock)
        {
            if (_cache.TryGetValue(index, out TemplateNode? cached))
            {
                return cached;
            }

            from = index / CheckpointInterval * CheckpointInterval;
            pos = _starts[(int)(index / CheckpointInterval)];
        }

        // 直前のチェックポイントから順に作り直す (要素 i の開始位置は要素 i - 1 の終わり)
        Func<long, long, TemplateNode> recreate = Recreate ?? throw new InvalidOperationException("The element is not available.");
        TemplateNode? result = null;
        for (long i = from; i <= index; i++)
        {
            TemplateNode? el;
            lock (_lock)
            {
                _cache.TryGetValue(i, out el);
            }

            if (el is null)
            {
                el = recreate(i, pos);
                lock (_lock)
                {
                    Put(i, el);
                }
            }

            pos = el.End;
            result = el;
        }

        return result!;
    }

    /// <summary>
    /// <paramref name="offset"/> より後ろで終わる最初の要素の添字 (要素は位置の順に並ぶ)。チェックポイントで二分探索し、そのブロックの中だけを順に見る。
    /// </summary>
    public long FirstEndingAfter(long offset)
    {
        int lo;
        lock (_lock)
        {
            // offset 以下で始まる最後のチェックポイント
            int a = 0;
            int b = _starts.Count;
            while (a < b)
            {
                int mid = (a + b) / 2;
                if (_starts[mid] <= offset)
                {
                    a = mid + 1;
                }
                else
                {
                    b = mid;
                }
            }

            lo = Math.Max(0, a - 1);
        }

        long count = Count;
        long i = (long)lo * CheckpointInterval;
        while (i < count && Get(i).End <= offset)
        {
            i++;
        }

        return i;
    }

    /// <summary>作ってある (評価済みの) 要素 (添字の順。結果ツリーの絞り込み。TPL-21 の仕様 5)。</summary>
    public IReadOnlyList<TemplateNode> Cached
    {
        get
        {
            lock (_lock)
            {
                return [.. _cache.OrderBy(p => p.Key).Select(p => p.Value)];
            }
        }
    }

    private void Put(long index, TemplateNode element)
    {
        if (Recreate is not null && _cache.Count >= CacheLimit)
        {
            Evicted?.Invoke([.. _cache.Values]);
            _cache.Clear(); // 要素の数に比例してメモリを使わない
        }

        _cache[index] = element;
    }
}

/// <summary>可変長の要素の配列の要素を添字で読む一覧 (全体を持たない)。</summary>
internal sealed class SequenceList(SequenceChildren children) : IReadOnlyList<TemplateNode>
{
    public TemplateNode this[int index] => children.Get(index);

    public int Count => (int)Math.Min(int.MaxValue, children.Count);

    public IEnumerator<TemplateNode> GetEnumerator()
    {
        long n = children.Count;
        for (long i = 0; i < n; i++)
        {
            yield return children.Get(i);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>ポインタの指す先 (開いたとき、または式で参照したときに評価する。TPL-08 の仕様 4)。</summary>
internal sealed class PointerChildren(Func<TemplateNode?> resolve) : ChildSource
{
    private bool _resolved;
    private TemplateNode? _target;

    public TemplateNode? Target
    {
        get
        {
            if (!_resolved)
            {
                _resolved = true;
                _target = resolve();
            }

            return _target;
        }
    }

    public bool Resolved => _resolved;

    public override long? KnownCount => null;

    public override bool IsMaterialized => _resolved;
}
