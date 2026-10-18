using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace HexEditor.Core.Remote.Gdb;

/// <summary>GDB サーバーとの通信の失敗 (接続が切れた・応答がない)。</summary>
public class GdbConnectionException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>GDB サーバーの応答を解釈できなかった (ENG-40 の「エラー」: 切断して「GDB サーバーの応答を解釈できませんでした」)。</summary>
public sealed class GdbProtocolException(string message) : GdbConnectionException(message);

/// <summary>1 つの要求の待ち時間の上限を超えた (ENG-40 の仕様 8)。</summary>
public sealed class GdbTimeoutException(TimeSpan timeout) : GdbConnectionException($"No response within {timeout.TotalSeconds:0.#} s.");

/// <summary>対象がエラーを返した (<c>Exx</c>。その範囲は「アクセス不可」。ENG-40 の仕様 4)。</summary>
public sealed class GdbErrorReplyException(int code) : IOException($"The target returned error {code:X2}.")
{
    public int Code { get; } = code;
}

/// <summary>
/// GDB のリモートシリアルプロトコル (RSP) のクライアント (ENG-40)。メモリの読み書き (<c>m</c> / <c>M</c> / <c>X</c>) だけを行い、実行の停止・再開・
/// ブレークポイントなどデバッガとしての操作はしない (仕様 7)。要求は 1 つずつ順に送る (スレッドセーフ)。各要求の待ち時間の上限は
/// <see cref="Timeout"/> (既定 5 秒)。上限を超えた・接続が切れた・応答が不正な場合は <see cref="GdbConnectionException"/> で、以後は使えない。
/// </summary>
public sealed class GdbClient : IAsyncDisposable, IDisposable
{
    /// <summary>対象がパケットサイズを報告しない場合の値 (GDB と同じく小さめにする)。</summary>
    public const int DefaultPacketSize = 400;

    /// <summary>1 回の要求の上限 (対象が大きなパケットサイズを報告しても、これより大きくはしない)。</summary>
    public const int MaxPacketSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _one = new byte[1];
    private bool _noAck;
    private bool? _binaryWrite;
    private volatile bool _broken;

    private GdbClient(Stream stream, TimeSpan timeout)
    {
        _stream = stream;
        Timeout = timeout;
    }

    /// <summary>1 つの要求の待ち時間の上限。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>対象が報告したパケットサイズ (<c>qSupported</c> の <c>PacketSize</c>)。</summary>
    public int PacketSize { get; private set; } = DefaultPacketSize;

    /// <summary>接続が使えなくなった (切断・タイムアウト・不正な応答)。</summary>
    public bool IsBroken => _broken;

    /// <summary>
    /// 相手が接続を閉じたか (通信せずに確かめる。要求の合間に、接続先の停止を早く知るため)。要求の途中・確かめられないストリームでは false。
    /// </summary>
    public bool PeerClosed()
    {
        if (_broken)
        {
            return true;
        }

        if (_stream is not OwnedStream owned || !_gate.Wait(0))
        {
            return false;
        }

        try
        {
            Socket socket = owned.Socket;
            bool closed = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            _broken |= closed;
            return closed;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _broken = true;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>1 回の <c>m</c> で読む最大のバイト数 (応答は 16 進で 2 倍になる)。</summary>
    public int MaxReadLength => Math.Max(16, (PacketSize - 8) / 2);

    /// <summary>1 回の <c>M</c> / <c>X</c> で書く最大のバイト数。</summary>
    public int MaxWriteLength => Math.Max(16, (PacketSize - 40) / 2);

    /// <summary>TCP で接続し、対象の機能を問い合わせる。接続できなければ <see cref="SocketException"/> または <see cref="GdbConnectionException"/>。</summary>
    public static async Task<GdbClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(timeout);
                try
                {
                    await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GdbTimeoutException(timeout);
                }
            }

            return await OpenAsync(new OwnedStream(tcp), timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>接続済みのストリームで始める (テストの偽の GDB サーバーにも使う)。</summary>
    public static async Task<GdbClient> OpenAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var client = new GdbClient(stream, timeout);
        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        string supported = await RequestAsync("qSupported:multiprocess+;swbreak+;hwbreak+", cancellationToken).ConfigureAwait(false);
        foreach (string feature in supported.Split(';'))
        {
            if (feature.StartsWith("PacketSize=", StringComparison.Ordinal)
                && int.TryParse(feature.AsSpan(11), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) && size > 0)
            {
                PacketSize = Math.Clamp(size, 64, MaxPacketSize);
            }
        }

        if (supported.Split(';').Contains("QStartNoAckMode+")
            && await RequestAsync("QStartNoAckMode", cancellationToken).ConfigureAwait(false) == "OK")
        {
            _noAck = true;
        }

        // 停止の理由を問い合わせる (応答しない対象もあるため、内容は使わない)。
        await RequestAsync("?", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// メモリを読む (<c>m</c>)。対象がエラーを返したら <see cref="GdbErrorReplyException"/>。対象が要求より短く返した場合は読めた分だけを返す。
    /// <paramref name="length"/> は <see cref="MaxReadLength"/> 以下。
    /// </summary>
    public async Task<int> ReadMemoryAsync(ulong address, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        string reply = await RequestAsync($"m{address:x},{destination.Length:x}", cancellationToken).ConfigureAwait(false);
        if (IsError(reply, out int code))
        {
            throw new GdbErrorReplyException(code);
        }

        if (reply.Length == 0)
        {
            // 空の応答: 対象が m に対応していない。
            throw new GdbErrorReplyException(0);
        }

        if (reply.Length % 2 != 0 || reply.Length / 2 > destination.Length)
        {
            throw Fail(new GdbProtocolException($"Unexpected m reply length {reply.Length}."));
        }

        try
        {
            int n = reply.Length / 2;
            Convert.FromHexString(reply).CopyTo(destination.Span);
            return n;
        }
        catch (FormatException)
        {
            throw Fail(new GdbProtocolException("The m reply is not hexadecimal."));
        }
    }

    /// <summary>
    /// メモリに書く。対象が <c>X</c> (バイナリ) に対応していれば <c>X</c>、なければ <c>M</c> (16 進) を使う (ENG-40 の仕様 3)。
    /// エラーなら <see cref="GdbErrorReplyException"/>。<paramref name="data"/> は <see cref="MaxWriteLength"/> 以下。
    /// </summary>
    public async Task WriteMemoryAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_binaryWrite is null)
        {
            // X に対応しているかを、長さ 0 の X で確かめる (GDB と同じ。空の応答は未対応)。
            string probe = await RequestAsync($"X{address:x},0:", cancellationToken).ConfigureAwait(false);
            _binaryWrite = probe == "OK";
        }

        string reply;
        if (_binaryWrite == true)
        {
            byte[] packet = [.. Encoding.ASCII.GetBytes($"X{address:x},{data.Length:x}:"), .. data.Span];
            reply = await RequestAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            reply = await RequestAsync($"M{address:x},{data.Length:x}:{Convert.ToHexString(data.Span).ToLowerInvariant()}", cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsError(reply, out int code))
        {
            throw new GdbErrorReplyException(code);
        }

        if (reply != "OK")
        {
            throw Fail(new GdbProtocolException($"Unexpected write reply '{Truncate(reply)}'."));
        }
    }

    private static bool IsError(string reply, out int code)
    {
        code = 0;
        return reply.Length == 3 && reply[0] == 'E' && int.TryParse(reply.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    private static string Truncate(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private Task<string> RequestAsync(string packet, CancellationToken cancellationToken) =>
        RequestAsync(Encoding.ASCII.GetBytes(packet), cancellationToken);

    /// <summary>パケットを送って応答を受け取る (1 つずつ。待ち時間の上限つき)。</summary>
    private async Task<string> RequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (_broken)
        {
            throw new GdbConnectionException("The connection is closed.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            try
            {
                await SendAsync(payload, cts.Token).ConfigureAwait(false);
                return await ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Fail(new GdbTimeoutException(Timeout));
            }
            catch (OperationCanceledException)
            {
                // 呼び出し側のキャンセル: 応答の途中で止めた可能性があるため、この接続はもう使えない。
                _broken = true;
                throw;
            }
            catch (IOException ex) when (ex is not GdbConnectionException)
            {
                throw Fail(new GdbConnectionException(ex.Message, ex));
            }
            catch (ObjectDisposedException ex)
            {
                throw Fail(new GdbConnectionException(ex.Message, ex));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private GdbConnectionException Fail(GdbConnectionException ex)
    {
        _broken = true;
        return ex;
    }

    private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = Frame(payload);
        for (int attempt = 0; ; attempt++)
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_noAck)
            {
                return;
            }

            // 確認応答 ('+' / '-') を待つ。'-' なら送り直す。
            while (true)
            {
                int b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (b == '+')
                {
                    return;
                }

                if (b == '-')
                {
                    break;
                }

                if (b == '$')
                {
                    throw Fail(new GdbProtocolException("A packet arrived before the acknowledgement."));
                }
            }

            if (attempt >= 3)
            {
                throw Fail(new GdbProtocolException("The packet was rejected repeatedly."));
            }
        }
    }

    /// <summary><c>$data#cs</c> の形にする (<c>$ # } *</c> はエスケープする)。</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new List<byte>(payload.Length + 4) { (byte)'$' };
        byte sum = 0;
        foreach (byte b in payload)
        {
            if (b is (byte)'$' or (byte)'#' or (byte)'}' or (byte)'*')
            {
                frame.Add((byte)'}');
                frame.Add((byte)(b ^ 0x20));
                sum += (byte)'}';
                sum += (byte)(b ^ 0x20);
            }
            else
            {
                frame.Add(b);
                sum += b;
            }
        }

        frame.Add((byte)'#');
        frame.AddRange(Encoding.ASCII.GetBytes(sum.ToString("x2", CultureInfo.InvariantCulture)));
        return [.. frame];
    }

    private async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // パケットの先頭まで読み飛ばす ('+' の重複・通知 '%' の前の文字など)。
            int b;
            do
            {
                b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }
            while (b != '$' && b != '%');

            bool notification = b == '%';
            var raw = new List<byte>();
            byte sum = 0;
            while ((b = await ReadByteAsync(cancellationToken).ConfigureAwait(false)) != '#')
            {
                raw.Add((byte)b);
                sum += (byte)b;
                if (raw.Count > MaxPacketSize * 4)
                {
                    throw Fail(new GdbProtocolException("The reply is too long."));
                }
            }

            int hi = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            int lo = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            bool valid = int.TryParse($"{(char)hi}{(char)lo}", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int expected)
                && expected == sum;
            if (notification)
            {
                // 非同期の通知 (%Stop など) は使わない。
                continue;
            }

            if (!_noAck)
            {
                _one[0] = valid ? (byte)'+' : (byte)'-';
                await _stream.WriteAsync(_one, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!valid)
            {
                if (_noAck)
                {
                    throw Fail(new GdbProtocolException("Checksum mismatch."));
                }

                continue;
            }

            // 連長圧縮で展開した長さにも上限を付ける (短い応答から巨大な文字列を作らない)。
            string text = Decode(raw, MaxPacketSize * 4) ?? throw Fail(new GdbProtocolException("The reply is too long."));

            // コンソールへの出力 (O で始まる 16 進。OK は除く) は読み飛ばす。
            if (text.Length > 1 && text[0] == 'O' && text != "OK" && text.Skip(1).All(Uri.IsHexDigit))
            {
                continue;
            }

            return text;
        }
    }

    /// <summary>エスケープ (<c>}</c>) と連長圧縮 (<c>*</c>) を戻す。</summary>
    public static string Decode(IReadOnlyList<byte> raw) => Decode(raw, int.MaxValue)!;

    /// <summary>エスケープと連長圧縮を戻す。展開した長さが <paramref name="maxLength"/> を超えたら null。</summary>
    public static string? Decode(IReadOnlyList<byte> raw, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(raw.Count, maxLength));
        for (int i = 0; i < raw.Count; i++)
        {
            byte b = raw[i];
            if (b == '}' && i + 1 < raw.Count)
            {
                sb.Append((char)(raw[++i] ^ 0x20));
            }
            else if (b == '*' && sb.Length > 0 && i + 1 < raw.Count)
            {
                int repeat = raw[++i] - 29;
                if (sb.Length + (long)Math.Max(0, repeat) > maxLength)
                {
                    return null;
                }

                sb.Append(sb[^1], Math.Max(0, repeat));
            }
            else
            {
                sb.Append((char)b);
            }

            if (sb.Length > maxLength)
            {
                return null;
            }
        }

        return sb.ToString();
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        int n = await _stream.ReadAsync(_one, cancellationToken).ConfigureAwait(false);
        if (n == 0)
        {
            throw Fail(new GdbConnectionException("The GDB server closed the connection."));
        }

        return _one[0];
    }

    public async ValueTask DisposeAsync()
    {
        _broken = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        _broken = true;
        _stream.Dispose();
        _gate.Dispose();
    }

    /// <summary>TcpClient の寿命をストリームに持たせる。</summary>
    private sealed class OwnedStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _inner = tcp.GetStream();

        public Socket Socket => tcp.Client;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
