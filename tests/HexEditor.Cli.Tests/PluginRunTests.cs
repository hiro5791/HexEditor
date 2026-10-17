using HexEditor.Cli.Services;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>ヘッドレス実行 (<c>hexed run</c>) でプラグインを読み込む (08 の AUTO-30、TC-AUTO-12-02)。</summary>
[Collection("Plugins")]
public sealed class PluginRunTests
{
    /// <summary>TD-AUTO-JS-PLGTYPE: inspector.types にプラグインの型があるかと、オフセット 0x12 の decode の結果を出す。</summary>
    private const string PlgType = """
        // @name plgtype
        const found = inspector.types.some(t => t.id === "test.sample.nibble-swap8");
        console.log("found=" + found);
        console.log(inspector.decode(doc, 0x12, "test.sample.nibble-swap8").text);
        """;

    [Fact]
    [Trait("TC", "TC-AUTO-12-02")]
    public void A_headless_script_decodes_with_a_plugin_inspector_type()
    {
        using var dir = new TempDir();
        string settings = dir.File("settings");
        string folder = TestPlugins.Install(Path.Combine(settings, "plugins"), TestPlugins.Sample);

        // データフォルダに入れ、信頼済みとして記録している (アプリで「有効にする」を選んだ記録)。
        new TrustStore(Path.Combine(settings, "scripts", "trust.json")).Add(TrustStore.PluginKind, "test.sample", PluginManager.ContentHash(folder), TrustSource.UserAllowed, []);
        File.WriteAllBytes(dir.File("bytes.bin"), [.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
        File.WriteAllText(dir.File("plgtype.js"), PlgType);
        var services = new CliServices { Scripts = new CoreScriptRunner(() => new ScriptFolders(settings, dir.File("data")), () => null, () => false) };

        CliResult result = Hexed.RunInProcess(dir.Path, services, ["run", "plgtype.js", "bytes.bin", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["found=true", "21"], result.OutLines);
        Assert.Null(PluginManager.Current);
    }

    [Fact]
    public void Untrusted_plugins_are_not_loaded_headless()
    {
        using var dir = new TempDir();
        string settings = dir.File("settings");
        TestPlugins.Install(Path.Combine(settings, "plugins"), TestPlugins.Sample);
        File.WriteAllBytes(dir.File("bytes.bin"), [0x12]);
        File.WriteAllText(dir.File("plgtype.js"), """console.log("found=" + inspector.types.some(t => t.id === "test.sample.nibble-swap8"));""");
        var services = new CliServices { Scripts = new CoreScriptRunner(() => new ScriptFolders(settings, dir.File("data")), () => null, () => false) };

        CliResult result = Hexed.RunInProcess(dir.Path, services, ["run", "plgtype.js", "bytes.bin", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["found=false"], result.OutLines);
    }

    /// <summary>AUTO-38 の仕様 2: hexed hash はプラグインのアルゴリズムを使える (信頼の記録のあるものだけ。scripts.policy・plugins.signedOnly に従う)。</summary>
    [Fact]
    public void Hash_uses_algorithms_of_trusted_plugins_only()
    {
        using var dir = new TempDir();
        string settings = dir.File("settings");
        string folder = TestPlugins.Install(Path.Combine(settings, "plugins"), TestPlugins.Sample);
        File.WriteAllBytes(dir.File("bytes.bin"), [.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
        var services = new CliServices { Plugins = new CorePluginLoader(() => new ScriptFolders(settings, dir.File("data")), () => false) };

        // 信頼されていないプラグインのアルゴリズムは使えない (知らない ID は使い方の誤り)。
        CliResult untrusted = Hexed.RunInProcess(dir.Path, services, ["hash", "bytes.bin", "-a", "test.sample.sum8", "--format", "gnu", "--lang", "en"]);
        Assert.Equal(2, untrusted.ExitCode);

        new TrustStore(Path.Combine(settings, "scripts", "trust.json")).Add(TrustStore.PluginKind, "test.sample", PluginManager.ContentHash(folder), TrustSource.UserAllowed, []);
        CliResult trusted = Hexed.RunInProcess(dir.Path, services, ["hash", "bytes.bin", "-a", "test.sample.sum8", "--format", "gnu"]);
        Assert.True(trusted.ExitCode == 0, trusted.Err);
        Assert.Equal(["80  bytes.bin"], trusted.OutLines);
        CliResult list = Hexed.RunInProcess(dir.Path, services, ["hash", "-a", "list"]);
        Assert.Contains(list.OutLines, l => l.Contains("test.sample.sum8", StringComparison.Ordinal));

        // 読み込みは実行の間だけ (本体の一覧に残さない)。
        Assert.Null(PluginManager.Current);
        Assert.DoesNotContain(Core.Hashing.HashCatalog.All, a => a.Id == "test.sample.sum8");

        // 「署名されたプラグインだけ」・組織の設定で無効にすると読み込まない。
        File.WriteAllText(Path.Combine(settings, "settings.json"), """{ "plugins.signedOnly": true }""");
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["hash", "bytes.bin", "-a", "test.sample.sum8"]).ExitCode);
        File.WriteAllText(Path.Combine(settings, "settings.json"), """{ "scripts.policy": "disabled" }""");
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["hash", "bytes.bin", "-a", "test.sample.sum8"]).ExitCode);
    }

    /// <summary>
    /// AUTO-34 の仕様 8: 管理者として実行中は、信頼の記録のあるプラグインも --trust がある場合だけ読み込む
    /// (hexed run の --trust なしは終了コード 5 でスクリプトもプラグインも動かない。--trust のない hexed hash は読み込まない)。
    /// </summary>
    [Fact]
    public void Elevated_headless_runs_load_plugins_only_with_trust()
    {
        using var dir = new TempDir();
        string settings = dir.File("settings");
        string folder = TestPlugins.Install(Path.Combine(settings, "plugins"), TestPlugins.Sample);
        var trust = new TrustStore(Path.Combine(settings, "scripts", "trust.json"));
        trust.Add(TrustStore.PluginKind, "test.sample", PluginManager.ContentHash(folder), TrustSource.UserAllowed, []);
        File.WriteAllBytes(dir.File("bytes.bin"), [.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
        string script = dir.File("plgtype.js");
        File.WriteAllText(script, PlgType);
        trust.Add(TrustStore.ScriptKind, TrustStore.ScriptKey(script), TrustStore.HashOf(PlgType), TrustSource.UserAllowed, []);
        var services = new CliServices
        {
            Scripts = new CoreScriptRunner(() => new ScriptFolders(settings, dir.File("data")), () => null, () => true),
            Plugins = new CorePluginLoader(() => new ScriptFolders(settings, dir.File("data")), () => true),
        };

        CliResult denied = Hexed.RunInProcess(dir.Path, services, ["run", "plgtype.js", "bytes.bin", "--lang", "en"]);
        Assert.Equal(5, denied.ExitCode);
        Assert.Equal(string.Empty, denied.Out);
        Assert.Null(PluginManager.Current);

        CliResult trusted = Hexed.RunInProcess(dir.Path, services, ["run", "plgtype.js", "bytes.bin", "--trust"]);
        Assert.True(trusted.ExitCode == 0, trusted.Err);
        Assert.Equal(["found=true", "21"], trusted.OutLines);

        // hexed hash には --trust がないため、管理者として実行中はプラグインのアルゴリズムを使えない (知らない ID は使い方の誤り)。
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["hash", "bytes.bin", "-a", "test.sample.sum8", "--lang", "en"]).ExitCode);
        Assert.Null(PluginManager.Current);
    }
}

[CollectionDefinition("Plugins", DisableParallelization = true)]
public sealed class PluginCollection;
