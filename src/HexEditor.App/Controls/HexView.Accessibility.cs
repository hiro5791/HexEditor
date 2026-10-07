using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビューのアクセシビリティ (VIEW-41 の仕様 5・6、UI-50、UI-51)。読み上げ文を作ってオートメーションピアに渡す。
/// </summary>
public sealed partial class HexView
{
    /// <summary>カーソル移動の読み上げを待つ時間 (UI-51 の仕様 3。キーリピート中は読み上げない)。</summary>
    private static readonly TimeSpan CursorAnnounceDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>選択範囲の読み上げを待つ時間 (EDIT-02 の仕様 7)。</summary>
    private static readonly TimeSpan SelectionAnnounceDelay = TimeSpan.FromMilliseconds(500);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cursorAnnounceTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _selectionAnnounceTimer;
    private bool _selectionChangedPending;
    private bool _valueChangedPending;
    private string _lastValue = string.Empty;

    /// <summary>
    /// 文書名 (UI-50 の仕様 2 の Name)。<see cref="AutomationProperties.NameProperty"/> が設定されていればそちらを使う。
    /// </summary>
    public string? DocumentName { get; set; }

    /// <summary>最後に送った読み上げ文 (UI テスト・診断用)。</summary>
    internal string? LastAnnouncement { get; private set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new HexViewAutomationPeer(this);

    private HexViewAutomationPeer? Peer => FrameworkElementAutomationPeer.FromElement(this) as HexViewAutomationPeer;

    /// <summary>ビューの状態が変わった。読み上げとオートメーションのイベントを予約する。</summary>
    private void OnEditorStateChanged(EditorSnapshot before, EditorSnapshot after)
    {
        if (before == default)
        {
            return;
        }

        if (before.InsertMode != after.InsertMode)
        {
            // 上書き / 挿入の切り替え (EDIT-10 の仕様 7)。
            Announce(Loc.Get(after.InsertMode ? "HexView_Announce_InsertMode" : "HexView_Announce_OverwriteMode"), "HexViewMode");
        }

        if (before.ReadOnly != after.ReadOnly)
        {
            Announce(Loc.Get(after.ReadOnly ? "HexView_Announce_ReadOnlyOn" : "HexView_Announce_ReadOnlyOff"), "HexViewMode");
        }

        if (before.SelectionStart != after.SelectionStart || before.SelectionLength != after.SelectionLength)
        {
            _selectionChangedPending = true;
            if (after.SelectionLength > 0)
            {
                _selectionAnnounceTimer ??= CreateOneShot(SelectionAnnounceDelay, AnnounceSelection);
                _selectionAnnounceTimer.Stop();
                _selectionAnnounceTimer.Start();
            }
        }

        if (before.Cursor != after.Cursor || before.LowNibble != after.LowNibble || before.Column != after.Column)
        {
            _selectionChangedPending = true;
            _cursorAnnounceTimer ??= CreateOneShot(CursorAnnounceDelay, AnnounceCursor);
            _cursorAnnounceTimer.Stop();
            _cursorAnnounceTimer.Start();
        }

        _valueChangedPending = true;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateOneShot(TimeSpan delay, Action action)
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = _uiQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = delay;
        timer.Tick += (_, _) => action();
        return timer;
    }

    /// <summary>描画のあとに、Text パターンの選択の変更と Value の変更を知らせる (UI-50 の仕様 5)。</summary>
    private void RaiseAccessibilityChanges()
    {
        HexViewAutomationPeer? peer = Peer;
        if (peer is null)
        {
            _selectionChangedPending = _valueChangedPending = false;
            return;
        }

        peer.InvalidateCells();
        if (_selectionChangedPending && AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
        {
            peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
        }

        if (_valueChangedPending && AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
        {
            string value = CursorSummary();
            if (value != _lastValue)
            {
                peer.RaisePropertyChangedEvent(Microsoft.UI.Xaml.Automation.ValuePatternIdentifiers.ValueProperty, _lastValue, value);
                _lastValue = value;
            }
        }

        _selectionChangedPending = _valueChangedPending = false;
    }

    private void AnnounceCursor()
    {
        if (_focused)
        {
            Announce(CursorSummary(), "HexViewCursor");
        }
    }

    private void AnnounceSelection()
    {
        if (_editor is { HasSelection: true } e && _focused)
        {
            long last = e.SelectionStart + e.SelectionLength - 1;
            Announce(Loc.Format("HexView_Announce_Selection", e.SelectionLength.ToString("N0", CultureInfo.CurrentCulture),
                Spell(FormatOffset(e.SelectionStart)), Spell(FormatOffset(last))), "HexViewSelection");
        }
    }

    /// <summary>入力ごとの読み上げ (EDIT-11 の仕様 8)。入力した位置 (カーソルの直前のバイト) と値。</summary>
    private void AnnounceTyped()
    {
        if (_editor is null)
        {
            return;
        }

        long offset = _editor.LowNibble ? _editor.Cursor : Math.Max(0, _editor.Cursor - 1);
        if (offset >= _editor.Document.Length)
        {
            return;
        }

        byte[] one = new byte[1];
        var state = new ByteState[1];
        _editor.Document.Current.ReadForDisplay(offset, one, state);
        if (state[0] == ByteState.Valid)
        {
            Announce(Loc.Format("HexView_Announce_Typed", Spell(FormatOffset(offset)), Spell(HexStrings[one[0]])), "HexViewTyped");
        }
    }

    /// <summary>スクリーンリーダーへ読み上げ文を送る (UI-51 の仕様 1。新しいもので古いものを置き換える)。</summary>
    private void Announce(string text, string activityId)
    {
        LastAnnouncement = text;
        OnAnnounced(text, activityId);
        Peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, activityId);
    }

    /// <summary>読み上げ文を送るたびに呼ぶ (テスト用のビルドで記録する)。</summary>
    partial void OnAnnounced(string text, string activityId);

    /// <summary>
    /// カーソル位置の要約 (UI-51 の仕様 1 の full。UI-50 の Value にも使う)。例: 「オフセット 0x00001F00、値 4A、文字 J、Hex 列、変更あり」。
    /// 状態 (VIEW-41 の仕様 5) は該当するものだけを付ける。
    /// </summary>
    internal string CursorSummary()
    {
        if (_editor is null)
        {
            return string.Empty;
        }

        long offset = _editor.Cursor;
        var states = new List<string>
        {
            Loc.Get(_editor.ActiveColumn == ActiveColumn.Hex ? "HexView_State_HexColumn" : "HexView_State_TextColumn"),
        };
        if (_editor.ActiveColumn == ActiveColumn.Hex && _editor.LowNibble)
        {
            states.Add(Loc.Get("HexView_State_LowNibble"));
        }

        if (_editor.InsertMode)
        {
            states.Add(Loc.Get("HexView_State_Insert"));
        }

        string main;
        if (offset >= _editor.Document.Length)
        {
            main = Loc.Format("HexView_Announce_End", FormatOffset(offset));
        }
        else
        {
            byte[] one = new byte[1];
            var state = new ByteState[1];
            DocumentSnapshot snapshot = _editor.Document.Current;
            snapshot.ReadForDisplay(offset, one, state);
            if (state[0] == ByteState.Loading)
            {
                main = Loc.Format("HexView_Announce_Offset", FormatOffset(offset));
                states.Add(Loc.Get("HexView_State_Loading"));
            }
            else if (state[0] == ByteState.Unreadable)
            {
                main = Loc.Format("HexView_Announce_Offset", FormatOffset(offset));
                states.Add(Loc.Get("HexView_State_Unreadable"));
            }
            else
            {
                main = Loc.Format("HexView_Announce_Position", FormatOffset(offset), HexStrings[one[0]], CharacterName(one[0]));
                if (snapshot.EnumerateModifiedRanges(offset, 1).Any())
                {
                    states.Add(Loc.Get("HexView_State_Modified"));
                }
            }

            if (_editor.HasSelection && offset >= _editor.SelectionStart && offset < _editor.SelectionStart + _editor.SelectionLength)
            {
                states.Add(Loc.Get("HexView_State_Selected"));
            }
        }

        return Loc.Format("HexView_Announce_WithStates", main, string.Join(Loc.Get("HexView_Announce_ListSeparator"), states));
    }

    /// <summary>テキスト列の文字の読み方 (UI-51 の仕様 7)。</summary>
    private static string CharacterName(byte b) => b switch
    {
        0 => Loc.Get("HexView_Char_Null"),
        0x20 => Loc.Get("HexView_Char_Space"),
        _ when IsPrintable(b) => ((char)b).ToString(),
        _ => Loc.Get("HexView_Char_Unprintable"),
    };

    /// <summary>16 進の数字を 1 文字ずつ読ませるため、桁の間に空白を入れる (UI-51 の仕様 2。表示はしない)。</summary>
    private static string Spell(string hex)
    {
        var sb = new StringBuilder(hex.Length * 2);
        foreach (char c in hex)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // ---- UI オートメーションに渡す描画の内容 (VIEW-41 の仕様 6、テスト方針の「描画モデルの取得」) ----

    /// <summary>表示中の行数。</summary>
    internal int VisibleRowCount
    {
        get
        {
            int n = 0;
            while (n < _rows.Count && _rows[n].Visible)
            {
                n++;
            }

            return n;
        }
    }

    /// <summary>描画面 (UI オートメーションの座標の基準)。</summary>
    internal UIElement SurfaceElement => Surface;

    internal IEnumerable<UIElement> ScrollBars => [VerticalBar, HorizontalBar];

    internal int BytesPerRowShown => Math.Max(1, _bytesPerRow);

    internal int OffsetDigits => _digits;

    internal RowVisual? RowAt(int index) => index >= 0 && index < _rows.Count && _rows[index].Visible ? _rows[index] : null;

    /// <summary>画面と同じ書式の行の文字列 (オフセット列 + 2 文字の空白 + 内容)。</summary>
    internal string RowLine(int index)
    {
        RowVisual? row = RowAt(index);
        return row is null ? string.Empty : row.OffsetText + "  " + row.ContentText;
    }

    /// <summary>表示中の行の先頭オフセット。</summary>
    internal long RowStartAt(int index) => RowAt(index)?.OffsetRowStart ?? -1;

    /// <summary>Surface の座標での行の上端。</summary>
    internal double RowTop(int index) => index * _rowHeight - _subRowOffset;

    /// <summary>行の中の文字位置の左端 (Surface の座標)。オフセット列は固定、内容は横スクロールする。</summary>
    internal double CharacterLeft(int column)
    {
        int offsetChars = _digits + 2;
        return column < offsetChars
            ? LeftPadding + column * _cellWidth
            : ContentLeft + (column - offsetChars) * _cellWidth - _horizontalOffset;
    }

    /// <summary>行の中のオフセットのバイトの文字位置 (行の文字列の中で。RowLine と同じ数え方)。</summary>
    internal int CharacterIndexOf(long offset, ActiveColumn column, bool lowNibble)
    {
        var columns = new RowColumns(BytesPerRowShown);
        int c = (int)(offset % BytesPerRowShown);
        int inContent = column == ActiveColumn.Hex ? columns.HexIndex(c) + (lowNibble ? 1 : 0) : columns.TextIndex(c);
        return _digits + 2 + inContent;
    }

    /// <summary>表示中の行の中の文字位置から、バイトのオフセットと列を求める。</summary>
    internal (long Offset, ActiveColumn Column) OffsetAtCharacter(int rowIndex, int column)
    {
        long rowStart = RowStartAt(rowIndex);
        if (rowStart < 0 || _editor is null)
        {
            return (0, ActiveColumn.Hex);
        }

        var columns = new RowColumns(BytesPerRowShown);
        int ch = column - (_digits + 2);
        if (ch < 0)
        {
            return (rowStart, ActiveColumn.Hex);
        }

        if (ch >= columns.TextIndex(0))
        {
            return (Math.Min(rowStart + Math.Min(ch - columns.TextIndex(0), BytesPerRowShown - 1), _editor.Layout.MaxCursor), ActiveColumn.Text);
        }

        int c = 0;
        while (c < BytesPerRowShown - 1 && ch >= columns.HexIndex(c + 1))
        {
            c++;
        }

        return (Math.Min(rowStart + c, _editor.Layout.MaxCursor), ActiveColumn.Hex);
    }

    /// <summary>
    /// ビュー全体の描画の情報 (UI テストが読む。フォントの大きさ・セル幅・行の高さ・カーソルの形・フォーカスの枠)。
    /// </summary>
    internal string RenderSummary()
    {
        var inv = CultureInfo.InvariantCulture;
        string caret = _editor is { InsertMode: true } && _focused ? "bar" : "block";
        return string.Join(';',
            "fontSize=" + _fontSize.ToString("0.###", inv),
            "fontFamily=" + _fontFamilyName,
            "cellWidth=" + _cellWidth.ToString("0.###", inv),
            "rowHeight=" + _rowHeight.ToString("0.###", inv),
            "textScale=" + TextScaleFactor.ToString("0.###", inv),
            "zoom=" + _zoom.ToString("0.###", inv),
            "caret=" + caret,
            "caretBlinking=" + (_focused && _blinkTimer.IsRunning ? "true" : "false"),
            "nibble=" + (_editor?.LowNibble == true ? "low" : "high"),
            "focusFrame=" + (_focused ? FocusFrameOuter.StrokeThickness.ToString("0.###", inv) : "0"),
            "topRow=" + (_editor?.TopRow ?? 0).ToString(inv),
            "subRowOffset=" + _subRowOffset.ToString("0.###", inv),
            "horizontalOffset=" + _horizontalOffset.ToString("0.###", inv),
            "highContrast=" + (_accessibilitySettings.HighContrast ? "true" : "false"));
    }

    /// <summary>セルの描画の情報 (UI テストが読む。VIEW-17 の層のうちフェーズ 0 で描くもの)。</summary>
    internal string CellSummary(int rowIndex, int byteIndex, bool hex)
    {
        RowVisual? row = RowAt(rowIndex);
        if (row is null || _editor is null || _palette is null)
        {
            return string.Empty;
        }

        long offset = row.OffsetRowStart + byteIndex;
        CellKind kind = row.KindAt(byteIndex);
        bool selected = _editor.HasSelection && offset >= _editor.SelectionStart && offset < _editor.SelectionStart + _editor.SelectionLength
            && kind != CellKind.Empty;
        bool cursor = offset == _editor.Cursor;
        bool activeColumn = hex == (_editor.ActiveColumn == ActiveColumn.Hex);
        string text = hex ? row.HexCellText(byteIndex) : row.TextCellText(byteIndex);
        if (kind == CellKind.Loading && row.IsBlank)
        {
            text = hex ? "  " : " ";
        }

        var parts = new List<string>
        {
            "offset=" + FormatOffset(offset),
            "text=" + text,
            "kind=" + kind.ToString().ToLowerInvariant(),
            "foreground=" + ColorOf(selected ? (activeColumn ? _palette.SelectionText : _palette.SelectionInactiveText) : _palette.For(kind)),
            "background=" + ColorOf(selected ? (activeColumn ? _palette.Selection : _palette.SelectionInactive) : _palette.Background),
        };
        if (kind == CellKind.Modified)
        {
            parts.Add("underline=solid");
        }

        if (kind == CellKind.Unreadable)
        {
            parts.Add("pattern=hatch");
            parts.Add("patternColor=" + ColorOf(_palette.Hatch));
        }

        if (selected)
        {
            parts.Add("layer=selection");
        }

        if (cursor)
        {
            parts.Add(activeColumn ? "cursor=" + (_editor.InsertMode && _focused ? "bar" : "block") : "cursor=secondary");
            if (activeColumn && hex && _editor.LowNibble)
            {
                parts.Add("nibble=low");
            }
        }

        return string.Join(';', parts);
    }

    private static string ColorOf(Microsoft.UI.Xaml.Media.Brush brush) =>
        brush is Microsoft.UI.Xaml.Media.SolidColorBrush s ? $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "?";

    /// <summary>UI オートメーションの Value の書き換え (UI-50 の仕様 3)。カーソル位置から Hex 文字列のバイトを上書きする。</summary>
    internal void SetValueFromAutomation(string value)
    {
        if (_editor is null || _editor.ReadOnly || _editor.Document.IsEditLocked)
        {
            throw new InvalidOperationException(Loc.Get("Notice_Busy"));
        }

        string digits = new([.. value.Where(Uri.IsHexDigit)]);
        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            throw new ArgumentException(Loc.Get("Find_Error_OddDigits"), nameof(value));
        }

        byte[] data = Convert.FromHexString(digits);
        long at = _editor.Cursor;
        if (!_editor.Document.CanResize && at + data.Length > _editor.Document.Length)
        {
            throw new InvalidOperationException(Loc.Get("Notice_FixedLength"));
        }

        _editor.Document.Overwrite(at, data, "上書き");
    }
}
