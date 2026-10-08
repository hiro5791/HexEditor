using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Commands;

/// <summary>
/// メニューの項目をコマンドにつなぐ (UI-03 の仕様 2、3、UI-16 の仕様 2): <see cref="CommandUi.Id"/> を持つ項目のクリックで
/// コマンドを実行し、ショートカットの表示 (今の割り当て)・有効状態・チェックの状態をコマンドから取る。
/// 表示名とアクセスキーは項目の x:Uid のリソース (言語ごとのアクセスキー付き。UI-03 の仕様 5)。
/// </summary>
public sealed class MenuBinder(CommandHost host)
{
    private readonly List<(MenuFlyoutItem Item, string Id)> _items = [];

    /// <summary>メニュー (サブメニューを含む) の項目をつなぐ。</summary>
    public void Bind(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase item in items)
        {
            if (item is MenuFlyoutSubItem sub)
            {
                Bind(sub.Items);
            }
            else if (item is MenuFlyoutItem menuItem && CommandUi.GetId(menuItem) is { } id)
            {
                _items.Add((menuItem, id));
                menuItem.Click += Item_Click;
            }
        }
    }

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && CommandUi.GetId(item) is { } id)
        {
            await host.ExecuteAsync(id);
        }
    }

    /// <summary>コマンド ID からメニュー項目を探す (テスト用の命令とショートカット一覧から使う)。</summary>
    public MenuFlyoutItem? Find(string id) => _items.FirstOrDefault(i => i.Id == id).Item;

    public IEnumerable<(MenuFlyoutItem Item, string Id)> Items => _items;

    /// <summary>ショートカットの表示を作り直す (割り当て・キーボード配列が変わったとき。UI-03 の受け入れ基準 1)。</summary>
    public void RefreshShortcuts()
    {
        foreach ((MenuFlyoutItem item, string id) in _items)
        {
            item.KeyboardAcceleratorTextOverride = CommandService.ShortcutText(id);
        }
    }

    /// <summary>有効状態とチェックの状態を作り直す (使えないコマンドは無効表示にし、非表示にはしない。UI-03 の仕様 3)。</summary>
    public void RefreshStates()
    {
        foreach ((MenuFlyoutItem item, string id) in _items)
        {
            CommandState state = host.StateOf(id);
            item.IsEnabled = state.Enabled;
            if (state.Checked is bool on)
            {
                if (item is ToggleMenuFlyoutItem toggle)
                {
                    toggle.IsChecked = on;
                }
                else if (item is RadioMenuFlyoutItem radio)
                {
                    radio.IsChecked = on;
                }
            }
        }
    }
}
