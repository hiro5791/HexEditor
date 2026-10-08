namespace HexEditor.Core.Tabs;

/// <summary>タブのドロップの結果 (UI-11)。</summary>
public enum TabDropAction
{
    /// <summary>何もしない (タブ列の近くに落とした)。</summary>
    None,

    /// <summary>新しいウィンドウを作り、タブを移す (仕様 1)。</summary>
    NewWindow,

    /// <summary>ウィンドウの最後のタブ: ウィンドウ自体を動かす (仕様 4)。</summary>
    MoveWindow,
}

/// <summary>タブをタブ列の外へドロップしたときの規則 (UI-11 の仕様 1・4・6)。座標は画面の物理ピクセル。</summary>
public static class TabDragRules
{
    /// <summary>タブ列から上下にこれだけ離れていれば「タブ列の外」(UI-11 の仕様 1。論理ピクセル)。</summary>
    public const double DetachDistance = 32;

    /// <summary>
    /// タブ列の外へのドロップでどうするか。<paramref name="stripTop"/>〜<paramref name="stripBottom"/> はタブ列の上下の位置、
    /// <paramref name="dropY"/> はドロップした位置、<paramref name="scale"/> は表示倍率 (DPI / 96)。
    /// </summary>
    public static TabDropAction Decide(double stripTop, double stripBottom, double dropY, double scale, int tabCount)
    {
        double distance = DetachDistance * scale;
        bool outside = dropY < stripTop - distance || dropY > stripBottom + distance;
        if (!outside || tabCount <= 0)
        {
            return TabDropAction.None;
        }

        return tabCount == 1 ? TabDropAction.MoveWindow : TabDropAction.NewWindow;
    }

    /// <summary>
    /// 切り離したタブの新しいウィンドウの位置 (UI-11 の仕様 1・6): 元のウィンドウの位置を、ドラッグした分だけずらす (ドラッグを始めた
    /// 点とウィンドウの位置関係を保つ)。大きさは元のウィンドウと同じ。
    /// </summary>
    public static (int X, int Y) Place(int windowX, int windowY, int startX, int startY, int dropX, int dropY) =>
        (windowX + (dropX - startX), windowY + (dropY - startY));
}
