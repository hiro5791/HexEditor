using System.Globalization;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Wiping;

/// <summary>消去の種類 (監査ログの項目)。</summary>
public enum WipeKind
{
    /// <summary>選択範囲 (FOR-30)。</summary>
    Range,

    /// <summary>ディスク全体 (FOR-30)。</summary>
    Disk,

    /// <summary>空き領域の直接書き込み (FOR-31)。</summary>
    FreeSpaceDirect,

    /// <summary>空き領域の OS 経由 (FOR-31)。</summary>
    FreeSpaceOs,

    /// <summary>スラック (FOR-31)。</summary>
    Slack,
}

/// <summary>
/// 消去 1 回の内容 (確認の後に実行する)。<see cref="Ranges"/> は書き込み先 (<see cref="IWipeTarget"/>) の先頭からの位置で、回ごとに列挙し直す。
/// </summary>
public sealed record WipeJob
{
    public required WipeKind Kind { get; init; }

    /// <summary>対象の表示名 (監査ログ)。</summary>
    public required string TargetName { get; init; }

    /// <summary>データソースの識別子 (証拠かの判定)。</summary>
    public required string Identity { get; init; }

    /// <summary>ファイル・デバイスのパス (証拠かの判定)。</summary>
    public string? Path { get; init; }

    public required IEnumerable<WipeRange> Ranges { get; init; }

    /// <summary>範囲の合計 (省くと数える)。</summary>
    public long? TotalBytes { get; init; }

    public required ShredMethod Method { get; init; }

    public bool Verify { get; init; }

    /// <summary>書き込みの区切りの単位 (直接書き込みはクラスタサイズ)。</summary>
    public int ChunkAlignment { get; init; } = 1;

    /// <summary>監査ログに加える項目 (ファイルシステム、ボリュームなど)。</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 消去の実行の手順 (FOR-30・FOR-31 で共通): 書き込み保護モード・証拠・証拠管理の拒否を確かめる (拒否も監査ログに記録する) →
/// ボリュームをロックする → 消去する → ロックを解除する → 結果を監査ログに記録する (成功・失敗・キャンセルにかかわらず)。
/// </summary>
public static class WipeRunner
{
    /// <summary>書き込み保護モード・証拠・証拠管理を確かめる。拒否なら理由、よければ <see cref="WipeRefusal.None"/>。</summary>
    public static WipeRefusal Check(string identity, string? path, string target, out string? detail, IWipeForensicHooks? hooks = null)
    {
        hooks ??= WipeForensics.Hooks;
        detail = null;
        if (hooks.IsWriteBlockEnabled)
        {
            return WipeRefusal.WriteBlock;
        }

        if (hooks.IsEvidence(identity, path))
        {
            return WipeRefusal.Evidence;
        }

        if (hooks.EnsureCanWrite(WipeAuditOperations.Wipe, target) is { } reason)
        {
            detail = reason;
            return WipeRefusal.PolicyDenied;
        }

        return WipeRefusal.None;
    }

    /// <summary>監査ログの項目 (方式、回数、範囲)。</summary>
    public static Dictionary<string, string> AuditDetails(WipeJob job)
    {
        var details = new Dictionary<string, string>(job.Details)
        {
            ["kind"] = job.Kind.ToString(),
            ["method"] = job.Method.Id,
            ["passes"] = job.Method.Passes.Count.ToString(CultureInfo.InvariantCulture),
            ["patterns"] = string.Join(" / ", job.Method.Passes.Select(p => p.Pattern is { } b ? Convert.ToHexString(b) : "random")),
            ["verify"] = job.Verify ? "true" : "false",
        };
        if (job.TotalBytes is long total)
        {
            details["bytes"] = total.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }

    /// <summary>
    /// 消去する。拒否なら <see cref="WipeRefusedException"/> (監査ログに「拒否」を記録する)。<paramref name="lockVolumes"/> はロックを取る処理
    /// (失敗すると <see cref="VolumeLockException"/>)。<paramref name="afterWrite"/> は開いているドキュメントのキャッシュを捨てて読み直す処理
    /// (成功・失敗・キャンセルのどれでも、書き込んだ後に呼ぶ。FOR-30 の仕様 6)。
    /// </summary>
    public static WipeResult Run(WipeJob job, Func<IWipeTarget> openTarget, Func<IDisposable?>? lockVolumes = null, LongRunningOperation? operation = null,
        Func<WipeWriteError, WipeErrorChoice>? onError = null, Action<WipeProgress>? progress = null, Action? afterWrite = null,
        IWipeForensicHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        hooks ??= WipeForensics.Hooks;
        Dictionary<string, string> details = AuditDetails(job);
        WipeRefusal refusal = Check(job.Identity, job.Path, job.TargetName, out string? reason, hooks);
        if (refusal != WipeRefusal.None)
        {
            details["refusal"] = refusal.ToString();
            hooks.Audit(WipeAuditOperations.Wipe, job.TargetName, WipeAuditOutcome.Refused, details);
            throw new WipeRefusedException(refusal, reason);
        }

        WipeResult? result = null;
        try
        {
            using IDisposable? locks = lockVolumes?.Invoke();
            using IWipeTarget target = openTarget();
            try
            {
                result = WipeEngine.Execute(target, job.Ranges, job.Method, job.Verify, operation, onError, progress, cancellationToken, job.ChunkAlignment,
                    job.TotalBytes);
            }
            finally
            {
                afterWrite?.Invoke();
            }
        }
        catch (Exception ex)
        {
            details["error"] = ex.Message;
            hooks.Audit(WipeAuditOperations.Wipe, job.TargetName, WipeAuditOutcome.Failed, details);
            throw;
        }

        details["bytes"] = result.TotalBytes.ToString(CultureInfo.InvariantCulture);
        details["written"] = result.BytesWritten.ToString(CultureInfo.InvariantCulture);
        details["elapsedSeconds"] = result.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        if (result.Unwritten.Count > 0)
        {
            details["unwritten"] = string.Join(";", result.Unwritten.Take(100).Select(r => $"0x{r.Offset:X}+{r.Length}"));
        }

        if (result.Verified)
        {
            details["verification"] = result.Mismatches.Count == 0 ? "ok" : "mismatch:" + string.Join(";", result.Mismatches.Take(100).Select(r => $"0x{r.Offset:X}+{r.Length}"));
        }

        if (result.Cancelled || result.Aborted)
        {
            details["stoppedAt"] = result.StoppedAt.ToString(CultureInfo.InvariantCulture);
            details["stoppedPass"] = result.StoppedPass.ToString(CultureInfo.InvariantCulture);
        }

        hooks.Audit(WipeAuditOperations.Wipe, job.TargetName,
            result.Cancelled ? WipeAuditOutcome.Cancelled : result.Succeeded ? WipeAuditOutcome.Succeeded : WipeAuditOutcome.Failed, details);
        return result;
    }

    /// <summary>OS 経由の空き領域の消去 (拒否・監査ログの手順は <see cref="Run"/> と同じ)。</summary>
    public static OsFreeSpaceWipeResult RunOs(IFreeSpaceVolume volume, string targetName, string identity, ShredMethod method,
        LongRunningOperation? operation = null, Action<WipeProgress>? progress = null, IWipeForensicHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        hooks ??= WipeForensics.Hooks;
        var details = new Dictionary<string, string>
        {
            ["kind"] = WipeKind.FreeSpaceOs.ToString(),
            ["method"] = method.Id,
            ["passes"] = method.Passes.Count.ToString(CultureInfo.InvariantCulture),
            ["volume"] = volume.Root,
            ["fileSystem"] = volume.FileSystem,
        };
        WipeRefusal refusal = Check(identity, volume.Root, targetName, out string? reason, hooks);
        if (refusal != WipeRefusal.None)
        {
            details["refusal"] = refusal.ToString();
            hooks.Audit(WipeAuditOperations.Wipe, targetName, WipeAuditOutcome.Refused, details);
            throw new WipeRefusedException(refusal, reason);
        }

        OsFreeSpaceWipeResult result = OsFreeSpaceWipe.Execute(volume, method, operation, progress, cancellationToken);
        details["bytes"] = result.BytesOverwritten.ToString(CultureInfo.InvariantCulture);
        details["written"] = result.BytesWritten.ToString(CultureInfo.InvariantCulture);
        details["smallFiles"] = result.SmallFiles.ToString(CultureInfo.InvariantCulture);
        details["elapsedSeconds"] = result.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        if (result.Leftovers.Count > 0)
        {
            details["leftovers"] = string.Join(";", result.Leftovers.Take(20));
        }

        if (result.Error is { } error)
        {
            details["error"] = error;
        }

        hooks.Audit(WipeAuditOperations.Wipe, targetName,
            result.Cancelled ? WipeAuditOutcome.Cancelled : result.Error is null && result.Leftovers.Count == 0 ? WipeAuditOutcome.Succeeded : WipeAuditOutcome.Failed,
            details);
        return result;
    }

    /// <summary>
    /// 範囲に未保存の編集があるか (FOR-30 の仕様 7: 消去前に「保存」「破棄」を選ばせる)。<paramref name="offset"/> はドキュメント上の位置。
    /// </summary>
    public static bool HasUnsavedEditsIn(Document document, long offset, long length) =>
        document.IsModified && (document.Current.EnumerateModifiedRanges(offset, length).Any()
            || document.LastDeviceWriteRanges.Any(r => r.Offset < offset + length && offset < r.Offset + r.Length));

    /// <summary>ドキュメントのどこかに未保存の編集があるか (空き領域・スラックはボリューム全体が対象)。</summary>
    public static bool HasUnsavedEdits(Document document) => document.IsModified;
}

/// <summary>範囲の消去 (FOR-30) の範囲の決め方。</summary>
public static class RangeWipe
{
    /// <summary>セクタ境界に広げる (仕様 1: ディスクではセクタ境界に広げ、広げた範囲を確認ダイアログに示す)。</summary>
    public static WipeRange Widen(WipeRange range, int sectorSize, long length)
    {
        if (sectorSize <= 1)
        {
            return range;
        }

        long from = range.Offset / sectorSize * sectorSize;
        long to = Math.Min((range.End + sectorSize - 1) / sectorSize * sectorSize, length);
        return new WipeRange(from, to - from);
    }

    /// <summary>
    /// ディスクがシステム・ブートのディスクか (Windows が使用中のボリュームを含む)。消去しない (システムディスクは対象外)。
    /// </summary>
    public static bool IsSystemDisk(DiskInfo? disk, DeviceCatalog catalog) =>
        disk is not null && (disk.IsSystem == true || catalog.Volumes.Any(v => v.DiskNumber == disk.Number && v.IsInUseByWindows));

    /// <summary>SSD・フラッシュメディアと分かるか (仕様 5: 確認ダイアログに上書きの限界を示す)。</summary>
    public static bool IsFlashMedia(DiskInfo? disk) =>
        disk is not null && (disk.Bus is DiskBusType.Nvme or DiskBusType.Sd or DiskBusType.Mmc || disk.Bus == DiskBusType.Usb && disk.Removable == true);
}
