using System.Runtime.InteropServices.WindowsRuntime;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace HexEditor.App.Controls.Charts;

/// <summary>
/// 件数のヒートマップ (ANA-14: ダイグラム 256 × 256、位置ごとのバイト分布 512 × 256)。1 枚の画像に描き、セルごとの UI 要素は作らない。
/// 濃さは件数の対数に比例させ、テーマの単色の濃淡で描く。ハイコントラストではシステム色で、8 段階のディザの模様にする (ANA-14 の仕様 5)。
/// 縦軸は下から上へ値が増える。キーボードでは矢印でセルを選び、Enter でダブルクリックと同じ動作をする。
/// </summary>
public sealed partial class HeatmapChart : UserControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(object), typeof(HeatmapChart),
        new PropertyMetadata(null, (d, _) => ((HeatmapChart)d).Render()));

    /// <summary>ハイコントラストの 4 × 4 のディザの順 (Bayer 行列)。</summary>
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Grid _root = new();
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _focus = new() { StrokeThickness = 1.5, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private (int X, int Y) _selected = (-1, -1);

    public HeatmapChart()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _root.Children.Add(_image);
        _root.Children.Add(_focus);
        _root.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        Content = _root;
        ActualThemeChanged += (_, _) => Render();
        SizeChanged += (_, _) => DrawFocus();
        PointerMoved += (_, e) =>
        {
            (int x, int y) = CellAt(e.GetCurrentPoint(this).Position);
            ChartSupport.SetToolTip(this, x >= 0 ? Describe?.Invoke(x, y) ?? string.Empty : string.Empty);
        };
        Tapped += (_, e) =>
        {
            Focus(FocusState.Pointer);
            (int x, int y) = CellAt(e.GetPosition(this));
            if (x >= 0)
            {
                Select(x, y);
                CellClicked?.Invoke(this, (x, y));
            }
        };
        DoubleTapped += (_, e) =>
        {
            (int x, int y) = CellAt(e.GetPosition(this));
            if (x >= 0)
            {
                CellInvoked?.Invoke(this, (x, y));
            }
        };
    }

    /// <summary>件数 ([x × Rows + y])。</summary>
    public object? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int Columns { get; set; } = 256;

    public int Rows { get; set; } = 256;

    /// <summary>セルの説明 (ツールチップと UI オートメーションの読み上げ)。</summary>
    public Func<int, int, string>? Describe { get; set; }

    /// <summary>スクリーンリーダー向けの要約。</summary>
    public string Summary
    {
        get => AutomationProperties.GetHelpText(this);
        set => AutomationProperties.SetHelpText(this, value);
    }

    public event EventHandler<(int X, int Y)>? CellClicked;

    public event EventHandler<(int X, int Y)>? CellInvoked;

    /// <summary>描画の情報 (テスト用): ハイコントラストの模様か、描いた画像の大きさ。</summary>
    internal string RenderMode { get; private set; } = string.Empty;

    private long[]? Data => Values as long[];

    /// <summary>セルの中央の点 (要素の座標。テストで位置を求める)。</summary>
    internal Point CellCenter(int x, int y) =>
        new((x + 0.5) * ActualWidth / Columns, (Rows - 1 - y + 0.5) * ActualHeight / Rows);

    internal (int X, int Y) CellAt(Point p)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || p.X < 0 || p.Y < 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
        {
            return (-1, -1);
        }

        int x = Math.Clamp((int)(p.X / ActualWidth * Columns), 0, Columns - 1);
        int y = Rows - 1 - Math.Clamp((int)(p.Y / ActualHeight * Rows), 0, Rows - 1);
        return (x, y);
    }

    private void Select(int x, int y)
    {
        _selected = (x, y);
        DrawFocus();
        string text = Describe?.Invoke(x, y) ?? string.Empty;
        AutomationProperties.SetName(this, text);
        ChartSupport.SetToolTip(this, text);
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, string.Empty, text);
        }
    }

    private void DrawFocus()
    {
        if (_selected.X < 0 || ActualWidth <= 0)
        {
            _focus.Visibility = Visibility.Collapsed;
            return;
        }

        double w = ActualWidth / Columns;
        double h = ActualHeight / Rows;
        _focus.Width = Math.Max(3, w);
        _focus.Height = Math.Max(3, h);
        _focus.Margin = new Thickness(_selected.X * w, (Rows - 1 - _selected.Y) * h, 0, 0);
        _focus.Stroke = ChartSupport.Brush("FocusStrokeColorOuterBrush", this);
        _focus.Visibility = Visibility.Visible;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        (int x, int y) = _selected.X < 0 ? (0, 0) : _selected;
        switch (e.Key)
        {
            case VirtualKey.Left:
                Select(Math.Max(0, x - 1), y);
                break;
            case VirtualKey.Right:
                Select(Math.Min(Columns - 1, x + 1), y);
                break;
            case VirtualKey.Up:
                Select(x, Math.Min(Rows - 1, y + 1));
                break;
            case VirtualKey.Down:
                Select(x, Math.Max(0, y - 1));
                break;
            case VirtualKey.Enter when _selected.X >= 0:
                CellInvoked?.Invoke(this, _selected);
                break;
            case VirtualKey.Space when _selected.X >= 0:
                CellClicked?.Invoke(this, _selected);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    /// <summary>画像を作る (セル 1 つを 2 × 2 ピクセル。ハイコントラストでは 4 × 4 のディザ)。</summary>
    private void Render()
    {
        if (Data is not { } values || values.Length < Columns * Rows)
        {
            _image.Source = null;
            return;
        }

        bool hc = ChartSupport.IsHighContrast;
        int cell = hc ? 4 : 2;
        int width = Columns * cell;
        int height = Rows * cell;
        long max = 0;
        foreach (long v in values)
        {
            max = Math.Max(max, v);
        }

        Windows.UI.Color fore = ChartSupport.Color(hc ? "TextFillColorPrimaryBrush" : "AccentFillColorDefaultBrush", this);
        Windows.UI.Color back = ChartSupport.Color(hc ? "SolidBackgroundFillColorBaseBrush" : "LayerFillColorDefaultBrush", this);
        if (back.A == 0)
        {
            back = ChartSupport.Color("SolidBackgroundFillColorBaseBrush", this);
        }

        byte[] pixels = new byte[width * height * 4];
        double logMax = Math.Log(max + 1);
        for (int x = 0; x < Columns; x++)
        {
            for (int y = 0; y < Rows; y++)
            {
                long v = values[(x * Rows) + y];
                double t = v == 0 || max == 0 ? 0 : Math.Log(v + 1) / logMax;
                int top = (Rows - 1 - y) * cell;
                int left = x * cell;
                if (hc)
                {
                    // 8 段階に減らし、段階ごとにディザの模様を変える。
                    int level = v == 0 ? 0 : 1 + (int)Math.Min(7, t * 7.999);
                    for (int k = 0; k < 16; k++)
                    {
                        bool on = Bayer[k] < level * 2;
                        Put(pixels, width, left + (k % 4), top + (k / 4), on ? fore : back);
                    }
                }
                else
                {
                    var c = Blend(back, fore, t);
                    for (int dy = 0; dy < cell; dy++)
                    {
                        for (int dx = 0; dx < cell; dx++)
                        {
                            Put(pixels, width, left + dx, top + dy, c);
                        }
                    }
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        _image.Source = bitmap;
        RenderMode = hc ? "highContrastDither8" : "logShade";
    }

    private static Windows.UI.Color Blend(Windows.UI.Color a, Windows.UI.Color b, double t) => new()
    {
        A = 255,
        R = (byte)(a.R + ((b.R - a.R) * t)),
        G = (byte)(a.G + ((b.G - a.G) * t)),
        B = (byte)(a.B + ((b.B - a.B) * t)),
    };

    private static void Put(byte[] pixels, int width, int x, int y, Windows.UI.Color c)
    {
        int i = ((y * width) + x) * 4;
        pixels[i] = c.B;
        pixels[i + 1] = c.G;
        pixels[i + 2] = c.R;
        pixels[i + 3] = 255;
    }

    /// <summary>テスト用: セルの上にマウスを置いたときのツールチップの文字列。</summary>
    internal string HoverText(int x, int y)
    {
        string text = Describe?.Invoke(x, y) ?? string.Empty;
        ChartSupport.SetToolTip(this, text);
        return text;
    }

    internal void InvokeCell(int x, int y) => CellInvoked?.Invoke(this, (x, y));

    internal void ClickCell(int x, int y)
    {
        Select(x, y);
        CellClicked?.Invoke(this, (x, y));
    }
}
