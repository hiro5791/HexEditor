using System.Collections;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 複数ファイル検索の結果の 1 行 (FIND-30 の仕様 5)。ファイルの行 (パス・件数・サイズ・更新日時) と、その下の一致の行 (オフセット・長さ・
/// 一致データ)。置換モードではチェックボックスで置換するかを選ぶ (FIND-31 の仕様 2)。行は一覧に見えている分だけ作り
/// (<see cref="MultiFileRowList"/>)、チェックそのものは <see cref="MultiFileResultView"/> が持つ。
/// </summary>
public sealed partial class MultiFileRow : ObservableObject
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Normal = new("Segoe UI Variable, Segoe UI");
    private readonly Action<MultiFileRow, bool?>? _checkedChanged;
    private bool _syncing;

    internal MultiFileRow(int row, int fileIndex, FileSearchResult file, int matchIndex, SearchMatch? match, string title, bool? isChecked,
        Visibility checkVisibility, Action<MultiFileRow, bool?>? checkedChanged)
    {
        Row = row;
        FileIndex = fileIndex;
        File = file;
        MatchIndex = matchIndex;
        Match = match;
        _syncing = true;
        Title = title;
        Checked = isChecked;
        CheckVisibility = checkVisibility;
        _syncing = false;
        _checkedChanged = checkedChanged;
    }

    /// <summary>一覧の行の番号。</summary>
    public int Row { get; }

    /// <summary>結果の中のファイルの番号。</summary>
    public int FileIndex { get; }

    /// <summary>行のファイルの結果。</summary>
    public FileSearchResult File { get; }

    /// <summary>一致の行なら、ファイルの中の一致の番号 (ファイルの行なら −1)。</summary>
    public int MatchIndex { get; }

    /// <summary>一致の行なら一致 (ファイルの行なら null)。</summary>
    public SearchMatch? Match { get; }

    public bool IsFile => Match is null;

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>置換するか (ファイルの行は一致の行のまとめ。一部だけなら null)。</summary>
    [ObservableProperty]
    public partial bool? Checked { get; set; }

    [ObservableProperty]
    public partial Visibility CheckVisibility { get; set; }

    public Thickness Indent => IsFile ? new Thickness(0) : new Thickness(24, 0, 0, 0);

    public FontFamily Font => IsFile ? Normal : Mono;

    /// <summary>チェックの表示だけを変える (まとめの更新。変更の通知は出さない)。</summary>
    internal void ShowChecked(bool? value)
    {
        _syncing = true;
        Checked = value;
        _syncing = false;
    }

    partial void OnCheckedChanged(bool? value)
    {
        if (!_syncing)
        {
            _checkedChanged?.Invoke(this, value);
        }
    }
}

/// <summary>
/// 複数ファイル検索の結果の行の一覧。行は一覧が尋ねたときに作る (100 万件以上でも、一覧が仮想化して見えている行だけを尋ねる。
/// FIND-20 の「巨大ファイル・長時間処理」と同じ)。件数が変わったら <see cref="Reset"/> で 1 回だけ知らせる。
/// </summary>
public sealed class MultiFileRowList : IList, IReadOnlyList<MultiFileRow>, INotifyCollectionChanged
{
    /// <summary>覚えておく行の数の上限 (超えたら捨てて作り直す)。</summary>
    internal const int CacheLimit = 2_000;

    private readonly Func<int, MultiFileRow> _create;
    private readonly Dictionary<int, MultiFileRow> _cache = [];

    public MultiFileRowList(Func<int, MultiFileRow> create) => _create = create;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count { get; private set; }

    /// <summary>これまでに作った行の数 (テスト用: 見えていない行を作っていないことの確認)。</summary>
    internal long CreatedRows { get; private set; }

    /// <summary>今覚えている行 (見えている行を含む)。</summary>
    internal IEnumerable<MultiFileRow> Realized => _cache.Values;

    public MultiFileRow this[int index]
    {
        get
        {
            if (!_cache.TryGetValue(index, out MultiFileRow? row))
            {
                if (_cache.Count >= CacheLimit)
                {
                    _cache.Clear();
                }

                row = _create(index);
                CreatedRows++;
                _cache[index] = row;
            }

            return row;
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <summary>件数・内容が変わった (行を作り直す)。</summary>
    public void Reset(int count)
    {
        Count = count;
        _cache.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value) => value is MultiFileRow r && r.Row < Count ? r.Row : -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator<MultiFileRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
