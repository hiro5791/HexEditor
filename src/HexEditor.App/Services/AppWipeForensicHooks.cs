using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.Wiping;

namespace HexEditor.App.Services;

/// <summary>
/// 消去・墨塗り (12 の FOR-30〜32) の、書き込み保護モード・証拠・監査ログへの口 (<see cref="IWipeForensicHooks"/>) のアプリの実装。
/// 書き込み保護モードは Core の <see cref="WriteBlockGate"/> と <see cref="ForensicsService.IsWriteBlockEnabled"/> (FOR-01)、証拠の判定は
/// <see cref="ForensicsService.Cases"/> (FOR-03)、書き込みの可否と記録は <see cref="ForensicsService.Audit"/> (FOR-04 の EnsureCanWrite と
/// write.wipe / write.redact)、出力先の検査は <see cref="OutputGuard"/> を使う。
/// </summary>
internal sealed class AppWipeForensicHooks : IWipeForensicHooks
{
    public static AppWipeForensicHooks Instance { get; } = new();

    private AppWipeForensicHooks()
    {
        WriteBlockMode.Changed += (_, _) => WriteBlockChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsWriteBlockEnabled => WriteBlockGate.Current.IsEnabled || ForensicsService.IsWriteBlockEnabled();

    /// <summary>
    /// 対象が証拠に当たるか。証拠のボリュームを含むディスク・証拠のディスクの中のボリューム・別の書き方のパスも証拠とみなす
    /// (<see cref="EvidenceOverlap"/>)。
    /// </summary>
    public bool IsEvidence(string identity, string? path)
    {
        IReadOnlyList<Core.Forensics.Cases.EvidenceItem> evidence = [.. ForensicsService.Cases.ActiveEvidence];
        if (evidence.Count == 0)
        {
            return false;
        }

        Core.Devices.DeviceCatalog? catalog = null;
        bool enumerated = false;
        Core.Devices.DeviceCatalog? Catalog()
        {
            if (!enumerated)
            {
                enumerated = true;
                try
                {
                    catalog = App.Devices?.EnumerateDevices();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    catalog = null;
                }
            }

            return catalog;
        }

        return evidence.Any(e => EvidenceOverlap.Overlaps(e.Path, identity, Catalog) || (path is not null && EvidenceOverlap.Overlaps(e.Path, path, Catalog)));
    }

    public string? EnsureCanWrite(string operation, string target)
    {
        try
        {
            ForensicsService.Audit.EnsureCanWrite();
            return null;
        }
        catch (AuditWriteException)
        {
            return Loc.Get("Audit_WritesDisabled");
        }
    }

    public string? CheckOutput(string path)
    {
        IReadOnlyList<string> devices = ForensicsService.Cases.EvidenceDevicePaths;
        if (devices.Count == 0 || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } folder)
        {
            return null;
        }

        OutputCheck check = OutputGuard.Check(folder, devices, 0, 0, new CatalogDiskLocator(() => App.Devices.EnumerateDevices()));
        return check.Problem switch
        {
            OutputProblem.None or OutputProblem.NotEnoughSpace or OutputProblem.FileTooLarge => null,
            OutputProblem.SameDisk => Loc.Get("Carve_Error_SameDisk"),
            _ => Loc.Get("Carve_Error_Folder"),
        };
    }

    public void Audit(string operation, string target, WipeAuditOutcome outcome, IReadOnlyDictionary<string, string> details)
    {
        // アプリのログにも残す (ケースを開いていないときの診断用)。
        AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Audit {operation} {outcome} target={target} {string.Join(' ', details.Select(d => d.Key + "=" + d.Value))}"));
        var parameters = new List<KeyValuePair<string, string>>(details) { new("outcome", outcome.ToString()) };
        ForensicsService.Audit.Record(new AuditRecord(operation)
        {
            EvidenceId = ForensicsService.Cases.ActiveEvidence.FirstOrDefault(e => Same(e.Path, target))?.Id,
            Path = target,
            Parameters = parameters,
            Result = outcome switch
            {
                WipeAuditOutcome.Succeeded => AuditResult.Success,
                WipeAuditOutcome.Cancelled => AuditResult.Cancelled,
                _ => AuditResult.Failure,
            },
            Message = outcome == WipeAuditOutcome.Refused && details.TryGetValue("refusal", out string? why) ? "Refused: " + why : string.Empty,
        });
    }

    public event EventHandler? WriteBlockChanged;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
