namespace HexEditor.App.Controls;

/// <summary>
/// スクリプトのパネル (スクリプト一覧・出力・コンソール。AUTO-19・AUTO-23・AUTO-24) とスクリプトエディタが、ウィンドウに頼む操作。
/// MainWindow が実装する (MainWindow.ScriptPanels.cs)。
/// </summary>
public interface IScriptPanelHost
{
    /// <summary>スクリプトエディタのタブで開く。<paramref name="line"/> が 1 以上ならその行にカーソルを置く。</summary>
    void OpenScriptEditor(string path, int line = 0, int column = 0);

    /// <summary>保存先を尋ねる (キャンセルは null)。</summary>
    Task<string?> PickSavePathAsync(string suggestedName, string extension);

    /// <summary>テキストをクリップボードに置く。</summary>
    void SetClipboardText(string text);

    /// <summary>ステータスバーに一時表示する。</summary>
    void ShowStatus(string text);

    /// <summary>スクリプトエディタ以外にフォーカスを戻す (パネルの Esc)。</summary>
    void FocusEditor();
}

/// <summary>スクリプト一覧の操作が使う追加の窓の機能。</summary>
internal interface IScriptListHost : IScriptPanelHost
{
    /// <summary>確認なしのメッセージ (InfoBar)。</summary>
    void ShowNotice(string text, bool error);

    /// <summary>名前の入力ダイアログ (キャンセルは null)。</summary>
    Task<string?> PromptNameAsync(string title, string label, string initial);

    /// <summary>ショートカットの割り当て (スクリプトのコマンド ID)。</summary>
    void AssignShortcut(string commandId);

    /// <summary>フックの設定を開く。</summary>
    void OpenHookSettings(string? scriptPath);

    /// <summary>エクスプローラーで場所を開く。</summary>
    void ShowInFolder(string path);

    /// <summary>実行中のスクリプト (常駐の表示)。</summary>
    IReadOnlyList<string> ResidentScriptPaths { get; }
}
