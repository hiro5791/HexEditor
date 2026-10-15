namespace HexEditor.Sdk;

/// <summary>
/// Base class of the errors thrown by the automation API. JavaScript sees an error object of the same name.
/// <ja>自動化 API が投げる例外の基底クラス。JavaScript では同じ名前のエラーオブジェクトになる。</ja>
/// </summary>
public abstract class ScriptError : Exception
{
    /// <summary>Creates the error. <ja>例外を作る。</ja></summary>
    protected ScriptError(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>
    /// The error name shared by JavaScript and C# (for example <c>ArgumentError</c>).
    /// <ja>JavaScript と C# で共通の例外の名前 (例: <c>ArgumentError</c>)。</ja>
    /// </summary>
    public string Name => GetType().Name;
}

/// <summary>An argument is invalid. <ja>引数が不正。</ja></summary>
public sealed class ArgumentError(string message, Exception? inner = null) : ScriptError(message, inner);

/// <summary>An offset or range is out of bounds. <ja>範囲外。</ja></summary>
public sealed class RangeError(string message, Exception? inner = null) : ScriptError(message, inner);

/// <summary>The document is read-only or cannot change its length. <ja>読み取り専用、または長さを変えられない。</ja></summary>
public sealed class ReadOnlyError(string message, Exception? inner = null) : ScriptError(message, inner);

/// <summary>Reading or writing failed. <ja>読み書きに失敗した。</ja></summary>
public sealed class IoError(string message, Exception? inner = null) : ScriptError(message, inner)
{
    /// <summary>The offset where the failure happened, if known. <ja>失敗した位置 (分かる場合)。</ja></summary>
    public long? Offset { get; init; }
}

/// <summary>The script has no permission for the operation. <ja>許可がない。</ja></summary>
public sealed class PermissionError(string message, string permission) : ScriptError(message)
{
    /// <summary>The name of the required permission (for example <c>file-read</c>). <ja>必要な許可の名前 (例: <c>file-read</c>)。</ja></summary>
    public string Permission { get; } = permission;
}

/// <summary>The operation was canceled. <ja>キャンセルされた。</ja></summary>
public sealed class CanceledError(string message, Exception? inner = null) : ScriptError(message, inner);

/// <summary>A limit (size, count, time, memory) was exceeded. <ja>上限 (量・件数・時間・メモリ) を超えた。</ja></summary>
public sealed class LimitError(string message, Exception? inner = null) : ScriptError(message, inner)
{
    /// <summary>
    /// Which limit was exceeded: <c>size</c>, <c>count</c>, <c>time</c>, <c>memory</c>, <c>recursion</c> or <c>storage</c>.
    /// <ja>超えた上限の種類: <c>size</c>、<c>count</c>、<c>time</c>、<c>memory</c>、<c>recursion</c>、<c>storage</c>。</ja>
    /// </summary>
    public string Limit { get; init; } = "size";
}
