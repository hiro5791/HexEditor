using System.Buffers.Binary;
using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Saving;

/// <summary>
/// 書き込む旧内容がジャーナルの上限を超える (ENG-23 の仕様 3)。UI は確認ダイアログで「安全な保存を使う」「保護なしで書き込む」
/// 「キャンセル」を選ばせる (<see cref="SavePlanner"/> の <see cref="SaveIssue.JournalTooLarge"/>)。
/// </summary>
public sealed class JournalLimitException(long required, long limit)
    : IOException($"その場保存のジャーナルが上限を超えます。必要: {required:N0} バイト、上限: {limit:N0} バイト")
{
    public long Required { get; } = required;

    public long Limit { get; } = limit;
}

/// <summary>その場保存の書き込みに失敗したため、ジャーナルから保存前の状態に戻した (ENG-23 の「エラー」)。</summary>
public sealed class InPlaceSaveRolledBackException(Exception inner)
    : IOException($"保存に失敗したため、保存前の状態に戻しました。理由: {inner.Message}", inner);

/// <summary>
/// その場保存の書き込みに失敗し、保存前の状態にも戻せなかった (ENG-23 の「エラー」)。ジャーナルがあれば残してあり、次回起動時に
/// 復旧できる。<see cref="JournalPath"/> が null なのは保護なしで書き込んだ場合。
/// </summary>
public sealed class InPlaceSavePartiallyWrittenException(string? journalPath, Exception inner)
    : IOException($"ファイルの一部だけが書き換わっています。{(journalPath is null ? string.Empty : "次回起動時に復旧できます。")}理由: {inner.Message}", inner)
{
    public string? JournalPath { get; } = journalPath;
}

/// <summary>
/// ジャーナルの対象の識別情報 (パス・ファイル ID・長さ) が今のファイルと一致しない (ENG-23 の仕様 4)。保存の中断の後にファイルが
/// 変更されたため、書き戻さない。ジャーナルは残す。
/// </summary>
public sealed class JournalMismatchException(string path)
    : IOException($"{path} は保存の中断の後に変更されているため、保存前の状態に戻せません。");

/// <summary>テスト用: 強制終了の再現 (テスト方針 7.2)。その場保存はこの例外を後始末せずにそのまま投げる。</summary>
internal sealed class SimulatedCrashException() : Exception("simulated crash");

/// <summary>その場保存で書き込んだ範囲と、その旧内容を退避した追加バッファの位置。</summary>
public sealed record InPlaceSaveResult(FileByteSource Source, IReadOnlyList<SavedRange> Ranges);

public readonly record struct SavedRange(long Offset, long Length, long OldContentInAddBuffer);

/// <summary>ジャーナルの対象の識別情報 (ENG-23 の仕様 2)。</summary>
public sealed record JournalInfo(string Path, long Length, DateTime LastWriteUtc, string FileId);

/// <summary>
/// その場保存 (ENG-23): 長さが変わらず、元データのピースがすべて元の位置を指している場合に、変更した範囲だけを書き込む。
/// 書き込む前に旧内容をジャーナルに書いてディスクに反映し、途中で止まっても次の起動時に元へ戻せるようにする (00 の 11.3)。
/// </summary>
public static class InPlaceSaver
{
    /// <summary>ジャーナルの上限の既定値 (ENG-23 の仕様 3)。</summary>
    public const long DefaultJournalLimit = 1024L * 1024 * 1024;

    /// <summary>近い変更範囲をまとめて 1 回で書く間隔 (ENG-23 の仕様 5)。</summary>
    public const long MergeGap = 64 * 1024;

    private static readonly byte[] Magic = "HEXJRNL2"u8.ToArray();
    private static readonly byte[] OldMagic = "HEXJRNL1"u8.ToArray();

    /// <summary>テスト用: ジャーナルを書いた後、ファイルに書き込む前に呼ぶ (強制終了の再現。テスト方針 7.2)。</summary>
    internal static Action? AfterJournalWritten { get; set; }

    /// <summary>テスト用: 手順 4 で範囲 (0 から数える) を書いた直後に呼ぶ。</summary>
    internal static Action<int>? AfterRangeWritten { get; set; }

    /// <summary>その場保存できるか (ENG-23 の仕様 1)。</summary>
    public static bool CanSaveInPlace(DocumentSnapshot snapshot, string targetPath)
    {
        if (snapshot.Storage.Source is not FileByteSource file
            || !file.Capabilities.HasFlag(SourceCapabilities.CanWrite)
            || !string.Equals(Path.GetFullPath(targetPath), file.Path, StringComparison.OrdinalIgnoreCase)
            || snapshot.Length != file.Length)
        {
            return false;
        }

        foreach ((long docOffset, Piece piece) in snapshot.Tree.EnumerateAll())
        {
            if (piece.Kind == PieceKind.Original && piece.Offset != docOffset)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>その場保存で書く量 (= ジャーナルに書く旧内容の量)。近い範囲をまとめた後の合計。</summary>
    public static long JournalSize(DocumentSnapshot snapshot) => MergeRanges(snapshot.EnumerateModifiedRanges()).Sum(r => r.Length);

    /// <summary>
    /// 変更した範囲を書き込む。旧内容は追加バッファにも退避して返す (保存前の Undo 履歴のため。ENG-05 の仕様 5)。
    /// 成功したら呼び出し側は UI スレッドで <see cref="Document.CompleteInPlaceSave"/> を呼ぶ。
    /// </summary>
    /// <param name="documentId">ジャーナルの名前 (<c>journal-&lt;ドキュメント ID&gt;.bin</c>) に使う。</param>
    /// <param name="protect">偽なら、ジャーナルを書かずに書き込む (「保護なしで書き込む」。ENG-23 の仕様 3)。</param>
    /// <exception cref="JournalLimitException">保護ありで、旧内容が <paramref name="journalLimit"/> を超える。何も書いていない。</exception>
    /// <exception cref="InsufficientSpaceException">ジャーナルの置き場所の空き容量が足りない。何も書いていない。</exception>
    /// <exception cref="InPlaceSaveRolledBackException">書き込みに失敗し、保存前の状態に戻した。</exception>
    /// <exception cref="InPlaceSavePartiallyWrittenException">書き込みに失敗し、戻せなかった。</exception>
    public static InPlaceSaveResult Save(DocumentSnapshot snapshot, string journalDirectory, long journalLimit = DefaultJournalLimit,
        LongRunningOperation? operation = null, Guid? documentId = null, bool protect = true, IVolumeInfoProvider? volumes = null)
    {
        var file = (FileByteSource)snapshot.Storage.Source;
        List<(long Offset, long Length)> ranges = MergeRanges(snapshot.EnumerateModifiedRanges());
        long total = ranges.Sum(r => r.Length);
        if (protect && total > journalLimit)
        {
            throw new JournalLimitException(total, journalLimit);
        }

        operation?.SetTotal(total * 2);
        string? journal = null;
        if (protect)
        {
            Directory.CreateDirectory(journalDirectory);
            DocumentSaver.CheckFreeSpace(journalDirectory, total, volumes);
            journal = JournalPath(journalDirectory, documentId ?? Guid.NewGuid());
        }

        // 1. 旧内容をジャーナルと追加バッファに書く。失敗・キャンセルしたらジャーナルを消す (ファイルにはまだ書いていない)。
        var saved = new List<SavedRange>(ranges.Count);
        byte[] buffer = new byte[4 * 1024 * 1024];
        byte[] record = new byte[16];
        long done = 0;
        FileStream? stream = null;
        try
        {
            if (journal is not null)
            {
                stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough);
                WriteHeader(stream, file);
            }

            foreach ((long offset, long length) in ranges)
            {
                // ジャーナルにはファイル上の位置を書く (範囲を指定して開いた場合は開始位置を足す。ENG-13 の仕様 3)。
                BinaryPrimitives.WriteInt64LittleEndian(record, file.RangeStart + offset);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), length);
                stream?.Write(record);
                long firstAt = -1;
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    ReadResult read = file.Read(offset + pos, buffer.AsSpan(0, n));
                    if (!read.IsComplete)
                    {
                        throw new UnreadableDataException(read.Unreadable);
                    }

                    stream?.Write(buffer, 0, n);
                    long at = snapshot.Storage.AddBuffer.Append(buffer.AsSpan(0, n));
                    firstAt = firstAt < 0 ? at : firstAt;
                    done += n;
                    operation?.Report(done);
                }

                saved.Add(new SavedRange(offset, length, firstAt));
            }

            if (stream is not null)
            {
                // 終わりの印 (オフセット −1)。印のないジャーナルは書きかけで、ファイルはまだ変わっていない。
                BinaryPrimitives.WriteInt64LittleEndian(record, -1);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), 0);
                stream.Write(record);
                stream.Flush(flushToDisk: true);
                stream.Dispose();
                stream = null;
            }
        }
        catch
        {
            stream?.Dispose();
            if (journal is not null)
            {
                TryDelete(journal);
            }

            throw;
        }

        // 自分の書き込み禁止のハンドル (ENG-15) を閉じてから書く (仕様 5。保存の完了で元の方針に戻す)。
        snapshot.Storage.Owner.SuspendLock();
        AfterJournalWritten?.Invoke();

        // 2. 変更した範囲をオフセットの昇順に書き、ディスクへの反映を待つ。ここからはキャンセルしない (ジャーナルで戻せる)。
        try
        {
            using SafeFileHandle handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            for (int i = 0; i < ranges.Count; i++)
            {
                (long offset, long length) = ranges[i];
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    ReadResult read = snapshot.Read(offset + pos, buffer.AsSpan(0, n));
                    if (!read.IsComplete)
                    {
                        throw new UnreadableDataException(read.Unreadable);
                    }

                    RandomAccess.Write(handle, buffer.AsSpan(0, n), file.RangeStart + offset + pos);
                    done += n;
                    operation?.Report(done);
                }

                AfterRangeWritten?.Invoke(i);
            }

            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            if (journal is null)
            {
                throw new InPlaceSavePartiallyWrittenException(null, ex);
            }

            try
            {
                Rollback(journal);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new InPlaceSavePartiallyWrittenException(journal, ex);
            }

            throw new InPlaceSaveRolledBackException(ex);
        }

        // 3. ジャーナルを削除する。
        if (journal is not null)
        {
            File.Delete(journal);
        }

        return new InPlaceSaveResult(file, saved);
    }

    /// <summary>ジャーナルのパス。同じ名前の (前回の中断で残った) ジャーナルがあれば別の名前にする。</summary>
    private static string JournalPath(string directory, Guid documentId)
    {
        string path = Path.Combine(directory, $"journal-{documentId:N}.bin");
        return File.Exists(path) ? Path.Combine(directory, $"journal-{documentId:N}-{Guid.NewGuid():N}.bin") : path;
    }

    /// <summary>近い範囲 (間隔 64 KiB 以内) をまとめる。間の未変更部分は元データと同じ内容を書き戻す。</summary>
    private static List<(long Offset, long Length)> MergeRanges(IEnumerable<(long Offset, long Length)> ranges)
    {
        var merged = new List<(long Offset, long Length)>();
        foreach ((long offset, long length) in ranges)
        {
            if (merged.Count > 0 && offset - (merged[^1].Offset + merged[^1].Length) <= MergeGap)
            {
                merged[^1] = (merged[^1].Offset, offset + length - merged[^1].Offset);
            }
            else
            {
                merged.Add((offset, length));
            }
        }

        return merged;
    }

    private static void WriteHeader(Stream stream, FileByteSource file)
    {
        FileStamp stamp = FileStamp.FromPath(file.Path) ?? file.Stamp;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new JournalInfo(file.Path, stamp.Length, stamp.LastWriteTimeUtc, stamp.FileId));
        stream.Write(Magic);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
        stream.Write(size);
        stream.Write(json);
    }

    /// <summary>復旧用フォルダに残ったジャーナル (保存が途中で止まったもの。ENG-27 の仕様 7)。</summary>
    public static IReadOnlyList<string> FindJournals(string journalDirectory) =>
        Directory.Exists(journalDirectory) ? Directory.GetFiles(journalDirectory, "journal-*.bin") : [];

    /// <summary>ジャーナルの対象のファイルのパスを読む。</summary>
    public static string ReadJournalTarget(string journalPath) => ReadJournalInfo(journalPath).Path;

    /// <summary>ジャーナルの対象の識別情報を読む。</summary>
    public static JournalInfo ReadJournalInfo(string journalPath)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read);
        return ReadHeader(stream, out _);
    }

    private static JournalInfo ReadHeader(Stream stream, out bool hasEndMarker)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        bool old = magic.SequenceEqual(OldMagic);
        if (!old && !magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("ジャーナルの形式が違います。");
        }

        Span<byte> sizeBytes = stackalloc byte[4];
        stream.ReadExactly(sizeBytes);
        byte[] json = new byte[BinaryPrimitives.ReadInt32LittleEndian(sizeBytes)];
        stream.ReadExactly(json);
        JsonElement root = JsonDocument.Parse(json).RootElement;
        string Get(string upper, string lower) =>
            root.TryGetProperty(upper, out JsonElement e) || root.TryGetProperty(lower, out e) ? e.ToString() : string.Empty;
        long length = long.TryParse(Get("Length", "length"), out long l) ? l : -1;
        DateTime write = DateTime.TryParse(Get("LastWriteUtc", "lastWriteUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime w)
            ? w : DateTime.MinValue;
        hasEndMarker = old; // 旧形式には終わりの印がない (書きかけかどうか分からないため、書き戻す)。
        return new JournalInfo(Get("Path", "path"), length, write, Get("FileId", "fileId"));
    }

    /// <summary>
    /// 残ったジャーナルで保存前の状態に戻す (ENG-23 の仕様 4)。対象のファイルのパスを返す。ジャーナルが書きかけ (終わりの印がない)
    /// 場合は、ファイルはまだ書き換わっていないため何も書かずにジャーナルを消す。
    /// </summary>
    /// <exception cref="JournalMismatchException">対象の識別情報 (パス・ファイル ID・長さ) が一致しない。書き戻さず、ジャーナルは残す。</exception>
    public static string Rollback(string journalPath)
    {
        var records = new List<(long Offset, long Length, long At)>();
        JournalInfo info;
        bool complete;
        using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read))
        {
            info = ReadHeader(stream, out complete);
            byte[] record = new byte[16];
            while (stream.Read(record) == 16)
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(record);
                long length = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8));
                if (offset < 0)
                {
                    complete = true;
                    break;
                }

                if (length < 0 || stream.Position + length > stream.Length)
                {
                    break; // 書きかけの範囲
                }

                records.Add((offset, length, stream.Position));
                stream.Seek(length, SeekOrigin.Current);
            }
        }

        if (!complete)
        {
            File.Delete(journalPath);
            return info.Path;
        }

        // 最終更新日時は中断した保存そのものが変えるため比べない。パス・ファイル ID・長さで同じファイルかを確かめる。
        FileStamp? current = FileStamp.FromPath(info.Path);
        if (current is null
            || (info.Length >= 0 && current.Length != info.Length)
            || (info.FileId.Length > 0 && current.FileId.Length > 0 && current.FileId != info.FileId))
        {
            throw new JournalMismatchException(info.Path);
        }

        using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read))
        using (SafeFileHandle handle = File.OpenHandle(info.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] buffer = new byte[4 * 1024 * 1024];
            foreach ((long offset, long length, long at) in records)
            {
                stream.Position = at;
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    stream.ReadExactly(buffer, 0, n);
                    RandomAccess.Write(handle, buffer.AsSpan(0, n), offset + pos);
                }
            }

            RandomAccess.FlushToDisk(handle);
        }

        File.Delete(journalPath);
        return info.Path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>保存前の内容の重ね合わせ。後の保存で同じファイルが書き換わったら、内側をその保存の重ね合わせに付け替える。</summary>
internal interface IRebasableOverlay
{
    IByteSource Inner { get; set; }
}

/// <summary>
/// その場保存の前の内容を読むためのデータソース (ENG-05 の仕様 5): 現在のファイルに、上書きした範囲の旧内容
/// (追加バッファに退避したもの) を重ねて返す。後の保存で同じファイルがさらに書き換わったときは、
/// <see cref="Inner"/> をその保存の重ね合わせに付け替えて、つねに「この保存の直後の内容」を元にする。
/// </summary>
internal sealed class OverlayByteSource(IByteSource inner, AddBuffer addBuffer, IReadOnlyList<SavedRange> ranges) : ByteSourceBase, IRebasableOverlay
{
    public IByteSource Inner { get; set; } = inner;

    public override string DisplayName => Inner.DisplayName;

    public override string Identity => Inner.Identity + "#before-save";

    public override long Length => Inner.Length;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        ReadResult result = Inner.Read(offset, buffer);
        long end = offset + result.BytesReturned;
        foreach (SavedRange range in ranges)
        {
            long from = Math.Max(range.Offset, offset);
            long to = Math.Min(range.Offset + range.Length, end);
            if (from < to)
            {
                addBuffer.Read(range.OldContentInAddBuffer + (from - range.Offset), buffer.Slice((int)(from - offset), (int)(to - from)));
            }
        }

        return result;
    }

    // 内側のデータソースは現在の版が持っているため、ここでは閉じない。
    protected override void Dispose(bool disposing)
    {
    }
}
