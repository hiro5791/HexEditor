using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// 画像表示のパネル (VIEW-36)。設定の入力と画像の描画・マウスとキーの操作だけを持ち、範囲の計算と画素は <see cref="VisualizerViewModel"/> が作る。
/// 拡大は補間なし (画素を拡大率の正方形にして描く)。
/// </summary>
public sealed partial class VisualizerPanel : UserControl, Panels.IPanelContent
{
    private bool _syncing;

    /// <summary>キーボードで選んでいる画素 (矢印で動かし、Enter でカーソルを移す)。</summary>
    private (int X, int Y) _keyPixel;

    public VisualizerPanel(VisualizerViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Visualizer_PanelName"));
        foreach (BytePixelFormat f in VisualizerViewModel.Formats)
        {
            FormatChoice.Items.Add(Loc.Get("Visualizer_Format_" + f));
        }

        foreach (string key in new[] { "Visualizer_StartFollow", "Visualizer_StartSelection", "Visualizer_StartFixed" })
        {
            StartChoice.Items.Add(Loc.Get(key));
        }

        OrientationChoice.Items.Add(Loc.Get("Visualizer_TopDown"));
        OrientationChoice.Items.Add(Loc.Get("Visualizer_BottomUp"));
        foreach (int z in VisualizerViewModel.Zooms)
        {
            ZoomChoice.Items.Add(z == 0 ? Loc.Get("Visualizer_ZoomAuto") : Loc.Format("Visualizer_ZoomTimes", z));
        }

        Label(FormatChoice, "Visualizer_FormatName");
        Label(WidthBox, "Visualizer_WidthName");
        Label(StrideBox, "Visualizer_StrideName");
        Label(StartChoice, "Visualizer_StartName");
        Label(StartBox, "Visualizer_StartOffsetName");
        Label(OrientationChoice, "Visualizer_OrientationName");
        Label(ZoomChoice, "Visualizer_ZoomName");
        Label(MenuButton, "Visualizer_MenuName");
        Label(Viewport, "Visualizer_ImageName");
        Label(RowScroll, "Visualizer_ScrollName");
        SaveItem.Text = Loc.Get("Visualizer_SaveImage");
        StrideBox.PlaceholderText = Loc.Get("Visualizer_StrideAuto");
        SyncFromVm();
        Loaded += (_, _) =>
        {
            Vm.Rendered -= Vm_Rendered;
            Vm.Rendered += Vm_Rendered;
            Vm.Render();
        };
        Unloaded += (_, _) => Vm.Rendered -= Vm_Rendered;
    }

    public VisualizerViewModel Vm { get; }

    /// <summary>「画像として保存…」(仕様 7)。持ち主が保存先を選んで書く。</summary>
    public event EventHandler? SaveRequested;

    /// <summary>描き直した後に、Hex ビューのカーソル・選択範囲の枠を描く関数 (持ち主が付ける)。画素の座標の矩形の一覧を返す。</summary>
    public Func<IReadOnlyList<(int X, int Y, int Width, int Height, bool Cursor)>>? FramesSource { get; set; }

    private static void Label(FrameworkElement element, string key)
    {
        AutomationProperties.SetName(element, Loc.Get(key));
        ToolTipService.SetToolTip(element, Loc.Get(key));
    }

    bool Panels.IPanelContent.FocusContent() => FormatChoice.Focus(FocusState.Keyboard);

    private void SyncFromVm()
    {
        _syncing = true;
        FormatChoice.SelectedIndex = Array.IndexOf([.. VisualizerViewModel.Formats], Vm.Format);
        WidthBox.Text = Vm.Width.ToString(CultureInfo.InvariantCulture);
        StrideBox.Text = Vm.Stride > 0 ? Vm.Stride.ToString(CultureInfo.InvariantCulture) : string.Empty;
        StartChoice.SelectedIndex = (int)Vm.StartMode;
        StartBox.Text = Vm.FixedStartText;
        StartBox.IsEnabled = Vm.StartMode == ByteImageStart.Fixed;
        OrientationChoice.SelectedIndex = Vm.BottomUp ? 1 : 0;
        ZoomChoice.SelectedIndex = Math.Max(0, VisualizerViewModel.Zooms.ToList().IndexOf(Vm.Zoom));
        _syncing = false;
    }

    // ---- 設定 ----

    private void FormatChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && FormatChoice.SelectedIndex >= 0)
        {
            Vm.Format = VisualizerViewModel.Formats[FormatChoice.SelectedIndex];
        }
    }

    private void StartChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && StartChoice.SelectedIndex >= 0)
        {
            Vm.StartMode = (ByteImageStart)StartChoice.SelectedIndex;
            StartBox.IsEnabled = Vm.StartMode == ByteImageStart.Fixed;
        }
    }

    private void OrientationChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && OrientationChoice.SelectedIndex >= 0)
        {
            Vm.BottomUp = OrientationChoice.SelectedIndex == 1;
        }
    }

    private void ZoomChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && ZoomChoice.SelectedIndex >= 0)
        {
            Vm.Zoom = VisualizerViewModel.Zooms[ZoomChoice.SelectedIndex];
        }
    }

    private void NumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox box)
        {
            e.Handled = true;
            Commit(box);
        }
    }

    private void NumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            Commit(box);
        }
    }

    /// <summary>幅・1 行のバイト数・固定の開始位置の入力を確定する (入力式。不正なら今の値に戻す)。</summary>
    internal void Commit(TextBox box)
    {
        var context = Vm.Document is { } doc ? new EditorExpressionContext(doc.Editor) : null;
        long Parse(string text, long fallback) =>
            context is not null && Core.Expressions.ExpressionEvaluator.TryEvaluate(text, context, out long v, out _, Core.Expressions.DefaultRadix.Decimal) ? v : fallback;
        if (box == WidthBox)
        {
            Vm.Width = (int)Math.Clamp(Parse(box.Text, Vm.Width), 1, ByteImage.MaxWidth);
        }
        else if (box == StrideBox)
        {
            Vm.Stride = box.Text.Trim().Length == 0 ? 0 : Math.Max(0, Parse(box.Text, Vm.Stride));
        }
        else if (box == StartBox)
        {
            Vm.SetFixedStart(box.Text);
        }

        SyncFromVm();
    }

    // ---- 描画 ----

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Vm.ViewportWidth = e.NewSize.Width;
        Vm.ViewportHeight = e.NewSize.Height;
        Vm.Render();
    }

    private void Vm_Rendered(object? sender, EventArgs e)
    {
        // 設定が外から変わった (テスト用の命令の通り道など) ときに選択を合わせる。入力中の欄は書き換えない。
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            SyncFromVm();
        }

        Draw();
    }

    /// <summary>画素を拡大率の正方形にして描く (補間なし)。</summary>
    private void Draw()
    {
        _syncing = true;
        RowScroll.Maximum = Vm.ScrollMaximum;
        RowScroll.Value = Math.Min(Vm.ScrollRow, Vm.ScrollMaximum);
        RowScroll.LargeChange = Math.Max(1, Vm.Layout?.Rows ?? 1);
        RowScroll.ViewportSize = Math.Max(1, Vm.Layout?.Rows ?? 1);
        RowScroll.Visibility = Vm.ScrollMaximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;
        if (Vm.Layout is not { Rows: > 0 } layout)
        {
            ImageView.Source = null;
            Overlay.Children.Clear();
            return;
        }

        int zoom = Vm.EffectiveZoom;
        int width = layout.Width * zoom, height = layout.Rows * zoom;
        byte[] bgra = new byte[width * height * 4];
        uint[] pixels = Vm.Pixels;
        for (int y = 0; y < layout.Rows; y++)
        {
            for (int x = 0; x < layout.Width; x++)
            {
                uint p = pixels[y * layout.Width + x];
                // WriteableBitmap は乗算済みの BGRA。
                byte a = (byte)(p >> 24);
                byte r = (byte)((p >> 16 & 255) * a / 255), g = (byte)((p >> 8 & 255) * a / 255), b = (byte)((p & 255) * a / 255);
                for (int dy = 0; dy < zoom; dy++)
                {
                    int o = ((y * zoom + dy) * width + x * zoom) * 4;
                    for (int dx = 0; dx < zoom; dx++, o += 4)
                    {
                        bgra[o] = b;
                        bgra[o + 1] = g;
                        bgra[o + 2] = r;
                        bgra[o + 3] = a;
                    }
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        ImageView.Source = bitmap;
        ImageView.Width = width;
        ImageView.Height = height;
        DrawFrames();
    }

    /// <summary>Hex ビューのカーソル位置と選択範囲を画像の上に枠で示す (仕様 5)。</summary>
    internal void DrawFrames()
    {
        Overlay.Children.Clear();
        if (FramesSource is null || Vm.Layout is null)
        {
            return;
        }

        int zoom = Vm.EffectiveZoom;
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var selection = (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"];
        foreach ((int x, int y, int w, int h, bool cursor) in FramesSource())
        {
            var rect = new Rectangle
            {
                Width = Math.Max(2, w * zoom),
                Height = Math.Max(2, h * zoom),
                Stroke = cursor ? accent : selection,
                StrokeThickness = cursor ? 2 : 1,
            };
            Canvas.SetLeft(rect, x * zoom);
            Canvas.SetTop(rect, y * zoom);
            Overlay.Children.Add(rect);
        }
    }

    /// <summary>描いている枠の数 (テスト用)。</summary>
    internal int FrameCount => Overlay.Children.Count;

    // ---- マウス・キー (仕様 4・6) ----

    private (int X, int Y) PixelAt(PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point p = e.GetCurrentPoint(ImageView).Position;
        int zoom = Math.Max(1, Vm.EffectiveZoom);
        return ((int)Math.Floor(p.X / zoom), (int)Math.Floor(p.Y / zoom));
    }

    private void Image_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        (int x, int y) = PixelAt(e);
        Vm.Hover(x, y);
    }

    private void Viewport_PointerExited(object sender, PointerRoutedEventArgs e) => Vm.HoverText = string.Empty;

    private void Image_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(ImageView).Properties.IsLeftButtonPressed)
        {
            (int x, int y) = PixelAt(e);
            _keyPixel = (x, y);
            e.Handled = Vm.Click(x, y);
        }
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;
        Vm.ScrollRow = Math.Clamp(Vm.ScrollRow - Math.Sign(delta) * 3, 0, Vm.ScrollMaximum);
        e.Handled = true;
    }

    private void RowScroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncing)
        {
            Vm.ScrollRow = (long)e.NewValue;
        }
    }

    private void Viewport_KeyDown(object sender, KeyRoutedEventArgs e) => e.Handled = HandleImageKey(e.Key);

    /// <summary>画像の上のキー: 矢印で画素を選び (値を表示)、Enter でカーソルを移す、PageUp / PageDown でスクロール。</summary>
    internal bool HandleImageKey(VirtualKey key)
    {
        if (Vm.Layout is not { Rows: > 0 } layout)
        {
            return false;
        }

        (int x, int y) = _keyPixel;
        switch (key)
        {
            case VirtualKey.Left: x--; break;
            case VirtualKey.Right: x++; break;
            case VirtualKey.Up: y--; break;
            case VirtualKey.Down: y++; break;
            case VirtualKey.Enter:
                return Vm.Click(x, y);
            case VirtualKey.PageDown:
                Vm.ScrollRow = Math.Min(Vm.ScrollMaximum, Vm.ScrollRow + layout.Rows);
                return true;
            case VirtualKey.PageUp:
                Vm.ScrollRow = Math.Max(0, Vm.ScrollRow - layout.Rows);
                return true;
            default:
                return false;
        }

        _keyPixel = (Math.Clamp(x, 0, layout.Width - 1), Math.Clamp(y, 0, layout.Rows - 1));
        Vm.Hover(_keyPixel.X, _keyPixel.Y);
        return true;
    }

    private void SaveItem_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
}
