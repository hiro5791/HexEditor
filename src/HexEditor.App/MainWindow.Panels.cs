using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Panels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// パネル (UI-05): 左・右・下の領域と浮動パネルへの配置、表示切り替え、配置の保存。パネルの中身は <see cref="PanelRegistry"/> に
/// 登録したもので、このウィンドウで初めて表示するときに 1 度だけ作る。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>最後に閉じたウィンドウの配置 (新しいウィンドウが引き継ぐ。UI-05 の仕様 7)。state.json のキー。</summary>
    public const string LastPanelLayoutKey = "panels.lastLayout";

    private readonly Dictionary<string, FrameworkElement> _panelContents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FloatingPanelWindow> _floatingPanels = new(StringComparer.Ordinal);
    private PanelLayout _panelLayout = new();
    private PanelContext _panelContext = null!;
    private bool _windowClosing;

    /// <summary>このウィンドウのパネルの配置 (セッション UI-31 に保存するもの)。</summary>
    public JsonObject PanelLayoutJson => _panelLayout.ToJson();

    /// <summary>セッションから配置を戻す (UI-31)。</summary>
    public void RestorePanelLayout(JsonNode? layout)
    {
        _panelLayout = PanelLayout.FromJson(layout);
        ApplyPanelLayout();
    }

    private IEnumerable<(PanelDock Dock, PanelDockArea Area)> DockAreas =>
        [(PanelDock.Left, LeftPanel), (PanelDock.Right, RightPanel), (PanelDock.Bottom, BottomPanel)];

    private void InitializePanels()
    {
        _panelContext = new PanelContext(this, Vm);
        _panelLayout = PanelLayout.FromJson(CommandService.State.Get(LastPanelLayoutKey));
        BottomSplitter.Vertical = false;
        foreach ((PanelDock dock, PanelDockArea area) in DockAreas)
        {
            area.Dock = dock;
            WireArea(area);
        }

        LeftSplitter.Moved += (_, d) => ResizeDock(PanelDock.Left, d);
        RightSplitter.Moved += (_, d) => ResizeDock(PanelDock.Right, -d);
        BottomSplitter.Moved += (_, d) => ResizeDock(PanelDock.Bottom, -d);

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.Selected))
            {
                _panelContext.RaiseActiveDocumentChanged();
                ApplyPanelLayout();
            }
        };

        // 後から登録されたパネル (プラグインなど) もこのウィンドウにつなぐ。
        Action<PanelRegistration> registered = p => DispatcherQueue.TryEnqueue(() =>
        {
            RegisterPanelToggle(p);
            ApplyPanelLayout();
        });
        PanelRegistry.Registered += registered;
        Closed += (_, _) =>
        {
            _windowClosing = true;
            PanelRegistry.Registered -= registered;
            SavePanelLayout();

            // まだ書いていない keybindings.json と state.json を書く (終了の確認を経ない閉じ方でも)。
            CommandService.Flush();
            foreach (FloatingPanelWindow w in _floatingPanels.Values.ToList())
            {
                w.Close();
            }
        };

        // ドラッグ中は、すべての場所をドロップ先として示す (UI-05 の仕様 3)。
        Action<string?> dragging = id => DispatcherQueue.TryEnqueue(() => ShowDropTargets(id is not null));
        PanelDockArea.DraggingChanged += dragging;
        Closed += (_, _) => PanelDockArea.DraggingChanged -= dragging;
        ApplyPanelLayout();
    }

    private void WireArea(PanelDockArea area)
    {
        area.TabSelected += (_, id) =>
        {
            _panelLayout.ActiveTab[area.Dock] = id;
            ApplyPanelLayout();
        };
        area.CloseRequested += (_, id) => HidePanel(id);
        area.MoveRequested += (_, request) => MovePanel(request.PanelId, request.Target);
        area.HeaderDoubleTapped += (_, id) =>
        {
            if (area.Dock == PanelDock.Floating)
            {
                _panelLayout.Redock(id);
                ApplyPanelLayout();
            }
        };
    }

    private void RegisterPanelCommands()
    {
        foreach ((string id, PanelDock dock) in new[] { ("view.leftPanel", PanelDock.Left), ("view.rightPanel", PanelDock.Right), ("view.bottomPanel", PanelDock.Bottom) })
        {
            Commands.Register(id, () => ToggleDock(dock), () => _panelLayout.VisibleIn(dock).Count == 0
                ? new CommandState(false, Loc.Get("Command_NoPanels"), false)
                : Toggle(_panelLayout.IsDockShown(dock)));
        }

        Commands.Register("view.resetPanelLayout", ResetPanelLayout);

        // 表示切り替えのコマンド (view.panel.<ID>) は、パネルが登録されるまで使えない。
        foreach (Core.Commands.CommandDefinition c in CommandService.Catalog.All.Where(c => c.Id.StartsWith("view.panel.", StringComparison.Ordinal)))
        {
            Commands.Register(c.Id, () => { }, () => CommandState.Unavailable(Loc.Get("Command_PanelMissing")));
        }

        foreach (PanelRegistration panel in PanelRegistry.All)
        {
            RegisterPanelToggle(panel);
        }
    }

    private void RegisterPanelToggle(PanelRegistration panel)
    {
        if (CommandService.Catalog.Contains(panel.ToggleCommand))
        {
            Commands.Register(panel.ToggleCommand, () => TogglePanel(panel.Id), () => Toggle(IsPanelShown(panel.Id)));
        }
    }

    /// <summary>パネルが表示されているか (隠した領域にあるものは表示されていない)。</summary>
    public bool IsPanelShown(string id) =>
        _panelLayout.Find(id) is { Visible: true } p && (p.Dock == PanelDock.Floating || !_panelLayout.HiddenDocks.Contains(p.Dock));

    /// <summary>パネルを表示する (既定の場所、または前回の場所)。</summary>
    public void ShowPanel(string id)
    {
        if (PanelRegistry.Find(id) is not { } reg)
        {
            return;
        }

        _panelLayout.Show(id, reg.DefaultDock);
        ApplyPanelLayout();
        FocusPanel(id);
    }

    public void HidePanel(string id)
    {
        _panelLayout.Hide(id);
        ApplyPanelLayout();
    }

    public void TogglePanel(string id)
    {
        if (IsPanelShown(id))
        {
            HidePanel(id);
            FocusEditor();
        }
        else
        {
            ShowPanel(id);
        }
    }

    /// <summary>パネルを別の場所に移す (見出しのメニュー・ドラッグ。UI-05 の仕様 3、4)。</summary>
    public void MovePanel(string id, PanelDock dock)
    {
        if (PanelRegistry.Find(id) is not { } reg)
        {
            return;
        }

        _panelLayout.Move(id, dock, reg.DefaultDock);
        ApplyPanelLayout();
        FocusPanel(id);
    }

    private void ToggleDock(PanelDock dock)
    {
        if (!_panelLayout.HiddenDocks.Remove(dock))
        {
            _panelLayout.HiddenDocks.Add(dock);
        }

        ApplyPanelLayout();
    }

    /// <summary>「パネルの配置を元に戻す」(UI-05 の仕様 8)。</summary>
    private void ResetPanelLayout()
    {
        _panelLayout.ResetToDefault(PanelRegistry.Defaults);
        ApplyPanelLayout();
    }

    private void ResizeDock(PanelDock dock, double delta)
    {
        double available = dock == PanelDock.Bottom ? WorkArea.ActualHeight - 120 : WorkArea.ActualWidth - 320;
        switch (dock)
        {
            case PanelDock.Left:
                _panelLayout.LeftWidth = PanelLayout.Clamp(_panelLayout.LeftWidth + delta, available - (RightPanel.Visibility == Visibility.Visible ? _panelLayout.RightWidth : 0));
                break;
            case PanelDock.Right:
                _panelLayout.RightWidth = PanelLayout.Clamp(_panelLayout.RightWidth + delta, available - (LeftPanel.Visibility == Visibility.Visible ? _panelLayout.LeftWidth : 0));
                break;
            default:
                _panelLayout.BottomHeight = PanelLayout.Clamp(_panelLayout.BottomHeight + delta, available);
                break;
        }

        ApplyPanelLayout();
    }

    /// <summary>
    /// パネルの中身。ウィンドウ (XamlRoot) をまたいで要素を移せないため、浮動パネルの中身は別に作る
    /// (メインウィンドウと浮動パネルの間を移るたびに Factory をもう一度呼ぶ)。
    /// </summary>
    private FrameworkElement? ContentOf(PanelRegistration reg, bool floating = false)
    {
        if (reg.RequiresDocument && Vm.Selected is null)
        {
            return null;
        }

        string key = floating ? reg.Id + "|floating" : reg.Id;
        if (!_panelContents.TryGetValue(key, out FrameworkElement? content))
        {
            content = reg.Factory(_panelContext);
            _panelContents[key] = content;
        }

        return content;
    }

    /// <summary>配置をそれぞれの場所に反映し、保存する。</summary>
    private void ApplyPanelLayout()
    {
        // まず各場所に置く中身を決め、移る中身を元の場所から外してから置く。
        var plans = new List<(PanelDock Dock, PanelDockArea Area, List<PanelRegistration> Tabs, bool Shown, string? Active, FrameworkElement? Content)>();
        foreach ((PanelDock dock, PanelDockArea area) in DockAreas)
        {
            var tabs = _panelLayout.VisibleIn(dock).Select(p => PanelRegistry.Find(p.Id)).OfType<PanelRegistration>().ToList();
            bool shown = tabs.Count > 0 && !_panelLayout.HiddenDocks.Contains(dock);
            string? active = shown && _panelLayout.ActiveTab.TryGetValue(dock, out string? a) && tabs.Any(t => t.Id == a) ? a : tabs.FirstOrDefault()?.Id;
            FrameworkElement? content = shown && active is not null ? ContentOf(tabs.First(t => t.Id == active)) : null;
            plans.Add((dock, area, tabs, shown, active, content));
            area.DetachContent(content);
        }

        foreach ((PanelDock dock, PanelDockArea area, List<PanelRegistration> tabs, bool shown, string? active, FrameworkElement? content) in plans)
        {
            if (!shown && area.Visibility == Visibility.Visible)
            {
                area.Show([], null, null);
            }

            area.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown)
            {
                area.Show([.. tabs.Select(t => new PanelTab(t.Id, PanelTitle(t)))], active, content);
            }

            Splitter splitter = dock switch { PanelDock.Left => LeftSplitter, PanelDock.Right => RightSplitter, _ => BottomSplitter };
            splitter.Visibility = area.Visibility;
            switch (dock)
            {
                case PanelDock.Left:
                    LeftColumn.Width = shown ? new GridLength(_panelLayout.LeftWidth) : GridLength.Auto;
                    break;
                case PanelDock.Right:
                    RightColumn.Width = shown ? new GridLength(_panelLayout.RightWidth) : GridLength.Auto;
                    break;
                default:
                    BottomRow.Height = shown ? new GridLength(_panelLayout.BottomHeight) : GridLength.Auto;
                    break;
            }
        }

        ApplyFloatingPanels();
        SyncHashTarget();
        SavePanelLayout();
        RefreshCommandUi();
    }

    private void ApplyFloatingPanels()
    {
        var floating = _panelLayout.VisibleIn(PanelDock.Floating).Where(p => PanelRegistry.Find(p.Id) is not null).ToList();
        foreach ((string id, FloatingPanelWindow window) in _floatingPanels.ToList())
        {
            if (floating.All(p => p.Id != id))
            {
                if (_panelLayout.Find(id) is { } placement)
                {
                    placement.FloatingBounds = window.ScreenBounds;
                }

                _floatingPanels.Remove(id);
                _panelContents.Remove(id + "|floating");
                window.Area.Show([], null, null);
                window.Close();
            }
        }

        foreach (PanelPlacement p in floating)
        {
            PanelRegistration reg = PanelRegistry.Find(p.Id)!;
            if (!_floatingPanels.TryGetValue(p.Id, out FloatingPanelWindow? window))
            {
                window = new FloatingPanelWindow(this, p.Id, PanelTitle(reg));
                WireArea(window.Area);
                string id = p.Id;
                window.Closed += (_, _) =>
                {
                    // 利用者が浮動パネルを閉じたら非表示にする (メインウィンドウと一緒に閉じるときは配置を保つ)。
                    if (!_windowClosing && _floatingPanels.Remove(id))
                    {
                        _panelContents.Remove(id + "|floating");
                        if (_panelLayout.Find(id) is { } placement)
                        {
                            placement.FloatingBounds = window.ScreenBounds;
                        }

                        _panelLayout.Hide(id);
                        ApplyPanelLayout();
                    }
                };
                _floatingPanels[p.Id] = window;
                window.Place(PanelLayout.EnsureOnScreen(p.FloatingBounds ?? DefaultFloatingBounds(), WorkAreas(), MainBounds()));
                window.ShowWindow();
            }

            window.Area.Show([new PanelTab(reg.Id, PanelTitle(reg))], reg.Id, ContentOf(reg, floating: true));
        }
    }

    private static string PanelTitle(PanelRegistration reg) => reg.Title ?? Loc.Get(reg.TitleKey);

    private PanelBounds MainBounds() => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private PanelBounds DefaultFloatingBounds()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        int w = (int)(320 * scale), h = (int)(480 * scale);
        PanelBounds main = MainBounds();
        return new PanelBounds(main.Right - w - (int)(24 * scale), main.Y + (int)(96 * scale), w, h);
    }

    private static List<PanelBounds> WorkAreas()
    {
        // DisplayArea.FindAll の結果は列挙すると InvalidCastException になる (Windows App SDK の既知の問題) ので、添字で読む。
        IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
        var list = new List<PanelBounds>();
        for (int i = 0; i < areas.Count; i++)
        {
            Windows.Graphics.RectInt32 w = areas[i].WorkArea;
            list.Add(new PanelBounds(w.X, w.Y, w.Width, w.Height));
        }

        return list;
    }

    private void SavePanelLayout()
    {
        foreach ((string id, FloatingPanelWindow window) in _floatingPanels)
        {
            if (_panelLayout.Find(id) is { } placement)
            {
                placement.FloatingBounds = window.ScreenBounds;
            }
        }

        CommandService.State?.Set(LastPanelLayoutKey, _panelLayout.ToJson());
    }

    private void FocusPanel(string id)
    {
        if (_floatingPanels.ContainsKey(id))
        {
            return;
        }

        foreach ((PanelDock _, PanelDockArea area) in DockAreas)
        {
            if (area.ActivePanel == id && area.Visibility == Visibility.Visible)
            {
                DispatcherQueue.TryEnqueue(() => area.FocusHeader());
                return;
            }
        }
    }

    /// <summary>ドラッグ中は、空の場所も含めてすべての場所をドロップ先として示す。</summary>
    private void ShowDropTargets(bool dragging)
    {
        foreach ((PanelDock dock, PanelDockArea area) in DockAreas)
        {
            if (dragging && area.Visibility == Visibility.Collapsed)
            {
                area.Visibility = Visibility.Visible;
                if (dock == PanelDock.Bottom)
                {
                    BottomRow.Height = new GridLength(PanelLayout.MinSize);
                }
                else
                {
                    (dock == PanelDock.Left ? LeftColumn : RightColumn).Width = new GridLength(PanelLayout.MinSize);
                }
            }

            area.ShowDropTarget(dragging);
        }

        if (!dragging)
        {
            ApplyPanelLayout();
        }
    }

    /// <summary>パネルのある場所 (テスト用の命令と F6 の領域の移動に使う)。</summary>
    public string? PanelLocation(string id) =>
        _panelLayout.Find(id) is { Visible: true } p && PanelRegistry.Find(id) is not null ? PanelLayout.DockName(p.Dock) : null;
}
