namespace HexEditor.Core.Scripting;

/// <summary>スクリプトを強制的に中断した (停止の第 2 段階・<c>app.stop()</c>)。スクリプトの try / catch では捕まえられない。</summary>
internal sealed class ScriptTerminatedException(bool normal) : Exception("スクリプトを中断しました。")
{
    /// <summary><c>app.stop()</c> による正常な終了。</summary>
    public bool Normal { get; } = normal;
}

/// <summary>実行時間・メモリの上限を超えた (AUTO-02 の仕様 4)。スクリプトの try / catch では捕まえられない。</summary>
internal sealed class ScriptLimitExceededException(string limit, string message) : Exception(message)
{
    public string Limit { get; } = limit;
}
