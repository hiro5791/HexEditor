using System.Collections.Concurrent;

namespace HexEditor.Core.Elevation;

/// <summary>補助プロセスとの接続が切れた (補助プロセスの異常終了。ENG-28 の仕様 7)。</summary>
public sealed class HelperDisconnectedException() : IOException("The elevated helper process has exited.");

/// <summary>補助プロセスの応答が時間内に来なかった (ENG-28 の仕様 5)。</summary>
public sealed class HelperTimeoutException(HelperCommand command) : TimeoutException($"The elevated helper did not answer {command} in time.");

/// <summary>
/// UI のプロセス側の要求の送信 (ENG-28 の仕様 3・5)。要求に ID を付けて送り、応答を ID で対応付ける。最大 16 個まで応答を待たずに送る。
/// 1 つの要求の待ち時間の上限 (既定 30 秒) を超えたら <see cref="HelperTimeoutException"/>。接続が切れたら待っている要求はすべて
/// <see cref="HelperDisconnectedException"/> になり、<see cref="Disconnected"/> を通知する。
/// </summary>
public sealed class HelperClient : IAsyncDisposable, IDisposable
{
    private readonly Stream _stream;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(uint Status, byte[] Body)>> _pending = new();
    private readonly SemaphoreSlim _inFlight = new(HelperProtocol.MaxInFlight, HelperProtocol.MaxInFlight);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private int _nextId;
    private int _disconnected;

    public HelperClient(Stream stream)
    {
        _stream = stream;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>1 つの要求の待ち時間の上限 (設定で 5〜300 秒)。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;

    /// <summary>接続が切れた (補助プロセスの終了)。スレッドプールから 1 回だけ呼ばれる。</summary>
    public event EventHandler? Disconnected;

    /// <summary>送った要求の数 (テスト・診断用)。</summary>
    public int RequestCount => Volatile.Read(ref _nextId);

    public async Task<(uint Status, byte[] Body)> SendAsync(HelperCommand command, ushort flags, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new HelperDisconnectedException();
        }

        await _inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        uint id = (uint)Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<(uint, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (!IsConnected)
            {
                throw new HelperDisconnectedException();
            }

            byte[] frame = HelperProtocol.Request(id, command, flags, body.Span);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                OnDisconnected();
                throw new HelperDisconnectedException();
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HelperTimeoutException(command);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _inFlight.Release();
        }
    }

    /// <summary>同期版 (データソースの読み込みはスレッドプールで同期的に呼ばれる)。</summary>
    public (uint Status, byte[] Body) Send(HelperCommand command, ushort flags, ReadOnlySpan<byte> body) =>
        SendAsync(command, flags, body.ToArray()).GetAwaiter().GetResult();

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                (uint Length, uint Id, uint Word, byte[] Body)? frame = await HelperProtocol.ReadFrameAsync(_stream, _stop.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (_pending.TryGetValue(frame.Value.Id, out TaskCompletionSource<(uint, byte[])>? completion))
                {
                    completion.TrySetResult((frame.Value.Word, frame.Value.Body));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or HelperProtocolException)
        {
        }

        OnDisconnected();
    }

    private void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        foreach (TaskCompletionSource<(uint, byte[])> completion in _pending.Values)
        {
            completion.TrySetException(new HelperDisconnectedException());
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
