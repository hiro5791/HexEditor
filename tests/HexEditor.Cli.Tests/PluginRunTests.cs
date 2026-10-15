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
        var services = new CliServices { Scripts = new CoreScriptRunner(() => new ScriptFolders(settings, dir.File("data")), () => null) };

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
        var services = new CliServices { Scripts = new CoreScriptRunner(() => new ScriptFolders(settings, dir.File("data")), () => null) };

        CliResult result = Hexed.RunInProcess(dir.Path, services, ["run", "plgtype.js", "bytes.bin", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["found=false"], result.OutLines);
    }
}

[CollectionDefinition("Plugins", DisableParallelization = true)]
public sealed class PluginCollection;
