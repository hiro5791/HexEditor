using System.Runtime.InteropServices;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// ウィンドウの非クライアント領域の判定 (WM_NCHITTEST)。マウスを動かさず、メッセージを送るだけで、タイトルバーのどこが
/// ドラッグ領域 (キャプション) かを調べる。
/// </summary>
public static partial class WindowHitTest
{
    public const int Client = 1;
    public const int Caption = 2;

    private const uint WmNcHitTest = 0x0084;

    /// <summary>ウィンドウのクライアント領域の中の点 (エピクセル) の判定 (HTCLIENT = 1、HTCAPTION = 2 など)。</summary>
    public static int At(nint hwnd, double x, double y, double scale)
    {
        ClientOrigin(hwnd, out int originX, out int originY);
        int sx = originX + (int)Math.Round(x * scale);
        int sy = originY + (int)Math.Round(y * scale);
        nint lParam = (nint)(((sy & 0xFFFF) << 16) | (sx & 0xFFFF));
        return (int)SendMessageTimeout(hwnd, WmNcHitTest, 0, lParam, 0x0002 /* SMTO_ABORTIFHUNG */, 5000, out nint result) == 0 ? -1 : (int)result;
    }

    /// <summary>クライアント領域の左上の画面座標。</summary>
    public static void ClientOrigin(nint hwnd, out int x, out int y)
    {
        var point = new Point();
        ClientToScreen(hwnd, ref point);
        x = point.X;
        y = point.Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
