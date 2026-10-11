using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Saving;

/// <summary>
/// ずらしながらのその場保存の計画 (ENG-24 の仕様 1・2・7)。確認ダイアログに「書き換える量」「一時的に必要な容量」と、保存前の Undo 履歴を
/// 破棄するかを示す。
/// </summary>
public sealed record ShiftPlan
{
    /// <summary>ファイルに書き換える量 (バイト)。</summary>
    public required long WriteBytes { get; init; }

    /// <summary>循環する依存関係 (入れ替えなど) のために退避ファイルにコピーする量。</summary>
    public required long SpillBytes { get; init; }

    /// <summary>保存前の Undo 履歴のために追加バッファへ退避する量 (<see cref="DiscardHistory"/> なら 0)。</summary>
    public required long BackupBytes { get; init; }

    /// <summary>一時的に必要な容量 (退避ファイル + Undo 用の退避)。</summary>
    public long TemporaryBytes => SpillBytes + BackupBytes;

    /// <summary>空き容量が足りないため、保存前の Undo 履歴を破棄する (仕様 7)。</summary>
    public required bool DiscardHistory { get; init; }

    public required long OriginalLength { get; init; }

    public required long FinalLength { get; init; }

    /// <summary>書く順序に並べた書き込み (内部)。</summary>
    internal IReadOnlyList<ShiftWrite> Writes { get; init; } = [];
}

/// <summary>書き込み 1 つ: ドキュメントの [Destination, + Length) に、元データの [Source, + Length) (移動) か、ピースの内容 (生成) を書く。</summary>
internal sealed record ShiftWrite(long Destination, long Length, long Source, bool IsMove)
{
    /// <summary>循環を切るために、書き始める前に退避ファイルへコピーする。</summary>
    public bool Spill { get; set; }

    /// <summary>退避ファイルの中の位置。</summary>
    public long SpillPosition { get; set; }
}

/// <summary>ずらしながらのその場保存の結果。<see cref="Document.CompleteShiftSave"/> に渡す。</summary>
public sealed record ShiftSaveResult(FileByteSource Source, IReadOnlyList<SavedRange> Backups, long OriginalLength, bool DiscardedHistory);

/// <summary>書き込みの途中で失敗した (ENG-24 の「エラー」。ファイルが壊れている可能性がある)。</summary>
public sealed class ShiftSaveFailedException(Exception inner, ShiftSaveResult? recovered)
    : IOException($"その場保存の途中でエラーが発生しました。ファイルが壊れている可能性があります。理由: {inner.Message}", inner)
{
    /// <summary>保存前の内容を読むための退避 (Undo 用の退避があった場合)。ドキュメントの内容を保つために使う。</summary>
    public ShiftSaveResult? Recovered { get; } = recovered;
}

/// <summary>前回の起動で中断したずらしながらのその場保存 (ENG-24 の仕様 6)。</summary>
public sealed record ShiftInterruption(string MarkerPath, string TargetPath, DateTime StartedUtc);

/// <summary>
/// ずらしながらのその場保存 (ENG-24): 長さが変わる保存を、一時ファイルを作らず元のファイルの中でデータをずらしながら行う。まだ必要な元データを
/// 上書きしないよう書く順序を決め (後ろにずらす範囲は末尾側から、前にずらす範囲は先頭側から)、循環する依存関係は退避ファイルにコピーして切る。
/// 書き始めたらキャンセルできない。途中で落ちるとファイルが壊れるため、実行中は中断マーカーを置く。
/// </summary>
public static class ShiftSaver
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>テスト用: 書き込んだ量 (バイト) ごとに呼ぶ (強制終了の再現。テスト方針 7.2)。</summary>
    internal static Action<long>? AfterBytesWritten { get; set; }

    /// <summary>その場でずらしながら書けるか (元データが書き込めるファイルで、保存先がそのファイル)。</summary>
    public static bool CanShift(DocumentSnapshot snapshot, string targetPath) =>
        snapshot.Storage.Source is FileByteSource { IsRange: false } file
        && file.Capabilities.HasFlag(SourceCapabilities.CanWrite)
        && string.Equals(Path.GetFullPath(targetPath), file.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 書き込み計画を作る (仕様 1・2・7)。<paramref name="availableTemporary"/> は退避の置き場所の空き容量 (不明なら null)。Undo 用の退避が
    /// 空き容量に収まらなければ、保存前の Undo 履歴を破棄する計画にする。
    /// </summary>
    public static ShiftPlan Plan(DocumentSnapshot snapshot, long? availableTemporary = null)
    {
        var file = (FileByteSource)snapshot.Storage.Source;
        long originalLength = file.Length;
        var writes = new List<ShiftWrite>();
        foreach ((long docOffset, Piece piece) in snapshot.Tree.EnumerateAll())
        {
            if (piece.Kind == PieceKind.Original)
            {
                if (piece.Offset != docOffset)
                {
                    writes.Add(new ShiftWrite(docOffset, piece.Length, piece.Offset, IsMove: true));
                }
            }
            else
            {
                // 他のドキュメント・保存前の版の範囲の参照は、書き換えの途中で同じファイルを読む可能性があるため、先に退避ファイルへ写す。
                bool reference = piece.Kind == PieceKind.External && snapshot.ExternalSource(piece.ExternalIndex) is SnapshotRange;
                writes.Add(new ShiftWrite(docOffset, piece.Length, -1, IsMove: false) { Spill = reference });
            }
        }

        IReadOnlyList<ShiftWrite> ordered = Order(writes);
        long spill = ordered.Where(w => w.Spill).Sum(w => w.Length);
        long write = writes.Sum(w => w.Length);

        // 保存前の版が読む元データ: 書き換える範囲と、短くなる場合に切り捨てる末尾。
        long backup = Preserved(writes, originalLength, snapshot.Length).Sum(r => r.Length);
        bool discard = availableTemporary is long free && free < spill + backup + DocumentSaver.FreeSpaceMargin;
        return new ShiftPlan
        {
            WriteBytes = write,
            SpillBytes = spill,
            BackupBytes = discard ? 0 : backup,
            DiscardHistory = discard,
            OriginalLength = originalLength,
            FinalLength = snapshot.Length,
            Writes = ordered,
        };
    }

    /// <summary>保存前の内容を保つために退避するファイルの範囲 (重なりをまとめ、オフセットの昇順)。</summary>
    private static List<(long Offset, long Length)> Preserved(IEnumerable<ShiftWrite> writes, long originalLength, long finalLength)
    {
        var ranges = new List<(long Offset, long Length)>();
        foreach (ShiftWrite w in writes)
        {
            long end = Math.Min(w.Destination + w.Length, originalLength);
            if (end > w.Destination)
            {
                ranges.Add((w.Destination, end - w.Destination));
            }
        }

        if (finalLength < originalLength)
        {
            ranges.Add((finalLength, originalLength - finalLength));
        }

        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var merged = new List<(long Offset, long Length)>();
        foreach ((long o, long l) in ranges)
        {
            if (merged.Count > 0 && o <= merged[^1].Offset + merged[^1].Length)
            {
                (long mo, long ml) = merged[^1];
                merged[^1] = (mo, Math.Max(mo + ml, o + l) - mo);
            }
            else
            {
                merged.Add((o, l));
            }
        }

        return merged;
    }

    /// <summary>
    /// 書く順序を決める (仕様 1)。書き込み B が、移動 A の読む元データを上書きするなら A を先にする (A → B)。順序が決まらない (循環する) 場合は、
    /// 残りのうち最も短い移動を退避ファイルにコピーして依存を切る。
    /// </summary>
    private static List<ShiftWrite> Order(List<ShiftWrite> writes)
    {
        int n = writes.Count;
        var successors = new List<int>[n];
        int[] incoming = new int[n];
        for (int i = 0; i < n; i++)
        {
            successors[i] = [];
        }

        // 移動を元データの開始位置の順に並べ、書き込み先の区間と重なる移動を探す (終わりの最大値で打ち切る)。
        int[] moves = [.. Enumerable.Range(0, n).Where(i => writes[i].IsMove).OrderBy(i => writes[i].Source)];
        long[] maxEnd = new long[moves.Length];
        for (int k = 0; k < moves.Length; k++)
        {
            long end = writes[moves[k]].Source + writes[moves[k]].Length;
            maxEnd[k] = k == 0 ? end : Math.Max(maxEnd[k - 1], end);
        }

        for (int b = 0; b < n; b++)
        {
            long dst = writes[b].Destination;
            long dstEnd = dst + writes[b].Length;
            int hi = UpperBound(moves, writes, dstEnd); // Source < dstEnd の移動の数
            for (int k = hi - 1; k >= 0 && maxEnd[k] > dst; k--)
            {
                int a = moves[k];
                ShiftWrite move = writes[a];
                if (a != b && move.Source + move.Length > dst)
                {
                    successors[a].Add(b);
                    incoming[b]++;
                }
            }
        }

        var order = new List<ShiftWrite>(n);
        var ready = new Queue<int>();
        bool[] done = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (incoming[i] == 0)
            {
                ready.Enqueue(i);
            }
        }

        int remaining = n;
        while (remaining > 0)
        {
            if (ready.Count == 0)
            {
                // 循環: 依存を持つ残りの移動のうち最も短いものを退避して、その移動の後続への依存を外す。
                int pick = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!done[i] && writes[i].IsMove && !writes[i].Spill && successors[i].Count > 0
                        && (pick < 0 || writes[i].Length < writes[pick].Length))
                    {
                        pick = i;
                    }
                }

                writes[pick].Spill = true;
                foreach (int s in successors[pick])
                {
                    if (--incoming[s] == 0 && !done[s])
                    {
                        ready.Enqueue(s);
                    }
                }

                successors[pick].Clear();
                if (incoming[pick] == 0 && !done[pick])
                {
                    ready.Enqueue(pick);
                }

                continue;
            }

            int next = ready.Dequeue();
            if (done[next])
            {
                continue;
            }

            done[next] = true;
            remaining--;
            order.Add(writes[next]);
            foreach (int s in successors[next])
            {
                if (--incoming[s] == 0 && !done[s])
                {
                    ready.Enqueue(s);
                }
            }
        }

        return order;
    }

    private static int UpperBound(int[] moves, List<ShiftWrite> writes, long value)
    {
        int lo = 0, hi = moves.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (writes[moves[mid]].Source < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ---- 実行 ----

    /// <summary>
    /// 計画どおりに書き換える (バックグラウンドで呼ぶ)。退避 (Undo 用・循環を切るため) を終えるまではキャンセルでき、書き込みを始める直前に
    /// <paramref name="beforeWriting"/> を呼ぶ (キャンセルできなくする)。成功したら呼び出し側は UI スレッドで <see cref="Document.CompleteShiftSave"/>
    /// を呼ぶ。
    /// </summary>
    /// <param name="markerDirectory">中断マーカーの置き場所 (復旧用フォルダ。仕様 6)。</param>
    /// <param name="spillDirectory">循環を切るための退避ファイルの置き場所 (追加バッファの一時ファイルと同じ場所)。</param>
    public static ShiftSaveResult Save(DocumentSnapshot snapshot, ShiftPlan plan, string markerDirectory, string spillDirectory,
        LongRunningOperation? operation = null, Action? beforeWriting = null)
    {
        var file = (FileByteSource)snapshot.Storage.Source;
        AddBuffer addBuffer = snapshot.Storage.AddBuffer;
        operation?.SetTotal(plan.BackupBytes + plan.SpillBytes + plan.WriteBytes);
        byte[] buffer = new byte[BufferSize];
        long progress = 0;

        // 1. 保存前の Undo 履歴が参照する元データを追加バッファへ退避する (仕様 7)。
        var backups = new List<SavedRange>();
        if (!plan.DiscardHistory)
        {
            foreach ((long offset, long length) in Preserved(plan.Writes, plan.OriginalLength, plan.FinalLength))
            {
                long first = -1;
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    ReadOriginal(file, offset + pos, buffer.AsSpan(0, n));
                    long at = addBuffer.Append(buffer.AsSpan(0, n));
                    first = first < 0 ? at : first;
                    progress += n;
                    operation?.Report(progress);
                }

                backups.Add(new SavedRange(offset, length, first));
            }
        }

        // 2. 循環を切る移動の元データを退避ファイルにコピーする。
        using SafeFileHandle? spill = plan.SpillBytes > 0 ? OpenSpill(spillDirectory) : null;
        long spillAt = 0;
        foreach (ShiftWrite w in plan.Writes.Where(w => w.Spill))
        {
            w.SpillPosition = spillAt;
            for (long pos = 0; pos < w.Length; pos += buffer.Length)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, w.Length - pos);
                if (w.IsMove)
                {
                    ReadOriginal(file, w.Source + pos, buffer.AsSpan(0, n));
                }
                else if (snapshot.Read(w.Destination + pos, buffer.AsSpan(0, n)) is { IsComplete: false } bad)
                {
                    throw new UnreadableDataException(bad.Unreadable);
                }

                RandomAccess.Write(spill!, buffer.AsSpan(0, n), spillAt + pos);
                progress += n;
                operation?.Report(progress);
            }

            spillAt += w.Length;
        }

        operation?.CancellationToken.ThrowIfCancellationRequested();

        // 3. 中断マーカーを置き、書き込みを始める。ここからはキャンセルしない (仕様 5)。
        string marker = WriteMarker(markerDirectory, file.Path);
        beforeWriting?.Invoke();
        snapshot.Storage.Owner.SuspendLock();
        var recovered = new ShiftSaveResult(file, backups, plan.OriginalLength, plan.DiscardHistory);
        try
        {
            using SafeFileHandle handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

            // 長くなる場合は先にファイルを伸ばす (仕様 4)。
            if (plan.FinalLength > plan.OriginalLength)
            {
                RandomAccess.SetLength(handle, plan.FinalLength);
            }

            long written = 0;
            foreach (ShiftWrite w in plan.Writes)
            {
                if (w.Spill)
                {
                    for (long pos = 0; pos < w.Length; pos += buffer.Length)
                    {
                        int n = (int)Math.Min(buffer.Length, w.Length - pos);
                        Read(spill!, w.SpillPosition + pos, buffer.AsSpan(0, n));
                        RandomAccess.Write(handle, buffer.AsSpan(0, n), w.Destination + pos);
                        Written(n);
                    }

                    continue;
                }

                if (!w.IsMove)
                {
                    for (long pos = 0; pos < w.Length; pos += buffer.Length)
                    {
                        int n = (int)Math.Min(buffer.Length, w.Length - pos);
                        ReadResult read = snapshot.Read(w.Destination + pos, buffer.AsSpan(0, n));
                        if (!read.IsComplete)
                        {
                            throw new UnreadableDataException(read.Unreadable);
                        }

                        RandomAccess.Write(handle, buffer.AsSpan(0, n), w.Destination + pos);
                        Written(n);
                    }

                    continue;
                }

                // 自分自身と重なる移動: 後ろへずらすときは末尾側から、前へずらすときは先頭側から写す。
                bool backward = w.Destination > w.Source;
                for (long done = 0; done < w.Length;)
                {
                    int n = (int)Math.Min(buffer.Length, w.Length - done);
                    long pos = backward ? w.Length - done - n : done;
                    Read(handle, w.Source + pos, buffer.AsSpan(0, n));
                    RandomAccess.Write(handle, buffer.AsSpan(0, n), w.Destination + pos);
                    done += n;
                    Written(n);
                }
            }

            // 短くなる場合は最後に切り詰める。
            if (plan.FinalLength < plan.OriginalLength)
            {
                RandomAccess.SetLength(handle, plan.FinalLength);
            }

            RandomAccess.FlushToDisk(handle);

            void Written(int n)
            {
                written += n;
                progress += n;
                operation?.Report(progress);
                AfterBytesWritten?.Invoke(written);
            }
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            // マーカーは残す (ファイルが壊れている可能性がある)。保存前の内容は退避から読める。
            throw new ShiftSaveFailedException(ex, plan.DiscardHistory ? null : recovered);
        }

        File.Delete(marker);
        FileByteSource saved = FileByteSource.Open(file.Path);
        return new ShiftSaveResult(saved, backups, plan.OriginalLength, plan.DiscardHistory);
    }

    /// <summary>元のファイルの今の内容を読む (ブロックキャッシュを通さない)。</summary>
    private static void ReadOriginal(FileByteSource file, long offset, Span<byte> destination)
    {
        ReadResult r = file.Read(offset, destination);
        if (!r.IsComplete || r.BytesReturned != destination.Length)
        {
            throw new UnreadableDataException(r.Unreadable);
        }
    }

    private static void Read(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[done..], offset + done);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            done += n;
        }
    }

    private static SafeFileHandle OpenSpill(string directory)
    {
        Directory.CreateDirectory(directory);
        return File.OpenHandle(Path.Combine(directory, $"shift-{Guid.NewGuid():N}.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            FileOptions.DeleteOnClose);
    }

    // ---- 中断マーカー (仕様 6) ----

    private sealed record MarkerContent(string Path, DateTime StartedUtc);

    private static string WriteMarker(string directory, string target)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"shift-{Guid.NewGuid():N}.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new MarkerContent(target, DateTime.UtcNow));
        }

        return path;
    }

    /// <summary>復旧用フォルダに残った中断マーカー (起動時に「前回、data.bin のその場保存が中断されました」と示す)。</summary>
    public static IReadOnlyList<ShiftInterruption> FindInterrupted(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var list = new List<ShiftInterruption>();
        foreach (string path in Directory.GetFiles(directory, "shift-*.json"))
        {
            try
            {
                MarkerContent? content = JsonSerializer.Deserialize<MarkerContent>(File.ReadAllText(path));
                if (content is not null)
                {
                    list.Add(new ShiftInterruption(path, content.Path, content.StartedUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    /// <summary>警告を見た: マーカーを消す (自動の修復はしない)。</summary>
    public static void Dismiss(ShiftInterruption interruption)
    {
        try
        {
            File.Delete(interruption.MarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// ずらしながらのその場保存の前の内容を読むためのデータソース (ENG-24 の仕様 7): 書き換えた範囲と切り捨てた末尾は追加バッファに退避した旧内容、
/// それ以外は (位置の変わっていない) 今のファイル。
/// </summary>
internal sealed class ShiftOverlaySource(IByteSource current, AddBuffer addBuffer, IReadOnlyList<SavedRange> ranges, long length)
    : ByteSourceBase, IRebasableOverlay
{
    /// <summary>位置の変わっていない部分を読むデータソース (後の保存で、その保存の前の内容に付け替える)。</summary>
    public IByteSource Inner { get; set; } = current;

    public override string DisplayName => Inner.DisplayName;

    public override string Identity => Inner.Identity + "#before-shift";

    public override long Length => length;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        Span<byte> target = buffer[..count];
        long end = offset + count;
        long at = offset;
        foreach (SavedRange range in ranges)
        {
            long from = Math.Max(range.Offset, offset);
            long to = Math.Min(range.Offset + range.Length, end);
            if (from >= to)
            {
                continue;
            }

            if (from > at)
            {
                Inner.Read(at, target.Slice((int)(at - offset), (int)(from - at)));
            }

            addBuffer.Read(range.OldContentInAddBuffer + (from - range.Offset), target.Slice((int)(from - offset), (int)(to - from)));
            at = to;
        }

        if (at < end)
        {
            Inner.Read(at, target.Slice((int)(at - offset), (int)(end - at)));
        }

        return new ReadResult(count);
    }

    protected override void Dispose(bool disposing)
    {
    }
}
