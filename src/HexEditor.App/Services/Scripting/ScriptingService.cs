using System.Reflection;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Platform;
using HexEditor.Platform.Network;
using HexEditor.Platform.Updates;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services.Scripting;

/// <summary>
/// スクリプトの実行の管理 (アプリ全体で 1 つ。08 の AUTO-01〜AUTO-18、AUTO-34)。Core の <see cref="ScriptHost"/> にアプリの環境
/// (<see cref="AppScriptEnvironment"/>) をつなぐ。
/// </summary>
/// <remarks>
/// 次の領域 (スクリプト一覧・エディタ・コンソール・出力パネル・フック・マクロ・プラグイン。AUTO-19〜AUTO-35) はここから
/// <see cref="Host"/> (実行)、<see cref="Output"/> (出力パネルの元)、<see cref="Trust"/> (信頼と許可の一覧)、<see cref="Components"/>
/// (C# スクリプト コンポーネント)、<see cref="Environment"/> (イベントの発生: <c>EventSource</c>) を使う。
/// </remarks>
internal static class ScriptingService
{
    /// <summary>アプリに埋め込んだ C# スクリプト コンポーネントの検証用の一覧 (build/Build-CSharpComponent.ps1 が作る)。</summary>
    private const string ManifestResource = "HexEditor.CSharpComponent.json";

    private static Lazy<ScriptHost>? _host;
    private static Lazy<CSharpComponentManager>? _components;
    private static string? _settingsFolder;

    /// <summary>
    /// 実行の管理。最初に使うときに作る (起動の経路でファイル・通信の準備をしないため。信頼の一覧の読み込み、コンポーネントの一覧の解析を含む)。
    /// </summary>
    public static ScriptHost Host => (_host ?? throw new InvalidOperationException("ScriptingService is not initialized.")).Value;

    public static AppScriptEnvironment Environment { get; private set; } = null!;

    /// <summary>スクリプトの置き場所 (設定フォルダとデータフォルダ。ファイルは作らない)。</summary>
    public static ScriptFolders Folders { get; private set; } = null!;

    public static TrustStore Trust => Host.Trust;

    public static CSharpComponentManager Components => (_components ?? throw new InvalidOperationException("ScriptingService is not initialized.")).Value;

    public static ScriptOutputLog Output { get; } = new();

    /// <summary>実行中のスクリプトの数 (まだ実行の管理を作っていなければ 0。コマンドの状態のために作らない)。</summary>
    public static int RunningCount => _host is { IsValueCreated: true } h ? h.Value.Running.Count : 0;

    /// <summary>テンプレートエンジン (07) をスクリプトに渡すもの (AUTO-13)。最初に使うときに作る。</summary>
    public static Core.Templates.TemplateScriptEngine Templates => _templates.Value;

    private static readonly Lazy<Core.Templates.TemplateScriptEngine> _templates = new(CreateTemplates);

    /// <summary>組織の設定でスクリプトが無効 (AUTO-34 の仕様 9)。</summary>
    public static bool PolicyDisabled => App.Settings.GetString(ScriptSettingKeys.Policy, "none") == ScriptSettingKeys.PolicyDisabled;

    /// <summary>
    /// 起動時に呼ぶ。ここでは UI スレッドへの受け渡しと環境 (イベントの発生元) だけを用意し、ディスク・通信を使う部分 (信頼の一覧、
    /// C# スクリプト コンポーネント、テンプレートの一覧) は最初に使うときに作る。準備の失敗はログに残し、アプリの起動は続ける
    /// (スクリプトが使えないだけにする)。
    /// </summary>
    public static void Initialize(IAppEnvironment env, DispatcherQueue queue)
    {
        try
        {
            Msg.Default = System.Globalization.CultureInfo.CurrentUICulture.Name;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Script messages: {ex.GetType().Name}");
        }

        _settingsFolder = env.Locations.Settings;
        Folders = new ScriptFolders(env.Locations.Settings, env.Locations.Root);
        Environment = new AppScriptEnvironment(queue, env.AppVersion, TestHooks.ElevationOverride ?? env.IsElevated);
        Environment.Templates = new LazyTemplateEngine();
        _components = new Lazy<CSharpComponentManager>(() => CreateComponents(env), LazyThreadSafetyMode.ExecutionAndPublication);
        _host = new Lazy<ScriptHost>(() => CreateHost(env, queue), LazyThreadSafetyMode.ExecutionAndPublication);

        // コマンドラインの --run (AUTO-37): 開いた後に実行する (信頼の確認は通常どおり)。
        CommandLineActions.RunScript = (_, path) =>
        {
            Start(new ScriptRunRequest { Path = Path.GetFullPath(path) });
            return Task.CompletedTask;
        };
    }

    private static CSharpComponentManager CreateComponents(IAppEnvironment env)
    {
        CSharpComponentManifest? manifest = null;
        try
        {
            manifest = LoadManifest();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            AppLog.Warning($"C# component manifest unreadable: {ex.GetType().Name}");
        }

        // 配布元と通信は追加するときに初めて使う (起動時に HttpClient を作らない)。
        return new CSharpComponentManager(env.Locations.Components, manifest, new GitHubComponentFeed(() => AppUpdates.Network, () => UpdateSource.Resolve(AppUpdates.Preferences)),
            new PlatformVerifier(), () => AppUpdates.Policy.DownloadsAllowed);
    }

    private static ScriptHost CreateHost(IAppEnvironment env, DispatcherQueue queue)
    {
        var folders = new ScriptFolders(env.Locations.Settings, env.Locations.Root);
        TrustStore trust;
        try
        {
            trust = new TrustStore(folders.TrustFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 一覧を読めない場合は空として扱う (信頼は安全側。もう一度確認を出す)。
            AppLog.Warning($"Script trust list unreadable: {ex.GetType().Name}");
            trust = new TrustStore(null);
        }

        var host = new ScriptHost(folders, trust, LimitsFromSettings, () => App.Settings.GetString(ScriptSettingKeys.Policy, "none"), Components)
        {
            BundledSampleFolders = [Path.Combine(AppContext.BaseDirectory, "Samples", "scripts")],
        };
        host.RunStarted += (_, run) => queue.TryEnqueue(() => WindowManager.Current.OnScriptStarted(run));
        return host;
    }

    private static Core.Templates.TemplateScriptEngine CreateTemplates() =>
        new(Core.Templates.TemplateCatalog.CreateDefault(_settingsFolder is null ? null : Path.Combine(_settingsFolder, "templates")));

    /// <summary>テンプレートエンジンを最初に使うときに作る (起動時にテンプレートの一覧を読まない)。</summary>
    private sealed class LazyTemplateEngine : IScriptTemplateEngine
    {
        public IReadOnlyList<Sdk.TemplateInfo> List() => Templates.List();

        public IReadOnlyList<Sdk.TemplateInfo> Detect(Core.Engine.Document document, string? path) => Templates.Detect(document, path);

        public Task<ITemplateResultNode> ApplyAsync(IScriptDocumentHost document, string nameOrPath, long offset, bool show, CancellationToken cancellationToken) =>
            Templates.ApplyAsync(document, nameOrPath, offset, show, cancellationToken);

        public Core.Templates.Extensions.TemplateExtensionRegistry Extensions => Templates.Extensions;
    }

    private static ScriptLimits LimitsFromSettings() => ScriptLimits.FromSettings(key => App.Settings.Contains(key) ? App.Settings.GetDouble(key, 0) : null);

    private static CSharpComponentManifest? LoadManifest()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ManifestResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return CSharpComponentManifest.Parse(reader.ReadToEnd());
    }

    /// <summary>実行する (UI スレッドから)。</summary>
    public static ScriptRun Start(ScriptRunRequest request) => Host.Start(request, Environment);

    public static void StopAll()
    {
        if (_host is { IsValueCreated: true } h)
        {
            h.Value.StopAll();
        }
    }

    private sealed class PlatformVerifier : IAuthenticodeVerifier
    {
        public bool IsMicrosoftSigned(string path, out string? error) => Platform.Security.AuthenticodeVerifier.IsMicrosoftSigned(path, out error);
    }
}

/// <summary>
/// コンポーネントの配布元 (更新と同じ配布元・チャネル。10 の PKG-17〜PKG-21)。GitHub Releases の <c>v&lt;バージョン&gt;</c> の資産から
/// zip をダウンロードする。通信は「追加コンポーネントのダウンロード」(00 の 11.4) として記録する。
/// </summary>
internal sealed class GitHubComponentFeed(Func<NetworkClient> network, Func<UpdateSource> source) : ICSharpComponentFeed
{
    public string Host => source().Repository.Host;

    public async Task DownloadAsync(CSharpComponentManifest manifest, Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{source().Repository}/releases/download/v{manifest.Version}/{Uri.EscapeDataString(manifest.PackageName)}");
        using HttpResponseMessage response = await network().SendAsync(NetworkFeature.Components, HttpMethod.Get, uri, manual: true,
            timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long total = response.Content.Headers.ContentLength ?? manifest.PackageSize;
        byte[] buffer = new byte[81920];
        long done = 0;
        // 一覧の大きさより長い応答は途中で打ち切る (ハッシュの確認の前に、際限なく書き込まないため)。
        long limit = manifest.PackageSize > 0 ? manifest.PackageSize : 512L * 1024 * 1024;
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (done + n > limit)
            {
                throw new IOException(Loc.Format("CSharpComponent_DownloadTooLarge", limit));
            }

            await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            done += n;
            progress?.Report(total > 0 ? (double)done / total : 0);
        }
    }
}
