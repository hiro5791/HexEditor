using System.Collections.ObjectModel;
using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Fat;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Panels;

/// <summary>ファイル一覧の列 (FOR-09 の仕様 3。表示・非表示を切り替えられる)。</summary>
internal enum FsColumn
{
    Name,
    Extension,
    Size,
    AllocatedSize,
    Created,
    Modified,
    Accessed,
    Changed,
    Attributes,
    FileId,
    FirstCluster,
    Fragments,
    State,
    Path,
    OriginalPath,
    Recovery,
}

/// <summary>ファイル一覧の 1 行。文字列は表示するときに作る (100 万件でも全部の文字列を持たない)。</summary>
internal sealed class FsRow(FsEntry entry, Func<FsEntry, string> format)
{
    public FsEntry Entry { get; } = entry;

    public string Text => format(Entry);

    public override string ToString() => Text;
}

/// <summary>
/// ファイルシステムパネル (FOR-09 の画面): ツリー (ルートと仮想フォルダ)、ファイル一覧 (絞り込み、仮想化)、選んだ項目の詳細と断片の一覧。ディレクトリは
/// 展開したときに読み、一覧はバックグラウンドで読みながら順に表示する。ファイルシステムには書き込まない (書き込む操作はない。FOR-17 の仕様 6)。
/// </summary>
public sealed partial class FileSystemPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly TreeView _tree = new() { SelectionMode = TreeViewSelectionMode.Single, MinHeight = 80 };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly ListView _fragments = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 160 };
    private readonly TextBox _filter = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 0) };
    private readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _header = new() { Margin = new Thickness(12, 0, 8, 0) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _listStatus = new() { Margin = new Thickness(8, 0, 8, 0) };
    private readonly ComboBox _fatChoice = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _override = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(8, 4, 8, 0) };
    private readonly NumberBox _overrideSpc = new() { Minimum = 1, Maximum = 128, Value = 8, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ObservableCollection<FsRow> _rows = [];
    private readonly HashSet<FsColumn> _columns =
        [FsColumn.Name, FsColumn.Size, FsColumn.Modified, FsColumn.Attributes, FsColumn.FileId, FsColumn.Fragments, FsColumn.State];
    private DocumentViewModel? _document;
    private FileSystemDocumentState? _state;
    private FileSystemSession? _session;
    private FsEntry? _folder;
    private List<FsEntry> _all = [];
    private CancellationTokenSource? _loading;
    private FsNameFilter _nameFilter = new(null);
    private int _loadVersion;

    public FileSystemPanel(PanelContext context)
    {
        _context = context;
        var mono = new FontFamily("Cascadia Mono, Consolas");
        _list.FontFamily = mono;
        _list.ItemsSource = _rows;
        _list.DisplayMemberPath = nameof(FsRow.Text);
        AutomationProperties.SetAutomationId(_list, "Fs_List");
        AutomationProperties.SetName(_list, Loc.Get("Fs_ListName"));
        AutomationProperties.SetAutomationId(_tree, "Fs_Tree");
        AutomationProperties.SetName(_tree, Loc.Get("Fs_TreeName"));
        AutomationProperties.SetAutomationId(_details, "Fs_Details");
        AutomationProperties.SetAutomationId(_title, "Fs_Title");
        AutomationProperties.SetAutomationId(_warnings, "Fs_Warnings");
        AutomationProperties.SetAutomationId(_listStatus, "Fs_ListStatus");
        AutomationProperties.SetAutomationId(_fragments, "Fs_Fragments");
        AutomationProperties.SetName(_fragments, Loc.Get("Fs_FragmentsName"));
        _fragments.FontFamily = mono;
        _fragments.DisplayMemberPath = nameof(FragmentItem.Text);
        _fragments.IsItemClickEnabled = true;
        _fragments.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FragmentItem f && _document is { } d)
            {
                d.Editor.GoTo(Math.Clamp(f.Offset, 0, Math.Max(0, d.Document.Length - 1)));
            }
        };
        _header.FontFamily = mono;
        _header.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _warnings.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        _tree.Expanding += Tree_Expanding;
        _tree.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItem is TreeViewNode node && node.Content is FolderItem folder)
            {
                ShowFolder(folder.Entry);
            }
        };
        _tree.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.FirstOrDefault() is TreeViewNode { Content: FolderItem folder } && !ReferenceEquals(folder.Entry, _folder))
            {
                ShowFolder(folder.Entry);
            }
        };

        _list.SelectionChanged += (_, _) => ShowSelectedDetails();
        _list.DoubleTapped += (_, _) => ActivateSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ActivateSelected();
                e.Handled = true;
            }
        };
        _list.ContextFlyout = BuildMenu();

        _filter.PlaceholderText = Loc.Get("Fs_Filter");
        AutomationProperties.SetName(_filter, Loc.Get("Fs_Filter"));
        AutomationProperties.SetAutomationId(_filter, "Fs_Filter");
        _filter.TextChanged += (_, _) => ApplyFilter();
        _search.PlaceholderText = Loc.Get("Fs_Search");
        AutomationProperties.SetName(_search, Loc.Get("Fs_Search"));
        AutomationProperties.SetAutomationId(_search, "Fs_Search");
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _document is { } d && _search.Text.Trim().Length > 0)
            {
                _ = Window?.FindFilesAsync(d, _search.Text.Trim());
                e.Handled = true;
            }
        };

        AutomationProperties.SetAutomationId(_fatChoice, "Fs_FatChoice");
        AutomationProperties.SetName(_fatChoice, Loc.Get("Fs_FatChoice"));
        _fatChoice.SelectionChanged += (_, _) => SwitchFat();
        _overrideSpc.Header = Loc.Get("Fs_OverrideSpc");
        AutomationProperties.SetAutomationId(_overrideSpc, "Fs_OverrideSpc");
        var reanalyze = new Button { Content = Loc.Get("Fs_Reanalyze"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(reanalyze, "Fs_Reanalyze");
        reanalyze.Click += (_, _) => _ = ReanalyzeWithOverrideAsync();
        _override.Children.Add(_overrideSpc);
        _override.Children.Add(reanalyze);

        var columns = new DropDownButton { Content = Loc.Get("Fs_Columns") };
        AutomationProperties.SetAutomationId(columns, "Fs_Columns");
        var columnMenu = new MenuFlyout();
        foreach (FsColumn column in Enum.GetValues<FsColumn>())
        {
            var item = new ToggleMenuFlyoutItem { Text = Loc.Get("FsColumn_" + column), IsChecked = _columns.Contains(column) };
            AutomationProperties.SetAutomationId(item, "FsColumn_" + column);
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                {
                    _columns.Add(column);
                }
                else
                {
                    _columns.Remove(column);
                }

                RefreshRowTexts();
            };
            columnMenu.Items.Add(item);
        }

        columns.Flyout = columnMenu;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4, 8, 4) };
        _filter.MinWidth = 120;
        _search.MinWidth = 120;
        toolbar.Children.Add(_filter);
        toolbar.Children.Add(_search);
        toolbar.Children.Add(columns);
        toolbar.Children.Add(_fatChoice);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.7, GridUnitType.Star), MinHeight = 60 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star), MinHeight = 60 });
        Add(grid, _title, 0);
        Add(grid, _warnings, 1);
        Add(grid, _override, 2);
        Add(grid, _tree, 3);
        Add(grid, new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, 4);
        Add(grid, new ScrollViewer { Content = _header, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, 5);
        Add(grid, new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }, 6);
        Add(grid, _listStatus, 7);
        var detailStack = new StackPanel();
        detailStack.Children.Add(_details);
        detailStack.Children.Add(_fragments);
        Add(grid, new ScrollViewer { Content = detailStack }, 8);
        Content = grid;

        context.ActiveDocumentChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private static void Add(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private MainWindow? Window => _context.Window as MainWindow;

    public bool FocusContent() => _rows.Count > 0 ? _list.Focus(FocusState.Programmatic) : _tree.Focus(FocusState.Programmatic);

    // ---- ドキュメントへの結び付け ----

    private void Attach()
    {
        DocumentViewModel? doc = _context.ActiveDocument;
        if (ReferenceEquals(doc, _document) && _state is not null && ReferenceEquals(_session, _state.Session))
        {
            return;
        }

        Detach();
        _document = doc;
        _state = doc is null ? null : FileSystemDocumentState.For(doc.Document);
        if (_state is not null)
        {
            _state.Changed += OnStateChanged;
        }

        Rebuild();
    }

    private void Detach()
    {
        if (_state is not null)
        {
            _state.Changed -= OnStateChanged;
        }

        _loading?.Cancel();
        _state = null;
        _document = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_state is not null && !ReferenceEquals(_session, _state.Session))
        {
            Rebuild();
        }
        else
        {
            UpdateHeader();
        }
    });

    /// <summary>ツリーを作り直す (ファイルシステムを開いた・閉じた)。</summary>
    private void Rebuild()
    {
        _loading?.Cancel();
        _session = _state?.Session;
        _tree.RootNodes.Clear();
        _rows.Clear();
        _all = [];
        _folder = null;
        _details.Text = string.Empty;
        _fragments.ItemsSource = null;
        _fatChoice.Visibility = Visibility.Collapsed;
        _override.Visibility = Visibility.Collapsed;
        UpdateHeader();
        if (_session is not { } session)
        {
            _title.Text = _state?.FileSystemMessage ?? Loc.Get("Fs_NotOpened");
            _override.Visibility = _state?.FileSystemMessage is not null && _document is not null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        FileSystemSummary summary = session.FileSystem.Summary;
        _title.Text = Loc.Format("Fs_TitleText", summary.TypeName, summary.Label, summary.Serial,
            StatusFormat.ShortSize(summary.ClusterSize, CultureInfo.CurrentCulture) ?? summary.ClusterSize.ToString(CultureInfo.CurrentCulture));
        _warnings.Text = string.Join(Environment.NewLine, session.FileSystem.Issues.Select(i => "⚠ " + Loc.Format("FsIssue_" + i.Kind, i.Detail)));

        var root = new TreeViewNode { Content = new FolderItem(session.Root, Loc.Get("Fs_Root")), HasUnrealizedChildren = true, IsExpanded = false };
        _tree.RootNodes.Add(root);
        foreach ((FsVirtualFolder kind, FsEntry entry) in session.Virtual)
        {
            _tree.RootNodes.Add(new TreeViewNode { Content = new FolderItem(entry, Loc.Get("FsFolder_" + kind)) });
        }

        if (session.FileSystem is FatFileSystem fat && fat.FatCount > 1)
        {
            _fatChoice.Items.Clear();
            for (int i = 0; i < fat.FatCount; i++)
            {
                _fatChoice.Items.Add(Loc.Format("Fs_FatN", i + 1));
            }

            _fatChoice.SelectedIndex = fat.FatIndex;
            _fatChoice.Visibility = Visibility.Visible;
            _ = CountFatDifferencesAsync(fat);
        }

        _tree.SelectedNode = root;
        ShowFolder(session.Root);
        ShowSummary();
        _ = CountFreeAsync(session);
    }

    private long? _freeClusters;
    private long? _fatDifferences;

    private async Task CountFreeAsync(FileSystemSession session)
    {
        _freeClusters = null;
        if (session.FileSystem.Allocation is not { } map)
        {
            return;
        }

        try
        {
            long free = await Task.Run(() => map.CountFree());
            if (ReferenceEquals(session, _session))
            {
                _freeClusters = free;
                if (_list.SelectedItems.Count == 0)
                {
                    ShowSummary();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private async Task CountFatDifferencesAsync(FatFileSystem fat)
    {
        _fatDifferences = null;
        long diff = await Task.Run(() => fat.CountFatDifferences());
        if (ReferenceEquals(fat, _session?.FileSystem))
        {
            _fatDifferences = diff;
            UpdateHeader();
            if (_list.SelectedItems.Count == 0)
            {
                ShowSummary();
            }
        }
    }

    /// <summary>使う FAT を切り替えて、今のフォルダを読み直す (FOR-11 の仕様 3)。</summary>
    private void SwitchFat()
    {
        if (_session?.FileSystem is FatFileSystem fat && _fatChoice.SelectedIndex >= 0 && _fatChoice.SelectedIndex != fat.FatIndex)
        {
            fat.SelectFat(_fatChoice.SelectedIndex);
            if (_folder is { } folder)
            {
                ShowFolder(folder);
            }

            Window?.NotifyForensicStateChanged(_document!);
        }
    }

    /// <summary>BPB の値を上書きして再解析する (FOR-11 の「エラー」)。</summary>
    private async Task ReanalyzeWithOverrideAsync()
    {
        if (_document is { } d && Window is { } w)
        {
            long offset = 0, length = d.Document.Length;
            if (_state?.Partitions?.PartitionAt(d.Editor.Cursor) is { } row)
            {
                (offset, length) = (row.StartOffset, row.Size);
            }

            await w.ShowFileSystemAsync(d, offset, length, null, new FileSystemOpenOptions { OverrideSectorsPerCluster = (int)_overrideSpc.Value });
        }
    }

    private void UpdateHeader()
    {
        _header.Text = FormatHeader();
        if (_fatDifferences is long diff && _session?.FileSystem is FatFileSystem)
        {
            _listStatus.Text = Loc.Format("Fs_FatDifferences", diff) + "  " + _listStatus.Text.Split("  ").LastOrDefault();
        }
    }

    // ---- ツリー ----

    private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren && args.Node.Content is FolderItem folder && _session is { } session)
        {
            args.Node.HasUnrealizedChildren = false;
            _ = LoadSubfoldersAsync(args.Node, folder.Entry, session);
        }
    }

    private async Task LoadSubfoldersAsync(TreeViewNode node, FsEntry folder, FileSystemSession session)
    {
        List<FsEntry> dirs;
        try
        {
            dirs = await Task.Run(() => session.FileSystem.ListDirectory(folder).Where(e => e.IsDirectory && !e.Flags.HasFlag(FsEntryFlags.Stream))
                .Take(10_000).ToList());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return;
        }

        foreach (FsEntry dir in dirs)
        {
            node.Children.Add(new TreeViewNode { Content = new FolderItem(dir, dir.Name), HasUnrealizedChildren = true });
        }
    }

    // ---- 一覧 ----

    /// <summary>フォルダの中身を一覧に出す (バックグラウンドで読み、500 件ずつ加える。UI を止めない)。</summary>
    internal void ShowFolder(FsEntry folder)
    {
        if (_session is not { } session)
        {
            return;
        }

        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;
        _folder = folder;
        _all = [];
        _rows.Clear();
        int version = ++_loadVersion;
        bool deleted = FileSystemSession.VirtualKind(folder) == FsVirtualFolder.Deleted;
        if (deleted)
        {
            _columns.Add(FsColumn.OriginalPath);
            _columns.Add(FsColumn.Recovery);
        }

        _header.Text = FormatHeader();
        _listStatus.Text = Loc.Get("Fs_Loading");
        IsLoadingList = true;
        Window?.TrackFileSystemListing(session, folder, cts.Token, batch => DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _loadVersion)
            {
                return;
            }

            _all.AddRange(batch);
            foreach (FsEntry e in batch)
            {
                if (_nameFilter.Matches(e.Name))
                {
                    _rows.Add(new FsRow(e, Format));
                }
            }

            _listStatus.Text = Loc.Format("Fs_LoadingCount", _all.Count.ToString("N0", CultureInfo.CurrentCulture));
        }), (error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _loadVersion)
            {
                return;
            }

            IsLoadingList = false;
            _listStatus.Text = error is null ? Loc.Format("Fs_Count", _all.Count.ToString("N0", CultureInfo.CurrentCulture)) : Loc.Format("Fs_ListError", error);
            UpdateHeader();
        }));
    }

    /// <summary>今のファイルシステムのフォルダを読み終えたか (テスト用)。</summary>
    internal bool FolderLoaded => !IsLoadingList && _folder is not null && _state is not null && ReferenceEquals(_session, _state.Session);

    /// <summary>読み込み中か (テスト用)。</summary>
    internal bool IsLoadingList { get; private set; }

    private void ApplyFilter()
    {
        _nameFilter = new FsNameFilter(_filter.Text);
        _rows.Clear();
        foreach (FsEntry e in _all.Where(e => _nameFilter.Matches(e.Name)))
        {
            _rows.Add(new FsRow(e, Format));
        }
    }

    private void RefreshRowTexts()
    {
        _header.Text = FormatHeader();
        FsEntry[] entries = [.. _rows.Select(r => r.Entry)];
        _rows.Clear();
        foreach (FsEntry e in entries)
        {
            _rows.Add(new FsRow(e, Format));
        }
    }

    private static readonly int[] Widths = [40, 6, 14, 14, 21, 21, 21, 21, 8, 12, 12, 6, 10, 40, 40, 16];

    private string FormatHeader() =>
        string.Join("  ", Enum.GetValues<FsColumn>().Where(_columns.Contains).Select(c => Pad(Loc.Get("FsColumn_" + c), c)));

    private static string Pad(string text, FsColumn column)
    {
        int width = Widths[(int)column];
        if (column is FsColumn.Path or FsColumn.OriginalPath)
        {
            return text;
        }

        return text.Length > width ? text[..(width - 1)] + "…" : column is FsColumn.Size or FsColumn.AllocatedSize or FsColumn.Fragments ? text.PadLeft(width) : text.PadRight(width);
    }

    /// <summary>1 行の文字列 (仕様 3 の列のうち表示する列)。</summary>
    internal string Format(FsEntry e)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        var parts = new List<string>();
        foreach (FsColumn column in Enum.GetValues<FsColumn>().Where(_columns.Contains))
        {
            string value = column switch
            {
                FsColumn.Name => (e.Flags.HasFlag(FsEntryFlags.Corrupt) ? "⚠" : string.Empty) + (e.IsDirectory ? "📁 " : string.Empty) + e.Name,
                FsColumn.Extension => e.Extension,
                FsColumn.Size => e.IsDirectory ? string.Empty : e.Size.ToString("N0", c),
                FsColumn.AllocatedSize => e.AllocatedSize.ToString("N0", c),
                FsColumn.Created => Time(e.Created),
                FsColumn.Modified => Time(e.Modified),
                FsColumn.Accessed => Time(e.Accessed),
                FsColumn.Changed => Time(e.Changed),
                FsColumn.Attributes => e.Attributes,
                FsColumn.FileId => e.FileIdText,
                FsColumn.FirstCluster => e.FirstCluster?.ToString("N0", c) ?? string.Empty,
                FsColumn.Fragments => e.FragmentCount?.ToString(c) ?? string.Empty,
                FsColumn.State => Loc.Get("FsState_" + e.State) + (e.Flags.HasFlag(FsEntryFlags.Corrupt) ? " " + Loc.Get("Fs_Corrupt") : string.Empty)
                    + (e.Flags.HasFlag(FsEntryFlags.TimeMismatch) ? " " + Loc.Get("Fs_TimeMismatch") : string.Empty),
                FsColumn.Path => e.Path,
                FsColumn.OriginalPath => e.OriginalPath,
                FsColumn.Recovery => e.Recovery == FsRecoveryStatus.None ? string.Empty : Loc.Get("FsRecovery_" + e.Recovery)
                    + (e.DeletedSource == FsDeletedSource.IndexSlack ? " (" + Loc.Get("Fs_IndexSlack") + ")" : string.Empty),
                _ => string.Empty,
            };
            parts.Add(Pad(value, column));
        }

        return string.Join("  ", parts);
    }

    /// <summary>日時の表示 (FOR-05: UTC とローカル時刻。タイムゾーンの分からない FAT の日時には「~」の印)。</summary>
    internal static string Time(FsTimestamp t)
    {
        if (t.Invalid)
        {
            return Loc.Get("Fs_InvalidTime");
        }

        if (t.Utc is not DateTime utc)
        {
            return string.Empty;
        }

        string text = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        return t.ZoneUnknown ? "~" + text : text;
    }

    /// <summary>表示している項目 (テスト用)。</summary>
    internal IReadOnlyList<FsEntry> Entries => [.. _rows.Select(r => r.Entry)];

    /// <summary>読み込んだ件数 (絞り込みの前。テスト用)。</summary>
    internal int LoadedCount => _all.Count;

    internal IReadOnlyList<FsEntry> SelectedEntries => [.. _list.SelectedItems.OfType<FsRow>().Select(r => r.Entry)];

    internal string DetailsText => _details.Text;

    internal string TitleText => _title.Text;

    internal string WarningsText => _warnings.Text;

    internal string ListStatusText => _listStatus.Text;

    internal IReadOnlyList<string> FragmentTexts => (_fragments.ItemsSource as List<FragmentItem>)?.Select(f => f.Text).ToList() ?? [];

    internal FsEntry? CurrentFolder => _folder;

    internal int FatChoice
    {
        get => _fatChoice.SelectedIndex;
        set => _fatChoice.SelectedIndex = value;
    }

    internal long? FatDifferences => _fatDifferences;

    /// <summary>テスト用: 名前の項目を選ぶ。</summary>
    internal bool SelectEntry(string name)
    {
        FsRow? row = _rows.FirstOrDefault(r => r.Entry.Name == name);
        _list.SelectedItems.Clear();
        if (row is not null)
        {
            _list.SelectedItems.Add(row);
        }

        return row is not null;
    }

    internal bool SelectEntries(IEnumerable<string> names)
    {
        _list.SelectedItems.Clear();
        bool all = true;
        foreach (string name in names)
        {
            if (_rows.FirstOrDefault(r => r.Entry.Name == name) is { } row)
            {
                _list.SelectedItems.Add(row);
            }
            else
            {
                all = false;
            }
        }

        return all;
    }

    /// <summary>仮想フォルダを開く (「削除されたファイルを探す」など)。</summary>
    internal void SelectVirtualFolder(FsVirtualFolder kind)
    {
        if (_session?.Virtual.GetValueOrDefault(kind) is { } folder)
        {
            if (_tree.RootNodes.FirstOrDefault(n => n.Content is FolderItem f && ReferenceEquals(f.Entry, folder)) is { } node)
            {
                _tree.SelectedNode = node;
            }

            ShowFolder(folder);
        }
    }

    /// <summary>テスト用: パス (<c>\dir1</c>) のフォルダを開く。</summary>
    internal async Task<bool> OpenPathAsync(string path)
    {
        if (_session is not { } session)
        {
            return false;
        }

        FsEntry current = session.Root;
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            FsEntry? next = await Task.Run(() => session.FileSystem.ListDirectory(current).FirstOrDefault(e => e.IsDirectory && e.Name == part));
            if (next is null)
            {
                return false;
            }

            current = next;
        }

        ShowFolder(current);
        return true;
    }

    // ---- 詳細 (仕様 5・9) ----

    private void ShowSelectedDetails()
    {
        if (_list.SelectedItems.Count == 0 || _session is not { } session)
        {
            ShowSummary();
            return;
        }

        FsEntry e = ((FsRow)_list.SelectedItems[0]).Entry;
        var lines = new List<string>
        {
            e.Path.Length > 0 ? e.Path : e.OriginalPath,
            Loc.Format("FsDetail_Size", e.Size.ToString("N0", CultureInfo.CurrentCulture), e.AllocatedSize.ToString("N0", CultureInfo.CurrentCulture)),
            Loc.Format("FsDetail_Times", Time(e.Created), Time(e.Modified), Time(e.Accessed), Time(e.Changed)),
            Loc.Format("FsDetail_Id", e.FileIdText, e.Attributes),
        };
        if (e.Corruption != FsCorruptionKind.None)
        {
            lines.Add("⚠ " + Loc.Get("FsCorrupt_" + e.Corruption) + (e.CorruptionDetail.Length > 0 ? $" ({e.CorruptionDetail})" : string.Empty));
        }

        if (e.Flags.HasFlag(FsEntryFlags.TimeMismatch))
        {
            lines.Add("⚠ " + Loc.Get("Fs_TimeMismatchDetail"));
        }

        if (e.Flags.HasFlag(FsEntryFlags.Encrypted))
        {
            lines.Add(Loc.Get("Fs_EfsEncrypted"));
        }

        if (e.Flags.HasFlag(FsEntryFlags.AssumedContiguous))
        {
            lines.Add(Loc.Get("Fs_AssumedContiguous"));
        }

        if (e.Flags.HasFlag(FsEntryFlags.LocationUnknown))
        {
            lines.Add(Loc.Get("FsRecovery_Unknown"));
        }

        if (e.State == FsEntryState.Deleted)
        {
            lines.Add(Loc.Format("FsDetail_Deleted", Loc.Get("FsRecovery_" + e.Recovery), Loc.Get("FsDeleted_" + e.DeletedSource), e.OriginalPath));
        }

        string? group = null;
        foreach (FsDetail d in session.FileSystem.Describe(e))
        {
            if (d.Group != group && d.Group is not null)
            {
                lines.Add("— " + d.Group);
            }

            group = d.Group;
            lines.Add($"{Field(d.Key)}: {d.Value}");
        }

        _details.Text = string.Join(Environment.NewLine, lines);
        var fragments = new List<FragmentItem>();
        foreach ((long offset, long length, FsExtent x) in session.DocumentFragments(e))
        {
            fragments.Add(new FragmentItem(offset, Loc.Format(x.Resident ? "Fs_FragmentResident" : "Fs_Fragment",
                "0x" + x.LogicalOffset.ToString("X", CultureInfo.InvariantCulture), x.StartCluster.ToString("N0", CultureInfo.CurrentCulture),
                x.ClusterCount.ToString("N0", CultureInfo.CurrentCulture), "0x" + offset.ToString("X", CultureInfo.InvariantCulture),
                length.ToString("N0", CultureInfo.CurrentCulture)) + (x.Compressed ? " " + Loc.Get("Fs_FragmentCompressed") : string.Empty)));
        }

        _fragments.ItemsSource = fragments;
        _fragments.Visibility = fragments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Field(string key) => Loc.TryGet("FsField_" + key) ?? key;

    /// <summary>ルートの詳細 (仕様 9): 種類、ボリューム名、シリアル番号、クラスタサイズ、総クラスタ数、空きクラスタ数、作成日時と形式ごとの項目。</summary>
    private void ShowSummary()
    {
        _fragments.ItemsSource = null;
        _fragments.Visibility = Visibility.Collapsed;
        if (_session is not { } session)
        {
            _details.Text = string.Empty;
            return;
        }

        FileSystemSummary s = session.FileSystem.Summary;
        CultureInfo c = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            Loc.Format("FsSummary_Type", s.TypeName),
            Loc.Format("FsSummary_Label", s.Label),
            Loc.Format("FsSummary_Serial", s.Serial),
            Loc.Format("FsSummary_ClusterSize", s.ClusterSize.ToString("N0", c)),
            Loc.Format("FsSummary_Clusters", s.TotalClusters.ToString("N0", c)),
            Loc.Format("FsSummary_Free", _freeClusters?.ToString("N0", c) ?? Loc.Get("Fs_Counting")),
            Loc.Format("FsSummary_Created", Time(s.Created)),
            Loc.Format("FsSummary_Volume", "0x" + session.VolumeOffset.ToString("X", CultureInfo.InvariantCulture), session.VolumeLength.ToString("N0", c)),
        };
        if (_fatDifferences is long diff)
        {
            lines.Add(Loc.Format("Fs_FatDifferences", diff));
        }

        string? group = null;
        foreach (FsDetail d in s.Details)
        {
            if (d.Group != group && d.Group is not null)
            {
                lines.Add("— " + d.Group);
            }

            group = d.Group;
            lines.Add($"{Field(d.Key)}: {d.Value}");
        }

        foreach (FsIssue issue in session.FileSystem.Issues)
        {
            lines.Add("⚠ " + Loc.Format("FsIssue_" + issue.Kind, issue.Detail));
        }

        _details.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>ダブルクリック・Enter: ディレクトリなら開き、ファイルなら全断片を選択して先頭へジャンプする (仕様 5)。</summary>
    internal void ActivateSelected()
    {
        if (_list.SelectedItems.FirstOrDefault() is not FsRow row || _document is not { } d)
        {
            return;
        }

        if (row.Entry.IsDirectory && !row.Entry.Flags.HasFlag(FsEntryFlags.Stream))
        {
            ShowFolder(row.Entry);
            return;
        }

        Window?.JumpToFsEntry(d, row.Entry);
    }

    // ---- 右クリックメニュー (仕様 6) ----

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Fs_Menu_OpenFile", () => { foreach (FsEntry e in SelectedEntries.Where(e => !e.IsDirectory).Take(16)) { Window?.OpenFsEntryTab(_document!, e); } }));
        menu.Items.Add(Item("Fs_Menu_Export", () => _ = Window?.ExportFsEntriesAsync(_document!, SelectedEntries)));
        menu.Items.Add(Item("Fs_Menu_GoToMetadata", () => Window?.GoToFsMetadata(_document!, SelectedEntries[0])));
        menu.Items.Add(Item("Fs_Menu_ShowClusters", () => Window?.ShowFileClusters(_document!, SelectedEntries[0])));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Fs_Menu_MftRecord", () => Window?.ShowMftRecord(_document!, SelectedEntries[0])));
        menu.Items.Add(Item("Fs_Menu_Indx", () => Window?.ShowIndexRecord(_document!, SelectedEntries[0])));
        menu.Items.Add(Item("Fs_Menu_LogFile", () => _ = Window?.ShowLogFileAsync(_document!)));
        menu.Items.Add(Item("Fs_Menu_DirectoryEntry", () => Window?.ShowDirectoryEntry(_document!, SelectedEntries[0])));
        menu.Items.Add(Item("Fs_Menu_ClusterChain", () => Window?.ShowClusterChain(_document!, SelectedEntries[0])));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Fs_Menu_CopyPath", CopyPath));
        menu.Items.Add(Item("Fs_Menu_ExportList", () => _ = Window?.ExportListAsync("files.csv", json => json ? FsListExport.ToJson(Entries) : FsListExport.ToCsv(Entries))));
        menu.Opening += (_, _) =>
        {
            IReadOnlyList<FsEntry> selected = SelectedEntries;
            FsEntry? first = selected.FirstOrDefault();
            bool ntfs = _session?.FileSystem is NtfsFileSystem;
            bool fat = _session?.FileSystem is FatFileSystem or ExFatFileSystem;
            foreach (MenuFlyoutItem item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string id = AutomationProperties.GetAutomationId(item);
                (bool visible, bool enabled) = id switch
                {
                    "Fs_Menu_OpenFile" => (true, selected.Any(e => !e.IsDirectory)),
                    "Fs_Menu_Export" => (true, selected.Count > 0 && !selected.All(e => e.Flags.HasFlag(FsEntryFlags.Virtual))),
                    "Fs_Menu_GoToMetadata" => (true, first?.MetadataOffset is not null),
                    "Fs_Menu_ShowClusters" => (true, first is not null && !first.IsDirectory),
                    "Fs_Menu_MftRecord" => (ntfs, first?.MetadataOffset is not null && first.DeletedSource != FsDeletedSource.IndexSlack),
                    "Fs_Menu_Indx" => (ntfs, first is { IsDirectory: true }),
                    "Fs_Menu_LogFile" => (ntfs, first?.Name == "$LogFile"),
                    "Fs_Menu_DirectoryEntry" => (fat, first?.MetadataOffset is not null && !first.Flags.HasFlag(FsEntryFlags.Metadata)),
                    "Fs_Menu_ClusterChain" => (fat, first?.FirstCluster is not null),
                    "Fs_Menu_CopyPath" => (true, first is not null),
                    _ => (true, _rows.Count > 0),
                };
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = enabled;
            }
        };
        return menu;
    }

    /// <summary>右クリックメニューの項目の ID と表示 (テスト用。TC-FOR-17-03)。</summary>
    internal IReadOnlyList<(string Id, string Text, bool Visible)> MenuItems =>
        [.. ((MenuFlyout)_list.ContextFlyout).Items.OfType<MenuFlyoutItem>().Select(i => (AutomationProperties.GetAutomationId(i), i.Text, i.Visibility == Visibility.Visible))];

    private static MenuFlyoutItem Item(string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        AutomationProperties.SetAutomationId(item, key);
        item.Click += (_, _) => action();
        return item;
    }

    private void CopyPath()
    {
        if (SelectedEntries.FirstOrDefault() is { } e)
        {
            var package = new DataPackage();
            package.SetText(e.Path.Length > 0 ? e.Path : e.OriginalPath);
            SystemClipboard.SetContent(package);
        }
    }

    private sealed record FolderItem(FsEntry Entry, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record FragmentItem(long Offset, string Text)
    {
        public override string ToString() => Text;
    }
}
