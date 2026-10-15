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
}

/// <summary>順に評価した要素 (可変長の要素の配列、while / until の配列)。</summary>
internal sealed class SequenceChildren : ChildSource
{
    public List<TemplateNode> Items { get; } = [];

    public override long? KnownCount => Items.Count;

    public override bool IsMaterialized => true;
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
