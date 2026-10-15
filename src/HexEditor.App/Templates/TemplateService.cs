using System.Text.Json.Nodes;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App.Templates;

/// <summary>
/// テンプレートのアプリ全体の状態 (07): 使えるテンプレートの一覧 (標準・利用者のフォルダ。TPL-35)、利用者の選択 (有効・無効・
/// 自動適用・「今後表示しない」)、最近使ったテンプレート (TPL-20 の仕様 1)、インスペクタ用の型 (TPL-29)、利用者のフォルダの監視
/// (再起動せずに一覧に出す。TPL-35 の仕様 1)。
/// </summary>
public static class TemplateService
{
    public const string AutoApplyKey = "templates.autoApply";
    public const string AutoReevaluateKey = "templates.autoReevaluate";
    public const string ColoringKey = "templates.coloring";
    public const string OpacityKey = "templates.coloringOpacity";
    public const string FoldersKey = "templates.folders";
    public const string InspectorKey = "templates.inspector";
    public const string PreferencesKey = "templates.preferences";

    /// <summary>最近使ったテンプレート (state.json)。</summary>
    public const string RecentStateKey = "templates.recent";

    /// <summary>結果ツリーの「カーソルに追従」(state.json。既定でオン)。</summary>
    public const string FollowCursorStateKey = "templates.followCursor";

    /// <summary>結果ツリーの列 (state.json)。</summary>
    public const string ColumnsStateKey = "templates.columns";

    private const int RecentLimit = 10;

    private static readonly List<FileSystemWatcher> Watchers = [];
    private static TemplateCatalog? _catalog;
    private static string _foldersSignature = string.Empty;
    private static TemplatePreferences? _preferences;
    private static TemplateInspectorTypes? _inspector;
    private static string _inspectorSignature = "\0";

    /// <summary>テンプレートの一覧が変わった (利用者のフォルダのファイルの追加・削除、設定の変更)。どのスレッドからも呼ばれる。</summary>
    public static event EventHandler? CatalogChanged;

    /// <summary>既定の利用者のテンプレートのフォルダ (設定フォルダの <c>templates/</c>。TPL-35 の仕様 1)。</summary>
    public static string DefaultUserFolder => Path.Combine(Program.Environment.Locations.Settings, "templates");

    /// <summary>利用者のテンプレートのフォルダ (既定のフォルダ、設定で加えたフォルダの順)。</summary>
    public static IReadOnlyList<string> UserFolders
    {
        get
        {
            var folders = new List<string> { DefaultUserFolder };
            if (App.Settings?.GetNode(FoldersKey) is JsonArray extra)
            {
                foreach (JsonNode? n in extra)
                {
                    if (n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
                        && !folders.Contains(s, StringComparer.OrdinalIgnoreCase))
                    {
                        folders.Add(s);
                    }
                }
            }

            return folders;
        }
    }

    /// <summary>今の一覧 (設定のフォルダが変われば作り直す)。</summary>
    public static TemplateCatalog Catalog
    {
        get
        {
            IReadOnlyList<string> folders = UserFolders;
            string signature = string.Join("|", folders);
            if (_catalog is null || signature != _foldersSignature)
            {
                _foldersSignature = signature;
                _catalog = new TemplateCatalog(folders, includeStandard: true, new TemplateLoadOptions { SearchPaths = folders });
                Preferences.ApplyTo(_catalog);
                Watch(folders);
            }

            return _catalog;
        }
    }

    /// <summary>読み込みの設定 (検索パスは利用者のフォルダ)。</summary>
    public static TemplateLoadOptions LoadOptions => Catalog.LoadOptions;

    public static TemplatePreferences Preferences
    {
        get
        {
            if (_preferences is null)
            {
                JsonNode? node = null;
                try
                {
                    node = App.Settings?.GetString(PreferencesKey, string.Empty) is { Length: > 0 } json ? JsonNode.Parse(json) : null;
                }
                catch (System.Text.Json.JsonException)
                {
                    node = null;
                }

                _preferences = TemplatePreferences.FromJson(node);
            }

            return _preferences;
        }
    }

    /// <summary>利用者の選択を保存して、一覧と自動適用に反映する。</summary>
    public static void SavePreferences()
    {
        App.Settings.SetString(PreferencesKey, Preferences.ToJson().ToJsonString(), string.Empty);
        if (_catalog is not null)
        {
            Preferences.ApplyTo(_catalog);
        }

        CatalogChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>設定「自動適用」。</summary>
    public static TemplateAutoApplyMode AutoApplyMode => App.Settings.GetString(AutoApplyKey, "suggest") switch
    {
        "always" => TemplateAutoApplyMode.Always,
        "none" => TemplateAutoApplyMode.None,
        _ => TemplateAutoApplyMode.Suggest,
    };

    public static bool AutoReevaluate => App.Settings.GetBool(AutoReevaluateKey, true);

    public static bool ColoringEnabled => App.Settings.GetBool(ColoringKey, true);

    /// <summary>色分けの不透明度 (0〜1。既定 35%。TPL-23 の仕様 3)。</summary>
    public static double Opacity => Math.Clamp(App.Settings.GetInt(OpacityKey, 35), 5, 100) / 100.0;

    /// <summary>適用の設定 (拡張関数はスクリプトが登録したもの。TPL-19)。</summary>
    public static TemplateApplyOptions ApplyOptions(bool autoApply = false) => new()
    {
        Extensions = Services.Scripting.ScriptingService.Templates.Extensions,
        IsAutoApply = autoApply,
        TempDirectory = Path.Combine(App.TempRoot ?? Path.GetTempPath(), "templates"),
    };

    /// <summary>パスのテンプレートを読み込む (標準テンプレートは同梱のもの)。</summary>
    public static TemplateProgram Load(string path) => Catalog.Load(path);

    // ───────────── 最近使ったテンプレート ─────────────

    public static IReadOnlyList<string> Recent
    {
        get
        {
            var list = new List<string>();
            if (Commands.CommandService.State?.Get(RecentStateKey) is JsonArray a)
            {
                foreach (JsonNode? n in a)
                {
                    if (n is JsonValue v && v.TryGetValue(out string? s))
                    {
                        list.Add(s);
                    }
                }
            }

            return list;
        }
    }

    public static void AddRecent(string path)
    {
        List<string> list = [path, .. Recent.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))];
        Commands.CommandService.State?.Set(RecentStateKey, new JsonArray([.. list.Take(RecentLimit).Select(p => (JsonNode?)p)]));
    }

    // ───────────── インスペクタ用の型 (TPL-29) ─────────────

    /// <summary>設定「インスペクタ用テンプレート」のテンプレートの型 (設定が変われば読み直す)。</summary>
    public static TemplateInspectorTypes InspectorTypes
    {
        get
        {
            List<string> paths = [];
            if (App.Settings?.GetNode(InspectorKey) is JsonArray a)
            {
                foreach (JsonNode? n in a)
                {
                    if (n is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0)
                    {
                        paths.Add(s);
                    }
                }
            }

            string signature = string.Join("|", paths.Select(p => p + "@" + Stamp(p)));
            if (_inspector is null || signature != _inspectorSignature)
            {
                _inspectorSignature = signature;
                var types = new TemplateInspectorTypes();
                try
                {
                    types.Load(paths);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning($"Inspector templates: {ex.Message}");
                }

                _inspector = types;
            }

            return _inspector;
        }
    }

    private static string Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "0";
        }
    }

    // ───────────── 監視 ─────────────

    private static void Watch(IReadOnlyList<string> folders)
    {
        lock (Watchers)
        {
            foreach (FileSystemWatcher w in Watchers)
            {
                w.Dispose();
            }

            Watchers.Clear();
            foreach (string folder in folders)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    };
                    FileSystemEventHandler changed = (_, _) => CatalogChanged?.Invoke(null, EventArgs.Empty);
                    watcher.Created += changed;
                    watcher.Deleted += changed;
                    watcher.Changed += changed;
                    watcher.Renamed += (_, _) => CatalogChanged?.Invoke(null, EventArgs.Empty);
                    watcher.EnableRaisingEvents = true;
                    Watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // アクセスできないフォルダは一覧にその旨を出す (TPL-35 の「エラー」)
                    AppLog.Warning($"Template folder not watched: {ex.GetType().Name}");
                }
            }
        }
    }

    /// <summary>設定が変わった (フォルダ・インスペクタ用テンプレート)。</summary>
    public static void SettingsChanged()
    {
        if (_catalog is not null && string.Join("|", UserFolders) != _foldersSignature)
        {
            _ = Catalog;
            CatalogChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
