using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>表示に返すバイトの状態 (ENG-06 の「画面」)。</summary>
public enum ByteState : byte
{
    Valid,
    Loading,
    Unreadable,
}

/// <summary>
/// ある時点のドキュメントの内容 (ENG-05)。不変で、スレッド間で共有できる。バックグラウンド処理は開始時の
/// スナップショットを読み、処理中に編集が続いても開始時の内容に対する結果を返す。
/// </summary>
public sealed class DocumentSnapshot
{
    private readonly DocumentStorage _storage;

    internal DocumentSnapshot(DocumentStorage storage, PieceTree tree)
    {
        _storage = storage;
        Tree = tree;
    }

    public PieceTree Tree { get; }

    public long Length => Tree.Length;

    internal DocumentStorage Storage => _storage;

    /// <summary>
    /// 表示用の読み込み。ブロックしない。キャッシュにない元データは読み込みを始めて <see cref="ByteState.Loading"/> にする。
    /// <paramref name="states"/> は <paramref name="destination"/> と同じ長さ。末尾を越えた部分は書き換えない。
    /// </summary>
    /// <returns>返したバイト数 (末尾までで切り詰めた長さ)。</returns>
    public int ReadForDisplay(long offset, Span<byte> destination, Span<ByteState> states)
    {
        if (states.Length < destination.Length)
        {
            throw new ArgumentException("states が短すぎます。", nameof(states));
        }

        int count = CountWithinLength(offset, destination.Length);
        foreach ((long docOffset, Piece piece) in Tree.Enumerate(offset, count))
        {
            int at = (int)(docOffset - offset);
            int len = (int)piece.Length;
            Span<byte> dst = destination.Slice(at, len);
            Span<ByteState> st = states.Slice(at, len);
            if (piece.Kind == PieceKind.Original)
            {
                ReadOriginalForDisplay(piece.Offset, dst, st);
            }
            else if (piece.Kind == PieceKind.External)
            {
                ReadExternalForDisplay(piece, dst, st);
            }
            else
            {
                ReadGenerated(piece, dst);
                st.Fill(ByteState.Valid);
            }
        }

        return count;
    }

    /// <summary>
    /// バックグラウンド用の読み込み。必要ならデータソースを待つ。読めなかった範囲はドキュメント上の位置で返す。
    /// </summary>
    public ReadResult Read(long offset, Span<byte> destination)
    {
        int count = CountWithinLength(offset, destination.Length);
        List<UnreadableRange>? bad = null;
        foreach ((long docOffset, Piece piece) in Tree.Enumerate(offset, count))
        {
            Span<byte> dst = destination.Slice((int)(docOffset - offset), (int)piece.Length);
            if (piece.Kind == PieceKind.Original)
            {
                ReadResult r = _storage.Cache.ReadDirect(piece.Offset, dst);
                foreach (UnreadableRange u in r.Unreadable)
                {
                    (bad ??= []).Add(u with { Offset = u.Offset - piece.Offset + docOffset });
                }
            }
            else if (piece.Kind == PieceKind.External)
            {
                ReadResult r = _storage.Externals[piece.ExternalIndex].Read(piece.Offset, dst);
                foreach (UnreadableRange u in r.Unreadable)
                {
                    (bad ??= []).Add(u with { Offset = u.Offset - piece.Offset + docOffset });
                }
            }
            else
            {
                ReadGenerated(piece, dst);
            }
        }

        return new ReadResult(count, bad);
    }

    /// <summary>
    /// 変更された範囲を先頭から列挙する (ENG-02 の仕様 7)。元データ以外のピースと、ドキュメント上の位置と
    /// 元データ上の位置が異なる元データのピースが対象。隣り合う範囲はまとめる。
    /// </summary>
    public IEnumerable<(long Offset, long Length)> EnumerateModifiedRanges()
    {
        long start = -1;
        long end = -1;
        foreach ((long docOffset, Piece piece) in Tree.EnumerateAll())
        {
            bool modified = piece.Kind != PieceKind.Original || piece.Offset != docOffset;
            if (!modified)
            {
                continue;
            }

            if (start >= 0 && docOffset == end)
            {
                end += piece.Length;
                continue;
            }

            if (start >= 0)
            {
                yield return (start, end - start);
            }

            start = docOffset;
            end = docOffset + piece.Length;
        }

        if (start >= 0)
        {
            yield return (start, end - start);
        }
    }

    /// <summary>[offset, offset + length) の中の変更された範囲 (表示の強調用。範囲外のピースは見ない)。</summary>
    public IEnumerable<(long Offset, long Length)> EnumerateModifiedRanges(long offset, long length)
    {
        length = Math.Min(length, Math.Max(0, Length - offset));
        foreach ((long docOffset, Piece piece) in Tree.Enumerate(offset, length))
        {
            // 切り詰めたピースでも、元データ上の位置とドキュメント上の位置の差は変わらない。
            if (piece.Kind != PieceKind.Original || piece.Offset != docOffset)
            {
                yield return (docOffset, piece.Length);
            }
        }
    }

    /// <summary>上書き・挿入の区別のために前後をたどるピースの数の上限 (超えたら上書きとして扱う)。</summary>
    private const int ChangeWalkLimit = 4096;

    /// <summary>
    /// [offset, offset + length) の中の、元データ以外のピースに含まれるバイト (変更されたバイト。VIEW-15 の仕様 2) を、上書きか挿入かの
    /// 区別を付けて列挙する (VIEW-15 の仕様 4)。前後の元データのピースの間で、元データ上で飛ばした長さ (置き換えられたバイト数) までを
    /// 上書き、それを超える分を挿入とみなす。前後のピースが遠すぎて分からない場合は上書きとして扱う。
    /// </summary>
    public IEnumerable<(long Offset, long Length, bool Inserted)> EnumerateChanges(long offset, long length)
    {
        length = Math.Min(length, Math.Max(0, Length - offset));
        if (length <= 0 || offset < 0)
        {
            yield break;
        }

        long end = offset + length;
        long at = offset;
        while (at < end)
        {
            if (Tree.PieceAt(at) is not { } found)
            {
                yield break;
            }

            (long start, Piece piece) = found;
            if (piece.Kind == PieceKind.Original)
            {
                at = start + piece.Length;
                continue;
            }

            (long runStart, long runEnd, long overwritten) = ChangeRun(start, start + piece.Length);
            long split = runStart + overwritten;
            long from = Math.Max(at, runStart);
            long to = Math.Min(end, runEnd);
            if (from < Math.Min(split, to))
            {
                yield return (from, Math.Min(split, to) - from, false);
            }

            if (Math.Max(from, split) < to)
            {
                yield return (Math.Max(from, split), to - Math.Max(from, split), true);
            }

            at = runEnd;
        }
    }

    /// <summary>元データ以外のピースが続く範囲と、そのうち上書きとみなすバイト数。</summary>
    private (long Start, long End, long Overwritten) ChangeRun(long pieceStart, long pieceEnd)
    {
        long runStart = pieceStart;
        long previousSourceEnd = 0;
        bool known = true;
        int steps = 0;
        while (true)
        {
            if (runStart == 0)
            {
                break;
            }

            if (++steps > ChangeWalkLimit || Tree.PieceAt(runStart - 1) is not { } p)
            {
                known = false;
                break;
            }

            if (p.Piece.Kind == PieceKind.Original)
            {
                previousSourceEnd = p.Piece.Offset + p.Piece.Length;
                break;
            }

            runStart = p.Start;
        }

        long runEnd = pieceEnd;
        long nextSourceStart = _storage.Source.Length;
        steps = 0;
        while (runEnd < Length)
        {
            if (++steps > ChangeWalkLimit || Tree.PieceAt(runEnd) is not { } p)
            {
                known = false;
                break;
            }

            if (p.Piece.Kind == PieceKind.Original)
            {
                nextSourceStart = p.Piece.Offset;
                break;
            }

            runEnd = p.Start + p.Piece.Length;
        }

        long run = runEnd - runStart;
        long gap = Math.Max(0, nextSourceStart - previousSourceEnd);
        return (runStart, runEnd, known ? Math.Min(gap, run) : run);
    }

    private int CountWithinLength(long offset, int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return offset >= Length ? 0 : (int)Math.Min(requested, Length - offset);
    }

    private void ReadOriginalForDisplay(long sourceOffset, Span<byte> destination, Span<ByteState> states)
    {
        BlockCache cache = _storage.Cache;
        int done = 0;
        while (done < destination.Length)
        {
            long pos = sourceOffset + done;
            long index = cache.BlockIndexOf(pos);
            int inBlock = (int)(pos - index * cache.BlockSize);
            int n = Math.Min(cache.BlockSize - inBlock, destination.Length - done);
            if (cache.TryGetBlock(index, out CachedBlock block))
            {
                int valid = Math.Max(0, Math.Min(n, block.Length - inBlock));
                block.Data.AsSpan(inBlock, valid).CopyTo(destination[done..]);
                states.Slice(done, valid).Fill(ByteState.Valid);
                if (valid < n)
                {
                    // 開いた後に元データが短くなった部分は読めない扱い。
                    states.Slice(done + valid, n - valid).Fill(ByteState.Unreadable);
                }

                foreach (UnreadableRange r in block.Unreadable)
                {
                    long from = Math.Max(r.Offset, pos);
                    long to = Math.Min(r.End, pos + n);
                    if (from < to)
                    {
                        states.Slice(done + (int)(from - pos), (int)(to - from)).Fill(ByteState.Unreadable);
                    }
                }
            }
            else
            {
                cache.Request(index, ReadPriority.Display);
                destination.Slice(done, n).Clear();
                states.Slice(done, n).Fill(ByteState.Loading);
            }

            done += n;
        }
    }

    private void ReadExternalForDisplay(Piece piece, Span<byte> destination, Span<ByteState> states)
    {
        IByteSource source = _storage.Externals[piece.ExternalIndex];
        if (source is SnapshotRange range)
        {
            range.ReadForDisplay(piece.Offset, destination, states);
            return;
        }

        // 復旧用データの一時ファイルなど: ローカルのファイルなのでそのまま読む。
        ReadResult r = source.Read(piece.Offset, destination);
        states.Fill(ByteState.Valid);
        foreach (UnreadableRange u in r.Unreadable)
        {
            states.Slice((int)(u.Offset - piece.Offset), (int)u.Length).Fill(ByteState.Unreadable);
        }
    }

    /// <summary>外部参照のピースが指すデータ (復旧用データの書き出しで使う)。</summary>
    internal IByteSource ExternalSource(int index) => _storage.Externals[index];

    private void ReadGenerated(Piece piece, Span<byte> destination)
    {
        switch (piece.Kind)
        {
            case PieceKind.Added:
                _storage.AddBuffer.Read(piece.Offset, destination);
                break;
            case PieceKind.Pattern:
                Span<byte> pattern = stackalloc byte[piece.PatternLength];
                _storage.AddBuffer.Read(piece.Offset, pattern);
                GeneratedData.FillPattern(pattern, piece.Phase, destination);
                break;
            case PieceKind.Random:
                GeneratedData.FillRandom(piece.Seed, piece.Offset, destination);
                break;
            default:
                throw new InvalidOperationException();
        }
    }
}

/// <summary>ドキュメントのスナップショットが共有する、データの置き場所。</summary>
internal sealed class DocumentStorage(Document owner, IByteSource source, AddBuffer addBuffer, BlockCache cache)
{
    /// <summary>このデータの持ち主のドキュメント。</summary>
    public Document Owner { get; } = owner;

    /// <summary>外部参照のピース (<see cref="PieceKind.External"/>) が指すデータの表。追記だけで、番号は変えない。</summary>
    public List<IByteSource> Externals { get; } = [];

    /// <summary>元データ。その場保存の後は、保存前の内容を返す重ね合わせ (OverlayByteSource) に差し替える。</summary>
    public IByteSource Source { get; set; } = source;

    public AddBuffer AddBuffer { get; } = addBuffer;

    public BlockCache Cache { get; set; } = cache;
}
