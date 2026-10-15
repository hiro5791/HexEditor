using System.Security.Cryptography.X509Certificates;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Plugins;

/// <summary>
/// プラグインのテストの環境: 一時的な設定フォルダ (plugins と信頼の記録)、確認の UI の代わり、出力の記録、署名の確認。
/// 署名の確認は、テストで作ったルート証明書だけを信頼する <see cref="TestAuthenticode.Verify"/> を使う (Windows の証明書ストアは使わない)。
/// </summary>
public sealed class PluginTestHost : IDisposable
{
    private readonly List<PluginManager> _managers = [];

    public PluginTestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "HexEditorTests", "plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Certificates = TestAuthenticode.CreateCertificates();
    }

    public string Root { get; }

    public string PluginsRoot => Path.Combine(Root, "settings", "plugins");

    public string TrustFile => Path.Combine(Root, "settings", "scripts", "trust.json");

    public TestAuthenticode.Certificates Certificates { get; }

    public FakePrompts Prompts { get; } = new();

    public ScriptOutputLog Output { get; } = new();

    public PluginManager? Manager { get; private set; }

    public string Install(TestPlugins.Definition d) => TestPlugins.Install(PluginsRoot, d);

    /// <summary>新しい起動と同じく、管理を作り直して読み込む (再起動)。</summary>
    public async Task<PluginManager> StartAsync(Func<PluginHostOptions, PluginHostOptions>? configure = null, bool attach = true)
    {
        var options = new PluginHostOptions
        {
            PluginsRoot = PluginsRoot,
            AppVersion = "1.0.0",
            Trust = new TrustStore(TrustFile),
            Verifier = new TestRootVerifier(Certificates.Root),
            Prompts = Prompts,
            Output = Output,
            UiLanguage = "en",
        };
        options = configure?.Invoke(options) ?? options;
        var manager = new PluginManager(options);
        _managers.Add(manager);
        Manager = manager;
        if (attach)
        {
            PluginManager.Current = manager;
            PluginIntegration.Attach(manager);
        }

        manager.Discover();
        await manager.LoadAllAsync();
        return manager;
    }

    /// <summary>信頼済みにする (「有効にする」を選んだ記録を置く)。</summary>
    public void Trust(string folder, string id) =>
        new TrustStore(TrustFile).Add(TrustStore.PluginKind, id, PluginManager.ContentHash(folder), TrustSource.UserAllowed, []);

    public IReadOnlyList<string> OutputOf(string pluginName) => [.. Output.LinesOf("plugin:" + pluginName).Select(l => l.Text)];

    public RegisteredExtension Extension(string id) => Manager!.Registry.All.First(e => e.Id == id);

    public bool Execute(string id) => PluginCommands.Execute(Manager!, Manager!.Registry.Find(id) ?? throw new InvalidOperationException("no command " + id), null);

    public void Dispose()
    {
        if (_managers.Contains(PluginManager.Current!))
        {
            PluginManager.Current = null;
        }

        Core.Hashing.HashCatalog.SetPlugins([]);
        Core.FileTypes.FileTypeDetector.GlobalDetectors = null;
        Certificates.Dispose();
        try
        {
            // プラグインの DLL はアンロードしないため消せないことがある (一時フォルダに残す)。
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>確認の UI の代わり。答えを決めておき、出した確認を記録する。</summary>
    public sealed class FakePrompts : IPluginPrompts
    {
        public PluginTrustAnswer Answer { get; set; } = PluginTrustAnswer.Enable;

        public bool ElevatedAnswer { get; set; } = true;

        public List<PluginTrustPrompt> Trust { get; } = [];

        public List<IReadOnlyList<PluginTrustPrompt>> Elevated { get; } = [];

        public Task<PluginTrustAnswer> ConfirmTrustAsync(PluginTrustPrompt prompt)
        {
            lock (Trust)
            {
                Trust.Add(prompt);
            }

            return Task.FromResult(Answer);
        }

        public Task<bool> ConfirmElevatedLoadAsync(IReadOnlyList<PluginTrustPrompt> plugins)
        {
            lock (Elevated)
            {
                Elevated.Add(plugins);
            }

            return Task.FromResult(ElevatedAnswer);
        }
    }

    /// <summary>テストのルート証明書だけを信頼する署名の確認。</summary>
    public sealed class TestRootVerifier(X509Certificate2 root) : IPluginSignatureVerifier
    {
        public FileSignature Verify(string path)
        {
            TestAuthenticode.Result r = TestAuthenticode.Verify(path, root);
            return r.State switch
            {
                TestAuthenticode.State.Valid => new FileSignature(PluginSignatureState.Valid, r.Publisher, r.Thumbprint),
                TestAuthenticode.State.Unsigned => FileSignature.Unsigned,
                _ => new FileSignature(PluginSignatureState.Invalid, Problem: r.Problem),
            };
        }
    }
}
