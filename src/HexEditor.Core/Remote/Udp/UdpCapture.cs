using System.Net;
using System.Net.Sockets;

namespace HexEditor.Core.Remote.Udp;

/// <summary>UDP の受信の設定 (ENG-42 の仕様 1)。</summary>
public sealed record UdpCaptureSettings
{
    /// <summary>保持する上限の既定値 (64 MiB)。</summary>
    public const long DefaultLimit = 64L * 1024 * 1024;

    /// <summary>保持する上限の最小値 (1 MiB)。</summary>
    public const long MinimumLimit = 1024L * 1024;

    /// <summary>待ち受けるアドレス (既定 127.0.0.1)。</summary>
    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>ポート (1〜65535)。</summary>
    public required int Port { get; init; }

    /// <summary>保持する上限 (1 MiB〜エンジンのメモリ上限)。</summary>
    public long Limit { get; init; } = DefaultLimit;

    /// <summary>メモリに置く上限 (超えた分は一時ファイル)。</summary>
    public long MemoryLimit { get; init; } = DefaultLimit;

    /// <summary>超えた分を置く一時フォルダ。</summary>
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "HexEditor", "udp");

    /// <summary>`127.0.0.1` / `::1` 以外で待ち受ける (開始前に Windows ファイアウォールの確認が出ることを示す。仕様 7)。</summary>
    public bool NeedsFirewallNotice => !IPAddress.IsLoopback(Address);

    /// <summary>入力の検査 (ポートの範囲と上限の範囲)。問題がなければ null。</summary>
    public static string? Validate(int port, long limit, long engineLimit) =>
        port is < 1 or > 65535 ? "port" : limit < MinimumLimit || limit > Math.Max(MinimumLimit, engineLimit) ? "limit" : null;
}

/// <summary>受信したデータグラム 1 つ (ENG-42 の仕様 2)。<see cref="Position"/> は受信開始からの通算の位置。</summary>
public sealed record UdpPacket(int Number, long Position, int Length, DateTimeOffset ReceivedAt, IPEndPoint Source)
{
    public long End => Position + Length;
}

/// <summary>受信の状態。</summary>
public enum UdpCaptureState
{
    Receiving,
    Paused,

    /// <summary>上限に達したため記録を止めた (ENG-42 の仕様 4)。</summary>
    LimitReached,

    /// <summary>止めた (閉じた・受信のエラー)。</summary>
    Stopped,
}

/// <summary>ポートが他のアプリに使われている (ENG-42 の「エラー」)。</summary>
public sealed class UdpPortInUseException(int port, Exception inner) : IOException($"Port {port} is in use.", inner)
{
    public int Port { get; } = port;
}

/// <summary>
/// UDP の受信 (ENG-42)。指定したアドレス・ポートで受信したデータグラムを受信順に追記する。受信だけを行い、送信はしない (仕様 8)。
/// 上限に達したら記録を止める (<see cref="LimitReached"/>)。<see cref="DiscardOldData"/> では先頭から捨てて続ける (仕様 4)。
/// 受信はスレッドプールで行い、<see cref="DataReceived"/> は任意のスレッドから呼ばれる。
/// </summary>
public sealed class UdpCapture : IDisposable
{
    /// <summary>
    /// 保持するデータグラムの数の上限 (長さ 0 のデータグラムはバイト数の上限に数えられないため、件数でもメモリを抑える。1 件あたり約 150 バイト)。
    /// 超えたら、バイト数の上限と同じく記録を止める (古いデータを捨てて続ける設定では、古いものから捨てる)。
    /// </summary>
    public const int MaxPackets = 262_144;

    private readonly object _lock = new();

    // 保持しているパケットは _packets[_head..] (先頭から捨てるときは _head を進め、半分を超えたら詰める。捨てるたびに一覧を詰め直さない)。
    private readonly List<UdpPacket> _packets = [];
    private int _head;
    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _socket;
    private readonly TimeProvider _time;
    private Task? _loop;
    private int _nextNumber;
    private long _received;
    private bool _discardOld;
    private int _disposed;
    private UdpCaptureState _state = UdpCaptureState.Receiving;

    private UdpCapture(UdpCaptureSettings settings, Socket socket, TimeProvider time)
    {
        Settings = settings;
        _socket = socket;
        _time = time;
        Store = new AppendStore(Math.Min(settings.MemoryLimit, settings.Limit), settings.TempDirectory);
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    /// <summary>
    /// 待ち受けを始める。ポートが使用中なら <see cref="UdpPortInUseException"/>、権限・ファイアウォールなどで受信できなければ
    /// <see cref="SocketException"/> (OS の理由)。
    /// </summary>
    public static UdpCapture Start(UdpCaptureSettings settings, TimeProvider? time = null)
    {
        var socket = new Socket(settings.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // 他のアプリと同じポートを共有しない (使用中を検出する)。
            socket.ExclusiveAddressUse = true;
            if (settings.Address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            // 受信の取りこぼしを減らすため、OS の受信のバッファを大きくする (受信を UI の更新より先に済ませる)。
            socket.ReceiveBufferSize = 4 * 1024 * 1024;
            socket.Bind(new IPEndPoint(settings.Address, settings.Port));
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
            && IsInUse(ex, settings))
        {
            socket.Dispose();
            throw new UdpPortInUseException(settings.Port, ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var capture = new UdpCapture(settings, socket, time ?? TimeProvider.System);
        capture._loop = Task.Run(capture.ReceiveLoopAsync);
        return capture;
    }

    // Windows は排他で使われているポートへの Bind を WSAEACCES にすることがある。権限の問題と区別するため、AddressAlreadyInUse は常に使用中、
    // AccessDenied はポートを別のソケットが使っているときだけ使用中とする。
    private static bool IsInUse(SocketException ex, UdpCaptureSettings settings)
    {
        if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }

        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == settings.Port);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    public UdpCaptureSettings Settings { get; }

    /// <summary>実際に待ち受けているアドレスとポート。</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>受信したデータ。</summary>
    public AppendStore Store { get; }

    public UdpCaptureState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>古いデータを捨てて続けている (ENG-42 の仕様 4)。</summary>
    public bool DiscardOld
    {
        get
        {
            lock (_lock)
            {
                return _discardOld;
            }
        }
    }

    /// <summary>記録したデータグラムの数 (捨てたものを含む。ステータスバーの受信数)。</summary>
    public long ReceivedCount
    {
        get
        {
            lock (_lock)
            {
                return _received;
            }
        }
    }

    /// <summary>受信のエラー (止まった理由)。</summary>
    public Exception? Error { get; private set; }

    /// <summary>データグラムを記録した (任意のスレッド)。</summary>
    public event EventHandler? DataReceived;

    /// <summary>上限に達して記録を止めた (1 回だけ。任意のスレッド)。</summary>
    public event EventHandler? LimitReached;

    /// <summary>状態が変わった (一時停止・再開・停止。任意のスレッド)。</summary>
    public event EventHandler? StateChanged;

    /// <summary>保持しているパケットの一覧 (捨てたパケットを除く。受信順)。</summary>
    public IReadOnlyList<UdpPacket> Packets
    {
        get
        {
            lock (_lock)
            {
                return _packets.GetRange(_head, _packets.Count - _head);
            }
        }
    }

    /// <summary>保持しているパケットの数 (一覧を写さずに数える。パケットの一覧のパネルの仮想化に使う)。</summary>
    public int PacketCount
    {
        get
        {
            lock (_lock)
            {
                return _packets.Count - _head;
            }
        }
    }

    /// <summary><paramref name="index"/> 番目のパケット (範囲外なら null)。</summary>
    public UdpPacket? PacketAtIndex(int index)
    {
        lock (_lock)
        {
            return index >= 0 && index < _packets.Count - _head ? _packets[_head + index] : null;
        }
    }

    /// <summary>位置を含むパケット。</summary>
    public UdpPacket? PacketAt(long position)
    {
        lock (_lock)
        {
            int lo = _head, hi = _packets.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                UdpPacket p = _packets[mid];
                if (position < p.Position)
                {
                    hi = mid - 1;
                }
                else if (position >= p.End)
                {
                    lo = mid + 1;
                }
                else
                {
                    return p;
                }
            }

            return null;
        }
    }

    /// <summary>一時停止 (受信したデータグラムを記録しない。ENG-42 の仕様 5)。</summary>
    public void Pause() => SetState(s => s == UdpCaptureState.Receiving ? UdpCaptureState.Paused : s);

    /// <summary>再開。</summary>
    public void Resume() => SetState(s => s == UdpCaptureState.Paused ? UdpCaptureState.Receiving : s);

    /// <summary>消去 (受信したデータとパケットの一覧を捨て、通算の位置を 0 に戻す。上限で止まっていたら再開する)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Store.Clear();
            _packets.Clear();
            _head = 0;
            _received = 0;
            _nextNumber = 0;
            if (_state == UdpCaptureState.LimitReached)
            {
                _state = UdpCaptureState.Receiving;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        DataReceived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「古いデータを捨てて続ける」(ENG-42 の仕様 4)。以後、上限を超える分は先頭から捨てる。</summary>
    public void DiscardOldData()
    {
        lock (_lock)
        {
            _discardOld = true;
            if (_state == UdpCaptureState.LimitReached)
            {
                _state = UdpCaptureState.Receiving;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(Func<UdpCaptureState, UdpCaptureState> change)
    {
        bool changed;
        lock (_lock)
        {
            UdpCaptureState next = change(_state);
            changed = next != _state;
            _state = next;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[65536];
        EndPoint any = new IPEndPoint(Settings.Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP の到達不能の通知・大きすぎるデータグラム: 受信は続ける。
                continue;
            }
            catch (SocketException ex)
            {
                Error = ex;
                SetState(_ => UdpCaptureState.Stopped);
                break;
            }

            Record(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
        }
    }

    /// <summary>データグラムを 1 つ記録する (テストからも呼べる)。</summary>
    internal void Record(ReadOnlySpan<byte> data, IPEndPoint source)
    {
        bool limit = false;
        lock (_lock)
        {
            if (_state != UdpCaptureState.Receiving)
            {
                return;
            }

            long retained = Store.Length;
            bool overBytes = retained + data.Length > Settings.Limit;
            bool overCount = _packets.Count - _head >= MaxPackets;
            if (overBytes || overCount)
            {
                if (!_discardOld || data.Length > Settings.Limit)
                {
                    // 上限に達した: 記録を止める (このデータグラムも記録しない)。
                    _state = UdpCaptureState.LimitReached;
                    limit = true;
                }
                else
                {
                    if (overBytes)
                    {
                        Store.DiscardFront(retained + data.Length - Settings.Limit);
                    }

                    if (overCount)
                    {
                        // 件数の上限: 最も古いデータグラムの終わりまで捨てる。
                        UdpPacket oldest = _packets[_head];
                        Store.DiscardFront(Math.Max(0, oldest.End - Store.Start));
                        _head++;
                    }

                    DropDiscardedPackets();
                }
            }

            if (!limit)
            {
                long at = Store.Append(data);
                _packets.Add(new UdpPacket(++_nextNumber, at, data.Length, _time.GetUtcNow(), source));
                _received++;
            }
        }

        if (limit)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            LimitReached?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            DataReceived?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>捨てたデータの中のパケットを一覧から外す (ロックの中で呼ぶ)。</summary>
    private void DropDiscardedPackets()
    {
        long start = Store.Start;
        while (_head < _packets.Count && _packets[_head].End <= start && _packets[_head].Position < start)
        {
            _head++;
        }

        if (_head > 4096 && _head > _packets.Count / 2)
        {
            _packets.RemoveRange(0, _head);
            _head = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _socket.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        SetState(_ => UdpCaptureState.Stopped);
        Store.Dispose();
        _cts.Dispose();
    }
}
