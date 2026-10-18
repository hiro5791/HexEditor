using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace HexEditor.Platform.Network;

/// <summary>通信を許可されていない (オフラインモードなど。09 の UI-58)。</summary>
public sealed class NetworkDisabledException(NetworkFeature feature)
    : InvalidOperationException($"Network access for {feature} is disabled.")
{
    public NetworkFeature Feature { get; } = feature;
}

/// <summary>応答の本体が上限を超えた (受信は上限を超えた時点でやめている)。</summary>
public sealed class NetworkResponseTooLargeException(long maxBytes)
    : IOException($"The response is larger than {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>呼んだスレッドでそのまま報告する進捗 (<see cref="Progress{T}"/> と違い、同期コンテキストに送らない)。</summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
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

    /// <summary>
    /// GET して文字列を返す。タイムアウトは <paramref name="timeout"/> (既定 15 秒)。応答の本体が <paramref name="maxBytes"/> を超えたら、
    /// その時点で読み込みをやめて <see cref="NetworkResponseTooLargeException"/> (応答の全体をメモリに読み込んでから調べない)。
    /// </summary>
    public async Task<string> GetStringAsync(NetworkFeature feature, Uri uri, bool manual, IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default, long maxBytes = DefaultMaxStringBytes)
    {
        using var buffer = new MemoryStream();
        string? charset = null;
        await DownloadAsync(feature, uri, manual, buffer, maxBytes, null, headers, timeout, cancellationToken,
            response => charset = response.Content.Headers.ContentType?.CharSet).ConfigureAwait(false);
        Encoding encoding = Encoding.UTF8;
        if (charset is { Length: > 0 })
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // 知らない文字コードは UTF-8 として読む。
            }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>文字列で受け取る応答の既定の上限 (32 MiB)。</summary>
    public const long DefaultMaxStringBytes = 32L * 1024 * 1024;

    /// <summary>GET して本体のバイト列を返す。<paramref name="maxBytes"/> を超えたらその時点でやめる (<see cref="NetworkResponseTooLargeException"/>)。</summary>
    public async Task<byte[]> GetBytesAsync(NetworkFeature feature, Uri uri, bool manual, long maxBytes, IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await DownloadAsync(feature, uri, manual, buffer, maxBytes, null, headers, timeout, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// GET して本体を <paramref name="destination"/> に少しずつ書く (ダウンロード)。<paramref name="timeout"/> は応答の待ちと本体の受信の全体に
    /// かかる。<c>Content-Length</c> が <paramref name="maxBytes"/> を超えるとき、または受信した量が超えた時点で、超えた分を書かずに
    /// <see cref="NetworkResponseTooLargeException"/> を投げる (サーバーが大きさを偽っても際限なく受け取らない)。戻り値は受信したバイト数。
    /// <paramref name="progress"/> には (受信した量, 応答の大きさ (分かれば)) を報告する。
    /// </summary>
    public async Task<long> DownloadAsync(NetworkFeature feature, Uri uri, bool manual, Stream destination, long maxBytes,
        IProgress<(long Done, long? Total)>? progress = null, IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, Action<HttpResponseMessage>? onResponse = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        TimeSpan wait = timeout ?? DefaultTimeout;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(wait);
        try
        {
            using HttpResponseMessage response = await SendCoreAsync(feature, HttpMethod.Get, uri, manual, headers, HttpCompletionOption.ResponseHeadersRead,
                limit.Token).ConfigureAwait(false);
            onResponse?.Invoke(response);
            long? total = response.Content.Headers.ContentLength;
            if (total > maxBytes)
            {
                throw new NetworkResponseTooLargeException(maxBytes);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            byte[] chunk = new byte[81920];
            long done = 0;
            int n;
            while ((n = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (done + n > maxBytes)
                {
                    throw new NetworkResponseTooLargeException(maxBytes);
                }

                await destination.WriteAsync(chunk.AsMemory(0, n), limit.Token).ConfigureAwait(false);
                done += n;
                progress?.Report((done, total));
            }

            return done;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response from {uri.Host} within {wait.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// 要求を送る。成功 (2xx) 以外は <see cref="HttpRequestException"/> (状態コード付き)。応答の本体はすべて受信してから返す
    /// (大きさの決まらない応答の受信には <see cref="DownloadAsync"/> を使う)。
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(NetworkFeature feature, HttpMethod method, Uri uri, bool manual,
        IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            return await SendCoreAsync(feature, method, uri, manual, headers, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response from {uri.Host} within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(NetworkFeature feature, HttpMethod method, Uri uri, bool manual,
        IReadOnlyDictionary<string, string>? headers, HttpCompletionOption completion, CancellationToken token)
    {
        EnsureAllowed(feature, manual);
        Record(feature, method.Method, uri);
        using var request = new HttpRequestMessage(method, uri);
        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response = await _http.SendAsync(request, completion, token).ConfigureAwait(false);
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
