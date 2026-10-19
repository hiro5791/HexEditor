using System.Globalization;
using HexEditor.Core.Devices;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images;

/// <summary>クローンを実行できない理由 (FOR-24 の仕様 1・2・6)。</summary>
public enum CloneProblem
{
    None,

    /// <summary>出力先が入力元と同じディスク。</summary>
    SameDevice,

    /// <summary>セクタサイズが違う (「セクタサイズが異なるディスクにはクローンできません」)。</summary>
    SectorSizeMismatch,

    /// <summary>書き込み保護モード中。</summary>
    WriteBlocked,

    /// <summary>出力先が証拠一覧に登録されたディスク。</summary>
    TargetIsEvidence,

    /// <summary>出力先にシステムボリュームなど、Windows が使用中でロックできないボリュームがある。</summary>
    TargetInUseByWindows,

    /// <summary>開始 LBA が出力先の外。</summary>
    StartOutside,

    /// <summary>出力先を書き込みのアクセス権で開いていない。</summary>
    TargetNotWritable,
}

/// <summary>クローンの計画 (確認ダイアログの本文の元)。</summary>
public sealed record ClonePlan
{
    public required IByteSource Source { get; init; }

    public required AcquisitionSourceInfo SourceInfo { get; init; }

    /// <summary>入力元の範囲 (バイト)。</summary>
    public long SourceStart { get; init; }

    public long SourceLength { get; init; }

    public required DeviceByteSource Target { get; init; }

    public DiskInfo? TargetDisk { get; init; }

    public long TargetStartLba { get; init; }

    /// <summary>入力元から書き込むバイト数 (出力先が小さければ先頭のこのバイト数だけ)。</summary>
    public long BytesToWrite { get; init; }

    /// <summary>出力先が入力元より小さい (「入力元の先頭 N バイトだけを書き込みます」の警告)。</summary>
    public bool Truncated { get; init; }

    /// <summary>出力先の残りの領域を 0 で埋めるバイト数 (オプション)。</summary>
    public long ZeroFillBytes { get; init; }

    /// <summary>出力先のディスクのすべてのボリューム (内容がすべて失われる。ロックの対象)。</summary>
    public IReadOnlyList<VolumeDeviceInfo> TargetVolumes { get; init; } = [];

    public CloneProblem Problem { get; init; }
}

/// <summary>クローンのオプション (FOR-24 の仕様 3)。</summary>
public sealed record CloneOptions
{
    public int Retries { get; init; } = 3;

    public byte Fill { get; init; }

    public bool ZeroFillRest { get; init; }

    public bool Verify { get; init; } = true;

    /// <summary>検証のハッシュ (既定 SHA-256)。</summary>
    public string Hash { get; init; } = ImageHashes.Sha256;

    /// <summary>ログ (取得ログと同じ形式) の出力先。null なら書かない (既定はケースの reports/。UI が決める)。</summary>
    public string? LogPath { get; init; }

    public AcquisitionLogText LogText { get; init; } = new();

    public string AppVersion { get; init; } = string.Empty;

    public int ReadUnit { get; init; } = 1024 * 1024;
}

/// <summary>クローンの結果。</summary>
public sealed record CloneResult
{
    public AcquisitionStatus Status { get; init; }

    public long BytesWritten { get; init; }

    public IReadOnlyList<SectorRange> BadSectors { get; init; } = [];

    public byte[]? SourceHash { get; init; }

    public byte[]? TargetHash { get; init; }

    public VerificationVerdict? Verification { get; init; }

    /// <summary>書き込みに失敗した出力先の位置 (バイト)。</summary>
    public long? FailedOffset { get; init; }

    public string? LogPath { get; init; }

    public string? Message { get; init; }
}

/// <summary>ディスクのクローン (FOR-24)。出力先はデバイス (偽のデバイスを含む) だけ。</summary>
public static class DiskClone
{
    /// <summary>計画を作る (仕様 1・2・6)。</summary>
    public static ClonePlan Plan(IByteSource source, AcquisitionSourceInfo info, long sourceStart, long sourceLength, DeviceByteSource target,
        long targetStartLba, bool zeroFillRest, DeviceCatalog catalog)
    {
        DiskInfo? disk = target.Info.Disk;
        IReadOnlyList<VolumeDeviceInfo> volumes = disk is null ? [] : [.. catalog.Volumes.Where(v => v.DiskNumber == disk.Number)];
        int sector = target.LogicalSectorSize;
        long start = targetStartLba * sector;
        long room = target.Length - start;
        long bytes = Math.Min(sourceLength, Math.Max(0, room));
        CloneProblem problem =
            ImageForensics.Hooks.IsWriteBlocked ? CloneProblem.WriteBlocked
            : string.Equals(info.Identity, target.Identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.Path, target.Path, StringComparison.OrdinalIgnoreCase)
                || (disk is not null && info.DiskNumber == disk.Number) ? CloneProblem.SameDevice
            : ImageForensics.Hooks.EvidenceNumberOf(target.Path) is not null || ImageForensics.Hooks.EvidenceNumberOf(target.Identity) is not null
                || (disk is not null && ImageForensics.Hooks.EvidenceDiskNumbers.Contains(disk.Number)) ? CloneProblem.TargetIsEvidence
            : info.SectorSize != sector ? CloneProblem.SectorSizeMismatch
            : targetStartLba < 0 || room <= 0 ? CloneProblem.StartOutside
            : volumes.Any(v => v.IsInUseByWindows) ? CloneProblem.TargetInUseByWindows
            : !target.Handle.Writable ? CloneProblem.TargetNotWritable
            : CloneProblem.None;
        return new ClonePlan
        {
            Source = source,
            SourceInfo = info,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            Target = target,
            TargetDisk = disk,
            TargetStartLba = targetStartLba,
            BytesToWrite = bytes,
            Truncated = sourceLength > bytes,
            ZeroFillBytes = zeroFillRest ? Math.Max(0, room - bytes) : 0,
            TargetVolumes = volumes,
            Problem = problem,
        };
    }

    /// <summary>
    /// クローンを実行する (仕様 3〜7): 出力先のディスクのすべてのボリュームをロックし (処理の間保持する)、読み込みエラー時は FOR-20 と同じ動作で読み、
    /// 書き込みの失敗は 1 回だけ再試行して、だめならその位置を示して中止する。完了後に入力元と出力先の書き込んだ範囲のハッシュを比べる。
    /// </summary>
    /// <exception cref="VolumeLockException">ロックできないボリュームがある (何も書かない)。</exception>
    public static async Task<CloneResult> RunAsync(ClonePlan plan, CloneOptions options, LongRunningOperation? operation = null,
        Func<VolumeDeviceInfo, bool>? confirmDismount = null, TimeProvider? time = null)
    {
        if (plan.Problem != CloneProblem.None || ImageForensics.Hooks.IsWriteBlocked)
        {
            throw new InvalidOperationException("The clone cannot run: " + (plan.Problem == CloneProblem.None ? CloneProblem.WriteBlocked : plan.Problem));
        }

        await Task.Yield();
        time ??= TimeProvider.System;
        DateTimeOffset started = time.GetUtcNow();
        int sector = plan.Target.LogicalSectorSize;
        long targetStart = plan.TargetStartLba * sector;
        CancellationToken token = operation?.CancellationToken ?? CancellationToken.None;
        operation?.SetTotal(plan.BytesToWrite * (options.Verify ? 2 : 1) + plan.ZeroFillBytes);
        Audit(plan, ImageAuditResult.Started, null, null);

        // ロックできないボリュームがあれば実行しない (仕様 5。ここで例外にし、出力先には何も書かない)。
        IDisposable locks = DiskWrite.LockVolumes(plan.Target, plan.TargetVolumes, confirmDismount);
        var bad = new List<SectorRange>();
        using var sourceHash = new ImageHashes([options.Hash]);
        long written = 0;
        AcquisitionStatus status = AcquisitionStatus.Completed;
        long? failedAt = null;
        string? message = null;
        try
        {
            byte[] buffer = new byte[Math.Max(sector, options.ReadUnit / sector * sector)];
            ImageAcquisition.ReadFunc read = plan.Source.Read;
            while (written < plan.BytesToWrite)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, plan.BytesToWrite - written);
                if (!ImageAcquisition.RecoveringRead(read, plan.SourceStart + written, buffer.AsSpan(0, n), sector, options.Retries, options.Fill,
                    plan.SourceStart, bad, token))
                {
                    status = AcquisitionStatus.SourceLost;
                    message = options.LogText.StatusSourceLost;
                    break;
                }

                sourceHash.Append(buffer.AsSpan(0, n));
                if (!SectorRangeArchive.WriteWithRetry(plan.Target, targetStart + written, buffer.AsSpan(0, n), sector, out long failed))
                {
                    status = AcquisitionStatus.Failed;
                    failedAt = failed;
                    message = string.Create(CultureInfo.InvariantCulture, $"write failed at LBA {failed / sector}");
                    break;
                }

                written += n;
                operation?.Report(written);
            }

            if (status == AcquisitionStatus.Completed && plan.ZeroFillBytes > 0)
            {
                Array.Clear(buffer);
                for (long done = 0; done < plan.ZeroFillBytes;)
                {
                    token.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, plan.ZeroFillBytes - done);
                    if (!SectorRangeArchive.WriteWithRetry(plan.Target, targetStart + written + done, buffer.AsSpan(0, n), sector, out long failed))
                    {
                        status = AcquisitionStatus.Failed;
                        failedAt = failed;
                        message = string.Create(CultureInfo.InvariantCulture, $"write failed at LBA {failed / sector}");
                        break;
                    }

                    done += n;
                    operation?.Report(written + done);
                }
            }

            plan.Target.Flush();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = AcquisitionStatus.Cancelled;
            try
            {
                plan.Target.Flush();
            }
            catch (Exception ex) when (ex is IOException or DeviceException)
            {
            }
        }
        finally
        {
            locks.Dispose();
        }

        byte[] sourceValue = sourceHash.Finish()[ImageHashes.Normalize(options.Hash)];
        byte[]? targetValue = null;
        VerificationVerdict? verdict = null;
        if (status == AcquisitionStatus.Completed && options.Verify)
        {
            using var targetHash = new ImageHashes([options.Hash]);
            byte[] buffer = new byte[1 << 20];
            bool readError = false;
            for (long done = 0; done < plan.BytesToWrite; done += buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, plan.BytesToWrite - done);
                readError |= !plan.Target.Read(targetStart + done, buffer.AsSpan(0, n)).IsComplete;
                targetHash.Append(buffer.AsSpan(0, n));
                operation?.Report(plan.BytesToWrite + plan.ZeroFillBytes + done + n);
            }

            targetValue = targetHash.Finish()[ImageHashes.Normalize(options.Hash)];
            verdict = readError ? VerificationVerdict.ReadError : targetValue.AsSpan().SequenceEqual(sourceValue) ? VerificationVerdict.Match : VerificationVerdict.Mismatch;
        }

        var result = new CloneResult
        {
            Status = status,
            BytesWritten = written,
            BadSectors = ImageAcquisition.Merge(bad),
            SourceHash = sourceValue,
            TargetHash = targetValue,
            Verification = verdict,
            FailedOffset = failedAt,
            LogPath = options.LogPath,
            Message = message,
        };
        if (options.LogPath is not null)
        {
            WriteLog(plan, options, result, started, time.GetUtcNow());
        }

        Audit(plan, status switch
        {
            AcquisitionStatus.Completed => ImageAuditResult.Success,
            AcquisitionStatus.Cancelled => ImageAuditResult.Cancelled,
            _ => ImageAuditResult.Failure,
        }, result, message);
        return result;
    }

    private static void WriteLog(ClonePlan plan, CloneOptions options, CloneResult result, DateTimeOffset started, DateTimeOffset ended)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (result.SourceHash is not null)
        {
            hashes[ImageHashes.Normalize(options.Hash)] = result.SourceHash;
        }

        var data = new AcquisitionLogData
        {
            IsClone = true,
            Status = result.Status,
            Source = plan.SourceInfo,
            RangeStart = plan.SourceStart,
            RangeLength = plan.BytesToWrite,
            FormatName = string.Create(CultureInfo.InvariantCulture, $"clone to LBA {plan.TargetStartLba}"),
            Files = [plan.Target.Path + (plan.TargetDisk is { } d ? $" ({d.Model} {d.SerialNumber})" : string.Empty)],
            Started = started,
            Ended = ended,
            SectorsRead = plan.BytesToWrite / Math.Max(1, plan.Target.LogicalSectorSize),
            Unreadable = result.BadSectors,
            Hashes = hashes,
            Verification = result.Verification,
            AppVersion = options.AppVersion,
            CaseFolder = ImageForensics.Hooks.Case,
            Message = result.Message,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogPath!))!);
            AcquisitionLog.Write(options.LogPath!, data, options.LogText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Audit(ClonePlan plan, ImageAuditResult result, CloneResult? r, string? message)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = plan.SourceInfo.Path,
            ["sourceStart"] = plan.SourceStart.ToString(CultureInfo.InvariantCulture),
            ["bytes"] = plan.BytesToWrite.ToString(CultureInfo.InvariantCulture),
            ["targetStartLba"] = plan.TargetStartLba.ToString(CultureInfo.InvariantCulture),
            ["zeroFill"] = plan.ZeroFillBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (r?.SourceHash is { } h)
        {
            parameters["hash.source"] = Convert.ToHexStringLower(h);
        }

        if (r?.TargetHash is { } t)
        {
            parameters["hash.target"] = Convert.ToHexStringLower(t);
        }

        if (r?.Verification is { } v)
        {
            parameters["verification"] = v.ToString();
        }

        ImageForensics.Hooks.Audit(new ImageAuditRecord
        {
            Operation = "disk.clone",
            TargetPath = plan.Target.Path,
            TargetIdentity = plan.Target.Identity,
            Parameters = parameters,
            Result = result,
            Message = message,
        });
    }
}
