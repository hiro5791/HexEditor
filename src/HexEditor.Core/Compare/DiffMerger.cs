using HexEditor.Core.Engine;

namespace HexEditor.Core.Compare;

/// <summary>マージの向き (ANA-07)。</summary>
public enum MergeDirection
{
    /// <summary>左の内容で右を置き換える (「右へコピー」)。</summary>
    ToRight,

    /// <summary>右の内容で左を置き換える (「左へコピー」)。</summary>
    ToLeft,
}

/// <summary>マージの結果。<see cref="Skipped"/> は長さを変えられない書き込み先のため飛ばした差分の数 (仕様 3)。</summary>
public sealed record MergeOutcome(int Copied, int Skipped);

/// <summary>
/// 差分のマージ (ANA-07)。差分の内容を一方からもう一方へコピーする。1 回の操作を書き込み先の Undo 1 回分にまとめ (仕様 4)、
/// コピーした差分は一覧から外して後ろの差分の位置をずらす (仕様 5。再比較はしない)。ドキュメントの編集なので UI スレッドで呼ぶ。
/// 書き込み先は自動で保存しない (仕様 7)。
/// </summary>
public static class DiffMerger
{
    /// <summary>これを超える件数のコピーは長時間処理として扱う (「巨大ファイル」)。</summary>
    public const int LongRunningThreshold = 10_000;

    public const string CopyRightDescription = "差分を右へコピー";
    public const string CopyLeftDescription = "差分を左へコピー";

    /// <summary>これより短い差分は、バイト列を読んでそのまま書く (長い差分は範囲の参照として挿入し、データをコピーしない)。</summary>
    private const int DirectCopyLimit = 1 << 20;

    /// <summary>何件ごとに <c>pause</c> を呼ぶか (UI を止めないため)。</summary>
    private const int Batch = 1000;

    /// <summary>
    /// 指定した差分 (一覧の番号) をコピーする。キャンセルした場合は、それまでの変更をすべて取り消してから
    /// <see cref="OperationCanceledException"/> を投げる (中途半端な状態を残さない)。
    /// </summary>
    /// <param name="left">比較の左側のドキュメント (比較の左側がドキュメントでなければ null。書き込み先にはできない)。</param>
    /// <param name="pause">一定の件数ごとに呼ぶ (画面の更新とキャンセルの受け付けのため)。</param>
    /// <exception cref="DocumentReadOnlyException">書き込み先が読み取り専用 (仕様 6)。</exception>
    public static async Task<MergeOutcome> CopyAsync(CompareResult result, Document? left, Document? right, IReadOnlyCollection<long> indices,
        MergeDirection direction, CancellationToken cancellationToken = default, Func<Task>? pause = null)
    {
        bool toRight = direction == MergeDirection.ToRight;
        Document target = (toRight ? right : left) ?? throw new InvalidOperationException("書き込み先がドキュメントではありません。");
        if (target.IsReadOnly)
        {
            throw new DocumentReadOnlyException();
        }

        CompareRange source = toRight ? result.Left : result.Right;
        Document? sourceDocument = toRight ? left : right;
        DocumentSnapshot? sourceSnapshot = CompareData.SnapshotOf(source.Data);
        long[] order = [.. indices.Distinct().OrderByDescending(i => i)];
        var copied = new List<(long Index, DiffRange Diff, long Delta)>();
        int skipped = 0;
        bool applied = false;
        try
        {
            using (target.BeginGroup(toRight ? CopyRightDescription : CopyLeftDescription))
            {
                int done = 0;
                foreach (long index in order)
                {
                    if (++done % Batch == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pause is not null)
                        {
                            await pause();
                        }
                    }

                    DiffRange d = result.Diffs[index];
                    if (d.Kind == DiffKind.Unreadable)
                    {
                        continue;
                    }

                    long targetOffset = d.Start(toRight);
                    long targetLength = d.Length(toRight);
                    long sourceOffset = d.Start(!toRight);
                    long sourceLength = d.Length(!toRight);
                    if (sourceLength != targetLength && !target.CanResize)
                    {
                        skipped++;
                        continue;
                    }

                    Replace(target, targetOffset, targetLength, source, sourceSnapshot, sourceDocument, sourceOffset, sourceLength);
                    applied = true;
                    copied.Add((index, d, sourceLength - targetLength));
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            if (applied)
            {
                target.Undo();
            }

            throw;
        }

        // コピーした差分を一覧から外す (番号の大きい方から。後ろの差分の位置は書き込み先の長さの変化だけずらす)。
        foreach ((long index, DiffRange d, long delta) in copied)
        {
            result.Diffs.RemoveAt(index, toRight ? 0 : delta, toRight ? delta : 0);
            result.Forget(d);
        }

        return new MergeOutcome(copied.Count, skipped);
    }

    /// <summary>すべての差分をコピーする (「すべての差分を右へコピー」)。</summary>
    public static Task<MergeOutcome> CopyAllAsync(CompareResult result, Document? left, Document? right, MergeDirection direction,
        CancellationToken cancellationToken = default, Func<Task>? pause = null)
    {
        long count = result.Diffs.Count;
        var indices = new LongRange(count);
        return CopyAsync(result, left, right, indices, direction, cancellationToken, pause);
    }

    private static void Replace(Document target, long targetOffset, long targetLength, CompareRange source, DocumentSnapshot? sourceSnapshot,
        Document? sourceDocument, long sourceOffset, long sourceLength)
    {
        if (sourceLength <= DirectCopyLimit || (sourceSnapshot is null && sourceDocument is null))
        {
            byte[] data = new byte[sourceLength];
            source.Data.Read(sourceOffset, data);
            if (sourceLength == targetLength)
            {
                target.Overwrite(targetOffset, data);
                return;
            }

            if (targetLength > 0)
            {
                target.Delete(targetOffset, targetLength);
            }

            target.Insert(targetOffset, data);
            return;
        }

        DocumentSnapshot snapshot = sourceSnapshot ?? sourceDocument!.Current;
        if (sourceLength == targetLength)
        {
            target.OverwriteFrom(targetOffset, snapshot, sourceOffset, sourceLength);
            return;
        }

        if (targetLength > 0)
        {
            target.Delete(targetOffset, targetLength);
        }

        target.InsertFrom(targetOffset, snapshot, sourceOffset, sourceLength);
    }

    /// <summary>0 から <c>count</c> − 1 までの番号 (すべての差分。件数分の配列を作らない)。</summary>
    private sealed class LongRange(long count) : IReadOnlyCollection<long>
    {
        public int Count => (int)Math.Min(int.MaxValue, count);

        public IEnumerator<long> GetEnumerator()
        {
            for (long i = 0; i < count; i++)
            {
                yield return i;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
