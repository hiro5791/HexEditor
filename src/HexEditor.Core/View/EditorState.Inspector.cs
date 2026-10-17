namespace HexEditor.Core.View;

public sealed partial class EditorState
{
    /// <summary>
    /// カーソルを移す。ジャンプ履歴には記録しない (インスペクタの配列モードの前後の移動。連続した移動で履歴を埋めないため。INSP-21 の仕様 7)。
    /// 移動先が表示外ならカーソルが見えるようにスクロールする。
    /// </summary>
    public void MoveCursorWithoutHistory(long offset)
    {
        CollapseCarets();
        ClearSelectionAnchor();
        MoveTo(offset, extend: false);
    }
}
