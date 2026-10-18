using System.Net;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Templates.Online;
using HexEditor.Platform.Network;

namespace HexEditor.App.Templates;

/// <summary>
/// オンラインのテンプレートのリポジトリ (07 の TPL-36) のアプリ全体の状態: リポジトリの一覧 (公式と、利用者が追加したもの)、通信、
/// インストール先 (<c>templates-online/</c>)、起動時の更新の確認 (1 日 1 回)。
/// <para>
/// 通信はアプリの通信の入口 (09 の UI-58 の <see cref="NetworkClient"/>、機能「テンプレートなどのダウンロード」) だけを使う。オフラインモード・
/// ダウンロードの設定がオフなら一切通信しない (受け入れ基準 4)。要求は索引・署名・テンプレートの URL への GET だけで、開いているファイルの
/// 名前や内容を送らない (仕様 7)。プロキシはシステムの設定 (仕様 8。NetworkClient の既定)。
/// </para>
/// <para>
/// 公式のリポジトリの置き場所と鍵は未決定 (仕様 1)。決まるまでは公式のリポジトリを持たない。テスト用のビルドだけ、設定
/// <c>test.templates.officialRepository</c> (索引の URL) と <c>test.templates.officialKey</c> (Ed25519 の公開鍵、Base64) で公式の扱いの
/// リポジトリを差し替えられる (TC-TPL-36-01 の「その公開鍵を公式の鍵の代わりに使うテスト用のビルド」)。
/// </para>
/// </summary>
public static class OnlineTemplateService
{
    /// <summary>利用者が追加したリポジトリ (索引の URL の一覧)。</summary>
    public const string RepositoriesKey = "templates.online.repositories";

    /// <summary>起動時に更新を確認する (1 日 1 回。仕様 5)。</summary>
    public const string CheckUpdatesKey = "templates.online.checkUpdates";

    /// <summary>新しい版を自動で更新する (既定はオフ: InfoBar で知らせるだけ。仕様 5)。</summary>
    public const string AutoUpdateKey = "templates.online.autoUpdate";

    /// <summary>最後に更新を確認した時刻 (state.json)。</summary>
    public const string LastCheckStateKey = "templates.online.lastCheck";

    public const string TestOfficialRepositoryKey = "test.templates.officialRepository";
    public const string TestOfficialKeyKey = "test.templates.officialKey";

    /// <summary>公式のリポジトリ (置き場所が決まったら索引の URL と鍵をここに書く)。</summary>
    private static readonly (string IndexUrl, string PublicKeyBase64)[] Official = [];

    private static OnlineTemplateStore? _store;
    private static TemplateRepositoryClient? _client;

    /// <summary>インストール先。</summary>
    public static OnlineTemplateStore Store => _store ??= CreateStore();

    private static OnlineTemplateStore CreateStore()
    {
        var store = new OnlineTemplateStore(TemplateService.OnlineFolder);
        store.Changed += (_, _) => TemplateService.NotifyCatalogChanged();
        return store;
    }

    public static TemplateRepositoryClient Client => _client ??= new TemplateRepositoryClient(new NetworkFeedTransport());

    /// <summary>通信してよいか (オフラインモード・ダウンロードの設定。仕様 7)。</summary>
    public static bool DownloadsAllowed => AppUpdates.Policy.DownloadsAllowed;

    /// <summary>テスト用のビルドではループバックの HTTP のリポジトリ (テスト用のサーバー) を使える。</summary>
    public static bool AllowLoopbackHttp => PackagingTestHooks.TestBuild;

    /// <summary>今の時刻 (テスト用の時刻の固定と、更新の確認の時計の進め方に従う)。</summary>
    public static DateTimeOffset Now() => (TestHooks.Time ?? TimeProvider.System).GetUtcNow() + PackagingTestHooks.UpdateClockOffset;

    /// <summary>リポジトリの一覧 (公式、利用者が追加したものの順)。</summary>
    public static IReadOnlyList<TemplateRepository> Repositories
    {
        get
        {
            var list = new List<TemplateRepository>();
            foreach ((string url, string key) in OfficialRepositories())
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && TemplateRepository.IsAllowedUrl(uri, AllowLoopbackHttp))
                {
                    try
                    {
                        list.Add(new TemplateRepository(uri, Loc.Get("TplOnline_OfficialRepository"), [Convert.FromBase64String(key)]));
                    }
                    catch (FormatException)
                    {
                        AppLog.Warning("Online templates: the official key is not Base64.");
                    }
                }
            }

            foreach (string url in UserRepositories)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && TemplateRepository.IsAllowedUrl(uri, AllowLoopbackHttp)
                    && list.All(r => r.IndexUrl != uri))
                {
                    list.Add(new TemplateRepository(uri, uri.Host, []));
                }
            }

            return list;
        }
    }

    private static IEnumerable<(string Url, string Key)> OfficialRepositories()
    {
        if (PackagingTestHooks.TestBuild && App.Settings.GetString(TestOfficialRepositoryKey, string.Empty) is { Length: > 0 } testUrl)
        {
            yield return (testUrl, App.Settings.GetString(TestOfficialKeyKey, string.Empty));
            yield break;
        }

        foreach ((string url, string key) in Official)
        {
            yield return (url, key);
        }
    }

    /// <summary>利用者が追加したリポジトリの URL。</summary>
    public static IReadOnlyList<string> UserRepositories =>
        App.Settings.GetNode(RepositoriesKey) is JsonArray a
            ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>().Where(s => s.Length > 0)]
            : [];

    /// <summary>利用者のリポジトリを加える。HTTPS でない URL は加えない (false)。</summary>
    public static bool AddRepository(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || !TemplateRepository.IsAllowedUrl(uri, AllowLoopbackHttp))
        {
            return false;
        }

        List<string> list = [.. UserRepositories.Where(u => !string.Equals(u, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)), uri.AbsoluteUri];
        App.Settings.SetNode(RepositoriesKey, new JsonArray([.. list.Select(u => (JsonNode?)u)]));
        return true;
    }

    public static void RemoveRepository(string url)
    {
        List<string> list = [.. UserRepositories.Where(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase))];
        App.Settings.SetNode(RepositoriesKey, new JsonArray([.. list.Select(u => (JsonNode?)u)]));
    }

    /// <summary>すべてのリポジトリの索引を読む (通信がオフなら何もしない)。読めないリポジトリは結果に理由を返す。</summary>
    public static async Task<IReadOnlyList<TemplateRepositoryListing>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!DownloadsAllowed)
        {
            return [];
        }

        var listings = new List<TemplateRepositoryListing>();
        foreach (TemplateRepository repository in Repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateRepositoryListing listing = await Client.LoadAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!listing.Ok)
            {
                AppLog.Warning($"Online templates: {repository.IndexUrl.Host}: {listing.Error} {listing.Message}");
            }

            listings.Add(listing);
        }

        return listings;
    }

    /// <summary>ダウンロードしてインストールする (照合に失敗したら <see cref="TemplateTamperedException"/>。ファイルは残さない)。</summary>
    public static async Task<InstalledOnlineTemplate> InstallAsync(TemplateRepositoryListing listing, OnlineTemplateEntry entry, CancellationToken cancellationToken)
    {
        byte[] data = await Client.DownloadAsync(listing.Repository, entry, cancellationToken).ConfigureAwait(false);
        return Store.Install(listing, entry, data);
    }

    /// <summary>前回の更新の確認の時刻。</summary>
    public static DateTimeOffset? LastCheck
    {
        get => Commands.CommandService.State?.Get(LastCheckStateKey) is JsonValue v && v.TryGetValue(out string? s)
            && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;
        set => Commands.CommandService.State?.Set(LastCheckStateKey, value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 新しい版を探す (起動時の確認と「テンプレート: 更新を確認」)。<paramref name="scheduled"/> なら 1 日 1 回の確認で、まだ時刻でなければ
    /// 何もしない (null)。通信がオフ・インストール済みがない場合も null。
    /// </summary>
    public static async Task<IReadOnlyList<OnlineTemplateUpdate>?> FindUpdatesAsync(bool scheduled, CancellationToken cancellationToken)
    {
        if (!DownloadsAllowed || Store.Installed.Count == 0)
        {
            return null;
        }

        DateTimeOffset now = Now();
        if (scheduled && (!App.Settings.GetBool(CheckUpdatesKey, true) || !OnlineTemplateUpdateSchedule.IsDue(LastCheck, now)))
        {
            return null;
        }

        IReadOnlyList<TemplateRepositoryListing> listings = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        LastCheck = now;
        return Store.FindUpdates(listings);
    }
}

/// <summary>アプリの通信の入口 (UI-58) を使うテンプレートの配信の読み込み。</summary>
internal sealed class NetworkFeedTransport : ITemplateFeedTransport
{
    /// <summary>1 つの要求のタイムアウト。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<byte[]> GetAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await AppUpdates.Network.SendAsync(NetworkFeature.Downloads, HttpMethod.Get, uri, manual: true,
                timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                throw new TemplateFeedTooLargeException(maxBytes);
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return body.LongLength > maxBytes ? throw new TemplateFeedTooLargeException(maxBytes) : body;
        }
        catch (NetworkDisabledException ex)
        {
            throw new TemplateFeedUnavailableException(ex.Message, ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode code)
        {
            throw new TemplateFeedUnavailableException($"{(int)code} {code}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            throw new TemplateFeedUnavailableException(ex.Message, ex);
        }
    }
}
