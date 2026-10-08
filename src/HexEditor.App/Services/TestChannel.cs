#if HEX_TEST_HOOKS
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.App.Services;

/// <summary>
/// テスト用の命令の通り道 (テスト用のビルドだけ)。名前付きパイプ <c>HexEditor.Test.&lt;プロセス ID&gt;</c> で、
/// 1 行に 1 つの JSON の命令を受け取り、UI スレッドで実行して 1 行の JSON で答える。
/// フォーカス・マウス・システムのキーボードやクリップボードを使わずに、キー入力の注入・状態の取得・描画内容の読み出しを行う
/// (テスト方針 7.2)。命令の一覧は MainWindow.TestMenu.cs の HandleTestCommandAsync。
/// </summary>
public static class TestChannel
{
    public static string PipeName(int processId) => $"HexEditor.Test.{processId}";

    private static int _started;

    /// <summary>通り道を開く。<paramref name="handler"/> は UI スレッドで呼ぶ。</summary>
    public static void Start(Func<JsonObject, Task<JsonObject>> handler)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        string name = PipeName(Environment.ProcessId);
        var thread = new Thread(() => Listen(name, handler)) { IsBackground = true, Name = "TestChannel" };
        thread.Start();
        AppLog.Info($"Test channel: {name}");
    }

    private static void Listen(string name, Func<JsonObject, Task<JsonObject>> handler)
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
            }
            catch (IOException ex)
            {
                AppLog.Error($"Test channel: {ex.Message}");
                Thread.Sleep(500);
                continue;
            }

            // 接続ごとに別のタスクで扱い、次の接続を待つ。
            _ = Task.Run(() => ServeAsync(pipe, handler));
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<JsonObject, Task<JsonObject>> handler)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    JsonObject response;
                    try
                    {
                        JsonObject request = JsonNode.Parse(line)?.AsObject() ?? new JsonObject();
                        response = await RunOnUiThreadAsync(() => handler(request));
                    }
                    catch (Exception ex)
                    {
                        // 失敗の原因を追えるよう、スタックトレースをログに残す。
                        AppLog.Warning($"Test command failed: {ex}");
                        response = new JsonObject { ["ok"] = false, ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
                    }

                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (IOException)
            {
                // 相手が切断した。
            }
        }
    }

    private static Task<JsonObject> RunOnUiThreadAsync(Func<Task<JsonObject>> work)
    {
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = App.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        if (!queued)
        {
            done.SetException(new InvalidOperationException("The UI thread is not running."));
        }

        return done.Task;
    }
}
#endif
