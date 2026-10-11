using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 注釈と色付けルールのための Hex ビューの口 (INSP-31〜INSP-34):
/// <list type="bullet">
/// <item><see cref="CellForegroundSource"/>: セルの文字色 (色付けルール、VIEW-17 の層 10)。背景と枠線は範囲の強調 (<see cref="SetHighlightSource"/>) で描く。</item>
/// <item><see cref="AnnotationColumnText"/>: 注釈の列 (INSP-32 の仕様 4。テキスト列の右)。</item>
/// <item><see cref="RichToolTipContent"/>: ツールチップの注釈の部分 (Markdown を描いたもの。INSP-31 の仕様 4)。</item>
/// </list>
/// </summary>
public sealed partial class HexView
{
    /// <summary>注釈の列の幅 (文字数)。</summary>
    public const int AnnotationColumnChars = 24;

    private Brush?[] _ruleHexWork = [];
    private Brush?[] _ruleTextWork = [];
    private Canvas? _annotationColumnLayer;
    private readonly List<TextBlock> _annotationLabels = [];
    private readonly List<(long Row, string Text)> _annotationRows = [];
    private bool _annotationColumnVisible;
    private double _annotationColumnX;

    /// <summary>
    /// セルの文字色を返す関数 (表示範囲の先頭のオフセット、バイト数、Hex 列の色、テキスト列の色)。色がなければ null を入れる。色を 1 つでも
    /// 入れたら true。表示のたびに呼ぶのでブロックしないこと。
    /// </summary>
    public Func<long, int, Brush?[], Brush?[], bool>? CellForegroundSource { get; set; }

    /// <summary>注釈の列の文字 (その行 [開始, 終了) で始まる注釈のラベル。なければ null)。</summary>
    public Func<long, long, string?>? AnnotationColumnText { get; set; }

    /// <summary>ツールチップに加える注釈の内容 (なければ null)。</summary>
    public Func<long, FrameworkElement?>? RichToolTipContent { get; set; }

    /// <summary>注釈の列を表示する (既定は非表示)。</summary>
    public bool AnnotationColumnVisible
    {
        get => _annotationColumnVisible;
        set
        {
            if (_annotationColumnVisible != value)
            {
                _annotationColumnVisible = value;
                UpdateColumnsLayout();
                QueueRender();
            }
        }
    }

    /// <summary>注釈の列の分だけ内容の幅を広げる文字数 (列の間の 2 文字を含む)。</summary>
    private int AnnotationColumnExtra => _annotationColumnVisible ? AnnotationColumnChars + 2 : 0;

    private bool FillCellForegrounds(long firstOffset, int span)
    {
        if (CellForegroundSource is not { } source)
        {
            return false;
        }

        if (_ruleHexWork.Length < span)
        {
            _ruleHexWork = new Brush?[span];
            _ruleTextWork = new Brush?[span];
        }
        else
        {
            Array.Clear(_ruleHexWork, 0, span);
            Array.Clear(_ruleTextWork, 0, span);
        }

        return source(firstOffset, span, _ruleHexWork, _ruleTextWork);
    }

    // ---- 軽い背景 (HexHighlight.LightBackground) ----

    /// <summary>
    /// 背景を合成の図形で塗る (強調の背景の層の奥。セルごとの強調が多い提供元のため)。行の下の面と同じ背景の面 (<see cref="SpriteSurface"/>)
    /// の、行の層より手前の層に描く。
    /// </summary>
    private void PlaceLightBackground(Brush background, double x, double y, double width, double height) =>
        Sprites.Highlights.Fill(background, x, y, width, height);

    /// <summary>今回の描画で使わなかった図形を隠す。</summary>
    private void HideLightBackgrounds() => Sprites.Highlights.End();

    /// <summary>注釈の列を描く (Render から呼ぶ)。行ごとに、その行で始まる注釈のラベルを出す。</summary>
    private void RenderAnnotationColumn(long firstOffset, int rows, RowColumns columns)
    {
        _annotationRows.Clear();
        int used = 0;
        if (_annotationColumnVisible && AnnotationColumnText is { } text)
        {
            if (_annotationColumnLayer is null)
            {
                _annotationColumnLayer = new Canvas { IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(_annotationColumnLayer, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                ContentHost.Children.Add(_annotationColumnLayer);
            }

            _annotationColumnX = (columns.LineLength + 2) * _cellWidth;
            int bytesPerRow = columns.BytesPerRow;
            for (int r = 0; r < rows; r++)
            {
                long rowStart = firstOffset + (long)r * bytesPerRow;
                if (text(Math.Max(0, rowStart), rowStart + bytesPerRow) is not { Length: > 0 } label)
                {
                    continue;
                }

                if (used >= _annotationLabels.Count)
                {
                    var block = new TextBlock
                    {
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        IsTextScaleFactorEnabled = false,
                    };
                    _annotationLabels.Add(block);
                    _annotationColumnLayer.Children.Add(block);
                }

                TextBlock t = _annotationLabels[used++];
                t.Visibility = Visibility.Visible;
                t.Text = label;
                t.FontFamily = _rows.Count > 0 ? _rows[0].Content.FontFamily : t.FontFamily;
                t.FontSize = _fontSize;
                t.Foreground = (Brush)Application.Current.Resources["AnnotationColumnTextBrush"];
                t.Width = AnnotationColumnChars * _cellWidth;
                Canvas.SetLeft(t, _annotationColumnX);
                Canvas.SetTop(t, r * _rowHeight - _subRowOffset);
                _annotationRows.Add((_editor!.TopRow + r, label));
            }
        }

        for (int i = used; i < _annotationLabels.Count; i++)
        {
            _annotationLabels[i].Visibility = Visibility.Collapsed;
        }
    }

#if HEX_TEST_HOOKS
    /// <summary>
    /// 注釈の説明の要素の文字列と書式 (テスト用): TextBlock は {text, bold}、RichTextBlock は段落の文字ごとに {text, bold, size}。
    /// </summary>
    internal static System.Text.Json.Nodes.JsonArray? RichToolTipRuns(FrameworkElement? root)
    {
        if (root is null)
        {
            return null;
        }

        var runs = new System.Text.Json.Nodes.JsonArray();
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case TextBlock t:
                    runs.Add(new System.Text.Json.Nodes.JsonObject { ["text"] = t.Text, ["bold"] = t.FontWeight.Weight >= 600, ["size"] = t.FontSize });
                    return;
                case RichTextBlock r:
                    foreach (Microsoft.UI.Xaml.Documents.Block block in r.Blocks)
                    {
                        if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
                        {
                            AddInlines(p.Inlines, p.FontWeight.Weight >= 600, p.FontSize);
                        }
                    }

                    return;
                case Panel panel:
                    foreach (UIElement child in panel.Children)
                    {
                        Walk(child);
                    }

                    return;
                case ScrollViewer { Content: DependencyObject content }:
                    Walk(content);
                    return;
            }
        }

        void AddInlines(Microsoft.UI.Xaml.Documents.InlineCollection inlines, bool bold, double size)
        {
            foreach (Microsoft.UI.Xaml.Documents.Inline inline in inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                {
                    runs.Add(new System.Text.Json.Nodes.JsonObject { ["text"] = run.Text, ["bold"] = bold || run.FontWeight.Weight >= 600, ["size"] = size });
                }
                else if (inline is Microsoft.UI.Xaml.Documents.Span span)
                {
                    AddInlines(span.Inlines, bold || span is Microsoft.UI.Xaml.Documents.Bold || span.FontWeight.Weight >= 600, size);
                }
            }
        }

        Walk(root);
        return runs;
    }

    /// <summary>注釈の列の描画内容 (テスト用): 列の x 座標、テキスト列の x 座標、行ごとの文字列。</summary>
    internal System.Text.Json.Nodes.JsonObject ReadAnnotationColumn()
    {
        var rows = new System.Text.Json.Nodes.JsonArray();
        foreach ((long row, string text) in _annotationRows)
        {
            rows.Add(new System.Text.Json.Nodes.JsonObject { ["row"] = row, ["text"] = text });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["visible"] = _annotationColumnVisible,
            ["x"] = _annotationColumnX,
            ["textX"] = new RowColumns(_format).TextIndex(0) * _cellWidth,
            ["rows"] = rows,
        };
    }
#endif
}
