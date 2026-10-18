using HexEditor.Core.Clipboard;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Sources;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;

namespace HexEditor.Core.Patches;

/// <summary>
/// 変換後のデータの一部分。<see cref="FromSource"/> なら変換前の <see cref="SourceOffset"/> からの同じ内容、そうでなければ
/// 変換後のデータそのもの (パッチに入れるデータ)。
/// </summary>
public readonly record struct PatchSegment(bool FromSource, long SourceOffset, long TargetOffset, long Length);

/// <summary>
/// 差分 (EDIT-42 の仕様 1): 変換後のデータを、変換前からのコピーとそれ以外のデータの並びで表したもの。各形式の書き出しはこれから作る。
/// 変換前・変換後のデータは <see cref="ReadSource"/>・<see cref="ReadTarget"/> で読む (どちらもスナップショットなので、作成中の編集の影響を受けない)。
/// </summary>
public sealed class PatchScript
{
    public PatchScript(long sourceLength, long targetLength, IReadOnlyList<PatchSegment> segments, ByteReader readSource, ByteReader readTarget)
    {
        SourceLength = sourceLength;
        TargetLength = targetLength;
        Segments = segments;
        ReadSource = readSource;
        ReadTarget = readTarget;
    }

    public long SourceLength { get; }

    public long TargetLength { get; }

    /// <summary>変換後のデータの先頭から順の部分。隙間なく変換後の全体を覆う。</summary>
    public IReadOnlyList<PatchSegment> Segments { get; }

    public ByteReader ReadSource { get; }

    public ByteReader ReadTarget { get; }

    /// <summary>変換前から同じ位置にコピーした部分以外 (位置で比べる形式の差分の候補。IPS・UPS)。変換後の長さを超える部分は含まない。</summary>
    public IEnumerable<(long Offset, long Length)> PositionalCandidates()
    {
        foreach (PatchSegment s in Segments)
        {
            if (!s.FromSource || s.SourceOffset != s.TargetOffset)
            {
                yield return (s.TargetOffset, s.Length);
            }
        }
    }

    /// <summary>
    /// 位置で比べた差分の範囲 (変換後の位置。値が違うバイトだけ)。IPS・IPS32・UPS が使う。変換後が長くなった部分はすべて差分。
    /// </summary>
    public List<(long Offset, long Length)> PositionalChanges(CancellationToken cancellationToken = default)
    {
        var candidates = new List<(long, long)>();
        foreach ((long o, long l) in PositionalCandidates())
        {
            if (candidates.Count > 0 && candidates[^1].Item1 + candidates[^1].Item2 == o)
            {
                candidates[^1] = (candidates[^1].Item1, candidates[^1].Item2 + l);
            }
            else
            {
                candidates.Add((o, l));
            }
        }

        return IpsPatch.Refine(candidates, ReadTarget, TargetLength, ReadSource, SourceLength, cancellationToken);
    }

    /// <summary>
    /// 置き換え・挿入・削除の命令 (独自形式。EDIT-42 の仕様 2)。変換前からのコピーのうち、変換前の位置が単調に増えるものを一致とし、
    /// その間を命令にする。間の長さが違う場合は、短い方の長さの置き換えと、残りの挿入か削除にする。
    /// </summary>
    public List<HexPatchOp> Operations()
    {
        var ops = new List<HexPatchOp>();
        long source = 0, target = 0;
        void Gap(long sourceEnd, long targetEnd)
        {
            long s = sourceEnd - source, t = targetEnd - target;
            long common = Math.Min(s, t);
            if (common > 0)
            {
                ops.Add(new HexPatchOp(HexPatchOpKind.Replace, source, common, target, common));
            }

            if (t > common)
            {
                ops.Add(new HexPatchOp(HexPatchOpKind.Insert, source + common, 0, target + common, t - common));
            }
            else if (s > common)
            {
                ops.Add(new HexPatchOp(HexPatchOpKind.Delete, source + common, s - common, target + common, 0));
            }
        }

        foreach (PatchSegment seg in Segments)
        {
            if (seg.FromSource && seg.SourceOffset >= source)
            {
                Gap(seg.SourceOffset, seg.TargetOffset);
                source = seg.SourceOffset + seg.Length;
                target = seg.TargetOffset + seg.Length;
            }
        }

        Gap(SourceLength, TargetLength);
        return ops;
    }
}

/// <summary>差分の取り方 (EDIT-42 の仕様 1)。</summary>
public static class PatchDiff
{
    /// <summary>
    /// 保存されている内容と現在の内容 (既定): データエンジンのピースツリーから変更箇所を求める (読み込み不要。O(ピース数))。
    /// </summary>
    public static PatchScript FromSaved(DocumentSnapshot current)
    {
        long sourceLength = current.OriginalSource.Length;
        var original = new DocumentSnapshot(current.Storage, sourceLength > 0 ? PieceTree.FromPiece(Piece.Original(0, sourceLength)) : PieceTree.Empty);
        return Between(original, current);
    }

    /// <summary>
    /// 2 つのスナップショットの差分 (履歴の 2 時点、2 つのタブ)。同じデータエンジンの版ならピースツリーを照らし合わせて求め (O(ピース数 × log))、
    /// 元データが違う場合 (別のファイル、保存をまたぐ時点) は、同じ位置のバイトを比べる (ファイルサイズに比例する長時間処理)。
    /// </summary>
    public static PatchScript Between(DocumentSnapshot source, DocumentSnapshot target, CancellationToken cancellationToken = default)
    {
        ByteReader readSource = (o, d) => ReadPadded(source, o, d);
        ByteReader readTarget = (o, d) => ReadPadded(target, o, d);
        if (!ReferenceEquals(source.Storage, target.Storage) || source.Transform is not null || target.Transform is not null)
        {
            return Positional(readSource, source.Length, readTarget, target.Length, cancellationToken);
        }

        // 変換前のピースを (種類, 外部参照の番号, 位置) の順に並べ、変換後のピースが指すデータが変換前のどこにあるかを引く。
        var index = new List<(PieceKind Kind, ulong Seed, long Store, long Length, long Doc)>();
        foreach ((long doc, Piece p) in source.Tree.EnumerateAll())
        {
            if (p.Kind is PieceKind.Original or PieceKind.Added or PieceKind.External)
            {
                index.Add((p.Kind, p.Kind == PieceKind.External ? p.Seed : 0, p.Offset, p.Length, doc));
            }
        }

        index.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Seed != b.Seed ? a.Seed.CompareTo(b.Seed) : a.Store.CompareTo(b.Store));
        var segments = new List<PatchSegment>();
        void Add(bool fromSource, long src, long dst, long len)
        {
            if (segments.Count > 0 && segments[^1] is { } last && last.FromSource == fromSource && last.TargetOffset + last.Length == dst
                && (!fromSource || last.SourceOffset + last.Length == src))
            {
                segments[^1] = last with { Length = last.Length + len };
            }
            else
            {
                segments.Add(new PatchSegment(fromSource, fromSource ? src : 0, dst, len));
            }
        }

        int count = 0;
        foreach ((long doc, Piece p) in target.Tree.EnumerateAll())
        {
            if ((++count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (p.Kind is not (PieceKind.Original or PieceKind.Added or PieceKind.External))
            {
                Add(false, 0, doc, p.Length);
                continue;
            }

            ulong seed = p.Kind == PieceKind.External ? p.Seed : 0;
            long store = p.Offset, end = p.Offset + p.Length, at = doc;
            while (store < end)
            {
                int i = Find(index, p.Kind, seed, store);
                if (i < 0)
                {
                    // この位置を含むピースが変換前にない: 次に変換前にある位置まで (なければ終わりまで) をデータにする。
                    int next = ~i;
                    long until = next < index.Count && index[next].Kind == p.Kind && index[next].Seed == seed ? Math.Min(end, index[next].Store) : end;
                    Add(false, 0, at, until - store);
                    at += until - store;
                    store = until;
                    continue;
                }

                var e = index[i];
                long take = Math.Min(end, e.Store + e.Length) - store;
                Add(true, e.Doc + (store - e.Store), at, take);
                at += take;
                store += take;
            }
        }

        return new PatchScript(source.Length, target.Length, segments, readSource, readTarget);
    }

    /// <summary>
    /// <paramref name="store"/> を含む項目の番号。なければ、その位置より後ろの最初の項目の番号のビット反転 (負の値)。
    /// </summary>
    private static int Find(List<(PieceKind Kind, ulong Seed, long Store, long Length, long Doc)> index, PieceKind kind, ulong seed, long store)
    {
        int lo = 0, hi = index.Count - 1, after = index.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var e = index[mid];
            int c = e.Kind != kind ? e.Kind.CompareTo(kind) : e.Seed != seed ? e.Seed.CompareTo(seed) : 0;
            if (c == 0)
            {
                if (store < e.Store)
                {
                    c = 1;
                }
                else if (store >= e.Store + e.Length)
                {
                    c = -1;
                }
                else
                {
                    return mid;
                }
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                after = mid;
                hi = mid - 1;
            }
        }

        return ~after;
    }

    /// <summary>同じ位置のバイトを比べた差分 (元データが違う 2 つのデータ)。</summary>
    public static PatchScript Positional(ByteReader readSource, long sourceLength, ByteReader readTarget, long targetLength,
        CancellationToken cancellationToken = default)
    {
        List<(long Offset, long Length)> changes = IpsPatch.ChangedRanges(readSource, sourceLength, readTarget, targetLength, cancellationToken);
        var segments = new List<PatchSegment>();
        long pos = 0;
        foreach ((long o, long l) in changes)
        {
            if (o > pos)
            {
                segments.Add(new PatchSegment(true, pos, pos, o - pos));
            }

            segments.Add(new PatchSegment(false, 0, o, l));
            pos = o + l;
        }

        if (pos < targetLength)
        {
            // 変換後の残り: 変換前にある部分は同じ値 (差分がない)、それより後ろはない (短くなった)。
            long common = Math.Min(targetLength, sourceLength) - pos;
            if (common > 0)
            {
                segments.Add(new PatchSegment(true, pos, pos, common));
                pos += common;
            }

            if (pos < targetLength)
            {
                segments.Add(new PatchSegment(false, 0, pos, targetLength - pos));
            }
        }

        return new PatchScript(sourceLength, targetLength, segments, readSource, readTarget);
    }

    internal static void ReadPadded(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        destination.Clear();
        if (offset < snapshot.Length)
        {
            ReadResult r = snapshot.Read(offset, destination[..(int)Math.Min(destination.Length, snapshot.Length - offset)]);
            if (r.Unreadable is { Count: > 0 } bad)
            {
                throw new DataReadException(bad[0].Offset);
            }
        }
    }
}
