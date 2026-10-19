using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics.Images.Scope;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images;

/// <summary>セクタ範囲の付随ファイル (<c>.bin.json</c>。FOR-25 の仕様 1)。</summary>
public sealed record SectorRangeSidecar
{
    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>入力元のディスクの容量 (バイト)。</summary>
    public long Capacity { get; init; }

    public int SectorSize { get; init; } = 512;

    public long StartLba { get; init; }

    public long SectorCount { get; init; }

    /// <summary>保存日時 (UTC、ISO 8601 の Z 付き)。</summary>
    public string SavedAt { get; init; } = string.Empty;

    /// <summary>データファイルの SHA-256 (小文字の 16 進)。</summary>
    public string Sha256 { get; init; } = string.Empty;

    public string? Source { get; init; }
}

/// <summary>書き込み先の説明 (シリアル番号・容量の照合と、確認ダイアログの本文)。</summary>
public sealed record SectorTargetInfo(string Path, string? Model, string? SerialNumber, long Capacity, int SectorSize, string? Identity = null);

/// <summary>復元の計画 (FOR-25 の仕様 2〜5)。</summary>
public sealed record SectorRestorePlan
{
    public SectorRangeSidecar? Sidecar { get; init; }

    public required SectorTargetInfo Target { get; init; }

    public long StartLba { get; init; }

    public long SectorCount { get; init; }

    public long Bytes => SectorCount * Target.SectorSize;

    /// <summary>データファイルの SHA-256 が付随ファイルと合わない (実行しない)。</summary>
    public bool HashMismatch { get; init; }

    /// <summary>付随ファイルのディスクと書き込み先のシリアル番号・容量が違う (警告し、確認ダイアログに含める)。</summary>
    public bool IdentityMismatch { get; init; }

    /// <summary>付随ファイルがなく、開始 LBA を利用者が入力する必要がある。</summary>
    public bool NeedsStartLba { get; init; }

    /// <summary>範囲が書き込み先の外にはみ出す。</summary>
    public bool OutOfRange { get; init; }

    /// <summary>データファイルの長さがセクタサイズの倍数でない。</summary>
    public bool NotAligned { get; init; }

    /// <summary>範囲がディスク全体またはパーティション全体に及ぶ (強い確認にする)。</summary>
    public bool WholeDiskOrPartition { get; init; }

    /// <summary>範囲に重なる、ロックが要るボリューム。</summary>
    public IReadOnlyList<VolumeDeviceInfo> VolumesToLock { get; init; } = [];

    /// <summary>書き込み保護モード (FOR-01) のため復元できない。</summary>
    public bool WriteBlocked { get; init; }

    /// <summary>実行できる (警告は確認ダイアログで示す)。</summary>
    public bool CanRun => !HashMismatch && !NeedsStartLba && !OutOfRange && !NotAligned && !WriteBlocked && SectorCount > 0;
}

/// <summary>復元の結果。</summary>
public sealed record SectorRestoreResult
{
    public bool Success { get; init; }

    /// <summary>書き込んだバイト数 (失敗した場合は失敗した位置まで)。</summary>
    public long WrittenBytes { get; init; }

    /// <summary>書き込みに失敗した LBA (なければ null)。</summary>
    public long? FailedLba { get; init; }

    /// <summary>書き込み後に読み直してデータファイルと一致した。</summary>
    public bool? Verified { get; init; }

    /// <summary>復元前の退避ファイル (<c>&lt;名前&gt;.before-&lt;日時&gt;.bin</c>)。</summary>
    public string? BackupPath { get; init; }

    public string? Message { get; init; }
}

/// <summary>セクタ範囲の保存と復元 (FOR-25)。</summary>
public static class SectorRangeArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 8 };

    public static string SidecarPath(string binPath) => binPath + ".json";

    /// <summary>
    /// [開始 LBA, + セクタ数) をデータファイル (<c>.bin</c>) と付随ファイル (<c>.bin.json</c>) に保存する (仕様 1)。読めないセクタがあれば保存しない
    /// (0 で埋めた内容を後で書き戻さないため)。
    /// </summary>
    /// <exception cref="Saving.UnreadableDataException">読めないセクタがある。</exception>
    public static SectorRangeSidecar Save(IByteSource disk, AcquisitionSourceInfo info, long startLba, long sectorCount, string binPath,
        LongRunningOperation? operation = null, TimeProvider? time = null)
    {
        int sector = Math.Max(1, info.SectorSize);
        long offset = checked(startLba * sector);
        long length = checked(sectorCount * sector);
        if (startLba < 0 || sectorCount <= 0 || offset + length > disk.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "The sector range is outside the disk.");
        }

        time ??= TimeProvider.System;
        operation?.SetTotal(length);
        string temp = binPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(binPath))!);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1 << 20];
                for (long done = 0; done < length; done += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - done);
                    ReadResult r = disk.Read(offset + done, buffer.AsSpan(0, n));
                    if (!r.IsComplete)
                    {
                        throw new Saving.UnreadableDataException(r.Unreadable);
                    }

                    stream.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                    operation?.Report(done + n);
                }
            }

            File.Move(temp, binPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        var sidecar = new SectorRangeSidecar
        {
            Model = info.Model,
            SerialNumber = info.SerialNumber,
            Capacity = info.Capacity,
            SectorSize = sector,
            StartLba = startLba,
            SectorCount = sectorCount,
            SavedAt = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            Sha256 = Convert.ToHexStringLower(sha.GetHashAndReset()),
            Source = info.Path,
        };
        File.WriteAllText(SidecarPath(binPath), JsonSerializer.Serialize(sidecar, Json));
        Audit("sectors.save", info.Path, info.Identity, binPath, startLba, sectorCount, ImageAuditResult.Success, sidecar.Sha256, null);
        return sidecar;
    }

    /// <summary>付随ファイルを読む。なければ null。壊れていれば <see cref="InvalidDataException"/>。</summary>
    public static SectorRangeSidecar? ReadSidecar(string binPath)
    {
        string path = SidecarPath(binPath);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw new InvalidDataException("The sidecar file is too large.");
        }

        try
        {
            SectorRangeSidecar? s = JsonSerializer.Deserialize<SectorRangeSidecar>(File.ReadAllText(path), Json);
            if (s is null || s.SectorSize is < 1 or > 65536 || s.StartLba < 0 || s.SectorCount < 0)
            {
                throw new InvalidDataException("The sidecar file has invalid values.");
            }

            return s;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The sidecar file is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>ファイルの SHA-256 (小文字の 16 進)。</summary>
    public static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 復元の計画を作る (仕様 2〜5)。<paramref name="startLba"/> を指定すると付随ファイルの開始 LBA の代わりに使う。
    /// <paramref name="catalog"/> と <paramref name="diskNumber"/> を渡すと、範囲に重なるボリューム (ロックの対象) を求める。
    /// </summary>
    public static SectorRestorePlan PlanRestore(string binPath, IByteSource target, SectorTargetInfo targetInfo, long? startLba = null,
        DeviceCatalog? catalog = null, int? diskNumber = null, long targetBaseOffset = 0)
    {
        SectorRangeSidecar? sidecar = ReadSidecar(binPath);
        long fileLength = new FileInfo(binPath).Length;
        int sector = targetInfo.SectorSize;
        bool hashMismatch = sidecar is not null && !string.Equals(Sha256Of(binPath), sidecar.Sha256, StringComparison.OrdinalIgnoreCase);
        bool identityMismatch = sidecar is not null && ((sidecar.SerialNumber is { Length: > 0 } s && !string.Equals(s, targetInfo.SerialNumber, StringComparison.Ordinal))
            || sidecar.Capacity != targetInfo.Capacity || sidecar.SectorSize != sector);
        long? start = startLba ?? sidecar?.StartLba;
        long count = fileLength / sector;
        bool outOfRange = start is long st && (st < 0 || (st + count) * sector > target.Length);
        bool whole = false;
        if (start is long first && !outOfRange)
        {
            long offset = first * sector;
            long length = count * sector;
            whole = offset == 0 && length == target.Length
                || ImagePartitions.Read(target, sector).Partitions.Any(p => p.Offset == offset && p.Length == length);
        }

        IReadOnlyList<VolumeDeviceInfo> volumes = start is long at && catalog is not null && diskNumber is int disk
            ? catalog.VolumesOverlapping(disk, targetBaseOffset + at * sector, count * sector)
            : [];
        return new SectorRestorePlan
        {
            Sidecar = sidecar,
            Target = targetInfo,
            StartLba = start ?? 0,
            SectorCount = count,
            HashMismatch = hashMismatch,
            IdentityMismatch = identityMismatch,
            NeedsStartLba = start is null,
            OutOfRange = outOfRange,
            NotAligned = fileLength % sector != 0,
            WholeDiskOrPartition = whole,
            VolumesToLock = volumes,
            WriteBlocked = ImageForensics.Hooks.IsWriteBlocked,
        };
    }

    /// <summary>
    /// 復元する (仕様 3〜5): 退避 (既定オン) → 範囲に重なるボリュームのロック → 書き込み (失敗は 1 回だけ再試行) → 読み直してデータファイルと比べる。
    /// 書き込みに失敗したら、書き込めた範囲と失敗した LBA を返す (退避ファイルからの書き戻しを提案する)。
    /// </summary>
    public static SectorRestoreResult Restore(SectorRestorePlan plan, string binPath, IByteSource target, bool backup, DeviceByteSource? lockDevice = null,
        Func<VolumeDeviceInfo, bool>? confirmDismount = null, LongRunningOperation? operation = null, TimeProvider? time = null)
    {
        if (plan.WriteBlocked || ImageForensics.Hooks.IsWriteBlocked)
        {
            throw new InvalidOperationException("Write-block mode is on; sector ranges cannot be restored.");
        }

        if (!plan.CanRun)
        {
            throw new InvalidOperationException("The restore plan cannot be run.");
        }

        time ??= TimeProvider.System;
        int sector = plan.Target.SectorSize;
        long offset = plan.StartLba * sector;
        long length = plan.Bytes;
        operation?.SetTotal(length * (backup ? 3 : 2));
        string? backupPath = null;
        if (backup)
        {
            string stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            backupPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(binPath))!, Path.GetFileNameWithoutExtension(binPath) + ".before-" + stamp + ".bin");
            var info = new AcquisitionSourceInfo
            {
                Path = plan.Target.Path,
                Identity = plan.Target.Identity,
                Model = plan.Target.Model,
                SerialNumber = plan.Target.SerialNumber,
                Capacity = plan.Target.Capacity,
                SectorSize = sector,
            };
            Save(target, info, plan.StartLba, plan.SectorCount, backupPath, null, time);
            operation?.Report(length);
        }

        Audit("sectors.restore", plan.Target.Path, plan.Target.Identity, binPath, plan.StartLba, plan.SectorCount, ImageAuditResult.Started, null, null);
        IDisposable? locks = lockDevice is null || plan.VolumesToLock.Count == 0 ? null : DiskWrite.LockVolumes(lockDevice, plan.VolumesToLock, confirmDismount);
        long written = 0;
        try
        {
            using FileStream data = File.OpenRead(binPath);
            byte[] buffer = new byte[1 << 20];
            for (; written < length;)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - written);
                data.ReadExactly(buffer, 0, n);
                if (!WriteWithRetry(target, offset + written, buffer.AsSpan(0, n), sector, out long failedAt))
                {
                    long failedLba = failedAt / sector;
                    Audit("sectors.restore", plan.Target.Path, plan.Target.Identity, binPath, plan.StartLba, plan.SectorCount, ImageAuditResult.Failure, null,
                        $"write failed at LBA {failedLba}");
                    return new SectorRestoreResult { WrittenBytes = failedAt - offset, FailedLba = failedLba, BackupPath = backupPath, Message = "write failed" };
                }

                written += n;
                operation?.Report((backup ? length : 0) + written);
            }

            Flush(target);
        }
        finally
        {
            locks?.Dispose();
        }

        // 書き込み後に読み直してデータファイルと比べる (仕様 5)。
        bool verified;
        using (FileStream data = File.OpenRead(binPath))
        {
            byte[] a = new byte[1 << 20];
            byte[] b = new byte[1 << 20];
            verified = true;
            for (long done = 0; done < length && verified; done += a.Length)
            {
                int n = (int)Math.Min(a.Length, length - done);
                data.ReadExactly(a, 0, n);
                verified = target.Read(offset + done, b.AsSpan(0, n)).IsComplete && a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n));
                operation?.Report((backup ? 2 * length : length) + done + n);
            }
        }

        Audit("sectors.restore", plan.Target.Path, plan.Target.Identity, binPath, plan.StartLba, plan.SectorCount,
            verified ? ImageAuditResult.Success : ImageAuditResult.Failure, null, verified ? null : "verification after writing failed");
        return new SectorRestoreResult { Success = verified, WrittenBytes = written, Verified = verified, BackupPath = backupPath };
    }

    /// <summary>書き込む (失敗したら 1 回だけ再試行)。失敗したら false と、失敗した位置。</summary>
    internal static bool WriteWithRetry(IByteSource target, long offset, ReadOnlySpan<byte> data, int sector, out long failedAt)
    {
        failedAt = offset;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                target.Write(offset, data);
                return true;
            }
            catch (DeviceWriteException ex)
            {
                failedAt = ex.Offset / sector * sector;
            }
            catch (Exception ex) when (ex is IOException or DeviceException or UnauthorizedAccessException)
            {
                failedAt = offset;
            }
        }

        return false;
    }

    internal static void Flush(IByteSource target)
    {
        if (target is DeviceByteSource device)
        {
            device.Flush();
        }
    }

    private static void Audit(string operation, string target, string? identity, string file, long startLba, long count, ImageAuditResult result, string? sha256,
        string? message)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["file"] = file,
            ["startLba"] = startLba.ToString(CultureInfo.InvariantCulture),
            ["sectorCount"] = count.ToString(CultureInfo.InvariantCulture),
        };
        if (sha256 is not null)
        {
            parameters["hash.SHA-256"] = sha256;
        }

        ImageForensics.Hooks.Audit(new ImageAuditRecord
        {
            Operation = operation,
            TargetPath = target,
            TargetIdentity = identity,
            Parameters = parameters,
            Result = result,
            Message = message,
        });
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
