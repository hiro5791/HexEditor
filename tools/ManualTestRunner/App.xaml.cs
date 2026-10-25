using Microsoft.UI.Xaml;

namespace ManualTestRunner;

/// <summary>
/// 開発中の確認用の印 (HexEditor の DevOptions と同じ %TEMP%\HexEditor\dev-no-activate)。印があるときは、ツールのウィンドウを常に手前に
/// せず、アクティブにもしない (作業中の利用者のキー入力を奪わない。ツール自身の UI テストもこの印を置いて動かす)。
/// </summary>
public static class DevOptions
{
    public static string NoActivateMarker => Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate");

    public static bool NoActivate => File.Exists(NoActivateMarker);
}

/// <summary>手動テスト支援ツールのアプリ。</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        if (!DevOptions.NoActivate)
        {
            _window.Activate();
            return;
        }

        // WS_EX_NOACTIVATE を付けてから Activate する (HexEditor の TestHooks.ShowWithoutActivation と同じ)。Activate しないと中身が
        // 読み込まれず、付けないと XAML のフォーカスの移動でウィンドウがアクティブになる。表示したら一番後ろに回す。
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExNoActivate);
        _window.Activate();
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint HwndBottom = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
}
