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
    /// 計画を実行する (ENG-30 の仕様 4): 旧内容をジャーナルに退避し、ボリュームをロックし、セクタ単位で書き込み、ディスクに反映し、
    /// ロックを解除してジャーナルを消す。途中で失敗したらジャーナルから書き戻す。書き込んだ範囲と旧内容を退避した位置を返す
    /// (Undo のため。ENG-30 の仕様 5)。
    /// </summary>
    public static IReadOnlyList<Saving.SavedRange> Execute(DiskWritePlan plan, DocumentSnapshot snapshot, string journalDirectory,
        Func<VolumeDeviceInfo, bool>? confirmDismount = null, LongRunningOperation? operation = null)
    {
        if (plan.IsBlocked)
        {
            throw new DiskWriteBlockedException(plan.BlockedVolumes[0].Name);
        }

        DeviceByteSource source = plan.Source;
        operation?.SetTotal(plan.TotalBytes * 2);

        Directory.CreateDirectory(journalDirectory);
        string journalPath = Path.Combine(journalDirectory, $"disk-{Guid.NewGuid():N}.bin");
        var saved = new List<Saving.SavedRange>(plan.Ranges.Count);
        AddBuffer addBuffer = snapshot.Storage.AddBuffer;
        byte[] buffer = new byte[DeviceByteSource.MaxTransfer];
        long done = 0;

        // 1. 旧内容をジャーナルと追加バッファに退避する。
        using (var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough))
        {
            WriteJournalHeader(journal, source);
            byte[] record = new byte[16];
            foreach ((long offset, long length) in plan.Ranges)
            {
                BinaryPrimitives.WriteInt64LittleEndian(record, offset);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), length);
                journal.Write(record);
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

                    journal.Write(buffer, 0, n);
                    long at = addBuffer.Append(buffer.AsSpan(0, n));
                    firstAt = firstAt < 0 ? at : firstAt;
                    done += n;
                    operation?.Report(done);
                }

                saved.Add(new Saving.SavedRange(offset, length, firstAt));
            }

            BinaryPrimitives.WriteInt64LittleEndian(record, -1);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), 0);
            journal.Write(record);
            journal.Flush(flushToDisk: true);
        }

        // 2. ボリュームをロックする。ロックの失敗はデバイスを変えていないため、ジャーナルを消して中止する (書き戻しは不要)。
        var locks = new List<IDeviceHandle>();
        try
        {
            foreach (VolumeDeviceInfo volume in plan.VolumesToLock)
            {
                locks.Add(LockVolume(source.Access, volume, confirmDismount));
            }
        }
        catch
        {
            UnlockAll(locks);
            TryDelete(journalPath);
            throw;
        }

        try
        {
            AfterLock?.Invoke();

            // 3. 新しい内容をセクタ単位で書き、ディスクへの反映を待つ (ここからはジャーナルで戻せる)。
            foreach ((long offset, long length) in plan.Ranges)
            {
                for (long pos = 0; pos < length; pos += buffer.Length)
                {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 書き込みの途中で失敗: ジャーナルから書き戻す (仕様 6)。
            try
            {
                RollbackFromJournal(journalPath, source);
            }
            catch (Exception rollback) when (rollback is IOException or DeviceException or InvalidDataException)
            {
                // 書き戻しにも失敗: ジャーナルを残す (次回起動時に復旧。仕様「エラー」)。
                UnlockAll(locks);
                throw new DiskWritePartiallyWrittenException(journalPath, ex);
            }

            UnlockAll(locks);
            TryDelete(journalPath);
            throw new DiskWriteRolledBackException(ex);
        }

        // 4. ロックを解除し、ジャーナルを消す。
        UnlockAll(locks);
        TryDelete(journalPath);
        return saved;
    }

    private static IDeviceHandle LockVolume(IDeviceAccess access, VolumeDeviceInfo volume, Func<VolumeDeviceInfo, bool>? confirmDismount)
    {
        IDeviceHandle handle = access.Open(volume.Path, writable: true);
        try
        {
            if (handle.LockVolume() == 0)
            {
                return handle;
            }

            // ロックできない (開いているファイルがある。仕様 3 の 2): ディスマウントの確認を取る。ディスマウントできれば
            // ボリュームは排他状態になる (OS が再マウントするまでロックは不要)。
            if (confirmDismount?.Invoke(volume) == true && handle.DismountVolume() == 0)
            {
                return handle;
            }

            handle.Dispose();
            throw new VolumeLockException(volume.Name);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void UnlockAll(List<IDeviceHandle> locks)
    {
        foreach (IDeviceHandle handle in locks)
        {
            try
            {
                handle.UnlockVolume();
            }
            catch (Exception ex) when (ex is IOException or DeviceException)
            {
            }

            handle.Dispose();
        }
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
public sealed class DiskWritePartiallyWrittenException(string journalPath, Exception inner)
    : IOException($"Part of the device has been overwritten. It can be recovered on the next launch. {inner.Message}", inner)
{
    public string JournalPath { get; } = journalPath;
}
