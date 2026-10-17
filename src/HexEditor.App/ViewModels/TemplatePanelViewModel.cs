using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 結果ツリーパネル (TPL-21) の状態。ウィンドウごとに 1 つで、作業中のドキュメントの適用 (<see cref="DocumentTemplates"/>) を最上位の行にする。
/// 再評価の後も、開閉状態と選択中のノードをパスで復元する (TPL-20 の仕様 5)。評価中は 0.5 秒ごとに評価済みの部分を反映する (TPL-28 の仕様 6)。
/// </summary>
public sealed partial class TemplatePanelViewModel : ObservableObject
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private readonly Dictionary<int, TemplateTreeItem> _roots = [];
    private DocumentTemplates? _templates;
    private bool _loading;

    public TemplatePanelViewModel(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        _queue = queue;
        Rows = new TemplateRowList(Model, Layout) { ApplicationNameOf = ApplicationName };
        Rows.NeedsData += (_, row) => QueueLoad(row.Item);
        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.Tick += (_, _) => RefreshRunning();
        _followCursor = Commands.CommandService.State?.Get(TemplateService.FollowCursorStateKey)?.GetValue<bool>() ?? true;
    }

    public TemplateTreeModel Model { get; } = new();

    public TemplateColumnLayout Layout { get; } = new();

    public TemplateRowList Rows { get; }

    /// <summary>今の対象 (作業中のドキュメントの適用)。</summary>
    internal DocumentTemplates? Templates => _templates;

    /// <summary>選んでいる行 (一覧が設定する)。</summary>
    [ObservableProperty]
    public partial TemplateTreeItem? SelectedItem { get; set; }

    /// <summary>選んでいる行のパス (パネル下部のパス表示。TPL-21 の仕様 6)。</summary>
    [ObservableProperty]
    public partial string PathText { get; set; } = string.Empty;

    /// <summary>評価の状態 (「評価済み: 12,345 パターン」「評価中…」)。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>エラー・警告の件数 (クリックで出力パネルを開く。TPL-27 の仕様 5)。</summary>
    [ObservableProperty]
    public partial string CountsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasApplications { get; set; }

    [ObservableProperty]
    public partial bool IsEvaluating { get; set; }

    /// <summary>上限に達した (「上限を上げて再実行」を出す。TPL-28 の仕様 3)。</summary>
    [ObservableProperty]
    public partial string LimitText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LimitReached { get; set; }

    [ObservableProperty]
    public partial bool FilterActive { get; set; }

    /// <summary>「再実行」ボタンを出す (設定「編集後に自動で再評価」がオフのとき。TPL-20 の仕様 5)。</summary>
    public bool ShowRerun => !TemplateService.AutoReevaluate;

    private bool _followCursor;

    /// <summary>「カーソルに追従」(既定でオン。TPL-23 の仕様 5)。</summary>
    public bool FollowCursor
    {
        get => _followCursor;
        set
        {
            if (SetProperty(ref _followCursor, value))
            {
                Commands.CommandService.State?.Set(TemplateService.FollowCursorStateKey, value);
            }
        }
    }

    /// <summary>行を選び直すように一覧に頼む (再評価の後の復元、パスでの移動、カーソルに追従)。</summary>
    public event EventHandler<TemplateTreeItem>? SelectRequested;

    /// <summary>適用の結果が変わった (色分けを描き直す)。</summary>
    public event EventHandler? ResultsChanged;

    /// <summary>上限に達した適用 (「上限を上げて再実行」)。</summary>
    internal TemplateApplicationEntry? LimitedEntry { get; private set; }

    // ───────────── 対象のドキュメント ─────────────

    internal void Attach(DocumentTemplates? templates)
    {
        if (ReferenceEquals(_templates, templates))
        {
            return;
        }

        if (_templates is not null)
        {
            _templates.Changed -= Templates_Changed;
            _templates.ResultChanged -= Templates_ResultChanged;
        }

        _templates = templates;
        if (templates is not null)
        {
            templates.Changed += Templates_Changed;
            templates.ResultChanged += Templates_ResultChanged;
        }

        Rebuild();
    }

    /// <summary>ウィンドウを閉じた。</summary>
    internal void Detach()
    {
        Attach(null);
        _refreshTimer.Stop();
    }

    private void Templates_Changed(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        // 同じ適用の行は、開閉の状態と選択を引き継ぐ (適用を加えた・解除したとき)
        var keys = new Dictionary<int, HashSet<string>>();
        foreach ((int id, TemplateTreeItem root) in _roots)
        {
            keys[id] = TemplateTreeModel.ExpandedKeys(root);
        }

        string? selected = SelectedItem is { Kind: TemplateTreeItemKind.Node } s ? s.Node!.Path : null;
        int? selectedId = SelectedItem is { } si ? EntryOf(si)?.Id : null;
        Model.Clear();
        _roots.Clear();
        foreach (TemplateApplicationEntry entry in _templates?.Entries ?? [])
        {
            if (entry.Result is { } result)
            {
                _roots[entry.Id] = Model.AddApplication(result, entry);
            }
        }

        HasApplications = _roots.Count > 0;
        UpdateStatus();
        _ = LoadRootsAsync(keys, selected, selectedId);
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadRootsAsync(Dictionary<int, HashSet<string>> keys, string? selected, int? selectedId)
    {
        foreach ((int id, TemplateTreeItem root) in _roots.ToList())
        {
            await Model.LoadRowDataAsync([root]);
            if (keys.TryGetValue(id, out HashSet<string>? k))
            {
                await Model.RestoreAsync(root, k);
            }
            else
            {
                await Model.ExpandAsync(root);
            }

            await Model.RefreshRowsAsync([root]);
            if (id == selectedId && selected is not null && await Model.RevealPathAsync(root, selected) is { } item)
            {
                SelectRequested?.Invoke(this, item);
            }
        }
    }

    private async void Templates_ResultChanged(object? sender, TemplateApplicationEntry entry)
    {
        if (entry.Result is not { } result)
        {
            return;
        }

        if (!_roots.TryGetValue(entry.Id, out TemplateTreeItem? root))
        {
            _roots[entry.Id] = Model.AddApplication(result, entry);
            HasApplications = true;
            if (_roots.Count == 1)
            {
                // 最初の適用は最上位の行を開いておく
                await Model.ExpandAsync(_roots[entry.Id]);
            }
        }
        else if (!ReferenceEquals(root.Result, result) && !result.Completion.IsCompleted)
        {
            // 再評価の途中: 終わるまで前の結果を出しておく (終わったら開閉の状態を復元して置き換える)
            _refreshTimer.Start();
            return;
        }
        else if (!ReferenceEquals(root.Result, result))
        {
            // 再評価: 開閉の状態と選択をパスで復元する (TPL-20 の仕様 5)
            string? selected = SelectedItem is { Kind: TemplateTreeItemKind.Node } s && ReferenceEquals(s.Result, root.Result) ? s.Node!.Path : null;
            bool selectedRoot = ReferenceEquals(SelectedItem, root);
            if (!Model.Applications.Contains(root))
            {
                return;
            }

            TemplateTreeItem replaced = await Model.ReplaceApplicationAsync(root, result);
            if (!Model.Applications.Contains(replaced))
            {
                return;
            }

            _roots[entry.Id] = replaced;
            await Model.LoadRowDataAsync([replaced], force: true);
            if (selected is not null && await Model.RevealPathAsync(replaced, selected) is { } item)
            {
                SelectRequested?.Invoke(this, item);
            }
            else if (selectedRoot)
            {
                SelectRequested?.Invoke(this, replaced);
            }
        }
        else
        {
            if (root.MaterializedChildren is null && result.Completion.IsCompleted)
            {
                await Model.ExpandAsync(root);
            }

            await Model.RefreshRowsAsync([root]);
        }

        UpdateStatus();
        ResultsChanged?.Invoke(this, EventArgs.Empty);
        if (_templates?.Entries.Any(e => e.Result is { Completion.IsCompleted: false }) == true)
        {
            _refreshTimer.Start();
        }
    }

    /// <summary>評価中の適用の、評価済みの部分を反映する (0.5 秒ごと)。</summary>
    private async void RefreshRunning()
    {
        bool running = false;
        foreach ((int id, TemplateTreeItem root) in _roots.ToList())
        {
            if (root.Result.Completion.IsCompleted)
            {
                continue;
            }

            running = true;
            if (root.MaterializedChildren is null)
            {
                await Model.ExpandAsync(root);
            }

            await Model.RefreshRunningAsync(root);
            if (root.Tag is TemplateApplicationEntry entry)
            {
                entry.Owner.Annotations.Invalidate(entry);
            }
        }

        UpdateStatus();
        if (!running)
        {
            _refreshTimer.Stop();
        }
    }

    // ───────────── 状態の表示 ─────────────

    internal void UpdateStatus()
    {
        var results = _roots.Values.Select(r => r.Result).ToList();
        if (results.Count == 0)
        {
            StatusText = string.Empty;
            CountsText = string.Empty;
            IsEvaluating = false;
            LimitReached = false;
            return;
        }

        IsEvaluating = results.Any(r => !r.Completion.IsCompleted);
        long patterns = results.Sum(r => r.PatternCount);
        // 上限で打ち切った結果があれば、途中までの表示であることを示す (TPL-26 の仕様 5、TPL-28)
        TemplateResult? stopped = IsEvaluating ? null : results.FirstOrDefault(r => r.State == TemplateRunState.LimitReached);
        StatusText = IsEvaluating ? Loc.Get("Tpl_Evaluating")
            : stopped is not null ? Loc.Format("Tpl_EvaluatedPartial", patterns.ToString("N0", CultureInfo.CurrentCulture), Loc.Get("Tpl_LimitKind_" + stopped.LimitReached))
            : Loc.Format("Tpl_Evaluated", patterns.ToString("N0", CultureInfo.CurrentCulture));
        int errors = results.Sum(r => r.ErrorCount);
        int warnings = results.Sum(r => r.WarningCount);
        CountsText = Loc.Format("Tpl_Counts", errors, warnings);
        TemplateTreeItem? limited = _roots.Values.FirstOrDefault(r => r.Result.State == TemplateRunState.LimitReached && r.Result.LimitReached == TemplateLimitKind.Patterns);
        LimitedEntry = limited?.Tag as TemplateApplicationEntry;
        LimitReached = limited is not null;
        LimitText = limited is null ? string.Empty
            : Loc.Format("Tpl_PatternLimit", limited.Result.Limits.Patterns.ToString("N0", CultureInfo.CurrentCulture));
    }

    private string ApplicationName(TemplateTreeItem item)
    {
        if (item.Tag is not TemplateApplicationEntry entry)
        {
            return item.Data?.Name ?? string.Empty;
        }

        string name = entry.DisplayName + " @ " + Rows.FormatNumber(entry.IsFollow ? entry.Application.FollowOffset : entry.Info.StartOffset);
        if (entry.IsFollow && entry.Info.TypeName is { } type)
        {
            name += " (" + type + ")";
        }

        if (entry.Application.IsUnsaved)
        {
            name += " " + Loc.Get("Tpl_Unsaved");
        }

        if (entry.Application.TemplateMissing)
        {
            name += " " + Loc.Get("Tpl_TemplateMissing");
        }

        return name;
    }

    // ───────────── 行の内容の読み込み ─────────────

    private readonly HashSet<TemplateTreeItem> _pendingLoads = [];
    private bool _loadQueued;

    private void QueueLoad(TemplateTreeItem item)
    {
        _pendingLoads.Add(item);
        if (_loadQueued)
        {
            return;
        }

        _loadQueued = true;
        _queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            _loadQueued = false;
            if (_loading)
            {
                QueueLoad(_pendingLoads.FirstOrDefault()!);
                return;
            }

            List<TemplateTreeItem> items = [.. _pendingLoads.Where(i => i is not null)];
            _pendingLoads.Clear();
            _loading = true;
            try
            {
                await Model.LoadRowDataAsync(items);
            }
            finally
            {
                _loading = false;
            }

            foreach (TemplateTreeItem i in items)
            {
                Rows.Existing(i)?.Update();
            }
        });
    }

    // ───────────── 操作 ─────────────

    public async Task ToggleAsync(TemplateTreeItem item)
    {
        if (item.IsExpanded)
        {
            Model.Collapse(item);
        }
        else
        {
            await Model.ExpandAsync(item);
        }
    }

    /// <summary>パスを入力して移動する (TPL-21 の仕様 6)。見つからなければ false (選択は変えない)。</summary>
    public async Task<bool> GoToPathAsync(string path)
    {
        foreach (TemplateTreeItem root in _roots.Values.ToList())
        {
            if (await Model.RevealPathAsync(root, path) is { } item)
            {
                SelectRequested?.Invoke(this, item);
                return true;
            }
        }

        return false;
    }

    /// <summary>ノードを選ぶ (テンプレートの値の検索の結果を選んだ。FIND-29 の仕様 3)。見つからなければ false。</summary>
    public async Task<bool> SelectNodeAsync(TemplateNode node)
    {
        foreach (TemplateTreeItem root in _roots.Values.ToList())
        {
            if (ReferenceEquals(root.Result, node.Result) && await Model.RevealNodeAsync(root, node) is { } item)
            {
                SelectRequested?.Invoke(this, item);
                return true;
            }
        }

        return false;
    }

    /// <summary>カーソル位置を含む最も深いパターンを選ぶ (TPL-23 の仕様 5)。</summary>
    public async Task FollowAsync(long offset)
    {
        if (!FollowCursor)
        {
            return;
        }

        foreach (TemplateTreeItem root in _roots.Values.ToList())
        {
            if (root.Tag is TemplateApplicationEntry { IsFollow: true })
            {
                continue;
            }

            TemplateResult result = root.Result;
            TemplateNode? node = await result.InvokeAsync(() => result.DeepestAt(offset));
            if (node is not null && await Model.RevealNodeAsync(root, node) is { } item)
            {
                SelectRequested?.Invoke(this, item);
                return;
            }
        }
    }

    /// <summary>絞り込み (名前・値・型。TPL-21 の仕様 5)。</summary>
    public async Task<int> FilterAsync(string text, bool evaluateAll, CancellationToken token = default)
    {
        int n = await Model.FilterAsync(text, evaluateAll, null, token);
        FilterActive = Model.IsFiltered;
        return n;
    }

    /// <summary>適用の行 (最上位) の一覧。</summary>
    internal IReadOnlyCollection<TemplateTreeItem> Roots => _roots.Values;

    /// <summary>行の適用 (絞り込みの結果の行は、同じ評価結果を持つ適用)。</summary>
    internal TemplateApplicationEntry? EntryOf(TemplateTreeItem item) =>
        _roots.Values.FirstOrDefault(r => ReferenceEquals(r.Result, item.Result))?.Tag as TemplateApplicationEntry;

    partial void OnSelectedItemChanged(TemplateTreeItem? value) =>
        PathText = value?.Kind == TemplateTreeItemKind.Node ? value.Node!.Path : string.Empty;
}
