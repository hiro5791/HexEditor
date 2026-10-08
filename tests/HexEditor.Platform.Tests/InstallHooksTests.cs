using HexEditor.Platform.Shell;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>インストーラ版のフック (PKG-08、PKG-09)。レジストリは偽物に差し替える (この PC には書かない)。</summary>
public sealed class InstallHooksTests : IDisposable
{
    private const string Exe = @"C:\Users\u\AppData\Local\HexEditor\current\HexEditor.exe";
    private readonly TempFolder _temp = new();
    private readonly FakeRegistry _registry = new();
    private int _notified;

    public void Dispose() => _temp.Dispose();

    private string DataRoot => _temp.Sub("HexEditorData");

    private const string Folder = @"C:\Users\u\AppData\Local\HexEditor\current\";
    private int _environmentNotified;

    private InstallHooks Hooks(TimeSpan? budget = null) => new(_registry, DataRoot, Exe, () => _notified++, budget, notifyEnvironmentChanged: () => _environmentNotified++);

    /// <summary>設定画面 (インストーラ版) と同じ登録・解除。state.json はフックと同じデータフォルダのもの。</summary>
    private ShellIntegration Settings(Distribution distribution = Distribution.Installer) =>
        new(_registry, distribution, Exe, new OtherInstallations(_temp.Sub("LocalAppData")), () => ShellLabels.English,
            () => ShellRegistration.DefaultOpenWithExtensions, null, new StateFile(DataRoot), () => _environmentNotified++);

    private const string UserPathBefore = @"%USERPROFILE%\bin;C:\Tools";

    private IReadOnlyList<string> PathEntries() => UserPath.Entries(_registry);

    [Fact]
    public void AfterInstallRegistersAppPathsInHkcu()
    {
        Hooks().AfterInstall("1.0.0");
        Assert.Equal(Exe, _registry.GetValue(ShellRegistration.AppPathsKey, null));
        Assert.Equal(Path.GetDirectoryName(Exe), _registry.GetValue(ShellRegistration.AppPathsKey, "Path"));
        Assert.Equal(1, _notified);
        Assert.Contains("after-install 1.0.0", File.ReadAllText(Hooks().LogPath));
    }

    [Fact]
    public void EveryEntryIsUnderHkcuSoftware()
    {
        // 登録先はすべて HKCU (IUserRegistry は HKCU だけを扱う)。HKLM の場所を書いた項目がないことも確かめる (PKG-08 の仕様 3)。
        Hooks().AfterInstall("1.0.0");
        Assert.All(_registry.Keys.Where(k => k != UserPath.EnvironmentKey), key =>
        {
            Assert.StartsWith(@"Software\", key);
            Assert.DoesNotContain("HKEY_LOCAL_MACHINE", key, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(ShellRegistration.AppPathsId, ShellRegistration.Entries[0].Id);
    }

    [Fact]
    public void AfterUpdateIsIdempotent()
    {
        Hooks().AfterInstall("1.0.0");
        IReadOnlyDictionary<string, string> installed = _registry.Snapshot();
        Hooks().AfterUpdate("1.1.0");
        Hooks().AfterUpdate("1.1.0");
        Assert.Equal(installed.OrderBy(p => p.Key), _registry.Snapshot().OrderBy(p => p.Key));
        Assert.Equal(Exe, _registry.GetValue(ShellRegistration.AppPathsKey, null));
    }

    [Fact]
    public void AfterUpdateDoesNotRestoreEntriesTheUserTurnedOff()
    {
        Hooks().AfterInstall("1.0.0");
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, InstallHooks.StateFileName), """{ "shellRegistration": { "disabled": ["appPaths"] } }""");
        Hooks().AfterUpdate("1.1.0");
        Assert.False(_registry.KeyExists(ShellRegistration.AppPathsKey));
    }

    [Fact]
    public void BeforeUninstallRemovesEveryEntryInTheList()
    {
        Hooks().AfterInstall("1.0.0");
        Hooks().BeforeUninstall("1.0.0");
        Assert.All(ShellRegistration.Entries, e => Assert.False(_registry.KeyExists(e.Key)));
        Assert.DoesNotContain(_registry.Keys, k => k != UserPath.EnvironmentKey);
        Assert.Null(_registry.GetRawString(UserPath.EnvironmentKey, UserPath.ValueName));
    }

    // ---- 登録と PATH (PKG-08 の仕様 2・6) ----

    [Fact]
    [Trait(TC, "TC-PKG-08-01")]
    public void AfterInstallRegistersTheMenuAndAppendsTheFolderToTheUserPathOnce()
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        Hooks().AfterInstall("1.0.0");

        // 右クリックメニューと、ShellRegistration の一覧のキーがある。
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.All(ShellRegistration.Entries, e => Assert.All(e.OwnedKeys, k => Assert.True(_registry.KeyExists(k), k)));

        // PATH の末尾に 1 つだけ足し、他の項目 (展開しない %USERPROFILE%) と値の種類はそのまま。
        Assert.Equal([@"%USERPROFILE%\bin", @"C:\Tools", Folder], PathEntries());
        Assert.True(_registry.GetRawString(UserPath.EnvironmentKey, UserPath.ValueName)!.Value.Expandable);
        Assert.Equal(1, _environmentNotified);

        // 更新を繰り返しても 1 つだけ。
        Hooks().AfterUpdate("1.0.1");
        Hooks().AfterUpdate("1.0.2");
        Assert.Equal([@"%USERPROFILE%\bin", @"C:\Tools", Folder], PathEntries());
        Assert.Equal(1, _environmentNotified);
    }

    [Theory]
    [Trait(TC, "TC-PKG-08-01")]
    [InlineData(@"C:\Tools;C:\Users\u\AppData\Local\HexEditor\current")]
    [InlineData(@"c:\users\u\appdata\local\hexeditor\current\;C:\Tools")]
    [InlineData(@"C:\Tools;""C:\Users\u\AppData\Local\HexEditor\current\"";")]
    public void TheFolderIsNotAddedTwice(string path)
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, path, expandable: false);
        Hooks().AfterInstall("1.0.0");
        Assert.Equal(path, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));
        Assert.Equal(0, _environmentNotified);
    }

    [Fact]
    [Trait(TC, "TC-PKG-08-01")]
    public void WithoutAUserPathTheValueIsCreated()
    {
        Hooks().AfterInstall("1.0.0");
        (string value, bool expandable) = _registry.GetRawString(UserPath.EnvironmentKey, UserPath.ValueName)!.Value;
        Assert.Equal(Folder, value);
        Assert.True(expandable);
    }

    [Fact]
    [Trait(TC, "TC-PKG-08-02")]
    public void BeforeUninstallRemovesTheRegistrationAndOnlyTheAddedPathEntry()
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        _registry.Seed(@"Software\Classes\.iso\OpenWithProgids", "Other.Iso", string.Empty);
        Hooks().AfterInstall("1.0.0");
        Hooks().BeforeUninstall("1.0.0");

        // ShellRegistration の一覧のキー・値がすべてない (他のアプリの値は残る)。
        Assert.All(ShellRegistration.Entries, e => Assert.All(e.OwnedKeys, k => Assert.False(_registry.KeyExists(k), k)));
        Assert.All(ShellRegistration.DefaultOpenWithExtensions, e =>
            Assert.Null(_registry.GetValue($@"Software\Classes\{e}\OpenWithProgids", ShellRegistration.BinaryProgId)));
        Assert.Equal(string.Empty, _registry.GetValue(@"Software\Classes\.iso\OpenWithProgids", "Other.Iso"));

        // PATH はインストール前と同じ。
        Assert.Equal(UserPathBefore, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));
        Assert.True(_registry.GetRawString(UserPath.EnvironmentKey, UserPath.ValueName)!.Value.Expandable);
        Assert.Equal(2, _environmentNotified);
    }

    [Fact]
    [Trait(TC, "TC-PKG-08-02")]
    public void BeforeUninstallClearsTheJumpListAndTheToastRegistration()
    {
        int jumpList = 0, toast = 0;
        var hooks = new InstallHooks(_registry, DataRoot, Exe, () => _notified++, notifyEnvironmentChanged: () => _environmentNotified++,
            clearJumpList: () => jumpList++, unregisterToast: () => toast++);
        hooks.AfterInstall("1.0.0");
        Assert.Equal((0, 0), (jumpList, toast));
        hooks.BeforeUninstall("1.0.0");
        Assert.Equal((1, 1), (jumpList, toast));
        string log = File.ReadAllText(hooks.LogPath);
        Assert.Contains("jumpList: removed.", log);
        Assert.Contains("toast: removed.", log);
    }

    [Fact]
    public void FailingToastUnregistrationIsLoggedAndTheRestContinues()
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        int jumpList = 0;
        var hooks = new InstallHooks(_registry, DataRoot, Exe, () => _notified++, notifyEnvironmentChanged: () => _environmentNotified++,
            clearJumpList: () => jumpList++, unregisterToast: () => throw new System.Runtime.InteropServices.COMException("toast"));
        hooks.AfterInstall("1.0.0");
        hooks.BeforeUninstall("1.0.0");
        Assert.Equal(1, jumpList);
        Assert.False(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.Equal(UserPathBefore, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));
        Assert.Contains("toast failed: COMException", File.ReadAllText(hooks.LogPath));
    }

    [Fact]
    public void UnregisterCommandRemovesEverythingButKeepsTheData()
    {
        // HexEditor.exe --unregister (インストーラ版。08 の AUTO-36 の 9): アンインストール前のフックと同じ解除で、データフォルダは消さない。
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        int jumpList = 0, toast = 0;
        var hooks = new InstallHooks(_registry, DataRoot, Exe, () => _notified++, notifyEnvironmentChanged: () => _environmentNotified++,
            clearJumpList: () => jumpList++, unregisterToast: () => toast++);
        hooks.AfterInstall("1.0.0");
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), "{\"uninstall.removeUserData\": true}");

        UnregisterResult result = Unregistration.Run(Distribution.Installer, () => [], hooks.Unregister);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.ExitCode);
        Assert.All(ShellRegistration.Entries, e => Assert.All(e.OwnedKeys, k => Assert.False(_registry.KeyExists(k), k)));
        Assert.Equal(UserPathBefore, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));
        Assert.Equal((1, 1), (jumpList, toast));
        Assert.True(File.Exists(Path.Combine(DataRoot, "settings.json")));
        Assert.Contains("unregister --unregister", File.ReadAllText(hooks.LogPath));
    }

    [Fact]
    public void UnregisterCommandReportsRegistryFailures()
    {
        Hooks().AfterInstall("1.0.0");
        _registry.FailDeletes = true;
        IReadOnlyList<string> failures = Hooks().Unregister();
        Assert.Contains(failures, f => f.StartsWith(ShellRegistration.ContextMenuId + ": ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-PKG-08-02")]
    public void BeforeUninstallLeavesAPathWithoutTheFolderUnchanged()
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        Hooks().BeforeUninstall("1.0.0");
        Assert.Equal(UserPathBefore, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));
        Assert.Equal(0, _environmentNotified);
    }

    // ---- 利用者が解除した登録は更新で復活しない (PKG-08 の仕様 2・5・6) ----

    [Fact]
    [Trait(TC, "TC-PKG-08-03")]
    public void ContextMenuUnregisteredInTheSettingsStaysOffAfterAnUpdate()
    {
        Hooks().AfterInstall("0.9.0");

        // 設定画面「Explorer 連携」で右クリックメニューの登録を解除する。
        Assert.Empty(Settings().Unregister(new HashSet<string> { ShellRegistration.ContextMenuId }));
        Assert.Contains(ShellRegistration.ContextMenuId, Hooks().ReadDisabled());

        // 0.9.1 に更新: メニューは復活せず、ファイルの関連付けは登録し直される。
        _registry.DeleteKeyTree($@"{ShellRegistration.ClassesKey}\{ShellRegistration.ProjectProgId}");
        Hooks().AfterUpdate("0.9.1");
        Assert.False(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.True(_registry.KeyExists($@"{ShellRegistration.ClassesKey}\{ShellRegistration.ProjectProgId}"));

        // 登録し直すと記録が消え、次の更新でも登録したまま。
        Assert.Empty(Settings().Register(new HashSet<string> { ShellRegistration.ContextMenuId }));
        Assert.DoesNotContain(ShellRegistration.ContextMenuId, Hooks().ReadDisabled());
        Hooks().AfterUpdate("0.9.2");
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
    }

    [Fact]
    [Trait(TC, "TC-PKG-08-03")]
    public void CommandLineTurnedOffStaysOffAfterAnUpdate()
    {
        _registry.SeedString(UserPath.EnvironmentKey, UserPath.ValueName, UserPathBefore, expandable: true);
        Hooks().AfterInstall("0.9.0");
        ShellIntegration settings = Settings();
        Assert.True(settings.CommandLineEnabled);

        // 設定画面「詳細」の「コマンドラインから使えるようにする」を外す。
        Assert.Empty(settings.SetCommandLineEnabled(false));
        Assert.False(settings.CommandLineEnabled);
        Assert.Equal(UserPathBefore, _registry.GetValue(UserPath.EnvironmentKey, UserPath.ValueName));

        Hooks().AfterUpdate("0.9.1");
        Assert.DoesNotContain(PathEntries(), e => e.Equals(Folder, StringComparison.OrdinalIgnoreCase));

        // 付け直すと、次の更新でも残る。
        Assert.Empty(settings.SetCommandLineEnabled(true));
        Hooks().AfterUpdate("0.9.2");
        Assert.Single(PathEntries(), e => e.Equals(Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PortableVersionDoesNotRecordUnregistering()
    {
        // ポータブル版には更新後のフックがなく、データフォルダもアプリのフォルダの中にある (記録しない)。
        Settings(Distribution.Portable).Register();
        Settings(Distribution.Portable).Unregister();
        Assert.False(File.Exists(Path.Combine(DataRoot, InstallHooks.StateFileName)));
        Assert.Equal(["unsupported"], Settings(Distribution.Portable).SetCommandLineEnabled(false));
    }

    [Fact]
    public void BeforeUninstallKeepsUserDataByDefault()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), """{ "$schemaVersion": 1, "ui.theme": "dark" }""");
        Hooks().BeforeUninstall("1.0.0");
        Assert.True(File.Exists(Path.Combine(DataRoot, "settings.json")));
    }

    [Fact]
    public void BeforeUninstallRemovesUserDataWhenTheSettingIsOn()
    {
        Directory.CreateDirectory(Path.Combine(DataRoot, "recovery"));
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), """{ "$schemaVersion": 1, "uninstall.removeUserData": true }""");
        Hooks().BeforeUninstall("1.0.0");
        Assert.False(Directory.Exists(DataRoot));
    }

    [Theory]
    [InlineData("""{ "uninstall.removeUserData": false }""")]
    [InlineData("""{ "uninstall.removeUserData": "yes" }""")]
    [InlineData("not json")]
    public void BrokenOrFalseSettingKeepsUserData(string settings)
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), settings);
        Hooks().BeforeUninstall("1.0.0");
        Assert.True(Directory.Exists(DataRoot));
    }

    [Fact]
    public void FailuresAreLoggedAndDoNotThrow()
    {
        _registry.FailWrites = true;
        Hooks().AfterInstall("1.0.0");
        string log = File.ReadAllText(Hooks().LogPath);
        Assert.Contains("Register failed: appPaths", log);
    }

    [Fact]
    public void StopsWhenTheTimeLimitIsUsedUp()
    {
        Hooks(TimeSpan.Zero).AfterInstall("1.0.0");
        Assert.False(_registry.KeyExists(ShellRegistration.AppPathsKey));
        Assert.Contains("time limit", File.ReadAllText(Hooks().LogPath));
    }

    [Fact]
    public void HooksFinishWithinFiveSeconds()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Hooks().AfterInstall("1.0.0");
        Hooks().AfterUpdate("1.0.1");
        Hooks().BeforeUninstall("1.0.1");
        Assert.True(clock.Elapsed < InstallHooks.Budget);
    }

    /// <summary>
    /// 配布のテスト (build/tests/Test-Installer.ps1 の TC-PKG-08-01・02) は Windows PowerShell で動き、アプリのアセンブリを読めないため、
    /// ShellRegistration の一覧の写しを持つ。写しが一覧と同じであることを確かめる。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-PKG-08-02")]
    public void InstallerScriptListsEveryShellRegistrationKey()
    {
        string script = File.ReadAllText(RepoFile("build/tests/Test-Installer.ps1"));
        static IReadOnlyList<string> Quoted(string block) =>
            [.. System.Text.RegularExpressions.Regex.Matches(block, "'([^']+)'").Select(m => m.Groups[1].Value)];
        string Block(string name)
        {
            int start = script.IndexOf($"${name} = @(", StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            return script[start..script.IndexOf(')', start)];
        }

        Assert.Equal(ShellRegistration.Entries.SelectMany(e => e.OwnedKeys), Quoted(Block("shellRegistrationKeys")));
        Assert.Equal(ShellRegistration.DefaultOpenWithExtensions, Quoted(Block("openWithExtensions")));
    }
}
