using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Formats;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// エンコード形式をデコードして開いたときの誤りの一覧 (ENG-38 の仕様 6、TOOL-11 の仕様 4): 行番号・内容・理由。作業中の文書の誤りを表示する。
/// </summary>
public sealed partial class FormatIssuesPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _list;
    private readonly TextBlock _empty;

    public FormatIssuesPanel(PanelContext context)
    {
        _context = context;
        _list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(_list, "FormatIssues_List");
        AutomationProperties.SetName(_list, Loc.Get("FormatIssues_Title"));
        _empty = new TextBlock { Text = Loc.Get("FormatIssues_None"), Margin = new Thickness(8), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        var grid = new Grid();
        grid.Children.Add(_list);
        grid.Children.Add(_empty);
        Content = grid;
        context.ActiveDocumentChanged += OnActiveDocumentChanged;
        Unloaded += (_, _) => context.ActiveDocumentChanged -= OnActiveDocumentChanged;
        Refresh();
    }

    private void OnActiveDocumentChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>今の文書の誤り (テスト用の命令が読む)。</summary>
    internal IReadOnlyList<ImportIssue> Items { get; private set; } = [];

    public void Refresh()
    {
        DocumentViewModel? doc = _context.ActiveDocument;
        Items = doc?.FormatIssues ?? [];
        _list.Items.Clear();
        foreach (ImportIssue issue in Items)
        {
            var row = new TextBlock
            {
                Text = MainWindow.FormatIssueRow(issue),
                FontFamily = DialogParts.Mono,
                TextWrapping = TextWrapping.NoWrap,
                FlowDirection = FlowDirection.LeftToRight,
            };
            _list.Items.Add(row);
        }

        _empty.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);
}
