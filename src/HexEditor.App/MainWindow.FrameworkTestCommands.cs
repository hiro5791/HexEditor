#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Views;
using HexEditor.Core.Commands;
using HexEditor.Core.Panels;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令 (テスト方針 7.2) のうち、コマンド・ショートカット・ツールバー・パネル・設定画面 (UI-04、UI-05、UI-16〜UI-25、UI-39) のもの。
/// どれも画面の操作と同じ処理を呼ぶ。テスト用の画面なので、文字列はリソース化しない。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleFrameworkTestCommandAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "commands":
                return new JsonObject
                {
                    ["items"] = new JsonArray([.. CommandService.Catalog.All.Select(c => (JsonNode?)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["title"] = CommandService.Title(c),
                        ["english"] = CommandService.EnglishName(c),
                        ["shortcut"] = CommandService.ShortcutText(c.Id),
                        ["enabled"] = Commands.StateOf(c.Id).Enabled,
                        ["menu"] = _menus.Find(c.Id) is { } item ? AutomationProperties.GetAutomationId(item) : null,
                        ["menuShortcut"] = _menus.Find(c.Id)?.KeyboardAcceleratorTextOverride,
                    })]),
                };
            case "execute":
                return new JsonObject
                {
                    ["executed"] = await Commands.ExecuteAsync(Str(request, "id"), request["argument"]?.GetValue<string>(), request["fromPalette"]?.GetValue<bool>() ?? false),
                };
            case "palette":
                if (request["text"] is { } text)
                {
                    if (!Palette.IsOpen)
                    {
                        OpenPalette(text.GetValue<string>());
                    }
                    else
                    {
                        Palette.SetText(text.GetValue<string>());
                    }
                }

                return PaletteState();
            case "paletteEnter":
                return new JsonObject { ["invoked"] = await Palette.InvokeSelectedAsync() };
            case "paletteClose":
                Palette.Close();
                return PaletteState();
            case "assignKey":
            {
                KeyAssignResult r = KeyAssign.TryAssign(Str(request, "command"), KeyChord.Parse(Str(request, "key")),
                    KeyScopes.TryParse(request["scope"]?.GetValue<string>(), out KeyScope scope) ? scope : KeyScope.Global, request["replace"]?.GetValue<bool>() ?? false);
                return new JsonObject
                {
                    ["added"] = r.Added,
                    ["rejection"] = r.Rejection,
                    ["needsReplace"] = r.NeedsReplaceConfirmation,
                    ["conflicts"] = new JsonArray([.. r.Conflicts.Select(c => (JsonNode?)new JsonObject { ["command"] = c.Command, ["name"] = CommandService.DisplayName(c.Command), ["scope"] = KeyScopes.Name(c.Binding.Scope) })]),
                    ["warnings"] = new JsonArray([.. r.Warnings.Select(w => (JsonNode?)w)]),
                };
            }

            case "keyBindings":
            {
                string id = Str(request, "command");
                return new JsonObject
                {
                    ["bindings"] = new JsonArray([.. CommandService.Keys.BindingsFor(id).Select(b => (JsonNode?)new JsonObject
                    {
                        ["key"] = b.Binding.Chord.ToString(),
                        ["scope"] = KeyScopes.Name(b.Binding.Scope),
                        ["origin"] = b.Origin.ToString(),
                    })]),
                    ["menuShortcut"] = _menus.Find(id)?.KeyboardAcceleratorTextOverride,
                    ["preset"] = CommandService.Keys.Preset,
                };
            }

            case "setPreset":
                CommandService.Keys.SwitchPreset(Str(request, "preset"), request["preferUser"]?.GetValue<bool>() ?? true);
                return new JsonObject { ["preset"] = CommandService.Keys.Preset };
            case "resetKeys":
                CommandService.Keys.ResetAll();
                return new JsonObject();
            case "flushSettings":
                App.Settings.Flush();
                CommandService.Flush();
                return new JsonObject();
            case "importKeys":
            {
                if (ReadKeybindings(Str(request, "path")) is not { } doc)
                {
                    return new JsonObject { ["read"] = false };
                }

                KeyImportPreview preview = CommandService.Keys.PreviewImport(doc, Str(request, "mode") == "replace" ? KeyImportMode.Replace : KeyImportMode.Merge);
                var result = new JsonObject
                {
                    ["read"] = true,
                    ["summary"] = Loc.Format("Keys_ImportSummary", preview.Added.Count, preview.ChangedCommands.Count, preview.Removed.Count),
                    ["unknown"] = preview.UnknownCommands,
                    ["unknownText"] = preview.UnknownCommands > 0 ? Loc.Format("Keys_ImportUnknown", preview.UnknownCommands) : null,
                    ["conflicts"] = new JsonArray([.. preview.Conflicts.Select(c => (JsonNode?)new JsonObject
                    {
                        ["key"] = c.First.Chord.ToString(),
                        ["first"] = c.First.Command,
                        ["second"] = c.Second.Command,
                    })]),
                };

                // 差分の一覧 (追加・変更・削除の項目) と読み込めなかった行 (UI-21 の仕様 2。ダイアログと同じ文)。
                KeyImportLines lines = ImportLines(preview);
                result["added"] = new JsonArray([.. lines.Added.Select(l => (JsonNode?)l)]);
                result["changed"] = new JsonArray([.. lines.Changed.Select(l => (JsonNode?)l)]);
                result["removed"] = new JsonArray([.. lines.Removed.Select(l => (JsonNode?)l)]);
                result["errors"] = new JsonArray([.. doc.Errors.Select(e => (JsonNode?)Loc.Format("Keys_ImportErrorLine", e.Line, CommandService.LineErrorText(e)))]);
                if (request["apply"]?.GetValue<bool>() == true)
                {
                    ApplyKeybindingsImport(preview);
                }

                return result;
            }

            case "exportKeys":
                ExportKeybindings(Str(request, "path"));
                return new JsonObject();
            case "toolbar":
                return new JsonObject
                {
                    ["visible"] = Toolbar.Visibility == Visibility.Visible,
                    ["items"] = new JsonArray([.. ToolbarItemIds.Select(i => (JsonNode?)i)]),
                    ["buttons"] = new JsonArray([.. Toolbar.PrimaryCommands.OfType<AppBarButton>().Select(b => (JsonNode?)new JsonObject
                    {
                        ["id"] = (string)b.Tag,
                        ["inOverflow"] = b.IsInOverflow,
                        ["toolTip"] = ToolTipService.GetToolTip(b) as string,
                        ["enabled"] = b.IsEnabled,
                    })]),
                };
            case "toolbarAdd":
                AddToolbarItem(Str(request, "id"));
                return new JsonObject();
            case "panels":
                return PanelState();
            case "panelShow":
                ShowPanel(Str(request, "id"));
                return PanelState();
            case "panelMove":
                MovePanel(Str(request, "id"), Enum.Parse<PanelDock>(Str(request, "dock"), ignoreCase: true));
                return PanelState();
            case "panelMenu":
            {
                // 見出しの右クリックメニュー (Shift+F10 と同じ) を開く。
                string id = Str(request, "id");
                FloatingPanelWindow? floating = _floatingPanels.GetValueOrDefault(id);
                PanelDockArea? area = floating?.Area ?? DockAreas.Select(d => d.Area).FirstOrDefault(a => a.Tabs.Any(t => t.Id == id));
                if (area?.MenuOf(id) is not { } menu)
                {
                    throw new InvalidOperationException($"No header for panel {id}.");
                }

                // dock を指定すると「移動 > 左 / 右 / 下 / 浮動」の項目を押す (サブメニューは開いたときに作られるので、項目の処理を直接呼ぶ)。
                if (request["dock"]?.GetValue<string>() is { } dock)
                {
                    MenuFlyoutItem item = menu.Items.OfType<MenuFlyoutSubItem>().Single().Items.OfType<MenuFlyoutItem>()
                        .Single(i => AutomationProperties.GetAutomationId(i) == $"PanelMove_{id}_{dock}");
                    if (!item.IsEnabled)
                    {
                        throw new InvalidOperationException($"Menu item for {dock} is disabled.");
                    }

                    area.RequestMove(id, Enum.Parse<PanelDock>(dock, ignoreCase: true));
                    return PanelState();
                }

                return new JsonObject { ["items"] = menu.Items.Count };
            }

            case "settingsPage":
                return SettingsPageState(request);
            case "settingSet":
            {
                SettingDefinition def = CommandService.Settings.Find(Str(request, "key")) ?? throw new ArgumentException("Unknown setting.");
                JsonNode? value = request["value"]?.DeepClone();
                if (!def.Validate(value))
                {
                    return new JsonObject { ["valid"] = false };
                }

                // 変更を始めた時刻 (TestClock)。反映までの時間をアプリの中で測るため (往復の時間を含めない)。
                double at = TestClock.NowMs;
                ChangeSetting(def, value);
                return new JsonObject { ["valid"] = true, ["atMs"] = at };
            }

            case "settingRaw":
                // 登録されていないキー (後で他の機能が登録する項目) もそのまま書く。
                App.Settings.SetNode(Str(request, "key"), request["value"]?.DeepClone(), null);
                return new JsonObject();
            case "noticeUndo":
            {
                Core.Notifications.Notification notice = Vm.Notifications.Open.LastOrDefault(n => n.Undo is not null)
                    ?? throw new InvalidOperationException("No notification with undo.");
                notice.Undo!.Execute();
                Vm.Notifications.Dismiss(notice);
                return new JsonObject { ["message"] = notice.DisplayMessage };
            }

            case "showDocument":
                // 文書のタブを選んだときと同じく、設定画面などのページを隠す。
                HideToolPages();
                return new JsonObject { ["active"] = ActiveToolPage };
            case "settingReset":
                ResetSetting(Str(request, "key"));
                return new JsonObject();
            case "resetAll":
                return new JsonObject { ["done"] = ResetAll(ParseParts(request["parts"], SettingsParts.ResetDefault)) };
            case "exportSettings":
                ExportSettings(ParseParts(request["parts"], ExportDefault), Str(request, "path"));
                return new JsonObject();
            case "importSettings":
            {
                if (ReadBundle(Str(request, "path")) is not { } bundle)
                {
                    return new JsonObject { ["read"] = false };
                }

                var modes = new Dictionary<SettingsParts, ImportMode>();
                foreach ((string part, JsonNode? mode) in request["modes"]?.AsObject() ?? [])
                {
                    modes[Enum.Parse<SettingsParts>(part, ignoreCase: true)] = Enum.Parse<ImportMode>(mode!.GetValue<string>(), ignoreCase: true);
                }

                IReadOnlyList<SkippedSetting>? skipped = ImportSettings(bundle, modes);
                return new JsonObject
                {
                    ["read"] = true,
                    ["skipped"] = new JsonArray([.. (skipped ?? []).Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["value"] = s.Value })]),
                };
            }

            case "shortcutsPage":
            {
                OpenShortcutsPage();
                var rows = _shortcutsPage!.FilteredRows();
                return new JsonObject
                {
                    ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["command"] = r.CommandId, ["keys"] = r.Keys, ["scope"] = KeyScopes.Name(r.ScopeValue), ["category"] = r.Category })]),
                    ["shown"] = SettingsPage.FindByAutomationId(ToolPageHost, "Shortcuts_List") is StackPanel list
                        ? new JsonArray([.. list.Children.OfType<Grid>().Select(g => (JsonNode?)AutomationProperties.GetAutomationId(g))])
                        : null,
                };
            }

            case "saveShortcutsHtml":
                OpenShortcutsPage();
                await SaveShortcutsHtmlAsync();
                return new JsonObject();
            case "shortcutWarnings":
            {
                // ショートカット一覧の各行の注意 (「この配列では押せません」など。UI-20 の仕様 3)。
                OpenShortcutsPage();
                return new JsonObject([.. _shortcutsPage!.FilteredRows().Where(r => ShortcutsPage.Warnings(r).Length > 0)
                    .Select(r => new KeyValuePair<string, JsonNode?>(r.CommandId + " " + KeyScopes.Name(r.ScopeValue), ShortcutsPage.Warnings(r)))]);
            }

            case "titleBarPalette":
            {
                // タイトルバーのコマンドパレットの入口 (UI-02 の仕様 4)。invoke で押す。
                if (request["invoke"]?.GetValue<bool>() == true)
                {
                    OpenPalette(">");
                }

                (string label, bool compact, double width) = PaletteButtonState;
                return new JsonObject { ["text"] = label, ["compact"] = compact, ["width"] = width, ["paletteOpen"] = Palette.IsOpen, ["paletteText"] = Palette.Text };
            }

            case "paletteChangeShortcut":
                // 候補の右クリック >「ショートカットを変更」と同じ処理 (UI-18 の呼び出し)。
                Palette.RequestChangeShortcut(Str(request, "id"));
                await Task.Delay(50);
                return new JsonObject
                {
                    ["paletteOpen"] = Palette.IsOpen,
                    ["active"] = ActiveToolPage,
                    ["category"] = _settingsPage?.Category,
                    ["search"] = (SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Search") as TextBox)?.Text,
                    ["hasMenu"] = Palette.Entries.Any(e => e.CommandId == Str(request, "id")),
                };
            case "keyboardSection":
                return KeyboardSectionState(request);
            case "hexViewMenuShortcuts":
                return new JsonObject([.. (CurrentView()?.ContextMenuShortcutTexts ?? new Dictionary<string, string>())
                    .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))]);
            default:
                return null;
        }
    }

    /// <summary>
    /// 設定画面の「キーボード」の表 (UI-18)。<c>searchByKey</c> を渡すと「キーで検索」を押してから、そのキーを検索欄にフォーカスがある
    /// ときと同じ経路 (ウィンドウのキーの振り分け) で押す。<c>reset</c> を渡すと、その行の「既定に戻す」を押す。
    /// </summary>
    private JsonObject KeyboardSectionState(JsonObject request)
    {
        // 開いている「キーボード」はそのまま使う (開き直すと表が作り直され、検索欄が空になる)。
        if (ActiveToolPage != "settings" || _settingsPage?.Category != SettingCategories.Keyboard)
        {
            OpenSettingsPage(SettingCategories.Keyboard);
        }

        _settingsPage!.UpdateLayout();
        TextBox search = SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Search") as TextBox ?? throw new InvalidOperationException("No keyboard section.");
        KeyboardSettingsSection section = Ancestor<KeyboardSettingsSection>(search) ?? throw new InvalidOperationException("No keyboard section.");
        var result = new JsonObject();
        if (request["search"] is { } text)
        {
            search.Text = text.GetValue<string>();
        }

        if (request["searchByKey"] is { } key)
        {
            section.StartSearchByKey();
            result["capturing"] = section.IsCapturingKeys;
            KeyModifiers modifiers = (request["ctrl"]?.GetValue<bool>() == true ? KeyModifiers.Ctrl : 0) | (request["shift"]?.GetValue<bool>() == true ? KeyModifiers.Shift : 0)
                | (request["alt"]?.GetValue<bool>() == true ? KeyModifiers.Alt : 0);
            DispatchResult r = RouteKey(search, (int)Enum.Parse<Windows.System.VirtualKey>(key.GetValue<string>(), ignoreCase: true), modifiers, new KeyContext(KeyScope.Global, true));
            result["handled"] = r.Handled;
            result["command"] = r.Command;
        }

        if (request["reset"] is { } reset)
        {
            result["skipped"] = new JsonArray([.. section.ResetCommand(reset.GetValue<string>()).Select(c => (JsonNode?)new JsonObject
            {
                ["key"] = c.First.Chord.ToString(),
                ["other"] = c.Second.Command,
            })]);
        }

        section.Fill();
        section.UpdateLayout();
        result["search"] = search.Text;
        result["capturingAfter"] = section.IsCapturingKeys;
        result["rows"] = new JsonArray([.. section.Rows.Select(r => (JsonNode?)new JsonObject
        {
            ["command"] = r.Command.Id,
            ["name"] = r.Name,
            ["english"] = r.English,
            ["keys"] = r.Keys,
            ["origin"] = r.Origin,
            ["warnings"] = r.Warnings,
            ["canReset"] = r.CanReset,
        })]);
        ListView list = (ListView)SettingsPage.FindByAutomationId(ToolPageHost, "Keyboard_Rows")!;
        result["realized"] = Enumerable.Range(0, section.Rows.Count).Count(i => list.ContainerFromIndex(i) is not null);
        result["isListView"] = true;
        result["goToBarVisible"] = GoToBar.Visibility == Visibility.Visible;
        return result;
    }

    private static T? Ancestor<T>(DependencyObject node)
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

    private static string Str(JsonObject request, string name) => request[name]?.GetValue<string>() ?? throw new ArgumentException($"{name} is required.");

    private static SettingsParts ParseParts(JsonNode? node, SettingsParts fallback) =>
        node is JsonArray parts ? parts.Aggregate(SettingsParts.None, (a, p) => a | Enum.Parse<SettingsParts>(p!.GetValue<string>(), ignoreCase: true)) : fallback;

    private JsonObject PaletteState() => new()
    {
        ["open"] = Palette.IsOpen,
        ["text"] = Palette.Text,
        ["message"] = Palette.MessageText,
        ["entries"] = new JsonArray([.. Palette.Entries.Select(e => (JsonNode?)new JsonObject
        {
            ["key"] = e.Key,
            ["title"] = e.Title,
            ["secondary"] = e.Secondary,
            ["shortcut"] = e.Shortcut,
            ["reason"] = e.Reason,
            ["header"] = e.IsHeader,
        })]),
    };

    private JsonObject PanelState()
    {
        var panels = new JsonObject();
        foreach (PanelRegistration p in PanelRegistry.All)
        {
            panels[p.Id] = new JsonObject
            {
                ["location"] = PanelLocation(p.Id),
                ["shown"] = IsPanelShown(p.Id),
            };
        }

        nint main = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return new JsonObject
        {
            ["panels"] = panels,
            ["areas"] = new JsonObject([.. DockAreas.Select(d => new KeyValuePair<string, JsonNode?>(PanelLayout.DockName(d.Dock), new JsonObject
            {
                ["visible"] = d.Area.Visibility == Visibility.Visible,
                ["tabs"] = new JsonArray([.. d.Area.Tabs.Select(t => (JsonNode?)t.Id)]),
                ["active"] = d.Area.ActivePanel,
            }))]),
            ["floating"] = new JsonArray([.. _floatingPanels.Values.Select(w => (JsonNode?)new JsonObject
            {
                ["id"] = w.PanelId,
                ["ownedByMain"] = w.OwnerHwnd == main,
                ["visible"] = w.AppWindow.IsVisible,
            })]),
            ["layout"] = _panelLayout.ToJson(),
        };
    }

    private JsonObject SettingsPageState(JsonObject request)
    {
        if (request["open"]?.GetValue<bool>() == true || _settingsPage is null)
        {
            OpenSettingsPage(request["category"]?.GetValue<string>());
        }
        else if (request["category"] is { } category)
        {
            OpenSettingsPage(category.GetValue<string>());
        }

        if (request["search"] is { } search)
        {
            _settingsPage!.SetSearch(search.GetValue<string>());
        }

        _settingsPage!.UpdateLayout();
        var cards = new JsonArray();
        foreach (SettingDefinition def in CommandService.Settings.All)
        {
            if (SettingsPage.FindByAutomationId(_settingsPage, "Setting_" + def.Key) is FrameworkElement)
            {
                bool marked = SettingsPage.FindByAutomationId(_settingsPage, "SettingModified_" + def.Key) is FrameworkElement { Opacity: > 0 };
                bool reset = SettingsPage.FindByAutomationId(_settingsPage, "SettingReset_" + def.Key) is FrameworkElement { Visibility: Visibility.Visible };
                cards.Add(new JsonObject
                {
                    ["key"] = def.Key,
                    ["name"] = (SettingsPage.FindByAutomationId(_settingsPage, "SettingName_" + def.Key) as TextBlock)?.Text,
                    ["modified"] = marked,
                    ["resetVisible"] = reset,
                    ["restartNote"] = SettingsPage.FindByAutomationId(_settingsPage, "SettingRestart_" + def.Key) is not null,
                    ["error"] = (SettingsPage.FindByAutomationId(_settingsPage, "SettingError_" + def.Key) as TextBlock) is { Visibility: Visibility.Visible } e ? e.Text : null,
                });
            }
        }

        return new JsonObject
        {
            ["pages"] = new JsonArray([.. OpenToolPages.Select(p => (JsonNode?)p)]),
            ["active"] = ActiveToolPage,
            ["category"] = _settingsPage.Category,
            ["cards"] = cards,
            ["restartBar"] = _settingsPage.RestartBarOpen,
            ["toolbarMissing"] = (SettingsPage.FindByAutomationId(_settingsPage, "Toolbar_MissingCount") as TextBlock)?.Text,
        };
    }
}
#endif
