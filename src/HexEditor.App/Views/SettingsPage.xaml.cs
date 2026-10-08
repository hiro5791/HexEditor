using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Notifications;
using HexEditor.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面 (UI-22)。汎用の項目は <see cref="CommandService.Settings"/> の定義から、専用の区画は <see cref="SettingsSections"/> から作る。
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly MainWindow _window;
    private string _category = SettingCategories.General;
    private readonly Action<IReadOnlyCollection<string>> _settingsChanged;

    public SettingsPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        BuiltInSections.EnsureRegistered();
        foreach (string category in SettingCategories.All)
        {
            var item = new NavigationViewItem { Content = CategoryName(category), Tag = category };
            AutomationProperties.SetAutomationId(item, "SettingsCategory_" + category);
            Nav.MenuItems.Add(item);
        }

        Nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string category })
            {
                Search.Text = string.Empty;
                ShowCategory(category);
            }
        };
        Search.TextChanged += (_, _) => Refresh();
        RestartButton.Click += async (_, _) => await _window.RestartAsync();

        // 外部の編集・他の画面での変更も反映する (変更の印と値)。
        _settingsChanged = _ => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_editing)
            {
                Refresh();
            }
        });
        App.Settings.Changed += _settingsChanged;
        Unloaded += (_, _) => App.Settings.Changed -= _settingsChanged;
        Loaded += (_, _) => App.Settings.Changed -= _settingsChanged;
        Loaded += (_, _) => App.Settings.Changed += _settingsChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private bool _editing;

    public string Category => _category;

    public static string CategoryName(string category) => Loc.Get("SetCategory_" + category);

    /// <summary>カテゴリを表示する。</summary>
    public void ShowCategory(string category)
    {
        _category = category;
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == category) is { } item && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item;
        }

        Refresh();
    }

    /// <summary>項目を開いてフォーカスを置く (コマンドパレットの # モード。UI-17 の仕様 2)。</summary>
    public void FocusSetting(string key)
    {
        if (CommandService.Settings.Find(key) is { } def)
        {
            ShowCategory(def.Category);
            if (FindByAutomationId(Items, "SettingControl_" + key) is Control control)
            {
                control.StartBringIntoView();
                control.Focus(FocusState.Programmatic);
            }
        }
    }

    public void SetSearch(string text)
    {
        Search.Text = text;
        Refresh();
    }

    /// <summary>今の表示を作り直す (検索中なら検索の結果)。</summary>
    public void Refresh()
    {
        Items.Children.Clear();
        string query = Search.Text ?? string.Empty;
        if (query.Trim().Length > 0)
        {
            ShowSearchResults(query);
            return;
        }

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = CategoryName(_category), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 8, 0, 8) });

        // 「…」>「このカテゴリを既定に戻す」(UI-24 の仕様 2)。
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(more, "SettingsCategoryMore");
        AutomationProperties.SetName(more, Loc.Get("Settings_CategoryMore"));
        ToolTipService.SetToolTip(more, Loc.Get("Settings_CategoryMore"));
        var menu = new MenuFlyout();
        var reset = new MenuFlyoutItem { Text = Loc.Get("Settings_ResetCategory") };
        AutomationProperties.SetAutomationId(reset, "SettingsResetCategory");
        reset.Click += async (_, _) => await _window.ResetCategoryAsync(_category);
        menu.Items.Add(reset);
        more.Flyout = menu;
        Grid.SetColumn(more, 1);
        header.Children.Add(more);
        Items.Children.Add(header);

        var settings = CommandService.Settings.InCategory(_category).Where(CommandService.Settings.IsShown).ToList();
        string? group = null;
        foreach (SettingDefinition s in settings)
        {
            // 欄の見出し (例: 「言語」の「翻訳者向け」。UI-41 の仕様 5)。
            if (s.Group is { } g && g != group)
            {
                var heading = new TextBlock { Text = Loc.Get("SetGroup_" + g), Style = (Style)Resources["SectionHeaderStyle"] };
                AutomationProperties.SetAutomationId(heading, "SettingsGroup_" + g);
                AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
                Items.Children.Add(heading);
            }

            group = s.Group;
            Items.Children.Add(BuildCard(s, []));
        }

        foreach (SettingsSection section in SettingsSections.In(_category))
        {
            Items.Children.Add(new TextBlock { Text = Loc.Get(section.TitleKey), Style = (Style)Resources["SectionHeaderStyle"] });
            FrameworkElement content = section.Factory(_window);
            AutomationProperties.SetAutomationId(content, "SettingsSection_" + section.Id);
            Items.Children.Add(content);
        }

        if (settings.Count == 0 && !SettingsSections.In(_category).Any())
        {
            Items.Children.Add(new TextBlock { Text = Loc.Get("Settings_EmptyCategory"), Style = (Style)Resources["SettingDescriptionStyle"] });
        }
    }

    /// <summary>検索 (UI-22 の仕様 4): カテゴリをまたいで、一致した項目をカテゴリ名つきで表示する。一致した文字を強調する。</summary>
    private void ShowSearchResults(string query)
    {
        var results = CommandService.Settings.Search(query, Texts);
        var sections = SettingsSections.All.Where(s => new[] { Loc.Get(s.TitleKey) }.Concat(s.SearchKeys.Select(Loc.Get))
            .Concat(s.SearchKeys.Select(k => Loc.English(k) ?? string.Empty)).Append(Loc.English(s.TitleKey) ?? string.Empty)
            .Any(t => Core.Commands.SearchText.Normalize(t).Contains(Core.Commands.SearchText.Normalize(query.Trim()), StringComparison.Ordinal))).ToList();
        if (results.Count == 0 && sections.Count == 0)
        {
            var none = new TextBlock { Text = Loc.Get("Settings_NoResults"), Style = (Style)Resources["SettingDescriptionStyle"] };
            AutomationProperties.SetAutomationId(none, "Settings_NoResults");
            Items.Children.Add(none);
            return;
        }

        foreach (var group in results.GroupBy(r => r.Setting.Category))
        {
            Items.Children.Add(new TextBlock { Text = CategoryName(group.Key), Style = (Style)Resources["SectionHeaderStyle"] });
            foreach (SettingSearchResult r in group)
            {
                Items.Children.Add(BuildCard(r.Setting, r.NamePositions));
            }
        }

        foreach (SettingsSection section in sections)
        {
            var link = new HyperlinkButton { Content = Loc.Format("Settings_SectionLink", CategoryName(section.Category), Loc.Get(section.TitleKey)) };
            AutomationProperties.SetAutomationId(link, "SettingsSectionLink_" + section.Id);
            link.Click += (_, _) =>
            {
                Search.Text = string.Empty;
                ShowCategory(section.Category);
            };
            Items.Children.Add(link);
        }
    }

    /// <summary>名前・説明・英語の名前 (リソース <c>Set_…</c>、<c>SetDesc_…</c>)。</summary>
    public static SettingTexts Texts(SettingDefinition s)
    {
        string part = Core.Commands.CommandDefinition.KeyPart(s.Key);
        return new(Loc.Get("Set_" + part), Loc.Get("SetDesc_" + part), Loc.English("Set_" + part) ?? string.Empty);
    }

    private Border BuildCard(SettingDefinition def, IReadOnlyList<int> highlight)
    {
        bool modified = App.Settings.Contains(def.Key);
        var card = new Border { Style = (Style)Resources["SettingCardStyle"] };
        AutomationProperties.SetAutomationId(card, "Setting_" + def.Key);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 変更の印 (左端の縦線)。変更していなければ場所だけ取る。
        var mark = new Border { Style = (Style)Resources["ModifiedMarkStyle"], Opacity = modified ? 1 : 0 };
        AutomationProperties.SetAutomationId(mark, "SettingModified_" + def.Key);
        AutomationProperties.SetAccessibilityView(mark, modified ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (modified)
        {
            AutomationProperties.SetName(mark, Loc.Get("Settings_Modified"));
        }

        grid.Children.Add(mark);

        SettingTexts texts = Texts(def);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AddHighlighted(name, texts.Name, highlight);
        AutomationProperties.SetAutomationId(name, "SettingName_" + def.Key);
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = texts.Description, Style = (Style)Resources["SettingDescriptionStyle"] });
        var error = new TextBlock { Style = (Style)Resources["SettingErrorStyle"], Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(error, "SettingError_" + def.Key);
        text.Children.Add(error);
        if (def.RequiresRestart && _window.PendingRestartKeys.Contains(def.Key))
        {
            var note = new TextBlock { Text = Loc.Get("Settings_RestartNote"), Style = (Style)Resources["SettingNoteStyle"] };
            AutomationProperties.SetAutomationId(note, "SettingRestart_" + def.Key);
            text.Children.Add(note);
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        FrameworkElement control = BuildControl(def, texts.Name, error);
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(control, "SettingControl_" + def.Key);
        AutomationProperties.SetName(control, texts.Name);
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);

        // 「既定に戻す」(変更時だけ。UI-22 の仕様 3、UI-24 の仕様 1)。
        var reset = new Button { Content = Loc.Get("Settings_ResetItem"), VerticalAlignment = VerticalAlignment.Center, Visibility = modified ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetAutomationId(reset, "SettingReset_" + def.Key);
        reset.Click += (_, _) => _window.ResetSetting(def.Key);
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);
        card.Child = grid;
        return card;
    }

    private FrameworkElement BuildControl(SettingDefinition def, string label, TextBlock error)
    {
        JsonNode? value = App.Settings.GetNode(def.Key) ?? def.Default?.DeepClone();
        switch (def.Kind)
        {
            case SettingKind.Bool:
            {
                var toggle = new ToggleSwitch { IsOn = value is JsonValue v && v.TryGetValue(out bool b) && b, MinWidth = 0, OnContent = Loc.Get("Settings_On"), OffContent = Loc.Get("Settings_Off") };
                toggle.Toggled += (_, _) => Commit(def, toggle.IsOn, error);
                return toggle;
            }

            case SettingKind.Choice:
            {
                var combo = new ComboBox { MinWidth = 200 };
                foreach (string option in def.Options)
                {
                    combo.Items.Add(new ComboBoxItem { Content = OptionLabel(def, option), Tag = option });
                }

                string current = value is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
                combo.SelectedIndex = Math.Max(0, def.Options.ToList().IndexOf(current));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string option })
                    {
                        Commit(def, option, error);
                    }
                };
                return combo;
            }

            case SettingKind.Int or SettingKind.Number:
            {
                double number = SettingDefinition.TryGetNumber(value, out double d) ? d : 0;
                var box = new NumberBox { Value = number, MinWidth = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = def.Min ?? double.MinValue, Maximum = def.Max ?? double.MaxValue, ValidationMode = NumberBoxValidationMode.Disabled };

                // 入力が確定した時点 (Enter またはフォーカスが外れたとき) に反映する (UI-22 の仕様 5)。
                box.ValueChanged += (_, e) =>
                {
                    if (double.IsNaN(e.NewValue))
                    {
                        ShowError(error, box, Loc.Get("Settings_InvalidNumber"));
                        return;
                    }

                    JsonNode node = def.Kind == SettingKind.Int ? JsonValue.Create((int)Math.Round(e.NewValue)) : JsonValue.Create(e.NewValue);
                    Commit(def, node, error, box);
                };
                return box;
            }

            default:
            {
                var box = new TextBox { Text = value is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty, MinWidth = 240 };
                void CommitText() => Commit(def, box.Text, error, box);
                box.LostFocus += (_, _) => CommitText();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        CommitText();
                        e.Handled = true;
                    }
                };
                return box;
            }
        }
    }

    /// <summary>
    /// 選択肢の表示名。表示言語 (UI-43 の仕様 2) は「Windows の設定に従う (現在: 言語名)」と、各言語の自分の言語での名前・今の表示言語での名前・
    /// 確認済みの割合。
    /// </summary>
    public static string OptionLabel(SettingDefinition def, string option) =>
        def.Key == Platform.Localization.DisplayLanguages.SettingKey
            ? MainWindow.DisplayLanguageItems().FirstOrDefault(i => i.Tag.Equals(option, StringComparison.OrdinalIgnoreCase)).Text
                ?? CultureInfo.GetCultureInfo(option).NativeName
            : Loc.Get("SetOpt_" + Core.Commands.CommandDefinition.KeyPart(def.Key) + "_" + option);

    /// <summary>値を検証して保存する。検証に失敗したら赤枠と説明文で示し、保存しない (UI-22 の仕様 7)。</summary>
    private void Commit(SettingDefinition def, JsonNode? value, TextBlock error, Control? box = null)
    {
        if (!def.Validate(value))
        {
            string message = def.Min is { } min && def.Max is { } max
                ? Loc.Format("Settings_OutOfRange", min.ToString(CultureInfo.CurrentCulture), max.ToString(CultureInfo.CurrentCulture))
                : Loc.Get("Settings_InvalidValue");
            ShowError(error, box, message);
            return;
        }

        error.Visibility = Visibility.Collapsed;
        if (box is not null)
        {
            box.ClearValue(Control.BorderBrushProperty);
        }

        if (JsonNode.DeepEquals(App.Settings.GetNode(def.Key) ?? def.Default, value))
        {
            return;
        }

        _editing = true;
        try
        {
            _window.ChangeSetting(def, value);
        }
        finally
        {
            _editing = false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            // 変更の印と「既定に戻す」を出し直す。フォーカスは操作した部品に戻す。
            Refresh();
            if (FindByAutomationId(Items, "SettingControl_" + def.Key) is Control c)
            {
                c.Focus(FocusState.Programmatic);
            }
        });
    }

    private static void ShowError(TextBlock error, Control? box, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        if (box is not null)
        {
            box.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)error.Foreground;
        }
    }

    private static void AddHighlighted(TextBlock block, string text, IReadOnlyList<int> positions)
    {
        var set = positions.ToHashSet();
        for (int i = 0; i < text.Length;)
        {
            int j = i;
            bool bold = set.Contains(i);
            while (j < text.Length && set.Contains(j) == bold)
            {
                j++;
            }

            var run = new Run { Text = text[i..j] };
            if (bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            block.Inlines.Add(run);
            i = j;
        }
    }

    internal static DependencyObject? FindByAutomationId(DependencyObject root, string id)
    {
        if (root is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) == id)
        {
            return root;
        }

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindByAutomationId(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>上部の InfoBar の「今すぐ再起動」(UI-22 の仕様 6)。</summary>
    public void ShowRestartBar() => RestartBar.IsOpen = true;

    public bool RestartBarOpen => RestartBar.IsOpen;
}
