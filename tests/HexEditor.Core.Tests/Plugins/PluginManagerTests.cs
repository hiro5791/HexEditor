using System.Text;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Plugins;

/// <summary>プラグインマネージャの追加・有効 / 無効・削除・更新 (08 の AUTO-32)、開発用のフォルダ (AUTO-33)、パッケージの検証。</summary>
[Collection(SerialCollection.Name)]
public sealed class PluginManagerTests
{
    private static string SamplePackage(PluginTestHost t, TestPlugins.Definition? definition = null, string? topFolder = null)
    {
        string staging = Path.Combine(t.Root, "pkg-src-" + Guid.NewGuid().ToString("N"));
        TestPlugins.WriteFolder(definition ?? TestPlugins.Sample, staging);
        return TestPlugins.Package(staging, Path.Combine(t.Root, (definition ?? TestPlugins.Sample).Id + "-" + Guid.NewGuid().ToString("N")[..6] + ".hexplugin"), topFolder);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-32-01")]
    public async Task Adding_a_hexplugin_asks_for_trust_extracts_it_and_enables_it_after_restart()
    {
        using var t = new PluginTestHost();
        PluginManager m = await t.StartAsync();
        Assert.Empty(m.Plugins);
        string package = SamplePackage(t);

        // 1〜2. 追加: 信頼の確認で「有効にする」。展開は確認の後。
        PluginInstallResult result = await m.InstallAsync(package);
        Assert.True(result.Installed, result.Message);
        PluginTrustPrompt prompt = Assert.Single(t.Prompts.Trust);
        Assert.Equal("test.sample", prompt.Id);
        Assert.True(File.Exists(Path.Combine(t.PluginsRoot, "test.sample", "1.0.0", "Plugin.Sample.dll")));
        Assert.True(m.RestartRequired);
        Assert.False(Directory.Exists(Path.Combine(t.PluginsRoot, ".staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(t.PluginsRoot, ".staging")).Any());

        // 3〜4. 再起動後: 有効で、コマンド「sample: hello」がある (確認はもう出ない)。
        await t.StartAsync();
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
        Assert.Equal("sample: hello", t.Manager.Registry.Find("test.sample.hello")!.DisplayName);
        Assert.Single(t.Prompts.Trust);
    }

    [Fact]
    public async Task Declining_the_confirmation_does_not_extract_anything()
    {
        using var t = new PluginTestHost();
        PluginManager m = await t.StartAsync();
        t.Prompts.Answer = PluginTrustAnswer.DontEnable;
        PluginInstallResult result = await m.InstallAsync(SamplePackage(t));
        Assert.False(result.Installed);
        Assert.False(Directory.Exists(Path.Combine(t.PluginsRoot, "test.sample")));
    }

    [Fact]
    public async Task A_newer_version_is_an_update_and_the_old_version_is_removed_on_restart()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        PluginManager m = await t.StartAsync();
        PluginInstallResult result = await m.InstallAsync(SamplePackage(t, TestPlugins.Sample with { Version = "1.1.0" }, topFolder: "sample"));
        Assert.True(result.Installed, result.Message);
        Assert.True(result.Update);
        Assert.True(t.Prompts.Trust.Single().Update);
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("test.sample")!;
        Assert.Equal("1.1.0", p.Version);
        Assert.Equal(PluginState.Enabled, p.State);

        // 古いバージョンは次の起動で消す (このテストのプロセスでは DLL が使用中で消せないため、消せるまで読み込まないことを確かめる)。
        Assert.DoesNotContain(t.Manager.Plugins, x => x.Folder.EndsWith("1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-32-02")]
    public async Task A_disabled_plugin_is_not_loaded_after_restart()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        PluginManager m = await t.StartAsync();
        Assert.Equal(PluginState.Enabled, m.Find("test.sample")!.State);

        // 1〜2. 無効にする (再起動で反映)。
        m.SetEnabled("test.sample", false);
        Assert.True(m.RestartRequired);
        Assert.True(m.Find("test.sample")!.PendingRestart);

        // 3. 再起動後: 無効で、コマンドもアセンブリの読み込みもない。
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("test.sample")!;
        Assert.Equal(PluginState.Disabled, p.State);
        Assert.False(p.AssemblyLoaded);
        Assert.Null(t.Manager.Registry.Find("test.sample.hello"));

        // 有効に戻せる。
        t.Manager.SetEnabled("test.sample", true);
        await t.StartAsync();
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-32-03")]
    public async Task Unloadable_plugins_show_the_reason_and_broken_packages_are_not_added()
    {
        using var t = new PluginTestHost();
        t.Install(TestPlugins.NoEntry);
        PluginManager m = await t.StartAsync();

        // 1. 見つからない DLL の名前が理由に入る。
        PluginRecord p = m.Find("noentry")!;
        Assert.Equal(PluginState.Failed, p.State);
        Assert.Contains("Missing.Entry.dll", p.Reason);

        // 2〜3. 途中で切れた zip: 追加できず、理由は zip が壊れていること。plugins に何も増えない。
        string[] before = [.. Directory.EnumerateFileSystemEntries(t.PluginsRoot, "*", SearchOption.AllDirectories)];
        string broken = TestPlugins.Truncated(SamplePackage(t), Path.Combine(t.Root, "broken.hexplugin"));
        PluginInstallResult result = await m.InstallAsync(broken);
        Assert.False(result.Installed);
        Assert.Contains("zip", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(t.Prompts.Trust);
        string[] after = [.. Directory.EnumerateFileSystemEntries(t.PluginsRoot, "*", SearchOption.AllDirectories)
            .Where(e => !e.Contains(".staging", StringComparison.Ordinal))];
        Assert.Equal(before.Order(), after.Order());

        // マニフェストのないパッケージも理由付きで拒否する。
        string noManifest = TestPlugins.RawPackage(Path.Combine(t.Root, "nomanifest.hexplugin"), new Dictionary<string, byte[]> { ["a.dll"] = [1, 2, 3] });
        PluginInstallResult r2 = await m.InstallAsync(noManifest);
        Assert.False(r2.Installed);
        Assert.Contains("plugin.json", r2.Message);
    }

    [Fact]
    public async Task Removing_a_loaded_plugin_deletes_it_after_restart()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        PluginManager m = await t.StartAsync();
        Assert.True(m.Remove("test.sample"));
        Assert.True(m.RestartRequired);
        await t.StartAsync();

        // 次の起動で消す (このテストのプロセスでは DLL が使用中で消せない。消せるまで一覧に出さず、読み込まない)。
        Assert.Null(t.Manager!.Find("test.sample"));
        Assert.DoesNotContain(t.Manager.Options.Trust.Records, r => r.Kind == TrustStore.PluginKind && r.Key == "test.sample");
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("/abs.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("\\\\server\\share\\x.dll")]
    [InlineData("a.dll:stream")]
    [InlineData("nul.txt")]
    [InlineData("sub/CON")]
    [InlineData("trailing.")]
    public async Task Packages_with_unsafe_paths_are_refused_and_nothing_is_written_outside(string entry)
    {
        using var t = new PluginTestHost();
        PluginManager m = await t.StartAsync();
        string manifest = TestPlugins.Manifest(TestPlugins.Sample).ToJsonString();
        string package = TestPlugins.RawPackage(Path.Combine(t.Root, "evil.hexplugin"), new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Encoding.UTF8.GetBytes(manifest),
            [entry] = [0x4D, 0x5A],
        });
        PluginInstallResult result = await m.InstallAsync(package);
        Assert.False(result.Installed);
        Assert.Empty(t.Prompts.Trust);
        Assert.False(File.Exists(Path.Combine(t.Root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(t.PluginsRoot, "evil.dll")));
        Assert.False(Directory.Exists(Path.Combine(t.PluginsRoot, "test.sample")));
    }

    [Fact]
    public void Safe_parts_accepts_normal_relative_paths()
    {
        Assert.Equal(["Strings", "ja", "Resources.resw"], PluginPackage.SafeParts("Strings/ja/Resources.resw"));
        Assert.Equal(["sub", "a.dll"], PluginPackage.SafeParts("sub\\a.dll"));
        Assert.Equal(["folder"], PluginPackage.SafeParts("folder/"));
        Assert.Null(PluginPackage.SafeParts(""));
        Assert.Null(PluginPackage.SafeParts("a//b"));
    }

    [Fact]
    public async Task A_zip_bomb_is_refused()
    {
        using var t = new PluginTestHost();
        PluginManager m = await t.StartAsync();
        string package = TestPlugins.RawPackage(Path.Combine(t.Root, "bomb.hexplugin"), new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Encoding.UTF8.GetBytes(TestPlugins.Manifest(TestPlugins.Sample).ToJsonString()),
            ["zeros.bin"] = new byte[64 * 1024 * 1024],
        });
        PluginInstallResult result = await m.InstallAsync(package);
        Assert.False(result.Installed);
        Assert.Empty(t.Prompts.Trust);
    }

    [Theory]
    [InlineData("""{"name":"x","version":"1.0.0","sdk":"1.0","entry":"a.dll"}""", "id")]
    [InlineData("""{"id":"Bad_ID","name":"x","version":"1.0.0","sdk":"1.0","entry":"a.dll"}""", "Bad_ID")]
    [InlineData("""{"id":"ok","name":"x","version":"one","sdk":"1.0","entry":"a.dll"}""", "one")]
    [InlineData("""{"id":"ok","name":"x","version":"1.0.0","sdk":"1","entry":"a.dll"}""", "sdk")]
    [InlineData("""{"id":"ok","name":"x","version":"1.0.0","sdk":"1.0","entry":"../a.dll"}""", "entry")]
    [InlineData("""{"id":"ok","name":"x","version":"1.0.0","sdk":"1.0","entry":"a.exe"}""", "entry")]
    [InlineData("""{"id":"ok","name":"x","version":"1.0.0","sdk":"1.0","entry":"a.dll","permissions":"all"}""", "permissions")]
    [InlineData("""[1,2]""", "object")]
    [InlineData("""{""", "JSON")]
    public void Invalid_manifests_are_rejected_with_the_reason(string json, string expected)
    {
        Msg.Default = "en";
        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(json));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifests_carry_localized_names_and_versions_compare_semantically()
    {
        PluginManifest m = PluginManifest.Parse("""
            {"id":"example.crc-tools","name":"CRC Tools","name.ja":"CRC ツール","version":"1.2.0","publisher":"Example","sdk":"1.3",
             "minAppVersion":"1.4.0","entry":"Example.CrcTools.dll","permissions":["file-read"],"description":"Additional CRC algorithms."}
            """);
        Assert.Equal("CRC ツール", m.NameFor("ja-JP"));
        Assert.Equal("CRC Tools", m.NameFor("de"));
        Assert.Equal(new Version(1, 3), m.Sdk);
        Assert.True(PluginVersion.Parse("1.10.0").CompareTo(PluginVersion.Parse("1.9.9")) > 0);
        Assert.True(PluginVersion.Parse("1.0.0-beta").CompareTo(PluginVersion.Parse("1.0.0")) < 0);
        Assert.NotNull(PluginCompatibility.AppVersionProblem(m.MinAppVersion, "1.3.9"));
        Assert.Null(PluginCompatibility.AppVersionProblem(m.MinAppVersion, "1.4.0"));
        Assert.Null(PluginCompatibility.AppVersionProblem(m.MinAppVersion, "0.0.0-local"));
        Assert.True(PluginCompatibility.IsSdkCompatible(new Version(1, 2), new Version(1, 3)));
        Assert.False(PluginCompatibility.IsSdkCompatible(new Version(1, 4), new Version(1, 3)));
        Assert.False(PluginCompatibility.IsSdkCompatible(new Version(2, 0), new Version(1, 3)));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-33-01")]
    public async Task A_development_folder_loads_directly_is_shown_as_development_and_asks_once()
    {
        // 手順 2〜4 (デバッガの下で起動してブレークポイントで止める) は自動化しない: ICorDebug を使うテスト用のデバッガがなく、
        // 用意するにはパッケージのダウンロードが要るため (起動設定の中身は TemplatePackageTests で確かめる)。ここでは手順 5 と信頼の確認を確かめる。
        using var t = new PluginTestHost();
        string dev = TestPlugins.WriteFolder(TestPlugins.Sample, Path.Combine(t.Root, "build-output"));
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, DevFolders = [dev],
        });
        PluginRecord p = t.Manager!.Find("test.sample")!;
        Assert.True(p.IsDevelopment);
        Assert.Equal(PluginState.Enabled, p.State);
        Assert.True(Assert.Single(t.Prompts.Trust).Development);

        // ビルドし直して内容が変わっても、同じフォルダなら確認は出ない (指定したときに 1 回だけ)。
        File.AppendAllText(Path.Combine(dev, "test.json"), "{}");
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, DevFolders = [dev],
        });
        Assert.Single(t.Prompts.Trust);
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
    }

    [Fact]
    public async Task A_crash_with_a_plugin_frame_is_remembered_for_the_next_start()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.DepA), "dep-a");
        PluginManager m = await t.StartAsync();
        Assert.Null(m.RecordCrash("   at HexEditor.App.Something()\n"));
        Assert.Same(m.Find("dep-a"), m.RecordCrash("   at Plugin.Dep.DepPlugin.VersionCommand.Execute(ICommandContext context) in Plugin.DepA.dll\n   at Plugin.DepA.Something()"));
        PluginManager next = await t.StartAsync();
        Assert.Equal("dep-a", next.TakeCrashSuspect());
        Assert.Null(next.TakeCrashSuspect());
    }

    [Fact]
    public async Task Plugin_strings_come_from_the_plugin_resw_in_the_ui_language()
    {
        using var t = new PluginTestHost();
        string folder = t.Install(TestPlugins.DepA);
        Directory.CreateDirectory(Path.Combine(folder, "Strings", "ja"));
        Directory.CreateDirectory(Path.Combine(folder, "Strings", "en"));
        File.WriteAllText(Path.Combine(folder, "Strings", "ja", "Resources.resw"), """<?xml version="1.0" encoding="utf-8"?><root><data name="Hello"><value>こんにちは</value></data></root>""");
        File.WriteAllText(Path.Combine(folder, "Strings", "en", "Resources.resw"), """<?xml version="1.0" encoding="utf-8"?><root><data name="Hello"><value>Hello</value></data><data name="Only"><value>English only</value></data></root>""");
        var ja = new PluginStrings(folder, "ja-JP");
        Assert.Equal("こんにちは", ja.Get("Hello"));
        Assert.Equal("English only", ja.Get("Only"));
        Assert.Equal("Missing", ja.Get("Missing"));

        // DTD を含むもの (外部実体) は読まない。
        File.WriteAllText(Path.Combine(folder, "Strings", "ja", "Resources.resw"), """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><root><data name="Hello"><value>&x;</value></data></root>""");
        Assert.Equal("Hello", new PluginStrings(folder, "ja").Get("Hello"));
        await Task.CompletedTask;
    }
}
