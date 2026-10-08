using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ツールバー (UI-04): 設定 <c>ui.toolbar.items</c> のコマンドをアイコンで並べる。既定では非表示。入りきらないボタンは
/// CommandBar の「…」メニューに入る (仕様 4)。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterToolbarCommands()
    {
        Commands.Register("view.toolbar", () =>
        {
            App.Settings.SetBool(ToolbarItems.VisibleKey, !App.Settings.GetBool(ToolbarItems.VisibleKey, false), false);
            RefreshToolbar();
        }, () => Toggle(App.Settings.GetBool(ToolbarItems.VisibleKey, false)));
        Commands.Register("view.customizeToolbar", CustomizeToolbarAsync);
    }

    /// <summary>今の構成のコマンド ID (存在しないものを含む)。</summary>
    public IReadOnlyList<string> ToolbarItemIds => ToolbarItems.Parse(App.Settings.GetNode(ToolbarItems.ItemsKey));

    private void SetToolbarItems(IEnumerable<string> ids)
    {
        App.Settings.SetNode(ToolbarItems.ItemsKey, new JsonArray([.. ids.Select(i => (JsonNode?)i)]), ToolbarItems.DefaultJson());
        RefreshToolbar();
    }

    /// <summary>ボタンを作り直す (構成・割り当て・表示の設定が変わったとき)。</summary>
    private void RefreshToolbar()
    {
        bool visible = App.Settings.GetBool(ToolbarItems.VisibleKey, false);
        Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.PrimaryCommands.Clear();
        if (!visible)
        {
            return;
        }

        (IReadOnlyList<string> shown, _) = ToolbarItems.Resolve(ToolbarItemIds, CommandService.Catalog);
        foreach (string id in shown)
        {
            CommandDefinition command = CommandService.Catalog.Find(id)!;
            string name = CommandService.DisplayName(command);
            string keys = CommandService.ShortcutText(id);
            var button = new AppBarButton
            {
                Label = name,
                Tag = id,
                Icon = command.Icon is { } glyph
                    ? new FontIcon { Glyph = glyph }
                    : new FontIcon { Glyph = ToolbarItems.FallbackIcon(name), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI") },
            };

            // ツールチップは「表示名 (ショートカット)」(仕様 3)。
            ToolTipService.SetToolTip(button, keys.Length > 0 ? Loc.Format("Toolbar_ToolTip", name, keys) : name);
            AutomationProperties.SetAutomationId(button, "ToolbarButton_" + id);
            AutomationProperties.SetName(button, name);
            button.Click += async (_, _) => await Commands.ExecuteAsync(id);
            Toolbar.PrimaryCommands.Add(button);
        }

        RefreshToolbarStates();
    }

    private void RefreshToolbarStates()
    {
        foreach (AppBarButton button in Toolbar.PrimaryCommands.OfType<AppBarButton>())
        {
            button.IsEnabled = Commands.StateOf((string)button.Tag).Enabled;
        }
    }

    /// <summary>「ツールバーのカスタマイズ」(仕様 2): コマンドの追加・削除・並べ替え。変更はその場で反映する。</summary>
    private async Task CustomizeToolbarAsync()
    {
        var items = new List<string>(ToolbarItemIds);
        var list = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "ToolbarCustomize_Items");
        AutomationProperties.SetName(list, Loc.Get("ToolbarCustomize_Items"));
        var all = CommandService.Catalog.All.Where(c => !c.Hidden).ToList();
        var picker = new ComboBox
        {
            ItemsSource = all.Select(CommandService.Title).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = Loc.Get("ToolbarCustomize_Pick"),
        };
        AutomationProperties.SetAutomationId(picker, "ToolbarCustomize_Pick");
        AutomationProperties.SetName(picker, Loc.Get("ToolbarCustomize_Pick"));

        void Fill(int select)
        {
            list.ItemsSource = items.Select(id => CommandService.Catalog.Find(id) is { } c ? CommandService.Title(c) : Loc.Format("ToolbarCustomize_Missing", id)).ToList();
            list.SelectedIndex = Math.Min(select, items.Count - 1);
        }

        void Apply(int select)
        {
            SetToolbarItems(items);
            Fill(select);
        }

        Button MakeButton(string key, Action action)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, key);
            b.Click += (_, _) => action();
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(MakeButton("ToolbarCustomize_Up", () =>
        {
            int i = list.SelectedIndex;
            if (i > 0)
            {
                (items[i - 1], items[i]) = (items[i], items[i - 1]);
                Apply(i - 1);
            }
        }));
        buttons.Children.Add(MakeButton("ToolbarCustomize_Down", () =>
        {
            int i = list.SelectedIndex;
            if (i >= 0 && i < items.Count - 1)
            {
                (items[i + 1], items[i]) = (items[i], items[i + 1]);
                Apply(i + 1);
            }
        }));
        buttons.Children.Add(MakeButton("ToolbarCustomize_Remove", () =>
        {
            int i = list.SelectedIndex;
            if (i >= 0)
            {
                items.RemoveAt(i);
                Apply(i);
            }
        }));
        buttons.Children.Add(MakeButton("ToolbarCustomize_Reset", () =>
        {
            items = [.. ToolbarItems.Default];
            Apply(0);
        }));

        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(picker);
        add.Children.Add(MakeButton("ToolbarCustomize_Add", () =>
        {
            if (picker.SelectedIndex >= 0)
            {
                items.Add(all[picker.SelectedIndex].Id);
                Apply(items.Count - 1);
            }
        }));

        var body = new StackPanel { Spacing = 12, MinWidth = 420 };
        body.Children.Add(list);
        body.Children.Add(buttons);
        body.Children.Add(add);
        Fill(0);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("ToolbarCustomize_Title"),
            Content = body,
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ToolbarCustomizeDialog");
        await dialog.ShowAsync();
    }

    /// <summary>ボタンを末尾に足す (カスタマイズの「追加」と同じ。テスト用の命令からも使う)。</summary>
    public void AddToolbarItem(string id) => SetToolbarItems([.. ToolbarItemIds, id]);
}
