using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.Core.Commands;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using HexEditor.Core.Scripting.Hooks;
using HexEditor.Core.Scripting.Library;
using HexEditor.Core.Scripting.Repl;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services.Scripting;

/// <summary>
/// スクリプトの作成と管理 (08 の AUTO-19〜AUTO-28) のアプリ全体の状態: スクリプト一覧の元、ブレークポイント、フックの設定と実行、
/// コンソールの履歴、マクロ。実行そのものは <see cref="ScriptingService"/>。最初に使うときに作る (起動の経路でファイルを読まない)。
/// </summary>
internal static class ScriptWorkspace
{
    private static readonly Lazy<ScriptLibrary> LibraryLazy = new(CreateLibrary, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<BreakpointStore> BreakpointsLazy = new(() => new BreakpointStore(Path.Combine(ScriptingService.Folders.Scripts, "breakpoints.json")));
    private static readonly Lazy<FileHookConfigStore> HookStoreLazy = new(() => new FileHookConfigStore(Path.Combine(ScriptingService.Folders.Scripts, "hooks.json")));
    private static readonly Lazy<ReplHistory> HistoryLazy = new(() => new ReplHistory(Path.Combine(ScriptingService.Folders.Scripts, "console-history.json")));
    private static readonly Lazy<ScriptHookManager> HooksLazy = new(CreateHooks, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Dictionary<string, ScriptNode> Registered = new(StringComparer.Ordinal);
    private static DispatcherQueue? _queue;

    /// <summary>同梱のサンプルのフォルダ (AUTO-46)。</summary>
    public static string SamplesFolder => Path.Combine(AppContext.BaseDirectory, "Samples", "scripts");

    public static ScriptLibrary Library => LibraryLazy.Value;

    public static bool LibraryStarted { get; private set; }

    public static BreakpointStore Breakpoints => BreakpointsLazy.Value;

    public static FileHookConfigStore HookStore => HookStoreLazy.Value;

    public static ReplHistory History => HistoryLazy.Value;

    public static ScriptHookManager Hooks => HooksLazy.Value;

    public static MacroService Macros { get; } = new();

    /// <summary>テストでごみ箱ではなく完全に削除する (テストがごみ箱を汚さないように)。</summary>
    public static bool DeleteWithoutRecycleBin { get; set; }

    /// <summary>スクリプトのコマンドの登録が変わった (登録した窓に処理を足す)。</summary>
    public static event EventHandler? CommandsChanged;

    private static ScriptLibrary CreateLibrary()
    {
        var library = ScriptLibrary.Create(ScriptingService.Folders, ExtraFolders, () => Directory.Exists(SamplesFolder) ? SamplesFolder : null);
        library.Language = CurrentLanguage();
        return library;
    }

    private static string CurrentLanguage() => System.Globalization.CultureInfo.CurrentUICulture.Name;

    /// <summary>設定「スクリプトのフォルダ」(AUTO-19 の仕様 1)。</summary>
    public static IReadOnlyList<string> ExtraFolders() =>
        App.Settings.GetNode("scripts.folders") is JsonArray array
            ? [.. array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>()]
            : [];

    private static ScriptHookManager CreateHooks()
    {
        var manager = new ScriptHookManager(ScriptingService.Host, () => ScriptingService.Environment, HookStore, () => Hosting.Program.CommandLine.SafeMode);
        manager.Notice += (_, notice) => _queue?.TryEnqueue(() => WindowManager.Current.ShowHookNotice(notice));
        manager.ConfirmSaveAfterFailure = notice => ConfirmSaveAfterFailureAsync(notice);
        return manager;
    }

    private static Task<bool> ConfirmSaveAfterFailureAsync(HookNotice notice)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue is null || !_queue.TryEnqueue(async () => tcs.TrySetResult(await WindowManager.Current.ConfirmSaveAfterHookFailureAsync(notice))))
        {
            tcs.TrySetResult(true);
        }

        return tcs.Task;
    }

    /// <summary>
    /// スクリプト一覧の監視を始め、一覧のスクリプトをコマンドとして登録する (最初のウィンドウが表示された後に 1 度。AUTO-19 の仕様 4)。
    /// 走査は別のスレッドで行い、起動を遅らせない。
    /// </summary>
    public static void Start(DispatcherQueue queue)
    {
        if (LibraryStarted)
        {
            return;
        }

        LibraryStarted = true;
        _queue = queue;
        s_foldersSnapshot = string.Join('|', ExtraFolders());
        _ = Task.Run(() =>
        {
            ScriptLibrary library = Library;
            library.Changed += (_, _) => queue.TryEnqueue(SyncCommands);
            library.Start();
        });
    }

    private static string s_foldersSnapshot = string.Empty;

    /// <summary>
    /// 設定「スクリプトのフォルダ」が変わったら一覧を作り直す。設定の変更の購読はウィンドウごとに持つ (ウィンドウを閉じると外れるように。
    /// アプリ全体の購読を足すと、ウィンドウを閉じても残る購読になる)。
    /// </summary>
    public static void RefreshFoldersIfChanged()
    {
        string snapshot = string.Join('|', ExtraFolders());
        if (!LibraryStarted || snapshot == s_foldersSnapshot)
        {
            return;
        }

        s_foldersSnapshot = snapshot;
        _ = Task.Run(Library.Refresh);
    }

    // ---- スクリプトのコマンド (AUTO-19 の仕様 4、09 の UI-16 の仕様 3) ----

    /// <summary>一覧のスクリプトをコマンド登録に反映する (UI スレッド)。</summary>
    public static void SyncCommands()
    {
        CommandCatalog catalog = CommandService.Catalog;
        var wanted = Library.CommandScripts.ToDictionary(ScriptCommandIds.For, n => n, StringComparer.Ordinal);
        foreach (string id in Registered.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            catalog.Unregister(id);
            Registered.Remove(id);
        }

        foreach ((string id, ScriptNode node) in wanted)
        {
            string text = node.Name;
            if (Registered.TryGetValue(id, out ScriptNode? existing))
            {
                if (catalog.Find(id)?.Text == text)
                {
                    Registered[id] = node;
                    continue;
                }

                // 表示名が変わった (メタデータの @name の変更・表示言語): 定義を作り直す (ショートカットは ID に付いている)。
                catalog.Unregister(id);
            }

            Registered[id] = node;
            catalog.Register(new CommandDefinition(id, "script") { Text = text });
        }

        CommandsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>ウィンドウにスクリプトのコマンドの処理を登録する (ウィンドウを作ったとき、スクリプトが増えたとき)。</summary>
    public static void AttachWindow(CommandHost host)
    {
        foreach (string id in Registered.Keys)
        {
            if (!host.HasHandler(id))
            {
                string key = id;
                host.Register(id, () => RunCommand(key), ScriptCommandState);
            }
        }
    }

    private static CommandState ScriptCommandState() =>
        ScriptingService.PolicyDisabled ? CommandState.Unavailable(Loc.Get("Script_DisabledByPolicy")) : CommandState.Available;

    private static void RunCommand(string id)
    {
        if (Registered.TryGetValue(id, out ScriptNode? node))
        {
            Run(node);
        }
    }

    /// <summary>一覧のスクリプトを実行する (Enter・ダブルクリック・コマンド)。マクロなら「直前のマクロ」にする。</summary>
    public static ScriptRun Run(ScriptNode node)
    {
        if (node.IsMacro)
        {
            Macros.SetLast(string.Empty, node.Path);
        }

        return ScriptingService.Start(new ScriptRunRequest { Path = node.Path });
    }

    /// <summary>コマンド ID から一覧のスクリプトを探す。</summary>
    public static ScriptNode? NodeOfCommand(string id) => Registered.GetValueOrDefault(id);

    /// <summary>スクリプトのコマンド ID (割り当てたショートカットを探す)。</summary>
    public static string CommandIdOf(ScriptNode node) => ScriptCommandIds.For(node);

    /// <summary>ごみ箱へ移動する (AUTO-19 の仕様 3)。</summary>
    public static void DeleteToRecycleBin(string path)
    {
        if (DeleteWithoutRecycleBin)
        {
            File.Delete(path);
            return;
        }

        Platform.Shell.RecycleBin.Delete(path);
    }
}
