using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>スクロールバーの上の印 (VIEW-02 の仕様 9)。既定はカーソル位置と検索結果。</summary>
public sealed partial class HexView
{
    /// <summary>描く印の上限 (同じピクセルの印はまとめるため、通常はこれより少ない)。</summary>
    private const int MaxMarkers = 2000;

    private readonly List<Rectangle> _markers = [];
    private IReadOnlyList<long> _searchMarkers = [];

    /// <summary>カーソル位置の印を出すか。</summary>
    public bool ShowCursorMarker { get; set; } = true;

    /// <summary>検索結果の印を出すか。</summary>
    public bool ShowSearchMarkers { get; set; } = true;

    /// <summary>検索結果の位置 (一致の先頭オフセット、昇順でなくてもよい)。null で消す。</summary>
    public void SetSearchMarkers(IReadOnlyList<long>? offsets)
    {
        _searchMarkers = offsets ?? [];
        UpdateMarkers();
    }

    private void UpdateMarkers()
    {
        int used = 0;
        double height = VerticalBar.ActualHeight;
        if (_editor is not null && _palette is not null && height > 0)
        {
            long totalRows = Math.Max(1, _editor.Layout.TotalRows);
            int bytesPerRow = Math.Max(1, _editor.BytesPerRow);
            if (ShowSearchMarkers && _searchMarkers.Count > 0)
            {
                var pixels = new HashSet<int>();
                foreach (long offset in _searchMarkers)
                {
                    int y = (int)(height * ((double)(offset / bytesPerRow) / totalRows));
                    if (pixels.Add(y) && used < MaxMarkers)
                    {
                        PlaceMarker(used++, y, 2, _palette.SearchMarker);
                    }
                }
            }

            if (ShowCursorMarker)
            {
                double y = height * ((double)_editor.Layout.RowOf(_editor.Cursor) / totalRows);
                PlaceMarker(used++, Math.Min(height - 3, y), 3, _palette.CursorMarker);
            }
        }

        for (int i = used; i < _markers.Count; i++)
        {
            _markers[i].Visibility = Visibility.Collapsed;
        }
    }

    private void PlaceMarker(int index, double y, double height, Microsoft.UI.Xaml.Media.Brush brush)
    {
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
