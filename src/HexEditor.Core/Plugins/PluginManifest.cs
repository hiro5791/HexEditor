using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HexEditor.Core.Scripting;

namespace HexEditor.Core.Plugins;

/// <summary>マニフェストの検証の誤り (AUTO-30 の「エラー」。プラグインマネージャに理由として表示する)。</summary>
public sealed class PluginManifestException(string message) : Exception(message);

/// <summary>
/// プラグインのマニフェスト <c>plugin.json</c> (AUTO-30 の仕様 1)。内容は信頼できない入力として扱い、読むときにすべて検証する。
/// </summary>
public sealed partial record PluginManifest
{
    public const string FileName = "plugin.json";

    /// <summary>マニフェストの大きさの上限 (これより大きいものは読まない)。</summary>
    public const int MaxBytes = 256 * 1024;

    public required string Id { get; init; }

    /// <summary>既定 (英語) の表示名。</summary>
    public required string Name { get; init; }

    /// <summary>言語ごとの表示名 (<c>name.ja</c> など)。</summary>
    public IReadOnlyDictionary<string, string> LocalizedNames { get; init; } = new Dictionary<string, string>();

    public required PluginVersion Version { get; init; }

    public string Publisher { get; init; } = string.Empty;

    /// <summary>ビルドに使った SDK のバージョン (<c>メジャー.マイナー</c>)。</summary>
    public required Version Sdk { get; init; }

    public PluginVersion? MinAppVersion { get; init; }

    /// <summary>入口のアセンブリ (プラグインのフォルダ直下のファイル名)。</summary>
    public required string Entry { get; init; }

    /// <summary>宣言した許可 (プラグインは完全信頼のコード。確認ダイアログに一覧するだけ。AUTO-34)。</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    public string Description { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> LocalizedDescriptions { get; init; } = new Dictionary<string, string>();

    /// <summary>表示言語の名前 (<c>ja-JP</c> なら <c>name.ja-JP</c>、<c>name.ja</c>、<c>name</c> の順に探す)。</summary>
    public string NameFor(string? language) => Localized(LocalizedNames, Name, language);

    public string DescriptionFor(string? language) => Localized(LocalizedDescriptions, Description, language);

    private static string Localized(IReadOnlyDictionary<string, string> values, string fallback, string? language)
    {
        if (!string.IsNullOrEmpty(language))
        {
            if (values.TryGetValue(language, out string? exact))
            {
                return exact;
            }

            int dash = language.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && values.TryGetValue(language[..dash], out string? neutral))
            {
                return neutral;
            }
        }

        return fallback;
    }

    /// <summary>ID の形式: 英小文字・数字・<c>-</c> の部分を <c>.</c> でつないだもの (例: <c>example.crc-tools</c>)。最大 100 文字。</summary>
    public static bool IsValidId(string id) => id.Length is > 0 and <= 100 && IdPattern().IsMatch(id);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>フォルダのマニフェストを読む。</summary>
    public static PluginManifest Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            throw new PluginManifestException(Msg.T("マニフェスト (plugin.json) がありません。", "The manifest (plugin.json) is missing."));
        }

        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            throw new PluginManifestException(Msg.T("マニフェストが大きすぎます。", "The manifest is too large."));
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>マニフェストを解析して検証する (必須項目と形式。SDK とアプリのバージョンの判定は <see cref="PluginCompatibility"/>)。</summary>
    public static PluginManifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException(Msg.T($"マニフェストを JSON として読めません: {ex.Message}", $"The manifest is not valid JSON: {ex.Message}"));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PluginManifestException(Msg.T("マニフェストが JSON のオブジェクトではありません。", "The manifest is not a JSON object."));
            }

            JsonElement root = doc.RootElement;
            string id = Required(root, "id");
            if (!IsValidId(id))
            {
                throw new PluginManifestException(Msg.T($"ID の形式が正しくありません: {id}", $"The ID is not valid: {id}"));
            }

            string name = Required(root, "name");
            string versionText = Required(root, "version");
            if (!PluginVersion.TryParse(versionText, out PluginVersion? version))
            {
                throw new PluginManifestException(Msg.T($"version の形式が正しくありません: {versionText}", $"The version is not valid: {versionText}"));
            }

            string sdkText = Required(root, "sdk");
            if (!TryParseSdk(sdkText, out Version? sdk))
            {
                throw new PluginManifestException(Msg.T($"sdk の形式が正しくありません (メジャー.マイナー): {sdkText}", $"The sdk version is not valid (major.minor): {sdkText}"));
            }

            PluginVersion? minApp = null;
            if (Optional(root, "minAppVersion") is { } minText)
            {
                if (!PluginVersion.TryParse(minText, out minApp))
                {
                    throw new PluginManifestException(Msg.T($"minAppVersion の形式が正しくありません: {minText}", $"minAppVersion is not valid: {minText}"));
                }
            }

            string entry = Required(root, "entry");
            if (!IsSafeFileName(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginManifestException(Msg.T($"entry はプラグインのフォルダ直下の .dll のファイル名にしてください: {entry}", $"entry must be the name of a .dll directly in the plugin folder: {entry}"));
            }

            var permissions = new List<string>();
            if (root.TryGetProperty("permissions", out JsonElement perms))
            {
                if (perms.ValueKind != JsonValueKind.Array)
                {
                    throw new PluginManifestException(Msg.T("permissions は文字列の配列にしてください。", "permissions must be an array of strings."));
                }

                foreach (JsonElement p in perms.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 and <= 64 } s)
                    {
                        throw new PluginManifestException(Msg.T("permissions は文字列の配列にしてください。", "permissions must be an array of strings."));
                    }

                    permissions.Add(s);
                }
            }

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (property.Name.StartsWith("name.", StringComparison.Ordinal) && property.Name.Length > 5)
                {
                    names[property.Name[5..]] = Limit(property.Value.GetString()!, 200);
                }
                else if (property.Name.StartsWith("description.", StringComparison.Ordinal) && property.Name.Length > 12)
                {
                    descriptions[property.Name[12..]] = Limit(property.Value.GetString()!, 2000);
                }
            }

            return new PluginManifest
            {
                Id = id,
                Name = Limit(name, 200),
                LocalizedNames = names,
                Version = version!,
                Publisher = Limit(Optional(root, "publisher") ?? string.Empty, 200),
                Sdk = sdk!,
                MinAppVersion = minApp,
                Entry = entry,
                Permissions = permissions,
                Description = Limit(Optional(root, "description") ?? string.Empty, 2000),
                LocalizedDescriptions = descriptions,
            };
        }
    }

    /// <summary><c>メジャー.マイナー</c> (例: <c>1.3</c>)。</summary>
    public static bool TryParseSdk(string text, out Version? version)
    {
        version = null;
        string[] parts = text.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) || major > 65535 || minor > 65535)
        {
            return false;
        }

        version = new Version(major, minor);
        return true;
    }

    /// <summary>フォルダ直下のファイル名として安全か (区切り・<c>..</c>・ドライブ・代替データストリーム・予約名を含まない)。</summary>
    internal static bool IsSafeFileName(string name) =>
        name.Length is > 0 and <= 200 && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0 && name != "." && name != ".."
        && !name.EndsWith('.') && !name.EndsWith(' ') && !PluginPackage.IsReservedDeviceName(name);

    private static string Required(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PluginManifestException(Msg.T($"マニフェストに必須の項目 {name} がありません。", $"The manifest has no required field {name}."));
        }

        return value.GetString()!.Trim();
    }

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>プラグインとアプリのバージョン (セマンティックバージョニングの <c>メジャー.マイナー.パッチ[-プレリリース]</c>)。</summary>
public sealed partial record PluginVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<PluginVersion>
{
    public static bool TryParse(string? text, out PluginVersion? version)
    {
        version = null;
        if (text is null)
        {
            return false;
        }

        Match m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        version = new PluginVersion(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0, m.Groups[4].Success ? m.Groups[4].Value : string.Empty);
        return true;
    }

    public static PluginVersion Parse(string text) => TryParse(text, out PluginVersion? v) ? v! : throw new FormatException(text);

    // プレリリース・ビルドの部分は英数字と - の識別子を . でつないだもの (フォルダ名にするため、空の識別子・末尾の . は許さない)。
    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?(?:-([0-9A-Za-z-]{1,32}(?:\.[0-9A-Za-z-]{1,32}){0,7}))?(?:\+[0-9A-Za-z-]{1,32}(?:\.[0-9A-Za-z-]{1,32}){0,7})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public int CompareTo(PluginVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int c = Major.CompareTo(other.Major);
        c = c != 0 ? c : Minor.CompareTo(other.Minor);
        c = c != 0 ? c : Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        // プレリリースは正式版より前。
        return (Prerelease.Length == 0, other.Prerelease.Length == 0) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => Math.Sign(string.CompareOrdinal(Prerelease, other.Prerelease)),
        };
    }

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>SDK とアプリのバージョンの判定 (AUTO-29 の仕様 2、AUTO-30 の仕様 3 の 1)。</summary>
public static class PluginCompatibility
{
    /// <summary>
    /// プラグインが宣言・参照した SDK のバージョンを、アプリの SDK のバージョンで読み込めるか: メジャーバージョンが同じで、マイナーバージョンが同じか小さい。
    /// </summary>
    public static bool IsSdkCompatible(Version plugin, Version app) => plugin.Major == app.Major && plugin.Minor <= app.Minor;

    /// <summary>読み込めない理由 (読み込めるなら null)。理由には必要な SDK のバージョンとアプリの SDK のバージョンを示す (AUTO-29 の受け入れ基準 3)。</summary>
    public static string? SdkProblem(Version plugin, Version app) => IsSdkCompatible(plugin, app)
        ? null
        : Msg.T($"SDK のバージョンが合いません (必要な SDK: {plugin.Major}.{plugin.Minor}、アプリの SDK: {app.Major}.{app.Minor})。",
            $"The SDK version does not match (required SDK: {plugin.Major}.{plugin.Minor}, application SDK: {app.Major}.{app.Minor}).");

    /// <summary>
    /// アプリのバージョンが <c>minAppVersion</c> を満たさない理由 (満たすなら null)。ローカルのビルド (<c>-local</c>) は開発中の最新のコードとして扱い、判定しない。
    /// </summary>
    public static string? AppVersionProblem(PluginVersion? required, string appVersion)
    {
        if (required is null || appVersion.EndsWith("-local", StringComparison.Ordinal))
        {
            return null;
        }

        if (!PluginVersion.TryParse(appVersion, out PluginVersion? app) || app!.CompareTo(required) >= 0)
        {
            return null;
        }

        return Msg.T($"アプリのバージョン {required} 以降が必要です (このアプリ: {appVersion})。", $"Requires application version {required} or later (this application: {appVersion}).");
    }
}
