using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ManualTestRunner;

/// <summary>
/// ウィンドウの一部をクリックしても、このウィンドウをアクティブにしない。手順を読むためにスクロールやクリックをしても、
/// キーボードのフォーカスがテスト対象の HexEditor に残るようにする (テストの結果がフォーカスの場所で変わらないように)。
/// </summary>
internal sealed partial class NoActivateArea
{
    private const uint WmMouseActivate = 0x0021;
    private const nint MaNoActivate = 3;

    private readonly nint _hwnd;
    private readonly FrameworkElement _area;
    private readonly SubclassProc _proc; // ウィンドウがある間、ガベージコレクションで回収されないように持つ。

    public NoActivateArea(nint hwnd, FrameworkElement area)
    {
        _hwnd = hwnd;
        _area = area;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 1, 0);
    }

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data) =>
        message == WmMouseActivate && CursorIsInside() ? MaNoActivate : DefSubclassProc(hwnd, message, wParam, lParam);

    private bool CursorIsInside()
    {
        if (_area.XamlRoot is not { } root || !GetCursorPos(out Point cursor) || !ScreenToClient(_hwnd, ref cursor))
        {
            return false;
        }

        double scale = root.RasterizationScale;
        Windows.Foundation.Point topLeft = _area.TransformToVisual(null).TransformPoint(default);
        double x = (cursor.X / scale) - topLeft.X, y = (cursor.Y / scale) - topLeft.Y;
        return x >= 0 && y >= 0 && x < _area.ActualWidth && y < _area.ActualHeight;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref Point point);
}
