using HexEditor.App.ViewModels;

namespace HexEditor.App;

/// <summary>
/// ウィンドウの枠の機能: ズーム (UI-08。MainWindow.Zoom.cs)、全画面表示 (UI-07。MainWindow.FullScreen.cs)、狭い幅でのパネルの折りたたみ
/// (UI-01 の仕様 4。MainWindow.PanelCollapse.cs)、元に戻す・やり直しの操作名 (EDIT-19 の仕様 11。MainWindow.UndoNames.cs)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>コンストラクタから 1 回呼ぶ (表示メニューの項目を作った後、コマンドとメニューをつなぐ前)。</summary>
    private void InitializeShell()
    {
        InitializeZoom();
        InitializeFullScreen();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                UpdateZoomStatus();
                WatchUndoNames();
            }
        };
    }
}
