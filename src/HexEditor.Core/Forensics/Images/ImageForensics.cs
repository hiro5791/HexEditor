namespace HexEditor.Core.Forensics.Images;

/// <summary>監査ログの 1 件の結果 (FOR-04 の仕様 2)。</summary>
public enum ImageAuditResult
{
    Started,
    Success,
    Failure,
    Cancelled,
}

/// <summary>イメージ・クローン・セクタ範囲の機能が監査ログ (FOR-04) に渡す 1 件。</summary>
public sealed record ImageAuditRecord
{
    /// <summary>操作の種類 (例: <c>image.create</c>、<c>image.verify</c>、<c>disk.clone</c>、<c>sectors.restore</c>)。</summary>
    public required string Operation { get; init; }

    /// <summary>対象のデータソースのパス。</summary>
    public required string TargetPath { get; init; }

    /// <summary>対象の識別子 (データソースの Identity。証拠一覧の照合に使う)。</summary>
    public string? TargetIdentity { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public ImageAuditResult Result { get; init; }

    public string? Message { get; init; }
}

/// <summary>ケースの情報のうち、イメージの機能が使うもの (FOR-03 の仕様 2)。</summary>
public sealed record ImageCaseInfo
{
    public string CaseNumber { get; init; } = string.Empty;

    public string CaseName { get; init; } = string.Empty;

    public string Examiner { get; init; } = string.Empty;

    public string Organization { get; init; } = string.Empty;

    /// <summary>ケースのフォルダ (イメージの既定の出力先 <c>images/</c>、レポート <c>reports/</c>、RAID の構成)。</summary>
    public string? Folder { get; init; }

    /// <summary>ケースのタイムゾーン (FOR-05。ローカル時刻の表示)。null なら Windows のタイムゾーン。</summary>
    public TimeZoneInfo? TimeZone { get; init; }
}

/// <summary>
/// イメージの機能 (FOR-20〜26) から、ほかの領域の機能 (書き込み保護 FOR-01、ケースと証拠一覧 FOR-03、監査ログ FOR-04) を使う口。
/// これらの機能の実装はこのインターフェイスを実装して <see cref="ImageForensics.Hooks"/> に設定する。設定がなければ
/// 書き込み保護は無効・ケースなし・監査ログは記録しない、として動く。
/// </summary>
public interface IImageForensicHooks
{
    /// <summary>書き込み保護モード (FOR-01) が有効か。</summary>
    bool IsWriteBlocked { get; }

    /// <summary>開いているケース (なければ null)。</summary>
    ImageCaseInfo? Case { get; }

    /// <summary>
    /// データソース (パスまたは Identity) が証拠一覧に登録されていれば、その証拠番号。クローン・復元の出力先の拒否 (FOR-24 の仕様 6) と、
    /// 出力先の検査 (共通の前提) に使う。
    /// </summary>
    string? EvidenceNumberOf(string pathOrIdentity);

    /// <summary>証拠一覧のディスクの物理ディスク番号 (出力先の検査: 調査対象のディスク上のボリュームには出力しない)。</summary>
    IReadOnlyList<int> EvidenceDiskNumbers { get; }

    /// <summary>監査ログに記録する (FOR-04)。記録できなければ例外 (呼び出し側は書き込みを伴う操作を止める)。</summary>
    void Audit(ImageAuditRecord record);

    /// <summary>
    /// 取得したイメージを証拠一覧に「取得時のハッシュ」付きで登録する (FOR-20 の仕様 5)。ケースがなければ何もしない。
    /// </summary>
    void RegisterAcquiredImage(string imagePath, string sourcePath, IReadOnlyDictionary<string, byte[]> hashes);

    /// <summary>証拠一覧に登録されたハッシュ (検証の比べる値。FOR-22 の仕様 2)。</summary>
    IReadOnlyDictionary<string, byte[]> EvidenceHashesOf(string pathOrIdentity);

    /// <summary>検証の結果を証拠一覧の「検証結果」に出す (FOR-22 の仕様 5)。</summary>
    void RecordVerification(string pathOrIdentity, VerificationResult result);
}

/// <summary>何もしない既定の実装 (ほかの領域の機能がまだ設定していないとき)。</summary>
public sealed class NullImageForensicHooks : IImageForensicHooks
{
    public static NullImageForensicHooks Instance { get; } = new();

    public bool IsWriteBlocked => false;

    public ImageCaseInfo? Case => null;

    public IReadOnlyList<int> EvidenceDiskNumbers => [];

    public string? EvidenceNumberOf(string pathOrIdentity) => null;

    public void Audit(ImageAuditRecord record)
    {
    }

    public void RegisterAcquiredImage(string imagePath, string sourcePath, IReadOnlyDictionary<string, byte[]> hashes)
    {
    }

    public IReadOnlyDictionary<string, byte[]> EvidenceHashesOf(string pathOrIdentity) => new Dictionary<string, byte[]>();

    public void RecordVerification(string pathOrIdentity, VerificationResult result)
    {
    }
}

/// <summary>イメージの機能が使う、アプリ全体の口。</summary>
public static class ImageForensics
{
    private static IImageForensicHooks s_hooks = NullImageForensicHooks.Instance;

    /// <summary>ほかの領域の機能 (FOR-01・03・04) の口。アプリの起動時に設定する。</summary>
    public static IImageForensicHooks Hooks
    {
        get => s_hooks;
        set => s_hooks = value ?? NullImageForensicHooks.Instance;
    }
}
