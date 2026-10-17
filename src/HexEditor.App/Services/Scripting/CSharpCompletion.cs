using HexEditor.App.Controls;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Sdk.Hosting;

namespace HexEditor.App.Services.Scripting;

/// <summary>
/// スクリプトエディタの C# の補完 (08 の AUTO-20 の仕様 3)。C# スクリプト コンポーネントがあれば Roslyn で候補を作る
/// (<see cref="CSharpScriptCompletion"/>)。UI スレッドを止めないよう裏で計算し、時間の上限を超えたら候補なしにする。
/// </summary>
internal static class CSharpCompletion
{
    /// <summary>1 回の補完の時間の上限。</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>コンポーネントが入っているか (入っていなければエディタに案内を出す)。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return CSharpScriptCompletion.IsAvailable(ScriptingService.Components);
            }
            catch (InvalidOperationException)
            {
                return false; // スクリプトの機能を初期化していない
            }
        }
    }

    public static async Task<CodeCompletionList?> CompleteAsync(string code, int offset, string path)
    {
        if (!IsAvailable)
        {
            return null;
        }

        CSharpComponentManager manager = ScriptingService.Components;
        string[] folders = [.. new[] { System.IO.Path.GetDirectoryName(path), ScriptingService.Folders?.Library }.OfType<string>()];
        using var cts = new CancellationTokenSource(Limit);
        try
        {
            IReadOnlyList<CSharpCompletionItem>? items = await Task.Run(() => CSharpScriptCompletion.Complete(manager, code, offset, path, folders, cts.Token), cts.Token);
            if (items is null)
            {
                return null;
            }

            (int start, int length) = CSharpScriptCompletion.WordBefore(code, offset);
            return new CodeCompletionList(start, length, [.. items.Select(i => new CodeCompletionItem(i.Label, i.Kind, i.Detail, i.Documentation))]);
        }
        catch (Exception ex) when (ex is OperationCanceledException or CSharpComponentException or IOException or InvalidOperationException or BadImageFormatException)
        {
            AppLog.Warning("C# completion failed: " + ex.Message);
            return null;
        }
    }
}
