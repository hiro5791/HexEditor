using System.Text.Json;

namespace HexEditor.Core.Files;

/// <summary>タブの種類 (UI-31 の仕様 1)。プロセスメモリはセッション・閉じたタブに記録しない (仕様 5、UI-12 の仕様 6)。</summary>
public enum SessionTabKind
{
    File,
    Disk,
    Untitled,
    Process,
}

/// <summary>タブ 1 つの記録 (UI-31 の仕様 1 のタブの単位)。</summary>
public sealed record SessionTab
{
    public SessionTabKind Kind { get; init; } = SessionTabKind.File;

    /// <summary>ファイルのパス (またはデバイスの識別子)。無題なら null。</summary>
    public string? Path { get; init; }

    /// <summary>タブの見出し (遅延して開くタブでもすぐに出す。UI-31 の仕様 6)。</summary>
    public required string DisplayName { get; init; }

    public bool Pinned { get; init; }

    public bool ReadOnly { get; init; }

    public long Cursor { get; init; }

    public long SelectionStart { get; init; }

    public long SelectionLength { get; init; }

    public long TopRow { get; init; }

    /// <summary>記録したときのファイルの長さと更新日時 (前回の終了後に変わったかを調べる。UI-31 の仕様 7)。</summary>
    public long Length { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>文書ごとの表示設定 (表示の担当が決める形。持たなければ null)。</summary>
    public JsonElement? View { get; init; }
}

/// <summary>ウィンドウ 1 つの記録 (UI-31 の仕様 1 のウィンドウの単位)。位置と大きさは物理ピクセル。</summary>
public sealed record SessionWindow
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool Maximized { get; init; }

    public bool FullScreen { get; init; }

    /// <summary>モニターの識別 (表示名)。そのモニターがなければ主モニターに出す。</summary>
    public string? Monitor { get; init; }

    /// <summary>アクティブなタブの番号 (<see cref="Tabs"/> の中)。タブがなければ -1。</summary>
    public int ActiveTab { get; init; } = -1;

    public List<SessionTab> Tabs { get; init; } = [];

    /// <summary>パネルの配置と大きさ (パネルの担当が決める形)。</summary>
    public JsonElement? Panels { get; init; }
}

/// <summary>閉じたタブの記録 (UI-12 の仕様 1): タブの記録と、元の並び位置。</summary>
public sealed record ClosedTab
{
    public required SessionTab Tab { get; init; }

    /// <summary>閉じたときのタブの並び位置。</summary>
    public int Index { get; init; }

    public DateTime ClosedAtUtc { get; init; }
}

/// <summary>session.json の内容 (UI-31)。</summary>
public sealed record SessionState
{
    public int Version { get; init; } = 1;

    public List<SessionWindow> Windows { get; init; } = [];

    /// <summary>閉じたタブ (新しい順。UI-12 の仕様 5)。</summary>
    public List<ClosedTab> ClosedTabs { get; init; } = [];

    /// <summary>最後にアクティブだったウィンドウの番号。</summary>
    public int LastActiveWindow { get; init; }

    /// <summary>復元するタブがあるか (無題・プロセスを除く)。</summary>
    public bool HasRestorableTabs => Windows.Any(w => w.Tabs.Any(SessionRules.IsRestorable));
}

/// <summary>セッションに記録する・復元するタブの規則 (UI-31 の仕様 4・5)。</summary>
public static class SessionRules
{
    /// <summary>復元するタブか: プロセスメモリ・無題 (内容は終了時の確認で保存されている) は復元しない。</summary>
    public static bool IsRestorable(SessionTab tab) => tab.Kind is SessionTabKind.File or SessionTabKind.Disk && tab.Path is not null;
}

/// <summary>
/// 閉じたタブの記録 (UI-12)。アプリ全体で新しい順に最大 <see cref="Capacity"/> 件。プロセスメモリと無題のタブは記録しない。
/// </summary>
public sealed class ClosedTabHistory
{
    public const int Capacity = 20;

    private readonly List<ClosedTab> _items = [];

    /// <summary>新しい順。</summary>
    public IReadOnlyList<ClosedTab> Items => _items;

    public int Count => _items.Count;

    /// <summary>閉じたタブを記録する。記録しない種類なら false。</summary>
    public bool Push(ClosedTab closed)
    {
        if (!SessionRules.IsRestorable(closed.Tab))
        {
            return false;
        }

        _items.Insert(0, closed);
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        }

        return true;
    }

    /// <summary>
    /// 次に開き直す記録を取り出す (UI-12 の仕様 2・4)。<paramref name="exists"/> が偽を返す (削除・移動された) 記録は飛ばして
    /// <paramref name="skipped"/> に渡し、次の記録を見る。記録が空なら null。
    /// </summary>
    public ClosedTab? Pop(Func<ClosedTab, bool> exists, Action<ClosedTab>? skipped = null)
    {
        while (_items.Count > 0)
        {
            ClosedTab next = _items[0];
            _items.RemoveAt(0);
            if (exists(next))
            {
                return next;
            }

            skipped?.Invoke(next);
        }

        return null;
    }

    /// <summary>セッションから読み直す。</summary>
    public void Load(IEnumerable<ClosedTab> items)
    {
        _items.Clear();
        _items.AddRange(items.Where(i => SessionRules.IsRestorable(i.Tab)).Take(Capacity));
    }
}

/// <summary>セッションを読んだ結果。</summary>
public enum SessionLoadStatus
{
    /// <summary>読めた (ファイルがない場合を含む)。</summary>
    Ok,

    /// <summary>壊れていた。空のセッションで起動し、壊れたファイルを session.json.broken-&lt;日時&gt; として残した (UI-31 の「エラー」)。</summary>
    Broken,
}

/// <summary>
/// session.json の読み書き (UI-31 の仕様 2)。正常終了時と、30 秒ごと (内容が変わったときだけ) に書く。書き込みは一時ファイルに書いてから
/// 置き換える。
/// </summary>
public sealed class SessionStore(string folder)
{
    public const string FileName = "session.json";

    /// <summary>定期の保存の間隔 (UI-31 の仕様 2)。</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private string? _lastWritten;

    public string PathName => Path.Combine(folder, FileName);

    public (SessionState State, SessionLoadStatus Status) Load(DateTime now)
    {
        try
        {
            SessionState? state = JsonFile.Read<SessionState>(PathName);
            _lastWritten = state is null ? null : JsonFile.Serialize(state);
            return (state ?? new SessionState(), SessionLoadStatus.Ok);
        }
        catch (JsonException)
        {
            JsonFile.KeepBroken(PathName, now);
            return (new SessionState(), SessionLoadStatus.Broken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new SessionState(), SessionLoadStatus.Ok);
        }
    }

    /// <summary>前回書いた内容と違えば書く。書いたら true。書けなければ例外。</summary>
    public bool SaveIfChanged(SessionState state)
    {
        string json = JsonFile.Serialize(state);
        if (json == _lastWritten)
        {
            return false;
        }

        JsonFile.WriteText(PathName, json);
        _lastWritten = json;
        return true;
    }
}

/// <summary>
/// 起動時の動作 (UI-30)。設定 <c>session.restoreOnStartup</c>: <c>always</c> (既定)、<c>ask</c>、<c>never</c>。
/// </summary>
public enum RestoreOnStartup
{
    Always,
    Ask,
    Never,
}

/// <summary>起動時にセッションをどう扱うか (UI-30 の仕様 1〜3)。</summary>
public enum StartupSessionAction
{
    /// <summary>何もしない (復元するタブがない、または never)。</summary>
    None,

    /// <summary>すぐに復元する。</summary>
    Restore,

    /// <summary>スタートページに「前回のセッションを復元」を出す。</summary>
    Offer,
}

public static class StartupPlanner
{
    public const string RestoreOnStartupKey = "session.restoreOnStartup";

    public static RestoreOnStartup Parse(string value) => value switch
    {
        "ask" => RestoreOnStartup.Ask,
        "never" => RestoreOnStartup.Never,
        _ => RestoreOnStartup.Always,
    };

    /// <summary>
    /// 起動時の動作を決める。ファイルを指定して起動した場合も、<c>always</c> なら復元したうえで指定のファイルをアクティブにする
    /// (呼び出し側が復元の後に開く。仕様 2)。
    /// </summary>
    public static StartupSessionAction Decide(RestoreOnStartup setting, SessionState session) =>
        !session.HasRestorableTabs ? StartupSessionAction.None
        : setting switch
        {
            RestoreOnStartup.Always => StartupSessionAction.Restore,
            RestoreOnStartup.Ask => StartupSessionAction.Offer,
            _ => StartupSessionAction.None,
        };

    /// <summary>
    /// 復旧したドキュメントのパスと重なるタブを除く (仕様 3: 復旧した文書のタブはセッションのタブと重複させない)。
    /// </summary>
    public static SessionWindow WithoutRecovered(SessionWindow window, IEnumerable<string> recoveredPaths)
    {
        var recovered = new HashSet<string>(recoveredPaths, StringComparer.OrdinalIgnoreCase);
        var tabs = window.Tabs.Where(t => t.Path is null || !recovered.Contains(t.Path)).ToList();
        int active = window.ActiveTab >= 0 && window.ActiveTab < window.Tabs.Count
            ? tabs.IndexOf(window.Tabs[window.ActiveTab])
            : -1;
        return window with { Tabs = tabs, ActiveTab = active >= 0 ? active : tabs.Count - 1 };
    }
}

/// <summary>
/// state.json (UI-23 の仕様 1): ダイアログの「次回から表示しない」などのアプリの状態。ここでは初回起動の「はじめに」(UI-38) を閉じたか。
/// 知らない項目は捨てずに残す。
/// </summary>
public sealed class AppStateStore(string folder)
{
    public const string FileName = "state.json";

    private System.Text.Json.Nodes.JsonObject _values = [];

    public string PathName => Path.Combine(folder, FileName);

    /// <summary>state.json がなかった (初回起動。UI-38 の仕様 2)。</summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>「はじめに」を閉じた (または何も選ばずにファイルを開いた)。</summary>
    public bool WelcomeDismissed => _values["welcomeDismissed"]?.GetValue<bool>() ?? false;

    public void Load()
    {
        try
        {
            IsFirstRun = !File.Exists(PathName);
            _values = IsFirstRun ? [] : System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PathName)) as System.Text.Json.Nodes.JsonObject ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _values = [];
        }
    }

    /// <summary>「はじめに」を二度と出さない (UI-38 の仕様 3)。書けなければ理由を返す。</summary>
    public string? DismissWelcome()
    {
        _values["welcomeDismissed"] = true;
        try
        {
            JsonFile.WriteText(PathName, _values.ToJsonString(JsonFile.Options));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
