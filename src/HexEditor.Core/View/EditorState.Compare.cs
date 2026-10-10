namespace HexEditor.Core.View;

/// <summary>
/// 比較ビュー (ANA-04 の仕様 4) の同期スクロールで、もう一方のビューを合わせるための操作。利用者の操作ではないので、ジャンプ履歴に
/// 記録せず、表示外への移動でも指定した一番上の行をそのまま使う (左右の対応する行を同じ高さに並べるため)。
/// </summary>
public sealed partial class EditorState
{
    /// <summary>カーソルを <paramref name="cursor"/> に置き (選択は解除)、一番上の行を <paramref name="topRow"/> にする。</summary>
    public void FollowTo(long cursor, long topRow)
    {
        ClearSelectionAnchor();
        _cursor = Math.Clamp(cursor, 0, Layout.MaxCursor);
        LowNibble = false;
        SetTopRow(topRow);
        RaiseChanged();
    }

    /// <summary>範囲を選択し (カーソルは範囲の先頭)、一番上の行を <paramref name="topRow"/> にする (相手側の差分の選択。ANA-05 の仕様 1)。</summary>
    public void FollowSelection(long start, long length, long topRow)
    {
        start = Math.Clamp(start, 0, Layout.Length);
        length = Math.Clamp(length, 0, Layout.Length - start);
        _anchor = start;
        SetSelection(start, length);
        _cursor = Math.Min(start, Layout.MaxCursor);
        LowNibble = false;
        SetTopRow(topRow);
        RaiseChanged();
    }
}
