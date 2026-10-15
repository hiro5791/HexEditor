#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Annotations;
using HexEditor.Core.Commands;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Editing;
using HexEditor.Core.Templates.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HexEditor.App;

/// <summary>構造体テンプレートのテスト用の命令 ("template"・"templateEditor")。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleTemplateTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        "template" => await TestTemplateAsync(request),
        "templateEditor" => await TestTemplateEditorAsync(request),
        _ => null,
    };

    private int? _templateTestApp;

    private async Task<JsonObject> TestTemplateAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        string? path = request["path"]?.GetValue<string>();
        _templateTestApp = request["app"]?.GetValue<int>();
        switch (action)
        {
            case "state":
                return TemplateTestState(request);
            case "run":
                // ダイアログを出すコマンドは閉じるまで終わらないので、待たずに返す
                _ = Commands.ExecuteAsync(request["id"]!.GetValue<string>());
                return new JsonObject();
            case "apply":
            {
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                string name = request["template"]!.GetValue<string>();
                TemplateCatalogEntry entry = TemplateService.Catalog.Find(name) ?? throw new ArgumentException("No template " + name);
                var info = new TemplateApplicationInfo
                {
                    TemplatePath = entry.Path,
                    StartOffset = TestHookSettings.ReadLong(request["offset"], 0),
                    BigEndian = request["bigEndian"]?.GetValue<bool>(),
                    Kind = request["follow"]?.GetValue<bool>() == true ? TemplateApplyKind.Follow : TemplateApplyKind.Fixed,
                    TypeName = request["type"]?.GetValue<string>(),
                };
                AddTemplateApplication(doc, info);
                return new JsonObject();
            }

            case "applyText":
            {
                // テストが作ったテンプレートのファイル (一時フォルダ) を適用する
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                string file = request["file"]!.GetValue<string>();
                AddTemplateApplication(doc, new TemplateApplicationInfo
                {
                    TemplatePath = file,
                    StartOffset = TestHookSettings.ReadLong(request["offset"], 0),
                    Kind = request["follow"]?.GetValue<bool>() == true ? TemplateApplyKind.Follow : TemplateApplyKind.Fixed,
                    TypeName = request["type"]?.GetValue<string>(),
                });
                return new JsonObject();
            }

            case "wait":
                await WaitTemplatesAsync();
                return TemplateTestState(request);
            case "removeAll":
                foreach (TemplateApplicationEntry e in CurrentTemplates?.Entries.ToList() ?? [])
                {
                    e.Owner.Remove(e);
                }

                return new JsonObject();
            case "expand" or "collapse" or "reveal" or "select":
            {
                TemplateTreeItem item = await RevealForTestAsync(path!);
                if (action == "collapse")
                {
                    TemplateVm.Model.Collapse(item);
                }
                else if (action == "expand")
                {
                    await TemplateVm.Model.ExpandAsync(item);
                }

                if (action is "select" || action == "reveal" && request["select"]?.GetValue<bool>() != false)
                {
                    SelectTemplateItemForTest(item);
                }

                return new JsonObject();
            }

            case "expandAll":
            {
                // ノードとその子のグループ・要素をすべて開く (TC-TPL-21-02 の前提。開いた行は 100 万行を超える)
                TemplateTreeItem item = await RevealForTestAsync(path!);
                TemplateTreeModel model = TemplateVm.Model;
                await model.ExpandAsync(item);
                int i = model.IndexOf(item) + 1;
                while (i < model.RowCount && model.ItemAt(i) is { } row && row.Depth > item.Depth)
                {
                    if (row.HasChildren && !row.IsExpanded)
                    {
                        await model.ExpandAsync(row);
                    }

                    i++;
                }

                if (request["load"]?.GetValue<bool>() == true)
                {
                    // すべての行の内容を読み込む (状態の "rows" で比べるため)
                    await model.LoadRowDataAsync([.. Enumerable.Range(0, model.RowCount).Select(model.ItemAt)]);
                }

                return new JsonObject { ["rowCount"] = model.RowCount };
            }

            case "treeScrollMeasured":
                return await TreeScrollMeasuredAsync((int)TestHookSettings.ReadLong(request["count"], 200));
            case "swatches":
            {
                // 結果ツリーに描画されている色見本 (TPL-03 の仕様 4) の色
                var list = new JsonArray();
                var stack = new Stack<DependencyObject>();
                if (TemplatePanelView is { } view)
                {
                    stack.Push(view);
                }

                while (stack.Count > 0)
                {
                    DependencyObject o = stack.Pop();
                    if (o is TextBlock { Visibility: Visibility.Visible, Foreground: Microsoft.UI.Xaml.Media.SolidColorBrush brush } t
                        && AutomationProperties.GetAutomationId(t) == "Tpl_ValueSwatch")
                    {
                        list.Add(new JsonObject
                        {
                            ["name"] = AutomationProperties.GetName(t),
                            ["color"] = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}",
                            ["width"] = t.ActualWidth,
                        });
                    }

                    for (int i = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o) - 1; i >= 0; i--)
                    {
                        stack.Push(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i));
                    }
                }

                return new JsonObject { ["swatches"] = list };
            }

            case "selectRoot":
            {
                TemplateTreeItem root = TemplateVm.Roots.ElementAt((int)TestHookSettings.ReadLong(request["index"], 0));
                SelectTemplateItemForTest(root);
                return new JsonObject();
            }

            case "key":
            {
                TemplatePanel panel = TemplatePanelView ?? throw new InvalidOperationException("No template panel.");
                TemplateTreeItem item = TemplateVm.SelectedItem ?? TemplateVm.Model.ItemAt(0);
                var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
                bool handled = await panel.HandleKeyAsync(key, request["ctrl"]?.GetValue<bool>() ?? false, item);
                return new JsonObject { ["handled"] = handled };
            }

            case "focusTree":
                ShowPanel(TemplatesPanelId);
                (TemplatePanelView ?? throw new InvalidOperationException("No template panel.")).FocusTree();
                return new JsonObject();
            case "goToPath":
            {
                TemplatePanel panel = TemplatePanelView ?? throw new InvalidOperationException("No template panel.");
                bool found = await panel.GoToPathAsync(path!);
                return new JsonObject { ["found"] = found, ["message"] = panel.Message };
            }

            case "filter":
            {
                TemplatePanel panel = TemplatePanelView ?? throw new InvalidOperationException("No template panel.");
                panel.FilterInput.Text = request["text"]?.GetValue<string>() ?? string.Empty;
                await panel.FilterAsync(request["all"]?.GetValue<bool>() ?? false);
                return new JsonObject();
            }

            case "edit":
            {
                TemplateTreeItem item = await RevealForTestAsync(path!);
                SelectTemplateItemForTest(item);
                if (TemplatePanelView is { } panel)
                {
                    _ = panel.EditAsync(item);
                }

                await WaitUntilForTestAsync(() => ActiveValueEditor is not null, TimeSpan.FromSeconds(10));
                return EditorStateForTest();
            }

            case "editState":
                return EditorStateForTest();
            case "editSet":
            {
                TemplateValueEditorFlyout editor = ActiveValueEditor ?? throw new InvalidOperationException("No value editor.");
                if (request["text"]?.GetValue<string>() is { } text && editor.TextInput is { } box)
                {
                    if (editor.EnumInput is { } combo)
                    {
                        combo.SelectedIndex = combo.Items.Count - 1;
                    }

                    box.Text = text;
                }

                if (request["enum"]?.GetValue<string>() is { } name && editor.EnumInput is { } e2)
                {
                    e2.SelectedItem = name;
                }

                if (request["flags"] is JsonArray flags)
                {
                    var set = flags.Select(f => f!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    foreach (CheckBox b in editor.FlagInputs)
                    {
                        b.IsChecked = set.Contains((string)b.Tag);
                    }
                }

                // TextChanged は後から届くので、ここで検証して状態を返す
                editor.Validate();
                return EditorStateForTest();
            }

            case "editKey":
            {
                TemplateValueEditorFlyout editor = ActiveValueEditor ?? throw new InvalidOperationException("No value editor.");
                string key = request["key"]!.GetValue<string>();
                bool committed = key == "Enter" ? editor.Commit() : Run(editor.Cancel) is not null && false;
                await WaitUntilForTestAsync(() => ActiveValueEditor is null || !committed && key == "Enter", TimeSpan.FromSeconds(5));
                return new JsonObject { ["committed"] = committed };
            }

            case "menu":
            {
                TemplateTreeItem item = await RevealForTestAsync(path!);
                var menu = new MenuFlyout();
                FillTemplateMenu(menu, [item]);
                return new JsonObject
                {
                    ["items"] = new JsonArray([.. menu.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)new JsonObject
                    {
                        ["id"] = AutomationProperties.GetAutomationId(i),
                        ["text"] = i.Text,
                        ["enabled"] = i.IsEnabled,
                        ["tooltip"] = ToolTipService.GetToolTip(i) as string,
                    })]),
                };
            }

            case "menuInvoke":
            {
                TemplateTreeItem item = await RevealForTestAsync(path!);
                // 右クリックは選択を変えない (Hex ビューのカーソルも動かさない)
                var menu = new MenuFlyout();
                FillTemplateMenu(menu, [item]);
                string id = request["id"]!.GetValue<string>();
                MenuFlyoutItem mi = menu.Items.OfType<MenuFlyoutItem>().First(i => AutomationProperties.GetAutomationId(i) == id);
                if (!mi.IsEnabled)
                {
                    throw new InvalidOperationException("Menu item disabled: " + id);
                }

                // MenuFlyoutItem.Click は UI オートメーションの Invoke で起こす
                if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(mi)?.GetPattern(
                    Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
                {
                    invoke.Invoke();
                }

                return new JsonObject();
            }

            case "segments":
            {
                long start = TestHookSettings.ReadLong(request["start"], 0);
                long length = TestHookSettings.ReadLong(request["length"], 256);
                var list = new JsonArray();
                if (Vm.Selected is { } doc && DocumentTemplates.Find(doc.Document) is { } templates)
                {
                    foreach (TemplateApplicationEntry e in templates.Entries.Where(e => !e.IsFollow && e.Result is not null))
                    {
                        TemplateResult r = e.Result!;
                        IReadOnlyList<TemplatePaintSegment> segments = await r.InvokeAsync(() => TemplateColoring.Segments(r, start, start + length));
                        foreach (TemplatePaintSegment s in segments)
                        {
                            list.Add(new JsonObject { ["start"] = s.Start, ["length"] = s.Length, ["color"] = s.Color.Name, ["path"] = s.Node.Path });
                        }
                    }
                }

                return new JsonObject { ["segments"] = list };
            }

            case "export":
            {
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                TemplateApplicationEntry entry = CurrentTemplates!.Entries[(int)TestHookSettings.ReadLong(request["index"], 0)];
                TemplateExportFormat format = request["format"]?.GetValue<string>() == "csv" ? TemplateExportFormat.Csv : TemplateExportFormat.Json;
                TemplateExportFailAt = request["failAt"] is { } failAt ? TestHookSettings.ReadLong(failAt, 0) : null;
                TemplateNode root = entry.Result!.Root;
                Task task = ExportTemplateToFileAsync(doc, root, request["file"]!.GetValue<string>(), format, new TemplateExportOptions { Display = TemplateDisplayContext.Current });
                if (request["wait"]?.GetValue<bool>() != false)
                {
                    await task;
                }

                return new JsonObject();
            }

            case "output":
            {
                ShowTemplateOutput();
                _templateOutput?.Refresh(CurrentTemplates);
                return new JsonObject
                {
                    ["rows"] = new JsonArray([.. (_templateOutput?.Rows ?? []).Select(r => (JsonNode?)new JsonObject
                    {
                        ["text"] = r.ToString(),
                        ["severity"] = r.Entry.Severity.ToString(),
                        ["code"] = r.Code,
                        ["glyph"] = r.Glyph,
                        ["severityText"] = r.SeverityText,
                    })]),
                };
            }

            case "outputInvoke":
            {
                TemplateOutputRow row = (_templateOutput ?? throw new InvalidOperationException("No output panel.")).Rows[(int)TestHookSettings.ReadLong(request["index"], 0)];
                MenuFlyout menu = _templateOutput.RowMenu(row);
                string id = request["id"]!.GetValue<string>();
                MenuFlyoutItem mi = menu.Items.OfType<MenuFlyoutItem>().First(i => AutomationProperties.GetAutomationId(i) == id);
                if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(mi)?.GetPattern(
                    Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
                {
                    invoke.Invoke();
                }

                return new JsonObject();
            }

            case "suggestion":
                return new JsonObject
                {
                    ["action"] = LastAutoApplyDecision?.Action.ToString(),
                    ["top"] = LastAutoApplyDecision?.Top?.Entry.Metadata.Name,
                    ["candidates"] = new JsonArray([.. (LastAutoApplyDecision?.Candidates ?? []).Select(c => (JsonNode?)new JsonObject
                    {
                        ["name"] = c.Entry.Metadata.Name,
                        ["line"] = CandidateText(c),
                        ["magic"] = c.Match.Magic,
                        ["mime"] = c.Match.Mime,
                        ["extension"] = c.Match.Extension,
                    })]),
                    ["elapsedMs"] = LastAutoApplyDecision?.Elapsed.TotalMilliseconds,
                };
            case "applyDialog":
                return new JsonObject { ["items"] = new JsonArray([.. (_lastApplyDialog?.Items ?? []).Select(i => (JsonNode?)i.Name)]) };
            case "inspectorEdit":
                return new JsonObject { ["ok"] = _inspectorVm.EditForTest(request["name"]!.GetValue<string>(), request["text"]!.GetValue<string>()) };
            case "settingsText":
                return new JsonObject { ["text"] = LastTemplateSettingsText };
            case "manager":
                return new JsonObject
                {
                    ["rows"] = new JsonArray([.. Views.TemplateSettingsSection.Open().SelectMany(Views.TemplateSettingsSection.Rows).Select(r => (JsonNode?)new JsonObject
                    {
                        ["name"] = r.Name,
                        ["origin"] = r.Origin,
                        ["enabled"] = r.Enabled,
                        ["autoApply"] = r.AutoApply,
                        ["shadowed"] = r.Shadowed,
                    })]),
                };
            case "unsuppressAll":
                TemplateService.Preferences.Suppressed.Clear();
                TemplateService.SavePreferences();
                Views.TemplateSettingsSection.RefreshAll();
                return new JsonObject();
            case "highlights":
            {
                // Hex ビューの描いた強調のうちテンプレートのもの (色分け・追従・デバッガ)
                HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
                view.RenderNow();
                return new JsonObject { ["highContrast"] = view.IsHighContrast };
            }

            default:
                throw new ArgumentException("Unknown template action: " + action);
        }
    }

    private void SelectTemplateItemForTest(TemplateTreeItem item)
    {
        if (TemplatePanelView is { } panel && panel.TreeList is { } tree)
        {
            int index = TemplateVm.Model.IndexOf(item);
            if (index >= 0)
            {
                tree.SelectedItems.Clear();
                tree.SelectedIndex = index;
                tree.ScrollIntoView(TemplateVm.Rows.Row(item));
            }
        }

        TemplateVm.SelectedItem = item;
    }

    private async Task<TemplateTreeItem> RevealForTestAsync(string path)
    {
        await WaitTemplatesAsync();
        IEnumerable<TemplateTreeItem> roots = TemplateVm.Roots.ToList();
        if (_templateTestApp is int index)
        {
            // "app" を指定したらその番号の適用の中だけを探す (同じパスが複数の適用にある場合)
            roots = roots.Skip(index).Take(1);
        }

        foreach (TemplateTreeItem root in roots)
        {
            if (path == "@app")
            {
                await TemplateVm.Model.LoadRowDataAsync([root], force: true);
                return root;
            }

            if (await TemplateVm.Model.RevealPathAsync(root, path) is { } item)
            {
                await TemplateVm.Model.LoadRowDataAsync([item]);
                return item;
            }
        }

        throw new ArgumentException("Path not found: " + path);
    }

    private async Task WaitTemplatesAsync()
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<TemplateApplicationEntry> entries = CurrentTemplates?.Entries ?? [];
            if (entries.All(e => e.Result is { Completion.IsCompleted: true } || e.Debugger is not null)
                && entries.All(e => TemplateVm.Roots.Any(r => ReferenceEquals(r.Result, e.Result))))
            {
                await Task.Delay(20);
                return;
            }

            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilForTestAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// 結果ツリーを 1 画面ずつ <paramref name="count"/> 回スクロールし (PageDown と同じ量)、フレームの間隔を記録する。続けてスクロールバーを
    /// 先頭・中央・末尾に移し、それぞれ描画までの時間を計る (TC-TPL-21-02)。
    /// </summary>
    private async Task<JsonObject> TreeScrollMeasuredAsync(int count)
    {
        TemplatePanel panel = TemplatePanelView ?? throw new InvalidOperationException("No template panel.");
        ScrollViewer scroller = FindDescendant<ScrollViewer>(panel.TreeList) ?? throw new InvalidOperationException("No scroll viewer.");
        var intervals = new JsonArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double last = -1;
        void OnRendering(object? sender, object e)
        {
            double now = watch.Elapsed.TotalMilliseconds;
            if (last >= 0)
            {
                intervals.Add(now - last);
            }

            last = now;
        }

        async Task NextFrameAsync()
        {
            var done = new TaskCompletionSource();
            void Once(object? s, object e)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Once;
                done.TrySetResult();
            }

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Once;
            await done.Task;
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        try
        {
            for (int i = 0; i < count; i++)
            {
                scroller.ChangeView(null, scroller.VerticalOffset + scroller.ViewportHeight, null, true);
                await NextFrameAsync();
            }
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        var jumps = new JsonArray();
        foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            scroller.ChangeView(null, scroller.ScrollableHeight * fraction, null, true);
            scroller.UpdateLayout();
            await NextFrameAsync();
            jumps.Add(t.Elapsed.TotalMilliseconds);
        }

        return new JsonObject { ["intervals"] = intervals, ["jumps"] = jumps, ["rowCount"] = TemplateVm.Model.RowCount };
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            DependencyObject o = stack.Pop();
            if (o is T found)
            {
                return found;
            }

            for (int i = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o) - 1; i >= 0; i--)
            {
                stack.Push(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i));
            }
        }

        return null;
    }

    private JsonObject EditorStateForTest()
    {
        if (ActiveValueEditor is not { } editor)
        {
            return new JsonObject { ["open"] = false };
        }

        return new JsonObject
        {
            ["open"] = true,
            ["kind"] = editor.Session.Kind.ToString(),
            ["initial"] = editor.Session.InitialText,
            ["text"] = editor.TextInput?.Text,
            ["raw"] = editor.Session.EditsRawValue,
            ["error"] = editor.ErrorText,
            ["canCommit"] = editor.CanCommit,
            ["choices"] = new JsonArray([.. editor.Session.Choices.Select(c => (JsonNode?)c.Name)]),
            ["checked"] = new JsonArray([.. editor.FlagInputs.Where(b => b.IsChecked == true).Select(b => (JsonNode?)(string)b.Tag)]),
        };
    }

    private JsonObject TemplateTestState(JsonObject request)
    {
        var apps = new JsonArray();
        foreach (TemplateApplicationEntry e in CurrentTemplates?.Entries ?? [])
        {
            TemplateResult? r = e.Result;
            apps.Add(new JsonObject
            {
                ["name"] = e.DisplayName,
                ["template"] = e.Info.TemplatePath,
                ["offset"] = e.Info.StartOffset,
                ["kind"] = e.Info.Kind.ToString(),
                ["type"] = e.Info.TypeName,
                ["bigEndian"] = e.Info.BigEndian,
                ["state"] = r?.State.ToString(),
                ["completed"] = r?.Completion.IsCompleted,
                ["errors"] = r?.ErrorCount,
                ["warnings"] = r?.WarningCount,
                ["patterns"] = r?.PatternCount,
                ["unsaved"] = e.Application.IsUnsaved,
                ["missing"] = e.Application.TemplateMissing,
                ["followOffset"] = e.Application.FollowOffset,
                ["evaluations"] = e.Application.EvaluationCount,
                ["debugging"] = e.Debugger is not null,
            });
        }

        int max = (int)TestHookSettings.ReadLong(request["rows"], 200);
        var rows = new JsonArray();
        TemplateTreeModel model = TemplateVm.Model;
        for (int i = 0; i < Math.Min(max, model.RowCount); i++)
        {
            TemplateTreeItem item = model.ItemAt(i);
            TemplateRowViewModel row = TemplateVm.Rows.Row(item);
            rows.Add(new JsonObject
            {
                ["name"] = row.Name,
                ["value"] = row.Value,
                ["type"] = row.Type,
                ["offset"] = row.OffsetText,
                ["size"] = row.SizeText,
                ["path"] = item.Kind == TemplateTreeItemKind.Node ? item.Node!.Path : item.Key,
                ["kind"] = item.Kind.ToString(),
                ["depth"] = item.Depth,
                ["expanded"] = item.IsExpanded,
                ["hasChildren"] = item.HasChildren,
                ["status"] = row.StatusText,
                ["glyph"] = row.KindGlyph,
                ["tooltip"] = row.ToolTipText,
                ["accessibleName"] = row.AccessibleName,
                ["loaded"] = item.Data is not null,
                ["start"] = item.Data?.Offset,
                ["length"] = item.Data?.Size,
                ["valueSwatch"] = item.Data?.ValueArgb is { } argb ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) : null,
            });
        }

        return new JsonObject
        {
            ["panelOpen"] = IsPanelShown(TemplatesPanelId),
            ["outputOpen"] = IsPanelShown(TemplateOutputPanelId),
            ["applications"] = apps,
            ["rowCount"] = model.RowCount,
            ["rows"] = rows,
            ["selectedPath"] = TemplateVm.SelectedItem?.Node?.Path,
            ["pathText"] = TemplateVm.PathText,
            ["status"] = TemplateVm.StatusText,
            ["counts"] = TemplateVm.CountsText,
            ["limitReached"] = TemplateVm.LimitReached,
            ["message"] = TemplatePanelView?.Message,
            ["followCursor"] = TemplateVm.FollowCursor,
            ["coloring"] = TemplateService.ColoringEnabled,
            ["fullScreen"] = IsFullScreen,
            ["readOnly"] = Vm.Selected?.Document.IsReadOnly,
            ["readOnlyReason"] = Vm.Selected?.Document.ReadOnlyReason.ToString(),
        };
    }

    // ───────────── テンプレートエディタ ─────────────

    private async Task<JsonObject> TestTemplateEditorAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        TemplateEditorPage? page = ActiveTemplateEditor ?? _templateEditors.Values.LastOrDefault();
        switch (action)
        {
            case "open":
                page = OpenTemplateEditor(request["path"]!.GetValue<string>()) ?? throw new InvalidOperationException("Cannot open.");
                break;
            case "openStandard":
                page = OpenTemplateEditor(TemplateService.Catalog.Find(request["name"]!.GetValue<string>())!.Path) ?? throw new InvalidOperationException("Cannot open.");
                break;
            case "new":
                page = OpenNewTemplateEditor();
                break;
            case "setText":
                Need(page).SetText(request["text"]!.GetValue<string>());
                break;
            case "insert":
            {
                TemplateEditorPage p = Need(page);
                string text = request["text"]!.GetValue<string>();
                int caret = p.Caret;
                p.SetText(p.Text[..caret] + text + p.Text[caret..]);
                p.Caret = caret + text.Length;
                break;
            }

            case "caret":
            {
                TemplateEditorPage p = Need(page);
                if (request["position"] is { } pos)
                {
                    p.Caret = (int)TestHookSettings.ReadLong(pos, 0);
                }
                else
                {
                    p.MoveCaret((int)TestHookSettings.ReadLong(request["line"], 1), (int)TestHookSettings.ReadLong(request["column"], 1));
                }

                break;
            }

            case "end":
                Need(page).Caret = int.MaxValue;
                break;
            case "complete":
                Need(page).ShowCompletion();
                break;
            case "check":
                Need(page).SetText(Need(page).Text);
                break;
            case "save":
                await Need(page).SaveAsync();
                break;
            case "duplicate":
                await DuplicateTemplateAsync(Need(page));
                page = ActiveTemplateEditor;
                break;
            case "target":
                Need(page).SelectTarget(Vm.Documents[(int)TestHookSettings.ReadLong(request["index"], 0)]);
                break;
            case "tryRun":
                Need(page).TryRun();
                break;
            case "focus":
                ShowToolPageTab(ActiveToolPage ?? _templateEditors.Keys.Last());
                Need(page).FocusEditor();
                break;
            case "key":
            {
                // コードエディタにフォーカスがあるときのキーの振り分け (00-overview 8.5。F11 はグローバルの全画面表示より優先する)
                var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
                KeyModifiers modifiers = (request["ctrl"]?.GetValue<bool>() == true ? KeyModifiers.Ctrl : 0)
                    | (request["shift"]?.GetValue<bool>() == true ? KeyModifiers.Shift : 0);
                DispatchResult r = Need(page).HandleEditorKey(key, modifiers.HasFlag(KeyModifiers.Ctrl), modifiers.HasFlag(KeyModifiers.Shift), false)
                    ? new DispatchResult(true)
                    : _keys.Dispatch((int)key, modifiers, new KeyContext(KeyScope.CodeEditor, true));
                await Task.Delay(50);
                JsonObject state = EditorTestState(page);
                state["handledBy"] = r.Command;
                return state;
            }

            case "showDocument":
                // ドキュメントのタブに切り替える (エディタのタブは開いたまま)
                _activeToolPage = null;
                UpdateToolPages();
                SyncTabSelection();
                FocusEditor();
                break;
            case "breakpoint":
                Need(page).ToggleBreakpoint((int)TestHookSettings.ReadLong(request["line"], 1));
                break;
            case "addWatch":
                Need(page).AddWatch(request["expression"]!.GetValue<string>());
                break;
            case "waitPaused":
                await WaitUntilForTestAsync(() => Need(page).Debugger.IsPaused && Need(page).DebugLine is not null, TimeSpan.FromSeconds(30));
                break;
            case "waitEnded":
                await WaitUntilForTestAsync(() => !Need(page).IsDebugging, TimeSpan.FromSeconds(30));
                await Task.Delay(50);
                break;
            case "close":
                CloseToolPage(_templateEditors.First(p => ReferenceEquals(p.Value, Need(page))).Key);
                return new JsonObject();
            case "state":
                break;
            default:
                throw new ArgumentException("Unknown editor action: " + action);
        }

        await Task.Delay(10);
        return EditorTestState(page);
    }

    private static TemplateEditorPage Need(TemplateEditorPage? page) => page ?? throw new InvalidOperationException("No template editor.");

    private JsonObject EditorTestState(TemplateEditorPage? page)
    {
        if (page is null)
        {
            return new JsonObject { ["open"] = false };
        }

        TemplateCompletionResult? completion = page.Completion;
        return new JsonObject
        {
            ["open"] = true,
            ["count"] = _templateEditors.Count,
            ["path"] = page.Path,
            ["text"] = page.Text,
            ["dirty"] = page.IsDirty,
            ["standard"] = page.IsStandard,
            ["caretLine"] = page.CaretLine,
            ["message"] = page.LastMessage,
            ["debugging"] = page.IsDebugging,
            ["paused"] = page.Debugger.IsPaused,
            ["debugLine"] = page.DebugLine,
            ["position"] = page.Debugger.State?.Position,
            ["variables"] = new JsonArray([.. page.VariableRows.Select(v => (JsonNode?)v)]),
            ["watches"] = new JsonArray([.. page.WatchRows.Select(v => (JsonNode?)v)]),
            ["callStack"] = new JsonArray([.. page.CallStackRows.Select(v => (JsonNode?)v)]),
            ["completion"] = completion is null ? null : new JsonArray([.. completion.Items.Select(i => (JsonNode?)new JsonObject { ["label"] = i.Label, ["detail"] = i.Detail })]),
            ["decorations"] = new JsonArray([.. page.Decorations.Select(d => (JsonNode?)new JsonObject
            {
                ["line"] = d.Line,
                ["column"] = d.Column,
                ["length"] = d.Length,
                ["severity"] = d.Diagnostic.Severity.ToString(),
                ["tooltip"] = page.ToolTipAt(d.Line, d.Column),
            })]),
            ["fullScreen"] = IsFullScreen,
            ["activePage"] = ActiveToolPage,
        };
    }
}
#endif
