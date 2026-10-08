using HexEditor.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// 分割バー (UI-01 の仕様 5)。<see cref="Vertical"/> なら左右の境界 (左右にドラッグ)、そうでなければ上下の境界。
/// フォーカスでき、矢印キーで 8 px ずつ動かす。動かした量は <see cref="Moved"/> で知らせる (大きさの計算は呼び出し側)。
/// </summary>
public sealed partial class Splitter : UserControl
{
    public const double Thickness = 4;
    public const double KeyStep = 8;

    private double? _start;

    public Splitter()
    {
        InitializeComponent();
        Vertical = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => _start = null;
        KeyDown += OnKeyDown;
    }

    /// <summary>左右の境界なら true (既定)。</summary>
    public bool Vertical
    {
        get;
        set
        {
            field = value;
            Width = value ? Thickness : double.NaN;
            Height = value ? double.NaN : Thickness;
            ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            AutomationProperties.SetName(this, Loc.Get("Panel_Splitter"));
        }
    }

    /// <summary>
    /// 境界を動かした。値は右 (または下) への移動量 (エピクセル)。
    /// </summary>
    public event EventHandler<double>? Moved;

    private double Position(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(XamlRoot?.Content).Position;
        return Vertical ? p.X : p.Y;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = Position(e);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_start is double start)
        {
            double now = Position(e);
            if (Math.Abs(now - start) >= 1)
            {
                Moved?.Invoke(this, now - start);
                _start = now;
            }
        }
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        _start = null;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        double? delta = (e.Key, Vertical) switch
        {
            (VirtualKey.Left, true) or (VirtualKey.Up, false) => -KeyStep,
            (VirtualKey.Right, true) or (VirtualKey.Down, false) => KeyStep,
            _ => null,
        };
        if (delta is double d)
        {
            // 右から左に書く言語では左右が反転する。
            Moved?.Invoke(this, Vertical && FlowDirection == FlowDirection.RightToLeft ? -d : d);
            e.Handled = true;
        }
    }

    /// <summary>テスト用: キー操作と同じ移動。</summary>
    public void Nudge(bool forward) => Moved?.Invoke(this, forward ? KeyStep : -KeyStep);
}
