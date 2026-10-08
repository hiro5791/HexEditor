namespace HexEditor.Platform.Shell;

/// <summary>最近使ったファイルの 1 件 (09 の UI-32、01 の ENG-16)。</summary>
public sealed record RecentFileEntry(string Path, string? DisplayName = null)
{
    public string Title => DisplayName ?? System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 最近使ったファイルの一覧 (UI-32 の画面・ENG-16 の記録)。ジャンプリスト (UI-35) はこの一覧を読み、変わるたびに作り直す。
/// 一覧そのものは「最近使ったファイル」の機能 (files/session) が持つ。この口だけをここで定める。
/// </summary>
public interface IRecentFilesSource
{
    /// <summary>ピン留めしたファイル (UI-32)。</summary>
    IReadOnlyList<RecentFileEntry> Pinned { get; }

    /// <summary>最近使ったファイル (新しい順。ピン留めを含んでもよい)。</summary>
    IReadOnlyList<RecentFileEntry> Recent { get; }

    /// <summary>一覧が変わった。どのスレッドからも呼ばれる。</summary>
    event EventHandler? Changed;
}

public enum JumpListCategory
{
    Pinned,
    Recent,
    Tasks,
}

/// <summary>ジャンプリストの項目 1 つ。選ぶと <see cref="Arguments"/> を付けてアプリを起動する (UI-35 の仕様 3)。</summary>
public sealed record JumpListItem(JumpListCategory Category, string Title, string Arguments, string? FilePath, string? TaskId = null);

/// <summary>
/// ジャンプリストの内容 (09 の UI-35 の仕様 1): 「固定済み」、「最近使ったもの」(最大 10 件)、「タスク」。
/// 項目の起動は単一インスタンス (UI-15) により既存のプロセスに転送される。
/// </summary>
public static class JumpListPlan
{
    public const string EnabledKey = "shell.jumpList.enabled";

    public const int MaxRecent = 10;

    /// <summary>一覧の変更をまとめる時間 (仕様 4)。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>タスクの ID (タイトルのリソースキーは <c>JumpList_Task_&lt;ID&gt;</c>)。</summary>
    public static IReadOnlyList<(string Id, string Arguments)> Tasks { get; } =
    [
        // 新しいウィンドウ (AUTO-37 の --new-window。パスなしなら空の新しいウィンドウ)。
        ("NewWindow", "--new-window"),

        // 新規作成 (AUTO-37 の --new-document)。
        ("NewDocument", "--new-document"),

        // ディスクを開く (F2-09) はその機能を作るときに加える。
    ];

    /// <summary>パスを 1 つの引数にする (空白を含むパスを引用符で囲む。ファイル名の " は Windows では使えない)。</summary>
    public static string Quote(string path) => "\"" + path + "\"";

    public static IReadOnlyList<JumpListItem> Build(IRecentFilesSource source, Func<string, string> taskTitle)
    {
        var items = new List<JumpListItem>();
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentFileEntry entry in source.Pinned)
        {
            if (pinned.Add(entry.Path))
            {
                items.Add(new JumpListItem(JumpListCategory.Pinned, entry.Title, Quote(entry.Path), entry.Path));
            }
        }

        foreach (RecentFileEntry entry in source.Recent.Where(e => !pinned.Contains(e.Path))
            .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(MaxRecent))
        {
            items.Add(new JumpListItem(JumpListCategory.Recent, entry.Title, Quote(entry.Path), entry.Path));
        }

        foreach ((string id, string arguments) in Tasks)
        {
            items.Add(new JumpListItem(JumpListCategory.Tasks, taskTitle(id), arguments, null, id));
        }

        return items;
    }
}

/// <summary>
/// このプロセスで開いたファイルの一覧 (「最近使ったファイル」の機能ができるまでの代わり)。新しい順、最大 25 件。
/// files/session の一覧 (UI-32) ができたら、そちらの <see cref="IRecentFilesSource"/> に置き換える。
/// </summary>
public sealed class SessionRecentFiles : IRecentFilesSource
{
    private readonly object _lock = new();
    private readonly List<RecentFileEntry> _recent = [];

    public IReadOnlyList<RecentFileEntry> Pinned => [];

    public IReadOnlyList<RecentFileEntry> Recent
    {
        get
        {
            lock (_lock)
            {
                return [.. _recent];
            }
        }
    }

    public event EventHandler? Changed;

    public void Add(string path)
    {
        lock (_lock)
        {
            _recent.RemoveAll(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, new RecentFileEntry(path));
            if (_recent.Count > 25)
            {
                _recent.RemoveRange(25, _recent.Count - 25);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
