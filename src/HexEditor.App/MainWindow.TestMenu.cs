#if HEX_TEST_HOOKS
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Commands;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用のビルドだけの「テスト」メニュー (テスト方針 8.4) と、テスト用の命令の通り道 (<see cref="TestChannel"/>) の命令。
/// テスト用の画面なので、文字列はリソース化せず英語で書く (製品版には含めない)。
/// </summary>
public sealed partial class MainWindow
{
    private bool _testMenuAttached;

    /// <summary>メニューバーの末尾に「テスト」メニューを加える。</summary>
    public void AttachTestMenu()
    {
        if (_testMenuAttached || MainMenu is not { } menuBar)
        {
            return;
        }

        _testMenuAttached = true;
        var test = new MenuBarItem { Title = "Test" };
        AutomationProperties.SetAutomationId(test, "TestMenu");

        var hooks = new MenuFlyoutSubItem { Text = "Hooks" };
        AutomationProperties.SetAutomationId(hooks, "TestMenu_Hooks");
        hooks.Items.Add(HookToggle("Save: I/O error", "TestMenu_SaveIoError",
            s => s.SaveFault is { Kind: SaveFaultKind.Io },
            (s, on) => s with { SaveFault = on ? new SaveFault(0, SaveFaultKind.Io, false) : null }));
        hooks.Items.Add(HookToggle("Save: disk full", "TestMenu_SaveDiskFull",
            s => s.SaveFault is { Kind: SaveFaultKind.DiskFull },
            (s, on) => s with { SaveFault = on ? new SaveFault(0, SaveFaultKind.DiskFull, false) : null }));
        hooks.Items.Add(new MenuFlyoutSeparator());
        foreach (KillPoint point in new[] { KillPoint.SaveWrite, KillPoint.SaveBeforeReplace, KillPoint.SaveAfterReplace, KillPoint.InPlaceAfterJournal })
        {
            hooks.Items.Add(HookToggle("Kill at " + point, "TestMenu_Kill_" + point,
                s => s.KillAt == point,
                (s, on) => s with { KillAt = on ? point : KillPoint.None }));
        }

        hooks.Items.Add(HookToggle("Unhandled exception in save", "TestMenu_ThrowInSave",
            s => s.UnhandledException == ExceptionPlace.Save,
            (s, on) => s with { UnhandledException = on ? ExceptionPlace.Save : null }));
        test.Items.Add(hooks);

        var sources = new MenuFlyoutSubItem { Text = "Open virtual source" };
        AutomationProperties.SetAutomationId(sources, "TestMenu_Virtual");
        sources.Items.Add(TestItem("1 TiB (offsets)", "TestMenu_Virtual_1T",
            () => TestHooks.OpenVirtual(Vm, new VirtualSourceSpec("virtual-1T", 1L << 40, Core.Sources.VirtualContent.Offset64, false, 0, 0, 0, []))));
        sources.Items.Add(TestItem("2^63 - 1 bytes (fixed length)", "TestMenu_Virtual_Max",
            () => TestHooks.OpenVirtual(Vm, new VirtualSourceSpec("virtual-max", long.MaxValue, Core.Sources.VirtualContent.Offset64, false, 0, 0, 0, []))));
        sources.Items.Add(TestItem("Slow reads (500 ms)", "TestMenu_Virtual_Slow",
            () => TestHooks.OpenVirtual(Vm, new VirtualSourceSpec("virtual-slow", 1L << 30, Core.Sources.VirtualContent.Offset64, false, 0, 0, 500, []))));
        sources.Items.Add(TestItem("Read errors at 0x1000-0x1FFF", "TestMenu_Virtual_Errors",
            () => TestHooks.OpenVirtual(Vm, new VirtualSourceSpec("virtual-errors", 1L << 20, Core.Sources.VirtualContent.Offset64, false, 0, 0, 0, [(0x1000, 0x1000)]))));
        test.Items.Add(sources);
        test.Items.Add(new MenuFlyoutSeparator());

        test.Items.Add(TestItem("Show state", "TestMenu_ShowState", () => _ = ShowTestTextAsync("State", FormatState(TestState()))));
        test.Items.Add(TestItem("Show log", "TestMenu_ShowLog", () => _ = ShowTestTextAsync("Log", string.Join("\n", AppLog.Recent()))));
        test.Items.Add(TestItem("Save log", "TestMenu_SaveLog", () => ShowNotice("Log saved: " + SaveTestLog(), InfoBarSeverity.Informational)));
        test.Items.Add(TestItem("Open data folder", "TestMenu_OpenDataFolder", () =>
        {
            Directory.CreateDirectory(Program.Environment.Locations.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Program.Environment.Locations.Root}\"") { UseShellExecute = true });
        }));
        test.Items.Add(TestItem("Simulate low memory", "TestMenu_LowMemory", () => Vm.Memory.OnLowMemory()));
        test.Items.Add(new MenuFlyoutSeparator());
        test.Items.Add(TestItem("Throw on UI thread", "TestMenu_ThrowUi", () => TestHooks.Throw(ExceptionPlace.UiThread)));
        test.Items.Add(TestItem("Throw on background thread", "TestMenu_ThrowBackground", () => TestHooks.Throw(ExceptionPlace.Background)));
        test.Items.Add(TestItem("Unobserved task exception", "TestMenu_ThrowTask", () => TestHooks.Throw(ExceptionPlace.UnobservedTask)));

        menuBar.Items.Add(test);
    }

    private static MenuFlyoutItem TestItem(string text, string id, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        return item;
    }

    private static ToggleMenuFlyoutItem HookToggle(string text, string id, Func<TestHookSettings, bool> isOn,
        Func<TestHookSettings, bool, TestHookSettings> set)
    {
        var item = new ToggleMenuFlyoutItem { Text = text };
        AutomationProperties.SetAutomationId(item, id);
        item.Loaded += (_, _) => item.IsChecked = isOn(TestHooks.Settings);
        item.Click += (_, _) => TestHooks.Update(s => set(s, item.IsChecked));
        return item;
    }

    private async Task ShowTestTextAsync(string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = text, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono, Consolas") },
            },
            CloseButtonText = "Close",
        };
        AutomationProperties.SetAutomationId(dialog, "TestDialog");
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // ほかのダイアログが開いている。
        }
    }

    /// <summary>ログを設定フォルダの logs に書き出し、そのパスを返す。</summary>
    private static string SaveTestLog()
    {
        string folder = Program.Environment.Locations.Logs;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllLines(path, AppLog.Recent(), new UTF8Encoding(false));
        return path;
    }

    private static string FormatState(JsonObject state)
    {
        var text = new StringBuilder();
        foreach ((string key, JsonNode? value) in state)
        {
            if (value is JsonObject or JsonArray)
            {
                continue;
            }

            text.AppendLine($"{key}: {value}");
        }

        if (state["document"] is JsonObject doc)
        {
            text.AppendLine();
            foreach ((string key, JsonNode? value) in doc)
            {
                text.AppendLine($"{key}: {value}");
            }
        }

        return text.ToString();
    }

    // ---- テスト用の命令の通り道 ----

    /// <summary>
    /// テスト用の命令を UI スレッドで実行する。命令は {"cmd": 名前, ...引数}、答えは {"ok": true, ...} または
    /// {"ok": false, "error": 理由}。
    /// </summary>
    public async Task<JsonObject> HandleTestCommandAsync(JsonObject request)
    {
        string cmd = request["cmd"]?.GetValue<string>() ?? string.Empty;
        JsonObject result = cmd switch
        {
            "ping" => new JsonObject { ["pid"] = Environment.ProcessId, ["hooks"] = TestHooks.SettingsPath },
            "state" => TestState(),
            "key" => TestKey(request),
            "text" => TestText(request),
            "invoke" => TestInvoke(request["id"]!.GetValue<string>()),
            "element" => TestElement(request["id"]!.GetValue<string>()),
            "setSelectedIndex" => TestSetSelectedIndex(request["id"]!.GetValue<string>(), (int)TestHookSettings.ReadLong(request["index"], 0),
                request["text"]?.GetValue<string>()),
            "open" => TestOpen(request),
            "openVirtual" => DocumentResult(TestHooks.OpenVirtual(Vm, TestHookSettings.ParseVirtual(request))),
            "new" => DocumentResult(Vm.NewDocument()),
            "selectTab" => TestSelectTab((int)TestHookSettings.ReadLong(request["index"], 0)),
            "bytes" => TestBytes(request),
            "render" => TestRender(),
            "goto" => TestEditor(e => e.GoTo(TestHookSettings.ReadLong(request["offset"], 0), request["extend"]?.GetValue<bool>() ?? false)),
            "click" => TestEditor(e => e.Click(
                TestHookSettings.ReadLong(request["offset"], 0),
                Enum.Parse<ActiveColumn>(request["column"]?.GetValue<string>() ?? "Hex", ignoreCase: true),
                request["lowNibble"]?.GetValue<bool>() ?? false,
                request["shift"]?.GetValue<bool>() ?? false)),
            "select" => TestEditor(e => e.Select(TestHookSettings.ReadLong(request["start"], 0), TestHookSettings.ReadLong(request["length"], 0))),
            "lowMemory" => Run(() => Vm.Memory.OnLowMemory()),
            "gc" => Run(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }),
            "throw" => Run(() => TestHooks.Throw(Enum.Parse<ExceptionPlace>(request["place"]!.GetValue<string>(), ignoreCase: true))),
            "kill" => Run(() => TestHooks.Kill("command")),
            "setHooks" => TestSetHooks(request),
            "log" => new JsonObject { ["lines"] = new JsonArray(AppLog.Recent().Select(l => (JsonNode?)l).ToArray()) },
            "saveLog" => new JsonObject { ["path"] = SaveTestLog() },
            "writeRecovery" => await TestWriteRecoveryAsync(),
            "idle" => await TestIdleAsync((int)TestHookSettings.ReadLong(request["timeoutMs"], 10_000)),
            "pointer" => TestPointer(request),
            "wheel" => TestWheel(request),
            "scrollBar" => TestScrollBar(request),
            "hideContextMenu" => TestView(v => v.HideContextMenu()),
            "announcements" => new JsonObject
            {
                ["items"] = (CurrentView() ?? throw new InvalidOperationException("No hex view.")).ReadAnnouncements(request["clear"]?.GetValue<bool>() ?? false),
            },
            "setSystem" => TestSetSystem(request),
            "goToKey" => TestGoToKey(request),
            "resize" => TestResize(request),
            "clipboard" => await TestClipboardAsync(),
            "setClipboard" => await TestSetClipboardAsync(request),

            // 性能のテストと結合テストの命令 (MainWindow.TestPerf.cs)。
            "diagnostics" => TestDiagnostics(request),
            "keyMeasured" => TestKeyMeasured(request),
            "mark" => TestMark(),
            "insertBytes" => TestInsertBytes(request),
            "exit" => Run(() =>
            {
                _closingConfirmed = true;
                Close();
            }),
            _ => await HandleMoreTestCommandsAsync(cmd, request) ?? throw new ArgumentException($"Unknown command: {cmd}"),
        };
        result["ok"] ??= true;
        return result;
    }

    private static JsonObject Run(Action action)
    {
        action();
        return new JsonObject();
    }

    private JsonObject TestEditor(Action<EditorState> action)
    {
        if (Editor is not { } editor)
        {
            throw new InvalidOperationException("No document.");
        }

        action(editor);
        return new JsonObject();
    }

    private JsonObject DocumentResult(DocumentViewModel doc) => new() { ["index"] = Vm.Documents.IndexOf(doc), ["name"] = doc.DisplayName };

    /// <summary>選択中のタブの Hex ビュー。</summary>
    private HexView? CurrentView() => _views.FirstOrDefault(v => v.Editor == Editor);

    private JsonObject TestState()
    {
        var docs = new JsonArray();
        foreach (DocumentViewModel d in Vm.Documents)
        {
            docs.Add(new JsonObject
            {
                ["name"] = d.DisplayName,
                ["header"] = d.Header,
                ["path"] = d.FilePath,
                ["length"] = d.Document.Length,
                ["modified"] = d.Document.IsModified,
            });
        }

        object? focused = Root.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) : null;
        var state = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["title"] = Title,
            ["rootSize"] = $"{Root.ActualWidth}x{Root.ActualHeight}",
            ["hexViews"] = _views.Count,
            ["documents"] = docs,
            ["selectedIndex"] = Vm.Selected is { } s ? Vm.Documents.IndexOf(s) : -1,
            ["notice"] = NotificationState(Vm.Notifications.Open.FirstOrDefault(n => n.Message != Loc.Get("Crash_Message"))),
            ["crashNotice"] = NotificationState(Vm.Notifications.Open.FirstOrDefault(n => n.Message == Loc.Get("Crash_Message"))),
            ["notifications"] = new JsonArray([.. Vm.Notifications.Open.Select(n => (JsonNode?)NotificationState(n))]),
            ["findBarVisible"] = FindBar.Visibility == Visibility.Visible,
            ["goToBarVisible"] = GoToBar.Visibility == Visibility.Visible,
            ["activeOperations"] = Vm.Operations.Active.Count,
            ["focused"] = focused is FrameworkElement fe
                ? $"{fe.GetType().Name}:{AutomationProperties.GetAutomationId(fe)}"
                : focused?.GetType().Name,
            ["privateBytes"] = Process.GetCurrentProcess().PrivateMemorySize64,
            ["cacheBytes"] = Vm.Memory.CacheBytes,
            ["recoveryRoot"] = Vm.RecoveryRoot,
            ["dataRoot"] = Program.Environment.Locations.Root,
        };
        AddTestStateExtras(state);

        if (Vm.Selected is { } doc)
        {
            EditorState e = doc.Editor;
            Document d = doc.Document;
            EngineMemoryUsage memory = d.MemoryUsage;
            state["document"] = new JsonObject
            {
                ["name"] = doc.DisplayName,
                ["header"] = doc.Header,
                ["path"] = doc.FilePath,
                ["length"] = d.Length,
                ["modified"] = d.IsModified,
                ["canResize"] = d.CanResize,
                ["canSave"] = d.CanSave,
                ["editLocked"] = d.IsEditLocked,
                ["readOnly"] = e.ReadOnly,
                ["cursor"] = e.Cursor,
                ["lowNibble"] = e.LowNibble,
                ["selectionStart"] = e.SelectionStart,
                ["selectionLength"] = e.SelectionLength,
                ["insertMode"] = e.InsertMode,
                ["activeColumn"] = e.ActiveColumn.ToString(),
                ["topRow"] = e.TopRow,
                ["visibleRows"] = e.VisibleRows,
                ["bytesPerRow"] = e.BytesPerRow,
                ["canUndo"] = d.History.CanUndo,
                ["canRedo"] = d.History.CanRedo,
                ["undoCount"] = d.History.CurrentIndex,
                ["historyCount"] = d.History.Count,
                ["pieceCount"] = d.Current.Tree.PieceCount,
                ["cacheBytes"] = memory.Cache,
                ["addBufferMemory"] = memory.AddBufferMemory,
                ["addBufferSpilled"] = memory.AddBufferSpilled,
                ["memoryInUse"] = memory.TotalInMemory,
                ["canGoBack"] = e.CanGoBack,
                ["canGoForward"] = e.CanGoForward,
                ["sourceType"] = d.Source.GetType().Name,
            };
        }

        return state;
    }

    /// <summary>通知 (UI-36) の状態。null なら閉じている。</summary>
    private static JsonObject NotificationState(Core.Notifications.Notification? n) => n is null
        ? new JsonObject { ["open"] = false }
        : new JsonObject
        {
            ["open"] = true,
            ["message"] = n.DisplayMessage,
            ["severity"] = n.Severity.ToString(),
            ["scope"] = n.Scope.ToString(),
            ["count"] = n.Count,
        };

    /// <summary>キー 1 つ。Hex ビューが処理しなければ、メニューのショートカットキーとして扱う。</summary>
    private JsonObject TestKey(JsonObject request)
    {
        var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        bool shift = request["shift"]?.GetValue<bool>() ?? false;
        bool ctrl = request["ctrl"]?.GetValue<bool>() ?? false;
        bool alt = request["alt"]?.GetValue<bool>() ?? false;
        int count = (int)TestHookSettings.ReadLong(request["count"], 1);
        string handledBy = "none";
        for (int i = 0; i < count; i++)
        {
            // 実際のキー入力と同じく、まずウィンドウのキーの振り分け (コマンドの割り当て。UI-18) に通す。
            KeyModifiers modifiers = (ctrl ? KeyModifiers.Ctrl : 0) | (shift ? KeyModifiers.Shift : 0) | (alt ? KeyModifiers.Alt : 0);
            KeyContext context = CurrentView() is { } v
                ? new KeyContext(KeyScope.Editor, v.Editor?.ActiveColumn == ActiveColumn.Text)
                : CurrentKeyContext();
            DispatchResult dispatched = _keys.Dispatch((int)key, modifiers, context);
            if (dispatched.Handled)
            {
                handledBy = dispatched.Command is not { } command ? "pending"
                    : _menus.Find(command) is { } menuItem ? "menu:" + AutomationProperties.GetAutomationId(menuItem)
                    : "command:" + command;
            }
            else if (CurrentView() is { } view && view.InjectKey(key, shift, ctrl, alt))
            {
                handledBy = "hexView";
            }
            else if (FindAccelerator(key, shift, ctrl, alt) is { } item)
            {
                InvokeMenuItem(item);
                handledBy = "menu:" + AutomationProperties.GetAutomationId(item);
            }
        }

        CurrentView()?.RenderNow();
        return new JsonObject { ["handledBy"] = handledBy };
    }

    private JsonObject TestText(JsonObject request)
    {
        HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
        int handled = view.InjectText(request["text"]!.GetValue<string>(), request["ctrl"]?.GetValue<bool>() ?? false);
        view.RenderNow();
        return new JsonObject { ["handled"] = handled };
    }

    private MenuFlyoutItem? FindAccelerator(VirtualKey key, bool shift, bool ctrl, bool alt)
    {
        VirtualKeyModifiers modifiers = (shift ? VirtualKeyModifiers.Shift : 0) | (ctrl ? VirtualKeyModifiers.Control : 0)
            | (alt ? VirtualKeyModifiers.Menu : 0);
        return MenuItems().FirstOrDefault(i => i.KeyboardAccelerators.Any(a => a.Key == key && a.Modifiers == modifiers && a.IsEnabled));
    }

    private IEnumerable<MenuFlyoutItem> MenuItems()
    {
        if (MainMenu is not { } menuBar)
        {
            yield break;
        }

        // ステータスバーの右クリックメニュー (UI-06 の仕様 2) の項目も、メニューを開かずに押せるようにする。
        var pending = new Stack<MenuFlyoutItemBase>(menuBar.Items.SelectMany(m => m.Items).Concat(StatusItemsMenu.Items).Reverse());
        while (pending.Count > 0)
        {
            MenuFlyoutItemBase item = pending.Pop();
            if (item is MenuFlyoutItem flyoutItem)
            {
                yield return flyoutItem;
            }
            else if (item is MenuFlyoutSubItem sub)
            {
                foreach (MenuFlyoutItemBase child in sub.Items.Reverse())
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void InvokeMenuItem(MenuFlyoutItem item)
    {
        if (!item.IsEnabled)
        {
            throw new InvalidOperationException($"Menu item {AutomationProperties.GetAutomationId(item)} is disabled.");
        }

        if (item is ToggleMenuFlyoutItem toggle)
        {
            ((IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(toggle)).Toggle();
        }
        else
        {
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item)).Invoke();
        }
    }

    /// <summary>
    /// AutomationId で探した要素を、UI オートメーションの Invoke と同じ処理で押す。メニューの項目は、メニューを開かずに
    /// 実行する (メニューを開くとフォーカスが動くため)。
    /// </summary>
    private JsonObject TestInvoke(string id)
    {
        if (MenuItems().FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == id) is { } item)
        {
            InvokeMenuItem(item);
            return new JsonObject { ["target"] = "menu" };
        }

        FrameworkElement element = FindElement(id) ?? throw new ArgumentException($"Element not found: {id}");
        AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
        switch (peer.GetPattern(PatternInterface.Invoke))
        {
            case IInvokeProvider invoke:
                invoke.Invoke();
                break;
            default:
                if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                {
                    toggle.Toggle();
                    break;
                }

                throw new InvalidOperationException($"Element {id} cannot be invoked.");
        }

        return new JsonObject { ["target"] = element.GetType().Name };
    }

    /// <summary>AutomationId で探した要素の状態 (見つからなければ found = false)。</summary>
    private JsonObject TestElement(string id)
    {
        if (FindElement(id) is not { } e)
        {
            return new JsonObject { ["found"] = false };
        }

        var result = new JsonObject
        {
            ["found"] = true,
            ["type"] = e.GetType().Name,
            ["visibility"] = e.Visibility.ToString(),
            ["width"] = e.ActualWidth,
            ["height"] = e.ActualHeight,
            ["isLoaded"] = e.IsLoaded,
            ["name"] = AutomationProperties.GetName(e),
        };
        result["flowDirection"] = e.FlowDirection.ToString();
        result["bounds"] = BoundsInRoot(e);
        result["toolTip"] = ToolTipService.GetToolTip(e) is { } tip ? (tip as ToolTip)?.Content?.ToString() ?? tip.ToString() : null;
        AddElementExtras(e, result);
        switch (e)
        {
            case TextBlock t:
                result["text"] = t.Text;
                result["isTextTrimmed"] = t.IsTextTrimmed;
                break;
            case ContentControl { Content: string content }:
                result["content"] = content;
                if (e is Control control)
                {
                    result["isEnabled"] = control.IsEnabled;
                }

                break;
            case TextBox t:
                result["text"] = t.Text;
                result["selectedText"] = t.SelectedText;
                result["isEnabled"] = t.IsEnabled;
                result["focusState"] = t.FocusState.ToString();
                result["borderBrush"] = (t.BorderBrush as SolidColorBrush)?.Color.ToString();
                result["errorBorder"] = t.BorderBrush is SolidColorBrush border
                    && Application.Current.Resources["SystemFillColorCriticalBrush"] is SolidColorBrush critical
                    && border.Color == critical.Color;
                break;
            case Microsoft.UI.Xaml.Controls.Primitives.Selector selector:
                result["selectedIndex"] = selector.SelectedIndex;
                result["isEnabled"] = selector.IsEnabled;
                break;
            case Control c:
                result["isEnabled"] = c.IsEnabled;
                result["focusState"] = c.FocusState.ToString();
                break;
        }

        return result;
    }

    /// <summary>
    /// コンボボックスなどの選択を変える (UI オートメーションで選ぶにはドロップダウンを開く必要があり、開くとフォーカスが動くため)。
    /// </summary>
    private JsonObject TestSetSelectedIndex(string id, int index, string? text)
    {
        if (FindElement(id) is not Microsoft.UI.Xaml.Controls.Primitives.Selector selector)
        {
            throw new ArgumentException($"Selector not found: {id}");
        }

        // text を指定したら、表示の文字列が一致する項目を選ぶ (項目の並びが変わってもテストが壊れないように)。
        if (text is not null)
        {
            index = selector.Items.Select(i => i is ContentControl c ? c.Content?.ToString() : i?.ToString()).ToList().IndexOf(text);
            if (index < 0)
            {
                throw new ArgumentException($"Item not found in {id}: {text}");
            }
        }

        selector.SelectedIndex = index;
        return new JsonObject();
    }

    /// <summary>ウィンドウの中と、開いているポップアップ (ダイアログ) の中から AutomationId で探す。</summary>
    internal FrameworkElement? FindElement(string id)
    {
        var roots = new List<DependencyObject> { Root };
        if (Root.XamlRoot is { } xamlRoot)
        {
            roots.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Select(p => (DependencyObject)p.Child).Where(c => c is not null));
        }

        var pending = new Queue<DependencyObject>(roots);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();
            if (node is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) == id)
            {
                return fe;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    private JsonObject TestOpen(JsonObject request)
    {
        string path = request["path"]!.GetValue<string>();
        int delay = (int)TestHookSettings.ReadLong(request["delayMs"], 0);
        IReadOnlyList<(long, long)> errors = request["readErrors"]?.AsArray()
            .Select(r => (TestHookSettings.ReadLong(r!["offset"], 0), TestHookSettings.ReadLong(r["length"], 1))).ToList() ?? [];
        DocumentViewModel doc = delay > 0 || errors.Count > 0
            ? TestHooks.OpenFile(Vm, path, new FileSourceSpec("*", delay, errors, TestHookSettings.ReadLong(request["delayFromOffset"], 0)))
            : Vm.Open(path);
        return DocumentResult(doc);
    }

    private JsonObject TestSelectTab(int index)
    {
        Vm.Selected = Vm.Documents[index];
        return new JsonObject();
    }

    private JsonObject TestBytes(JsonObject request)
    {
        DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
        long offset = TestHookSettings.ReadLong(request["offset"], 0);
        long length = Math.Min(TestHookSettings.ReadLong(request["length"], doc.Document.Length - offset), 16 * 1024 * 1024);
        byte[] buffer = new byte[Math.Max(0, length)];
        Core.Sources.ReadResult read = doc.Document.Current.Read(offset, buffer);
        return new JsonObject
        {
            ["hex"] = Convert.ToHexString(buffer, 0, read.BytesReturned),
            ["unreadable"] = new JsonArray(read.Unreadable.Select(u => (JsonNode?)new JsonObject { ["offset"] = u.Offset, ["length"] = u.Length }).ToArray()),
        };
    }

    private JsonObject TestRender()
    {
        HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
        view.RenderNow();
        return view.ReadRendered();
    }

    private JsonObject TestSetHooks(JsonObject request)
    {
        JsonObject settings = request["settings"]?.AsObject() ?? new JsonObject();
        TestHookSettings parsed = TestHookSettings.Parse(settings.ToJsonString());
        TestHooks.Update(s => s with
        {
            SaveFault = settings.ContainsKey("saveFault") ? parsed.SaveFault : s.SaveFault,
            KillAt = settings.ContainsKey("killAt") ? parsed.KillAt : s.KillAt,
            UnhandledException = settings.ContainsKey("unhandledException") ? parsed.UnhandledException : s.UnhandledException,
            FileSources = settings.ContainsKey("fileSources") ? parsed.FileSources : s.FileSources,
        });
        return new JsonObject();
    }

    private async Task<JsonObject> TestWriteRecoveryAsync()
    {
        await Vm.WriteRecoveryAsync(ShowRecoveryWriteError);
        return new JsonObject();
    }

    /// <summary>長時間処理が終わり、UI スレッドの待ちがなくなるまで待つ。</summary>
    private async Task<JsonObject> TestIdleAsync(int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (Vm.Operations.Active.Count > 0 && watch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }

        // 優先度の低い処理まで終わらせる (描画の予約など)。
        var flushed = new TaskCompletionSource();
        App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => flushed.SetResult());
        await flushed.Task;
        CurrentView()?.RenderNow();
        return new JsonObject { ["idle"] = Vm.Operations.Active.Count == 0 };
    }
}
#endif
