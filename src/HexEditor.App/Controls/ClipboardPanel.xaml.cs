using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>クリップボードパネル (EDIT-28) の表示。貼り付けは作業中の文書に対して <see cref="PasteRequested"/> で頼む。</summary>
public sealed partial class ClipboardPanel : UserControl, Panels.IPanelContent
{
    private ClipboardItemRow? _selected;

    public ClipboardPanel(ClipboardPanelViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        foreach ((Button button, string key) in new[]
        {
            (PasteButton, "ClipboardPanel_PasteButton"), (PasteOverwriteButton, "ClipboardPanel_PasteOverwriteButton"),
            (ClearButton, "ClipboardPanel_ClearButton"), (RenameButton, "ClipboardPanel_RenameButton"), (MoveButton, "ClipboardPanel_MoveButton"),
        })
        {
            AutomationProperties.SetName(button, Loc.Get(key));
            ToolTipService.SetToolTip(button, Loc.Get(key));
        }

        NameOk.Content = Loc.Get("ClipboardPanel_NameOk");
        AutomationProperties.SetName(NameBox, Loc.Get("ClipboardPanel_NameBox"));
        SlotsHeader.Text = Loc.Get("ClipboardPanel_SlotsHeader");
        HistoryHeader.Text = Loc.Get("ClipboardPanel_HistoryHeader");
        AutomationProperties.SetName(SlotList, SlotsHeader.Text);
        AutomationProperties.SetName(HistoryList, HistoryHeader.Text);
        UpdateButtons();
    }

    public ClipboardPanelViewModel Vm { get; }

    /// <summary>項目の内容を作業中の文書に貼るよう頼む (引数は項目と、上書き貼り付けか)。</summary>
    public event EventHandler<(ClipboardEntry Entry, bool Overwrite)>? PasteRequested;

    bool Panels.IPanelContent.FocusContent() => SlotList.Focus(FocusState.Keyboard);

    private void UpdateButtons()
    {
        bool hasEntry = _selected?.Entry is not null;
        PasteButton.IsEnabled = PasteOverwriteButton.IsEnabled = hasEntry;
        ClearButton.IsEnabled = _selected is { Number: > 0, Entry: not null };
        RenameButton.IsEnabled = _selected is { Number: > 0 };
        MoveButton.IsEnabled = _selected is { HistoryIndex: >= 0 };
    }

    private void SlotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlotList.SelectedItem is ClipboardItemRow row)
        {
            _selected = row;
            HistoryList.SelectedItem = null;
        }

        UpdateButtons();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is ClipboardItemRow row)
        {
            _selected = row;
            SlotList.SelectedItem = null;
        }

        UpdateButtons();
    }

    private void Paste(bool overwrite)
    {
        if (_selected?.Entry is { } entry)
        {
            PasteRequested?.Invoke(this, (entry, overwrite));
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste(overwrite: false);

    private void PasteOverwrite_Click(object sender, RoutedEventArgs e) => Paste(overwrite: true);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Number: > 0 } row)
        {
            Vm.Clipboards.Clear(row.Number);
        }
    }

    private void RenameFlyout_Opening(object? sender, object e) =>
        NameBox.Text = _selected is { Number: > 0 } row ? Vm.Clipboards.NameOf(row.Number) ?? string.Empty : string.Empty;

    private void NameOk_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Number: > 0 } row)
        {
            Vm.Clipboards.SetName(row.Number, NameBox.Text);
        }

        RenameFlyout.Hide();
    }

    private void MoveMenu_Opening(object? sender, object e)
    {
        MoveMenu.Items.Clear();
        foreach (MenuFlyoutItem item in MoveItems("ClipboardPanel_MoveTo"))
        {
            MoveMenu.Items.Add(item);
        }
    }

    /// <summary>「ユーザークリップボードに移す」の 1〜9 の項目。</summary>
    private IEnumerable<MenuFlyoutItem> MoveItems(string automationPrefix)
    {
        for (int n = 1; n <= UserClipboards.SlotCount; n++)
        {
            int number = n;
            var item = new MenuFlyoutItem { Text = Loc.Format("ClipboardPanel_Slot", n) };
            AutomationProperties.SetAutomationId(item, automationPrefix + n);
            item.Click += (_, _) =>
            {
                if (_selected is { HistoryIndex: >= 0 } row)
                {
                    Vm.Clipboards.CopyHistoryToSlot(row.HistoryIndex, number);
                }
            };
            yield return item;
        }
    }

    /// <summary>
    /// 一覧の右クリックメニュー (EDIT-28 の仕様 2: 上書き貼り付けはパネルの右クリックメニューから選ぶ)。押した項目を選び、上部のボタンと同じ
    /// 「貼り付け」「上書き貼り付け」「消去」「名前を付ける」「ユーザークリップボードに移す」を出す。Shift+F10 / アプリケーションキーでも開く。
    /// </summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var list = (ListView)sender;
        if ((args.OriginalSource as FrameworkElement)?.DataContext is ClipboardItemRow row)
        {
            list.SelectedItem = row;
        }

        if (_selected is null || !ReferenceEquals(list.SelectedItem, _selected))
        {
            return;
        }

        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string id, string key, RoutedEventHandler click, bool enabled)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, "ClipboardPanel_Menu_" + id);
            item.Click += click;
            menu.Items.Add(item);
            return item;
        }

        Item("Paste", "ClipboardPanel_PasteButton", Paste_Click, PasteButton.IsEnabled);
        Item("PasteOverwrite", "ClipboardPanel_PasteOverwriteButton", PasteOverwrite_Click, PasteOverwriteButton.IsEnabled);
        if (_selected.Number > 0)
        {
            Item("Clear", "ClipboardPanel_ClearButton", Clear_Click, ClearButton.IsEnabled);
            Item("Rename", "ClipboardPanel_RenameButton", (_, _) => RenameFlyout.ShowAt(RenameButton), RenameButton.IsEnabled);
        }

        if (_selected.HistoryIndex >= 0)
        {
            var move = new MenuFlyoutSubItem { Text = Loc.Get("ClipboardPanel_MoveButton") };
            AutomationProperties.SetAutomationId(move, "ClipboardPanel_Menu_MoveToSlot");
            foreach (MenuFlyoutItem item in MoveItems("ClipboardPanel_Menu_MoveTo"))
            {
                move.Items.Add(item);
            }

            menu.Items.Add(move);
        }

        if (args.TryGetPosition(list, out Windows.Foundation.Point position))
        {
            menu.ShowAt(list, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
        }
        else
        {
            menu.ShowAt(list.ContainerFromItem(_selected) as FrameworkElement ?? list);
        }

        args.Handled = true;
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Paste(overwrite: false);

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Paste(overwrite: false);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete && _selected is { Number: > 0 } row)
        {
            Vm.Clipboards.Clear(row.Number);
            e.Handled = true;
        }
    }
}
