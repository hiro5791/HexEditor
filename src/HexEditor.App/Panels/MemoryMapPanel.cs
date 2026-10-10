using System.Globalization;
using HexEditor.App.ViewModels;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Panels;

/// <summary>メモリマップのパネル (ENG-33): 領域とモジュールの一覧を表示し、選んだ領域にジャンプする。</summary>
public sealed partial class MemoryMapPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _regions = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _modules = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ComboBox _tab = new();
    private readonly TextBlock _empty = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8) };
    private readonly DispatcherQueueTimer? _timer;

    public MemoryMapPanel(PanelContext context)
    {
        _context = context;
        _regions.DisplayMemberPath = nameof(RegionRow.Text);
        _modules.DisplayMemberPath = nameof(ModuleRow.Text);
        _regions.DoubleTapped += (_, _) => JumpToSelectedRegion();
        _regions.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) JumpToSelectedRegion(); };
        _modules.DoubleTapped += (_, _) => JumpToSelectedModule();
        _modules.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) JumpToSelectedModule(); };
        AutomationProperties.SetAutomationId(_regions, "MemoryMap_Regions");
        AutomationProperties.SetAutomationId(_modules, "MemoryMap_Modules");
        AutomationProperties.SetName(_regions, Loc.Get("MemoryMap_RegionsTab"));
        AutomationProperties.SetName(_modules, Loc.Get("MemoryMap_ModulesTab"));

        _tab.Items.Add(Loc.Get("MemoryMap_RegionsTab"));
        _tab.Items.Add(Loc.Get("MemoryMap_ModulesTab"));
        _tab.SelectedIndex = 0;
        _tab.SelectionChanged += (_, _) => UpdateTabVisibility();
        AutomationProperties.SetName(_tab, Loc.Get("MemoryMap_RegionsTab"));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _tab.Margin = new Thickness(8, 8, 8, 4);
        Grid.SetRow(_tab, 0);
        grid.Children.Add(_tab);
        Grid.SetRow(_regions, 1);
        Grid.SetRow(_modules, 1);
        Grid.SetRow(_empty, 1);
        grid.Children.Add(_regions);
        grid.Children.Add(_modules);
        grid.Children.Add(_empty);
        Content = grid;

        context.ActiveDocumentChanged += (_, _) => Refresh();
        _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(5);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Refresh(keepSelection: true);
            _timer.Start();
        }

        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => _timer?.Stop();
    }

    public bool FocusContent() => _tab.SelectedIndex == 1 ? _modules.Focus(FocusState.Programmatic) : _regions.Focus(FocusState.Programmatic);

    private void UpdateTabVisibility()
    {
        bool regions = _tab.SelectedIndex == 0;
        _regions.Visibility = regions && _empty.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _modules.Visibility = !regions && _empty.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh(bool keepSelection = false)
    {
        DocumentViewModel? doc = _context.ActiveDocument;
        (IReadOnlyList<SourceRegion> map, IReadOnlyList<ProcessModule> modules, long baseAddress)? data = doc switch
        {
            { ProcessMemory: { } p } => (p.Regions, p.Modules, p.BaseAddress),
            { Snapshot: { } s } => (s.Regions, s.Modules, 0L),
            _ => null,
        };

        if (data is null)
        {
            _empty.Text = Loc.Get("MemoryMap_NoProcess");
            _empty.Visibility = Visibility.Visible;
            _regions.ItemsSource = null;
            _modules.ItemsSource = null;
            UpdateTabVisibility();
            return;
        }

        _empty.Visibility = Visibility.Collapsed;
        (IReadOnlyList<SourceRegion> regions, IReadOnlyList<ProcessModule> mods, long bas) = data.Value;
        object? selectedRegion = keepSelection ? (_regions.SelectedItem as RegionRow)?.Offset : null;

        // 「空き」の領域は既定で隠す (ENG-33 の仕様 2)。
        var rows = regions.Where(r => r.Access != RegionAccess.Unallocated || r.Label is not null)
            .Select(r => new RegionRow(r.Offset, bas + r.Offset, r, FormatRegion(r, bas))).ToList();
        _regions.ItemsSource = rows;
        _modules.ItemsSource = mods.Select(m => new ModuleRow(m.BaseAddress, FormatModule(m))).ToList();
        if (selectedRegion is long off)
        {
            _regions.SelectedItem = rows.FirstOrDefault(r => r.Offset == off);
        }

        UpdateTabVisibility();
    }

    private void JumpToSelectedRegion()
    {
        if (_regions.SelectedItem is RegionRow row && _context.ActiveDocument is { } doc)
        {
            doc.Editor.GoTo(row.Offset);
        }
    }

    private void JumpToSelectedModule()
    {
        if (_modules.SelectedItem is ModuleRow row && _context.ActiveDocument is { } doc)
        {
            long baseAddress = doc.ProcessMemory?.BaseAddress ?? doc.Snapshot?.BaseAddress ?? 0;
            doc.Editor.GoTo(Math.Max(0, row.Address - baseAddress));
        }
    }

    private static string FormatRegion(SourceRegion region, long baseAddress)
    {
        long address = baseAddress + region.Offset;
        string access = region.Access switch
        {
            RegionAccess.Readable => "R",
            RegionAccess.NoAccess => Loc.Get("MemoryMap_NoAccess"),
            _ => Loc.Get("MemoryMap_Unallocated"),
        };
        string size = StatusFormat.ShortSize(region.Length, CultureInfo.CurrentCulture) ?? region.Length.ToString("N0", CultureInfo.CurrentCulture);
        return $"0x{address:X}  {size}  {access}  {region.Label}".TrimEnd();
    }

    private static string FormatModule(ProcessModule module)
    {
        string size = StatusFormat.ShortSize(module.Size, CultureInfo.CurrentCulture) ?? module.Size.ToString("N0", CultureInfo.CurrentCulture);
        return $"{module.Name}  0x{module.BaseAddress:X}  {size}";
    }

    private sealed record RegionRow(long Offset, long Address, SourceRegion Region, string Text);

    private sealed record ModuleRow(long Address, string Text);
}
