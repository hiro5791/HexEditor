#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、ファイル・セッションの命令 (最近使ったファイル、閉じたタブ、セッション、スタートページ、外部変更)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>この担当の命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleFilesTestCommandAsync(string cmd, JsonObject request)
    {
        await Task.CompletedTask;
        return cmd switch
        {
            "files" => TestFilesState(),
            "recentPin" => new JsonObject { ["done"] = Vm.Recent.Pin(request["path"]!.GetValue<string>()) },
            "recentUnpin" => new JsonObject { ["done"] = Vm.Recent.Unpin(request["path"]!.GetValue<string>()) },
            "openRecentAt" => Run(() => StartPage.OpenRecentAt((int)TestHookSettings.ReadLong(request["index"], 0))),
            "openRecentMenuAt" => Run(() => OpenRecent(Vm.Recent.MenuEntries()[(int)TestHookSettings.ReadLong(request["index"], 0)])),
            "saveSession" => Run(() => SaveSession()),

            // スタートページの最近使ったファイルの行のメニュー (Shift+F10 と同じ)。invoke があれば、その AutomationId の項目を押す。
            "startRecentMenu" => TestStartRecentMenu((int)TestHookSettings.ReadLong(request["index"], 0), request["invoke"]?.GetValue<string>()),

            // 設定画面の配色とフォントの区画 (UI-28、UI-29)。設定画面を開いた後に使う。
            "schemeEditor" => TestSchemeEditor(request),

            // ウィンドウがアクティブになったときと同じ確認 (ウィンドウを前面に出さずに再現する。ENG-19 の仕様 1)。
            "activated" => Run(CheckSelectedForExternalChange),

            // 異常を再現する仕組みの「外部変更」(テスト方針 7.2): 別のハンドルで開いて書く (他のアプリと同じ)。
            "externalWrite" => TestExternalWrite(request),
            _ => null,
        };
    }

    /// <summary>「すべて表示…」を選んでから一覧が表示される (Loaded) までの時間 (ミリ秒。テスト用)。</summary>
    private double? _recentAllShownMs;

    private JsonObject TestFilesState()
    {
        double started = TestClock.NowMs;
        var recent = new JsonArray();
        foreach (RecentItem item in Vm.Recent.Items)
        {
            recent.Add(new JsonObject { ["path"] = item.Path, ["name"] = item.DisplayName, ["pinned"] = item.Pinned });
        }

        var menu = new JsonArray();
        foreach (MenuFlyoutItemBase item in RecentMenu.Items)
        {
            if (item is MenuFlyoutItem { Tag: RecentItem } file)
            {
                menu.Add(new JsonObject { ["text"] = file.Text, ["opacity"] = file.Opacity });
            }
        }

        var start = new JsonArray();
        foreach (RecentEntryViewModel entry in StartPage.ViewModel.RecentItems)
        {
            start.Add(new JsonObject { ["name"] = entry.Name, ["missing"] = entry.IsMissing, ["status"] = entry.StatusText });
        }

        var tabs = new JsonArray();
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            tabs.Add(new JsonObject
            {
                ["name"] = doc.DisplayName,
                ["header"] = doc.Header,
                ["path"] = doc.FilePath,
                ["readOnly"] = doc.Editor.ReadOnly,
                ["cursor"] = doc.Editor.Cursor,
                ["missing"] = doc.MissingPath,
                ["missingText"] = doc.MissingText,
                ["externalChange"] = doc.ExternalChange.ToString(),
                ["watched"] = doc.Watch is not null,
            });
        }

        return new JsonObject
        {
            ["recent"] = recent,
            ["recentMenu"] = menu,
            ["closedTabs"] = Vm.ClosedTabs.Count,
            ["startPageVisible"] = StartPage.Visibility == Visibility.Visible,
            ["startRecent"] = start,
            ["welcome"] = StartPage.ViewModel.ShowWelcome,
            ["startShortcuts"] = new JsonArray(StartPage.ShortcutTexts.Open, StartPage.ShortcutTexts.New),
            ["startLanguages"] = new JsonArray([.. StartPage.ViewModel.Languages.Select(l => (JsonNode?)l.Label)]),
            ["canRestoreSession"] = StartPage.ViewModel.CanRestoreSession,
            ["tabs"] = tabs,
            ["windowBounds"] = $"{AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}",
            ["theme"] = App.Settings.GetString(Appearance.ThemeKey, Appearance.ThemeDefault),
            ["language"] = App.Settings.GetString(LanguageKey, "system"),
            ["preset"] = HexEditor.App.Commands.CommandService.Keys.Preset,
            // 状態を集める時間 (最近使ったファイルのサブメニューを含む。アプリの中で測る) と、「すべて表示…」の一覧が表示されるまでの時間。
            ["elapsedMs"] = TestClock.NowMs - started,
            ["recentAllShownMs"] = _recentAllShownMs,
        };
    }

    private JsonObject TestStartRecentMenu(int index, string? invoke)
    {
        _startRecentMenu = null;
        StartPage.RequestRecentMenuAt(index);
        if (_startRecentMenu is not { } menu)
        {
            return new JsonObject { ["open"] = false };
        }

        var items = new JsonArray([.. menu.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(i))]);
        if (invoke is not null)
        {
            MenuFlyoutItem item = menu.Items.OfType<MenuFlyoutItem>().First(i => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(i) == invoke);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(item)).Invoke();
        }

        menu.Hide();

        return new JsonObject { ["open"] = true, ["items"] = items };
    }

    /// <summary>配色の区画: action = state / duplicate {name} / set {element, color?, dark} / save / import {path}。区画の状態を返す。</summary>
    private static JsonObject TestSchemeEditor(JsonObject request)
    {
        Views.ColorSchemeSection section = Views.ColorSchemeSection.Current ?? throw new InvalidOperationException("The settings page is not open.");
        var result = new JsonObject();
        switch (request["action"]?.GetValue<string>() ?? "state")
        {
            case "duplicate":
                result["created"] = section.Duplicate(request["name"]!.GetValue<string>())?.Name;
                break;
            case "set":
                {
                    var element = Enum.Parse<Core.View.SchemeElement>(request["element"]!.GetValue<string>(), ignoreCase: true);
                    Core.View.SchemeColor? color = Core.View.SchemeColor.TryParse(request["color"]?.GetValue<string>(), out Core.View.SchemeColor c) ? c : null;
                    section.SetColorForTest(element, request["dark"]?.GetValue<bool>() ?? false, color);
                    break;
                }

            case "save":
                section.SaveEditing();
                break;
            case "import":
                result["imported"] = section.Import(request["path"]!.GetValue<string>())?.Name;
                break;
        }

        (IReadOnlyList<string> names, string? selected, bool editorVisible) = section.ListState;
        (string? background, string family, double points, double lineHeight) = section.Preview.StateForTest;
        result["schemes"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]);
        result["selected"] = selected;
        result["editing"] = section.Editing?.Name;
        result["editorVisible"] = editorVisible;
        result["contrastOpen"] = section.ContrastState.Open;
        result["contrastMessage"] = section.ContrastState.Message;
        result["loadWarningsOpen"] = section.LoadWarningState.Open;
        result["loadWarnings"] = section.LoadWarningState.Message;
        result["previewBackground"] = background;
        result["previewFont"] = family;
        result["previewPoints"] = points;
        result["previewLineHeight"] = lineHeight;
        return result;
    }

    private static JsonObject TestExternalWrite(JsonObject request)
    {
        string path = request["path"]!.GetValue<string>();
        byte[] data = Convert.FromHexString(request["hex"]!.GetValue<string>());
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Position = TestHookSettings.ReadLong(request["offset"], 0);
            stream.Write(data);
        }

        AppLog.Info($"Test hooks: external write to {Path.GetFileName(path)}");
        return new JsonObject();
    }
}
#endif
