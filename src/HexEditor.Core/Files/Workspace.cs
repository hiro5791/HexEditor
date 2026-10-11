using System.Text.Json;

namespace HexEditor.Core.Files;

/// <summary>
/// ワークスペースファイル (<c>.hexworkspace</c>、JSON。UI-33 の仕様 1): 現在のウィンドウのタブ (UI-31 のタブ単位の内容) とパネルの配置。
/// ファイルのパスは、同じドライブならワークスペースファイルからの相対パスで保存する (フォルダごと移しても開ける)。ブックマークなどの付随データは
/// 含めない (仕様 3)。
/// </summary>
public sealed record WorkspaceFile
{
    public const string Extension = ".hexworkspace";

    public int Version { get; init; } = 1;

    /// <summary>タブ。<see cref="SessionTab.Path"/> は相対パス (別のドライブなら絶対パス)。</summary>
    public List<SessionTab> Tabs { get; init; } = [];

    public int ActiveTab { get; init; } = -1;

    /// <summary>パネルの配置 (セッションと同じ形)。</summary>
    public JsonElement? Panels { get; init; }

    /// <summary>
    /// ウィンドウの状態からワークスペースを作る。タブのパスを <paramref name="workspacePath"/> からの相対パスにする (同じドライブの場合)。
    /// 復元できないタブ (無題・プロセス) は含めない。
    /// </summary>
    public static WorkspaceFile FromWindow(SessionWindow window, string workspacePath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!;
        var tabs = new List<SessionTab>();
        int active = -1;
        for (int i = 0; i < window.Tabs.Count; i++)
        {
            SessionTab tab = window.Tabs[i];
            if (!SessionRules.IsRestorable(tab))
            {
                continue;
            }

            if (i == window.ActiveTab)
            {
                active = tabs.Count;
            }

            tabs.Add(tab with { Path = Relative(folder, tab.Path!), View = tab.View });
        }

        return new WorkspaceFile { Tabs = tabs, ActiveTab = active, Panels = window.Panels };
    }

    /// <summary>開くためのタブ (パスを絶対パスに戻す)。</summary>
    public SessionWindow ToWindow(string workspacePath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!;
        return new SessionWindow
        {
            Tabs = [.. Tabs.Select(t => t with { Path = t.Path is null ? null : Path.GetFullPath(Path.Combine(folder, t.Path)) })],
            ActiveTab = ActiveTab,
            Panels = Panels,
        };
    }

    /// <summary>同じドライブ (ルート) なら相対パス、違えば絶対パス。</summary>
    internal static string Relative(string folder, string path)
    {
        string full = Path.GetFullPath(path);
        return string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, full)
            : full;
    }

    public void Save(string path) => JsonFile.Write(path, this);

    /// <summary>読む。読めなければ <see cref="JsonException"/>、なければ <see cref="FileNotFoundException"/>。</summary>
    public static WorkspaceFile Load(string path) =>
        JsonFile.Read<WorkspaceFile>(path) ?? throw new FileNotFoundException(null, path);
}
