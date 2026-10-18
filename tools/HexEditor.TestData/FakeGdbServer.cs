using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HexEditor.TestData;

/// <summary>
/// テスト用の偽の GDB サーバー (ENG-40 のテスト。QEMU の GDB サーバーの代わり)。127.0.0.1 で待ち受け、RSP の <c>qSupported</c>・
/// <c>QStartNoAckMode</c>・<c>?</c>・<c>m</c>・<c>M</c>・<c>X</c> に応える。メモリはページ単位の疎な表で、<see cref="Unreadable"/> の範囲には
/// <c>E14</c> を返す。<see cref="Stop"/> で接続を切って待ち受けをやめ、<see cref="Start"/> で同じポートで再開できる (接続先の停止と再起動)。
/// 外部のホストには接続しない。
/// </summary>
public sealed class FakeGdbServer : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<long, byte[]> _pages = [];
    private readonly List<string> _log = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<TcpClient> _clients = [];

    public FakeGdbServer(int packetSize = 0x1000, bool noAck = true, bool binaryWrite = true)
    {
        PacketSize = packetSize;
        SupportsNoAck = noAck;
        SupportsBinaryWrite = binaryWrite;
    }

    public int PacketSize { get; }

    public bool SupportsNoAck { get; }

    public bool SupportsBinaryWrite { get; }

    /// <summary>読み書きできない範囲 (E14 を返す)。</summary>
    public List<(long Start, long Length)> Unreadable { get; } = [];

    /// <summary>応答を遅らせる (タイムアウトの確認)。</summary>
    public TimeSpan ReplyDelay { get; set; }

    /// <summary>不正な応答を返す (プロトコルのエラーの確認)。</summary>
    public bool Garbage { get; set; }

    public int Port { get; private set; }

    /// <summary>受け取ったパケット (先頭の文字と内容)。</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>読み込み (m) で返したバイト数の合計。</summary>
    public long BytesRead { get; private set; }

    public void Start(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port == 0 ? Port : port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    /// <summary>待ち受けをやめ、接続を切る (QEMU のプロセスの終了の代わり)。</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        lock (_lock)
        {
            foreach (TcpClient c in _clients)
            {
                c.Dispose();
            }

            _clients.Clear();
        }
    }

    public void Write(long address, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                long a = address + i;
                if (!_pages.TryGetValue(a >> 12, out byte[]? page))
                {
                    page = new byte[4096];
                    _pages[a >> 12] = page;
                }

                page[a & 0xFFF] = data[i];
            }
        }
    }

    public byte[] Read(long address, int length)
    {
        byte[] result = new byte[length];
        lock (_lock)
        {
            for (int i = 0; i < length; i++)
            {
                long a = address + i;
                if (_pages.TryGetValue(a >> 12, out byte[]? page))
                {
                    result[i] = page[a & 0xFFF];
                }
            }
        }

        return result;
    }

    private bool IsUnreadable(long address, long length) => Unreadable.Any(u => address < u.Start + u.Length && u.Start < address + length);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            lock (_lock)
            {
                _clients.Add(client);
            }

            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        bool noAck = false;
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] one = new byte[1];
            while (!token.IsCancellationRequested)
            {
                // パケットを 1 つ読む。
                int b;
                do
                {
                    if (await stream.ReadAsync(one, token) == 0)
                    {
                        return;
                    }

                    b = one[0];
                }
                while (b != '$');

                var raw = new List<byte>();
                while (true)
                {
                    if (await stream.ReadAsync(one, token) == 0)
                    {
                        return;
                    }

                    if (one[0] == '#')
                    {
                        break;
                    }

                    raw.Add(one[0]);
                }

                byte[] cs = new byte[2];
                await stream.ReadExactlyAsync(cs, token);
                if (!noAck)
                {
                    await stream.WriteAsync("+"u8.ToArray(), token);
                }

                byte[] payload = Unescape(raw);
                string text = Encoding.Latin1.GetString(payload);
                lock (_lock)
                {
                    _log.Add(text.Length > 64 ? text[..64] : text);
                }

                if (ReplyDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ReplyDelay, token);
                }

                string reply = Handle(text, payload, ref noAck, out bool enableNoAckAfter);
                byte[] frame = Frame(Garbage ? "zz" : reply);
                await stream.WriteAsync(frame, token);
                if (!noAck || enableNoAckAfter)
                {
                    // 確認応答を読む (no-ack に切り替える応答の確認応答まで)。
                    await stream.ReadExactlyAsync(one, token);
                }

                if (enableNoAckAfter)
                {
                    noAck = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private string Handle(string text, byte[] payload, ref bool noAck, out bool enableNoAckAfter)
    {
        enableNoAckAfter = false;
        if (text.StartsWith("qSupported", StringComparison.Ordinal))
        {
            return $"PacketSize={PacketSize:x}" + (SupportsNoAck ? ";QStartNoAckMode+" : string.Empty);
        }

        if (text == "QStartNoAckMode")
        {
            if (!SupportsNoAck)
            {
                return string.Empty;
            }

            enableNoAckAfter = !noAck;
            return "OK";
        }

        if (text == "?")
        {
            return "S05";
        }

        if (text.StartsWith('m'))
        {
            string[] parts = text[1..].Split(',');
            long address = long.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int length = int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (length * 2 > PacketSize)
            {
                return "E01";
            }

            if (IsUnreadable(address, length))
            {
                return "E14";
            }

            BytesRead += length;
            return Convert.ToHexString(Read(address, length)).ToLowerInvariant();
        }

        if (text.StartsWith('M') || text.StartsWith('X'))
        {
            if (text[0] == 'X' && !SupportsBinaryWrite)
            {
                return string.Empty;
            }

            int colon = Array.IndexOf(payload, (byte)':');
            string[] parts = text[1..colon].Split(',');
            long address = long.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int length = int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (length > 0 && IsUnreadable(address, length))
            {
                return "E14";
            }

            byte[] data = text[0] == 'X' ? payload[(colon + 1)..] : Convert.FromHexString(text[(colon + 1)..]);
            if (data.Length != length)
            {
                return "E02";
            }

            Write(address, data);
            return "OK";
        }

        return string.Empty;
    }

    private static byte[] Unescape(List<byte> raw)
    {
        var result = new List<byte>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            result.Add(raw[i] == '}' && i + 1 < raw.Count ? (byte)(raw[++i] ^ 0x20) : raw[i]);
        }

        return [.. result];
    }

    private static byte[] Frame(string reply)
    {
        byte sum = 0;
        foreach (char c in reply)
        {
            sum += (byte)c;
        }

        return Encoding.Latin1.GetBytes($"${reply}#{sum:x2}");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
