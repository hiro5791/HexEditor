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
/// 設定 (UI-22〜UI-25) とキー割り当てのインポート / エクスポート (UI-21) の操作。設定画面・ショートカット一覧はタブ列のタブ
/// (MainWindow.TabItems.cs) で、内容はエディタ領域に重ねて出す (<see cref="ToolPageHost"/>)。
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
        // 「設定: 表示言語を変更」(UI-43 の呼び出し): 設定画面の「言語」の表示言語の項目を開く。
        Commands.Register("settings.changeLanguage", () => OpenSettingsPage(SettingCategories.Language, "ui.language"));
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
        // 設定画面はアプリ全体で 1 つ。別のウィンドウで開いていれば、そのタブを前に出す (UI-22 の仕様 1)。
        if (SettingsOpenElsewhere() is { } other)
        {
            WindowManager.MarkActive(other);
            WindowManager.BringToFront(other);
            other.OpenSettingsPage(category, settingKey);
            return;
        }

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

    /// <summary>ショートカット一覧を開く (UI-39。設定画面と同じくアプリ全体で 1 つだけ。別のウィンドウで開いていれば、そのタブを前に出す)。</summary>
    public void OpenShortcutsPage()
    {
        if (WindowManager.Windows.FirstOrDefault(w => w != this && w.ToolTabIds.Contains("shortcuts")) is { } other)
        {
            WindowManager.MarkActive(other);
            WindowManager.BringToFront(other);
            other.OpenShortcutsPage();
            return;
        }

        _shortcutsPage ??= new ShortcutsPage(this);
        _shortcutsPage.Refresh();
        ShowToolPage("shortcuts", _shortcutsPage);
    }

    private void RefreshShortcutsPage() => _shortcutsPage?.Refresh();

    /// <summary>ページを開いて前に出す。ページはタブ列のタブにする (UI-22 の仕様 1。MainWindow.TabItems.cs)。</summary>
    private void ShowToolPage(string id, FrameworkElement page)
    {
        if (_toolPages.All(p => p.Id != id))
        {
            _toolPages.Add((id, page));
        }

        AddToolTab(id);
        ShowToolPageTab(id);
    }

    /// <summary>開いているページを出す (ページのタブを選んだ)。</summary>
    private void ShowToolPageTab(string id)
    {
        _activeToolPage = id;
        UpdateToolPages();
        SyncTabSelection();
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

        RemoveToolTab(id);
        _activeToolPage = _activeToolPage == id ? _toolPages.LastOrDefault().Id : _activeToolPage;
        UpdateToolPages();
        SyncTabSelection();
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
            SyncTabSelection();
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
        ToolPageHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (page.Parent is Panel old)
        {
            old.Children.Remove(page);
        }

        Grid.SetRow(page, 0);
        ToolPageHost.Children.Add(page);
        ToolPageHost.Visibility = Visibility.Visible;
        PlaceToolPageHost();
    }

    /// <summary>ページの上端をタブ列の下端に合わせる (文書のタブの見出しは使えるまま)。</summary>
    private void PlaceToolPageHost() => ToolPageHost.Margin = StartPage.Margin;

    private void InitializeToolPages()
    {
        // ページのタブの選択と文書のタブへの切り替えは MainWindow.TabItems.cs でつなぐ。
        Tabs.SizeChanged += (_, _) => PlaceToolPageHost();
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

            // 最近使ったファイルの一覧はアプリ全体で 1 つ (メモリの一覧も空にする。残すと次の記録で recent.json に書き戻る)。
            Vm.Recent.Load("{}");
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

        // 最近使ったファイルはメモリの一覧から書き出す (絶対パス。ポータブル版の recent.json は exe からの相対パスで、別の環境では解決できないため)。
        JsonNode? recent = parts.HasFlag(SettingsParts.RecentAndState) ? JsonNode.Parse(Vm.Recent.ToJson()) : null;

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
                throw new SettingsBundleException(SettingsBundleError.TooLarge);
            }

            return SettingsBundle.Parse(File.ReadAllText(path));
        }
        catch (SettingsBundleException ex)
        {
            ShowNotice(BundleErrorText(ex), InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ImportInvalid", ex.Message), InfoBarSeverity.Error);
        }

        return null;
    }

    /// <summary>エクスポートのファイルが読めない理由の表示 (UI-25 の「エラー」)。</summary>
    public static string BundleErrorText(SettingsBundleException ex) => ex.Error switch
    {
        SettingsBundleError.TooLarge => Loc.Get("Settings_ImportTooLarge"),
        SettingsBundleError.TooNew => Loc.Format("Settings_ImportTooNew", "$schemaVersion " + ex.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => Loc.Format("Settings_ImportInvalid", Loc.Format("ImportError_" + ex.Error, ex.Line)),
    };

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
            // 最近使ったファイルはメモリの一覧 (アプリ全体で 1 つ) に読み込み、一覧が recent.json に書く (ファイルだけを書き換えると、
            // 次の記録でメモリの一覧に上書きされる)。マージでは今の項目と合わせ、同じパスは新しい方を残す。
            ImportRecent(recent, recentMode);
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

    /// <summary>最近使ったファイルを読み込む (UI-25 の仕様 3)。読めない内容なら何もしない。</summary>
    private void ImportRecent(JsonNode recent, ImportMode mode)
    {
        try
        {
            JsonArray items = recent["items"] is JsonArray imported ? (JsonArray)imported.DeepClone() : [];
            if (mode == ImportMode.Merge && JsonNode.Parse(Vm.Recent.ToJson())?["items"] is JsonArray current)
            {
                foreach (JsonNode? item in current)
                {
                    items.Add(item?.DeepClone());
                }
            }

            var merged = recent.DeepClone().AsObject();
            merged["items"] = items;
            Vm.Recent.Load(merged.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            AppLog.Warning($"recent files not imported: {ex.Message}");
        }
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

        // 差分 (追加・変更・削除) と重複を表示し、「マージ」と「置き換える」を選ばせる (UI-21 の仕様 2)。選んだ読み込み方の差分を出す。
        KeyImportPreview replace = CommandService.Keys.PreviewImport(doc, KeyImportMode.Replace);
        KeyImportPreview merge = CommandService.Keys.PreviewImport(doc, KeyImportMode.Merge);
        var mode = new RadioButtons { Header = Loc.Get("Keys_ImportMode"), MaxColumns = 2 };
        mode.Items.Add(Loc.Get("ImportMode_Merge"));
        mode.Items.Add(Loc.Get("ImportMode_Replace"));
        mode.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(mode, "KeysImport_Mode");
        var previewHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        void ShowPreview() => previewHost.Content = ImportPreviewView(mode.SelectedIndex == 1 ? replace : merge, doc.Errors);
        mode.SelectionChanged += (_, _) => ShowPreview();
        ShowPreview();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(mode);
        body.Children.Add(previewHost);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("Keys_ImportTitle"),
            Content = new ScrollViewer { MaxHeight = 480, Content = body },
            PrimaryButtonText = Loc.Get("Settings_ImportConfirm"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "KeysImportDialog");
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary)
        {
            ApplyKeybindingsImport(mode.SelectedIndex == 1 ? replace : merge);
        }
    }

    /// <summary>インポートの差分の項目 (表示する文。テスト用の命令からも使う)。</summary>
    public sealed record KeyImportLines(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

    /// <summary>
    /// 差分を、コマンドごとの「追加」(今は割り当てがなく、読み込むと付く)・「変更」(キーが変わる)・「削除」(読み込むと割り当てが
    /// なくなる) に分ける (UI-21 の仕様 2)。
    /// </summary>
    public static KeyImportLines ImportLines(KeyImportPreview preview)
    {
        string Keys(IEnumerable<KeyAssignment> list) => string.Join(", ", list.Select(a => a.Scope == KeyScope.Global
            ? KeyboardLayout.Format(a.Chord) : $"{KeyboardLayout.Format(a.Chord)} ({CommandService.ScopeName(a.Scope)})"));
        var added = new List<string>();
        var changed = new List<string>();
        var removed = new List<string>();
        foreach (string command in preview.ChangedCommands)
        {
            var before = CommandService.Keys.BindingsFor(command).Select(b => new KeyAssignment(b.Command, b.Binding)).ToList();
            var after = before.Where(a => !preview.Removed.Contains(a)).Concat(preview.Added.Where(a => a.Command == command)).ToList();
            string name = CommandService.DisplayName(command);
            if (before.Count == 0)
            {
                added.Add(Loc.Format("Keys_ImportItem", name, Keys(after)));
            }
            else if (after.Count == 0)
            {
                removed.Add(Loc.Format("Keys_ImportItem", name, Keys(before)));
            }
            else
            {
                changed.Add(Loc.Format("Keys_ImportChangedItem", name, Keys(before), Keys(after)));
            }
        }

        return new KeyImportLines(added, changed, removed);
    }

    /// <summary>差分の一覧の表示 (追加・変更・削除の項目、重複、不明なコマンド、読み込めなかった行)。</summary>
    public static StackPanel ImportPreviewView(KeyImportPreview preview, IReadOnlyList<KeyBindingsError> errors)
    {
        var body = new StackPanel { Spacing = 6, MinWidth = 420 };
        AutomationProperties.SetAutomationId(body, "KeysImportPreview");
        void Line(string text, string id, bool heading = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (heading)
            {
                t.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
                t.Margin = new Thickness(0, 6, 0, 0);
                AutomationProperties.SetHeadingLevel(t, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            }

            AutomationProperties.SetAutomationId(t, id);
            body.Children.Add(t);
        }

        Line(Loc.Format("Keys_ImportSummary", preview.Added.Count, preview.ChangedCommands.Count, preview.Removed.Count), "KeysImport_Summary");
        KeyImportLines lines = ImportLines(preview);
        foreach ((IReadOnlyList<string> items, string header, string id) in new[]
        {
            (lines.Added, "Keys_ImportAddedHeader", "Added"),
            (lines.Changed, "Keys_ImportChangedHeader", "Changed"),
            (lines.Removed, "Keys_ImportRemovedHeader", "Removed"),
        })
        {
            if (items.Count > 0)
            {
                Line(Loc.Get(header), "KeysImport_" + id + "Header", heading: true);
                foreach (string item in items)
                {
                    Line(item, "KeysImport_" + id);
                }
            }
        }

        if (preview.ChangedCommands.Count == 0)
        {
            Line(Loc.Get("Keys_ImportNoChanges"), "KeysImport_NoChanges");
        }

        foreach (KeyConflict c in preview.Conflicts)
        {
            Line(Loc.Format("Keys_ImportConflict", KeyboardLayout.Format(c.First.Chord), CommandService.DisplayName(c.First.Command), CommandService.DisplayName(c.Second.Command),
                CommandService.ScopeName(c.First.Scope)), "KeysImport_Conflict");
        }

        if (preview.UnknownCommands > 0)
        {
            Line(Loc.Format("Keys_ImportUnknown", preview.UnknownCommands), "KeysImport_Unknown");
        }

        // 読み込めなかった行 (その行は飛ばして読み込む)。
        if (errors.Count > 0)
        {
            Line(Loc.Get("Keys_ImportErrorsHeader"), "KeysImport_ErrorsHeader", heading: true);
            foreach (KeyBindingsError error in errors)
            {
                Line(Loc.Format("Keys_ImportErrorLine", error.Line, CommandService.LineErrorText(error)), "KeysImport_Error");
            }
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
            ShowNotice(Loc.Format("Settings_ImportInvalid", Loc.Format("ImportError_InvalidJson", (int)(ex.LineNumber ?? 0) + 1)), InfoBarSeverity.Error);
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
        // 設定画面はアプリ全体で 1 つ。別のウィンドウで開いていれば、そのウィンドウで絞り込む。
        if (SettingsOpenElsewhere() is { } other)
        {
            other.OpenKeyboardSettingsFor(commandId);
            return;
        }

        // 絞り込みは「キーボード」の表が表示されたときに入れる (開いたばかりの画面は、まだ表の部品ができていない)。
        PendingKeyboardFilter = commandId;
        OpenSettingsPage(SettingCategories.Keyboard);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Search") is TextBox { IsLoaded: true } search
                && VisualAncestor<KeyboardSettingsSection>(search) is { } section)
            {
                section.ApplyPendingFilter();
            }
        });
    }

    /// <summary>設定画面の「キーボード」を開いたときに入れる絞り込み (コマンド ID)。表が受け取ったら null に戻す。</summary>
    internal string? PendingKeyboardFilter { get; set; }

    private static T? VisualAncestor<T>(DependencyObject node)
        where T : class
    {
        for (DependencyObject? n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (n is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>「HTML として保存」(UI-39 の仕様 4)。</summary>
    public async Task SaveShortcutsHtmlAsync()
    {
        if (_shortcutsPage is null || await PickSaveFileAsync("HexEditor.ShortcutsHtml", "shortcuts.html", "FileType_Html") is not { } path)
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
        try
        {
            CommandService.WriteAtomic(path, _shortcutsPage.ToHtml());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Settings_ExportFailed", ex.Message), InfoBarSeverity.Error);
            return;
        }

        await OpenUriAsync(new Uri(path));
    }

    /// <summary>設定画面からの注意の通知。</summary>
    public void ShowSettingsNotice(string message) => ShowNotice(message, InfoBarSeverity.Warning);

    // ---- 補助 ----

    /// <summary>保存先を選ぶ。<paramref name="fileTypeKey"/> はファイルの種類の表示名のリソースのキー (既定は JSON)。</summary>
    private async Task<string?> PickSaveFileAsync(string identifier, string suggestedName, string fileTypeKey = "FileType_Json")
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = identifier };
        picker.FileTypeChoices.Add(Loc.Get(fileTypeKey), [Path.GetExtension(suggestedName)]);
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
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 「今すぐ再起動」(UI-22 の仕様 6、UI-43 の仕様 5): すべてのウィンドウの文書を閉じる確認 (UI-13 の仕様 2) をしてから起動し直し、
    /// セッション (UI-31) ですべてのウィンドウとタブを戻す (<see cref="WindowManager.RestartAsync"/>、起動の引数は <see cref="AppRestart"/>)。
    /// </summary>
    public async Task RestartAsync()
    {
        if (!await WindowManager.RestartAsync(this) && WindowManager.LastRestartFailed)
        {
            ShowNotice(Loc.Get("Language_RestartFailed"), InfoBarSeverity.Warning);
        }
    }
}
