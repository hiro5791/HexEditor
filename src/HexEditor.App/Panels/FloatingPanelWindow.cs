using System.Runtime.InteropServices;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Panels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace HexEditor.App.Panels;

/// <summary>
/// 切り離した浮動パネル (UI-05 の仕様 5)。メインウィンドウの子ウィンドウ (所有されたウィンドウ) として常にメインウィンドウより前に置き、
/// メインウィンドウを閉じると一緒に閉じる。見出しのダブルクリックで元の場所に戻す。
/// </summary>
public sealed class FloatingPanelWindow : Window
{
    private const int GwlpHwndParent = -8;

    public FloatingPanelWindow(Window owner, string panelId, string title)
    {
        PanelId = panelId;
        Area = new PanelDockArea { Dock = PanelDock.Floating };
        var root = new Grid();
        root.Children.Add(Area);
        Content = root;
        Title = title;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        if (owner.Content is FrameworkElement ownerRoot)
        {
            root.RequestedTheme = ownerRoot.ActualTheme;
            root.FlowDirection = ownerRoot.FlowDirection;
        }

        Owner = owner;
        SetWindowLongPtr(Hwnd, GwlpHwndParent, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
    }

    public string PanelId { get; }

    public PanelDockArea Area { get; }

    public Window Owner { get; }

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>所有者のウィンドウ (テスト用: 子ウィンドウであることの確認)。</summary>
    public nint OwnerHwnd => GetWindowLongPtr(Hwnd, GwlpHwndParent);

    /// <summary>画面上の位置 (物理ピクセル)。</summary>
    public PanelBounds ScreenBounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    public void Place(PanelBounds bounds) => AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));

    /// <summary>作業中のウィンドウからフォーカスを奪わずに表示する (テスト用のビルド・開発中の確認)。</summary>
    public void ShowWindow()
    {
        if (!TestHooks.ShowWithoutActivation(this))
        {
            if (DevOptions.NoActivate)
            {
                AppWindow.Show(activateWindow: false);
            }
            else
            {
                Activate();
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
