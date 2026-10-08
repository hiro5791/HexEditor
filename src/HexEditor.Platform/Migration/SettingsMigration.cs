using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Platform.Migration;

/// <summary>取り込む区分 (10 の PKG-31 の仕様 2、09 の UI-25 の仕様 1)。</summary>
[Flags]
public enum MigrationCategories
{
    None = 0,
    Settings = 1,
    KeyBindings = 2,
    Themes = 4,
    RecentFiles = 8,
    DocumentData = 16,

    /// <summary>既定で選ぶ区分 (設定・キー割り当て・配色)。</summary>
    Default = Settings | KeyBindings | Themes,
    All = Settings | KeyBindings | Themes | RecentFiles | DocumentData,
}

/// <summary>他の配布形態の設定フォルダ 1 つ。</summary>
public sealed record OtherDistributionData(Distribution Distribution, string Folder);

/// <summary>取り込みの結果: 取り込んだ区分と、失敗した区分と理由 (PKG-31 の「エラー」: 失敗した区分は一覧で示し、他は取り込む)。</summary>
public sealed record MigrationResult(MigrationCategories Imported, IReadOnlyList<(MigrationCategories Category, string Reason)> Failed);

/// <summary>
/// 配布形態の間の設定の取り込み (10 の PKG-31)。他の配布形態の設定フォルダ (PKG-13 の MSIX・インストーラの場所。ポータブル版は
/// 場所が分からないため対象外) を探し、選んだ区分を今の設定フォルダに取り込む。元のフォルダは変更しない (読むだけ)。
/// 取り込みの形は 09 の UI-25 のインポートと同じ区分で、設定はマージする (取り込む側にないキーだけでなく、同じキーは取り込む値で上書き)。
/// 配布形態に固有の項目 (データフォルダの場所、Explorer 連携の登録状態。UI-25 の仕様 4) とテスト用の項目は取り込まない。
/// </summary>
public static class SettingsMigration
{
    /// <summary>取り込まない設定のキー (前方一致)。</summary>
    public static IReadOnlyList<string> ExcludedKeyPrefixes { get; } =
    [
        "$schema", "storage.tempDirectory", "uninstall.", "test.", "shell.", "notifications.toast",
    ];

    /// <summary>
    /// 他の配布形態の設定フォルダのうち、<c>settings.json</c> があるもの (PKG-31 の仕様 1)。今の配布形態と、今の設定フォルダは除く。
    /// MSIX 版の場所は <c>%LocalAppData%\Packages\HexEditor_*\LocalState</c>。
    /// </summary>
    public static IReadOnlyList<OtherDistributionData> Find(Distribution current, string currentFolder, string localAppData)
    {
        var found = new List<OtherDistributionData>();
        string own = Path.GetFullPath(currentFolder).TrimEnd('\\');
        void Add(Distribution d, string folder)
        {
            if (d != current && File.Exists(Path.Combine(folder, "settings.json"))
                && !Path.GetFullPath(folder).TrimEnd('\\').Equals(own, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new OtherDistributionData(d, folder));
            }
        }

        Add(Distribution.Installer, Path.Combine(localAppData, AppEnvironment.InstallerDataFolderName));
        try
        {
            string packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string dir in Directory.EnumerateDirectories(packages, "HexEditor_*"))
                {
                    Add(Distribution.Msix, Path.Combine(dir, "LocalState"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    /// <summary>取り込む設定 (キーと値)。除くキーを除いたもの。</summary>
    public static IReadOnlyList<KeyValuePair<string, JsonNode>> ReadSettings(string folder)
    {
        JsonObject source = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "settings.json"))) as JsonObject
            ?? throw new FormatException("settings.json is not a JSON object.");
        return [.. source.Where(p => p.Value is not null && !ExcludedKeyPrefixes.Any(x => p.Key.StartsWith(x, StringComparison.Ordinal)))
            .Select(p => new KeyValuePair<string, JsonNode>(p.Key, p.Value!.DeepClone()))];
    }

    /// <summary>
    /// 選んだ区分を取り込む。設定は <paramref name="applySetting"/> で今の設定 (SettingsStore) に入れる (書き込みの順序と通知を
    /// 設定の仕組みに任せる)。ファイルの区分は今の設定フォルダにコピーする (同じ名前は上書き)。
    /// </summary>
    public static MigrationResult Import(string sourceFolder, string targetFolder, MigrationCategories categories,
        Action<string, JsonNode> applySetting)
    {
        var imported = MigrationCategories.None;
        var failed = new List<(MigrationCategories, string)>();
        void Try(MigrationCategories category, Action action)
        {
            if (!categories.HasFlag(category))
            {
                return;
            }

            try
            {
                action();
                imported |= category;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            {
                failed.Add((category, ex.Message));
            }
        }

        Try(MigrationCategories.Settings, () =>
        {
            foreach ((string key, JsonNode value) in ReadSettings(sourceFolder))
            {
                applySetting(key, value);
            }
        });
        Try(MigrationCategories.KeyBindings, () => CopyFile(sourceFolder, targetFolder, "keybindings.json"));
        Try(MigrationCategories.Themes, () => CopyFolder(Path.Combine(sourceFolder, "themes"), Path.Combine(targetFolder, "themes")));
        Try(MigrationCategories.RecentFiles, () => CopyFile(sourceFolder, targetFolder, "recent.json"));
        Try(MigrationCategories.DocumentData, () => CopyFolder(Path.Combine(sourceFolder, "documents"), Path.Combine(targetFolder, "documents")));
        return new MigrationResult(imported, failed);
    }

    private static void CopyFile(string from, string to, string name)
    {
        string source = Path.Combine(from, name);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(to);
            File.Copy(source, Path.Combine(to, name), overwrite: true);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
