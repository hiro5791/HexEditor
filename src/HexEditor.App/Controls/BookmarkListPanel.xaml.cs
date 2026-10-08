using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// ブックマーク一覧のパネル (INSP-26)。表示とキー操作だけを持つ。移動・編集・削除の通知はウィンドウが行う (イベント)。
/// </summary>
public sealed partial class BookmarkListPanel : UserControl
{
    private bool _clicking;

    public BookmarkListPanel(BookmarkListViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Bookmarks_PanelName"));
        AutomationProperties.SetName(List, Loc.Get("Bookmarks_PanelName"));
        foreach ((Button button, string key) in new[]
        {
            (AddButton, "Bookmarks_AddName"), (EditButton, "Bookmarks_EditName"), (DeleteButton, "Bookmarks_DeleteName"), (UndoButton, "Bookmarks_UndoName"),
        })
        {
            AutomationProperties.SetName(button, Loc.Get(key));
            ToolTipService.SetToolTip(button, Loc.Get(key));
        }

        for (int i = 1; i <= BookmarkColor.PaletteSize; i++)
        {
            int index = i;
            var item = new MenuFlyoutItem { Text = Loc.Format("Bookmarks_ColorNumber", i) };
            AutomationProperties.SetAutomationId(item, "Bookmarks_MenuColor" + i);
            item.Click += (_, _) => SetColor(BookmarkColor.Palette(index));
            ColorMenu.Items.Add(item);
        }
    }

    public BookmarkListViewModel Vm { get; }

    /// <summary>ブックマークへ移動する (クリック・Enter)。</summary>
    public event EventHandler<Bookmark>? GoToRequested;

    /// <summary>↑ / ↓ で行を選んだ: プレビューとしてエディタをスクロールする。</summary>
    public event EventHandler<Bookmark>? PreviewRequested;

    /// <summary>編集のフライアウトを開く。<c>Name</c> を指定すると名前の欄にフォーカスを置く。</summary>
    public event EventHandler<(Bookmark Bookmark, FrameworkElement Anchor, bool Rename)>? EditRequested;

    /// <summary>削除した (InfoBar の「元に戻す」を出す)。</summary>
    public event EventHandler<IReadOnlyList<Bookmark>>? Deleted;

    /// <summary>ツールバーの「追加」(カーソル位置・選択範囲にブックマークを付ける)。</summary>
    public event EventHandler? AddRequested;

    /// <summary>ツールバーの「元に戻す」(直近の削除を取り消す)。</summary>
    public event EventHandler? UndoRequested;

    public IReadOnlyList<Bookmark> SelectedBookmarks =>
        [.. List.SelectedItems.OfType<BookmarkRowViewModel>().Select(r => r.Bookmark)];

    /// <summary>一覧にフォーカスを置く (F6 で領域に入ったとき)。</summary>
    public bool FocusList()
    {
        if (Vm.Rows.Count == 0)
        {
            return FilterBox.Focus(FocusState.Keyboard);
        }

        if (List.SelectedIndex < 0)
        {
            List.SelectedIndex = 0;
        }

        List.UpdateLayout();
        return List.ContainerFromIndex(List.SelectedIndex) is ListViewItem item ? item.Focus(FocusState.Keyboard) : List.Focus(FocusState.Keyboard);
    }

    public bool ContainsFocus()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == this)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>行を選ぶ (テスト用の命令の通り道)。</summary>
    internal void SelectBookmarks(IEnumerable<Bookmark> bookmarks)
    {
        List.SelectedItems.Clear();
        foreach (Bookmark b in bookmarks)
        {
            int index = Vm.Rows.IndexOf(b);
            if (index >= 0)
            {
                List.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(index, 1));
            }
        }
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is BookmarkRowViewModel row && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetName(container, row.AutomationName);
        }
    }

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkRowViewModel row)
        {
            _clicking = true;
            GoToRequested?.Invoke(this, row.Bookmark);
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_clicking && List.SelectedItems.Count == 1 && List.SelectedItem is BookmarkRowViewModel row)
        {
            PreviewRequested?.Invoke(this, row.Bookmark);
        }

        _clicking = false;
    }

    // ↑ / ↓・Shift+↑ / ↓・Ctrl+A は一覧の標準の操作に任せる (INSP-26 の仕様 5)。
    private void List_KeyDown(object sender, KeyRoutedEventArgs e) => e.Handled = e.Key is VirtualKey.Enter or VirtualKey.Delete && HandleKey(e.Key);

    /// <summary>一覧のキー (実際のキー入力とテスト用の命令の通り道の両方から呼ぶ)。処理したら true。</summary>
    internal bool HandleKey(VirtualKey key, bool ctrl = false)
    {
        switch (key)
        {
            case VirtualKey.Enter when List.SelectedItem is BookmarkRowViewModel row:
                GoToRequested?.Invoke(this, row.Bookmark);
                return true;
            case VirtualKey.Delete when List.SelectedItems.Count > 0:
                DeleteSelected();
                return true;
            case VirtualKey.Down or VirtualKey.Up when Vm.Rows.Count > 0:
            {
                int index = Math.Clamp(List.SelectedIndex + (key == VirtualKey.Down ? 1 : -1), 0, Vm.Rows.Count - 1);
                List.SelectedIndex = index;
                List.ScrollIntoView(Vm.Rows[index]);
                List.UpdateLayout();
                (List.ContainerFromIndex(index) as ListViewItem)?.Focus(FocusState.Keyboard);
                return true;
            }

            case VirtualKey.A when ctrl && Vm.Rows.Count > 0:
                List.SelectAll();
                return true;
            default:
                return false;
        }
    }

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is BookmarkRowViewModel row && !List.SelectedItems.Contains(row))
        {
            List.SelectedItem = row;
        }
    }

    private void RowMenu_Opening(object? sender, object e)
    {
        bool any = List.SelectedItems.Count > 0;
        foreach (MenuFlyoutItemBase item in RowMenu.Items)
        {
            if (item is Control c)
            {
                c.IsEnabled = any;
            }
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke(this, EventArgs.Empty);

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void Edit_Click(object sender, RoutedEventArgs e) => RequestEdit(rename: false);

    private void MenuRename_Click(object sender, RoutedEventArgs e) => RequestEdit(rename: true);

    private void RequestEdit(bool rename)
    {
        if (List.SelectedItem is BookmarkRowViewModel row)
        {
            FrameworkElement anchor = List.ContainerFromItem(row) as FrameworkElement ?? List;
            EditRequested?.Invoke(this, (row.Bookmark, anchor, rename));
        }
    }

    private void MenuGo_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is BookmarkRowViewModel row)
        {
            GoToRequested?.Invoke(this, row.Bookmark);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    /// <summary>選んだ行をまとめて削除する (INSP-26 の仕様 5・6)。</summary>
    public void DeleteSelected()
    {
        IReadOnlyList<Bookmark> selected = SelectedBookmarks;
        if (selected.Count == 0 || Vm.Bookmarks is not { } bookmarks)
        {
            return;
        }

        int index = List.SelectedIndex;
        bookmarks.RemoveRange([.. selected]);
        Deleted?.Invoke(this, selected);
        if (Vm.Rows.Count > 0)
        {
            List.SelectedIndex = Math.Clamp(index, 0, Vm.Rows.Count - 1);
        }
    }

    private void SetColor(BookmarkColor color)
    {
        if (Vm.Bookmarks is not { } bookmarks)
        {
            return;
        }

        foreach (Bookmark b in SelectedBookmarks)
        {
            bookmarks.SetColor(b, color);
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out BookmarkSortColumn column))
        {
            Vm.Sort(column);
        }
    }
}
