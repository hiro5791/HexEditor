using System.Text.Json;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面「外観」の配色 (UI-28): サムネイル付きの配色の一覧 (選ぶとすべての Hex 表示に反映)、複製・エクスポート・インポート、
/// 独自の配色の編集 (要素ごとの色の一覧とプレビュー。変更はプレビューに即座に反映)、コントラスト比の警告 (仕様 4。保存は可能)、
/// 配色ファイルの不正な要素の警告 (「エラー」)。
/// </summary>
public sealed class ColorSchemeSection
{
    /// <summary>
    /// 編集できる要素 (仕様 1)。差分・レコードの交互色はその機能 (フェーズ 2 以降) が描くまで、ブックマークは色がブックマークごとに決まる
    /// (05 の INSP) ため、編集の一覧には出さない (配色ファイルにあれば保存する)。
    /// </summary>
    public static readonly SchemeElement[] EditableElements =
    [
        SchemeElement.Background,
        SchemeElement.AlternateBackground,
        SchemeElement.CurrentRowBackground,
        SchemeElement.OffsetText,
        SchemeElement.HexText,
        SchemeElement.TextText,
        SchemeElement.Zero,
        SchemeElement.NonPrintable,
        SchemeElement.Modified,
        SchemeElement.Inserted,
        SchemeElement.SelectionBackground,
        SchemeElement.SelectionText,
        SchemeElement.Caret,
        SchemeElement.Match,
        SchemeElement.Separator,
    ];

    private readonly MainWindow _window;
    private readonly HexPreview _preview;
    private readonly ColorSchemeStore _store = new(App.Settings.Folder);
    private readonly GridView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly InfoBar _loadWarnings = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
    private readonly InfoBar _message = new() { IsClosable = true };
    private readonly Expander _editor = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly InfoBar _contrast = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
    private readonly RadioButtons _variant = new() { MaxColumns = 2 };
    private bool _filling;
    private ColorScheme? _editing;

    // 設定画面は設定が変わるたびに作り直される (配色を選んだときも)。編集中の配色 (保存前の変更を含む) と編集の欄の開閉は、作り直しても
    // 失わないようにここに持つ。
    private static ColorScheme? s_editingCache;
    private static bool s_expanded;

    private ColorSchemeSection(MainWindow window, HexPreview preview)
    {
        _window = window;
        _preview = preview;
    }

    /// <summary>最後に作った区画 (テスト用の命令が操作する)。</summary>
    internal static ColorSchemeSection? Current { get; private set; }

    /// <summary>区画を作って <paramref name="panel"/> に加える。</summary>
    public static ColorSchemeSection Build(MainWindow window, HexPreview preview, Panel panel)
    {
        var section = new ColorSchemeSection(window, preview);
        section.Create(panel);
        Current = section;
        return section;
    }

    /// <summary>編集中の配色 (なければ null)。</summary>
    internal ColorScheme? Editing => _editing;

    /// <summary>プレビュー (テスト用)。</summary>
    internal HexPreview Preview => _preview;

    /// <summary>一覧の配色の名前と、選んでいる配色 (テスト用)。</summary>
    internal (IReadOnlyList<string> Names, string? Selected, bool EditorVisible) ListState => (
        [.. _list.Items.OfType<FrameworkElement>().Select(i => (string)i.Tag)],
        (_list.SelectedItem as FrameworkElement)?.Tag as string,
        _editor.Visibility == Visibility.Visible);

    /// <summary>選んでいる配色の名前 (設定 view.colorScheme)。</summary>
    private static string SelectedName => App.Settings.GetString(ViewOptions.ColorSchemeKey, ColorScheme.DefaultName);

    private static string DisplayName(ColorScheme scheme) => scheme.BuiltIn ? Loc.Get("Scheme_" + scheme.Name) : scheme.Name;

    private void Create(Panel panel)
    {
        var header = new TextBlock { Text = Loc.Get("Set_view_colorScheme"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        panel.Children.Add(header);

        AutomationProperties.SetAutomationId(_list, "SettingControl_view.colorScheme");
        AutomationProperties.SetName(_list, Loc.Get("Set_view_colorScheme"));
        _list.SelectionChanged += (_, _) =>
        {
            if (!_filling && _list.SelectedItem is FrameworkElement { Tag: string name })
            {
                _ = _window.Commands.ExecuteAsync("view.colorScheme", name);
                ShowSelected(name);
            }
        };
        panel.Children.Add(_list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(DuplicateButton());
        buttons.Children.Add(MakeButton("SchemeUi_Export", "Settings_SchemeExport", () => _ = ExportAsync()));
        buttons.Children.Add(MakeButton("SchemeUi_Import", "Settings_SchemeImport", () => _ = ImportAsync()));
        panel.Children.Add(buttons);

        AutomationProperties.SetAutomationId(_loadWarnings, "Settings_SchemeLoadWarnings");
        AutomationProperties.SetAutomationId(_message, "Settings_SchemeMessage");
        panel.Children.Add(_loadWarnings);
        panel.Children.Add(_message);

        // 編集 (独自の配色だけ。設定画面の中の展開できる欄)。
        AutomationProperties.SetAutomationId(_editor, "Settings_SchemeEditor");
        _variant.Header = Loc.Get("SchemeUi_Variant");
        _variant.Items.Add(Loc.Get("SchemeUi_Light"));
        _variant.Items.Add(Loc.Get("SchemeUi_Dark"));
        AutomationProperties.SetAutomationId(_variant, "Settings_SchemeVariant");
        _variant.SelectionChanged += (_, _) => FillRows();
        AutomationProperties.SetAutomationId(_contrast, "Settings_SchemeContrast");
        _contrast.Title = Loc.Get("SchemeUi_ContrastTitle");
        var save = new Button { Content = Loc.Get("SchemeUi_Save"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(save, "Settings_SchemeSave");
        save.Click += (_, _) => SaveEditing();
        var editorBody = new StackPanel { Spacing = 8 };
        editorBody.Children.Add(_variant);
        editorBody.Children.Add(_rows);
        editorBody.Children.Add(_contrast);
        editorBody.Children.Add(save);
        _editor.Content = editorBody;
        _editor.IsExpanded = s_expanded;
        _editor.Expanding += (_, _) => s_expanded = true;
        _editor.Collapsed += (_, _) => s_expanded = false;
        panel.Children.Add(_editor);

        FillList();
        ShowSelected(SelectedName);
    }

    private static Button MakeButton(string textKey, string automationId, Action click)
    {
        var button = new Button { Content = Loc.Get(textKey) };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => click();
        return button;
    }

    // ---- 一覧 (サムネイル付き) ----

    private void FillList()
    {
        _filling = true;
        try
        {
            _list.Items.Clear();
            string selected = SelectedName;
            bool dark = _window.Content is FrameworkElement { ActualTheme: ElementTheme.Dark };
            foreach (ColorScheme scheme in _store.All())
            {
                FrameworkElement item = Thumbnail(scheme, dark);
                _list.Items.Add(item);
                if (scheme.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SelectedItem = item;
                }
            }
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>サムネイル: 背景にオフセット・Hex の文字・選択範囲の色の見本と、配色の名前。</summary>
    private static FrameworkElement Thumbnail(ColorScheme scheme, bool dark)
    {
        // 配色の色は利用者が設定した値 (配色ファイル) で、コードに直書きした色ではない。値がなければテーマの色。
        Brush? Pick(SchemeElement element) =>
            scheme.Get(element, dark) is { } c ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B)) : null;
        Brush Theme(string key) => (Brush)Application.Current.Resources[key];

        var sample = new StackPanel { Spacing = 2, Padding = new Thickness(6) };
        var font = new FontFamily("Cascadia Mono, Consolas");
        void Line(string offset, string hex, bool selected)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new TextBlock { Text = offset, FontFamily = font, FontSize = 11, Foreground = Pick(SchemeElement.OffsetText) ?? Theme("TextFillColorSecondaryBrush") });
            var hexText = new TextBlock
            {
                Text = hex,
                FontFamily = font,
                FontSize = 11,
                Foreground = selected ? Pick(SchemeElement.SelectionText) ?? Theme("TextOnAccentFillColorPrimaryBrush") : Pick(SchemeElement.HexText) ?? Theme("TextFillColorPrimaryBrush"),
            };
            line.Children.Add(selected
                ? new Border { Background = Pick(SchemeElement.SelectionBackground) ?? Theme("AccentFillColorDefaultBrush"), Child = hexText }
                : hexText);
            sample.Children.Add(line);
        }

        Line("00", "48 65 78", false);
        Line("10", "45 64 69", true);
        Line("20", "74 00 FF", false);
        var swatch = new Border
        {
            Child = sample,
            Background = Pick(SchemeElement.Background) ?? Theme("SolidBackgroundFillColorBaseBrush"),
            BorderBrush = Theme("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            FlowDirection = FlowDirection.LeftToRight,
        };
        var item = new StackPanel { Spacing = 4, Width = 132, Tag = scheme.Name };
        item.Children.Add(swatch);
        item.Children.Add(new TextBlock { Text = DisplayName(scheme), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
        AutomationProperties.SetName(item, DisplayName(scheme));
        AutomationProperties.SetAutomationId(item, "Settings_Scheme_" + scheme.Name);
        return item;
    }

    /// <summary>選んだ配色をプレビューに出し、読み込みの警告と編集の欄を用意する。</summary>
    private void ShowSelected(string name)
    {
        ColorScheme scheme = _store.Find(name);
        _loadWarnings.Message = Loc.Format("SchemeUi_LoadWarnings", string.Join(", ", scheme.LoadWarnings));
        _loadWarnings.IsOpen = scheme.LoadWarnings.Count > 0;
        if (!scheme.BuiltIn && s_editingCache is { } cached && cached.Name.Equals(scheme.Name, StringComparison.OrdinalIgnoreCase))
        {
            scheme = cached;
        }

        _editing = scheme.BuiltIn ? null : scheme;
        s_editingCache = _editing;
        _preview.Scheme = scheme.Name == ColorScheme.DefaultName ? null : scheme;
        _editor.Visibility = _editing is null ? Visibility.Collapsed : Visibility.Visible;
        _editor.Header = _editing is null ? null : Loc.Format("SchemeUi_Edit", _editing.Name);
        if (_editing is not null)
        {
            _filling = true;
            _variant.SelectedIndex = _window.Content is FrameworkElement { ActualTheme: ElementTheme.Dark } ? 1 : 0;
            _filling = false;
            FillRows();
        }
    }

    // ---- 編集 ----

    private bool EditingDark => _variant.SelectedIndex == 1;

    private void FillRows()
    {
        _rows.Children.Clear();
        if (_editing is null)
        {
            return;
        }

        foreach (SchemeElement element in EditableElements)
        {
            _rows.Children.Add(Row(element));
        }

        UpdateWarnings();
    }

    /// <summary>要素 1 つの行: 名前、色見本 (押すと色の選択)、#RRGGBB の入力欄、「テーマの色に戻す」。</summary>
    private Grid Row(SchemeElement element)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string label = Loc.Get("SchemeElement_" + element);
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(name);

        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
        };
        var pick = new Button { Content = swatch, Padding = new Thickness(6) };
        AutomationProperties.SetName(pick, Loc.Format("SchemeUi_PickColor", label));
        AutomationProperties.SetAutomationId(pick, "Settings_SchemePick_" + element);
        ToolTipService.SetToolTip(pick, Loc.Format("SchemeUi_PickColor", label));
        Grid.SetColumn(pick, 1);
        grid.Children.Add(pick);

        var box = new TextBox { Width = 110, PlaceholderText = Loc.Get("SchemeUi_ThemeColor") };
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetAutomationId(box, "Settings_SchemeColor_" + element);
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);

        var reset = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetName(reset, Loc.Get("SchemeUi_Reset"));
        ToolTipService.SetToolTip(reset, Loc.Get("SchemeUi_Reset"));
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);

        void Show()
        {
            SchemeColor? color = _editing?.Get(element, EditingDark);
            swatch.Background = color is { } c ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B)) : null;
            box.Text = color?.ToString() ?? string.Empty;
            reset.IsEnabled = color is not null;
        }

        void Set(SchemeColor? color)
        {
            if (_editing is null)
            {
                return;
            }

            _editing.Set(element, EditingDark, color);
            Show();
            _preview.Render();
            UpdateWarnings();
        }

        // 色の選択 (ColorPicker)。動かしている間もプレビューに映す。
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring };
        var flyout = new Flyout { Content = picker };
        flyout.Opening += (_, _) =>
        {
            if (_editing?.Get(element, EditingDark) is { } c)
            {
                picker.Color = Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B);
            }
        };
        picker.ColorChanged += (_, e) =>
        {
            if (flyout.IsOpen)
            {
                Set(new SchemeColor(0xFF, e.NewColor.R, e.NewColor.G, e.NewColor.B));
            }
        };
        pick.Flyout = flyout;

        void Commit()
        {
            string text = box.Text.Trim();
            if (text.Length == 0)
            {
                Set(null);
            }
            else if (SchemeColor.TryParse(text.StartsWith('#') ? text : "#" + text, out SchemeColor parsed))
            {
                Set(parsed);
            }
            else
            {
                Show();
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        reset.Click += (_, _) => Set(null);
        Show();
        return grid;
    }

    /// <summary>コントラスト比が 4.5:1 未満の組み合わせ (仕様 4)。警告だけで、保存はできる。</summary>
    private void UpdateWarnings()
    {
        IReadOnlyList<ContrastWarning> warnings = _editing?.ContrastWarnings() ?? [];
        _contrast.Message = string.Join(Environment.NewLine, warnings.Select(w => Loc.Format(
            "SchemeUi_ContrastItem",
            Loc.Get("SchemeElement_" + w.Foreground),
            Loc.Get("SchemeElement_" + w.Background),
            Loc.Get(w.Dark ? "SchemeUi_Dark" : "SchemeUi_Light"),
            w.Ratio.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture))));
        _contrast.IsOpen = warnings.Count > 0;
    }

    /// <summary>コントラストの警告 (テスト用)。</summary>
    internal (bool Open, string Message) ContrastState => (_contrast.IsOpen, _contrast.Message ?? string.Empty);

    /// <summary>読み込みの警告 (テスト用)。</summary>
    internal (bool Open, string Message) LoadWarningState => (_loadWarnings.IsOpen, _loadWarnings.Message ?? string.Empty);

    /// <summary>編集した配色を保存し、すべてのウィンドウの Hex 表示に反映する。</summary>
    internal void SaveEditing()
    {
        if (_editing is null)
        {
            return;
        }

        try
        {
            _store.Save(_editing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(Loc.Format("SchemeUi_SaveFailed", ex.Message), InfoBarSeverity.Error);
            return;
        }

        foreach (MainWindow w in WindowManager.Windows)
        {
            w.ApplyEditorSettings();
        }

        string name = _editing.Name;
        FillList();
        ShowSelected(name);
    }

    /// <summary>テスト用: 編集中の配色の色を変える (入力欄に入力したのと同じ)。</summary>
    internal void SetColorForTest(SchemeElement element, bool dark, SchemeColor? color)
    {
        _variant.SelectedIndex = dark ? 1 : 0;
        _editing?.Set(element, dark, color);
        FillRows();
        _preview.Render();
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        _message.Message = message;
        _message.Severity = severity;
        _message.IsOpen = true;
    }

    // ---- 複製 ----

    private Button DuplicateButton()
    {
        var button = new Button { Content = Loc.Get("SchemeUi_Duplicate") };
        AutomationProperties.SetAutomationId(button, "Settings_SchemeDuplicate");
        var name = new TextBox { Header = Loc.Get("SchemeUi_NewName"), MinWidth = 240 };
        AutomationProperties.SetAutomationId(name, "Settings_SchemeNewName");
        var error = new TextBlock
        {
            Text = Loc.Get("SchemeUi_NameInvalid"),
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var ok = new Button { Content = Loc.Get("Common_Ok"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(ok, "Settings_SchemeDuplicateOk");
        var body = new StackPanel { Spacing = 8, MaxWidth = 320 };
        body.Children.Add(name);
        body.Children.Add(error);
        body.Children.Add(ok);
        var flyout = new Flyout { Content = body };
        flyout.Opening += (_, _) =>
        {
            name.Text = Loc.Format("SchemeUi_CopyName", DisplayName(_store.Find(SelectedName)));
            error.Visibility = Visibility.Collapsed;
        };
        void Confirm()
        {
            if (Duplicate(name.Text) is null)
            {
                error.Visibility = Visibility.Visible;
                return;
            }

            flyout.Hide();
        }

        ok.Click += (_, _) => Confirm();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Confirm();
                e.Handled = true;
            }
        };
        button.Flyout = flyout;
        return button;
    }

    /// <summary>
    /// 選んでいる配色を <paramref name="name"/> で複製して保存し、その配色を選んで編集の欄を開く (仕様 3)。名前が空・他の配色と同じなら null。
    /// </summary>
    internal ColorScheme? Duplicate(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _store.All().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        ColorScheme copy = _store.Find(SelectedName).Duplicate(name);
        try
        {
            _store.Save(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(Loc.Format("SchemeUi_SaveFailed", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        // 編集の欄を開いた状態にしてから選ぶ (設定が変わると設定画面が作り直される)。
        s_expanded = true;
        s_editingCache = copy;
        _editor.IsExpanded = true;
        App.Settings.SetString(ViewOptions.ColorSchemeKey, copy.Name, ColorScheme.DefaultName);
        FillList();
        ShowSelected(copy.Name);
        return copy;
    }

    // ---- エクスポート・インポート (仕様 5) ----

    private async Task ExportAsync()
    {
        ColorScheme scheme = _store.Find(SelectedName);
        string suggested = ColorScheme.FileNameFor(scheme.Name);
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(_window.AppWindow.Id)
            {
                SuggestedFileName = suggested,
                SettingsIdentifier = "HexEditor.ColorScheme",
            };
            picker.FileTypeChoices.Add(Loc.Get("SchemeUi_FileType"), [".json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            ColorSchemeStore.Export(scheme, path);
            ShowMessage(Loc.Format("SchemeUi_Exported", path), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(Loc.Format("SchemeUi_SaveFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task ImportAsync()
    {
        const string settingsIdentifier = "HexEditor.ColorScheme";
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult(settingsIdentifier);
        if (paths is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_window.AppWindow.Id) { SettingsIdentifier = settingsIdentifier };
            picker.FileTypeFilter.Add(".json");
            paths = await picker.PickSingleFileAsync() is { } file ? [file.Path] : [];
        }

        if (paths.Count > 0)
        {
            Import(paths[0]);
        }
    }

    /// <summary>インポートして保存し、その配色を選ぶ。不正な要素があれば警告を出す (「エラー」)。</summary>
    internal ColorScheme? Import(string path)
    {
        ColorScheme imported;
        try
        {
            imported = _store.Import(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(Loc.Format("SchemeUi_ImportFailed", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        _ = _window.Commands.ExecuteAsync("view.colorScheme", imported.Name);
        FillList();
        ShowSelected(imported.Name);

        // 読み込んだファイルの不正な要素 (保存し直したファイルからは消えるので、読み込んだときの結果を出す)。
        _loadWarnings.Message = Loc.Format("SchemeUi_LoadWarnings", string.Join(", ", imported.LoadWarnings));
        _loadWarnings.IsOpen = imported.LoadWarnings.Count > 0;
        return imported;
    }
}
