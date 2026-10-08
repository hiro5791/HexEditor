using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.Views;
using HexEditor.Core.Commands;
using HexEditor.Core.Notifications;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// 設定 (UI-22〜UI-25) とキー割り当てのインポート / エクスポート (UI-21) の操作。設定画面・ショートカット一覧はエディタ領域に重ねて
/// 開くページ (<see cref="ToolPageHost"/>。タブ UI-09 ができたらタブにする)。
/// </summary>
public sealed partial class MainWindow
{
    private SettingsPage? _settingsPage;
    private ShortcutsPage? _shortcutsPage;
    private readonly List<(string Id, FrameworkElement Page)> _toolPages = [];
    private string? _activeToolPage;

    /// <summary>再起動が必要な項目のうち、変更したもの (「再起動後に反映されます」を出す。UI-22 の仕様 6)。</summary>
    public HashSet<string> PendingRestartKeys { get; } = [];

    private void RegisterSettingsCommands()
    {
        Commands.Register("settings.open", () => OpenSettingsPage());
        Commands.Register("settings.keyboard", () => OpenSettingsPage(SettingCategories.Keyboard));
        Commands.Register("settings.selectPreset", () =>
        {
            OpenSettingsPage(SettingCategories.Keyboard);
            DispatcherQueue.TryEnqueue(() => (SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Preset") as Control)?.Focus(FocusState.Programmatic));
        });
        Commands.Register("settings.exportKeybindings", ExportKeybindingsAsync);
        Commands.Register("settings.importKeybindings", ImportKeybindingsAsync);
        Commands.Register("settings.export", ExportSettingsAsync);
        Commands.Register("settings.import", ImportSettingsAsync);
        Commands.Register("settings.resetAll", ResetAllAsync);
        Commands.Register("settings.openFile", async () =>
        {
            // settings.json を既定のテキストエディタで開く (UI-22 の仕様 8)。まだなければ今の内容で作る。
            if (!File.Exists(App.Settings.PathName))
            {
                App.Settings.SaveNow();
            }

            await OpenUriAsync(new Uri(App.Settings.PathName));
        });
        Commands.Register("settings.openDataFolder", async () =>
        {
            Directory.CreateDirectory(Program.Environment.Locations.Root);
            await Windows.System.Launcher.LaunchFolderPathAsync(Program.Environment.Locations.Root);
        });
    }

    // ---- ページ (設定画面・ショートカット一覧) ----

    /// <summary>
    /// 設定画面を開く (UI-22 の仕様 1)。既に開いていれば、それを前に出す (2 つ目は開かない)。<paramref name="settingKey"/> を
    /// 指定すると、その項目を開いてフォーカスを置く。
    /// </summary>
    public void OpenSettingsPage(string? category = null, string? settingKey = null)
    {
        _settingsPage ??= new SettingsPage(this);
        ShowToolPage("settings", _settingsPage);
        if (settingKey is not null)
        {
            DispatcherQueue.TryEnqueue(() => _settingsPage.FocusSetting(settingKey));
        }
        else if (category is not null)
        {
            _settingsPage.ShowCategory(category);
        }
    }

    /// <summary>ショートカット一覧を開く (UI-39。設定画面と同じく 1 つだけ)。</summary>
    public void OpenShortcutsPage()
    {
        _shortcutsPage ??= new ShortcutsPage(this);
        _shortcutsPage.Refresh();
        ShowToolPage("shortcuts", _shortcutsPage);
    }

    private void RefreshShortcutsPage() => _shortcutsPage?.Refresh();

    /// <summary>ページを開いて前に出す。見出しの帯 (ページの切り替えと「閉じる」) を作り直す。</summary>
    private void ShowToolPage(string id, FrameworkElement page)
    {
        if (_toolPages.All(p => p.Id != id))
        {
            _toolPages.Add((id, page));
        }

        _activeToolPage = id;
        UpdateToolPages();
    }

    public void CloseToolPage(string id)
    {
        _toolPages.RemoveAll(p => p.Id == id);
        if (id == "settings")
        {
            _settingsPage = null;
        }
        else if (id == "shortcuts")
        {
            _shortcutsPage = null;
        }

        _activeToolPage = _activeToolPage == id ? _toolPages.LastOrDefault().Id : _activeToolPage;
        UpdateToolPages();
        if (_activeToolPage is null)
        {
            FocusEditor();
        }
    }

    /// <summary>文書のタブに切り替えたらページを隠す (ページは開いたまま)。</summary>
    private void HideToolPages()
    {
        if (_activeToolPage is not null)
        {
            _activeToolPage = null;
            UpdateToolPages();
        }
    }

    /// <summary>開いているページの一覧 (テスト用の命令)。</summary>
    public IReadOnlyList<string> OpenToolPages => [.. _toolPages.Select(p => p.Id)];

    public string? ActiveToolPage => _activeToolPage;

    private void UpdateToolPages()
    {
        ToolPageHost.Children.Clear();
        if (_activeToolPage is null || _toolPages.FirstOrDefault(p => p.Id == _activeToolPage).Page is not { } page)
        {
            ToolPageHost.Visibility = Visibility.Collapsed;
            return;
        }

        ToolPageHost.RowDefinitions.Clear();
        ToolPageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ToolPageHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ページの見出しの帯 (タブ相当): ページ名のボタンと「閉じる」。
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 0) };
        AutomationProperties.SetAutomationId(strip, "ToolPageTabs");
        foreach ((string id, FrameworkElement _) in _toolPages)
        {
            string title = Loc.Get(id == "settings" ? "ToolPage_Settings" : "ToolPage_Shortcuts");
            var select = new ToggleButton { Content = title, IsChecked = id == _activeToolPage };
            AutomationProperties.SetAutomationId(select, "ToolPageTab_" + id);
            select.Click += (_, _) =>
            {
                _activeToolPage = id;
                UpdateToolPages();
            };
            var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(6) };
            AutomationProperties.SetAutomationId(close, "ToolPageClose_" + id);
            AutomationProperties.SetName(close, Loc.Format("ToolPage_Close", title));
            ToolTipService.SetToolTip(close, Loc.Format("ToolPage_Close", title));
            close.Click += (_, _) => CloseToolPage(id);
            strip.Children.Add(select);
            strip.Children.Add(close);
        }

        ToolPageHost.Children.Add(strip);
        if (page.Parent is Panel old)
        {
            old.Children.Remove(page);
        }

        Grid.SetRow(page, 1);
        ToolPageHost.Children.Add(page);
        ToolPageHost.Visibility = Visibility.Visible;
        PlaceToolPageHost();
    }

    /// <summary>ページの上端をタブ列の下端に合わせる (文書のタブの見出しは使えるまま)。</summary>
    private void PlaceToolPageHost() => ToolPageHost.Margin = StartPage.Margin;

    private void InitializeToolPages()
    {
        Tabs.SizeChanged += (_, _) => PlaceToolPageHost();
        Tabs.SelectionChanged += (_, _) => HideToolPages();
        Tabs.Tapped += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is TabViewItem)
                {
                    HideToolPages();
                    return;
                }
            }
        };
    }

    // ---- 設定の変更とリセット (UI-22、UI-24) ----

    /// <summary>設定画面での変更 (検証済み)。再起動が必要な項目は印と「今すぐ再起動」を出す。</summary>
    public void ChangeSetting(SettingDefinition def, JsonNode? value)
    {
        App.Settings.SetNode(def.Key, value, def.Default);
        if (def.RequiresRestart)
        {
            PendingRestartKeys.Add(def.Key);
            _settingsPage?.ShowRestartBar();
        }

        AfterSettingsChanged();
    }

    /// <summary>項目を既定に戻す。確認なしで戻し、「元に戻す」付きの InfoBar を 8 秒出す (UI-24 の仕様 1)。</summary>
    public void ResetSetting(string key)
    {
        SettingDefinition? def = CommandService.Settings.Find(key);
        JsonNode? before = App.Settings.GetNode(key);
        App.Settings.SetNode(key, null, def?.Default);
        AfterSettingsChanged();
        string name = def is null ? key : Loc.Get(def.NameKey);
        ShowNotice(Loc.Format("Settings_ResetDone", name), InfoBarSeverity.Informational, undo: new NotificationAction(Loc.Get("Common_Undo"), () =>
        {
            App.Settings.SetNode(key, before, def?.Default);
            AfterSettingsChanged();
        }));
    }

    /// <summary>「このカテゴリを既定に戻す」: 戻す項目の数を示して確認する (UI-24 の仕様 2)。</summary>
    public async Task ResetCategoryAsync(string category)
    {
        var modified = CommandService.Settings.Modified(App.Settings, category);
        if (modified.Count == 0)
        {
            ShowNotice(Loc.Get("Settings_NothingToReset"), InfoBarSeverity.Informational);
            return;
        }

        if (!await ConfirmAsync(Loc.Get("Settings_ResetCategoryTitle"), Loc.Format("Settings_ResetCategoryBody", modified.Count, SettingsPage.CategoryName(category)),
            Loc.Get("Settings_ResetConfirm"), "ResetCategoryDialog"))
        {
            return;
        }

        foreach (SettingDefinition s in modified)
        {
            App.Settings.SetNode(s.Key, null, s.Default);
        }

        AfterSettingsChanged();
    }

    /// <summary>
    /// 「すべての設定を既定に戻す」(UI-24 の仕様 3、4): 対象をチェックボックスで選び、対象のファイルを backup-&lt;日時&gt;/ に
    /// 残してから戻す。バックアップに失敗したら中止する。
    /// </summary>
    private async Task ResetAllAsync()
    {
        var boxes = PartBoxes(SettingsParts.ResetDefault, includeDocuments: true, "ResetAll_");
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Settings_ResetAllBody"), TextWrapping = TextWrapping.Wrap });
        foreach (CheckBox box in boxes.Values)
        {
            body.Children.Add(box);
        }

        if (!await ConfirmAsync(Loc.Get("Settings_ResetAllTitle"), body, Loc.Get("Settings_ResetConfirm"), "ResetAllDialog"))
        {
            return;
        }

        SettingsParts parts = Selected(boxes);
        ResetAll(parts);
    }

    /// <summary>全体のリセットの本体 (テスト用の命令からも呼ぶ)。</summary>
    public bool ResetAll(SettingsParts parts)
    {
        string folder = App.Settings.Folder;
        try
        {
            App.Settings.Flush();
            CommandService.Flush();
            SettingsFiles.CreateBackup(folder, SettingsFiles.PathsOf(parts), DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_BackupFailed", ex.Message), InfoBarSeverity.Error);
            return false;
        }

        if (parts.HasFlag(SettingsParts.Settings))
        {
            App.Settings.ReplaceAll([]);
        }

        if (parts.HasFlag(SettingsParts.KeyBindings))
        {
            CommandService.Keys.Load(new KeyBindingsDocument());
        }

        if (parts.HasFlag(SettingsParts.RecentAndState))
        {
            CommandService.State.Clear();
            CommandService.Recent.Clear();
        }

        try
        {
            SettingsFiles.Delete(folder, SettingsFiles.PathsOf(parts & (SettingsParts.Themes | SettingsParts.Documents)));
            if (parts.HasFlag(SettingsParts.RecentAndState))
            {
                SettingsFiles.Delete(folder, [SettingsFiles.Recent]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ResetPartial", ex.Message), InfoBarSeverity.Warning);
        }

        App.Settings.Flush();
        CommandService.Flush();
        AfterSettingsChanged();
        ShowNotice(Loc.Get("Settings_ResetAllDone"), InfoBarSeverity.Success);
        return true;
    }

    private void AfterSettingsChanged()
    {
        ApplyAppearance();
        ApplyEditorSettings();
        RefreshToolbar();
        _settingsPage?.Refresh();
        RefreshCommandUi();
    }

    private static Dictionary<SettingsParts, CheckBox> PartBoxes(SettingsParts on, bool includeDocuments, string idPrefix)
    {
        var parts = new List<(SettingsParts Part, string Key)>
        {
            (SettingsParts.Settings, "SettingsPart_Settings"),
            (SettingsParts.KeyBindings, "SettingsPart_KeyBindings"),
            (SettingsParts.Themes, "SettingsPart_Themes"),
            (SettingsParts.RecentAndState, includeDocuments ? "SettingsPart_RecentAndState" : "SettingsPart_Recent"),
        };
        if (includeDocuments)
        {
            parts.Add((SettingsParts.Documents, "SettingsPart_Documents"));
        }

        var boxes = new Dictionary<SettingsParts, CheckBox>();
        foreach ((SettingsParts part, string key) in parts)
        {
            var box = new CheckBox { Content = Loc.Get(key), IsChecked = on.HasFlag(part) };
            AutomationProperties.SetAutomationId(box, idPrefix + part);
            boxes[part] = box;
        }

        return boxes;
    }

    private static SettingsParts Selected(Dictionary<SettingsParts, CheckBox> boxes) =>
        boxes.Where(b => b.Value.IsChecked == true).Aggregate(SettingsParts.None, (a, b) => a | b.Key);

    // ---- 設定のインポート / エクスポート (UI-25) ----

    /// <summary>エクスポートの初期状態: 設定・キー割り当て・独自の配色はオン、最近使ったファイルはオフ (パスを含むため)。</summary>
    public const SettingsParts ExportDefault = SettingsParts.Settings | SettingsParts.KeyBindings | SettingsParts.Themes;

    private async Task ExportSettingsAsync()
    {
        var boxes = PartBoxes(ExportDefault, includeDocuments: false, "Export_");
        var body = new StackPanel { Spacing = 8 };
        foreach (CheckBox box in boxes.Values)
        {
            body.Children.Add(box);
        }

        if (!await ConfirmAsync(Loc.Get("Settings_ExportTitle"), body, Loc.Get("Settings_ExportConfirm"), "ExportSettingsDialog"))
        {
            return;
        }

        string? path = await PickSaveFileAsync("HexEditor.SettingsExport", SettingsBundle.DefaultFileName(DateTime.Now));
        if (path is not null)
        {
            ExportSettings(Selected(boxes), path);
        }
    }

    /// <summary>エクスポートの本体 (テスト用の命令からも呼ぶ)。</summary>
    public void ExportSettings(SettingsParts parts, string path)
    {
        App.Settings.Flush();
        string folder = App.Settings.Folder;
        var themes = new List<(string, JsonNode)>();
        string themeDir = Path.Combine(folder, SettingsFiles.Themes);
        if (Directory.Exists(themeDir))
        {
            foreach (string file in Directory.GetFiles(themeDir, "*.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is { } content)
                    {
                        themes.Add((Path.GetFileName(file), content));
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        JsonNode? recent = null;
        string recentPath = Path.Combine(folder, SettingsFiles.Recent);
        if (File.Exists(recentPath))
        {
            try
            {
                recent = JsonNode.Parse(File.ReadAllText(recentPath));
            }
            catch (JsonException)
            {
            }
        }

        var bundle = SettingsBundle.Create(parts, Program.Environment.AppVersion.ToString(), App.Settings.Snapshot(), CommandService.Settings,
            CommandService.Keys.ToDocument(), themes, recent);
        try
        {
            CommandService.WriteAtomic(path, bundle.ToJson());
            ShowNotice(Loc.Format("Settings_Exported", Path.GetFileName(path)), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task ImportSettingsAsync()
    {
        string? path = await PickOpenFileAsync("HexEditor.SettingsImport");
        if (path is null || ReadBundle(path) is not { } bundle)
        {
            return;
        }

        // 区分ごとに「置き換える」「マージ」「読み込まない」を選ぶ (UI-25 の仕様 3)。
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        var choices = new Dictionary<SettingsParts, ComboBox>();
        SettingsImportPlan? plan = bundle.Settings is null ? null : bundle.PlanSettings(App.Settings.Snapshot(), CommandService.Settings, ImportMode.Merge);
        foreach (SettingsParts part in new[] { SettingsParts.Settings, SettingsParts.KeyBindings, SettingsParts.Themes, SettingsParts.RecentAndState })
        {
            if (!bundle.Parts.HasFlag(part))
            {
                continue;
            }

            int count = part switch
            {
                SettingsParts.Settings => plan?.ChangedCount ?? 0,
                SettingsParts.KeyBindings => CommandService.Keys.PreviewImport(bundle.Keybindings!, KeyImportMode.Merge).ChangedCommands.Count,
                SettingsParts.Themes => bundle.Themes!.Count,
                _ => 1,
            };
            var combo = new ComboBox { Header = Loc.Format("Settings_ImportPart", Loc.Get(part == SettingsParts.RecentAndState ? "SettingsPart_Recent" : "SettingsPart_" + part), count) };
            foreach (ImportMode mode in Enum.GetValues<ImportMode>())
            {
                combo.Items.Add(new ComboBoxItem { Content = Loc.Get("ImportMode_" + mode), Tag = mode });
            }

            combo.SelectedIndex = 1;
            AutomationProperties.SetAutomationId(combo, "Import_" + part);
            choices[part] = combo;
            body.Children.Add(combo);
        }

        if (!await ConfirmAsync(Loc.Get("Settings_ImportTitle"), body, Loc.Get("Settings_ImportConfirm"), "ImportSettingsDialog"))
        {
            return;
        }

        var modes = choices.ToDictionary(c => c.Key, c => (ImportMode)((ComboBoxItem)c.Value.SelectedItem).Tag);
        ImportSettings(bundle, modes);
    }

    /// <summary>エクスポートのファイルを読む。読めなければ理由を出して null (UI-25 の「エラー」)。</summary>
    public SettingsBundle? ReadBundle(string path)
    {
        try
        {
            if (new FileInfo(path).Length > SettingsBundle.MaxBytes)
            {
                throw new SettingsBundleException(Loc.Get("Settings_ImportTooLarge"));
            }

            return SettingsBundle.Parse(File.ReadAllText(path));
        }
        catch (SettingsBundleException ex)
        {
            ShowNotice(Loc.Format(ex.TooNew ? "Settings_ImportTooNew" : "Settings_ImportInvalid", ex.Message), InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ImportInvalid", ex.Message), InfoBarSeverity.Error);
        }

        return null;
    }

    /// <summary>
    /// インポートの本体 (テスト用の命令からも呼ぶ)。前に UI-24 と同じバックアップを作る。不正な値の項目は飛ばし、一覧を出す。
    /// 飛ばした項目を返す。
    /// </summary>
    public IReadOnlyList<SkippedSetting>? ImportSettings(SettingsBundle bundle, IReadOnlyDictionary<SettingsParts, ImportMode> modes)
    {
        SettingsParts parts = modes.Where(m => m.Value != ImportMode.Skip).Aggregate(SettingsParts.None, (a, m) => a | m.Key);
        try
        {
            App.Settings.Flush();
            CommandService.Flush();
            SettingsFiles.CreateBackup(App.Settings.Folder, SettingsFiles.PathsOf(parts), DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_BackupFailed", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        var skipped = new List<SkippedSetting>();
        if (modes.TryGetValue(SettingsParts.Settings, out ImportMode settingsMode) && settingsMode != ImportMode.Skip)
        {
            SettingsImportPlan plan = bundle.PlanSettings(App.Settings.Snapshot(), CommandService.Settings, settingsMode);
            App.Settings.ReplaceAll(plan.Result);
            skipped.AddRange(plan.Skipped);
        }

        if (modes.TryGetValue(SettingsParts.KeyBindings, out ImportMode keyMode) && keyMode != ImportMode.Skip && bundle.Keybindings is { } keys)
        {
            CommandService.Keys.ApplyImport(CommandService.Keys.PreviewImport(keys, keyMode == ImportMode.Replace ? KeyImportMode.Replace : KeyImportMode.Merge));
        }

        if (modes.TryGetValue(SettingsParts.Themes, out ImportMode themeMode) && themeMode != ImportMode.Skip && bundle.Themes is { } themes)
        {
            string dir = Path.Combine(App.Settings.Folder, SettingsFiles.Themes);
            if (themeMode == ImportMode.Replace && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            foreach ((string name, JsonNode content) in themes)
            {
                CommandService.WriteAtomic(Path.Combine(dir, name), content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        if (modes.TryGetValue(SettingsParts.RecentAndState, out ImportMode recentMode) && recentMode != ImportMode.Skip && bundle.Recent is { } recent)
        {
            CommandService.WriteAtomic(Path.Combine(App.Settings.Folder, SettingsFiles.Recent), recent.ToJsonString());
        }

        App.Settings.Flush();
        AfterSettingsChanged();
        if (skipped.Count > 0)
        {
            ShowNotice(Loc.Format("Settings_ImportSkipped", string.Join(", ", skipped.Select(s => $"{s.Key} = {s.Value}"))), InfoBarSeverity.Warning);
        }
        else
        {
            ShowNotice(Loc.Get("Settings_Imported"), InfoBarSeverity.Success);
        }

        return skipped;
    }

    // ---- キー割り当てのインポート / エクスポート (UI-21) ----

    private async Task ExportKeybindingsAsync()
    {
        string? path = await PickSaveFileAsync("HexEditor.KeybindingsExport", KeyBindingsDocument.ExportFileName);
        if (path is not null)
        {
            ExportKeybindings(path);
        }
    }

    public void ExportKeybindings(string path)
    {
        try
        {
            CommandService.WriteAtomic(path, CommandService.Keys.ToDocument().ToJson());
            ShowNotice(Loc.Format("Settings_Exported", Path.GetFileName(path)), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task ImportKeybindingsAsync()
    {
        string? path = await PickOpenFileAsync("HexEditor.KeybindingsImport");
        if (path is null || ReadKeybindings(path) is not { } doc)
        {
            return;
        }

        // 差分と重複を表示し、「置き換える」と「マージ」を選ばせる (UI-21 の仕様 2)。
        KeyImportPreview replace = CommandService.Keys.PreviewImport(doc, KeyImportMode.Replace);
        KeyImportPreview merge = CommandService.Keys.PreviewImport(doc, KeyImportMode.Merge);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("Keys_ImportTitle"),
            Content = new ScrollViewer { MaxHeight = 420, Content = ImportPreviewView(merge) },
            PrimaryButtonText = Loc.Get("ImportMode_Replace"),
            SecondaryButtonText = Loc.Get("ImportMode_Merge"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Secondary,
        };
        AutomationProperties.SetAutomationId(dialog, "KeysImportDialog");
        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.None)
        {
            ApplyKeybindingsImport(result == ContentDialogResult.Primary ? replace : merge);
        }
    }

    /// <summary>差分の一覧の表示 (追加・変更・削除、重複、不明なコマンド)。</summary>
    public static StackPanel ImportPreviewView(KeyImportPreview preview)
    {
        var body = new StackPanel { Spacing = 6, MinWidth = 420 };
        AutomationProperties.SetAutomationId(body, "KeysImportPreview");
        void Line(string text, string id)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(t, id);
            body.Children.Add(t);
        }

        Line(Loc.Format("Keys_ImportSummary", preview.Added.Count, preview.ChangedCommands.Count, preview.Removed.Count), "KeysImport_Summary");
        foreach (KeyConflict c in preview.Conflicts)
        {
            Line(Loc.Format("Keys_ImportConflict", KeyboardLayout.Format(c.First.Chord), CommandService.DisplayName(c.First.Command), CommandService.DisplayName(c.Second.Command),
                CommandService.ScopeName(c.First.Scope)), "KeysImport_Conflict");
        }

        if (preview.UnknownCommands > 0)
        {
            Line(Loc.Format("Keys_ImportUnknown", preview.UnknownCommands), "KeysImport_Unknown");
        }

        return body;
    }

    public KeyBindingsDocument? ReadKeybindings(string path)
    {
        try
        {
            if (new FileInfo(path).Length > KeyBindingsDocument.MaxImportBytes)
            {
                ShowNotice(Loc.Get("Keys_ImportTooLarge"), InfoBarSeverity.Error);
                return null;
            }

            KeyBindingsDocument doc = KeyBindingsDocument.Parse(File.ReadAllText(path));
            if (doc.IsTooNew)
            {
                ShowNotice(Loc.Format("Settings_ImportTooNew", doc.SchemaVersion), InfoBarSeverity.Error);
                return null;
            }

            return doc;
        }
        catch (JsonException ex)
        {
            ShowNotice(Loc.Format("Settings_ImportInvalid", ex.Message), InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ImportInvalid", ex.Message), InfoBarSeverity.Error);
        }

        return null;
    }

    /// <summary>インポートを確定する。前の keybindings.json を keybindings.json.bak に残す (UI-21 の仕様 4)。</summary>
    public void ApplyKeybindingsImport(KeyImportPreview preview)
    {
        CommandService.Flush();
        string path = CommandService.KeybindingsPath;
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            else
            {
                CommandService.WriteAtomic(path + ".bak", new KeyBindingsDocument().ToJson());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_BackupFailed", ex.Message), InfoBarSeverity.Error);
            return;
        }

        CommandService.Keys.ApplyImport(preview);
        CommandService.SaveKeybindingsNow();
        ShowNotice(Loc.Get("Settings_Imported"), InfoBarSeverity.Success);
    }

    // ---- ショートカット一覧 (UI-39) ----

    /// <summary>設定画面の「キーボード」を、そのコマンドで絞り込んで開く (UI-39 の仕様 5)。</summary>
    public void OpenKeyboardSettingsFor(string commandId)
    {
        OpenSettingsPage(SettingCategories.Keyboard);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Search") is TextBox search)
            {
                search.Text = commandId;
                search.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>「HTML として保存」(UI-39 の仕様 4)。</summary>
    public async Task SaveShortcutsHtmlAsync()
    {
        if (_shortcutsPage is null || await PickSaveFileAsync("HexEditor.ShortcutsHtml", "shortcuts.html") is not { } path)
        {
            return;
        }

        try
        {
            CommandService.WriteAtomic(path, _shortcutsPage.ToHtml());
            ShowNotice(Loc.Format("Settings_Exported", Path.GetFileName(path)), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>「印刷」: HTML を一時フォルダに書き出し、既定のブラウザで開いて、そこから印刷する。</summary>
    public async Task PrintShortcutsAsync()
    {
        if (_shortcutsPage is null)
        {
            return;
        }

        string path = Path.Combine(App.TempRoot, "shortcuts.html");
        CommandService.WriteAtomic(path, _shortcutsPage.ToHtml());
        await OpenUriAsync(new Uri(path));
    }

    /// <summary>設定画面からの注意の通知。</summary>
    public void ShowSettingsNotice(string message) => ShowNotice(message, InfoBarSeverity.Warning);

    // ---- 補助 ----

    private async Task<string?> PickSaveFileAsync(string identifier, string suggestedName)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = identifier };
        picker.FileTypeChoices.Add(Loc.Get("FileType_Json"), [Path.GetExtension(suggestedName)]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<string?> PickOpenFileAsync(string identifier)
    {
        if (TestHooks.OpenPickerResult(identifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = identifier };
        picker.FileTypeFilter.Add(".json");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private Task<bool> ConfirmAsync(string title, string body, string primary, string automationId) =>
        ConfirmAsync(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, primary, automationId);

    private async Task<bool> ConfirmAsync(string title, UIElement body, string primary, string automationId)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = body,
            PrimaryButtonText = primary,
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 「今すぐ再起動」(UI-22 の仕様 6、UI-43 の仕様 5): 文書を閉じる確認をしてから起動し直し、開いていたファイルを開き直す
    /// (起動の引数の扱いは <see cref="AppRestart"/>)。
    /// </summary>
    public async Task RestartAsync()
    {
        List<string> files = [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()];
        if (!await CloseAsync(Vm.Documents.ToList()))
        {
            return;
        }

        CommandService.Flush();
        if (!AppRestart.Restart(files))
        {
            ShowNotice(Loc.Get("Language_RestartFailed"), InfoBarSeverity.Warning);
        }
    }
}
