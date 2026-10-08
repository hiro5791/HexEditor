#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令のうち、ウィンドウの枠の機能 (全画面表示 UI-07、ズーム UI-08、パネルの折りたたみ UI-01 など) のもの。
/// 実際のマウスは使わず、ポインタの位置を渡して同じ処理を呼ぶ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ウィンドウの枠の命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleShellTestCommandsAsync(string cmd, JsonObject request)
    {
        await Task.CompletedTask;
        return cmd switch
        {
            "shellState" => ShellState(),
            "moveWindow" => Run(() => AppWindow.MoveAndResize(new RectInt32(
                (int)TestHookSettings.ReadLong(request["x"], 100), (int)TestHookSettings.ReadLong(request["y"], 100),
                (int)TestHookSettings.ReadLong(request["width"], 1200), (int)TestHookSettings.ReadLong(request["height"], 800)))),
            "fullScreenPointer" => Run(() => OnFullScreenPointer(request["y"]?.GetValue<double>() ?? 0)),

            // 分割バーで矢印キーを押す (キーの処理は Splitter.OnKeyDown と同じ Nudge。UI-01 の仕様 5)。
            "splitterKey" => Run(() =>
            {
                var splitter = (Controls.Splitter)(FindElement(request["id"]!.GetValue<string>()) ?? throw new ArgumentException("No splitter."));
                string key = request["key"]?.GetValue<string>() ?? "Left";
                for (long i = 0; i < TestHookSettings.ReadLong(request["count"], 1); i++)
                {
                    splitter.Nudge(key is "Right" or "Down");
                }
            }),
            _ => null,
        };
    }

    private JsonObject ShellState()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        FrameworkElement? strip = TabStrip;
        var result = new JsonObject
        {
            ["fullScreen"] = _fullScreen,
            ["presenter"] = AppWindow.Presenter.Kind.ToString(),
            ["x"] = AppWindow.Position.X,
            ["y"] = AppWindow.Position.Y,
            ["width"] = AppWindow.Size.Width,
            ["height"] = AppWindow.Size.Height,
            ["monitorX"] = area.OuterBounds.X,
            ["monitorY"] = area.OuterBounds.Y,
            ["monitorWidth"] = area.OuterBounds.Width,
            ["monitorHeight"] = area.OuterBounds.Height,
            ["workX"] = area.WorkArea.X,
            ["workY"] = area.WorkArea.Y,
            ["workWidth"] = area.WorkArea.Width,
            ["workHeight"] = area.WorkArea.Height,
            ["titleBarVisible"] = AppTitleBar.Visibility == Visibility.Visible,
            ["statusBarVisible"] = StatusBar.Visibility == Visibility.Visible,
            ["tabsRevealed"] = _tabsRevealed,
            ["tabStripVisible"] = strip is { Visibility: Visibility.Visible },
            ["uiZoom"] = _zoomHost.Zoom,
            ["appliedUiZoom"] = _zoomHost.AppliedZoom,
            ["hexZoom"] = CurrentHexZoom,
        };
        if (strip is { Visibility: Visibility.Visible, ActualHeight: > 0 } && Root.XamlRoot is not null)
        {
            Rect r = strip.TransformToVisual(null).TransformBounds(new Rect(0, 0, strip.ActualWidth, strip.ActualHeight));
            result["tabStripTop"] = r.Y;
            result["tabStripBottom"] = r.Y + r.Height;
        }

        if (SelectedView() is { ActualHeight: > 0 } view)
        {
            Rect r = view.TransformToVisual(null).TransformBounds(new Rect(0, 0, view.ActualWidth, view.ActualHeight));
            result["editorTop"] = r.Y;
        }

        return result;
    }
}
#endif
