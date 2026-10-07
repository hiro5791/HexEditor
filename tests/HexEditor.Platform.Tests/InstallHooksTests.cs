using HexEditor.Platform.Shell;
using HexEditor.Platform.Tests.Support;

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

    private InstallHooks Hooks(TimeSpan? budget = null) => new(_registry, DataRoot, Exe, () => _notified++, budget);

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
        Assert.All(ShellRegistration.Entries, e =>
        {
            Assert.StartsWith(@"Software\", e.Key);
            Assert.DoesNotContain("HKEY_LOCAL_MACHINE", e.Key, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(ShellRegistration.AppPathsId, ShellRegistration.Entries[0].Id);
    }

    [Fact]
    public void AfterUpdateIsIdempotent()
    {
        Hooks().AfterInstall("1.0.0");
        Hooks().AfterUpdate("1.1.0");
        Hooks().AfterUpdate("1.1.0");
        Assert.Single(_registry.Keys);
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
        Assert.Empty(_registry.Keys);
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

    private sealed class FakeRegistry : IUserRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool FailWrites { get; set; }

        public IReadOnlyCollection<string> Keys => _keys.Keys;

        public void SetValue(string key, string? name, string value)
        {
            if (FailWrites)
            {
                throw new UnauthorizedAccessException();
            }

            if (!_keys.TryGetValue(key, out Dictionary<string, string>? values))
            {
                _keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            }

            values[name ?? string.Empty] = value;
        }

        public string? GetValue(string key, string? name) =>
            _keys.TryGetValue(key, out Dictionary<string, string>? values) && values.TryGetValue(name ?? string.Empty, out string? v) ? v : null;

        public bool KeyExists(string key) => _keys.ContainsKey(key);

        public void DeleteKeyTree(string key)
        {
            foreach (string k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _keys.Remove(k);
            }
        }
    }
}
