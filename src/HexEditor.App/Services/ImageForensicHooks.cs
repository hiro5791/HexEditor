using System.Collections.Concurrent;
using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Time;
using HexEditor.Core.Forensics.WriteBlock;

namespace HexEditor.App.Services;

/// <summary>
/// イメージの機能 (12 の FOR-20〜26) がほかの領域の機能を使う口の、アプリの実装。書き込み保護モード (FOR-01) は Core の
/// <see cref="WriteBlockGate"/> と <see cref="ForensicsService.IsWriteBlockEnabled"/>、ケース (FOR-03)・証拠一覧・監査ログ (FOR-04) は
/// <see cref="ForensicsService"/> (<see cref="ForensicsService.Cases"/>、<see cref="ForensicsService.Audit"/>) を使う。
/// </summary>
internal sealed class AppImageForensicHooks : IImageForensicHooks
{
    /// <summary>開始を記録した長い操作 (終了の行に phase=end を付ける)。キーは操作名と対象。</summary>
    private readonly ConcurrentDictionary<(string Operation, string Target), byte> _started = new();

    public bool IsWriteBlocked => WriteBlockGate.Current.IsEnabled || ForensicsService.IsWriteBlockEnabled();

    public ImageCaseInfo? Case => ForensicsService.Cases.CurrentCase is { } c
        ? new ImageCaseInfo
        {
            CaseNumber = c.Info.Number,
            CaseName = c.Info.Name,
            Examiner = c.Info.Investigator,
            Organization = c.Info.Organization,
            Folder = c.Folder,
            TimeZone = ForensicTimeContext.ResolveZone(c.Info.TimeZoneId),
        }
        : null;

    /// <summary>
    /// 証拠のディスクの番号。証拠のボリュームがあるディスクも含める (証拠のボリューム E: のあるディスクへのクローン・出力を拒否するため)。
    /// </summary>
    public IReadOnlyList<int> EvidenceDiskNumbers
    {
        get
        {
            IReadOnlyList<EvidenceItem> evidence = ForensicsService.Cases.ActiveEvidence;
            var disks = new HashSet<int>(evidence.Where(e => e.Kind == EvidenceKind.PhysicalDisk).Select(e => DiskNumberOf(e.Path)).OfType<int>());
            string[] volumes = [.. evidence.Where(e => e.Kind == EvidenceKind.Volume).Select(e => e.Path)];
            if (volumes.Length > 0)
            {
                var locator = new CatalogDiskLocator(() => App.Devices.EnumerateDevices());
                foreach (string v in volumes)
                {
                    try
                    {
                        disks.UnionWith(locator.DisksOfDevice(v) ?? []);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                    }
                }
            }

            return [.. disks];
        }
    }

    public string? EvidenceNumberOf(string pathOrIdentity) => FindEvidence(pathOrIdentity)?.Id;

    public void Audit(ImageAuditRecord record)
    {
        string operation = OperationName(record.Operation);
        var parameters = new List<KeyValuePair<string, string>>(record.Parameters);
        if (record.TargetIdentity is { Length: > 0 } identity)
        {
            parameters.Add(new("identity", identity));
        }

        var key = (operation, record.TargetPath);
        if (record.Result == ImageAuditResult.Started)
        {
            // 書き込みを伴う操作は、始める前に監査ログに書けることを確かめる (書けなければ AuditWriteException で操作を止める。FOR-04 の「エラー」)。
            ForensicsService.Audit.EnsureCanWrite();
            parameters.Add(new("phase", "start"));
            _started[key] = 0;
        }
        else if (_started.TryRemove(key, out _))
        {
            parameters.Add(new("phase", "end"));
        }

        ForensicsService.Audit.Record(new AuditRecord(operation)
        {
            EvidenceId = FindEvidence(record.TargetPath)?.Id ?? (record.TargetIdentity is { } id ? FindEvidence(id)?.Id : null),
            Path = record.TargetPath,
            Parameters = parameters,
            Result = record.Result switch
            {
                ImageAuditResult.Failure => AuditResult.Failure,
                ImageAuditResult.Cancelled => AuditResult.Cancelled,
                _ => AuditResult.Success,
            },
            Message = record.Message ?? string.Empty,
        });
    }

    public void RegisterAcquiredImage(string imagePath, string sourcePath, IReadOnlyDictionary<string, byte[]> hashes)
    {
        // レポートの「取得記録」の節 (FOR-33)。取得ログは出力と同じ名前の .txt (FOR-20 の仕様 5)。
        string log = Path.ChangeExtension(imagePath, ".txt");
        ForensicsService.AcquisitionReports.Add(new Core.Forensics.Reports.AcquiredImageRecord(imagePath, sourcePath,
            hashes.ToDictionary(h => h.Key, h => Convert.ToHexStringLower(h.Value), StringComparer.Ordinal), ForensicsService.Clock.GetUtcNow().UtcDateTime,
            File.Exists(log) ? log : null));

        if (ForensicsService.Cases.CurrentCase is not { } c)
        {
            return;
        }

        EvidenceHash[] list = [.. hashes.Select(h => new EvidenceHash(h.Key, Convert.ToHexStringLower(h.Value)))];
        if (c.FindBySource(EvidenceKind.ImageFile, imagePath) is { } existing)
        {
            ForensicsService.Cases.RecordHashes(existing.Id, list);
            return;
        }

        try
        {
            ForensicsService.Cases.Register(new EvidenceRegistration
            {
                Kind = EvidenceKind.ImageFile,
                Path = imagePath,
                Description = Path.GetFileName(imagePath),
                Hashes = list,
                DerivedFrom = FindEvidence(sourcePath)?.Id,
            });
        }
        catch (DuplicateEvidenceException)
        {
            // 同時に登録された (取得のハッシュは上の分岐と同じく記録済み)。
        }
    }

    public IReadOnlyDictionary<string, byte[]> EvidenceHashesOf(string pathOrIdentity)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (FindEvidence(pathOrIdentity) is { } item)
        {
            foreach (EvidenceHash h in item.Hashes)
            {
                if (ImageHashes.TryNormalize(h.Algorithm, out string name))
                {
                    try
                    {
                        result[name] = Convert.FromHexString(h.Value);
                    }
                    catch (FormatException)
                    {
                        // 壊れた値は比べない。
                    }
                }
            }
        }

        return result;
    }

    public void RecordVerification(string pathOrIdentity, VerificationResult result)
    {
        if (FindEvidence(pathOrIdentity) is { } item && result.Overall is VerificationVerdict.Match or VerificationVerdict.Mismatch or VerificationVerdict.ReadError)
        {
            ForensicsService.Cases.RecordVerification(item.Id, result.Overall == VerificationVerdict.Match);
        }
    }

    /// <summary>監査ログの操作名 (AuditOperations の名前にそろえる)。</summary>
    internal static string OperationName(string operation) => operation switch
    {
        "image.create" => AuditOperations.ImageCreate,
        "image.verify" => AuditOperations.ImageVerify,
        "disk.clone" => AuditOperations.Clone,
        "sectors.restore" => AuditOperations.Restore,
        "sectors.save" => AuditOperations.FileExport,
        _ => operation,
    };

    private static EvidenceItem? FindEvidence(string pathOrIdentity) =>
        string.IsNullOrEmpty(pathOrIdentity) ? null
        : ForensicsService.Cases.ActiveEvidence.FirstOrDefault(e => string.Equals(Normalize(e.Path), Normalize(pathOrIdentity), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary><c>\\.\PhysicalDriveN</c> の N。</summary>
    internal static int? DiskNumberOf(string path)
    {
        const string prefix = @"\\.\PhysicalDrive";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(path.AsSpan(prefix.Length), out int n) ? n : null;
    }

    /// <summary>アプリの起動時に設定する。</summary>
    public static AppImageForensicHooks Install()
    {
        var hooks = new AppImageForensicHooks();
        ImageForensics.Hooks = hooks;
        return hooks;
    }
}
