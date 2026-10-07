using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Services;

/// <summary>開発中の確認だけで使う設定。一時フォルダの印のファイルで有効にする。</summary>
internal static class DevOptions
{
    /// <summary>%TEMP%\HexEditor\dev-no-activate があれば、起動時にウィンドウをアクティブにしない。</summary>
    public static bool NoActivate =>
        File.Exists(Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate"));

    public static nint ForegroundWindow() => GetForegroundWindow();

    /// <summary>ウィンドウを一番後ろに回し、<paramref name="previous"/> を前面に戻す。</summary>
    public static void SendToBack(Window window, nint previous)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetForegroundWindow(previous);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private static readonly nint HwndBottom = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
