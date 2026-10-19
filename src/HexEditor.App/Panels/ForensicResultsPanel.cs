using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.FileSystems.Partitions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Panels;

/// <summary>
/// フォレンジックの結果一覧パネル: 失われたパーティションの候補 (FOR-08 の仕様 4)、$LogFile のログレコード (FOR-10 の仕様 9)、クラスタの持ち主 (FOR-15 の仕様 3)、
/// ファイルの検索・クラスタチェーン (FOR-09 の仕様 7、FOR-11)。行を選ぶとその位置へジャンプする。
/// </summary>
public sealed partial class ForensicResultsPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly ComboBox _kind = new() { MinWidth = 200 };
    private readonly TextBlock _header = new() { Margin = new Thickness(12, 0, 8, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };
    private FileSystemDocumentState? _state;
    private DocumentViewModel? _document;
    private List<ForensicList> _lists = [];
    private ForensicList? _shown;
    private int _shownVersion = -1;
    private bool _updating;

    public ForensicResultsPanel(PanelContext context)
    {
        _context = context;
        var mono = new FontFamily("Cascadia Mono, Consolas");
        _list.FontFamily = mono;
        _header.FontFamily = mono;
        _header.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        AutomationProperties.SetAutomationId(_list, "ForensicResults_List");
        AutomationProperties.SetName(_list, Loc.Get("Panel_ForensicResults_Title"));
        AutomationProperties.SetAutomationId(_kind, "ForensicResults_Kind");
        AutomationProperties.SetName(_kind, Loc.Get("ForensicResults_Kind"));
        AutomationProperties.SetAutomationId(_status, "ForensicResults_Status");
        AutomationProperties.SetAutomationId(_header, "ForensicResults_Header");
        _list.SelectionChanged += (_, _) => JumpToSelected();
        _list.ContextFlyout = BuildMenu();
        _kind.SelectionChanged += (_, _) =>
        {
            if (!_updating && _state is not null && _kind.SelectedIndex >= 0 && _kind.SelectedIndex < _lists.Count)
            {
                _state.ActiveList = _lists[_kind.SelectedIndex].Kind;
                Refresh();
            }
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4), Spacing = 8 };
        bar.Children.Add(_kind);
        grid.Children.Add(bar);
        Grid.SetRow(_status, 1);
        grid.Children.Add(_status);
        var headerScroll = new ScrollViewer { Content = _header, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(headerScroll, 2);
        grid.Children.Add(headerScroll);
        var scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 3);
        grid.Children.Add(scroll);
        Content = grid;
        context.ActiveDocumentChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private MainWindow? Window => _context.Window as MainWindow;

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);

    internal ForensicList? Shown => _shown;

    internal IReadOnlyList<ForensicRow> SelectedRows => [.. _list.SelectedItems.OfType<ForensicRow>()];

    internal void SelectRows(IEnumerable<int> indexes)
    {
        _list.SelectedItems.Clear();
        foreach (int i in indexes)
        {
            if (_shown is { } s && i >= 0 && i < s.Rows.Count)
            {
                _list.SelectedItems.Add(s.Rows[i]);
            }
        }
    }

    private void Attach()
    {
        Detach();
        _document = _context.ActiveDocument;
        _state = _document is null ? null : FileSystemDocumentState.For(_document.Document);
        if (_state is not null)
        {
            _state.Changed += OnChanged;
        }

        _shown = null;
        Refresh();
    }

    private void Detach()
    {
        if (_state is not null)
        {
            _state.Changed -= OnChanged;
        }

        _state = null;
        _document = null;
    }

    private void OnChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        _updating = true;
        try
        {
            _lists = _state is null ? [] : [.. _state.Lists.Values];
            _kind.Items.Clear();
            foreach (ForensicList l in _lists)
            {
                _kind.Items.Add(l.Title);
            }

            ForensicList? active = _state?.ActiveList is { } k ? _lists.FirstOrDefault(l => l.Kind == k) : _lists.FirstOrDefault();
            _kind.SelectedIndex = active is null ? -1 : _lists.IndexOf(active);
            if (active is null)
            {
                _list.ItemsSource = null;
                _header.Text = string.Empty;
                _status.Text = Loc.Get("ForensicResults_Empty");
                _shown = null;
                return;
            }

            _header.Text = active.Header;
            _status.Text = active.Status;
            if (!ReferenceEquals(active, _shown) || active.Version != _shownVersion)
            {
                _shown = active;
                _shownVersion = active.Version;
                _list.ItemsSource = null;
                _list.ItemsSource = active.Rows.ToList();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void JumpToSelected()
    {
        if (_list.SelectedItems.Count == 1 && _list.SelectedItem is ForensicRow { Offset: long offset } && _document is { } d)
        {
            d.Editor.GoTo(Math.Clamp(offset, 0, Math.Max(0, d.Document.Length - 1)));
        }
    }

    private IReadOnlyList<LostPartitionCandidate> SelectedCandidates => [.. SelectedRows.Select(r => r.Item).OfType<LostPartitionCandidate>()];

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("LostScan_Menu_Open", () => Window?.OpenCandidate(_document!, SelectedCandidates[0])));
        menu.Items.Add(Item("LostScan_Menu_ShowFs", () => _ = Window?.ShowCandidateFileSystemAsync(_document!, SelectedCandidates[0])));
        menu.Items.Add(Item("LostScan_Menu_CreateMbr", () => Window?.CreatePartitionEntries(_document!, SelectedCandidates, gpt: false)));
        menu.Items.Add(Item("LostScan_Menu_CreateGpt", () => Window?.CreatePartitionEntries(_document!, SelectedCandidates, gpt: true)));
        menu.Items.Add(Item("Fs_Menu_OpenFile", () =>
        {
            foreach (FsEntry e in SelectedRows.Select(r => r.Item).OfType<FsEntry>().Take(16))
            {
                Window?.OpenFsEntryTab(_document!, e);
            }
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("ForensicResults_Export", () => _ = ExportAsync()));
        menu.Opening += (_, _) =>
        {
            bool candidates = _shown?.Kind == ForensicListKind.LostPartitions;
            bool one = SelectedCandidates.Count == 1;
            bool writable = !WriteBlockMode.IsEnabled && _document is { Editor.ReadOnly: false };
            foreach (MenuFlyoutItem item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string id = AutomationProperties.GetAutomationId(item);
                (bool visible, bool enabled) = id switch
                {
                    "LostScan_Menu_Open" or "LostScan_Menu_ShowFs" => (candidates, one),
                    "LostScan_Menu_CreateMbr" or "LostScan_Menu_CreateGpt" => (candidates, SelectedCandidates.Count > 0 && writable),
                    "Fs_Menu_OpenFile" => (_shown?.Kind is ForensicListKind.FileSearch or ForensicListKind.Owners, SelectedRows.Any(r => r.Item is FsEntry)),
                    _ => (true, _shown?.Rows.Count > 0),
                };
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = enabled;
                if (id.StartsWith("LostScan_Menu_Create", StringComparison.Ordinal) && WriteBlockMode.IsEnabled)
                {
                    ToolTipService.SetToolTip(item, Loc.Get("WriteBlock_Enabled"));
                }
            }
        };
        return menu;
    }

    private static MenuFlyoutItem Item(string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        AutomationProperties.SetAutomationId(item, key);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>一覧を CSV / JSON に書き出す (FOR-08 の仕様 5)。</summary>
    private Task ExportAsync()
    {
        if (_shown is not { } list || Window is not { } w)
        {
            return Task.CompletedTask;
        }

        if (list.Kind == ForensicListKind.LostPartitions)
        {
            LostPartitionCandidate[] items = [.. list.Rows.Select(r => r.Item).OfType<LostPartitionCandidate>()];
            return w.ExportListAsync("partitions.csv", json => json ? LostPartitionScanner.ToJson(items) : LostPartitionScanner.ToCsv(items));
        }

        if (list.Rows.Any(r => r.Item is FsEntry))
        {
            FsEntry[] entries = [.. list.Rows.Select(r => r.Item).OfType<FsEntry>()];
            return w.ExportListAsync("files.csv", json => json ? FsListExport.ToJson(entries) : FsListExport.ToCsv(entries));
        }

        return w.ExportListAsync("results.csv", json => json
            ? System.Text.Json.JsonSerializer.Serialize(list.Rows.Select(r => r.Text))
            : list.Header + "\r\n" + string.Join("\r\n", list.Rows.Select(r => r.Text)));
    }
}
