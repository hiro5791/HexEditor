using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面の「キーボード」(UI-18〜UI-21): プリセットの選択、全コマンドの割り当ての表 (カテゴリ・表示名・英語名・キー・有効範囲・
/// 由来)、検索 (表示名・英語名・ID・キーの表記)、「キーで検索」、行ごとの「編集」「既定に戻す」、「すべて既定に戻す」、
/// インポート / エクスポート。
/// </summary>
public sealed partial class KeyboardSettingsSection : UserControl
{
    private readonly MainWindow _window;
    private readonly TextBox _search = new();
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ComboBox _preset = new() { MinWidth = 220 };
    private readonly ToggleButton _byKey = new();
    private bool _changingPreset;

    public KeyboardSettingsSection(MainWindow window)
    {
        _window = window;
        var root = new StackPanel { Spacing = 12 };

        // プリセット (UI-19)。
        _preset.Header = Loc.Get("Keys_Preset");
        AutomationProperties.SetAutomationId(_preset, "Keyboard_Preset");
        foreach (string id in KeyPresets.Ids)
        {
            _preset.Items.Add(new ComboBoxItem { Content = Loc.Get("KeyPreset_" + id), Tag = id });
        }

        _preset.SelectionChanged += async (_, _) =>
        {
            if (!_changingPreset && _preset.SelectedItem is ComboBoxItem { Tag: string id } && id != CommandService.Keys.Preset)
            {
                await SwitchPresetAsync(id);
            }
        };
        root.Children.Add(_preset);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _search.PlaceholderText = Loc.Get("Keys_SearchPlaceholder");
        _search.MinWidth = 280;
        AutomationProperties.SetAutomationId(_search, "Keyboard_Search");
        AutomationProperties.SetName(_search, Loc.Get("Keys_SearchPlaceholder"));
        _search.TextChanged += (_, _) => Fill();

        // 「キーで検索」: 次に押したキーの組み合わせで絞り込む (UI-18 の仕様 2)。
        _byKey.Content = Loc.Get("Keys_SearchByKey");
        AutomationProperties.SetAutomationId(_byKey, "Keyboard_SearchByKey");
        _byKey.Click += (_, _) =>
        {
            if (_byKey.IsChecked == true)
            {
                _search.Text = string.Empty;
                _search.Focus(FocusState.Programmatic);
            }
        };
        _search.PreviewKeyDown += Search_PreviewKeyDown;
        tools.Children.Add(_search);
        tools.Children.Add(_byKey);
        tools.Children.Add(Action("Keys_Import", "Keyboard_Import", () => _ = window.Commands.ExecuteAsync("settings.importKeybindings")));
        tools.Children.Add(Action("Keys_Export", "Keyboard_Export", () => _ = window.Commands.ExecuteAsync("settings.exportKeybindings")));
        tools.Children.Add(Action("Keys_ResetAll", "Keyboard_ResetAll", () => _ = ResetAllAsync()));
        root.Children.Add(new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled });

        AutomationProperties.SetAutomationId(_rows, "Keyboard_Rows");
        root.Children.Add(_rows);
        Content = root;

        Action changed = () => DispatcherQueue.TryEnqueue(Fill);
        CommandService.BindingsChanged += changed;
        Unloaded += (_, _) => CommandService.BindingsChanged -= changed;
        Fill();
    }

    private static Button Action(string key, string id, Action action)
    {
        var b = new Button { Content = Loc.Get(key) };
        AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => action();
        return b;
    }

    private void Search_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_byKey.IsChecked != true || VirtualKeys.IsModifier((int)e.Key))
        {
            return;
        }

        e.Handled = true;
        _byKey.IsChecked = false;
        _search.Text = new KeyStroke(KeyCapture.Modifiers(), (int)e.Key).ToString();
    }

    /// <summary>表を作り直す。</summary>
    public void Fill()
    {
        _changingPreset = true;
        _preset.SelectedIndex = Array.IndexOf(KeyPresets.Ids, CommandService.Keys.Preset);
        _changingPreset = false;

        _rows.Children.Clear();
        _rows.Children.Add(Row(null, [Loc.Get("Keys_ColCategory"), Loc.Get("Keys_ColCommand"), Loc.Get("Keys_ColKeys"), Loc.Get("Keys_ColScope"), Loc.Get("Keys_ColOrigin")]));
        string query = _search.Text;
        foreach (CommandDefinition c in CommandService.Catalog.All.Where(c => !c.Hidden))
        {
            var bindings = CommandService.Keys.BindingsFor(c.Id);
            string keys = KeyboardLayout.Format(bindings.Select(b => b.Binding.Chord));
            string stored = string.Join(" ", bindings.Select(b => b.Binding.Chord.ToString()));
            var row = new ShortcutRow(c.Id, CommandService.CategoryName(c.Category), CommandService.DisplayName(c), keys,
                string.Join(", ", bindings.Select(b => CommandService.ScopeName(b.Binding.Scope)).Distinct()), KeyScope.Global);
            if (!ShortcutList.Matches(row, query, CommandService.EnglishName(c), stored))
            {
                continue;
            }

            string origin = string.Join(", ", bindings.Select(b => Loc.Get("KeyOrigin_" + b.Origin)).Distinct());
            string english = CommandService.EnglishName(c);
            string name = english == row.Command ? row.Command : $"{row.Command} ({english})";
            var warnings = bindings.SelectMany(b => KeyAssign.StateWarnings(b.Binding.Chord)).Distinct().ToList();
            _rows.Children.Add(Row(c, [row.Category, name, warnings.Count > 0 ? $"{keys} ⚠ {string.Join(" ", warnings)}" : keys, row.Scope, origin]));
        }
    }

    private Grid Row(CommandDefinition? command, string[] cells)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 6, 4, 6) };
        double[] widths = [110, 3, 2, 110, 90];
        foreach (double w in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 10 ? new GridLength(w) : new GridLength(w, GridUnitType.Star) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (command is null)
            {
                t.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
            }
            else if (i == 2)
            {
                t.FlowDirection = FlowDirection.LeftToRight;
                AutomationProperties.SetAutomationId(t, "KeyboardKeys_" + command.Id);
            }

            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }

        if (command is not null)
        {
            AutomationProperties.SetAutomationId(grid, "KeyboardRow_" + command.Id);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var edit = new Button { Content = Loc.Get("Keys_Edit") };
            AutomationProperties.SetAutomationId(edit, "KeyboardEdit_" + command.Id);
            AutomationProperties.SetName(edit, Loc.Format("Keys_EditFor", CommandService.DisplayName(command)));
            edit.Click += async (_, _) => await EditAsync(command);
            var reset = new Button { Content = Loc.Get("Keys_ResetRow"), IsEnabled = CommandService.Keys.BindingsFor(command.Id).Any(b => b.Origin == BindingOrigin.User) || CommandService.Keys.UserEntries.Any(e => e.Command == command.Id) };
            AutomationProperties.SetAutomationId(reset, "KeyboardReset_" + command.Id);
            AutomationProperties.SetName(reset, Loc.Format("Keys_ResetRowFor", CommandService.DisplayName(command)));
            reset.Click += (_, _) => CommandService.Keys.ResetCommand(command.Id);
            buttons.Children.Add(edit);
            buttons.Children.Add(reset);
            Grid.SetColumn(buttons, cells.Length);
            grid.Children.Add(buttons);
        }

        return grid;
    }

    /// <summary>「すべて既定に戻す」(確認ダイアログあり。UI-18 の仕様 8)。</summary>
    private async Task ResetAllAsync()
    {
        var dialog = Dialog(Loc.Get("Keys_ResetAllTitle"), new TextBlock { Text = Loc.Get("Keys_ResetAllBody"), TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = Loc.Get("Settings_ResetConfirm");
        AutomationProperties.SetAutomationId(dialog, "KeysResetAllDialog");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            CommandService.Keys.ResetAll();
        }
    }

    /// <summary>プリセットの切り替え。利用者の割り当てと重複する場合は一覧を出して選ばせる (UI-19 の仕様 4)。</summary>
    private async Task SwitchPresetAsync(string preset)
    {
        PresetSwitchPreview preview = CommandService.Keys.PreviewPreset(preset);
        bool preferUser = true;
        if (preview.Conflicts.Count > 0)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock { Text = Loc.Get("Keys_PresetConflictBody"), TextWrapping = TextWrapping.Wrap });
            foreach (KeyConflict c in preview.Conflicts)
            {
                body.Children.Add(new TextBlock
                {
                    Text = Loc.Format("Keys_PresetConflict", KeyboardLayout.Format(c.First.Chord), CommandService.DisplayName(c.First.Command), CommandService.DisplayName(c.Second.Command)),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var dialog = Dialog(Loc.Get("Keys_PresetConflictTitle"), body);
            dialog.PrimaryButtonText = Loc.Get("Keys_PreferUser");
            dialog.SecondaryButtonText = Loc.Get("Keys_PreferPreset");
            AutomationProperties.SetAutomationId(dialog, "KeysPresetConflictDialog");
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                Fill();
                return;
            }

            preferUser = result == ContentDialogResult.Primary;
        }

        CommandService.Keys.SwitchPreset(preset, preferUser);
        if (preview.Error is { } error)
        {
            _window.ShowSettingsNotice(Loc.Format("Keys_PresetError", error));
        }
    }

    /// <summary>
    /// 「編集」: 今のキーの一覧 (削除できる) と、キー入力待ちの欄で新しいキーを追加する (UI-18 の仕様 3〜7)。
    /// Enter で確定、Esc で取り消し。
    /// </summary>
    private async Task EditAsync(CommandDefinition command)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        var current = new StackPanel { Spacing = 4 };
        void FillCurrent()
        {
            current.Children.Clear();
            foreach (EffectiveBinding b in CommandService.Keys.BindingsFor(command.Id))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(new TextBlock { Text = $"{KeyboardLayout.Format(b.Binding.Chord)} ({CommandService.ScopeName(b.Binding.Scope)})", VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight });
                var remove = new Button { Content = Loc.Get("Keys_RemoveKey") };
                AutomationProperties.SetAutomationId(remove, "KeyboardRemoveKey_" + b.Binding.Chord);
                remove.Click += (_, _) =>
                {
                    CommandService.Keys.Remove(command.Id, b.Binding);
                    FillCurrent();
                };
                line.Children.Add(remove);
                current.Children.Add(line);
            }
        }

        FillCurrent();
        body.Children.Add(current);
        var capture = new KeyCapture();
        AutomationProperties.SetAutomationId(capture, "KeyboardCapture");
        body.Children.Add(capture);
        var scope = new ComboBox { Header = Loc.Get("Keys_ColScope") };
        foreach (KeyScope s in KeyScopes.All)
        {
            scope.Items.Add(new ComboBoxItem { Content = CommandService.ScopeName(s), Tag = s });
        }

        scope.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(scope, "KeyboardScope");
        body.Children.Add(scope);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(message, "KeyboardMessage");
        AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        body.Children.Add(message);

        var dialog = Dialog(Loc.Format("Keys_EditTitle", CommandService.DisplayName(command)), body);
        dialog.PrimaryButtonText = Loc.Get("Keys_AddKey");
        AutomationProperties.SetAutomationId(dialog, "KeyboardEditDialog");
        bool replacing = false;
        void TryAdd()
        {
            if (capture.Chord is not { } chord)
            {
                message.Text = Loc.Get("Keys_PressKeys");
                return;
            }

            KeyScope selectedScope = (KeyScope)((ComboBoxItem)scope.SelectedItem).Tag;
            KeyAssignResult result = KeyAssign.TryAssign(command.Id, chord, selectedScope, replacing);
            if (result.Rejection is { } reason)
            {
                message.Text = reason;
                return;
            }

            if (result.NeedsReplaceConfirmation)
            {
                // 重複の一覧を出し、「置き換える」「キャンセル」を選ばせる (UI-18 の仕様 6)。
                message.Text = Loc.Get("Keys_ConflictIntro") + Environment.NewLine + string.Join(Environment.NewLine,
                    result.Conflicts.Select(c => Loc.Format("Keys_ConflictLine", CommandService.DisplayName(c.Command), CommandService.ScopeName(c.Binding.Scope))));
                dialog.PrimaryButtonText = Loc.Get("Keys_Replace");
                replacing = true;
                return;
            }

            replacing = false;
            dialog.PrimaryButtonText = Loc.Get("Keys_AddKey");
            message.Text = result.Warnings.Count > 0 ? string.Join(Environment.NewLine, result.Warnings) : Loc.Get("Keys_Added");
            capture.Clear();
            FillCurrent();
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            TryAdd();
        };
        capture.Committed += (_, _) => TryAdd();
        capture.Changed += (_, _) =>
        {
            replacing = false;
            dialog.PrimaryButtonText = Loc.Get("Keys_AddKey");
            message.Text = string.Empty;
        };
        await dialog.ShowAsync();
    }

    private ContentDialog Dialog(string title, UIElement body) => new()
    {
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        FlowDirection = FlowDirection,
        Title = title,
        Content = new ScrollViewer { Content = body, MaxHeight = 480 },
        CloseButtonText = Loc.Get("Common_Close"),
        DefaultButton = ContentDialogButton.Primary,
    };
}

/// <summary>
/// キー入力待ちの欄 (UI-18 の仕様 3): 押したキーの組み合わせをそのまま記録する。2 打鍵までの連続キーを記録でき、3 打鍵目で
/// 記録し直す。Enter で確定 (<see cref="Committed"/>)、Esc で取り消し。Tab はフォーカスの移動に使う。
/// </summary>
public sealed partial class KeyCapture : UserControl
{
    private readonly TextBox _box = new() { IsReadOnly = true, MinWidth = 280 };
    private readonly List<KeyStroke> _strokes = [];

    public KeyCapture()
    {
        _box.Header = Loc.Get("Keys_CaptureHeader");
        _box.PlaceholderText = Loc.Get("Keys_PressKeys");
        AutomationProperties.SetName(_box, Loc.Get("Keys_CaptureHeader"));
        _box.PreviewKeyDown += OnKey;
        Content = _box;
        IsTabStop = false;
    }

    public KeyChord? Chord => _strokes.Count == 0 ? null : new KeyChord([.. _strokes]);

    public event EventHandler? Committed;

    public event EventHandler? Changed;

    public void Clear()
    {
        _strokes.Clear();
        _box.Text = string.Empty;
    }

    /// <summary>今押されている修飾キー。</summary>
    public static KeyModifiers Modifiers()
    {
        static bool Down(VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        return (Down(VirtualKey.Control) ? KeyModifiers.Ctrl : 0) | (Down(VirtualKey.Shift) ? KeyModifiers.Shift : 0)
            | (Down(VirtualKey.Menu) ? KeyModifiers.Alt : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? KeyModifiers.Win : 0);
    }

    /// <summary>キーを 1 つ記録する (テスト用の命令からも使う)。</summary>
    public void Record(KeyStroke stroke)
    {
        if (_strokes.Count >= 2)
        {
            _strokes.Clear();
        }

        _strokes.Add(stroke);
        _box.Text = KeyboardLayout.Format(Chord!);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        KeyModifiers modifiers = Modifiers();
        int key = (int)e.Key;
        if (VirtualKeys.IsModifier(key) || key == 229)
        {
            e.Handled = true;
            return;
        }

        if (modifiers == KeyModifiers.None && key == VirtualKeys.Tab)
        {
            return;
        }

        e.Handled = true;
        if (modifiers == KeyModifiers.None && key == VirtualKeys.Enter)
        {
            Committed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (modifiers == KeyModifiers.None && key == VirtualKeys.Escape)
        {
            Clear();
            return;
        }

        Record(new KeyStroke(modifiers, key));
    }
}
