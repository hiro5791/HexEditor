using System.Globalization;
using HexEditor.Core.Templates.Runtime;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>結果ツリーの行の種類。</summary>
public enum TemplateTreeItemKind
{
    /// <summary>適用 (最上位。TPL-21 の仕様 9)。</summary>
    Application,

    /// <summary>パターン。</summary>
    Node,

    /// <summary>1,000 要素ずつのグループ (TPL-21 の仕様 3)。</summary>
    Group,
}

/// <summary>
/// 結果ツリーの 1 行 (TPL-21)。子は開いたときに初めて作る (遅延)。<see cref="Data"/> は表示する内容の写しで、
/// <see cref="TemplateTreeModel.LoadRowDataAsync"/> で評価器のスレッドから読み込む。
/// </summary>
public sealed class TemplateTreeItem
{
    internal TemplateTreeItem(TemplateTreeItemKind kind, TemplateResult result, TemplateNode? node, TemplateTreeItem? parent, string key)
    {
        Kind = kind;
        Result = result;
        Node = node;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Key = key;
    }

    public TemplateTreeItemKind Kind { get; }

    public TemplateResult Result { get; internal set; }

    /// <summary>パターン (グループの行は null)。</summary>
    public TemplateNode? Node { get; internal set; }

    public TemplateTreeItem? Parent { get; }

    /// <summary>階層の深さ (適用の行が 0)。</summary>
    public int Depth { get; }

    /// <summary>開閉の状態を復元するための鍵 (パターンのパス。TPL-20 の仕様 5)。</summary>
    public string Key { get; }

    public bool IsExpanded { get; internal set; }

    /// <summary>子を持つ (評価せずに分かる範囲)。</summary>
    public bool HasChildren { get; internal set; }

    public TemplateRowData? Data { get; internal set; }

    /// <summary>検索の結果の行 (名前の列にパスを出す)。</summary>
    public bool ShowsPath { get; internal set; }

    /// <summary>適用の行に付けた値 (画面側の適用の情報)。</summary>
    public object? Tag { get; set; }

    /// <summary>グループ: 要素を持つ配列のノード、最初の添字、要素の数、1 つのグループの最大の要素数。</summary>
    public TemplateNode? GroupOwner { get; internal set; }

    public long GroupStart { get; internal set; }

    public long GroupCount { get; internal set; }

    internal long GroupSpan { get; set; }

    internal List<TemplateTreeItem>? Children { get; set; }

    /// <summary>開いたことがあり、子を作ってある (テスト用)。</summary>
    public IReadOnlyList<TemplateTreeItem>? MaterializedChildren => Children;

    /// <summary>この行と、開いている子孫の行の数。</summary>
    internal long VisibleCount { get; set; } = 1;

    internal int IndexInParent { get; set; }

    public override string ToString() => Kind == TemplateTreeItemKind.Group ? Key : Node?.Path ?? Key;
}

/// <summary>結果ツリーの行の変化 (一覧の表示に伝える)。</summary>
public enum TemplateTreeChangeAction
{
    Insert,
    Remove,
    Reset,

    /// <summary>行の内容 (<see cref="TemplateTreeItem.Data"/>・開閉の状態) が変わった。</summary>
    Update,
}

public sealed record TemplateTreeChange(TemplateTreeChangeAction Action, int Index, int Count);

/// <summary>
/// 結果ツリーの行のモデル (TPL-21)。適用ごとの最上位の行と、開いている部分だけの行を持つ (仮想化: 開いていない部分の行は作らない)。
/// 要素が 1,000 を超える子の一覧は 1,000 ずつのグループに分け、グループが 1,000 を超える場合はさらにまとめる (仕様 3)。
/// <para>
/// UI スレッドから使う (スレッドセーフではない)。子と行の内容は評価器のスレッドで作り (<see cref="TemplateResult.InvokeAsync{T}(Func{T})"/>)、
/// UI スレッドを止めない。
/// </para>
/// </summary>
public sealed class TemplateTreeModel
{
    /// <summary>1 つのグループの要素の数 (TPL-21 の仕様 3)。</summary>
    public const int GroupSize = 1000;

    /// <summary>「すべて評価して検索」・絞り込みの結果の上限。</summary>
    public const int MaxSearchResults = 10_000;

    private readonly List<TemplateTreeItem> _applications = [];
    private List<TemplateTreeItem>? _filter;

    /// <summary>値の表示の地域設定 (既定は現在の設定)。</summary>
    public TemplateDisplayContext DisplayContext { get; set; } = TemplateDisplayContext.Current;

    public event EventHandler<TemplateTreeChange>? Changed;

    /// <summary>適用の行 (最上位)。</summary>
    public IReadOnlyList<TemplateTreeItem> Applications => _applications;

    /// <summary>絞り込み中 (検索の結果を表示している)。</summary>
    public bool IsFiltered => _filter is not null;

    private List<TemplateTreeItem> Roots => _filter ?? _applications;

    /// <summary>表示している行の数。</summary>
    public int RowCount => (int)Math.Min(int.MaxValue, Roots.Sum(r => r.VisibleCount));

    // ───────────── 適用 ─────────────

    /// <summary>適用の行を加える (閉じた状態)。</summary>
    public TemplateTreeItem AddApplication(TemplateResult result, object? tag = null)
    {
        TemplateTreeItem item = NewApplicationItem(result, tag);
        item.IndexInParent = _applications.Count;
        _applications.Add(item);
        if (_filter is null)
        {
            Raise(TemplateTreeChangeAction.Insert, RowCount - 1, 1);
        }

        return item;
    }

    private static TemplateTreeItem NewApplicationItem(TemplateResult result, object? tag) =>
        new(TemplateTreeItemKind.Application, result, result.Root, null, "@app") { HasChildren = true, Tag = tag };

    public void RemoveApplication(TemplateTreeItem application)
    {
        int index = _applications.IndexOf(application);
        if (index < 0)
        {
            return;
        }

        int row = _filter is null ? IndexOf(application) : -1;
        int count = (int)application.VisibleCount;
        _applications.RemoveAt(index);
        Renumber(_applications);
        if (row >= 0)
        {
            Raise(TemplateTreeChangeAction.Remove, row, count);
        }
    }

    /// <summary>すべての行を消す。</summary>
    public void Clear()
    {
        _applications.Clear();
        _filter = null;
        Raise(TemplateTreeChangeAction.Reset, 0, 0);
    }

    /// <summary>開いている行の鍵 (再評価の後に復元するため。TPL-20 の仕様 5)。</summary>
    public static HashSet<string> ExpandedKeys(TemplateTreeItem application)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<TemplateTreeItem>();
        stack.Push(application);
        while (stack.Count > 0)
        {
            TemplateTreeItem item = stack.Pop();
            if (!item.IsExpanded)
            {
                continue;
            }

            keys.Add(item.Key);
            foreach (TemplateTreeItem child in item.Children ?? [])
            {
                stack.Push(child);
            }
        }

        return keys;
    }

    /// <summary>
    /// 適用の結果を新しいものに置き換え、開いていた行を鍵 (パス) で開き直す (TPL-20 の仕様 5)。置き換えた適用の行を返す。
    /// </summary>
    public async Task<TemplateTreeItem> ReplaceApplicationAsync(TemplateTreeItem application, TemplateResult result, IReadOnlySet<string>? expandedKeys = null)
    {
        int index = _applications.IndexOf(application);
        IReadOnlySet<string> keys = expandedKeys ?? ExpandedKeys(application);
        TemplateTreeItem replacement = NewApplicationItem(result, application.Tag);
        if (index < 0)
        {
            return replacement; // 待っている間に一覧から外れた (作り直した) 行は置き換えない
        }

        replacement.IndexInParent = index;
        _applications[index] = replacement;

        Raise(TemplateTreeChangeAction.Reset, 0, 0);
        await RestoreAsync(replacement, keys).ConfigureAwait(true);
        return replacement;
    }

    /// <summary>鍵が <paramref name="keys"/> にある行を開く (浅い順)。</summary>
    public async Task RestoreAsync(TemplateTreeItem item, IReadOnlySet<string> keys)
    {
        if (!keys.Contains(item.Key) || !item.HasChildren)
        {
            return;
        }

        await ExpandAsync(item, raise: false).ConfigureAwait(true);
        foreach (TemplateTreeItem child in item.Children?.ToList() ?? [])
        {
            await RestoreAsync(child, keys).ConfigureAwait(true);
        }

        if (item.Parent is null)
        {
            Raise(TemplateTreeChangeAction.Reset, 0, 0);
        }
    }

    /// <summary>
    /// 評価中の適用の行を最新にする (評価済みの部分を 0.5 秒ごとに反映する。TPL-28 の仕様 6)。最上位の子が増えただけなら行を足す。
    /// </summary>
    public async Task RefreshRunningAsync(TemplateTreeItem application)
    {
        TemplateResult result = application.Result;
        if (application.Children is not { } old || !application.IsExpanded)
        {
            await LoadRowDataAsync([application]).ConfigureAwait(true);
            Raise(TemplateTreeChangeAction.Update, IndexOf(application), 1);
            return;
        }

        List<TemplateTreeItem> fresh = await result.InvokeAsync(() => BuildChildren(application)).ConfigureAwait(true);
        if (!_applications.Contains(application) || !ReferenceEquals(application.Result, result))
        {
            return;
        }

        bool appendOnly = fresh.Count >= old.Count && old.All(o => o.Kind == TemplateTreeItemKind.Node)
            && fresh.Take(old.Count).Select(f => f.Key).SequenceEqual(old.Select(o => o.Key), StringComparer.Ordinal);
        await LoadRowDataAsync([application]).ConfigureAwait(true);
        if (appendOnly)
        {
            if (fresh.Count == old.Count)
            {
                Raise(TemplateTreeChangeAction.Update, IndexOf(application), 1);
                return;
            }

            List<TemplateTreeItem> added = fresh.Skip(old.Count).ToList();
            int row = IndexOf(application) + (int)application.VisibleCount;
            foreach (TemplateTreeItem a in added)
            {
                a.IndexInParent = old.Count;
                old.Add(a);
            }

            AdjustCounts(application, added.Count);
            Raise(TemplateTreeChangeAction.Insert, row, added.Count);
            return;
        }

        HashSet<string> keys = ExpandedKeys(application);
        application.Children = null;
        application.VisibleCount = 1;
        application.IsExpanded = false;
        await RestoreAsync(application, keys).ConfigureAwait(true);
        Raise(TemplateTreeChangeAction.Reset, 0, 0);
    }

    // ───────────── 行の位置 ─────────────

    /// <summary>表示している <paramref name="row"/> 番目の行。</summary>
    public TemplateTreeItem ItemAt(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        long remaining = row;
        IReadOnlyList<TemplateTreeItem> level = Roots;
        while (true)
        {
            bool found = false;
            foreach (TemplateTreeItem item in level)
            {
                if (remaining < item.VisibleCount)
                {
                    if (remaining == 0)
                    {
                        return item;
                    }

                    remaining--;
                    level = item.Children!;
                    found = true;
                    break;
                }

                remaining -= item.VisibleCount;
            }

            if (!found)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }
        }
    }

    /// <summary>行の位置 (表示していない行は -1)。</summary>
    public int IndexOf(TemplateTreeItem item)
    {
        long index = 0;
        TemplateTreeItem current = item;
        while (current.Parent is { } parent)
        {
            if (!parent.IsExpanded || parent.Children is null || current.IndexInParent >= parent.Children.Count
                || !ReferenceEquals(parent.Children[current.IndexInParent], current))
            {
                return -1;
            }

            index += 1;
            for (int i = 0; i < current.IndexInParent; i++)
            {
                index += parent.Children[i].VisibleCount;
            }

            current = parent;
        }

        List<TemplateTreeItem> roots = Roots;
        int top = current.IndexInParent;
        if (top >= roots.Count || !ReferenceEquals(roots[top], current))
        {
            return -1;
        }

        for (int i = 0; i < top; i++)
        {
            index += roots[i].VisibleCount;
        }

        return (int)Math.Min(int.MaxValue, index);
    }

    // ───────────── 開閉 ─────────────

    /// <summary>行を開く (子を作っていなければ評価器のスレッドで作る)。開けない行なら何もしない。</summary>
    public Task ExpandAsync(TemplateTreeItem item) => ExpandAsync(item, raise: true);

    private async Task ExpandAsync(TemplateTreeItem item, bool raise)
    {
        if (item.IsExpanded || !item.HasChildren)
        {
            return;
        }

        if (item.Children is null)
        {
            TemplateResult result = item.Result;
            List<TemplateTreeItem> children = await result.InvokeAsync(() => BuildChildren(item)).ConfigureAwait(true);
            if (item.IsExpanded || item.Children is not null)
            {
                return; // 待っている間に別の要求で開いた
            }

            if (children.Count == 0 && !result.Completion.IsCompleted && item.Kind == TemplateTreeItemKind.Application)
            {
                return; // 評価中でまだ子がない (評価が進んでから開き直す)
            }

            item.Children = children;
            if (children.Count == 0)
            {
                item.HasChildren = false;
                if (raise && IndexOf(item) is int r and >= 0)
                {
                    Raise(TemplateTreeChangeAction.Update, r, 1);
                }

                return;
            }
        }

        item.IsExpanded = true;
        long added = item.Children.Sum(c => c.VisibleCount);
        AdjustCounts(item, added);
        if (raise && IndexOf(item) is int row and >= 0)
        {
            Raise(TemplateTreeChangeAction.Update, row, 1);
            Raise(TemplateTreeChangeAction.Insert, row + 1, (int)Math.Min(int.MaxValue, added));
        }
    }

    /// <summary>行を閉じる (子の行は残し、開き直すとすぐ表示する)。</summary>
    public void Collapse(TemplateTreeItem item)
    {
        if (!item.IsExpanded)
        {
            return;
        }

        int row = IndexOf(item);
        long removed = item.VisibleCount - 1;
        item.IsExpanded = false;
        AdjustCounts(item, -removed);
        if (row >= 0)
        {
            Raise(TemplateTreeChangeAction.Update, row, 1);
            if (removed > 0)
            {
                Raise(TemplateTreeChangeAction.Remove, row + 1, (int)Math.Min(int.MaxValue, removed));
            }
        }
    }

    private static void AdjustCounts(TemplateTreeItem item, long delta)
    {
        for (TemplateTreeItem? p = item; p is not null; p = p.Parent)
        {
            p.VisibleCount += delta;
            if (!p.IsExpanded && !ReferenceEquals(p, item))
            {
                break;
            }
        }
    }

    // ───────────── 行の内容 ─────────────

    /// <summary>行の内容を評価器のスレッドで読み込む (まだ読み込んでいない行だけ。<paramref name="force"/> ならすべて)。</summary>
    public async Task LoadRowDataAsync(IEnumerable<TemplateTreeItem> items, bool force = false)
    {
        TemplateDisplayContext context = DisplayContext;
        foreach (IGrouping<TemplateResult, TemplateTreeItem> group in items.Where(i => force || i.Data is null).GroupBy(i => i.Result))
        {
            List<TemplateTreeItem> list = [.. group];
            TemplateRowData[] data = await group.Key.InvokeAsync(() => list.Select(i => BuildData(i, context)).ToArray()).ConfigureAwait(true);
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Data = data[i];
            }
        }
    }

    private static TemplateRowData BuildData(TemplateTreeItem item, TemplateDisplayContext context)
    {
        if (item.Kind == TemplateTreeItemKind.Group)
        {
            return item.Data ?? TemplateRowData.ForGroup(item.GroupStart, item.GroupCount, 0, 0);
        }

        TemplateRowData data = TemplateRowData.FromNode(item.Node!, context);
        return item.ShowsPath && data.Path.Length > 0 ? data with { Name = data.Path } : data;
    }

    /// <summary>行の内容を読み込み直す (値の編集・評価の進み)。</summary>
    public async Task RefreshRowsAsync(IEnumerable<TemplateTreeItem> items)
    {
        List<TemplateTreeItem> list = [.. items];
        await LoadRowDataAsync(list, force: true).ConfigureAwait(true);
        foreach (TemplateTreeItem item in list)
        {
            if (IndexOf(item) is int row and >= 0)
            {
                Raise(TemplateTreeChangeAction.Update, row, 1);
            }
        }
    }

    // ───────────── パスでの移動 ─────────────

    /// <summary>
    /// パスのノードの行を表示する (親の行を開く。TPL-21 の仕様 6)。見つからなければ null (選択は変えない)。未評価の部分は評価する。
    /// </summary>
    public async Task<TemplateTreeItem?> RevealPathAsync(TemplateTreeItem application, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        TemplateResult result = application.Result;
        TemplateNode? node = await result.InvokeAsync(() => result.FindByPath(path.Trim())).ConfigureAwait(true);
        return node is null ? null : await RevealNodeAsync(application, node).ConfigureAwait(true);
    }

    /// <summary>ノードの行を表示する (カーソルに追従。TPL-23 の仕様 5)。親の行を順に開く。</summary>
    public async Task<TemplateTreeItem?> RevealNodeAsync(TemplateTreeItem application, TemplateNode node)
    {
        TemplateResult result = application.Result;
        if (!ReferenceEquals(node.Result, result))
        {
            return null;
        }

        // 適用の行から順に、(ノードのパス, 親の子の一覧での位置)
        List<(string Path, long Index)> chain = await result.InvokeAsync(() => AncestorChain(node)).ConfigureAwait(true);
        if (chain.Count == 0)
        {
            return null;
        }

        TemplateTreeItem current = application;
        foreach ((string path, long index) in chain)
        {
            TemplateTreeItem? next = null;
            while (next is null)
            {
                await ExpandAsync(current).ConfigureAwait(true);
                if (current.Children is not { Count: > 0 } children)
                {
                    return null;
                }

                TemplateTreeItem? group = children.FirstOrDefault(c => c.Kind == TemplateTreeItemKind.Group
                    && index >= c.GroupStart && index < c.GroupStart + c.GroupCount);
                if (group is not null)
                {
                    current = group;
                    continue;
                }

                next = children.FirstOrDefault(c => c.Kind == TemplateTreeItemKind.Node && c.Key == path)
                    ?? (index >= 0 && index < children.Count && children[(int)index].Kind == TemplateTreeItemKind.Node ? children[(int)index] : null);
                if (next is null)
                {
                    return null;
                }
            }

            current = next;
        }

        return current;
    }

    /// <summary>適用の直下から <paramref name="node"/> までの (パス, 位置)。評価器のスレッドで呼ぶ。</summary>
    private static List<(string Path, long Index)> AncestorChain(TemplateNode node)
    {
        var chain = new List<(string, long)>();
        for (TemplateNode? n = node; n is not null && n.Kind != TemplateNodeKind.Application; n = n.Parent)
        {
            TemplateNode? parent = n.Parent;
            long index = -1;
            if (parent is not null)
            {
                if (n.ArrayIndex >= 0 && parent.Kind is TemplateNodeKind.Array)
                {
                    index = n.ArrayIndex;
                }
                else
                {
                    IReadOnlyList<TemplateNode> siblings = parent.Children;
                    if (siblings.Count <= 100_000)
                    {
                        string path = n.Path;
                        for (int i = 0; i < siblings.Count; i++)
                        {
                            if (ReferenceEquals(siblings[i], n) || siblings[i].Path == path)
                            {
                                index = i;
                                break;
                            }
                        }
                    }
                }
            }

            chain.Add((n.Path, index));
        }

        chain.Reverse();
        return chain;
    }

    // ───────────── 子の作成 (評価器のスレッド) ─────────────

    private static List<TemplateTreeItem> BuildChildren(TemplateTreeItem item)
    {
        var result = new List<TemplateTreeItem>();
        if (item.Kind == TemplateTreeItemKind.Group)
        {
            TemplateNode owner = item.GroupOwner!;
            IReadOnlyList<TemplateNode> kids = owner.Children;
            if (item.GroupSpan > GroupSize)
            {
                long sub = item.GroupSpan / GroupSize;
                for (long s = item.GroupStart; s < item.GroupStart + item.GroupCount; s += sub)
                {
                    result.Add(NewGroup(item, owner, kids, s, Math.Min(sub, item.GroupStart + item.GroupCount - s), sub));
                }
            }
            else
            {
                for (long i = item.GroupStart; i < item.GroupStart + item.GroupCount; i++)
                {
                    result.Add(NewNodeItem(item, kids[(int)i]));
                }
            }
        }
        else
        {
            TemplateNode node = item.Node!;

            // 子の一覧は 1 度だけ作る (構造体の子は平らにした一覧、配列の要素は参照したときに作る)
            IReadOnlyList<TemplateNode> kids = node.Children;
            long count = node.Kind == TemplateNodeKind.Pointer ? kids.Count : node.KnownChildCount ?? kids.Count;
            count = Math.Min(count, kids.Count);
            if (count > GroupSize)
            {
                long span = GroupSize;
                while ((count + span - 1) / span > GroupSize)
                {
                    span *= GroupSize;
                }

                for (long s = 0; s < count; s += span)
                {
                    result.Add(NewGroup(item, node, kids, s, Math.Min(span, count - s), span));
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    TemplateNode child = kids[i];
                    if (child.Attributes.Hidden || child.Attributes.HighlightHidden)
                    {
                        continue; // hidden は結果ツリーに出さない (TPL-13)
                    }

                    result.Add(NewNodeItem(item, child));
                }
            }
        }

        for (int i = 0; i < result.Count; i++)
        {
            result[i].IndexInParent = i;
        }

        return result;
    }

    private static TemplateTreeItem NewNodeItem(TemplateTreeItem parent, TemplateNode node) =>
        new(TemplateTreeItemKind.Node, parent.Result, node, parent, node.Path)
        {
            HasChildren = CanExpand(node),
        };

    /// <summary>開ける (子を持つ) ノードか。u8 / char の配列と <c>sealed</c> は開かない (TPL-07 の仕様 6、TPL-13)。</summary>
    public static bool CanExpand(TemplateNode node) =>
        node.HasChildren && !node.Attributes.Sealed && !node.IsByteOrCharArray && node.Kind != TemplateNodeKind.Error;

    private static TemplateTreeItem NewGroup(TemplateTreeItem parent, TemplateNode owner, IReadOnlyList<TemplateNode> kids, long start, long count, long span)
    {
        long offset = 0;
        long size = 0;
        try
        {
            TemplateNode first = kids[(int)start];
            TemplateNode last = kids[(int)(start + count - 1)];
            offset = first.Offset;
            size = Math.Max(0, last.End - first.Offset);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // 範囲が分からなければ 0 のまま
        }

        string key = owner.Path + "#" + start.ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(CultureInfo.InvariantCulture);
        return new TemplateTreeItem(TemplateTreeItemKind.Group, parent.Result, null, parent, key)
        {
            HasChildren = true,
            GroupOwner = owner,
            GroupStart = start,
            GroupCount = count,
            GroupSpan = span,
            Data = TemplateRowData.ForGroup(start, count, offset, size),
        };
    }

    // ───────────── 検索 (TPL-21 の仕様 5) ─────────────

    /// <summary>
    /// 名前・値・型に <paramref name="text"/> を含むノードを探して、結果を行にする (絞り込み)。<paramref name="evaluateAll"/> が false なら
    /// 評価済みのノードだけが対象。true なら未評価の部分も評価する (長時間処理。<paramref name="progress"/> に調べたノードの数)。
    /// 空の文字列で絞り込みを解く。
    /// </summary>
    public async Task<int> FilterAsync(string text, bool evaluateAll = false, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_filter is not null)
            {
                _filter = null;
                Raise(TemplateTreeChangeAction.Reset, 0, 0);
            }

            return 0;
        }

        var items = new List<TemplateTreeItem>();
        TemplateDisplayContext context = DisplayContext;
        foreach (TemplateTreeItem application in _applications.ToList())
        {
            TemplateResult result = application.Result;
            List<TemplateNode> found = await Task.Run(() => result.Invoke(() =>
                Search(result.Root, text.Trim(), evaluateAll, context, MaxSearchResults - items.Count, progress, cancellationToken)), cancellationToken)
                .ConfigureAwait(true);
            foreach (TemplateNode node in found)
            {
                var item = new TemplateTreeItem(TemplateTreeItemKind.Node, result, node, null, node.Path)
                {
                    HasChildren = CanExpand(node),
                    ShowsPath = true,
                    IndexInParent = items.Count,
                };
                items.Add(item);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _filter = items;
        Raise(TemplateTreeChangeAction.Reset, 0, 0);
        return items.Count;
    }

    /// <summary>評価器のスレッドで検索する。</summary>
    internal static List<TemplateNode> Search(TemplateNode root, string text, bool evaluateAll, TemplateDisplayContext context, int max,
        IProgress<long>? progress, CancellationToken token)
    {
        var found = new List<TemplateNode>();

        // 子の一覧と次に調べる位置の組を積む (1 億要素の配列でも、要素を積み上げない)
        var stack = new Stack<(IReadOnlyList<TemplateNode> List, int Next)>();
        stack.Push(([root], 0));
        long visited = 0;
        while (stack.Count > 0 && found.Count < max)
        {
            token.ThrowIfCancellationRequested();
            (IReadOnlyList<TemplateNode> list, int next) = stack.Pop();
            if (next >= list.Count)
            {
                continue;
            }

            stack.Push((list, next + 1));
            TemplateNode node = list[next];
            if (++visited % 4096 == 0)
            {
                progress?.Report(visited);
            }

            if (node.Kind != TemplateNodeKind.Application && Matches(node, text, context))
            {
                found.Add(node);
            }

            IReadOnlyList<TemplateNode> children = evaluateAll ? AllChildren(node) : EvaluatedChildren(node);
            if (children.Count > 0)
            {
                stack.Push((children, 0));
            }
        }

        progress?.Report(visited);
        return found;
    }

    private static bool Matches(TemplateNode node, string text, TemplateDisplayContext context)
    {
        if (node.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || node.TypeName.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Section)
        {
            return false;
        }

        try
        {
            return node.GetDisplay(context).Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>評価済みの子 (評価しない)。</summary>
    internal static IReadOnlyList<TemplateNode> EvaluatedChildren(TemplateNode node) => node.Children0 switch
    {
        ListChildren l => [.. l.Items],
        LazyChildren { Items: { } items } => [.. items],
        SequenceChildren s => [.. s.Items],
        OnDemandChildren od => od.Cached,
        PointerChildren { Resolved: true, Target: { } t } => [t],
        _ => [],
    };

    /// <summary>すべての子 (未評価なら評価する)。</summary>
    private static IReadOnlyList<TemplateNode> AllChildren(TemplateNode node)
    {
        if (node.Children0 is null || node.Kind == TemplateNodeKind.Pointer && node.Pointer is not { IsNull: false, OutOfRange: false })
        {
            return [];
        }

        if (node.Children0 is OnDemandChildren od)
        {
            return new OnDemandList(node, od.Count);
        }

        return node.Children;
    }

    /// <summary>オンデマンド配列の要素を順に作る一覧 (1 億要素でも全体を持たない)。</summary>
    private sealed class OnDemandList(TemplateNode node, long count) : IReadOnlyList<TemplateNode>
    {
        public TemplateNode this[int index] => node.GetChild(index);

        public int Count => (int)Math.Min(int.MaxValue, count);

        public IEnumerator<TemplateNode> GetEnumerator()
        {
            for (long i = 0; i < count; i++)
            {
                yield return node.GetChild(i);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ───────────── 内部 ─────────────

    private static void Renumber(List<TemplateTreeItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].IndexInParent = i;
        }
    }

    private void Raise(TemplateTreeChangeAction action, int index, int count) => Changed?.Invoke(this, new TemplateTreeChange(action, index, count));
}
