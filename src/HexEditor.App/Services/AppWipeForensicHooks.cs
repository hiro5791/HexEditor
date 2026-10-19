using System.Globalization;
using HexEditor.Core.Wiping;

namespace HexEditor.App.Services;

/// <summary>
/// 消去・墨塗り (12 の FOR-30〜32) の、書き込み保護モード・証拠・監査ログへの口 (<see cref="IWipeForensicHooks"/>) のアプリの実装。
/// <para>
/// 統合の注記: このブランチでは、書き込み保護モードはフェーズ 4 の <see cref="WriteBlockMode"/> を見て、監査ログはアプリのログに書くだけにしている
/// (証拠一覧はない)。ケース・監査の機能 (ForensicsService) と統合するときは、<see cref="Inner"/> に ForensicsService へつなぐ実装を設定する
/// (<c>IsWriteBlockEnabled</c>・<c>Cases</c> (IEvidenceRegistry) の判定・<c>Audit.EnsureCanWrite</c>・<c>Audit.Record</c> (AuditOperations の
/// write.wipe / write.redact)・<c>OutputGuard.Check</c>)。設定すると、すべての呼び出しがそちらへ行く。
/// </para>
/// </summary>
internal sealed class AppWipeForensicHooks : IWipeForensicHooks
{
    public static AppWipeForensicHooks Instance { get; } = new();

    private AppWipeForensicHooks()
    {
        WriteBlockMode.Changed += (_, _) => WriteBlockChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>証拠管理の本体 (統合で設定する)。null の間は、この既定の実装で動く。</summary>
    public static IWipeForensicHooks? Inner { get; set; }

    public bool IsWriteBlockEnabled => Inner?.IsWriteBlockEnabled ?? WriteBlockMode.IsEnabled;

    public bool IsEvidence(string identity, string? path) => Inner?.IsEvidence(identity, path) ?? false;

    public string? EnsureCanWrite(string operation, string target) => Inner?.EnsureCanWrite(operation, target);

    public string? CheckOutput(string path) => Inner?.CheckOutput(path);

    public void Audit(string operation, string target, WipeAuditOutcome outcome, IReadOnlyDictionary<string, string> details)
    {
        if (Inner is { } inner)
        {
            inner.Audit(operation, target, outcome, details);
            return;
        }

        // 監査ログ (FOR-04) がない構成: アプリのログに残す。
        AppLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Audit {operation} {outcome} target={target} {string.Join(' ', details.Select(d => d.Key + "=" + d.Value))}"));
    }

    public event EventHandler? WriteBlockChanged;
}
