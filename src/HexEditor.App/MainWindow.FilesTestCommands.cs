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

            // ウィンドウがアクティブになったときと同じ確認 (ウィンドウを前面に出さずに再現する。ENG-19 の仕様 1)。
            "activated" => Run(CheckSelectedForExternalChange),

            // 異常を再現する仕組みの「外部変更」(テスト方針 7.2): 別のハンドルで開いて書く (他のアプリと同じ)。
            "externalWrite" => TestExternalWrite(request),
            _ => null,
        };
    }

    private JsonObject TestFilesState()
    {
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
            ["canRestoreSession"] = StartPage.ViewModel.CanRestoreSession,
            ["tabs"] = tabs,
            ["windowBounds"] = $"{AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}",
            ["theme"] = App.Settings.GetString(Appearance.ThemeKey, Appearance.ThemeDefault),
            ["language"] = App.Settings.GetString(LanguageKey, "system"),
            ["preset"] = HexEditor.App.Commands.CommandService.Keys.Preset,
        };
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
