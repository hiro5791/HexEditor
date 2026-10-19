using HexEditor.Core.Forensics.Audit;

namespace HexEditor.Core.Forensics.WriteBlock;

/// <summary>
/// 書き込み保護モード (FOR-01) の切り替えと例外の設定を、監査ログ (FOR-04) への記録と一緒に行う。有効化・解除はケースがなくても設定フォルダの
/// <c>forensics-audit.log</c> に記録する (<see cref="AuditRecord.AlwaysGlobal"/>。FOR-04 の仕様 8)。ドキュメント単位の例外の設定・解除は、
/// ケースの監査ログに記録する (仕様 4・5)。操作者 (Windows のユーザー名) と日時は監査ログの各行に入る。
/// </summary>
public sealed class WriteBlockService(WriteBlockPolicy policy, Func<ForensicAudit> audit)
{
    public WriteBlockPolicy Policy { get; } = policy;

    public bool IsEnabled => Policy.IsEnabled;

    /// <summary>有効にする。<paramref name="source"/> は理由 (<c>menu</c>、<c>startup</c>、<c>commandLine</c>、<c>case</c>)。変わったら true。</summary>
    public bool Enable(string source)
    {
        if (!Policy.SetEnabled(true))
        {
            return false;
        }

        audit().Record(new AuditRecord(AuditOperations.WriteBlockEnable)
        {
            AlwaysGlobal = true,
            Message = "Write protection mode was enabled.",
            Parameters = AuditRecord.Args(("source", source)),
        });
        return true;
    }

    /// <summary>解除する (確認ダイアログで「書き込み保護を解除する」を選んだ後)。<paramref name="openEvidence"/> は開いていた証拠の名前。</summary>
    public bool Disable(IReadOnlyList<string> openEvidence)
    {
        IReadOnlyList<string> exceptions = Policy.Exceptions;
        if (!Policy.SetEnabled(false))
        {
            return false;
        }

        // 解除で消えた例外も記録しておく (例外は有効な間だけのもの)。
        foreach (string path in exceptions)
        {
            RecordException(AuditOperations.WriteBlockExceptionClear, path, "The document exception ended because write protection mode was disabled.");
        }

        audit().Record(new AuditRecord(AuditOperations.WriteBlockDisable)
        {
            AlwaysGlobal = true,
            Message = "Write protection mode was disabled.",
            Parameters = AuditRecord.Args(("openEvidence", openEvidence.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("evidence", string.Join(", ", openEvidence))),
        });
        return true;
    }

    /// <summary>ドキュメント単位の例外を設定する (仕様 5)。設定したら true。</summary>
    public bool AddException(string filePath)
    {
        if (!Policy.AddException(filePath))
        {
            return false;
        }

        RecordException(AuditOperations.WriteBlockExceptionSet, filePath, "Writing was allowed for this document.");
        return true;
    }

    /// <summary>例外を解除する (ドキュメントを閉じた)。解除したら true。</summary>
    public bool RemoveException(string filePath)
    {
        if (!Policy.RemoveException(filePath))
        {
            return false;
        }

        RecordException(AuditOperations.WriteBlockExceptionClear, filePath, "The document was closed; the exception ended.");
        return true;
    }

    private void RecordException(string operation, string path, string message) =>
        audit().Record(new AuditRecord(operation) { Path = WriteBlockPolicy.Normalize(path), Message = message });
}
