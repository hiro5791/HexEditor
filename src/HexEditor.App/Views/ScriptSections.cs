using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Core.Scripting.Hooks;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 設定「自動化」の専用の区画: 自動実行 (フック。AUTO-25 の画面)、スクリプトのフォルダ (AUTO-19 の仕様 1)、信頼と許可 (AUTO-34 の仕様 5)、
/// C# スクリプト コンポーネント (AUTO-03 の「画面」)。
/// 自動実行はフックごとにスクリプトを追加・削除・並べ替え・有効 / 無効にできる。
/// </summary>
public static class ScriptSections
{
    public static void Register()
    {
        SettingsSections.Register(new SettingsSection(SettingCategories.Automation, "scriptHooks", "SetSection_ScriptHooks", Hooks, 20)
        {
            SearchKeys = ["SetSearch_ScriptHooks"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Automation, "scriptFolders", "SetSection_ScriptFolders", Folders, 30)
        {
            SearchKeys = ["SetSearch_ScriptFolders"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Automation, "scriptTrust", "SetSection_ScriptTrust", Trust, 40)
        {
            SearchKeys = ["SetSearch_ScriptTrust"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Automation, "csharpComponent", "SetSection_CSharpComponent", CSharpComponent, 50)
        {
            SearchKeys = ["SetSearch_CSharpComponent"],
        });
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button Small(string glyph, string name, string id)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    // ---- 自動実行 (フック) ----

    private static FrameworkElement Hooks(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 14 };
        AutomationProperties.SetAutomationId(panel, "ScriptHooks");
        panel.Children.Add(Secondary(Loc.Get("Hooks_Intro")));
        if (Hosting.Program.CommandLine.SafeMode)
        {
            panel.Children.Add(Secondary(Loc.Get("Hooks_SafeMode")));
        }

        var kinds = new StackPanel { Spacing = 16 };
        panel.Children.Add(kinds);
        void Render()
        {
            kinds.Children.Clear();
            HookConfig config = ScriptWorkspace.HookStore.Load();
            foreach (HookKind kind in HookKinds.All)
            {
                kinds.Children.Add(Kind(window, config, kind, Render));
            }
        }

        Render();
        window.PendingHookScript = null;
        return panel;
    }

    private static FrameworkElement Kind(MainWindow window, HookConfig config, HookKind kind, Action refresh)
    {
        string name = HookKinds.Name(kind);
        var panel = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(panel, "Hooks_" + name);
        panel.Children.Add(Heading($"{Loc.Get("Hook_Kind_" + name)}  ({Loc.Format("Hooks_Timeout", HookKinds.DefaultTimeout(kind).TotalSeconds)})"));
        IReadOnlyList<HookEntry> entries = config.Entries(kind);
        if (entries.Count == 0)
        {
            panel.Children.Add(Secondary(Loc.Get("Hooks_None")));
        }

        for (int i = 0; i < entries.Count; i++)
        {
            panel.Children.Add(Row(window, kind, entries[i], i, entries.Count, refresh));
        }

        var add = new Button { Content = Loc.Get("Hooks_Add") };
        AutomationProperties.SetAutomationId(add, "Hooks_Add_" + name);
        add.Click += async (_, _) => await AddAsync(window, kind, refresh, null);
        panel.Children.Add(add);
        if (window.PendingHookScript is { } pending && kind == HookKind.Open && !entries.Any(e => e.Script == pending))
        {
            // スクリプト一覧の「自動実行の設定」から来た: 追加する欄を示す (登録はこの画面で利用者が明示的に行う。AUTO-25 の仕様 4)。
            panel.Children.Add(Secondary(Loc.Format("Hooks_PendingScript", System.IO.Path.GetFileName(pending))));
        }

        return panel;
    }

    private static FrameworkElement Row(MainWindow window, HookKind kind, HookEntry entry, int index, int count, Action refresh)
    {
        string name = HookKinds.Name(kind);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(row, $"Hook_Row_{name}_{index}");
        string display = System.IO.Path.GetFileName(entry.Script);
        var enabled = new CheckBox { IsChecked = entry.Enabled, MinWidth = 0, Content = display };
        AutomationProperties.SetAutomationId(enabled, $"Hook_Enabled_{name}_{index}");
        AutomationProperties.SetName(enabled, Loc.Format("Hooks_EnabledName", display));
        enabled.Click += (_, _) =>
        {
            ScriptWorkspace.Hooks.Update(c => c.Replace(kind, index, entry with { Enabled = enabled.IsChecked == true }));
            refresh();
        };
        row.Children.Add(enabled);

        string detail = entry.Script;
        if (entry.Condition is { IsEmpty: false } condition)
        {
            detail += "  [" + string.Join(", ", new[] { condition.Extension, condition.PathPattern, condition.Magic, condition.Source }.Where(s => !string.IsNullOrWhiteSpace(s))) + "]";
        }

        TextBlock text = Secondary(detail);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (HookKinds.SupportsCondition(kind))
        {
            Button conditionButton = Small("", Loc.Get("Hooks_Condition"), $"Hook_Condition_{name}_{index}");
            conditionButton.Click += async (_, _) =>
            {
                if (await EditConditionAsync(window, entry.Condition) is { } edited)
                {
                    ScriptWorkspace.Hooks.Update(c => c.Replace(kind, index, entry with { Condition = edited.IsEmpty ? null : edited }));
                    refresh();
                }
            };
            actions.Children.Add(conditionButton);
        }

        Button up = Small("", Loc.Get("Hooks_MoveUp"), $"Hook_Up_{name}_{index}");
        up.IsEnabled = index > 0;
        up.Click += (_, _) =>
        {
            ScriptWorkspace.Hooks.Update(c => c.Move(kind, index, -1));
            refresh();
        };
        Button down = Small("", Loc.Get("Hooks_MoveDown"), $"Hook_Down_{name}_{index}");
        down.IsEnabled = index < count - 1;
        down.Click += (_, _) =>
        {
            ScriptWorkspace.Hooks.Update(c => c.Move(kind, index, 1));
            refresh();
        };
        Button remove = Small("", Loc.Get("Hooks_Remove"), $"Hook_Remove_{name}_{index}");
        remove.Click += (_, _) =>
        {
            ScriptWorkspace.Hooks.Update(c => c.Remove(kind, index));
            refresh();
        };
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return row;
    }

    /// <summary>スクリプトを選んで追加する (一覧のスクリプトから選ぶか、ファイルを参照する)。</summary>
    private static async Task AddAsync(MainWindow window, HookKind kind, Action refresh, string? preselect)
    {
        var candidates = ScriptWorkspace.Library.Scripts.Where(n => n.IsCommand).ToList();
        string? chosen = null;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260, ItemsSource = candidates.Select(n => $"{n.Name}  ({n.RelativePath})").ToList() };
        AutomationProperties.SetAutomationId(list, "Hooks_AddList");
        var browse = new Button { Content = Loc.Get("Hooks_Browse") };
        AutomationProperties.SetAutomationId(browse, "Hooks_Browse");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Hooks_PickScript"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(list);
        body.Children.Add(browse);
        ContentDialog dialog = Controls.DialogParts.Dialog(window.Content as FrameworkElement ?? throw new InvalidOperationException(), "HookAddDialog",
            Loc.Get("Hooks_Add"), body, Loc.Get("Hooks_AddButton"));
        if (preselect is not null)
        {
            list.SelectedIndex = candidates.FindIndex(n => string.Equals(n.Path, preselect, StringComparison.OrdinalIgnoreCase));
        }

        browse.Click += async (_, _) =>
        {
            if ((await window.ScriptPickAsync("open", new Sdk.PickOptions { Filters = ["*.js", "*.mjs", "*.csx"] }))?.FirstOrDefault() is { } path)
            {
                chosen = path;
                dialog.Hide();
            }
        };
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        if (chosen is null && result == ContentDialogResult.Primary && list.SelectedIndex >= 0)
        {
            chosen = candidates[list.SelectedIndex].Path;
        }

        if (chosen is not null)
        {
            ScriptWorkspace.Hooks.Update(c => c.Add(kind, new HookEntry(chosen)));
            refresh();
        }
    }

    /// <summary>「ファイルを開いたとき」の絞り込みの条件 (拡張子・パス・先頭のマジックバイト・データソースの種類)。</summary>
    private static async Task<HookCondition?> EditConditionAsync(MainWindow window, HookCondition? current)
    {
        TextBox extension = Controls.DialogParts.Field("HookCondition_Extension", Loc.Get("Hooks_CondExtension"), current?.Extension ?? string.Empty, monospace: true);
        TextBox path = Controls.DialogParts.Field("HookCondition_Path", Loc.Get("Hooks_CondPath"), current?.PathPattern ?? string.Empty, monospace: true);
        TextBox magic = Controls.DialogParts.Field("HookCondition_Magic", Loc.Get("Hooks_CondMagic"), current?.Magic ?? string.Empty, monospace: true);
        ComboBox source = Controls.DialogParts.Combo("HookCondition_Source", Loc.Get("Hooks_CondSource"),
            [Loc.Get("Hooks_SourceAny"), "file", "disk", "volume", "process", "memory"], current?.Source is { Length: > 0 } s ? Math.Max(0, new[] { "", "file", "disk", "volume", "process", "memory" }.ToList().IndexOf(s)) : 0);
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Hooks_CondIntro"), TextWrapping = TextWrapping.Wrap });
        foreach (UIElement e in new UIElement[] { extension, path, magic, source })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = Controls.DialogParts.Dialog(window.Content as FrameworkElement ?? throw new InvalidOperationException(), "HookConditionDialog",
            Loc.Get("Hooks_Condition"), body, Loc.Get("Common_Ok"));
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new HookCondition
        {
            Extension = extension.Text.Trim(),
            PathPattern = path.Text.Trim(),
            Magic = magic.Text.Trim(),
            Source = source.SelectedIndex > 0 ? (string)source.SelectedItem : string.Empty,
        };
    }

    // ---- スクリプトのフォルダ ----

    private static FrameworkElement Folders(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "ScriptFolders");
        panel.Children.Add(Secondary(Loc.Get("ScriptFolders_Intro")));
        var rows = new StackPanel { Spacing = 4 };
        panel.Children.Add(rows);
        void Render()
        {
            rows.Children.Clear();
            IReadOnlyList<string> folders = ScriptWorkspace.ExtraFolders();
            if (folders.Count == 0)
            {
                rows.Children.Add(Secondary(Loc.Get("ScriptFolders_None")));
            }

            for (int i = 0; i < folders.Count; i++)
            {
                int index = i;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new TextBlock { Text = folders[i], FontFamily = Controls.DialogParts.Mono, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                AutomationProperties.SetAutomationId(text, "ScriptFolder_Path_" + i);
                Button remove = Small("", Loc.Get("ScriptFolders_Remove"), "ScriptFolder_Remove_" + i);
                remove.Click += (_, _) =>
                {
                    SetFolders([.. ScriptWorkspace.ExtraFolders().Where((_, n) => n != index)]);
                    Render();
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(text);
                row.Children.Add(remove);
                rows.Children.Add(row);
            }
        }

        var add = new Button { Content = Loc.Get("ScriptFolders_Add") };
        AutomationProperties.SetAutomationId(add, "ScriptFolders_Add");
        add.Click += async (_, _) =>
        {
            if ((await window.ScriptPickAsync("folder", new Sdk.PickOptions()))?.FirstOrDefault() is { } folder
                && !ScriptWorkspace.ExtraFolders().Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                SetFolders([.. ScriptWorkspace.ExtraFolders(), folder]);
                Render();
            }
        };
        Render();
        panel.Children.Add(add);
        return panel;
    }

    // ---- 信頼と許可 (AUTO-34 の仕様 5・9) ----

    /// <summary>
    /// 「常に許可する」を選んだスクリプトの一覧と取り消し。取り消すと、次の実行で再び確認が出る。組織の設定 (<c>scripts.policy</c>) があれば、
    /// その内容を示す (設定ファイルで決まり、ここでは変えられない)。プラグインの信頼はプラグインマネージャで扱う。
    /// </summary>
    private static FrameworkElement Trust(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "ScriptTrust");
        panel.Children.Add(Secondary(Loc.Get("TrustList_Intro")));
        string policy = App.Settings.GetString(ScriptSettingKeys.Policy, "none");
        if (policy != "none")
        {
            TextBlock managed = Secondary(Loc.Format("TrustList_Policy", Loc.TryGet("SetOpt_scripts_policy_" + policy) ?? policy));
            AutomationProperties.SetAutomationId(managed, "ScriptTrust_Policy");
            panel.Children.Add(managed);
        }

        var rows = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(rows, "ScriptTrust_List");
        panel.Children.Add(rows);
        void Render()
        {
            rows.Children.Clear();
            IReadOnlyList<TrustRecord> records = ScriptingService.Trust.AllowedScripts;
            if (records.Count == 0)
            {
                TextBlock none = Secondary(Loc.Get("TrustList_None"));
                AutomationProperties.SetAutomationId(none, "ScriptTrust_None");
                rows.Children.Add(none);
            }

            for (int i = 0; i < records.Count; i++)
            {
                rows.Children.Add(TrustRow(records[i], i, Render));
            }
        }

        Render();
        var plugins = new HyperlinkButton { Content = Loc.Get("TrustList_Plugins"), Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(plugins, "ScriptTrust_Plugins");
        plugins.Click += (_, _) => _ = window.Commands.ExecuteAsync("plugins.manage");
        panel.Children.Add(plugins);
        return panel;
    }

    private static FrameworkElement TrustRow(TrustRecord record, int index, Action refresh)
    {
        string path = record.DisplayPath ?? record.Key;
        string name = System.IO.Path.GetFileName(path);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(row, "ScriptTrust_Row_" + index);
        var text = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "ScriptTrust_Name_" + index);
        text.Children.Add(title);
        TextBlock location = Secondary(path);
        location.FontFamily = Controls.DialogParts.Mono;
        text.Children.Add(location);
        string permissions = record.Permissions.Count == 0
            ? Loc.Get("TrustList_NoPermissions")
            : Loc.Format("TrustList_Permissions", string.Join(", ", record.Permissions.Select(p => Loc.TryGet("Script_Permission_" + p.Replace('-', '_')) ?? p)));
        TextBlock granted = Secondary(Loc.Format("TrustList_Granted", permissions, record.Time.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
        AutomationProperties.SetAutomationId(granted, "ScriptTrust_Permissions_" + index);
        text.Children.Add(granted);
        row.Children.Add(text);

        var revoke = new Button { Content = Loc.Get("TrustList_Revoke"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(revoke, "ScriptTrust_Revoke_" + index);
        AutomationProperties.SetName(revoke, Loc.Format("TrustList_RevokeName", name));
        revoke.Click += (_, _) =>
        {
            ScriptingService.Trust.Revoke(record.Kind, record.Key);
            refresh();
        };
        Grid.SetColumn(revoke, 1);
        row.Children.Add(revoke);
        return row;
    }

    // ---- C# スクリプト コンポーネント (AUTO-03 の「画面」) ----

    /// <summary>コンポーネントのバージョン (入っていなければその旨と「追加」)、削除ボタン、コンパイル結果のキャッシュの削除ボタン。</summary>
    private static FrameworkElement CSharpComponent(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "CSharpComponent");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "CSharpComponent_Status");
        TextBlock message = Secondary(string.Empty);
        AutomationProperties.SetAutomationId(message, "CSharpComponent_Message");
        message.Visibility = Visibility.Collapsed;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = Loc.Get("CSharpComponent_AddButton") };
        AutomationProperties.SetAutomationId(add, "CSharpComponent_AddButton");
        var remove = new Button { Content = Loc.Get("CSharpComponent_Remove") };
        AutomationProperties.SetAutomationId(remove, "CSharpComponent_Remove");
        var clear = new Button { Content = Loc.Get("CSharpComponent_ClearCache") };
        AutomationProperties.SetAutomationId(clear, "CSharpComponent_ClearCache");
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        buttons.Children.Add(clear);
        panel.Children.Add(status);
        panel.Children.Add(buttons);
        panel.Children.Add(message);

        void Show(string text)
        {
            message.Text = text;
            message.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Render()
        {
            CSharpComponentManager components = ScriptingService.Components;
            IReadOnlyList<string> installed = components.InstalledVersions;
            status.Text = components.IsInstalled
                ? Loc.Format("CSharpComponent_InstalledVersion", components.Manifest?.Version ?? string.Empty)
                : installed.Count > 0 ? Loc.Format("CSharpComponent_OtherVersion", string.Join(", ", installed))
                : Loc.Get("CSharpComponent_NotInstalled");
            add.IsEnabled = !components.IsInstalled;
            remove.IsEnabled = installed.Count > 0;
            clear.IsEnabled = Directory.Exists(ScriptingService.Folders.CSharpCache);
        }

        add.Click += async (_, _) =>
        {
            Show(string.Empty);
            await window.Commands.ExecuteAsync("scripting.addCSharpComponent");
            Render();
        };
        remove.Click += (_, _) =>
        {
            try
            {
                ScriptingService.Components.Remove();
                Show(Loc.Get("CSharpComponent_Removed"));
            }
            catch (Exception ex) when (ex is CSharpComponentException or IOException or UnauthorizedAccessException)
            {
                Show(ex.Message);
            }

            Render();
        };
        clear.Click += (_, _) =>
        {
            try
            {
                Directory.Delete(ScriptingService.Folders.CSharpCache, recursive: true);
                Show(Loc.Get("CSharpComponent_CacheCleared"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show(ex.Message);
            }

            Render();
        };
        Render();
        return panel;
    }

    private static void SetFolders(IEnumerable<string> folders)
    {
        var array = new JsonArray();
        foreach (string f in folders)
        {
            array.Add(f);
        }

        App.Settings.SetNode("scripts.folders", array, new JsonArray());
    }
}
