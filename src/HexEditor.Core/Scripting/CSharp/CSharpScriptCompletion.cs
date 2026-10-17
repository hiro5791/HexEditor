using HexEditor.Sdk.Hosting;

namespace HexEditor.Core.Scripting.CSharp;

/// <summary>
/// スクリプトエディタの C# の補完 (08 の AUTO-20 の仕様 3)。C# スクリプト コンポーネント (Roslyn) があるときだけ使える。
/// コンパイルと同じ参照・<c>#load</c> の制限で、コンポーネントの <see cref="ICSharpCompletionBackend"/> に尋ねる。
/// </summary>
public static class CSharpScriptCompletion
{
    /// <summary>補完が使えるか (コンポーネントが入っている)。読み込みはしない。</summary>
    public static bool IsAvailable(CSharpComponentManager? manager) => manager is { IsInstalled: true };

    /// <summary>
    /// 位置 (本文の文字の位置) の候補。コンポーネントがない・補完に対応しない古いコンポーネントなら null。コンポーネントを初めて
    /// 読み込むときはファイルの検証をするので、UI スレッドから呼ばない。
    /// </summary>
    public static IReadOnlyList<CSharpCompletionItem>? Complete(CSharpComponentManager? manager, string code, int position, string? path,
        IReadOnlyList<string> loadFolders, CancellationToken cancellationToken)
    {
        if (!IsAvailable(manager))
        {
            return null;
        }

        if (manager!.LoadBackend() is not ICSharpCompletionBackend backend)
        {
            return null;
        }

        var request = new CSharpCompileRequest
        {
            Code = code,
            Path = path is null ? null : Path.GetFullPath(path),
            LoadFolders = loadFolders,
            AllowedReferences = CSharpScriptRunner.AllowedReferences,
            ReferencePaths = CSharpCompileCache.ReferencePaths(),
        };
        return backend.Complete(request, Math.Clamp(position, 0, code.Length), cancellationToken);
    }

    /// <summary>位置の前の入力中の名前の範囲 (候補で置き換える範囲)。</summary>
    public static (int Start, int Length) WordBefore(string code, int position)
    {
        int end = Math.Clamp(position, 0, code.Length);
        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
        {
            start--;
        }

        return (start, end - start);
    }
}
