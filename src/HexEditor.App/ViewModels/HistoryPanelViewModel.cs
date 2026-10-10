using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>履歴パネルの 1 行 (EDIT-20 の仕様 1・2)。</summary>
public sealed class HistoryRow
{
    public required int Index { get; init; }

    /// <summary>番号 (履歴の項目の番号。1 が最初の編集)。</summary>
    public string NumberText => Index.ToString(CultureInfo.CurrentCulture);

    public required string Name { get; init; }

    /// <summary>対象範囲 (開始・長さ)。開いた時点の行では空。</summary>
    public required string RangeText { get; init; }

    /// <summary>長さの変化 (+N / −N / 0)。</summary>
    public required string DeltaText { get; init; }

    public required string TimeText { get; init; }

    /// <summary>保存点の行か (「保存」の印)。</summary>
    public required bool IsSaved { get; init; }

    /// <summary>現在の状態の行か (色に加えて太字と印で強調する)。</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>やり直しできる行 (現在より後ろ。薄く表示する)。</summary>
    public required bool IsRedo { get; init; }

    public string SavedText => IsSaved ? Loc.Get("History_SavedMark") : string.Empty;

    public string CurrentMark => IsCurrent ? "▶" : string.Empty;

    public Windows.UI.Text.FontWeight Weight => IsCurrent ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    public double Opacity => IsRedo ? 0.55 : 1;

    /// <summary>読み上げ (番号、操作名、範囲、変化、保存・現在の状態)。</summary>
    public string AccessibleName => string.Join(", ", new[] { NumberText, Name, RangeText, DeltaText, SavedText,
        IsCurrent ? Loc.Get("History_CurrentState") : string.Empty, IsRedo ? Loc.Get("History_RedoState") : string.Empty }.Where(s => s.Length > 0));
}

/// <summary>
/// 履歴の行の一覧。行は表示するときに作る (100 万項目以上でも、一覧が仮想化して見えている行だけを尋ねる。EDIT-20 の仕様 6)。
/// </summary>
public sealed class HistoryRowList : IList, IReadOnlyList<HistoryRow>, INotifyCollectionChanged
{
    private readonly Func<int, HistoryRow> _create;
    private readonly Dictionary<int, HistoryRow> _cache = [];

    public HistoryRowList(Func<int, HistoryRow> create) => _create = create;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count { get; private set; }

    public HistoryRow this[int index]
    {
        get
        {
            if (!_cache.TryGetValue(index, out HistoryRow? row))
            {
                if (_cache.Count > 2_000)
                {
                    _cache.Clear();
                }

                row = _create(index);
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

    /// <summary>件数・内容が変わった。</summary>
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

    public bool Contains(object? value) => value is HistoryRow r && r.Index < Count;

    public int IndexOf(object? value) => value is HistoryRow r && r.Index < Count ? r.Index : -1;

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

    public IEnumerator<HistoryRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 履歴パネル (EDIT-20)。ウィンドウごとに 1 つで、作業中の文書の編集履歴を表示する。行をダブルクリック (または Enter) すると、その行の
/// 編集を適用した直後の状態に移る (仕様 3)。
/// </summary>
public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private readonly MainViewModel _vm;
    private DocumentViewModel? _doc;

    public HistoryPanelViewModel(MainViewModel vm)
    {
        _vm = vm;
        Rows = new HistoryRowList(CreateRow);
    }

    public HistoryRowList Rows { get; }

    /// <summary>
    /// 「2 つの時点を比較」(仕様 5) の処理。比較の機能 (06) が設定する。null なら比較のボタンを無効にする。引数は 2 時点の内容と名前。
    /// </summary>
    public static Func<DocumentViewModel, DocumentSnapshot, DocumentSnapshot, string, string, Task>? CompareSnapshots { get; set; }

    /// <summary>作業中の文書が変わった (パネルの文脈から呼ぶ)。</summary>
    public void Attach(DocumentViewModel? doc)
    {
        if (ReferenceEquals(doc, _doc))
        {
            return;
        }

        if (_doc is not null)
        {
            _doc.Document.History.Changed -= History_Changed;
        }

        _doc = doc;
        if (_doc is not null)
        {
            _doc.Document.History.Changed += History_Changed;
        }

        Refresh();
    }

    public void Detach() => Attach(null);

    private void History_Changed(object? sender, EventArgs e) =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(Refresh);

    /// <summary>現在の状態の行の番号。</summary>
    public int CurrentIndex => _doc?.Document.History.CurrentIndex ?? -1;

    public bool CanUndo => _doc is { } d && d.Document.History.CanUndo && !d.Document.IsReadOnly;

    public bool CanRedo => _doc is { } d && d.Document.History.CanRedo && !d.Document.IsReadOnly;

    public bool CanCompare => CompareSnapshots is not null && _doc is not null;

    /// <summary>行の件数が変わった、または現在の位置が変わったので一覧を作り直す。</summary>
    public void Refresh()
    {
        // 開いた時点 (項目 0) は行にしない。行 i は項目 i + 1 (番号 1 が最初の編集)。
        Rows.Reset(Math.Max(0, (_doc?.Document.History.Count ?? 1) - 1));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanCompare));
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一覧を作り直した (現在の行を見える位置に出す)。</summary>
    public event EventHandler? Refreshed;

    /// <summary>行の編集を適用した直後の状態に移る (仕様 3)。</summary>
    public void GoTo(int index)
    {
        if (_doc is { } doc && !doc.Document.IsReadOnly && !doc.Document.IsEditLocked)
        {
            doc.Document.MoveToHistory(index);
        }
    }

    public void Undo() => _doc?.Editor.Undo();

    public void Redo() => _doc?.Editor.Redo();

    /// <summary>「この範囲へ移動」(仕様 4): その編集の対象範囲を選択してスクロールする (状態は変えない)。</summary>
    public void SelectRangeOf(int index)
    {
        if (_doc is not { } doc || index <= 0 || index >= doc.Document.History.Count)
        {
            return;
        }

        HistoryEntry entry = doc.Document.History.Entries[index];
        if (entry.Range is { } r)
        {
            // 現在の内容の上での範囲 (やり直せる項目なら編集前、適用済みなら編集後の長さ)。
            long length = index <= doc.Document.History.CurrentIndex ? r.AfterLength : r.BeforeLength;
            doc.Editor.RecordJump();
            doc.Editor.Select(Math.Min(r.Offset, doc.Document.Length), Math.Max(0, length));
        }
    }

    /// <summary>「2 つの時点を比較」(仕様 5)。</summary>
    public async Task CompareAsync(int first, int second)
    {
        if (_doc is not { } doc || CompareSnapshots is not { } compare)
        {
            return;
        }

        EditHistory history = doc.Document.History;
        (int a, int b) = (Math.Min(first, second), Math.Max(first, second));
        await compare(doc, history.Entries[a].Snapshot, history.Entries[b].Snapshot, Loc.Format("History_PointName", a), Loc.Format("History_PointName", b));
    }

    private HistoryRow CreateRow(int row)
    {
        int index = row + 1;
        if (_doc is not { } doc || index >= doc.Document.History.Count)
        {
            return new HistoryRow
            {
                Index = index, Name = string.Empty, RangeText = string.Empty, DeltaText = string.Empty, TimeText = string.Empty,
                IsSaved = false, IsCurrent = false, IsRedo = false,
            };
        }

        EditHistory history = doc.Document.History;
        HistoryEntry entry = history.Entries[index];
        CultureInfo culture = CultureInfo.CurrentCulture;
        string range = string.Empty, delta = string.Empty;
        if (entry.Range is { } r)
        {
            long length = Math.Max(r.BeforeLength, r.AfterLength);
            range = Loc.Format("History_Range", doc.Editor.OffsetFormat.Value(r.Offset, culture), length.ToString("N0", culture));
            long change = r.AfterLength - r.BeforeLength;
            delta = change > 0 ? "+" + change.ToString("N0", culture) : change < 0 ? "−" + (-change).ToString("N0", culture) : "0";
        }

        return new HistoryRow
        {
            Index = index,
            Name = MainWindow.EditOperationName(entry.Description),
            RangeText = range,
            DeltaText = delta,
            TimeText = entry.Time == default ? string.Empty : entry.Time.ToLocalTime().ToString("T", culture),
            IsSaved = index == history.SavedIndex,
            IsCurrent = index == history.CurrentIndex,
            IsRedo = index > history.CurrentIndex,
        };
    }

    /// <summary>テスト用の命令の通り道に返す一覧。</summary>
    internal JsonObject TestModel()
    {
        var rows = new JsonArray();
        for (int i = 0; i < Rows.Count; i++)
        {
            HistoryRow row = Rows[i];
            rows.Add(new JsonObject
            {
                ["index"] = row.Index,
                ["name"] = row.Name,
                ["range"] = row.RangeText,
                ["delta"] = row.DeltaText,
                ["saved"] = row.IsSaved,
                ["current"] = row.IsCurrent,
                ["redo"] = row.IsRedo,
                ["bold"] = row.Weight.Weight == Microsoft.UI.Text.FontWeights.Bold.Weight,
                ["opacity"] = row.Opacity,
            });
        }

        return new JsonObject { ["rows"] = rows, ["current"] = CurrentIndex };
    }
}
