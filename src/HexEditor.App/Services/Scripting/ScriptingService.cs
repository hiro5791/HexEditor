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

    public static ScriptHost Host { get; private set; } = null!;

    public static AppScriptEnvironment Environment { get; private set; } = null!;

    public static TrustStore Trust { get; private set; } = null!;

    public static CSharpComponentManager Components { get; private set; } = null!;

    public static ScriptOutputLog Output { get; } = new();

    /// <summary>組織の設定でスクリプトが無効 (AUTO-34 の仕様 9)。</summary>
    public static bool PolicyDisabled => App.Settings.GetString(ScriptSettingKeys.Policy, "none") == ScriptSettingKeys.PolicyDisabled;

    public static void Initialize(IAppEnvironment env, DispatcherQueue queue)
    {
        Msg.Default = System.Globalization.CultureInfo.CurrentUICulture.Name;
        var folders = new ScriptFolders(env.Locations.Settings, env.Locations.Root);
        Trust = new TrustStore(folders.TrustFile);
        Components = new CSharpComponentManager(env.Locations.Components, LoadManifest(),
            new GitHubComponentFeed(AppUpdates.Network, UpdateSource.Product), new PlatformVerifier(), () => AppUpdates.Policy.DownloadsAllowed);
        Host = new ScriptHost(folders, Trust, LimitsFromSettings, () => App.Settings.GetString(ScriptSettingKeys.Policy, "none"), Components)
        {
            BundledSampleFolders = [Path.Combine(AppContext.BaseDirectory, "Samples", "scripts")],
        };
        Environment = new AppScriptEnvironment(queue, env.AppVersion, env.IsElevated);
        Host.RunStarted += (_, run) => queue.TryEnqueue(() => WindowManager.Current.OnScriptStarted(run));
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

    public static void StopAll() => Host.StopAll();

    private sealed class PlatformVerifier : IAuthenticodeVerifier
    {
        public bool IsMicrosoftSigned(string path, out string? error) => Platform.Security.AuthenticodeVerifier.IsMicrosoftSigned(path, out error);
    }
}

/// <summary>
/// コンポーネントの配布元 (更新と同じ配布元・チャネル。10 の PKG-17〜PKG-21)。GitHub Releases の <c>v&lt;バージョン&gt;</c> の資産から
/// zip をダウンロードする。通信は「追加コンポーネントのダウンロード」(00 の 11.4) として記録する。
/// </summary>
internal sealed class GitHubComponentFeed(NetworkClient network, UpdateSource source) : ICSharpComponentFeed
{
    public string Host => source.Repository.Host;

    public async Task DownloadAsync(CSharpComponentManifest manifest, Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{source.Repository}/releases/download/v{manifest.Version}/{Uri.EscapeDataString(manifest.PackageName)}");
        using HttpResponseMessage response = await network.SendAsync(NetworkFeature.Components, HttpMethod.Get, uri, manual: true,
            timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long total = response.Content.Headers.ContentLength ?? manifest.PackageSize;
        byte[] buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            done += n;
            progress?.Report(total > 0 ? (double)done / total : 0);
        }
    }
}
