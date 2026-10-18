using System.Net;
using HexEditor.Platform.Network;
using HexEditor.Platform.Tests.Support;

namespace HexEditor.Platform.Tests;

/// <summary>
/// 通信の入口 (<see cref="NetworkClient"/>) の応答の大きさの上限。上限を超えた応答は、全体を受信してから調べるのではなく、超えた時点で
/// 受信をやめる (07 の TPL-36 の配信、追加コンポーネントのダウンロードなど)。実際のネットワークには接続しない。
/// </summary>
public sealed class NetworkClientTests
{
    private static readonly Uri Address = new("https://example.invalid/feed");

    /// <summary>終わりのない本体を返す偽のサーバー (読まれた量を数える)。</summary>
    private sealed class EndlessHandler(bool declareLength) : HttpMessageHandler
    {
        public long Served => _stream.Served;

        private readonly EndlessStream _stream = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(_stream, 4096);
            if (declareLength)
            {
                content.Headers.ContentLength = 1L << 40;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static NetworkClient Client(HttpMessageHandler handler) => new(new NetworkPolicy(new MemorySettings()), "1.0.0", handler);

    [Fact]
    public async Task An_endless_response_is_cut_off_once_it_exceeds_the_limit()
    {
        var handler = new EndlessHandler(declareLength: false);
        using NetworkClient client = Client(handler);
        const long Limit = 1024 * 1024;
        await Assert.ThrowsAsync<NetworkResponseTooLargeException>(() =>
            client.GetBytesAsync(NetworkFeature.Updates, Address, manual: true, Limit, timeout: TimeSpan.FromMinutes(5)));

        // 上限を少し超えたところで受信をやめている (際限なく読まない)。
        Assert.InRange(handler.Served, Limit, Limit + (1024 * 1024));
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_reading_the_body()
    {
        var handler = new EndlessHandler(declareLength: true);
        using NetworkClient client = Client(handler);
        await Assert.ThrowsAsync<NetworkResponseTooLargeException>(() =>
            client.GetBytesAsync(NetworkFeature.Updates, Address, manual: true, 1024, timeout: TimeSpan.FromMinutes(5)));
        Assert.True(handler.Served < 1024 * 1024);
    }

    [Fact]
    public async Task Responses_within_the_limit_are_returned_whole()
    {
        byte[] body = "{\"a\":\"あ\"}"u8.ToArray();
        using NetworkClient client = Client(new FixedHandler(body));
        Assert.Equal(body, await client.GetBytesAsync(NetworkFeature.Updates, Address, manual: true, body.Length));
        Assert.Equal("{\"a\":\"あ\"}", await client.GetStringAsync(NetworkFeature.Updates, Address, manual: true));
        await Assert.ThrowsAsync<NetworkResponseTooLargeException>(() =>
            client.GetStringAsync(NetworkFeature.Updates, Address, manual: true, maxBytes: body.Length - 1));

        using var destination = new MemoryStream();
        var reports = new List<long>();
        long received = await client.DownloadAsync(NetworkFeature.Updates, Address, manual: true, destination, body.Length,
            new SynchronousProgress<(long Done, long? Total)>(p => reports.Add(p.Done)));
        Assert.Equal(body.Length, received);
        Assert.Equal(body, destination.ToArray());
        Assert.Equal(body.Length, reports[^1]);
    }
}
