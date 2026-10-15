using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 「テンプレートの管理」(TPL-35。設定画面の 1 ページ): 利用者のフォルダ、テンプレートの一覧 (表示名・ファイル・出所・版・自動適用の条件・
/// 有効・無効)、名前の絞り込み、有効・無効と自動適用の切り替え、隠れたテンプレートの表示、「複製して編集」、インポート・エクスポート、
/// 「この形式では今後表示しない」の取り消し (TPL-30 の仕様 5)、インスペクタ用テンプレート (TPL-29 の仕様 1)。
/// </summary>
public static class TemplateSettingsSection
{
    public const string Category = "templates";

    private static readonly List<WeakReference<Root>> Instances = [];

    public static void Register()
    {
        if (SettingsSections.All.All(s => s.Id != "templateManager"))
        {
            SettingsSections.Register(new SettingsSection(Category, "templateManager", "SetSection_TemplateManager", w => Create(w), 10)
            {
                SearchKeys = ["SetSection_TemplateFolders", "SetSection_TemplateInspector", "TplMgr_Suppressed"],
            });
        }
    }

    /// <summary>開いている管理画面の一覧を作り直す (利用者のフォルダの変化。TPL-35 の仕様 1)。</summary>
    public static void RefreshAll()
    {
        lock (Instances)
        {
            Instances.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (WeakReference<Root> w in Instances)
            {
                if (w.TryGetTarget(out Root? r))
                {
                    r.Refresh();
                }
            }
        }
    }

    /// <summary>開いている管理画面 (テスト用の命令)。</summary>
    internal static IEnumerable<FrameworkElement> Open()
    {
        lock (Instances)
        {
            foreach (WeakReference<Root> w in Instances)
            {
                if (w.TryGetTarget(out Root? r) && r.XamlRoot is not null)
                {
                    yield return r;
                }
            }
        }
    }

    /// <summary>管理画面の一覧の行 (テスト用の命令)。</summary>
    internal static IReadOnlyList<(string Name, string Origin, bool Enabled, bool AutoApply, bool Shadowed)> Rows(FrameworkElement section) =>
        section is Root r ? r.RowsForTest : [];

    private static FrameworkElement Create(MainWindow window)
    {
        var root = new Root(window);
        lock (Instances)
        {
            Instances.Add(new WeakReference<Root>(root));
        }

        return root;
    }

    private sealed partial class Root : StackPanel
    {
        private readonly MainWindow _window;
        private readonly TextBox _filter;
        private readonly StackPanel _folders = new() { Spacing = 4 };
        private readonly StackPanel _list = new() { Spacing = 2 };
        private readonly StackPanel _suppressed = new() { Spacing = 4 };
        private readonly StackPanel _inspector = new() { Spacing = 4 };
        private readonly List<(string, string, bool, bool, bool)> _rows = [];

        public Root(MainWindow window)
        {
            _window = window;
            Spacing = 8;
            AutomationProperties.SetAutomationId(this, "TplMgr");
            Children.Add(Heading("SetSection_TemplateFolders"));
            Children.Add(_folders);
            var addFolder = Button("TplMgr_AddFolder", "TplMgr_AddFolder", async () => await AddFolderAsync());
            Children.Add(addFolder);

            Children.Add(Heading("SetSection_TemplateList"));
            _filter = new TextBox { PlaceholderText = Loc.Get("TplMgr_Filter") };
            AutomationProperties.SetAutomationId(_filter, "TplMgr_Filter");
            AutomationProperties.SetName(_filter, Loc.Get("TplMgr_Filter"));
            _filter.TextChanged += (_, _) => Refresh();
            Children.Add(_filter);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button("TplMgr_Import", "TplMgr_Import", async () => await ImportAsync()));
            actions.Children.Add(Button("TplMgr_New", "TplMgr_New", () => _ = _window.ExecuteCommandAsync("templates.new")));
            Children.Add(actions);
            Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420 });

            Children.Add(Heading("TplMgr_Suppressed"));
            Children.Add(_suppressed);

            Children.Add(Heading("SetSection_TemplateInspector"));
            Children.Add(_inspector);
            Children.Add(Button("TplMgr_AddInspector", "TplMgr_AddInspector", async () => await AddInspectorAsync()));
            Refresh();
        }

        internal IReadOnlyList<(string Name, string Origin, bool Enabled, bool AutoApply, bool Shadowed)> RowsForTest => _rows;

        public void Refresh()
        {
            TemplateCatalog catalog = TemplateService.Catalog;
            TemplatePreferences prefs = TemplateService.Preferences;

            // ---- フォルダ ----
            _folders.Children.Clear();
            IReadOnlyList<string> inaccessible = catalog.InaccessibleFolders;
            for (int i = 0; i < catalog.UserFolders.Count; i++)
            {
                string folder = catalog.UserFolders[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                if (inaccessible.Contains(folder))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = Loc.Get("TplMgr_FolderInaccessible"),
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                if (i > 0)
                {
                    string f = folder;
                    row.Children.Add(Button("TplMgr_RemoveFolder_" + i, "TplMgr_RemoveFolder", () => RemoveFolder(f)));
                }

                _folders.Children.Add(row);
            }

            // ---- 一覧 ----
            _list.Children.Clear();
            _rows.Clear();
            string filter = _filter.Text.Trim();
            CultureInfo ui = CultureInfo.CurrentUICulture;
            foreach ((TemplateCatalogEntry entry, bool shadowed) in catalog.AllEntries)
            {
                string name = entry.DisplayName(ui);
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool enabled = prefs.IsEnabled(entry);
                bool auto = prefs.IsAutoApplyEnabled(entry);
                string origin = Loc.Get(entry.Origin == TemplateOrigin.Standard ? "Tpl_OriginStandard" : "Tpl_OriginUser");
                _rows.Add((name, origin, enabled, auto, shadowed));
                _list.Children.Add(Row(entry, name, origin, enabled, auto, shadowed));
            }

            // ---- 今後表示しない ----
            _suppressed.Children.Clear();
            foreach (string path in prefs.Suppressed.Order(StringComparer.OrdinalIgnoreCase).ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                string label = catalog.AllEntries.FirstOrDefault(e => string.Equals(e.Entry.Path, path, StringComparison.OrdinalIgnoreCase)).Entry?.DisplayName(ui) ?? path;
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                string p = path;
                row.Children.Add(Button("TplMgr_Unsuppress_" + Safe(label), "TplMgr_Unsuppress", () =>
                {
                    prefs.Suppressed.Remove(p);
                    TemplateService.SavePreferences();
                    Refresh();
                }));
                _suppressed.Children.Add(row);
            }

            if (prefs.Suppressed.Count == 0)
            {
                _suppressed.Children.Add(new TextBlock { Text = Loc.Get("TplMgr_NoSuppressed"), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            }

            // ---- インスペクタ用テンプレート ----
            _inspector.Children.Clear();
            foreach (string path in InspectorPaths())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                string names = string.Join(", ", InspectorUserTypeService.RegisteredTypes(path)
                    .Select(t => t.DisplayName + (t.NotFixedSize ? " (" + Loc.Get("TplMgr_NotFixedSize") + ")" : string.Empty)));
                row.Children.Add(new TextBlock { Text = Path.GetFileName(path) + (names.Length > 0 ? ": " + names : string.Empty), VerticalAlignment = VerticalAlignment.Center });
                string p = path;
                row.Children.Add(Button("TplMgr_RemoveInspector_" + Safe(Path.GetFileName(path)), "TplMgr_RemoveFolder", () => SetInspectorPaths(InspectorPaths().Where(x => x != p))));
                _inspector.Children.Add(row);
            }
        }

        private static string Safe(string s) => new([.. s.Where(char.IsLetterOrDigit)]);

        private FrameworkElement Row(TemplateCatalogEntry entry, string name, string origin, bool enabled, bool auto, bool shadowed)
        {
            TemplateMetadata m = entry.Metadata;
            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var conditions = new List<string>();
            if (m.Magic.Count + m.MagicAll.Count > 0)
            {
                conditions.Add(Loc.Get("Tpl_MatchMagic"));
            }

            if (m.Mimes.Count > 0)
            {
                conditions.Add("MIME " + string.Join(", ", m.Mimes));
            }

            if (m.Extensions.Count > 0)
            {
                conditions.Add(string.Join(", ", m.Extensions));
            }

            string detail = origin + " · " + (TemplateCatalog.IsStandardPath(entry.Path) ? entry.Name + Path.GetExtension(entry.Path) : entry.Path)
                + (m.Version is { } v ? " · " + Loc.Format("TplApply_Version", v) : string.Empty)
                + (conditions.Count > 0 ? " · " + string.Join(" / ", conditions) : string.Empty);
            info.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
            if (shadowed)
            {
                info.Children.Add(new TextBlock
                {
                    Text = Loc.Get("TplMgr_Shadowed"),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                });
            }

            grid.Children.Add(info);
            string id = Safe(entry.Name + entry.Origin);
            var enabledBox = new CheckBox { Content = Loc.Get("TplMgr_Enabled"), IsChecked = enabled, MinWidth = 0 };
            AutomationProperties.SetAutomationId(enabledBox, "TplMgr_Enabled_" + id);
            enabledBox.Click += (_, _) =>
            {
                if (enabledBox.IsChecked == true)
                {
                    TemplateService.Preferences.Disabled.Remove(entry.Path);
                }
                else
                {
                    TemplateService.Preferences.Disabled.Add(entry.Path);
                }

                TemplateService.SavePreferences();
            };
            Grid.SetColumn(enabledBox, 1);
            grid.Children.Add(enabledBox);
            var autoBox = new CheckBox { Content = Loc.Get("TplMgr_AutoApply"), IsChecked = auto, MinWidth = 0 };
            AutomationProperties.SetAutomationId(autoBox, "TplMgr_AutoApply_" + id);
            autoBox.Click += (_, _) =>
            {
                if (autoBox.IsChecked == true)
                {
                    TemplateService.Preferences.NoAutoApply.Remove(entry.Path);
                }
                else
                {
                    TemplateService.Preferences.NoAutoApply.Add(entry.Path);
                }

                TemplateService.SavePreferences();
            };
            Grid.SetColumn(autoBox, 2);
            grid.Children.Add(autoBox);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            buttons.Children.Add(Button("TplMgr_Edit_" + id, "TplMgr_Edit", () => _window.OpenTemplateEditorFromManager(entry.Path)));
            if (entry.Origin == TemplateOrigin.Standard)
            {
                buttons.Children.Add(Button("TplMgr_Duplicate_" + id, "TplEd_Duplicate", () =>
                {
                    string copy = TemplateLibrary.Duplicate(entry.Path, TemplateService.DefaultUserFolder);
                    _window.OpenTemplateEditorFromManager(copy);
                }));
            }

            buttons.Children.Add(Button("TplMgr_Export_" + id, "TplMgr_Export", async () => await ExportAsync(entry)));
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);
            AutomationProperties.SetName(grid, name);
            return grid;
        }

        private async Task AddFolderAsync()
        {
            string? folder = await _window.PickTemplateFolderAsync();
            if (folder is null)
            {
                return;
            }

            JsonArray list = App.Settings.GetNode(TemplateService.FoldersKey) as JsonArray ?? [];
            var copy = new JsonArray([.. list.Select(n => (JsonNode?)n?.GetValue<string>()), folder]);
            App.Settings.SetNode(TemplateService.FoldersKey, copy, new JsonArray());
            TemplateService.SettingsChanged();
            Refresh();
        }

        private void RemoveFolder(string folder)
        {
            JsonArray list = App.Settings.GetNode(TemplateService.FoldersKey) as JsonArray ?? [];
            var copy = new JsonArray([.. list.Select(n => n?.GetValue<string>()).Where(s => !string.Equals(s, folder, StringComparison.OrdinalIgnoreCase)).Select(s => (JsonNode?)s)]);
            App.Settings.SetNode(TemplateService.FoldersKey, copy, new JsonArray());
            TemplateService.SettingsChanged();
            Refresh();
        }

        private async Task ImportAsync()
        {
            string? file = await _window.PickTemplateImportAsync();
            if (file is null)
            {
                return;
            }

            try
            {
                IReadOnlyList<string> imported = TemplateLibrary.Import(file, TemplateService.DefaultUserFolder);
                _window.ShowSettingsNotice(Loc.Format("TplMgr_Imported", imported.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _window.ShowSettingsNotice(Loc.Format("TplMgr_ImportError", ex.Message));
            }

            Refresh();
        }

        private async Task ExportAsync(TemplateCatalogEntry entry)
        {
            if (TemplateCatalog.IsStandardPath(entry.Path))
            {
                _window.ShowSettingsNotice(Loc.Get("TplMgr_ExportStandard"));
                return;
            }

            string? zip = await _window.PickTemplateZipSaveAsync(entry.Name + ".zip");
            if (zip is null)
            {
                return;
            }

            try
            {
                IReadOnlyList<string> files = TemplateLibrary.Export([entry.Path], zip, TemplateService.LoadOptions);
                _window.ShowSettingsNotice(Loc.Format("TplMgr_Exported", files.Count, Path.GetFileName(zip)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _window.ShowSettingsNotice(Loc.Format("TplMgr_ExportError", ex.Message));
            }
        }

        private static List<string> InspectorPaths() =>
            App.Settings.GetNode(TemplateService.InspectorKey) is JsonArray a ? [.. a.Select(n => n?.GetValue<string>()).OfType<string>()] : [];

        private void SetInspectorPaths(IEnumerable<string> paths)
        {
            App.Settings.SetNode(TemplateService.InspectorKey, new JsonArray([.. paths.Select(p => (JsonNode?)p)]), new JsonArray());
            Refresh();
        }

        private async Task AddInspectorAsync()
        {
            string? file = await _window.PickTemplateImportAsync(hxtOnly: true);
            if (file is null)
            {
                return;
            }

            SetInspectorPaths([.. InspectorPaths(), file]);
            if (InspectorUserTypeService.RegisteredTypes(file).Any(t => t.NotFixedSize))
            {
                // 可変長の型・4 KB を超える型は登録時に警告する (TPL-29 の仕様 4)
                _window.ShowSettingsNotice(Loc.Get("TplMgr_NotFixedSizeWarning"));
            }
        }

        private static TextBlock Heading(string key) => new()
        {
            Text = Loc.Get(key),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 8, 0, 0),
        };

        private static Button Button(string id, string key, Action click)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click();
            return b;
        }
    }
}
