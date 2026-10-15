using HexEditor.App.Services;
using HexEditor.Core.Templates;

namespace HexEditor.App.Templates;

/// <summary>
/// テンプレートのエラー・警告の画面の文言 (TPL-27 の仕様 7)。Core はコード (<c>HXT1004</c>) と引数だけを返し、文言はリソース
/// <c>TplDiag_&lt;コード&gt;</c> で翻訳する。結果ツリー・出力パネル・テンプレートエディタで使う。
/// </summary>
public static class TemplateDiagnosticText
{
    public static string Message(TemplateDiagnostic diagnostic)
    {
        string? format = Loc.TryGet("TplDiag_" + diagnostic.Code);
        if (format is null)
        {
            return diagnostic.Message;
        }

        string text;
        try
        {
            text = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, diagnostic.Arguments.Cast<object>().ToArray());
        }
        catch (FormatException)
        {
            return diagnostic.Message;
        }

        if (diagnostic.Code == TemplateMessages.Undefined && diagnostic.Arguments.Count > 1 && diagnostic.Arguments[1].Length > 0)
        {
            text += " (" + Loc.Format("TplDiag_HXT2001_Suggestion", diagnostic.Arguments[1]) + ")";
        }

        return text;
    }

    /// <summary>種類の文字 (「情報」「警告」「エラー」。色だけにしない。TPL-21 の「エラー」)。</summary>
    public static string Severity(TemplateSeverity severity) => Loc.Get("TplSeverity_" + severity);

    /// <summary>出力パネルの 1 行 (例: <c>エラー HXT2002: 0 で除算しました (test.hxt:3:21)</c>)。</summary>
    public static string Line(TemplateDiagnostic diagnostic)
    {
        string where = diagnostic.Location is { } at ? $" ({Path.GetFileName(at.File)}:{at.Line}:{at.Column})" : string.Empty;
        return $"{Severity(diagnostic.Severity)} {diagnostic.Code}: {Message(diagnostic)}{where}";
    }
}
