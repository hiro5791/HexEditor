using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Commands;

namespace HexEditor.Core.Settings;

/// <summary>インポートの区分ごとの扱い (UI-25 の仕様 3)。</summary>
public enum ImportMode
{
    Replace,
    Merge,
    Skip,
}

/// <summary>インポートで飛ばした設定の値 (UI-25 の「エラー」)。</summary>
public sealed record SkippedSetting(string Key, string Value);

/// <summary>設定の区分のインポートの結果の見込み。</summary>
public sealed record SettingsImportPlan(JsonObject Result, IReadOnlyList<SkippedSetting> Skipped, int ChangedCount);

/// <summary>エクスポートのファイルが読めない理由の種類 (表示の文は App がリソースから作る)。</summary>
public enum SettingsBundleError
{
    /// <summary>上限 (<see cref="SettingsBundle.MaxBytes"/>) を超えている。</summary>
    TooLarge,

    /// <summary>JSON として読めない (<see cref="SettingsBundleException.Line"/> に行番号)。</summary>
    InvalidJson,

    /// <summary>HexEditor の設定のファイルではない。</summary>
    NotSettingsFile,

    /// <summary><c>$schemaVersion</c> がない。</summary>
    MissingVersion,

    /// <summary>新しい版のアプリで作られた (<see cref="SettingsBundleException.Version"/> に版)。</summary>
    TooNew,
}

/// <summary>読めないエクスポートのファイル (形式が違う・新しすぎる)。<see cref="Exception.Message"/> は記録用 (英語) で、表示には使わない。</summary>
public sealed class SettingsBundleException(SettingsBundleError error, int line = 0, int version = 0)
    : Exception($"Settings bundle error: {error}")
{
    public SettingsBundleError Error { get; } = error;

    public bool TooNew => Error == SettingsBundleError.TooNew;

    /// <summary>JSON として読めない位置 (1 始まり。0 は不明)。</summary>
    public int Line { get; } = line;

    /// <summary>新しすぎるファイルの <c>$schemaVersion</c>。</summary>
    public int Version { get; } = version;
}

/// <summary>
/// 設定のエクスポートのファイル (UI-25): 設定・キー割り当て・独自の配色・最近使ったファイルを 1 つの JSON にまとめたもの。
/// 内容は <c>{"$schemaVersion":1, "app":"HexEditor", "version":"…", "settings":{…}, "keybindings":{…}, "themes":[…]}</c>。
/// 別の配布形態からの移行 (PKG-31) もこの形式を使う。
/// </summary>
public sealed class SettingsBundle
{
    public const int CurrentSchemaVersion = 1;
    public const string AppName = "HexEditor";

    /// <summary>ファイルの大きさの上限 (UI-25 の「巨大ファイル」)。</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Version { get; init; } = string.Empty;

    public JsonObject? Settings { get; init; }

    public KeyBindingsDocument? Keybindings { get; init; }

    /// <summary>独自の配色 (themes フォルダのファイル名と内容)。</summary>
    public IReadOnlyList<(string Name, JsonNode Content)>? Themes { get; init; }

    /// <summary>最近使ったファイル (recent.json の内容。パスを含むため既定ではエクスポートしない)。</summary>
    public JsonNode? Recent { get; init; }

    public static string DefaultFileName(DateTime now) => $"hexeditor-settings-{now:yyyyMMdd}.json";

    /// <summary>
    /// エクスポートの内容を作る。設定は配布形態に固有の項目 (<see cref="SettingDefinition.Exportable"/> が false) を除く。
    /// </summary>
    public static SettingsBundle Create(SettingsParts parts, string version, JsonObject settings, SettingsCatalog catalog,
        KeyBindingsDocument? keybindings, IReadOnlyList<(string Name, JsonNode Content)> themes, JsonNode? recent)
    {
        JsonObject? exported = null;
        if (parts.HasFlag(SettingsParts.Settings))
        {
            exported = [];
            foreach ((string key, JsonNode? value) in settings.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (catalog.Find(key) is not { Exportable: false })
                {
                    exported[key] = value?.DeepClone();
                }
            }
        }

        return new SettingsBundle
        {
            Version = version,
            Settings = exported,
            Keybindings = parts.HasFlag(SettingsParts.KeyBindings) ? keybindings ?? new KeyBindingsDocument() : null,
            Themes = parts.HasFlag(SettingsParts.Themes) ? themes : null,
            Recent = parts.HasFlag(SettingsParts.RecentAndState) ? recent?.DeepClone() : null,
        };
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["$schemaVersion"] = SchemaVersion,
            ["app"] = AppName,
            ["version"] = Version,
        };
        if (Settings is not null)
        {
            root["settings"] = Settings.DeepClone();
        }

        if (Keybindings is not null)
        {
            root["keybindings"] = JsonNode.Parse(Keybindings.ToJson());
        }

        if (Themes is not null)
        {
            root["themes"] = new JsonArray([.. Themes.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["content"] = t.Content.DeepClone() })]);
        }

        if (Recent is not null)
        {
            root["recent"] = Recent.DeepClone();
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>読む。形式が違う・新しすぎる場合は <see cref="SettingsBundleException"/>。</summary>
    public static SettingsBundle Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new SettingsBundleException(SettingsBundleError.TooLarge);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? throw new SettingsBundleException(SettingsBundleError.NotSettingsFile);
        }
        catch (JsonException ex)
        {
            throw new SettingsBundleException(SettingsBundleError.InvalidJson, (int)(ex.LineNumber ?? 0) + 1);
        }

        if (root["app"] is not JsonValue app || !app.TryGetValue(out string? name) || name != AppName)
        {
            throw new SettingsBundleException(SettingsBundleError.NotSettingsFile);
        }

        int version = KeyBindingsDocument.ReadVersion(root["$schemaVersion"], fallback: 0);
        if (version > CurrentSchemaVersion)
        {
            throw new SettingsBundleException(SettingsBundleError.TooNew, version: version);
        }

        if (version < 1)
        {
            throw new SettingsBundleException(SettingsBundleError.MissingVersion);
        }

        KeyBindingsDocument? keys = null;
        if (root["keybindings"] is JsonObject k)
        {
            try
            {
                keys = KeyBindingsDocument.Parse(k.ToJsonString());
            }
            catch (JsonException)
            {
                throw new SettingsBundleException(SettingsBundleError.NotSettingsFile);
            }

            if (keys.IsTooNew)
            {
                throw new SettingsBundleException(SettingsBundleError.TooNew, version: keys.SchemaVersion);
            }
        }

        List<(string, JsonNode)>? themes = null;
        if (root["themes"] is JsonArray t)
        {
            themes = t.OfType<JsonObject>()
                .Where(o => o["name"] is JsonValue nv && nv.TryGetValue(out string? s) && IsSafeFileName(s) && o["content"] is not null)
                .Select(o => (o["name"]!.GetValue<string>(), o["content"]!.DeepClone())).ToList();
        }

        return new SettingsBundle
        {
            SchemaVersion = version,
            Version = root["version"] is JsonValue ver && ver.TryGetValue(out string? vs) ? vs : string.Empty,
            Settings = root["settings"] as JsonObject is { } s ? (JsonObject)s.DeepClone() : null,
            Keybindings = keys,
            Themes = themes,
            Recent = root["recent"]?.DeepClone(),
        };
    }

    /// <summary>ファイルに含まれる区分。</summary>
    public SettingsParts Parts =>
        (Settings is not null ? SettingsParts.Settings : 0) | (Keybindings is not null ? SettingsParts.KeyBindings : 0)
        | (Themes is not null ? SettingsParts.Themes : 0) | (Recent is not null ? SettingsParts.RecentAndState : 0);

    /// <summary>
    /// 設定の区分を読み込んだ結果を作る。不正な値の項目は飛ばして一覧にする。配布形態に固有の項目は読み込まず、今の値を残す。
    /// </summary>
    public SettingsImportPlan PlanSettings(JsonObject current, SettingsCatalog catalog, ImportMode mode)
    {
        var skipped = new List<SkippedSetting>();
        if (Settings is null || mode == ImportMode.Skip)
        {
            return new SettingsImportPlan((JsonObject)current.DeepClone(), skipped, 0);
        }

        JsonObject result = mode == ImportMode.Merge ? (JsonObject)current.DeepClone() : [];
        if (mode == ImportMode.Replace)
        {
            foreach ((string key, JsonNode? value) in current)
            {
                if (catalog.Find(key) is { Exportable: false })
                {
                    result[key] = value?.DeepClone();
                }
            }
        }

        foreach ((string key, JsonNode? value) in Settings)
        {
            SettingDefinition? def = catalog.Find(key);
            if (def is { Exportable: false })
            {
                continue;
            }

            if (!catalog.Validate(key, value))
            {
                skipped.Add(new SkippedSetting(key, value?.ToJsonString() ?? "null"));
                continue;
            }

            // 既定値と同じ値は書かない (UI-23 の仕様 4)。
            if (def is not null && JsonNode.DeepEquals(value, def.Default))
            {
                result.Remove(key);
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        int changed = current.Select(p => p.Key).Union(result.Select(p => p.Key))
            .Count(k => current[k]?.ToJsonString() != result[k]?.ToJsonString());
        return new SettingsImportPlan(result, skipped, changed);
    }

    private static bool IsSafeFileName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");
}
