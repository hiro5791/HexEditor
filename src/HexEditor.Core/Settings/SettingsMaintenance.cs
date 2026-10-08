using System.Globalization;

namespace HexEditor.Core.Settings;

/// <summary>全体のリセット・インポートの対象 (UI-24 の仕様 3、UI-25 の仕様 1)。</summary>
[Flags]
public enum SettingsParts
{
    None = 0,
    Settings = 1,
    KeyBindings = 2,
    Themes = 4,
    RecentAndState = 8,
    Documents = 16,

    /// <summary>全体のリセットの既定 (設定とキー割り当てだけ。UI-24 の仕様 3)。</summary>
    ResetDefault = Settings | KeyBindings,
}

/// <summary>設定フォルダのファイル (UI-23 の仕様 1) と、リセット・インポートの前のバックアップ (UI-24 の仕様 4)。</summary>
public static class SettingsFiles
{
    public const string Keybindings = "keybindings.json";
    public const string Themes = "themes";
    public const string Session = "session.json";
    public const string Recent = "recent.json";
    public const string Documents = "documents";
    public const string BackupPrefix = "backup-";

    /// <summary>残すバックアップの数。</summary>
    public const int BackupsKept = 5;

    /// <summary>対象の区分のファイル・フォルダ (設定フォルダからの相対パス)。</summary>
    public static IReadOnlyList<string> PathsOf(SettingsParts parts)
    {
        var paths = new List<string>();
        if (parts.HasFlag(SettingsParts.Settings))
        {
            paths.Add(SettingsStore.FileName);
        }

        if (parts.HasFlag(SettingsParts.KeyBindings))
        {
            paths.Add(Keybindings);
        }

        if (parts.HasFlag(SettingsParts.Themes))
        {
            paths.Add(Themes);
        }

        if (parts.HasFlag(SettingsParts.RecentAndState))
        {
            paths.Add(Recent);
            paths.Add(StateStore.FileName);
        }

        if (parts.HasFlag(SettingsParts.Documents))
        {
            paths.Add(Documents);
        }

        return paths;
    }

    /// <summary>
    /// 対象のファイルを <c>backup-&lt;日時&gt;/</c> にコピーし、作ったフォルダを返す。古いバックアップは直近 5 個を残して消す。
    /// コピーに失敗したら例外 (呼び出し側はリセットを中止する。UI-24 の「エラー」)。
    /// </summary>
    public static string CreateBackup(string folder, IEnumerable<string> relativePaths, DateTime now)
    {
        string name = BackupPrefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(folder, name);
        for (int n = 2; Directory.Exists(target); n++)
        {
            target = Path.Combine(folder, $"{name}-{n.ToString(CultureInfo.InvariantCulture)}");
        }

        Directory.CreateDirectory(target);
        foreach (string relative in relativePaths)
        {
            string source = Path.Combine(folder, relative);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(target, relative));
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(source, Path.Combine(target, relative));
            }
        }

        foreach (string old in Directory.GetDirectories(folder, BackupPrefix + "*")
            .OrderByDescending(d => Directory.GetCreationTimeUtc(d)).ThenByDescending(d => d, StringComparer.Ordinal).Skip(BackupsKept))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return target;
    }

    /// <summary>対象のファイル・フォルダを消す (リセット)。</summary>
    public static void Delete(string folder, IEnumerable<string> relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(folder, relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
