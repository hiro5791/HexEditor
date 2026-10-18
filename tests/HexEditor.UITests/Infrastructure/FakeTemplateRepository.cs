using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// オンラインのテンプレートのテスト用のリポジトリ (07 の TPL-36)。フォルダ (TD-TPL-REPO) のファイルを 127.0.0.1 で配信し、受け取った要求
/// (要求の行・ヘッダー・本文) をすべて記録する (TC-TPL-36-05)。外部には接続しない。アプリには設定
/// <c>test.templates.officialRepository</c> に <see cref="IndexUrl"/>、<c>test.templates.officialKey</c> にテスト用の公開鍵を書いて渡す。
/// </summary>
public sealed class FakeTemplateRepository : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _folder;

    public FakeTemplateRepository(string folder)
    {
        _folder = folder;
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>URL のパス → 返すファイル (フォルダからの相対パス) の差し替え。</summary>
    public ConcurrentDictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

    /// <summary>受け取った要求 (要求の行・ヘッダー・本文をそのまま)。</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public string IndexUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/index.json";

    /// <summary>設定ファイルに書く項目 (公式の扱いのリポジトリとテスト用の鍵)。</summary>
    public JsonObject Settings(bool downloads = true) => new()
    {
        ["test.templates.officialRepository"] = IndexUrl,
        ["test.templates.officialKey"] = HexEditor.TestData.TestDataCatalog.TplRepo.PublicKeyBase64,
        ["network.downloads.enabled"] = downloads,
    };

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
                    try
                    {
                        await ServeAsync(client.GetStream());
                    }
                    catch (IOException)
                    {
                    }
                }
            });
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        var raw = new StringBuilder();
        var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync();
        raw.AppendLine(requestLine);
        int contentLength = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            raw.AppendLine(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        if (contentLength > 0)
        {
            char[] body = new char[contentLength];
            int read = await reader.ReadBlockAsync(body, 0, contentLength);
            raw.Append(body, 0, read);
        }

        Requests.Enqueue(raw.ToString());
        string path = requestLine?.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
        string relative = Map.TryGetValue(path, out string? mapped) ? mapped : path.TrimStart('/');
        string file = Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar));
        bool found = !relative.Contains("..", StringComparison.Ordinal) && File.Exists(file);
        byte[] content = found ? await File.ReadAllBytesAsync(file) : [];
        string head = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/octet-stream\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
