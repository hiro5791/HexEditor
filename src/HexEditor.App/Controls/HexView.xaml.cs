using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Windows.UI.ViewManagement;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビュー (VIEW-01〜VIEW-04)。見えている行の数だけ行の要素を用意して使い回し、ファイルの大きさに関係なく
/// 同じ手間で描く。カーソル・選択・入力の規則は UI に依存しない <see cref="EditorState"/> が持ち、このコントロールは
/// 描画と入力の振り分けだけを行う。
/// <para>
/// ファイルの分け方: 描画 (このファイル)、入力 (HexView.Input.cs)、文字入力 TSF (HexView.TextInput.cs)、
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
    private int _digits = 8;
    private int _bytesPerRow;

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
        _blinkTimer.Tick += (_, _) => Caret.Opacity = Caret.Opacity > 0 ? 0 : 1;
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
            }

            UpdateScrollBar();
            QueueRender();
        }
    }

    /// <summary>フォント名 (VIEW-01 の仕様 3。設定から変える)。</summary>
    public string HexFontFamily
    {
        get => _fontFamilyName;
        set
        {
            _fontFamilyName = string.IsNullOrWhiteSpace(value) ? DefaultFontFamily : value;
            _font = new FontFamily(_fontFamilyName);
            RemeasureAndRender();
        }
    }

    /// <summary>フォントの大きさ (表示倍率 100%・文字サイズ 100% のときの epx。VIEW-01 の仕様 3)。</summary>
    public double HexFontSize
    {
        get => _baseFontSize;
        set
        {
            _baseFontSize = Math.Clamp(value, 4, 200);
            RemeasureAndRender();
        }
    }

    /// <summary>Hex ビューのズームの倍率 (VIEW-43。Windows の文字サイズとは掛け算)。</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.25, 8);
            RemeasureAndRender();
        }
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

    // ---- 文字の大きさ・表示倍率 (VIEW-01 の仕様 4・11、VIEW-41 の仕様 4) ----

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
        QueueRender();
    }

    /// <summary>
    /// セル幅と行の高さを `0` の送り幅とフォントの高さから求め、物理ピクセルに切り上げる (VIEW-01 の仕様 4)。
    /// 文字サイズの設定は TextBlock に任せず、ここで大きさに掛ける (セル幅の計算と描画を一致させるため)。
    /// </summary>
    private void MeasureCell()
    {
        double scale = _rasterizationScale > 0 ? _rasterizationScale : 1;
        _fontSize = _baseFontSize * TextScaleFactor * _zoom;
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
        _rowHeight = Math.Ceiling(probe.DesiredSize.Height * 1.2 * scale) / scale;
        CompositionText.FontFamily = _font;
        CompositionText.FontSize = _fontSize;
        CompositionText.CharacterSpacing = _characterSpacing;
        foreach (RowVisual row in _rows)
        {
            row.ApplyFont(_font, _fontSize, _rowHeight, _characterSpacing);
        }

        InvalidateRows();
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
        _palette = Palette.Load(this);
        _paletteVersion++;
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
        int bytesPerRow = layout.BytesPerRow;
        int digits = layout.MaxCursor > uint.MaxValue ? 16 : 8;
        if (digits != _digits || bytesPerRow != _bytesPerRow)
        {
            _digits = digits;
            _bytesPerRow = bytesPerRow;
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

        long firstOffset = layout.RowStart(_editor.TopRow);
        int span = rows * bytesPerRow;
        byte[] bytes = new byte[span];
        var states = new ByteState[span];
        DocumentSnapshot snapshot = _editor.Document.Current;
        int available = snapshot.ReadForDisplay(firstOffset, bytes, states);

        // 変更されたバイト (VIEW-15)。色に加えて下線でも示す。
        var modified = new bool[span];
        foreach ((long offset, long length) in snapshot.EnumerateModifiedRanges(firstOffset, available))
        {
            modified.AsSpan((int)(offset - firstOffset), (int)length).Fill(true);
        }

        long now = Stopwatch.GetTimestamp();
        bool anyLoading = states.AsSpan(0, available).Contains(ByteState.Loading);
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

        bool[] matched = ComputeMatched(snapshot, firstOffset, span);
        var columns = new RowColumns(bytesPerRow);
        long selStart = _editor.SelectionStart;
        long selEnd = selStart + _editor.SelectionLength;
        var frame = new RowFrame(columns, _editor.ActiveColumn, _paletteVersion, _editor.TextEncoding);
        int rebuilt = 0;
        int loadingCells = 0;
        int placeholderCells = 0;
        bool anyUnreadable = false;
        ReuseRowsByOffset(firstOffset, bytesPerRow, rows);
        for (int r = 0; r < rows; r++)
        {
            long rowStart = firstOffset + (long)r * bytesPerRow;
            int from = r * bytesPerRow;
            int count = Math.Max(0, Math.Min(bytesPerRow, available - from));
            ReadOnlySpan<ByteState> rowStates = states.AsSpan(from, bytesPerRow);
            bool rowLoading = rowStates[..count].Contains(ByteState.Loading);
            anyUnreadable |= rowStates[..count].Contains(ByteState.Unreadable);
            RowVisual row = _rows[r];
            if (row.OffsetRowStart != rowStart || row.OffsetDigits != digits)
            {
                row.SetOffset(rowStart, digits, _palette);
            }

            CellMode mode = !rowLoading ? CellMode.Normal : inGrace ? CellMode.Blank : CellMode.Placeholder;
            if (rowLoading)
            {
                int loading = rowStates[..count].Count(ByteState.Loading);
                loadingCells += loading;
                placeholderCells += mode == CellMode.Placeholder ? loading : 0;
            }

            // 猶予中で、同じ行の前の内容があればそのまま残す (VIEW-03 の仕様 3)。
            bool keep = mode == CellMode.Blank && row.ContentRowStart == rowStart && row.HasContent;
            if (!keep && row.Update(frame, rowStart, count, bytes.AsSpan(from, bytesPerRow), rowStates, modified.AsSpan(from, bytesPerRow),
                mode, selStart, selEnd, _palette, _cellWidth, _rowHeight, matched.AsSpan(from, bytesPerRow)))
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
            if (row.ContentRowStart >= 0)
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
        UpdateHorizontalBar();
    }

    /// <summary>内容 (Hex 列・テキスト列) の左端 (Surface の座標)。オフセット列の後ろに 2 文字分の空白をあける。</summary>
    internal double ContentLeft => LeftPadding + (_digits + 2) * _cellWidth;

    /// <summary>内容の全体の幅。</summary>
    private double ContentWidth => (new RowColumns(Math.Max(1, _bytesPerRow)).LineLength + 1) * _cellWidth;

    private void PlaceCaret(HexLayout layout, RowColumns columns)
    {
        long row = layout.RowOf(_editor!.Cursor) - _editor.TopRow;
        bool visible = row >= 0 && row <= _editor.VisibleRows + 1;
        Caret.Visibility = SecondaryCaret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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

        // もう一方の列の対応位置は枠で示す (VIEW-06)。
        SetRect(SecondaryCaret, hexActive ? textX : hexX, y, hexActive ? _cellWidth : _cellWidth * 2, _rowHeight);

        // 上書きモードは枠、挿入モードは縦線 (EDIT-10 の仕様 3。色だけで区別しない)。フォーカスがなければ細い枠 (VIEW-01 の仕様 13)。
        if (_editor.InsertMode && _focused)
        {
            Caret.Fill = _palette!.Caret;
            Caret.Stroke = null;
            SetRect(Caret, activeX - 1, y, 2, _rowHeight);
        }
        else
        {
            Caret.Fill = null;
            Caret.Stroke = _palette!.Caret;
            Caret.StrokeThickness = _focused ? 2 : 1;
            SetRect(Caret, activeX, y, _cellWidth, _rowHeight);
        }

        PlaceComposition(activeX, y);
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

        var columns = new RowColumns(_editor.BytesPerRow);
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
        UpdateScrollBar();
        QueueRender();
    }

    /// <summary>データの読み込みが終わった (スレッドプールから)。UI スレッドで 1 回だけ描き直す (変わった行だけ作り直される)。</summary>
    private void OnDataLoaded(object? sender, EventArgs e) => _uiQueue.TryEnqueue(QueueRender);

    private void RestartBlink()
    {
        Caret.Opacity = 1;
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

    // ---- 行の要素 ----

    /// <summary>セルの描き方。</summary>
    internal enum CellKind
    {
        Normal,
        Modified,
        Loading,
        Unreadable,
        Empty,
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

    /// <summary>1 行の文字列の中での各列の位置 (内容の領域の左端からの文字数)。</summary>
    internal readonly record struct RowColumns(int BytesPerRow)
    {
        /// <summary>c 番目のバイトの Hex 列の先頭の文字位置。中央に 1 文字分の区切りを入れる。</summary>
        public int HexIndex(int c) => c * 3 + (c >= BytesPerRow / 2 && BytesPerRow > 1 ? 1 : 0);

        /// <summary>テキスト列の c 番目の文字位置。Hex 列との間は 2 文字あける (VIEW-01 の仕様 1)。</summary>
        public int TextIndex(int c) => BytesPerRow * 3 + (BytesPerRow > 1 ? 1 : 0) + 1 + c;

        public int LineLength => TextIndex(BytesPerRow);
    }

    /// <summary>1 フレームの中で全行に共通の条件 (変われば全行を作り直す)。</summary>
    /// <summary>行の描き方を決める値。テキスト列の文字コード (VIEW-21) はドキュメントのものを使い、入力・コピーと揃える。</summary>
    internal readonly record struct RowFrame(RowColumns Columns, ActiveColumn Active, int PaletteVersion, TextEncoding Encoding);

    /// <summary>
    /// 1 行分の要素。前回描いた内容を覚えておき、変わったときだけ Run を作り直す (VIEW-04 の仕様 3・5)。
    /// </summary>
    internal sealed class RowVisual
    {
        private byte[] _bytes = [];
        private ByteState[] _states = [];
        private bool[] _modified = [];
        private bool[] _matched = [];

        /// <summary>検索の一致の範囲が前回と同じか (行を作り直すかの判定)。</summary>
        private bool SameMatches(ReadOnlySpan<bool> matched, int count)
        {
            if (matched.IsEmpty)
            {
                return !_matched.AsSpan(0, Math.Min(count, _matched.Length)).Contains(true);
            }

            return matched[..count].SequenceEqual(_matched.AsSpan(0, count));
        }

        /// <summary>
        /// 検索の一致の強調 (FIND-04 の仕様 9、FIND-12 の仕様 4)。選択範囲より下の層に塗り、選択範囲・変更の色と区別できる色にする。
        /// </summary>
        private void HighlightMatches(RowColumns columns, Palette palette)
        {
            int count = Count;
            var hex = new TextHighlighter { Background = palette.Match, Foreground = palette.MatchText };
            var text = new TextHighlighter { Background = palette.Match, Foreground = palette.MatchText };
            for (int c = 0; c < count; c++)
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

                int hexStart = columns.HexIndex(c);
                hex.Ranges.Add(new TextRange { StartIndex = hexStart, Length = columns.HexIndex(end) + 2 - hexStart });
                text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(c), Length = end - c + 1 });
                c = end;
            }

            if (hex.Ranges.Count > 0)
            {
                Content.TextHighlighters.Add(hex);
                Content.TextHighlighters.Add(text);
            }
        }
        private int _count = -1;
        private CellMode _mode;
        private long _selFrom;
        private long _selTo;
        private RowFrame _frame;
        private readonly List<Path> _hatches = [];

        public RowVisual(FontFamily font, double fontSize, double rowHeight, int spacing)
        {
            Offset = CreateText();
            Content = CreateText();
            Container = new Canvas();
            Container.Children.Add(Content);
            ApplyFont(font, fontSize, rowHeight, spacing);
        }

        public TextBlock Offset { get; }

        public TextBlock Content { get; }

        /// <summary>内容の TextBlock と斜線の模様を入れる。</summary>
        public Canvas Container { get; }

        public long OffsetRowStart { get; private set; } = -1;

        public int OffsetDigits { get; private set; }

        public long ContentRowStart { get; private set; } = -1;

        public bool HasContent => _count >= 0;

        /// <summary>画面と同じ書式の行の文字列 (オフセット列を除く。UI オートメーションの Text パターンに渡す。VIEW-41 の仕様 6)。</summary>
        public string ContentText { get; private set; } = string.Empty;

        public string OffsetText => Offset.Text;

        /// <summary>表示中のバイト数。</summary>
        public int Count => Math.Max(0, _count);

        public ReadOnlySpan<byte> Bytes => _bytes;

        public ReadOnlySpan<ByteState> States => _states;

        public ReadOnlySpan<bool> Modified => _modified;

        public bool IsBlank => _mode == CellMode.Blank;

        public double Top { get; private set; }

        public bool Visible
        {
            get => Offset.Visibility == Visibility.Visible;
            set
            {
                Visibility v = value ? Visibility.Visible : Visibility.Collapsed;
                if (Offset.Visibility != v)
                {
                    Offset.Visibility = v;
                    Container.Visibility = v;
                }
            }
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
        }

        public void Invalidate()
        {
            _count = -1;
            OffsetRowStart = -1;
            ContentRowStart = -1;
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

        public void SetOffset(long rowStart, int digits, Palette palette)
        {
            OffsetRowStart = rowStart;
            OffsetDigits = digits;
            Offset.Text = rowStart.ToString(digits == 16 ? "X16" : "X8");
            Offset.Foreground = palette.OffsetText;
        }

        /// <summary>内容を更新する。前回と同じなら何もしない。作り直したら true。</summary>
        public bool Update(RowFrame frame, long rowStart, int count, ReadOnlySpan<byte> bytes, ReadOnlySpan<ByteState> states,
            ReadOnlySpan<bool> modified, CellMode mode, long selStart, long selEnd, Palette palette, double cellWidth, double rowHeight,
            ReadOnlySpan<bool> matched = default)
        {
            long selFrom = Math.Max(selStart, rowStart) - rowStart;
            long selTo = Math.Min(selEnd, rowStart + count) - rowStart;
            if (selFrom >= selTo)
            {
                selFrom = selTo = 0;
            }

            if (_count == count && ContentRowStart == rowStart && _mode == mode && _frame == frame && _selFrom == selFrom && _selTo == selTo
                && bytes[..count].SequenceEqual(_bytes.AsSpan(0, count)) && states[..count].SequenceEqual(_states.AsSpan(0, count))
                && modified[..count].SequenceEqual(_modified.AsSpan(0, count))
                && SameMatches(matched, count))
            {
                return false;
            }

            ContentRowStart = rowStart;
            _count = count;
            _mode = mode;
            _frame = frame;
            _selFrom = selFrom;
            _selTo = selTo;
            if (_bytes.Length < bytes.Length)
            {
                _bytes = new byte[bytes.Length];
                _states = new ByteState[bytes.Length];
                _modified = new bool[bytes.Length];
                _matched = new bool[bytes.Length];
            }

            bytes[..count].CopyTo(_bytes);
            states[..count].CopyTo(_states);
            modified[..count].CopyTo(_modified);
            _matched.AsSpan(0, count).Clear();
            if (!matched.IsEmpty)
            {
                matched[..count].CopyTo(_matched);
            }

            Fill(frame.Columns, palette);
            Highlight(frame, palette);
            UpdateHatches(frame.Columns, palette, cellWidth, rowHeight);
            return true;
        }

        public CellKind KindAt(int c)
        {
            if (c >= Count)
            {
                return CellKind.Empty;
            }

            return _states[c] switch
            {
                ByteState.Loading => CellKind.Loading,
                ByteState.Unreadable => CellKind.Unreadable,
                _ => _modified[c] ? CellKind.Modified : CellKind.Normal,
            };
        }

        /// <summary>Hex 列のセルの表示 (VIEW-03 の仮表示を含む)。</summary>
        public string HexCellText(int c) => KindAt(c) switch
        {
            CellKind.Empty => "  ",
            CellKind.Loading => _mode == CellMode.Blank ? "  " : "··",
            CellKind.Unreadable => "??",
            _ => HexStrings[_bytes[c]],
        };

        /// <summary>テキスト列のセルの表示。</summary>
        public string TextCellText(int c) => KindAt(c) switch
        {
            CellKind.Empty => string.Empty,
            CellKind.Loading or CellKind.Unreadable => " ",
            _ => _frame.Encoding.DisplayChar(_bytes[c]).ToString(),
        };

        private static TextBlock CreateText()
        {
            var t = new TextBlock
            {
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap,

                // 文字サイズの設定は MeasureCell で大きさに掛けてある (VIEW-41 の仕様 4)。二重に掛けない。
                IsTextScaleFactorEnabled = false,
                TextLineBounds = TextLineBounds.Full,
            };

            // 行の文字列はビューの UI オートメーション (Text・Grid パターン) で返すため、個々の TextBlock は木に出さない。
            AutomationProperties.SetAccessibilityView(t, AccessibilityView.Raw);
            return t;
        }

        /// <summary>1 行の文字列を、色の違う区間ごとの Run に分けて作る。</summary>
        private void Fill(RowColumns columns, Palette palette)
        {
            Content.Inlines.Clear();
            var builder = new RunBuilder(Content, palette);
            int count = Count;

            // Hex 列
            for (int c = 0; c < columns.BytesPerRow; c++)
            {
                builder.Append(HexCellText(c), KindAt(c));
                builder.Append(c == columns.BytesPerRow / 2 - 1 && columns.BytesPerRow > 1 ? "  " : " ", palette.Text);
            }

            builder.Append(" ", palette.Text);

            // テキスト列
            for (int c = 0; c < count; c++)
            {
                builder.Append(TextCellText(c), KindAt(c));
            }

            ContentText = builder.Flush();
        }

        private void Highlight(RowFrame frame, Palette palette)
        {
            Content.TextHighlighters.Clear();
            HighlightMatches(frame.Columns, palette);
            if (_selFrom >= _selTo)
            {
                return;
            }

            RowColumns columns = frame.Columns;
            int first = (int)_selFrom;
            int last = (int)_selTo - 1;
            bool hexActive = frame.Active == ActiveColumn.Hex;

            // 操作中でない列の選択は薄い色で塗る (EDIT-01 の画面)。
            var hex = new TextHighlighter
            {
                Background = hexActive ? palette.Selection : palette.SelectionInactive,
                Foreground = hexActive ? palette.SelectionText : palette.SelectionInactiveText,
            };
            int hexStart = columns.HexIndex(first);
            hex.Ranges.Add(new TextRange { StartIndex = hexStart, Length = columns.HexIndex(last) + 2 - hexStart });
            var text = new TextHighlighter
            {
                Background = hexActive ? palette.SelectionInactive : palette.Selection,
                Foreground = hexActive ? palette.SelectionInactiveText : palette.SelectionText,
            };
            text.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(first), Length = last - first + 1 });
            Content.TextHighlighters.Add(hex);
            Content.TextHighlighters.Add(text);
        }

        /// <summary>
        /// 読み取れない範囲の斜線の模様 (VIEW-03 の仕様 5)。色だけに頼らないため必ず描く。ほかの層の背景の上に描く (VIEW-17 の仕様 7)。
        /// </summary>
        private void UpdateHatches(RowColumns columns, Palette palette, double cellWidth, double rowHeight)
        {
            int used = 0;
            int count = Count;
            for (int c = 0; c < count; c++)
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

                double hexLeft = columns.HexIndex(c) * cellWidth;
                double hexRight = (columns.HexIndex(end) + 2) * cellWidth;
                PlaceHatch(used++, hexLeft, hexRight - hexLeft, rowHeight, palette);
                PlaceHatch(used++, columns.TextIndex(c) * cellWidth, (end - c + 1) * cellWidth, rowHeight, palette);
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


    /// <summary>同じ見た目の文字をまとめて Run にする。</summary>
    private sealed class RunBuilder(TextBlock row, Palette palette)
    {
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _text = new();
        private Brush? _brush;
        private CellKind _kind;

        public void Append(string text, Brush brush) => Append(text, CellKind.Normal, brush);

        public void Append(string text, CellKind kind) => Append(text, kind, palette.For(kind));

        /// <summary>残りを書き出し、行全体の文字列を返す。</summary>
        public string Flush()
        {
            FlushRun();
            return _line.ToString();
        }

        private void FlushRun()
        {
            if (_text.Length == 0)
            {
                return;
            }

            var run = new Run { Text = _text.ToString(), Foreground = _brush };
            if (_kind == CellKind.Modified)
            {
                run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            }

            row.Inlines.Add(run);
            _text.Clear();
        }

        private void Append(string text, CellKind kind, Brush brush)
        {
            // 空白は色が見えないので、下線のない区間にはそのまま続ける (仮表示の行などで、セルごとに Run が分かれて
            // 描画が重くならないようにする。VIEW-03 の仕様 1・VIEW-04)。
            bool blank = text.Length > 0 && text.AsSpan().TrimStart(' ').IsEmpty;
            if (blank && _text.Length > 0 && _kind != CellKind.Modified)
            {
                _text.Append(text);
                _line.Append(text);
                return;
            }

            if (!ReferenceEquals(brush, _brush) || kind != _kind)
            {
                FlushRun();
                _brush = brush;
                _kind = kind;
            }

            _text.Append(text);
            _line.Append(text);
        }
    }

    /// <summary>
    /// 配色 (VIEW-01 の仕様 10)。XAML の ThemeDictionaries (Light / Dark / HighContrast) から取るため、
    /// テーマとハイコントラストの切り替えに追従する。ハイコントラストではシステム色だけになる。
    /// </summary>
    internal sealed record Palette(Brush Text, Brush OffsetText, Brush Modified, Brush Dim, Brush Selection, Brush SelectionText,
        Brush SelectionInactive, Brush SelectionInactiveText, Brush Caret, Brush Hatch, Brush Background, Brush CursorMarker,
        Brush SearchMarker, Brush Match, Brush MatchText)
    {
        public static Palette Load(HexView view) => new(
            view.ProbeText.Fill,
            view.ProbeOffset.Fill,
            view.ProbeModified.Fill,
            view.ProbeDim.Fill,
            view.ProbeSelection.Fill,
            view.ProbeSelectionText.Fill,
            view.ProbeSelectionInactive.Fill,
            view.ProbeSelectionInactiveText.Fill,
            view.ProbeCaret.Fill,
            view.ProbeHatch.Fill,
            view.ProbeBackground.Fill,
            view.ProbeCursorMarker.Fill,
            view.ProbeSearchMarker.Fill,
            view.ProbeMatch.Fill,
            view.ProbeMatchText.Fill);

        public Brush For(CellKind kind) => kind switch
        {
            CellKind.Modified => Modified,
            CellKind.Loading or CellKind.Empty => Dim,
            _ => Text,
        };
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
