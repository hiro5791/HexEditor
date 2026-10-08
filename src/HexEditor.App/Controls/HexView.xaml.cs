using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビュー (VIEW-01〜VIEW-04)。見えている行の数だけ行の要素を用意して使い回し、ファイルの大きさに関係なく
/// 同じ手間で描く。カーソル・選択・入力の規則は UI に依存しない <see cref="EditorState"/> が持ち、このコントロールは
/// 描画と入力の振り分けだけを行う。
/// <para>
/// ファイルの分け方: 描画 (このファイル)、行の要素 (HexView.Rows.cs)、配色 (HexView.Palette.cs)、列見出し (HexView.Ruler.cs)、
/// 表示の設定 (HexView.Options.cs)、入力 (HexView.Input.cs)、文字入力 TSF (HexView.TextInput.cs)、
/// アクセシビリティ (HexView.Accessibility.cs、HexViewAutomationPeer.cs)、診断 (HexViewDiagnostics.cs)。
/// </para>
/// </summary>
public sealed partial class HexView : UserControl
{
    /// <summary>既定のフォントの大きさ (10 pt = 13.33 epx。VIEW-01 の仕様 3)。</summary>
    public const double DefaultFontSize = 13.333;

    /// <summary>既定のフォント (VIEW-01 の仕様 3)。</summary>
    public const string DefaultFontFamily = "Cascadia Mono, Consolas";

    /// <summary>描画面の左端の余白。</summary>
    private const double LeftPadding = 8;

    /// <summary>読み込み中の仮表示を出すまでの猶予 (VIEW-03 の仕様 3)。</summary>
    private static readonly TimeSpan LoadingGrace = TimeSpan.FromMilliseconds(100);

    /// <summary>`00`〜`FF` の文字列 (VIEW-04 の仕様 3。毎回 ToString しない)。</summary>
    private static readonly string[] HexStrings = [.. Enumerable.Range(0, 256).Select(i => i.ToString("X2"))];

    /// <summary>小文字の `00`〜`ff` (VIEW-12)。</summary>
    private static readonly string[] HexStringsLower = [.. Enumerable.Range(0, 256).Select(i => i.ToString("x2"))];

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blinkTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _graceTimer;
    private readonly List<RowVisual> _rows = [];
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly HexViewDiagnostics _diagnostics;

    private string _fontFamilyName = DefaultFontFamily;
    private FontFamily _font = new(DefaultFontFamily);
    private double _baseFontSize = DefaultFontSize;
    private double _zoom = 1;
    private double _fontSize = DefaultFontSize;
    private int _characterSpacing;
    private double _cellWidth = 8;
    private double _rowHeight = 16;
    private double _rasterizationScale = 1;

    /// <summary>オフセット列の文字数 (VIEW-19 の仕様 3。表示しないときも、表示したときの桁数を持つ)。</summary>
    private int _digits = 8;
    private int _bytesPerRow;
    private RowFormat _format = new(16, 1, true);
    private bool _showOffset = true;

    /// <summary>行内のずれ (VIEW-28 の仕様 2。0 以上 1 行の高さ未満のピクセル)。</summary>
    private double _subRowOffset;

    /// <summary>横スクロールの位置 (ピクセル)。</summary>
    private double _horizontalOffset;

    private EditorState? _editor;
    private Palette? _palette;
    private int _paletteVersion;
    private bool _renderQueued;
    private bool _updatingScrollBar;
    private bool _focused;
    private long _loadingSince = -1;
    private bool _unreadableReported;
    private long _lastTopRow;
    private long _lastCursor = -1;
    private EditorSnapshot _lastState;

    public HexView()
    {
        InitializeComponent();

        // 読み込み完了の通知はスレッドプールから来るため、UI スレッドのキューをここで取っておく。
        _uiQueue = DispatcherQueue;
        _diagnostics = new HexViewDiagnostics(DiagnosticsPanel, DiagnosticsText);
        _blinkTimer = _uiQueue.CreateTimer();
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, CaretBlinkMilliseconds()));

        // 点滅は Opacity だけを変える (VIEW-04 の仕様 5。行は描き直さない)。
        _blinkTimer.Tick += (_, _) => Caret.Opacity = Caret.Opacity > 0 ? 0 : _caretOpacity;
        _graceTimer = _uiQueue.CreateTimer();
        _graceTimer.IsRepeating = false;
        _graceTimer.Interval = LoadingGrace;
        _graceTimer.Tick += (_, _) => QueueRender();

        GotFocus += (_, _) => OnFocusChanged(true);
        LostFocus += (_, _) => OnFocusChanged(false);
        ActualThemeChanged += (_, _) => ReloadPalette();
        foreach (UIElement probe in PaletteProbes.Children)
        {
            // ハイコントラストの切り替えでは ActualThemeChanged が来ない場合があるため、ブラシの変化も見る。
            probe.RegisterPropertyChangedCallback(Shape.FillProperty, (_, _) => ReloadPalette());
        }

        Loaded += HexView_Loaded;
        Unloaded += HexView_Unloaded;
        InitializeInput();
        InitializeTextInput();
        InitializeOptions();
    }

    /// <summary>入力を拒否したときに呼ぶ (InfoBar を出すため)。</summary>
    public event EventHandler<EditResult>? EditRejected;

    /// <summary>エディタの範囲のコマンド (クリップボードなど。00-overview 8.3) を要求した。右クリックメニューからも来る。</summary>
    public event EventHandler<EditorCommand>? CommandRequested;

    /// <summary>
    /// F6 / Shift+F6 で次 / 前の領域へフォーカスを移すよう求めた (VIEW-27 の仕様 4、UI-52 の仕様 1)。
    /// ウィンドウが <see cref="HexViewFocusRegionEventArgs.Handled"/> を立てなければ、このコントロールが
    /// 次 / 前のフォーカス可能な要素へフォーカスを移す (キーボードの罠を作らない)。
    /// </summary>
    public event EventHandler<HexViewFocusRegionEventArgs>? FocusRegionRequested;

    /// <summary>ステータスバーに一時的な文を出すよう求めた (VIEW-03 の「読み取れない範囲があります」など)。</summary>
    public event EventHandler<HexViewStatusMessageEventArgs>? StatusMessageRequested;

    /// <summary>表示するビューの状態。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            if (_editor is not null)
            {
                _editor.Changed -= OnEditorChanged;
                _editor.Document.DataLoaded -= OnDataLoaded;
            }

            _editor = value;
            _subRowOffset = 0;
            _horizontalOffset = 0;
            _unreadableReported = false;
            _loadingSince = -1;
            InvalidateRows();
            if (_editor is not null)
            {
                _editor.Changed += OnEditorChanged;
                _editor.Document.DataLoaded += OnDataLoaded;
                _lastTopRow = _editor.TopRow;
                _lastState = EditorSnapshot.Of(_editor);
                UpdateVisibleRows();
                QueueAutoFit();
            }

            UpdateScrollBar();
            QueueRender();
        }
    }

    /// <summary>フォント名 (VIEW-01 の仕様 3、UI-29 の仕様 2。設定から変える)。</summary>
    public string HexFontFamily
    {
        get => _fontFamilyName;
        set
        {
            _fontFamilyName = string.IsNullOrWhiteSpace(value) ? DefaultFontFamily : value;
            _font = new FontFamily(_fontFamilyName);
            _glyphWidths.Clear();
            RemeasureAndRender();
        }
    }

    /// <summary>フォントの大きさ (表示倍率 100%・文字サイズ 100%・ズーム 100% のときの epx。VIEW-01 の仕様 3)。</summary>
    public double HexFontSize
    {
        get => _baseFontSize;
        set
        {
            _baseFontSize = Math.Clamp(value, 4, 200);
            RemeasureAndRender();
        }
    }

    /// <summary>Hex 表示のズームの倍率 (VIEW-43 の仕様 1。UI-08 の 2)。ズームの操作は <see cref="ZoomAt"/>。</summary>
    public double Zoom
    {
        get => _zoom;
        set => ZoomAt(value, null);
    }

    /// <summary>診断表示 (VIEW-04 の仕様 8)。設定の「診断」から有効にする。</summary>
    public bool DiagnosticsEnabled
    {
        get => _diagnostics.Enabled;
        set => _diagnostics.Enabled = value;
    }

    /// <summary>診断の記録を書き出すファイル (CSV)。null なら書き出さない。性能のテストが読む。</summary>
    public string? DiagnosticsLogPath
    {
        get => _diagnostics.LogPath;
        set => _diagnostics.LogPath = value;
    }

    /// <summary>実際に使っているフォントの大きさ (epx。文字サイズの設定とズームを掛けた値)。</summary>
    internal double EffectiveFontSize => _fontSize;

    internal double CellWidth => _cellWidth;

    internal double RowHeight => _rowHeight;

    /// <summary>行の中の列の位置 (グループ化・列の表示・非表示。VIEW-09、VIEW-16)。</summary>
    internal RowColumns Columns => new(_format);

    private void HexView_Loaded(object sender, RoutedEventArgs e)
    {
        // デスクトップアプリでは購読できない環境がある。ハイコントラストの切り替えは配色の取り出し用の要素でも検出する。
        TrySubscribe(() => _uiSettings.TextScaleFactorChanged += UiSettings_TextScaleFactorChanged);
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += XamlRoot_Changed;
            _rasterizationScale = XamlRoot.RasterizationScale;
        }

        ReloadPalette();
        MeasureCell();
        UpdateVisibleRows();
        UpdateScrollBar();
        Render();
    }

    private void HexView_Unloaded(object sender, RoutedEventArgs e)
    {
        TrySubscribe(() => _uiSettings.TextScaleFactorChanged -= UiSettings_TextScaleFactorChanged);
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= XamlRoot_Changed;
        }

        _blinkTimer.Stop();
        _graceTimer.Stop();
        StopPointerTimers();
        _diagnostics.Stop();
    }

    // ---- 文字の大きさ・表示倍率 (VIEW-01 の仕様 4・11、VIEW-41 の仕様 4、VIEW-43) ----

    /// <summary>Windows の「文字サイズを大きくする」(スレッドプールから来る)。</summary>
    private void UiSettings_TextScaleFactorChanged(UISettings sender, object args) => _uiQueue.TryEnqueue(RemeasureAndRender);

    private static void TrySubscribe(Action subscribe)
    {
        try
        {
            subscribe();
        }
        catch (COMException ex)
        {
            Services.AppLog.Warning($"HexView: 設定の変更を購読できません ({ex.HResult:X8})");
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _rasterizationScale)
        {
            // 別の表示倍率のモニターへ移った。カーソルと一番上の行はそのまま (行の番号は倍率に依存しない)。
            _rasterizationScale = sender.RasterizationScale;
            RemeasureAndRender();
        }
    }

    private void RemeasureAndRender()
    {
        MeasureCell();
        UpdateVisibleRows();
        UpdateScrollBar();
        QueueAutoFit();
        QueueRender();
    }

    /// <summary>
    /// セル幅と行の高さを `0` の送り幅とフォントの高さから求め、物理ピクセルに切り上げる (VIEW-01 の仕様 4、VIEW-43 の仕様 2)。
    /// 文字サイズの設定は TextBlock に任せず、ここで大きさに掛ける (セル幅の計算と描画を一致させるため)。
    /// </summary>
    private void MeasureCell()
    {
        double scale = _rasterizationScale > 0 ? _rasterizationScale : 1;

        // フォントの大きさ = 設定 × Hex 表示のズーム × 画面全体のズーム × Windows の文字サイズ (VIEW-43 の仕様 1)。
        _fontSize = _baseFontSize * (FollowTextScaling ? TextScaleFactor : 1) * _zoom * _screenZoom;
        var probe = new TextBlock
        {
            Text = new string('0', 64),
            FontFamily = _font,
            FontSize = _fontSize,
            IsTextScaleFactorEnabled = false,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double advance = probe.DesiredSize.Width / 64;
        double cell = Math.Ceiling(advance * scale) / scale;

        // 切り上げた分は字間で埋める (CharacterSpacing は 1/1000 em 単位)。
        _characterSpacing = (int)Math.Round((cell - advance) / _fontSize * 1000);
        _cellWidth = advance + _characterSpacing * _fontSize / 1000;
        _rowHeight = Math.Ceiling(probe.DesiredSize.Height * _lineSpacing * scale) / scale;
        CompositionText.FontFamily = _font;
        CompositionText.FontSize = _fontSize;
        CompositionText.CharacterSpacing = _characterSpacing;
        foreach (RowVisual row in _rows)
        {
            row.ApplyFont(_font, _fontSize, _rowHeight, _characterSpacing);
        }

        _glyphWidths.Clear();
        ApplyRulerFont();
        InvalidateRows();
        UpdateColumnsLayout();
    }

    /// <summary>Windows の「文字サイズを大きくする」の倍率 (VIEW-41 の仕様 4)。</summary>
    private double TextScaleFactor
    {
        get
        {
#if HEX_TEST_HOOKS
            // テストでは Windows の設定を変えずに倍率だけを差し替える (利用者の設定を変えないため)。
            if (TestTextScaleFactor is { } test)
            {
                return test;
            }
#endif
            return _uiSettings.TextScaleFactor;
        }
    }

    // ---- 描画 ----

    /// <summary>次の描画で全行を作り直す。</summary>
    private void InvalidateRows()
    {
        foreach (RowVisual row in _rows)
        {
            row.Invalidate();
        }
    }

    private void ReloadPalette()
    {
        _palette = Palette.Load(this, _colorScheme, IsHighContrast);
        _paletteVersion++;
        Surface.Background = _palette.Background;
        InvalidateRows();
        QueueRender();
    }

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        _uiQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        // 閉じたタブのドキュメントは解放済みで読めない (追加バッファを読むと例外になり、XAML の処理の中で落ちる)。
        if (_editor is null || _palette is null || _editor.Document.IsDisposed)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        HexLayout layout = _editor.Layout;
        ViewSettings view = _editor.View;
        int bytesPerRow = layout.BytesPerRow;
        OffsetFormat offsetFormat = _editor.OffsetFormat;
        RowFormat format = RowFormat.For(view, bytesPerRow);
        int digits = offsetFormat.ColumnWidth;
        if (digits != _digits || bytesPerRow != _bytesPerRow || format != _format || _showOffset != view.ShowOffsetColumn)
        {
            _digits = digits;
            _bytesPerRow = bytesPerRow;
            _format = format;
            _showOffset = view.ShowOffsetColumn;
            InvalidateRows();
            UpdateColumnsLayout();
        }

        if (_editor.TopRow >= layout.MaxTopRow(_editor.VisibleRows))
        {
            _subRowOffset = 0;
        }

        // 上下 1 行ずつの余分 (VIEW-04 の仕様 1)。行内のずれがあると下にもう 1 行見える。
        int rows = (int)Math.Max(1, Math.Min(_editor.VisibleRows + 2, layout.TotalRows - _editor.TopRow));
        EnsureRowCount(rows);

        // 行の先頭のずれ (VIEW-20) があると、最初の行は負のオフセットから始まる。データは 0 から読む。
        long firstOffset = layout.RowStart(_editor.TopRow);
        int lead = (int)Math.Max(0, -firstOffset);
        long readStart = firstOffset + lead;
        int span = rows * bytesPerRow;
        byte[] bytes = new byte[span];
        var states = new ByteState[span];
        DocumentSnapshot snapshot = _editor.Document.Current;
        int available = lead + snapshot.ReadForDisplay(readStart, bytes.AsSpan(lead), states.AsSpan(lead));

        // 変更されたバイト (VIEW-15)。上書き・挿入・保存済みの変更を区別して、色に加えて下線の形でも示す。
        var marks = new ChangeMark[span];
        if (view.HighlightModified)
        {
            foreach ((long offset, long length) in _editor.SavedChangesIn(readStart, available - lead))
            {
                FillMarks(marks, offset, length, firstOffset, ChangeMark.Saved);
            }

            foreach ((long offset, long length, bool inserted) in snapshot.EnumerateChanges(readStart, available - lead))
            {
                FillMarks(marks, offset, length, firstOffset, inserted ? ChangeMark.Inserted : ChangeMark.Overwritten);
            }
        }

        long now = Stopwatch.GetTimestamp();
        bool anyLoading = states.AsSpan(lead, available - lead).Contains(ByteState.Loading);
        if (anyLoading && _loadingSince < 0)
        {
            _loadingSince = now;
        }
        else if (!anyLoading)
        {
            _loadingSince = -1;
            _graceTimer.Stop();
        }

        bool inGrace = anyLoading && Stopwatch.GetElapsedTime(_loadingSince, now) < LoadingGrace;
        if (inGrace && !_graceTimer.IsRunning)
        {
            _graceTimer.Interval = LoadingGrace - Stopwatch.GetElapsedTime(_loadingSince, now) + TimeSpan.FromMilliseconds(5);
            _graceTimer.Start();
        }

        bool[] matched = Shifted(ComputeMatched(snapshot, readStart, span - lead), lead, span);
        bool[] focus = ComputeFocusRanges(readStart, span - lead, lead, span);
        TextCell[] text = DecodeText(snapshot, view, readStart, lead, span, available);
        var columns = new RowColumns(format);
        long selStart = _editor.SelectionStart;
        long selEnd = selStart + _editor.SelectionLength;
        var style = new RowStyle(view.LowercaseHex, view.DimZeros, view.AlternateColumns, view.AlternateTextColumns, view.HighlightModified,
            view.ShowContinuation, _palette.HighContrast);
        var frame = new RowFrame(columns, _editor.ActiveColumn, _paletteVersion, _editor.TextEncoding, style);
        long cursorRow = view.HighlightCurrentRow ? layout.RowOf(_editor.Cursor) : -1;
        int rebuilt = 0;
        int loadingCells = 0;
        int placeholderCells = 0;
        bool anyUnreadable = false;
        ReuseRowsByOffset(firstOffset, bytesPerRow, rows);
        for (int r = 0; r < rows; r++)
        {
            long rowStart = firstOffset + (long)r * bytesPerRow;
            int from = r * bytesPerRow;
            int rowLead = r == 0 ? lead : 0;
            int count = Math.Max(rowLead, Math.Min(bytesPerRow, available - from));
            ReadOnlySpan<ByteState> rowStates = states.AsSpan(from, bytesPerRow);
            bool rowLoading = rowStates[rowLead..count].Contains(ByteState.Loading);
            anyUnreadable |= rowStates[rowLead..count].Contains(ByteState.Unreadable);
            RowVisual row = _rows[r];
            if (row.OffsetRowStart != rowStart || !ReferenceEquals(row.OffsetFormatUsed, offsetFormat) || row.OffsetDigits != digits)
            {
                row.SetOffset(rowStart, offsetFormat, _palette);
            }

            row.OffsetShown = _showOffset;
            CellMode mode = !rowLoading ? CellMode.Normal : inGrace ? CellMode.Blank : CellMode.Placeholder;
            if (rowLoading)
            {
                int loading = rowStates[rowLead..count].Count(ByteState.Loading);
                loadingCells += loading;
                placeholderCells += mode == CellMode.Placeholder ? loading : 0;
            }

            // 猶予中で、同じ行の前の内容があればそのまま残す (VIEW-03 の仕様 3)。
            bool keep = mode == CellMode.Blank && row.ContentRowStart == rowStart && row.HasContent;
            if (!keep && row.Update(frame, rowStart, rowLead, count, bytes.AsSpan(from, bytesPerRow), rowStates, marks.AsSpan(from, bytesPerRow),
                matched.AsSpan(from, bytesPerRow), focus.AsSpan(from, bytesPerRow), text.AsSpan(from, bytesPerRow), mode, selStart, selEnd,
                _editor.TopRow + r == cursorRow, _palette, _cellWidth, _rowHeight, MeasureGlyph))
            {
                rebuilt++;
            }

            row.SetTop(r * _rowHeight - _subRowOffset);
            row.Visible = true;
        }

        for (int r = rows; r < _rows.Count; r++)
        {
            _rows[r].Visible = false;
        }

        PlaceCaret(layout, columns);
        UpdateRuler(layout, view, offsetFormat);
        UpdateMarkers();
        if (anyUnreadable && !_unreadableReported)
        {
            // 最初に読み取れない範囲が見つかったときだけ知らせる (VIEW-03 のエラー)。InfoBar は出さない。
            _unreadableReported = true;
            StatusMessageRequested?.Invoke(this, new HexViewStatusMessageEventArgs(
                Services.Loc.Get("HexView_Status_UnreadableFound"), TimeSpan.FromSeconds(5)));
        }

        _diagnostics.RecordRender(Stopwatch.GetElapsedTime(started), rows, rebuilt, loadingCells);
        OnRendered(placeholderCells);
        RaiseAccessibilityChanges();
    }

    private static void FillMarks(ChangeMark[] marks, long offset, long length, long firstOffset, ChangeMark mark)
    {
        long from = Math.Max(0, offset - firstOffset);
        long to = Math.Min(marks.Length, offset + length - firstOffset);
        if (from < to)
        {
            marks.AsSpan((int)from, (int)(to - from)).Fill(mark);
        }
    }

    /// <summary>[readStart, …) の配列を、行の先頭のずれの分だけ後ろにずらして span 個にする。</summary>
    private static bool[] Shifted(bool[] values, int lead, int span)
    {
        if (lead == 0 && values.Length == span)
        {
            return values;
        }

        var result = new bool[span];
        values.AsSpan(0, Math.Min(values.Length, span - lead)).CopyTo(result.AsSpan(lead));
        return result;
    }

    /// <summary>
    /// テキスト列の解読 (VIEW-21、VIEW-22)。マルチバイトの文字コードでは、同期点を探すための読み戻しと末尾の文字の先読みを付けて読む。
    /// </summary>
    private TextCell[] DecodeText(DocumentSnapshot snapshot, ViewSettings view, long readStart, int lead, int span, int available)
    {
        var cells = new TextCell[span];
        if (!view.ShowTextColumn || available <= lead)
        {
            return cells;
        }

        TextEncoding encoding = _editor!.TextEncoding;
        int windowLength = available - lead;
        int back = (int)Math.Min(readStart, TextCellDecoder.LookbackFor(encoding));
        int ahead = encoding.Kind == TextEncodingKind.SingleByte ? 0 : TextCellDecoder.Lookahead;
        long dataStart = readStart - back;
        int length = back + windowLength + ahead;
        byte[] data = new byte[length];
        var dataStates = new ByteState[length];
        int read = snapshot.ReadForDisplay(dataStart, data, dataStates);
        TextCellDecoder.Decode(encoding, data.AsSpan(0, read), dataStart, readStart, cells.AsSpan(lead, windowLength),
            dataStates.AsSpan(0, read), view.Utf16Phase, view.Utf32Phase);
        return cells;
    }

    /// <summary>描画のたびに呼ぶ (テスト用のビルドで、仮表示 `··` を描いたフレームを数える)。</summary>
    partial void OnRendered(int placeholderCells);

    /// <summary>
    /// スクロールしたとき、同じ行 (先頭オフセット) を描いていた要素をその行に回す。内容が同じ行は作り直さずに位置だけ変わる
    /// (VIEW-04 の仕様 3・5)。
    /// </summary>
    private void ReuseRowsByOffset(long firstOffset, int bytesPerRow, int rows)
    {
        var byStart = new Dictionary<long, RowVisual>(_rows.Count);
        foreach (RowVisual row in _rows)
        {
            if (row.ContentRowStart != long.MinValue)
            {
                byStart.TryAdd(row.ContentRowStart, row);
            }
        }

        var ordered = new RowVisual?[_rows.Count];
        var used = new HashSet<RowVisual>();
        for (int r = 0; r < rows && r < ordered.Length; r++)
        {
            if (byStart.TryGetValue(firstOffset + (long)r * bytesPerRow, out RowVisual? match) && used.Add(match))
            {
                ordered[r] = match;
            }
        }

        var free = new Queue<RowVisual>(_rows.Where(v => !used.Contains(v)));
        for (int r = 0; r < ordered.Length; r++)
        {
            ordered[r] ??= free.Dequeue();
        }

        for (int r = 0; r < ordered.Length; r++)
        {
            _rows[r] = ordered[r]!;
        }
    }

    private void EnsureRowCount(int count)
    {
        while (_rows.Count < count)
        {
            var row = new RowVisual(_font, _fontSize, _rowHeight, _characterSpacing);
            _rows.Add(row);
            OffsetHost.Children.Add(row.Offset);
            RowsLayer.Children.Add(row.Container);
        }
    }

    /// <summary>オフセット列の幅が変わったときに、内容の領域の位置と横スクロールバーを決め直す。</summary>
    private void UpdateColumnsLayout()
    {
        double contentLeft = ContentLeft;
        ContentViewport.Margin = new Thickness(contentLeft, 0, 0, 0);
        double width = Math.Max(0, Surface.ActualWidth - contentLeft);
        double height = Math.Max(0, Surface.ActualHeight);
        ContentClip.Rect = new Windows.Foundation.Rect(0, 0, width, height);
        OffsetHost.Margin = new Thickness(LeftPadding, 0, 0, 0);
        OffsetHost.Visibility = _showOffset ? Visibility.Visible : Visibility.Collapsed;
        LayoutRuler();
        UpdateHorizontalBar();
    }

    /// <summary>オフセット列 (と後ろの 2 文字の空白) の文字数。オフセット列を表示しないときは 0 (VIEW-16)。</summary>
    internal int OffsetChars => _showOffset ? _digits + RowFormat.ColumnGap : 0;

    /// <summary>内容 (Hex 列・テキスト列) の左端 (Surface の座標)。オフセット列の後ろに 2 文字分の空白をあける。</summary>
    internal double ContentLeft => LeftPadding + OffsetChars * _cellWidth;

    /// <summary>内容の全体の幅。</summary>
    private double ContentWidth => (_format.LineLength + 1) * _cellWidth;

    private void PlaceCaret(HexLayout layout, RowColumns columns)
    {
        long row = layout.RowOf(_editor!.Cursor) - _editor.TopRow;
        bool visible = row >= 0 && row <= _editor.VisibleRows + 1;
        Caret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        bool both = columns.ShowHex && columns.ShowText;
        SecondaryCaret.Visibility = visible && both ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            CompositionBox.Visibility = Visibility.Collapsed;
            return;
        }

        int column = layout.ColumnOf(_editor.Cursor);
        double y = row * _rowHeight - _subRowOffset;
        double hexX = columns.HexIndex(column) * _cellWidth;
        double textX = columns.TextIndex(column) * _cellWidth;
        bool hexActive = _editor.ActiveColumn == ActiveColumn.Hex;
        double activeX = hexActive ? hexX + (_editor.LowNibble ? _cellWidth : 0) : textX;

        // もう一方の列の対応位置は枠で示す (VIEW-06 の仕様 4)。テキスト列ではそのバイトが属する文字のセル全体を囲む (VIEW-22)。
        (double textLeft, double textWidth) = TextCharacterRange(layout, columns, _editor.Cursor);
        SetRect(SecondaryCaret, hexActive ? textLeft : hexX, y, hexActive ? textWidth : _cellWidth * 2, _rowHeight);

        // 上書きモードは塗りつぶしの帯、挿入モードは縦棒 (VIEW-06 の仕様 3。色だけで区別しない)。フォーカスがなければ枠 (VIEW-01 の仕様 13)。
        if (_editor.InsertMode)
        {
            Caret.Fill = _palette!.Caret;
            Caret.Stroke = null;
            SetCaretOpacity(1);
            SetRect(Caret, activeX - 1, y, 2, _rowHeight);
        }
        else if (_focused)
        {
            // 帯は文字が読めるよう半透明にする。
            Caret.Fill = _palette!.Caret;
            Caret.Stroke = null;
            SetCaretOpacity(BandOpacity);
            SetRect(Caret, activeX, y, _cellWidth, _rowHeight);
        }
        else
        {
            Caret.Fill = null;
            Caret.Stroke = _palette!.Caret;
            Caret.StrokeThickness = 1;
            SetCaretOpacity(1);
            SetRect(Caret, activeX, y, _cellWidth, _rowHeight);
        }

        PlaceComposition(activeX, y);
    }

    /// <summary>上書きモードの帯の不透明度。</summary>
    private const double BandOpacity = 0.4;

    private double _caretOpacity = 1;

    /// <summary>カーソルの不透明度を変える。点滅で消えている間は消えたままにする。</summary>
    private void SetCaretOpacity(double opacity)
    {
        bool hidden = Caret.Opacity == 0 && _blinkTimer.IsRunning;
        _caretOpacity = opacity;
        Caret.Opacity = hidden ? 0 : opacity;
    }

    /// <summary>テキスト列で、オフセットのバイトが属する文字のセルの範囲 (VIEW-06 の仕様 4、VIEW-22 の仕様 9)。</summary>
    private (double Left, double Width) TextCharacterRange(HexLayout layout, RowColumns columns, long offset)
    {
        int column = layout.ColumnOf(offset);
        double left = columns.TextIndex(column) * _cellWidth;
        long r = layout.RowOf(offset) - _editor!.TopRow;
        if (r < 0 || r >= _rows.Count || !_rows[(int)r].Visible)
        {
            return (left, _cellWidth);
        }

        TextCell cell = _rows[(int)r].TextAt(column);
        if (cell.Kind is not (TextCellKind.Char or TextCellKind.Continuation) || cell.Span <= 1 || cell.Offset < 0)
        {
            return (left, _cellWidth);
        }

        long rowStart = layout.RowStart(layout.RowOf(offset));
        int first = (int)Math.Max(0, cell.Offset - rowStart);
        int last = (int)Math.Min(columns.BytesPerRow - 1, cell.Offset + cell.Span - 1 - rowStart);
        return (columns.TextIndex(first) * _cellWidth, (last - first + 1) * _cellWidth);
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = width;
        element.Height = height;
    }

    private void OnFocusChanged(bool focused)
    {
        // GotFocus / LostFocus は子要素からも上がってくるため、実際の状態で判断する。
        bool now = focused && FocusState != FocusState.Unfocused;

        _focused = now;
        FocusFrameOuter.Visibility = FocusFrameInner.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
        if (now)
        {
            RestartBlink();
            TextInputFocusEnter();
        }
        else
        {
            _blinkTimer.Stop();
            Caret.Opacity = 1;
            TextInputFocusLeave();
        }

        QueueRender();
    }

    // ---- スクロール (VIEW-02、VIEW-28) ----

    private void UpdateVisibleRows()
    {
        if (_editor is not null && _rowHeight > 0 && Surface.ActualHeight > 0)
        {
            _editor.VisibleRows = Math.Max(1, (int)(Surface.ActualHeight / _rowHeight));
        }
    }

    private void UpdateScrollBar()
    {
        _updatingScrollBar = true;
        try
        {
            if (_editor is null)
            {
                VerticalBar.Maximum = 0;
                return;
            }

            long maxTop = _editor.Layout.MaxTopRow(_editor.VisibleRows);
            long scale = ScrollMapping.Scale(maxTop);
            VerticalBar.Minimum = 0;
            VerticalBar.Maximum = scale;
            VerticalBar.SmallChange = 1;
            VerticalBar.LargeChange = Math.Max(1, _editor.VisibleRows - 1);
            VerticalBar.ViewportSize = scale == 0
                ? 1
                : Math.Max(1, (double)scale * _editor.VisibleRows / Math.Max(1, _editor.Layout.TotalRows));
            VerticalBar.Value = ScrollMapping.ToValue(_editor.TopRow, maxTop);
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    private void UpdateHorizontalBar()
    {
        double viewport = Math.Max(0, Surface.ActualWidth - ContentLeft);
        double max = Math.Max(0, ContentWidth - viewport);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, max);
        ContentShift.X = -_horizontalOffset;
        RulerShift.X = -_horizontalOffset;

        // 横スクロールバーは表示部分の幅が足りないときだけ出す (VIEW-28 の仕様 5)。
        Visibility visibility = max > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        if (HorizontalBar.Visibility != visibility)
        {
            HorizontalBar.Visibility = visibility;
        }

        _updatingScrollBar = true;
        try
        {
            HorizontalBar.Minimum = 0;
            HorizontalBar.Maximum = max;
            HorizontalBar.ViewportSize = Math.Max(1, viewport);
            HorizontalBar.SmallChange = _cellWidth * 3;
            HorizontalBar.LargeChange = Math.Max(_cellWidth, viewport - _cellWidth * 3);
            HorizontalBar.Value = _horizontalOffset;
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    private void SetHorizontalOffset(double value)
    {
        double viewport = Math.Max(0, Surface.ActualWidth - ContentLeft);
        double max = Math.Max(0, ContentWidth - viewport);
        value = Math.Clamp(value, 0, max);
        if (value != _horizontalOffset)
        {
            _horizontalOffset = value;
            UpdateHorizontalBar();
        }
    }

    /// <summary>カーソルのセルが横に見えていなければ、入る最小の横スクロールをする (VIEW-34 の仕様 4)。</summary>
    private void EnsureCaretHorizontallyVisible()
    {
        if (_editor is null || HorizontalBar.Visibility != Visibility.Visible)
        {
            return;
        }

        RowColumns columns = Columns;
        int column = _editor.Layout.ColumnOf(_editor.Cursor);
        double left = (_editor.ActiveColumn == ActiveColumn.Hex ? columns.HexIndex(column) : columns.TextIndex(column)) * _cellWidth;
        double right = left + _cellWidth * (_editor.ActiveColumn == ActiveColumn.Hex ? 2 : 1);
        double viewport = Math.Max(0, Surface.ActualWidth - ContentLeft);
        if (left < _horizontalOffset)
        {
            SetHorizontalOffset(left);
        }
        else if (right > _horizontalOffset + viewport)
        {
            SetHorizontalOffset(right - viewport);
        }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SurfaceClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        UpdateColumnsLayout();
        UpdateVisibleRows();
        UpdateScrollBar();
        QueueAutoFit();
        QueueRender();
    }

    private void VerticalBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMarkers();

    // ---- 更新 ----

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        // 自分のスクロール (ホイール・タッチ) 以外で一番上の行が変わったら、行内のずれを捨てる。
        if (!_scrollingByPixels && _editor.TopRow != _lastTopRow)
        {
            _subRowOffset = 0;
        }

        _lastTopRow = _editor.TopRow;
        if (_editor.Cursor != _lastCursor)
        {
            _lastCursor = _editor.Cursor;
            EnsureCaretHorizontallyVisible();
        }

        EditorSnapshot state = EditorSnapshot.Of(_editor);
        OnEditorStateChanged(_lastState, state);
        _lastState = state;
        if (_lastView != _editor.View)
        {
            _lastView = _editor.View;
            QueueAutoFit();
        }

        UpdateScrollBar();
        QueueRender();
    }

    private ViewSettings? _lastView;

    /// <summary>データの読み込みが終わった (スレッドプールから)。UI スレッドで 1 回だけ描き直す (変わった行だけ作り直される)。</summary>
    private void OnDataLoaded(object? sender, EventArgs e) => _uiQueue.TryEnqueue(QueueRender);

    private void RestartBlink()
    {
        Caret.Opacity = _caretOpacity;
        _blinkTimer.Stop();
        if (CaretBlinkMilliseconds() > 0 && _focused)
        {
            _blinkTimer.Start();
        }
    }

    /// <summary>カーソルの点滅間隔 (VIEW-01 の仕様 13。Windows の設定に従い、点滅しない設定なら 0)。</summary>
    private static int CaretBlinkMilliseconds()
    {
        uint t = GetCaretBlinkTime();
        return t is 0 or uint.MaxValue ? 0 : (int)t;
    }

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    /// <summary>ビューの状態のうち、読み上げや UI オートメーションのイベントに使うもの。</summary>
    private readonly record struct EditorSnapshot(long Cursor, bool LowNibble, ActiveColumn Column, long SelectionStart, long SelectionLength,
        bool InsertMode, bool ReadOnly, long Length)
    {
        public static EditorSnapshot Of(EditorState e) =>
            new(e.Cursor, e.LowNibble, e.ActiveColumn, e.SelectionStart, e.SelectionLength, e.InsertMode, e.ReadOnly, e.Document.Length);
    }
}

/// <summary>Hex ビューから要求するエディタの範囲のコマンド。</summary>
public enum EditorCommand
{
    Copy,
    Cut,
    Paste,
    PasteOverwrite,
    SelectAll,
}

/// <summary>F6 / Shift+F6 (UI-52 の仕様 1)。</summary>
public sealed class HexViewFocusRegionEventArgs(bool forward) : EventArgs
{
    /// <summary>true なら次の領域 (F6)、false なら前の領域 (Shift+F6)。</summary>
    public bool Forward { get; } = forward;

    /// <summary>ウィンドウがフォーカスを移したら true にする。false のままならコントロールが次の要素へ移す。</summary>
    public bool Handled { get; set; }
}

/// <summary>ステータスバーに一時的に出す文。</summary>
public sealed class HexViewStatusMessageEventArgs(string message, TimeSpan duration) : EventArgs
{
    public string Message { get; } = message;

    /// <summary>表示しておく時間。</summary>
    public TimeSpan Duration { get; } = duration;
}
