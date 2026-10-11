using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>位置マネージャのパネル (INSP-31)。表示と入力だけを持つ。移動・書き出しはウィンドウが行う (イベント)。</summary>
public sealed partial class PositionManagerPanel : UserControl, Panels.IPanelContent
{
    /// <summary>左右と上下を切り替える幅。</summary>
    private const double NarrowWidth = 480;

    private bool _loading;
    private bool _programmatic;

    public PositionManagerPanel(PositionManagerViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("PositionManager_PanelName"));
        AutomationProperties.SetName(List, Loc.Get("PositionManager_PanelName"));
        AutomationProperties.SetName(CommentBox, Loc.Get("Bookmark_CommentLabel/Text"));
        AutomationProperties.SetName(ExportButton, Loc.Get("PositionManager_ExportName"));
        ToolTipService.SetToolTip(ExportButton, Loc.Get("PositionManager_ExportName"));
        FollowBox.IsChecked = Vm.FollowCursor;
        Vm.PropertyChanged += Vm_PropertyChanged;
        Unloaded += (_, _) => Vm.PropertyChanged -= Vm_PropertyChanged;
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged -= Vm_PropertyChanged;
            Vm.PropertyChanged += Vm_PropertyChanged;
            LoadSelected();
        };
    }

    public PositionManagerViewModel Vm { get; }

    /// <summary>一覧で選んだ: エディタをその位置に移す (INSP-31 の仕様 2)。</summary>
    public event EventHandler<Bookmark>? GoToRequested;

    /// <summary>書き出し (true なら HTML)。</summary>
    public event EventHandler<bool>? ExportRequested;

    bool Panels.IPanelContent.FocusContent()
    {
        if (Vm.Rows.Count == 0)
        {
            return OnlyCommentsBox.Focus(FocusState.Keyboard);
        }

        if (List.SelectedIndex < 0)
        {
            List.SelectedIndex = 0;
        }

        List.UpdateLayout();
        return List.ContainerFromIndex(List.SelectedIndex) is ListViewItem item ? item.Focus(FocusState.Keyboard) : List.Focus(FocusState.Keyboard);
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PositionManagerViewModel.Selected) or nameof(PositionManagerViewModel.SelectedTitle))
        {
            LoadSelected();
        }
    }

    /// <summary>選んだブックマークのコメントを編集欄に入れる (入力中の欄は書き換えない)。</summary>
    private void LoadSelected()
    {
        if (Vm.Selected is { } row && !ReferenceEquals(List.SelectedItem, row))
        {
            _programmatic = true;
            List.SelectedItem = row;
            List.ScrollIntoView(row);
            _programmatic = false;
        }

        string comment = Vm.Selected?.Bookmark.Comment ?? string.Empty;
        if (CommentBox.Text != comment)
        {
            _loading = true;
            CommentBox.Text = comment;
            _loading = false;
        }

        if (PreviewToggle.IsChecked == true)
        {
            RenderPreview();
        }
    }

    /// <summary>カーソルに追従して選ぶ (エディタは動かさない)。</summary>
    internal void SelectQuietly(Bookmark? bookmark)
    {
        _programmatic = true;
        Vm.Selected = bookmark is null || Vm.Rows.IndexOf(bookmark) < 0 ? null : Vm.Rows.Row(bookmark);
        _programmatic = false;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is PositionRowViewModel row)
        {
            bool programmatic = _programmatic;
            Vm.Selected = row;
            if (!programmatic)
            {
                GoToRequested?.Invoke(this, row.Bookmark);
            }
        }
    }

    /// <summary>行を選ぶ (テスト用の命令の通り道。クリックと同じくエディタを動かす)。</summary>
    internal void SelectIndex(int index) => List.SelectedIndex = index;

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PositionRowViewModel row && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetName(container, row.AutomationName);
        }
    }

    private void CommentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            Vm.SetComment(CommentBox.Text);
        }
    }

    /// <summary>コメントを書き換える (テスト用の命令の通り道。入力と同じ)。</summary>
    internal void SetComment(string text) => CommentBox.Text = text;

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        bool preview = PreviewToggle.IsChecked == true;
        CommentBox.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            RenderPreview();
        }
    }

    private void RenderPreview() => MarkdownRenderer.Render(Preview, CommentBox.Text, _ => { });

    private void FollowBox_Click(object sender, RoutedEventArgs e) => Vm.FollowCursor = FollowBox.IsChecked == true;

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, false);

    private void ExportHtml_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, true);

    /// <summary>幅が狭い場合は一覧と編集欄を上下に並べる (INSP-31 の仕様 1)。</summary>
    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < NarrowWidth;
        EditorColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        EditorRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(EditorArea, narrow ? 0 : 1);
        Grid.SetRow(EditorArea, narrow ? 1 : 0);
    }
}
