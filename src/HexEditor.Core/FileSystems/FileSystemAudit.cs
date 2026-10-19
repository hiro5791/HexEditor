namespace HexEditor.Core.FileSystems;

/// <summary>ファイルシステムの機能が監査ログに残す操作 (FOR-04 に渡す。FOR-09 の受け入れ基準 4 の「監査ログにも記録される」)。</summary>
public enum FileSystemAuditAction
{
    /// <summary>ファイルを書き出した (FOR-09 の仕様 6、FOR-17 の仕様 5)。</summary>
    ExportFiles,

    /// <summary>未割り当て領域を書き出した (FOR-16 の仕様 5)。</summary>
    ExportUnallocated,

    /// <summary>スラックを書き出した (FOR-16 の仕様 6)。</summary>
    ExportSlack,

    /// <summary>パーティション表のエントリを作成した (ドキュメントの編集。保存はディスクへの書き込みとして ENG-30 が記録する。FOR-08 の仕様 4)。</summary>
    CreatePartitionEntries,
}

/// <summary>監査ログの 1 件 (<see cref="Details"/> は項目名と値。出力先・件数・ハッシュのマニフェストの場所など)。</summary>
public sealed record FileSystemAuditEvent(FileSystemAuditAction Action, string Source, IReadOnlyDictionary<string, string> Details);

/// <summary>
/// ファイルシステムの機能から監査ログ (FOR-04) への通り道。監査ログを実装する側が <see cref="Sink"/> を設定する。設定されていなければ
/// 何もしない (監査ログのない構成・テスト)。
/// </summary>
public static class FileSystemAudit
{
    /// <summary>監査ログに書く処理 (FOR-04 が設定する)。</summary>
    public static Action<FileSystemAuditEvent>? Sink { get; set; }

    public static void Record(FileSystemAuditAction action, string source, IReadOnlyDictionary<string, string> details) =>
        Sink?.Invoke(new FileSystemAuditEvent(action, source, details));
}
