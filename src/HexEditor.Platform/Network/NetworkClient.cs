using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace HexEditor.Platform.Network;

/// <summary>通信を許可されていない (オフラインモードなど。09 の UI-58)。</summary>
public sealed class NetworkDisabledException(NetworkFeature feature)
    : InvalidOperationException($"Network access for {feature} is disabled.")
{
    public NetworkFeature Feature { get; } = feature;
}

/// <summary>アプリが行った通信の記録 1 件 (テストと診断用。ファイルの内容・パスは含まない)。</summary>
public sealed record NetworkRequestRecord(DateTimeOffset Time, NetworkFeature Feature, string Method, string Host, string Path);

/// <summary>
/// アプリが自分から行う HTTP の通信の唯一の入口 (00-overview 11.4、09 の UI-58)。
/// 要求の直前に <see cref="NetworkPolicy"/> で許可を確かめ、User-Agent は <c>HexEditor/&lt;版&gt;</c> だけを付ける
/// (10 の PKG-17 の仕様 5。利用者の識別子を送らない)。接続にはシステムのプロキシ設定を使う (UI-58 の仕様 4)。
/// 行った通信は <see cref="Requests"/> に記録する (テストでオフラインのときに通信がないことを確かめる)。
/// </summary>
public sealed class NetworkClient : IDisposable
{
    /// <summary>更新の確認のタイムアウト (PKG-17 の「巨大ファイル・長時間処理」)。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly NetworkPolicy _policy;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<NetworkRequestRecord> _requests = new();
    private readonly TimeProvider _time;

    /// <param name="handler">テストでは偽のサーバーに向けた handler を渡す。既定はシステムのプロキシを使う SocketsHttpHandler。</param>
    public NetworkClient(NetworkPolicy policy, string appVersion, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = true }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        UserAgent = $"HexEditor/{appVersion}";
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HexEditor", appVersion));
    }

    public NetworkPolicy Policy => _policy;

    /// <summary>送る User-Agent。</summary>
    public string UserAgent { get; }

    /// <summary>このプロセスで行った通信 (古い順)。</summary>
    public IReadOnlyList<NetworkRequestRecord> Requests => [.. _requests];

    /// <summary>通信の直前に呼ぶ。許可されていなければ <see cref="NetworkDisabledException"/>。</summary>
    public void EnsureAllowed(NetworkFeature feature, bool manual = true)
    {
        bool allowed = feature == NetworkFeature.Updates ? _policy.UpdateCheckAllowed(manual) : _policy.Allows(feature);
        if (!allowed)
        {
            throw new NetworkDisabledException(feature);
        }
    }

    /// <summary>外部のライブラリ (Velopack のダウンロード) が通信する前に記録する。</summary>
    public void Record(NetworkFeature feature, string method, Uri uri) =>
        _requests.Enqueue(new NetworkRequestRecord(_time.GetUtcNow(), feature, method, uri.Host, uri.AbsolutePath));

    /// <summary>GET して文字列を返す。タイムアウトは <paramref name="timeout"/> (既定 15 秒)。</summary>
    public async Task<string> GetStringAsync(NetworkFeature feature, Uri uri, bool manual, IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(feature, HttpMethod.Get, uri, manual, headers, timeout, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>要求を送る。成功 (2xx) 以外は <see cref="HttpRequestException"/> (状態コード付き)。</summary>
    public async Task<HttpResponseMessage> SendAsync(NetworkFeature feature, HttpMethod method, Uri uri, bool manual,
        IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(feature, manual);
        Record(feature, method.Method, uri);
        using var request = new HttpRequestMessage(method, uri);
        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response from {uri.Host} within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException($"{uri.Host} returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining) && remaining.FirstOrDefault() == "0")
            {
                error.Data["RateLimited"] = true;
            }

            response.Dispose();
            throw error;
        }

        return response;
    }

    public void Dispose() => _http.Dispose();
}
