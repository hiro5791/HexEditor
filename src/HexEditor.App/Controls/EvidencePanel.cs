using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Time;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 証拠パネル (12 の FOR-03 の仕様 6)。開いているケースの証拠一覧を結果一覧で表示する。列は証拠番号、説明、種類、パス、容量、ハッシュ、検証結果、登録日時
/// (日時は FOR-05 の設定に従って UTC とローカル)。行のダブルクリックでその証拠を開く (読み取り専用)。Delete で一覧から外す。
/// </summary>
public sealed class EvidencePanel : UserControl, IPanelContent
{
    private readonly MainWindow _window;
    private readonly VirtualTable _table = new("Evidence_List");
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<EvidenceItem> _items = [];

    public EvidencePanel(MainWindow window)
    {
        _window = window;
        AutomationProperties.SetAutomationId(_summary, "Evidence_Summary");
        AutomationProperties.SetAutomationId(_empty, "Evidence_Empty");
        var add = new Button { Content = Loc.Get("Evidence_Add") };
        AutomationProperties.SetAutomationId(add, "Evidence_Add");
        add.Click += async (_, _) => await window.Commands.ExecuteAsync("forensics.case.addEvidence");
        var bar = new Grid { Padding = new Thickness(12, 6, 12, 6), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_summary);
        Grid.SetColumn(add, 1);
        bar.Children.Add(add);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        Grid.SetRow(_table, 1);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_table);
        root.Children.Add(_empty);
        Content = root;

        _table.RowProvider = RowOf;
        _table.Activated += async (_, i) => await OpenAsync(i);
        _table.DeleteRequested += (_, i) => Exclude(i);
        _table.BuildContextMenu = BuildMenu;
        ForensicsService.CaseChanged += OnChanged;
        ForensicsService.EvidenceChanged += OnChanged;
        ForensicsService.TimeDisplayChanged += OnChanged;
        Unloaded += (_, _) =>
        {
            ForensicsService.CaseChanged -= OnChanged;
            ForensicsService.EvidenceChanged -= OnChanged;
            ForensicsService.TimeDisplayChanged -= OnChanged;
        };
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>テスト用: 今の行。</summary>
    internal IReadOnlyList<EvidenceItem> Items => _items;

    /// <summary>テスト用: 表の列の見出し。</summary>
    internal IReadOnlyList<string> ColumnHeaders { get; private set; } = [];

    internal IReadOnlyList<string> Cells(int index) => RowOf(index);

    public void Refresh()
    {
        ForensicCase? c = ForensicsService.Cases.CurrentCase;
        _items = c?.Evidence ?? [];
        ForensicTimeContext time = ForensicsService.TimeContext;
        var columns = new List<VirtualColumn>
        {
            new(Loc.Get("Evidence_Col_Id"), 70, true), new(Loc.Get("Evidence_Col_Description"), 160), new(Loc.Get("Evidence_Col_Type"), 80),
            new(Loc.Get("Evidence_Col_Path"), 240, true), new(Loc.Get("Evidence_Col_Capacity"), 120, true), new(Loc.Get("Evidence_Col_Hash"), 520, true),
            new(Loc.Get("Evidence_Col_Verification"), 100),
        };
        foreach (TimeColumn column in time.Columns)
        {
            columns.Add(new VirtualColumn(Loc.Get(column == TimeColumn.Utc ? "Evidence_Col_AddedUtc" : "Evidence_Col_AddedLocal"), 210, true));
        }

        ColumnHeaders = [.. columns.Select(x => x.Header)];
        _table.SetColumns(columns);
        _table.Refresh(_items.Count);
        _summary.Text = c is null ? string.Empty : Loc.Format("Evidence_Summary", c.Info.Number, c.Info.Name, _items.Count);
        _empty.Text = c is null ? Loc.Get("Evidence_NoCase") : Loc.Get("Evidence_None");
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _table.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private IReadOnlyList<string> RowOf(long index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return [];
        }

        EvidenceItem e = _items[(int)index];
        ForensicTimeContext time = ForensicsService.TimeContext;
        string invalid = Loc.Get("Time_Invalid");
        ForensicTimestamp added = ForensicTimestamp.FromUtc(DateTime.SpecifyKind(e.AddedUtc, DateTimeKind.Utc));
        var cells = new List<string>
        {
            e.Id, e.Description, Loc.Get("Evidence_Kind_" + e.Kind), e.Path, e.CapacityBytes is { } cap ? cap.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : string.Empty,
            e.Hashes.Count == 0 ? Loc.Get("Evidence_HashNotComputed") : string.Join("  ", e.Hashes.Select(h => $"{h.Algorithm}: {h.Value}")),
            Loc.Get("Evidence_Verification_" + (e.Verification ?? "none")),
        };
        foreach (TimeColumn column in time.Columns)
        {
            cells.Add(time.Cell(added, column, invalid));
        }

        return cells;
    }

    private void BuildMenu(long index, MenuFlyout menu)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        EvidenceItem e = _items[(int)index];
        var open = new MenuFlyoutItem { Text = Loc.Get("Evidence_Menu_Open") };
        open.Click += async (_, _) => await OpenAsync(index);
        var remove = new MenuFlyoutItem { Text = Loc.Get("Evidence_Menu_Remove") };
        remove.Click += (_, _) => Exclude(index);
        var copyHash = new MenuFlyoutItem { Text = Loc.Get("Evidence_Menu_CopyHash"), IsEnabled = e.Hashes.Count > 0 };
        copyHash.Click += (_, _) => Tools.ToolParts.Copy(string.Join(Environment.NewLine, e.Hashes.Select(h => $"{h.Algorithm}: {h.Value}")));
        var copyPath = new MenuFlyoutItem { Text = Loc.Get("Evidence_Menu_CopyPath") };
        copyPath.Click += (_, _) => Tools.ToolParts.Copy(e.Path);
        menu.Items.Add(open);
        menu.Items.Add(remove);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(copyHash);
        menu.Items.Add(copyPath);
    }

    private async Task OpenAsync(long index)
    {
        if (index >= 0 && index < _items.Count)
        {
            await _window.OpenEvidenceAsync(_items[(int)index]);
        }
    }

    private void Exclude(long index)
    {
        if (index >= 0 && index < _items.Count)
        {
            _window.ExcludeEvidence(_items[(int)index]);
        }
    }

    /// <summary>テスト用: 行を選ぶ。</summary>
    internal void SelectRow(int index) => _table.Select(index);

    internal void ExcludeSelected() => Exclude(_table.SelectedIndex);

    public bool FocusContent() => _table.Focus(FocusState.Programmatic);
}
