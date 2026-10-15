using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// スクリプトのコードエディタの部品 (AUTO-20)。Monaco Editor (WebView2) を使う実装と、WebView2 がないときの等幅のテキスト入力欄の
/// 実装 (<see cref="PlainCodeEditor"/>) があり、スクリプトエディタ (<c>ScriptEditorPage</c>) は同じ口で使う。
/// </summary>
public interface IScriptCodeEditor
{
    FrameworkElement View { get; }

    /// <summary>本文。行の区切りは <c>\n</c>。</summary>
    string Text { get; set; }

    bool IsReadOnly { get; set; }

    /// <summary>1 から数える、カーソルの行と列。</summary>
    (int Line, int Column) CaretPosition { get; }

    event EventHandler? TextChanged;

    event EventHandler? CaretMoved;

    /// <summary>左の余白 (行番号とブレークポイント) がクリックされた (行は 1 から)。</summary>
    event EventHandler<int>? GutterClicked;

    /// <summary>行・列 (1 から) にカーソルを置いて見える位置へ動かす。</summary>
    void GoTo(int line, int column);

    /// <summary>ブレークポイントの表示 (赤い丸。無効は白抜き、条件付きは中に「=」。AUTO-21 の画面)。</summary>
    void SetBreakpoints(IReadOnlyList<Breakpoint> breakpoints);

    /// <summary>一時停止中の現在の行の強調表示 (null で消す)。</summary>
    void SetCurrentLine(int? line);

    /// <summary>エラー・警告の波線。</summary>
    void SetMarkers(IReadOnlyList<ScriptDiagnostic> diagnostics);

    bool FocusEditor();
}

/// <summary>
/// 等幅のテキスト入力欄でできたコードエディタ (AUTO-20 の仕様 9: WebView2 ランタイムがない環境の代わり。色分け・補完はない)。
/// 左の余白に行番号とブレークポイント、波線と現在の行の強調は上に重ねて描く。コードの表示は常に左から右 (00 の 5.4)。
/// </summary>
public sealed class PlainCodeEditor : Grid, IScriptCodeEditor
{
    private const double GutterWidth = 64;
    private const double BreakpointArea = 20;

    private readonly TextBox _box;
    private readonly Canvas _gutter = new() { Width = GutterWidth, Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private List<int> _lineStarts = [0];
    private ScrollViewer? _viewer;
    private IReadOnlyList<Breakpoint> _breakpoints = [];
    private IReadOnlyList<ScriptDiagnostic> _markers = [];
    private int? _currentLine;
    private bool _suppressChange;
    private bool _drawQueued;

    public PlainCodeEditor()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 14,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_box, "ScriptEditor_Text");
        SetColumn(_box, 1);
        SetColumn(_overlay, 1);
        SetColumn(_gutter, 0);
        Children.Add(_box);
        Children.Add(_overlay);
        Children.Add(_gutter);
        _box.TextChanged += (_, _) =>
        {
            RecomputeLines();
            QueueDraw();
            if (!_suppressChange)
            {
                TextChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _box.SelectionChanged += (_, _) => CaretMoved?.Invoke(this, EventArgs.Empty);
        _box.Loaded += (_, _) =>
        {
            AttachViewer();
            QueueDraw();
        };
        _box.SizeChanged += (_, _) => QueueDraw();
        _gutter.PointerPressed += Gutter_PointerPressed;
        ActualThemeChanged += (_, _) => QueueDraw();
    }

    public FrameworkElement View => this;

    public string Text
    {
        get => _box.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        set
        {
            _suppressChange = true;
            try
            {
                _box.Text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
            }
            finally
            {
                _suppressChange = false;
            }

            RecomputeLines();
            QueueDraw();
        }
    }

    public bool IsReadOnly
    {
        get => _box.IsReadOnly;
        set => _box.IsReadOnly = value;
    }

    public (int Line, int Column) CaretPosition
    {
        get
        {
            int index = _box.SelectionStart;
            int line = LineOfIndex(index);
            return (line + 1, index - _lineStarts[line] + 1);
        }
    }

    public event EventHandler? TextChanged;

    public event EventHandler? CaretMoved;

    public event EventHandler<int>? GutterClicked;

    /// <summary>内部の入力欄 (フォーカスの判定・テスト用)。</summary>
    public TextBox Box => _box;

    public bool FocusEditor() => _box.Focus(FocusState.Keyboard);

    public void GoTo(int line, int column)
    {
        int index = Math.Clamp(line - 1, 0, _lineStarts.Count - 1);
        int length = (index + 1 < _lineStarts.Count ? _lineStarts[index + 1] - 1 : _box.Text.Length) - _lineStarts[index];
        _box.Select(_lineStarts[index] + Math.Clamp(column - 1, 0, Math.Max(0, length)), 0);
        _box.Focus(FocusState.Programmatic);
        ScrollLineIntoView(index);
        QueueDraw();
    }

    public void SetBreakpoints(IReadOnlyList<Breakpoint> breakpoints)
    {
        _breakpoints = breakpoints;
        QueueDraw();
    }

    public void SetCurrentLine(int? line)
    {
        _currentLine = line;
        if (line is int l)
        {
            ScrollLineIntoView(Math.Max(0, l - 1));
        }

        QueueDraw();
    }

    public void SetMarkers(IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        _markers = diagnostics;
        QueueDraw();
    }

    // ---- 行 ----

    private void RecomputeLines()
    {
        string text = _box.Text;
        var starts = new List<int>(Math.Max(16, text.Length / 32)) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts;
    }

    private int LineOfIndex(int index)
    {
        int i = _lineStarts.BinarySearch(index);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    private void AttachViewer()
    {
        if (_viewer is not null)
        {
            return;
        }

        _viewer = FindViewer(_box);
        if (_viewer is not null)
        {
            _viewer.ViewChanged += (_, _) => QueueDraw();
        }
    }

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer v)
            {
                return v;
            }

            if (FindViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void ScrollLineIntoView(int line0)
    {
        if (_viewer is null)
        {
            AttachViewer();
        }

        if (_viewer is null || _lineStarts.Count == 0)
        {
            return;
        }

        double lineHeight = LineHeight();
        double top = line0 * lineHeight;
        if (top < _viewer.VerticalOffset || top + lineHeight > _viewer.VerticalOffset + _viewer.ViewportHeight)
        {
            _viewer.ChangeView(null, Math.Max(0, top - _viewer.ViewportHeight / 2), null, disableAnimation: true);
        }
    }

    private double LineHeight()
    {
        try
        {
            Rect r = _box.GetRectFromCharacterIndex(0, false);
            if (r.Height > 1)
            {
                return r.Height;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }

        return _box.FontSize * 1.4;
    }

    // ---- 描画 (見えている行だけ) ----

    private void QueueDraw()
    {
        if (_drawQueued)
        {
            return;
        }

        _drawQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _drawQueued = false;
            Draw();
        });
    }

    private void Draw()
    {
        _gutter.Children.Clear();
        _overlay.Children.Clear();
        if (!_box.IsLoaded)
        {
            return;
        }

        double lineHeight = LineHeight();
        double offset = _viewer?.VerticalOffset ?? 0;
        double viewport = _viewer?.ViewportHeight > 0 ? _viewer.ViewportHeight : ActualHeight;
        int first = Math.Max(0, (int)(offset / lineHeight) - 1);
        int last = Math.Min(_lineStarts.Count - 1, (int)((offset + viewport) / lineHeight) + 1);
        Brush secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Brush critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        Brush caution = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        int caretLine = LineOfIndex(_box.SelectionStart);
        for (int line0 = first; line0 <= last; line0++)
        {
            double y = LineTop(line0);
            var number = new TextBlock
            {
                Text = (line0 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontFamily = _box.FontFamily,
                FontSize = _box.FontSize - 2,
                Foreground = line0 == caretLine ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : secondary,
                TextAlignment = TextAlignment.Right,
                Width = GutterWidth - BreakpointArea - 6,
            };
            Canvas.SetLeft(number, BreakpointArea);
            Canvas.SetTop(number, y + 1);
            _gutter.Children.Add(number);
            if (_breakpoints.FirstOrDefault(b => b.Line == line0 + 1) is { } bp)
            {
                _gutter.Children.Add(BreakpointShape(bp, y, lineHeight, critical));
            }

            if (_currentLine == line0 + 1)
            {
                var arrow = new FontIcon { Glyph = "", FontSize = 12, Foreground = accent };
                Canvas.SetLeft(arrow, 3);
                Canvas.SetTop(arrow, y + (lineHeight - 14) / 2);
                _gutter.Children.Add(arrow);
                var band = new Rectangle
                {
                    Width = Math.Max(ActualWidth, 2000),
                    Height = lineHeight,
                    Fill = accent,
                    Opacity = 0.22,
                };
                Canvas.SetLeft(band, 0);
                Canvas.SetTop(band, y);
                _overlay.Children.Add(band);
            }
        }

        foreach (ScriptDiagnostic d in _markers.Where(m => m.Line > 0))
        {
            int line0 = d.Line - 1;
            if (line0 < first || line0 > last || line0 >= _lineStarts.Count)
            {
                continue;
            }

            try
            {
                int lineStart = _lineStarts[line0];
                int lineEnd = line0 + 1 < _lineStarts.Count ? _lineStarts[line0 + 1] - 1 : _box.Text.Length;
                int startIndex = Math.Min(lineEnd, lineStart + Math.Max(0, d.Column - 1));
                int endIndex = Math.Max(startIndex, Math.Min(lineEnd, startIndex + 1));
                if (startIndex == lineEnd && lineEnd > lineStart)
                {
                    startIndex = lineEnd - 1;
                }

                Rect a = _box.GetRectFromCharacterIndex(startIndex, false);
                Rect b = _box.GetRectFromCharacterIndex(Math.Max(startIndex + 1, endIndex), false);
                var line = new Rectangle
                {
                    Height = 2,
                    Width = Math.Max(8, b.X - a.X),
                    Fill = d.IsError ? critical : caution,
                };
                Canvas.SetLeft(line, a.X);
                Canvas.SetTop(line, a.Y + a.Height - 2);
                _overlay.Children.Add(line);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 文字の位置を引けないときは波線を省く。
            }
        }
    }

    /// <summary>行の上端の位置 (入力欄の中の位置。スクロールを含む)。</summary>
    private double LineTop(int line0)
    {
        try
        {
            return _box.GetRectFromCharacterIndex(_lineStarts[line0], false).Y;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return line0 * LineHeight() - (_viewer?.VerticalOffset ?? 0);
        }
    }

    private static UIElement BreakpointShape(Breakpoint bp, double y, double lineHeight, Brush critical)
    {
        double size = Math.Min(12, lineHeight - 4);
        var grid = new Grid { Width = size, Height = size };
        var circle = new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = critical,
            StrokeThickness = 2,
            Fill = bp.Enabled ? critical : (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
        };
        grid.Children.Add(circle);
        if (bp.HasCondition)
        {
            // 条件付きは丸の中に「=」(色だけで区別しない。AUTO-21 の画面)。
            grid.Children.Add(new TextBlock
            {
                Text = "=",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = bp.Enabled ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] : critical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        AutomationProperties.SetName(grid, Services.Loc.Get(bp.Enabled ? (bp.HasCondition ? "ScriptEditor_BreakpointConditional" : "ScriptEditor_Breakpoint") : "ScriptEditor_BreakpointDisabled"));
        AutomationProperties.SetAutomationId(grid, "ScriptEditor_BreakpointMark");
        Canvas.SetLeft(grid, 4);
        Canvas.SetTop(grid, y + (lineHeight - size) / 2);
        return grid;
    }

    private void Gutter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(_gutter).Position;
        double lineHeight = LineHeight();
        for (int line0 = 0; line0 < _lineStarts.Count; line0++)
        {
            double top = LineTop(line0);
            if (p.Y >= top && p.Y < top + lineHeight)
            {
                GutterClicked?.Invoke(this, line0 + 1);
                e.Handled = true;
                return;
            }

            if (top > p.Y + lineHeight)
            {
                break;
            }
        }
    }

    /// <summary>テスト用: 行頭の文字位置。</summary>
    internal IReadOnlyList<int> LineStarts => _lineStarts;
}
