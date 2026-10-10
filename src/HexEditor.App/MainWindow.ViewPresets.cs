using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Views;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// 表示プリセット (VIEW-42 の仕様 6・7): 表示 > 表示設定 > プリセット (一覧、現在の設定をプリセットとして保存…、プリセットの管理…)、
/// コマンドパレット「表示: プリセットを適用」、設定画面「表示」ページの管理 (削除・エクスポート・インポート)。拡張子による自動適用は
/// <see cref="ViewOptions.Attach"/> (初めて開いたとき)。
/// </summary>
public sealed partial class MainWindow
{
    private MenuFlyoutSubItem? _presetMenu;

    /// <summary>プリセットの表示設定に入れるバイトテーマ (アプリ全体の設定 view.byteTheme の値)。</summary>
    private const string PresetByteThemeKey = "byteTheme";

    private static ViewSettingsStore PresetStore => new(App.Settings, ViewOptions.Documents);

    /// <summary>プリセットの一覧が変わった (メニューと設定画面を作り直す)。アプリ全体で共通。</summary>
    internal static event EventHandler? ViewPresetsChanged;

    /// <summary>最後のインポートの誤り (テスト用)。</summary>
    internal string? LastPresetImportError { get; private set; }

    /// <summary>表示 > 表示設定 のサブメニューにプリセットの項目を足す (ViewMenu から呼ぶ)。</summary>
    private void AddPresetMenu(MenuFlyoutSubItem settings)
    {
        Commands.Register("view.presetApply", new CommandHandler(ApplyPresetAsync, NeedsDocument));
        Commands.Register("view.presetSave", SavePresetAsync, NeedsDocument);
        Commands.Register("view.presetManage", () => OpenSettingsPage(SettingCategories.View, ViewPresets.SettingsKey));
        _presetMenu = Sub("Command_ViewPresets", "Menu_View_Presets");
        settings.Items.Add(new MenuFlyoutSeparator());
        settings.Items.Add(_presetMenu);
        RefreshPresetMenu();
        EventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(RefreshPresetMenu);
        ViewPresetsChanged += changed;
        Closed += (_, _) => ViewPresetsChanged -= changed;
    }

    /// <summary>プリセットのサブメニューを作り直す (プリセットの一覧、区切り、保存、管理)。</summary>
    private void RefreshPresetMenu()
    {
        if (_presetMenu is null)
        {
            return;
        }

        _presetMenu.Items.Clear();
        IReadOnlyList<ViewPreset> presets = PresetStore.Presets;
        for (int i = 0; i < presets.Count; i++)
        {
            string name = presets[i].Name;
            var item = new MenuFlyoutItem { Text = name };
            AutomationProperties.SetAutomationId(item, "Command_ViewPreset_" + i);
            item.Click += (_, _) => _ = Commands.ExecuteAsync("view.presetApply", name);
            _presetMenu.Items.Add(item);
        }

        if (presets.Count > 0)
        {
            _presetMenu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach ((string id, string key, string command) in new[]
        {
            ("Command_ViewPresetSave", "Menu_View_PresetSave", "view.presetSave"),
            ("Command_ViewPresetManage", "Menu_View_PresetManage", "view.presetManage"),
        })
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey") };
            AutomationProperties.SetAutomationId(item, id);
            CommandUi.SetId(item, command);
            _presetMenu.Items.Add(item);
        }
    }

    /// <summary>「表示: プリセットを適用」。引数はプリセットの名前。引数がなければ (コマンドパレット) 設定画面のプリセットの管理を開く。</summary>
    private Task ApplyPresetAsync(string? name)
    {
        if (name is not { Length: > 0 })
        {
            OpenSettingsPage(SettingCategories.View, ViewPresets.SettingsKey);
            return Task.CompletedTask;
        }

        if (Editor is { } editor && PresetStore.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } preset)
        {
            editor.ApplyView(preset.ApplyTo(editor.View));

            // バイトテーマはアプリ全体の設定 (VIEW-17) なので、プリセットを選んで適用したときだけ変える。
            if (preset.View[PresetByteThemeKey] is JsonValue theme && theme.TryGetValue(out string? themeKey))
            {
                SetByteTheme(themeKey);
            }

            UpdateViewMenu();
            ShowStatusMessage(Loc.Format("Preset_Applied", preset.Name));
        }

        return Task.CompletedTask;
    }

    /// <summary>「現在の設定をプリセットとして保存…」: 名前と自動適用の条件 (拡張子) を入力して保存する。同じ名前は置き換える。</summary>
    private async Task SavePresetAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var name = new TextBox { Header = Loc.Get("Preset_Name") };
        AutomationProperties.SetAutomationId(name, "Preset_Name");
        var extensions = new TextBox { Header = Loc.Get("Preset_Extensions"), PlaceholderText = ".gba;.nes" };
        AutomationProperties.SetAutomationId(extensions, "Preset_Extensions");
        var body = new StackPanel { Spacing = 8, MinWidth = 320 };
        body.Children.Add(name);
        body.Children.Add(extensions);
        ViewSettings captured = editor.View;
        if (!await ConfirmAsync(Loc.Get("Preset_SaveTitle"), body, Loc.Get("Preset_Save"), "PresetSaveDialog") || name.Text.Trim().Length == 0)
        {
            return;
        }

        IReadOnlyList<ViewPreset> presets = PresetStore.Presets;
        if (presets.Count >= ViewPresets.MaxPresets && !presets.Any(p => string.Equals(p.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ShowNotice(Loc.Format("Preset_TooMany", ViewPresets.MaxPresets), InfoBarSeverity.Warning);
            return;
        }

        ViewPreset preset = ViewPresets.FromView(name.Text, extensions.Text, captured);
        preset.View[PresetByteThemeKey] = App.Settings.GetString(ByteThemeKey, "none");
        PresetStore.SavePresets([.. presets, preset]);
        ViewPresetsChanged?.Invoke(null, EventArgs.Empty);
        ShowStatusMessage(Loc.Format("Preset_Saved", name.Text.Trim()));
    }

    /// <summary>プリセットをエクスポートする (VIEW-42 の仕様 7)。</summary>
    internal bool ExportViewPresets(string path)
    {
        try
        {
            File.WriteAllText(path, ViewPresets.Export(PresetStore.Presets));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Preset_ExportError", ex.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    /// <summary>
    /// プリセットをインポートする (VIEW-42 の仕様 7・「エラー」): 同じ名前は置き換える。不正な JSON は理由を InfoBar で示し、一覧を変えない。
    /// 知らない項目は無視する。
    /// </summary>
    internal bool ImportViewPresets(string path)
    {
        LastPresetImportError = null;
        try
        {
            IReadOnlyList<ViewPreset> imported = ViewPresets.Import(File.ReadAllText(path));
            PresetStore.SavePresets([.. PresetStore.Presets, .. imported]);
            ViewPresetsChanged?.Invoke(null, EventArgs.Empty);
            ShowNotice(Loc.Format("Preset_Imported", imported.Count), InfoBarSeverity.Success);
            return true;
        }
        catch (ViewPresetFormatException ex)
        {
            LastPresetImportError = ex.Line is { } line ? Loc.Format("Preset_ImportErrorLine", Path.GetFileName(path), line, ex.Message)
                : Loc.Format("Preset_ImportError", Path.GetFileName(path), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastPresetImportError = Loc.Format("Preset_ImportError", Path.GetFileName(path), ex.Message);
        }

        ShowNotice(LastPresetImportError, InfoBarSeverity.Error);
        return false;
    }

    /// <summary>プリセットを削除する。</summary>
    internal void DeleteViewPreset(string name)
    {
        PresetStore.SavePresets(PresetStore.Presets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
        ViewPresetsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>設定画面「表示」ページのプリセットの管理 (一覧、削除、エクスポート、インポート)。</summary>
    internal static void RegisterPresetSection() =>
        SettingsSections.Register(new SettingsSection(SettingCategories.View, "viewPresets", "SetSection_ViewPresets", w => w.CreatePresetSection(), 50)
        {
            SearchKeys = ["SetSearch_ViewPresets"],
        });

    private FrameworkElement CreatePresetSection()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240, MinWidth = 320 };
        AutomationProperties.SetAutomationId(list, "Presets_List");
        AutomationProperties.SetName(list, Loc.Get("SetSection_ViewPresets"));
        var delete = new Button { Content = Loc.Get("Preset_Delete") };
        AutomationProperties.SetAutomationId(delete, "Presets_Delete");
        var export = new Button { Content = Loc.Get("Preset_Export") };
        AutomationProperties.SetAutomationId(export, "Presets_Export");
        var import = new Button { Content = Loc.Get("Preset_Import") };
        AutomationProperties.SetAutomationId(import, "Presets_Import");

        void Fill()
        {
            list.Items.Clear();
            foreach (ViewPreset p in PresetStore.Presets)
            {
                string text = p.Extensions.Count > 0 ? $"{p.Name} ({string.Join(";", p.Extensions)})" : p.Name;
                var item = new ListViewItem { Content = text, Tag = p.Name };
                AutomationProperties.SetName(item, text);
                list.Items.Add(item);
            }

            delete.IsEnabled = list.SelectedItem is not null;
            export.IsEnabled = list.Items.Count > 0;
        }

        list.SelectionChanged += (_, _) => delete.IsEnabled = list.SelectedItem is not null;
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem { Tag: string name })
            {
                DeleteViewPreset(name);
                Fill();
            }
        };
        export.Click += async (_, _) =>
        {
            const string suggested = "view-presets.json";
            if (!TestHooks.TrySavePicker(suggested, out string? path))
            {
                var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.ViewPresets" };
                picker.FileTypeChoices.Add("JSON", [".json"]);
                path = (await picker.PickSaveFileAsync())?.Path;
            }

            if (path is not null)
            {
                ExportViewPresets(path);
            }
        };
        import.Click += async (_, _) =>
        {
            const string identifier = "HexEditor.ViewPresetsImport";
            string? path = TestHooks.OpenPickerResult(identifier)?.FirstOrDefault();
            if (path is null)
            {
                var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = identifier };
                picker.FileTypeFilter.Add(".json");
                path = (await picker.PickSingleFileAsync())?.Path;
            }

            if (path is not null)
            {
                ImportViewPresets(path);
                Fill();
            }
        };
        Fill();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(delete);
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Preset_SectionHelp"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        return panel;
    }

#if HEX_TEST_HOOKS
    /// <summary>テスト用の命令 "viewPresets": {import: パス, export: パス, delete: 名前}。一覧 (名前) と最後のインポートの誤りを返す。</summary>
    private JsonObject TestViewPresets(JsonObject request)
    {
        if (request["import"]?.GetValue<string>() is { } import)
        {
            ImportViewPresets(import);
        }

        if (request["export"]?.GetValue<string>() is { } export)
        {
            ExportViewPresets(export);
        }

        if (request["delete"]?.GetValue<string>() is { } delete)
        {
            DeleteViewPreset(delete);
        }

        return new JsonObject
        {
            ["presets"] = new JsonArray([.. PresetStore.Presets.Select(p => (JsonNode?)p.Name)]),
            ["error"] = LastPresetImportError,
        };
    }
#endif
}
