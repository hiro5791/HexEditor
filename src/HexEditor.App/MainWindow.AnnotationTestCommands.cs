#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;
using HexEditor.Core.View;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、注釈 (INSP-27〜INSP-34) の命令。
/// <list type="bullet">
/// <item><c>bmTree</c> / <c>bmGroup</c>: 一覧のツリーとグループの操作。<c>bmImport</c> / <c>bmExport</c>: インポート・エクスポート。</item>
/// <item><c>multiSelection</c>: マルチ選択の範囲を設定・読む。<c>annotationsAdd</c>: 出どころ「スクリプト」などの注釈を付ける。</item>
/// <item><c>positionManager</c>: 位置マネージャの状態と操作。<c>coloring</c>: 色付けルールの操作。<c>legend</c>: 凡例。</item>
/// <item><c>annotationColumn</c>: 注釈の列の描画内容。<c>descriptionFlyout</c>: 「カーソル位置の説明を表示」のフライアウト。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleAnnotationTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        "bmTree" => TestBookmarkTree(),
        "bmGroup" => TestBookmarkGroup(request),
        "bmImport" => await TestBookmarkImportAsync(request),
        "bmExport" => await TestBookmarkExportAsync(request),
        "multiSelection" => TestMultiSelectionCommand(request),
        "annotationsAdd" => TestAnnotationsAdd(request),
        "positionManager" => await TestPositionManagerAsync(request),
        "coloring" => TestColoring(request),
        "legend" => await TestLegendAsync(request),
        "annotationColumn" => (CurrentView() ?? throw new InvalidOperationException("No hex view.")).ReadAnnotationColumn(),
        "descriptionFlyout" => TestDescriptionFlyout(request),
        _ => null,
    };

    private static string Opt(JsonObject request, string key) => request[key]?.GetValue<string>() ?? string.Empty;

    // ---- ブックマークのグループ (INSP-27) ----

    private JsonObject TestBookmarkTree()
    {
        var entries = new JsonArray();
        foreach (object entry in _bookmarksVm.Rows.Entries)
        {
            switch (entry)
            {
                case BookmarkGroupEntry g:
                    entries.Add(new JsonObject
                    {
                        ["kind"] = "group", ["path"] = g.Group.Path, ["depth"] = g.Group.Depth, ["count"] = g.Count, ["visible"] = g.Group.Visible,
                        ["color"] = g.Group.Color?.ToString(),
                    });
                    break;
                case Bookmark b:
                    BookmarkRowViewModel row = _bookmarksVm.Rows.Row(b);
                    entries.Add(new JsonObject
                    {
                        ["kind"] = "bookmark", ["name"] = b.Name, ["start"] = b.Start, ["group"] = b.Group, ["dimmed"] = row.Dimmed, ["indent"] = row.Indent.Left,
                        ["color"] = CurrentAnnotations()?.Bookmarks.EffectiveColor(b).ToString(),
                    });
                    break;
            }
        }

        return new JsonObject { ["entries"] = entries };
    }

    /// <summary>グループの操作: create (parent, name)、set (start, group)、color (path, index。0 は色なし)、bookmarkColor (start, index)、select (path)、key (key)、delete (path, contents)。</summary>
    private JsonObject TestBookmarkGroup(JsonObject request)
    {
        BookmarkCollection bookmarks = CurrentAnnotations()?.Bookmarks ?? throw new InvalidOperationException("No document.");
        string action = Opt(request, "action");
        string path = Opt(request, "path");
        switch (action)
        {
            case "create":
                return new JsonObject { ["path"] = BookmarkListView?.CreateGroup(request["parent"]?.GetValue<string>(), false, Opt(request, "name"))?.Path };
            case "set":
                bookmarks.SetGroup(bookmarks.StartingAt(TestHookSettings.ReadLong(request["start"], 0)) ?? throw new InvalidOperationException("No bookmark."),
                    request["group"]?.GetValue<string>());
                break;
            case "color":
                int index = request["index"]?.GetValue<int>() ?? 0;
                bookmarks.SetGroupColor(bookmarks.FindGroup(path)!, index == 0 ? null : BookmarkColor.Palette(index));
                break;
            case "bookmarkColor":
                bookmarks.SetColor(bookmarks.StartingAt(TestHookSettings.ReadLong(request["start"], 0))!, BookmarkColor.Palette(request["index"]!.GetValue<int>()));
                break;
            case "select":
                _bookmarksVm.Rebuild();
                return new JsonObject { ["selected"] = BookmarkListView?.SelectGroup(path) ?? false };
            case "key":
                return new JsonObject { ["handled"] = BookmarkListView?.HandleKey(Enum.Parse<VirtualKey>(Opt(request, "key"), ignoreCase: true)) ?? false };
            case "delete":
                BookmarkListPanel list = BookmarkListView ?? throw new InvalidOperationException("No list.");
                _bookmarksVm.Rebuild();
                list.SelectGroup(path);
                list.DeleteGroup(list.SelectedGroup ?? throw new InvalidOperationException("No group."), request["contents"]?.GetValue<bool>() ?? false);
                break;
            case "showDelete":
                BookmarkListPanel l2 = BookmarkListView ?? throw new InvalidOperationException("No list.");
                _bookmarksVm.Rebuild();
                l2.SelectGroup(path);
                l2.ShowDeleteGroup(l2.SelectedGroup ?? throw new InvalidOperationException("No group."));
                break;
            case "toSelection":
                IReadOnlyList<Bookmark> selected = BookmarkListView?.SelectedBookmarks ?? [];
                BookmarksToSelection(selected);
                break;
        }

        return new JsonObject { ["ok"] = true };
    }

    // ---- インポート / エクスポート (INSP-30) ----

    private async Task<JsonObject> TestBookmarkImportAsync(JsonObject request)
    {
        if (!TryParseShift(Opt(request, "shift"), out long shift))
        {
            return new JsonObject { ["error"] = "shift" };
        }

        await ImportBookmarksNowAsync(Opt(request, "path"), Opt(request, "mode") == "replace" ? BookmarkImportMode.Replace : BookmarkImportMode.Append, shift);
        return new JsonObject { ["report"] = LastImportReport, ["error"] = LastImportError };
    }

    private async Task<JsonObject> TestBookmarkExportAsync(JsonObject request)
    {
        BookmarkCollection bookmarks = CurrentAnnotations()?.Bookmarks ?? throw new InvalidOperationException("No document.");
        BookmarkGroup? group = request["group"] is { } g ? bookmarks.FindGroup(g.GetValue<string>()) : null;
        await ExportBookmarksAsync(BookmarkListView?.SelectedBookmarks ?? [], group, Opt(request, "path"));
        return new JsonObject { ["ok"] = true };
    }

    // ---- マルチ選択 (INSP-28) ----

    private JsonObject TestMultiSelectionCommand(JsonObject request)
    {
        EditorState editor = Editor ?? throw new InvalidOperationException("No document.");
        if (request["ranges"] is JsonArray ranges)
        {
            MultiSelectionBridge.Select(editor, [.. ranges.Select(r => new SelectedRange(r![0]!.GetValue<long>(), r[1]!.GetValue<long>()))]);
        }

        return new JsonObject
        {
            ["ranges"] = new JsonArray([.. MultiSelectionBridge.RangesOf(editor).Select(r => (JsonNode?)new JsonArray(r.Start, r.Length))]),
        };
    }

    // ---- 注釈 (INSP-32) ----

    /// <summary>出どころの注釈を付ける (スクリプト・YARA の代わり): {id, origin, name, items: [{start, length, label, description}]}。空の items は出どころを外す。</summary>
    private JsonObject TestAnnotationsAdd(JsonObject request)
    {
        DocumentAnnotations a = CurrentAnnotations() ?? throw new InvalidOperationException("No document.");
        string id = Opt(request, "id");
        var origin = Enum.Parse<AnnotationOrigin>(Opt(request, "origin"), ignoreCase: true);
        JsonArray items = request["items"]?.AsArray() ?? [];
        if (items.Count == 0)
        {
            a.Layer.Unregister(id);
        }
        else
        {
            var set = new AnnotationSet(id, origin, Opt(request, "name"));
            set.AddRange(items.Select(i => new Annotation(i!["start"]!.GetValue<long>(), i["length"]!.GetValue<long>(), i["label"]!.GetValue<string>(), null,
                i["description"]?.GetValue<string>() ?? string.Empty)));
            a.Layer.Register(set);
        }

        return new JsonObject { ["sources"] = new JsonArray([.. a.Layer.Sources.Select(s => (JsonNode?)s.Id)]) };
    }

    private JsonObject TestDescriptionFlyout(JsonObject request)
    {
        if (request["key"]?.GetValue<string>() is { } key)
        {
            HandleDescriptionKey(Enum.Parse<VirtualKey>(key, ignoreCase: true));
        }

        bool open = _descriptionFlyout?.IsOpen ?? false;
        var scroll = _descriptionFlyout?.Content as Microsoft.UI.Xaml.Controls.ScrollViewer;
        return new JsonObject
        {
            ["open"] = open,
            ["runs"] = open ? HexView.RichToolTipRuns(scroll) : null,
            ["verticalOffset"] = scroll?.VerticalOffset,
            ["scrollableHeight"] = scroll?.ScrollableHeight,
        };
    }

    // ---- 位置マネージャ (INSP-31) ----

    private async Task<JsonObject> TestPositionManagerAsync(JsonObject request)
    {
        PositionManagerViewModel vm = _positionVm!;
        switch (Opt(request, "action"))
        {
            case "select":
                PositionManagerView?.SelectIndex(request["index"]!.GetValue<int>());
                break;
            case "comment":
                PositionManagerView?.SetComment(Opt(request, "text"));
                break;
            case "export":
                await ExportPositionNotesAsync(request["html"]?.GetValue<bool>() ?? false, Opt(request, "path"));
                break;
        }

        return new JsonObject
        {
            ["visible"] = IsPanelShown(PositionManagerPanelId),
            ["rows"] = new JsonArray([.. vm.Rows.Bookmarks.Select(b => (JsonNode?)b.Name)]),
            ["selected"] = vm.Selected?.Name,
        };
    }

    // ---- 色付けルール (INSP-33、INSP-34) ----

    /// <summary>
    /// 色付けルールの操作: scope (global / document)、add {rule}、enable {name, on}、move {name, delta}、preset {id, on}、select {name}、key {key}、state。
    /// rule は ColoringRule の JSON。
    /// </summary>
    private JsonObject TestColoring(JsonObject request)
    {
        ColoringRulesViewModel vm = _coloringVm!;
        if (request["scope"] is { } scope)
        {
            vm.Scope = scope.GetValue<string>() == "global" ? ColoringScope.Global : ColoringScope.Document;
        }

        string name = Opt(request, "name");
        ColoringRuleItem? item = vm.Items.FirstOrDefault(i => i.Rule.Name == name || i.Id == name);
        switch (Opt(request, "action"))
        {
            case "add":
                ColoringRule rule = ColoringRule.FromJson(request["rule"]!);
                List<ColoringRule> rules = [.. vm.Rules, rule];
                if (vm.Scope == ColoringScope.Global)
                {
                    GlobalColoringRules.Set(rules);
                }
                else
                {
                    CurrentAnnotations()!.SetColoringRules(rules);
                }

                vm.Reload();
                break;
            case "enable" when item is not null:
                vm.SetEnabled(item, request["on"]?.GetValue<bool>() ?? true);
                break;
            case "move" when item is not null:
                vm.Selected = item;
                vm.Move(request["delta"]!.GetValue<int>());
                break;
            case "select":
                vm.Selected = item;
                break;
            case "timings":
                return new JsonObject { ["times"] = new JsonArray([.. (CurrentAnnotations()?.Coloring.TakeEvaluationTimes() ?? []).Select(t => (JsonNode?)t)]) };
            case "key":
                return new JsonObject { ["handled"] = (ShownPanelContent(ColoringRulesPanelId) as ColoringRulesPanel)?.HandleKey(Enum.Parse<VirtualKey>(Opt(request, "key"), ignoreCase: true)) ?? false };
        }

        DocumentAnnotations? a = CurrentAnnotations();
        if (a is not null)
        {
            CompileColoring(a);
            RefreshAnnotationViews();
        }

        return new JsonObject
        {
            ["items"] = new JsonArray([.. vm.Items.Select(i => (JsonNode?)new JsonObject { ["name"] = i.Name, ["enabled"] = i.Enabled, ["error"] = i.Error })]),
            ["compiled"] = a?.Coloring.Rules.Rules.Count ?? 0,
            ["selected"] = vm.Selected?.Name,
        };
    }

    private async Task<JsonObject> TestLegendAsync(JsonObject request)
    {
        LegendViewModel vm = _legendVm!;
        LegendItem? item = vm.Items.FirstOrDefault(i => i.Name == Opt(request, "name"));
        switch (Opt(request, "action"))
        {
            case "next" when item is not null:
                await NavigateRuleAsync(item, true);
                break;
            case "previous" when item is not null:
                await NavigateRuleAsync(item, false);
                break;
            case "countAll":
                await CountAllRulesAsync();
                break;
        }

        vm.Refresh();
        return new JsonObject
        {
            ["visible"] = IsPanelShown(LegendPanelId),
            ["items"] = new JsonArray([.. vm.Items.Select(i => (JsonNode?)new JsonObject
            {
                ["kind"] = i.Kind.ToString(), ["name"] = i.Name, ["count"] = i.CountText, ["total"] = i.TotalText, ["shape"] = i.ShapeText,
                ["dash"] = i.Dash is null ? null : string.Join(",", i.Dash),
            })]),
        };
    }
}
#endif
