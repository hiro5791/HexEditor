using HexEditor.App.Commands;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// コードエディタの有効範囲 (00-overview 8.1・8.5) の目印と、そのコマンド (<c>codeEditor.*</c>) の受け口。フォーカスのある部品の祖先に
/// これがあれば、キーの振り分けはコードエディタの割り当て (F5・F9・F10・F11・F12 など) を先に見る。
/// テンプレートエディタ (07 の TPL-31・TPL-32) とスクリプトエディタ (08 の AUTO-20・AUTO-21) が実装し、コマンドの登録は 1 か所
/// (MainWindow.CodeEditor.cs) で表示中のエディタに振り分ける。
/// </summary>
public interface ICodeEditorHost
{
    /// <summary>コマンドの状態 (対応しないコマンドは理由付きで使えない)。</summary>
    CommandState GetCodeEditorCommandState(string commandId);

    /// <summary>コマンドを実行する。</summary>
    Task ExecuteCodeEditorCommandAsync(string commandId);

    /// <summary>ウォッチ式を加える (<c>codeEditor.addWatch</c> の入力欄の後)。</summary>
    void AddWatch(string expression);
}

/// <summary>
/// コードエディタの部品が持つ標準の編集キー (Ctrl+G・Ctrl+/ など。00-overview 8.1) をグローバルの割り当てより先に処理する部品。
/// キーの振り分け (MainWindow.RouteKey) がコマンドより先に呼ぶ。処理したら true。
/// </summary>
public interface ICodeEditorKeys
{
    bool HandleEditorKey(VirtualKey key, bool ctrl, bool shift, bool alt);
}

/// <summary>コードエディタのコマンドの ID (00-overview 8.5)。</summary>
public static class CodeEditorCommands
{
    public const string Run = "codeEditor.run";
    public const string Stop = "codeEditor.stop";
    public const string ToggleBreakpoint = "codeEditor.toggleBreakpoint";
    public const string StepOver = "codeEditor.stepOver";
    public const string StepInto = "codeEditor.stepInto";
    public const string StepOut = "codeEditor.stepOut";
    public const string RunToCursor = "codeEditor.runToCursor";
    public const string AddWatch = "codeEditor.addWatch";
    public const string GoToDefinition = "codeEditor.goToDefinition";
    public const string FindReferences = "codeEditor.findReferences";
    public const string Format = "codeEditor.format";
    public const string ToggleComment = "codeEditor.toggleComment";
    public const string Complete = "codeEditor.complete";

    public static readonly string[] All =
        [Run, Stop, ToggleBreakpoint, StepOver, StepInto, StepOut, RunToCursor, AddWatch, GoToDefinition, FindReferences, Format, ToggleComment, Complete];

    /// <summary>このエディタでは使えない (スクリプトエディタの定義へ移動など)。</summary>
    public static CommandState NotSupported => CommandState.Unavailable(Services.Loc.Get("CodeEditor_NotSupported"));
}
