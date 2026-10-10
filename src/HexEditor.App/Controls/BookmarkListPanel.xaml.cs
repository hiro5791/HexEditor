using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>ブックマーク一覧の行の見た目を選ぶ (ブックマークかグループ)。</summary>
public sealed partial class BookmarkRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Bookmark { get; set; }

    public DataTemplate? Group { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is BookmarkGroupRowViewModel ? Group : Bookmark;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// ブックマーク一覧のパネル (INSP-26、INSP-27)。表示とキー操作だけを持つ。移動・編集・削除の通知はウィンドウが行う (イベント)。
/// グループがある場合はツリーで表示し、グループの行で表示 / 非表示・色・名前・削除を操作する。
/// </summary>
public sealed partial class BookmarkListPanel : UserControl, Panels.IPanelContent
{
    private bool _clicking;
    private List<Bookmark>? _dragged;

    public BookmarkListPanel(BookmarkListViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Bookmarks_PanelName"));
        AutomationProperties.SetName(List, Loc.Get("Bookmarks_PanelName"));
        foreach ((Button button, string key) in new[]
        {
            (AddButton, "Bookmarks_AddName"), (EditButton, "Bookmarks_EditName"), (DeleteButton, "Bookmarks_DeleteName"), (UndoButton, "Bookmarks_UndoName"),
            (NewGroupButton, "Bookmarks_NewGroupName"), (ImportButton, "Bookmarks_ImportName"), (ExportButton, "Bookmarks_ExportName"),
        })
        {
            AutomationProperties.SetName(button, Loc.Get(key));
            ToolTipService.SetToolTip(button, Loc.Get(key));
        }

        AutomationProperties.SetName(AllDocumentsButton, Loc.Get("Bookmarks_AllDocumentsName"));
        ToolTipService.SetToolTip(AllDocumentsButton, Loc.Get("Bookmarks_AllDocumentsName"));
        BuildHeader();
        // 列の配置はウィンドウの一覧と共有する。パネルの中身は浮動パネルとの間を移るたびに作り直すので、表示している間だけ受ける。
        Loaded += (_, _) =>
        {
            Vm.Layout.PropertyChanged -= Layout_PropertyChanged;
            Vm.Layout.PropertyChanged += Layout_PropertyChanged;
            BuildHeader();
        };
        Unloaded += (_, _) => Vm.Layout.PropertyChanged -= Layout_PropertyChanged;

        var noColor = new MenuFlyoutItem { Text = Loc.Get("Bookmarks_GroupNoColor"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(noColor, "Bookmarks_MenuColorNone");
        noColor.Click += (_, _) => SetColor(null);
        ColorMenu.Items.Add(noColor);
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

    /// <summary>グループを削除した (InfoBar の「元に戻す」を出す。INSP-27 の仕様 4)。</summary>
    public event EventHandler<(BookmarkCollection Owner, BookmarkGroupDeletion Deletion)>? GroupDeleted;

    /// <summary>グループが 8 階層を超える (InfoBar で知らせる)。</summary>
    public event EventHandler? GroupTooDeep;

    /// <summary>ツールバーの「追加」(カーソル位置・選択範囲にブックマークを付ける)。</summary>
    public event EventHandler? AddRequested;

    /// <summary>ツールバーの「元に戻す」(直近の削除を取り消す)。</summary>
    public event EventHandler? UndoRequested;

    /// <summary>「インポート」「エクスポート」(INSP-30)。エクスポートは選んだブックマーク (なければすべて) とグループを渡す。</summary>
    public event EventHandler? ImportRequested;

    public event EventHandler<(IReadOnlyList<Bookmark> Bookmarks, BookmarkGroup? Group)>? ExportRequested;

    /// <summary>「選択範囲にする」(INSP-28 の仕様 2)。</summary>
    public event EventHandler<IReadOnlyList<Bookmark>>? ToSelectionRequested;

    public IReadOnlyList<Bookmark> SelectedBookmarks =>
        [.. List.SelectedItems.OfType<BookmarkRowViewModel>().Select(r => r.Bookmark)];

    /// <summary>選んでいるグループの行 (1 つ選んでいるとき)。</summary>
    public BookmarkGroupRowViewModel? SelectedGroup => List.SelectedItems.Count == 1 ? List.SelectedItem as BookmarkGroupRowViewModel : null;

    /// <summary>一覧にフォーカスを置く (F6 で領域に入ったとき)。</summary>
    bool Panels.IPanelContent.FocusContent() => FocusList();

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

    /// <summary>グループの行を選ぶ (テスト用の命令の通り道)。</summary>
    internal bool SelectGroup(string path)
    {
        List.SelectedItems.Clear();
        int index = Vm.Rows.Entries.ToList().FindIndex(e => e is BookmarkGroupEntry g && g.Group.Path == path);
        if (index < 0)
        {
            return false;
        }

        List.SelectedIndex = index;
        return true;
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.Item is BookmarkRowViewModel row)
        {
            AutomationProperties.SetName(container, row.AutomationName);
            AutomationProperties.SetAutomationId(container, string.Empty);
        }
        else if (args.Item is BookmarkGroupRowViewModel group)
        {
            AutomationProperties.SetName(container, group.AutomationName);
            AutomationProperties.SetAutomationId(container, "Bookmarks_Group_" + group.Path);
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
    private void List_KeyDown(object sender, KeyRoutedEventArgs e) =>
        e.Handled = e.Key is VirtualKey.Enter or VirtualKey.Delete or VirtualKey.Space or VirtualKey.Left or VirtualKey.Right && HandleKey(e.Key);

    /// <summary>一覧のキー (実際のキー入力とテスト用の命令の通り道の両方から呼ぶ)。処理したら true。</summary>
    internal bool HandleKey(VirtualKey key, bool ctrl = false)
    {
        if (SelectedGroup is { } group)
        {
            switch (key)
            {
                // グループの行: Space で表示 / 非表示 (INSP-27 の仕様 3。00-overview 8.6)、Enter と ← / → で折りたたみ・展開、Delete で削除。
                case VirtualKey.Space:
                    ToggleVisible(group);
                    return true;
                case VirtualKey.Enter:
                case VirtualKey.Left when group.IsExpanded:
                case VirtualKey.Right when !group.IsExpanded:
                    ToggleExpanded(group);
                    return true;
                case VirtualKey.Delete:
                    ShowDeleteGroup(group);
                    return true;
            }
        }

        switch (key)
        {
            case VirtualKey.Enter when List.SelectedItem is BookmarkRowViewModel row:
                GoToRequested?.Invoke(this, row.Bookmark);
                return true;
            case VirtualKey.Delete when SelectedBookmarks.Count > 0:
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
        object? item = (args.OriginalSource as FrameworkElement)?.DataContext;
        if (item is BookmarkRowViewModel or BookmarkGroupRowViewModel && !List.SelectedItems.Contains(item))
        {
            List.SelectedItem = item;
        }
    }

    /// <summary>右クリックメニュー: 選んだ行の種類 (ブックマーク・グループ) に合う項目だけを出す。</summary>
    private void RowMenu_Opening(object? sender, object e)
    {
        bool any = SelectedBookmarks.Count > 0;
        BookmarkGroupRowViewModel? group = SelectedGroup;
        foreach (MenuFlyoutItemBase item in RowMenu.Items)
        {
            if (item is Control c)
            {
                c.IsEnabled = any || group is not null;
            }
        }

        foreach (Control c in new Control[] { MenuGo, MenuEdit, MoveGroupMenu, MenuToSelection, OpenInNewTabItem })
        {
            c.Visibility = group is null ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (Control c in new Control[] { MenuGroupVisible, MenuNewSubgroup })
        {
            c.Visibility = group is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        ColorMenu.Items[0].Visibility = group is not null ? Visibility.Visible : Visibility.Collapsed;
        MenuGroupVisible.IsChecked = group?.Group.Visible ?? false;
        MenuNewSubgroup.IsEnabled = group is not null && group.Group.Depth < BookmarkGroups.MaxDepth;
        MenuToSelection.IsEnabled = any;
        BuildMoveGroupMenu();

        // 範囲を持たないブックマークは新しいタブで開けない (ENG-39 の仕様 3)。
        OpenInNewTabItem.IsEnabled = List.SelectedItem is BookmarkRowViewModel { Bookmark.Length: > 0 };
    }

    /// <summary>「グループに移動」のサブメニュー: グループなし、今あるグループ、新しいグループ。</summary>
    private void BuildMoveGroupMenu()
    {
        MoveGroupMenu.Items.Clear();
        if (Vm.Bookmarks is not { } bookmarks)
        {
            return;
        }

        var none = new MenuFlyoutItem { Text = Loc.Get("Bookmark_GroupNone") };
        AutomationProperties.SetAutomationId(none, "Bookmarks_MoveToNoGroup");
        none.Click += (_, _) => MoveSelected(null);
        MoveGroupMenu.Items.Add(none);
        foreach (BookmarkGroup group in bookmarks.Groups.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase))
        {
            string path = group.Path;
            var item = new MenuFlyoutItem { Text = path };
            AutomationProperties.SetAutomationId(item, "Bookmarks_MoveTo_" + path);
            item.Click += (_, _) => MoveSelected(path);
            MoveGroupMenu.Items.Add(item);
        }

        var create = new MenuFlyoutItem { Text = Loc.Get("Bookmarks_MoveToNewGroup") };
        AutomationProperties.SetAutomationId(create, "Bookmarks_MoveToNewGroup");
        create.Click += (_, _) =>
        {
            BookmarkGroup created = bookmarks.CreateGroup(null, Loc.Get("Bookmarks_NewGroupDefaultName"));
            MoveSelected(created.Path);
            ShowRenameGroup(created);
        };
        MoveGroupMenu.Items.Add(create);
    }

    /// <summary>選んだブックマークをグループに入れる (null はグループなし)。</summary>
    internal void MoveSelected(string? path)
    {
        foreach (IGrouping<DocumentAnnotations?, Bookmark> owned in SelectedBookmarks.GroupBy(Vm.OwnerOf))
        {
            owned.Key?.Bookmarks.MoveToGroup([.. owned], path);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke(this, EventArgs.Empty);

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void Edit_Click(object sender, RoutedEventArgs e) => RequestEdit(rename: false);

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group)
        {
            ShowRenameGroup(group.Group);
        }
        else
        {
            RequestEdit(rename: true);
        }
    }

    private void RequestEdit(bool rename)
    {
        if (List.SelectedItem is BookmarkRowViewModel row)
        {
            FrameworkElement anchor = List.ContainerFromItem(row) as FrameworkElement ?? List;
            EditRequested?.Invoke(this, (row.Bookmark, anchor, rename));
        }
    }

    /// <summary>ブックマークの範囲を新しいタブで開く (ENG-39 の仕様 3)。</summary>
    public event EventHandler<Bookmark>? OpenInNewTabRequested;

    private void OpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is BookmarkRowViewModel { Bookmark.Length: > 0 } row)
        {
            OpenInNewTabRequested?.Invoke(this, row.Bookmark);
        }
    }

    private void MenuGo_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is BookmarkRowViewModel row)
        {
            GoToRequested?.Invoke(this, row.Bookmark);
        }
    }

    private void MenuToSelection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmarks is { Count: > 0 } selected)
        {
            ToSelectionRequested?.Invoke(this, selected);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>エクスポートの対象 (INSP-30 の仕様 5): 選んだグループ、選んだブックマーク、何も選んでいなければすべて。</summary>
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, (SelectedBookmarks, SelectedGroup?.Group));

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group)
        {
            ShowDeleteGroup(group);
        }
        else
        {
            DeleteSelected();
        }
    }

    /// <summary>選んだ行をまとめて削除する (INSP-26 の仕様 5・6)。</summary>
    public void DeleteSelected()
    {
        IReadOnlyList<Bookmark> selected = SelectedBookmarks;
        if (selected.Count == 0)
        {
            return;
        }

        int index = List.SelectedIndex;
        foreach (IGrouping<DocumentAnnotations?, Bookmark> owned in selected.GroupBy(Vm.OwnerOf))
        {
            owned.Key?.Bookmarks.RemoveRange([.. owned]);
        }

        Deleted?.Invoke(this, selected);
        if (Vm.Rows.Count > 0)
        {
            List.SelectedIndex = Math.Clamp(index, 0, Vm.Rows.Count - 1);
        }
    }

    /// <summary>色を変える: グループの行ならグループの色 (null は色なし。INSP-27 の仕様 2)、ブックマークならまとめて (INSP-26 の仕様 6)。</summary>
    private void SetColor(BookmarkColor? color)
    {
        if (SelectedGroup is { } group)
        {
            group.Owner.SetGroupColor(group.Group, color);
            return;
        }

        if (color is not { } c)
        {
            return;
        }

        foreach (Bookmark b in SelectedBookmarks)
        {
            Vm.OwnerOf(b)?.Bookmarks.SetColor(b, c);
        }
    }

    // ---- グループ (INSP-27) ----

    private void ToggleVisible(BookmarkGroupRowViewModel group) => group.Owner.SetGroupVisible(group.Group, !group.Group.Visible);

    private void ToggleExpanded(BookmarkGroupRowViewModel group)
    {
        string path = group.Path;
        Vm.ToggleExpanded(group.Group);
        SelectGroup(path);
        FocusList();
    }

    private void GroupVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BookmarkGroupRowViewModel group })
        {
            ToggleVisible(group);
        }
    }

    private void GroupChevron_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BookmarkGroupRowViewModel group })
        {
            e.Handled = true;
            ToggleExpanded(group);
        }
    }

    private void MenuGroupVisible_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group)
        {
            ToggleVisible(group);
        }
    }

    /// <summary>「グループの作成」: 選んでいるグループの下 (なければ最上位) に作り、名前の入力を出す。</summary>
    private void NewGroup_Click(object sender, RoutedEventArgs e) => CreateGroup(SelectedGroup?.Path, showRename: true);

    /// <summary>グループを作る (テスト用の命令からも呼ぶ)。9 階層目なら作らずに知らせる。</summary>
    internal BookmarkGroup? CreateGroup(string? parent, bool showRename, string? name = null)
    {
        if (Vm.Bookmarks is not { } bookmarks)
        {
            return null;
        }

        try
        {
            BookmarkGroup group = bookmarks.CreateGroup(parent, name ?? Loc.Get("Bookmarks_NewGroupDefaultName"));
            if (showRename)
            {
                DispatcherQueue.TryEnqueue(() => ShowRenameGroup(group));
            }

            return group;
        }
        catch (BookmarkGroupDepthException)
        {
            GroupTooDeep?.Invoke(this, EventArgs.Empty);
            return null;
        }
    }

    private FrameworkElement AnchorOf(BookmarkGroup group)
    {
        int index = Vm.Rows.IndexOf(group);
        if (index >= 0)
        {
            List.ScrollIntoView(Vm.Rows[index]);
            List.UpdateLayout();
        }

        return index >= 0 && List.ContainerFromIndex(index) is FrameworkElement container ? container : List;
    }

    /// <summary>グループの名前を変えるフライアウト。</summary>
    private void ShowRenameGroup(BookmarkGroup group)
    {
        var box = new TextBox { Text = group.Name, Width = 220, Header = Loc.Get("Bookmarks_GroupNameHeader") };
        AutomationProperties.SetAutomationId(box, "Bookmarks_GroupName");
        var ok = new Button { Content = Loc.Get("Bookmarks_GroupRename"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(ok, "Bookmarks_GroupRenameOk");
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { box, ok } } };
        void Apply()
        {
            BookmarkCollection? owner = Vm.Bookmarks;
            owner?.RenameGroup(group, box.Text);
            flyout.Hide();
            SelectGroup(group.Path);
        }

        ok.Click += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Apply();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.ShowAt(AnchorOf(group));
    }

    /// <summary>
    /// グループの削除 (INSP-27 の仕様 4): 「中のブックマークも削除する」「中のブックマークを親のグループに移す」を選ぶフライアウト。
    /// </summary>
    internal void ShowDeleteGroup(BookmarkGroupRowViewModel group)
    {
        var text = new TextBlock { Text = Loc.Format("Bookmarks_DeleteGroupPrompt", group.Name, group.CountText), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        var deleteAll = new Button { Content = Loc.Get("Bookmarks_DeleteGroupWithContents"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(deleteAll, "Bookmarks_DeleteGroupWithContents");
        var moveUp = new Button { Content = Loc.Get("Bookmarks_DeleteGroupMoveToParent"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(moveUp, "Bookmarks_DeleteGroupMoveToParent");
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { text, moveUp, deleteAll } } };
        AutomationProperties.SetAutomationId(flyout.Content as StackPanel, "Bookmarks_DeleteGroupFlyout");
        deleteAll.Click += (_, _) =>
        {
            flyout.Hide();
            DeleteGroup(group, deleteContents: true);
        };
        moveUp.Click += (_, _) =>
        {
            flyout.Hide();
            DeleteGroup(group, deleteContents: false);
        };
        flyout.ShowAt(AnchorOf(group.Group));
    }

    /// <summary>グループを削除して、取り消しの通知を出す。</summary>
    internal void DeleteGroup(BookmarkGroupRowViewModel group, bool deleteContents)
    {
        BookmarkGroupDeletion deletion = group.Owner.DeleteGroup(group.Group, deleteContents);
        GroupDeleted?.Invoke(this, (group.Owner, deletion));
        FocusList();
    }

    // ---- ドラッグでグループに入れる (INSP-27 の仕様 5) ----

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragged = [.. e.Items.OfType<BookmarkRowViewModel>().Select(r => r.Bookmark)];
        if (_dragged.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.Data.SetText(string.Join(", ", _dragged.Select(b => b.Name)));
    }

    private void Group_DragOver(object sender, DragEventArgs e)
    {
        if (_dragged is { Count: > 0 })
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = Loc.Get("Bookmarks_DropToGroup");
        }
    }

    private void Group_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BookmarkGroupRowViewModel group } && _dragged is { Count: > 0 } dragged)
        {
            group.Owner.MoveToGroup(dragged, group.Path);
        }

        _dragged = null;
    }

    // ---- 列の見出し (INSP-26 の仕様 1・2) ----

    /// <summary>右クリック (Shift+F10) した見出しの列 (列の移動の対象)。</summary>
    private BookmarkColumn? _menuColumn;

    private void Layout_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => BuildHeader();

    /// <summary>見出しを列の配置に合わせて作り直す。</summary>
    private void BuildHeader()
    {
        BookmarkColumnLayout layout = Vm.Layout;
        Header.ColumnDefinitions.Clear();
        Header.Children.Clear();
        foreach (Microsoft.UI.Xaml.GridLength width in new[] { layout.Width0, layout.Width1, layout.Width2, layout.Width3, layout.Width4, layout.Width5, layout.Width6, layout.Width7 })
        {
            Header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        for (int i = 0; i < layout.Order.Count; i++)
        {
            BookmarkColumn? column = layout.Order[i];
            string text = Loc.Get(column is { } c ? "Bookmarks_Column" + c : "Bookmarks_ColumnDocument");
            var button = new HyperlinkButton
            {
                Content = column == BookmarkColumn.Color ? "●" : text,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(button, Loc.Format("Bookmarks_SortBy", text));
            ToolTipService.SetToolTip(button, Loc.Format("Bookmarks_SortBy", text));
            AutomationProperties.SetAutomationId(button, column is { } id ? "Bookmarks_Sort" + id : "Bookmarks_SortDocument");
            if (column is { } sortable && SortOf(sortable) is { } sort)
            {
                button.Click += (_, _) => Vm.Sort(sort);
            }
            else
            {
                button.IsEnabled = column is not null;
            }

            button.ContextRequested += (_, _) => _menuColumn = column;
            Grid.SetColumn(button, i);
            Header.Children.Add(button);
        }
    }

    private static BookmarkSortColumn? SortOf(BookmarkColumn column) => column switch
    {
        BookmarkColumn.Number => BookmarkSortColumn.Number,
        BookmarkColumn.Color => BookmarkSortColumn.Color,
        BookmarkColumn.Name => BookmarkSortColumn.Name,
        BookmarkColumn.Start => BookmarkSortColumn.Start,
        BookmarkColumn.Length => BookmarkSortColumn.Length,
        BookmarkColumn.Group => BookmarkSortColumn.Group,
        BookmarkColumn.Comment => BookmarkSortColumn.Comment,
        _ => null,
    };

    private void Header_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not HyperlinkButton)
        {
            _menuColumn = null;
        }
    }

    /// <summary>見出しの右クリックメニュー: 列ごとの表示の切り替えと、右クリックした列の左右への移動。</summary>
    private void ColumnsMenu_Opening(object? sender, object e)
    {
        ColumnsMenu.Items.Clear();
        foreach ((BookmarkColumn column, bool visible) in BookmarkColumns.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = Loc.Get("Bookmarks_Column" + column),
                IsChecked = visible,
                IsEnabled = column != BookmarkColumn.Name,
            };
            AutomationProperties.SetAutomationId(item, "Bookmarks_ShowColumn" + column);
            item.Click += (_, _) => BookmarkColumns.Toggle(column);
            ColumnsMenu.Items.Add(item);
        }

        ColumnsMenu.Items.Add(new MenuFlyoutSeparator());
        foreach ((string key, int delta) in new[] { ("Bookmarks_MoveColumnLeft", -1), ("Bookmarks_MoveColumnRight", 1) })
        {
            var move = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = _menuColumn is not null };
            AutomationProperties.SetAutomationId(move, key);
            BookmarkColumn? target = _menuColumn;
            move.Click += (_, _) =>
            {
                if (target is { } c)
                {
                    BookmarkColumns.Move(c, delta);
                }
            };
            ColumnsMenu.Items.Add(move);
        }

        var reset = new MenuFlyoutItem { Text = Loc.Get("Bookmarks_ResetColumns") };
        AutomationProperties.SetAutomationId(reset, "Bookmarks_ResetColumns");
        reset.Click += (_, _) => BookmarkColumns.Reset();
        ColumnsMenu.Items.Add(reset);
    }

    /// <summary>見出しの並び (テスト用): 表示している列の見出しの文字列。</summary>
    internal IReadOnlyList<string> HeaderTexts => [.. Header.Children.OfType<HyperlinkButton>().OrderBy(Grid.GetColumn).Select(b => b.Content as string ?? string.Empty)];
}
