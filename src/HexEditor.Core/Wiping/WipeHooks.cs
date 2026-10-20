namespace HexEditor.Core.Wiping;

/// <summary>監査ログ (FOR-04) の操作名 (FOR-30〜32 が記録するもの)。ForensicsService の AuditOperations と同じ文字列。</summary>
public static class WipeAuditOperations
{
    /// <summary>範囲・ディスク・空き領域・スラックの消去 (FOR-30・FOR-31)。</summary>
    public const string Wipe = "write.wipe";

    /// <summary>ファイルシステムを理解した墨塗り (FOR-32)。</summary>
    public const string Redact = "write.redact";
}

/// <summary>監査ログに記録する結果 (共通の前提「破壊的な操作の確認」の 5: 成功・失敗・キャンセルにかかわらず記録する)。</summary>
public enum WipeAuditOutcome
{
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>実行の前に拒否した (書き込み保護モード、証拠、不整合など)。</summary>
    Refused,
}

/// <summary>
/// 消去・墨塗り (FOR-30〜32) が使う、証拠管理と書き込み保護 (FOR-01・FOR-03・FOR-04) への最小限の口。
/// <para>
/// 統合の注記: 既定の実装は何もしない (書き込み保護なし・証拠なし・監査ログなし)。App は起動時に <see cref="WipeForensics.Hooks"/> を
/// 設定する (フェーズ 4 の WriteBlockMode を見る実装)。ケース・監査の機能 (ForensicsService) と統合するときは、この口を
/// <c>ForensicsService.IsWriteBlockEnabled</c>・<c>Cases</c> (IEvidenceRegistry)・<c>Audit.EnsureCanWrite</c>・<c>Audit.Record</c> に
/// つなぐ実装に差し替える。
/// </para>
/// </summary>
public interface IWipeForensicHooks
{
    /// <summary>書き込み保護モード (FOR-01) が有効か。</summary>
    bool IsWriteBlockEnabled { get; }

    /// <summary>
    /// データソース (<see cref="Sources.IByteSource.Identity"/>、ファイル・デバイスのパス) が証拠一覧 (FOR-03) に登録されているか。
    /// </summary>
    bool IsEvidence(string identity, string? path);

    /// <summary>
    /// 書き込んでよいかを確かめる (FOR-04 の EnsureCanWrite)。拒否する場合は理由 (表示の言語の文)、よければ null。
    /// </summary>
    string? EnsureCanWrite(string operation, string target);

    /// <summary>監査ログに記録する (対象、範囲、方式、結果。<paramref name="details"/> は項目名と値)。</summary>
    void Audit(string operation, string target, WipeAuditOutcome outcome, IReadOnlyDictionary<string, string> details);

    /// <summary>出力先 (墨塗りの記録など) が調査対象のディスク上でないか (共通の前提「出力先の検査」)。問題があれば理由、なければ null。</summary>
    string? CheckOutput(string path);

    /// <summary>書き込み保護モードの状態が変わった (メニューの有効・無効の更新)。</summary>
    event EventHandler? WriteBlockChanged;
}

/// <summary>何もしない既定の実装 (書き込み保護なし、証拠なし、監査ログなし)。</summary>
public sealed class NullWipeForensicHooks : IWipeForensicHooks
{
    public static NullWipeForensicHooks Instance { get; } = new();

    public bool IsWriteBlockEnabled => false;

    public bool IsEvidence(string identity, string? path) => false;

    public string? EnsureCanWrite(string operation, string target) => null;

    public void Audit(string operation, string target, WipeAuditOutcome outcome, IReadOnlyDictionary<string, string> details)
    {
    }

    public string? CheckOutput(string path) => null;

    public event EventHandler? WriteBlockChanged
    {
        add { }
        remove { }
    }
}

/// <summary>消去・墨塗りの口の置き場所 (アプリ全体で 1 つ)。</summary>
public static class WipeForensics
{
    private static IWipeForensicHooks s_hooks = NullWipeForensicHooks.Instance;

    public static IWipeForensicHooks Hooks
    {
        get => s_hooks;
        set => s_hooks = value ?? NullWipeForensicHooks.Instance;
    }
}

/// <summary>実行の前に拒否した (書き込み保護モード、証拠、システムディスク、不整合など)。<see cref="Reason"/> は拒否の種類。</summary>
public sealed class WipeRefusedException(WipeRefusal reason, string? detail = null)
    : InvalidOperationException(detail ?? reason.ToString())
{
    public WipeRefusal Reason { get; } = reason;

    public string? Detail { get; } = detail;
}

/// <summary>消去・墨塗りを拒否する理由。</summary>
public enum WipeRefusal
{
    None,

    /// <summary>書き込み保護モード (FOR-30 の仕様 8、FOR-31 の仕様 5)。</summary>
    WriteBlock,

    /// <summary>証拠一覧のデータソース。</summary>
    Evidence,

    /// <summary>Windows が使用中のボリューム (システム・ブート・ページファイル) を含むディスク、またはロックできないボリューム。</summary>
    SystemDisk,

    /// <summary>直接書き換えられないデータソース (変換・派生・圧縮されたイメージなど)。</summary>
    UnsupportedSource,

    /// <summary>範囲が空、またはデータソースの外。</summary>
    EmptyRange,

    /// <summary>ファイルシステムに不整合がある (NTFS の dirty フラグなど。FOR-31 の仕様 4)。</summary>
    Inconsistent,

    /// <summary>対応していないファイルシステム (FOR-31 の仕様 1・2: NTFS、FAT12/16/32、exFAT だけ)。</summary>
    UnsupportedFileSystem,

    /// <summary>割り当て情報が読めない。</summary>
    NoAllocation,

    /// <summary>未保存の編集がある (先に保存か破棄を選ぶ。FOR-30 の仕様 7)。</summary>
    UnsavedEdits,

    /// <summary>証拠管理の側 (EnsureCanWrite) が拒否した。</summary>
    PolicyDenied,
}
