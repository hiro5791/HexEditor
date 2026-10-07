using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Search;

/// <summary>見つかった一致。<see cref="Wrapped"/> は折り返して見つけたか (FIND-09 の仕様 4)。</summary>
public readonly record struct SearchHit(long Offset, long Length, bool Wrapped);

/// <summary>
/// スナップショットに対する検索 (FIND-01、FIND-03、FIND-09)。チャンク単位で読み、前のチャンクの末尾を
/// 「パターンの長さ − 1」バイト重ねて読むため、チャンクの境界をまたぐ一致も見つかる。リテラルは SIMD 化された
/// <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> を使う。
/// </summary>
public static class SearchEngine
{
    /// <summary>チャンクの既定のサイズ (FIND-01 の仕様 2)。</summary>
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// <paramref name="start"/> から次 (前方) または前 (後方) の一致を探す。前方は start 以上で始まる一致、後方は
    /// start 未満で始まる一致を探す。<paramref name="wrap"/> なら反対側から開始位置まで続ける。
    /// </summary>
    public static SearchHit? Find(DocumentSnapshot snapshot, SearchPattern pattern, long start, bool forward, bool wrap,
        LongRunningOperation? operation = null, int chunkSize = DefaultChunkSize)
    {
        long length = snapshot.Length;
        start = Math.Clamp(start, 0, length);
        var progress = new Progress(operation, length);
        if (forward)
        {
            long? hit = FindForward(snapshot, pattern, start, length, chunkSize, progress);
            if (hit is null && wrap && start > 0)
            {
                hit = FindForward(snapshot, pattern, 0, Math.Min(length, start + pattern.Length - 1), chunkSize, progress);
                return hit is long w ? new SearchHit(w, pattern.Length, true) : null;
            }

            return hit is long h ? new SearchHit(h, pattern.Length, false) : null;
        }
        else
        {
            long? hit = FindBackward(snapshot, pattern, 0, Math.Min(length, start - 1 + pattern.Length), chunkSize, progress);
            if (hit is null && wrap && start < length)
            {
                hit = FindBackward(snapshot, pattern, start, length, chunkSize, progress);
                return hit is long w ? new SearchHit(w, pattern.Length, true) : null;
            }

            return hit is long h ? new SearchHit(h, pattern.Length, false) : null;
        }
    }

    /// <summary>[from, to) の中にすっかり収まる最初の一致の開始位置。</summary>
    private static long? FindForward(DocumentSnapshot snapshot, SearchPattern pattern, long from, long to, int chunkSize, Progress progress)
    {
        int overlap = pattern.Length - 1;
        byte[] buffer = new byte[chunkSize + overlap];
        for (long pos = from; pos <= to - pattern.Length; pos += chunkSize)
        {
            progress.Check();
            int count = (int)Math.Min(buffer.Length, to - pos);
            snapshot.Read(pos, buffer.AsSpan(0, count));
            int found = IndexOf(buffer.AsSpan(0, count), pattern);
            if (found >= 0)
            {
                return pos + found;
            }

            progress.Advance(Math.Min(chunkSize, count));
        }

        return null;
    }

    /// <summary>[from, to) の中にすっかり収まる最後の一致の開始位置。チャンクを末尾側から順に読む (FIND-01 の仕様 8)。</summary>
    private static long? FindBackward(DocumentSnapshot snapshot, SearchPattern pattern, long from, long to, int chunkSize, Progress progress)
    {
        int overlap = pattern.Length - 1;
        byte[] buffer = new byte[chunkSize + overlap];
        for (long end = to; end - pattern.Length >= from; end -= chunkSize)
        {
            progress.Check();
            long pos = Math.Max(from, end - buffer.Length);
            int count = (int)(end - pos);
            snapshot.Read(pos, buffer.AsSpan(0, count));
            int found = LastIndexOf(buffer.AsSpan(0, count), pattern);
            if (found >= 0)
            {
                return pos + found;
            }

            progress.Advance(Math.Min(chunkSize, count));
        }

        return null;
    }

    /// <summary>バッファの中の最初の一致の位置。ワイルドカードは、ワイルドカードのない最も長い区間で候補を絞り込んで照合する。</summary>
    internal static int IndexOf(ReadOnlySpan<byte> data, SearchPattern pattern)
    {
        if (pattern.IsLiteral)
        {
            return data.IndexOf(pattern.Bytes);
        }

        if (pattern.AnchorLength == 0)
        {
            // すべてワイルドカード (例: ?? ??): 先頭から順に照合する。
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                if (pattern.MatchesAt(data[i..]))
                {
                    return i;
                }
            }

            return -1;
        }

        ReadOnlySpan<byte> anchor = pattern.Bytes.AsSpan(pattern.AnchorOffset, pattern.AnchorLength);
        int searchFrom = pattern.AnchorOffset;
        while (searchFrom <= data.Length - pattern.Length + pattern.AnchorOffset)
        {
            int limit = data.Length - pattern.Length + pattern.AnchorOffset + pattern.AnchorLength;
            int found = data[searchFrom..limit].IndexOf(anchor);
            if (found < 0)
            {
                return -1;
            }

            int candidate = searchFrom + found - pattern.AnchorOffset;
            if (pattern.MatchesAt(data[candidate..]))
            {
                return candidate;
            }

            searchFrom += found + 1;
        }

        return -1;
    }

    internal static int LastIndexOf(ReadOnlySpan<byte> data, SearchPattern pattern)
    {
        if (pattern.IsLiteral)
        {
            return data.LastIndexOf(pattern.Bytes);
        }

        for (int i = data.Length - pattern.Length; i >= 0; i--)
        {
            if (pattern.MatchesAt(data[i..]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>進捗の報告とキャンセルの確認 (ENG-09)。</summary>
    private sealed class Progress(LongRunningOperation? operation, long total)
    {
        private long _done;

        public void Check() => operation?.CancellationToken.ThrowIfCancellationRequested();

        public void Advance(long bytes)
        {
            _done = Math.Min(total, _done + bytes);
            operation?.Report(_done);
        }
    }
}
