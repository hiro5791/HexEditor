using HexEditor.App.Services;
using HexEditor.Core.Scripting.Debugging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace HexEditor.App.Controls;

/// <summary>エラー・警告の印 (波線・行の印・ツールチップ)。行・列は 1 から、長さは文字数 (0 なら 1 文字)。</summary>
public sealed record CodeMarker(int Line, int Column, int Length, bool IsError, string Message);

/// <summary>構文の色分けの 1 区間 (文字の位置は 0 から、行の区切りの <c>\n</c> は 1 文字)。<see cref="Class"/> は色の分類の名前。</summary>
public readonly record struct CodeSpan(int Start, int Length, string Class);

/// <summary>
/// コードエディタの部品 (08 の AUTO-20、07 の TPL-31)。スクリプトエディタ (<c>ScriptEditorPage</c>) とテンプレートエディタ
/// (<c>TemplateEditorPage</c>) は、どちらもこの口だけを使い、部品は <see cref="CodeEditorFactory"/> が作る。Monaco Editor (WebView2) を
/// 同梱するときは、この口の実装を差し替えれば両方のエディタが変わる。今の実装は WebView2 がないときの代替 (AUTO-20 の仕様 9) と同じ
/// 標準のリッチテキストの入力欄 (<see cref="CodeEditor"/>)。
/// </summary>
public interface IScriptCodeEditor
{
    FrameworkElement View { get; }

    /// <summary>本文。行の区切りは <c>\n</c>。</summary>
    string Text { get; set; }

    bool IsReadOnly { get; set; }

    /// <summary>1 から数える、カーソルの行と列。</summary>
    (int Line, int Column) CaretPosition { get; }

    /// <summary>カーソルの文字の位置 (0 から。<c>\n</c> は 1 文字)。</summary>
    int CaretIndex { get; set; }

    /// <summary>選択の範囲 (文字の位置)。</summary>
    (int Start, int End) Selection { get; }

    /// <summary>入力欄にフォーカスがあるか。</summary>
    bool HasFocus { get; }

    event EventHandler? TextChanged;

    event EventHandler? CaretMoved;

    /// <summary>左の余白 (行番号とブレークポイント) がクリックされた (行は 1 から)。</summary>
    event EventHandler<int>? GutterClicked;

    /// <summary>文字が入力された (補完の候補を出すきっかけ)。</summary>
    event EventHandler<char>? CharacterTyped;

    /// <summary>入力欄より先に見るキー (補完の一覧の ↑ ↓ Enter など)。処理したら true。</summary>
    Func<VirtualKey, bool>? PreviewKey { get; set; }

    /// <summary>色の分類の名前とテーマ (Light / Dark / HighContrast) から色を引く (null なら既定の文字色)。</summary>
    Func<string, string, Color?>? ClassColor { get; set; }

    /// <summary>行・列 (1 から) にカーソルを置いて見える位置へ動かす。</summary>
    void GoTo(int line, int column);

    /// <summary>範囲を置き換える (補完の確定)。</summary>
    void Replace(int start, int length, string text);

    /// <summary>カーソルの位置の矩形 (<paramref name="relativeTo"/> の座標)。補完の一覧を出す位置。</summary>
    Rect CaretBounds(UIElement relativeTo);

    /// <summary>ブレークポイントの表示 (赤い丸。無効は白抜き、条件付きは中に「=」。AUTO-21 の画面)。</summary>
    void SetBreakpoints(IReadOnlyList<Breakpoint> breakpoints);

    /// <summary>一時停止中の現在の行の強調表示 (null で消す)。</summary>
    void SetCurrentLine(int? line);

    /// <summary>エラー・警告の波線と行の印。</summary>
    void SetMarkers(IReadOnlyList<CodeMarker> markers);

    /// <summary>構文の色分け (大きな本文では色分けしない)。</summary>
    void SetClassification(IReadOnlyList<CodeSpan> spans);

    bool FocusEditor();
}

/// <summary>コードエディタの部品を作る (Monaco を同梱するときはここで切り替える)。</summary>
public static class CodeEditorFactory
{
    /// <param name="automationId">入力欄の AutomationId。</param>
    /// <param name="accessibleName">入力欄のアクセシブルな名前。</param>
    public static IScriptCodeEditor Create(string automationId, string accessibleName) => new CodeEditor(automationId, accessibleName);
}

/// <summary>
/// 標準のリッチテキストの入力欄でできたコードエディタ (Monaco の代替。AUTO-20 の仕様 9、TPL-31 の仕様 2 の「現在の実装」)。左の余白に行番号・
/// ブレークポイント・エラーの印・現在の行の矢印を描き、波線と現在の行の強調は上に重ねて描く (文書の書式は色分けだけに使い、元に戻す操作の
/// 履歴を汚さない)。コードの表示は常に左から右 (00 の 5.4)。描くのは見えている行だけ。
/// </summary>
public sealed partial class CodeEditor : Grid, IScriptCodeEditor
{
    private const double GutterWidth = 64;
    private const double BreakpointArea = 20;

    /// <summary>これより大きな本文は色分けしない (入力の遅れを抑える。TPL-31 の「巨大ファイル」)。</summary>
    private const int MaxColoredLength = 1024 * 1024;

    private readonly RichEditBox _box;
    private readonly Canvas _gutter = new() { Width = GutterWidth, Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private string _text = string.Empty;
    private List<int> _lineStarts = [0];
    private bool _trailingAdded;
    private ScrollViewer? _viewer;
    private IReadOnlyList<Breakpoint> _breakpoints = [];
    private IReadOnlyList<CodeMarker> _markers = [];
    private IReadOnlyList<CodeSpan> _spans = [];
    private int? _currentLine;
    private bool _suppressChange;
    private bool _drawQueued;

    public CodeEditor(string automationId, string accessibleName)
    {
        FlowDirection = FlowDirection.LeftToRight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 14,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Left,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_box, automationId);
        AutomationProperties.SetName(_box, accessibleName);
        AutomationProperties.SetAutomationId(_gutter, automationId + "_Gutter");
        SetColumn(_box, 1);
        SetColumn(_overlay, 1);
        SetColumn(_gutter, 0);
        Children.Add(_box);
        Children.Add(_overlay);
        Children.Add(_gutter);
        _box.TextChanged += (_, _) => Box_TextChanged();
        _box.SelectionChanged += (_, _) =>
        {
            QueueDraw();
            CaretMoved?.Invoke(this, EventArgs.Empty);
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (PreviewKey?.Invoke(e.Key) == true)
            {
                e.Handled = true;
            }
        };
        _box.CharacterReceived += (_, e) => CharacterTyped?.Invoke(this, e.Character);
        _box.PointerMoved += Box_PointerMoved;
        _box.Loaded += (_, _) =>
        {
            AttachViewer();
            QueueDraw();
        };
        _box.SizeChanged += (_, _) => QueueDraw();
        _gutter.PointerPressed += Gutter_PointerPressed;
        ActualThemeChanged += (_, _) =>
        {
            ApplyColoring();
            QueueDraw();
        };
    }

    public FrameworkElement View => this;

    /// <summary>内部の入力欄 (フライアウトの位置・テスト用)。</summary>
    internal RichEditBox Box => _box;

    public string Text
    {
        get => _text;
        set
        {
            string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            _suppressChange = true;
            try
            {
                _box.Document.SetText(TextSetOptions.None, normalized);
            }
            finally
            {
                _suppressChange = false;
            }

            // リッチテキストの入力欄は末尾に段落の区切りを 1 つ足すことがある (読むときに外す)
            string raw = RawText();
            _trailingAdded = raw.Length == normalized.Length + 1 && raw.EndsWith('\n') && raw.StartsWith(normalized, StringComparison.Ordinal);
            _text = ReadText();
            RecomputeLines();
            ApplyColoring();
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
            int index = CaretIndex;
            int line = LineOfIndex(index);
            return (line + 1, index - _lineStarts[line] + 1);
        }
    }

    public int CaretIndex
    {
        get => Math.Clamp(_box.Document.Selection.StartPosition, 0, _text.Length);
        set
        {
            int p = Math.Clamp(value, 0, _text.Length);
            _box.Document.Selection.SetRange(p, p);
        }
    }

    public (int Start, int End) Selection =>
        (Math.Clamp(_box.Document.Selection.StartPosition, 0, _text.Length), Math.Clamp(_box.Document.Selection.EndPosition, 0, _text.Length));

    public bool HasFocus => _box.FocusState != FocusState.Unfocused;

    public event EventHandler? TextChanged;

    public event EventHandler? CaretMoved;

    public event EventHandler<int>? GutterClicked;

    public event EventHandler<char>? CharacterTyped;

    public Func<VirtualKey, bool>? PreviewKey { get; set; }

    public Func<string, string, Color?>? ClassColor { get; set; }

    public bool FocusEditor() => _box.Focus(FocusState.Keyboard);

    public void GoTo(int line, int column)
    {
        int index = Math.Clamp(line - 1, 0, _lineStarts.Count - 1);
        int length = LineEnd(index) - _lineStarts[index];
        int p = _lineStarts[index] + Math.Clamp(column - 1, 0, Math.Max(0, length));
        _box.Document.Selection.SetRange(p, p);
        _box.Document.Selection.ScrollIntoView(PointOptions.None);
        _box.Focus(FocusState.Programmatic);
        QueueDraw();
    }

    public void Replace(int start, int length, string text)
    {
        int s = Math.Clamp(start, 0, _text.Length);
        int e = Math.Clamp(start + length, s, _text.Length);
        _box.Document.GetRange(s, e).Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
        CaretIndex = s + text.Length;
    }

    public Rect CaretBounds(UIElement relativeTo)
    {
        _box.Document.Selection.GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
        Point origin = _box.TransformToVisual(relativeTo).TransformPoint(new Point(rect.Left, rect.Top));
        return new Rect(origin.X, origin.Y, Math.Max(1, rect.Width), rect.Height);
    }

    public void SetBreakpoints(IReadOnlyList<Breakpoint> breakpoints)
    {
        _breakpoints = breakpoints;
        QueueDraw();
    }

    public void SetCurrentLine(int? line)
    {
        _currentLine = line;
        if (line is int l && l >= 1 && l <= _lineStarts.Count)
        {
            _box.Document.GetRange(_lineStarts[l - 1], _lineStarts[l - 1]).ScrollIntoView(PointOptions.None);
        }

        QueueDraw();
    }

    public void SetMarkers(IReadOnlyList<CodeMarker> markers)
    {
        _markers = markers;
        QueueDraw();
    }

    public void SetClassification(IReadOnlyList<CodeSpan> spans)
    {
        _spans = spans;
        ApplyColoring();
    }

    // ---- 本文 ----

    private string RawText()
    {
        _box.Document.GetText(TextGetOptions.None, out string raw);
        return raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private string ReadText()
    {
        string text = RawText();
        return _trailingAdded && text.EndsWith('\n') ? text[..^1] : text;
    }

    private void Box_TextChanged()
    {
        if (_suppressChange)
        {
            return;
        }

        _text = ReadText();
        RecomputeLines();
        QueueDraw();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecomputeLines()
    {
        string text = _text;
        var starts = new List<int>(Math.Max(16, text.Length / 32)) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
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

    private int LineEnd(int line0) => line0 + 1 < _lineStarts.Count ? _lineStarts[line0 + 1] - 1 : _text.Length;

    private int IndexOf(int line, int column)
    {
        int line0 = Math.Clamp(line - 1, 0, _lineStarts.Count - 1);
        return Math.Min(LineEnd(line0), _lineStarts[line0] + Math.Max(0, column - 1));
    }

    // ---- 色分け (文書の書式。テーマの色、ハイコントラストではシステム色だけ) ----

    private void ApplyColoring()
    {
        if (_text.Length > MaxColoredLength || (_spans.Count == 0 && !_colored))
        {
            return;
        }

        bool hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        string theme = AnnotationBrushes.ThemeOf(this, hc);
        Color fg = ((SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"]).Color;
        int caretStart = _box.Document.Selection.StartPosition;
        int caretEnd = _box.Document.Selection.EndPosition;
        _suppressChange = true;
        _box.Document.BatchDisplayUpdates();
        try
        {
            _box.Document.GetRange(0, _text.Length).CharacterFormat.ForegroundColor = fg;
            if (!hc && ClassColor is { } colorOf)
            {
                foreach (CodeSpan s in _spans)
                {
                    if (s.Start >= 0 && s.Start + s.Length <= _text.Length && colorOf(s.Class, theme) is { } c)
                    {
                        _box.Document.GetRange(s.Start, s.Start + s.Length).CharacterFormat.ForegroundColor = c;
                    }
                }
            }

            _colored = _spans.Count > 0;
        }
        finally
        {
            _box.Document.ApplyDisplayUpdates();
            _box.Document.Selection.SetRange(caretStart, caretEnd);
            _suppressChange = false;
        }
    }

    private bool _colored;

    // ---- ツールチップ (波線の上のエラー・警告の理由) ----

    /// <summary>位置のエラー・警告の理由 (なければその行の最初のもの)。</summary>
    public string? MarkerAt(int line, int column) =>
        _markers.FirstOrDefault(m => m.Line == line && column >= m.Column && column < m.Column + Math.Max(1, m.Length))?.Message
        ?? _markers.FirstOrDefault(m => m.Line == line)?.Message;

    private void Box_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_markers.Count == 0)
        {
            ToolTipService.SetToolTip(_box, null);
            return;
        }

        Point p = e.GetCurrentPoint(_box).Position;
        ITextRange range = _box.Document.GetRangeFromPoint(p, PointOptions.ClientCoordinates);
        int index = Math.Clamp(range.StartPosition, 0, _text.Length);
        int line0 = LineOfIndex(index);
        ToolTipService.SetToolTip(_box, MarkerAt(line0 + 1, index - _lineStarts[line0] + 1));
    }

    // ---- 余白と重ねる描画 (見えている行だけ) ----

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

    /// <summary>行頭の矩形 (入力欄の座標。スクロールを含む)。</summary>
    private Rect LineRect(int line0)
    {
        int p = _lineStarts[Math.Clamp(line0, 0, _lineStarts.Count - 1)];
        _box.Document.GetRange(p, p).GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
        return rect;
    }

    private void Draw()
    {
        _gutter.Children.Clear();
        _overlay.Children.Clear();
        if (!_box.IsLoaded || _box.ActualHeight <= 0)
        {
            return;
        }

        // 見えている行: 上端と下端の位置の文字から求める
        int top = Math.Clamp(_box.Document.GetRangeFromPoint(new Point(2, 2), PointOptions.ClientCoordinates).StartPosition, 0, _text.Length);
        int bottom = Math.Clamp(_box.Document.GetRangeFromPoint(new Point(2, Math.Max(2, _box.ActualHeight - 2)), PointOptions.ClientCoordinates).StartPosition, 0, _text.Length);
        int first = Math.Max(0, LineOfIndex(top) - 1);
        int last = Math.Min(_lineStarts.Count - 1, LineOfIndex(bottom) + 1);
        Brush secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Brush primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        Brush critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        Brush caution = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        int caretLine = LineOfIndex(CaretIndex);
        for (int line0 = first; line0 <= last; line0++)
        {
            Rect r = LineRect(line0);
            if (r.Bottom < -20 || r.Top > _box.ActualHeight + 20)
            {
                continue;
            }

            double lineHeight = r.Height > 1 ? r.Height : _box.FontSize * 1.4;
            double y = r.Top;
            var number = new TextBlock
            {
                Text = (line0 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontFamily = _box.FontFamily,
                FontSize = _box.FontSize - 2,
                Foreground = line0 == caretLine ? primary : secondary,
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
            else if (_markers.FirstOrDefault(m => m.Line == line0 + 1) is { } marker)
            {
                var mark = new FontIcon
                {
                    Glyph = marker.IsError ? "" : "",
                    FontSize = 12,
                    Foreground = marker.IsError ? critical : caution,
                };
                ToolTipService.SetToolTip(mark, marker.Message);
                AutomationProperties.SetName(mark, marker.Message);
                Canvas.SetLeft(mark, 4);
                Canvas.SetTop(mark, y + (lineHeight - 14) / 2);
                _gutter.Children.Add(mark);
            }

            if (_currentLine == line0 + 1)
            {
                var arrow = new FontIcon { Glyph = "", FontSize = 12, Foreground = accent };
                Canvas.SetLeft(arrow, 3);
                Canvas.SetTop(arrow, y + (lineHeight - 14) / 2);
                _gutter.Children.Add(arrow);
                var band = new Rectangle { Width = Math.Max(ActualWidth, 2000), Height = lineHeight, Fill = accent, Opacity = 0.22 };
                Canvas.SetLeft(band, 0);
                Canvas.SetTop(band, y);
                _overlay.Children.Add(band);
            }
        }

        foreach (CodeMarker m in _markers.Where(m => m.Line > 0))
        {
            int line0 = m.Line - 1;
            if (line0 < first || line0 > last || line0 >= _lineStarts.Count)
            {
                continue;
            }

            int start = IndexOf(m.Line, m.Column);
            int end = Math.Min(LineEnd(line0), start + Math.Max(1, m.Length));
            if (start >= end && start > _lineStarts[line0])
            {
                start--;
            }

            _box.Document.GetRange(start, start).GetRect(PointOptions.ClientCoordinates, out Rect a, out _);
            _box.Document.GetRange(Math.Max(start + 1, end), Math.Max(start + 1, end)).GetRect(PointOptions.ClientCoordinates, out Rect b, out _);
            var underline = new Rectangle
            {
                Height = 2,
                Width = Math.Max(8, b.Left - a.Left),
                Fill = m.IsError ? critical : caution,
            };
            Canvas.SetLeft(underline, a.Left);
            Canvas.SetTop(underline, a.Top + a.Height - 2);
            _overlay.Children.Add(underline);
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

        AutomationProperties.SetName(grid, Loc.Get(bp.Enabled ? (bp.HasCondition ? "ScriptEditor_BreakpointConditional" : "ScriptEditor_Breakpoint") : "ScriptEditor_BreakpointDisabled"));
        AutomationProperties.SetAutomationId(grid, "ScriptEditor_BreakpointMark");
        Canvas.SetLeft(grid, 4);
        Canvas.SetTop(grid, y + (lineHeight - size) / 2);
        return grid;
    }

    private void Gutter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(_gutter).Position;
        ITextRange range = _box.Document.GetRangeFromPoint(new Point(2, p.Y), PointOptions.ClientCoordinates);
        GutterClicked?.Invoke(this, LineOfIndex(Math.Clamp(range.StartPosition, 0, _text.Length)) + 1);
        e.Handled = true;
    }

    /// <summary>テスト用: 行頭の文字位置。</summary>
    internal IReadOnlyList<int> LineStarts => _lineStarts;
}
