using HexEditor.App.Services;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.ViewModels;

/// <summary>「問題」の一覧の 1 行 (AUTO-20 の仕様 4): エラー・警告と位置。クリックで該当行へ移動する。</summary>
public sealed class ProblemRow(ScriptDiagnostic diagnostic)
{
    public ScriptDiagnostic Diagnostic { get; } = diagnostic;

    public string Message => Diagnostic.Code is { Length: > 0 } code && !Diagnostic.Message.StartsWith(code, StringComparison.Ordinal)
        ? $"{code}: {Diagnostic.Message}" : Diagnostic.Message;

    /// <summary>行と列 (<c>9:5</c>)。位置がなければ空。</summary>
    public string Position => Diagnostic.Line > 0 ? $"{Diagnostic.Line}:{Math.Max(1, Diagnostic.Column)}" : string.Empty;

    public string Glyph => Diagnostic.IsError ? "" : "";

    public Brush Brush => (Brush)Application.Current.Resources[Diagnostic.IsError ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];

    public string AccessibleName =>
        $"{Loc.Get(Diagnostic.IsError ? "Output_KindError" : "Output_KindWarning")} {Message} {Position}".Trim();
}

/// <summary>ウォッチ式の 1 行 (AUTO-21 の仕様 4)。評価エラーはその行に赤字で出す (デバッグは続ける)。</summary>
public sealed class WatchRow(WatchResult result)
{
    public string Expression => result.Expression;

    public string ValueText => result.Error is { } error ? error : result.Value is { } v ? "= " + v.Display.ReplaceLineEndings(" ") : "= ?";

    public bool IsError => result.Error is not null;

    public Brush Brush => (Brush)Application.Current.Resources[IsError ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"];

    public string AccessibleName => $"{Expression} {ValueText}";
}
