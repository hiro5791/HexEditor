using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.Platform.Tests.Support;

/// <summary>
/// テスト用の偽の配布元 (TD-PKG-UPDATE-FEED と同じ形のリリースを返す GitHub の API)。実際のネットワークには接続しない。
/// 既定のリリース: 0.9.0 (安定版)、0.9.1 (安定版)、0.9.2-preview.1 (Pre-release)、0.9.2 (安定版、下書き = 一覧に出ない)。
/// </summary>
public sealed class FakeReleaseFeed : HttpMessageHandler
{
    private readonly object _lock = new();

    public FakeReleaseFeed()
    {
        Releases =
        [
            ("0.9.0", false, false),
            ("0.9.1", false, false),
            ("0.9.2-preview.1", true, false),
            ("0.9.2", false, true),
        ];
    }

    /// <summary>版、Pre-release か、下書きか。</summary>
    public List<(string Version, bool Prerelease, bool Draft)> Releases { get; }

    /// <summary>受けた要求の URL。</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>true なら応答しない (接続できない状態。取り消されるまで待つ)。</summary>
    public bool Hang { get; set; }

    /// <summary>指定すればその状態コードを返す (利用制限など)。</summary>
    public HttpStatusCode? Status { get; set; }

    /// <summary>下書きを公開する (テストの中で 0.9.2 を公開する手順)。</summary>
    public void Publish(string version)
    {
        int i = Releases.FindIndex(r => r.Version == version);
        Releases[i] = (version, Releases[i].Prerelease, false);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Requests.Add(request.RequestUri!);
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Status is { } status)
        {
            var error = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Forbidden)
            {
                error.Headers.Add("X-RateLimit-Remaining", "0");
            }

            return error;
        }

        // 下書きは認証のない要求には返らない (GitHub と同じ)。
        var array = new JsonArray();
        foreach ((string version, bool pre, bool draft) in Releases.Where(r => !r.Draft).OrderByDescending(r => r.Version, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["tag_name"] = "v" + version,
                ["html_url"] = $"https://github.com/test/feed/releases/tag/v{version}",
                ["draft"] = draft,
                ["prerelease"] = pre,
                ["assets"] = new JsonArray(new JsonObject
                {
                    ["name"] = $"HexEditor-{version}-x64-portable.zip",
                    ["browser_download_url"] = $"https://github.com/test/feed/releases/download/v{version}/HexEditor-{version}-x64-portable.zip",
                    ["size"] = 1234,
                }),
            });
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(array.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

/// <summary>時刻を手で進める時計。</summary>
public sealed class ManualClock(DateTimeOffset start)
{
    public DateTimeOffset Now { get; private set; } = start;

    public void Advance(TimeSpan by) => Now += by;
}
