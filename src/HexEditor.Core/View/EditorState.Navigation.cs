using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>
/// 表示形式・逆順表示に合わせた横の移動 (VIEW-10 の仕様 6、VIEW-11 の仕様 6)、レコード (VIEW-18) と区切り (VIEW-33) の移動、
/// ページ単位で表示 (VIEW-33 の仕様 4)、ほかのビューとの同期 (VIEW-37・VIEW-39) と、ほかのビューからの編集への追従 (VIEW-37 の仕様 4、
/// VIEW-38 の仕様 5)。
/// </summary>
public sealed partial class EditorState
{
    // ---- 横の移動 ----

    /// <summary>
    /// ← / → の移動先。Hex 列では、2 バイト以上のセルの表示形式なら単位ごと (VIEW-10 の仕様 6)、逆順表示なら画面上の並び順 (VIEW-11 の仕様 6)。
    /// テキスト列はバイト単位 (VIEW-10 の仕様 8)。
    /// </summary>
    private long HorizontalTarget(bool forward)
    {
        if (ActiveColumn != ActiveColumn.Hex)
        {
            return forward ? SaturatingAdd(_cursor, 1) : _cursor - 1;
        }

        int unit = View.CellUnit;
        if (unit > 1)
        {
            long start = CellStartOf(_cursor);
            return forward ? SaturatingAdd(start, unit) : start == _cursor ? _cursor - unit : start;
        }

        RowFormat format = RowFormat.For(View, BytesPerRow);
        if (!format.Reverse)
        {
            return forward ? SaturatingAdd(_cursor, 1) : _cursor - 1;
        }

        // 逆順表示: 画面上の右隣 / 左隣のセルのバイトへ。行の端を越えたら隣の行の端のセルへ。
        HexLayout layout = Layout;
        long row = layout.RowOf(_cursor);
        long rowStart = layout.RowStart(row);
        int valid = (int)Math.Clamp(layout.Length - rowStart, 0, BytesPerRow);
        int slot = format.SlotOf(layout.ColumnOf(_cursor), valid);
        int nextSlot = slot + (forward ? 1 : -1);
        if (nextSlot >= 0 && nextSlot < BytesPerRow)
        {
            return rowStart + format.ByteOfSlot(nextSlot, valid);
        }

        long otherRow = row + (forward ? 1 : -1);
        if (otherRow < 0)
        {
            return 0;
        }

        long otherStart = layout.RowStart(otherRow);
        int otherValid = (int)Math.Clamp(layout.Length - otherStart, 0, BytesPerRow);
        return otherStart + format.ByteOfSlot(forward ? 0 : BytesPerRow - 1, otherValid);
    }

    /// <summary>オフセットを含むセル (VIEW-10) の先頭。セルは行の先頭から単位ごとに並ぶ。</summary>
    public long CellStartOf(long offset)
    {
        int unit = View.CellUnit;
        if (unit <= 1)
        {
            return offset;
        }

        HexLayout layout = Layout;
        long rowStart = layout.RowStart(layout.RowOf(offset));
        long within = offset - rowStart;
        return Math.Max(0, rowStart + within / unit * unit);
    }

    // ---- レコード (VIEW-18) ----

    /// <summary>「移動: 次のレコード」(VIEW-18 の仕様 6)。移動先がなければ false。</summary>
    public bool NextRecord()
    {
        if (RecordLayout.For(View).Next(_cursor, Layout.MaxCursor) is not { } target)
        {
            return false;
        }

        MoveTo(target, extend: false);
        return true;
    }

    /// <summary>「移動: 前のレコード」(VIEW-18 の仕様 6)。移動先がなければ false。</summary>
    public bool PreviousRecord()
    {
        if (RecordLayout.For(View).Previous(_cursor) is not { } target)
        {
            return false;
        }

        MoveTo(target, extend: false);
        return true;
    }

    // ---- 区切り (VIEW-33) ----

    /// <summary>区切り線の長さ (VIEW-33)。区切り線がなければ 0。</summary>
    public long SectionLength => SectionLayout.LengthFor(View, SectorSize);

    /// <summary>区切りの割り当て (VIEW-33 の仕様 6 のステータスバーなど)。</summary>
    public SectionLayout Sections => new(SectionLength, Document.Length);

    /// <summary>
    /// 「移動: 次のページ」「移動: 前のページ」(VIEW-33 の仕様 5。VIEW-32 と同じ規則で区切りの先頭に移り、その行を一番上に表示する)。
    /// 区切り線がない、または移動先がなければ false。
    /// </summary>
    public bool MoveSection(bool forward)
    {
        SectionLayout sections = Sections;
        if (sections.SectionLength <= 0 || (forward ? sections.Next(_cursor) : sections.Previous(_cursor)) is not { } target)
        {
            return false;
        }

        RecordJump();
        MoveTo(target, extend: false, scroll: false);
        long row = Layout.RowOf(_cursor);
        SetTopRow(row, row);
        RaiseChanged();
        return true;
    }

    /// <summary>ページ単位で表示しているときの区切りの長さ (VIEW-33 の仕様 4)。していなければ null。</summary>
    private long? PageSection()
    {
        if (!View.PageView)
        {
            return null;
        }

        long length = SectionLength;
        return length > 0 && length % BytesPerRow == 0 && Layout.RowShift == 0 ? length : null;
    }

    /// <summary>行 <paramref name="row"/> を含む区切りの最初と最後の行。</summary>
    private (long First, long Last) SectionRows(long sectionLength, long row)
    {
        long rowsPerSection = Math.Max(1, sectionLength / BytesPerRow);
        long first = row / rowsPerSection * rowsPerSection;
        long last = Math.Min(Layout.LastRow, first + rowsPerSection - 1);
        return (first, last);
    }

    private bool SameSection(long sectionLength, long a, long b) => SectionRows(sectionLength, a).First == SectionRows(sectionLength, b).First;

    /// <summary>
    /// ページ単位で表示しているときの、表示する行の範囲 (一番上の行の区切りの最初と最後の行。VIEW-33 の仕様 4)。表示側はこの範囲の外の行を
    /// 描かない。ページ単位で表示していなければ null。
    /// </summary>
    public (long First, long Last)? VisibleSectionRows => PageSection() is { } length ? SectionRows(length, _topRow) : null;

    /// <summary>
    /// ページ単位で表示しているときのスクロール (ホイール・Ctrl+↑↓)。区切りの端でさらにスクロールすると、隣の区切りへ移る (VIEW-33 の仕様 4)。
    /// </summary>
    private bool ScrollWithinSection(long rows)
    {
        if (PageSection() is not { } length || rows == 0)
        {
            return false;
        }

        (long first, long last) = SectionRows(length, _topRow);
        long maxTop = Math.Max(first, last - _visibleRows + 1);
        long target = _topRow + rows;
        if (rows > 0 && _topRow >= maxTop && last < Layout.LastRow)
        {
            // 区切りの最後まで表示している: 次の区切りの先頭へ。
            SetTopRow(last + 1, last + 1);
        }
        else if (rows < 0 && _topRow <= first && first > 0)
        {
            (long prevFirst, long prevLast) = SectionRows(length, first - 1);
            SetTopRow(Math.Max(prevFirst, prevLast - _visibleRows + 1), first - 1);
        }
        else
        {
            SetTopRow(Math.Clamp(target, first, maxTop), _topRow);
        }

        return true;
    }

    // ---- 同期 (VIEW-37 の仕様 8、VIEW-39) ----

    /// <summary>一番上の行の先頭オフセット (行の先頭のずれで負になる最初の行は 0)。</summary>
    public long TopOffset => Math.Max(0, Layout.RowStart(_topRow));

    /// <summary>
    /// ほかのビューに合わせて、一番上の行とカーソルを動かす (同期スクロール。VIEW-39 の仕様 2)。オフセットは自分の最大値で止める。
    /// ジャンプ履歴には記録しない。<paramref name="cursor"/> が null ならカーソルは動かさない。
    /// </summary>
    public void SyncTo(long topOffset, long? cursor, (long Start, long Length)? selection = null)
    {
        HexLayout layout = Layout;
        if (cursor is { } c)
        {
            long target = Math.Clamp(c, 0, layout.MaxCursor);
            if (target != _cursor || selection is null && HasSelection)
            {
                ClearSelectionAnchor();
                _cursor = target;
                LowNibble = false;
                _selectionStart = target;
            }
        }

        if (selection is { } s)
        {
            long start = Math.Clamp(s.Start, 0, layout.Length);
            _anchor = start;
            SetSelection(start, Math.Clamp(s.Length, 0, layout.Length - start));
        }

        SetTopRow(layout.RowOf(Math.Clamp(topOffset, 0, layout.MaxCursor)));
        RaiseChanged();
    }

    // ---- ほかのビューからの編集への追従 (VIEW-37 の仕様 4、VIEW-38 の仕様 5) ----

    /// <summary>
    /// 操作中でないビュー (分割したもう一方のペイン、別のタブのビュー) なら true。表示側が設定する。true のビューは、ほかのビューの挿入・削除で
    /// 表示中のデータが動かないよう、一番上の行の先頭オフセット・カーソル・選択範囲をずらす。元に戻す・やり直しの範囲の選択もしない
    /// (Undo を実行したビューだけで行う)。
    /// </summary>
    public bool FollowsEdits { get; set; }

    /// <summary>直前に知っていたドキュメントの長さ (元に戻す・やり直しの長さの変化を求める)。</summary>
    private long _knownLength = -1;

    /// <summary>ほかのビューの編集でずらした一番上の位置 (行の先頭に丸める前)。</summary>
    private long _followTop = -1;

    /// <summary>ほかのビューの編集に合わせて位置をずらす。</summary>
    private void FollowEdit(DocumentChangedEventArgs e)
    {
        long before = _knownLength < 0 ? Document.Length : _knownLength;
        if (e.IsWholeDocument && e.Selection is { } range && e.Kind is DocumentChangeKind.Undo or DocumentChangeKind.Redo)
        {
            // 元に戻す・やり直し: 範囲 [offset, offset + 長さ) が、長さの変化の分だけ伸び縮みした編集として扱う。
            long delta = Document.Length - before;
            e = new DocumentChangedEventArgs(range.Offset, Math.Max(0, range.Length - delta), range.Length, false);
        }

        if (e.IsWholeDocument || e.RemovedLength == e.InsertedLength)
        {
            return;
        }

        // 一番上の行は行単位なので、1 バイトずつの挿入では行の先頭に丸められてずれが失われる。ずらした正確な位置を覚えておき、
        // 同じ行を表示している間は続けてそこからずらす (16 バイトの挿入の後に 1 行分ずれる)。
        long current = _followTop >= 0 && Layout.RowOf(_followTop) == _topRow ? _followTop : TopOffset;
        long top = JumpHistory.ShiftForEdit(current, e);
        _followTop = top;
        _cursor = JumpHistory.ShiftForEdit(_cursor, e);
        if (_anchor >= 0)
        {
            _anchor = JumpHistory.ShiftForEdit(_anchor, e);
        }

        if (_selectionLength > 0)
        {
            long start = JumpHistory.ShiftForEdit(_selectionStart, e);
            long end = JumpHistory.ShiftForEdit(_selectionStart + _selectionLength, e);
            SetSelection(start, Math.Max(0, end - start));
        }
        else
        {
            _selectionStart = _cursor;
        }

        HexLayout layout = Layout;
        _topRow = Math.Clamp(layout.RowOf(top), 0, layout.MaxTopRow(_visibleRows));
    }
}
