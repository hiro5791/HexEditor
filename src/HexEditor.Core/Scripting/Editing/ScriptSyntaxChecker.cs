using Acornima;

namespace HexEditor.Core.Scripting.Editing;

/// <summary>
/// スクリプトエディタの構文のチェック (AUTO-20 の仕様 4): 入力が止まったら構文を調べ、誤りを問題の一覧に出して波線を引く。
/// JavaScript は構文解析 (Acornima)、メタデータの書式の誤りは警告。C# の構文・型は C# スクリプト コンポーネントがあるときに
/// コンパイルで調べる (実行時のコンパイルエラーと同じ診断)。
/// </summary>
public static class ScriptSyntaxChecker
{
    /// <summary>本文を調べる。誤りがなければ空 (警告は <see cref="ScriptDiagnostic.IsError"/> が false)。</summary>
    public static IReadOnlyList<ScriptDiagnostic> Check(ScriptLanguage language, string code, string? path)
    {
        var list = new List<ScriptDiagnostic>();
        ScriptMetadata meta = ScriptMetadata.Parse(code);
        foreach (ScriptMetadataWarning w in meta.Warnings)
        {
            list.Add(new ScriptDiagnostic(Msg.T($"メタデータ: {w.Message}", $"Metadata: {w.Message}"), path, w.Line, 1, null, IsError: false));
        }

        if (language == ScriptLanguage.JavaScript)
        {
            try
            {
                _ = new Parser(new ParserOptions()).ParseModule(code);
            }
            catch (ParseErrorException ex)
            {
                list.Add(new ScriptDiagnostic(ex.Description, path, ex.LineNumber, ex.Column + 1, "SyntaxError"));
            }
        }

        return list;
    }
}
