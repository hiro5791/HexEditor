using System.Collections;
using System.Globalization;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Templates.Visualization;
using HexEditor.Platform.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>
/// テンプレートのビジュアライザのパネル (07 の TPL-16 の仕様 2・5・6、「画面」)。上部に種類と大きさ、保存ボタン。内容は種類ごとに作る
/// (画像、音声 (再生・停止とシークバー、波形)、逆アセンブル、表、グラフ、色、テキスト)。状態はウィンドウごとの
/// <see cref="TemplateVisualizerViewModel"/> が持ち、この部品はパネルを浮動にするたびに作り直される。
/// </summary>
public sealed partial class TemplateVisualizerPanel : UserControl, IPanelContent
{
    private readonly TemplateVisualizerViewModel _vm;
    private readonly TextBlock _header;
    private readonly TextBlock _message;
    private readonly Button _save;
    private readonly ContentControl _body;
    private readonly ProgressRing _busy;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private Button? _play;
    private Button? _stop;
    private Slider? _seek;
    private Line? _playhead;
    private bool _seeking;

    public TemplateVisualizerPanel(TemplateVisualizerViewModel vm)
    {
        _vm = vm;
        _header = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(_header, "TplVis_Header");
        _save = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetAutomationId(_save, "TplVis_Save");
        AutomationProperties.SetName(_save, Loc.Get("TplVis_Save"));
        ToolTipService.SetToolTip(_save, Loc.Get("TplVis_Save"));
        _save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0), Foreground = Brush("TextFillColorSecondaryBrush") };
        AutomationProperties.SetAutomationId(_message, "TplVis_Message");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _busy = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        _body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(_body, "TplVis_Body");

        var top = new Grid { Padding = new Thickness(8, 4, 8, 4), ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_header);
        Grid.SetColumn(_busy, 1);
        top.Children.Add(_busy);
        Grid.SetColumn(_save, 2);
        top.Children.Add(_save);

        var root = new Grid { RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(top);
        Grid.SetRow(_message, 1);
        root.Children.Add(_message);
        Grid.SetRow(_body, 2);
        root.Children.Add(_body);
        Content = root;
        AutomationProperties.SetAutomationId(this, "TplVis_Panel");

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => UpdatePlayback();
        vm.ContentChanged += OnContentChanged;
        vm.PlaybackChanged += OnPlaybackChanged;
        Unloaded += (_, _) =>
        {
            vm.ContentChanged -= OnContentChanged;
            vm.PlaybackChanged -= OnPlaybackChanged;
            _timer.Stop();
        };
        Rebuild();
    }

    /// <summary>保存ボタンを押した (保存先の選択と書き込みはウィンドウが行う)。</summary>
    public event EventHandler? SaveRequested;

    /// <summary>表の一覧 (テスト用の命令が読む)。</summary>
    internal ListView? Table { get; private set; }

    /// <summary>今の内容の要素 (テスト用の命令が代替テキストを読む)。</summary>
    internal FrameworkElement? ContentElement => _body.Content as FrameworkElement;

    internal int WaveformBuckets { get; private set; }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void OnContentChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void OnPlaybackChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdatePlayback);

    public bool FocusContent() => (_body.Content as Control)?.Focus(FocusState.Programmatic) ?? _save.Focus(FocusState.Programmatic);

    // ───────────── 内容 ─────────────

    private void Rebuild()
    {
        _header.Text = _vm.HeaderText;
        _message.Text = _vm.MessageText;
        _message.Visibility = _vm.MessageText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _busy.IsActive = _vm.IsLoading;
        _save.IsEnabled = _vm.CanSave;
        Table = null;
        _play = _stop = null;
        _seek = null;
        _playhead = null;
        _timer.Stop();
        FrameworkElement? content = _vm.Visual switch
        {
            BitmapVisual b => ImageView(b.Width, b.Height, ToBgra(b.Pixels)),
            EncodedImageVisual when _vm.Decoded is { } d => ImageView(d.Width, d.Height, d.Bgra),
            AudioVisual a => AudioView(a),
            DisassemblyVisual d => DisassemblyView(d),
            TableVisual t => TableView(t),
            ChartVisual c => ChartView(c),
            ColorVisual c => ColorView(c),
            TextVisual t => TextView(t),
            _ => null,
        };
        if (content is not null)
        {
            AutomationProperties.SetName(content, _vm.AltText);
            AutomationProperties.SetHelpText(content, _vm.HeaderText);
        }

        _body.Content = content;
        UpdatePlayback();
    }

    private static byte[] ToBgra(uint[] argb)
    {
        byte[] bgra = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            bgra[i * 4] = (byte)p;
            bgra[i * 4 + 1] = (byte)(p >> 8);
            bgra[i * 4 + 2] = (byte)(p >> 16);
            bgra[i * 4 + 3] = (byte)(p >> 24);
        }

        return bgra;
    }

    /// <summary>画像 (画素は拡大しても滲ませない: 小さな画像は整数倍に拡大して描く)。</summary>
    private static FrameworkElement ImageView(int width, int height, byte[] bgraStraight)
    {
        int scale = Math.Clamp(256 / Math.Max(1, Math.Max(width, height)), 1, 32);
        int w = width * scale, h = height * scale;
        var bitmap = new WriteableBitmap(w, h);
        byte[] premultiplied = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int s = ((y / scale) * width + (x / scale)) * 4;
                int d = (y * w + x) * 4;
                byte a = bgraStraight[s + 3];
                premultiplied[d] = (byte)(bgraStraight[s] * a / 255);
                premultiplied[d + 1] = (byte)(bgraStraight[s + 1] * a / 255);
                premultiplied[d + 2] = (byte)(bgraStraight[s + 2] * a / 255);
                premultiplied[d + 3] = a;
            }
        }

        using (Stream stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
        {
            stream.Write(premultiplied);
        }

        bitmap.Invalidate();
        var image = new Image { Source = bitmap, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(image, "TplVis_Image");
        return new ScrollViewer
        {
            Content = image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Enabled,
            Padding = new Thickness(8),
        };
    }

    private FrameworkElement AudioView(AudioVisual a)
    {
        _play = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetAutomationId(_play, "TplVis_Play");
        AutomationProperties.SetName(_play, Loc.Get("TplVis_Play"));
        ToolTipService.SetToolTip(_play, Loc.Get("TplVis_Play"));
        _play.Click += (_, _) => _vm.Play();
        _stop = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetAutomationId(_stop, "TplVis_Stop");
        AutomationProperties.SetName(_stop, Loc.Get("TplVis_Stop"));
        ToolTipService.SetToolTip(_stop, Loc.Get("TplVis_Stop"));
        _stop.Click += (_, _) => _vm.StopAudio();
        _seek = new Slider { Minimum = 0, Maximum = Math.Max(0.001, a.Duration.TotalSeconds), StepFrequency = 0.01, MinWidth = 120 };
        AutomationProperties.SetAutomationId(_seek, "TplVis_Seek");
        AutomationProperties.SetName(_seek, Loc.Get("TplVis_Seek"));
        _seek.ThumbToolTipValueConverter = new SecondsConverter();
        _seek.ValueChanged += (_, e) =>
        {
            if (!_seeking)
            {
                _vm.Seek(TimeSpan.FromSeconds(e.NewValue));
            }
        };

        var controls = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 0, 8, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.Children.Add(_play);
        Grid.SetColumn(_stop, 1);
        controls.Children.Add(_stop);
        Grid.SetColumn(_seek, 2);
        controls.Children.Add(_seek);

        // 波形 (区間ごとの最小・最大の縦線)。
        const int Buckets = 400;
        const double Height = 120;
        (float Min, float Max)[] wave = a.Waveform(Buckets);
        WaveformBuckets = wave.Length;
        var figure = new PathGeometry();
        for (int i = 0; i < wave.Length; i++)
        {
            double top = (1 - wave[i].Max) * Height / 2;
            double bottom = Math.Max(top + 1, (1 - wave[i].Min) * Height / 2);
            var f = new PathFigure { StartPoint = new Windows.Foundation.Point(i, top) };
            f.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(i, bottom) });
            figure.Figures.Add(f);
        }

        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = figure, Stroke = Brush("AccentFillColorDefaultBrush"), StrokeThickness = 1 };
        _playhead = new Line { Y1 = 0, Y2 = Height, Stroke = Brush("TextFillColorPrimaryBrush"), StrokeThickness = 1 };
        var canvas = new Canvas { Width = Math.Max(1, wave.Length), Height = Height };
        canvas.Children.Add(path);
        canvas.Children.Add(_playhead);
        var waveBox = new Viewbox { Child = canvas, Stretch = Stretch.Fill, Height = Height, Margin = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetAutomationId(waveBox, "TplVis_Waveform");
        AutomationProperties.SetName(waveBox, _vm.AltText);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(controls);
        panel.Children.Add(waveBox);
        return panel;
    }

    private sealed partial class SecondsConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            ((double)value).ToString("0.00", CultureInfo.CurrentCulture) + " s";

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    private void UpdatePlayback()
    {
        if (_seek is null || _vm.Visual is not AudioVisual a)
        {
            return;
        }

        bool playing = _vm.IsPlaying;
        if (_play is not null)
        {
            _play.IsEnabled = !playing;
        }

        if (_stop is not null)
        {
            _stop.IsEnabled = playing;
        }

        double seconds = _vm.PlaybackPosition.TotalSeconds;
        _seeking = true;
        _seek.Value = Math.Clamp(seconds, 0, _seek.Maximum);
        _seeking = false;
        if (_playhead is not null && a.Duration > TimeSpan.Zero)
        {
            double x = Math.Clamp(seconds / a.Duration.TotalSeconds, 0, 1) * Math.Max(1, WaveformBuckets);
            _playhead.X1 = _playhead.X2 = x;
        }

        if (playing)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private static ListView MonoList(IList items, string id)
    {
        var list = new ListView
        {
            ItemsSource = items,
            SelectionMode = ListViewSelectionMode.Single,
            FontFamily = DialogParts.Mono,
            FlowDirection = FlowDirection.LeftToRight,
        };
        AutomationProperties.SetAutomationId(list, id);
        return list;
    }

    private static FrameworkElement DisassemblyView(DisassemblyVisual d) => MonoList(d.Lines.ToList(), "TplVis_Disassembly");

    /// <summary>表 (仕様 6: 通常の一覧として読み上げる)。行は表示するときに作る。</summary>
    private FrameworkElement TableView(TableVisual t)
    {
        const double ColumnWidth = 140;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(12, 0, 0, 0) };
        foreach (string c in t.Columns)
        {
            header.Children.Add(new TextBlock
            {
                Text = c,
                Width = ColumnWidth,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        AutomationProperties.SetAutomationId(header, "TplVis_TableHeader");
        var list = new ListView
        {
            ItemsSource = new TableRows(t),
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\" /></DataTemplate>"),
        };
        AutomationProperties.SetAutomationId(list, "TplVis_Table");
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue || e.Item is not TableRow row || e.ItemContainer.ContentTemplateRoot is not StackPanel cells)
            {
                return;
            }

            cells.Children.Clear();
            IReadOnlyList<string> values = row.Values;
            for (int i = 0; i < t.Columns.Count; i++)
            {
                cells.Children.Add(new TextBlock
                {
                    Text = i < values.Count ? values[i] : string.Empty,
                    Width = ColumnWidth,
                    FontFamily = DialogParts.Mono,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = FlowDirection.LeftToRight,
                });
            }

            AutomationProperties.SetName(e.ItemContainer, row.ToString());
        };
        Table = list;
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        var grid = new Grid { MinWidth = 12 + ColumnWidth * t.Columns.Count };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(header);
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        scroll.Content = grid;
        return scroll;
    }

    /// <summary>表の 1 行 (読み上げは「列: 値」をつないだもの)。</summary>
    internal sealed class TableRow(TableVisual table, long index)
    {
        private IReadOnlyList<string>? _values;

        public long Index { get; } = index;

        public IReadOnlyList<string> Values => _values ??= table.Row(Index);

        public override string ToString() => string.Join("; ", table.Columns.Select((c, i) => c + ": " + (i < Values.Count ? Values[i] : string.Empty)));
    }

    /// <summary>行を必要になったときに作る一覧 (ListView の仮想化と組み合わせる)。</summary>
    private sealed class TableRows(TableVisual table) : IList
    {
        private readonly Dictionary<int, TableRow> _rows = [];

        public int Count => (int)Math.Min(int.MaxValue, table.RowCount);

        public object? this[int index]
        {
            get
            {
                if (!_rows.TryGetValue(index, out TableRow? row))
                {
                    if (_rows.Count > 4096)
                    {
                        _rows.Clear();
                    }

                    row = new TableRow(table, index);
                    _rows[index] = row;
                }

                return row;
            }
            set => throw new NotSupportedException();
        }

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => value is TableRow r && r.Index < Count;

        public int IndexOf(object? value) => value is TableRow r ? (int)r.Index : -1;

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }
    }

    /// <summary>グラフ (幅に合わせて間引く。折れ線は区間の平均、棒は区間の最大)。</summary>
    private FrameworkElement ChartView(ChartVisual c)
    {
        const double Width = 400, Height = 160;
        double min = Math.Min(0, c.Minimum), max = Math.Max(c.Maximum, min + 1e-9);
        int n = c.Values.Length;
        int points = Math.Min(n, (int)Width);
        var canvas = new Canvas { Width = Width, Height = Height };
        double Y(double v) => Height - (v - min) / (max - min) * Height;
        if (points > 0)
        {
            if (c.Chart == TemplateChartKind.Line)
            {
                var line = new Polyline { Stroke = Brush("AccentFillColorDefaultBrush"), StrokeThickness = 1.5 };
                for (int i = 0; i < points; i++)
                {
                    int from = (int)((long)n * i / points), to = Math.Max(from + 1, (int)((long)n * (i + 1) / points));
                    double avg = c.Values.AsSpan(from, to - from).ToArray().Where(double.IsFinite).DefaultIfEmpty(0).Average();
                    line.Points.Add(new Windows.Foundation.Point(points == 1 ? 0 : i * Width / (points - 1), Y(avg)));
                }

                canvas.Children.Add(line);
            }
            else
            {
                double barWidth = Width / points;
                for (int i = 0; i < points; i++)
                {
                    int from = (int)((long)n * i / points), to = Math.Max(from + 1, (int)((long)n * (i + 1) / points));
                    double peak = c.Values.AsSpan(from, to - from).ToArray().Where(double.IsFinite).DefaultIfEmpty(0).Max();
                    double top = Y(Math.Max(peak, 0)), bottom = Y(Math.Min(peak, 0));
                    var bar = new Rectangle { Width = Math.Max(1, barWidth - 1), Height = Math.Max(1, bottom - top), Fill = Brush("AccentFillColorDefaultBrush") };
                    Canvas.SetLeft(bar, i * barWidth);
                    Canvas.SetTop(bar, top);
                    canvas.Children.Add(bar);
                }
            }
        }

        var axis = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, "{0:G6} – {1:G6}", c.Minimum, c.Maximum),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FlowDirection = FlowDirection.LeftToRight,
        };
        var box = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxHeight = 320 };
        AutomationProperties.SetAutomationId(box, "TplVis_Chart");
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(8) };
        panel.Children.Add(box);
        panel.Children.Add(axis);
        return panel;
    }

    private static FrameworkElement ColorView(ColorVisual c)
    {
        Windows.UI.Color color = Microsoft.UI.ColorHelper.FromArgb((byte)(c.Argb >> 24), (byte)(c.Argb >> 16), (byte)(c.Argb >> 8), (byte)c.Argb);
        // 色見本はデータの色そのもの (テーマの色ではない)。枠はテーマの色で描き、ハイコントラストでも境目が見えるようにする。
        var swatch = new Border
        {
            Width = 160,
            Height = 96,
            Background = new SolidColorBrush(color),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        AutomationProperties.SetAutomationId(swatch, "TplVis_Swatch");
        var text = new TextBlock { Text = c.Hex + "  " + c.ValueText, FontFamily = DialogParts.Mono, FlowDirection = FlowDirection.LeftToRight, IsTextSelectionEnabled = true };
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(8) };
        panel.Children.Add(swatch);
        panel.Children.Add(text);
        return panel;
    }

    private static FrameworkElement TextView(TextVisual t)
    {
        var box = new TextBox
        {
            Text = t.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = DialogParts.Mono,
            Margin = new Thickness(8),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(box, "TplVis_Text");
        return box;
    }
}
