using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;

namespace HexEditor.App.ViewModels;

/// <summary>ブックマーク一覧の 1 行 (INSP-26 の仕様 1)。表示する行だけ作る。</summary>
public sealed partial class BookmarkRowViewModel(Bookmark bookmark) : ObservableObject
{
    public Bookmark Bookmark { get; } = bookmark;

    public string NumberText => Bookmark.Number > 0 ? Bookmark.Number.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public string Name => Bookmark.Name;

    public string StartText => "0x" + Bookmark.Start.ToString(Bookmark.Start > uint.MaxValue ? "X16" : "X8", CultureInfo.InvariantCulture);

    public string LengthText => Bookmark.Length.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>コメントの 1 行目。</summary>
    public string CommentLine => Bookmark.Comment.Split('\n', 2)[0].TrimEnd('\r');

    public bool HasComment => Bookmark.Comment.Length > 0;

    /// <summary>コメントの 1 行目を 2 行目に出すか (コメントがあり、コメントの列を出していないとき)。</summary>
    public static Microsoft.UI.Xaml.Visibility CommentLineShown(bool hasComment, Microsoft.UI.Xaml.Visibility layout) =>
        hasComment ? layout : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>範囲がすべて削除された印 (INSP-23 の仕様 4)。</summary>
    public string DeletedText => Bookmark.RangeDeleted ? Loc.Get("Bookmarks_RangeDeleted") : string.Empty;

    public bool RangeDeleted => Bookmark.RangeDeleted;

    /// <summary>色見本の色 (UI が付ける)。</summary>
    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush? Swatch { get; set; }

    public string ColorText => Bookmark.Color.IsCustom ? Bookmark.Color.ToString() : Loc.Format("Bookmarks_ColorNumber", Bookmark.Color.PaletteIndex);

    /// <summary>グループの列 (INSP-26 の仕様 1)。</summary>
    public string GroupText => Bookmark.Group ?? string.Empty;

    /// <summary>ドキュメントの列 (「すべてのドキュメント」のとき。INSP-26 の仕様 8)。</summary>
    public string DocumentName { get; set; } = string.Empty;

    /// <summary>列の配置 (表示・順序。INSP-26 の仕様 1)。</summary>
    public BookmarkColumnLayout Layout => BookmarkColumnLayout.Instance;

    /// <summary>読み上げ用の名前。</summary>
    public string AutomationName => Loc.Format("Bookmarks_RowName", Name, StartText, LengthText)
        + (Bookmark.Number > 0 ? ", " + Loc.Format("Bookmarks_NumberName", Bookmark.Number) : string.Empty)
        + (RangeDeleted ? ", " + DeletedText : string.Empty);

    /// <summary>ブックマークが変わったので表示を更新する。</summary>
    public void Update() => OnPropertyChanged(string.Empty);
}

/// <summary>一覧の並べ替えの列 (INSP-26 の仕様 2)。</summary>
public enum BookmarkSortColumn
{
    Start,
    Number,
    Name,
    Length,
    Color,
    Comment,
    Group,
}

/// <summary>
/// 表示する行の並び。ブックマークの参照の配列だけを持ち、行の表示用の項目は一覧が表示するときに作る (100 万件でも仮想化する。
/// INSP-26 の「巨大ファイル・長時間処理」)。
/// </summary>
public sealed class BookmarkRowList : IList, IReadOnlyList<BookmarkRowViewModel>, INotifyCollectionChanged
{
    private Bookmark[] _items = [];
    private readonly Dictionary<Bookmark, BookmarkRowViewModel> _rows = new(ReferenceEqualityComparer.Instance);

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>行の表示用の項目を作ったときに呼ぶ (色見本を付ける)。</summary>
    public Action<BookmarkRowViewModel>? Prepare { get; set; }

    /// <summary>行の表示用の項目を作ったときに付けるドキュメントの名前 (「すべてのドキュメント」のとき)。</summary>
    public Func<Bookmark, string>? DocumentNameOf { get; set; }

    public int Count => _items.Length;

    public IReadOnlyList<Bookmark> Bookmarks => _items;

    public BookmarkRowViewModel this[int index] => Row(_items[index]);

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public BookmarkRowViewModel Row(Bookmark b)
    {
        if (!_rows.TryGetValue(b, out BookmarkRowViewModel? row))
        {
            row = new BookmarkRowViewModel(b) { DocumentName = DocumentNameOf?.Invoke(b) ?? string.Empty };
            Prepare?.Invoke(row);
            _rows[b] = row;
        }

        return row;
    }

    public void Reset(Bookmark[] items, bool clearRows = false)
    {
        _items = items;
        if (_rows.Count > 4096 || clearRows)
        {
            _rows.Clear();
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>作った行の表示を更新する。</summary>
    public void UpdateRows(IEnumerable<Bookmark>? only = null)
    {
        foreach (BookmarkRowViewModel row in only is null ? _rows.Values : only.Where(_rows.ContainsKey).Select(b => _rows[b]))
        {
            Prepare?.Invoke(row);
            row.Update();
        }
    }

    public int IndexOf(Bookmark b) => Array.IndexOf(_items, b);

    public int IndexOf(object? value) => value is BookmarkRowViewModel r ? IndexOf(r.Bookmark) : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<BookmarkRowViewModel> GetEnumerator() => _items.Select(Row).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _items.Length; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}

/// <summary>
/// ブックマーク一覧 (INSP-26)。開いているドキュメントのブックマークを、絞り込み (名前・コメント。大文字・小文字を区別しない) と
/// 並べ替えをして表示する。
/// </summary>
public sealed partial class BookmarkListViewModel : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private DocumentAnnotations? _annotations;
    private bool _rebuildQueued;

    // 「すべてのドキュメント」(INSP-26 の仕様 8): 一覧に出しているドキュメントと、ブックマークの持ち主。
    private readonly List<DocumentAnnotations> _shown = [];
    private readonly Dictionary<Bookmark, DocumentAnnotations> _owners = new(ReferenceEqualityComparer.Instance);
    private bool _lastAllDocuments;

    public BookmarkRowList Rows { get; } = new();

    public DocumentAnnotations? Annotations => _annotations;

    public BookmarkCollection? Bookmarks => _annotations?.Bookmarks;

    /// <summary>開いているすべてのドキュメントの付随データ (ウィンドウが渡す。「すべてのドキュメント」で使う)。</summary>
    public Func<IReadOnlyList<DocumentAnnotations>>? AllAnnotations { get; set; }

    /// <summary>
    /// 「すべてのドキュメント」(INSP-26 の仕様 8。既定オフ): 開いているすべてのドキュメントのブックマークを、ドキュメントごとにまとめて表示する。
    /// </summary>
    [ObservableProperty]
    public partial bool AllDocuments { get; set; }

    partial void OnAllDocumentsChanged(bool value)
    {
        BookmarkColumnLayout.Instance.ShowDocument = value;
        Rebuild();
    }

    /// <summary>ブックマークを持っているドキュメント (一覧に出しているもの。なければ今のドキュメント)。</summary>
    public DocumentAnnotations? OwnerOf(Bookmark bookmark) => _owners.TryGetValue(bookmark, out DocumentAnnotations? owner) ? owner : _annotations;

    /// <summary>開いているドキュメントが増えた・減った (「すべてのドキュメント」のときは一覧を作り直す)。</summary>
    public void DocumentsChanged()
    {
        if (AllDocuments)
        {
            Rebuild();
        }
    }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    public BookmarkSortColumn SortColumn { get; private set; } = BookmarkSortColumn.Start;

    public bool SortDescending { get; private set; }

    /// <summary>件数の表示 (「3 件」)。</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    /// <summary>パネルが表示されている (一覧を作る)。</summary>
    public bool IsActive { get; set; }

    public void Attach(DocumentAnnotations? annotations)
    {
        _annotations = annotations;
        Rebuild();
    }

    /// <summary>一覧に出すドキュメントの変更を受ける (出すドキュメントが変わったら付け替える)。</summary>
    private void Subscribe(IReadOnlyList<DocumentAnnotations> shown)
    {
        if (_shown.SequenceEqual(shown))
        {
            return;
        }

        foreach (DocumentAnnotations a in _shown)
        {
            a.Bookmarks.Changed -= Bookmarks_Changed;
        }

        _shown.Clear();
        _shown.AddRange(shown);
        foreach (DocumentAnnotations a in _shown)
        {
            a.Bookmarks.Changed += Bookmarks_Changed;
        }
    }

    partial void OnFilterChanged(string value) => Rebuild();

    private void Bookmarks_Changed(object? sender, BookmarksChangedEventArgs e)
    {
        if (!IsActive)
        {
            return;
        }

        if (e.Kind == BookmarkChangeKind.Modified && SortColumn == BookmarkSortColumn.Start && Filter.Length == 0 && !AllDocuments)
        {
            Rows.UpdateRows(e.Items);
            return;
        }

        if (e.Kind == BookmarkChangeKind.Positions && SortColumn == BookmarkSortColumn.Start && !AllDocuments)
        {
            // 位置が変わっても開始位置の順は変わらない (区間木の順)。表示中の行の数字だけを直す。
            Rows.UpdateRows();
            return;
        }

        // 続けて起きた変更 (まとめての追加など) は 1 回の作り直しにまとめる。
        if (_queue is null)
        {
            Rebuild();
        }
        else if (!_rebuildQueued)
        {
            _rebuildQueued = true;
            _queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _rebuildQueued = false;
                Rebuild();
            });
        }
    }

    /// <summary>並べ替える。同じ列をもう一度選ぶと逆順にする。</summary>
    public void Sort(BookmarkSortColumn column)
    {
        SortDescending = column == SortColumn && !SortDescending;
        SortColumn = column;
        Rebuild();
    }

    /// <summary>絞り込みと並べ替えをして一覧を作り直す。</summary>
    public void Rebuild()
    {
        IReadOnlyList<DocumentAnnotations> shown = !IsActive ? []
            : AllDocuments && AllAnnotations is { } all ? all()
            : _annotations is { } current ? [current] : [];
        Subscribe(shown);
        bool modeChanged = _lastAllDocuments != AllDocuments;
        _lastAllDocuments = AllDocuments;
        _owners.Clear();
        if (shown.Count == 0)
        {
            Rows.Reset([], modeChanged);
            CountText = string.Empty;
            return;
        }

        // ドキュメントごとにまとめる (ドキュメントの順、その中で並べ替え)。
        var items = new List<Bookmark>();
        foreach (DocumentAnnotations a in shown)
        {
            Bookmark[] part = Sorted(a.Bookmarks);
            if (AllDocuments)
            {
                foreach (Bookmark b in part)
                {
                    _owners[b] = a;
                }
            }

            items.AddRange(part);
        }

        Rows.DocumentNameOf = AllDocuments ? b => _owners.TryGetValue(b, out DocumentAnnotations? a) ? a.Document.DisplayName : string.Empty : null;
        Rows.Reset([.. items], modeChanged);
        CountText = Loc.Format("Bookmarks_Count", items.Count.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>1 つのドキュメントのブックマークを、絞り込みと並べ替えをして返す。</summary>
    private Bookmark[] Sorted(BookmarkCollection bookmarks)
    {
        IEnumerable<Bookmark> all = bookmarks.Ordered;
        string filter = Filter.Trim();
        if (filter.Length > 0)
        {
            all = all.Where(b => b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || b.Comment.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        Bookmark[] items = [.. all];
        // 文字列は序数比較 (大文字・小文字を区別しない。00-overview 5.4)。100 万件でも 1 秒以内に並べる (INSP-26 の受け入れ基準 5)。
        // 並べ替えの鍵は先に作り、同じ値どうしは開始位置の順にする (安定な並べ替え)。
        if (SortColumn is BookmarkSortColumn.Name or BookmarkSortColumn.Comment or BookmarkSortColumn.Color or BookmarkSortColumn.Group)
        {
            string[] keys = new string[items.Length];
            int[] order = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                Bookmark b = items[i];
                keys[i] = SortColumn switch
                {
                    BookmarkSortColumn.Name => b.Name,
                    BookmarkSortColumn.Comment => b.Comment,
                    BookmarkSortColumn.Group => b.Group ?? string.Empty,
                    _ => b.Color.ToString(),
                };
                order[i] = i;
            }

            Array.Sort(keys, order, StringComparer.OrdinalIgnoreCase);

            // 同じ値の並びの中は開始位置の順に直す。
            for (int i = 0; i < keys.Length;)
            {
                int j = i + 1;
                while (j < keys.Length && StringComparer.OrdinalIgnoreCase.Equals(keys[i], keys[j]))
                {
                    j++;
                }

                if (j - i > 1)
                {
                    Array.Sort(order, i, j - i);
                }

                i = j;
            }

            Bookmark[] source = items;
            items = [.. order.Select(k => source[k])];
        }
        else if (SortColumn is BookmarkSortColumn.Number or BookmarkSortColumn.Length)
        {
            var keys = new (long Key, int Index)[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                keys[i] = (SortColumn == BookmarkSortColumn.Length ? items[i].Length : items[i].Number == 0 ? 10 : items[i].Number, i);
            }

            Array.Sort(keys);
            Bookmark[] source = items;
            items = [.. keys.Select(k => source[k.Index])];
        }

        if (SortDescending)
        {
            Array.Reverse(items);
        }

        return items;
    }
}
