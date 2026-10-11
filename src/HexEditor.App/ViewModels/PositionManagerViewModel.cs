using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Settings;

namespace HexEditor.App.ViewModels;

/// <summary>位置マネージャの一覧の 1 行 (INSP-31 の仕様 1)。表示する行だけ作る。</summary>
public sealed partial class PositionRowViewModel(Bookmark bookmark) : ObservableObject
{
    public Bookmark Bookmark { get; } = bookmark;

    public string Name => Bookmark.Name;

    /// <summary>範囲 (開始・終了 (このバイトを含む)・長さ)。</summary>
    public string RangeText => Loc.Format("Annotations_Range", "0x" + Bookmark.Start.ToString("X", CultureInfo.InvariantCulture),
        "0x" + (Bookmark.Start + Math.Max(1, Bookmark.Length) - 1).ToString("X", CultureInfo.InvariantCulture), Bookmark.Length.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>コメントの 1 行目。</summary>
    public string CommentLine => Bookmark.Comment.Split('\n', 2)[0].TrimEnd('\r');

    public string AutomationName => Name + ", " + RangeText + (Bookmark.Comment.Length > 0 ? ", " + CommentLine : string.Empty);

    public void Update() => OnPropertyChanged(string.Empty);
}

/// <summary>ブックマークの配列を、表示するときに行の項目にする一覧 (100 万件でも仮想化する。INSP-31 の「巨大ファイル・長時間処理」)。</summary>
public sealed class PositionRowList : IList, IReadOnlyList<PositionRowViewModel>, INotifyCollectionChanged
{
    private Bookmark[] _items = [];
    private readonly Dictionary<Bookmark, PositionRowViewModel> _rows = new(ReferenceEqualityComparer.Instance);

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _items.Length;

    public IReadOnlyList<Bookmark> Bookmarks => _items;

    public PositionRowViewModel this[int index] => Row(_items[index]);

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public PositionRowViewModel Row(Bookmark b)
    {
        if (!_rows.TryGetValue(b, out PositionRowViewModel? row))
        {
            row = new PositionRowViewModel(b);
            _rows[b] = row;
        }

        return row;
    }

    public void Reset(Bookmark[] items)
    {
        _items = items;
        if (_rows.Count > 4096)
        {
            _rows.Clear();
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void UpdateRows()
    {
        foreach (PositionRowViewModel row in _rows.Values)
        {
            row.Update();
        }
    }

    public int IndexOf(Bookmark b) => Array.IndexOf(_items, b);

    public int IndexOf(object? value) => value is PositionRowViewModel r ? IndexOf(r.Bookmark) : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<PositionRowViewModel> GetEnumerator() => _items.Select(Row).GetEnumerator();

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
/// 位置マネージャ (INSP-31): ブックマークを開始オフセット順に並べた一覧と、選んだブックマークのコメント (長い説明) の編集・プレビュー。
/// データはブックマークと同じもの (編集はブックマーク一覧にも反映される)。
/// </summary>
public sealed partial class PositionManagerViewModel : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly SettingsStore _settings;
    private DocumentAnnotations? _annotations;
    private bool _rebuildQueued;

    public PositionManagerViewModel(SettingsStore settings)
    {
        _settings = settings;
    }

    public PositionRowList Rows { get; } = new();

    public DocumentAnnotations? Annotations => _annotations;

    /// <summary>パネルが表示されている (一覧を作る)。</summary>
    public bool IsActive { get; set; }

    /// <summary>「説明のあるものだけ」(INSP-31 の仕様 3。既定オフ)。</summary>
    [ObservableProperty]
    public partial bool OnlyWithComments { get; set; }

    partial void OnOnlyWithCommentsChanged(bool value) => Rebuild();

    /// <summary>「カーソルに追従する」(INSP-31 の仕様 2。既定オン。設定)。</summary>
    public bool FollowCursor
    {
        get => _settings.GetBool(MainWindow.FollowCursorKey, true);
        set
        {
            _settings.SetBool(MainWindow.FollowCursorKey, value, true);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedRange))]
    public partial PositionRowViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public string SelectedTitle => Selected?.Name ?? Loc.Get("PositionManager_NoSelection");

    public string SelectedRange => Selected?.RangeText ?? string.Empty;

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    public void Attach(DocumentAnnotations? annotations)
    {
        if (_annotations is not null)
        {
            _annotations.Bookmarks.Changed -= Bookmarks_Changed;
        }

        _annotations = annotations;
        if (_annotations is not null)
        {
            _annotations.Bookmarks.Changed += Bookmarks_Changed;
        }

        Selected = null;
        Rebuild();
    }

    /// <summary>ウィンドウを閉じた: ブックマークの通知から外す。</summary>
    public void Detach() => Attach(null);

    private void Bookmarks_Changed(object? sender, BookmarksChangedEventArgs e)
    {
        if (!IsActive)
        {
            return;
        }

        if (e.VisibilityOnly)
        {
            // グループの表示 / 非表示は位置マネージャの並びを変えない (INSP-27)。
            return;
        }

        if (e.Kind is BookmarkChangeKind.Modified or BookmarkChangeKind.Positions && !OnlyWithComments)
        {
            Rows.UpdateRows();
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedRange));
            return;
        }

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

    /// <summary>開始オフセット順に並べ直す (絞り込みを含む)。</summary>
    public void Rebuild()
    {
        if (!IsActive || _annotations is null)
        {
            Rows.Reset([]);
            CountText = string.Empty;
            return;
        }

        IEnumerable<Bookmark> all = _annotations.Bookmarks.Ordered;
        if (OnlyWithComments)
        {
            all = all.Where(b => b.Comment.Trim().Length > 0);
        }

        Bookmark? keep = Selected?.Bookmark;
        Rows.Reset([.. all]);
        CountText = Loc.Format("Bookmarks_Count", Rows.Count.ToString("N0", CultureInfo.CurrentCulture));
        Selected = keep is not null && Rows.IndexOf(keep) >= 0 ? Rows.Row(keep) : null;
    }

    /// <summary>選んだブックマークのコメントを書き換える (入力と同時に反映する)。</summary>
    public void SetComment(string comment)
    {
        if (Selected is { } row && _annotations is { } a)
        {
            a.Bookmarks.SetComment(row.Bookmark, comment);
        }
    }

    /// <summary>カーソル位置を含むブックマーク (最も内側。なければ null)。</summary>
    public Bookmark? AtCursor(long cursor) =>
        _annotations?.Bookmarks.Overlapping(cursor, cursor + 1).Where(b => !OnlyWithComments || b.Comment.Trim().Length > 0).OrderBy(b => b.Length).FirstOrDefault();

    /// <summary>位置マネージャの内容の書き出し (INSP-31 の仕様 6)。<paramref name="html"/> なら HTML、そうでなければ Markdown。</summary>
    public string ExportText(bool html)
    {
        IEnumerable<Bookmark> items = Rows.Bookmarks;
        string title = _annotations?.Document.DisplayName ?? string.Empty;
        string range = Loc.Get("PositionManager_ExportRange");
        return html ? PositionNotes.ToHtml(items, title, range) : PositionNotes.ToMarkdown(items, title, range);
    }
}
