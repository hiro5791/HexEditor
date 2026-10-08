using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using HexEditor.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// タブのドラッグ (UI-10 の並べ替え、UI-11 の切り離しとウィンドウ間の移動)。タブ列の中・別のウィンドウのタブ列へのドロップは
/// <c>TabStripDrop</c>、タブ列の外へのドロップは <c>TabDroppedOutside</c> で受ける。テスト用の命令は同じ処理
/// (<see cref="DropTab"/>、<see cref="OnTabDroppedOutside"/>) を呼ぶ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ドラッグ中のタブのデータ形式。プロセス ID を含め、別のプロセスのタブは受け付けない (UI-11 の仕様 5)。</summary>
    private const string TabFormatPrefix = "HexEditor.Tab.";

    private static string TabFormat => TabFormatPrefix + Environment.ProcessId;

    /// <summary>ドラッグ中のタブ (このプロセスの中だけ)。</summary>
    private static (MainWindow Window, DocumentViewModel Document, PointInt32 Start)? s_draggedTab;

    private void InitializeTabDrag()
    {
        // 並べ替えも自分で行う (ピン留めの規則を守り、ウィンドウの間の移動と同じ処理にする)。
        Tabs.CanDragTabs = true;
        Tabs.CanReorderTabs = false;
        Tabs.TabDragStarting += Tabs_TabDragStarting;
        Tabs.TabDragCompleted += (_, _) => s_draggedTab = null;
        Tabs.TabDroppedOutside += Tabs_TabDroppedOutside;
        Tabs.TabStripDragOver += TabStrip_DragOverTab;
        Tabs.TabStripDrop += TabStrip_DropTab;
    }

    private void Tabs_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Item is not DocumentViewModel doc)
        {
            args.Cancel = true;
            return;
        }

        s_draggedTab = (this, doc, CursorPosition());
        args.Data.SetData(TabFormat, doc.DisplayName);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TabStrip_DragOverTab(object sender, DragEventArgs e)
    {
        if (!e.DataView.AvailableFormats.Any(f => f.StartsWith(TabFormatPrefix, StringComparison.Ordinal)))
        {
            return;
        }

        // 別のプロセス (管理者として実行中のものなど) のタブは移せない (UI-11 の仕様 5)。
        e.AcceptedOperation = e.DataView.Contains(TabFormat) && s_draggedTab is not null ? DataPackageOperation.Move : DataPackageOperation.None;
        e.Handled = true;
    }

    private void TabStrip_DropTab(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(TabFormat) || s_draggedTab is not { } dragged)
        {
            return;
        }

        e.Handled = true;
        DropTab(dragged.Document, dragged.Window, TabInsertIndex(e));
    }

    private void Tabs_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Item is DocumentViewModel doc && s_draggedTab is { } dragged && dragged.Document == doc)
        {
            OnTabDroppedOutside(doc, dragged.Start, CursorPosition());
        }
    }

    /// <summary>
    /// タブ列へのドロップ: 同じウィンドウなら並べ替え (UI-10 の仕様 1)、別のウィンドウのタブならその位置に移す (UI-11 の仕様 2)。
    /// <paramref name="index"/> はドロップした位置 (タブの間の番号)。
    /// </summary>
    internal void DropTab(DocumentViewModel doc, MainWindow source, int index)
    {
        if (source == this)
        {
            int from = Vm.Documents.IndexOf(doc);
            if (from >= 0)
            {
                Vm.MoveDocument(doc, index > from ? index - 1 : index);
            }

            return;
        }

        MoveTab(doc, source, this, index);
    }

    /// <summary>
    /// タブ列の外へのドロップ (UI-11 の仕様 1・4・6)。タブ列から上下 32 px 以上離れていれば、ドロップした位置に元と同じ大きさの新しい
    /// ウィンドウを作ってタブを移す。ウィンドウの最後のタブなら、ウィンドウ自体を動かす。座標は画面の物理ピクセル。
    /// </summary>
    internal void OnTabDroppedOutside(DocumentViewModel doc, PointInt32 start, PointInt32 end)
    {
        if (!Vm.Documents.Contains(doc))
        {
            return;
        }

        (double top, double bottom) = TabStripScreenRange();
        double scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        PointInt32 position = AppWindow.Position;
        (int x, int y) = TabDragRules.Place(position.X, position.Y, start.X, start.Y, end.X, end.Y);
        switch (TabDragRules.Decide(top, bottom, end.Y, scale, Vm.Documents.Count))
        {
            case TabDropAction.MoveWindow:
                AppWindow.Move(new PointInt32(x, y));
                break;
            case TabDropAction.NewWindow:
                SizeInt32 size = AppWindow.Size;
                MoveTabToNewWindow(doc, new SessionWindow { X = x, Y = y, Width = size.Width, Height = size.Height });
                break;
        }
    }

    /// <summary>タブ列の上端と下端 (画面の物理ピクセル)。</summary>
    private (double Top, double Bottom) TabStripScreenRange()
    {
        FrameworkElement strip = Vm.Documents.Count > 0 && Tabs.ContainerFromIndex(0) is TabViewItem item ? item : Tabs;
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        Windows.Foundation.Point origin = strip.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var client = new NativePoint();
        ClientToScreen(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), ref client);
        double top = client.Y + (origin.Y * scale);
        double height = (strip == Tabs ? 40 : strip.ActualHeight) * scale;
        return (top, top + height);
    }

    /// <summary>タブを新しいウィンドウに移す (UI-11)。<paramref name="bounds"/> を省くと、元のウィンドウから少しずらして同じ大きさで開く。</summary>
    internal void MoveTabToNewWindow(DocumentViewModel doc, SessionWindow? bounds)
    {
        if (!Vm.Documents.Contains(doc) || Vm.Documents.Count < 2)
        {
            return;
        }

        MainWindow target = WindowManager.CreateWindow(this, bounds);
        MoveTab(doc, this, target, null);
    }

    /// <summary>
    /// タブを別のウィンドウに移す (UI-11 の仕様 2・3)。文書を読み直さず、未保存の編集・Undo 履歴・カーソル・選択・表示設定・実行中の処理を
    /// 保ったまま移る。実行中のすべて検索の結果一覧も移す。最後のタブを移したウィンドウは閉じる (仕様 7)。失敗したらタブを元のウィンドウに
    /// 残し「タブを移動できませんでした」と知らせる (「エラー」)。
    /// </summary>
    internal static bool MoveTab(DocumentViewModel doc, MainWindow from, MainWindow to, int? index)
    {
        if (from == to || !from.Vm.Documents.Contains(doc))
        {
            return false;
        }

        var watch = Stopwatch.StartNew();
        int original = from.Vm.Documents.IndexOf(doc);
        try
        {
            if (from.SearchResults.Shows(doc.Editor))
            {
                from.SearchResults.TransferTo(to.SearchResults);
            }

            from._mru.Remove(doc);
            from.Vm.Detach(doc);
            try
            {
                to.Vm.Attach(doc, index);
            }
            catch
            {
                from.Vm.Attach(doc, original);
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or NullReferenceException)
        {
            AppLog.Warning($"Moving a tab failed: {ex}");
            from.ShowNotice(Loc.Get("Tab_MoveFailed"), InfoBarSeverity.Error);
            return false;
        }

        from.UpdateTitle();
        to.UpdateTitle();
        to.UpdateMatchHighlights();
        WindowManager.MarkActive(to);
        AppLog.Info($"Tab moved to window {WindowManager.NumberOf(to)} in {watch.Elapsed.TotalMilliseconds:F1} ms");

        if (from.Vm.Documents.Count == 0)
        {
            from.CloseEmptyWindow();
        }

        return true;
    }

    /// <summary>タブがなくなったウィンドウを閉じる (確かめる文書がない)。</summary>
    private void CloseEmptyWindow()
    {
        if (WindowManager.Windows.Count < 2 || Vm.Documents.Count > 0)
        {
            return;
        }

        _closingConfirmed = true;
        SavePanelLayout();
        StopWindowTimers();
        WindowManager.Unregister(this);
        DispatcherQueue.TryEnqueue(Close);
    }

    /// <summary>マウスカーソルの位置 (画面の物理ピクセル)。</summary>
    private static PointInt32 CursorPosition() => GetCursorPos(out NativePoint p) ? new PointInt32(p.X, p.Y) : default;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
}
