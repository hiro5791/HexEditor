using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Scripting.Hooks;

/// <summary>フックの種類 (AUTO-25 の仕様 1)。</summary>
public enum HookKind
{
    /// <summary>最初のウィンドウが表示され操作できるようになった後 (起動を遅らせない)。</summary>
    Startup,

    /// <summary>ドキュメントの先頭が表示された後。</summary>
    Open,

    /// <summary>保存の直前。スクリプトが <c>event.cancel(理由)</c> を呼ぶと保存しない。</summary>
    BeforeSave,

    /// <summary>保存の完了後。</summary>
    AfterSave,

    /// <summary>ドキュメントを閉じる直前 (中止はできない)。</summary>
    BeforeClose,
}

public static class HookKinds
{
    public static IReadOnlyList<HookKind> All { get; } = [HookKind.Startup, HookKind.Open, HookKind.BeforeSave, HookKind.AfterSave, HookKind.BeforeClose];

    /// <summary>設定ファイルとイベントの名前。</summary>
    public static string Name(HookKind kind) => kind switch
    {
        HookKind.Startup => "startup",
        HookKind.Open => "open",
        HookKind.BeforeSave => "beforeSave",
        HookKind.AfterSave => "afterSave",
        _ => "beforeClose",
    };

    public static HookKind? Parse(string? name) => All.Cast<HookKind?>().FirstOrDefault(k => string.Equals(Name(k!.Value), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>上限時間の既定 (AUTO-25 の仕様 1 の表)。</summary>
    public static TimeSpan DefaultTimeout(HookKind kind) => kind switch
    {
        HookKind.Startup => TimeSpan.FromSeconds(60),
        HookKind.Open => TimeSpan.FromSeconds(10),
        HookKind.BeforeSave => TimeSpan.FromSeconds(5),
        HookKind.AfterSave => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(5),
    };

    /// <summary>条件で絞り込めるか (「ファイルを開いたとき」だけ)。</summary>
    public static bool SupportsCondition(HookKind kind) => kind == HookKind.Open;

    /// <summary><c>@on</c> の値 (例: <c>open *.bin</c>) の最初の語からフックの種類を決める。</summary>
    public static HookKind? FromCandidate(string candidate)
    {
        string first = candidate.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.ToLowerInvariant() switch
        {
            "startup" or "start" => HookKind.Startup,
            "open" => HookKind.Open,
            "beforesave" or "before-save" or "save" => HookKind.BeforeSave,
            "aftersave" or "after-save" => HookKind.AfterSave,
            "beforeclose" or "before-close" or "close" => HookKind.BeforeClose,
            _ => null,
        };
    }
}

/// <summary>条件の対象になる、開いたドキュメントの情報。</summary>
/// <param name="Head">先頭のバイト (マジックバイトの判定用。最大 64 バイト)。</param>
public sealed record HookTarget(string? Path, string Name, string SourceKind, ReadOnlyMemory<byte> Head);

/// <summary>
/// 「ファイルを開いたとき」の絞り込み (AUTO-25 の仕様 1): 拡張子・パス・先頭のマジックバイト・データソースの種類。指定した条件は
/// すべて満たす必要がある。条件がなければ (<see cref="IsEmpty"/>) すべてのドキュメントに動く。
/// </summary>
public sealed record HookCondition
{
    /// <summary>拡張子 (<c>.bin</c>、<c>bin</c>、<c>*.bin</c>。<c>,</c> か空白で複数)。</summary>
    public string? Extension { get; init; }

    /// <summary>パスのパターン (<c>*</c> と <c>?</c>。大文字と小文字は区別しない)。</summary>
    public string? PathPattern { get; init; }

    /// <summary>先頭のマジックバイト (Hex。例: <c>4D 5A</c>)。</summary>
    public string? Magic { get; init; }

    /// <summary>データソースの種類 (<c>file</c> / <c>disk</c> / <c>volume</c> / <c>process</c> / <c>memory</c>)。</summary>
    public string? Source { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Extension) && string.IsNullOrWhiteSpace(PathPattern) && string.IsNullOrWhiteSpace(Magic) && string.IsNullOrWhiteSpace(Source);

    public bool Matches(HookTarget target)
    {
        if (!string.IsNullOrWhiteSpace(Extension))
        {
            string ext = System.IO.Path.GetExtension(target.Path ?? target.Name);
            bool any = Extension.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(e => "." + e.TrimStart('*', '.'))
                .Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
            if (!any)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(PathPattern))
        {
            string regex = "^" + Regex.Escape(PathPattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            if (target.Path is null || !Regex.IsMatch(target.Path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Magic))
        {
            byte[]? magic = Clipboard.HexText.TryParse(Magic);
            if (magic is null || magic.Length == 0 || target.Head.Length < magic.Length || !target.Head.Span[..magic.Length].SequenceEqual(magic))
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(Source) || string.Equals(Source.Trim(), target.SourceKind, StringComparison.OrdinalIgnoreCase);
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (!string.IsNullOrWhiteSpace(Extension))
        {
            o["extension"] = Extension;
        }

        if (!string.IsNullOrWhiteSpace(PathPattern))
        {
            o["path"] = PathPattern;
        }

        if (!string.IsNullOrWhiteSpace(Magic))
        {
            o["magic"] = Magic;
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            o["source"] = Source;
        }

        return o;
    }

    public static HookCondition FromJson(JsonNode? node) => node is JsonObject o
        ? new HookCondition
        {
            Extension = o["extension"]?.GetValue<string>(),
            PathPattern = o["path"]?.GetValue<string>(),
            Magic = o["magic"]?.GetValue<string>(),
            Source = o["source"]?.GetValue<string>(),
        }
        : new HookCondition();
}

/// <summary>フックに登録したスクリプト 1 つ (AUTO-25 の仕様 2)。</summary>
public sealed record HookEntry(string Script, bool Enabled = true, HookCondition? Condition = null)
{
    /// <summary>上限時間の上書き (秒)。null は既定。</summary>
    public double? TimeoutSeconds { get; init; }
}

/// <summary>
/// フックの設定 (設定 <c>scripts.hooks</c>。AUTO-25)。1 つのフックに複数のスクリプトを登録でき、並び順に実行する。
/// </summary>
public sealed class HookConfig
{
    private readonly Dictionary<HookKind, List<HookEntry>> _entries = [];

    public IReadOnlyList<HookEntry> Entries(HookKind kind) => _entries.TryGetValue(kind, out List<HookEntry>? list) ? list : [];

    public void Add(HookKind kind, HookEntry entry)
    {
        if (!_entries.TryGetValue(kind, out List<HookEntry>? list))
        {
            _entries[kind] = list = [];
        }

        list.Add(entry);
    }

    public bool Remove(HookKind kind, int index)
    {
        if (_entries.TryGetValue(kind, out List<HookEntry>? list) && index >= 0 && index < list.Count)
        {
            list.RemoveAt(index);
            return true;
        }

        return false;
    }

    public void Replace(HookKind kind, int index, HookEntry entry)
    {
        if (_entries.TryGetValue(kind, out List<HookEntry>? list) && index >= 0 && index < list.Count)
        {
            list[index] = entry;
        }
    }

    /// <summary>並べ替え (<paramref name="delta"/> が負なら前へ)。</summary>
    public bool Move(HookKind kind, int index, int delta)
    {
        if (!_entries.TryGetValue(kind, out List<HookEntry>? list))
        {
            return false;
        }

        int target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
        {
            return false;
        }

        (list[index], list[target]) = (list[target], list[index]);
        return true;
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject();
        foreach (HookKind kind in HookKinds.All)
        {
            if (!_entries.TryGetValue(kind, out List<HookEntry>? list) || list.Count == 0)
            {
                continue;
            }

            var array = new JsonArray();
            foreach (HookEntry e in list)
            {
                var o = new JsonObject { ["script"] = e.Script, ["enabled"] = e.Enabled };
                if (e.Condition is { IsEmpty: false } c)
                {
                    o["condition"] = c.ToJson();
                }

                if (e.TimeoutSeconds is double t)
                {
                    o["timeoutSeconds"] = t;
                }

                array.Add(o);
            }

            root[HookKinds.Name(kind)] = array;
        }

        return root;
    }

    public static HookConfig FromJson(JsonNode? node)
    {
        var config = new HookConfig();
        if (node is not JsonObject root)
        {
            return config;
        }

        foreach ((string key, JsonNode? value) in root)
        {
            if (HookKinds.Parse(key) is not HookKind kind || value is not JsonArray array)
            {
                continue;
            }

            foreach (JsonNode? item in array)
            {
                if (item is JsonObject o && o["script"]?.GetValue<string>() is { Length: > 0 } script)
                {
                    config.Add(kind, new HookEntry(script, o["enabled"]?.GetValue<bool>() ?? true, o["condition"] is { } c ? HookCondition.FromJson(c) : null)
                    {
                        TimeoutSeconds = o["timeoutSeconds"]?.GetValue<double>(),
                    });
                }
            }
        }

        return config;
    }
}

/// <summary>フックの設定の保存先 (App は設定ファイル、テストはメモリ)。</summary>
public interface IHookConfigStore
{
    HookConfig Load();

    void Save(HookConfig config);
}

/// <summary>
/// ファイルの保存先 (<c>&lt;設定フォルダ&gt;/scripts/hooks.json</c>)。設定画面「スクリプト &gt; 自動実行」で編集する。
/// 読めないファイルは空の設定として扱う。保存は一時ファイルに書いてから置き換える。
/// </summary>
public sealed class FileHookConfigStore(string path) : IHookConfigStore
{
    private readonly Lock _lock = new();

    /// <summary>変わった (保存した)。</summary>
    public event EventHandler? Changed;

    public HookConfig Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(path) ? HookConfig.FromJson(JsonNode.Parse(File.ReadAllText(path))) : new HookConfig();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                return new HookConfig();
            }
        }
    }

    public void Save(HookConfig config)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, config.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>メモリ上の保存先 (テスト・ヘッドレス)。</summary>
public sealed class MemoryHookConfigStore(HookConfig? initial = null) : IHookConfigStore
{
    private JsonObject _json = (initial ?? new HookConfig()).ToJson();

    public HookConfig Load() => HookConfig.FromJson(_json);

    public void Save(HookConfig config) => _json = config.ToJson();
}
