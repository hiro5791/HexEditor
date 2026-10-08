using HexEditor.Platform.Shell;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// Explorer 連携 (09 の UI-54 従来の右クリックメニュー、UI-56 ファイルの関連付け) の登録の内容と状態。レジストリは偽物
/// (<see cref="FakeRegistry"/>) で、この PC には書かない。インストールした配布物での確認は build/tests/Test-Installer.ps1 と
/// Test-Portable.ps1 (CI) が行う。
/// </summary>
public sealed class ShellIntegrationTests
{
    private const string LocalAppData = @"C:\Users\u\AppData\Local";
    private const string InstallerExe = LocalAppData + @"\HexEditor\current\HexEditor.exe";
    private const string PortableExe = @"C:\p1\HexEditor\HexEditor.exe";
    private const string MovedExe = @"C:\p2\HexEditor\HexEditor.exe";

    private readonly FakeRegistry _registry = new();
    private int _notified;
    private ShellLabels _labels = new("HexEditor で開く", "HexEditor プロジェクト", "HexEditor ワークスペース", "バイナリ ファイル");

    private ShellIntegration Shell(Distribution distribution, string exe) =>
        new(_registry, distribution, exe, new OtherInstallations(LocalAppData), () => _labels, () => ShellRegistration.DefaultOpenWithExtensions, () => _notified++);

    [Fact]
    [Trait(TC, "TC-UI-54-01")]
    public void Installer_hooks_register_the_context_menu_under_hkcu_with_the_app_icon()
    {
        using var temp = new TempFolder();
        new InstallHooks(_registry, temp.Path, InstallerExe, () => _notified++, labels: _labels).AfterInstall("1.0.0");
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.Equal("HexEditor で開く", _registry.GetValue(@"Software\Classes\*\shell\HexEditor", null));
        Assert.Equal($"\"{InstallerExe}\",0", _registry.GetValue(ShellRegistration.ContextMenuKey, "Icon"));
        Assert.Equal($"\"{InstallerExe}\" \"%1\"", _registry.GetValue(ShellRegistration.ContextMenuKey + @"\command", null));
        Assert.All(_registry.Keys, k => Assert.StartsWith(@"Software\", k));
    }

    [Fact]
    [Trait(TC, "TC-UI-54-02")]
    public void Portable_version_writes_nothing_until_the_user_registers()
    {
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        ShellIntegrationState state = shell.State();
        Assert.True(state.Supported);
        Assert.False(state.ContextMenuRegistered);
        Assert.Empty(_registry.Keys);

        Assert.Empty(shell.Register());
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.Equal(1, _notified);

        // App Paths はインストーラ版のフックだけが扱う (ポータブル版の「登録する」の対象外)。
        Assert.False(_registry.KeyExists(ShellRegistration.AppPathsKey));
    }

    [Fact]
    [Trait(TC, "TC-UI-54-03")]
    public void Moving_the_portable_folder_is_detected_and_update_points_to_the_new_exe()
    {
        Shell(Distribution.Portable, PortableExe).Register();
        ShellIntegration moved = Shell(Distribution.Portable, MovedExe);
        ShellIntegrationState state = moved.State();
        Assert.True(state.Stale);
        Assert.Equal(PortableExe, state.RegisteredExe);

        moved.Register();
        Assert.False(moved.State().Stale);
        Assert.Equal($"\"{MovedExe}\" \"%1\"", _registry.GetValue(ShellRegistration.ContextMenuKey + @"\command", null));
        Assert.Equal($"\"{MovedExe}\" \"%1\"", _registry.GetValue(@"Software\Classes\HexEditor.Project\shell\open\command", null));
    }

    [Fact]
    [Trait(TC, "TC-UI-54-04")]
    public void The_menu_command_passes_one_file_so_the_single_instance_merges_them_into_one_window()
    {
        // Explorer は選んだファイルごとに "%1" のコマンドを実行する。各起動は単一インスタンス (UI-15) で既存のウィンドウに転送される。
        // 3 つのファイルを実際に開く確認は build/tests/Test-Installer.ps1 (IContextMenu で実行する)。
        Assert.Equal("\"C:\\a b\\HexEditor.exe\" \"%1\"", ShellRegistration.Command(@"C:\a b\HexEditor.exe"));
        Assert.Equal(@"C:\a b\HexEditor.exe", ShellRegistration.ExeFromCommand("\"C:\\a b\\HexEditor.exe\" \"%1\""));
        CommandLine parsed = CommandLine.ParseString("\"C:\\a b\\HexEditor.exe\" \"C:\\data\\file01.bin\"");
        Assert.Equal([@"C:\data\file01.bin"], parsed.Files);
    }

    [Fact]
    public void Another_distribution_registration_is_reported_and_is_not_called_stale()
    {
        Shell(Distribution.Installer, InstallerExe).Register();
        ShellIntegrationState portable = Shell(Distribution.Portable, PortableExe).State();
        Assert.False(portable.Stale);
        Assert.True(portable.OtherDistributionRegistered);

        Shell(Distribution.Portable, PortableExe).Register();
        ShellIntegrationState installer = Shell(Distribution.Installer, InstallerExe).State();
        Assert.True(installer.OtherDistributionRegistered);
    }

    [Fact]
    public void Msix_and_development_do_not_register()
    {
        Assert.False(Shell(Distribution.Msix, PortableExe).Supported);
        Assert.False(Shell(Distribution.Development, PortableExe).Supported);
        Assert.Equal(["unsupported"], Shell(Distribution.Development, PortableExe).Register());
        Assert.Empty(_registry.Keys);
    }

    [Fact]
    public void Changing_the_display_language_rewrites_the_menu_name()
    {
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        shell.Register();
        Assert.False(shell.RefreshLabels());
        _labels = ShellLabels.English;
        Assert.True(shell.RefreshLabels());
        Assert.Equal("Open with HexEditor", _registry.GetValue(ShellRegistration.ContextMenuKey, null));
    }

    // ---- UI-56 ファイルの関連付け ----

    [Fact]
    [Trait(TC, "TC-UI-56-01")]
    public void Own_formats_become_the_default_app()
    {
        Shell(Distribution.Portable, PortableExe).Register();
        Assert.Equal("HexEditor.Project", _registry.GetValue(@"Software\Classes\.hexproj", null));
        Assert.Equal("HexEditor.Workspace", _registry.GetValue(@"Software\Classes\.hexworkspace", null));
        Assert.Equal($"\"{PortableExe}\" \"%1\"", _registry.GetValue(@"Software\Classes\HexEditor.Project\shell\open\command", null));
        Assert.Equal("HexEditor プロジェクト", _registry.GetValue(@"Software\Classes\HexEditor.Project", null));
    }

    [Fact]
    [Trait(TC, "TC-UI-56-02")]
    public void Binary_formats_are_only_added_to_open_with_and_the_default_app_is_kept()
    {
        _registry.Seed(@"Software\Classes\.iso", null, "Windows.IsoFile");
        Shell(Distribution.Installer, InstallerExe).Register();
        Assert.Equal("Windows.IsoFile", _registry.GetValue(@"Software\Classes\.iso", null));
        foreach (string ext in ShellRegistration.DefaultOpenWithExtensions)
        {
            Assert.Equal(string.Empty, _registry.GetValue($@"Software\Classes\{ext}\OpenWithProgids", "HexEditor.Binary"));
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-56-03")]
    public void Unregistering_the_portable_version_removes_every_key_and_value_it_added()
    {
        _registry.Seed(@"Software\Classes\.iso", null, "Windows.IsoFile");
        _registry.Seed(@"Software\Classes\.bin\OpenWithProgids", "Other.App", string.Empty);
        IReadOnlyDictionary<string, string> before = _registry.Snapshot();
        IReadOnlyCollection<string> keysBefore = [.. _registry.Keys];

        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        shell.Register();
        Assert.True(_registry.KeyExists(@"Software\Classes\HexEditor.Binary"));
        Assert.Empty(shell.Unregister());

        Assert.Equal(before.OrderBy(p => p.Key), _registry.Snapshot().OrderBy(p => p.Key));
        Assert.Equal(keysBefore.Order(), _registry.Keys.Order());
        foreach (string progId in new[] { "HexEditor.Project", "HexEditor.Workspace", "HexEditor.Binary" })
        {
            Assert.False(_registry.KeyExists($@"Software\Classes\{progId}"));
        }

        Assert.False(_registry.KeyExists(@"Software\Classes\.dat"));
        Assert.False(_registry.KeyExists(ShellRegistration.ContextMenuKey));
    }

    [Fact]
    public void Removing_an_extension_from_the_list_removes_its_value_on_the_next_registration()
    {
        var registry = _registry;
        var context = new ShellRegistrationContext(PortableExe, _labels, [".bin", ".iso"]);
        ShellRegistration.Register(registry, context, new HashSet<string>(), new HashSet<string> { ShellRegistration.FileAssociationsId });
        Assert.Equal(".bin;.iso", registry.GetValue(@"Software\Classes\HexEditor.Binary", ShellRegistration.RegisteredExtensionsValue));
        ShellRegistration.Register(registry, context with { OpenWithExtensions = [".bin"] }, new HashSet<string>(), new HashSet<string> { ShellRegistration.FileAssociationsId });
        Assert.False(registry.KeyExists(@"Software\Classes\.iso"));
        Assert.True(registry.KeyExists(@"Software\Classes\.bin\OpenWithProgids"));
    }

    [Theory]
    [InlineData("", ".bin;.dat;.img;.rom;.dmp;.raw;.iso")]
    [InlineData("bin; .ISO ,x86", ".bin;.iso;.x86")]
    [InlineData(".hexproj;.bad/name;.ok", ".ok")]
    public void Extension_lists_are_normalized(string text, string expected) =>
        Assert.Equal(expected, string.Join(';', ShellRegistration.ParseExtensions(text)));
}
