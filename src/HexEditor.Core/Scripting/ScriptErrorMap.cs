using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// アプリの例外を API の共通の例外 (AUTO-04 の仕様 9) にそろえる。JavaScript と C# のどちらにも同じ種類の例外を見せる。
/// </summary>
public static class ScriptErrorMap
{
    /// <summary>実行を止める例外 (スクリプトに見せず、そのまま伝える)。</summary>
    public static bool IsControl(Exception ex) => ex is ScriptTerminatedException or ScriptLimitExceededException;

    /// <summary>そのまま伝える例外 (実行の制御、API の例外、スクリプトの関数が投げた JavaScript の例外)。</summary>
    public static bool PassesThrough(Exception ex) => IsControl(ex) || ex is ScriptError or Jint.Runtime.JavaScriptException;

    public static ScriptError ToScriptError(Exception ex) => ex switch
    {
        ScriptError se => se,
        OperationCanceledException => new CanceledError(Msg.T("キャンセルされました。", "Canceled."), ex),
        DocumentReadOnlyException => new ReadOnlyError(Msg.T("ドキュメントは読み取り専用です。", "The document is read-only."), ex),
        FixedLengthException => new ReadOnlyError(Msg.T("このドキュメントは長さを変えられません。", "The length of this document cannot be changed."), ex),
        DocumentLockedException => new ReadOnlyError(Msg.T("処理中のため編集できません。", "Cannot edit while an operation is in progress."), ex),
        ObjectDisposedException => new Sdk.ArgumentError(Msg.T("ドキュメントは閉じられています。", "The document has been closed."), ex),
        ArgumentOutOfRangeException => new Sdk.RangeError(ex.Message, ex),
        ExpressionException ee => new Sdk.ArgumentError(Msg.T($"入力式を読めません ({ee.Error} {ee.Detail})", $"Cannot parse the input expression ({ee.Error} {ee.Detail})").TrimEnd(), ex),
        Search.PatternException => new Sdk.ArgumentError(ex.Message, ex),
        ArgumentException or FormatException or KeyNotFoundException => new Sdk.ArgumentError(ex.Message, ex),
        IOException or UnauthorizedAccessException => new IoError(ex.Message, ex),
        OutOfMemoryException or InsufficientExecutionStackException => new LimitError(Msg.T("メモリが足りません。", "Out of memory."), ex) { Limit = "memory" },
        TemplateScriptException te => new Sdk.ArgumentError(te.Line > 0 ? $"{te.Message} ({te.File}:{te.Line}:{te.Column})" : te.Message, ex),
        _ => new IoError(ex.Message, ex),
    };

    /// <summary>API の処理を包み、アプリの例外を API の例外にする。</summary>
    public static T Guard<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (!PassesThrough(ex))
        {
            throw ToScriptError(ex);
        }
    }

    public static void Guard(Action work) => Guard(() =>
    {
        work();
        return true;
    });

    public static async Task<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (!PassesThrough(ex))
        {
            throw ToScriptError(ex);
        }
    }

    public static async Task GuardAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (!PassesThrough(ex))
        {
            throw ToScriptError(ex);
        }
    }
}
