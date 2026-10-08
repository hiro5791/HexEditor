using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>スクロールバーの上の印 (VIEW-02 の仕様 9)。既定はカーソル位置と検索結果。選択範囲・ブックマークは設定で出せる。</summary>
public sealed partial class HexView
{
    /// <summary>描く印の上限 (同じピクセルの印はまとめるため、通常はこれより少ない)。</summary>
    private const int MaxMarkers = 2000;

    private readonly List<Rectangle> _markers = [];
    private IReadOnlyList<long> _searchMarkers = [];

    /// <summary>カーソル位置の印を出すか (設定 view.scrollBar.cursorMark、既定オン)。</summary>
    public bool ShowCursorMarker { get; set; } = true;

    /// <summary>検索結果の印を出すか (設定 view.scrollBar.searchMarks、既定オン)。</summary>
    public bool ShowSearchMarkers { get; set; } = true;

    /// <summary>選択範囲の印を出すか (設定 view.scrollBar.selectionMark、既定オフ)。</summary>
    public bool ShowSelectionMarker { get; set; }

    /// <summary>ブックマークの印を出すか (設定 view.scrollBar.bookmarkMarks、既定オフ)。</summary>
    public bool ShowBookmarkMarkers { get; set; }

    /// <summary>
    /// ブックマークの印の提供元 (INSP が設定する): [start, end) から始まるブックマークがあればその色、なければ null。
    /// スクロールバーの 1 ピクセルごとに問い合わせるので、件数に比例する処理をしないこと。
    /// </summary>
    public Func<long, long, Microsoft.UI.Xaml.Media.Brush?>? BookmarkMarkerSource { get; set; }

    /// <summary>描いた印 (テスト用): 種類と上端の位置。</summary>
    internal List<(string Kind, double Top, double Height)> PlacedMarkers { get; } = [];

    private readonly HashSet<int> _markerPixels = [];

    /// <summary>検索結果の位置 (一致の先頭オフセット、昇順でなくてもよい)。null で消す。</summary>
    public void SetSearchMarkers(IReadOnlyList<long>? offsets)
    {
        _searchMarkers = offsets ?? [];
        UpdateMarkers();
    }

    /// <summary>印の表示の設定が変わったので描き直す。</summary>
    public void RefreshMarkers() => UpdateMarkers();

    private void UpdateMarkers()
    {
        int used = 0;
        PlacedMarkers.Clear();
        double height = VerticalBar.ActualHeight;
        if (_editor is not null && _palette is not null && height > 0)
        {
            long totalRows = Math.Max(1, _editor.Layout.TotalRows);
            int bytesPerRow = Math.Max(1, _editor.BytesPerRow);
            double RowY(long row) => height * ((double)row / totalRows);

            // 選択範囲 (VIEW-35 と同じ種類の印。範囲の行の分の高さ、最低 2 px)。
            if (ShowSelectionMarker && _editor.HasSelection)
            {
                double top = RowY(_editor.Layout.RowOf(_editor.SelectionStart));
                double bottom = RowY(_editor.Layout.RowOf(_editor.SelectionStart + _editor.SelectionLength - 1) + 1);
                PlaceMarker(used++, Math.Min(height - 2, top), Math.Max(2, bottom - top), _palette.Selection, "selection");
            }

            // ブックマーク: スクロールバーの 1 ピクセルごとに、その範囲から始まるものがあるかを問い合わせる (件数によらない)。
            if (ShowBookmarkMarkers && BookmarkMarkerSource is { } source)
            {
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
                        PlaceMarker(used++, y, 2, brush, "bookmark");
                    }
                }
            }

            if (ShowSearchMarkers && _searchMarkers.Count > 0)
            {
                // 同じピクセルの印はまとめる (作業用の集合は使い回す)。
                HashSet<int> seen = _markerPixels;
                seen.Clear();
                foreach (long offset in _searchMarkers)
                {
                    int y = (int)(height * ((double)(offset / bytesPerRow) / totalRows));
                    if (seen.Add(y) && used < MaxMarkers)
                    {
                        PlaceMarker(used++, y, 2, _palette.SearchMarker, "search");
                    }
                }
            }

            if (ShowCursorMarker)
            {
                double y = RowY(_editor.Layout.RowOf(_editor.Cursor));
                PlaceMarker(used++, Math.Min(height - 3, y), 3, _palette.CursorMarker, "cursor");
            }
        }

        for (int i = used; i < _markers.Count; i++)
        {
            _markers[i].Visibility = Visibility.Collapsed;
        }
    }

    private void PlaceMarker(int index, double y, double height, Microsoft.UI.Xaml.Media.Brush brush, string kind)
    {
        PlacedMarkers.Add((kind, y, height));
        if (index >= _markers.Count)
        {
            var mark = new Rectangle { Width = 4, IsHitTestVisible = false };
            _markers.Add(mark);
            MarkerLayer.Children.Add(mark);
        }

        Rectangle m = _markers[index];
        m.Visibility = Visibility.Visible;
        m.Height = height;
        m.Fill = brush;
        Canvas.SetTop(m, y);
    }
}
