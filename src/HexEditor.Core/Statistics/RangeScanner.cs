using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Statistics;

/// <summary>順次読み込みの 1 回分。<see cref="Bad"/> は読めなかったバイトの位置 (<see cref="Buffer"/> の中の位置)。</summary>
internal readonly record struct ScanChunk(long Logical, long Document, byte[] Buffer, int Length, IReadOnlyList<(int Start, int Length)> Bad)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public bool HasBad => Bad.Count > 0;

    /// <summary>読めたバイトの部分 (読めない部分で区切る)。</summary>
    public IEnumerable<(int Start, int Length)> GoodParts()
    {
        int at = 0;
        foreach ((int s, int l) in Bad)
        {
            if (s > at)
            {
                yield return (at, s - at);
            }

            at = Math.Max(at, s + l);
        }

        if (at < Length)
        {
            yield return (at, Length - at);
        }
    }
}

/// <summary>
/// 対象範囲 (連結した論理的な並び) を先頭から順に読む。次の読み込みを先に始めて計算と重ねる (読み込みは 1 回だけ)。
/// 読めなかった範囲は除いて続け、<see cref="Unreadable"/> に記録する (ANA-10 の「エラー」)。
/// </summary>
internal sealed class RangeScanner(DocumentSnapshot snapshot, LogicalRanges ranges, int chunkSize, CancellationToken token)
{
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    private readonly int _chunkSize = Math.Max(4096, chunkSize);

    public UnreadableSummary Unreadable { get; } = new();

    /// <summary>読んだバイト数 (読めなかった部分を含む)。</summary>
    public long BytesRead { get; private set; }

    /// <summary>論理範囲 [from, to) を読む。バッファは使い回すので、次の要素を求める前に使い終えること。</summary>
    public IEnumerable<ScanChunk> Read(long from = 0, long to = long.MaxValue)
    {
        to = Math.Min(to, ranges.Length);
        var pieces = new List<(long Logical, long Document, int Length)>();
        foreach (HashRange r in ranges.ToDocumentRanges(from, to))
        {
            long logical = ranges.ToLogical(r.Offset)!.Value;
            for (long done = 0; done < r.Length; done += _chunkSize)
            {
                pieces.Add((logical + done, r.Offset + done, (int)Math.Min(_chunkSize, r.Length - done)));
            }
        }

        return ReadPieces(pieces);
    }

    private IEnumerable<ScanChunk> ReadPieces(List<(long Logical, long Document, int Length)> pieces)
    {
        if (pieces.Count == 0)
        {
            yield break;
        }

        byte[] current = new byte[Math.Min(_chunkSize, pieces.Max(p => p.Length))];
        byte[] next = new byte[current.Length];
        Task<List<(int, int)>> pending = StartRead(current, pieces[0]);
        for (int i = 0; i < pieces.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            List<(int, int)> bad = pending.GetAwaiter().GetResult();
            (long logical, long document, int length) = pieces[i];
            if (i + 1 < pieces.Count)
            {
                pending = StartRead(next, pieces[i + 1]);
            }

            BytesRead += length;
            foreach ((int s, int l) in bad)
            {
                Unreadable.Add(document + s, l);
            }

            yield return new ScanChunk(logical, document, current, length, bad);
            (current, next) = (next, current);
        }
    }

    private Task<List<(int, int)>> StartRead(byte[] buffer, (long Logical, long Document, int Length) piece) => Task.Run(() =>
    {
        ReadResult r = snapshot.Read(piece.Document, buffer.AsSpan(0, piece.Length));
        var bad = new List<(int, int)>();
        foreach (UnreadableRange u in r.Unreadable)
        {
            int s = (int)Math.Clamp(u.Offset - piece.Document, 0, piece.Length);
            int e = (int)Math.Clamp(u.End - piece.Document, 0, piece.Length);
            if (e > s)
            {
                bad.Add((s, e - s));
            }
        }

        if (r.BytesReturned < piece.Length)
        {
            bad.Add((r.BytesReturned, piece.Length - r.BytesReturned));
        }

        bad.Sort((a, b) => a.Item1.CompareTo(b.Item1));

        // 重なりをまとめる。
        var merged = new List<(int, int)>(bad.Count);
        foreach ((int s, int l) in bad)
        {
            if (merged.Count > 0 && merged[^1].Item1 + merged[^1].Item2 >= s)
            {
                (int ms, int ml) = merged[^1];
                merged[^1] = (ms, Math.Max(ms + ml, s + l) - ms);
            }
            else
            {
                merged.Add((s, l));
            }
        }

        return merged;
    }, token);
}
