using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Formats;

namespace HexEditor.App;

/// <summary>デコードして開いたときの誤りの一覧のパネル (ENG-38 の仕様 6。下部のパネル)。</summary>
public sealed partial class MainWindow
{
    /// <summary>パネル ID (表示切り替えのコマンドは <c>view.panel.formatIssues</c>)。</summary>
    public const string FormatIssuesPanelId = "formatIssues";

    private FormatIssuesPanel? _formatIssuesPanel;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterFormatIssuesPanel()
    {
        if (PanelRegistry.Find(FormatIssuesPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(FormatIssuesPanelId, "FormatIssues_Title", Core.Panels.PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window)._formatIssuesPanel = new FormatIssuesPanel(ctx)));
        }
    }

    /// <summary>誤りの一覧を開いて、今の文書の誤りを表示する。</summary>
    private void ShowFormatIssues(DocumentViewModel vm)
    {
        if (Vm.Selected != vm)
        {
            Vm.Selected = vm;
        }

        ShowPanel(FormatIssuesPanelId, focus: false);
        _formatIssuesPanel?.Refresh();
    }

    /// <summary>一覧の 1 行: 行番号、理由、内容。</summary>
    internal static string FormatIssueRow(ImportIssue issue) => IssueText(issue) + "  " + issue.Content;

    /// <summary>テスト用: 一覧に出ている誤り。</summary>
    internal IReadOnlyList<ImportIssue> FormatIssuesShown => _formatIssuesPanel?.Items ?? [];
}
