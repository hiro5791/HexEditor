#if HEX_TEST_HOOKS
using System.Reflection;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令のうち、ウィンドウ・タブ・パネル・ポップアップのズーム (UI-05、UI-08、UI-09、UI-13、UI-14) のもの。
/// 実際のマウスは使わず、ドラッグの始まりと終わりなどと同じ処理を呼ぶ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ウィンドウ・タブ・パネルの命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleWindowsTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "subscribers":
                return TestSubscribers();
            case "tabCloseMark":
                return new JsonObject { ["glyph"] = TabCloseMark(TestTab(request)), ["unsaved"] = TabCloseMark(TestTab(request)) == UnsavedGlyph };
            case "tabHover":
                SimulateTabHover(TestTab(request), request["over"]?.GetValue<bool>() ?? true);
                return new JsonObject();
            case "popupZoom":
                return await TestPopupZoomAsync(request["kind"]?.GetValue<string>() ?? "menu");
            case "panelDragOutside":
            {
                // パネルの見出しのドラッグを、画面の (x, y) (物理ピクセル) でどこにも落とさずに終える (UI-05 の仕様 3)。
                string id = request["id"]!.GetValue<string>();
                PanelDockArea area = AreaOf(id);
                area.BeginDrag(id);
                area.EndDrag(id, DataPackageOperation.None, new PointInt32((int)TestHookSettings.ReadLong(request["x"], 0), (int)TestHookSettings.ReadLong(request["y"], 0)));
                return PanelState();
            }

            case "panelDragTo":
            {
                // このウィンドウのパネルの見出しを、toWindow のウィンドウの dock の場所に落とす (別のウィンドウには落とせない)。
                string id = request["id"]!.GetValue<string>();
                PanelDockArea from = AreaOf(id);
                MainWindow target = WindowManager.Windows[(int)TestHookSettings.ReadLong(request["toWindow"], 0)];
                PanelDockArea to = target.DockAreaOf(Enum.Parse<PanelDock>(request["dock"]!.GetValue<string>(), ignoreCase: true));
                from.BeginDrag(id);
                bool accepted = to.AcceptsDraggedPanel;
                if (accepted)
                {
                    to.RequestMove(id, to.Dock);
                }

                from.EndDrag(id, accepted ? DataPackageOperation.Move : DataPackageOperation.None, new PointInt32(AppWindow.Position.X + 10, AppWindow.Position.Y + 10));
                JsonObject state = PanelState();
                state["accepted"] = accepted;
                return state;
            }

            case "floatingPanels":
                return new JsonObject
                {
                    ["panels"] = new JsonArray([.. _floatingPanels.Values.Select(w => (JsonNode?)new JsonObject
                    {
                        ["id"] = w.PanelId,
                        ["x"] = w.ScreenBounds.X,
                        ["y"] = w.ScreenBounds.Y,
                        ["appliedZoom"] = w.AppliedZoom,
                    })]),
                };
            case "closeWait":
                return new JsonObject
                {
                    ["open"] = _operationWaitDialog is not null,
                    ["text"] = (_operationWaitDialog?.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
                };
            default:
                return null;
        }
    }

    /// <summary>パネルの見出しのある領域 (浮動パネルならその中の領域)。</summary>
    private PanelDockArea AreaOf(string id) =>
        _floatingPanels.GetValueOrDefault(id)?.Area
        ?? DockAreas.Select(d => d.Area).FirstOrDefault(a => a.Tabs.Any(t => t.Id == id))
        ?? throw new InvalidOperationException($"No header for panel {id}.");

    private PanelDockArea DockAreaOf(PanelDock dock) => DockAreas.First(d => d.Dock == dock).Area;

    /// <summary>
    /// アプリ全体のイベントの購読者の数 (閉じたウィンドウが購読を残していないかの確認。UI-14)。イベントの裏の欄を読む。
    /// </summary>
    private JsonObject TestSubscribers()
    {
        static int Count(object? target, Type type, string name)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            return field?.GetValue(field.IsStatic ? null : target) is Delegate d ? d.GetInvocationList().Length : 0;
        }

        return new JsonObject
        {
            ["settings"] = Count(App.Settings, App.Settings.GetType(), "Changed"),
            ["bindings"] = Count(null, typeof(CommandService), "BindingsChanged"),
            ["catalog"] = Count(CommandService.Catalog, CommandService.Catalog.GetType(), "Changed"),
            ["panelRegistry"] = Count(null, typeof(PanelRegistry), "Registered"),
            ["panelDragging"] = Count(null, typeof(PanelDockArea), "DraggingChanged"),
            ["screenZoom"] = Count(null, typeof(ScreenZoom), "Changed"),
            ["operations"] = Count(Vm.Operations, Vm.Operations.GetType(), "Changed"),
            ["recent"] = Count(Vm.Recent, Vm.Recent.GetType(), "Changed"),
            ["floatingPanels"] = _floatingPanels.Count,
            ["operationsTimer"] = _operationsTimer?.IsRunning ?? false,
        };
    }

    /// <summary>
    /// メニュー (ステータスバーの右クリックメニュー) かフライアウト (通知の履歴) を開き、画面全体のズームが効いているかを読んで閉じる
    /// (UI-08 の仕様 3)。メニューは項目の文字の大きさ、フライアウトは中身の拡大の倍率。
    /// </summary>
    private async Task<JsonObject> TestPopupZoomAsync(string kind)
    {
        FlyoutBase flyout = kind == "menu" ? StatusItemsMenu : NotificationHistoryFlyout;
        var opened = new TaskCompletionSource();
        void OnOpened(object? sender, object e) => opened.TrySetResult();
        flyout.Opened += OnOpened;
        try
        {
            flyout.ShowAt(StatusBar);
            await Task.WhenAny(opened.Task, Task.Delay(5000));
        }
        finally
        {
            flyout.Opened -= OnOpened;
        }

        // 配置が終わるまで待つ。
        for (int i = 0; i < 3; i++)
        {
            var next = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => next.SetResult());
            await next.Task;
        }

        var result = new JsonObject { ["opened"] = flyout.IsOpen, ["appliedZoom"] = _zoomHost.AppliedZoom };
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
        {
            switch (popup.Child)
            {
                case MenuFlyoutPresenter menu:
                    result["menuFontSizes"] = new JsonArray([.. menu.Items.OfType<Control>().Where(c => c is not MenuFlyoutSeparator).Select(c => (JsonNode?)c.FontSize)]);
                    result["menuHeight"] = menu.ActualHeight;
                    break;
                case FlyoutPresenter presenter:
                    PopupZoomPanel? panel = VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as PopupZoomPanel : null;
                    result["flyoutZoom"] = panel?.AppliedZoom;
                    result["flyoutWidth"] = presenter.ActualWidth;
                    result["flyoutContentWidth"] = (presenter.Content as FrameworkElement)?.ActualWidth;
                    break;
            }
        }

        flyout.Hide();
        return result;
    }
}
#endif
