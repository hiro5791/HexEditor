using HexEditor.Core.View;
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

    /// <summary>層 8: 注釈 (INSP-32。YARA、すべて検索の結果、スクリプト・プラグイン)。</summary>
    Annotation = 8,

    /// <summary>層 9: テンプレートの範囲の色分け (TPL-23)。</summary>
    Template = 9,

    /// <summary>層 10: 色付けルール (INSP-33)。</summary>
    ColorRule = 10,

    /// <summary>層 11: 差分 (比較 ANA-02〜ANA-04、並列表示の「違いを強調」VIEW-39)。</summary>
    Difference = 11,
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
        _placedBands.Clear();
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
            RowFormat format = columns.Format;
            int texts = format.ShowText ? Math.Max(1, format.ShownTextColumns) : 0;
            long length = _editor?.Layout.Length ?? 0;
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
            foreach ((HexHighlight h, _) in items)
            {
                long from = Math.Max(h.Offset, firstOffset);
                long to = h.Length == 0 ? h.Offset + 1 : Math.Min(h.Offset + h.Length, end);
                if (from >= to || from >= end)
                {
                    continue;
                }

                bool band = h.Background is not null && (int)h.Layer >= (int)HexHighlightLayer.Bookmark && (int)h.Layer <= (int)HexHighlightLayer.ColorRule;
                for (long rowStart = firstOffset + (from - firstOffset) / bytesPerRow * bytesPerRow; rowStart < to; rowStart += bytesPerRow)
                {
                    int c0 = (int)(Math.Max(from, rowStart) - rowStart);
                    int c1 = (int)(Math.Min(to, rowStart + bytesPerRow) - rowStart) - 1;
                    double y = (rowStart - firstOffset) / bytesPerRow * _rowHeight - _subRowOffset;
                    int valid = (int)Math.Clamp(length - rowStart, 0, bytesPerRow);
                    if (format.ShowHex)
                    {
                        if (h.Length == 0)
                        {
                            PlaceSegment(h, "hex", rowStart + c0, rowStart + c1, format.ByteSpan(c0, valid).Start * _cellWidth, y, 2,
                                ref backUsed, ref frontUsed);
                        }
                        else
                        {
                            // 逆順表示などで表示が分かれる範囲は、分けて描く (VIEW-11)。
                            int n = format.HexSpans(c0, c1, valid, spans);
                            for (int i = 0; i < n; i++)
                            {
                                PlaceSegment(h, "hex", rowStart + c0, rowStart + c1, spans[i].Start * _cellWidth, y, spans[i].Length * _cellWidth,
                                    ref backUsed, ref frontUsed);
                            }
                        }
                    }

                    for (int t = 0; t < texts; t++)
                    {
                        double textLeft = format.TextIndex(t, c0) * _cellWidth;
                        double textWidth = h.Length == 0 ? 2 : (c1 - c0 + 1) * _cellWidth;
                        PlaceSegment(h, t == 0 ? "text" : "text" + (t + 1), rowStart + c0, rowStart + c1, textLeft, y, textWidth,
                            ref backUsed, ref frontUsed);
                    }

                    // 層 7〜10 の背景が、選択範囲・検索の一致 (層 2〜5) に隠れるセルでは、下端に高さ 2 px の帯として残す (VIEW-17 の仕様 6)。
                    if (band && h.Length > 0)
                    {
                        PlaceBands(h, format, firstOffset, rowStart, c0, c1, valid, texts, y, ref frontUsed);
                    }
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

    /// <summary>帯の高さ (VIEW-17 の仕様 6)。</summary>
    private const double BandHeight = 2;

    /// <summary>描いた帯 (テスト用の読み出し): 層・提供元・列・最初と最後のバイト・色・高さ。</summary>
    private readonly List<(HexHighlightLayer Layer, string Tag, string Column, long First, long Last, Brush Brush)> _placedBands = [];

    /// <summary>
    /// 選択範囲・検索の一致・注目している範囲に隠れるバイトに、強調の背景の色の帯をセルの下端 (変更の下線のすぐ上) に描く。
    /// </summary>
    private void PlaceBands(HexHighlight h, RowFormat format, long firstOffset, long rowStart, int c0, int c1, int valid, int texts, double y,
        ref int frontUsed)
    {
        long selStart = _editor!.SelectionStart;
        long selEnd = selStart + _editor.SelectionLength;
        bool[] matched = _work.Matched;
        bool Hidden(int c)
        {
            long offset = rowStart + c;
            long index = offset - firstOffset;
            return (offset >= selStart && offset < selEnd) || (index >= 0 && index < matched.Length && matched[index]);
        }

        Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
        for (int c = c0; c <= c1; c++)
        {
            if (!Hidden(c))
            {
                continue;
            }

            int last = c;
            while (last + 1 <= c1 && Hidden(last + 1))
            {
                last++;
            }

            double top = y + _rowHeight - 3 - BandHeight;
            if (format.ShowHex)
            {
                int n = format.HexSpans(c, last, valid, spans);
                for (int i = 0; i < n; i++)
                {
                    PlaceBand(h.Background!, spans[i].Start * _cellWidth, top, spans[i].Length * _cellWidth, ref frontUsed);
                }

                _placedBands.Add((h.Layer, h.Tag, "hex", rowStart + c, rowStart + last, h.Background!));
            }

            for (int t = 0; t < texts; t++)
            {
                PlaceBand(h.Background!, format.TextIndex(t, c) * _cellWidth, top, (last - c + 1) * _cellWidth, ref frontUsed);
                _placedBands.Add((h.Layer, h.Tag, t == 0 ? "text" : "text" + (t + 1), rowStart + c, rowStart + last, h.Background!));
            }

            c = last;
        }
    }

    private void PlaceBand(Brush brush, double x, double y, double width, ref int frontUsed)
    {
        Rectangle r = Take(_highlightFront, _frontLayer!, frontUsed++);
        while (_frontDash.Count <= frontUsed - 1)
        {
            _frontDash.Add(null);
        }

        if (_frontDash[frontUsed - 1] is not null)
        {
            _frontDash[frontUsed - 1] = null;
            r.StrokeDashArray = null;
        }

        r.Fill = brush;
        r.Stroke = null;
        SetRect(r, x, y, Math.Max(1, width), BandHeight);
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

        var bands = new System.Text.Json.Nodes.JsonArray();
        foreach ((HexHighlightLayer layer, string tag, string column, long first, long last, Brush brush) in _placedBands)
        {
            bands.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["layer"] = (int)layer,
                ["tag"] = tag,
                ["column"] = column,
                ["first"] = first,
                ["last"] = last,
                ["color"] = ColorOf(brush),
                ["height"] = BandHeight,
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["segments"] = segments,
            ["bands"] = bands,
            ["marks"] = marks,
            ["highContrast"] = IsHighContrast,
            ["rowHeight"] = _rowHeight,
        };

        static string? MaybeColor(Brush? brush) => brush is null ? null : ColorOf(brush);
    }
#endif
}
