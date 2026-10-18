namespace HexEditor.Core.Remote.Sftp;

/// <summary>
/// SFTP の接続 1 つ (ENG-41)。接続先・資格情報 (メモリの中だけ。再接続に使う)・セッションを持つ。切断したら
/// <see cref="RemoteConnectionState.Disconnected"/> にし、<see cref="ReconnectAsync"/> で接続し直す。複数のファイル (データソース) が共有できる。
/// </summary>
public sealed class SftpConnection : IDisposable
{
    private readonly object _lock = new();
    private readonly ISftpConnector _connector;
    private readonly SftpCredentials _credentials;
    private ISftpSession? _session;
    private RemoteConnectionState _state;
    private int _generation;

    private SftpConnection(SftpEndpoint endpoint, SftpCredentials credentials, ISftpConnector connector, KnownHosts hostKeys, TimeSpan timeout,
        ISftpSession session)
    {
        Endpoint = endpoint;
        _credentials = credentials;
        _connector = connector;
        HostKeys = hostKeys;
        Timeout = timeout;
        _session = session;
        _state = RemoteConnectionState.Connected;
    }

    /// <summary>1 つの要求の待ち時間の上限の既定値。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>接続する。ホスト鍵を信頼していなければ <see cref="HostKeyRejectedException"/> (認証の前)。</summary>
    public static async Task<SftpConnection> ConnectAsync(SftpEndpoint endpoint, SftpCredentials credentials, ISftpConnector connector,
        KnownHosts hostKeys, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        TimeSpan t = timeout ?? DefaultTimeout;
        ISftpSession session = await connector.ConnectAsync(endpoint, credentials, hostKeys, t, cancellationToken).ConfigureAwait(false);
        return new SftpConnection(endpoint, credentials, connector, hostKeys, t, session);
    }

    public SftpEndpoint Endpoint { get; }

    public KnownHosts HostKeys { get; }

    public TimeSpan Timeout { get; }

    /// <summary>接続に使った認証方式 (資格情報の保存の確認など)。</summary>
    public SftpAuthMethod Method => _credentials.Method;

    public RemoteConnectionState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>接続し直した回数 (データソースは開いたファイルのハンドルを開き直す)。</summary>
    public int Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>状態が変わった (任意のスレッド)。</summary>
    public event EventHandler? StateChanged;

    /// <summary>今のセッション。切断していれば <see cref="SftpException"/> (<see cref="SftpFailure.Disconnected"/>)。</summary>
    public ISftpSession Session
    {
        get
        {
            lock (_lock)
            {
                return _state == RemoteConnectionState.Connected && _session is { IsConnected: true } s
                    ? s
                    : throw new SftpException(SftpFailure.Disconnected, "The SFTP connection is closed.");
            }
        }
    }

    /// <summary>切断したことにする (読み書きで接続が切れたのを見つけたとき)。</summary>
    public void MarkDisconnected()
    {
        ISftpSession? old;
        bool changed;
        lock (_lock)
        {
            changed = _state != RemoteConnectionState.Disconnected;
            _state = RemoteConnectionState.Disconnected;
            old = _session;
            _session = null;
        }

        try
        {
            old?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>接続し直す (自動の 1 回と「再接続」ボタン。ENG-41 の仕様 7)。</summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _state = RemoteConnectionState.Connecting;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            ISftpSession session = await _connector.ConnectAsync(Endpoint, _credentials, HostKeys, Timeout, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _session?.Dispose();
                _session = session;
                _state = RemoteConnectionState.Connected;
                _generation++;
            }
        }
        catch
        {
            lock (_lock)
            {
                _state = RemoteConnectionState.Disconnected;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private int _users;

    /// <summary>このデータソースが接続を使い始めた (データソースを開いたとき)。</summary>
    internal void AddUser() => Interlocked.Increment(ref _users);

    /// <summary>接続を使うデータソースが閉じた。使うものがなくなったら切断する (タブを閉じたとき)。</summary>
    internal void RemoveUser()
    {
        if (Interlocked.Decrement(ref _users) <= 0)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        ISftpSession? old;
        lock (_lock)
        {
            old = _session;
            _session = null;
            _state = RemoteConnectionState.Disconnected;
        }

        old?.Dispose();
    }
}
