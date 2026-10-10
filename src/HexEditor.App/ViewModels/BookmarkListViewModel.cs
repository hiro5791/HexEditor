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

    /// <summary>色の列: グループの色を使っているときも、表示している色を示す (INSP-27 の仕様 2)。</summary>
    public BookmarkColor ShownColor { get; set; }

    public string ColorText => ShownColor.IsCustom ? ShownColor.ToString() : Loc.Format("Bookmarks_ColorNumber", ShownColor.PaletteIndex);

    /// <summary>グループの列 (INSP-26 の仕様 1)。</summary>
    public string GroupText => Bookmark.Group ?? string.Empty;

    /// <summary>ツリー表示の字下げ (グループの階層。INSP-27)。</summary>
    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Thickness Indent { get; set; }

    /// <summary>非表示のグループのブックマークは薄く表示する (INSP-27 の仕様 3)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    public partial bool Dimmed { get; set; }

    public double RowOpacity => Dimmed ? 0.45 : 1.0;

    /// <summary>ドキュメントの列 (「すべてのドキュメント」のとき。INSP-26 の仕様 8)。</summary>
    public string DocumentName { get; set; } = string.Empty;

    /// <summary>列の配置 (表示・順序。INSP-26 の仕様 1)。一覧 (ウィンドウ) ごとに 1 つで、行を作るときに付ける。</summary>
    public required BookmarkColumnLayout Layout { get; init; }

    /// <summary>読み上げ用の名前。</summary>
    public string AutomationName => Loc.Format("Bookmarks_RowName", Name, StartText, LengthText)
        + (Bookmark.Number > 0 ? ", " + Loc.Format("Bookmarks_NumberName", Bookmark.Number) : string.Empty)
        + (RangeDeleted ? ", " + DeletedText : string.Empty)
        + (Dimmed ? ", " + Loc.Get("Bookmarks_InHiddenGroup") : string.Empty);

    /// <summary>ブックマークが変わったので表示を更新する。</summary>
    public void Update() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// ブックマーク一覧のグループの行 (INSP-27 の「画面」): 名前、件数、表示 / 非表示のトグル (目のアイコンと文字の状態表示)、色見本。
/// </summary>
public sealed partial class BookmarkGroupRowViewModel(BookmarkGroup group, BookmarkCollection owner) : ObservableObject
{
    public BookmarkGroup Group { get; } = group;

    public BookmarkCollection Owner { get; } = owner;

    public string Name => Group.Name;

    public string Path => Group.Path;

    /// <summary>件数 (下のグループのものを含む)。</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; } = true;

    public string Chevron => IsExpanded ? "" : "";

    /// <summary>字下げ (階層)。</summary>
    public Microsoft.UI.Xaml.Thickness Indent => new((Group.Depth - 1) * 16, 0, 0, 0);

    /// <summary>自身か祖先が非表示。</summary>
    public bool Hidden => !Owner.IsGroupVisible(Group.Path);

    public string EyeGlyph => Group.Visible ? "" : "";

    /// <summary>表示 / 非表示の状態の文字 (色・アイコンだけに頼らない)。</summary>
    public string VisibilityText => Loc.Get(Group.Visible ? "Bookmarks_GroupShown" : "Bookmarks_GroupHidden");

    public double RowOpacity => Hidden ? 0.45 : 1.0;

    /// <summary>グループの色の見本 (色がなければ null)。</summary>
    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush? Swatch { get; set; }

    public string AutomationName => Loc.Format("Bookmarks_GroupRowName", Name, CountText, VisibilityText);

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
/// 表示する行の並び (ブックマークか、ツリー表示のグループの行)。ブックマークの参照の配列だけを持ち、行の表示用の項目は一覧が表示する
/// ときに作る (100 万件でも仮想化する。INSP-26 の「巨大ファイル・長時間処理」)。
/// </summary>
public sealed class BookmarkRowList : IList, IReadOnlyList<object>, INotifyCollectionChanged
{
    private object[] _entries = [];
    private Bookmark[] _bookmarks = [];
    private Dictionary<Bookmark, int> _depths = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Bookmark, BookmarkRowViewModel> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BookmarkGroup, BookmarkGroupRowViewModel> _groupRows = new(ReferenceEqualityComparer.Instance);

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>行の表示用の項目を作ったときに呼ぶ (色見本を付ける)。</summary>
    public Action<BookmarkRowViewModel>? Prepare { get; set; }

    /// <summary>グループの行の表示用の項目を作ったときに呼ぶ。</summary>
    public Action<BookmarkGroupRowViewModel>? PrepareGroup { get; set; }

    /// <summary>行に付ける列の配置 (一覧の持ち主が渡す)。</summary>
    public required BookmarkColumnLayout Layout { get; init; }

    /// <summary>行の表示用の項目を作ったときに付けるドキュメントの名前 (「すべてのドキュメント」のとき)。</summary>
    public Func<Bookmark, string>? DocumentNameOf { get; set; }

    /// <summary>ブックマークが非表示のグループにあるか (薄く表示する)。</summary>
    public Func<Bookmark, bool>? IsHidden { get; set; }

    public int Count => _entries.Length;

    /// <summary>一覧のブックマーク (表示の順。グループの行を除く)。</summary>
    public IReadOnlyList<Bookmark> Bookmarks => _bookmarks;

    /// <summary>表示の順の項目 (<see cref="Bookmark"/> か <see cref="BookmarkGroup"/>)。</summary>
    public IReadOnlyList<object> Entries => _entries;

    public object this[int index] => _entries[index] switch
    {
        Bookmark b => Row(b),
        BookmarkGroupEntry g => GroupRow(g),
        _ => throw new InvalidOperationException(),
    };

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public BookmarkRowViewModel Row(Bookmark b)
    {
        if (!_rows.TryGetValue(b, out BookmarkRowViewModel? row))
        {
            row = new BookmarkRowViewModel(b) { Layout = Layout, DocumentName = DocumentNameOf?.Invoke(b) ?? string.Empty };
            Prepare?.Invoke(row);
            _rows[b] = row;
        }

        row.Indent = new Microsoft.UI.Xaml.Thickness(_depths.TryGetValue(b, out int depth) ? depth * 16 : 0, 0, 0, 0);
        row.Dimmed = IsHidden?.Invoke(b) ?? false;
        return row;
    }

    private BookmarkGroupRowViewModel GroupRow(BookmarkGroupEntry entry)
    {
        if (!_groupRows.TryGetValue(entry.Group, out BookmarkGroupRowViewModel? row))
        {
            row = new BookmarkGroupRowViewModel(entry.Group, entry.Owner);
            _groupRows[entry.Group] = row;
        }

        row.CountText = entry.Count.ToString("N0", CultureInfo.CurrentCulture);
        row.IsExpanded = entry.Expanded;
        PrepareGroup?.Invoke(row);
        return row;
    }

    /// <summary>
    /// 並びを置き換える。<paramref name="entries"/> は <see cref="Bookmark"/> か <see cref="BookmarkGroupEntry"/>。<paramref name="depths"/> は
    /// ツリー表示のときのブックマークの階層 (字下げ)。
    /// </summary>
    public void Reset(object[] entries, bool clearRows = false, Dictionary<Bookmark, int>? depths = null)
    {
        _entries = entries;
        _bookmarks = entries.Length == 0 ? [] : entries.All(e => e is Bookmark) ? [.. entries.Cast<Bookmark>()] : [.. entries.OfType<Bookmark>()];
        _depths = depths ?? new(ReferenceEqualityComparer.Instance);
        if (_rows.Count > 4096 || clearRows)
        {
            _rows.Clear();
        }

        _groupRows.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>作った行の表示を更新する。</summary>
    public void UpdateRows(IEnumerable<Bookmark>? only = null)
    {
        foreach (BookmarkRowViewModel row in only is null ? _rows.Values : only.Where(_rows.ContainsKey).Select(b => _rows[b]))
        {
            Prepare?.Invoke(row);
            row.Dimmed = IsHidden?.Invoke(row.Bookmark) ?? false;
            row.Update();
        }
    }

    /// <summary>作ったグループの行の表示 (目のアイコン・状態の文字・薄く表示) を更新する。</summary>
    public void UpdateGroupRows()
    {
        foreach (BookmarkGroupRowViewModel row in _groupRows.Values)
        {
            row.Update();
        }
    }

    public int IndexOf(Bookmark b) => Array.IndexOf(_entries, b);

    public int IndexOf(BookmarkGroup g) => Array.FindIndex(_entries, e => e is BookmarkGroupEntry entry && entry.Group == g);

    public int IndexOf(object? value) => value switch
    {
        BookmarkRowViewModel r => IndexOf(r.Bookmark),
        BookmarkGroupRowViewModel g => IndexOf(g.Group),
        _ => -1,
    };

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<object> GetEnumerator()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            yield return this[i];
        }
    }

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
        for (int i = 0; i < _entries.Length; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}

/// <summary>ツリー表示のグループの行の項目 (グループ、持ち主、件数、展開しているか)。</summary>
public sealed record BookmarkGroupEntry(BookmarkGroup Group, BookmarkCollection Owner, int Count, bool Expanded);

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

    /// <summary>
    /// 列の配置。列の表示・順序はアプリ全体で共通 (<see cref="BookmarkColumns"/>) だが、ドキュメントの列 (「すべてのドキュメント」) は
    /// ウィンドウごとなので、配置もウィンドウごとに持つ。ウィンドウを閉じたら <see cref="Dispose"/> でアプリ全体の通知から外す。
    /// </summary>
    public BookmarkColumnLayout Layout { get; } = new();

    public BookmarkRowList Rows { get; }

    public BookmarkListViewModel() => Rows = new BookmarkRowList { Layout = Layout };

    /// <summary>ウィンドウを閉じた: アプリ全体の列の設定の通知から外す。</summary>
    public void Dispose() => Layout.Dispose();

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
        Layout.ShowDocument = value;
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

        if (e.VisibilityOnly)
        {
            // グループの表示 / 非表示 (INSP-27 の仕様 3): 並びは変わらないので作り直さず、作った行の表示だけを直す (100 万件でも 200 ms 以内)。
            Rows.UpdateRows();
            Rows.UpdateGroupRows();
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
        Rows.IsHidden = b => OwnerOf(b) is { } owner && !owner.Bookmarks.IsVisible(b);
        if (!AllDocuments && shown[0].Bookmarks.Groups.Count > 0)
        {
            // グループがある場合はツリーで表示する (INSP-26 の「画面」、INSP-27)。
            (object[] tree, Dictionary<Bookmark, int> depths) = BuildTree(shown[0].Bookmarks, items);
            Rows.Reset(tree, modeChanged, depths);
        }
        else
        {
            Rows.Reset([.. items], modeChanged);
        }

        CountText = Loc.Format("Bookmarks_Count", items.Count.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>折りたたんでいるグループのパス (ドキュメントによらない)。</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>グループの行を折りたたむ・展開する。</summary>
    public void ToggleExpanded(BookmarkGroup group)
    {
        if (!_collapsed.Remove(group.Path))
        {
            _collapsed.Add(group.Path);
        }

        Rebuild();
    }

    public bool IsExpanded(BookmarkGroup group) => !_collapsed.Contains(group.Path);

    /// <summary>
    /// ツリーの並び: 各グループの行の後に、その下のグループ (名前の順) と、そのグループのブックマーク (並べ替えの順) を置く。グループなしの
    /// ブックマークは最後。絞り込み中は、一致するブックマークのあるグループだけを出す。
    /// </summary>
    private (object[] Entries, Dictionary<Bookmark, int> Depths) BuildTree(BookmarkCollection bookmarks, List<Bookmark> sorted)
    {
        var byGroup = new Dictionary<string, List<Bookmark>>(StringComparer.Ordinal);
        var ungrouped = new List<Bookmark>();
        foreach (Bookmark b in sorted)
        {
            if (b.Group is { } g)
            {
                if (!byGroup.TryGetValue(g, out List<Bookmark>? list))
                {
                    byGroup[g] = list = [];
                }

                list.Add(b);
            }
            else
            {
                ungrouped.Add(b);
            }
        }

        // 下のグループを含む件数。
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string path, List<Bookmark> list) in byGroup)
        {
            foreach (string p in BookmarkGroups.SelfAndAncestors(path))
            {
                counts[p] = counts.GetValueOrDefault(p) + list.Count;
            }
        }

        ILookup<string, BookmarkGroup> children = bookmarks.Groups.ToLookup(g => g.ParentPath ?? string.Empty, StringComparer.Ordinal);
        bool filtering = Filter.Trim().Length > 0;
        var entries = new List<object>(sorted.Count + bookmarks.Groups.Count);
        var depths = new Dictionary<Bookmark, int>(ReferenceEqualityComparer.Instance);
        void Add(string parent)
        {
            foreach (BookmarkGroup group in children[parent].OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                int count = counts.GetValueOrDefault(group.Path);
                if (filtering && count == 0)
                {
                    continue;
                }

                bool expanded = IsExpanded(group);
                entries.Add(new BookmarkGroupEntry(group, bookmarks, count, expanded));
                if (!expanded)
                {
                    continue;
                }

                Add(group.Path);
                if (byGroup.TryGetValue(group.Path, out List<Bookmark>? list))
                {
                    foreach (Bookmark b in list)
                    {
                        depths[b] = group.Depth;
                        entries.Add(b);
                    }
                }
            }
        }

        Add(string.Empty);
        entries.AddRange(ungrouped);
        return ([.. entries], depths);
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
