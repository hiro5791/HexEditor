namespace HexEditor.Platform.Updates;

/// <summary>
/// 更新の設定 (10 の PKG-17 の仕様 2、PKG-21)。値は settings.json から毎回読む (設定画面や外部の編集をすぐに反映する)。
/// </summary>
public sealed class UpdatePreferences(ISettingsAccess settings, bool testBuild = false)
{
    public const string CheckAutomaticallyKey = "update.checkAutomatically";
    public const string DownloadAutomaticallyKey = "update.downloadAutomatically";
    public const string ChannelKey = "update.channel";
    public const string SkippedVersionKey = "update.skippedVersion";

    /// <summary>テスト用のビルドだけが読む更新の確認先 (PKG-17 の仕様 8)。製品版は読まない。</summary>
    public const string TestSourceKey = "test.update.source";

    public bool CheckAutomatically => settings.GetBool(CheckAutomaticallyKey, true);

    public bool DownloadAutomatically => settings.GetBool(DownloadAutomaticallyKey, true);

    /// <summary>チャネル (<c>stable</c> / <c>preview</c>)。知らない値は stable。</summary>
    public ReleaseChannel Channel
    {
        get => settings.GetString(ChannelKey, "stable").Equals("preview", StringComparison.OrdinalIgnoreCase) ? ReleaseChannel.Preview : ReleaseChannel.Stable;
        set => settings.SetString(ChannelKey, value == ReleaseChannel.Preview ? "preview" : "stable", "stable");
    }

    /// <summary>「この版をスキップ」した版 (PKG-22 の仕様 5)。なければ null。</summary>
    public SemanticVersion? SkippedVersion
    {
        get => SemanticVersion.TryParse(settings.GetString(SkippedVersionKey, string.Empty), out SemanticVersion v, out _) ? v : null;
        set => settings.SetString(SkippedVersionKey, value?.SemVer ?? string.Empty, string.Empty);
    }

    /// <summary>テスト用の確認先。テスト用のビルドでなければ常に null (PKG-17 の仕様 8: 製品版はこのキーを読まない)。</summary>
    public string? TestSource => testBuild && settings.GetString(TestSourceKey, string.Empty) is { Length: > 0 } s ? s : null;
}
