#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// フェーズ 2 の表示のテスト用の命令 (テスト方針 7.2): 特定のビュー (分割したペイン・並べて表示) の描画モデル、ミニマップ、レコード表示の設定、
/// テキスト列の見出しのメニュー、色の層を確かめるための強調の追加。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleViewPhase2TestCommandsAsync(string cmd, JsonObject request)
    {
        await Task.CompletedTask;
        return cmd switch
        {
            // 対象のビュー: "active" (既定)、"pane1"・"pane2" (分割したペイン)、"side1"〜"side3" (並べて表示の右のビュー)。
            "renderView" => RenderTarget(request),
            "highlightsView" => TargetView(request).ReadHighlights(),
            "viewKey" => TestViewKey(request),
            "viewState" => ViewStateOf(TargetView(request)),
            "panes" => TestPanes(),
            "refreshMenus" => Run(() => { UpdateViewMenu(); RefreshCommandUi(); }),
            "focusPane" => Run(() => FocusPane((int)(request["pane"]?.GetValue<long>() ?? 0))),
            "minimap" => TestMinimap(request),
            "recordSettings" => TestRecordSettings(request),
            "textColumnMenu" => TestTextColumnMenu(request),
            "addHighlight" => TestAddHighlight(request),
            "sideBySide" => TestSideBySide(request),
            "openPickerPath" => Run(() => Services.TestHooks.Update(s => s with { OpenPicker = request["path"]?.GetValue<string>() is { } p ? [p] : null })),
            _ => null,
        };
    }

    /// <summary>テスト用の命令の対象のビュー。</summary>
    private HexView TargetView(JsonObject request)
    {
        string target = request["view"]?.GetValue<string>() ?? "active";
        HexView? view = target switch
        {
            "pane1" when Vm.Selected is { } d => _views.FirstOrDefault(v => ReferenceEquals(v.Editor, d.PrimaryEditor) && v.IsLoaded),
            "pane2" when Vm.Selected is { SecondaryEditor: { } second } => _views.FirstOrDefault(v => ReferenceEquals(v.Editor, second)),
            _ when target.StartsWith("side", StringComparison.Ordinal) && SideBySideOf(Vm.Selected) is { } group
                && int.TryParse(target[4..], out int n) && n >= 1 && n <= group.Views.Count => group.Views[n - 1],
            _ => CurrentView(),
        };
        return view ?? throw new InvalidOperationException("No hex view: " + target);
    }

    private JsonObject RenderTarget(JsonObject request)
    {
        HexView view = TargetView(request);
        view.RenderNow();
        JsonObject result = view.ReadRendered();
        result["minimap"] = view.ReadMinimap();
        return result;
    }

    private JsonObject TestViewKey(JsonObject request)
    {
        HexView view = TargetView(request);
        var key = Enum.Parse<Windows.System.VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        int count = (int)(request["count"]?.GetValue<long>() ?? 1);
        bool handled = false;
        for (int i = 0; i < count; i++)
        {
            handled |= view.InjectKey(key, request["shift"]?.GetValue<bool>() ?? false, request["ctrl"]?.GetValue<bool>() ?? false, false);
        }

        return new JsonObject { ["handled"] = handled };
    }

    private static JsonObject ViewStateOf(HexView view) => view.Editor is { } e
        ? new JsonObject
        {
            ["cursor"] = e.Cursor,
            ["topRow"] = e.TopRow,
            ["topOffset"] = e.TopOffset,
            ["bytesPerRow"] = e.BytesPerRow,
            ["selectionStart"] = e.SelectionStart,
            ["selectionLength"] = e.SelectionLength,
            ["length"] = e.Document.Length,
        }
        : new JsonObject();

    /// <summary>選択中のタブの Hex ビュー (ペイン) の数と外接矩形 (ウィンドウの座標)。</summary>
    private JsonObject TestPanes()
    {
        var items = new JsonArray();
        if (Vm.Selected is { } doc)
        {
            foreach (EditorState pane in doc.Panes)
            {
                if (_views.FirstOrDefault(v => ReferenceEquals(v.Editor, pane) && v.IsLoaded) is { } view && view.XamlRoot is not null)
                {
                    Windows.Foundation.Rect r = view.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, view.ActualWidth, view.ActualHeight));
                    items.Add(new JsonObject
                    {
                        ["left"] = r.X, ["top"] = r.Y, ["right"] = r.Right, ["bottom"] = r.Bottom,
                        ["cursor"] = pane.Cursor, ["topRow"] = pane.TopRow, ["active"] = ReferenceEquals(pane, doc.Editor),
                    });
                }
            }
        }

        return new JsonObject
        {
            ["views"] = items,
            ["split"] = Vm.Selected?.IsSplit ?? false,
            ["sideBySide"] = Vm.Selected?.SplitSideBySide ?? false,
            ["activePane"] = Vm.Selected?.ActivePane ?? 0,
            ["sync"] = Vm.Selected?.PaneSyncEnabled ?? false,
        };
    }

    /// <summary>
    /// ミニマップ: state (描画モデル) / click (高さに対する割合 fraction の位置) / exact (正確に計算を始める) / resize (境界のドラッグで幅を
    /// width にする) / wheel (ミニマップの上でホイールを delta で count 回)。
    /// </summary>
    private JsonObject TestMinimap(JsonObject request)
    {
        HexView view = TargetView(request);
        switch (request["action"]?.GetValue<string>() ?? "state")
        {
            case "click" when view.Minimap is { } m:
                m.ClickAt(m.ActualHeight * (request["fraction"]?.GetValue<double>() ?? 0));
                break;
            case "tooltip" when view.Minimap is { } m:
                return new JsonObject { ["text"] = m.ToolTipText(m.ActualHeight * (request["fraction"]?.GetValue<double>() ?? 0)) };
            case "exact":
                StartExactMinimap();
                break;
            case "resize" when view.Minimap is { } m:
                // 左の境界のドラッグ (始める・幅を変える・離す) と同じ処理。
                m.BeginResizeForTest();
                m.ResizeTo(request["width"]?.GetValue<double>() ?? 80);
                m.EndResize();
                break;
            case "wheel" when view.Minimap is { } m:
                for (int i = 0; i < (int)(request["count"]?.GetValue<long>() ?? 1); i++)
                {
                    m.Wheel?.Invoke((int)(request["delta"]?.GetValue<long>() ?? -120));
                }

                break;
        }

        return view.ReadMinimap() ?? new JsonObject { ["visible"] = false };
    }

    /// <summary>レコード表示の設定のフライアウト: open / 値の入力 (length・start・perRow・numbers) / commit。状態を返す。</summary>
    private JsonObject TestRecordSettings(JsonObject request)
    {
        if (request["open"]?.GetValue<bool>() == true || _recordFlyout is null)
        {
            ShowRecordSettings();
        }

        if (request["length"]?.GetValue<string>() is { } length)
        {
            _recordLength!.Text = length;
        }

        if (request["start"]?.GetValue<string>() is { } start)
        {
            _recordStart!.Text = start;
        }

        if (request["perRow"]?.GetValue<bool>() is { } perRow)
        {
            _recordPerRow!.IsChecked = perRow;
        }

        if (request["numbers"]?.GetValue<bool>() is { } numbers)
        {
            _recordNumbers!.IsChecked = numbers;
        }

        ValidateRecordSettings();
        if (request["commit"]?.GetValue<bool>() == true)
        {
            CommitRecordSettings();
        }

        return new JsonObject
        {
            ["open"] = _recordFlyout?.IsOpen ?? false,
            ["error"] = _recordError?.Text,
            ["okEnabled"] = _recordOk?.IsEnabled ?? false,
            ["perRowEnabled"] = _recordPerRow?.IsEnabled ?? false,
        };
    }

    private JsonObject TestTextColumnMenu(JsonObject request)
    {
        HexView view = TargetView(request);
        int column = (int)(request["column"]?.GetValue<long>() ?? 0);
        if (request["action"]?.GetValue<string>() is { } action)
        {
            view.ChooseTextColumnMenu(column, action);
        }
        else
        {
            view.ShowTextColumnMenu(column);
        }

        return new JsonObject();
    }

    /// <summary>
    /// 色の層の確認用の強調を加える (提供元 "test"): offset・length・layer (7〜11)・background・border (#AARRGGBB)。clear で消す。
    /// 色付けルール (INSP-33) などの提供元の代わりに、VIEW-17 の重ね方を確かめる。
    /// </summary>
    private JsonObject TestAddHighlight(JsonObject request)
    {
        if (request["clear"]?.GetValue<bool>() == true)
        {
            _testHighlights.Clear();
        }
        else
        {
            static Brush? BrushOf(string? text) => SchemeColor.TryParse(text, out SchemeColor c)
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B))
                : null;
            _testHighlights.Add(new HexHighlight(
                request["offset"]!.GetValue<long>(),
                request["length"]?.GetValue<long>() ?? 1,
                (CellLayer)(int)(request["layer"]?.GetValue<long>() ?? 10),
                BrushOf(request["background"]?.GetValue<string>()),
                BrushOf(request["border"]?.GetValue<string>()),
                null,
                request["tag"]?.GetValue<string>() ?? "test"));
        }

        foreach (HexView view in _views)
        {
            view.SetHighlightSource("test", _testHighlights.Count == 0 ? null
                : (start, end) => _testHighlights.Where(h => h.Offset < end && h.Offset + Math.Max(1, h.Length) > start).ToList());
        }

        return new JsonObject();
    }

    private readonly List<HexHighlight> _testHighlights = [];

    /// <summary>並べて表示の状態: 組の右のドキュメント、同期のモード、違いを強調。</summary>
    private JsonObject TestSideBySide(JsonObject request)
    {
        if (request["add"]?.GetValue<long>() is { } index && Vm.Selected is { } left)
        {
            AddSideBySide(left, Vm.Documents[(int)index]);
        }

        SideBySideGroup? group = SideBySideOf(Vm.Selected);
        var views = new JsonArray();
        foreach (HexView view in group?.Views ?? [])
        {
            JsonObject state = ViewStateOf(view);
            if (view.XamlRoot is not null)
            {
                Windows.Foundation.Rect r = view.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, view.ActualWidth, view.ActualHeight));
                state["left"] = r.X;
                state["right"] = r.Right;
            }

            views.Add(state);
        }

        return new JsonObject
        {
            ["active"] = group is not null,
            ["mode"] = group?.Mode.ToString(),
            ["differences"] = group?.HighlightDifferences ?? false,
            ["partners"] = new JsonArray([.. (group?.Partners ?? []).Select(p => (JsonNode?)p.DisplayName)]),
            ["views"] = views,
            ["status"] = SyncStatusText(),
        };
    }
}
#endif
