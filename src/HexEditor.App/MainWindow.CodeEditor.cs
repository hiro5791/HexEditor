using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// コードエディタの有効範囲のコマンド (<c>codeEditor.*</c>。00-overview 8.5) を、表示中のコードエディタ (テンプレートエディタ・
/// スクリプトエディタ) に振り分ける。登録はここだけ (エディタの種類ごとに別のコマンドを作らない)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>表示中のコードエディタ (なければ null)。</summary>
    internal ICodeEditorHost? ActiveCodeEditor => (ICodeEditorHost?)ActiveScriptEditor ?? ActiveTemplateEditor;

    private void RegisterCodeEditorCommands()
    {
        foreach (string id in CodeEditorCommands.All)
        {
            if (id == CodeEditorCommands.AddWatch)
            {
                Commands.Register(id, AddCodeEditorWatchAsync, () => CodeEditorState(id));
                continue;
            }

            Commands.Register(id, () => ActiveCodeEditor?.ExecuteCodeEditorCommandAsync(id) ?? Task.CompletedTask, () => CodeEditorState(id));
        }

        // Monaco (WebView2) の中で押したキーのうち、アプリが処理するもの (AUTO-20 の仕様 5)。ウィンドウを本当に閉じたら外す
        MonacoCodeEditor.KeyForwarded += CodeEditor_KeyForwarded;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                MonacoCodeEditor.KeyForwarded -= CodeEditor_KeyForwarded;
            }
        };
    }

    /// <summary>
    /// Monaco から受け取ったキーを、コードエディタの有効範囲 (00-overview 8.5) で振り分ける。F5・F9・F11 などのコードエディタのキーと、
    /// Monaco が使わないグローバルのキー (Ctrl+S、Ctrl+Shift+P、F1、Ctrl+W、Ctrl+Tab、F6 など) が届く。
    /// </summary>
    private void CodeEditor_KeyForwarded(object? sender, CodeEditorKeyEventArgs e)
    {
        if (Root.XamlRoot is null || !ReferenceEquals(e.Source.XamlRoot, Root.XamlRoot))
        {
            return; // ほかのウィンドウのエディタ
        }

        DispatchResult result = RouteKey(e.Source, e.Key, e.Modifiers, new KeyContext(Core.Commands.KeyScope.CodeEditor, true));
        e.Handled = result.Handled;
        e.ChordPending = _keys.IsPending;
        LastForwardedKeyCommand = result.Command;
    }

    /// <summary>Monaco から受け取った最後のキーで実行したコマンド (テスト用)。</summary>
    internal string? LastForwardedKeyCommand { get; private set; }

    /// <summary>フォーカスが Monaco (WebView2) の中にあるか。その間のキーはページから受け取る (二重に振り分けない)。</summary>
    private bool FocusInMonaco(DependencyObject? focused)
    {
        for (DependencyObject? node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is MonacoCodeEditor { IsMonaco: true, IsReady: true })
            {
                return true;
            }
        }

        return false;
    }

    private CommandState CodeEditorState(string id) =>
        ActiveCodeEditor is { } editor ? editor.GetCodeEditorCommandState(id) : CommandState.Unavailable(Loc.Get("CodeEditor_NoEditor"));

    /// <summary>ウォッチ式を入力して加える (テンプレート・スクリプトで共通の入力欄)。</summary>
    private async Task AddCodeEditorWatchAsync()
    {
        if (ActiveCodeEditor is not { } editor)
        {
            return;
        }

        var box = new TextBox { FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "TplEd_WatchDialogInput");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, Loc.Get("TplEd_AddWatch"));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.Get("TplEd_AddWatch"),
            Content = box,
            PrimaryButtonText = Loc.Get("TplEd_AddWatchOk"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "TemplateWatchDialog");
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary)
        {
            editor.AddWatch(box.Text);
        }
    }
}
