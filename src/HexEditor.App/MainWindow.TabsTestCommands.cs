#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// タブとウィンドウ (UI-09〜UI-11、UI-14) のテスト用の命令。マウスを使わずに、ドラッグ・右クリックメニュー・中ボタンのクリックと同じ処理を呼ぶ。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleTabsTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "tabs":
                return TestTabs();
            case "tabMenuItems":
                return new JsonObject { ["items"] = MenuEntriesJson(TabMenuEntries(TestTab(request))) };
            case "tabMenu":
                return TestTabMenu(request);
            case "tabCloseRequest":
                // 中ボタンのクリック・閉じるボタン (TabView の TabCloseRequested) と同じ処理 (UI-09 の仕様 8)。
                await CloseAsync([TestTab(request)]);
                return new JsonObject();
            case "dropTab":
            {
                // タブ列へのドロップ (UI-10 の仕様 1、UI-11 の仕様 2)。toWindow を省くと同じウィンドウの中の並べ替え。
                DocumentViewModel doc = TestTab(request);
                MainWindow target = request["toWindow"] is { } w ? WindowManager.Windows[(int)TestHookSettings.ReadLong(w, 0)] : this;
                target.DropTab(doc, this, (int)TestHookSettings.ReadLong(request["toIndex"], 0));
                return new JsonObject();
            }

            case "dragTabOutside":
            {
                // タブ列の外へのドロップ (UI-11 の仕様 1・4)。タブの中央から (dx, dy) 物理ピクセル動かした位置に落とす。
                DocumentViewModel doc = TestTab(request);
                PointInt32 start = TabCenterOnScreen(doc);
                var end = new PointInt32(start.X + (int)TestHookSettings.ReadLong(request["dx"], 0), start.Y + (int)TestHookSettings.ReadLong(request["dy"], 0));
                OnTabDroppedOutside(doc, start, end);
                return new JsonObject();
            }

            case "moveWindow":
                AppWindow.Move(new PointInt32((int)TestHookSettings.ReadLong(request["x"], 0), (int)TestHookSettings.ReadLong(request["y"], 0)));
                return new JsonObject();
            case "windowSummary":
                return TestWindowSummary();
            case "closeWindow":
                // タイトルバーの閉じるボタンと同じ (Closed の処理で確かめてから閉じる)。
                Close();
                return new JsonObject();
            case "switchTabMeasured":
                return await TestSwitchTabMeasuredAsync(request["forward"]?.GetValue<bool>() ?? true);
            default:
                return null;
        }
    }

    /// <summary>命令の index のタブ (省くと選択中のタブ)。</summary>
    private DocumentViewModel TestTab(JsonObject request) => request["index"] is { } index
        ? Vm.Documents[(int)TestHookSettings.ReadLong(index, 0)]
        : Vm.Selected ?? throw new InvalidOperationException("No document.");

    private JsonObject TestTabs()
    {
        var tabs = new JsonArray();
        foreach (DocumentViewModel d in Vm.Documents)
        {
            tabs.Add(new JsonObject
            {
                ["name"] = d.DisplayName,
                ["title"] = d.TabTitle,
                ["header"] = d.Header,
                ["toolTip"] = d.ToolTip,
                ["icon"] = d.IconGlyph,
                ["pinned"] = d.IsPinned,
                ["pending"] = d.IsPending,
                ["missing"] = d.IsMissing,
                ["modified"] = d.Document.IsModified,
                ["path"] = d.FilePath ?? d.PendingRecord?.Path ?? d.MissingPath,
            });
        }

        return new JsonObject
        {
            ["tabs"] = tabs,
            ["selectedIndex"] = Vm.Selected is { } s ? Vm.Documents.IndexOf(s) : -1,
            ["switcherOpen"] = _switcher?.IsOpen == true,
            ["mru"] = new JsonArray([.. _mru.Order.Select(d => (JsonNode?)d.DisplayName)]),
        };
    }

    private static JsonArray MenuEntriesJson(IEnumerable<TabMenuEntry?> entries) =>
        new([.. entries.Where(e => e is not null).Select(e => (JsonNode?)new JsonObject
        {
            ["id"] = e!.Id,
            ["text"] = e.Text,
            ["enabled"] = e.Enabled,
            ["checked"] = e.Checked,
            ["children"] = e.Children is { } c ? MenuEntriesJson(c) : null,
        })]);

    /// <summary>右クリックメニューの項目を押す (メニューを開かずに、同じ処理を呼ぶ)。</summary>
    private JsonObject TestTabMenu(JsonObject request)
    {
        string id = request["item"]!.GetValue<string>();
        IEnumerable<TabMenuEntry?> entries = TabMenuEntries(TestTab(request));
        TabMenuEntry? Find(IEnumerable<TabMenuEntry?> list) =>
            list.FirstOrDefault(e => e?.Id == id) ?? list.Where(e => e?.Children is not null).Select(e => Find(e!.Children!)).FirstOrDefault(e => e is not null);
        TabMenuEntry entry = Find(entries) ?? throw new ArgumentException($"Tab menu item not found: {id}");
        if (!entry.Enabled || entry.Execute is null)
        {
            throw new InvalidOperationException($"Tab menu item {id} is disabled.");
        }

        entry.Execute();
        return new JsonObject();
    }

    /// <summary>
    /// Ctrl+PageDown / Ctrl+PageUp のタブの切り替えの時間 (UI-09 の「巨大ファイル・長時間処理」): 切り替えてから、切り替え先の Hex ビューが
    /// 読み込まれて描画されるまで。
    /// </summary>
    private async Task<JsonObject> TestSwitchTabMeasuredAsync(bool forward)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Commands.ExecuteAsync(forward ? "tab.nextInOrder" : "tab.previousInOrder");
        while (watch.ElapsedMilliseconds < 5000 && (CurrentView() is not { IsLoaded: true } || CurrentView()?.Editor != Editor))
        {
            var next = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => next.SetResult());
            await next.Task;
        }

        // 次のフレームの描画まで待つ。
        var rendered = new TaskCompletionSource();
        void OnRendering(object? sender, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            rendered.TrySetResult();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        CurrentView()?.RenderNow();
        await Task.WhenAny(rendered.Task, Task.Delay(1000));
        return new JsonObject { ["ms"] = watch.Elapsed.TotalMilliseconds, ["name"] = Vm.Selected?.DisplayName };
    }

    /// <summary>タブの見出しの中央 (画面の物理ピクセル)。</summary>
    private PointInt32 TabCenterOnScreen(DocumentViewModel doc)
    {
        Tabs.UpdateLayout();
        if (Tabs.ContainerFromItem(doc) is not TabViewItem item)
        {
            throw new InvalidOperationException("The tab is not shown.");
        }

        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        Windows.Foundation.Point origin = item.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(item.ActualWidth / 2, item.ActualHeight / 2));
        var client = new NativePoint();
        ClientToScreen(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), ref client);
        return new PointInt32(client.X + (int)(origin.X * scale), client.Y + (int)(origin.Y * scale));
    }

    /// <summary>ウィンドウの要約 (windows の命令): 位置と大きさ (物理ピクセル)、タブ、アクティブなタブ、ウィンドウハンドル。</summary>
    internal JsonObject TestWindowSummary()
    {
        PointInt32 p = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return new JsonObject
        {
            ["number"] = WindowManager.NumberOf(this),
            ["hwnd"] = (long)Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id),
            ["x"] = p.X,
            ["y"] = p.Y,
            ["width"] = size.Width,
            ["height"] = size.Height,
            ["title"] = Title,
            ["tabs"] = new JsonArray([.. Vm.Documents.Select(d => (JsonNode?)d.DisplayName)]),
            ["selectedIndex"] = Vm.Selected is { } s ? Vm.Documents.IndexOf(s) : -1,
            ["startPageVisible"] = StartPage.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
            ["theme"] = Root.ActualTheme.ToString(),
        };
    }
}
#endif
