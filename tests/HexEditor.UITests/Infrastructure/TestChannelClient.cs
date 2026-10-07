using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// アプリのテスト用の命令の通り道 (名前付きパイプ <c>HexEditor.Test.&lt;プロセス ID&gt;</c>) の相手側。
/// 1 行に 1 つの JSON の命令を送り、1 行の JSON の答えを受け取る。命令の一覧はアプリの MainWindow.TestMenu.cs。
/// </summary>
public sealed class TestChannelClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private TestChannelClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>通り道が開くまで待って接続する。プロセスが終わったら null。</summary>
    public static async Task<TestChannelClient?> ConnectAsync(int processId, Func<bool> hasExited, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !hasExited())
        {
            var pipe = new NamedPipeClientStream(".", $"HexEditor.Test.{processId}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200);
                return new TestChannelClient(pipe);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(100);
            }
        }

        return null;
    }

    /// <summary>命令を送る。答えの ok が false なら例外にする。</summary>
    public async Task<JsonObject> SendAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null)
    {
        JsonObject request = args?.DeepClone().AsObject() ?? new JsonObject();
        request["cmd"] = cmd;
        await _lock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), cts.Token);
            string? line = await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("The app closed the test channel.");
            JsonObject response = JsonNode.Parse(line)!.AsObject();
            if (response["ok"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException($"Test command '{cmd}' failed: {response["error"]}");
            }

            return response;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
    }
}
