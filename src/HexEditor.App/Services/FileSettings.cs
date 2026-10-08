using HexEditor.Core.Files;
using HexEditor.Core.Saving;
using HexEditor.Core.Settings;

namespace HexEditor.App.Services;

/// <summary>
/// ファイル・セッションの設定 (ENG-16、ENG-19、ENG-26、ENG-27、UI-30、UI-32)。設定画面 (UI-22) ができるまでは settings.json で変える。
/// </summary>
public static class FileSettings
{
    /// <summary>最近使ったファイルの件数 (0〜100、既定 25。ENG-16 の仕様 3)。</summary>
    public const string RecentMaxItemsKey = "recent.maxItems";

    /// <summary>前回の位置を復元する (既定 true。ENG-16 の仕様 5)。</summary>
    public const string RestorePositionKey = "recent.restorePosition";

    /// <summary>起動時の動作 (always / ask / never。UI-30)。</summary>
    public const string RestoreOnStartupKey = StartupPlanner.RestoreOnStartupKey;

    /// <summary>未編集のファイルは自動で再読み込みする (既定 true。ENG-19 の仕様 4)。</summary>
    public const string AutoReloadKey = ExternalChangeRules.AutoReloadKey;

    /// <summary>復旧用データの保存間隔 (分。0 = 無効、1〜60、既定 1。ENG-27 の仕様 1)。</summary>
    public const string RecoveryIntervalKey = "save.recoveryIntervalMinutes";

    public static int RecentMaxItems(SettingsStore settings) =>
        Math.Clamp(settings.GetInt(RecentMaxItemsKey, RecentFileList.DefaultMaxItems), 0, RecentFileList.MaxItemsLimit);

    public static bool RestorePosition(SettingsStore settings) => settings.GetBool(RestorePositionKey, true);

    public static RestoreOnStartup RestoreOnStartup(SettingsStore settings) =>
        StartupPlanner.Parse(settings.GetString(RestoreOnStartupKey, "always"));

    public static bool AutoReload(SettingsStore settings) => settings.GetBool(AutoReloadKey, true);

    /// <summary>復旧用データの保存間隔。0 (無効) なら null。</summary>
    public static TimeSpan? RecoveryInterval(SettingsStore settings)
    {
        int minutes = Math.Clamp(settings.GetInt(RecoveryIntervalKey, 1), 0, 60);
        return minutes == 0 ? null : TimeSpan.FromMinutes(minutes);
    }

    /// <summary>「バックアップを作る」(ENG-26)。オフなら null。</summary>
    public static BackupSettings? Backup(SettingsStore settings)
    {
        if (!settings.GetBool(BackupSettings.EnabledKey, false))
        {
            return null;
        }

        string folder = settings.GetString(BackupSettings.FolderKey, string.Empty);
        bool inFolder = settings.GetString(BackupSettings.LocationKey, "sameFolder") == "folder" && folder.Length > 0;
        return new BackupSettings
        {
            Folder = inFolder ? Environment.ExpandEnvironmentVariables(folder) : null,
            Generations = Math.Clamp(settings.GetInt(BackupSettings.GenerationsKey, 1), 1, BackupSettings.MaxGenerations),
        };
    }
}
