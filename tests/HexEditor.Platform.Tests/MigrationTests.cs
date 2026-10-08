using System.Text.Json.Nodes;
using HexEditor.Platform.Migration;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>配布形態の間の移行と共存 (10 の PKG-31)。</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string LocalAppData => _temp.Sub("Local");

    private string InstallerData => Path.Combine(LocalAppData, "HexEditorData");

    private void WriteInstallerData()
    {
        Directory.CreateDirectory(InstallerData);

        // TD-UI-SET-DARK と TD-UI-KEYS-CUSTOM (09 のテストデータ)。
        File.WriteAllText(Path.Combine(InstallerData, "settings.json"), """{ "$schemaVersion": 1, "ui.theme": "dark", "storage.tempDirectory": "D:\\T", "test.update.source": "x" }""");
        File.WriteAllText(Path.Combine(InstallerData, "keybindings.json"),
            """{ "preset": "default", "bindings": [ { "command": "edit.fill", "key": "Ctrl+K Ctrl+F", "when": "editor" }, { "command": "-file.print", "key": "Ctrl+P" } ] }""");
        Directory.CreateDirectory(Path.Combine(InstallerData, "themes"));
        File.WriteAllText(Path.Combine(InstallerData, "themes", "mine.json"), "{}");
    }

    [Fact]
    [Trait(TC, "TC-PKG-31-01")]
    public void Portable_version_finds_the_installer_settings_and_imports_theme_and_shortcuts_without_changing_them()
    {
        WriteInstallerData();
        string portableData = _temp.Sub("p", "HexEditor", "Data");
        OtherDistributionData found = Assert.Single(SettingsMigration.Find(Distribution.Portable, portableData, LocalAppData));
        Assert.Equal(Distribution.Installer, found.Distribution);
        DateTime before = File.GetLastWriteTimeUtc(Path.Combine(InstallerData, "settings.json"));
        string[] filesBefore = [.. Directory.GetFiles(InstallerData, "*", SearchOption.AllDirectories).Order()];

        var applied = new Dictionary<string, JsonNode>();
        MigrationResult result = SettingsMigration.Import(found.Folder, portableData, MigrationCategories.All, (k, v) => applied[k] = v);
        Assert.Empty(result.Failed);
        Assert.Equal("dark", applied["ui.theme"].GetValue<string>());

        // 配布形態に固有の項目とテスト用の項目は取り込まない (UI-25 の仕様 4)。
        Assert.False(applied.ContainsKey("storage.tempDirectory"));
        Assert.False(applied.ContainsKey("test.update.source"));
        Assert.False(applied.ContainsKey("$schemaVersion"));
        Assert.Contains("Ctrl+K Ctrl+F", File.ReadAllText(Path.Combine(portableData, "keybindings.json")));
        Assert.True(File.Exists(Path.Combine(portableData, "themes", "mine.json")));

        // 元のフォルダは変更しない。
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(InstallerData, "settings.json")));
        Assert.Equal(filesBefore, Directory.GetFiles(InstallerData, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    [Trait(TC, "TC-PKG-31-02")]
    public void Installer_and_msix_keep_separate_data_and_single_instance_keys()
    {
        // 設定フォルダと単一インスタンスのキーが配布形態ごとに違う (設定が混ざらず、互いに転送しない)。
        var context = new EnvironmentContext
        {
            ExeFolder = Path.Combine(LocalAppData, "HexEditor", "current"),
            LocalAppData = LocalAppData,
            TempPath = _temp.Sub("Temp"),
            ProbeWritable = _ => true,
        };
        var installer = new InstallerEnvironment(context);
        var msix = new MsixEnvironment(context with
        {
            PackageFolders = () => (Path.Combine(LocalAppData, "Packages", "HexEditor_x", "LocalState"), Path.Combine(LocalAppData, "Packages", "HexEditor_x", "LocalCache")),
            PackageAppUserModelId = () => "HexEditor_x!App",
        });
        Assert.NotEqual(installer.Locations.Settings, msix.Locations.Settings);
        Assert.NotEqual(installer.InstanceKey, msix.InstanceKey);

        // MSIX 版から見ると、インストーラ版の設定が取り込みの候補になり、その逆も同じ。
        WriteInstallerData();
        Directory.CreateDirectory(msix.Locations.Settings);
        File.WriteAllText(Path.Combine(msix.Locations.Settings, "settings.json"), """{ "$schemaVersion": 1, "ui.theme": "light" }""");
        Assert.Equal(Distribution.Installer, Assert.Single(SettingsMigration.Find(Distribution.Msix, msix.Locations.Settings, LocalAppData)).Distribution);
        Assert.Equal(Distribution.Msix, Assert.Single(SettingsMigration.Find(Distribution.Installer, installer.Locations.Settings, LocalAppData)).Distribution);
    }

    [Fact]
    public void A_failed_category_is_listed_and_the_others_are_imported()
    {
        WriteInstallerData();
        File.WriteAllText(Path.Combine(InstallerData, "settings.json"), "{ broken");
        string target = _temp.Sub("target");
        MigrationResult result = SettingsMigration.Import(InstallerData, target, MigrationCategories.Default, (_, _) => { });
        Assert.Equal(MigrationCategories.Settings, Assert.Single(result.Failed).Category);
        Assert.True(result.Imported.HasFlag(MigrationCategories.KeyBindings));
    }

    [Fact]
    public void Nothing_is_offered_without_another_settings_file()
    {
        Assert.Empty(SettingsMigration.Find(Distribution.Portable, _temp.Sub("Data"), LocalAppData));
        WriteInstallerData();
        Assert.Empty(SettingsMigration.Find(Distribution.Installer, InstallerData, LocalAppData));
    }
}
