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
/// Hex ビューの行の要素 (VIEW-01 の仕様 2・8、VIEW-04 の仕様 3)。1 行を 1 つの TextBlock (色の違う区間ごとの Run) で描き、下線・枠・模様は
/// 図形で重ねる。重ねる順は VIEW-17 の仕様 5 の表に従う: 奥の層 (層 12〜16: バイトテーマの背景・現在行・レコードの交互色・列の交互色) は
/// 行の下の面 (<see cref="RowVisual.Under"/>) に四角形で、層 7〜11 (ブックマークなど) はその上の範囲の強調の面 (HexView.Highlights.cs) に、
/// 層 2〜5 (選択範囲・検索の一致) は行の文字の TextHighlighter で描く。
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

        public int TextIndex(int column, int c) => Format.TextIndex(column, c);

        public int LineLength => Format.LineLength;
    }

    /// <summary>表示設定のうち、行の描き方に関わるもの (変われば全行を作り直す)。</summary>
    internal readonly record struct RowStyle(bool Lowercase, bool DimZeros, bool AlternateColumns, bool AlternateText, bool HighlightModified,
        bool ShowContinuation, bool HighContrast, NonPrintableStyle NonPrintable = NonPrintableStyle.Dot, bool SpacePad = false);

    /// <summary>
    /// 1 フレームの中で全行に共通の条件 (変われば全行を作り直す)。<paramref name="Theme"/> はバイトテーマ (VIEW-17。ハイコントラストでは null)、
    /// <paramref name="Records"/> はレコード表示 (VIEW-18。オフなら null)、<paramref name="TableColumns"/> は文字表 (VIEW-23) の
    /// テキスト列のビット (文字の範囲をセルの数にする)、<paramref name="ActiveText"/> は操作中のテキスト列。
    /// </summary>
    /// <remarks>Proportional: 等幅でないフォント。文字を 1 文字ずつセルの中央に描く (UI-29 の仕様 2)。</remarks>
    internal readonly record struct RowFrame(RowColumns Columns, ActiveColumn Active, int PaletteVersion, TextEncoding Encoding, RowStyle Style,
        bool Proportional = false, ThemeBrushes? Theme = null, RecordLayout? Records = null, int TableColumns = 0, int ActiveText = 0);

    /// <summary>行ごとの飾り (区切り線と見出し。VIEW-33)。</summary>
    internal readonly record struct RowDecor(bool Separator, string? SeparatorLabel);

    /// <summary>描いたセルの情報 (UI オートメーション・テストの描画モデルで返す)。<paramref name="Frame"/> は点線の枠など。</summary>
    internal readonly record struct CellPaint(string Text, Brush Foreground, Brush Background, string Layer, ChangeMark Underline, string? Frame = null);

    /// <summary>バイトテーマ (VIEW-17) の値ごとの文字色と背景色のブラシ (null は指定なし)。テーマ・テーマの明暗・配色が変わったら作り直す。</summary>
    internal sealed class ThemeBrushes(Brush?[] fore, Brush?[] back)
    {
        public Brush?[] Fore { get; } = fore;

        public Brush?[] Back { get; } = back;

        public bool AnyBackground { get; } = back.Any(b => b is not null);
    }

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

        // そのバイトの後ろ (右) に削除によって詰まった境界がある (VIEW-15 の仕様 5)。
        private bool[] _deleted = [];

        // テキスト列ごとの解読結果 (VIEW-24。0 が 1 列目)。
        private TextCell[][] _texts = [[]];
        private int _count = -1;
        private int _lead;
        private CellMode _mode;
        private bool[] _selected = [];
        private bool _currentRow;
        private RowDecor _decor;
        private RowFrame _frame;
        private readonly List<Path> _hatches = [];
        private readonly List<Rectangle> _bars = [];
        private readonly List<Rectangle> _underBars = [];
        private readonly List<Line> _lines = [];
        private readonly List<TextBlock> _glyphs = [];
        private TextBlock? _separatorLabel;

        // 別に描いた文字のセル (_glyphs と同じ並び): Hex 列か (テキスト列なら列の番号)、セルの番号。選択などの層の文字の色を後から合わせる。
        private readonly List<(int Column, int Cell)> _glyphCells = [];

        // 行の TextBlock の Run (Content.Inlines と同じ並び)。作り直さずに文字と色を書き換えて使い回す (Run を毎回作ると、XAML の
        // オブジェクトの追跡のために GC が増え、1 行のバイト数が多いとフレームが遅れる。VIEW-04 の仕様 3)。
        private readonly List<RunSlot> _runs = [];
        private RunBuilder? _builder;
        private readonly Rectangle _rowBack;
        private int _barsUsed;
        private int _underUsed;
        private int _linesUsed;
        private int _glyphsUsed;

        public RowVisual(FontFamily font, double fontSize, double rowHeight, int spacing)
        {
            Offset = CreateText();
            Content = CreateText();
            _rowBack = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(_rowBack, AccessibilityView.Raw);
            Container = new Canvas();
            Container.Children.Add(Content);
            Under = new Canvas { IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(Under, AccessibilityView.Raw);
            Under.Children.Add(_rowBack);
            ApplyFont(font, fontSize, rowHeight, spacing);
        }

        public TextBlock Offset { get; }

        public TextBlock Content { get; }

        /// <summary>内容の TextBlock と、下線・枠・模様の図形を入れる。</summary>
        public Canvas Container { get; }

        /// <summary>行の下の面 (範囲の強調の面より奥): 層 12〜16 の背景 (バイトテーマ・現在行・レコードの交互色・列の交互色)。</summary>
        public Canvas Under { get; }

        public long OffsetRowStart { get; private set; } = long.MinValue;

        public int OffsetDigits { get; private set; }

        /// <summary>オフセット列の文字列を作ったときの書式 (変われば作り直す)。</summary>
        public OffsetFormat? OffsetFormatUsed { get; private set; }

        /// <summary>オフセット列の文字列を作ったときのレコード番号の表示 (VIEW-18 の仕様 5。変われば作り直す)。</summary>
        public object? OffsetKeyUsed { get; private set; }

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

        /// <summary>区切り線の見出し (VIEW-33。線がなければ null、線だけなら空)。</summary>
        public string? SeparatorLabel => _decor.Separator ? _decor.SeparatorLabel ?? string.Empty : null;

        /// <summary>Hex 列のセルの描画の情報 (テスト用)。</summary>
        public CellPaint[] HexPaint { get; private set; } = [];

        /// <summary>テキスト列 1 のセルの描画の情報 (テスト用)。</summary>
        public CellPaint[] TextPaint => TextPaints.Length > 0 ? TextPaints[0] : [];

        /// <summary>テキスト列ごとのセルの描画の情報 (テスト用。VIEW-24)。</summary>
        public CellPaint[][] TextPaints { get; private set; } = [];

        /// <summary>テキスト列 1 の文字の描画範囲 (セルごと。文字を描いていないセルは null。VIEW-22 の確認用)。</summary>
        public (string Glyph, double Left, double Width, double ScaleX)?[] Glyphs => GlyphsByColumn.Length > 0 ? GlyphsByColumn[0] : [];

        /// <summary>テキスト列ごとの文字の描画範囲。</summary>
        public (string Glyph, double Left, double Width, double ScaleX)?[][] GlyphsByColumn { get; private set; } = [];

        /// <summary>行に引いた線 (現在行の上下の線、列の交互色の代わりの縦の点線、レコードの境界、区切り線)。</summary>
        public List<(string Kind, double X1, double Y1, double X2, double Y2)> Lines { get; } = [];

        /// <summary>行の下の面に描いた背景 (層 12〜16。テスト用): 層の名前・列・最初と最後のバイト・色。</summary>
        public List<(string Layer, string Column, int First, int Last, Brush Brush)> UnderFills { get; } = [];

        public bool Visible
        {
            get => Offset.Visibility == Visibility.Visible;
            set
            {
                Visibility v = value ? Visibility.Visible : Visibility.Collapsed;
                if (Container.Visibility != v)
                {
                    Container.Visibility = v;
                    Under.Visibility = v;
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
            OffsetKeyUsed = null;
            ContentRowStart = long.MinValue;
        }

        public void SetTop(double y)
        {
            if (Top != y || Canvas.GetTop(Offset) != y)
            {
                Top = y;
                Canvas.SetTop(Offset, y);
                Canvas.SetTop(Container, y);
                Canvas.SetTop(Under, y);
            }
        }

        public void SetOffset(long rowStart, OffsetFormat format, Palette palette, Func<long, string?>? recordLabel = null, object? key = null,
            int width = 0)
        {
            OffsetRowStart = rowStart;
            OffsetFormatUsed = format;
            OffsetKeyUsed = key;
            OffsetDigits = Math.Max(width, format.ColumnWidth);

            // 行の先頭のずれで最初の行が負のオフセットから始まる場合は、アドレスがあればそのアドレス、なければ 0 を示す (VIEW-20)。
            long shown = rowStart < 0 && !format.UsesBaseAddress && format.ReferencePoint is null ? 0 : rowStart;

            // レコード番号の表示 (VIEW-18 の仕様 5): 「#レコード番号:レコード内の位置」。開始オフセットより前の行は通常のオフセット。
            string text = recordLabel?.Invoke(Math.Max(0, rowStart)) is { } label ? label : format.Column(shown);
            Offset.Text = text.Length < OffsetDigits ? text.PadLeft(OffsetDigits) : text;
            Offset.Foreground = palette.OffsetText;
        }

        /// <summary>内容を更新する。前回と同じなら何もしない。作り直したら true。</summary>
        public bool Update(in RowFrame frame, long rowStart, int lead, int count, ReadOnlySpan<byte> bytes, ReadOnlySpan<ByteState> states,
            ReadOnlySpan<ChangeMark> marks, ReadOnlySpan<bool> matched, ReadOnlySpan<bool> focus, ReadOnlySpan<bool> deleted, TextCell[][] texts,
            int textFrom, CellMode mode, ReadOnlySpan<bool> selected, bool currentRow, RowDecor decor, Palette palette, double cellWidth,
            double rowHeight, Func<string, double> measure)
        {
            int columns = Math.Max(1, frame.Columns.Format.ShownTextColumns);
            if (_count == count && _lead == lead && ContentRowStart == rowStart && _mode == mode && _frame == frame
                && selected[..count].SequenceEqual(_selected.AsSpan(0, count)) && _currentRow == currentRow && _decor == decor
                && bytes[..count].SequenceEqual(_bytes.AsSpan(0, count)) && states[..count].SequenceEqual(_states.AsSpan(0, count))
                && marks[..count].SequenceEqual(_marks.AsSpan(0, count)) && matched[..count].SequenceEqual(_matched.AsSpan(0, count))
                && focus[..count].SequenceEqual(_focus.AsSpan(0, count)) && deleted[..count].SequenceEqual(_deleted.AsSpan(0, count))
                && SameText(texts, textFrom, count, columns))
            {
                return false;
            }

            ContentRowStart = rowStart;
            _count = count;
            _lead = lead;
            _mode = mode;
            _frame = frame;
            _currentRow = currentRow;
            _decor = decor;
            int b = frame.Columns.BytesPerRow;
            if (_bytes.Length < b)
            {
                _bytes = new byte[b];
                _states = new ByteState[b];
                _marks = new ChangeMark[b];
                _matched = new bool[b];
                _focus = new bool[b];
                _deleted = new bool[b];
                _selected = new bool[b];
            }

            if (_texts.Length != columns || _texts[0].Length < b)
            {
                _texts = new TextCell[columns][];
                for (int t = 0; t < columns; t++)
                {
                    _texts[t] = new TextCell[b];
                }
            }

            // 作り直す行の先頭のずれ (lead) より前のセルは選択しない。
            _selected.AsSpan().Clear();
            selected[lead..count].CopyTo(_selected.AsSpan(lead));

            bytes[..count].CopyTo(_bytes);
            states[..count].CopyTo(_states);
            marks[..count].CopyTo(_marks);
            matched[..count].CopyTo(_matched);
            focus[..count].CopyTo(_focus);
            deleted[..count].CopyTo(_deleted);
            for (int t = 0; t < columns; t++)
            {
                _texts[t].AsSpan().Fill(TextCell.None);
                if (t < texts.Length)
                {
                    texts[t].AsSpan(textFrom, Math.Min(count, b)).CopyTo(_texts[t]);
                }
            }

            _barsUsed = 0;
            _underUsed = 0;
            _linesUsed = 0;
            _glyphsUsed = 0;
            Lines.Clear();
            UnderFills.Clear();
            Fill(frame, palette, cellWidth, rowHeight, measure);
            Highlight(frame, palette);
            ColorGlyphs();
            DrawUnder(frame, palette, cellWidth, rowHeight);
            DrawUnderlines(frame, palette, cellWidth, rowHeight);
            DrawMatchBorders(frame, palette, cellWidth, rowHeight);
            DrawDeletions(frame, palette, cellWidth, rowHeight);
            DrawAlternateLines(frame, palette, cellWidth, rowHeight);
            DrawRecordLines(frame, palette, cellWidth, rowHeight);
            DrawFrames(frame, palette, cellWidth, rowHeight);
            DrawSeparator(frame, palette, cellWidth);
            UpdateHatches(frame.Columns, palette, cellWidth, rowHeight);
            HideUnused();
            return true;
        }

        private bool SameText(TextCell[][] texts, int from, int count, int columns)
        {
            if (_texts.Length != columns)
            {
                return false;
            }

            for (int t = 0; t < columns; t++)
            {
                TextCell[] mine = _texts[t];
                TextCell[]? given = t < texts.Length ? texts[t] : null;
                for (int c = 0; c < count; c++)
                {
                    TextCell expected = given is not null && from + c < given.Length ? given[from + c] : TextCell.None;
                    if (c >= mine.Length || mine[c] != expected)
                    {
                        return false;
                    }
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

        /// <summary>Hex 列のセルの表示 (VIEW-03 の仮表示を含む。VIEW-12 の大文字・小文字)。1 バイトの 2 文字。</summary>
        public string HexCellText(int c) => KindAt(c) switch
        {
            CellKind.Empty => "  ",
            CellKind.Loading => _mode == CellMode.Blank ? "  " : "··",
            CellKind.Unreadable => "??",
            _ => (_frame.Style.Lowercase ? HexStringsLower : HexStrings)[_bytes[c]],
        };

        /// <summary>テキスト列 1 のセルの表示 (文字の範囲の続きのセルは空白。描く文字は <see cref="Glyphs"/>)。</summary>
        public string TextCellText(int c) => TextCellText(0, c);

        /// <summary>テキスト列 <paramref name="column"/> のセルの表示。</summary>
        public string TextCellText(int column, int c)
        {
            TextCell cell = TextAt(column, c);
            return KindAt(c) switch
            {
                CellKind.Empty => string.Empty,
                CellKind.Loading or CellKind.Unreadable => " ",
                _ => cell.Kind switch
                {
                    TextCellKind.Char => cell.Text,
                    TextCellKind.Continuation => _frame.Style.ShowContinuation ? "·" : " ",
                    TextCellKind.Empty => " ",
                    TextCellKind.NonPrintable => cell.Text,
                    TextCellKind.Invalid when cell.Text.Length > 0 => cell.Text,
                    _ => ".",
                },
            };
        }

        /// <summary>テキスト列 1 のセルの解読結果 (VIEW-22)。</summary>
        public TextCell TextAt(int c) => TextAt(0, c);

        /// <summary>テキスト列 <paramref name="column"/> のセルの解読結果 (VIEW-24)。</summary>
        public TextCell TextAt(int column, int c) =>
            column >= 0 && column < _texts.Length && c >= 0 && c < _texts[column].Length ? _texts[column][c] : TextCell.None;

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

        /// <summary>バイトテーマの文字色 (層 12。値で決まる層なので、読めるバイトだけ。VIEW-17 の仕様 7)。</summary>
        private Brush? ThemeFore(int c) => _frame.Theme is { } theme && c < Count ? theme.Fore[_bytes[c]] : null;

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

            if (ThemeFore(c) is { } themed)
            {
                return themed;
            }

            return _frame.Style.DimZeros && _bytes[c] == 0 ? palette.Zero : palette.HexText;
        }

        /// <summary>2 バイト以上のセルの文字色 (変更があれば変更の色、値全体が 0 なら薄く。VIEW-13 の仕様 3)。</summary>
        private Brush CellForeground(int first, int unit, Palette palette)
        {
            if (_frame.Style.HighlightModified)
            {
                for (int c = first; c < first + unit; c++)
                {
                    if (_marks[c] != ChangeMark.None)
                    {
                        return MarkBrush(_marks[c], palette);
                    }
                }
            }

            return _frame.Style.DimZeros && CellFormatter.IsZero(_bytes.AsSpan(first, unit)) ? palette.Zero : palette.HexText;
        }

        private Brush TextForeground(int column, int c, Palette palette)
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

            TextCell cell = TextAt(column, c);
            if (cell.Kind is TextCellKind.Invalid)
            {
                return palette.Invalid;
            }

            if (cell.Kind is TextCellKind.Continuation)
            {
                return palette.Dim;
            }

            if (ThemeFore(c) is { } themed)
            {
                return themed;
            }

            return cell.Kind switch
            {
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
            RowFormat format = frame.Columns.Format;
            int b = format.BytesPerRow;
            int count = Count;
            int columns = Math.Max(1, format.ShownTextColumns);

            // 配列は 1 行のバイト数が変わるまで使い回す (1 行 4,096 バイトでは大きなオブジェクトになり、毎回作ると GC でフレームが遅れる)。
            if (HexPaint.Length != b || TextPaints.Length != columns)
            {
                HexPaint = new CellPaint[b];
                TextPaints = new CellPaint[columns][];
                GlyphsByColumn = new (string, double, double, double)?[columns][];
                for (int t = 0; t < columns; t++)
                {
                    TextPaints[t] = new CellPaint[b];
                    GlyphsByColumn[t] = new (string, double, double, double)?[b];
                }
            }
            else
            {
                Array.Clear(HexPaint);
                for (int t = 0; t < columns; t++)
                {
                    Array.Clear(TextPaints[t]);
                    Array.Clear(GlyphsByColumn[t]);
                }
            }

            if (format.ShowHex)
            {
                if (format.IsHexBytes)
                {
                    FillHexBytes(frame, format, palette, cellWidth, rowHeight, measure, builder);
                }
                else
                {
                    FillCells(frame, format, palette, cellWidth, rowHeight, measure, builder);
                }
            }

            // テキスト列 (Hex 列との間、テキスト列どうしの間は 2 文字。VIEW-01 の仕様 1、VIEW-24)
            if (format.ShowText)
            {
                for (int t = 0; t < columns; t++)
                {
                    if (format.ShowHex || t > 0)
                    {
                        // 前の列の行末が欠けていれば (末尾の行)、その分も空白で埋めて列の位置をそろえる。
                        int pad = t == 0 ? RowFormat.ColumnGap : RowFormat.ColumnGap + (b - count);
                        builder.Append(Spaces(pad), palette.Text);
                    }

                    FillText(frame, format, t, palette, cellWidth, rowHeight, measure, builder);
                }
            }

            builder.Flush();
            _contentText = null;
        }

        /// <summary>Hex 形式: 1 バイト 1 セル。逆順表示ではグループの中の表示の位置にバイトを置く (VIEW-11 の仕様 4)。</summary>
        private void FillHexBytes(in RowFrame frame, RowFormat format, Palette palette, double cellWidth, double rowHeight, Func<string, double> measure,
            RunBuilder builder)
        {
            int b = format.BytesPerRow;
            int valid = Count;
            for (int s = 0; s < b; s++)
            {
                int c = format.ByteOfSlot(s, valid);
                string cell = HexCellText(c);
                Brush fore = HexForeground(c, palette);
                if (frame.Proportional)
                {
                    // 等幅でないフォント: 1 文字ずつセルの中央に描き、行には空白を入れる (UI-29 の仕様 2)。
                    for (int i = 0; i < cell.Length; i++)
                    {
                        if (cell[i] != ' ')
                        {
                            PlaceGlyph(cell[i].ToString(), (format.CellStart(s) + i) * cellWidth, cellWidth, fore, rowHeight, measure, -1, c);
                        }
                    }

                    builder.AppendMasked(cell, fore);
                }
                else
                {
                    builder.Append(cell, fore);
                }

                // 末尾の不完全なグループは逆順にせず、点線の枠を付ける (VIEW-11 の仕様 7)。
                string? frameKind = format.IsIncompleteReversedGroup(c, valid) && c >= _lead ? "dotted" : null;
                HexPaint[c] = new CellPaint(cell, fore, palette.Background, "normal", _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None, frameKind);
                int gap = s + 1 < b ? format.CellStart(s + 1) - format.CellStart(s) - RowFormat.HexCellChars : 0;
                if (gap > 0)
                {
                    builder.Append(Spaces(gap), palette.Text);
                }
            }
        }

        /// <summary>
        /// Hex 以外の形式 (VIEW-10): 単位ごとのセル。単位に満たない端数のバイトは 1 バイトずつ Hex の 2 文字で、点線の枠を付ける (仕様 5)。
        /// </summary>
        private void FillCells(in RowFrame frame, RowFormat format, Palette palette, double cellWidth, double rowHeight, Func<string, double> measure,
            RunBuilder builder)
        {
            int b = format.BytesPerRow;
            int unit = format.Unit;
            int width = format.CellChars;
            int valid = Count;
            for (int k = 0; k < format.CellsPerRow; k++)
            {
                int first = k * unit;
                int last = Math.Min(b, first + unit) - 1;
                int startChar = format.CellStart(k);
                bool complete = format.IsCompleteCell(k, valid) && first >= _lead;
                bool readable = complete;
                bool unreadable = false;
                bool loading = false;
                for (int c = first; complete && c <= last; c++)
                {
                    CellKind kind = KindAt(c);
                    unreadable |= kind == CellKind.Unreadable;
                    loading |= kind == CellKind.Loading;
                    readable &= kind is CellKind.Normal or CellKind.Modified;
                }

                if (readable)
                {
                    string text = CellFormatter.Format(format.CellFormat, _bytes.AsSpan(first, unit), format.BigEndian, frame.Style.SpacePad,
                        frame.Style.Lowercase);
                    Brush fore = CellForeground(first, unit, palette);
                    AppendCellText(frame, text, fore, startChar, cellWidth, rowHeight, measure, builder, first);
                    for (int c = first; c <= last; c++)
                    {
                        HexPaint[c] = new CellPaint(text, fore, palette.Background, "normal", _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None);
                    }
                }
                else if (complete)
                {
                    // 読み込み中・読み取れないバイトを含むセルは、セル全体を仮表示にする (VIEW-03)。
                    char symbol = unreadable ? '?' : loading && _mode == CellMode.Blank ? ' ' : '·';
                    string text = new(symbol, width);
                    Brush fore = unreadable ? palette.Text : palette.Dim;
                    builder.Append(text, fore);
                    for (int c = first; c <= last; c++)
                    {
                        HexPaint[c] = new CellPaint(text, fore, palette.Background, "normal", ChangeMark.None);
                    }
                }
                else
                {
                    // 端数・行の先頭の空白: 1 バイトずつ Hex の 2 文字。
                    int used = 0;
                    for (int c = first; c <= last; c++)
                    {
                        string text = HexCellText(c);
                        Brush fore = HexForeground(c, palette);
                        AppendCellText(frame, text, fore, startChar + used, cellWidth, rowHeight, measure, builder, c);
                        used += RowFormat.HexCellChars;
                        bool partial = c >= _lead && c < valid;
                        HexPaint[c] = new CellPaint(text, fore, palette.Background, "normal",
                            _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None, partial ? "dotted" : null);
                    }

                    if (width > used)
                    {
                        builder.Append(Spaces(width - used), palette.Text);
                    }
                }

                int gap = k + 1 < format.CellsPerRow ? format.CellStart(k + 1) - startChar - width : 0;
                if (gap > 0)
                {
                    builder.Append(Spaces(gap), palette.Text);
                }
            }
        }

        private void AppendCellText(in RowFrame frame, string text, Brush fore, int startChar, double cellWidth, double rowHeight,
            Func<string, double> measure, RunBuilder builder, int cellIndex)
        {
            if (!frame.Proportional)
            {
                builder.Append(text, fore);
                return;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ' ')
                {
                    PlaceGlyph(text[i].ToString(), (startChar + i) * cellWidth, cellWidth, fore, rowHeight, measure, -1, cellIndex);
                }
            }

            builder.AppendMasked(text, fore);
        }

        /// <summary>テキスト列 1 つ (VIEW-21〜VIEW-24)。</summary>
        private void FillText(in RowFrame frame, RowFormat format, int column, Palette palette, double cellWidth, double rowHeight,
            Func<string, double> measure, RunBuilder builder)
        {
            int b = format.BytesPerRow;
            int count = Count;
            bool table = (frame.TableColumns & (1 << column)) != 0;
            CellPaint[] paint = TextPaints[column];
            (string, double, double, double)?[] glyphs = GlyphsByColumn[column];
            for (int c = 0; c < count; c++)
            {
                string cell = TextCellText(column, c);
                Brush fore = TextForeground(column, c, palette);
                TextCell decoded = TextAt(column, c);
                double left = format.TextIndex(column, c) * cellWidth;

                // 制御文字の図記号 (VIEW-21 の仕様 7) も等幅フォントにないことがあるため、同じく別に描く。
                if (KindAt(c) is CellKind.Normal or CellKind.Modified && decoded.Kind is TextCellKind.Char or TextCellKind.NonPrintable or TextCellKind.Invalid
                    && NeedsOverlay(decoded))
                {
                    // 全角・結合文字などは別の TextBlock で、文字の範囲のセルに収めて描く (VIEW-22 の仕様 2・3・6)。文字表の右辺が複数の文字
                    // なら、左辺のバイト数分のセルに収める (VIEW-23 の仕様 4)。
                    int cells = decoded.Wide ? 2 : table ? Math.Max(1, decoded.Span) : 1;
                    cells = Math.Min(cells, Math.Max(1, b - c));
                    glyphs[c] = PlaceGlyph(decoded.Text, left, cells * cellWidth, fore, rowHeight, measure, column, c, truncate: table);
                    builder.Append(" ", fore);
                }
                else if (frame.Proportional && cell.Length > 0 && cell != " ")
                {
                    // 等幅でないフォント: セルの中央に描く (UI-29 の仕様 2)。
                    glyphs[c] = PlaceGlyph(cell, left, cellWidth, fore, rowHeight, measure, column, c);
                    builder.AppendMasked(cell, fore);
                }
                else
                {
                    builder.Append(cell, fore);
                    if (cell.Length > 0 && cell != " ")
                    {
                        glyphs[c] = (cell, left, cellWidth, 1);
                    }
                }

                paint[c] = new CellPaint(cell, fore, palette.Background, "normal", _frame.Style.HighlightModified ? MarkAt(c) : ChangeMark.None);
            }
        }

        private static readonly string[] SpaceStrings = [.. Enumerable.Range(0, 17).Select(n => new string(' ', n))];

        /// <summary>空白の文字列 (セルの間の空白。毎回作らない)。</summary>
        internal static string Spaces(int n) => n < SpaceStrings.Length ? SpaceStrings[Math.Max(0, n)] : new string(' ', n);

        private ChangeMark MarkAt(int c) => KindAt(c) is CellKind.Modified ? _marks[c] : ChangeMark.None;

        /// <summary>行の TextBlock では幅がそろわない文字 (全角、結合文字、対応する字形が等幅フォントにない文字)。</summary>
        private static bool NeedsOverlay(TextCell cell) =>
            cell.Wide || cell.Text.Length != 1 || cell.Text[0] is >= '̀' and <= 'ͯ' || cell.Text[0] >= '԰';

        /// <summary>
        /// 文字を別の TextBlock に描く。<paramref name="column"/> は Hex 列なら -1、テキスト列なら列の番号。<paramref name="truncate"/> なら、
        /// 縮小率 50% 未満になる場合は切り詰めて末尾に <c>…</c> を付ける (VIEW-23 の仕様 4)。
        /// </summary>
        private (string, double, double, double) PlaceGlyph(string text, double left, double width, Brush fore, double rowHeight,
            Func<string, double> measure, int column, int cellIndex, bool truncate = false)
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

            _glyphCells[_glyphsUsed] = (column, cellIndex);
            _glyphsUsed++;
            double natural = measure(text);
            if (truncate && natural > 0 && width / natural < MinimumGlyphScale && text.Length > 1)
            {
                // 縮小しても読めない (縮小率 50% 未満): 収まるところまで切り詰めて「…」を付ける。
                string shown = text;
                while (shown.Length > 1)
                {
                    shown = shown[..^1];
                    natural = measure(shown + "…");
                    if (natural <= 0 || width / natural >= MinimumGlyphScale)
                    {
                        break;
                    }
                }

                text = shown + "…";
                natural = measure(text);
            }

            glyph.Visibility = Visibility.Visible;
            glyph.Text = text;
            glyph.Foreground = fore;
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

        /// <summary>文字表の右辺を縮小して描く最小の倍率 (VIEW-23 の仕様 4)。</summary>
        private const double MinimumGlyphScale = 0.5;

        // ---- 背景 (VIEW-17 の層 2〜5) ----

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

        /// <summary>Hex 列の [c0, c1] の文字の範囲を TextHighlighter に加える (逆順表示などで分かれる範囲も)。</summary>
        private void AddHexRange(TextHighlighter h, RowFormat format, int c0, int c1)
        {
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
            int n = format.HexSpans(c0, c1, Count, spans);
            for (int i = 0; i < n; i++)
            {
                h.Ranges.Add(new TextRange { StartIndex = spans[i].Start, Length = spans[i].Length });
            }
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
            int texts = Math.Max(1, columns.Format.ShownTextColumns);

            // 層 14: 現在行 (背景は行の下の面に描く。DrawUnder)。セルの情報だけ付ける。
            if (_currentRow && !palette.HighContrast)
            {
                for (int i = 0; i < HexPaint.Length; i++)
                {
                    HexPaint[i] = HexPaint[i] with { Background = palette.CurrentRow, Layer = "currentRow" };
                    for (int t = 0; t < texts; t++)
                    {
                        TextPaints[t][i] = TextPaints[t][i] with { Background = palette.CurrentRow, Layer = "currentRow" };
                    }
                }
            }

            // 層 3: 注目している範囲 (INSP-18 など)。
            AddRanges(columns, _focus, palette.FocusRange, null, "focus");

            // 層 4・5: 検索の一致。
            AddRanges(columns, _matched, palette.Match, palette.MatchText, "match");

            // 層 2: 選択範囲 (マルチ選択・矩形選択では行の中に複数の範囲がある)。操作中でない列の選択は薄い色で塗る (EDIT-01 の画面)。
            int firstSelected = _selected.AsSpan(0, Count).IndexOf(true);
            if (firstSelected >= 0)
            {
                bool hexActive = frame.Active == ActiveColumn.Hex;
                Brush hexBack = hexActive ? palette.Selection : palette.SelectionInactive;
                Brush hexFore = hexActive ? palette.SelectionText : palette.SelectionInactiveText;
                TextHighlighter? hex = columns.ShowHex ? TakeHighlighter(hexBack, hexFore) : null;
                for (int first = firstSelected; first < Count; first++)
                {
                    if (!_selected[first])
                    {
                        continue;
                    }

                    int last = first;
                    while (last + 1 < Count && _selected[last + 1])
                    {
                        last++;
                    }

                    if (hex is not null)
                    {
                        AddHexRange(hex, columns.Format, first, last);
                    }

                    for (int i = first; i <= last; i++)
                    {
                        HexPaint[i] = HexPaint[i] with { Background = hexBack, Foreground = hexFore, Layer = "selection" };
                    }

                    first = last;
                }

                if (hex is not null)
                {
                    ShowHighlighter(hex);
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    bool active = !hexActive && t == frame.ActiveText;
                    Brush textBack = active ? palette.Selection : palette.SelectionInactive;
                    Brush textFore = active ? palette.SelectionText : palette.SelectionInactiveText;
                    TextHighlighter text = TakeHighlighter(textBack, textFore);
                    for (int first = firstSelected; first < Count; first++)
                    {
                        if (!_selected[first])
                        {
                            continue;
                        }

                        int last = first;
                        while (last + 1 < Count && _selected[last + 1])
                        {
                            last++;
                        }

                        text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(t, first), Length = last - first + 1 });
                        for (int i = first; i <= last; i++)
                        {
                            TextPaints[t][i] = TextPaints[t][i] with { Background = textBack, Foreground = textFore, Layer = "selection" };
                        }

                        first = last;
                    }

                    ShowHighlighter(text);
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

            int texts = Math.Max(1, columns.Format.ShownTextColumns);
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
                    AddHexRange(hex, columns.Format, c, end);
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(t, c), Length = end - c + 1 });
                }

                for (int i = c; i <= end; i++)
                {
                    HexPaint[i] = HexPaint[i] with { Background = background, Foreground = foreground ?? HexPaint[i].Foreground, Layer = layer };
                    for (int t = 0; t < texts; t++)
                    {
                        TextPaints[t][i] = TextPaints[t][i] with { Background = background, Foreground = foreground ?? TextPaints[t][i].Foreground, Layer = layer };
                    }
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

        // ---- 行の下の面 (VIEW-17 の層 12〜16) ----

        /// <summary>
        /// 奥の層の背景を行の下の面に描く (奥から順に): 層 16 列の交互色、層 15 レコードの交互色、層 14 現在行、層 12 バイトテーマの背景。
        /// 列の交互色・レコードの交互色は現在行に隠れる。ハイコントラストでは色を塗らず、線に置き換える (DrawAlternateLines・DrawRecordLines)。
        /// </summary>
        private void DrawUnder(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            RowColumns columns = frame.Columns;
            RowFormat format = columns.Format;
            int texts = Math.Max(1, format.ShownTextColumns);
            bool hc = palette.HighContrast;
            int count = Count;
            double width = format.LineLength * cellWidth;

            // 層 16: 列の交互色 (VIEW-14)。
            if (frame.Style.AlternateColumns && !hc && !_currentRow)
            {
                for (int c = 0; c < format.BytesPerRow; c++)
                {
                    if (!format.IsGroupStart(c) || format.GroupOf(c) % 2 == 0)
                    {
                        continue;
                    }

                    int last = Math.Min(format.BytesPerRow, c + Math.Max(1, format.IsHexBytes ? format.GroupSize : format.Unit)) - 1;
                    if (columns.ShowHex)
                    {
                        UnderHex(format, c, last, palette.Alternate, "alternate", cellWidth, rowHeight, contiguousCells: true);
                    }

                    if (frame.Style.AlternateText && columns.ShowText && c < count)
                    {
                        for (int t = 0; t < texts; t++)
                        {
                            UnderText(format, t, c, Math.Min(last, count - 1), palette.Alternate, "alternate", cellWidth, rowHeight);
                        }
                    }

                    for (int i = c; i <= last; i++)
                    {
                        if (HexPaint[i].Layer == "normal")
                        {
                            HexPaint[i] = HexPaint[i] with { Background = palette.Alternate, Layer = "alternate" };
                        }

                        for (int t = 0; frame.Style.AlternateText && t < texts; t++)
                        {
                            if (TextPaints[t][i].Layer == "normal")
                            {
                                TextPaints[t][i] = TextPaints[t][i] with { Background = palette.Alternate, Layer = "alternate" };
                            }
                        }
                    }
                }
            }

            // 層 15: レコードの交互色 (VIEW-18 の仕様 3)。奇数番のレコードを塗る。開始オフセットより前は塗らない。
            if (frame.Records is { } records && !hc && !_currentRow)
            {
                long rowStart = ContentRowStart;
                for (int c = Math.Max(0, _lead); c < count; c++)
                {
                    if (!records.IsOdd(rowStart + c))
                    {
                        continue;
                    }

                    int end = c;
                    while (end + 1 < count && records.IsOdd(rowStart + end + 1) && !records.IsBoundary(rowStart + end + 1))
                    {
                        end++;
                    }

                    if (columns.ShowHex)
                    {
                        UnderHex(format, c, end, palette.RecordAlternate, "record", cellWidth, rowHeight, contiguousCells: false);
                    }

                    for (int t = 0; columns.ShowText && t < texts; t++)
                    {
                        UnderText(format, t, c, end, palette.RecordAlternate, "record", cellWidth, rowHeight);
                    }

                    for (int i = c; i <= end; i++)
                    {
                        if (HexPaint[i].Layer is "normal" or "alternate")
                        {
                            HexPaint[i] = HexPaint[i] with { Background = palette.RecordAlternate, Layer = "record" };
                        }

                        for (int t = 0; t < texts; t++)
                        {
                            if (TextPaints[t][i].Layer is "normal" or "alternate")
                            {
                                TextPaints[t][i] = TextPaints[t][i] with { Background = palette.RecordAlternate, Layer = "record" };
                            }
                        }
                    }

                    c = end;
                }
            }

            // 層 14: 現在行 (VIEW-06 の仕様 1)。ハイコントラストでは背景を塗らず、行の上下に 1 px の線を引く。
            if (_currentRow && !hc)
            {
                _rowBack.Visibility = Visibility.Visible;
                _rowBack.Fill = palette.CurrentRow;
                _rowBack.Width = width;
                _rowBack.Height = rowHeight;
            }
            else
            {
                _rowBack.Visibility = Visibility.Collapsed;
            }

            if (_currentRow && hc)
            {
                PlaceBar(0, 0, width, 1, palette.CurrentRowLine);
                PlaceBar(0, rowHeight - 1, width, 1, palette.CurrentRowLine);
                Lines.Add(("currentRowTop", 0, 0, width, 0));
                Lines.Add(("currentRowBottom", 0, rowHeight - 1, width, rowHeight - 1));
            }

            // 層 12: バイトテーマの背景 (独自テーマで指定した場合)。値で決まるため、読めるバイトだけ。
            if (frame.Theme is { AnyBackground: true } theme && format.IsHexBytes)
            {
                for (int c = _lead; c < count; c++)
                {
                    if (KindAt(c) is not (CellKind.Normal or CellKind.Modified) || theme.Back[_bytes[c]] is not { } brush)
                    {
                        continue;
                    }

                    int end = c;
                    while (end + 1 < count && KindAt(end + 1) is CellKind.Normal or CellKind.Modified && ReferenceEquals(theme.Back[_bytes[end + 1]], brush))
                    {
                        end++;
                    }

                    if (columns.ShowHex)
                    {
                        UnderHex(format, c, end, brush, "byteTheme", cellWidth, rowHeight, contiguousCells: false);
                    }

                    for (int t = 0; columns.ShowText && t < texts; t++)
                    {
                        UnderText(format, t, c, end, brush, "byteTheme", cellWidth, rowHeight);
                    }

                    for (int i = c; i <= end; i++)
                    {
                        if (HexPaint[i].Layer is not ("selection" or "match" or "focus"))
                        {
                            HexPaint[i] = HexPaint[i] with { Background = brush, Layer = "byteTheme" };
                        }

                        for (int t = 0; t < texts; t++)
                        {
                            if (TextPaints[t][i].Layer is not ("selection" or "match" or "focus"))
                            {
                                TextPaints[t][i] = TextPaints[t][i] with { Background = brush, Layer = "byteTheme" };
                            }
                        }
                    }

                    c = end;
                }
            }

            for (int i = _underUsed; i < _underBars.Count; i++)
            {
                _underBars[i].Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Hex 列の [c0, c1] を行の下の面に塗る。<paramref name="contiguousCells"/> ならグループの間の空白も塗る。</summary>
        private void UnderHex(RowFormat format, int c0, int c1, Brush brush, string layer, double cellWidth, double rowHeight, bool contiguousCells)
        {
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
            int n = format.HexSpans(c0, c1, Count, spans);
            for (int i = 0; i < n; i++)
            {
                PlaceUnder(spans[i].Start * cellWidth, spans[i].Length * cellWidth, rowHeight, brush);
            }

            UnderFills.Add((layer, "hex", c0, c1, brush));
        }

        private void UnderText(RowFormat format, int column, int c0, int c1, Brush brush, string layer, double cellWidth, double rowHeight)
        {
            PlaceUnder(format.TextIndex(column, c0) * cellWidth, (c1 - c0 + 1) * cellWidth, rowHeight, brush);
            UnderFills.Add((layer, column == 0 ? "text" : "text" + (column + 1), c0, c1, brush));
        }

        private void PlaceUnder(double x, double width, double height, Brush brush)
        {
            Rectangle bar;
            if (_underUsed < _underBars.Count)
            {
                bar = _underBars[_underUsed];
            }
            else
            {
                bar = new Rectangle { IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(bar, AccessibilityView.Raw);
                _underBars.Add(bar);
                Under.Children.Add(bar);
            }

            _underUsed++;
            bar.Visibility = Visibility.Visible;
            if (!ReferenceEquals(bar.Fill, brush))
            {
                bar.Fill = brush;
            }

            bar.Width = Math.Max(0, width);
            bar.Height = height;
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, 0);
        }

        // ---- 下線・枠・線 ----

        /// <summary>変更されたバイトの下線 (VIEW-15 の仕様 3〜8): 上書きは実線、挿入は 2 本線、保存済みの変更は点線。どの層が重なっても描く。</summary>
        private void DrawUnderlines(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            if (!frame.Style.HighlightModified)
            {
                return;
            }

            RowColumns columns = frame.Columns;
            int count = Count;
            int texts = Math.Max(1, columns.Format.ShownTextColumns);
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
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
                    int n = columns.Format.HexSpans(c, end, count, spans);
                    for (int i = 0; i < n; i++)
                    {
                        Underline(mark, spans[i].Start * cellWidth, (spans[i].Start + spans[i].Length) * cellWidth, rowHeight, brush);
                    }
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    Underline(mark, columns.TextIndex(t, c) * cellWidth, columns.TextIndex(t, end + 1) * cellWidth, rowHeight, brush);
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
                (int column, int c) = _glyphCells[i];
                CellPaint[] paint = column < 0 ? HexPaint : column < TextPaints.Length ? TextPaints[column] : [];
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

            int texts = Math.Max(1, columns.Format.ShownTextColumns);
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
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
                    int n = columns.Format.HexSpans(c, end, count, spans);
                    for (int i = 0; i < n; i++)
                    {
                        double left = spans[i].Start * cellWidth;
                        double right = (spans[i].Start + spans[i].Length) * cellWidth;
                        Box(left, right, rowHeight, brush);
                        Lines.Add(("matchBorder", left, 0, right, rowHeight));
                    }
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    double left = columns.TextIndex(t, c) * cellWidth;
                    double right = columns.TextIndex(t, end + 1) * cellWidth;
                    Box(left, right, rowHeight, brush);
                    Lines.Add((t == 0 ? "matchBorderText" : "matchBorderText" + (t + 1), left, 0, right, rowHeight));
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
            int texts = Math.Max(1, columns.Format.ShownTextColumns);
            Brush brush = palette.HighContrast ? palette.Text : palette.Modified;
            for (int c = _lead; c < Count; c++)
            {
                if (!_deleted[c])
                {
                    continue;
                }

                if (columns.ShowHex)
                {
                    (int start, int length) = columns.Format.ByteSpan(c, Count);
                    double x = (start + length) * cellWidth - 1;
                    PlaceBar(x, 0, 2, rowHeight, brush);
                    Lines.Add(("deletion", x, 0, x, rowHeight));
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    double x = (columns.TextIndex(t, c) + 1) * cellWidth - 1;
                    PlaceBar(x, 0, 2, rowHeight, brush);
                    Lines.Add((t == 0 ? "deletionText" : "deletionText" + (t + 1), x, 0, x, rowHeight));
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
            int group = format.IsHexBytes ? Math.Max(1, format.GroupSize) : format.Unit;
            for (int c = group; c < format.BytesPerRow; c += group)
            {
                // グループの間の空白の中央 (中央区切りで空白が 2 文字のときも中央)。
                int k = c / format.Unit;
                double x = (format.CellStart(k - 1) + format.CellChars + format.CellStart(k)) / 2.0 * cellWidth;
                if (format.IsHexBytes)
                {
                    x = (format.CellStart(c - 1) + RowFormat.HexCellChars + format.CellStart(c)) / 2.0 * cellWidth;
                }

                PlaceLine(x, 0, x, rowHeight, palette.Separator, dashed: true);
                Lines.Add(("groupSeparator", x, 0, x, rowHeight));
            }
        }

        /// <summary>
        /// ハイコントラストでのレコードの交互色の代わり (VIEW-18 の仕様 3): レコードの境界に実線を引く。行の先頭が境界なら行の上に横線、
        /// 行の途中なら境界のセルの左に縦線。
        /// </summary>
        private void DrawRecordLines(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            if (frame.Records is not { } records || !palette.HighContrast)
            {
                return;
            }

            RowColumns columns = frame.Columns;
            int texts = Math.Max(1, columns.Format.ShownTextColumns);
            long rowStart = ContentRowStart;
            double width = columns.LineLength * cellWidth;
            for (int c = Math.Max(0, _lead); c < Math.Max(Count, 1) && c < columns.BytesPerRow; c++)
            {
                if (!records.IsBoundary(rowStart + c))
                {
                    continue;
                }

                if (c == 0 || c == _lead)
                {
                    PlaceBar(0, 0, width, 1, palette.Text);
                    Lines.Add(("recordBoundary", 0, 0, width, 0));
                    continue;
                }

                if (columns.ShowHex)
                {
                    double x = columns.Format.ByteSpan(c, Count).Start * cellWidth - 1;
                    PlaceBar(x, 0, 1, rowHeight, palette.Text);
                    Lines.Add(("recordBoundary", x, 0, x, rowHeight));
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    double x = columns.TextIndex(t, c) * cellWidth;
                    PlaceBar(x, 0, 1, rowHeight, palette.Text);
                }
            }
        }

        /// <summary>点線の枠 (VIEW-10 の仕様 5 の端数のバイト、VIEW-11 の仕様 7 の末尾の不完全なグループ)。</summary>
        private void DrawFrames(in RowFrame frame, Palette palette, double cellWidth, double rowHeight)
        {
            RowFormat format = frame.Columns.Format;
            if (!format.ShowHex || (format.IsHexBytes && !format.Reverse))
            {
                return;
            }

            for (int c = _lead; c < Count; c++)
            {
                if (HexPaint[c].Frame != "dotted")
                {
                    continue;
                }

                (int start, int length) = format.ByteSpan(c, Count);
                double left = start * cellWidth;
                double right = (start + length) * cellWidth;
                PlaceLine(left, 0.5, right, 0.5, palette.Separator, dashed: true);
                PlaceLine(left, rowHeight - 0.5, right, rowHeight - 0.5, palette.Separator, dashed: true);
                PlaceLine(left + 0.5, 0, left + 0.5, rowHeight, palette.Separator, dashed: true);
                PlaceLine(right - 0.5, 0, right - 0.5, rowHeight, palette.Separator, dashed: true);
                Lines.Add(("dottedFrame", left, 0, right, rowHeight));
            }
        }

        /// <summary>
        /// 区切り線 (VIEW-33 の仕様 3): 区切りの境目の行の上に 1 px の線を引き、線の右端に小さな見出しを重ねる (行の高さは変えない)。
        /// </summary>
        private void DrawSeparator(in RowFrame frame, Palette palette, double cellWidth)
        {
            if (!_decor.Separator)
            {
                if (_separatorLabel is not null)
                {
                    _separatorLabel.Visibility = Visibility.Collapsed;
                }

                return;
            }

            double width = frame.Columns.LineLength * cellWidth;
            PlaceBar(0, 0, width, 1, palette.Separator);
            Lines.Add(("separator", 0, 0, width, 0));
            if (string.IsNullOrEmpty(_decor.SeparatorLabel))
            {
                if (_separatorLabel is not null)
                {
                    _separatorLabel.Visibility = Visibility.Collapsed;
                }

                return;
            }

            if (_separatorLabel is null)
            {
                _separatorLabel = new TextBlock { IsHitTestVisible = false, IsTextScaleFactorEnabled = false, TextWrapping = TextWrapping.NoWrap };
                AutomationProperties.SetAccessibilityView(_separatorLabel, AccessibilityView.Raw);
                Container.Children.Add(_separatorLabel);
            }

            _separatorLabel.Visibility = Visibility.Visible;
            _separatorLabel.Text = _decor.SeparatorLabel;
            _separatorLabel.FontSize = Math.Max(8, Content.FontSize * 0.7);
            _separatorLabel.Foreground = palette.OffsetText;
            _separatorLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_separatorLabel, Math.Max(0, width - _separatorLabel.DesiredSize.Width));
            Canvas.SetTop(_separatorLabel, -_separatorLabel.DesiredSize.Height / 2);
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
            int texts = Math.Max(1, columns.Format.ShownTextColumns);
            Span<(int Start, int Length)> spans = stackalloc (int, int)[8];
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
                    int n = columns.Format.HexSpans(c, end, count, spans);
                    for (int i = 0; i < n; i++)
                    {
                        PlaceHatch(used++, spans[i].Start * cellWidth, spans[i].Length * cellWidth, rowHeight, palette);
                    }
                }

                for (int t = 0; columns.ShowText && t < texts; t++)
                {
                    PlaceHatch(used++, columns.TextIndex(t, c) * cellWidth, (end - c + 1) * cellWidth, rowHeight, palette);
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
