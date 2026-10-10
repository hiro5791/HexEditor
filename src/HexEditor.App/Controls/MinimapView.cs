using System.Runtime.InteropServices.WindowsRuntime;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>
/// ミニマップ (VIEW-35)。Hex ビューの縦スクロールバーの左に置く縦長の帯で、ピクセル行ごとにデータの性質を色と棒の長さで描く
/// (値は <see cref="MinimapComputer"/> がバックグラウンドで求める)。右端の幅 6 epx の帯に、表示中の範囲・カーソル・選択範囲・検索結果・
/// ブックマーク・変更されたバイトの印を重ねる。クリックでその位置にジャンプし (ジャンプ履歴に記録)、表示中の範囲の枠のドラッグで
/// カーソルを動かさずにスクロールする。
/// </summary>
public sealed partial class MinimapView : Grid
{
    /// <summary>印の帯の幅 (仕様 6)。</summary>
    public const double MarkBand = 6;

    /// <summary>編集の後に計算し直すまでの待ち (仕様 8)。</summary>
    private static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(500);

    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Rectangle _viewport;
    private readonly MinimapComputer _computer = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _recompute;
    private readonly List<Shape> _markShapes = [];
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private EditorState? _editor;
    private bool _drawQueued;
    private bool _draggingViewport;
    private double _dragOffset;
    private ToolTip? _toolTip;
    private MenuFlyout? _menu;
    private (long Offset, long Length)? _pendingEdit;

    public MinimapView()
    {
        AutomationProperties.SetAutomationId(this, "Minimap");
        AutomationProperties.SetName(this, Services.Loc.Get("Menu_View_Minimap/Text"));
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        Children.Add(_image);
        Children.Add(_marks);
        _viewport = new Rectangle
        {
            StrokeThickness = 1,
            Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Fill = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            IsHitTestVisible = false,
        };
        _marks.Children.Add(_viewport);
        _recompute = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _recompute.IsRepeating = false;
        _recompute.Interval = RecomputeDelay;
        _recompute.Tick += (_, _) => ApplyPendingEdit();
        _computer.Progress += (_, _) => DispatcherQueue.TryEnqueue(QueueDraw);
        SizeChanged += (_, _) => Restart();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) =>
        {
            _draggingViewport = false;
            ReleasePointerCapture(e.Pointer);
        };
        PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            _editor?.ScrollRows(-delta / 120 * 3);
            e.Handled = true;
        };
        PointerExited += (_, _) => HideToolTip();
        ContextRequested += (_, e) =>
        {
            ShowMenu(e.TryGetPosition(this, out Windows.Foundation.Point p) ? p : new Windows.Foundation.Point(0, 0));
            e.Handled = true;
        };
        Unloaded += (_, _) => _computer.Stop();
        ActualThemeChanged += (_, _) => QueueDraw();
    }

    /// <summary>表示内容 (仕様 3)。</summary>
    public MinimapContent Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                QueueDraw();
            }
        }
    }

    private MinimapContent _content = MinimapContent.Entropy;

    /// <summary>範囲 (仕様 2)。</summary>
    public MinimapRange Range
    {
        get => _range;
        set
        {
            if (_range != value)
            {
                _range = value;
                Restart();
            }
        }
    }

    private MinimapRange _range = MinimapRange.Whole;

    /// <summary>ハイコントラスト (色を使わず WindowText の棒の長さだけで示す。仕様 4)。</summary>
    public bool HighContrast { get; set; }

    /// <summary>バイトテーマ (表示内容「バイトテーマ」で使う)。</summary>
    public ByteTheme? ByteTheme { get; set; }

    /// <summary>表示している Hex ビューのビュー。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            if (_editor is not null)
            {
                _editor.Changed -= Editor_Changed;
                _editor.Document.Changed -= Document_Changed;
            }

            _editor = value;
            if (_editor is not null)
            {
                _editor.Changed += Editor_Changed;
                _editor.Document.Changed += Document_Changed;
            }

            Restart(force: true);
        }
    }

    /// <summary>計算 (テスト・「正確に計算」で使う)。</summary>
    public MinimapComputer Computer => _computer;

    /// <summary>印の色と位置の提供元 (Hex ビューが設定する)。</summary>
    internal Func<MinimapMarks>? Marks { get; set; }

    /// <summary>ミニマップの右クリックメニューの項目を作る (ウィンドウが表示内容・範囲・印・「正確に計算」の項目を加える)。</summary>
    public Action<MenuFlyout>? MenuOpening { get; set; }

    /// <summary>ミニマップのクリックで移動した (Hex ビューがフォーカスを戻す)。</summary>
    public event EventHandler? Navigated;

    /// <summary>ピクセル行の数 (物理ピクセル)。</summary>
    private int PixelRows => Math.Max(1, (int)Math.Round(ActualHeight * Scale));

    private int PixelWidth => Math.Max(1, (int)Math.Round(ActualWidth * Scale));

    private double Scale => XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;

    private void Editor_Changed(object? sender, EventArgs e)
    {
        if (_range == MinimapRange.Around)
        {
            Restart();
        }

        UpdateMarks();
    }

    private void Document_Changed(object? sender, DocumentChangedEventArgs e)
    {
        // 変わった範囲に対応するピクセル行だけを、500 ms 待ってまとめて計算し直す (仕様 8)。
        long offset = e.IsWholeDocument ? 0 : e.Offset;
        long length = e.IsWholeDocument || e.RemovedLength != e.InsertedLength ? -1 : Math.Max(1, e.InsertedLength);
        _pendingEdit = _pendingEdit is { } p
            ? (Math.Min(p.Offset, offset), p.Length < 0 || length < 0 ? -1 : Math.Max(p.Offset + p.Length, offset + length) - Math.Min(p.Offset, offset))
            : (offset, length);
        _recompute.Stop();
        _recompute.Start();
    }

    private void ApplyPendingEdit()
    {
        if (_pendingEdit is not { } edit || _editor is null || _editor.Document.IsDisposed)
        {
            return;
        }

        _pendingEdit = null;
        if (edit.Length < 0 || _computer.RowCount == 0)
        {
            Restart(force: true);
            return;
        }

        _computer.Invalidate(_editor.Document.Current, edit.Offset, edit.Length);
        QueueDraw();
    }

    /// <summary>割り当てを決め直して計算を始める。</summary>
    public void Restart(bool force = false)
    {
        if (_editor is null || _editor.Document.IsDisposed || ActualHeight <= 0 || Visibility != Visibility.Visible)
        {
            _computer.Stop();
            return;
        }

        DocumentSnapshot snapshot = _editor.Document.Current;
        (long first, long rowBytes, int count) = _range == MinimapRange.Around
            ? MinimapComputer.Around(snapshot.Length, PixelRows, _editor.Layout, _editor.TopRow, _editor.VisibleRows)
            : MinimapComputer.Whole(snapshot.Length, PixelRows, _editor.BytesPerRow);
        _computer.Start(snapshot, first, rowBytes, count, force);
        QueueDraw();
    }

    private void QueueDraw()
    {
        if (_drawQueued)
        {
            return;
        }

        _drawQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _drawQueued = false;
            Draw();
        });
    }

    // ---- 描画 ----

    /// <summary>ピクセル行の値の色 (仕様 3 の表)。ハイコントラストでは WindowText の棒 (仕様 4)。</summary>
    internal SchemeColor RowColor(MinimapStats stats)
    {
        if (HighContrast)
        {
            return ThemeColor("SystemColorWindowTextColor");
        }

        return _content switch
        {
            MinimapContent.Entropy => EntropyColor(stats.Entropy / 8),
            MinimapContent.ByteValue => ByteTheme.GradientColor(stats.Mean / 255),
            MinimapContent.Zero => ThemeColor("SystemAccentColor"),
            _ => ThemeColor("SystemAccentColor"),
        };
    }

    /// <summary>エントロピーの配色: 低 (青系) → 高 (赤系) の連続した色。</summary>
    internal static SchemeColor EntropyColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        // 青 (0x2B, 0x5C, 0xD6) → 紫 → 赤 (0xD6, 0x2B, 0x2B)。データの配色 (コードの UI の色ではない)。
        return new SchemeColor(0xFF, (byte)(0x2B + (0xD6 - 0x2B) * t), (byte)(0x5C + (0x2B - 0x5C) * t), (byte)(0xD6 + (0x2B - 0xD6) * t));
    }

    private SchemeColor ThemeColor(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Windows.UI.Color c)
        {
            return new SchemeColor(c.A, c.R, c.G, c.B);
        }

        return new SchemeColor(0xFF, 0x80, 0x80, 0x80);
    }

    private SchemeColor BackgroundColor() => Background is SolidColorBrush s
        ? new SchemeColor(s.Color.A, s.Color.R, s.Color.G, s.Color.B)
        : new SchemeColor(0xFF, 0xFF, 0xFF, 0xFF);

    private void Draw()
    {
        if (_editor is null || ActualHeight <= 0 || ActualWidth <= 0)
        {
            return;
        }

        int width = PixelWidth;
        int height = PixelRows;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _pixels = new byte[width * height * 4];
            _image.Source = _bitmap;
        }

        int barArea = Math.Max(1, width - (int)Math.Round(MarkBand * Scale));
        SchemeColor back = BackgroundColor();
        SchemeColor gray = ThemeColor("SystemColorGrayTextColor");
        bool dark = ActualTheme == ElementTheme.Dark;
        for (int y = 0; y < height; y++)
        {
            int row = _computer.RowCount == 0 ? -1 : (int)((long)y * _computer.RowCount / height);
            MinimapStats? stats = row >= 0 ? _computer.Row(row) : null;
            for (int x = 0; x < width; x++)
            {
                SchemeColor c = back;
                if (row < 0 || row >= _computer.RowCount)
                {
                    // ドキュメントの外。
                }
                else if (stats is null)
                {
                    // 計算の終わっていないピクセル行は灰色の点線 (「巨大ファイル」)。
                    if (x < barArea && x % 4 < 2)
                    {
                        c = gray;
                    }
                }
                else if (stats.Unreadable && (x + y) % 4 == 0)
                {
                    // 読み取れない範囲を含むピクセル行は斜線の模様 (「エラー」)。
                    c = gray;
                }
                else if (x < barArea)
                {
                    c = PixelColor(stats, x, barArea, dark, back);
                }

                int i = (y * width + x) * 4;
                _pixels[i] = c.B;
                _pixels[i + 1] = c.G;
                _pixels[i + 2] = c.R;
                _pixels[i + 3] = 0xFF;
            }
        }

        using (Stream stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(_pixels, 0, _pixels.Length);
        }

        _bitmap.Invalidate();
        UpdateMarks();
    }

    private SchemeColor PixelColor(MinimapStats stats, int x, int barArea, bool dark, SchemeColor back)
    {
        if (_content == MinimapContent.ByteKinds && !HighContrast)
        {
            // 種類ごとの色を割合に応じて横に並べる (全幅)。
            double at = (double)x / barArea;
            double sum = 0;
            for (int k = 0; k < stats.Kinds.Length; k++)
            {
                sum += stats.Kinds[k];
                if (at < sum)
                {
                    return ByteTheme.CategoryColor((ByteCategory)k, dark);
                }
            }

            return back;
        }

        double bar = stats.BarOf(_content);
        return x < (int)Math.Round(bar * barArea) ? RowColor(stats) : back;
    }

    /// <summary>印の帯と表示中の範囲の枠 (仕様 6)。</summary>
    private void UpdateMarks()
    {
        if (_editor is null || ActualHeight <= 0 || Marks?.Invoke() is not { } marks)
        {
            return;
        }

        int used = 0;
        double height = ActualHeight;
        double band = MarkBand;
        double left = Math.Max(0, ActualWidth - band);
        _placedMarks.Clear();

        // 表示中の範囲 (半透明の枠)。
        (double top, double bottom) = Span(_editor.TopOffset, _editor.Layout.RowStart(_editor.TopRow + _editor.VisibleRows));
        Canvas.SetLeft(_viewport, 0);
        Canvas.SetTop(_viewport, top);
        _viewport.Width = ActualWidth;
        _viewport.Height = Math.Max(2, bottom - top);
        _placedMarks.Add(("viewport", top, Math.Max(2, bottom - top)));

        if (marks.ShowSelection && _editor.HasSelection)
        {
            (double t, double b) = Span(_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
            Place(ref used, left, t, band, Math.Max(2, b - t), marks.Selection, "selection", isLine: false);
        }

        if (marks.ShowSearch)
        {
            var seen = new HashSet<int>();
            foreach (long offset in marks.Search)
            {
                double y = Y(offset);
                if (seen.Add((int)y) && used < 2000)
                {
                    Place(ref used, left, y, band, 1, marks.SearchBrush, "search", isLine: true);
                }
            }
        }

        if (marks.ShowBookmarks)
        {
            foreach ((long offset, Brush brush) in marks.Bookmarks)
            {
                Place(ref used, left, Y(offset) - 2, band, 5, brush, "bookmark", isLine: false, triangle: true);
            }
        }

        if (marks.ShowModified)
        {
            foreach ((long offset, long length) in marks.Modified)
            {
                (double t, double b) = Span(offset, offset + length);
                Place(ref used, left, t, 1.5, Math.Max(1, b - t), marks.ModifiedBrush, "modified", isLine: false);
            }
        }

        if (marks.ShowDifferences)
        {
            foreach ((long offset, long length) in marks.Differences)
            {
                (double t, double b) = Span(offset, offset + length);
                Place(ref used, left, t, band, Math.Max(1, b - t), marks.DifferenceBrush, "difference", isLine: false);
            }
        }

        if (marks.ShowCursor)
        {
            Place(ref used, 0, Y(_editor.Cursor), ActualWidth, 1, marks.CursorBrush, "cursor", isLine: true);
        }

        for (int i = used; i < _markShapes.Count; i++)
        {
            _markShapes[i].Visibility = Visibility.Collapsed;
        }
    }

    private readonly List<(string Kind, double Top, double Height)> _placedMarks = [];

    private void Place(ref int used, double x, double y, double width, double height, Brush brush, string kind, bool isLine, bool triangle = false)
    {
        Shape shape;
        if (used < _markShapes.Count && (_markShapes[used] is Polygon) == triangle)
        {
            shape = _markShapes[used];
        }
        else
        {
            shape = triangle
                ? new Polygon { IsHitTestVisible = false, Points = { new(0, 0), new(width, height / 2), new(0, height) } }
                : new Rectangle { IsHitTestVisible = false };
            if (used < _markShapes.Count)
            {
                _marks.Children.Remove(_markShapes[used]);
                _markShapes[used] = shape;
            }
            else
            {
                _markShapes.Add(shape);
            }

            _marks.Children.Add(shape);
        }

        used++;
        shape.Visibility = Visibility.Visible;
        shape.Fill = brush;
        if (shape is Rectangle)
        {
            shape.Width = width;
            shape.Height = height;
        }

        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, Math.Clamp(y, 0, Math.Max(0, ActualHeight - (isLine ? 1 : 0))));
        _placedMarks.Add((kind, y, height));
    }

    /// <summary>オフセットの y 座標 (epx)。</summary>
    private double Y(long offset)
    {
        if (_computer.RowCount == 0)
        {
            return 0;
        }

        double rows = _computer.RowCount;
        double row = (double)(offset - _computer.First) / _computer.RowBytes;
        return Math.Clamp(row / rows * ActualHeight, 0, ActualHeight);
    }

    private (double Top, double Bottom) Span(long from, long to) => (Y(from), Math.Max(Y(from) + 1, Y(to)));

    /// <summary>y 座標 (epx) のピクセル行。</summary>
    private int RowAt(double y) => _computer.RowCount == 0 ? -1 : Math.Clamp((int)(y / ActualHeight * _computer.RowCount), 0, _computer.RowCount - 1);

    // ---- 操作 (仕様 7) ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        double y = point.Position.Y;
        double top = Canvas.GetTop(_viewport);
        if (y >= top && y <= top + _viewport.Height)
        {
            // 表示中の範囲の枠のドラッグ: カーソルを動かさずにスクロールする。
            _draggingViewport = true;
            _dragOffset = y - top;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        ClickAt(y);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        double y = e.GetCurrentPoint(this).Position.Y;
        if (_draggingViewport && _editor is not null)
        {
            int row = RowAt(y - _dragOffset);
            if (row >= 0)
            {
                _editor.ScrollToRow(_editor.Layout.RowOf(_computer.RangeOf(row).Start));
            }

            return;
        }

        ShowToolTipAt(y);
    }

    /// <summary>
    /// クリック: その位置にカーソルを移し、ジャンプとして表示する (VIEW-34。ジャンプ履歴に記録する)。テスト用の命令からも呼ぶ。
    /// </summary>
    internal void ClickAt(double y)
    {
        if (_editor is null || RowAt(y) is var row && row < 0)
        {
            return;
        }

        _editor.GoTo(_computer.RangeOf(row).Start);
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>マウスを合わせたときのツールチップ: 「0x1A000000〜0x1A01FFFF エントロピー 7.98」。</summary>
    internal string? ToolTipText(double y)
    {
        int row = RowAt(y);
        if (row < 0 || _editor is null)
        {
            return null;
        }

        (long start, long length) = _computer.RangeOf(row);
        string range = _editor.OffsetFormat.Status(start, System.Globalization.CultureInfo.CurrentCulture) + "〜"
            + _editor.OffsetFormat.Status(Math.Max(start, start + length - 1), System.Globalization.CultureInfo.CurrentCulture);
        if (_computer.Row(row) is not { } stats)
        {
            return range;
        }

        string value = _content switch
        {
            MinimapContent.ByteValue => Services.Loc.Format("Minimap_TipMean", stats.Mean.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)),
            MinimapContent.Zero => Services.Loc.Format("Minimap_TipNonZero", (stats.NonZero * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture)),
            _ => Services.Loc.Format("Minimap_TipEntropy", stats.Entropy.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture)),
        };
        return range + " " + value;
    }

    private void ShowToolTipAt(double y)
    {
        if (ToolTipText(y) is not { } text)
        {
            HideToolTip();
            return;
        }

        _toolTip ??= new ToolTip();
        AutomationProperties.SetAutomationId(_toolTip, "MinimapToolTip");
        _toolTip.Content = text;
        ToolTipService.SetToolTip(this, _toolTip);
        _toolTip.IsOpen = true;
    }

    private void HideToolTip()
    {
        if (_toolTip is not null)
        {
            _toolTip.IsOpen = false;
        }
    }

    private void ShowMenu(Windows.Foundation.Point position)
    {
        _menu ??= new MenuFlyout();
        AutomationProperties.SetAutomationId(_menu, "MinimapMenu");
        MenuOpening?.Invoke(_menu);
        _menu.ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
    }

    // ---- テスト用の読み出し ----

    /// <summary>
    /// 描画モデル (テスト用): ピクセル行ごとの範囲・値・色・棒の長さ (幅に対する割合)、計算済みの行数、印、表示中の範囲。
    /// </summary>
    internal System.Text.Json.Nodes.JsonObject ReadModel()
    {
        var rows = new System.Text.Json.Nodes.JsonArray();
        bool dark = ActualTheme == ElementTheme.Dark;
        for (int i = 0; i < _computer.RowCount; i++)
        {
            (long start, long length) = _computer.RangeOf(i);
            MinimapStats? s = _computer.Row(i);
            rows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["start"] = start,
                ["length"] = length,
                ["value"] = s?.ValueOf(_content),
                ["bar"] = s?.BarOf(_content),
                ["color"] = s is null ? null : (_content == MinimapContent.ByteKinds && !HighContrast
                    ? ByteTheme.CategoryColor((ByteCategory)Array.IndexOf(s.Kinds, s.Kinds.Max()), dark) : RowColor(s)).ToString(),
                ["unreadable"] = s?.Unreadable,
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["visible"] = Visibility == Visibility.Visible,
            ["width"] = ActualWidth,
            ["height"] = ActualHeight,
            ["content"] = _content.ToString(),
            ["range"] = _range.ToString(),
            ["computed"] = _computer.Computed,
            ["total"] = _computer.RowCount,
            ["exact"] = _computer.IsExact,
            ["highContrast"] = HighContrast,
            ["rowBytes"] = _computer.RowBytes,
            ["rows"] = rows,
            ["marks"] = new System.Text.Json.Nodes.JsonArray([.. _placedMarks.Select(m => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = m.Kind, ["top"] = m.Top, ["height"] = m.Height,
            })]),
            ["menuOpen"] = _menu?.IsOpen ?? false,
        };
    }
}

/// <summary>ミニマップの印 (VIEW-35 の仕様 6)。各印の ON / OFF と、その位置。</summary>
internal sealed record MinimapMarks(
    bool ShowCursor,
    bool ShowSelection,
    bool ShowSearch,
    bool ShowBookmarks,
    bool ShowModified,
    bool ShowDifferences,
    IReadOnlyList<long> Search,
    IReadOnlyList<(long Offset, Brush Brush)> Bookmarks,
    IReadOnlyList<(long Offset, long Length)> Modified,
    IReadOnlyList<(long Offset, long Length)> Differences,
    Brush CursorBrush,
    Brush Selection,
    Brush SearchBrush,
    Brush ModifiedBrush,
    Brush DifferenceBrush);
