using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Statistics;

/// <summary>ブロックの状態 (ANA-13)。</summary>
public enum BlockState : byte
{
    /// <summary>まだ計算していない。</summary>
    None,

    Valid,

    /// <summary>読み込めなかったバイトを含む (グラフの線を途切れさせる。ANA-13 の「エラー」)。</summary>
    Unreadable,
}

/// <summary>
/// ブロックごとのエントロピー (ANA-13)。ブロックは対象の論理位置 0 からの固定の大きさ。メモリはブロック数に比例し、
/// 1 ブロック 13 バイト (上限 <see cref="MaxBlocks"/> 個)。
/// </summary>
public sealed class EntropyBlocks
{
    public const long MinBlockSize = 256;
    public const long MaxBlockSize = 64L * 1024 * 1024;

    /// <summary>「自動」のブロック数の目安 (対象の長さ / 1,024 以上で最小の 2 の累乗。ANA-13 の仕様 1)。</summary>
    public const long AutoBlocks = 1024;

    /// <summary>ブロック数の上限。指定の大きさで超える場合は、超えない最小の 2 の累乗に上げる。</summary>
    public const int MaxBlocks = 1 << 20;

    public EntropyBlocks(long length, long blockSize)
    {
        Length = length;
        BlockSize = blockSize;
        Count = (int)Math.Max(0, (length + blockSize - 1) / blockSize);
        Entropy = new float[Count];
        Zero = new float[Count];
        Printable = new float[Count];
        State = new BlockState[Count];
    }

    /// <summary>対象の長さ (論理)。</summary>
    public long Length { get; }

    public long BlockSize { get; }

    public int Count { get; }

    /// <summary>ブロックごとのエントロピー (0〜8 ビット / バイト)。</summary>
    public float[] Entropy { get; }

    /// <summary>ブロックごとの 0x00 の割合 (0〜1)。</summary>
    public float[] Zero { get; }

    /// <summary>ブロックごとの印字可能な ASCII の割合 (0〜1)。</summary>
    public float[] Printable { get; }

    public BlockState[] State { get; }

    public long BlockStart(int index) => index * BlockSize;

    public long BlockLength(int index) => Math.Min(BlockSize, Length - BlockStart(index));

    public int BlockOf(long logical) => (int)Math.Clamp(logical / BlockSize, 0, Math.Max(0, Count - 1));

    public int ComputedCount => State.Count(s => s != BlockState.None);

    /// <summary>「自動」のブロックの大きさ: 長さ / 1,024 以上で最小の 2 の累乗 (最小 256 バイト)。</summary>
    public static long AutoBlockSize(long length) =>
        StatMath.CeilPowerOfTwo((length + AutoBlocks - 1) / AutoBlocks, MinBlockSize);

    /// <summary>指定の大きさ (0 は「自動」) を、範囲 (256 バイト〜64 MB の 2 の累乗) とブロック数の上限に合わせる。</summary>
    public static long EffectiveBlockSize(long requested, long length)
    {
        long size = requested <= 0 ? AutoBlockSize(length) : StatMath.CeilPowerOfTwo(Math.Clamp(requested, MinBlockSize, MaxBlockSize), MinBlockSize);
        while ((length + size - 1) / size > MaxBlocks)
        {
            size <<= 1;
        }

        return size;
    }

    /// <summary>ブロックの計算結果を入れる。</summary>
    internal void Set(int index, ReadOnlySpan<long> counts, long readable, long bad)
    {
        Entropy[index] = (float)StatMath.Entropy(counts, readable);
        long printable = 0;
        for (int b = 0x20; b <= 0x7E; b++)
        {
            printable += counts[b];
        }

        Zero[index] = readable == 0 ? 0 : (float)((double)counts[0] / readable);
        Printable[index] = readable == 0 ? 0 : (float)((double)printable / readable);
        State[index] = bad > 0 ? BlockState.Unreadable : BlockState.Valid;
    }

    /// <summary>ほかの結果から計算済みのブロックを写す (大きさと長さが同じこと)。</summary>
    internal void CopyFrom(EntropyBlocks other, int from, int to)
    {
        for (int i = from; i < to && i < Count && i < other.Count; i++)
        {
            if (other.State[i] != BlockState.None)
            {
                Entropy[i] = other.Entropy[i];
                Zero[i] = other.Zero[i];
                Printable[i] = other.Printable[i];
                State[i] = other.State[i];
            }
        }
    }

    public EntropyBlocks Clone()
    {
        var copy = new EntropyBlocks(Length, BlockSize);
        copy.CopyFrom(this, 0, Count);
        return copy;
    }

    /// <summary>CSV (ブロックの開始オフセット, エントロピー。ANA-13 の仕様 9)。オフセットはドキュメント上の位置の 16 進。</summary>
    public string ToCsv(LogicalRanges ranges)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("offset,entropy\r\n");
        for (int i = 0; i < Count; i++)
        {
            sb.Append("0x").Append(ranges.ToDocument(BlockStart(i)).ToString("X", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append(State[i] switch
            {
                BlockState.Valid => Entropy[i].ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                BlockState.Unreadable => "unreadable",
                _ => string.Empty,
            });
            sb.Append("\r\n");
        }

        return sb.ToString();
    }
}

/// <summary>
/// ドキュメントごとの、ブロックごとのエントロピーのキャッシュ (ANA-13 の仕様 8)。ドキュメント全体を対象にした計算の結果を
/// ブロックの大きさごとに持ち、ミニマップ (VIEW-35) と統計パネルが共有する。編集されたら、編集された範囲を含むブロックだけを
/// 無効にする (長さが変わる編集では、編集位置より後ろのブロックを無効にする)。
/// </summary>
public sealed class EntropyCache
{
    private static readonly ConditionalWeakTable<Document, EntropyCache> Caches = new();

    private readonly object _lock = new();
    private readonly Document _document;
    private readonly Dictionary<long, EntropyBlocks> _entries = [];
    private readonly List<(long BlockSize, long Offset, long Length)> _log = [];

    private EntropyCache(Document document)
    {
        _document = document;
        document.Changed += OnChanged;
    }

    /// <summary>ドキュメントのキャッシュ (なければ作る)。</summary>
    public static EntropyCache For(Document document) => Caches.GetValue(document, d => new EntropyCache(d));

    /// <summary>計算のために読んだ範囲 (ドキュメント上。テストの確認用。TC-ANA-13-03、TC-ANA-13-04)。</summary>
    public IReadOnlyList<(long BlockSize, long Offset, long Length)> ComputeLog
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>キャッシュの内容 (写し)。なければ null。</summary>
    public EntropyBlocks? TryGet(long blockSize)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(blockSize, out EntropyBlocks? e) && e.Length == _document.Length ? e.Clone() : null;
        }
    }

    /// <summary>
    /// [from, to) (ドキュメント上の位置。ブロックの境界に広げる) のブロックを返す。計算済みでないブロックだけを読んで計算する
    /// (ミニマップのエントロピー表示はこれを呼ぶ。統計パネルで計算済みなら読み込みは 0)。
    /// </summary>
    public EntropyBlocks GetOrCompute(DocumentSnapshot snapshot, long blockSize, long from = 0, long to = long.MaxValue,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        long length = snapshot.Length;
        blockSize = EntropyBlocks.EffectiveBlockSize(blockSize, length);
        EntropyBlocks blocks;
        lock (_lock)
        {
            blocks = _entries.TryGetValue(blockSize, out EntropyBlocks? cached) && cached.Length == length && ReferenceEquals(snapshot, _document.Current)
                ? cached.Clone()
                : new EntropyBlocks(length, blockSize);
        }

        int first = blocks.BlockOf(Math.Max(0, from));
        int last = to >= length ? blocks.Count : Math.Min(blocks.Count, (int)((Math.Min(to, length) + blockSize - 1) / blockSize));
        var logical = new LogicalRanges([new HashRange(0, length)]);
        int i = first;
        while (i < last)
        {
            if (blocks.State[i] != BlockState.None)
            {
                i++;
                continue;
            }

            int j = i;
            while (j < last && blocks.State[j] == BlockState.None)
            {
                j++;
            }

            long start = blocks.BlockStart(i);
            long end = Math.Min(length, blocks.BlockStart(j));
            lock (_lock)
            {
                _log.Add((blockSize, start, end - start));
            }

            EntropyGraph.ComputeBlocks(snapshot, logical, blocks, start, end, operation, cancellationToken);
            i = j;
        }

        Publish(snapshot, blocks);
        return blocks;
    }

    /// <summary>ドキュメント全体を対象に計算した結果を入れる (計算を始めた時点の内容が今の内容と同じときだけ)。</summary>
    public void Publish(DocumentSnapshot snapshot, EntropyBlocks blocks)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(snapshot, _document.Current) || blocks.Length != snapshot.Length)
            {
                return;
            }

            if (_entries.TryGetValue(blocks.BlockSize, out EntropyBlocks? existing) && existing.Length == blocks.Length)
            {
                existing.CopyFrom(blocks, 0, blocks.Count);
            }
            else
            {
                _entries[blocks.BlockSize] = blocks.Clone();
            }
        }
    }

    public void ClearLog()
    {
        lock (_lock)
        {
            _log.Clear();
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        lock (_lock)
        {
            if (e.IsWholeDocument)
            {
                _entries.Clear();
                return;
            }

            long newLength = _document.Length;
            foreach ((long size, EntropyBlocks blocks) in _entries.ToList())
            {
                if (e.RemovedLength == e.InsertedLength && blocks.Length == newLength)
                {
                    // 上書き: 編集された範囲を含むブロックだけを無効にする。
                    long end = e.Offset + Math.Max(1, e.InsertedLength);
                    for (int i = blocks.BlockOf(e.Offset); i < blocks.Count && blocks.BlockStart(i) < end; i++)
                    {
                        blocks.State[i] = BlockState.None;
                    }

                    continue;
                }

                // 長さが変わる編集: 編集位置を含むブロックより前だけを残す。
                var resized = new EntropyBlocks(newLength, size);
                resized.CopyFrom(blocks, 0, (int)Math.Min(resized.Count, e.Offset / size));
                _entries[size] = resized;
            }
        }
    }
}

/// <summary>エントロピーグラフのブロックの計算 (ANA-13)。</summary>
public static class EntropyGraph
{
    /// <summary>
    /// 論理範囲 [from, to) (ブロックの境界) を読み、<paramref name="blocks"/> のブロックを計算する。拡大したときの表示範囲だけの
    /// 再計算 (ANA-13 の仕様 4) と、キャッシュの穴埋めに使う。
    /// </summary>
    public static void ComputeBlocks(DocumentSnapshot snapshot, LogicalRanges ranges, EntropyBlocks blocks, long from, long to,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        var analyzer = new ByteAnalyzer(ranges.Length, blocks, digram: false, positions: false);
        var scanner = new RangeScanner(snapshot, ranges, RangeScanner.DefaultChunkSize, token);
        long done = operation?.ProcessedBytes ?? 0;
        foreach (ScanChunk chunk in scanner.Read(from, to))
        {
            analyzer.Process(chunk);
            done += chunk.Length;
            operation?.Report(done);
        }

        analyzer.Finish(to);
    }

    /// <summary>拡大したときの細かいブロックの大きさ: 表示範囲の長さ / 1,024 以上で最小の 2 の累乗 (最小 256 バイト)。</summary>
    public static long DetailBlockSize(long visibleLength) => EntropyBlocks.AutoBlockSize(visibleLength);
}
