#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、データインスペクタとブックマークの命令 (テスト方針 7.2)。
/// <list type="bullet">
/// <item><c>inspector</c>: パネルの状態 (表示・起点・エンディアンの表示・格納される値・行の一覧)。</item>
/// <item><c>panelKey</c>: フォーカスのあるパネルの要素にキーを渡す (パネルにフォーカスがなければ <c>key</c> と同じ)。</item>
/// <item><c>inspectorSelect</c> / <c>inspectorHover</c>: 行を選ぶ (フォーカスも移す) / マウスを重ねたのと同じ。</item>
/// <item><c>highlights</c>: Hex ビューに描いた強調と目印 (層 3・層 7)。</item>
/// <item><c>bookmarks</c>: ブックマークと一覧の行。<c>bookmarksAdd</c>: まとめて付ける (性能のテスト)。</item>
/// <item><c>bookmarkEdit</c> / <c>bookmarkEditor</c> / <c>bookmarkLink</c>: 編集のフライアウトを開く・状態を読む・リンクを押す。</item>
/// <item><c>bookmarksSelect</c>: 一覧の行を選ぶ。<c>setHighContrast</c>: ハイコントラストの判定の差し替え。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private JsonObject? HandleInspectorTestCommand(string cmd, JsonObject request) => cmd switch
    {
        "inspector" => TestInspectorState(),
        "panelKey" => TestPanelKey(request),
        "inspectorSelect" => TestInspectorSelect(request),
        "inspectorHover" => TestInspectorHover(request),
        "highlights" => (CurrentView() ?? throw new InvalidOperationException("No hex view.")).ReadHighlights(),
        "bookmarks" => TestBookmarks(),
        "bookmarksAdd" => TestBookmarksAdd(request),
        "bookmarkEdit" => TestBookmarkEdit(request),
        "bookmarkEditor" => TestBookmarkEditor(request),
        "bookmarkLink" => new JsonObject { ["clicked"] = _bookmarkEditor?.ClickLink(request["text"]!.GetValue<string>()) ?? false },
        "bookmarksSelect" => TestBookmarksSelect(request),
        "setHighContrast" => TestSetHighContrast(request),
        _ => null,
    };

    private JsonObject TestInspectorState()
    {
        var items = new JsonArray();
        foreach (InspectorItemViewModel item in _inspectorVm.Items)
        {
            items.Add(new JsonObject
            {
                ["id"] = item.AutomationId,
                ["kind"] = item.Kind.ToString(),
                ["type"] = item.TypeId,
                ["name"] = item.Name,
                ["value"] = item.Value,
                ["toolTip"] = item.ToolTip,
                ["status"] = item.Status.ToString(),
                ["automationName"] = _inspector.Vm.Items.Contains(item) && TestContainerName(item) is { } n ? n : item.AutomationName,
                ["editing"] = item.IsEditing,
                ["editText"] = item.EditText,
                ["preview"] = item.Preview,
                ["error"] = item.Error,
                ["collapsed"] = item.IsCollapsed,
                ["expanded"] = item.IsExpanded,
                ["bits"] = item.IsBinary ? string.Concat(item.Bits.Select(b => b.Text)) : null,
            });
        }

        object? focused = Root.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) : null;
        return new JsonObject
        {
            ["visible"] = InspectorVisible,
            ["origin"] = _inspectorVm.OriginText,
            ["endian"] = _inspectorVm.EndianText,
            ["stored"] = _inspectorVm.StoredText,
            ["selected"] = _inspectorVm.Selected?.AutomationId,
            ["hasFocus"] = _inspector.ContainsFocus(),
            ["focusedBit"] = focused is Microsoft.UI.Xaml.Controls.Button { Tag: InspectorBitViewModel bit } ? bit.Index : null,
            ["rowSettingsOpen"] = _inspector.RowSettingsOpen,
            ["items"] = items,
        };
    }

    /// <summary>一覧の項目の UI オートメーションの名前 (コンテナに付けたもの)。表示されていなければ null。</summary>
    private string? TestContainerName(InspectorItemViewModel item)
    {
        var list = (Microsoft.UI.Xaml.Controls.ListView?)FindElement("Inspector_List");
        return list?.ContainerFromItem(item) is FrameworkElement container ? AutomationProperties.GetName(container) : null;
    }

    private JsonObject TestPanelKey(JsonObject request)
    {
        var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        bool shift = request["shift"]?.GetValue<bool>() ?? false;
        bool ctrl = request["ctrl"]?.GetValue<bool>() ?? false;
        string handledBy = "none";
        if (key == VirtualKey.F6 && (_inspector.ContainsFocus() || _bookmarkList.ContainsFocus()))
        {
            // F6 / Shift+F6 はグローバル (ウィンドウのキーボードアクセラレータと同じ処理)。
            MoveToRegion(!shift);
            handledBy = "region";
        }
        else if (_inspector.ContainsFocus())
        {
            handledBy = _inspector.InjectKey(key, shift) ? "inspector" : "none";
        }
        else if (_bookmarkList.ContainsFocus())
        {
            handledBy = _bookmarkList.HandleKey(key, ctrl) ? "bookmarks" : "none";
            if (handledBy == "none" && key == VirtualKey.Tab)
            {
                FocusManager.TryMoveFocus(shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = Root.XamlRoot.Content });
                handledBy = "focus";
            }
        }
        else
        {
            return TestKey(request);
        }

        CurrentView()?.RenderNow();
        object? focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        return new JsonObject
        {
            ["handledBy"] = handledBy,
            ["focused"] = focused is FrameworkElement fe ? $"{fe.GetType().Name}:{AutomationProperties.GetAutomationId(fe)}" : focused?.GetType().Name,
        };
    }

    private InspectorItemViewModel FindInspectorItem(JsonObject request)
    {
        string id = request["id"]!.GetValue<string>();
        return _inspectorVm.Items.FirstOrDefault(i => i.AutomationId == id || i.TypeId == id && i.Kind == InspectorItemKind.Row)
            ?? throw new ArgumentException($"Inspector item not found: {id}");
    }

    private JsonObject TestInspectorSelect(JsonObject request)
    {
        InspectorItemViewModel item = FindInspectorItem(request);
        _inspectorVm.Selected = item;
        var list = (Microsoft.UI.Xaml.Controls.ListView)FindElement("Inspector_List")!;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        (list.ContainerFromItem(item) as Microsoft.UI.Xaml.Controls.Control)?.Focus(FocusState.Keyboard);
        CurrentView()?.RenderNow();
        return new JsonObject();
    }

    private JsonObject TestInspectorHover(JsonObject request)
    {
        _inspectorVm.Hovered = request["id"] is null ? null : FindInspectorItem(request);
        CurrentView()?.RenderNow();
        return new JsonObject();
    }

    private JsonObject TestBookmarks()
    {
        var all = new JsonArray();
        if (CurrentAnnotations() is { } a)
        {
            foreach (Bookmark b in a.Bookmarks.All)
            {
                all.Add(BookmarkJson(b));
            }
        }

        var rows = new JsonArray();
        foreach (Bookmark b in _bookmarksVm.Rows.Bookmarks)
        {
            rows.Add(b.Name);
        }

        var list = (Microsoft.UI.Xaml.Controls.ListView?)FindElement("Bookmarks_List");
        return new JsonObject
        {
            ["visible"] = BookmarksVisible,
            ["count"] = all.Count,
            ["bookmarks"] = all,
            ["rows"] = rows,
            ["selected"] = new JsonArray([.. _bookmarkList.SelectedBookmarks.Select(b => (JsonNode?)b.Name)]),
            ["hasFocus"] = _bookmarkList.ContainsFocus(),
            ["firstRowName"] = list?.ContainerFromIndex(0) is FrameworkElement first ? AutomationProperties.GetName(first) : null,
        };
    }

    private static JsonObject BookmarkJson(Bookmark b) => new()
    {
        ["name"] = b.Name,
        ["start"] = b.Start,
        ["length"] = b.Length,
        ["number"] = b.Number,
        ["color"] = b.Color.ToString(),
        ["comment"] = b.Comment,
        ["rangeDeleted"] = b.RangeDeleted,
    };

    /// <summary>{count, step, length, name}: 開始 k × step、長さ length、名前 name{k} のブックマークをまとめて付ける。</summary>
    private JsonObject TestBookmarksAdd(JsonObject request)
    {
        DocumentAnnotations a = CurrentAnnotations() ?? throw new InvalidOperationException("No document.");
        long count = TestHookSettings.ReadLong(request["count"], 1);
        long step = TestHookSettings.ReadLong(request["step"], 1024);
        long length = TestHookSettings.ReadLong(request["length"], 16);
        string prefix = request["name"]?.GetValue<string>() ?? "bm";
        for (long k = 0; k < count; k++)
        {
            Bookmark b = a.Bookmarks.Add(k * step, length, prefix + k);
            if (request["color"] is null)
            {
                a.Bookmarks.SetColor(b, BookmarkColor.Palette((int)(k % 8) + 1));
            }
        }

        return new JsonObject { ["count"] = a.Bookmarks.Count };
    }

    private Bookmark FindBookmark(JsonObject request)
    {
        DocumentAnnotations a = CurrentAnnotations() ?? throw new InvalidOperationException("No document.");
        if (request["name"]?.GetValue<string>() is { } name)
        {
            return a.Bookmarks.All.FirstOrDefault(b => b.Name == name) ?? throw new ArgumentException($"Bookmark not found: {name}");
        }

        long start = TestHookSettings.ReadLong(request["start"], 0);
        return a.Bookmarks.StartingAt(start) ?? throw new ArgumentException($"No bookmark at {start}");
    }

    private JsonObject TestBookmarkEdit(JsonObject request)
    {
        Bookmark b = FindBookmark(request);
        EditBookmark(b, (FrameworkElement?)CurrentView() ?? Root, request["rename"]?.GetValue<bool>() ?? false);
        return new JsonObject();
    }

    /// <summary>編集のフライアウトの状態。<c>preview</c> を指定するとプレビューに切り替える。</summary>
    private JsonObject TestBookmarkEditor(JsonObject request)
    {
        if (_bookmarkEditor is null)
        {
            return new JsonObject { ["open"] = false };
        }

        if (request["preview"]?.GetValue<bool>() is bool preview)
        {
            _bookmarkEditor.ShowPreview(preview);
        }

        var runs = new JsonArray();
        foreach ((string text, bool bold, bool link) in _bookmarkEditor.PreviewRuns())
        {
            runs.Add(new JsonObject { ["text"] = text, ["bold"] = bold, ["link"] = link });
        }

        return new JsonObject
        {
            ["open"] = _bookmarkFlyout?.IsOpen ?? false,
            ["bookmark"] = _bookmarkEditor.Bookmark is { } b ? BookmarkJson(b) : null,
            ["runs"] = runs,
        };
    }

    private JsonObject TestBookmarksSelect(JsonObject request)
    {
        DocumentAnnotations a = CurrentAnnotations() ?? throw new InvalidOperationException("No document.");
        var names = request["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        _bookmarkList.SelectBookmarks(a.Bookmarks.All.Where(b => names.Contains(b.Name)));
        if (request["focus"]?.GetValue<bool>() ?? true)
        {
            var list = (Microsoft.UI.Xaml.Controls.ListView)FindElement("Bookmarks_List")!;
            list.UpdateLayout();
            int index = list.SelectedIndex;
            (list.ContainerFromIndex(Math.Max(0, index)) as Microsoft.UI.Xaml.Controls.Control)?.Focus(FocusState.Keyboard);
        }

        return new JsonObject();
    }

    private JsonObject TestSetHighContrast(JsonObject request)
    {
        HexView.TestHighContrast = request["value"]?.GetValue<bool>();
        foreach (HexView view in _views)
        {
            view.RefreshHighlights();
            view.RenderNow();
        }

        return new JsonObject();
    }
}
#endif
