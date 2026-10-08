using HexEditor.Core.Settings;

namespace HexEditor.Core.Tests.Settings;

/// <summary>配布形態に固有の設定項目の表示 (10 の PKG-17 の仕様 2: update.downloadAutomatically はインストーラ版のみ表示)。</summary>
public sealed class SettingVisibilityTests
{
    private static SettingTexts Texts(SettingDefinition s) => new(s.Key, string.Empty, s.Key);

    [Theory]
    [InlineData(SettingDistributions.Installer, true)]
    [InlineData(SettingDistributions.Portable, false)]
    [InlineData(SettingDistributions.Msix, false)]
    [InlineData(null, true)]
    public void Download_automatically_is_shown_only_for_the_installer(string? distribution, bool shown)
    {
        SettingsCatalog catalog = SettingsCatalog.CreateBuiltIn();
        catalog.Distribution = distribution;
        SettingDefinition download = catalog.Find("update.downloadAutomatically")!;

        Assert.Equal(shown, catalog.IsShown(download));
        Assert.Equal(shown, catalog.Search("update.downloadAutomatically", Texts).Any(r => r.Setting == download));

        // 配布形態に関係のない項目はどの配布形態でも出す。
        Assert.True(catalog.IsShown(catalog.Find("update.checkAutomatically")!));
    }

    /// <summary>「コマンドラインから使えるようにする」はインストーラ版だけ (10 の PKG-08 の仕様 6)。PC ごとの状態なのでエクスポートしない。</summary>
    [Theory]
    [InlineData(SettingDistributions.Installer, true)]
    [InlineData(SettingDistributions.Portable, false)]
    [InlineData(SettingDistributions.Msix, false)]
    public void Command_line_availability_is_shown_only_for_the_installer(string distribution, bool shown)
    {
        SettingsCatalog catalog = SettingsCatalog.CreateBuiltIn();
        catalog.Distribution = distribution;
        SettingDefinition commandLine = catalog.Find("shell.commandLine.enabled")!;

        Assert.Equal(shown, catalog.IsShown(commandLine));
        Assert.True((bool)commandLine.Default!);
        Assert.False(commandLine.Exportable);
    }
}
