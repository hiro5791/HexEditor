using HexEditor.Core.Sources;

namespace HexEditor.Core.Remote.Gdb;

/// <summary>GDB サーバーへの接続の指定 (ENG-40 の仕様 1)。</summary>
public sealed record GdbEndpoint
{
    /// <summary>1 つの要求の待ち時間の上限の既定値 (ENG-40 の仕様 8)。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1234;

    /// <summary>アドレスの幅 (32 / 64。既定 64)。</summary>
    public int AddressBits { get; init; } = 64;

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>ドキュメントの長さ (32 bit なら 4 GiB、64 bit なら 2^63 − 1。ENG-40 の仕様 2)。</summary>
    public long AddressSpaceLength => AddressBits == 32 ? 1L << 32 : long.MaxValue;

    /// <summary>表示用の「ホスト:ポート」。</summary>
    public string Display => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>セッションに記録する形 (<c>gdb://ホスト:ポート/?bits=64&amp;readonly=1</c>。ENG-40 の仕様 9。復元しても自動で接続しない)。</summary>
    public string ToUri(bool readOnly) =>
        $"gdb://{Display}/?bits={AddressBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}&readonly={(readOnly ? 1 : 0)}";

    /// <summary><see cref="ToUri"/> の形を読む。形が違えば null。</summary>
    public static (GdbEndpoint Endpoint, bool ReadOnly)? TryParseUri(string? text)
    {
        if (text is null || !text.StartsWith("gdb://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Port <= 0)
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        int bits = query["bits"] == "32" ? 32 : 64;
        return (new GdbEndpoint { Host = uri.Host.Trim('[', ']'), Port = uri.Port, AddressBits = bits }, query["readonly"] != "0");
    }
}

/// <summary>
/// GDB サーバーの対象のメモリのデータソース (ENG-40)。オフセット = 対象のアドレス。長さ固定 (アドレス空間全体)。データは変わりうる
/// (<see cref="SourceCapabilities.IsVolatile"/>)。対象がエラーを返した範囲は「アクセス不可」の読めない範囲にする (仕様 4)。接続が切れたら
/// 「切断」にし (<see cref="SourceChangeKind.Disconnected"/>)、<see cref="ReconnectAsync"/> で読み込みを再開できる (未保存の変更は
/// ドキュメントに残る。仕様 8)。読み込みは非同期 (ブロックキャッシュのワーカーから呼ばれる)。
/// </summary>
public sealed class GdbByteSource : ByteSourceBase
{
    private readonly Func<CancellationToken, Task<GdbClient>> _connect;
    private readonly object _lock = new();
    private GdbClient? _client;
    private RemoteConnectionState _state;
    private readonly bool _writable;

    public GdbByteSource(GdbEndpoint endpoint, GdbClient client, bool writable, Func<CancellationToken, Task<GdbClient>>? connect = null)
    {
        Endpoint = endpoint;
        _client = client;
        _writable = writable;
        _state = RemoteConnectionState.Connected;
        _connect = connect ?? (ct => GdbClient.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Timeout, ct));
        Length = endpoint.AddressSpaceLength;
    }

    /// <summary>接続して開く (ENG-40 の仕様 1)。接続できなければ例外 (呼び出し側が「GDB サーバーに接続できませんでした」と示す)。</summary>
    public static async Task<GdbByteSource> ConnectAsync(GdbEndpoint endpoint, bool writable, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<GdbClient>>? connect = null)
    {
        connect ??= ct => GdbClient.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Timeout, ct);
        GdbClient client = await connect(cancellationToken).ConfigureAwait(false);
        return new GdbByteSource(endpoint, client, writable, connect);
    }

    public GdbEndpoint Endpoint { get; }

    public override string DisplayName => $"GDB {Endpoint.Display}";

    public override string Identity => $"gdb:{Endpoint.Host.ToLowerInvariant()}:{Endpoint.Port}";

    public override long Length { get; }

    public override SourceCapabilities Capabilities =>
        SourceCapabilities.IsVolatile | (_writable && State == RemoteConnectionState.Connected ? SourceCapabilities.CanWrite : 0);

    /// <summary>書き込みを許可して開いた (「読み取り専用で開く」をオフ)。</summary>
    public bool Writable => _writable;

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

    /// <summary>接続の状態が変わった (任意のスレッド)。</summary>
    public event EventHandler? StateChanged;

    /// <summary>切断した理由 (最後のエラー)。</summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// 接続し直す (「再接続」ボタン。ENG-40 の仕様 8)。成功したら <see cref="RemoteConnectionState.Connected"/> にする。
    /// 呼び出し側はキャッシュを捨てて読み直す (<see cref="Engine.Document.RefreshFromSource"/>)。
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(RemoteConnectionState.Connecting);
        try
        {
            GdbClient client = await _connect(cancellationToken).ConfigureAwait(false);
            GdbClient? old;
            lock (_lock)
            {
                old = _client;
                _client = client;
                _state = RemoteConnectionState.Connected;
            }

            old?.Dispose();
            LastError = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LastError = ex;
            SetState(RemoteConnectionState.Disconnected);
            throw;
        }
    }

    /// <summary>
    /// 接続先が接続を閉じていないかを確かめる (通信しない。UI が数秒ごとに呼び、接続先の停止を「切断」として早く示す。ENG-40 の仕様 8)。
    /// 閉じていたら切断にして true。
    /// </summary>
    public bool CheckConnection()
    {
        GdbClient? client = ConnectedClient();
        if (client is null || !client.PeerClosed())
        {
            return false;
        }

        MarkDisconnected(new GdbConnectionException("The GDB server closed the connection."));
        return true;
    }

    /// <summary>切断する (利用者の操作・閉じるとき)。</summary>
    public void Disconnect()
    {
        GdbClient? old;
        lock (_lock)
        {
            old = _client;
            _client = null;
        }

        old?.Dispose();
        MarkDisconnected(null);
    }

    private void SetState(RemoteConnectionState state)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MarkDisconnected(Exception? error)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != RemoteConnectionState.Disconnected;
            _state = RemoteConnectionState.Disconnected;
        }

        if (error is not null)
        {
            LastError = error;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            OnChanged(SourceChangeKind.Disconnected);
        }
    }

    private GdbClient? ConnectedClient()
    {
        lock (_lock)
        {
            return _state == RemoteConnectionState.Connected ? _client : null;
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        byte[] temp = new byte[buffer.Length];
        ReadResult result = ReadAsync(offset, temp).AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, result.BytesReturned).CopyTo(buffer);
        return result;
    }

    public override async ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        Memory<byte> target = buffer[..length];
        var bad = new List<UnreadableRange>();
        int pos = 0;
        while (pos < length)
        {
            GdbClient? client = ConnectedClient();
            if (client is null)
            {
                target.Span[pos..].Clear();
                Add(bad, new UnreadableRange(offset + pos, length - pos, UnreadableReason.Disconnected));
                break;
            }

            int n = Math.Min(client.MaxReadLength, length - pos);
            try
            {
                int got = await client.ReadMemoryAsync((ulong)(offset + pos), target.Slice(pos, n), cancellationToken).ConfigureAwait(false);
                if (got < n)
                {
                    // 短い応答: 残りは読めない (対象の境界)。
                    target.Span.Slice(pos + got, n - got).Clear();
                    Add(bad, new UnreadableRange(offset + pos + got, n - got, UnreadableReason.AccessDenied));
                }
            }
            catch (GdbErrorReplyException ex)
            {
                // 対象がエラーを返した範囲は「アクセス不可」(ENG-40 の仕様 4)。
                target.Span.Slice(pos, n).Clear();
                Add(bad, new UnreadableRange(offset + pos, n, UnreadableReason.AccessDenied, ex.Code));
            }
            catch (GdbConnectionException ex)
            {
                MarkDisconnected(ex);
                continue;
            }

            pos += n;
        }

        return new ReadResult(length, bad.Count == 0 ? null : bad);
    }

    private static void Add(List<UnreadableRange> list, UnreadableRange range)
    {
        if (list.Count > 0 && list[^1].End == range.Offset && list[^1].Reason == range.Reason && list[^1].ErrorCode == range.ErrorCode)
        {
            list[^1] = list[^1] with { Length = list[^1].Length + range.Length };
        }
        else
        {
            list.Add(range);
        }
    }

    /// <summary>
    /// 書く (保存・即時書き込み。ENG-40 の仕様 6)。失敗したら例外 (<see cref="GdbErrorReplyException"/>: 対象のエラー、
    /// <see cref="GdbConnectionException"/>: 切断)。
    /// </summary>
    public async Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!_writable)
        {
            throw new NotSupportedException("The GDB target was opened read-only.");
        }

        for (int pos = 0; pos < data.Length;)
        {
            GdbClient client = ConnectedClient() ?? throw new GdbConnectionException("Disconnected.");
            int n = Math.Min(client.MaxWriteLength, data.Length - pos);
            try
            {
                await client.WriteMemoryAsync((ulong)(offset + pos), data.Slice(pos, n), cancellationToken).ConfigureAwait(false);
            }
            catch (GdbConnectionException ex)
            {
                MarkDisconnected(ex);
                throw;
            }

            pos += n;
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> data) => WriteAsync(offset, data.ToArray()).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GdbClient? client;
            lock (_lock)
            {
                client = _client;
                _client = null;
                _state = RemoteConnectionState.Disconnected;
            }

            client?.Dispose();
        }
    }
}
