using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 比較ビュー (ANA-04) が Hex ビューに足すもの: スクロールバーの上の差分の印 (VIEW-02 の仕様 9) と、カーソル位置の読み上げに加える
/// 状態 (差分の種類と相手側の値。ANA-04 の仕様 8)。どちらも提供元 (比較) が設定し、設定されていなければ何もしない。
/// </summary>
public sealed partial class HexView
{
    /// <summary>差分の印を出すか (設定 view.scrollBar.diffMarks、既定オフ。比較ビューは差分マップを別に持つ)。</summary>
    public bool ShowDiffMarkers { get; set; }

    /// <summary>
    /// 差分の印の提供元: [start, end) と重なる差分があれば、その種類の色 (複数あれば最も多い種類の色)、なければ null。
    /// スクロールバーの 1 ピクセルごとに問い合わせるので、差分の件数に比例する処理をしないこと (二分探索で答える)。
    /// </summary>
    public Func<long, long, Brush?>? DiffMarkerSource { get; set; }

    /// <summary>カーソル位置の要約 (読み上げと UI オートメーションの Value) に加える状態。</summary>
    public Func<long, IReadOnlyList<string>?>? CellStates { get; set; }

    /// <summary>差分の印を置く (HexView.Markers.cs から呼ぶ)。置いた後の印の数を返す。</summary>
    private int PlaceDiffMarkers(int used, double height, long totalRows)
    {
        if (!ShowDiffMarkers || DiffMarkerSource is not { } source || _editor is null)
        {
            return used;
        }

        int pixels = (int)Math.Ceiling(height);
        for (int y = 0; y < pixels && used < MaxMarkers; y++)
        {
            long fromRow = (long)Math.Ceiling(totalRows * (double)y / height);
            long toRow = (long)Math.Ceiling(totalRows * (double)(y + 1) / height);
            if (toRow <= fromRow)
            {
                continue;
            }

            long from = _editor.Layout.RowStart(fromRow);
            long to = _editor.Layout.RowStart(toRow);
            if (source(Math.Max(0, from), to) is { } brush)
            {
                PlaceMarker(used++, y, 2, brush, "diff");
            }
        }

        return used;
    }
}
