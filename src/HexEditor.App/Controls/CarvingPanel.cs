using System.Globalization;
using HexEditor.App.Panels;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using HexEditor.App.Services;
using HexEditor.Core.Forensics.Carving;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HexEditor.App.Controls;

/// <summary>
/// カービングの結果のパネル (12 の FOR-19 の仕様 5)。見つかったファイルを結果一覧で表示し、行を選ぶと開始位置へジャンプして範囲を選択する。
/// 画像の形式 (JPEG・PNG・GIF・BMP) は詳細欄に縮小表示する (設定 forensics.carving.thumbnails でオフにできる)。結果が 100 万件を超えても表示できる
/// (仮想化した表と、1 件 40 バイト足らずの結果の保持)。
/// </summary>
public sealed class CarvingPanel : UserControl, IPanelContent
{
    private const int MaxThumbnailBytes = 16 * 1024 * 1024;

    private readonly MainWindow _window;
    private readonly VirtualTable _table = new("Carving_List");
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontFamily = DialogParts.Mono, FlowDirection = FlowDirection.LeftToRight };
    private readonly Image _thumbnail = new() { MaxWidth = 160, MaxHeight = 160, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private CarvedFileStore? _store;
    private bool _refreshQueued;
    private int _thumbnailVersion;

    public CarvingPanel(MainWindow window)
    {
        _window = window;
        AutomationProperties.SetAutomationId(_summary, "Carving_Summary");
        AutomationProperties.SetAutomationId(_empty, "Carving_Empty");
        AutomationProperties.SetAutomationId(_detail, "Carving_Detail");
        AutomationProperties.SetAutomationId(_thumbnail, "Carving_Thumbnail");
        AutomationProperties.SetName(_thumbnail, Loc.Get("Carving_ThumbnailName"));
        var run = new Button { Content = Loc.Get("Carving_Run") };
        AutomationProperties.SetAutomationId(run, "Carving_Run");
        run.Click += async (_, _) => await window.Commands.ExecuteAsync("forensics.carving.run");
        var openFolder = new Button { Content = Loc.Get("Carving_OpenFolder") };
        AutomationProperties.SetAutomationId(openFolder, "Carving_OpenFolder");
        openFolder.Click += (_, _) => window.OpenCarvingFolder();
        var bar = new Grid { Padding = new Thickness(12, 6, 12, 6), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_summary);
        Grid.SetColumn(run, 1);
        Grid.SetColumn(openFolder, 2);
        bar.Children.Add(run);
        bar.Children.Add(openFolder);

        var detail = new StackPanel { Spacing = 6, Padding = new Thickness(12, 6, 12, 6) };
        detail.Children.Add(_detail);
        detail.Children.Add(_thumbnail);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        split.Children.Add(_table);
        split.Children.Add(_empty);
        var detailScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detailScroll, 1);
        split.Children.Add(detailScroll);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(split, 1);
        root.Children.Add(bar);
        root.Children.Add(split);
        Content = root;

        _table.SetColumns(
        [
            new VirtualColumn(Loc.Get("Carving_Col_Number"), 70, true), new VirtualColumn(Loc.Get("Carving_Col_Type"), 120), new VirtualColumn(Loc.Get("Carving_Col_Offset"), 150, true),
            new VirtualColumn(Loc.Get("Carving_Col_Lba"), 110, true), new VirtualColumn(Loc.Get("Carving_Col_Length"), 120, true), new VirtualColumn(Loc.Get("Carving_Col_Method"), 110),
            new VirtualColumn(Loc.Get("Carving_Col_Output"), 260, true),
        ]);
        _table.RowProvider = RowOf;
        _table.SelectionChanged += (_, _) => OnSelected();
        _table.Activated += (_, i) => OnSelected();
        ForensicsService.CarvingChanged += OnChanged;
        Unloaded += (_, _) => ForensicsService.CarvingChanged -= OnChanged;
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>テスト用: 結果の件数・選択・詳細。</summary>
    internal long RowCount => _store?.Count ?? 0;

    internal string DetailText => _detail.Text;

    internal bool ThumbnailVisible => _thumbnail.Visibility == Visibility.Visible;

    internal IReadOnlyList<string> Cells(long index) => RowOf(index);

    internal void SelectRow(long index) => _table.Select(index);

    public void Refresh()
    {
        _store = ForensicsService.LiveCarving ?? ForensicsService.LastCarving?.Files;
        CarvingResult? result = ForensicsService.LastCarving;
        long count = _store?.Count ?? 0;
        _table.Refresh(count);
        bool any = count > 0;
        _table.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = Loc.Get(result is null && _store is null ? "Carving_None" : "Carving_NoResults");
        if (_store is null)
        {
            _summary.Text = string.Empty;
            return;
        }

        string counts = result is null ? string.Empty : string.Join(", ", result.CountsByType.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value:N0}"));
        _summary.Text = Loc.Format("Carving_Summary", count) + (counts.Length > 0 ? " (" + counts + ")" : string.Empty);
    }

    private IReadOnlyList<string> RowOf(long index)
    {
        if (_store is null || index < 0 || index >= _store.Count)
        {
            return [];
        }

        CarvedFile f = _store[index];
        return
        [
            f.Number.ToString(CultureInfo.InvariantCulture), f.Type, $"0x{f.Offset:X} ({f.Offset.ToString("N0", CultureInfo.CurrentCulture)})", f.Lba.ToString(CultureInfo.InvariantCulture),
            f.Length.ToString("N0", CultureInfo.CurrentCulture), Loc.Get("Carving_Method_" + f.Method), f.Exported ? f.RelativePath : string.Empty,
        ];
    }

    private void OnSelected()
    {
        if (_store is null || _table.SelectedIndex < 0 || _table.SelectedIndex >= _store.Count)
        {
            _detail.Text = string.Empty;
            _thumbnail.Visibility = Visibility.Collapsed;
            return;
        }

        CarvedFile f = _store[_table.SelectedIndex];
        _detail.Text = string.Join(Environment.NewLine, Loc.Format("Carving_Detail_Type", f.Type), Loc.Format("Carving_Detail_Offset", f.Offset.ToString("X", CultureInfo.InvariantCulture), f.Offset),
            Loc.Format("Carving_Detail_Length", f.Length), Loc.Format("Carving_Detail_Method", Loc.Get("Carving_Method_" + f.Method)), f.Exported ? f.RelativePath : string.Empty);
        _window.JumpToCarved(f);
        _ = ShowThumbnailAsync(f);
    }

    private async Task ShowThumbnailAsync(CarvedFile f)
    {
        int version = ++_thumbnailVersion;
        _thumbnail.Visibility = Visibility.Collapsed;
        if (App.Settings.GetBool("forensics.carving.thumbnails", true) && f.Signature.Extension is "jpg" or "png" or "gif" or "bmp" && f.Length <= MaxThumbnailBytes
            && _window.ReadCarvedBytes(f) is { } bytes)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage { DecodePixelWidth = 160 };
                await bitmap.SetSourceAsync(stream);
                if (version == _thumbnailVersion)
                {
                    _thumbnail.Source = bitmap;
                    _thumbnail.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or IOException or InvalidOperationException)
            {
                // 壊れた画像: 縮小表示は出さない。
            }
        }
    }

    public bool FocusContent() => (_table.Visibility == Visibility.Visible ? _table : (Control)this).Focus(FocusState.Programmatic);
}
