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

    public bool IsEvidence(string identity, string? path) =>
        ForensicsService.Cases.ActiveEvidence.Any(e => Same(e.Path, identity) || (path is not null && Same(e.Path, path)));

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
