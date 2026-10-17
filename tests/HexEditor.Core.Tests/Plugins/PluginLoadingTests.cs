using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Core.Tests.Scripting;
using HexEditor.Core.Tests.Support;
using HexEditor.Sdk.Plugins;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Plugins;

/// <summary>
/// プラグインの SDK・読み込みと分離・信頼の確認・署名 (08 の AUTO-29、AUTO-30、AUTO-34 の仕様 8、AUTO-35)。
/// テスト用のプラグイン (tests/plugins) を一時的な設定フォルダに入れて、アプリと同じ <see cref="PluginManager"/> で読み込む。
/// <see cref="PluginManager.Current"/> とハッシュの一覧は静的なため、並列にしない。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class PluginLoadingTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-29-02")]
    public async Task Plugins_built_with_an_older_minor_sdk_load_and_newer_ones_are_refused_with_both_versions()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sdk12), "sdk12");
        t.Trust(t.Install(TestPlugins.Sdk13), "sdk13");

        // 1〜2. SDK 1.1 のアプリ (今の SDK 1.0 より 1 つ新しいマイナーバージョンを宣言する): SDK 1.0 でビルドしたプラグインが動く。
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output,
            AppSdkVersion = new Version(1, 1),
        });
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("sdk12")!.State);
        Assert.True(t.Execute("sdk12.ping"));
        Assert.Contains("pong from sdk12", t.OutputOf("sdk12"));

        // 3〜4. 今の SDK (1.0) のアプリ: SDK 1.1 でビルドしたプラグインは読み込めない。理由に両方のバージョン。
        await t.StartAsync();
        PluginRecord sdk13 = t.Manager!.Find("sdk13")!;
        Assert.Equal(PluginState.Failed, sdk13.State);
        Assert.Contains("1.1", sdk13.Reason);
        Assert.Contains("1.0", sdk13.Reason);
        Assert.False(sdk13.AssemblyLoaded);
        Assert.Null(t.Manager.Registry.Find("sdk13.ping"));
        Assert.Equal(PluginState.Enabled, t.Manager.Find("sdk12")!.State);
    }

    [Fact]
    public async Task A_manifest_that_hides_a_newer_referenced_sdk_is_refused()
    {
        using var t = new PluginTestHost();
        // マニフェストは sdk 1.0 と宣言するが、アセンブリは SDK 1.1 を参照している。
        string folder = t.Install(TestPlugins.Sdk13 with { Sdk = "1.0" });
        t.Trust(folder, "sdk13");
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("sdk13")!;
        Assert.Equal(PluginState.Failed, p.State);
        Assert.Contains("1.1", p.Reason);
        Assert.False(p.AssemblyLoaded);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-30-01")]
    public async Task Two_plugins_with_different_versions_of_the_same_library_run_together()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.DepA), "dep-a");
        t.Trust(t.Install(TestPlugins.DepB), "dep-b");
        await t.StartAsync();
        Assert.True(t.Execute("dep-a.version"));
        Assert.True(t.Execute("dep-b.version"));
        Assert.True(t.Execute("dep-a.version"));
        Assert.Equal(["TestLib 1.0.0", "TestLib 1.0.0"], t.OutputOf("dep-a").Where(l => l.StartsWith("TestLib", StringComparison.Ordinal)));
        Assert.Equal(["TestLib 2.0.0"], t.OutputOf("dep-b").Where(l => l.StartsWith("TestLib", StringComparison.Ordinal)));
        Assert.All(t.Manager!.Plugins, p => Assert.Equal(PluginState.Enabled, p.State));
    }

    [PerfMachineFact]
    [Trait("TC", "TC-AUTO-30-02")]
    [Trait("Category", "Performance")]
    [Trait("Category", "Nightly")]
    public async Task Ten_plugins_do_not_delay_the_startup()
    {
        // 起動の経路で行うのは、一覧を作るところまで (読み込みは最初のウィンドウを表示した後にバックグラウンドで行う。AUTO-30 の仕様 4)。
        // アプリの起動時間 (00 の 11.1) は起動の性能テスト (TC-UI-xx) が測る。ここでは、プラグインの分が起動の経路を 2 秒以上延ばさないことと、
        // 10 個すべてが読み込まれることを 10 回確かめる。
        using var t = new PluginTestHost();
        for (int i = 0; i < 10; i++)
        {
            t.Trust(t.Install(TestPlugins.Init200(i)), $"init{i:00}");
        }

        var startup = new List<TimeSpan>();
        for (int round = 0; round < 10; round++)
        {
            var manager = new PluginManager(new PluginHostOptions
            {
                PluginsRoot = t.PluginsRoot, AppVersion = "1.0.0", Trust = new TrustStore(t.TrustFile), Verifier = new PluginTestHost.TestRootVerifier(t.Certificates.Root),
                Prompts = t.Prompts, Output = t.Output,
            });
            var watch = Stopwatch.StartNew();
            manager.Discover();
            Task loading = manager.LoadAllAsync();
            startup.Add(watch.Elapsed);
            await loading;
            Assert.Equal(10, manager.Plugins.Count(p => p.State == PluginState.Enabled));
            Assert.Equal(10, manager.Registry.All.Count(e => e.Kind == PluginExtensionKinds.Command));
        }

        TimeSpan median = startup.Order().ElementAt(startup.Count / 2);
        Assert.True(median < TimeSpan.FromSeconds(2), $"median {median.TotalMilliseconds:0} ms");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-30-03")]
    public async Task A_plugin_that_throws_is_logged_and_suspended_and_the_app_keeps_running()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Throw), "throw");
        await t.StartAsync();
        PluginRecord plugin = t.Manager!.Find("throw")!;
        var suspended = new List<PluginRecord>();
        t.Manager.Suspended += (_, p) => suspended.Add(p);

        // 1. コマンドの例外は記録され、呼び出し元には失敗として返る。
        Assert.False(t.Execute("throw.run"));

        // 2. インスペクタの型が毎回例外を投げる (→ を 12 回)。
        PluginIntegration.InspectorTypeEntry type = PluginIntegration.InspectorTypes(t.Manager).Single(x => x.Id == "throw.type");
        for (int i = 0; i < 12; i++)
        {
            Assert.Null(type.Decode([(byte)i], bigEndian: false, uiThread: true));
        }

        // 3. 例外の記録が 10 行以上。4. 10 回で止めた (App はここで InfoBar を出す)。
        Assert.True(t.Output.LinesOf("plugin:throw").Count(l => l.Kind == ScriptOutputKind.Error) >= 10);
        Assert.Equal(PluginState.Suspended, plugin.State);
        Assert.Same(plugin, Assert.Single(suspended));
        Assert.Equal(PluginGuard.MaxFaultsPerMinute, plugin.Guard.FaultCount);

        // 止めた後は呼び出さない (例外の数が増えない)。
        Assert.Null(type.Decode([1], bigEndian: false, uiThread: true));
        Assert.Equal(PluginGuard.MaxFaultsPerMinute, plugin.Guard.FaultCount);
    }

    [Fact]
    public async Task Faults_older_than_a_minute_do_not_count()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Throw), "throw");
        var clock = new ManualTimeProvider();
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, Time = clock,
        });
        PluginIntegration.InspectorTypeEntry type = PluginIntegration.InspectorTypes(t.Manager).Single(x => x.Id == "throw.type");
        for (int i = 0; i < 20; i++)
        {
            type.Decode([1], bigEndian: false, uiThread: false);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(PluginState.Enabled, t.Manager!.Find("throw")!.State);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-30-04")]
    public async Task Safe_mode_loads_no_plugin_and_keeps_the_list_and_settings()
    {
        using var t = new PluginTestHost();
        string folder = t.Install(TestPlugins.Sample);
        t.Trust(folder, "test.sample");
        string trustBefore = File.ReadAllText(t.TrustFile);

        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, SafeMode = true,
        });
        PluginRecord p = t.Manager!.Find("test.sample")!;
        Assert.Equal(PluginState.NotLoaded, p.State);
        Assert.False(p.AssemblyLoaded);
        Assert.Contains("safe mode", p.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(t.Manager.Registry.All);
        Assert.Empty(t.Prompts.Trust);
        Assert.Equal(trustBefore, File.ReadAllText(t.TrustFile));

        // 4. オプションなしの起動で元に戻る。
        await t.StartAsync();
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
        Assert.NotNull(t.Manager.Registry.Find("test.sample.hello"));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-30-05")]
    public async Task A_plugin_with_a_wrong_sdk_is_shown_as_not_loadable_with_the_reason_and_without_a_confirmation()
    {
        using var t = new PluginTestHost();
        t.Install(TestPlugins.BadSdk);
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("badsdk")!;
        Assert.Equal(PluginState.Failed, p.State);
        Assert.Contains("99.0", p.Reason);
        Assert.Empty(t.Prompts.Trust);
        Assert.False(p.AssemblyLoaded);
    }

    [Fact]
    public async Task First_load_asks_for_trust_and_remembers_the_answer_until_files_change()
    {
        using var t = new PluginTestHost();
        string folder = t.Install(TestPlugins.Sample);
        await t.StartAsync();
        PluginTrustPrompt prompt = Assert.Single(t.Prompts.Trust);
        Assert.Equal("test.sample", prompt.Id);
        Assert.Equal("Sample Plugin", prompt.Name);
        Assert.Equal(PluginSignatureState.Unsigned, prompt.Signature.State);
        Assert.Equal(["file-read"], prompt.Permissions);
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);

        // 2 回目は確認しない。
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);

        // ファイルが変わったら再確認。「有効にしない」は無効として記録する。
        File.AppendAllText(Path.Combine(folder, "test.json"), " ");
        t.Prompts.Answer = PluginTrustAnswer.DontEnable;
        await t.StartAsync();
        Assert.Equal(2, t.Prompts.Trust.Count);
        PluginRecord p = t.Manager!.Find("test.sample")!;
        Assert.Equal(PluginState.Disabled, p.State);
        Assert.False(p.AssemblyLoaded);
        Assert.False(t.Manager.IsEnabledInState("test.sample"));
    }

    [Fact]
    public async Task Headless_runs_load_only_trusted_plugins_and_never_ask()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.DepA), "dep-a");
        t.Install(TestPlugins.DepB);
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Output = o.Output, Prompts = null,
        });
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("dep-a")!.State);
        Assert.Equal(PluginState.NotLoaded, t.Manager.Find("dep-b")!.State);
        Assert.False(t.Manager.Find("dep-b")!.AssemblyLoaded);
    }

    [Fact]
    public async Task Organization_policy_disabled_loads_no_plugin()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.DepA), "dep-a");
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output,
            Policy = () => ScriptSettingKeys.PolicyDisabled,
        });
        PluginRecord p = t.Manager!.Find("dep-a")!;
        Assert.Equal(PluginState.NotLoaded, p.State);
        Assert.Contains("scripts.policy", p.Reason);
        Assert.False(p.AssemblyLoaded);
    }

    [Fact]
    public async Task Initialize_failures_and_missing_entry_types_are_reported()
    {
        using var t = new PluginTestHost();
        TestPlugins.Definition failing = TestPlugins.Throw with { Id = "initfail", TestJson = new JsonObject { ["initThrow"] = true } };
        t.Trust(t.Install(failing), "initfail");
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("initfail")!;
        Assert.Equal(PluginState.Failed, p.State);
        Assert.Contains("init failed on purpose", p.Reason);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-08")]
    public async Task Running_as_administrator_asks_once_for_all_auto_loaded_plugins_and_dont_load_loads_none()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        t.Trust(t.Install(TestPlugins.DepA), "dep-a");
        t.Prompts.ElevatedAnswer = false;
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, IsElevated = true,
        });

        // 2. 確認は 1 つだけで、2 つのプラグインの名前が一覧されている (信頼済みでも省略しない)。
        IReadOnlyList<PluginTrustPrompt> list = Assert.Single(t.Prompts.Elevated);
        Assert.Equal(["dep-a", "Sample Plugin"], list.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(list, p => Assert.True(p.Elevated && p.Trusted));
        Assert.Empty(t.Prompts.Trust);

        // 3〜4. 「読み込まない」: どちらも読み込まれていない。
        Assert.All(t.Manager!.Plugins, p =>
        {
            Assert.NotEqual(PluginState.Enabled, p.State);
            Assert.False(p.AssemblyLoaded);
        });

        // 「読み込む」なら両方とも読み込む (確認はやはり 1 回)。
        t.Prompts.ElevatedAnswer = true;
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, IsElevated = true,
        });
        Assert.Equal(2, t.Prompts.Elevated.Count);
        Assert.All(t.Manager!.Plugins, p => Assert.Equal(PluginState.Enabled, p.State));

        // ヘッドレス実行は --trust がある場合だけ読み込む。
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Output = o.Output, IsElevated = true, Prompts = null,
        });
        Assert.All(t.Manager!.Plugins, p => Assert.NotEqual(PluginState.Enabled, p.State));

        // --trust があっても、信頼の記録のないプラグインは読み込まない (--trust は管理者の確認の代わりで、信頼の代わりではない)。
        t.Install(TestPlugins.Sample with { Id = "untrusted", Name = "untrusted" });
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Output = o.Output, IsElevated = true, Prompts = null,
            CommandLineTrust = true,
        });
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
        PluginRecord untrusted = t.Manager!.Find("untrusted")!;
        Assert.NotEqual(PluginState.Enabled, untrusted.State);
        Assert.False(untrusted.AssemblyLoaded);
    }

    // ======================== 署名 (AUTO-35) ========================

    private static string InstallSigned(PluginTestHost t, string id, bool tamper = false)
    {
        string folder = t.Install(TestPlugins.Sample with { Id = id, Name = id });
        TestAuthenticode.Sign(Path.Combine(folder, "Plugin.Sample.dll"), t.Certificates);
        if (tamper)
        {
            TestAuthenticode.Tamper(Path.Combine(folder, "Plugin.Sample.dll"));
        }

        return folder;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-01")]
    public async Task A_signed_plugin_shows_the_publisher_in_the_confirmation_and_the_manager()
    {
        using var t = new PluginTestHost();
        InstallSigned(t, "test.signed");
        await t.StartAsync();
        PluginTrustPrompt prompt = Assert.Single(t.Prompts.Trust);
        Assert.Equal(PluginSignatureState.Valid, prompt.Signature.State);
        Assert.Equal(TestAuthenticode.PublisherName, prompt.Signature.Publisher);
        PluginRecord p = t.Manager!.Find("test.signed")!;
        Assert.Equal(PluginState.Enabled, p.State);
        Assert.Equal(PluginSignatureState.Valid, p.Signature.State);
        Assert.Equal(TestAuthenticode.PublisherName, p.Signature.Publisher);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-02")]
    public async Task A_plugin_with_one_byte_changed_in_a_signed_dll_is_invalid_and_not_loaded()
    {
        using var t = new PluginTestHost();
        string folder = InstallSigned(t, "test.tampered", tamper: true);
        t.Trust(folder, "test.tampered");
        await t.StartAsync();
        PluginRecord p = t.Manager!.Find("test.tampered")!;
        Assert.Equal(PluginSignatureState.Invalid, p.Signature.State);
        Assert.Equal(PluginState.Failed, p.State);
        Assert.False(p.AssemblyLoaded);
        Assert.Empty(t.Prompts.Trust);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.Location.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Signatures_from_an_untrusted_root_or_an_expired_certificate_are_invalid()
    {
        using var t = new PluginTestHost();
        using TestAuthenticode.Certificates other = TestAuthenticode.CreateCertificates("Someone Else");
        string folder = t.Install(TestPlugins.Sample with { Id = "test.other" });
        TestAuthenticode.Sign(Path.Combine(folder, "Plugin.Sample.dll"), other);
        using TestAuthenticode.Certificates expired = TestAuthenticode.CreateCertificates(expired: true);
        string folder2 = t.Install(TestPlugins.Sample with { Id = "test.expired" });
        TestAuthenticode.Sign(Path.Combine(folder2, "Plugin.Sample.dll"), expired);

        // 期限切れの証明書は、それを発行したルートを信頼しても無効 (タイムスタンプなし)。
        var verifier = new PluginTestHost.TestRootVerifier(expired.Root);
        Assert.Equal(PluginSignatureState.Invalid, verifier.Verify(Path.Combine(folder2, "Plugin.Sample.dll")).State);

        await t.StartAsync();
        Assert.Equal(PluginState.Failed, t.Manager!.Find("test.other")!.State);
        Assert.Equal(PluginState.Failed, t.Manager.Find("test.expired")!.State);
        Assert.Empty(t.Prompts.Trust);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-03")]
    public async Task Signed_only_refuses_unsigned_plugins_with_the_reason()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        t.Trust(InstallSigned(t, "test.signed"), "test.signed");
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output, SignedOnly = () => true,
        });
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.signed")!.State);
        PluginRecord unsigned = t.Manager.Find("test.sample")!;
        Assert.Equal(PluginState.Failed, unsigned.State);
        Assert.Contains("signed", unsigned.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(unsigned.AssemblyLoaded);

        // 組織の設定「署名されたプラグインだけ」も同じ。
        await t.StartAsync(o => new PluginHostOptions
        {
            PluginsRoot = o.PluginsRoot, AppVersion = o.AppVersion, Trust = o.Trust, Verifier = o.Verifier, Prompts = o.Prompts, Output = o.Output,
            Policy = () => ScriptSettingKeys.PolicySignedPluginsOnly,
        });
        Assert.Equal(PluginState.Failed, t.Manager!.Find("test.sample")!.State);
    }

    [Fact]
    public async Task Trusting_a_publisher_skips_the_confirmation_for_its_other_plugins()
    {
        using var t = new PluginTestHost();
        InstallSigned(t, "test.signed");
        t.Prompts.Answer = PluginTrustAnswer.EnableAndTrustPublisher;
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);
        InstallSigned(t, "test.signed2");
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.signed2")!.State);

        // 取り消すと、発行者の信頼だけで読み込んでいたもの (test.signed2) と新しいもの (test.signed3) で確認が出る。
        TrustRecord publisher = t.Manager.Options.Trust.Records.Single(r => r.Kind == PluginManager.TrustKindPublisher);
        t.Manager.Options.Trust.Revoke(publisher.Kind, publisher.Key);
        InstallSigned(t, "test.signed3");
        t.Prompts.Answer = PluginTrustAnswer.Enable;
        await t.StartAsync();
        Assert.Equal(["test.signed2", "test.signed3"], t.Prompts.Trust.Skip(1).Select(p => p.Id));
    }

    [Fact]
    public void Folder_signatures_combine_to_the_weakest_state()
    {
        using var t = new PluginTestHost();
        string folder = t.Install(TestPlugins.DepA);
        var verifier = new PluginTestHost.TestRootVerifier(t.Certificates.Root);
        Assert.Equal(PluginSignatureState.Unsigned, PluginSignatures.VerifyFolder(folder, "Plugin.DepA.dll", verifier).State);
        TestAuthenticode.Sign(Path.Combine(folder, "Plugin.DepA.dll"), t.Certificates);

        // 入口だけ署名: TestLib.dll が署名なしなので全体は署名なし。
        Assert.Equal(PluginSignatureState.Unsigned, PluginSignatures.VerifyFolder(folder, "Plugin.DepA.dll", verifier).State);
        TestAuthenticode.Sign(Path.Combine(folder, "TestLib.dll"), t.Certificates);
        PluginSignatureInfo all = PluginSignatures.VerifyFolder(folder, "Plugin.DepA.dll", verifier);
        Assert.Equal(PluginSignatureState.Valid, all.State);
        Assert.NotNull(all.Thumbprint);
        TestAuthenticode.Tamper(Path.Combine(folder, "TestLib.dll"));
        PluginSignatureInfo tampered = PluginSignatures.VerifyFolder(folder, "Plugin.DepA.dll", verifier);
        Assert.Equal(PluginSignatureState.Invalid, tampered.State);
        Assert.Contains("TestLib.dll", tampered.Problem);

        // 確認の途中の例外は無効 (有効にしない)。
        Assert.Equal(PluginSignatureState.Invalid, PluginSignatures.VerifyFolder(folder, "Plugin.DepA.dll", new ThrowingVerifier()).State);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-02")]
    public void Every_executable_file_is_verified_not_only_dlls()
    {
        using var t = new PluginTestHost();
        string folder = InstallSigned(t, "test.signed");
        var verifier = new PluginTestHost.TestRootVerifier(t.Certificates.Root);
        Assert.Equal(PluginSignatureState.Valid, PluginSignatures.VerifyFolder(folder, "Plugin.Sample.dll", verifier).State);

        // 署名のない .exe・.winmd を加えると、全体は署名なし (発行者の信頼で読み込まない)。
        foreach (string name in new[] { "helper.exe", "Types.winmd", Path.Combine("native", "lib.bin") })
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03]);
            PluginSignatureInfo info = PluginSignatures.VerifyFolder(folder, "Plugin.Sample.dll", verifier);
            Assert.NotEqual(PluginSignatureState.Valid, info.State);
            Assert.Contains(name.Replace('\\', '/'), info.Files.Keys);
            File.Delete(path);
        }

        // 拡張子を変えた PE (先頭が MZ) も確かめる。PE でないデータは署名の対象にしない (内容のハッシュで守る)。
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "plain text: not a PE file (no MZ at the start)");
        Assert.Equal(PluginSignatureState.Valid, PluginSignatures.VerifyFolder(folder, "Plugin.Sample.dll", verifier).State);
        File.WriteAllBytes(Path.Combine(folder, "data.bin"), [(byte)'M', (byte)'Z', 0x90, 0x00]);
        Assert.NotEqual(PluginSignatureState.Valid, PluginSignatures.VerifyFolder(folder, "Plugin.Sample.dll", verifier).State);
    }

    [Fact]
    public async Task Publisher_trust_reconfirms_when_an_unsignable_file_is_swapped_after_loading()
    {
        using var t = new PluginTestHost();
        InstallSigned(t, "test.signed");
        t.Prompts.Answer = PluginTrustAnswer.EnableAndTrustPublisher;
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);

        // 同じ発行者の別のプラグインは、発行者の信頼で確認なしに読み込む (そのときの内容を記録する)。
        string second = InstallSigned(t, "test.signed2");
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.signed2")!.State);
        await t.StartAsync();
        Assert.Single(t.Prompts.Trust);

        // 署名で守られないファイル (マニフェストの横の設定・スクリプト) を差し替えると、どちらも再確認になる。
        File.WriteAllText(Path.Combine(second, "extra.js"), "// swapped");
        File.AppendAllText(Path.Combine(t.PluginsRoot, "test.signed", "1.0.0", "plugin.json"), " ");
        t.Prompts.Answer = PluginTrustAnswer.DontEnable;
        await t.StartAsync();
        Assert.Equal(["test.signed", "test.signed2"], t.Prompts.Trust.Skip(1).Select(p => p.Id).Order());
        Assert.False(t.Manager!.Find("test.signed2")!.AssemblyLoaded);
    }

    private sealed class ThrowingVerifier : IPluginSignatureVerifier
    {
        public FileSignature Verify(string path) => throw new InvalidOperationException("verifier broke");
    }
}
