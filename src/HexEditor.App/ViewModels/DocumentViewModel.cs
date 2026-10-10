using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Recovery;
using HexEditor.Core.View;
using System.Globalization;

namespace HexEditor.App.ViewModels;

/// <summary>タブ 1 つ分のドキュメント。</summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    public DocumentViewModel(Document document, string? filePath, string displayName)
    {
        Document = document;
        PrimaryEditor = new EditorState(document);
        FilePath = filePath;
        DisplayName = displayName;
        // ステータスバー・タブの見出しをまとめて更新する (空の名前は全プロパティの変更)。
        _documentChanged = (_, _) => OnPropertyChanged(string.Empty);
        _readOnlyChanged = (_, _) => OnPropertyChanged(string.Empty);
        Document.Changed += _documentChanged;
        Document.ReadOnlyChanged += _readOnlyChanged;

        // カーソルの値は、読み込みが終わってから表示する (読み込みの通知はスレッドプールから来る)。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // マウスのドラッグ中は、ステータスバーの更新を 30 fps に抑える (VIEW-40 の仕様 4)。それ以外はすぐ更新する (50 ms 以内)。
        _statusTimer = queue?.CreateTimer();
        if (_statusTimer is not null)
        {
            _statusTimer.IsRepeating = false;
            _statusTimer.Interval = StatusInterval;
            _statusTimer.Tick += (_, _) => RaiseStatus();
        }

        AttachPane(PrimaryEditor);
        _dataLoaded = (_, _) => queue?.TryEnqueue(() => OnPropertyChanged(nameof(ValueText)));
        Document.DataLoaded += _dataLoaded;
    }

    private readonly EventHandler<Core.Engine.DocumentChangedEventArgs> _documentChanged;
    private readonly EventHandler _readOnlyChanged;
    private readonly EventHandler _dataLoaded;

    /// <summary>
    /// ドキュメントを閉じずに、このビュー (とペイン) のドキュメントのイベントの購読を外す (比較タブの片側、同じドキュメントのほかのビューが残るとき)。
    /// </summary>
    public void DetachFromDocument()
    {
        Document.Changed -= _documentChanged;
        Document.ReadOnlyChanged -= _readOnlyChanged;
        Document.DataLoaded -= _dataLoaded;
        _statusTimer?.Stop();
        foreach (EditorState pane in Panes)
        {
            DetachPane(pane);
            pane.Detach();
        }
    }

    /// <summary>ペインのビューの変化でステータスバーを更新し、<see cref="EditorChanged"/> を出す。</summary>
    private void AttachPane(EditorState editor) => editor.Changed += Pane_Changed;

    private void DetachPane(EditorState editor) => editor.Changed -= Pane_Changed;

    private void Pane_Changed(object? sender, EventArgs e)
    {
        EditorChanged?.Invoke(this, EventArgs.Empty);
        if (!ReferenceEquals(sender, Editor))
        {
            // 操作中でないペインの変化はステータスバーに関係しない。
            return;
        }

        if (Editor.PointerDragging && _statusTimer is not null
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastStatus) < StatusInterval)
        {
            if (!_statusTimer.IsRunning)
            {
                _statusTimer.Start();
            }

            return;
        }

        RaiseStatus();
    }

    /// <summary>
    /// どちらかのペインのビュー (カーソル・選択範囲・スクロール位置・表示設定) が変わった、または操作中のペインが替わった
    /// (インスペクタ・ハッシュのパネルが購読する。VIEW-37)。
    /// </summary>
    public event EventHandler? EditorChanged;

    public Document Document { get; }

    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(1000.0 / 30);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private long _lastStatus;

    private void RaiseStatus()
    {
        _statusTimer?.Stop();
        _lastStatus = System.Diagnostics.Stopwatch.GetTimestamp();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>通知 (文書の範囲の通知をタブの中に出すため。UI-36)。</summary>
    /// <remarks>タブを別のウィンドウに移すと、移した先のウィンドウの通知に替わる (UI-11 の仕様 3)。</remarks>
    public Core.Notifications.NotificationCenter? Notifications { get; set; }

    /// <summary>このドキュメントの復旧用データ (ENG-27)。作れなかった場合は null (編集は続けられる)。</summary>
    public DocumentRecovery? Recovery { get; set; }

    private DocumentSnapshot? _lastRecorded;

    /// <summary>
    /// 前回の書き出しから内容が変わっていれば、書き出す内容を取る (UI スレッドで呼ぶ)。変更がなくなっていれば
    /// 復旧用データを消す (仕様 5)。
    /// </summary>
    public RecoveryCapture? CaptureRecoveryIfChanged()
    {
        if (Recovery is null)
        {
            return null;
        }

        if (!Document.IsModified)
        {
            if (_lastRecorded is not null)
            {
                Recovery.Clear();
                _lastRecorded = null;
            }

            return null;
        }

        if (ReferenceEquals(Document.Current, _lastRecorded))
        {
            return null;
        }

        (string, string, Core.Sources.FileStamp?)? encoded = Encoded is { } e && FilePath is { } encodedPath ? (encodedPath, e.Format, EncodedStamp) : null;
        RecoveryCapture? capture = DocumentRecovery.Capture(Document, Editor.Cursor, Editor.SelectionStart, Editor.SelectionLength, encoded);
        _lastRecorded = capture?.Snapshot ?? _lastRecorded;
        return capture;
    }

    /// <summary>書き出しに失敗したら、次の機会にもう一度書く。</summary>
    public void ForgetRecorded() => _lastRecorded = null;

    /// <summary>保存した: 復旧用データを消す (仕様 5)。</summary>
    public void OnSaved()
    {
        Recovery?.Clear();
        _lastRecorded = null;
    }

    /// <summary>操作中のペインのビュー (分割していなければ <see cref="PrimaryEditor"/>。VIEW-37 の仕様 6)。</summary>
    public EditorState Editor => ActivePane == 1 && SecondaryEditor is { } second ? second : PrimaryEditor;

    /// <summary>保存先のパス。無題のドキュメントでは null。</summary>
    public string? FilePath { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>鍵の記号 (読み取り専用の表示。EDIT-16 の仕様 6)。</summary>
    public const string LockGlyph = "🔒";

    /// <summary>
    /// タブの見出し。読み取り専用なら鍵の記号 (EDIT-16 の仕様 6)、変更があれば ● (色だけに頼らない)、外部で変更された (ENG-19) なら ⚠
    /// (形で区別する) を先頭に付ける。
    /// </summary>
    public string Header => (Document.IsReadOnly ? LockGlyph + " " : string.Empty) + (Document.IsModified ? "● " : string.Empty)
        + (HasExternalChange ? "⚠ " : string.Empty) + TabTitle;

    public bool IsUntitled => FilePath is null;

    public void SetSavedPath(string path)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(ToolTip));
    }

    // ---- ステータスバーの値 (VIEW-40) ----

    private int _hexDigits = StatusFormat.MinHexDigits;

    /// <summary>オフセットの 16 進の桁数。開いている間は増えるときだけ変える (幅が揺れないように。VIEW-19 の仕様 3)。</summary>
    public int HexDigits
    {
        get
        {
            _hexDigits = Math.Max(_hexDigits, StatusFormat.HexDigits(Document.Length));
            return _hexDigits;
        }
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>設定「ニブルの位置を表示」(VIEW-26 の仕様 8)。オンなら、下位ニブルにあるときカーソル位置の後ろに「(下位)」を付ける。</summary>
    public bool ShowNibble
    {
        get => _showNibble;
        set
        {
            if (SetProperty(ref _showNibble, value))
            {
                OnPropertyChanged(nameof(CursorText));
            }
        }
    }

    private bool _showNibble;

    /// <summary>
    /// カーソル位置 (VIEW-40 の仕様 1): 基数と桁数は VIEW-19、ベースアドレスを使うときは <c>@00401F00</c>、基準点があるときは
    /// 「相対 +00000020」(VIEW-20)。16 進の大文字・小文字は VIEW-12。
    /// </summary>
    public string CursorText
    {
        get
        {
            OffsetFormat format = Editor.OffsetFormat;
            string value = format.Status(Editor.Cursor, Culture);
            if (Editor.ReferencePoint is not null)
            {
                value = Loc.Format("Status_Relative", value);
            }

            return Loc.Format("Status_Offset", value
                + (ShowNibble && Editor.ActiveColumn == ActiveColumn.Hex && Editor.LowNibble ? " (" + Loc.Get("HexView_State_LowNibble") + ")" : string.Empty));
        }
    }

    /// <summary>カーソルの値: 「値: 4F (79)」。末尾 (データのない位置) と読み込み中は表示しない。</summary>
    public string ValueText
    {
        get
        {
            if (Document.IsDisposed || Editor.Cursor >= Document.Length)
            {
                return string.Empty;
            }

            Span<byte> value = stackalloc byte[1];
            Span<ByteState> state = stackalloc ByteState[1];
            Document.Current.ReadForDisplay(Editor.Cursor, value, state);
            if (state[0] != ByteState.Valid)
            {
                return string.Empty;
            }

            (string hex, string dec) = StatusFormat.ByteValue(value[0], Culture);
            return Loc.Format("Status_Value", Editor.View.LowercaseHex ? hex.ToLowerInvariant() : hex, dec);
        }
    }

    /// <summary>選択範囲: 「選択: 0x1F00–0x1FFF (長さ 0x100 = 256)」。開始と最後のバイトの閉区間 (VIEW-40 の仕様 3)。</summary>
    public string SelectionText => SpecialSelectionText() ?? (Editor.HasSelection
        ? Loc.Format("Status_SelectionRange", Editor.OffsetFormat.Value(Editor.SelectionStart, Culture),
            Editor.OffsetFormat.Value(Editor.SelectionStart + Editor.SelectionLength - 1, Culture),
            OffsetFormat.Hex(Editor.SelectionLength, Editor.View.LowercaseHex), StatusFormat.Number(Editor.SelectionLength, Culture))
        : string.Empty);

    /// <summary>
    /// マルチ選択「3 個の範囲、計 12 バイト (主要素: 0x30–0x33)」(EDIT-07 の仕様 5)、矩形「矩形 4 行 × 4 バイト (計 16 バイト)」(EDIT-06 の仕様 4)、
    /// マルチカーソル「4 個のカーソル」(EDIT-08 の仕様 8)。単一の選択なら null。
    /// </summary>
    private string? SpecialSelectionText()
    {
        if (Editor.HasMultipleCarets)
        {
            return Loc.Format("Status_Carets", Editor.CaretCount);
        }

        switch (Editor.SelectionKind)
        {
            case SelectionKind.Rectangle when Editor.Rectangle is { } r:
                return Loc.Format("Status_Rectangle", StatusFormat.Number(Editor.SelectedRangeCount, Culture), StatusFormat.Number(r.Width, Culture),
                    StatusFormat.Number(Editor.SelectedByteCount, Culture));
            case SelectionKind.Multiple when Editor.PrimaryRange is { } p:
                return Loc.Format("Status_MultiSelection", Editor.SelectedRangeCount, StatusFormat.Number(Editor.SelectedByteCount, Culture),
                    Editor.OffsetFormat.Value(p.Start, Culture), Editor.OffsetFormat.Value(p.Last, Culture));
            default:
                return null;
        }
    }

    public string SelectionToolTip => Editor.HasSelection
        ? Loc.Format("Status_SelectionTip", Editor.OffsetFormat.Status(Editor.SelectionStart, Culture),
            Editor.OffsetFormat.Status(Editor.SelectionStart + Editor.SelectionLength - 1, Culture),
            OffsetFormat.Hex(Editor.SelectionLength, Editor.View.LowercaseHex), StatusFormat.Number(Editor.SelectionLength, Culture))
        : string.Empty;

    /// <summary>モード。デコードして開いたドキュメントは形式 (「Intel HEX」など。ENG-38 の画面) を前に付ける。</summary>
    public string ModeText => (FileFormatText.Length > 0 ? FileFormatText + " · " : string.Empty) + (Editor.ReadOnly ? LockGlyph + " " + Loc.Get("Status_ReadOnly")
        : !Document.CanResize ? Loc.Get("Status_OverwriteFixed")
        : Editor.InsertMode ? Loc.Get("Status_Insert") : Loc.Get("Status_Overwrite"));

    public string ColumnText => Editor.ActiveColumn == ActiveColumn.Hex ? Loc.Get("Status_ColumnHex") : Loc.Get("Status_ColumnText");

    /// <summary>テキスト列の文字コード (フェーズ 0 は ASCII と ANSI。VIEW-21)。</summary>
    public string EncodingText => Editor.TextEncoding.Name;

    /// <summary>ファイルサイズ: 「サイズ: 1.50 GB (1,610,612,736 バイト)」(VIEW-40 の仕様 2)。</summary>
    public string SizeText => StatusFormat.ShortSize(Document.Length, Culture) is { } size
        ? Loc.Format("Status_Size", size, StatusFormat.Number(Document.Length, Culture))
        : Loc.Format("Status_SizeBytes", StatusFormat.Number(Document.Length, Culture));

    public string ModifiedText => Document.IsModified ? "● " + Loc.Get("Status_Modified") : string.Empty;

    /// <summary>
    /// セクタ / ページ / レコード (VIEW-40 の仕様 1): 「セクタ 15」(ディスク・セクタ形式・区切り線がセクタのとき。VIEW-32 の仕様 6)、
    /// 「ページ 3 / 25,600」(区切り線があるとき。VIEW-33 の仕様 6)、「レコード #1024 +0x04」(レコード表示がオンのとき。VIEW-18 の仕様 7)。
    /// 該当しなければ空 (表示しない)。
    /// </summary>
    public string PositionText
    {
        get
        {
            if (Document.IsDisposed)
            {
                return string.Empty;
            }

            EditorState e = Editor;
            ViewSettings v = e.View;
            var parts = new List<string>();
            long cursor = e.Cursor;
            if (v.Separator is SeparatorKind.Sector || (v.Separator == SeparatorKind.None
                && (v.Radix == OffsetRadix.Sector || Document.Source.LogicalSectorSize > 1)))
            {
                var sectors = new SectionLayout(e.SectorSize, Document.Length);
                parts.Add(Loc.Format("Status_Sector", sectors.IndexOf(cursor).ToString(Culture)));
            }
            else if (v.Separator != SeparatorKind.None && e.SectionLength > 0)
            {
                SectionLayout sections = e.Sections;
                string index = sections.IndexOf(cursor).ToString("N0", Culture);
                string count = sections.Count.ToString("N0", Culture);
                parts.Add(Loc.Format(v.Separator == SeparatorKind.Page ? "Status_Page" : "Status_Section", index, count));
            }

            if (v.RecordView && RecordLayout.For(v) is var records && records.IndexOf(cursor) is { } recordNo && records.WithinOf(cursor) is { } within)
            {
                string position = RecordLayout.WithinText(within, v.Radix, v.LowercaseHex);
                parts.Add(Loc.Format("Status_Record", recordNo.ToString(CultureInfo.InvariantCulture), position));
            }

            return string.Join("  ", parts);
        }
    }

    /// <summary>表示形式 (VIEW-40 の仕様 1): Hex 以外のセルの表示形式のとき <c>int32</c>、<c>float</c> など。Hex なら空。</summary>
    public string FormatText => CellFormatter.StatusName(Editor.View.CellFormat) ?? string.Empty;

    /// <summary>エンディアン (VIEW-40 の仕様 1): <c>LE</c> / <c>BE</c>。逆順表示中は <c>LE (逆順表示)</c>。</summary>
    public string EndianText
    {
        get
        {
            string endian = Editor.View.BigEndian ? "BE" : "LE";
            return RowFormat.For(Editor.View, Editor.BytesPerRow).Reverse ? Loc.Format("Status_ReversedEndian", endian) : endian;
        }
    }

    /// <summary>パスを持たない項目をコピーした一時ファイル (ENG-12 の仕様 2)。閉じるときに消す。</summary>
    public string? TemporaryFile { get; set; }

    /// <summary>
    /// このタブの Hex 表示の倍率 (百分率。UI-08 の仕様 2 の 2〜4)。適用範囲が「今のタブだけ」のときに使う。null なら全タブ共通の倍率。
    /// セッションに保存し、閉じたタブを開き直しても引き継がない。
    /// </summary>
    public int? HexZoom { get; set; }

    /// <summary>閉じる: ドキュメントを解放してから、復旧用データをフォルダごと消す (仕様 5)。</summary>
    public void Dispose()
    {
        PaneSync?.Dispose();

        // 同じドキュメントのビューがほかに残るなら、ドキュメントは閉じない (VIEW-38 の仕様 6)。
        if (LeaveShare())
        {
            DetachFromDocument();
            return;
        }

        Document.Dispose();
        Recovery?.Dispose();
        if (TemporaryFile is not null)
        {
            try
            {
                File.Delete(TemporaryFile);
                Directory.Delete(Path.GetDirectoryName(TemporaryFile)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
