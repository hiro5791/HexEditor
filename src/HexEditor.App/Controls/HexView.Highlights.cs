using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>強調表示の層 (VIEW-17 の仕様 5 の順位。小さいほど手前)。</summary>
public enum HexHighlightLayer
{
    /// <summary>層 3: 注目している範囲 (インスペクタの対象 INSP-18 など)。</summary>
    Focus = 3,

    /// <summary>層 7: ブックマーク (INSP-23)。</summary>
    Bookmark = 7,
}

/// <summary>
/// Hex 列とテキスト列に重ねる範囲の強調 1 つ。<see cref="Background"/> は背景 (null なら塗らない)、<see cref="Border"/> は枠線
/// (null なら描かない)、<see cref="Dash"/> は枠線の破線の模様 (ハイコントラストで形を変えて区別する)。<see cref="Tag"/> は
/// 提供元の識別 (テスト用の読み出しに出す)。長さ 0 の範囲は位置に細い縦線を描く。
/// </summary>
public sealed record HexHighlight(long Offset, long Length, HexHighlightLayer Layer, Brush? Background, Brush? Border,
    IReadOnlyList<double>? Dash = null, string Tag = "");

/// <summary>オフセット列の目印 (ブックマークの開始位置。INSP-23 の仕様 8、INSP-25 の仕様 5)。<see cref="Text"/> は番号など。</summary>
public sealed record HexOffsetMarker(long Offset, Brush Fill, Brush? Border, string Text, Brush? Foreground, string Tag = "");

/// <summary>
/// 範囲の強調とオフセット列の目印 (インスペクタの対象 INSP-18、ブックマーク INSP-23)。提供元ごとに「この範囲に重なる項目」を
/// 返す関数を登録し、描画のたびに表示範囲だけを問い合わせる (VIEW-04 の仕様 7)。選択範囲・検索の一致 (行の文字の層) より奥に
/// 背景を、手前に枠線を描く。
/// </summary>
public sealed partial class HexView
{
    private readonly Dictionary<string, Func<long, long, IEnumerable<HexHighlight>>> _highlightSources = [];
    private readonly Dictionary<string, Func<long, long, IEnumerable<HexOffsetMarker>>> _markerSources = [];
    private readonly List<Rectangle> _highlightBack = [];
    private readonly List<Rectangle> _highlightFront = [];
    private readonly List<Border> _offsetMarks = [];
    private readonly List<PlacedHighlight> _placed = [];
    private readonly List<(HexHighlight Item, int Order)> _highlightItems = [];
    private readonly HashSet<long> _markerRows = [];
    private readonly List<IReadOnlyList<double>?> _frontDash = [];
    private Canvas? _backLayer;
    private Canvas? _frontLayer;
    private Canvas? _markLayer;
    private Action<MenuFlyout>? _contextMenuOpening;

    /// <summary>描いた強調 (テスト用の読み出し)。Column は "hex" か "text"。</summary>
    private readonly record struct PlacedHighlight(HexHighlightLayer Layer, string Tag, string Column, long First, long Last, Brush? Background,
        Brush? Border, IReadOnlyList<double>? Dash);

    // ハイコントラストの判定は IsHighContrast (HexView.Options.cs。テスト用の模擬 ForcedHighContrast を含む) を使う。

    /// <summary>
    /// 範囲の強調の提供元を登録する (null で外す)。関数は [start, end) と重なる項目を返す。表示のたびに呼ぶため、ブロックしないこと。
    /// </summary>
    public void SetHighlightSource(string name, Func<long, long, IEnumerable<HexHighlight>>? source)
    {
        if (source is null)
        {
            _highlightSources.Remove(name);
        }
        else
        {
            _highlightSources[name] = source;
        }

        QueueRender();
    }

    /// <summary>オフセット列の目印の提供元を登録する (null で外す)。関数は [start, end) から始まる項目を返す。</summary>
    public void SetOffsetMarkerSource(string name, Func<long, long, IEnumerable<HexOffsetMarker>>? source)
    {
        if (source is null)
        {
            _markerSources.Remove(name);
        }
        else
        {
            _markerSources[name] = source;
        }

        QueueRender();
    }

    /// <summary>強調の内容が変わった (提供元が持つ項目の変更) ので描き直す。</summary>
    public void RefreshHighlights() => QueueRender();

    /// <summary>
    /// 右クリックメニューを開く直前に呼ぶ処理 (他の機能が項目を加え、状態を更新する)。加える項目の <c>Tag</c> には文字列を入れる
    /// (Hex ビューは知らない Tag の項目を有効にし、その後でこの処理を呼ぶ)。
    /// </summary>
    public void SetContextMenuExtension(Action<MenuFlyout>? opening)
    {
        _contextMenuOpening = opening;
        if (opening is not null && _contextMenu is null)
        {
            _contextMenu = CreateContextMenu();
            _contextMenu.Opening += (_, _) => _contextMenuOpening?.Invoke(_contextMenu);
            opening(_contextMenu);
        }
    }

    /// <summary>描画の最後に、表示中の範囲の強調と目印を置く (Render から呼ぶ)。</summary>
    private void RenderHighlights(long firstOffset, int rows, RowColumns columns)
    {
        _placed.Clear();
        int bytesPerRow = columns.BytesPerRow;
        long end = firstOffset + (long)rows * bytesPerRow;
        int backUsed = 0;
        int frontUsed = 0;
        int marksUsed = 0;
        if (_highlightSources.Count > 0)
        {
            EnsureHighlightLayers();
            List<(HexHighlight Item, int Order)> items = _highlightItems;
            items.Clear();
            foreach (Func<long, long, IEnumerable<HexHighlight>> source in _highlightSources.Values)
            {
                foreach (HexHighlight h in source(firstOffset, end))
                {
                    items.Add((h, items.Count));
                }
            }

            // 奥の層から描く (同じ層は与えた順)。作業用の一覧は使い回す (描画のたびに作らない。VIEW-04 の仕様 3)。
            items.Sort(static (a, b) => a.Item.Layer != b.Item.Layer ? b.Item.Layer.CompareTo(a.Item.Layer) : a.Order.CompareTo(b.Order));
            foreach ((HexHighlight h, _) in items)
            {
                long from = Math.Max(h.Offset, firstOffset);
                long to = h.Length == 0 ? h.Offset + 1 : Math.Min(h.Offset + h.Length, end);
                if (from >= to || from >= end)
                {
                    continue;
                }

                for (long rowStart = firstOffset + (from - firstOffset) / bytesPerRow * bytesPerRow; rowStart < to; rowStart += bytesPerRow)
                {
                    int c0 = (int)(Math.Max(from, rowStart) - rowStart);
                    int c1 = (int)(Math.Min(to, rowStart + bytesPerRow) - rowStart) - 1;
                    double y = (rowStart - firstOffset) / bytesPerRow * _rowHeight - _subRowOffset;
                    double hexLeft = columns.HexIndex(c0) * _cellWidth;
                    double hexWidth = (columns.HexIndex(c1) + 2) * _cellWidth - hexLeft;
                    double textLeft = columns.TextIndex(c0) * _cellWidth;
                    double textWidth = (c1 - c0 + 1) * _cellWidth;
                    if (h.Length == 0)
                    {
                        hexWidth = textWidth = 2;
                    }

                    PlaceSegment(h, "hex", rowStart + c0, rowStart + c1, hexLeft, y, hexWidth, ref backUsed, ref frontUsed);
                    PlaceSegment(h, "text", rowStart + c0, rowStart + c1, textLeft, y, textWidth, ref backUsed, ref frontUsed);
                }
            }
        }

        if (_markerSources.Count > 0)
        {
            EnsureHighlightLayers();
            HashSet<long> seenRows = _markerRows;
            seenRows.Clear();
            foreach (Func<long, long, IEnumerable<HexOffsetMarker>> source in _markerSources.Values)
            {
                foreach (HexOffsetMarker m in source(firstOffset, end))
                {
                    long row = (m.Offset - firstOffset) / bytesPerRow;
                    if (m.Offset < firstOffset || m.Offset >= end || !seenRows.Add(row))
                    {
                        continue;
                    }

                    PlaceMark(marksUsed++, m, row * _rowHeight - _subRowOffset);
                }
            }
        }

        _highlightItems.Clear();
        Hide(_highlightBack, backUsed);
        Hide(_highlightFront, frontUsed);
        for (int i = marksUsed; i < _offsetMarks.Count; i++)
        {
            _offsetMarks[i].Visibility = Visibility.Collapsed;
        }

        static void Hide(List<Rectangle> pool, int used)
        {
            for (int i = used; i < pool.Count; i++)
            {
                pool[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    private void PlaceSegment(HexHighlight h, string column, long first, long last, double x, double y, double width, ref int backUsed, ref int frontUsed)
    {
        _placed.Add(new PlacedHighlight(h.Layer, h.Tag, column, first, last, h.Background, h.Border, h.Dash));
        if (h.Background is not null)
        {
            Rectangle r = Take(_highlightBack, _backLayer!, backUsed++);
            r.Fill = h.Background;
            r.Stroke = null;
            SetRect(r, x, y, width, _rowHeight);
        }

        if (h.Border is not null)
        {
            Rectangle r = Take(_highlightFront, _frontLayer!, frontUsed++);
            r.Fill = null;
            r.Stroke = h.Border;
            r.StrokeThickness = 1;
            // 破線の模様は変わったときだけ設定する (DoubleCollection を描画のたびに作らない)。
            while (_frontDash.Count <= frontUsed - 1)
            {
                _frontDash.Add(null);
            }

            if (!ReferenceEquals(_frontDash[frontUsed - 1], h.Dash))
            {
                _frontDash[frontUsed - 1] = h.Dash;
                r.StrokeDashArray = h.Dash is null ? null : [.. h.Dash];
            }
            SetRect(r, x, y + 0.5, Math.Max(1, width), Math.Max(1, _rowHeight - 1));
        }
    }

    private static Rectangle Take(List<Rectangle> pool, Canvas layer, int index)
    {
        if (index >= pool.Count)
        {
            var r = new Rectangle { IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(r, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            pool.Add(r);
            layer.Children.Add(r);
        }

        Rectangle rect = pool[index];
        rect.Visibility = Visibility.Visible;
        return rect;
    }

    private void PlaceMark(int index, HexOffsetMarker m, double y)
    {
        if (index >= _offsetMarks.Count)
        {
            var text = new TextBlock
            {
                FontSize = Math.Max(8, _fontSize * 0.75),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false,
            };
            var border = new Border { Child = text, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(border, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            _offsetMarks.Add(border);
            _markLayer!.Children.Add(border);
        }

        Border mark = _offsetMarks[index];
        mark.Visibility = Visibility.Visible;
        mark.Background = m.Fill;
        mark.BorderBrush = m.Border;
        mark.BorderThickness = new Thickness(m.Border is null ? 0 : 1);
        mark.Tag = m.Tag;
        var label = (TextBlock)mark.Child;
        label.Text = m.Text;
        label.Foreground = m.Foreground;
        label.FontSize = Math.Max(8, _fontSize * 0.75);

        // オフセットの数字と内容の列の間 (2 文字分の余白) に置く。
        double width = Math.Max(10, _cellWidth * 1.6);
        Canvas.SetLeft(mark, _digits * _cellWidth + (_cellWidth * 2 - width) / 2);
        Canvas.SetTop(mark, y + 1);
        mark.Width = width;
        mark.Height = Math.Max(4, _rowHeight - 2);
    }

    private void EnsureHighlightLayers()
    {
        if (_backLayer is not null)
        {
            return;
        }

        _backLayer = new Canvas { IsHitTestVisible = false };
        _frontLayer = new Canvas { IsHitTestVisible = false };
        _markLayer = new Canvas { IsHitTestVisible = false };
        foreach (Canvas c in (Canvas[])[_backLayer, _frontLayer, _markLayer])
        {
            AutomationProperties.SetAccessibilityView(c, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }

        // 背景は行の文字 (選択範囲・検索の一致の層を含む) の奥、枠線は手前に置く。
        int rows = ContentHost.Children.IndexOf(RowsLayer);
        ContentHost.Children.Insert(rows, _backLayer);
        ContentHost.Children.Insert(rows + 2, _frontLayer);
        OffsetHost.Children.Add(_markLayer);
    }

#if HEX_TEST_HOOKS
    /// <summary>
    /// 描いた強調と目印 (テスト用の描画内容の読み出し。VIEW-17 の層): 層・提供元・列・最初と最後のバイト・背景色・枠線の色と破線の模様。
    /// </summary>
    public System.Text.Json.Nodes.JsonObject ReadHighlights()
    {
        var segments = new System.Text.Json.Nodes.JsonArray();
        foreach (PlacedHighlight p in _placed)
        {
            segments.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["layer"] = (int)p.Layer,
                ["tag"] = p.Tag,
                ["column"] = p.Column,
                ["first"] = p.First,
                ["last"] = p.Last,
                ["background"] = MaybeColor(p.Background),
                ["border"] = MaybeColor(p.Border),
                ["dash"] = p.Dash is null ? null : string.Join(",", p.Dash),
            });
        }

        var marks = new System.Text.Json.Nodes.JsonArray();
        foreach (Border m in _offsetMarks.Where(m => m.Visibility == Visibility.Visible))
        {
            marks.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["tag"] = m.Tag as string,
                ["text"] = (m.Child as TextBlock)?.Text,
                ["top"] = Canvas.GetTop(m),
                ["fill"] = MaybeColor(m.Background),
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["segments"] = segments,
            ["marks"] = marks,
            ["highContrast"] = IsHighContrast,
            ["rowHeight"] = _rowHeight,
        };

        static string? MaybeColor(Brush? brush) => brush is null ? null : ColorOf(brush);
    }
#endif
}
