using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// ウィンドウの列挙・前面のウィンドウの確認・PrintWindow による画面の取得。
/// 入力 (SendInput・マウス) やウィンドウの前面化 (SetForegroundWindow) の API は使わない。
/// </summary>
internal static partial class NativeMethods
{
    public const uint PwRenderFullContent = 2;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hWnd, nint hdc, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder name, int capacity);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>プロセスの、表示されているトップレベルのウィンドウ。</summary>
    public static List<nint> TopLevelWindows(int processId, string? className = null)
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId && IsWindowVisible(hwnd) && (className is null || ClassName(hwnd) == className))
            {
                result.Add(hwnd);
            }

            return true;
        }, 0);
        return result;
    }

    public static string ClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    public static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }
}
