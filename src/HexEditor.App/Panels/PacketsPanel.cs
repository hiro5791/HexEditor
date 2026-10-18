using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Remote.Udp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Panels;

/// <summary>
/// 下部パネル「パケット」(ENG-42 の「画面」): 受信したデータグラムの一覧 (番号・位置・長さ・受信時刻・送信元) と、一時停止・再開・消去・
/// エクスポート (CSV / JSON) のボタン。一覧の行を選ぶとその位置にジャンプし、パケットを選択する (仕様 2)。行は仮想化する (保持している
/// パケットを写さずに、見えている行だけを作る)。
/// </summary>
public sealed partial class PacketsPanel : UserControl, IPanelContent
{
    private readonly PanelContext _context;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Button _pause = new();
    private readonly Button _resume = new();
    private readonly Button _clear = new();
    private readonly Button _exportCsv = new();
    private readonly Button _exportJson = new();
    private readonly TextBlock _empty = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly PacketRows _rows = new();
    private bool _updating;

    public PacketsPanel(PanelContext context)
    {
        _context = context;
        _list.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _list.ItemsSource = _rows;
        AutomationProperties.SetAutomationId(_list, "Packets_List");
        AutomationProperties.SetName(_list, Loc.Get("Packets_List"));
        _list.SelectionChanged += (_, _) =>
        {
            if (!_updating)
            {
                JumpToSelected();
            }
        };

        Setup(_pause, "Packets_Pause", () => Capture?.Pause());
        Setup(_resume, "Packets_Resume", () => Capture?.Resume());
        Setup(_clear, "Packets_Clear", () => Capture?.Clear());
        Setup(_exportCsv, "Packets_ExportCsv", () => _ = ExportAsync(csv: true));
        Setup(_exportJson, "Packets_ExportJson", () => _ = ExportAsync(csv: false));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 8, 8, 4) };
        foreach (Button b in new[] { _pause, _resume, _clear, _exportCsv, _exportJson })
        {
            bar.Children.Add(b);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new ScrollViewer
        {
            Content = bar,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        });
        Grid.SetRow(_list, 1);
        Grid.SetRow(_empty, 1);
        grid.Children.Add(_list);
        grid.Children.Add(_empty);
        Content = grid;

        context.ActiveDocumentChanged += OnActiveDocumentChanged;
        Unloaded += (_, _) => context.ActiveDocumentChanged -= OnActiveDocumentChanged;
        Refresh();
    }

    private void OnActiveDocumentChanged(object? sender, EventArgs e) => Refresh();

    private static void Setup(Button button, string key, Action action)
    {
        button.Content = Loc.Get(key);
        AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => action();
    }

    private DocumentViewModel? Document => _context.ActiveDocument is { Udp: not null } doc ? doc : null;

    private UdpCapture? Capture => Document?.Udp?.Capture;

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);

    /// <summary>行の数 (テスト用)。</summary>
    internal int RowCount => _rows.Count;

    /// <summary>行の文字列 (テスト用)。</summary>
    internal IReadOnlyList<string> RowTexts => [.. Enumerable.Range(0, _rows.Count).Select(i => (string)_rows[i]!)];

    /// <summary>一覧を受信の状態に合わせる (データグラムの記録・タブの切り替えのたびに MainWindow が呼ぶ)。</summary>
    public void Refresh()
    {
        UdpCapture? capture = Capture;
        _updating = true;
        try
        {
            _rows.Reset(capture);
        }
        finally
        {
            _updating = false;
        }

        bool has = capture is not null;
        _list.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = Loc.Get("Packets_NotUdp");
        UdpCaptureState state = capture?.State ?? UdpCaptureState.Stopped;
        _pause.IsEnabled = state == UdpCaptureState.Receiving;
        _resume.IsEnabled = state == UdpCaptureState.Paused;
        _clear.IsEnabled = _exportCsv.IsEnabled = _exportJson.IsEnabled = has;
    }

    private void JumpToSelected()
    {
        if (Document is not { Udp: { } udp } doc || _list.SelectedIndex < 0 || udp.Capture.PacketAtIndex(_list.SelectedIndex) is not { } packet)
        {
            return;
        }

        // 位置は受信開始からの通算の位置。ドキュメントのオフセットは保持している先頭からの位置。
        long offset = Math.Max(0, packet.Position - udp.BaseAddress);
        long length = Math.Min(packet.Length, Math.Max(0, doc.Document.Length - offset));
        if (length > 0)
        {
            doc.Editor.Select(offset, length);
        }
        else
        {
            doc.Editor.GoTo(Math.Min(offset, doc.Document.Length), extendSelection: false);
        }
    }

    private async Task ExportAsync(bool csv)
    {
        if (Capture is not { } capture || _context.Window is not MainWindow window)
        {
            return;
        }

        string text = csv ? UdpPacketExport.ToCsv(capture.Packets) : UdpPacketExport.ToJson(capture.Packets);
        await window.SavePacketListAsync(text, csv ? ".csv" : ".json");
    }

    /// <summary>
    /// パケットの行を仮想化して出す一覧 (ListView は見えている行の分だけ添字で読む)。変わったら Reset で知らせる。
    /// </summary>
    private sealed class PacketRows : IList, INotifyCollectionChanged
    {
        private UdpCapture? _capture;
        private int _count;

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public void Reset(UdpCapture? capture)
        {
            _capture = capture;
            _count = capture?.PacketCount ?? 0;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public object? this[int index]
        {
            get => _capture?.PacketAtIndex(index) is { } p
                ? string.Create(CultureInfo.InvariantCulture,
                    $"#{p.Number,-6} 0x{p.Position:X8}  {p.Length,6}  {p.ReceivedAt.ToLocalTime():HH:mm:ss.fff}  {p.Source}")
                : string.Empty;
            set => throw new NotSupportedException();
        }

        public int Count => _count;

        public bool IsFixedSize => false;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => IndexOf(value) >= 0;

        public int IndexOf(object? value)
        {
            for (int i = 0; i < _count; i++)
            {
                if (Equals(this[i], value))
                {
                    return i;
                }
            }

            return -1;
        }

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < _count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return this[i];
            }
        }
    }
}
