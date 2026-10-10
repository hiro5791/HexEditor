using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Processes;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Panels;

/// <summary>
/// メモリマップのパネル (ENG-33): 領域 (開始・終了・サイズ・状態・保護属性・種類・名前) とモジュール (名前・ベース・サイズ・パス) の一覧。
/// 並べ替え・絞り込み・「空き」の表示の切り替え (仕様 2)、ダブルクリック / Enter でジャンプ、右クリックメニュー (仕様 3)、
/// カーソルのある領域の強調 (仕様 4)、一定の間隔と再読み込みでの更新 (選択とスクロール位置を保つ。仕様 6)。
/// </summary>
public sealed partial class MemoryMapPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _regions = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _modules = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ComboBox _tab = new();
    private readonly TextBox _filter = new();
    private readonly CheckBox _showFree = new();
    private readonly ComboBox _sort = new();
    private readonly CheckBox _descending = new();
    private readonly TextBlock _empty = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer? _timer;
    private List<MemoryMapRow> _allRows = [];
    private List<RegionRow> _shownRows = [];
    private EditorState? _editor;
    private DocumentViewModel? _document;
    private bool _refreshing;

    public MemoryMapPanel(PanelContext context)
    {
        _context = context;
        var mono = new FontFamily("Cascadia Mono, Consolas");
        _regions.FontFamily = mono;
        _modules.FontFamily = mono;
        _regions.DisplayMemberPath = nameof(RegionRow.Text);
        _modules.DisplayMemberPath = nameof(ModuleRow.Text);
        _regions.DoubleTapped += (_, _) => JumpToSelectedRegion();
        _regions.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                JumpToSelectedRegion();
                e.Handled = true;
            }
        };
        _modules.DoubleTapped += (_, _) => JumpToSelectedModule();
        _modules.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                JumpToSelectedModule();
                e.Handled = true;
            }
        };
        _regions.ContextFlyout = BuildRegionMenu();
        AutomationProperties.SetAutomationId(_regions, "MemoryMap_Regions");
        AutomationProperties.SetAutomationId(_modules, "MemoryMap_Modules");
        AutomationProperties.SetName(_regions, Loc.Get("MemoryMap_RegionsTab"));
        AutomationProperties.SetName(_modules, Loc.Get("MemoryMap_ModulesTab"));

        _tab.Items.Add(Loc.Get("MemoryMap_RegionsTab"));
        _tab.Items.Add(Loc.Get("MemoryMap_ModulesTab"));
        _tab.SelectedIndex = 0;
        _tab.SelectionChanged += (_, _) => UpdateTabVisibility();
        AutomationProperties.SetName(_tab, Loc.Get("MemoryMap_View"));
        AutomationProperties.SetAutomationId(_tab, "MemoryMap_Tab");

        _filter.PlaceholderText = Loc.Get("MemoryMap_Filter");
        _filter.MinWidth = 140;
        AutomationProperties.SetName(_filter, Loc.Get("MemoryMap_Filter"));
        AutomationProperties.SetAutomationId(_filter, "MemoryMap_Filter");
        _filter.TextChanged += (_, _) => ApplyFilter(keepSelection: true);

        // 「空き」の領域は既定で隠す (仕様 2)。
        _showFree.Content = Loc.Get("MemoryMap_ShowFree");
        _showFree.IsChecked = false;
        AutomationProperties.SetAutomationId(_showFree, "MemoryMap_ShowFree");
        _showFree.Click += (_, _) => ApplyFilter(keepSelection: true);

        foreach (MemoryMapSort sort in Enum.GetValues<MemoryMapSort>())
        {
            _sort.Items.Add(Loc.Get("MemoryMap_Sort_" + sort));
        }

        _sort.SelectedIndex = 0;
        AutomationProperties.SetName(_sort, Loc.Get("MemoryMap_SortBy"));
        AutomationProperties.SetAutomationId(_sort, "MemoryMap_Sort");
        _sort.SelectionChanged += (_, _) => ApplyFilter(keepSelection: true);
        _descending.Content = Loc.Get("MemoryMap_Descending");
        AutomationProperties.SetAutomationId(_descending, "MemoryMap_Descending");
        _descending.Click += (_, _) => ApplyFilter(keepSelection: true);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 8, 8, 4) };
        bar.Children.Add(_tab);
        bar.Children.Add(_filter);
        bar.Children.Add(_sort);
        bar.Children.Add(_descending);
        bar.Children.Add(_showFree);
        var scroller = new ScrollViewer
        {
            Content = bar,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroller, 0);
        grid.Children.Add(scroller);
        Grid.SetRow(_regions, 1);
        Grid.SetRow(_modules, 1);
        Grid.SetRow(_empty, 1);
        grid.Children.Add(_regions);
        grid.Children.Add(_modules);
        grid.Children.Add(_empty);
        Content = grid;

        context.ActiveDocumentChanged += OnActiveDocumentChanged;

        // 更新の間隔 (仕様 6: 既定 5 秒、設定で 1〜60 秒またはオフ)。パネルが表示されている間だけ動かす。
        _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        int seconds = Math.Clamp(App.Settings?.GetInt(DeviceSettings.MemoryMapRefreshKey, 5) ?? 5, 0, 60);
        if (_timer is not null && seconds > 0)
        {
            _timer.Interval = TimeSpan.FromSeconds(seconds);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => _ = RefreshAsync(requery: true);
        }

        Loaded += (_, _) =>
        {
            AttachDocument();
            _ = RefreshAsync(requery: false);
            if (seconds > 0)
            {
                _timer?.Start();
            }
        };
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            DetachDocument();
        };
    }

    public bool FocusContent() => _tab.SelectedIndex == 1 ? _modules.Focus(FocusState.Programmatic) : _regions.Focus(FocusState.Programmatic);

    /// <summary>表示している領域の行 (テスト用)。</summary>
    internal IReadOnlyList<string> RegionTexts => [.. _shownRows.Select(r => r.Text)];

    private void OnActiveDocumentChanged(object? sender, EventArgs e)
    {
        DetachDocument();
        AttachDocument();
        _ = RefreshAsync(requery: false);
    }

    private void AttachDocument()
    {
        _document = _context.ActiveDocument;
        if (_document is null)
        {
            return;
        }

        _document.Document.Changed += OnDocumentChanged;
        _document.EditorChanged += OnEditorChanged;
        AttachEditor();
    }

    private void DetachDocument()
    {
        if (_document is not null)
        {
            _document.Document.Changed -= OnDocumentChanged;
            _document.EditorChanged -= OnEditorChanged;
        }

        DetachEditor();
        _document = null;
    }

    private void AttachEditor()
    {
        _editor = _document?.Editor;
        if (_editor is not null)
        {
            _editor.Changed += OnCursorChanged;
        }
    }

    private void DetachEditor()
    {
        if (_editor is not null)
        {
            _editor.Changed -= OnCursorChanged;
        }

        _editor = null;
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        DetachEditor();
        AttachEditor();
    }

    /// <summary>再読み込み (ENG-18) のときも一覧を取り直す (仕様 6)。</summary>
    private void OnDocumentChanged(object? sender, Core.Engine.DocumentChangedEventArgs e)
    {
        if (e.Kind == Core.Engine.DocumentChangeKind.Reloaded)
        {
            DispatcherQueue.TryEnqueue(() => _ = RefreshAsync(requery: true));
        }
    }

    /// <summary>カーソルのある領域を一覧で強調する (仕様 4)。一覧にフォーカスがある (利用者が選んでいる) 間は動かさない。</summary>
    private void OnCursorChanged(object? sender, EventArgs e)
    {
        if (_editor is null || _regions.FocusState != FocusState.Unfocused || _shownRows.Count == 0)
        {
            return;
        }

        int index = MemoryMapModel.IndexOfOffset([.. _shownRows.Select(r => r.Row)], _editor.Cursor);
        if (index >= 0 && !ReferenceEquals(_regions.SelectedItem, _shownRows[index]))
        {
            _regions.SelectedItem = _shownRows[index];
            _regions.ScrollIntoView(_shownRows[index]);
        }
    }

    private void UpdateTabVisibility()
    {
        bool regions = _tab.SelectedIndex == 0;
        bool hasData = _empty.Visibility == Visibility.Collapsed;
        _regions.Visibility = regions && hasData ? Visibility.Visible : Visibility.Collapsed;
        _modules.Visibility = !regions && hasData ? Visibility.Visible : Visibility.Collapsed;
        _filter.IsEnabled = regions;
        _sort.IsEnabled = regions;
        _descending.IsEnabled = regions;
        _showFree.IsEnabled = regions;
    }

    /// <summary>
    /// 一覧を作り直す。<paramref name="requery"/> なら領域の一覧をプロセスから取り直す (バックグラウンドで。数万の領域でも UI を止めない)。
    /// 取得できなければパネルに理由を示し、ドキュメントは開いたままにする (ENG-33 の「エラー」)。
    /// </summary>
    private async Task RefreshAsync(bool requery)
    {
        if (_refreshing)
        {
            return;
        }

        DocumentViewModel? doc = _context.ActiveDocument;
        if (doc?.ProcessMemory is null && doc?.Snapshot is null)
        {
            ShowEmpty(Loc.Get("MemoryMap_NoProcess"));
            return;
        }

        _refreshing = true;
        try
        {
            List<MemoryMapRow> rows;
            IReadOnlyList<ProcessModule> modules;
            if (doc.ProcessMemory is { } process)
            {
                if (requery && !process.HasExited)
                {
                    try
                    {
                        await Task.Run(process.RefreshRegions);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        if (process.MemoryRegions.Count == 0)
                        {
                            ShowEmpty(Loc.Format("MemoryMap_Error", ex.Message));
                            return;
                        }
                    }
                }

                if (!ReferenceEquals(doc, _context.ActiveDocument))
                {
                    return;
                }

                rows = MemoryMapModel.Build(process.MemoryRegions, process.Modules, process.BaseAddress, process.Length);
                modules = process.Modules;
            }
            else
            {
                SnapshotByteSource snapshot = doc.Snapshot!;
                rows = MemoryMapModel.Build(snapshot.Metadata.Regions, 0);
                modules = snapshot.Modules;
            }

            _empty.Visibility = Visibility.Collapsed;
            _allRows = rows;
            ApplyFilter(keepSelection: true);
            object? selectedModule = (_modules.SelectedItem as ModuleRow)?.Address;
            var moduleRows = modules.Select(m => new ModuleRow(m.BaseAddress, FormatModule(m))).ToList();
            _modules.ItemsSource = moduleRows;
            if (selectedModule is long address)
            {
                _modules.SelectedItem = moduleRows.FirstOrDefault(r => r.Address == address);
            }

            UpdateTabVisibility();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowEmpty(string text)
    {
        _empty.Text = text;
        _empty.Visibility = Visibility.Visible;
        _allRows = [];
        _shownRows = [];
        _regions.ItemsSource = null;
        _modules.ItemsSource = null;
        UpdateTabVisibility();
    }

    /// <summary>絞り込み・並べ替えを当てて一覧を作り直す。選んでいた行とスクロール位置 (先頭に見えている行) を保つ (仕様 6)。</summary>
    private void ApplyFilter(bool keepSelection)
    {
        long? selected = keepSelection ? (_regions.SelectedItem as RegionRow)?.Row.Start : null;
        long? firstVisible = keepSelection ? FirstVisibleStart() : null;
        List<MemoryMapRow> filtered = MemoryMapModel.Filter(_allRows, _filter.Text, _showFree.IsChecked == true,
            (MemoryMapSort)Math.Max(0, _sort.SelectedIndex), _descending.IsChecked == true, StateName);
        _shownRows = [.. filtered.Select(r => new RegionRow(r, FormatRegion(r)))];
        _regions.ItemsSource = _shownRows;
        if (selected is long start && _shownRows.FirstOrDefault(r => r.Row.Start == start) is { } again)
        {
            _regions.SelectedItem = again;
        }

        if (firstVisible is long top && _shownRows.FirstOrDefault(r => r.Row.Start == top) is { } topRow)
        {
            _regions.ScrollIntoView(topRow, ScrollIntoViewAlignment.Leading);
        }
    }

    /// <summary>一覧の先頭に見えている行の開始アドレス。</summary>
    private long? FirstVisibleStart()
    {
        if (_regions.ItemsPanelRoot is ItemsStackPanel panel && panel.FirstVisibleIndex >= 0 && panel.FirstVisibleIndex < _shownRows.Count)
        {
            return _shownRows[panel.FirstVisibleIndex].Row.Start;
        }

        return null;
    }

    // ---- 操作 (仕様 3) ----

    private MenuFlyout BuildRegionMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("MemoryMap_SelectRegion", SelectRegion));
        menu.Items.Add(MenuItem("MemoryMap_OpenInNewTab", OpenRegionInNewTab));
        menu.Items.Add(MenuItem("MemoryMap_CopyAddress", CopyAddress));
        menu.Opening += (_, _) =>
        {
            bool has = _regions.SelectedItem is RegionRow;
            foreach (MenuFlyoutItemBase item in menu.Items)
            {
                if (item is MenuFlyoutItem m)
                {
                    m.IsEnabled = has;
                }
            }
        };
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        AutomationProperties.SetAutomationId(item, key);
        item.Click += (_, _) => action();
        return item;
    }

    private void JumpToSelectedRegion()
    {
        if (_regions.SelectedItem is RegionRow row && _context.ActiveDocument is { } doc)
        {
            doc.Editor.GoTo(Math.Clamp(row.Row.Offset, 0, Math.Max(0, doc.Document.Length - 1)));
        }
    }

    /// <summary>領域を選択 (仕様 3)。</summary>
    internal void SelectRegion()
    {
        if (_regions.SelectedItem is RegionRow row && _context.ActiveDocument is { } doc)
        {
            doc.Editor.Select(row.Row.Offset, Math.Min(row.Row.Size, doc.Document.Length - row.Row.Offset));
        }
    }

    /// <summary>この領域を新しいタブで開く (仕様 3、ENG-39)。</summary>
    internal void OpenRegionInNewTab()
    {
        if (_regions.SelectedItem is RegionRow row && _context.ActiveDocument is { } doc && _context.Window is MainWindow window)
        {
            long length = Math.Min(row.Row.Size, doc.Document.Length - row.Row.Offset);
            string name = $"0x{row.Row.Start:X}" + (row.Row.Name is { Length: > 0 } n ? $" {n}" : string.Empty);
            window.OpenRangeInNewTab(doc, row.Row.Offset, length, name, copy: false);
        }
    }

    /// <summary>アドレスをコピー (仕様 3): 開始アドレスを 16 進 (0x 付き) で。</summary>
    private void CopyAddress()
    {
        if (_regions.SelectedItem is RegionRow row)
        {
            var package = new DataPackage();
            package.SetText(AddressText(row.Row.Start));
            SystemClipboard.SetContent(package);
        }
    }

    /// <summary>テスト用: 開始アドレスの行を選ぶ。</summary>
    internal bool SelectRowAt(long address)
    {
        RegionRow? row = _shownRows.FirstOrDefault(r => r.Row.Start == address);
        _regions.SelectedItem = row;
        return row is not null;
    }

    private void JumpToSelectedModule()
    {
        if (_modules.SelectedItem is ModuleRow row && _context.ActiveDocument is { } doc)
        {
            long baseAddress = doc.ProcessMemory?.BaseAddress ?? doc.Snapshot?.BaseAddress ?? 0;
            doc.Editor.GoTo(Math.Max(0, row.Address - baseAddress));
        }
    }

    // ---- 表示 ----

    private static string AddressText(long address) => "0x" + address.ToString("X", CultureInfo.InvariantCulture);

    private static string StateName(RegionState state) => Loc.Get("MemoryMap_State_" + state);

    private static string TypeName(RegionType type) => type == RegionType.None ? string.Empty : Loc.Get("MemoryMap_Type_" + type);

    private static string FormatRegion(MemoryMapRow row)
    {
        string size = StatusFormat.ShortSize(row.Size, CultureInfo.CurrentCulture) ?? row.Size.ToString("N0", CultureInfo.CurrentCulture);
        string start = row.Start.ToString("X12", CultureInfo.InvariantCulture);
        string end = (row.End - 1).ToString("X12", CultureInfo.InvariantCulture);
        return $"{start}-{end}  {size,9}  {StateName(row.State),-8}  {row.ProtectText,-6}  {TypeName(row.Type),-8}  {row.Name}".TrimEnd();
    }

    private static string FormatModule(ProcessModule module)
    {
        string size = StatusFormat.ShortSize(module.Size, CultureInfo.CurrentCulture) ?? module.Size.ToString("N0", CultureInfo.CurrentCulture);
        return $"{module.Name}  0x{module.BaseAddress:X}  {size}  {module.Path}".TrimEnd();
    }

    private sealed record RegionRow(MemoryMapRow Row, string Text);

    private sealed record ModuleRow(long Address, string Text);
}
