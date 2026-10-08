using HexEditor.Core.View;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 表示の設定 (UI-28 配色、UI-29 フォント、VIEW-07 ツールチップ、VIEW-08 自動の 1 行のバイト数、VIEW-43 ズーム) と、
/// ほかの機能が強調を重ねるための入口 (INSP-18 の注目している範囲、ブックマークの名前)。
/// </summary>
public sealed partial class HexView
{
    /// <summary>自動の 1 行のバイト数を決め直すまでの遅延 (VIEW-08 の仕様 4)。</summary>
    private static readonly TimeSpan AutoFitDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>字形の幅を覚えておく数 (VIEW-04 の仕様 3 の LRU の代わり。超えたら捨てる)。</summary>
    private const int GlyphCacheLimit = 4096;

    private readonly Dictionary<string, double> _glyphWidths = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _autoFitTimer = null!;
    private ColorScheme? _colorScheme;
    private double _screenZoom = 1;
    private double _lineSpacing = 1.2;
    private bool _followTextScaling = true;
    private bool _forcedHighContrast;
    private (long Cursor, long TopRow, double Y)? _keyZoomAnchor;

    private void InitializeOptions()
    {
        _autoFitTimer = _uiQueue.CreateTimer();
        _autoFitTimer.IsRepeating = false;
        _autoFitTimer.Interval = AutoFitDelay;
        _autoFitTimer.Tick += (_, _) => AutoFit();
    }

    /// <summary>Hex 表示の配色 (UI-28)。null は「既定」(テーマのリソースの色)。変えるとすぐに描き直す。</summary>
    public ColorScheme? ColorScheme
    {
        get => _colorScheme;
        set
        {
            _colorScheme = value;
            ReloadPalette();
        }
    }

    /// <summary>
    /// ハイコントラストの描き方を使う (Windows のハイコントラスト、またはテスト用のビルドでの模擬)。ハイコントラストでは、色に頼る層を
    /// 線と模様に置き換え、システム色だけを使う (VIEW-41 の仕様 2)。
    /// </summary>
    internal bool IsHighContrast => _forcedHighContrast || _accessibilitySettings.HighContrast;

    /// <summary>テスト用: OS の設定を変えずにハイコントラストの描き方にする。</summary>
    internal bool ForcedHighContrast
    {
        get => _forcedHighContrast;
        set
        {
            _forcedHighContrast = value;
            ReloadPalette();
        }
    }

    /// <summary>
    /// 画面全体のズームの倍率 (UI-08 の 3)。拡大はウィンドウのルート要素で行う (Controls.ZoomHost) ので、文字の大きさには掛けず、
    /// セル幅と行の高さを物理ピクセルに合わせるときにだけ使う (VIEW-43 の仕様 2)。
    /// </summary>
    public double ScreenZoom
    {
        get => _screenZoom;
        set
        {
            _screenZoom = Math.Clamp(value, 0.25, 8);
            RemeasureAndRender();
        }
    }

    /// <summary>行間 (UI-29 の仕様 4。<c>view.font.lineHeight</c>。1.0〜2.0 倍、既定 1.2)。</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set
        {
            _lineSpacing = Math.Clamp(value, 1.0, 2.0);
            RemeasureAndRender();
        }
    }

    /// <summary>Windows の文字の大きさに合わせて拡大する (UI-29 の仕様 7。<c>view.font.followTextScaling</c>。既定 true)。</summary>
    public bool FollowTextScaling
    {
        get => _followTextScaling;
        set
        {
            _followTextScaling = value;
            RemeasureAndRender();
        }
    }

    /// <summary>設定「ツールチップを表示する」(VIEW-07。既定オン)。</summary>
    public bool ShowToolTips { get; set; } = true;

    /// <summary>削除位置を表示 (VIEW-15 の仕様 5。設定 view.modified.showDeletions、既定オフ)。</summary>
    public bool ShowDeletions
    {
        get => _showDeletions;
        set
        {
            if (_showDeletions != value)
            {
                _showDeletions = value;
                QueueRender();
            }
        }
    }

    private bool _showDeletions;

    /// <summary>表示しない文字の記号 (VIEW-21 の仕様 7。設定 view.text.nonPrintable)。</summary>
    public NonPrintableStyle NonPrintableStyle
    {
        get => _nonPrintableStyle;
        set
        {
            if (_nonPrintableStyle != value)
            {
                _nonPrintableStyle = value;
                QueueRender();
            }
        }
    }

    private NonPrintableStyle _nonPrintableStyle;

    /// <summary>不正なバイトの記号 (VIEW-22 の仕様 5。設定 view.text.invalidSymbol、既定 <c>.</c>)。</summary>
    public string InvalidSymbol
    {
        get => _invalidSymbol;
        set
        {
            if (_invalidSymbol != value)
            {
                _invalidSymbol = value;
                QueueRender();
            }
        }
    }

    private string _invalidSymbol = TextCellDecoder.DefaultInvalidSymbol;

    /// <summary>
    /// バイトに付いている情報の名前 (ブックマーク名など。VIEW-07 の仕様 2 の「付加情報」、UI-51 の「ブックマーク 名前」)。
    /// 提供元 (INSP、TPL) が設定する。範囲で問い合わせるため、ブロックしないこと。
    /// </summary>
    public Func<long, IReadOnlyList<string>>? AnnotationNames { get; set; }

    /// <summary>
    /// 注目している範囲の強調 (VIEW-17 の層 3) を、提供元の名前ごとに設定する。null で消す。範囲の強調の共通の仕組み
    /// (<see cref="SetHighlightSource"/>。HexView.Highlights.cs) の簡易版で、配色の「注目範囲」の色で塗る (ハイコントラストでは枠線)。
    /// </summary>
    public void SetFocusRanges(string source, IReadOnlyList<(long Offset, long Length)>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
        {
            SetHighlightSource("focus:" + source, null);
            return;
        }

        SetHighlightSource("focus:" + source, (start, end) => ranges
            .Where(r => r.Offset < end && r.Offset + r.Length > start)
            .Select(r => _palette.HighContrast
                ? new HexHighlight(r.Offset, r.Length, HexHighlightLayer.Focus, null, _palette.Text, null, source)
                : new HexHighlight(r.Offset, r.Length, HexHighlightLayer.Focus, _palette.FocusRange, null, null, source)));
    }

    // ---- ズーム (VIEW-43) ----

    /// <summary>
    /// Hex 表示のズームの倍率を変える (VIEW-43)。<paramref name="pointerY"/> (描画面の座標) を渡すと、その位置の行が画面上の同じ位置に残る
    /// (Ctrl+ホイール。仕様 4)。null ならカーソルの行が画面上の同じ高さに残る (キーとメニュー)。
    /// </summary>
    public void ZoomAt(double zoom, double? pointerY)
    {
        zoom = Math.Clamp(zoom, 0.25, 8);
        if (zoom == _zoom && _editor is not null)
        {
            return;
        }

        if (_editor is null || _rowHeight <= 0)
        {
            _zoom = zoom;
            RemeasureAndRender();
            return;
        }

        // ズームの前に、基準の行とその画面上の位置を求める。
        long anchorRow;
        double anchorY;
        if (pointerY is { } y)
        {
            anchorRow = _editor.TopRow + (long)Math.Floor((y + _subRowOffset) / _rowHeight);
            anchorY = y;
        }
        else
        {
            anchorRow = _editor.Layout.RowOf(_editor.Cursor);
            anchorY = (anchorRow - _editor.TopRow + 0.5) * _rowHeight - _subRowOffset;

            // 続けてズームしたときは、最初のズームの前の高さを基準にする (端数の切り捨てで少しずつずれないように)。
            if (_keyZoomAnchor is { } kept && kept.Cursor == _editor.Cursor && kept.TopRow == _editor.TopRow)
            {
                anchorY = kept.Y;
            }
        }

        long anchorOffset = Math.Max(0, _editor.Layout.RowStart(anchorRow));
        _zoom = zoom;
        MeasureCell();
        UpdateVisibleRows();

        // 自動の 1 行のバイト数はすぐに決め直す (ズームで 1 行のバイト数も変わる。仕様 5)。
        AutoFit();
        long row = _editor.Layout.RowOf(Math.Min(anchorOffset, _editor.Layout.MaxCursor));

        // ポインタの位置はその行の中に、カーソルの行は中心が元の高さに最も近くなるように置く。
        long top = pointerY is null ? row - (long)Math.Round(anchorY / _rowHeight - 0.5) : row - (long)Math.Floor(anchorY / _rowHeight);
        _subRowOffset = 0;
        _editor.ScrollToRow(top);
        _keyZoomAnchor = pointerY is null ? (_editor.Cursor, _editor.TopRow, anchorY) : null;
        UpdateScrollBar();
        QueueRender();
    }

    // ---- 自動の 1 行のバイト数 (VIEW-08 の仕様 3・4) ----

    private void QueueAutoFit()
    {
        if (_editor is { View.AutoBytesPerRow: true })
        {
            _autoFitTimer.Stop();
            _autoFitTimer.Start();
        }
    }

    /// <summary>表示部分の幅に収まる最大のバイト数を求めてビューに渡す。</summary>
    private void AutoFit()
    {
        if (_editor is not { View.AutoBytesPerRow: true } editor || Surface.ActualWidth <= 0 || _cellWidth <= 0)
        {
            return;
        }

        ViewSettings view = editor.View;
        double available = Surface.ActualWidth - ContentLeft;
        int best = view.AutoFit(n => (RowFormat.For(view, n).LineLength + 1) * _cellWidth <= available);
        editor.SetAutoBytesPerRow(best);
    }

    // ---- 字形の幅 (VIEW-22 の縮小) ----

    private double MeasureGlyph(string text)
    {
        if (_glyphWidths.TryGetValue(text, out double width))
        {
            return width;
        }

        var probe = new TextBlock { Text = text, FontFamily = _font, FontSize = _fontSize, IsTextScaleFactorEnabled = false, IsColorFontEnabled = false };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        width = probe.DesiredSize.Width;
        if (_glyphWidths.Count >= GlyphCacheLimit)
        {
            _glyphWidths.Clear();
        }

        _glyphWidths[text] = width;
        return width;
    }
}
