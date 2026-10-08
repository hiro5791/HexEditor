using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// ショートカット一覧 (UI-39)。コマンド登録と今の割り当てから作る。カテゴリごとに、キー・コマンド・有効範囲を表示し、
/// 有効範囲と検索欄で絞り込める。「HTML として保存」「印刷」で書き出す。各行の「変更」で設定画面の「キーボード」を開く。
/// </summary>
public sealed partial class ShortcutsPage : UserControl
{
    private readonly MainWindow _window;
    private readonly TextBox _search = new() { MinWidth = 280 };
    private readonly ComboBox _scope = new() { MinWidth = 160 };
    private readonly CheckBox _unassigned = new();
    private readonly StackPanel _list = new() { Spacing = 2 };

    public ShortcutsPage(MainWindow window)
    {
        _window = window;
        var root = new Grid { Padding = new Thickness(24, 12, 24, 12), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = Loc.Get("Shortcuts_Title"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _search.PlaceholderText = Loc.Get("Keys_SearchPlaceholder");
        AutomationProperties.SetAutomationId(_search, "Shortcuts_Search");
        AutomationProperties.SetName(_search, Loc.Get("Keys_SearchPlaceholder"));
        _search.TextChanged += (_, _) => Refresh();
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Shortcuts_AllScopes"), Tag = null });
        foreach (KeyScope s in KeyScopes.All)
        {
            _scope.Items.Add(new ComboBoxItem { Content = CommandService.ScopeName(s), Tag = s });
        }

        _scope.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_scope, "Shortcuts_Scope");
        AutomationProperties.SetName(_scope, Loc.Get("Keys_ColScope"));
        _scope.SelectionChanged += (_, _) => Refresh();
        _unassigned.Content = Loc.Get("Shortcuts_ShowUnassigned");
        AutomationProperties.SetAutomationId(_unassigned, "Shortcuts_ShowUnassigned");
        _unassigned.Click += (_, _) => Refresh();
        var save = new Button { Content = Loc.Get("Shortcuts_SaveHtml") };
        AutomationProperties.SetAutomationId(save, "Shortcuts_SaveHtml");
        save.Click += async (_, _) => await window.SaveShortcutsHtmlAsync();
        var print = new Button { Content = Loc.Get("Shortcuts_Print") };
        AutomationProperties.SetAutomationId(print, "Shortcuts_Print");
        print.Click += async (_, _) => await window.PrintShortcutsAsync();
        foreach (UIElement e in new UIElement[] { _search, _scope, _unassigned, save, print })
        {
            tools.Children.Add(e);
        }

        var toolsScroll = new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(toolsScroll, 1);
        root.Children.Add(toolsScroll);
        AutomationProperties.SetAutomationId(_list, "Shortcuts_List");
        var scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;
        Refresh();
    }

    /// <summary>今の割り当てで作る行 (絞り込み前)。</summary>
    public static List<ShortcutRow> Rows(bool includeUnassigned) =>
        ShortcutList.Build(CommandService.Keys, CommandService.DisplayName, CommandService.CategoryName, KeyboardLayout.Format, CommandService.ScopeName, includeUnassigned);

    /// <summary>絞り込んだ行。</summary>
    public List<ShortcutRow> FilteredRows()
    {
        KeyScope? scope = (_scope.SelectedItem as ComboBoxItem)?.Tag as KeyScope?;
        return Rows(_unassigned.IsChecked == true)
            .Where(r => scope is null || (r.HasKeys && r.ScopeValue == scope))
            .Where(r => CommandService.Catalog.Find(r.CommandId) is { } c && ShortcutList.Matches(r, _search.Text, CommandService.EnglishName(c),
                string.Join(" ", CommandService.Keys.BindingsFor(c.Id).Select(b => b.Binding.Chord.ToString()))))
            .ToList();
    }

    /// <summary>一覧を作り直す (割り当てを変えたら即座に反映する。UI-39 の受け入れ基準 1)。</summary>
    public void Refresh()
    {
        _list.Children.Clear();
        foreach (var category in FilteredRows().GroupBy(r => r.Category))
        {
            var header = new TextBlock { Text = category.Key, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 12, 0, 4) };
            AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            _list.Children.Add(header);
            foreach (ShortcutRow row in category)
            {
                var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 2, 4, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var keys = new TextBlock { Text = row.Keys, FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(keys, $"ShortcutKeys_{row.CommandId}_{KeyScopes.Name(row.ScopeValue)}");
                var name = new TextBlock { Text = row.Command, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var scope = new TextBlock { Text = row.Scope, VerticalAlignment = VerticalAlignment.Center };
                var change = new Button { Content = Loc.Get("Shortcuts_Change") };
                AutomationProperties.SetName(change, Loc.Format("Keys_EditFor", row.Command));
                AutomationProperties.SetAutomationId(change, "ShortcutChange_" + row.CommandId);
                string commandId = row.CommandId;
                change.Click += (_, _) => _window.OpenKeyboardSettingsFor(commandId);
                Grid.SetColumn(name, 1);
                Grid.SetColumn(scope, 2);
                Grid.SetColumn(change, 3);
                grid.Children.Add(keys);
                grid.Children.Add(name);
                grid.Children.Add(scope);
                grid.Children.Add(change);
                AutomationProperties.SetAutomationId(grid, "ShortcutRow_" + row.CommandId);
                _list.Children.Add(grid);
            }
        }
    }

    /// <summary>HTML (UI-39 の仕様 4)。</summary>
    public string ToHtml() => ShortcutList.ToHtml(FilteredRows(), Loc.Get("Shortcuts_Title"), Loc.Get("Keys_ColKeys"), Loc.Get("Keys_ColCommand"),
        Loc.Get("Keys_ColScope"), System.Globalization.CultureInfo.CurrentUICulture.Name);
}
