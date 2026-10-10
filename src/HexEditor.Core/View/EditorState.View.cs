using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>
/// ビューの表示設定 (VIEW-08、VIEW-09、VIEW-16、VIEW-20、VIEW-42) と、表示に関わる状態 (基準点、保存済みの変更)。
/// </summary>
public sealed partial class EditorState
{
    /// <summary>保存済みの変更として覚えておく範囲の最大数 (VIEW-15 の仕様 8。超えた分は強調しない)。</summary>
    public const int SavedChangeLimit = 100_000;

    private ViewSettings _view;
    private int? _autoBytesPerRow;
    private long? _referencePoint;
    private DocumentSnapshot? _lastSnapshot;
    private List<(long Offset, long Length)> _savedChanges = [];

    /// <summary>表示設定。変えるときは <see cref="ApplyView"/> を使う。</summary>
    public ViewSettings View => _view;

    /// <summary>表示設定・基準点が変わった (ドキュメントごとの設定の保存に使う。VIEW-42 の仕様 3)。</summary>
    public event EventHandler? ViewChanged;

    /// <summary>
    /// 表示設定を変える。1 行のバイト数が変わる場合も、カーソルのオフセットと選択範囲は変えず、カーソルのある行を画面上の同じ高さに
    /// 残す (VIEW-08 の仕様 7)。表示しない列にカーソルがあれば、表示されている列に移す (VIEW-16 の仕様 3)。
    /// </summary>
    public void ApplyView(ViewSettings settings)
    {
        settings = settings.Normalize();
        if (settings == _view)
        {
            return;
        }

        long screenRow = Layout.RowOf(_cursor) - _topRow;
        _view = settings;
        if (!ReferenceEquals(_textEncoding, TextEncoding.FromId(settings.Encoding)))
        {
            _textEncoding = TextEncoding.FromId(settings.Encoding);
        }

        ApplyBytesPerRow(screenRow);
        OnLayoutChangedForSelection();
        if (settings.PageView)
        {
            // ページ単位で表示にしたら、カーソルのある区切りを表示する (VIEW-33 の仕様 4)。
            SetTopRow(_topRow, Layout.RowOf(_cursor));
        }

        ActiveColumn before = ActiveColumn;
        ActiveColumn = VisibleColumn(ActiveColumn);
        if (before != ActiveColumn)
        {
            LowNibble = false;
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    /// <summary>
    /// 「自動」のときの 1 行のバイト数 (VIEW-08 の仕様 3・4)。表示側が幅から求めて設定する。カーソルの行の高さを保つ (VIEW-01 の仕様 12)。
    /// </summary>
    public void SetAutoBytesPerRow(int bytesPerRow)
    {
        bytesPerRow = Math.Clamp(bytesPerRow, 1, ViewSettings.MaxBytesPerRow);
        if (_autoBytesPerRow == bytesPerRow)
        {
            return;
        }

        long screenRow = Layout.RowOf(_cursor) - _topRow;
        _autoBytesPerRow = bytesPerRow;
        if (_view.AutoBytesPerRow && !_view.RecordRowsActive && BytesPerRow != bytesPerRow)
        {
            ApplyBytesPerRow(screenRow);
            OnLayoutChangedForSelection();
            RaiseChanged();
        }
    }

    private void ApplyBytesPerRow(long screenRow)
    {
        int bytesPerRow = _view.EffectiveBytesPerRow(_autoBytesPerRow);
        if (bytesPerRow == BytesPerRow && Layout.RowShift == _view.EffectiveRowShift(bytesPerRow))
        {
            return;
        }

        BytesPerRow = bytesPerRow;
        SetTopRow(Layout.RowOf(_cursor) - screenRow);
    }

    /// <summary>表示されている列 (VIEW-16 の仕様 3)。Hex 列とテキスト列の少なくとも一方は表示されている。</summary>
    private ActiveColumn VisibleColumn(ActiveColumn column) => column switch
    {
        ActiveColumn.Hex when !_view.ShowHexColumn => ActiveColumn.Text,
        ActiveColumn.Text when !_view.ShowTextColumn => ActiveColumn.Hex,
        _ => column,
    };

    private OffsetFormat? _offsetFormat;

    /// <summary>ファイルのセクタサイズ (VIEW-19 の仕様 6)。ディスク・ボリュームではデータソースの値、ファイルでは表示設定の値。</summary>
    public int SectorSize => Document.Source.LogicalSectorSize > 1 ? Document.Source.LogicalSectorSize : _view.SectorSize;

    /// <summary>
    /// オフセットの書式 (VIEW-19、VIEW-20)。オフセット列・列見出し・ステータスバーで共通に使う。桁数は、開いている間は増えるときだけ変わる
    /// (VIEW-19 の仕様 3)。
    /// </summary>
    public OffsetFormat OffsetFormat
    {
        get
        {
            OffsetFormat? current = _offsetFormat;
            if (current is null || !ReferenceEquals(current.Settings, _view) || current.ReferencePoint != _referencePoint
                || current.SectorSize != SectorSize)
            {
                // 基数・基準点が同じなら、それまでの桁数を保つ。
                int keep = current is not null && current.Radix == _view.Radix && (current.ReferencePoint is null) == (_referencePoint is null)
                    && current.BaseAddress == _view.BaseAddress ? current.Digits : 0;
                current = new OffsetFormat(_view, Layout.MaxCursor, SectorSize, _referencePoint, keep);
                _offsetFormat = current;
            }
            else
            {
                current.Grow(Layout.MaxCursor);
            }

            return current;
        }
    }

    // ---- 基準点 (VIEW-20 の仕様 6・7) ----

    /// <summary>基準点 p。あればオフセット列とステータスバーに相対オフセットを出す。</summary>
    public long? ReferencePoint => _referencePoint;

    /// <summary>「カーソル位置を基準点にする」。</summary>
    public void SetReferencePoint() => SetReferencePoint(_cursor);

    public void SetReferencePoint(long? offset)
    {
        long? value = offset is { } o ? Math.Clamp(o, 0, Layout.MaxCursor) : null;
        if (value != _referencePoint)
        {
            _referencePoint = value;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            RaiseChanged();
        }
    }

    /// <summary>「基準点を解除」。</summary>
    public void ClearReferencePoint() => SetReferencePoint(null);

    // ---- 保存済みの変更 (VIEW-15 の仕様 8) ----

    /// <summary>
    /// 設定「保存後も閉じるまで強調を残す」(既定オフ)。オンのとき、保存した時点の変更の範囲を覚えておき、閉じるまで「保存済みの変更」として示す。
    /// </summary>
    public bool KeepChangesAfterSave { get; set; }

    /// <summary>保存済みの変更の範囲のうち [offset, offset + length) に重なるもの。</summary>
    public IEnumerable<(long Offset, long Length)> SavedChangesIn(long offset, long length)
    {
        if (_savedChanges.Count == 0)
        {
            yield break;
        }

        // 範囲は先頭の順に並んでいて重ならない。最初に重なる可能性のある範囲を二分探索で探す。
        int lo = 0;
        int hi = _savedChanges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            (long o, long l) = _savedChanges[mid];
            if (o + l <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < _savedChanges.Count && _savedChanges[i].Offset < offset + length; i++)
        {
            yield return _savedChanges[i];
        }
    }

    private void OnDocumentChangedForView(DocumentChangedEventArgs e)
    {
        if (_referencePoint is { } p)
        {
            _referencePoint = JumpHistory.ShiftForEdit(p, e);
        }

        if (e.Kind == DocumentChangeKind.Saved)
        {
            if (KeepChangesAfterSave && _lastSnapshot is { } before)
            {
                var ranges = new List<(long, long)>(_savedChanges.Count);
                ranges.AddRange(_savedChanges);
                foreach ((long offset, long length, _) in before.EnumerateChanges(0, before.Length))
                {
                    if (ranges.Count >= SavedChangeLimit)
                    {
                        break;
                    }

                    ranges.Add((offset, length));
                }

                _savedChanges = Merge(ranges);
            }
            else
            {
                _savedChanges = [];
            }
        }
        else if (!e.IsWholeDocument && _savedChanges.Count > 0)
        {
            _savedChanges = Merge(_savedChanges.Select(r => ShiftRange(r, e)).Where(r => r.Length > 0));
        }

        _lastSnapshot = Document.Current;
    }

    private static (long Offset, long Length) ShiftRange((long Offset, long Length) r, DocumentChangedEventArgs e)
    {
        long start = r.Offset;
        long end = r.Offset + r.Length;
        if (e.RemovedLength == e.InsertedLength)
        {
            return r;
        }

        // 先頭は挿入位置と同じなら後ろへずれ、末尾は挿入位置と同じなら動かない (挿入したバイトを範囲に含めない)。
        long Shift(long x, bool isEnd) => (isEnd ? x <= e.Offset : x < e.Offset) ? x
            : x < e.Offset + e.RemovedLength ? e.Offset : x - e.RemovedLength + e.InsertedLength;
        long s = Shift(start, isEnd: false);
        long t = Shift(end, isEnd: true);
        return (s, Math.Max(0, t - s));
    }

    private static List<(long Offset, long Length)> Merge(IEnumerable<(long Offset, long Length)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        var result = new List<(long Offset, long Length)>(sorted.Count);
        foreach ((long o, long l) in sorted)
        {
            if (result.Count > 0 && result[^1].Offset + result[^1].Length >= o)
            {
                (long po, long pl) = result[^1];
                result[^1] = (po, Math.Max(po + pl, o + l) - po);
            }
            else
            {
                result.Add((o, l));
            }
        }

        return result;
    }
}
