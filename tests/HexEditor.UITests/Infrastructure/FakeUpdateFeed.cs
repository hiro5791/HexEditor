using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// 更新のテスト用の偽の配布元 (GitHub Enterprise と同じ形の API を 127.0.0.1 で返す。10 の PKG-17 の仕様 8)。
/// アプリには設定 <c>test.update.source</c> に <see cref="RepositoryUrl"/> を書いて渡す。外部には接続しない。
/// </summary>
public sealed class FakeUpdateFeed : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeUpdateFeed(params string[] versions)
    {
        Versions = [.. versions];
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>公開しているリリースの版 (-preview.N は Pre-release)。</summary>
    public List<string> Versions { get; }

    public int Requests { get; private set; }

    public string RepositoryUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/test/feed";

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string? requestLine = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                    {
                    }

                    Requests++;
                    bool releases = requestLine?.Contains("/api/v3/repos/test/feed/releases", StringComparison.Ordinal) == true;
                    byte[] body = Encoding.UTF8.GetBytes(releases ? ReleasesJson() : "{}");
                    string head = $"HTTP/1.1 {(releases ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                }
            });
        }
    }

    private string ReleasesJson()
    {
        var array = new JsonArray();
        foreach (string v in Versions)
        {
            array.Add(new JsonObject
            {
                ["tag_name"] = "v" + v,
                ["html_url"] = $"{RepositoryUrl}/releases/tag/v{v}",
                ["draft"] = false,
                ["prerelease"] = v.Contains('-'),
                ["assets"] = new JsonArray(),
            });
        }

        return array.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }
    }
}
