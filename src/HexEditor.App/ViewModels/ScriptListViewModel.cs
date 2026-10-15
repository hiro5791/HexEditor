using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Library;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.ViewModels;

/// <summary>スクリプト一覧の 1 行 (AUTO-19 の仕様 2): 名前、説明、言語、割り当てたショートカット、常駐中かどうか。</summary>
public sealed partial class ScriptListItem : ObservableObject
{
    public ScriptListItem(ScriptNode node, string displayName)
    {
        Node = node;
        DisplayName = displayName;
    }

    public ScriptNode Node { get; }

    public string Path => Node.Path;

    /// <summary>表示名 (スクリプトはメタデータの <c>@name</c> かファイル名、根は「スクリプト」「マクロ」など)。</summary>
    public string DisplayName { get; }

    public bool IsScript => Node.Type == ScriptNodeType.Script;

    public bool IsFolder => Node.Type == ScriptNodeType.Folder;

    public bool IsGroup => Node.Type == ScriptNodeType.Root;

    public string Description => Node.Description ?? string.Empty;

    /// <summary>言語の短い表示 (JavaScript は JS、C# は C#)。</summary>
    public string LanguageText => IsScript ? (Node.Language == ScriptLanguage.CSharp ? "C#" : "JS") : string.Empty;

    /// <summary>フォルダが見つからない (AUTO-19 の「エラー」)。</summary>
    public bool IsMissing => IsGroup && Node.Missing;

    /// <summary>メタデータの <c>@on</c> を書いたスクリプト (自動実行の候補。AUTO-25 の仕様 4)。</summary>
    public bool IsHookCandidate => IsScript && Node.HookCandidates.Count > 0;

    public string HookCandidateText => IsHookCandidate ? Loc.Format("ScriptList_HookCandidate", string.Join(", ", Node.HookCandidates)) : string.Empty;

    [ObservableProperty]
    public partial string ShortcutText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResidentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<ScriptListItem> Children { get; } = [];

    /// <summary>読み上げの名前。</summary>
    public string AccessibleName => string.Join(", ", new[]
    {
        DisplayName, LanguageText, Description, ShortcutText, ResidentText, HookCandidateText, IsMissing ? Loc.Get("ScriptList_FolderMissing") : string.Empty,
    }.Where(s => s.Length > 0));

    partial void OnShortcutTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    partial void OnResidentTextChanged(string value) => OnPropertyChanged(nameof(AccessibleName));
}

/// <summary>
/// スクリプト一覧 (AUTO-19)。利用者のスクリプト (サブフォルダはツリー)・マクロ・利用者が追加したフォルダ・サンプルを木で見せる。
/// 一覧の元 (<see cref="ScriptLibrary"/>) の変化、ショートカットの変更、常駐の状態の変化に追従する。
/// </summary>
public sealed partial class ScriptListViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScriptListItem> _all = [];
    private readonly HashSet<ScriptRun> _watched = [];
    private bool _rebuildQueued;
    private bool _disposed;

    public ScriptListViewModel(DispatcherQueue queue)
    {
        _queue = queue;
        ScriptWorkspace.Library.Changed += OnLibraryChanged;
        CommandService.Keys.Changed += OnKeysChanged;
        ScriptingService.Host.RunStarted += OnRunStarted;
        ScriptingService.Host.RunningChanged += OnRunningChanged;
        foreach (ScriptRun run in ScriptingService.Host.Running)
        {
            Watch(run);
        }

        Rebuild();
    }

    /// <summary>木の最上位。利用者のスクリプトの根は省き (中の項目を直接並べる)、マクロ・追加フォルダ・サンプルは根の見出しの下に置く。</summary>
    public ObservableCollection<ScriptListItem> Items { get; } = [];

    /// <summary>絞り込みの文字列 (名前・説明・ファイル名)。</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>項目を作り直した (選択の復元・テスト用)。</summary>
    public event EventHandler? Rebuilt;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>すべての項目 (木を平らにしたもの。テスト・検索用)。</summary>
    public IReadOnlyList<ScriptListItem> AllItems => _all;

    private void OnLibraryChanged(object? sender, EventArgs e) => QueueRebuild();

    private void OnKeysChanged(object? sender, EventArgs e) => _queue.TryEnqueue(UpdateShortcuts);

    private void OnRunStarted(object? sender, ScriptRun run) => _queue.TryEnqueue(() => Watch(run));

    private void OnRunningChanged(object? sender, EventArgs e) => _queue.TryEnqueue(UpdateResident);

    private void Watch(ScriptRun run)
    {
        if (_disposed || !_watched.Add(run))
        {
            return;
        }

        run.Changed += (_, _) => _queue.TryEnqueue(UpdateResident);
        _ = run.Completion.ContinueWith(_ => _queue.TryEnqueue(() =>
        {
            _watched.Remove(run);
            UpdateResident();
        }), TaskScheduler.Default);
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued || _disposed)
        {
            return;
        }

        _rebuildQueued = true;
        _queue.TryEnqueue(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>一覧の元から木を作り直す。展開の状態は保つ。</summary>
    public void Rebuild()
    {
        if (_disposed)
        {
            return;
        }

        foreach (ScriptListItem item in _all.Where(i => i.IsExpanded))
        {
            _expanded.Add(item.Path);
        }

        foreach (ScriptListItem item in _all.Where(i => !i.IsExpanded && (i.IsFolder || i.IsGroup)))
        {
            _expanded.Remove(item.Path);
        }

        _all.Clear();
        Items.Clear();
        foreach (ScriptNode root in ScriptWorkspace.Library.Tree)
        {
            if (root.Root.Kind == ScriptRootKind.Scripts)
            {
                // 利用者のスクリプトは根を省いて直接並べる (サブフォルダはツリー)。
                if (root.Missing)
                {
                    Items.Add(Make(root, Loc.Get("ScriptList_Scripts")));
                    continue;
                }

                foreach (ScriptNode child in root.Children)
                {
                    if (Build(child) is { } item)
                    {
                        Items.Add(item);
                    }
                }

                continue;
            }

            if (Build(root) is { } group)
            {
                Items.Add(group);
            }
        }

        UpdateShortcuts();
        UpdateResident();
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    private ScriptListItem Make(ScriptNode node, string name)
    {
        var item = new ScriptListItem(node, name) { IsExpanded = _expanded.Contains(node.Path) };
        _all.Add(item);
        return item;
    }

    private string NameOf(ScriptNode node) => node.Type == ScriptNodeType.Root
        ? node.Root.Kind switch
        {
            ScriptRootKind.Scripts => Loc.Get("ScriptList_Scripts"),
            ScriptRootKind.Macros => Loc.Get("ScriptList_Macros"),
            ScriptRootKind.Samples => Loc.Get("ScriptList_Samples"),
            _ => node.Name,
        }
        : node.Name;

    /// <summary>節を項目にする。絞り込み中は、一致しない項目と、一致する項目を含まないフォルダを省く。</summary>
    private ScriptListItem? Build(ScriptNode node)
    {
        bool filtering = Filter.Length > 0;
        if (node.Type == ScriptNodeType.Script)
        {
            return filtering && !Matches(node) ? null : Make(node, NameOf(node));
        }

        var folder = new List<ScriptListItem>();
        foreach (ScriptNode child in node.Children)
        {
            if (Build(child) is { } item)
            {
                folder.Add(item);
            }
        }

        // 絞り込み中は一致する項目のないフォルダ・根を出さない。ただし見つからない根は、絞り込みなしなら出す (理由を示すため)。
        if (filtering && folder.Count == 0)
        {
            return null;
        }

        ScriptListItem result = Make(node, NameOf(node));
        foreach (ScriptListItem item in folder)
        {
            result.Children.Add(item);
        }

        return result;
    }

    private bool Matches(ScriptNode node) =>
        node.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
        || (node.Description?.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || node.FileName.Contains(Filter, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>割り当てたショートカットを項目に反映する (AUTO-19 の仕様 2)。</summary>
    public void UpdateShortcuts()
    {
        foreach (ScriptListItem item in _all.Where(i => i.IsScript && i.Node.IsCommand))
        {
            string id = ScriptCommandIds.For(item.Node);
            item.ShortcutText = string.Join(", ", CommandService.Keys.BindingsFor(id).Select(b => b.Binding.ToString()));
        }
    }

    /// <summary>常駐中 (イベントを購読して動いている) の項目に「常駐」を出す (AUTO-06 の仕様 4)。</summary>
    public void UpdateResident()
    {
        HashSet<string> resident = new(ScriptingService.Host.Running.Where(r => r.IsResident && r.Path is not null)
            .Select(r => Path.GetFullPath(r.Path!)), StringComparer.OrdinalIgnoreCase);
        foreach (ScriptListItem item in _all.Where(i => i.IsScript))
        {
            item.ResidentText = resident.Contains(Path.GetFullPath(item.Path)) ? Loc.Get("ScriptList_Resident") : string.Empty;
        }
    }

    /// <summary>パスから項目を探す。</summary>
    public ScriptListItem? Find(string path) => _all.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _disposed = true;
        ScriptWorkspace.Library.Changed -= OnLibraryChanged;
        CommandService.Keys.Changed -= OnKeysChanged;
        ScriptingService.Host.RunStarted -= OnRunStarted;
        ScriptingService.Host.RunningChanged -= OnRunningChanged;
    }
}
