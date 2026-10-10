using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>
/// 差分マップ (ANA-04 の仕様 5): 比較ビューの右端の縦の帯。比較範囲全体のうち差分のある位置を、種類ごとの色の線で示す。帯の 1 ピクセルに
/// 複数の差分が入る場合は、最も多い (バイト数の多い) 種類で描く。色だけに頼らないよう、種類で線の位置も変える (変更は全幅、挿入は左半分、
/// 削除は右半分、読み込み不可は中央の細い線)。クリックするとその位置へ移動する。ピクセルごとの種類はバックグラウンドで求め、求め終わるまでは
/// 前の表示を薄くして仮表示する (ANA-04 の「巨大ファイル」)。
/// </summary>
public sealed partial class DiffMap : Grid
{
    /// <summary>1 ピクセルあたりに調べる差分の上限 (件数に比例する時間にしない)。</summary>
    private const int PerPixelLimit = 64;

    private readonly Canvas _canvas = new();
    private readonly Rectangle _viewport = new() { IsHitTestVisible = false };
    private readonly List<Rectangle> _lines = [];
    private CompareSessionViewModel? _session;
    private bool _right;
    private int _generation;
    private bool _pending;

    public DiffMap()
    {
        Width = 16;
        Background = (Brush)Application.Current.Resources["CompareMapBackgroundBrush"];
        Children.Add(_canvas);
        _canvas.Children.Add(_viewport);
        PointerPressed += DiffMap_PointerPressed;
        SizeChanged += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => Refresh();
    }

    /// <summary>描いた線 (テスト用): 種類と上端・高さ。</summary>
    internal List<(DiffKind Kind, double Top, double Height)> Placed { get; } = [];

    /// <summary>仮表示中か (テスト用)。</summary>
    internal bool IsProvisional => _canvas.Opacity < 1;

    public void Attach(CompareSessionViewModel session, bool right)
    {
        _session = session;
        _right = right;
        AutomationProperties.SetAutomationId(this, right ? "Compare_RightMap" : "Compare_LeftMap");
        AutomationProperties.SetName(this, Loc.Get(right ? "Compare_RightMap_Name" : "Compare_LeftMap_Name"));
        Refresh();
    }

    /// <summary>差分が変わった: バックグラウンドで描き直す。</summary>
    public void Refresh()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _pending = false;
            _ = RedrawAsync();
        });
    }

    /// <summary>表示範囲 (見えている行) の印を動かす。</summary>
    public void UpdateViewport()
    {
        if (_session?.Result is not { } r || ActualHeight <= 0)
        {
            _viewport.Visibility = Visibility.Collapsed;
            return;
        }

        CompareSideViewModel side = _right ? _session.Right : _session.Left;
        CompareRange range = _right ? r.Right : r.Left;
        long start = side.Editor.Layout.RowStart(side.Editor.TopRow);
        long end = start + (long)side.Editor.VisibleRows * side.Editor.BytesPerRow;
        double length = Math.Max(1, range.Length);
        double top = Math.Clamp((start - range.Start) / length, 0, 1) * ActualHeight;
        double bottom = Math.Clamp((end - range.Start) / length, 0, 1) * ActualHeight;
        _viewport.Visibility = Visibility.Visible;
        _viewport.Width = ActualWidth;
        _viewport.Height = Math.Max(2, bottom - top);
        _viewport.Fill = AnnotationBrushes.Get("CompareMapViewportBrush", this, IsHighContrast);
        Canvas.SetTop(_viewport, top);
    }

    /// <summary>ハイコントラストか (隣の Hex ビューの判定を使う。テスト用の模擬を含む)。</summary>
    public Func<bool>? HighContrastProvider { get; set; }

    private bool IsHighContrast => HighContrastProvider?.Invoke() ?? false;

    private async Task RedrawAsync()
    {
        if (_session?.Result is not { } r || ActualHeight <= 0)
        {
            return;
        }

        int generation = ++_generation;
        int pixels = (int)Math.Ceiling(ActualHeight);
        bool right = _right;
        CompareRange range = right ? r.Right : r.Left;
        _canvas.Opacity = 0.4;
        DiffKind?[] kinds = await Task.Run(() => Classify(r.Diffs, right, range, pixels));
        if (generation != _generation || !ReferenceEquals(_session?.Result, r))
        {
            return;
        }

        Placed.Clear();
        int used = 0;
        bool highContrast = IsHighContrast;
        for (int y = 0; y < pixels;)
        {
            if (kinds[y] is not { } kind)
            {
                y++;
                continue;
            }

            int end = y + 1;
            while (end < pixels && kinds[end] == kind)
            {
                end++;
            }

            (double left, double width) = kind switch
            {
                DiffKind.Inserted => (0.0, ActualWidth / 2),
                DiffKind.Deleted => (ActualWidth / 2, ActualWidth / 2),
                DiffKind.Unreadable => (ActualWidth / 2 - 1, 2.0),
                _ => (0.0, ActualWidth),
            };
            Rectangle line = Take(used++);
            line.Fill = CompareBrushes.Mark(kind, this, highContrast);
            line.Width = width;
            line.Height = Math.Max(2, end - y);
            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, y);
            Placed.Add((kind, y, end - y));
            y = end;
        }

        for (int i = used; i < _lines.Count; i++)
        {
            _lines[i].Visibility = Visibility.Collapsed;
        }

        _canvas.Opacity = 1;
        UpdateViewport();
    }

    /// <summary>ピクセルごとに、その範囲に重なる差分のうちバイト数の最も多い種類 (なければ null)。</summary>
    private static DiffKind?[] Classify(DiffStore diffs, bool right, CompareRange range, int pixels)
    {
        var kinds = new DiffKind?[pixels];
        double length = Math.Max(1, range.Length);
        Span<long> bytes = stackalloc long[4];
        for (int y = 0; y < pixels; y++)
        {
            long from = range.Start + (long)Math.Floor(length * y / pixels);
            long to = Math.Max(from + 1, range.Start + (long)Math.Floor(length * (y + 1) / pixels));
            bytes.Clear();
            bool any = false;
            foreach ((_, DiffRange d) in diffs.Overlapping(right, from, to, PerPixelLimit))
            {
                long s = Math.Max(from, d.Start(right));
                long e = Math.Min(to, d.Start(right) + d.Length(right));
                bytes[(int)d.Kind] += Math.Max(1, e - s);
                any = true;
            }

            if (any)
            {
                int best = 0;
                for (int k = 1; k < 4; k++)
                {
                    if (bytes[k] > bytes[best])
                    {
                        best = k;
                    }
                }

                kinds[y] = (DiffKind)best;
            }
        }

        return kinds;
    }

    private Rectangle Take(int index)
    {
        if (index >= _lines.Count)
        {
            var line = new Rectangle { IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            _lines.Add(line);
            _canvas.Children.Insert(_canvas.Children.Count - 1, line);
        }

        Rectangle r = _lines[index];
        r.Visibility = Visibility.Visible;
        return r;
    }

    private void DiffMap_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ActualHeight > 0)
        {
            Click(e.GetCurrentPoint(this).Position.Y / ActualHeight);
            e.Handled = true;
        }
    }

    /// <summary>帯の高さの <paramref name="fraction"/> の位置をクリックした (テスト用の命令からも呼ぶ)。</summary>
    public void Click(double fraction) => _session?.JumpToFraction(_right, fraction);
}
