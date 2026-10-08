using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Saving;

/// <summary>保存先のドライブの空き容量が足りない (ENG-25)。</summary>
public sealed class InsufficientSpaceException(string drive, long required, long available)
    : IOException($"保存先のドライブ ({drive}) の空き容量が足りません。必要: {required:N0} バイト、空き: {available:N0} バイト")
{
    public string Drive { get; } = drive;

    public long Required { get; } = required;

    public long Available { get; } = available;
}

/// <summary>
/// 保存先のファイルシステムの 1 ファイルの上限を超える (ENG-20 の「エラー」、ENG-25 の仕様 5)。UI は「このドライブのファイルシステム
/// (FAT32) では 4 GiB 以上のファイルを保存できません」と示す。
/// </summary>
public sealed class FileSizeLimitException(string drive, string fileSystem, long maxFileSize, long length)
    : IOException($"このドライブ ({drive}) のファイルシステム ({fileSystem}) では {maxFileSize:N0} バイトを超えるファイルを保存できません。サイズ: {length:N0} バイト")
{
    public string Drive { get; } = drive;

    public string FileSystem { get; } = fileSystem;

    public long MaxFileSize { get; } = maxFileSize;

    public long Length { get; } = length;
}

/// <summary>読めない元データを含むため保存できない (ENG-20 の仕様 3)。</summary>
public sealed class UnreadableDataException(IReadOnlyList<UnreadableRange> ranges)
    : IOException($"読めないデータが {ranges.Count} か所あるため保存できません。")
{
    public IReadOnlyList<UnreadableRange> Ranges { get; } = ranges;
}

/// <summary>作ったバックアップ (ENG-26)。</summary>
public sealed record BackupOutcome(string Path, TimeSpan Time);

/// <summary>
/// 安全な保存 (ENG-22): 保存先と同じフォルダの一時ファイルに書き出してから置き換える。途中で失敗・キャンセルしても
/// 保存先のファイルは保存前のまま残る。
/// </summary>
public static class DocumentSaver
{
    /// <summary>保存時に確保しておく空き容量の余裕 (ENG-25)。</summary>
    public const long FreeSpaceMargin = 16L * 1024 * 1024;

    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>スパースの保存で、書かずに未割り当てのままにするブロックの単位 (ENG-22 の仕様 5)。</summary>
    public const int SparseBlockSize = 64 * 1024;

    /// <summary>
    /// <paramref name="snapshot"/> を <paramref name="targetPath"/> に書き出す。成功したら保存したファイルを開いて返す
    /// (呼び出し側は UI スレッドで <see cref="Document.CompleteSave"/> を呼ぶ)。
    /// </summary>
    public static FileByteSource Save(DocumentSnapshot snapshot, string targetPath, LongRunningOperation? operation = null,
        IVolumeInfoProvider? volumes = null) => Save(snapshot, targetPath, operation, volumes, null, null, out _);

    /// <summary>
    /// 書き出す。<paramref name="backup"/> を指定すると、置き換え前のファイルの名前を変えてバックアップにする (ENG-26 の仕様 4。
    /// コピーしないため、ファイルサイズに関係なく即座に終わる)。置き場所が別のボリュームの場合はコピーする。
    /// <paramref name="markerDirectory"/> を指定すると、一時ファイルの名前と保存先をそこに記録し (ENG-22 の仕様 7)、異常終了で残った一時ファイルを
    /// 次回起動時に片付けられるようにする。
    /// </summary>
    public static FileByteSource Save(DocumentSnapshot snapshot, string targetPath, LongRunningOperation? operation,
        IVolumeInfoProvider? volumes, BackupSettings? backup, string? markerDirectory, out BackupOutcome? backupOutcome)
    {
        backupOutcome = null;
        string target = ResolveTarget(Path.GetFullPath(targetPath));
        string folder = Path.GetDirectoryName(target)!;
        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder);
        CheckFileSizeLimit(volume, snapshot.Length);
        IReadOnlyList<(long Offset, long Length)>? sparse = SparseDataRanges(snapshot);
        CheckFreeSpace(volume, sparse?.Sum(r => r.Length) ?? snapshot.Length);

        // バックアップを作れるかを先に確かめる。作れなければ書き始めない (ENG-26 の「エラー」)。別のボリュームに置く場合はコピーになるため、
        // 置き場所の空き容量も確かめる。既存のバックアップの世代をずらすのは、一時ファイルを書き終えて置き換える直前 (書き出しに失敗・
        // キャンセルしても、前のバックアップは残る)。
        string? backupPath = null;
        if (backup is not null && File.Exists(target))
        {
            string first = Backup.PathFor(target, backup);
            backupPath = Backup.Prepare(target, backup, volumes, Backup.SameVolume(target, first) ? 0 : new FileInfo(target).Length);
        }

        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.~hex{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp");
        SaveTempMarker marker = SaveTempMarker.Record(markerDirectory, temp, target);
        try
        {
            WriteTemp(snapshot, temp, sparse, operation);

            // 手順 7: 自分の書き込み禁止のハンドル (ENG-15) を閉じる。保存の完了で元の方針に戻す。
            snapshot.Storage.Owner.SuspendLock();
            if (File.Exists(target))
            {
                if (backupPath is not null && Backup.SameVolume(target, backupPath))
                {
                    // 置き換え前のファイルの名前を変えてバックアップにする。Undo 用に開いているハンドルはバックアップを指す。
                    string renamedTo = backupPath;
                    TimeSpan time = Backup.Measure(() =>
                    {
                        Backup.Rotate(target, backup!);
                        File.Replace(temp, target, renamedTo, ignoreMetadataErrors: true);
                    });
                    backupOutcome = new BackupOutcome(renamedTo, time);
                }
                else
                {
                    if (backupPath is not null)
                    {
                        string copyTo = backupPath;
                        TimeSpan time = Backup.Measure(() => Backup.CreateByCopy(target, backup!, operation));
                        backupOutcome = new BackupOutcome(copyTo, time);
                    }

                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch
        {
            // 一時ファイルを消せなければ記録を残す (次回起動時に削除を提案する)。
            if (TryDelete(temp))
            {
                marker.Dispose();
            }
            else
            {
                marker.Keep();
            }

            throw;
        }

        marker.Dispose();
        return FileByteSource.Open(target);
    }

    /// <summary>
    /// 安全な保存に必要な容量の見積もり (ENG-25 の仕様 1。余裕の 16 MiB を含まない)。元のファイルがスパースなら、00 でない可能性のある
    /// 64 KiB のブロックの合計 (元のファイルの割り当て済みの範囲と変更した範囲から計算し、全体は読まない)。それ以外は保存後のサイズ。
    /// </summary>
    public static long EstimateSize(DocumentSnapshot snapshot) => SparseDataRanges(snapshot)?.Sum(r => r.Length) ?? snapshot.Length;

    /// <summary>
    /// 元のファイルがスパースの場合に、書き出す必要のある範囲 (ENG-22 の仕様 5): 元のファイルの割り当て済みの範囲を指すピースと、
    /// 元データ以外のピース (すべて 00 のパターンを除く) の範囲を 64 KiB の境界に広げてまとめたもの。それ以外の範囲はすべて 00 なので
    /// 書かずに未割り当てのままにする。スパースでなければ null (全体を書く)。
    /// </summary>
    internal static IReadOnlyList<(long Offset, long Length)>? SparseDataRanges(DocumentSnapshot snapshot)
    {
        if (snapshot.Storage.Source is not FileByteSource { IsSparse: true } file)
        {
            return null;
        }

        IReadOnlyList<(long Offset, long Length)> allocated = file.AllocatedRanges(0, file.Length);
        var ranges = new List<(long Offset, long Length)>();
        void Add(long offset, long length)
        {
            long start = offset / SparseBlockSize * SparseBlockSize;
            long end = Math.Min(snapshot.Length, (offset + length + SparseBlockSize - 1) / SparseBlockSize * SparseBlockSize);
            if (ranges.Count > 0 && start <= ranges[^1].Offset + ranges[^1].Length)
            {
                (long o, long l) = ranges[^1];
                ranges[^1] = (o, Math.Max(o + l, end) - o);
            }
            else
            {
                ranges.Add((start, end - start));
            }
        }

        foreach ((long docOffset, Piece piece) in snapshot.Tree.EnumerateAll())
        {
            if (piece.Kind == PieceKind.Original)
            {
                // 割り当て済みの範囲のうち、このピースと重なるもの (範囲はオフセットの昇順)。
                long pieceEnd = piece.Offset + piece.Length;
                for (int i = FirstEndingAfter(allocated, piece.Offset); i < allocated.Count && allocated[i].Offset < pieceEnd; i++)
                {
                    long from = Math.Max(piece.Offset, allocated[i].Offset);
                    long to = Math.Min(pieceEnd, allocated[i].Offset + allocated[i].Length);
                    Add(docOffset + (from - piece.Offset), to - from);
                }
            }
            else if (!(piece.Kind == PieceKind.Pattern && IsZeroPattern(snapshot, piece)))
            {
                Add(docOffset, piece.Length);
            }
        }

        return ranges;
    }

    /// <summary>終わりが <paramref name="offset"/> より後にある最初の範囲の番号 (二分探索)。</summary>
    private static int FirstEndingAfter(IReadOnlyList<(long Offset, long Length)> ranges, long offset)
    {
        int lo = 0, hi = ranges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].Offset + ranges[mid].Length <= offset)
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

    private static bool IsZeroPattern(DocumentSnapshot snapshot, Piece piece)
    {
        byte[] pattern = new byte[piece.PatternLength];
        snapshot.Storage.AddBuffer.Read(piece.Offset, pattern);
        return !pattern.AsSpan().ContainsAnyExcept((byte)0);
    }

    /// <summary>
    /// 保存先のドライブの空き容量を確認する (ENG-25)。ネットワーク上の場所も確認し、空き容量を取得できない場合だけ省略する
    /// (書き込み時のエラーで扱う)。
    /// </summary>
    public static void CheckFreeSpace(string folder, long length, IVolumeInfoProvider? volumes = null) =>
        CheckFreeSpace((volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder), length);

    /// <summary>必要な容量 (書き出すサイズ + 16 MiB) が足りなければ <see cref="InsufficientSpaceException"/>。</summary>
    public static void CheckFreeSpace(VolumeInfo? volume, long length)
    {
        if (volume?.AvailableFreeSpace is not long available)
        {
            return;
        }

        long required = length > long.MaxValue - FreeSpaceMargin ? long.MaxValue : length + FreeSpaceMargin;
        if (available < required)
        {
            throw new InsufficientSpaceException(volume.Name, required, available);
        }
    }

    /// <summary>ファイルシステムのファイルサイズの上限を超えれば <see cref="FileSizeLimitException"/> (空き容量より先に確認する)。</summary>
    public static void CheckFileSizeLimit(VolumeInfo? volume, long length)
    {
        if (volume?.MaxFileSize is long max && length > max)
        {
            throw new FileSizeLimitException(volume.Name, volume.FileSystem!, max, length);
        }
    }

    private static void WriteTemp(DocumentSnapshot snapshot, string temp, IReadOnlyList<(long Offset, long Length)>? sparse,
        LongRunningOperation? operation)
    {
        using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.SequentialScan);
        File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);

        // 元のファイルがスパースなら、一時ファイルもスパースにする (長さを決める前に。ENG-22 の仕様 5)。
        // スパースにできない場所 (NTFS 以外) では全体を書く。
        if (sparse is not null && !SparseFiles.TryMakeSparse(stream.SafeFileHandle))
        {
            sparse = null;
        }

        // 最終的な長さを先に確保し、容量不足を書き込み前に検出する。
        stream.SetLength(snapshot.Length);
        byte[] buffer = new byte[BufferSize];
        if (sparse is not null)
        {
            WriteSparse(snapshot, stream, sparse, buffer, operation);
        }
        else
        {
            for (long offset = 0; offset < snapshot.Length; offset += BufferSize)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(BufferSize, snapshot.Length - offset);
                ReadResult read = snapshot.Read(offset, buffer.AsSpan(0, n));
                if (!read.IsComplete)
                {
                    throw new UnreadableDataException(read.Unreadable);
                }

                stream.Write(buffer, 0, n);
                operation?.Report(offset + n);
            }
        }

        stream.Flush(flushToDisk: true);
        stream.Close();
        File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
    }

    /// <summary>
    /// スパースの一時ファイルに、<paramref name="ranges"/> の範囲だけを書く。すべて 00 の 64 KiB のブロックは書かずに未割り当てのままにする。
    /// </summary>
    private static void WriteSparse(DocumentSnapshot snapshot, FileStream stream, IReadOnlyList<(long Offset, long Length)> ranges, byte[] buffer,
        LongRunningOperation? operation)
    {
        foreach ((long start, long length) in ranges)
        {
            for (long offset = start; offset < start + length; offset += BufferSize)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(BufferSize, start + length - offset);
                ReadResult read = snapshot.Read(offset, buffer.AsSpan(0, n));
                if (!read.IsComplete)
                {
                    throw new UnreadableDataException(read.Unreadable);
                }

                for (int block = 0; block < n; block += SparseBlockSize)
                {
                    ReadOnlySpan<byte> data = buffer.AsSpan(block, Math.Min(SparseBlockSize, n - block));
                    if (data.ContainsAnyExcept((byte)0))
                    {
                        RandomAccess.Write(stream.SafeFileHandle, data, offset + block);
                    }
                }

                operation?.Report(offset + n);
            }
        }

        operation?.Report(snapshot.Length);
    }

    /// <summary>シンボリックリンクの場合はリンク先のファイルを置き換える (ENG-22 の手順 1)。</summary>
    private static string ResolveTarget(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is not null)
        {
            FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
            {
                return resolved.FullName;
            }
        }

        return path;
    }

    /// <summary>消す。消せなければ false (残った一時ファイルは次回起動時の復旧の画面で片付ける。ENG-27 の仕様 7)。</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
