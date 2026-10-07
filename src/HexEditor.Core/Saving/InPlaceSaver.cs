using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Saving;

/// <summary>書き込む旧内容がジャーナルの上限を超える (ENG-23 の仕様 3)。呼び出し側は安全な保存に切り替える。</summary>
public sealed class JournalLimitException(long required, long limit)
    : IOException($"その場保存のジャーナルが上限を超えます。必要: {required:N0} バイト、上限: {limit:N0} バイト")
{
    public long Required { get; } = required;

    public long Limit { get; } = limit;
}

/// <summary>その場保存で書き込んだ範囲と、その旧内容を退避した追加バッファの位置。</summary>
public sealed record InPlaceSaveResult(FileByteSource Source, IReadOnlyList<SavedRange> Ranges);

public readonly record struct SavedRange(long Offset, long Length, long OldContentInAddBuffer);

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

    private static readonly byte[] Magic = "HEXJRNL1"u8.ToArray();

    /// <summary>テスト用: ジャーナルを書いた後、ファイルに書き込む前に呼ぶ (強制終了の再現。テスト方針 7.2)。</summary>
    internal static Action? AfterJournalWritten { get; set; }

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

    /// <summary>
    /// 変更した範囲を書き込む。旧内容は追加バッファにも退避して返す (保存前の Undo 履歴のため。ENG-05 の仕様 5)。
    /// 成功したら呼び出し側は UI スレッドで <see cref="Document.CompleteInPlaceSave"/> を呼ぶ。
    /// </summary>
    public static InPlaceSaveResult Save(DocumentSnapshot snapshot, string journalDirectory, long journalLimit = DefaultJournalLimit,
        LongRunningOperation? operation = null)
    {
        var file = (FileByteSource)snapshot.Storage.Source;
        List<(long Offset, long Length)> ranges = MergeRanges(snapshot.EnumerateModifiedRanges());
        long total = ranges.Sum(r => r.Length);
        if (total > journalLimit)
        {
            throw new JournalLimitException(total, journalLimit);
        }

        operation?.SetTotal(total * 2);
        Directory.CreateDirectory(journalDirectory);
        string journal = Path.Combine(journalDirectory, $"journal-{Guid.NewGuid():N}.bin");
        DocumentSaver.CheckFreeSpace(journalDirectory, total);

        // 1. 旧内容をジャーナルと追加バッファに書く。
        var saved = new List<SavedRange>(ranges.Count);
        byte[] buffer = new byte[4 * 1024 * 1024];
        byte[] record = new byte[16];
        long done = 0;
        using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough))
        {
            WriteHeader(stream, file);
            foreach ((long offset, long length) in ranges)
            {
                BinaryPrimitives.WriteInt64LittleEndian(record, offset);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), length);
                stream.Write(record);
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

                    stream.Write(buffer, 0, n);
                    long at = snapshot.Storage.AddBuffer.Append(buffer.AsSpan(0, n));
                    firstAt = firstAt < 0 ? at : firstAt;
                    done += n;
                    operation?.Report(done);
                }

                saved.Add(new SavedRange(offset, length, firstAt));
            }

            stream.Flush(flushToDisk: true);
        }

        AfterJournalWritten?.Invoke();

        // 2. 変更した範囲をオフセットの昇順に書き、ディスクへの反映を待つ。ここからはキャンセルしない (ジャーナルで戻せる)。
        using (SafeFileHandle handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            foreach ((long offset, long length) in ranges)
            {
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    snapshot.Read(offset + pos, buffer.AsSpan(0, n));
                    RandomAccess.Write(handle, buffer.AsSpan(0, n), offset + pos);
                    done += n;
                    operation?.Report(done);
                }
            }

            RandomAccess.FlushToDisk(handle);
        }

        // 3. ジャーナルを削除する。
        File.Delete(journal);
        return new InPlaceSaveResult(file, saved);
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
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            path = file.Path,
            length = file.Length,
            lastWriteUtc = File.GetLastWriteTimeUtc(file.Path),
        });
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
    public static string ReadJournalTarget(string journalPath)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read);
        return ReadHeader(stream);
    }

    private static string ReadHeader(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("ジャーナルの形式が違います。");
        }

        Span<byte> sizeBytes = stackalloc byte[4];
        stream.ReadExactly(sizeBytes);
        byte[] json = new byte[BinaryPrimitives.ReadInt32LittleEndian(sizeBytes)];
        stream.ReadExactly(json);
        return JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;
    }

    /// <summary>
    /// 残ったジャーナルで保存前の状態に戻す (ENG-23 の仕様 4)。対象のファイルのパスを返す。
    /// </summary>
    public static string Rollback(string journalPath)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read);
        string path = ReadHeader(stream);

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> record = stackalloc byte[16];
        byte[] buffer = new byte[4 * 1024 * 1024];
        while (stream.Read(record) == 16)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(record);
            long length = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            for (long pos = 0; pos < length; pos += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, length - pos);
                stream.ReadExactly(buffer, 0, n);
                RandomAccess.Write(handle, buffer.AsSpan(0, n), offset + pos);
            }
        }

        RandomAccess.FlushToDisk(handle);
        stream.Close();
        File.Delete(journalPath);
        return path;
    }
}

/// <summary>
/// その場保存の前の内容を読むためのデータソース (ENG-05 の仕様 5): 現在のファイルに、上書きした範囲の旧内容
/// (追加バッファに退避したもの) を重ねて返す。後の保存で同じファイルがさらに書き換わったときは、
/// <see cref="Inner"/> をその保存の重ね合わせに付け替えて、つねに「この保存の直後の内容」を元にする。
/// </summary>
internal sealed class OverlayByteSource(IByteSource inner, AddBuffer addBuffer, IReadOnlyList<SavedRange> ranges) : ByteSourceBase
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
