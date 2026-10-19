using HexEditor.Core.Forensics.Images;

namespace HexEditor.App.Services;

/// <summary>
/// イメージの機能 (12 の FOR-20〜26) がほかの領域の機能を使う口の、アプリの既定の実装。書き込み保護モード (FOR-01) は
/// <see cref="WriteBlockMode"/> の状態を使う。ケース (FOR-03)・証拠一覧・監査ログ (FOR-04) の実装は、この口
/// (<see cref="IImageForensicHooks"/>) を実装して <see cref="ImageForensics.Hooks"/> に設定し直す (<see cref="Inner"/> に渡せば、
/// 書き込み保護の判定はこちらと合わせられる)。
/// </summary>
internal sealed class AppImageForensicHooks : IImageForensicHooks
{
    /// <summary>ケース・証拠・監査ログの実装 (ほかの領域が設定する)。null なら記録しない。</summary>
    public IImageForensicHooks? Inner { get; set; }

    public bool IsWriteBlocked => WriteBlockMode.IsEnabled || Inner?.IsWriteBlocked == true;

    public ImageCaseInfo? Case => Inner?.Case;

    public IReadOnlyList<int> EvidenceDiskNumbers => Inner?.EvidenceDiskNumbers ?? [];

    public string? EvidenceNumberOf(string pathOrIdentity) => Inner?.EvidenceNumberOf(pathOrIdentity);

    public void Audit(ImageAuditRecord record)
    {
        AppLog.Info($"Forensic image audit: {record.Operation} {record.Result} {record.TargetPath} {record.Message}");
        Inner?.Audit(record);
    }

    public void RegisterAcquiredImage(string imagePath, string sourcePath, IReadOnlyDictionary<string, byte[]> hashes) =>
        Inner?.RegisterAcquiredImage(imagePath, sourcePath, hashes);

    public IReadOnlyDictionary<string, byte[]> EvidenceHashesOf(string pathOrIdentity) =>
        Inner?.EvidenceHashesOf(pathOrIdentity) ?? new Dictionary<string, byte[]>();

    public void RecordVerification(string pathOrIdentity, VerificationResult result) => Inner?.RecordVerification(pathOrIdentity, result);

    /// <summary>アプリの起動時に設定する。</summary>
    public static AppImageForensicHooks Install()
    {
        var hooks = new AppImageForensicHooks();
        ImageForensics.Hooks = hooks;
        return hooks;
    }
}
