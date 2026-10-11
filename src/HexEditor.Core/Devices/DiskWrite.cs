using System.Buffers.Binary;
using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Devices;

/// <summary>ディスク・ボリュームへの書き込みの計画 (ENG-30 の仕様 1・2)。UI は確認ダイアログの本文にこの内容を出す。</summary>
public sealed record DiskWritePlan
{
    public required DeviceByteSource Source { get; init; }

    /// <summary>書き込む範囲 (セクタ境界に広げたもの。昇順・重ならない)。</summary>
    public required IReadOnlyList<(long Offset, long Length)> Ranges { get; init; }

    /// <summary>書き込む総バイト数 (セクタ単位に広げた量)。</summary>
    public long TotalBytes => Ranges.Sum(r => r.Length);

    public int RangeCount => Ranges.Count;

    /// <summary>ロックが必要なボリューム (書き込む範囲に重なるマウント中のボリューム。ENG-30 の仕様 2)。</summary>
    public required IReadOnlyList<VolumeDeviceInfo> VolumesToLock { get; init; }

    /// <summary>Windows が使用中でロックもディスマウントもできないボリューム (重なると書き込みを拒否する。仕様 3 の 3)。</summary>
    public required IReadOnlyList<VolumeDeviceInfo> BlockedVolumes { get; init; }

    /// <summary>システムボリュームなどに重なるため書き込めない (仕様 3 の 3)。</summary>
    public bool IsBlocked => BlockedVolumes.Count > 0;
}

/// <summary>ディスク・ボリュームへの書き込みの計画と実行 (ENG-30)。</summary>
public static class DiskWrite
{
    private static readonly byte[] Magic = "HEXDISK1"u8.ToArray();

    /// <summary>テスト用: ロックの後、書き込みの前に呼ぶ (強制終了の再現)。</summary>
    public static Action? AfterLock { get; set; }

    /// <summary>書き込みの計画を作る (ENG-30 の仕様 1〜3)。</summary>
    public static DiskWritePlan Plan(Document document, DeviceByteSource source, DeviceCatalog catalog)
    {
        int sector = source.LogicalSectorSize;
        // 現在の版の変更範囲に加えて、前回この版に書き込んだ範囲も含める。Undo して再び保存したとき、旧内容を書き戻すため
        // (ENG-30 の仕様 5。Undo 後の版は変更ピースを持たないが、デバイスの内容とは違う)。
        IEnumerable<(long Offset, long Length)> dirty = document.Current.EnumerateModifiedRanges().Concat(document.LastDeviceWriteRanges);
        List<(long Offset, long Length)> ranges = WidenToSectors(Order(dirty), sector, source.Length);

        var toLock = new List<VolumeDeviceInfo>();
        var blocked = new List<VolumeDeviceInfo>();
        if (source.Info.Disk is { } disk)
        {
            foreach ((long offset, long length) in ranges)
            {
                foreach (VolumeDeviceInfo volume in catalog.VolumesOverlapping(disk.Number, source.Info.RangeStart + offset, length))
                {
                    (volume.IsInUseByWindows ? blocked : toLock).Add(volume);
                }
            }
        }
        else if (source.Info.Volume is { } vol)
        {
            (vol.IsInUseByWindows ? blocked : toLock).Add(vol);
        }

        return new DiskWritePlan
        {
            Source = source,
            Ranges = ranges,
            VolumesToLock = Distinct(toLock),
            BlockedVolumes = Distinct(blocked),
        };
    }

    /// <summary>範囲を昇順にし、重なり・隣接をまとめる (WidenToSectors の前処理)。</summary>
    private static List<(long Offset, long Length)> Order(IEnumerable<(long Offset, long Length)> ranges)
    {
        var sorted = ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset).ToList();
        var result = new List<(long Offset, long Length)>();
        foreach ((long offset, long length) in sorted)
        {
            if (result.Count > 0 && offset <= result[^1].Offset + result[^1].Length)
            {
                long end = Math.Max(result[^1].Offset + result[^1].Length, offset + length);
                result[^1] = (result[^1].Offset, end - result[^1].Offset);
            }
            else
            {
                result.Add((offset, length));
            }
        }

        return result;
    }

    private static IReadOnlyList<VolumeDeviceInfo> Distinct(List<VolumeDeviceInfo> volumes) =>
        [.. volumes.GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];

    /// <summary>変更範囲をセクタ境界に広げ、隣り合う範囲をまとめる (ENG-07 の仕様 6)。</summary>
    public static List<(long Offset, long Length)> WidenToSectors(IEnumerable<(long Offset, long Length)> ranges, int sector, long length)
    {
        var merged = new List<(long Offset, long Length)>();
        foreach ((long offset, long len) in ranges)
        {
            long from = offset / sector * sector;
            long to = Math.Min((offset + len + sector - 1) / sector * sector, length);
            if (merged.Count > 0 && from <= merged[^1].Offset + merged[^1].Length)
            {
                long end = Math.Max(merged[^1].Offset + merged[^1].Length, to);
                merged[^1] = (merged[^1].Offset, end - merged[^1].Offset);
            }
            else
            {
                merged.Add((from, to - from));
            }
        }

        return merged;
    }

    /// <summary>
    /// 計画を実行する (ENG-30 の仕様 3・4)。手順: ボリュームをロックする → 旧内容をジャーナルと追加バッファに退避する → セクタ単位で書き込む →
    /// ディスクに反映する → ロックを解除してジャーナルを消す。
    /// ロックを先に取るのは、ジャーナルに記録する旧内容が、書き込む直前のデバイスの内容と同じであることを保証するため
    /// (ロックの前に読むと、その間にファイルシステムが書いた内容を古い内容で書き戻してしまう)。
    /// ボリュームのドキュメントでは、開いているハンドルそのものでロックする (Windows ではロックしたハンドルからしか読み書きできない)。
    /// 物理ディスクのドキュメントでは、重なるボリュームを別のハンドルでロックし、ディスクのハンドルで書く。
    /// 書き込みの途中で失敗・キャンセルしたらジャーナルから書き戻す (仕様 6)。書き込んだ範囲と旧内容を退避した位置を返す (Undo のため。仕様 5)。
    /// </summary>
    /// <param name="journal">false なら旧内容をジャーナルに記録しない (上限を超えて利用者が「保護なしで書き込む」を選んだ。ENG-23 の仕様 3)。
    /// 旧内容は Undo のため追加バッファには退避する。</param>
    /// <exception cref="OperationCanceledException">キャンセルされた。デバイスは書き込み前の状態 (書き戻した)。</exception>
    public static IReadOnlyList<Saving.SavedRange> Execute(DiskWritePlan plan, DocumentSnapshot snapshot, string journalDirectory,
        Func<VolumeDeviceInfo, bool>? confirmDismount = null, LongRunningOperation? operation = null, bool journal = true)
    {
        if (plan.IsBlocked)
        {
            throw new DiskWriteBlockedException(plan.BlockedVolumes[0].Name);
        }

        DeviceByteSource source = plan.Source;
        operation?.SetTotal(plan.TotalBytes * 2);
        operation?.CancellationToken.ThrowIfCancellationRequested();

        // 1. ボリュームをロックする (仕様 3)。ロックの失敗はデバイスを変えていないため、そのまま中止する。
        var locks = new List<VolumeLock>();
        try
        {
            foreach (VolumeDeviceInfo volume in plan.VolumesToLock)
            {
                locks.Add(LockVolume(source, volume, confirmDismount));
            }
        }
        catch
        {
            UnlockAll(locks);
            throw;
        }

        string? journalPath = null;
        var saved = new List<Saving.SavedRange>(plan.Ranges.Count);
        AddBuffer addBuffer = snapshot.Storage.AddBuffer;
        byte[] buffer = new byte[DeviceByteSource.MaxTransfer];
        long done = 0;
        try
        {
            AfterLock?.Invoke();

            // 2. 旧内容をジャーナルと追加バッファに退避する (ENG-23 の手順 2・3)。まだ何も書いていないため、失敗したらジャーナルを消して中止する。
            if (journal)
            {
                Directory.CreateDirectory(journalDirectory);
                journalPath = Path.Combine(journalDirectory, $"disk-{Guid.NewGuid():N}.bin");
            }

            using (FileStream? stream = journalPath is null ? null
                : new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough))
            {
                if (stream is not null)
                {
                    WriteJournalHeader(stream, source);
                }

                byte[] record = new byte[16];
                foreach ((long offset, long length) in plan.Ranges)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(record, offset);
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), length);
                    stream?.Write(record);
                    long firstAt = -1;
                    for (long pos = 0; pos < length; pos += buffer.Length)
                    {
                        operation?.CancellationToken.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(buffer.Length, length - pos);
                        ReadResult read = source.Read(offset + pos, buffer.AsSpan(0, n));
                        if (!read.IsComplete)
                        {
                            throw new Saving.UnreadableDataException(read.Unreadable);
                        }

                        stream?.Write(buffer, 0, n);
                        long at = addBuffer.Append(buffer.AsSpan(0, n));
                        firstAt = firstAt < 0 ? at : firstAt;
                        done += n;
                        operation?.Report(done);
                    }

                    saved.Add(new Saving.SavedRange(offset, length, firstAt));
                }

                if (stream is not null)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(record, -1);
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), 0);
                    stream.Write(record);
                    stream.Flush(flushToDisk: true);
                }
            }
        }
        catch
        {
            UnlockAll(locks);
            if (journalPath is not null)
            {
                TryDelete(journalPath);
            }

            throw;
        }

        try
        {
            // 3. 新しい内容をセクタ単位で書き、ディスクへの反映を待つ (ここからはジャーナルで戻せる)。
            foreach ((long offset, long length) in plan.Ranges)
            {
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - pos);
                    ReadResult read = snapshot.Read(offset + pos, buffer.AsSpan(0, n));
                    if (!read.IsComplete)
                    {
                        throw new Saving.UnreadableDataException(read.Unreadable);
                    }

                    source.Write(offset + pos, buffer.AsSpan(0, n));
                    done += n;
                    operation?.Report(done);
                }
            }

            source.Flush();
        }
        catch (Exception ex)
        {
            // 書き込みの途中で失敗・キャンセル: ジャーナルから書き戻す (仕様 6)。ロックはまだ持っている (書き戻しもロックしたまま行う)。
            if (journalPath is null)
            {
                UnlockAll(locks);
                throw new DiskWritePartiallyWrittenException(null, ex);
            }

            try
            {
                RollbackFromJournal(journalPath, source);
            }
            catch (Exception rollback) when (rollback is IOException or DeviceException or InvalidDataException or ArgumentException)
            {
                // 書き戻しにも失敗: ジャーナルを残す (次回起動時に復旧。仕様「エラー」)。
                UnlockAll(locks);
                throw new DiskWritePartiallyWrittenException(journalPath, ex);
            }

            UnlockAll(locks);
            TryDelete(journalPath);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new DiskWriteRolledBackException(ex);
        }

        // 4. ロックを解除し、ジャーナルを消す。
        UnlockAll(locks);
        if (journalPath is not null)
        {
            TryDelete(journalPath);
        }

        return saved;
    }

    /// <summary>ロックしたボリューム。<see cref="Owned"/> なら、ロックのために開いたハンドル (解除の後に閉じる)。</summary>
    private readonly record struct VolumeLock(IDeviceHandle Handle, bool Owned);

    private static VolumeLock LockVolume(DeviceByteSource source, VolumeDeviceInfo volume, Func<VolumeDeviceInfo, bool>? confirmDismount)
    {
        // ボリュームのドキュメント自身のボリューム: そのハンドルでロックする (仕様 3。ロックしたハンドルからしか書けない)。
        bool own = string.Equals(volume.Path, source.Path, StringComparison.OrdinalIgnoreCase);
        IDeviceHandle handle = own ? source.Handle : source.Access.Open(volume.Path, writable: true);
        var result = new VolumeLock(handle, !own);
        try
        {
            if (handle.LockVolume() == 0)
            {
                return result;
            }

            // ロックできない (開いているファイルがある。仕様 3 の 2): 承認されたら強制的にディスマウントし、改めてロックする
            // (ディスマウントでファイルが閉じられるためロックできる。ロックせずに書くと OS が再マウントしうる)。
            if (confirmDismount?.Invoke(volume) == true && handle.DismountVolume() == 0 && handle.LockVolume() == 0)
            {
                return result;
            }

            throw new VolumeLockException(volume.Name);
        }
        catch
        {
            if (result.Owned)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static void UnlockAll(List<VolumeLock> locks)
    {
        foreach (VolumeLock held in locks)
        {
            try
            {
                held.Handle.UnlockVolume();
            }
            catch (Exception ex) when (ex is IOException or DeviceException)
            {
            }

            if (held.Owned)
            {
                held.Handle.Dispose();
            }
        }

        locks.Clear();
    }

    private static void WriteJournalHeader(Stream stream, DeviceByteSource source)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new DiskJournalInfo(source.Path, source.Info.SerialNumber ?? string.Empty, source.Length, source.LogicalSectorSize),
            HexEditor.Core.Elevation.DeviceJson.Options);
        stream.Write(Magic);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
        stream.Write(size);
        stream.Write(json);
    }

    /// <summary>残ったジャーナルの対象の識別情報を読む (起動時の復旧。ENG-30 の仕様 6)。</summary>
    public static DiskJournalInfo ReadJournalInfo(string journalPath)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read);
        return ReadHeader(stream);
    }

    private static DiskJournalInfo ReadHeader(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("The disk journal format is not recognized.");
        }

        Span<byte> sizeBytes = stackalloc byte[4];
        stream.ReadExactly(sizeBytes);
        byte[] json = new byte[BinaryPrimitives.ReadInt32LittleEndian(sizeBytes)];
        stream.ReadExactly(json);
        return JsonSerializer.Deserialize<DiskJournalInfo>(json, HexEditor.Core.Elevation.DeviceJson.Options)
            ?? throw new InvalidDataException("Empty disk journal header.");
    }

    /// <summary>残ったジャーナルを探す (ENG-27 の仕様 7・8)。</summary>
    public static IReadOnlyList<string> FindJournals(string journalDirectory) =>
        Directory.Exists(journalDirectory) ? Directory.GetFiles(journalDirectory, "disk-*.bin") : [];

    /// <summary>
    /// ジャーナルから旧内容をデバイスに書き戻す (ENG-30 の仕様 6、TC-ENG-30-05 の復旧)。識別情報 (パス・シリアル・長さ) が合わない
    /// デバイスには書き戻さない。
    /// </summary>
    public static void RollbackFromJournal(string journalPath, DeviceByteSource source)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read);
        DiskJournalInfo info = ReadHeader(stream);
        if (info.Length != source.Length
            || (info.Serial.Length > 0 && source.Info.SerialNumber is { Length: > 0 } s && !string.Equals(info.Serial, s, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The journal does not match this device.");
        }

        byte[] record = new byte[16];
        byte[] buffer = new byte[DeviceByteSource.MaxTransfer];
        bool complete = false;
        var records = new List<(long Offset, long Length, long At)>();
        while (stream.Read(record) == 16)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(record);
            long length = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8));
            if (offset < 0)
            {
                complete = true;
                break;
            }

            records.Add((offset, length, stream.Position));
            stream.Seek(length, SeekOrigin.Current);
        }

        // 終わりの印がない (旧内容を書き終える前に止まった): デバイスはまだ書き換わっていないため書き戻さない。
        if (!complete)
        {
            return;
        }

        foreach ((long offset, long length, long at) in records)
        {
            stream.Position = at;
            for (long pos = 0; pos < length; pos += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, length - pos);
                stream.ReadExactly(buffer, 0, n);
                source.Write(offset + pos, buffer.AsSpan(0, n));
            }
        }

        source.Flush();
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

/// <summary>ジャーナルの対象のデバイスの識別情報 (ENG-30 の仕様 4)。</summary>
public sealed record DiskJournalInfo(string Path, string Serial, long Length, int SectorSize);

/// <summary>システムボリュームなどに重なるため書き込めない (ENG-30 の仕様 3 の 3)。</summary>
public sealed class DiskWriteBlockedException(string volume) : IOException($"Windows is using the volume {volume}; it cannot be written.")
{
    public string Volume { get; } = volume;
}

/// <summary>ボリュームをロックできず、ディスマウントも承認されなかった (ENG-30 の「エラー」)。</summary>
public sealed class VolumeLockException(string volume) : IOException($"The volume {volume} could not be locked, so the write was cancelled.")
{
    public string Volume { get; } = volume;
}

/// <summary>書き込みに失敗し、ジャーナルから書き戻した (ENG-30 の「エラー」)。</summary>
public sealed class DiskWriteRolledBackException(Exception inner)
    : IOException($"An error occurred while writing; the device was restored to its state before the write. {inner.Message}", inner);

/// <summary>書き込みに失敗し、書き戻しにも失敗した (ENG-30 の「エラー」)。ジャーナルを残す。</summary>
public sealed class DiskWritePartiallyWrittenException(string? journalPath, Exception inner)
    : IOException($"Part of the device has been overwritten. It can be recovered on the next launch. {inner.Message}", inner)
{
    /// <summary>残したジャーナル。保護なしで書いた (ジャーナルがない) 場合は null。</summary>
    public string? JournalPath { get; } = journalPath;
}
