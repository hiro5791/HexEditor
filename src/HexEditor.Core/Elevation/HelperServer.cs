namespace HexEditor.Core.Elevation;

/// <summary>補助プロセスの終わり方。</summary>
public enum HelperServerExit
{
    /// <summary>UI のプロセスが接続を閉じた (終了した)。</summary>
    Disconnected,

    /// <summary>許可リストにない要求・不正なメッセージを受け取った (切断して終了する)。</summary>
    Rejected,

    /// <summary>開いているハンドルが 0 のまま、決めた時間がたった (ENG-28 の仕様 6)。</summary>
    Idle,

    /// <summary>止めるよう指示された (UI のプロセスの終了の通知など)。</summary>
    Cancelled,
}

/// <summary>
/// 補助プロセスの要求の受け付け (ENG-28 の仕様 3・4)。パイプ (またはテスト用のストリーム) から要求を読み、
/// <see cref="PrivilegedOperations"/> で実行して応答を返す。要求は並行して処理し、応答の順序は要求の順序と一致しなくてよい。
/// 終わるときは開いているハンドルを閉じ、ロックしたボリュームのロックを解除する。
/// </summary>
public sealed class HelperServer(Stream stream, PrivilegedOperations operations, Action<string>? log = null)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _inFlight = new(HelperProtocol.MaxInFlight, HelperProtocol.MaxInFlight);
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>開いているハンドルが 0 になってから終了するまでの時間。null なら終了しない (「アプリの終了まで残す」)。</summary>
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public async Task<HelperServerExit> RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exit = HelperServerExit.Disconnected;
        Task idle = WatchIdleAsync(stop, () => exit = HelperServerExit.Idle);
        var running = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                (uint Length, uint Id, uint Word, byte[] Body)? frame = await HelperProtocol.ReadFrameAsync(stream, stop.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                (_, uint id, uint word, byte[] body) = frame.Value;
                ushort command = (ushort)(word & 0xFFFF);
                ushort flags = (ushort)(word >> 16);
                if (!HelperProtocol.IsKnown(command))
                {
                    _log($"Rejected: unknown command 0x{command:X4}");
                    exit = HelperServerExit.Rejected;
                    break;
                }

                await _inFlight.WaitAsync(stop.Token).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        (uint status, byte[] response) = operations.Execute(command, flags, body);
                        await SendAsync(HelperProtocol.Response(id, status, response), stop.Token).ConfigureAwait(false);
                    }
                    catch (HelperProtocolException)
                    {
                        exit = HelperServerExit.Rejected;
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _inFlight.Release();
                    }
                }));
            }
        }
        catch (HelperProtocolException ex)
        {
            _log("Rejected: " + ex.Message);
            exit = HelperServerExit.Rejected;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (exit == HelperServerExit.Disconnected && cancellationToken.IsCancellationRequested)
            {
                exit = HelperServerExit.Cancelled;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }

            await idle.ConfigureAwait(false);

            // 開いているハンドルを閉じ、ロックを解除する (仕様 6)。切断の前に行う。
            operations.Dispose();
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
            }
        }

        _log($"Helper server stopped ({exit})");
        return exit;
    }

    private async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WatchIdleAsync(CancellationTokenSource stop, Action onIdle)
    {
        if (IdleTimeout is not { } timeout)
        {
            return;
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(10, timeout.TotalMilliseconds / 4))), Time, stop.Token)
                    .ConfigureAwait(false);
                if (operations.IdleSinceUtc is { } since && Time.GetUtcNow().UtcDateTime - since >= timeout)
                {
                    _log("Idle timeout");
                    onIdle();
                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
