using System.Globalization;
using HexEditor.App.Controls.Charts;
using HexEditor.App.Services;
using HexEditor.Core.FileMap;
using HexEditor.Core.Statistics;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HexEditor.App.Controls;

/// <summary>ファイルマップの種類ごとの色と模様 (ANA-34 の仕様 3。ThemeResource のキーと模様のリソース名)。</summary>
public static class FileMapVisuals
{
    /// <summary>「標本による推定」の模様 (凡例の項目)。</summary>
    public const string SamplePattern = "Pattern_SparseDots";

    public static string BrushKey(FileMapKind kind) => kind switch
    {
        FileMapKind.Structure => "SystemFillColorAttentionBrush",
        FileMapKind.Embedded => "AccentFillColorTertiaryBrush",
        FileMapKind.Compressed => "SystemFillColorCautionBrush",
        FileMapKind.Encrypted => "SystemFillColorCriticalBrush",
        FileMapKind.Text => "SystemFillColorSuccessBrush",
        FileMapKind.Zero => "ControlStrongFillColorDisabledBrush",
        FileMapKind.Constant => "SystemFillColorNeutralBrush",
        FileMapKind.Records => "AccentFillColorSecondaryBrush",
        FileMapKind.Binary => "AccentFillColorDefaultBrush",
        _ => "TextFillColorDisabledBrush",
    };

    /// <summary>ハイコントラストではシステム色と模様だけで描く (色のキーはシステム色のもの)。</summary>
    public static string FillKey(FileMapKind kind, bool highContrast) => highContrast ? "SystemColorWindowColorBrush" : BrushKey(kind);

    public static string PatternBrushKey(bool highContrast) => highContrast ? "SystemColorWindowTextColorBrush" : "TextFillColorPrimaryBrush";

    /// <summary>種類ごとに違う模様 (色が見えなくても区別できる)。</summary>
    public static string PatternOf(FileMapKind kind) => kind switch
    {
        FileMapKind.Structure => "Pattern_Grid",
        FileMapKind.Embedded => "Pattern_Frame",
        FileMapKind.Compressed => "Pattern_Diagonal",
        FileMapKind.Encrypted => "Pattern_Cross",
        FileMapKind.Text => "Pattern_Horizontal",
        FileMapKind.Zero => "Pattern_Empty",
        FileMapKind.Constant => "Pattern_Zigzag",
        FileMapKind.Records => "Pattern_Vertical",
        FileMapKind.Binary => "Pattern_BackDiagonal",
        _ => "Pattern_Checker",
    };

    public static string KindName(FileMapKind kind) => Loc.Get("FileMap_Kind_" + kind);

    /// <summary>文をリソースで表示の文字列にする。</summary>
    public static string Text(FileMapMessage message) =>
        FileMapText.Format(message, Loc.Get, CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

    public static string Evidence(FileMapResult map, int index) =>
        FileMapText.Join(map.EvidenceOf(index), Loc.Get, CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

    public static string Label(FileMapResult map, int index) => Text(map.LabelOf(index));

    /// <summary>範囲の表示 (<c>0x50000〜0x5045F (1,120 バイト)</c>)。</summary>
    public static string Range(FileMapRegion r) => Loc.Format("FileMap_RangeText", "0x" + r.Offset.ToString("X", CultureInfo.InvariantCulture),
        "0x" + r.Last.ToString("X", CultureInfo.InvariantCulture), r.Length);

    /// <summary>領域の UI オートメーションの名前 (種類、ラベル、開始オフセット、長さ、確度。ANA-34 の仕様 10)。</summary>
    public static string RegionName(FileMapResult map, int index)
    {
        FileMapRegion r = map.Regions[index];
        return Loc.Format("FileMap_RegionName", KindName(r.Kind), Label(map, index), "0x" + r.Offset.ToString("X", CultureInfo.InvariantCulture), r.Length,
            Loc.Get("FileMap_Confidence_" + r.Confidence));
    }
}

/// <summary>
/// ファイルマップの帯 (ANA-34 の仕様 2〜4・10)。構造と内容の 2 本のレーンとオフセットの目盛りを描く。横軸はオフセットで左が先頭
/// (右から左に書く言語でも反転しない)。表示中のピクセルの範囲に入る領域だけを区間の索引で探し、ピクセルの列ごとにまとめて種類ごとに
/// 1 つの塗りと 1 つの模様の Path にする (領域が 100 万個でも要素は数十個)。拡大・縮小は Ctrl + ホイール・+ / - キー・ボタンで、最大は
/// 1 ブロックが 8 ピクセル。
/// </summary>
public sealed partial class FileMapBand : UserControl
{
    public const double LaneHeight = 18;
    public const double LaneGap = 3;
    public const double RulerHeight = 18;
    private const double PixelsPerBlockMax = 8;

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), FlowDirection = FlowDirection.LeftToRight };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Horizontal, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, Minimum = 0 };
    private readonly Rectangle _viewport = new() { StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Rectangle _cursor = new() { Width = 2, IsHitTestVisible = false };
    private readonly Rectangle _focus = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private FileMapResult? _map;
    private EditorState? _editor;
    private long _viewStart;
    private long _viewLength;
    private bool _syncing;
    private bool _drawQueued;
    private FileMapBandAutomationPeer? _peer;
    private List<int> _visibleItems = [];

    public FileMapBand()
    {
        FlowDirection = FlowDirection.LeftToRight;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength((LaneHeight * 2) + LaneGap + RulerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_canvas);
        root.Children.Add(_scroll);
        Content = root;
        AutomationProperties.SetAutomationId(this, "FileMap_Band");
        AutomationProperties.SetName(this, Loc.Get("FileMap_BandName"));
        AutomationProperties.SetAutomationId(_scroll, "FileMap_BandScroll");
        AutomationProperties.SetName(_scroll, Loc.Get("FileMap_BandScroll"));
        _canvas.SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _scroll.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                _viewStart = (long)e.NewValue;
                Redraw();
            }
        };
        _canvas.Tapped += (_, e) =>
        {
            Focus(FocusState.Pointer);
            ClickAt(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y);
        };
        _canvas.DoubleTapped += (_, e) => DoubleClickAt(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y);
        _canvas.PointerMoved += (_, e) => ChartSupport.SetToolTip(this, ToolTipAt(e.GetCurrentPoint(_canvas).Position.X, e.GetCurrentPoint(_canvas).Position.Y));
        _canvas.PointerExited += (_, _) => ChartSupport.SetToolTip(this, string.Empty);
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) =>
        {
            // Tab でフォーカスを受け取ったら、カーソル位置を含む内容のレーンの領域を選ぶ (仕様 4)。
            if (SelectedRegion < 0 && _map is { } map && _editor is { } editor)
            {
                Select(map.ContentAt(Math.Clamp(editor.Cursor, map.Start, Math.Max(map.Start, map.End - 1))));
            }

            Redraw();
        };
        LostFocus += (_, _) => Redraw();
        ContextRequested += (_, e) =>
        {
            int region = SelectedRegion;
            Point at = region >= 0 ? new Point(RegionBounds(region).X + 4, RegionBounds(region).Y + 4) : new Point(0, 0);
            if (e.TryGetPosition(_canvas, out Point p) && HitTest(p.X, p.Y) is var hit && hit >= 0)
            {
                Select(hit);
                region = hit;
                at = p;
            }

            if (region >= 0)
            {
                ContextMenuRequested?.Invoke(this, (region, at));
                e.Handled = true;
            }
        };
        _viewport.SetValue(Canvas.ZIndexProperty, 10);
        _cursor.SetValue(Canvas.ZIndexProperty, 11);
        _focus.SetValue(Canvas.ZIndexProperty, 12);
    }

    /// <summary>クリックした位置へ移動する (オフセット)。</summary>
    public event EventHandler<long>? NavigateRequested;

    /// <summary>ダブルクリック・Enter: その領域を選択範囲にする (領域の番号)。</summary>
    public event EventHandler<int>? RegionActivated;

    /// <summary>帯の中で選んでいる領域が変わった。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>右クリックメニューを開く (領域と、帯の中の位置)。</summary>
    public event EventHandler<(int Region, Point At)>? ContextMenuRequested;

    /// <summary>拡大率が変わった。</summary>
    public event EventHandler? ZoomChanged;

    public FileMapResult? Map
    {
        get => _map;
        set
        {
            bool sameTarget = _map is { } old && value is { } now && old.Start == now.Start && old.Length == now.Length;
            _map = value;
            if (!sameTarget)
            {
                _viewStart = value?.Start ?? 0;
                _viewLength = value?.Length ?? 0;
                SelectedRegion = -1;
            }
            else if (SelectedRegion >= (value?.Regions.Count ?? 0))
            {
                SelectedRegion = -1;
            }

            Redraw();
        }
    }

    /// <summary>表示中の範囲の枠とカーソルの縦線の元 (Hex ビュー)。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null)
            {
                _editor.Changed -= Editor_Changed;
            }

            _editor = value;
            if (_editor is not null && IsLoaded)
            {
                _editor.Changed += Editor_Changed;
            }

            Redraw();
        }
    }

    /// <summary>テスト用の模擬のハイコントラスト (ChartSupport と同じ)。</summary>
    public bool HighContrast => ChartSupport.IsHighContrast;

    /// <summary>帯の中で選んでいる領域 (Regions の番号。なければ -1)。</summary>
    public int SelectedRegion { get; private set; } = -1;

    public long ViewStart => _viewStart;

    public long ViewLength => _viewLength;

    /// <summary>今の拡大率 (全体を 1 とした倍率)。</summary>
    public double Zoom => _map is { Length: > 0 } m && _viewLength > 0 ? (double)m.Length / _viewLength : 1;

    /// <summary>最小の表示の長さ (1 ブロックが 8 ピクセル)。</summary>
    private long MinViewLength => _map is { } m ? Math.Max(1, (long)Math.Ceiling(Math.Max(1, _canvas.ActualWidth) / PixelsPerBlockMax * m.BlockSize)) : 1;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new FileMapBandAutomationPeer(this);

    public void Attach()
    {
        if (_editor is not null)
        {
            _editor.Changed -= Editor_Changed;
            _editor.Changed += Editor_Changed;
        }
    }

    public void Detach()
    {
        if (_editor is not null)
        {
            _editor.Changed -= Editor_Changed;
        }
    }

    private void Editor_Changed(object? sender, EventArgs e) => QueueRedraw();

    // ---- 拡大・縮小とスクロール ----

    /// <summary>拡大 (factor &gt; 1) ・縮小 (factor &lt; 1)。<paramref name="anchorX"/> の位置のオフセットを動かさない。</summary>
    public void ZoomBy(double factor, double? anchorX = null)
    {
        if (_map is not { Length: > 0 } map)
        {
            return;
        }

        double width = Math.Max(1, _canvas.ActualWidth);
        double ax = anchorX ?? (width / 2);
        long anchor = OffsetAt(ax);
        long length = (long)Math.Clamp(_viewLength / factor, Math.Min(MinViewLength, map.Length), map.Length);
        _viewLength = Math.Max(1, length);
        _viewStart = Math.Clamp(anchor - (long)(ax / width * _viewLength), map.Start, map.End - _viewLength);
        Redraw();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>全体を表示する。</summary>
    public void ZoomToFit()
    {
        if (_map is { } map)
        {
            _viewStart = map.Start;
            _viewLength = map.Length;
            Redraw();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>表示の始まりを動かす (テスト・スクロールバー)。</summary>
    public void ScrollTo(long offset)
    {
        if (_map is { } map)
        {
            _viewStart = Math.Clamp(offset, map.Start, Math.Max(map.Start, map.End - _viewLength));
            Redraw();
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(_canvas);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || _map is null)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            ZoomBy(delta > 0 ? 1.25 : 0.8, point.Position.X);
        }
        else
        {
            ScrollTo(_viewStart - (long)(Math.Sign(delta) * _viewLength / 10.0));
        }

        e.Handled = true;
    }

    // ---- 座標 ----

    private double X(long offset) => _viewLength <= 0 ? 0 : (double)(offset - _viewStart) / _viewLength * _canvas.ActualWidth;

    private long OffsetAt(double x) => _viewStart + (long)(Math.Clamp(x, 0, _canvas.ActualWidth) / Math.Max(1, _canvas.ActualWidth) * _viewLength);

    private static double LaneTop(FileMapLane lane) => lane == FileMapLane.Structure ? 0 : LaneHeight + LaneGap;

    /// <summary>帯の中の領域の境界 (最小 2 ピクセル)。</summary>
    internal Rect RegionBounds(int index)
    {
        if (_map is not { } map || index < 0 || index >= map.Regions.Count)
        {
            return default;
        }

        FileMapRegion r = map.Regions[index];
        double x0 = Math.Clamp(X(r.Offset), -1, _canvas.ActualWidth + 1);
        double x1 = Math.Clamp(X(r.End), -1, _canvas.ActualWidth + 1);
        double top = LaneTop(r.Lane);
        int depth = map.DepthOf(index);
        double inset = r.Lane == FileMapLane.Structure ? depth * 5 : 0;
        return new Rect(x0, top + inset, Math.Max(2, x1 - x0), Math.Max(4, LaneHeight - inset));
    }

    /// <summary>位置の領域 (構造のレーンは最も深いもの)。なければ -1。</summary>
    internal int HitTest(double x, double y)
    {
        if (_map is not { } map)
        {
            return -1;
        }

        long offset = OffsetAt(x);
        return y < LaneHeight + (LaneGap / 2) ? map.StructureAt(offset) : map.ContentAt(offset);
    }

    internal void ClickAt(double x, double y)
    {
        if (_map is null)
        {
            return;
        }

        int hit = HitTest(x, y);
        if (hit >= 0)
        {
            Select(hit);
        }

        NavigateRequested?.Invoke(this, OffsetAt(x));
    }

    internal void DoubleClickAt(double x, double y)
    {
        int hit = HitTest(x, y);
        if (hit >= 0)
        {
            Select(hit);
            RegionActivated?.Invoke(this, hit);
        }
    }

    /// <summary>領域の中央の x 座標 (テスト用。見えていなければ表示を動かす)。</summary>
    internal double CenterXOf(int index)
    {
        if (_map is not { } map)
        {
            return 0;
        }

        FileMapRegion r = map.Regions[index];
        long mid = r.Offset + (r.Length / 2);
        if (mid < _viewStart || mid >= _viewStart + _viewLength)
        {
            ScrollTo(mid - (_viewLength / 2));
        }

        return X(mid);
    }

    internal static double LaneCenterY(FileMapLane lane) => LaneTop(lane) + (LaneHeight / 2);

    internal string ToolTipAt(double x, double y)
    {
        int hit = HitTest(x, y);
        if (_map is not { } map || hit < 0)
        {
            return string.Empty;
        }

        FileMapRegion r = map.Regions[hit];
        string evidence = map.EvidenceOf(hit) is { Count: > 0 } ev ? FileMapVisuals.Text(ev[0]) : string.Empty;
        return string.Join(Environment.NewLine, FileMapVisuals.KindName(r.Kind) + " — " + FileMapVisuals.Label(map, hit), FileMapVisuals.Range(r),
            Loc.Format("FileMap_ConfidenceText", Loc.Get("FileMap_Confidence_" + r.Confidence)), evidence);
    }

    // ---- キーボード (仕様 4) ----

    /// <summary>キー 1 つ (テスト用の命令からも呼ぶ)。処理したら true。</summary>
    internal bool HandleKey(VirtualKey key)
    {
        if (_map is not { } map)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Left or VirtualKey.Right:
            {
                FileMapLane lane = SelectedRegion >= 0 ? map.Regions[SelectedRegion].Lane : FileMapLane.Content;
                IReadOnlyList<int> list = map.Lane(lane);
                if (list.Count == 0)
                {
                    return true;
                }

                int at = SelectedRegion >= 0 ? map.PositionInLane(SelectedRegion) : -1;
                int next = at < 0 ? 0 : Math.Clamp(at + (key == VirtualKey.Right ? 1 : -1), 0, list.Count - 1);
                Select(list[next]);
                return true;
            }

            case VirtualKey.Up or VirtualKey.Down:
            {
                FileMapLane lane = key == VirtualKey.Up ? FileMapLane.Structure : FileMapLane.Content;
                long offset = SelectedRegion >= 0 ? map.Regions[SelectedRegion].Offset : _editor?.Cursor ?? map.Start;
                IReadOnlyList<int> list = map.Lane(lane);
                if (list.Count == 0)
                {
                    return true;
                }

                // そのレーンで同じ位置を含む領域 (なければ最も近い領域)。
                int found = lane == FileMapLane.Content ? map.ContentAt(offset) : map.StructureAt(offset);
                if (found < 0 || map.Regions[found].Lane != lane)
                {
                    found = list.OrderBy(i => Math.Abs(map.Regions[i].Offset - offset)).First();
                }

                if (lane == FileMapLane.Structure && found >= 0 && map.Regions[found].Parent >= 0 && SelectedRegion >= 0
                    && map.Regions[SelectedRegion].Lane == FileMapLane.Content)
                {
                    // 内容のレーンから上へ移るときは、最上位の領域を選ぶ。
                    while (map.Regions[found].Parent >= 0)
                    {
                        found = map.Regions[found].Parent;
                    }
                }

                Select(found);
                return true;
            }

            case VirtualKey.Enter when SelectedRegion >= 0:
                RegionActivated?.Invoke(this, SelectedRegion);
                return true;
            case VirtualKey.Add or (VirtualKey)187:
                ZoomBy(2);
                return true;
            case VirtualKey.Subtract or (VirtualKey)189:
                ZoomBy(0.5);
                return true;
            case VirtualKey.Home:
                ZoomToFit();
                return true;
            default:
                return false;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>帯の中で領域を選ぶ (一覧で選んだときも呼ぶ)。見えていなければ表示を動かす。</summary>
    public void Select(int index)
    {
        if (_map is null || index >= _map.Regions.Count)
        {
            index = -1;
        }

        if (index == SelectedRegion)
        {
            return;
        }

        SelectedRegion = index;
        if (index >= 0 && _map is { } map)
        {
            FileMapRegion r = map.Regions[index];
            if (r.End <= _viewStart || r.Offset >= _viewStart + _viewLength)
            {
                ScrollTo(r.Offset - (_viewLength / 4));
            }
        }

        Redraw();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _peer?.RaiseSelected(index);
    }

    // ---- 描画 ----

    private void QueueRedraw()
    {
        if (_drawQueued)
        {
            return;
        }

        _drawQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _drawQueued = false;
            Redraw();
        });
    }

    /// <summary>描いた要素の情報 (テスト用): 種類ごとの塗りの矩形の数。</summary>
    internal Dictionary<string, int> DrawnRects { get; } = [];

    /// <summary>描き直した回数と最後の描画の時間 (テスト用)。</summary>
    internal (int Count, double LastMs) DrawStats { get; private set; }

    public void Redraw()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _canvas.Children.Clear();
        DrawnRects.Clear();
        _visibleItems = [];
        double width = _canvas.ActualWidth;
        if (_map is not { Length: > 0 } map || width <= 0)
        {
            _peer?.Invalidate();
            return;
        }

        _viewLength = Math.Clamp(_viewLength <= 0 ? map.Length : _viewLength, Math.Min(map.Length, 1), map.Length);
        _viewStart = Math.Clamp(_viewStart, map.Start, map.End - _viewLength);
        _syncing = true;
        _scroll.Minimum = map.Start;
        _scroll.Maximum = Math.Max(map.Start, map.End - _viewLength);
        _scroll.ViewportSize = _viewLength;
        _scroll.LargeChange = Math.Max(1, _viewLength * 0.9);
        _scroll.SmallChange = Math.Max(1, _viewLength / 20.0);
        _scroll.Value = _viewStart;
        _scroll.Visibility = _viewLength < map.Length ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;

        bool hc = HighContrast;
        Brush patternBrush = ChartSupport.Brush(FileMapVisuals.PatternBrushKey(hc), this);
        DrawContent(map, width, hc, patternBrush);
        DrawStructure(map, width, hc, patternBrush);
        DrawRuler(map, width);
        DrawOverlays(map, width, hc);
        _peer?.Invalidate();
        DrawStats = (DrawStats.Count + 1, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// 内容のレーン: ピクセルの列ごとに種類ごとのバイト数を数え、最も長い種類で描く (仕様 2)。同じ種類が続く列は 1 つの矩形にまとめる。
    /// 標本による推定の部分は薄い色と点の模様で描く。
    /// </summary>
    private void DrawContent(FileMapResult map, double width, bool hc, Brush patternBrush)
    {
        int columns = Math.Max(1, (int)Math.Ceiling(width));
        int kinds = Enum.GetValues<FileMapKind>().Length;
        long[] tally = new long[columns * kinds];
        long[] sampled = new long[columns];
        long[] covered = new long[columns];
        long viewEnd = _viewStart + _viewLength;
        double perColumn = (double)_viewLength / columns;
        foreach (int i in map.InRange(FileMapLane.Content, _viewStart, viewEnd))
        {
            _visibleItems.Add(i);
            FileMapRegion r = map.Regions[i];
            long s = Math.Max(r.Offset, _viewStart);
            long e = Math.Min(r.End, viewEnd);
            int c0 = Math.Clamp((int)((s - _viewStart) / perColumn), 0, columns - 1);
            int c1 = Math.Clamp((int)((e - 1 - _viewStart) / perColumn), 0, columns - 1);
            for (int c = c0; c <= c1; c++)
            {
                long cs = _viewStart + (long)(c * perColumn);
                long ce = c == columns - 1 ? viewEnd : _viewStart + (long)((c + 1) * perColumn);
                long overlap = Math.Max(1, Math.Min(e, ce) - Math.Max(s, cs));
                tally[(c * kinds) + (int)r.Kind] += overlap;
                covered[c] += overlap;
                if (r.Stage == FileMapStage.Sample)
                {
                    sampled[c] += overlap;
                }
            }
        }

        var fills = new Dictionary<(FileMapKind, bool), GeometryGroup>();
        var marks = new Dictionary<(FileMapKind, bool), GeometryGroup>();
        double top = LaneTop(FileMapLane.Content);
        int start = -1;
        (FileMapKind Kind, bool Sample) current = default;
        void Flush(int end)
        {
            if (start < 0)
            {
                return;
            }

            var rect = new Rect(start * width / columns, top, (end - start) * width / columns, LaneHeight);
            if (!fills.TryGetValue(current, out GeometryGroup? fill))
            {
                fills[current] = fill = new GeometryGroup();
                marks[current] = new GeometryGroup();
            }

            fill.Children.Add(new RectangleGeometry { Rect = rect });
            marks[current].Children.Add(ChartSupport.Pattern(FileMapVisuals.PatternOf(current.Kind), rect, 6));
            if (current.Sample)
            {
                marks[current].Children.Add(ChartSupport.Pattern(FileMapVisuals.SamplePattern, rect, 4));
            }

            string key = FileMapWords.Kind(current.Kind) + (current.Sample ? ".sample" : string.Empty);
            DrawnRects[key] = DrawnRects.GetValueOrDefault(key) + 1;
            start = -1;
        }

        for (int c = 0; c < columns; c++)
        {
            if (covered[c] == 0)
            {
                Flush(c);
                continue;
            }

            int best = 0;
            for (int k = 1; k < kinds; k++)
            {
                if (tally[(c * kinds) + k] > tally[(c * kinds) + best])
                {
                    best = k;
                }
            }

            (FileMapKind, bool) kind = ((FileMapKind)best, sampled[c] * 2 > covered[c]);
            if (start >= 0 && kind != current)
            {
                Flush(c);
            }

            if (start < 0)
            {
                start = c;
                current = kind;
            }
        }

        Flush(columns);
        foreach (((FileMapKind kind, bool sample), GeometryGroup fill) in fills)
        {
            _canvas.Children.Add(new Path { Data = fill, Fill = ChartSupport.Brush(FileMapVisuals.FillKey(kind, hc), this), Opacity = sample && !hc ? 0.45 : 1 });
            _canvas.Children.Add(new Path { Data = marks[(kind, sample)], Stroke = patternBrush, Fill = patternBrush, StrokeThickness = 1, Opacity = sample ? 0.7 : 1 });
        }
    }

    /// <summary>構造のレーン: どれかの領域があれば描く (入れ子は少し内側に)。</summary>
    private void DrawStructure(FileMapResult map, double width, bool hc, Brush patternBrush)
    {
        var groups = new Dictionary<FileMapKind, (GeometryGroup Fill, GeometryGroup Marks)>();
        Brush border = ChartSupport.Brush(hc ? "SystemColorWindowTextColorBrush" : "ControlStrongStrokeColorDefaultBrush", this);
        var outlines = new GeometryGroup();
        foreach (int i in map.InRange(FileMapLane.Structure, _viewStart, _viewStart + _viewLength))
        {
            _visibleItems.Add(i);
            FileMapRegion r = map.Regions[i];
            Rect rect = RegionBounds(i);
            rect.X = Math.Max(0, rect.X);
            rect.Width = Math.Min(width - rect.X, rect.Width);
            if (rect.Width <= 0)
            {
                continue;
            }

            if (!groups.TryGetValue(r.Kind, out var g))
            {
                groups[r.Kind] = g = (new GeometryGroup(), new GeometryGroup());
            }

            g.Fill.Children.Add(new RectangleGeometry { Rect = rect });
            g.Marks.Children.Add(ChartSupport.Pattern(FileMapVisuals.PatternOf(r.Kind), rect, 6));
            outlines.Children.Add(new RectangleGeometry { Rect = rect });
            string key = FileMapWords.Kind(r.Kind);
            DrawnRects[key] = DrawnRects.GetValueOrDefault(key) + 1;
        }

        foreach ((FileMapKind kind, (GeometryGroup fill, GeometryGroup marks)) in groups)
        {
            _canvas.Children.Add(new Path { Data = fill, Fill = ChartSupport.Brush(FileMapVisuals.FillKey(kind, hc), this) });
            _canvas.Children.Add(new Path { Data = marks, Stroke = patternBrush, Fill = patternBrush, StrokeThickness = 1 });
        }

        _canvas.Children.Add(new Path { Data = outlines, Stroke = border, StrokeThickness = 1 });
    }

    /// <summary>オフセットの目盛り (地域設定によらない 16 進)。</summary>
    private void DrawRuler(FileMapResult map, double width)
    {
        double top = (LaneHeight * 2) + LaneGap;
        Brush text = ChartSupport.Brush(HighContrast ? "SystemColorWindowTextColorBrush" : "TextFillColorSecondaryBrush", this);
        int ticks = Math.Clamp((int)(width / 140), 2, 10);
        var lines = new GeometryGroup();
        for (int t = 0; t <= ticks; t++)
        {
            double x = t * width / ticks;
            long offset = OffsetAt(x);
            lines.Children.Add(new LineGeometry { StartPoint = new Point(x, top), EndPoint = new Point(x, top + 4) });
            var label = new TextBlock
            {
                Text = "0x" + offset.ToString("X", CultureInfo.InvariantCulture),
                FontSize = 11,
                FontFamily = DialogParts.Mono,
                Foreground = text,
            };
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - (label.DesiredSize.Width / 2), 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, top + 3);
            _canvas.Children.Add(label);
        }

        _canvas.Children.Add(new Path { Data = lines, Stroke = text, StrokeThickness = 1 });
    }

    /// <summary>Hex ビューの表示中の範囲の枠、カーソルの縦線、帯の中で選んだ領域の枠。</summary>
    private void DrawOverlays(FileMapResult map, double width, bool hc)
    {
        Brush accent = ChartSupport.Brush(hc ? "SystemColorHighlightColorBrush" : "AccentFillColorDefaultBrush", this);
        Brush strong = ChartSupport.Brush(hc ? "SystemColorWindowTextColorBrush" : "TextFillColorPrimaryBrush", this);
        if (_editor is { } editor)
        {
            long from = editor.Layout.RowStart(editor.TopRow);
            long to = editor.Layout.RowStart(editor.TopRow + editor.VisibleRows);
            double x0 = Math.Clamp(X(from), 0, width);
            double x1 = Math.Clamp(X(to), 0, width);
            _viewport.Stroke = accent;
            _viewport.Width = Math.Max(2, x1 - x0);
            _viewport.Height = (LaneHeight * 2) + LaneGap;
            Canvas.SetLeft(_viewport, x0);
            Canvas.SetTop(_viewport, 0);
            _canvas.Children.Add(_viewport);
            double cx = X(editor.Cursor);
            if (cx >= 0 && cx <= width)
            {
                _cursor.Fill = strong;
                _cursor.Height = (LaneHeight * 2) + LaneGap;
                Canvas.SetLeft(_cursor, Math.Clamp(cx - 1, 0, width - 2));
                Canvas.SetTop(_cursor, 0);
                _canvas.Children.Add(_cursor);
            }
        }

        if (SelectedRegion >= 0 && SelectedRegion < map.Regions.Count)
        {
            Rect r = RegionBounds(SelectedRegion);
            _focus.Stroke = strong;
            _focus.Width = Math.Max(4, Math.Min(width, r.X + r.Width) - Math.Max(0, r.X));
            _focus.Height = r.Height;
            Canvas.SetLeft(_focus, Math.Max(0, r.X));
            Canvas.SetTop(_focus, r.Y);
            _canvas.Children.Add(_focus);
        }
    }

    // ---- UI オートメーション (仕様 10: 表示中の範囲の領域だけを要素にする) ----

    internal IReadOnlyList<int> VisibleItems => _visibleItems;

    internal Rect ToScreen(Rect local)
    {
        if (XamlRoot is null)
        {
            return local;
        }

        Rect bounds = _canvas.TransformToVisual(null).TransformBounds(local);
        double scale = XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (hwnd != 0)
        {
            ClientToScreen(hwnd, ref origin);
        }

        return new Rect(origin.X + (bounds.X * scale), origin.Y + (bounds.Y * scale), bounds.Width * scale, bounds.Height * scale);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
}

/// <summary>帯の UI オートメーション: 選べる一覧で、子は表示中の領域 (上限 4,096 個)。</summary>
public sealed partial class FileMapBandAutomationPeer(FileMapBand owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private const int MaxChildren = 4096;
    private List<FileMapRegionAutomationPeer>? _children;

    internal FileMapBand Band => owner;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    public IRawElementProviderSimple[] GetSelection() =>
        Children().FirstOrDefault(c => c.Region == owner.SelectedRegion) is { } p ? [ProviderFromPeer(p)] : [];

    internal void Invalidate() => _children = null;

    internal IRawElementProviderSimple Provider(AutomationPeer peer) => ProviderFromPeer(peer);

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(FileMapBand);

    protected override IList<AutomationPeer> GetChildrenCore() => [.. Children()];

    internal List<FileMapRegionAutomationPeer> Children()
    {
        if (_children is null)
        {
            _children = [.. owner.VisibleItems.Take(MaxChildren).Select(i => new FileMapRegionAutomationPeer(this, i))];
            if (owner.SelectedRegion >= 0 && !_children.Any(c => c.Region == owner.SelectedRegion))
            {
                _children.Add(new FileMapRegionAutomationPeer(this, owner.SelectedRegion));
            }
        }

        return _children;
    }

    internal AutomationPeer? Sibling(FileMapRegionAutomationPeer item, int delta)
    {
        List<FileMapRegionAutomationPeer> list = Children();
        int i = list.IndexOf(item) + delta;
        return i >= 0 && i < list.Count ? list[i] : null;
    }

    /// <summary>帯の中で選んだ領域を知らせる (選択とフォーカスの移動。ナレーターが読み上げる)。</summary>
    internal void RaiseSelected(int region)
    {
        Invalidate();
        if (Children().FirstOrDefault(c => c.Region == region) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            if (owner.FocusState != FocusState.Unfocused)
            {
                peer.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
            }
        }
    }
}

/// <summary>帯の領域 1 つ (名前に種類、ラベル、開始オフセット、長さ、確度)。</summary>
public sealed partial class FileMapRegionAutomationPeer(FileMapBandAutomationPeer band, int region) : AutomationPeer, ISelectionItemProvider, IInvokeProvider
{
    internal int Region => region;

    public bool IsSelected => band.Band.SelectedRegion == region;

    public IRawElementProviderSimple SelectionContainer => band.Provider(band);

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
    }

    public void Select() => band.Band.Select(region);

    public void Invoke()
    {
        band.Band.Select(region);
        band.Band.HandleKey(VirtualKey.Enter);
    }

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke ? this : null;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => "FileMapRegion";

    protected override string GetAutomationIdCore() => "FileMap_Region_" + region.ToString(CultureInfo.InvariantCulture);

    protected override string GetNameCore() => band.Band.Map is { } map && region < map.Regions.Count ? FileMapVisuals.RegionName(map, region) : string.Empty;

    protected override string GetItemStatusCore() => band.Band.Map is { } map && region < map.Regions.Count
        ? "pattern=" + FileMapVisuals.PatternOf(map.Regions[region].Kind) + (map.Regions[region].Stage == FileMapStage.Sample ? ";" + FileMapVisuals.SamplePattern : string.Empty)
        : string.Empty;

    protected override Rect GetBoundingRectangleCore() => band.Band.ToScreen(band.Band.RegionBounds(region));

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() => IsSelected && band.Band.FocusState != FocusState.Unfocused;

    protected override bool IsOffscreenCore() => band.Band.RegionBounds(region).Width <= 0;

    protected override bool IsEnabledCore() => true;

    protected override AutomationPeer GetLabeledByCore() => null!;

    protected override object? NavigateCore(AutomationNavigationDirection direction) => direction switch
    {
        AutomationNavigationDirection.Parent => band,
        AutomationNavigationDirection.NextSibling => band.Sibling(this, 1),
        AutomationNavigationDirection.PreviousSibling => band.Sibling(this, -1),
        _ => null,
    };

    protected override IList<AutomationPeer> GetChildrenCore() => [];
}
