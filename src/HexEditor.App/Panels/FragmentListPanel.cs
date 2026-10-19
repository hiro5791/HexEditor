using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Panels;

/// <summary>
/// 断片一覧パネル (FOR-15 の仕様 4・画面): 「ファイルのクラスタを表示」で強調したファイルの断片 (論理オフセット、ディスク上のオフセット、長さ、クラスタの範囲)。
/// 行をクリックするとその断片へジャンプする。
/// </summary>
public sealed partial class FragmentListPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly TextBlock _title = new() { Margin = new Thickness(8, 6, 8, 2), TextWrapping = TextWrapping.Wrap };
    private readonly Button _clear = new();
    private FileSystemDocumentState? _state;
    private DocumentViewModel? _document;
    private List<Item> _items = [];

    public FragmentListPanel(PanelContext context)
    {
        _context = context;
        _list.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _list.DisplayMemberPath = nameof(Item.Text);
        AutomationProperties.SetAutomationId(_list, "Fragments_List");
        AutomationProperties.SetName(_list, Loc.Get("Panel_Fragments_Title"));
        AutomationProperties.SetAutomationId(_title, "Fragments_Title");
        _list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Item item && _document is { } d)
            {
                d.Editor.GoTo(Math.Clamp(item.Offset, 0, Math.Max(0, d.Document.Length - 1)));
            }
        };
        _clear.Content = Loc.Get("Fragments_Clear");
        AutomationProperties.SetAutomationId(_clear, "Fragments_Clear");
        _clear.Click += (_, _) =>
        {
            if (_document is { } d)
            {
                (_context.Window as MainWindow)?.ClearFileClusters(d);
            }
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(_title);
        bar.Children.Add(_clear);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(bar);
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);
        Content = grid;
        context.ActiveDocumentChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);

    internal IReadOnlyList<string> Texts => [.. _items.Select(i => i.Text)];

    private void Attach()
    {
        Detach();
        _document = _context.ActiveDocument;
        _state = _document is null ? null : FileSystemDocumentState.For(_document.Document);
        if (_state is not null)
        {
            _state.Changed += OnChanged;
        }

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
        CultureInfo c = CultureInfo.CurrentCulture;
        FsEntry? entry = _state?.HighlightedEntry;
        _title.Text = entry is null ? Loc.Get("Fragments_None") : Loc.Format("Fragments_Title", entry.Path.Length > 0 ? entry.Path : entry.OriginalPath,
            _state!.HighlightedFragments.Count);
        _clear.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        _items = [.. (_state?.HighlightedFragments ?? []).Select((f, i) => new Item(f.Offset, Loc.Format("Fragments_Row", i + 1,
            "0x" + f.Extent.LogicalOffset.ToString("X", CultureInfo.InvariantCulture), "0x" + f.Offset.ToString("X", CultureInfo.InvariantCulture),
            f.Length.ToString("N0", c), f.Extent.ClusterCount > 0
                ? $"{f.Extent.StartCluster.ToString("N0", c)}–{(f.Extent.StartCluster + f.Extent.ClusterCount - 1).ToString("N0", c)}" : "-")))];
        _list.ItemsSource = _items;
    }

    private sealed record Item(long Offset, string Text)
    {
        public override string ToString() => Text;
    }
}
