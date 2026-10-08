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
        new InstallHooks(_registry, temp.Path, InstallerExe, () => _notified++, labels: _labels, notifyEnvironmentChanged: () => { }).AfterInstall("1.0.0");
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.Equal("HexEditor で開く", _registry.GetValue(@"Software\Classes\*\shell\HexEditor", null));
        Assert.Equal($"\"{InstallerExe}\",0", _registry.GetValue(ShellRegistration.ContextMenuKey, "Icon"));
        Assert.Equal($"\"{InstallerExe}\" \"%1\"", _registry.GetValue(ShellRegistration.ContextMenuKey + @"\command", null));
        Assert.All(_registry.Keys.Where(k => k != UserPath.EnvironmentKey), k => Assert.StartsWith(@"Software\", k));
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

    // ---- ポータブル版の「この PC から登録を解除」(10 の PKG-09 の仕様 4) ----

    [Fact]
    [Trait(TC, "TC-PKG-09-03")]
    public void Unregister_from_this_pc_removes_the_registration_jump_list_toast_and_temp_folder()
    {
        using var temp = new TempFolder();
        string tempFolder = temp.Sub("HexEditor-12345678");
        Directory.CreateDirectory(Path.Combine(tempFolder, "doc"));
        File.WriteAllText(Path.Combine(tempFolder, "doc", "edit.tmp"), "x");
        IReadOnlyDictionary<string, string> before = _registry.Snapshot();
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        Assert.Empty(shell.Register());
        int jumpList = 0, toast = 0;

        Assert.Empty(shell.UnregisterFromThisPc(() => jumpList++, () => toast++, tempFolder));

        // レジストリは登録の前と同じ (HexEditor のキー・値が残らない)、ジャンプリストとトースト通知の登録を消し、一時フォルダもない。
        Assert.Equal(before.OrderBy(p => p.Key), _registry.Snapshot().OrderBy(p => p.Key));
        Assert.DoesNotContain(_registry.Keys, k => k.Contains("HexEditor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, jumpList);
        Assert.Equal(1, toast);
        Assert.False(Directory.Exists(tempFolder));
    }

    [Fact]
    [Trait(TC, "TC-PKG-09-03")]
    public void Unregister_from_this_pc_keeps_the_installer_registration_and_lists_failures()
    {
        // インストーラ版の登録 (別の配布形態) は消さない。
        Shell(Distribution.Installer, InstallerExe).Register();
        int jumpList = 0;
        IReadOnlyList<string> failures = Shell(Distribution.Portable, PortableExe).UnregisterFromThisPc(
            () => jumpList++, () => throw new System.Runtime.InteropServices.COMException("toast"), Path.Combine(Path.GetTempPath(), "HexEditor-none-" + Guid.NewGuid().ToString("N")));
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));

        // 失敗した項目は一覧で返し、残りの項目は続ける (PKG-09 の「エラー」)。
        Assert.Equal(["toast: COMException"], failures);
        Assert.Equal(1, jumpList);

        // ポータブル版以外では使えない。
        Assert.Equal(["unsupported"], Shell(Distribution.Installer, InstallerExe).UnregisterFromThisPc(() => { }, () => { }, Path.GetTempPath()));
    }

    // ---- 設定画面の項目ごとの登録・解除 (UI-54 の仕様 5、UI-56) ----

    [Fact]
    public void Items_report_each_entry_separately_and_the_buttons_act_on_one_entry()
    {
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        Assert.All(shell.Items(), i => Assert.False(i.Registered));

        // 右クリックメニューだけを登録する: 関連付けは登録しない。
        Assert.Empty(shell.Register(new HashSet<string> { ShellRegistration.ContextMenuId }));
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.False(_registry.KeyExists(@"Software\Classes\HexEditor.Project"));
        ShellItemState menu = shell.Items().Single(i => i.Id == ShellRegistration.ContextMenuId);
        Assert.True(menu.Ours);
        Assert.False(shell.Items().Single(i => i.Id == ShellRegistration.FileAssociationsId).Registered);

        // 関連付けを登録してから右クリックメニューだけを解除する: 関連付けは残る。
        Assert.Empty(shell.Register(new HashSet<string> { ShellRegistration.FileAssociationsId }));
        Assert.Empty(shell.Unregister(new HashSet<string> { ShellRegistration.ContextMenuId }));
        Assert.False(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.True(shell.Items().Single(i => i.Id == ShellRegistration.FileAssociationsId).Ours);
    }

    [Fact]
    public void Moved_portable_folder_updates_only_the_entries_that_were_registered()
    {
        Shell(Distribution.Portable, PortableExe).Register(new HashSet<string> { ShellRegistration.ContextMenuId });
        ShellIntegration moved = Shell(Distribution.Portable, MovedExe);

        // 古い場所を指すのは右クリックメニューだけ。「更新する」はその項目だけを登録し直す (関連付けを新しく登録しない)。
        IReadOnlySet<string> stale = moved.StaleItems();
        Assert.Equal([ShellRegistration.ContextMenuId], stale);
        Assert.Empty(moved.Register(stale));
        Assert.Equal($"\"{MovedExe}\" \"%1\"", _registry.GetValue(ShellRegistration.ContextMenuKey + @"\command", null));
        Assert.False(_registry.KeyExists(@"Software\Classes\HexEditor.Project"));
        Assert.Empty(moved.StaleItems());
    }

    [Fact]
    public void Portable_unregister_keeps_the_installer_registration()
    {
        Shell(Distribution.Installer, InstallerExe).Register();
        ShellIntegration portable = Shell(Distribution.Portable, PortableExe);
        ShellItemState menu = portable.Items().Single(i => i.Id == ShellRegistration.ContextMenuId);
        Assert.True(menu.OtherDistribution);
        Assert.False(menu.Stale);

        // ポータブル版の「登録を解除する」は、インストーラ版の exe を指す登録を消さない (「この PC から登録を解除」と同じ)。
        Assert.Empty(portable.Unregister());
        Assert.True(_registry.KeyExists(ShellRegistration.ContextMenuKey));
        Assert.Equal($"\"{InstallerExe}\" \"%1\"", _registry.GetValue(ShellRegistration.ContextMenuKey + @"\command", null));
        Assert.True(_registry.KeyExists(@"Software\Classes\HexEditor.Project"));
    }

    [Fact]
    public void Msix_shows_registrations_of_another_distribution_without_changing_them()
    {
        Assert.False(Shell(Distribution.Msix, PortableExe).State().OtherDistributionRegistered);
        Shell(Distribution.Installer, InstallerExe).Register();
        IReadOnlyDictionary<string, string> before = _registry.Snapshot();
        ShellIntegrationState msix = Shell(Distribution.Msix, PortableExe).State();
        Assert.False(msix.Supported);
        Assert.True(msix.OtherDistributionRegistered);
        Assert.Equal(InstallerExe, msix.RegisteredExe);
        Assert.Empty(Shell(Distribution.Msix, PortableExe).Items());
        Assert.Equal(before.OrderBy(p => p.Key), _registry.Snapshot().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(new[] { ".bin", ".dat", ".img", ".rom", ".dmp", ".raw", ".iso" }, "")]
    [InlineData(new[] { ".iso", ".rom", ".dat", ".bin", ".img", ".raw", ".dmp" }, "")]
    [InlineData(new[] { ".bin", ".hex" }, ".bin;.hex")]
    [InlineData(new string[0], ";")]
    public void Checked_extensions_become_the_setting_value(string[] chosen, string expected)
    {
        string value = ShellRegistration.FormatExtensions(chosen);
        Assert.Equal(expected, value);

        // 読み直すと同じ一覧 (何も選ばなければ候補なし。空は既定の 7 つ)。
        IReadOnlyList<string> parsed = ShellRegistration.ParseExtensions(value);
        Assert.Equal(chosen.Order(), parsed.Order());
    }

    [Fact]
    public void Registering_with_no_extensions_adds_no_open_with_entries()
    {
        var context = new ShellRegistrationContext(PortableExe, _labels, ShellRegistration.ParseExtensions(";"));
        ShellRegistration.Register(_registry, context, new HashSet<string>(), new HashSet<string> { ShellRegistration.FileAssociationsId });
        Assert.False(_registry.KeyExists(@"Software\Classes\.bin"));
        Assert.True(_registry.KeyExists(@"Software\Classes\.hexproj"));
    }

    // ---- HexEditor.exe --unregister (08 の AUTO-36 の 9、10 の PKG-09) ----

    [Fact]
    public void Unregister_command_removes_the_portable_registration_and_exits_with_0()
    {
        using var temp = new TempFolder();
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        shell.Register();
        int jumpList = 0, toast = 0;
        UnregisterResult result = Unregistration.Run(Distribution.Portable,
            () => shell.UnregisterFromThisPc(() => jumpList++, () => toast++, temp.Sub("HexEditor-1")), () => throw new InvalidOperationException());
        Assert.True(result.Supported);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(_registry.Keys, k => k.Contains("HexEditor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((1, 1), (jumpList, toast));
    }

    [Fact]
    public void Unregister_command_exits_with_3_when_something_could_not_be_removed()
    {
        ShellIntegration shell = Shell(Distribution.Portable, PortableExe);
        shell.Register();
        UnregisterResult result = Unregistration.Run(Distribution.Portable,
            () => shell.UnregisterFromThisPc(() => { }, () => throw new System.Runtime.InteropServices.COMException("toast"), Path.GetTempPath() + Guid.NewGuid().ToString("N")),
            () => []);
        Assert.Equal(["toast: COMException"], result.Failures);
        Assert.Equal(3, result.ExitCode);
    }

    [Theory]
    [InlineData(Distribution.Msix)]
    [InlineData(Distribution.Development)]
    public void Unregister_command_does_nothing_for_msix_and_development(Distribution distribution)
    {
        bool called = false;
        UnregisterResult result = Unregistration.Run(distribution, () => { called = true; return []; }, () => { called = true; return []; });
        Assert.False(result.Supported);
        Assert.False(called);
        Assert.Equal(0, result.ExitCode);
    }
}
