using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Statistics;

/// <summary>統計の計算の指定 (ANA-10〜ANA-14)。</summary>
public sealed record StatisticsRequest
{
    /// <summary>対象範囲。空ならドキュメント全体。複数ならオフセット順に連結する (06 の 0.1)。</summary>
    public IReadOnlyList<HashRange> Ranges { get; init; } = [];

    public ElementSpec Element { get; init; } = ElementSpec.Bytes;

    public BinSpec Bins { get; init; } = new();

    /// <summary>エントロピーグラフのブロックの大きさ (0 は「自動」)。</summary>
    public long BlockSize { get; init; }

    /// <summary>ダイグラム・位置ごとのバイト分布・ブロックごとのエントロピーも求める。</summary>
    public bool ByteFeatures { get; init; } = true;

    public int ChunkSize { get; init; } = RangeScanner.DefaultChunkSize;

    /// <summary>途中結果を知らせる間隔 (06 の 0.3: 0.5 秒ごと)。</summary>
    public TimeSpan PartialInterval { get; init; } = TimeSpan.FromSeconds(0.5);
}

/// <summary>
/// 統計の計算 (ANA-10 ヒストグラム、ANA-11 記述統計、ANA-12 エントロピー、ANA-13 エントロピーグラフ、ANA-14 ダイグラムと位置ごとの分布)。
/// 対象を 1 回の順次読み込みで計算する。32 / 64 bit の型で範囲が「自動」のビンは、最小値・最大値が分かってから数えるので、
/// 全要素を保持できない (1,600 万を超える) 場合だけ 2 回目の読み込みをする。開始時のスナップショットを読む (06 の 0.2)。
/// </summary>
public static class StatisticsEngine
{
    /// <summary>「自動で再計算」をする対象の上限 (ANA-10 の仕様 9)。</summary>
    public const long AutoComputeLimit = 64L * 1024 * 1024;

    /// <summary>
    /// 計算する。<paramref name="partial"/> に途中結果を 0.5 秒ごとに渡す (計算のスレッドから呼ぶ)。キャンセルされたら、
    /// そこまでの結果を持つ <see cref="StatisticsCancelledException"/> を投げる。
    /// </summary>
    public static StatisticsResult Compute(DocumentSnapshot snapshot, StatisticsRequest request, LongRunningOperation? operation = null,
        Action<StatisticsResult>? partial = null, CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        var run = new Run(snapshot, request, operation, token);
        try
        {
            return run.Execute(partial);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new StatisticsCancelledException(run.Build(final: false), token);
        }
    }

    /// <summary>1 回の計算の状態。</summary>
    private sealed class Run
    {
        private readonly DocumentSnapshot _snapshot;
        private readonly StatisticsRequest _request;
        private readonly LongRunningOperation? _operation;
        private readonly CancellationToken _token;
        private readonly LogicalRanges _ranges;
        private readonly ElementSpec _spec;
        private readonly ElementType _type;
        private readonly ByteAnalyzer _bytes;
        private readonly EntropyBlocks? _blocks;
        private readonly long[]? _values16;
        private readonly WideDescriptive? _wide;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Histogram? _wideHistogram;
        private PairAccumulator _pairs16;
        private (long Index, double Value) _previous = (-2, 0);
        private long _piInside;
        private long _piTotal;
        private long _processed;
        private long _total;
        private RangeScanner? _scanner;
        private readonly UnreadableSummary _unreadable = new();
        private long _bytesRead;
        private long _elementsSkipped;

        public Run(DocumentSnapshot snapshot, StatisticsRequest request, LongRunningOperation? operation, CancellationToken token)
        {
            _snapshot = snapshot;
            _request = request;
            _operation = operation;
            _token = token;
            _ranges = new LogicalRanges(HashEngine.Normalize(request.Ranges, snapshot.Length));
            _spec = request.Element;
            _type = _spec.Type;
            long length = _ranges.Length;
            _blocks = request.ByteFeatures ? new EntropyBlocks(length, EntropyBlocks.EffectiveBlockSize(request.BlockSize, length)) : null;
            _bytes = new ByteAnalyzer(length, _blocks, digram: request.ByteFeatures || _type is ElementType.U8 or ElementType.S8,
                positions: request.ByteFeatures);
            if (_type is ElementType.U16 or ElementType.S16)
            {
                _values16 = new long[65536];
            }
            else if (ElementTypes.Size(_type) > 2)
            {
                _wide = new WideDescriptive(_type, _spec.ElementCount(length));
                if (request.Bins.IsManual)
                {
                    _wideHistogram = new Histogram(_type, new long[Math.Clamp(request.Bins.Count, 2, BinSpec.MaxCount)], false, 0,
                        request.Bins.Min!.Value, request.Bins.Max!.Value);
                }
            }

            _total = length;
        }

        public StatisticsResult Execute(Action<StatisticsResult>? partial)
        {
            _operation?.SetTotal(_total);
            _scanner = new RangeScanner(_snapshot, _ranges, _request.ChunkSize, _token);
            ElementExtractor? elements = _type is ElementType.U8 or ElementType.S8 ? null : new ElementExtractor(_spec.Size, _spec.EffectiveStride);
            ElementExtractor? pi = _type == ElementType.U8 ? new ElementExtractor(6, 6) : null;
            ElementHandler? onElement = elements is null ? null : ElementHandlerFor(binOnly: false);
            TimeSpan nextPartial = _request.PartialInterval;
            foreach (ScanChunk chunk in _scanner.Read())
            {
                _token.ThrowIfCancellationRequested();
                Task? side = null;
                if (elements is not null)
                {
                    ScanChunk c = chunk;
                    side = Task.Run(() => elements.Feed(c.Logical, c.Span, c.Bad, onElement!), _token);
                }

                if (pi is not null)
                {
                    ScanChunk c = chunk;
                    side = Task.Run(() => pi.Feed(c.Logical, c.Span, c.Bad, CountPi), _token);
                }

                _bytes.Process(chunk);
                side?.GetAwaiter().GetResult();
                _processed += chunk.Length;
                _bytesRead = _scanner.BytesRead;
                _operation?.Report(_processed);
                if (partial is not null && _clock.Elapsed >= nextPartial)
                {
                    nextPartial = _clock.Elapsed + _request.PartialInterval;
                    partial(Build(final: false));
                }
            }

            _bytes.Finish(_ranges.Length);
            CopyUnreadable(_scanner);
            _elementsSkipped = elements?.Skipped ?? 0;

            // 32 / 64 bit で範囲が「自動」: 最小値・最大値が分かったので数える。全要素を保持していれば読み直さない。
            if (_wide is not null && _wideHistogram is null)
            {
                _wideHistogram = NewRangeHistogram(_wide.MinValue, _wide.MaxValue);
                if (_wide.HasAllValues)
                {
                    foreach (double v in _wide.StoredValues())
                    {
                        AddToHistogram(_wideHistogram, v);
                    }
                }
                else
                {
                    SecondPass(partial);
                }
            }

            return Build(final: true);
        }

        private void SecondPass(Action<StatisticsResult>? partial)
        {
            _total = _ranges.Length * 2;
            _operation?.SetTotal(_total);
            var scanner = new RangeScanner(_snapshot, _ranges, _request.ChunkSize, _token);
            var elements = new ElementExtractor(_spec.Size, _spec.EffectiveStride);
            ElementHandler handler = ElementHandlerFor(binOnly: true);
            TimeSpan nextPartial = _clock.Elapsed + _request.PartialInterval;
            foreach (ScanChunk chunk in scanner.Read())
            {
                _token.ThrowIfCancellationRequested();
                elements.Feed(chunk.Logical, chunk.Span, chunk.Bad, handler);
                _processed += chunk.Length;
                _operation?.Report(_processed);
                if (partial is not null && _clock.Elapsed >= nextPartial)
                {
                    nextPartial = _clock.Elapsed + _request.PartialInterval;
                    partial(Build(final: false));
                }
            }
        }

        private void CopyUnreadable(RangeScanner scanner)
        {
            foreach (HashRange r in scanner.Unreadable.Ranges)
            {
                _unreadable.Add(r.Offset, r.Length);
            }
        }

        private Histogram NewRangeHistogram(double min, double max)
        {
            int count = Math.Clamp(_request.Bins.Count, 2, BinSpec.MaxCount);
            if (double.IsInfinity(min) || double.IsInfinity(max) || double.IsNaN(min))
            {
                (min, max) = (0, 0);
            }

            return new Histogram(_type, new long[count], false, 0, min, max);
        }

        private static void AddToHistogram(Histogram h, double v)
        {
            int bin = h.BinOf(v);
            if (bin < 0)
            {
                h.Below++;
            }
            else if (bin >= h.BinCount)
            {
                h.Above++;
            }
            else
            {
                h.Counts[bin]++;
            }
        }

        private void CountPi(ReadOnlySpan<byte> b, long index)
        {
            // 6 バイトを 1 組の座標 (各 24 bit) とし、半径 2^24 − 1 の円の内側に入る割合から π を推定する (ent と同じ方法)。
            const long Radius = (1L << 24) - 1;
            long x = (b[0] << 16) | (b[1] << 8) | b[2];
            long y = (b[3] << 16) | (b[4] << 8) | b[5];
            _piTotal++;
            if ((x * x) + (y * y) <= Radius * Radius)
            {
                _piInside++;
            }
        }

        private ElementHandler ElementHandlerFor(bool binOnly)
        {
            bool big = _spec.BigEndian;
            switch (_type)
            {
                case ElementType.U16:
                case ElementType.S16:
                    return (b, index) =>
                    {
                        int raw = big ? (b[0] << 8) | b[1] : (b[1] << 8) | b[0];
                        _values16![raw]++;
                        double v = _type == ElementType.S16 ? (short)raw : raw;
                        AddPair(index, v);
                    };
                case ElementType.F32:
                case ElementType.F64:
                    return (b, index) =>
                    {
                        double v = ElementTypes.ReadDouble(_type, b, big);
                        if (binOnly)
                        {
                            if (double.IsFinite(v))
                            {
                                AddToHistogram(_wideHistogram!, v);
                            }

                            return;
                        }

                        if (_wide!.AddFloat(v))
                        {
                            AddPair(index, v);
                            if (_wideHistogram is not null)
                            {
                                AddToHistogram(_wideHistogram, v);
                            }
                        }
                        else
                        {
                            _previous = (-2, 0);
                        }
                    };
                default:
                    return (b, index) =>
                    {
                        Int128 value = ElementTypes.ReadInteger(_type, b, big);
                        if (binOnly)
                        {
                            AddToHistogram(_wideHistogram!, (double)value);
                            return;
                        }

                        _wide!.AddInteger(value);
                        AddPair(index, (double)value);
                        if (_wideHistogram is not null)
                        {
                            AddToHistogram(_wideHistogram, (double)value);
                        }
                    };
            }
        }

        /// <summary>隣り合う要素 (番号が続く要素) の組を系列相関に加える。</summary>
        private void AddPair(long index, double value)
        {
            if (_previous.Index == index - 1)
            {
                if (_wide is not null)
                {
                    _wide.Pairs.Add(_previous.Value, value);
                }
                else
                {
                    _pairs16.Add(_previous.Value, value);
                }
            }

            _previous = (index, value);
        }

        /// <summary>結果を組み立てる (途中結果は写しを使う)。</summary>
        public StatisticsResult Build(bool final)
        {
            long length = _ranges.Length;
            Histogram histogram;
            DescriptiveStats descriptive;
            long[] byteCounts = final ? _bytes.Histogram : (long[])_bytes.Histogram.Clone();
            switch (_type)
            {
                case ElementType.U8:
                case ElementType.S8:
                {
                    long[] counts = _type == ElementType.U8 ? byteCounts : [.. Enumerable.Range(0, 256).Select(i => byteCounts[(i + 128) & 0xFF])];
                    long first = ElementTypes.MinValue(_type);
                    histogram = new Histogram(_type, counts, true, first, first, first + 255);
                    descriptive = DescriptiveBuilder.FromValueCounts(_type, first, counts, BytePairs(),
                        _type == ElementType.U8 ? (_piInside, _piTotal) : null);
                    break;
                }

                case ElementType.U16:
                case ElementType.S16:
                {
                    long[] counts = _type == ElementType.U16 ? (long[])_values16!.Clone()
                        : [.. Enumerable.Range(0, 65536).Select(i => _values16![(i + 32768) & 0xFFFF])];
                    long first = ElementTypes.MinValue(_type);
                    descriptive = DescriptiveBuilder.FromValueCounts(_type, first, counts, _pairs16, null);
                    histogram = _request.Bins.PerValue
                        ? new Histogram(_type, counts, true, first, first, first + 65535)
                        : BinValueCounts(counts, first, descriptive);
                    break;
                }

                default:
                {
                    descriptive = final ? _wide!.Finish() : _wide!.Partial();
                    histogram = _wideHistogram?.Clone() ?? NewRangeHistogram(0, 0);
                    histogram.NaN = _wide.NaN;
                    histogram.PositiveInfinity = _wide.PositiveInfinity;
                    histogram.NegativeInfinity = _wide.NegativeInfinity;
                    break;
                }
            }

            long inBins = histogram.InBins;
            long elements = _wide is { } w ? w.Count + w.NaN + w.PositiveInfinity + w.NegativeInfinity : descriptive.Count;
            return new StatisticsResult
            {
                Ranges = _ranges,
                Element = _spec,
                Histogram = histogram,
                Descriptive = descriptive,
                Entropy = new EntropySummary(StatMath.Entropy(histogram.Counts, inBins), histogram.BinCount, inBins, _spec.Size),
                Classes = _type == ElementType.U8 ? ByteClassShares.From(byteCounts) : null,
                Blocks = _blocks is null ? null : final ? _blocks : _blocks.Clone(),
                Digram = _request.ByteFeatures && _bytes.DigramCounts is { } d ? new Digram(final ? d : (long[])d.Clone()) : null,
                Positions = _bytes.PositionCounts is { } p ? new PositionDistribution(length, final ? p : (long[])p.Clone()) : null,
                Elements = elements,
                Remainder = _spec.Remainder(length),
                Unreadable = final ? _unreadable : UnreadableSoFar(),
                Completed = final,
                Fraction = _total == 0 ? 1 : Math.Clamp((double)_processed / _total, 0, 1),
                BytesRead = _bytesRead,
            };
        }

        private UnreadableSummary UnreadableSoFar() => _scanner?.Unreadable.Clone() ?? new UnreadableSummary();

        /// <summary>u8 / s8 の系列相関: ダイグラムから隣り合うバイトの組の和を求める。</summary>
        private PairAccumulator BytePairs()
        {
            var pairs = new PairAccumulator();
            if (_bytes.DigramCounts is not { } d)
            {
                return pairs;
            }

            bool signed = _type == ElementType.S8;
            double n = 0;
            double sx = 0;
            double sy = 0;
            double sxx = 0;
            double syy = 0;
            double sxy = 0;
            for (int i = 0; i < 65536; i++)
            {
                long c = d[i];
                if (c == 0)
                {
                    continue;
                }

                double x = signed ? (sbyte)(i >> 8) : i >> 8;
                double y = signed ? (sbyte)(i & 0xFF) : i & 0xFF;
                n += c;
                sx += c * x;
                sy += c * y;
                sxx += c * x * x;
                syy += c * y * y;
                sxy += c * x * y;
            }

            return new PairAccumulator { N = (long)n, Sx = sx, Sy = sy, Sxx = sxx, Syy = syy, Sxy = sxy };
        }

        /// <summary>16 bit の値ごとの件数を、指定のビンにまとめる。</summary>
        private Histogram BinValueCounts(long[] counts, long first, DescriptiveStats descriptive)
        {
            BinSpec bins = _request.Bins;
            Histogram h = bins.IsManual
                ? new Histogram(_type, new long[Math.Clamp(bins.Count, 2, BinSpec.MaxCount)], false, 0, bins.Min!.Value, bins.Max!.Value)
                : NewRangeHistogram(descriptive.Count == 0 ? 0 : descriptive.Min, descriptive.Count == 0 ? 0 : descriptive.Max);
            for (int i = 0; i < counts.Length; i++)
            {
                long c = counts[i];
                if (c == 0)
                {
                    continue;
                }

                int bin = h.BinOf(first + i);
                if (bin < 0)
                {
                    h.Below += c;
                }
                else if (bin >= h.BinCount)
                {
                    h.Above += c;
                }
                else
                {
                    h.Counts[bin] += c;
                }
            }

            return h;
        }
    }
}
