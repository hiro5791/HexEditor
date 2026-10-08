using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Hashing;

/// <summary>計算するアルゴリズムとパラメータの組 (結果の表の 1 行になる)。</summary>
public sealed record HashAlgorithmChoice(HashAlgorithmInfo Algorithm, HashParameters Parameters)
{
    public HashAlgorithmChoice(HashAlgorithmInfo algorithm)
        : this(algorithm, HashParameters.Default)
    {
    }

    public string DisplayName => Algorithm.DisplayName(Parameters);
}

/// <summary>ドキュメント上の範囲。</summary>
public readonly record struct HashRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>マルチ選択の計算方法 (ANA-18 の仕様 1)。</summary>
public enum HashRangeMode
{
    /// <summary>連結して 1 つの値を求める (既定。オフセットの小さい順に連結)。</summary>
    Concatenate,

    /// <summary>範囲ごとに値を求める。</summary>
    PerRange,
}

/// <summary>除外の方法 (ANA-18 の仕様 2)。</summary>
public enum HashExclusionMode
{
    /// <summary>除外範囲のバイトを入力に含めない (既定)。</summary>
    Skip,

    /// <summary>除外範囲のバイトを指定した値とみなす。</summary>
    Replace,
}

/// <summary>計算の指定 (ANA-18)。</summary>
public sealed record HashRequest
{
    /// <summary>対象範囲。空ならドキュメント全体。</summary>
    public IReadOnlyList<HashRange> Ranges { get; init; } = [];

    public HashRangeMode RangeMode { get; init; } = HashRangeMode.Concatenate;

    public required IReadOnlyList<HashAlgorithmChoice> Algorithms { get; init; }

    public IReadOnlyList<HashRange> Exclusions { get; init; } = [];

    public HashExclusionMode ExclusionMode { get; init; } = HashExclusionMode.Skip;

    /// <summary>「置き換える」で使う値 (既定 0x00)。</summary>
    public byte ReplacementValue { get; init; }

    /// <summary>1 回の読み込みのバイト数。</summary>
    public int ChunkSize { get; init; } = HashEngine.DefaultChunkSize;
}

/// <summary>結果の表の 1 行 (ANA-18 の仕様 5)。</summary>
public sealed record HashResultRow(HashAlgorithmChoice Choice, byte[] Value, IReadOnlyList<HashRange> Ranges)
{
    public HashAlgorithmInfo Algorithm => Choice.Algorithm;

    /// <summary>範囲の先頭 (連結した場合は最初の範囲の先頭)。</summary>
    public long Start => Ranges.Count == 0 ? 0 : Ranges[0].Offset;

    /// <summary>計算した範囲の長さの合計 (除外の前)。</summary>
    public long Length => Ranges.Sum(r => r.Length);
}

/// <summary>計算の結果。</summary>
public sealed record HashComputation(IReadOnlyList<HashResultRow> Rows, IReadOnlyList<HashRange> Ranges, long BytesRead);

/// <summary>読めないバイトがあるため計算できない (ANA-18 の「エラー」)。</summary>
public sealed class HashReadException(UnreadableRange range)
    : IOException($"オフセット 0x{range.Offset:X} を読み込めないため計算できません。")
{
    public UnreadableRange Range { get; } = range;
}

/// <summary>
/// 対象を 1 回だけ読み込み、選んだすべてのアルゴリズムを同時に計算する (ANA-18 の仕様 4)。読み込みと計算を重ね、
/// 複数のアルゴリズムは並列に計算する。開始時のスナップショットを読むため、計算中の編集は結果に影響しない (0.2)。
/// </summary>
public static class HashEngine
{
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>「自動で再計算」をする対象の上限 (ANA-18 の仕様 7)。これを超える場合は自動で計算しない。</summary>
    public const long AutoComputeLimit = 64L * 1024 * 1024;

    /// <summary>
    /// 範囲を正規化する: オフセット順に並べ、重なり・隣接をまとめ、ドキュメントの長さで切り詰める (0.1 の「重なった部分は 1 回だけ数える」)。
    /// 空の指定はドキュメント全体。
    /// </summary>
    public static IReadOnlyList<HashRange> Normalize(IReadOnlyList<HashRange> ranges, long documentLength, bool mergeAdjacent = true)
    {
        if (ranges.Count == 0)
        {
            return [new HashRange(0, documentLength)];
        }

        var sorted = ranges
            .Select(r =>
            {
                long start = Math.Clamp(r.Offset, 0, documentLength);
                return new HashRange(start, Math.Max(0, Math.Min(r.End, documentLength) - start));
            })
            .Where(r => r.Length > 0)
            .OrderBy(r => r.Offset)
            .ToList();
        var result = new List<HashRange>();
        foreach (HashRange r in sorted)
        {
            if (result.Count > 0 && (result[^1].End > r.Offset || (mergeAdjacent && result[^1].End == r.Offset)))
            {
                HashRange last = result[^1];
                result[^1] = last with { Length = Math.Max(last.End, r.End) - last.Offset };
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }

    /// <summary>対象範囲の外にある除外範囲 (ANA-18 の「エラー」。計算では無視する)。</summary>
    public static IReadOnlyList<HashRange> ExclusionsOutside(IReadOnlyList<HashRange> targets, IReadOnlyList<HashRange> exclusions) =>
        [.. exclusions.Where(e => e.Length <= 0 || !targets.Any(t => t.Offset < e.End && e.Offset < t.End))];

    /// <summary>計算で読むバイト数 (進捗の全体量)。</summary>
    public static long TotalBytes(DocumentSnapshot snapshot, HashRequest request) =>
        Normalize(request.Ranges, snapshot.Length, request.RangeMode == HashRangeMode.Concatenate).Sum(r => r.Length);

    /// <summary>
    /// 計算する。<paramref name="operation"/> があれば進捗を報告し、キャンセルされれば <see cref="OperationCanceledException"/> を投げる
    /// (結果は破棄する。ANA-18 の「巨大ファイル・長時間処理」)。
    /// </summary>
    public static HashComputation Compute(DocumentSnapshot snapshot, HashRequest request, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Algorithms.Count == 0)
        {
            return new HashComputation([], [], 0);
        }

        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        bool perRange = request.RangeMode == HashRangeMode.PerRange;
        IReadOnlyList<HashRange> ranges = Normalize(request.Ranges, snapshot.Length, mergeAdjacent: !perRange);
        IReadOnlyList<HashRange> exclusions = Normalize(
            [.. request.Exclusions.Where(e => e.Length > 0)], snapshot.Length, mergeAdjacent: true);
        if (request.Exclusions.Count == 0)
        {
            exclusions = [];
        }

        var reader = new ChunkReader(snapshot, Math.Max(4096, request.ChunkSize), operation, token);
        var rows = new List<HashResultRow>();
        if (perRange)
        {
            foreach (HashRange range in ranges)
            {
                IHasher[] hashers = [.. request.Algorithms.Select(a => a.Algorithm.CreateHasher(a.Parameters))];
                reader.Feed([range], exclusions, request, hashers);
                rows.AddRange(request.Algorithms.Select((a, i) => new HashResultRow(a, hashers[i].Finish(), [range])));
            }
        }
        else
        {
            IHasher[] hashers = [.. request.Algorithms.Select(a => a.Algorithm.CreateHasher(a.Parameters))];
            reader.Feed(ranges, exclusions, request, hashers);
            rows.AddRange(request.Algorithms.Select((a, i) => new HashResultRow(a, hashers[i].Finish(), ranges)));
        }

        return new HashComputation(rows, ranges, reader.BytesRead);
    }

    /// <summary>1 つの値をその場で計算する (テスト・小さなデータ用)。</summary>
    public static byte[] ComputeBytes(HashAlgorithmInfo algorithm, ReadOnlySpan<byte> data, HashParameters? parameters = null)
    {
        IHasher hasher = algorithm.CreateHasher(parameters);
        hasher.Append(data);
        return hasher.Finish();
    }

    /// <summary>範囲を順に読み、読み込みと計算を重ねて全アルゴリズムに渡す。</summary>
    private sealed class ChunkReader(DocumentSnapshot snapshot, int chunkSize, LongRunningOperation? operation, CancellationToken token)
    {
        private byte[] _current = new byte[chunkSize];
        private byte[] _next = new byte[chunkSize];
        private byte[]? _fill;

        public long BytesRead { get; private set; }

        public void Feed(IReadOnlyList<HashRange> ranges, IReadOnlyList<HashRange> exclusions, HashRequest request, IHasher[] hashers)
        {
            // 読む単位: 範囲を除外で分けた部分。置き換える部分は読まずに値で埋める。
            List<(long Offset, long Length, bool Replace)> parts = Split(ranges, exclusions, request.ExclusionMode);
            Task<int>? pending = null;
            int partIndex = 0;
            long partDone = 0;

            // 次に読む位置を進め、読むべき部分を返す (読まない「置き換える」部分は Replace = true)。
            (long Offset, int Length, bool Replace)? NextPiece()
            {
                while (partIndex < parts.Count)
                {
                    (long offset, long length, bool replace) = parts[partIndex];
                    if (partDone >= length)
                    {
                        partIndex++;
                        partDone = 0;
                        continue;
                    }

                    int n = (int)Math.Min(chunkSize, length - partDone);
                    long at = offset + partDone;
                    partDone += n;
                    return (at, n, replace);
                }

                return null;
            }

            (long Offset, int Length, bool Replace)? piece = NextPiece();
            if (piece is { Replace: false } first)
            {
                pending = StartRead(_current, first.Offset, first.Length);
            }

            while (piece is { } p)
            {
                token.ThrowIfCancellationRequested();
                byte[] data;
                if (p.Replace)
                {
                    data = Fill(request.ReplacementValue);
                }
                else
                {
                    pending!.GetAwaiter().GetResult();
                    data = _current;
                }

                // 次の部分の読み込みを先に始めてから、今の部分を計算する。
                (long Offset, int Length, bool Replace)? next = NextPiece();
                pending = next is { Replace: false } n ? StartRead(_next, n.Offset, n.Length) : null;
                Hash(hashers, data, p.Length);
                if (!p.Replace)
                {
                    BytesRead += p.Length;
                }

                operation?.Report(BytesRead);
                if (next is { Replace: false })
                {
                    (_current, _next) = (_next, _current);
                }

                piece = next;
            }
        }

        private Task<int> StartRead(byte[] buffer, long offset, int length) => Task.Run(() =>
        {
            ReadResult r = snapshot.Read(offset, buffer.AsSpan(0, length));
            if (r.Unreadable.Count > 0)
            {
                throw new HashReadException(r.Unreadable[0]);
            }

            if (r.BytesReturned < length)
            {
                throw new HashReadException(new UnreadableRange(offset + r.BytesReturned, length - r.BytesReturned, UnreadableReason.IoError));
            }

            return r.BytesReturned;
        }, token);

        private byte[] Fill(byte value)
        {
            if (_fill is null || _fill[0] != value)
            {
                _fill = new byte[chunkSize];
                _fill.AsSpan().Fill(value);
            }

            return _fill;
        }

        private static void Hash(IHasher[] hashers, byte[] data, int length)
        {
            if (hashers.Length == 1)
            {
                hashers[0].Append(data.AsSpan(0, length));
                return;
            }

            // 選んだアルゴリズムはすべて同じ読み込みデータを並列に計算する (ANA-18 の仕様 4)。
            Parallel.For(0, hashers.Length, i => hashers[i].Append(data.AsSpan(0, length)));
        }

        private static List<(long Offset, long Length, bool Replace)> Split(IReadOnlyList<HashRange> ranges, IReadOnlyList<HashRange> exclusions,
            HashExclusionMode mode)
        {
            var parts = new List<(long, long, bool)>();
            foreach (HashRange range in ranges)
            {
                long at = range.Offset;
                foreach (HashRange e in exclusions)
                {
                    long from = Math.Max(e.Offset, range.Offset);
                    long to = Math.Min(e.End, range.End);
                    if (from >= to || from < at)
                    {
                        continue;
                    }

                    if (from > at)
                    {
                        parts.Add((at, from - at, false));
                    }

                    if (mode == HashExclusionMode.Replace)
                    {
                        parts.Add((from, to - from, true));
                    }

                    at = to;
                }

                if (at < range.End)
                {
                    parts.Add((at, range.End - at, false));
                }
            }

            return parts;
        }
    }
}
