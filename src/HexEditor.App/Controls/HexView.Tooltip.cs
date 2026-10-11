using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>
/// マウスを合わせたときのツールチップの内容 (VIEW-07)。数値は地域設定で書式化し、16 進の表記は大文字・小文字の設定 (VIEW-12) に従う。
/// あわせて、マウスの「戻る」「進む」ボタン (VIEW-31) と Ctrl+ホイール (VIEW-43) の入口。
/// </summary>
public sealed partial class HexView
{
    /// <summary>付加情報の名前を出す最大数 (VIEW-07 の仕様 2)。</summary>
    private const int MaxAnnotationsInToolTip = 3;

    /// <summary>Ctrl+ホイール (VIEW-43 の仕様 4)。ウィンドウ (UI-08) が倍率を決め、<see cref="ZoomAt"/> をポインタの位置で呼ぶ。</summary>
    public event EventHandler<HexViewZoomWheelEventArgs>? ZoomWheel;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>
    /// Hex 列・テキスト列のセルのツールチップ (VIEW-07 の仕様 2・3)。<paramref name="unreadable"/> は読み取れない理由。文字は
    /// <paramref name="textColumn"/> のテキスト列 (マウスを合わせた列。Hex 列なら 1 列目) の文字コードで示す (VIEW-24)。
    /// </summary>
    internal string CellToolTipText(long offset, string? unreadable, bool includeAnnotations = true, int textColumn = 0)
    {
        EditorState editor = _editor!;
        bool lower = editor.View.LowercaseHex;
        OffsetFormat format = editor.OffsetFormat;
        var lines = new List<string>
        {
            // オフセット: 16 進と 10 進の両方 (例: 0x1F00 (7,936))。
            Loc.Format("HexView_Tip_Offset", OffsetFormat.Hex(offset, lower), offset.ToString("N0", Culture)),
        };
        if (editor.View.BaseAddress != 0)
        {
            lines.Add(Loc.Format("HexView_Tip_Address", "@" + format.AddressOf(offset).ToString(lower ? "x" : "X", CultureInfo.InvariantCulture)));
        }

        byte[] one = new byte[1];
        var state = new ByteState[1];
        DocumentSnapshot snapshot = editor.Document.Current;
        snapshot.ReadForDisplay(offset, one, state);
        if (state[0] == ByteState.Valid)
        {
            byte b = one[0];
            lines.Add(Loc.Format("HexView_Tip_Value", (lower ? HexStringsLower : HexStrings)[b], b.ToString(Culture), ((sbyte)b).ToString(Culture),
                Convert.ToString(b, 8), Convert.ToString(b, 2).PadLeft(8, '0')));
            if (CharacterDescription(offset, textColumn) is { } character)
            {
                lines.Add(Loc.Format("HexView_Tip_Char", character));
            }
        }
        else if (state[0] == ByteState.Loading)
        {
            lines.Add(Loc.Get("HexView_Tip_ValueLoading"));
        }

        // 状態: 変更あり・挿入・読み取れません (理由)。
        var states = new List<string>();
        foreach ((_, _, bool inserted) in snapshot.EnumerateChanges(offset, 1))
        {
            states.Add(Loc.Get(inserted ? "HexView_State_Inserted" : "HexView_State_Modified"));
        }

        if (unreadable is not null)
        {
            states.Add(unreadable);
        }

        if (states.Count > 0)
        {
            lines.Add(Loc.Format("HexView_Tip_State", string.Join(Loc.Get("HexView_Announce_ListSeparator"), states)));
        }

        // 付加情報: ブックマーク名など (各最大 3 件、超えた分は「ほか N 件」)。
        if (includeAnnotations && (AnnotationToolTips?.Invoke(offset) is { Count: > 0 } tips ? tips : AnnotationNames?.Invoke(offset)) is { Count: > 0 } names)
        {
            string shown = string.Join(Loc.Get("HexView_Announce_ListSeparator"), names.Take(MaxAnnotationsInToolTip));
            if (names.Count > MaxAnnotationsInToolTip)
            {
                shown += Loc.Get("HexView_Announce_ListSeparator") + Loc.Format("HexView_Tip_More", names.Count - MaxAnnotationsInToolTip);
            }

            lines.Add(shown);
        }

        // 選択範囲の上では、開始・終了 (このバイトを含む)・長さ (VIEW-07 の仕様 3)。
        if (editor.HasSelection && offset >= editor.SelectionStart && offset < editor.SelectionStart + editor.SelectionLength)
        {
            long last = editor.SelectionStart + editor.SelectionLength - 1;
            lines.Add(Loc.Format("HexView_Tip_Selection", OffsetFormat.Hex(editor.SelectionStart, lower), OffsetFormat.Hex(last, lower),
                OffsetFormat.Hex(editor.SelectionLength, lower), editor.SelectionLength.ToString("N0", Culture)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>オフセット列のツールチップ: 行の先頭と末尾のアドレスと行番号 (10 進。VIEW-07 の仕様 4)。</summary>
    internal string RowToolTipText(long row)
    {
        EditorState editor = _editor!;
        HexLayout layout = editor.Layout;
        long start = Math.Max(0, layout.RowStart(row));
        long end = Math.Min(layout.RowStart(row) + layout.BytesPerRow - 1, Math.Max(0, layout.Length - 1));
        OffsetFormat format = editor.OffsetFormat;
        return Loc.Format("HexView_Tip_Row", row.ToString("N0", Culture), format.Status(start, Culture), format.Status(Math.Max(start, end), Culture));
    }

    /// <summary>
    /// テキスト列 <paramref name="textColumn"/> の文字と符号位置 (例: <c>あ U+3042 (UTF-8: E3 81 82)</c>)。表示中の行の解読結果を使う。
    /// 文字表 (VIEW-23) の項目は、セルに収まらず切り詰めた文字列も全体を示す (VIEW-23 の仕様 4)。
    /// </summary>
    private string? CharacterDescription(long offset, int textColumn)
    {
        EditorState editor = _editor!;
        HexLayout layout = editor.Layout;
        long r = layout.RowOf(offset) - editor.TopRow;
        if (r < 0 || r >= _rows.Count || !_rows[(int)r].Visible)
        {
            return null;
        }

        textColumn = Math.Clamp(textColumn, 0, Math.Max(0, editor.View.TextColumnCount - 1));
        TextCell cell = _rows[(int)r].TextAt(textColumn, layout.ColumnOf(offset));
        if (cell.Kind is not (TextCellKind.Char or TextCellKind.Continuation) || cell.Offset < 0)
        {
            return cell.Kind == TextCellKind.NonPrintable ? Loc.Get("HexView_Char_Unprintable") : null;
        }

        // 続きのセルでは、文字の先頭のセルの文字を示す。
        long first = cell.Offset;
        TextCell head = first == offset ? cell : HeadCell(first, textColumn) ?? cell;
        string text = head.Text.TrimStart(TextCellDecoder.DottedCircle);
        if (text.Length == 0)
        {
            return null;
        }

        TextEncoding encoding = editor.TextEncodingOf(textColumn);
        byte[] bytes = new byte[Math.Max(1, head.Span)];
        editor.Document.Current.Read(first, bytes);
        var hex = new StringBuilder();
        foreach (byte b in bytes)
        {
            hex.Append(hex.Length > 0 ? " " : string.Empty).Append((editor.View.LowercaseHex ? HexStringsLower : HexStrings)[b]);
        }

        // 文字表の項目 (例: <END>) は複数の文字のことがあるので、符号位置ではなく文字列全体を示す。
        if (encoding.Table is not null && System.Globalization.StringInfo.ParseCombiningCharacters(head.Text).Length > 1)
        {
            return $"{head.Text} ({encoding.Name}: {hex})";
        }

        int codePoint = char.ConvertToUtf32(text, 0);
        return $"{head.Text} U+{codePoint:X4} ({encoding.Name}: {hex})";
    }

    private TextCell? HeadCell(long offset, int textColumn)
    {
        HexLayout layout = _editor!.Layout;
        long r = layout.RowOf(offset) - _editor.TopRow;
        return r >= 0 && r < _rows.Count && _rows[(int)r].Visible ? _rows[(int)r].TextAt(textColumn, layout.ColumnOf(offset)) : null;
    }

    /// <summary>マウスの「戻る」(XButton1) と「進む」(XButton2) (VIEW-31)。</summary>
    private void MouseHistoryButton(bool back)
    {
        if (_editor is null)
        {
            return;
        }

        if (back)
        {
            _editor.GoBack();
        }
        else
        {
            _editor.GoForward();
        }
    }
}

/// <summary>Ctrl+ホイール (VIEW-43)。<see cref="Delta"/> は 1 ノッチ 120 (奥へ回すと正)、<see cref="PointerY"/> は描画面の座標。</summary>
public sealed class HexViewZoomWheelEventArgs(int delta, double pointerY) : EventArgs
{
    public int Delta { get; } = delta;

    public double PointerY { get; } = pointerY;
}
