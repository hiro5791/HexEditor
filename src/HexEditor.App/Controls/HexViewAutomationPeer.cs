using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビューの UI オートメーション (UI-50、VIEW-41 の仕様 5・6、00-overview 11.5)。
/// <list type="bullet">
/// <item>Value パターン: カーソル位置の要約 (UI-51 の読み上げ文と同じ)。SetValue でカーソル位置から Hex 文字列を上書きする。</item>
/// <item>Text パターン: 見えている行を「オフセット Hex 列 テキスト列」の文字列で返す (ファイル全体をテキストにしない)。</item>
/// <item>Grid パターン: 見えている行 × (オフセット列 + Hex 列 + テキスト列) のセル。セルの ItemStatus に描画の情報を入れる
/// (UI テストの「描画モデルの取得」)。</item>
/// </list>
/// </summary>
public sealed partial class HexViewAutomationPeer(HexView owner) : FrameworkElementAutomationPeer(owner), IValueProvider, ITextProvider, IGridProvider
{
    private readonly Dictionary<(int Row, int Column), HexCellAutomationPeer> _cells = [];
    private List<AutomationPeer>? _children;

    internal HexView View => owner;

    // ---- AutomationPeer ----

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Value or PatternInterface.Text or PatternInterface.Grid => this,
        _ => base.GetPatternCore(patternInterface),
    };

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        owner.Editor?.ReadOnly == true ? AutomationControlType.Document : AutomationControlType.Edit;

    protected override string GetLocalizedControlTypeCore() => Loc.Get("HexView_ControlType");

    protected override string GetClassNameCore() => nameof(HexView);

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return string.IsNullOrEmpty(owner.DocumentName) ? Loc.Get("HexView_ControlType") : owner.DocumentName;
    }

    protected override string GetHelpTextCore()
    {
        string help = base.GetHelpTextCore();
        return string.IsNullOrEmpty(help) ? Loc.Get("HexView_HelpText") : help;
    }

    protected override string GetItemStatusCore() => owner.RenderSummary();

    protected override bool IsKeyboardFocusableCore() => true;

    protected override IList<AutomationPeer> GetChildrenCore() => Children();

    protected override AutomationPeer? GetPeerFromPointCore(Point point)
    {
        Point local = owner.FromScreen(point);
        if (owner.TryHitTest(local, out _))
        {
            int row = (int)Math.Floor((local.Y - owner.RowTop(0)) / Math.Max(1, owner.RowHeight));
            int column = ColumnAtPoint(local.X);
            if (row >= 0 && row < RowCount && column >= 0)
            {
                return Cell(row, column);
            }
        }

        return base.GetPeerFromPointCore(point);
    }

    internal void InvalidateCells() => _children = null;

    internal IRawElementProviderSimple Provider(AutomationPeer peer) => ProviderFromPeer(peer);

    internal AutomationPeer FromProvider(IRawElementProviderSimple provider) => PeerFromProvider(provider);

    private List<AutomationPeer> Children()
    {
        if (_children is not null && _children.Count >= RowCount * ColumnCount)
        {
            return _children;
        }

        var list = new List<AutomationPeer>(RowCount * ColumnCount + 2);
        for (int r = 0; r < RowCount; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                list.Add(Cell(r, c));
            }
        }

        // スクロールバーと列見出しのオフセット列の見出し (VIEW-05 の仕様 7) も子として残す (UI テストが AutomationId で探す)。
        foreach (UIElement bar in owner.ScrollBars.Concat(owner.HeaderElements))
        {
            if (bar.Visibility == Visibility.Visible && CreatePeerForElement(bar) is { } peer)
            {
                list.Add(peer);
            }
        }

        _children = list;
        return list;
    }

    internal HexCellAutomationPeer Cell(int row, int column)
    {
        if (!_cells.TryGetValue((row, column), out HexCellAutomationPeer? cell))
        {
            cell = new HexCellAutomationPeer(this, row, column);
            _cells[(row, column)] = cell;
        }

        return cell;
    }

    internal AutomationPeer? Sibling(HexCellAutomationPeer cell, int delta)
    {
        List<AutomationPeer> children = Children();
        int index = children.IndexOf(cell);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < children.Count ? children[target] : null;
    }

    private int ColumnAtPoint(double x)
    {
        int b = owner.BytesPerRowShown;
        if (x < owner.ContentLeft - owner.CellWidth)
        {
            return 0;
        }

        for (int c = 0; c < b; c++)
        {
            if (CellRect(0, c + 1).Right > x)
            {
                return c + 1;
            }
        }

        for (int c = 0; c < b; c++)
        {
            if (CellRect(0, b + 1 + c).Right > x)
            {
                return b + 1 + c;
            }
        }

        return -1;
    }

    /// <summary>セルの矩形 (Surface の座標)。列 0 はオフセット列、1〜b は Hex 列、b+1〜2b はテキスト列。</summary>
    internal Rect CellRect(int row, int column)
    {
        int b = owner.BytesPerRowShown;
        int digits = owner.OffsetDigits;
        double top = owner.RowTop(row);
        double cw = owner.CellWidth;
        HexView.RowColumns columns = owner.Columns;
        if (column == 0)
        {
            return new Rect(owner.CharacterLeft(0), top, digits * cw, owner.RowHeight);
        }

        if (column <= b)
        {
            return new Rect(owner.CharacterLeft(owner.OffsetChars + columns.HexIndex(column - 1)), top, 2 * cw, owner.RowHeight);
        }

        return new Rect(owner.CharacterLeft(owner.OffsetChars + columns.TextIndex(column - b - 1)), top, cw, owner.RowHeight);
    }

    // ---- Value パターン ----

    public bool IsReadOnly => owner.Editor is not { } e || e.ReadOnly || e.Document.IsEditLocked;

    public string Value => owner.CursorSummary();

    public void SetValue(string value) => owner.SetValueFromAutomation(value);

    // ---- Grid パターン ----

    public int RowCount => owner.VisibleRowCount;

    public int ColumnCount => 1 + 2 * owner.BytesPerRowShown;

    public IRawElementProviderSimple? GetItem(int row, int column) =>
        row >= 0 && row < RowCount && column >= 0 && column < ColumnCount ? ProviderFromPeer(Cell(row, column)) : null;

    // ---- Text パターン ----

    public ITextRangeProvider DocumentRange
    {
        get
        {
            HexTextSnapshot text = HexTextSnapshot.Capture(owner);
            return new HexTextRange(this, text, 0, text.Text.Length);
        }
    }

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        HexTextSnapshot text = HexTextSnapshot.Capture(owner);
        (int start, int end) = text.SelectionPositions(owner);
        return [new HexTextRange(this, text, start, end)];
    }

    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        HexTextSnapshot text = HexTextSnapshot.Capture(owner);
        if (FromProvider(childElement) is HexCellAutomationPeer cell && cell.Row < text.LineCount)
        {
            int b = owner.BytesPerRowShown;
            int digits = owner.OffsetDigits;
            HexView.RowColumns columns = owner.Columns;
            (int col, int length) = cell.Column == 0 ? (0, digits)
                : cell.Column <= b ? (owner.OffsetChars + columns.HexIndex(cell.Column - 1), 2)
                : (owner.OffsetChars + columns.TextIndex(cell.Column - b - 1), 1);
            int start = Math.Min(text.LineStart(cell.Row) + col, text.LineEnd(cell.Row));
            int end = Math.Min(start + length, text.LineEnd(cell.Row));
            return new HexTextRange(this, text, start, end);
        }

        return new HexTextRange(this, text, 0, 0);
    }

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        HexTextSnapshot text = HexTextSnapshot.Capture(owner);
        Point local = owner.FromScreen(screenLocation);
        int line = Math.Clamp((int)Math.Floor((local.Y - owner.RowTop(0)) / Math.Max(1, owner.RowHeight)), 0, Math.Max(0, text.LineCount - 1));
        int digits = owner.OffsetDigits;
        double cw = Math.Max(1, owner.CellWidth);
        int col = local.X < owner.ContentLeft
            ? (int)Math.Floor((local.X - owner.CharacterLeft(0)) / cw)
            : owner.OffsetChars + (int)Math.Floor((local.X - owner.CharacterLeft(owner.OffsetChars)) / cw);
        int pos = text.LineCount == 0 ? 0 : Math.Clamp(text.LineStart(line) + Math.Max(0, col), text.LineStart(line), text.LineEnd(line));
        return new HexTextRange(this, text, pos, pos);
    }
}

/// <summary>Grid パターンの 1 つのセル (UI テストの描画モデル: 名前が表示文字、ItemStatus が色・模様・層)。</summary>
public sealed partial class HexCellAutomationPeer(HexViewAutomationPeer grid, int row, int column) : AutomationPeer, IGridItemProvider
{
    internal int Row => row;

    internal int Column => column;

    int IGridItemProvider.Column => column;

    public int ColumnSpan => 1;

    int IGridItemProvider.Row => row;

    public int RowSpan => 1;

    public IRawElementProviderSimple ContainingGrid => grid.Provider(grid);

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.GridItem ? this : null;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetClassNameCore() => "HexViewCell";

    protected override string GetAutomationIdCore() => $"HexViewCell_{row}_{column}";

    protected override string GetNameCore()
    {
        HexView view = grid.View;
        HexView.RowVisual? r = view.RowAt(row);
        if (r is null)
        {
            return string.Empty;
        }

        int b = view.BytesPerRowShown;
        if (column == 0)
        {
            return r.OffsetText;
        }

        return column <= b ? r.HexCellText(column - 1).Trim() : r.TextCellText(column - b - 1);
    }

    protected override string GetItemStatusCore()
    {
        HexView view = grid.View;
        int b = view.BytesPerRowShown;
        if (column == 0)
        {
            return view.RowAt(row) is { } r ? "offset=" + view.FormatOffset(r.OffsetRowStart) : string.Empty;
        }

        return column <= b ? view.CellSummary(row, column - 1, hex: true) : view.CellSummary(row, column - b - 1, hex: false);
    }

    protected override Rect GetBoundingRectangleCore() => grid.View.ToScreen(grid.View.SurfaceElement, grid.CellRect(row, column));

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => false;

    protected override bool IsOffscreenCore() => grid.View.RowAt(row) is null;

    protected override bool IsEnabledCore() => true;

    protected override AutomationPeer GetLabeledByCore() => null!;

    protected override object? NavigateCore(AutomationNavigationDirection direction) => direction switch
    {
        AutomationNavigationDirection.Parent => grid,
        AutomationNavigationDirection.NextSibling => grid.Sibling(this, 1),
        AutomationNavigationDirection.PreviousSibling => grid.Sibling(this, -1),
        _ => null,
    };

    protected override IList<AutomationPeer> GetChildrenCore() => [];
}

/// <summary>Text パターンに渡す、見えている行の文字列。</summary>
internal sealed class HexTextSnapshot
{
    private readonly int[] _lineStarts;

    private HexTextSnapshot(string text, int[] lineStarts)
    {
        Text = text;
        _lineStarts = lineStarts;
    }

    public string Text { get; }

    public int LineCount => _lineStarts.Length;

    public static HexTextSnapshot Capture(HexView view)
    {
        int rows = view.VisibleRowCount;
        var sb = new System.Text.StringBuilder();
        int[] starts = new int[rows];
        for (int r = 0; r < rows; r++)
        {
            if (r > 0)
            {
                sb.Append('\n');
            }

            starts[r] = sb.Length;
            sb.Append(view.RowLine(r));
        }

        return new HexTextSnapshot(sb.ToString(), starts);
    }

    public int LineStart(int line) => line < 0 ? 0 : line >= _lineStarts.Length ? Text.Length : _lineStarts[line];

    /// <summary>行の末尾 (改行の手前)。</summary>
    public int LineEnd(int line) => line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : Text.Length;

    public int LineOf(int position)
    {
        int index = Array.BinarySearch(_lineStarts, position);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    /// <summary>カーソル (選択がなければ長さ 0) と選択範囲の位置 (操作中の列の文字で表す)。</summary>
    public (int Start, int End) SelectionPositions(HexView view)
    {
        if (view.Editor is not { } e || LineCount == 0)
        {
            return (0, 0);
        }

        long first = view.RowStartAt(0);
        long b = view.BytesPerRowShown;
        int width = e.ActiveColumn == ActiveColumn.Hex ? 2 : 1;
        int Position(long offset, bool end)
        {
            long line = (offset - first) / b;
            if (offset < first)
            {
                return 0;
            }

            if (line >= LineCount)
            {
                return Text.Length;
            }

            int pos = LineStart((int)line) + view.CharacterIndexOf(offset, e.ActiveColumn, !end && e.LowNibble && !e.HasSelection);
            return Math.Min(end ? pos + width : pos, LineEnd((int)line));
        }

        if (!e.HasSelection)
        {
            int caret = Position(e.Cursor, end: false);
            return (caret, caret);
        }

        return (Position(e.SelectionStart, end: false), Position(e.SelectionStart + e.SelectionLength - 1, end: true));
    }
}

/// <summary>Text パターンの範囲。見えている行の文字列の上の [Start, End)。</summary>
internal sealed partial class HexTextRange(HexViewAutomationPeer peer, HexTextSnapshot text, int start, int end) : ITextRangeProvider
{
    private const int FontNameAttributeId = 40005;
    private const int FontSizeAttributeId = 40006;
    private const int IsReadOnlyAttributeId = 40015;

    private int _start = Math.Clamp(Math.Min(start, end), 0, text.Text.Length);
    private int _end = Math.Clamp(Math.Max(start, end), 0, text.Text.Length);

    private string Text => text.Text;

    public ITextRangeProvider Clone() => new HexTextRange(peer, text, _start, _end);

    public bool Compare(ITextRangeProvider textRangeProvider) =>
        textRangeProvider is HexTextRange other && other._start == _start && other._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = (HexTextRange)textRangeProvider;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return a.CompareTo(b);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        _start = IsBoundary(_start, unit) ? _start : Previous(_start, unit);
        _end = Next(_start, unit);
    }

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string search, bool backward, bool ignoreCase)
    {
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string within = Text[_start.._end];
        int index = backward ? within.LastIndexOf(search, comparison) : within.IndexOf(search, comparison);
        return index < 0 ? null : new HexTextRange(peer, text, _start + index, _start + index + search.Length);
    }

    public object? GetAttributeValue(int attributeId) => attributeId switch
    {
        FontNameAttributeId => peer.View.HexFontFamily,
        FontSizeAttributeId => peer.View.EffectiveFontSize * 0.75,
        IsReadOnlyAttributeId => true,
        _ => null,
    };

    public void GetBoundingRectangles(out double[] returnValue)
    {
        HexView view = peer.View;
        var rects = new List<double>();
        int digits = view.OffsetDigits;
        int firstLine = text.LineOf(_start);
        int lastLine = _end == _start ? firstLine : text.LineOf(Math.Max(_start, _end - 1));
        for (int line = firstLine; line <= lastLine && line < text.LineCount; line++)
        {
            int from = Math.Max(_start, text.LineStart(line)) - text.LineStart(line);
            int to = Math.Min(_end, text.LineEnd(line)) - text.LineStart(line);
            double top = view.RowTop(line);

            // オフセット列と内容の境目で矩形を分ける (内容は横スクロールするため)。
            foreach ((int a, int z) in (ReadOnlySpan<(int, int)>)[(from, Math.Min(to, view.OffsetChars)), (Math.Max(from, view.OffsetChars), to)])
            {
                if (z < a || (z == a && _start != _end))
                {
                    continue;
                }

                double left = view.CharacterLeft(a);
                double width = z == a ? 1 : (z - a) * view.CellWidth;
                Rect screen = view.ToScreen(view.SurfaceElement, new Rect(left, top, width, view.RowHeight));
                rects.AddRange([screen.X, screen.Y, screen.Width, screen.Height]);
                if (_start == _end)
                {
                    break;
                }
            }
        }

        returnValue = [.. rects];
    }

    public IRawElementProviderSimple GetEnclosingElement() => peer.Provider(peer);

    public string GetText(int maxLength)
    {
        string s = Text[_start.._end];
        return maxLength >= 0 && s.Length > maxLength ? s[..maxLength] : s;
    }

    public int Move(TextUnit unit, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        bool degenerate = _start == _end;
        int pos = _start;
        int moved = 0;
        for (int i = 0; i < Math.Abs(count); i++)
        {
            int next = count > 0 ? Next(pos, unit) : Previous(pos, unit);
            if (next == pos || (count > 0 && next >= Text.Length && !degenerate))
            {
                break;
            }

            pos = next;
            moved++;
        }

        _start = pos;
        _end = degenerate ? pos : Next(pos, unit);
        return count > 0 ? moved : -moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = (HexTextRange)textRangeProvider;
        int value = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        SetEndpoint(endpoint, value);
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = 0;
        for (int i = 0; i < Math.Abs(count); i++)
        {
            int next = count > 0 ? Next(pos, unit) : Previous(pos, unit);
            if (next == pos)
            {
                break;
            }

            pos = next;
            moved++;
        }

        SetEndpoint(endpoint, pos);
        return count > 0 ? moved : -moved;
    }

    public void AddToSelection() => throw new InvalidOperationException();

    public void RemoveFromSelection() => throw new InvalidOperationException();

    public void ScrollIntoView(bool alignToTop)
    {
        // 見えている行だけを返しているため、範囲は常に表示されている。
    }

    /// <summary>範囲を選ぶ。長さ 0 ならその位置にカーソルを置く。</summary>
    public void Select()
    {
        HexView view = peer.View;
        if (view.Editor is not { } editor || text.LineCount == 0)
        {
            return;
        }

        (long first, ActiveColumn column) = At(_start);
        if (_start == _end)
        {
            editor.Click(first, column, false, false);
            return;
        }

        (long last, _) = At(Math.Max(_start, _end - 1));
        long from = Math.Min(first, last);
        long length = Math.Max(first, last) - from + 1;
        editor.Click(from, column, false, false);
        editor.Select(from, Math.Min(length, editor.Layout.Length - from));
    }

    public IRawElementProviderSimple[] GetChildren() => [];

    private (long Offset, ActiveColumn Column) At(int position)
    {
        int line = text.LineOf(position);
        return peer.View.OffsetAtCharacter(line, position - text.LineStart(line));
    }

    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
    {
        value = Math.Clamp(value, 0, Text.Length);
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = value;
            if (_end < _start)
            {
                _end = _start;
            }
        }
        else
        {
            _end = value;
            if (_start > _end)
            {
                _start = _end;
            }
        }
    }

    private bool IsBoundary(int pos, TextUnit unit)
    {
        if (pos <= 0 || pos >= Text.Length)
        {
            return true;
        }

        return unit switch
        {
            TextUnit.Character => true,
            TextUnit.Format or TextUnit.Word => Text[pos - 1] == '\n' || (char.IsWhiteSpace(Text[pos - 1]) && !char.IsWhiteSpace(Text[pos])),
            TextUnit.Line or TextUnit.Paragraph => Text[pos - 1] == '\n',
            _ => false,
        };
    }

    private int Next(int pos, TextUnit unit)
    {
        for (int p = pos + 1; p <= Text.Length; p++)
        {
            if (IsBoundary(p, unit))
            {
                return p;
            }
        }

        return Text.Length;
    }

    private int Previous(int pos, TextUnit unit)
    {
        for (int p = pos - 1; p >= 0; p--)
        {
            if (IsBoundary(p, unit))
            {
                return p;
            }
        }

        return 0;
    }
}
