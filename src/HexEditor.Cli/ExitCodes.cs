namespace HexEditor.Cli;

/// <summary>終了コード (AUTO-36 の仕様 7。全サブコマンド共通)。</summary>
public static class ExitCodes
{
    /// <summary>成功 (compare: 一致、find: 見つかった、hash --check: すべて一致)。</summary>
    public const int Success = 0;

    /// <summary>処理は成功したが結果が否定 (compare: 違いがある、find: 見つからない、hash --check: 不一致がある、patch: 検証に失敗)。</summary>
    public const int Negative = 1;

    /// <summary>引数の誤り (不明なオプション、入力式の誤りなど)。</summary>
    public const int Usage = 2;

    /// <summary>入出力のエラー (ファイルがない、読めない、書けない、空き容量不足)。</summary>
    public const int Io = 3;

    /// <summary>スクリプト・テンプレートのエラー (構文エラー、実行時の例外)。</summary>
    public const int Script = 4;

    /// <summary>許可がない (信頼されていないスクリプト、アクセス拒否、管理者権限が必要)。</summary>
    public const int Denied = 5;

    /// <summary>キャンセルされた (Ctrl+C)。</summary>
    public const int Cancelled = 6;

    /// <summary>上限を超えた (スクリプトの実行時間・メモリ、--timeout)。</summary>
    public const int Limit = 7;

    /// <summary>形式のエラー (Intel HEX のチェックサムの誤り、壊れたパッチなど)。</summary>
    public const int Format = 8;

    /// <summary>内部エラー (想定外の例外。詳細はログ)。</summary>
    public const int Internal = 10;
}

/// <summary>
/// コマンドラインの処理を終える誤り。<see cref="ExitCode"/> で終了し、メッセージ (原因と対処) を標準エラー出力に出す。
/// メッセージはリソースのキーと引数で持ち、表示言語で組み立てる。
/// </summary>
public sealed class CliException : Exception
{
    public CliException(int exitCode, string messageKey, params object?[] args)
        : base(messageKey)
    {
        ExitCode = exitCode;
        MessageKey = messageKey;
        Arguments = args;
    }

    public int ExitCode { get; }

    public string MessageKey { get; }

    public object?[] Arguments { get; }

    /// <summary>引数の誤り (終了コード 2)。</summary>
    public static CliException Usage(string messageKey, params object?[] args) => new(ExitCodes.Usage, messageKey, args);

    /// <summary>入出力のエラー (終了コード 3)。</summary>
    public static CliException Io(string messageKey, params object?[] args) => new(ExitCodes.Io, messageKey, args);
}
