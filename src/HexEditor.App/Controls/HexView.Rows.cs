using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビューの行の要素 (VIEW-01 の仕様 2・8、VIEW-04 の仕様 3)。1 行を 1 つの TextBlock (色の違う区間ごとの Run) で描き、背景は
/// TextHighlighter、下線・枠・模様は図形で重ねる。重ねる順は VIEW-17 の仕様 5 の表に従う。
/// </summary>
public sealed partial class HexView
{
    /// <summary>セルの描き方。</summary>
    internal enum CellKind
    {
        Normal,
        Modified,
        Loading,
        Unreadable,
        Empty,
    }

    /// <summary>変更されたバイトの種類 (VIEW-15)。下線の形が違う (実線・2 本線・点線)。</summary>
    internal enum ChangeMark : byte
    {
        None,
        Overwritten,
        Inserted,
        Saved,
    }

    /// <summary>読み込み中のセルの描き方 (VIEW-03 の仕様 2・3)。</summary>
    internal enum CellMode
    {
        Normal,

        /// <summary>猶予中: セルを空白にする。</summary>
        Blank,

        /// <summary>猶予を過ぎた: `··` の仮表示。</summary>
        Placeholder,
    }

    /// <summary>1 行の文字列の中での各列の位置 (内容の領域の左端からの文字数。<see cref="RowFormat"/> と同じ)。</summary>
    internal readonly record struct RowColumns(RowFormat Format)
    {
        public RowColumns(int bytesPerRow)
            : this(new RowFormat(bytesPerRow, 1, bytesPerRow >= 16))
        {
        }

        public int BytesPerRow => Format.BytesPerRow;

        public bool ShowHex => Format.ShowHex;

        public bool ShowText => Format.ShowText;

        public int HexIndex(int c) => Format.HexIndex(c);

        public int TextIndex(int c) => Format.TextIndex(c);

        public int LineLength => Format.LineLength;
    }

    /// <summary>表示設定のうち、行の描き方に関わるもの (変われば全行を作り直す)。</summary>
    internal readonly record struct RowStyle(bool Lowercase, bool DimZeros, bool AlternateColumns, bool AlternateText, bool HighlightModified,
        bool ShowContinuation, bool HighContrast, NonPrintableStyle NonPrintable = NonPrintableStyle.Dot);

    /// <summary>1 フレームの中で全行に共通の条件 (変われば全行を作り直す)。</summary>
    /// <remarks>Proportional: 等幅でないフォント。文字を 1 文字ずつセルの中央に描く (UI-29 の仕様 2)。</remarks>
    internal readonly record struct RowFrame(RowColumns Columns, ActiveColumn Active, int PaletteVersion, TextEncoding Encoding, RowStyle Style,
        bool Proportional = false);

    /// <summary>描いたセルの情報 (UI オートメーション・テストの描画モデルで返す)。</summary>
    internal readonly record struct CellPaint(string Text, Brush Foreground, Brush Background, string Layer, ChangeMark Underline);

    /// <summary>
    /// 1 行分の要素。前回描いた内容を覚えておき、変わったときだけ Run を作り直す (VIEW-04 の仕様 3・5)。
    /// </summary>
    internal sealed class RowVisual
    {
        private byte[] _bytes = [];
        private ByteState[] _states = [];
        private ChangeMark[] _marks = [];
        private bool[] _matched = [];
        private bool[] _focus = [];

        // 色付けルール (VIEW-17 の層 10、INSP-33) の文字色。null は指定なし。
        private Brush?[] _ruleHex = [];
        private Brush?[] _ruleText = [];

        // そのバイトの後ろ (右) に削除によって詰まった境界がある (VIEW-15 の仕様 5)。
        private bool[] _deleted = [];
        private TextCell[] _text = [];
        private int _count = -1;
        private int _lead;
        private CellMode _mode;
        private long _selFrom;
        private long _selTo;
        private bool _currentRow;
        private RowFrame _frame;
        private readonly List<Path> _hatches = [];
        private readonly List<Rectangle> _bars = [];
        private readonly List<Line> _lines = [];
        private readonly List<TextBlock> _glyphs = [];

        // 別に描いた文字のセル (_glyphs と同じ並び): Hex 列か、セルの番号。選択などの層の文字の色を後から合わせる。
        private readonly List<(bool Hex, int Cell)> _glyphCells = [];

        // 行の TextBlock の Run (Content.Inlines と同じ並び)。作り直さずに文字と色を書き換えて使い回す (Run を毎回作ると、XAML の
        // オブジェクトの追跡のために GC が増え、1 行のバイト数が多いとフレームが遅れる。VIEW-04 の仕様 3)。
        private readonly List<RunSlot> _runs = [];
        private RunBuilder? _builder;
        private readonly Rectangle _rowBack;
        private int _barsUsed;
        private int _linesUsed;
        private int _glyphsUsed;

        public RowVisual(FontFamily font, double fontSize, double rowHeight, int spacing)
        {
            Offset = CreateText();
            Content = CreateText();
            _rowBack = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(_rowBack, AccessibilityView.Raw);
            Container = new Canvas();
            Container.Children.Add(_rowBack);
            Container.Children.Add(Content);
            ApplyFont(font, fontSize, rowHeight, spacing);
        }

        public TextBlock Offset { get; }

        public TextBlock Content { get; }

        /// <summary>内容の TextBlock と、背景・下線・模様の図形を入れる。</summary>
        public Canvas Container { get; }

        public long OffsetRowStart { get; private set; } = long.MinValue;

        public int OffsetDigits { get; private set; }

        /// <summary>オフセット列の文字列を作ったときの書式 (変われば作り直す)。</summary>
        public OffsetFormat? OffsetFormatUsed { get; private set; }

        public long ContentRowStart { get; private set; } = long.MinValue;

        public bool HasContent => _count >= 0;

        /// <summary>画面と同じ書式の行の文字列 (オフセット列を除く。UI オートメーションの Text パターンに渡す。VIEW-41 の仕様 6)。</summary>
        public string ContentText => _contentText ??= _builder?.Line ?? string.Empty;

        // 行の文字列は読まれたときに作る (描画のたびに 1 行分の文字列を作らない。1 行 4,096 バイトでは 1 行 32 KB になる)。
        private string? _contentText;

        public string OffsetText => Offset.Text;

        /// <summary>行の中で、データのある最後のセルの次の位置。</summary>
        public int Count => Math.Max(0, _count);

        /// <summary>行の先頭の空白のセルの数 (行の先頭のずれ。VIEW-20 の仕様 5)。</summary>
        public int Lead => _lead;

        public ReadOnlySpan<byte> Bytes => _bytes;

        public ReadOnlySpan<ByteState> States => _states;

        public ReadOnlySpan<ChangeMark> Marks => _marks;

        /// <summary>互換: 変更されたバイトか。</summary>
        public bool ModifiedAt(int c) => c < _marks.Length && _marks[c] != ChangeMark.None;

        public bool IsBlank => _mode == CellMode.Blank;

        public bool IsCurrentRow => _currentRow;

        public double Top { get; private set; }

        /// <summary>Hex 列のセルの描画の情報 (テスト用)。</summary>
        public CellPaint[] HexPaint { get; private set; } = [];

        /// <summary>テキスト列のセルの描画の情報 (テスト用)。</summary>
        public CellPaint[] TextPaint { get; private set; } = [];

        /// <summary>テキスト列の文字の描画範囲 (セルごと。文字を描いていないセルは null。VIEW-22 の確認用)。</summary>
        public (string Glyph, double Left, double Width, double ScaleX)?[] Glyphs { get; private set; } = [];

        /// <summary>行に引いた線 (現在行の上下の線、列の交互色の代わりの縦の点線。ハイコントラスト)。</summary>
        public List<(string Kind, double X1, double Y1, double X2, double Y2)> Lines { get; } = [];

        public bool Visible
        {
            get => Offset.Visibility == Visibility.Visible;
            set
            {
                Visibility v = value ? Visibility.Visible : Visibility.Collapsed;
                if (Container.Visibility != v)
                {
                    Container.Visibility = v;
                }

                if (Offset.Visibility != v)
                {
                    Offset.Visibility = v;
                }
            }
        }

        /// <summary>オフセット列を表示するか (VIEW-16)。行の要素の表示とは別に切り替える。</summary>
        public bool OffsetShown
        {
            set => Offset.Opacity = value ? 1 : 0;
        }

        public void ApplyFont(FontFamily font, double fontSize, double rowHeight, int spacing)
        {
            foreach (TextBlock t in (TextBlock[])[Offset, Content])
            {
                t.FontFamily = font;
                t.FontSize = fontSize;
                t.LineHeight = rowHeight;
                t.CharacterSpacing = spacing;
            }

            foreach (TextBlock g in _glyphs)
            {
                g.FontFamily = font;
                g.FontSize = fontSize;
                g.LineHeight = rowHeight;
            }
        }

        public void Invalidate()
        {
            _count = -1;
            OffsetRowStart = long.MinValue;
            OffsetFormatUsed = null;
            ContentRowStart = long.MinValue;
        }

        public void SetTop(double y)
        {
            if (Top != y || Canvas.GetTop(Offset) != y)
            {
                Top = y;
                Canvas.SetTop(Offset, y);
                Canvas.SetTop(Container, y);
            }
        }

        public void SetOffset(long rowStart, OffsetFormat format, Palette palette)
        {
            OffsetRowStart = rowStart;
            OffsetFormatUsed = format;
            OffsetDigits = format.ColumnWidth;

            // 行の先頭のずれで最初の行が負のオフセットから始まる場合は、アドレスがあればそのアドレス、なければ 0 を示す (VIEW-20)。
            long shown = rowStart < 0 && !format.UsesBaseAddress && format.ReferencePoint is null ? 0 : rowStart;
            Offset.Text = format.Column(shown);
            Offset.Foreground = palette.OffsetText;
        }

        /// <summary>内容を更新する。前回と同じなら何もしない。作り直したら true。</summary>
        public bool Update(in RowFrame frame, long rowStart, int lead, int count, ReadOnlySpan<byte> bytes, ReadOnlySpan<ByteState> states,
            ReadOnlySpan<ChangeMark> marks, ReadOnlySpan<bool> matched, ReadOnlySpan<bool> focus, ReadOnlySpan<bool> deleted, ReadOnlySpan<TextCell> text,
            CellMode mode,
            long selStart, long selEnd, bool currentRow, Palette palette, double cellWidth, double rowHeight, Func<string, double> measure,
            ReadOnlySpan<Brush?> ruleHex = default, ReadOnlySpan<Brush?> ruleText = default)
        {
            long selFrom = Math.Max(selStart, rowStart + lead) - rowStart;
            long selTo = Math.Min(selEnd, rowStart + count) - rowStart;
            if (selFrom >= selTo)
            {
                selFrom = selTo = 0;
            }

            if (_count == count && _lead == lead && ContentRowStart == rowStart && _mode == mode && _frame == frame && _selFrom == selFrom
                && _selTo == selTo && _currentRow == currentRow
                && bytes[..count].SequenceEqual(_bytes.AsSpan(0, count)) && states[..count].SequenceEqual(_states.AsSpan(0, count))
                && marks[..count].SequenceEqual(_marks.AsSpan(0, count)) && matched[..count].SequenceEqual(_matched.AsSpan(0, count))
                && focus[..count].SequenceEqual(_focus.AsSpan(0, count)) && deleted[..count].SequenceEqual(_deleted.AsSpan(0, count))
                && SameRule(ruleHex, _ruleHex, count) && SameRule(ruleText, _ruleText, count)
                && SameText(text, count))
            {
                return false;
            }

            ContentRowStart = rowStart;
            _count = count;
            _lead = lead;
            _mode = mode;
            _frame = frame;
            _selFrom = selFrom;
            _selTo = selTo;
            _currentRow = currentRow;
            int b = frame.Columns.BytesPerRow;
            if (_bytes.Length < b)
            {
                _bytes = new byte[b];
                _states = new ByteState[b];
                _marks = new ChangeMark[b];
                _matched = new bool[b];
                _focus = new bool[b];
                _deleted = new bool[b];
                _text = new TextCell[b];
            }

            if (_ruleHex.Length < b)
            {
                _ruleHex = new Brush?[b];
                _ruleText = new Brush?[b];
            }

            CopyRule(ruleHex, _ruleHex, count);
            CopyRule(ruleText, _ruleText, count);

            bytes[..count].CopyTo(_bytes);
            states[..count].CopyTo(_states);
            marks[..count].CopyTo(_marks);
            matched[..count].CopyTo(_matched);
            focus[..count].CopyTo(_focus);
            deleted[..count].CopyTo(_deleted);
            _text.AsSpan().Fill(TextCell.None);
            text[..Math.Min(count, text.Length)].CopyTo(_text);

            _barsUsed = 0;
            _linesUsed = 0;
            _glyphsUsed = 0;
            Lines.Clear();
            Fill(frame, palette, cellWidth, rowHeight, measure);
            Highlight(frame, palette);
            ColorGlyphs();
            DrawRowBackground(frame, palette, cellWidth, rowHeight);
            DrawUnderlines(frame, palette, cellWidth, rowHeight);
            DrawMatchBorders(frame, palette, cellWidth, rowHeight);
            DrawDeletions(frame, palette, cellWidth, rowHeight);
            DrawAlternateLines(frame, palette, cellWidth, rowHeight);
            UpdateHatches(frame.Columns, palette, cellWidth, rowHeight);
            HideUnused();
            return true;
        }

        private static bool SameRule(ReadOnlySpan<Brush?> given, Brush?[] kept, int count)
        {
            for (int c = 0; c < count; c++)
            {
                Brush? a = c < given.Length ? given[c] : null;
                if (!ReferenceEquals(a, c < kept.Length ? kept[c] : null))
                {
                    return false;
                }
            }

            return true;
        }

        private static void CopyRule(ReadOnlySpan<Brush?> given, Brush?[] kept, int count)
        {
            Array.Clear(kept);
            given[..Math.Min(count, given.Length)].CopyTo(kept);
        }

        private bool SameText(ReadOnlySpan<TextCell> text, int count)
        {
            for (int c = 0; c < count; c++)
            {
                TextCell expected = c < text.Length ? text[c] : TextCell.None;
                if (_text[c] != expected)
                {
                    return false;
                }
            }

            return true;
        }

        public CellKind KindAt(int c)
        {
            if (c >= Count || c < _lead)
            {
                return CellKind.Empty;
            }

            return _states[c] switch
            {
                ByteState.Loading => CellKind.Loading,
                ByteState.Unreadable => CellKind.Unreadable,
                _ => _marks[c] != ChangeMark.None ? CellKind.Modified : CellKind.Normal,
            };
        }

        /// <summary>Hex 列のセルの表示 (VIEW-03 の仮表示を含む。VIEW-12 の大文字・小文字)。</summary>
        public string HexCellText(int c) => KindAt(c) switch
        {
            CellKind.Empty => "  ",
            CellKind.Loading => _mode == CellMode.Blank ? "  " : "··",
            CellKind.Unreadable => "??",
            _ => (_frame.Style.Lowercase ? HexStringsLower : HexStrings)[_bytes[c]],
        };

        /// <summary>テキスト列のセルの表示 (文字の範囲の続きのセルは空白。描く文字は <see cref="Glyphs"/>)。</summary>
        public string TextCellText(int c) => KindAt(c) switch
        {
            CellKind.Empty => string.Empty,
            CellKind.Loading or CellKind.Unreadable => " ",
            _ => _text[c].Kind switch
            {
                TextCellKind.Char => _text[c].Text,
                TextCellKind.Continuation => _frame.Style.ShowContinuation ? "·" : " ",
                TextCellKind.Empty => " ",
                TextCellKind.NonPrintable => _text[c].Text,
                TextCellKind.Invalid when _text[c].Text.Length > 0 => _text[c].Text,
                _ => ".",
            },
        };

        /// <summary>テキスト列のセルの解読結果 (VIEW-22)。</summary>
        public TextCell TextAt(int c) => c < _text.Length ? _text[c] : TextCell.None;

        private static TextBlock CreateText()
        {
            var t = new TextBlock
            {
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap,

                // 文字サイズの設定は MeasureCell で大きさに掛けてある (VIEW-41 の仕様 4)。二重に掛けない。
                IsTextScaleFactorEnabled = false,
                TextLineBounds = TextLineBounds.Full,

                // 合字は常に無効 (UI-29 の仕様 6)。カラー絵文字は使わない (VIEW-22 の仕様 7)。
                IsColorFontEnabled = false,
            };
            Typography.SetStandardLigatures(t, false);
            Typography.SetContextualLigatures(t, false);
            Typography.SetDiscretionaryLigatures(t, false);

            // 行の文字列はビューの UI オートメーション (Text・Grid パターン) で返すため、個々の TextBlock は木に出さない。
            AutomationProperties.SetAccessibilityView(t, AccessibilityView.Raw);
            return t;
        }

        // ---- 文字 ----

        private Brush HexForeground(int c, Palette palette)
        {
            CellKind kind = KindAt(c);
            if (kind is CellKind.Empty or CellKind.Loading)
            {
                return palette.Dim;
            }

            if (kind == CellKind.Unreadable)
            {
                return palette.Text;
            }

            if (_frame.Style.HighlightModified && _marks[c] != ChangeMark.None)
            {
                return MarkBrush(_marks[c], palette);
            }

            // 色付けルール (層 10) は、変更されたバイト (層 6) より奥、ゼロのグレー表示 (層 13) より手前。
            if (_ruleHex[c] is { } rule)
            {
                return rule;
            }

            return _frame.Style.DimZeros && _bytes[c] == 0 ? palette.Zero : palette.HexText;
        }

        private Brush TextForeground(int c, Palette palette)
        {
            CellKind kind = KindAt(c);
            if (kind is CellKind.Empty or CellKind.Loading)
            {
                return palette.Dim;
            }

            if (kind == CellKind.Unreadable)
            {
                return palette.Text;
            }

            if (_frame.Style.HighlightModified && _marks[c] != ChangeMark.None)
            {
                return MarkBrush(_marks[c], palette);
            }

            if (_ruleText[c] is { } rule)
            {
                return rule;
            }

            TextCell cell = _text[c];
            return cell.Kind switch
            {
                TextCellKind.Invalid => palette.Invalid,
                TextCellKind.Continuation => palette.Dim,
                _ when _frame.Style.DimZeros && _bytes[c] == 0 => palette.Zero,
                TextCellKind.NonPrintable => palette.NonPrintable,
                _ => palette.TextText,
            };
        }

        /// <summary>変更されたバイトの文字色 (ハイコントラストでは色を変えない。VIEW-15 の仕様 6)。</summary>
        private static Brush MarkBrush(ChangeMark mark, Palette palette) => palette.HighContrast ? palette.Text : mark switch
        {
            ChangeMark.Inserted => palette.Inserted,
            ChangeMark.Saved => palette.SavedChange,
            _ => palette.Modified,
        };

        /// <summary>1 行の文字列を、色の違う区間ごとの Run に分けて作る。全角・結合文字などは別の TextBlock に描く。</summary>
        private void Fill(in RowFrame frame, Palette palette, double cellWidth, double rowHeight, Func<string, double> measure)
        {
            RunBuilder builder = _builder ??= new RunBuilder(Content, _runs);
            builder.Reset();
            RowColumns columns = frame.Columns;
            int b = columns.BytesPerRow;
            int count = Count;
            // 配列は 1 行のバイト数が変わるまで使い回す (1 行 4,096 バイトでは大きなオブジェクトになり、毎回作ると GC でフレームが遅れる)。
            if (HexPaint.Length != b)
            {
                HexPaint = new CellPaint[b];
                TextPaint = new CellPaint[b];
                Glyphs = new (string, double, double, double)?[b];
            }
            else
            {
                Array.Clear(HexPaint);
                Array.Clear(TextPaint);
                Array.Clear(Glyphs);
            }

            CellPaint[] hexPaint = HexPaint;
            CellPaint[] textPaint = TextPaint;
            (string, double, double, double)?[] glyphs = Glyphs;

            // Hex 列
            if (columns.ShowHex)
            {
                for (int c = 0; c < b; c++)
                {
                    string cell = HexCellText(c);
                    Brush fore = HexForeground(c, palette);
                    if (frame.Proportional)
                    {
                        // 等幅でないフォント: 1 文字ずつセルの中央に描き、行には空白を入れる (UI-29 の仕様 2)。
                        for (int i = 0; i < cell.Length; i++)
                        {
                            if (cell[i] != ' ')
                            {
                                PlaceGlyph(cell[i].ToString(), (columns.HexIndex(c) + i) * cellWidth, cellWidth, fore, rowHeight, measure, hex: true, c);
                            }
                        }

                        builder.AppendMasked(cell, fore);
                    }
                    else
                    {
                        builder.Append(cell, fore);
                    }
                    hexPaint[c] = new CellPaint(cell, fore, palette.Background, "normal", _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None);
                    int gap = c + 1 < b ? columns.HexIndex(c + 1) - columns.HexIndex(c) - RowFormat.HexCellChars : 0;
                    if (gap > 0)
                    {
                        builder.Append(Spaces(gap), palette.Text);
                    }
                }
            }

            // テキスト列 (Hex 列との間は 2 文字。VIEW-01 の仕様 1)
            if (columns.ShowText)
            {
                if (columns.ShowHex)
                {
                    builder.Append(Spaces(RowFormat.ColumnGap), palette.Text);
                }

                for (int c = 0; c < count; c++)
                {
                    string cell = TextCellText(c);
                    Brush fore = TextForeground(c, palette);
                    TextCell decoded = _text[c];
                    // 制御文字の図記号 (VIEW-21 の仕様 7) も等幅フォントにないことがあるため、同じく別に描く。
                    if (KindAt(c) is CellKind.Normal or CellKind.Modified && decoded.Kind is TextCellKind.Char or TextCellKind.NonPrintable or TextCellKind.Invalid
                        && NeedsOverlay(decoded))
                    {
                        // 全角・結合文字などは別の TextBlock で、文字の範囲のセルに収めて描く (VIEW-22 の仕様 2・3・6)。
                        int cells = decoded.Wide ? 2 : 1;
                        cells = Math.Min(cells, Math.Max(1, b - c));
                        glyphs[c] = PlaceGlyph(decoded.Text, columns.TextIndex(c) * cellWidth, cells * cellWidth, fore, rowHeight, measure, hex: false, c);
                        builder.Append(" ", fore);
                    }
                    else if (frame.Proportional && cell.Length > 0 && cell != " ")
                    {
                        // 等幅でないフォント: セルの中央に描く (UI-29 の仕様 2)。
                        glyphs[c] = PlaceGlyph(cell, columns.TextIndex(c) * cellWidth, cellWidth, fore, rowHeight, measure, hex: false, c);
                        builder.AppendMasked(cell, fore);
                    }
                    else
                    {
                        builder.Append(cell, fore);
                        if (cell.Length > 0 && cell != " ")
                        {
                            glyphs[c] = (cell, columns.TextIndex(c) * cellWidth, cellWidth, 1);
                        }
                    }

                    textPaint[c] = new CellPaint(cell, fore, palette.Background, "normal", _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None);
                }
            }

            builder.Flush();
            _contentText = null;
        }

        private static readonly string[] SpaceStrings = [.. Enumerable.Range(0, 17).Select(n => new string(' ', n))];

        /// <summary>空白の文字列 (セルの間の空白。毎回作らない)。</summary>
        internal static string Spaces(int n) => n < SpaceStrings.Length ? SpaceStrings[n] : new string(' ', n);

        private ChangeMark MarkAt(int c) => KindAt(c) is CellKind.Modified ? _marks[c] : ChangeMark.None;

        /// <summary>行の TextBlock では幅がそろわない文字 (全角、結合文字、対応する字形が等幅フォントにない文字)。</summary>
        private static bool NeedsOverlay(TextCell cell) =>
            cell.Wide || cell.Text.Length != 1 || cell.Text[0] is >= '̀' and <= 'ͯ' || cell.Text[0] >= '԰';

        private (string, double, double, double) PlaceGlyph(string text, double left, double width, Brush fore, double rowHeight,
            Func<string, double> measure, bool hex, int cellIndex)
        {
            TextBlock glyph;
            if (_glyphsUsed < _glyphs.Count)
            {
                glyph = _glyphs[_glyphsUsed];
            }
            else
            {
                glyph = CreateText();
                glyph.FontFamily = Content.FontFamily;
                glyph.FontSize = Content.FontSize;
                glyph.LineHeight = Content.LineHeight;
                glyph.IsHitTestVisible = false;
                _glyphs.Add(glyph);
                _glyphCells.Add(default);
                Container.Children.Add(glyph);
            }

            _glyphCells[_glyphsUsed] = (hex, cellIndex);
            _glyphsUsed++;
            glyph.Visibility = Visibility.Visible;
            glyph.Text = text;
            glyph.Foreground = fore;
            double natural = measure(text);
            double scale = natural > width && natural > 0 ? width / natural : 1;

            // 文字の範囲に収まらない場合は横方向に縮小する (VIEW-22 の仕様 3)。収まる場合は範囲の中央に置く。
            glyph.Width = Math.Max(natural, width / scale);
            glyph.TextAlignment = TextAlignment.Center;
            if (scale < 1)
            {
                if (glyph.RenderTransform is ScaleTransform transform)
                {
                    transform.ScaleX = scale;
                }
                else
                {
                    glyph.RenderTransform = new ScaleTransform { ScaleX = scale };
                }
            }
            else if (glyph.RenderTransform is not null)
            {
                glyph.RenderTransform = null;
            }
            Canvas.SetLeft(glyph, left);
            Canvas.SetTop(glyph, 0);
            return (text, left, width, scale);
        }

        // ---- 背景 (VIEW-17 の層 2〜5・14・16) ----

        // 背景の TextHighlighter は作り直さずに使い回す (行を作り直すたびに作ると、XAML のオブジェクトの追跡のために GC が増え、
        // スクロール中のフレームが遅れる。VIEW-04 の仕様 3)。
        private readonly List<TextHighlighter> _highlighters = [];
        private int _highlightersUsed;
        private int _highlightersShown;

        /// <summary>使い回しの TextHighlighter を 1 つ取る (範囲は空)。</summary>
        private TextHighlighter TakeHighlighter(Brush background, Brush? foreground)
        {
            TextHighlighter h;
            if (_highlightersUsed < _highlighters.Count)
            {
                h = _highlighters[_highlightersUsed];
                h.Ranges.Clear();
            }
            else
            {
                h = new TextHighlighter();
                _highlighters.Add(h);
            }

            _highlightersUsed++;
            if (!ReferenceEquals(h.Background, background))
            {
                h.Background = background;
            }

            if (!ReferenceEquals(h.Foreground, foreground))
            {
                h.Foreground = foreground;
            }

            return h;
        }

        /// <summary>範囲を入れた TextHighlighter を行に付ける。範囲がなければ取ったものを戻す。</summary>
        private void ShowHighlighter(TextHighlighter h)
        {
            if (h.Ranges.Count == 0)
            {
                if (_highlightersUsed > 0 && ReferenceEquals(_highlighters[_highlightersUsed - 1], h))
                {
                    _highlightersUsed--;
                }

                return;
            }

            Content.TextHighlighters.Add(h);
            _highlightersShown++;
        }

        private void Highlight(in RowFrame frame, Palette palette)
        {
            if (_highlightersShown > 0)
            {
                Content.TextHighlighters.Clear();
            }

            _highlightersUsed = 0;
            _highlightersShown = 0;
            RowColumns columns = frame.Columns;
            int count = Count;
            bool hc = palette.HighContrast;

            // 層 16: 列の交互色。現在行 (層 14) では現在行の背景が隠す。ハイコントラストでは縦の点線にする (DrawAlternateLines)。
            if (frame.Style.AlternateColumns && !hc && !_currentRow)
            {
                TextHighlighter alt = TakeHighlighter(palette.Alternate, null);
                for (int c = 0; c < columns.BytesPerRow; c++)
                {
                    if (!columns.Format.IsGroupStart(c) || columns.Format.GroupOf(c) % 2 == 0)
                    {
                        continue;
                    }

                    int last = Math.Min(columns.BytesPerRow, c + Math.Max(1, columns.Format.GroupSize)) - 1;
                    if (columns.ShowHex)
                    {
                        alt.Ranges.Add(new TextRange { StartIndex = columns.HexIndex(c), Length = columns.HexIndex(last) + 2 - columns.HexIndex(c) });
                    }

                    if (frame.Style.AlternateText && columns.ShowText && c < count)
                    {
                        alt.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(c), Length = Math.Min(last, count - 1) - c + 1 });
                    }

                    for (int i = c; i <= last; i++)
                    {
                        HexPaint[i] = HexPaint[i] with { Background = palette.Alternate, Layer = "alternate" };
                        if (frame.Style.AlternateText && i < TextPaint.Length)
                        {
                            TextPaint[i] = TextPaint[i] with { Background = palette.Alternate, Layer = "alternate" };
                        }
                    }
                }

                ShowHighlighter(alt);
            }

            if (_currentRow && !hc)
            {
                for (int i = 0; i < HexPaint.Length; i++)
                {
                    HexPaint[i] = HexPaint[i] with { Background = palette.CurrentRow, Layer = "currentRow" };
                    TextPaint[i] = TextPaint[i] with { Background = palette.CurrentRow, Layer = "currentRow" };
                }
            }

            // 層 3: 注目している範囲 (INSP-18 など)。
            AddRanges(columns, _focus, palette.FocusRange, null, "focus");

            // 層 4・5: 検索の一致。
            AddRanges(columns, _matched, palette.Match, palette.MatchText, "match");

            // 層 2: 選択範囲。操作中でない列の選択は薄い色で塗る (EDIT-01 の画面)。
            if (_selFrom < _selTo)
            {
                int first = (int)_selFrom;
                int last = (int)_selTo - 1;
                bool hexActive = frame.Active == ActiveColumn.Hex;
                Brush hexBack = hexActive ? palette.Selection : palette.SelectionInactive;
                Brush hexFore = hexActive ? palette.SelectionText : palette.SelectionInactiveText;
                Brush textBack = hexActive ? palette.SelectionInactive : palette.Selection;
                Brush textFore = hexActive ? palette.SelectionInactiveText : palette.SelectionText;
                if (columns.ShowHex)
                {
                    TextHighlighter hex = TakeHighlighter(hexBack, hexFore);
                    int hexStart = columns.HexIndex(first);
                    hex.Ranges.Add(new TextRange { StartIndex = hexStart, Length = columns.HexIndex(last) + 2 - hexStart });
                    ShowHighlighter(hex);
                }

                if (columns.ShowText)
                {
                    TextHighlighter text = TakeHighlighter(textBack, textFore);
                    text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(first), Length = last - first + 1 });
                    ShowHighlighter(text);
                }

                for (int i = first; i <= last; i++)
                {
                    HexPaint[i] = HexPaint[i] with { Background = hexBack, Foreground = hexFore, Layer = "selection" };
                    TextPaint[i] = TextPaint[i] with { Background = textBack, Foreground = textFore, Layer = "selection" };
                }
            }
        }

        private void AddRanges(RowColumns columns, bool[] flags, Brush background, Brush? foreground, string layer)
        {
            int count = Count;
            int first = flags.AsSpan(0, count).IndexOf(true);
            if (first < 0)
            {
                // 範囲がない (ほとんどの行): TextHighlighter を取らない。
                return;
            }

            TextHighlighter hex = TakeHighlighter(background, foreground);
            TextHighlighter text = TakeHighlighter(background, foreground);
            for (int c = first; c < count; c++)
            {
                if (!flags[c])
                {
                    continue;
                }

                int end = c;
                while (end + 1 < count && flags[end + 1])
                {
                    end++;
                }

                if (columns.ShowHex)
                {
                    int hexStart = columns.HexIndex(c);
                    hex.Ranges.Add(new TextRange { StartIndex = hexStart, Length = columns.HexIndex(end) + 2 - hexStart });
                }

                if (columns.ShowText)
                {
                    text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(c), Length = end - c + 1 });
                }

                for (int i = c; i <= end; i++)
                {
                    HexPaint[i] = HexPaint[i] with { Background = background, Foreground = foreground ?? HexPaint[i].Foreground, Layer = layer };
                    TextPaint[i] = TextPaint[i] with { Background = background, Foreground = foreground ?? TextPaint[i].Foreground, Layer = layer };
                }

                c = end;
            }

            // 取った順の逆に戻す (空のものを使い回しの列に返すため)。
            bool showHex = hex.Ranges.Count > 0;
            ShowHighlighter(text);
            if (showHex)
            {
                Content.TextHighlighters.Add(hex);
                _highlightersShown++;
            }
            else
            {
                ShowHighlighter(hex);
            }
        }

        /// <summary>現在行 (VIEW-06 の仕様 1)。ハイコントラストでは背景を塗らず、行の上下に 1 px の線を引く。</summary>
        private void DrawRowBackground(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            double width = frame.Columns.LineLength * cellWidth;
            if (!_currentRow)
            {
                _rowBack.Visibility = Visibility.Collapsed;
                return;
            }

            if (palette.HighContrast)
            {
                _rowBack.Visibility = Visibility.Collapsed;
                PlaceBar(0, 0, width, 1, palette.CurrentRowLine);
                PlaceBar(0, rowHeight - 1, width, 1, palette.CurrentRowLine);
                Lines.Add(("currentRowTop", 0, 0, width, 0));
                Lines.Add(("currentRowBottom", 0, rowHeight - 1, width, rowHeight - 1));
                return;
            }

            _rowBack.Visibility = Visibility.Visible;
            _rowBack.Fill = palette.CurrentRow;
            _rowBack.Width = width;
            _rowBack.Height = rowHeight;
        }

        /// <summary>変更されたバイトの下線 (VIEW-15 の仕様 3〜8): 上書きは実線、挿入は 2 本線、保存済みの変更は点線。どの層が重なっても描く。</summary>
        private void DrawUnderlines(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            if (!frame.Style.HighlightModified)
            {
                return;
            }

            RowColumns columns = frame.Columns;
            int count = Count;
            for (int c = 0; c < count; c++)
            {
                ChangeMark mark = MarkAt(c);
                if (mark == ChangeMark.None)
                {
                    continue;
                }

                int end = c;
                while (end + 1 < count && MarkAt(end + 1) == mark)
                {
                    end++;
                }

                Brush brush = palette.HighContrast ? palette.Text : MarkBrush(mark, palette);
                if (columns.ShowHex)
                {
                    double left = columns.HexIndex(c) * cellWidth;
                    double right = (columns.HexIndex(end) + 2) * cellWidth;
                    Underline(mark, left, right, rowHeight, brush);
                }

                if (columns.ShowText)
                {
                    Underline(mark, columns.TextIndex(c) * cellWidth, columns.TextIndex(end + 1) * cellWidth, rowHeight, brush);
                }

                c = end;
            }
        }

        /// <summary>
        /// 別に描いた文字の色を、層 (選択範囲・検索の一致など) を重ねた後の文字の色に合わせる (行の TextBlock の文字は TextHighlighter が
        /// 色を変えるが、別の TextBlock の文字には効かないため)。
        /// </summary>
        private void ColorGlyphs()
        {
            for (int i = 0; i < _glyphsUsed; i++)
            {
                (bool hex, int c) = _glyphCells[i];
                CellPaint[] paint = hex ? HexPaint : TextPaint;
                if (c < paint.Length && paint[c].Foreground is { } fore && !ReferenceEquals(_glyphs[i].Foreground, fore))
                {
                    _glyphs[i].Foreground = fore;
                }
            }
        }

        /// <summary>
        /// 検索の一致の枠線 (UI-28 の仕様 7: 色だけで伝えない。00-overview.md 11.5)。一致の続く範囲ごとに、Hex 列とテキスト列の
        /// それぞれを 1 px の枠で囲む (行をまたぐ一致は行ごとに囲む)。ハイコントラストでは文字の色で描く。
        /// </summary>
        private void DrawMatchBorders(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            RowColumns columns = frame.Columns;
            int count = Count;
            int first = _matched.AsSpan(0, count).IndexOf(true);
            if (first < 0)
            {
                return;
            }

            Brush brush = palette.HighContrast ? palette.Text : palette.MatchText;
            for (int c = first; c < count; c++)
            {
                if (!_matched[c])
                {
                    continue;
                }

                int end = c;
                while (end + 1 < count && _matched[end + 1])
                {
                    end++;
                }

                if (columns.ShowHex)
                {
                    double left = columns.HexIndex(c) * cellWidth;
                    double right = (columns.HexIndex(end) + RowFormat.HexCellChars) * cellWidth;
                    Box(left, right, rowHeight, brush);
                    Lines.Add(("matchBorder", left, 0, right, rowHeight));
                }

                if (columns.ShowText)
                {
                    double left = columns.TextIndex(c) * cellWidth;
                    double right = columns.TextIndex(end + 1) * cellWidth;
                    Box(left, right, rowHeight, brush);
                    Lines.Add(("matchBorderText", left, 0, right, rowHeight));
                }

                c = end;
            }
        }

        /// <summary>[left, right) と行の高さの範囲を 1 px の枠で囲む。</summary>
        private void Box(double left, double right, double rowHeight, Brush brush)
        {
            PlaceBar(left, 0, right - left, 1, brush);
            PlaceBar(left, rowHeight - 1, right - left, 1, brush);
            PlaceBar(left, 0, 1, rowHeight, brush);
            PlaceBar(right - 1, 0, 1, rowHeight, brush);
        }

        /// <summary>
        /// 削除された位置 (VIEW-15 の仕様 5): 詰まった境界の左側のセルの右端に縦の線 (幅 2 px) を引く。色は「変更」の色 (ハイコントラストでは
        /// 文字の色)。Hex 列とテキスト列の両方に引く。
        /// </summary>
        private void DrawDeletions(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            RowColumns columns = frame.Columns;
            Brush brush = palette.HighContrast ? palette.Text : palette.Modified;
            for (int c = _lead; c < Count; c++)
            {
                if (!_deleted[c])
                {
                    continue;
                }

                if (columns.ShowHex)
                {
                    double x = (columns.HexIndex(c) + RowFormat.HexCellChars) * cellWidth - 1;
                    PlaceBar(x, 0, 2, rowHeight, brush);
                    Lines.Add(("deletion", x, 0, x, rowHeight));
                }

                if (columns.ShowText)
                {
                    double x = (columns.TextIndex(c) + 1) * cellWidth - 1;
                    PlaceBar(x, 0, 2, rowHeight, brush);
                    Lines.Add(("deletionText", x, 0, x, rowHeight));
                }
            }
        }

        private void Underline(ChangeMark mark, double left, double right, double rowHeight, Brush brush)
        {
            switch (mark)
            {
                case ChangeMark.Inserted:
                    PlaceBar(left, rowHeight - 1, right - left, 1, brush);
                    PlaceBar(left, rowHeight - 3, right - left, 1, brush);
                    break;
                case ChangeMark.Saved:
                    PlaceLine(left, rowHeight - 1, right, rowHeight - 1, brush, dashed: true);
                    break;
                default:
                    PlaceBar(left, rowHeight - 2, right - left, 1, brush);
                    break;
            }
        }

        /// <summary>ハイコントラストでの列の交互色の代わり (VIEW-14 の仕様 5): グループの間に縦の点線を引く。</summary>
        private void DrawAlternateLines(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            if (!frame.Style.AlternateColumns || !palette.HighContrast || !frame.Columns.ShowHex)
            {
                return;
            }

            RowFormat format = frame.Columns.Format;
            for (int c = 1; c < format.BytesPerRow; c++)
            {
                if (!format.IsGroupStart(c))
                {
                    continue;
                }

                // グループの間の空白の中央 (中央区切りで空白が 2 文字のときも中央)。
                double x = (format.HexIndex(c - 1) + RowFormat.HexCellChars + format.HexIndex(c)) / 2.0 * cellWidth;
                PlaceLine(x, 0, x, rowHeight, palette.Separator, dashed: true);
                Lines.Add(("groupSeparator", x, 0, x, rowHeight));
            }
        }

        private void PlaceBar(double x, double y, double width, double height, Brush brush)
        {
            Rectangle bar;
            if (_barsUsed < _bars.Count)
            {
                bar = _bars[_barsUsed];
            }
            else
            {
                bar = new Rectangle { IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(bar, AccessibilityView.Raw);
                _bars.Add(bar);
                Container.Children.Add(bar);
            }

            _barsUsed++;
            bar.Visibility = Visibility.Visible;
            bar.Fill = brush;
            bar.Width = Math.Max(0, width);
            bar.Height = height;
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y);
        }

        private void PlaceLine(double x1, double y1, double x2, double y2, Brush brush, bool dashed)
        {
            Line line;
            if (_linesUsed < _lines.Count)
            {
                line = _lines[_linesUsed];
            }
            else
            {
                line = new Line { IsHitTestVisible = false, StrokeThickness = 1 };
                AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
                _lines.Add(line);
                Container.Children.Add(line);
            }

            _linesUsed++;
            line.Visibility = Visibility.Visible;
            line.Stroke = brush;
            line.X1 = x1;
            line.Y1 = y1;
            line.X2 = x2;
            line.Y2 = y2;
            line.StrokeDashArray = dashed ? [1, 1] : null;
        }

        private void HideUnused()
        {
            for (int i = _barsUsed; i < _bars.Count; i++)
            {
                _bars[i].Visibility = Visibility.Collapsed;
            }

            for (int i = _linesUsed; i < _lines.Count; i++)
            {
                _lines[i].Visibility = Visibility.Collapsed;
            }

            for (int i = _glyphsUsed; i < _glyphs.Count; i++)
            {
                _glyphs[i].Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>行の下線の情報 (テスト用): セルごとの下線の形。</summary>
        public string UnderlineAt(int c) => _frame.Style.HighlightModified ? MarkAt(c) switch
        {
            ChangeMark.Overwritten => "solid",
            ChangeMark.Inserted => "double",
            ChangeMark.Saved => "dotted",
            _ => "none",
        } : "none";

        /// <summary>
        /// 読み取れない範囲の斜線の模様 (VIEW-03 の仕様 5)。色だけに頼らないため必ず描く。ほかの層の背景の上に描く (VIEW-17 の仕様 7)。
        /// </summary>
        private void UpdateHatches(RowColumns columns, Palette palette, double cellWidth, double rowHeight)
        {
            int used = 0;
            int count = Count;
            for (int c = _lead; c < count; c++)
            {
                if (_states[c] != ByteState.Unreadable)
                {
                    continue;
                }

                int end = c;
                while (end + 1 < count && _states[end + 1] == ByteState.Unreadable)
                {
                    end++;
                }

                if (columns.ShowHex)
                {
                    double hexLeft = columns.HexIndex(c) * cellWidth;
                    double hexRight = (columns.HexIndex(end) + 2) * cellWidth;
                    PlaceHatch(used++, hexLeft, hexRight - hexLeft, rowHeight, palette);
                }

                if (columns.ShowText)
                {
                    PlaceHatch(used++, columns.TextIndex(c) * cellWidth, (end - c + 1) * cellWidth, rowHeight, palette);
                }

                c = end;
            }

            for (int i = used; i < _hatches.Count; i++)
            {
                _hatches[i].Visibility = Visibility.Collapsed;
            }
        }

        private void PlaceHatch(int index, double x, double width, double height, Palette palette)
        {
            if (index >= _hatches.Count)
            {
                var path = new Path { StrokeThickness = 1, IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(path, AccessibilityView.Raw);
                _hatches.Add(path);
                Container.Children.Add(path);
            }

            Path p = _hatches[index];
            p.Visibility = Visibility.Visible;
            p.Stroke = palette.Hatch;
            Canvas.SetLeft(p, x);
            p.Width = width;
            p.Height = height;
            p.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
            var group = new GeometryGroup();
            const double step = 6;
            for (double i = -height; i < width; i += step)
            {
                group.Children.Add(new LineGeometry
                {
                    StartPoint = new Windows.Foundation.Point(i, height),
                    EndPoint = new Windows.Foundation.Point(i + height, 0),
                });
            }

            p.Data = group;
        }
    }

    /// <summary>行の Run と、最後に設定した文字と色。</summary>
    private sealed class RunSlot
    {
        public required Run Run { get; init; }

        public required string Text { get; set; }

        public Brush? Brush { get; set; }
    }

    /// <summary>同じ色の文字をまとめて Run にする。前回の Run (<paramref name="runs"/>) を順に使い回し、余った Run は外す。</summary>
    private sealed class RunBuilder(TextBlock row, List<RunSlot> runs)
    {
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _text = new();
        private Brush? _brush;
        private int _used;

        /// <summary>行を作り直す前に呼ぶ (文字列の領域は使い回す)。</summary>
        public void Reset()
        {
            _line.Clear();
            _text.Clear();
            _brush = null;
            _used = 0;
        }

        /// <summary>行全体の文字列 (読まれたときに作る)。</summary>
        public string Line => _line.ToString();

        /// <summary>
        /// 残りを書き出す。余った Run は外さずに空にして残す (次に Run が増えたときに使い回す。Run を外して作り直すと、XAML の
        /// オブジェクトの追跡のために GC が増える。VIEW-04 の仕様 3)。
        /// </summary>
        public void Flush()
        {
            FlushRun();
            for (int i = _used; i < runs.Count; i++)
            {
                RunSlot slot = runs[i];
                if (slot.Text.Length != 0)
                {
                    slot.Run.Text = string.Empty;
                    slot.Text = string.Empty;
                }
            }
        }

        private void FlushRun()
        {
            if (_text.Length == 0)
            {
                return;
            }

            // 書き換えは、覚えている文字と色 (managed の値) と比べて変わったときだけ行う (Run の値を読むと文字列の写しを作るため)。
            // 同じ文字なら文字列を作らない。
            if (_used < runs.Count)
            {
                RunSlot slot = runs[_used];
                if (!_text.Equals(slot.Text.AsSpan()))
                {
                    string changed = _text.ToString();
                    slot.Run.Text = changed;
                    slot.Text = changed;
                }

                if (!ReferenceEquals(slot.Brush, _brush))
                {
                    slot.Run.Foreground = _brush;
                    slot.Brush = _brush;
                }
            }
            else
            {
                string text = _text.ToString();
                var run = new Run { Text = text, Foreground = _brush };
                runs.Add(new RunSlot { Run = run, Text = text, Brush = _brush });
                row.Inlines.Add(run);
            }

            _used++;
            _text.Clear();
        }

        /// <summary>
        /// 行には空白を描き、行の文字列 (UI オートメーション) には本当の文字を入れる (文字を別の TextBlock に描く場合。UI-29 の仕様 2)。
        /// </summary>
        public void AppendMasked(string text, Brush brush)
        {
            if (_text.Length == 0 && !ReferenceEquals(brush, _brush))
            {
                _brush = brush;
            }

            _text.Append(RowVisual.Spaces(text.Length));
            _line.Append(text);
        }

        public void Append(string text, Brush brush)
        {
            // 空白は色が見えないので、そのまま続ける (仮表示の行などで、セルごとに Run が分かれて描画が重くならないようにする。VIEW-04)。
            bool blank = text.Length > 0 && text.AsSpan().TrimStart(' ').IsEmpty;
            if (blank && _text.Length > 0)
            {
                _text.Append(text);
                _line.Append(text);
                return;
            }

            if (!ReferenceEquals(brush, _brush))
            {
                FlushRun();
                _brush = brush;
            }

            _text.Append(text);
            _line.Append(text);
        }
    }
}
