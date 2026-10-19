using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Panels;

/// <summary>
/// パーティションパネル (FOR-06 の画面): 上段に配置図、中段に一覧、下段に選んだ行の詳細と警告。一覧の行・配置図の区画を選ぶと、先頭へ移動・選択・別タブで開く・
/// エントリの構造を表示・ファイルシステムを表示ができる。パーティション表が見つからなければ、失われたパーティションのスキャン (FOR-08) への案内を出す。
/// </summary>
public sealed partial class PartitionPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Grid _layout = new() { Height = 44, MinWidth = 120 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
    private readonly HyperlinkButton _scanLink = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 8), IsTextSelectionEnabled = true };
    private readonly TextBlock _header = new() { Margin = new Thickness(12, 0, 8, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly ListView _lvList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 160 };
    private readonly TextBlock _lvTitle = new() { Margin = new Thickness(8, 6, 8, 0) };
    private readonly TextBlock _lvStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 6), IsTextSelectionEnabled = true };
    private readonly StackPanel _lvSection = new() { Visibility = Visibility.Collapsed };
    private DocumentViewModel? _document;
    private FileSystemDocumentState? _state;
    private List<RowItem> _rows = [];
    private List<LvItem> _lvRows = [];

    public PartitionPanel(PanelContext context)
    {
        _context = context;
        var mono = new FontFamily("Cascadia Mono, Consolas");
        _list.FontFamily = mono;
        _list.DisplayMemberPath = nameof(RowItem.Text);
        AutomationProperties.SetAutomationId(_list, "Partitions_List");
        AutomationProperties.SetName(_list, Loc.Get("Panel_Partitions_Title"));
        _list.SelectionChanged += (_, _) => ShowDetails();
        _list.DoubleTapped += (_, _) => GoToStart();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                GoToStart();
                e.Handled = true;
            }
        };
        _list.ContextFlyout = BuildMenu();
        AutomationProperties.SetAutomationId(_status, "Partitions_Status");
        AutomationProperties.SetAutomationId(_details, "Partitions_Details");
        AutomationProperties.SetAutomationId(_layout, "Partitions_Layout");
        AutomationProperties.SetName(_layout, Loc.Get("Part_LayoutName"));
        _scanLink.Content = Loc.Get("Cmd_forensics_partitions_scan");
        AutomationProperties.SetAutomationId(_scanLink, "Partitions_ScanLink");
        _scanLink.Visibility = Visibility.Collapsed;
        _scanLink.Click += (_, _) => _ = Window?.ShowLostPartitionScanAsync(null);
        _header.Text = Loc.Get("Part_Header");
        _header.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _header.FontFamily = mono;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.5, GridUnitType.Star), MinHeight = 60 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel { Margin = new Thickness(8, 8, 8, 0) };
        top.Children.Add(new ScrollViewer
        {
            Content = _layout,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Grid.SetRow(top, 0);
        grid.Children.Add(top);
        var statusRow = new StackPanel { Orientation = Orientation.Vertical };
        statusRow.Children.Add(_status);
        statusRow.Children.Add(_scanLink);
        Grid.SetRow(statusRow, 1);
        grid.Children.Add(statusRow);
        var headerScroll = new ScrollViewer
        {
            Content = _header,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(headerScroll, 2);
        grid.Children.Add(headerScroll);
        var listScroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(listScroll, 3);
        grid.Children.Add(listScroll);
        var detailScroll = new ScrollViewer { Content = _details };
        Grid.SetRow(detailScroll, 4);
        grid.Children.Add(detailScroll);
        BuildLogicalVolumeSection(mono);
        Grid.SetRow(_lvSection, 5);
        grid.Children.Add(_lvSection);
        Content = grid;

        context.ActiveDocumentChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        _layout.SizeChanged += (_, _) => BuildLayout();
    }

    private MainWindow? Window => _context.Window as MainWindow;

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);

    private void Attach()
    {
        Detach();
        _document = _context.ActiveDocument;
        _state = _document is null ? null : FileSystemDocumentState.For(_document.Document);
        if (_state is not null)
        {
            _state.Changed += OnStateChanged;
        }

        Refresh();
    }

    /// <summary>今のドキュメントに結び付いていなければ結び直す (ドキュメントを閉じた直後に開いた場合など)。</summary>
    internal void EnsureAttached()
    {
        if (!ReferenceEquals(_context.ActiveDocument, _document))
        {
            Attach();
        }
        else
        {
            Refresh();
        }
    }

    private void Detach()
    {
        if (_state is not null)
        {
            _state.Changed -= OnStateChanged;
        }

        _state = null;
        _document = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>表示している行 (テスト用)。</summary>
    internal IReadOnlyList<PartitionRow> Rows => [.. _rows.Select(r => r.Row)];

    internal string StatusText => _status.Text;

    internal string DetailsText => _details.Text;

    internal bool ScanLinkVisible => _scanLink.Visibility == Visibility.Visible;

    /// <summary>テスト用: 番号 (未割り当ては 0 と開始 LBA) の行を選ぶ。</summary>
    internal bool SelectRow(int number, long? startLba = null)
    {
        RowItem? row = _rows.FirstOrDefault(r => r.Row.Number == number && (startLba is null || r.Row.StartLba == startLba));
        _list.SelectedItem = row;
        return row is not null;
    }

    internal PartitionRow? SelectedRow => (_list.SelectedItem as RowItem)?.Row;

    private void Refresh()
    {
        RefreshLogicalVolumes();
        PartitionAnalysis? analysis = _state?.Partitions;
        _scanLink.Visibility = Visibility.Collapsed;
        if (_document is null)
        {
            _status.Text = Loc.Get("Command_NoDocument");
            SetRows([]);
            return;
        }

        if (_state?.PartitionsRunning == true)
        {
            _status.Text = Loc.Get("Part_Analyzing");
            return;
        }

        if (analysis is null)
        {
            _status.Text = Loc.Get("Part_NotAnalyzed");
            SetRows([]);
            return;
        }

        if (analysis.NoTable)
        {
            _status.Text = Loc.Get("Part_NoTable");
            _scanLink.Visibility = Visibility.Visible;
        }
        else
        {
            _status.Text = Loc.Format("Part_Summary", analysis.Partitions.Count(r => !r.IsContainer), analysis.SectorSize,
                analysis.DiskSignature is uint sig ? $"0x{sig:X8}" : analysis.DiskGuid?.ToString("D") ?? "-");
        }

        if (analysis.UnreadableLbas.Count > 0)
        {
            _status.Text += Environment.NewLine + Loc.Format("Part_Unreadable", string.Join(", ", analysis.UnreadableLbas.Take(8)));
        }

        SetRows(analysis.Rows);
    }

    // ---- 論理ボリューム (FOR-07 の画面) ----

    private void BuildLogicalVolumeSection(FontFamily mono)
    {
        _lvTitle.Text = Loc.Get("Lv_Title");
        _lvTitle.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        _lvList.FontFamily = mono;
        _lvList.DisplayMemberPath = nameof(LvItem.Text);
        AutomationProperties.SetAutomationId(_lvList, "Partitions_LogicalVolumes");
        AutomationProperties.SetName(_lvList, Loc.Get("Lv_Title"));
        AutomationProperties.SetAutomationId(_lvStatus, "Partitions_LogicalVolumesStatus");
        _lvList.DoubleTapped += (_, _) => _ = OpenSelectedLogicalVolumeAsync();
        _lvList.SelectionChanged += (_, _) => ShowLogicalVolumeStatus();
        _lvList.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _ = OpenSelectedLogicalVolumeAsync();
                e.Handled = true;
            }
        };
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Lv_Menu_Open", () => _ = OpenSelectedLogicalVolumeAsync()));
        menu.Items.Add(Item("Lv_Menu_Assign", () =>
        {
            if (SelectedLogicalVolume is { } v && (_context.ActiveDocument ?? _document) is { } d)
            {
                _ = Window?.AssignMembersAsync(d, v);
            }
        }));
        menu.Items.Add(Item("Lv_Menu_Refresh", () => _ = Window?.ShowLogicalVolumesAsync()));
        _lvList.ContextFlyout = menu;
        var header = new TextBlock { Text = Loc.Get("Lv_Header"), Margin = new Thickness(12, 0, 8, 0) };
        header.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        header.FontFamily = mono;
        _lvSection.Children.Add(_lvTitle);
        _lvSection.Children.Add(new ScrollViewer { Content = header, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _lvSection.Children.Add(new ScrollViewer
        {
            Content = _lvList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        _lvSection.Children.Add(_lvStatus);
    }

    private void RefreshLogicalVolumes()
    {
        LogicalVolumeScan? scan = _state?.LogicalVolumes;
        bool running = _state?.LogicalVolumesRunning == true;
        _lvSection.Visibility = scan is not null || running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            _lvStatus.Text = Loc.Get("Lv_Analyzing");
            return;
        }

        string? selected = SelectedLogicalVolume?.Name;
        _lvRows = scan is null ? [] : [.. scan.Volumes.Select(v => new LvItem(v, MainWindow.LvRowText(v, scan)))];
        _lvList.ItemsSource = _lvRows;
        _lvList.SelectedItem = _lvRows.FirstOrDefault(r => r.Volume.Name == selected) ?? _lvRows.FirstOrDefault();
        ShowLogicalVolumeStatus();
    }

    /// <summary>一覧の下の欄: 知らせ (足りない構成要素など)、メタデータの警告、選んだボリュームの詳細。</summary>
    private void ShowLogicalVolumeStatus()
    {
        LogicalVolumeScan? scan = _state?.LogicalVolumes;
        if (_state?.LogicalVolumesRunning == true)
        {
            return;
        }

        var lines = new List<string>();
        if (_state?.LogicalVolumesMessage is { } message)
        {
            lines.Add(message);
        }

        foreach (LogicalVolumeIssue issue in scan?.Issues ?? [])
        {
            lines.Add(Loc.Format("Lv_Issue", issue.Group, issue.Detail));
        }

        if (scan is not null && SelectedLogicalVolume is { } v)
        {
            lines.AddRange(MainWindow.LvDetailLines(v, scan));
        }

        _lvStatus.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>表示している論理ボリューム (テスト用)。</summary>
    internal IReadOnlyList<LogicalVolumeInfo> LogicalVolumes => [.. _lvRows.Select(r => r.Volume)];

    internal IReadOnlyList<string> LogicalVolumeTexts => [.. _lvRows.Select(r => r.Text)];

    internal string LogicalVolumeStatus => _lvStatus.Text;

    internal LogicalVolumeInfo? SelectedLogicalVolume => (_lvList.SelectedItem as LvItem)?.Volume;

    /// <summary>テスト用: 名前で選ぶ。</summary>
    internal bool SelectLogicalVolume(string name)
    {
        LvItem? item = _lvRows.FirstOrDefault(r => r.Volume.Name == name);
        _lvList.SelectedItem = item;
        return item is not null;
    }

    internal Task<DocumentViewModel?> OpenSelectedLogicalVolumeAsync() =>
        SelectedLogicalVolume is { } v && (_context.ActiveDocument ?? _document) is { } d && Window is { } w ? w.OpenLogicalVolumeAsync(d, v) : Task.FromResult<DocumentViewModel?>(null);

    private sealed record LvItem(LogicalVolumeInfo Volume, string Text)
    {
        public override string ToString() => Text;
    }

    private void SetRows(IReadOnlyList<PartitionRow> rows)
    {
        PartitionRow? selected = SelectedRow;
        _rows = [.. rows.Select(r => new RowItem(r, Format(r)))];
        _list.ItemsSource = _rows;
        if (selected is not null && _rows.FirstOrDefault(r => r.Row.Number == selected.Number && r.Row.StartLba == selected.StartLba) is { } again)
        {
            _list.SelectedItem = again;
        }

        BuildLayout();
        ShowDetails();
    }

    /// <summary>一覧の列 (仕様 3): 番号、形式、種類、名前、開始 LBA、終了 LBA、セクタ数、サイズ、開始オフセット、属性、推定ファイルシステム、状態。</summary>
    internal static string Format(PartitionRow r)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        string number = r.IsUnallocated ? "-" : r.Number.ToString(c);
        string type = TypeText(r);
        string size = StatusFormat.ShortSize(r.Size, c) ?? r.Size.ToString("N0", c);
        string fs = r.FileSystem == FileSystemKind.Unknown ? string.Empty : FileSystemDetector.DisplayName(r.FileSystem);
        string state = r.IsUnallocated ? string.Empty : Loc.Get(r.Warning ? "Part_State_Warning" : "Part_State_Ok");
        return string.Format(c, "{0,4}  {1,-9} {2,-28} {3,-12} {4,12} {5,12} {6,12} {7,10}  0x{8:X10}  {9,-12} {10,-9} {11}",
            number, Loc.Get("Part_Scheme_" + r.Scheme), Trim(type, 28), Trim(r.Name, 12), r.StartLba.ToString("N0", c), r.EndLba.ToString("N0", c),
            r.SectorCount.ToString("N0", c), size, r.StartOffset, Trim(r.Flags, 12), fs, state);
    }

    private static string Trim(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    /// <summary>種類 (ID または GUID とその名前)。</summary>
    internal static string TypeText(PartitionRow r)
    {
        string name = r.TypeKey is { } key ? Loc.Get("PartType_" + key) : r.TypeText ?? string.Empty;
        if (r.IsUnallocated)
        {
            return name;
        }

        return r.Scheme switch
        {
            PartitionScheme.Gpt => name.Length > 0 ? name : r.TypeCode,
            PartitionScheme.Mbr or PartitionScheme.Logical => $"{r.TypeCode} {name}".Trim(),
            _ => name.Length > 0 ? name : r.TypeCode,
        };
    }

    /// <summary>配置図 (仕様 5): ディスク全体を横長の帯で表し、区画の幅をセクタ数に比例させる (1% 未満も最小幅)。未割り当ては斜線。</summary>
    private void BuildLayout()
    {
        _layout.Children.Clear();
        _layout.ColumnDefinitions.Clear();
        PartitionAnalysis? analysis = _state?.Partitions;
        if (analysis is null || analysis.DiskLength <= 0)
        {
            return;
        }

        var segments = analysis.Rows.Where(r => !r.IsContainer && r.Scheme != PartitionScheme.Bsd && r.SectorCount > 0).OrderBy(r => r.StartLba).ToList();
        long total = Math.Max(1, analysis.DiskLength / analysis.SectorSize);
        double minShare = 0.01;
        int column = 0;
        foreach (PartitionRow r in segments)
        {
            double share = Math.Max(minShare, (double)r.SectorCount / total);
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star), MinWidth = 18 });
            var label = new TextBlock
            {
                Text = r.IsUnallocated ? Loc.Get("PartType_Unallocated") : $"{r.Number} {r.Name}".Trim(),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 2, 3, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Grid();
            if (r.IsUnallocated)
            {
                cell.Children.Add(Hatch());
            }

            cell.Children.Add(label);
            var segment = new Border
            {
                Child = cell,
                Background = (Brush)Application.Current.Resources[r.IsUnallocated ? "SolidBackgroundFillColorTertiaryBrush" : r.Warning
                    ? "SystemFillColorCautionBackgroundBrush" : "AccentFillColorTertiaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 1, 0),
            };
            ToolTipService.SetToolTip(segment, Format(r));
            AutomationProperties.SetName(segment, label.Text);
            PartitionRow captured = r;
            segment.Tapped += (_, _) => _list.SelectedItem = _rows.FirstOrDefault(x => ReferenceEquals(x.Row, captured));
            Grid.SetColumn(segment, column++);
            _layout.Children.Add(segment);
        }
    }

    private static Microsoft.UI.Xaml.Shapes.Path Hatch()
    {
        var group = new GeometryGroup();
        for (double i = -44; i < 2000; i += 7)
        {
            group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(i, 44), EndPoint = new Windows.Foundation.Point(i + 44, 0) });
        }

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = group,
            Stroke = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
            StrokeThickness = 1,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>詳細と警告 (仕様 8: 警告の詳細を一覧の下の欄に文で示す)。</summary>
    private void ShowDetails()
    {
        PartitionAnalysis? analysis = _state?.Partitions;
        var lines = new List<string>();
        if (SelectedRow is { } r)
        {
            lines.Add(Format(r).Trim());
            if (r.TypeGuid is { } type)
            {
                lines.Add(Loc.Format("Part_Detail_TypeGuid", type.ToString("D").ToUpperInvariant()));
            }

            if (r.UniqueGuid is { } unique)
            {
                lines.Add(Loc.Format("Part_Detail_UniqueGuid", unique.ToString("D").ToUpperInvariant()));
            }

            if (r.BlockStart is long blockStart)
            {
                lines.Add(Loc.Format("Part_Detail_Blocks", blockStart, r.BlockCount ?? 0));
            }

            if (r.EntryOffset is long entry)
            {
                lines.Add(Loc.Format("Part_Detail_Entry", "0x" + entry.ToString("X", CultureInfo.InvariantCulture)));
            }

            // 暗号化の検出 (FOR-29 の「画面」: パーティションパネルの詳細欄)。
            if (r.Encryption is { } encryption)
            {
                lines.Add(Loc.Format("Encryption_Kind", Loc.Get("Encryption_Kind_" + encryption.Kind)));
            }

            foreach (PartitionIssue issue in analysis?.IssuesFor(r.Number) ?? [])
            {
                lines.Add("⚠ " + IssueText(issue));
            }
        }

        foreach (PartitionIssue issue in analysis?.Issues.Where(i => i.Row is null) ?? [])
        {
            lines.Add("⚠ " + IssueText(issue));
        }

        _details.Text = string.Join(Environment.NewLine, lines);
    }

    internal static string IssueText(PartitionIssue issue) =>
        Loc.Format("PartIssue_" + issue.Kind, issue.Row ?? 0, issue.Lba?.ToString("N0", CultureInfo.CurrentCulture) ?? "-", issue.OtherRow ?? 0);

    // ---- 操作 (仕様 6) ----

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Part_Menu_GoToStart", GoToStart));
        menu.Items.Add(Item("Part_Menu_Select", SelectPartition));
        menu.Items.Add(Item("Part_Menu_OpenInNewTab", () => OpenInNewTab()));
        menu.Items.Add(Item("Part_Menu_ShowStructure", ShowStructure));
        menu.Items.Add(Item("Part_Menu_ShowFileSystem", () => _ = ShowFileSystemAsync()));
        menu.Items.Add(Item("Part_Menu_LogicalVolumes", () => _ = Window?.ShowLogicalVolumesAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Part_Menu_Scan", () => _ = Window?.ShowLostPartitionScanAsync(SelectedRow is { IsUnallocated: true } gap ? gap : null)));
        menu.Opening += (_, _) =>
        {
            PartitionRow? row = SelectedRow;
            foreach (MenuFlyoutItem item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string id = AutomationProperties.GetAutomationId(item);
                item.IsEnabled = id switch
                {
                    "Part_Menu_Scan" => _document is not null,
                    "Part_Menu_ShowStructure" => row?.Template is not null,
                    "Part_Menu_ShowFileSystem" or "Part_Menu_OpenInNewTab" => row is { IsUnallocated: false, IsContainer: false },
                    "Part_Menu_LogicalVolumes" => row?.FileSystem is FileSystemKind.Ldm or FileSystemKind.Lvm2,
                    _ => row is not null,
                };
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

    internal void GoToStart()
    {
        if (SelectedRow is { } r && _document is { } d)
        {
            d.Editor.GoTo(Math.Clamp(r.StartOffset, 0, Math.Max(0, d.Document.Length - 1)));
        }
    }

    internal void SelectPartition()
    {
        if (SelectedRow is { } r && _document is { } d)
        {
            d.Editor.Select(r.StartOffset, Math.Min(r.Size, d.Document.Length - r.StartOffset));
        }
    }

    internal DocumentViewModel? OpenInNewTab() =>
        SelectedRow is { IsUnallocated: false } r && _document is { } d ? Window?.OpenPartitionTab(d, r) : null;

    internal void ShowStructure()
    {
        if (SelectedRow is { } r && _document is { } d)
        {
            Window?.ShowPartitionEntryStructure(d, r);
        }
    }

    internal Task ShowFileSystemAsync() =>
        SelectedRow is { IsUnallocated: false, IsContainer: false } r && _document is { } d && Window is { } w
            ? w.ShowFileSystemAsync(d, r.StartOffset, r.Size, r.FileSystem is FileSystemKind.Unknown ? null : r.FileSystem)
            : Task.CompletedTask;

    private sealed record RowItem(PartitionRow Row, string Text);
}
