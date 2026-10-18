using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HexEditor.Core.Engine;
using HexEditor.Core.Remote.Udp;

namespace HexEditor.Core.Tests.Remote;

/// <summary>ENG-42 UDP の受信 (localhost の UDP で送る。外部のホストには送らない)。</summary>
public sealed class UdpCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-udp").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UdpCapture Start(long limit = UdpCaptureSettings.DefaultLimit, long memory = UdpCaptureSettings.DefaultLimit) =>
        UdpCapture.Start(new UdpCaptureSettings { Port = 0, Limit = limit, MemoryLimit = memory, TempDirectory = _dir });

    private static void Send(UdpCapture capture, params byte[][] datagrams)
    {
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        foreach (byte[] d in datagrams)
        {
            sender.Send(d, d.Length, new IPEndPoint(IPAddress.Loopback, capture.LocalEndPoint.Port));
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "timeout");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Three_datagrams_are_appended_in_order_with_a_packet_list()
    {
        using UdpCapture capture = Start();
        Send(capture, [1, 2, 3], [4, 5, 6, 7], [8]);
        WaitFor(() => capture.Packets.Count == 3);

        using var source = new UdpByteSource(capture, "UDP");
        Assert.Equal(8, source.Length);
        byte[] data = new byte[8];
        Assert.True(source.Read(0, data).IsComplete);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, data);
        Assert.Equal([(0L, 3), (3L, 4), (7L, 1)], capture.Packets.Select(p => (p.Position, p.Length)));
        Assert.All(capture.Packets, p => Assert.True(IPAddress.IsLoopback(p.Source.Address)));
        Assert.Equal(3, capture.PacketAt(5)!.Position);
        Assert.Equal(3, capture.ReceivedCount);

        // 読み取り専用のドキュメント (書き戻す先がない)。
        Assert.False(source.Capabilities.HasFlag(Sources.SourceCapabilities.CanWrite));
    }

    [Fact]
    public void The_document_grows_as_datagrams_arrive()
    {
        using UdpCapture capture = Start();
        var source = new UdpByteSource(capture, "UDP");
        using var doc = new Document(source);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        Assert.Equal(0, doc.Length);

        Send(capture, [0xAA, 0xBB]);
        WaitFor(() => capture.Store.End == 2);
        Assert.False(source.Publish());
        Assert.True(doc.SyncAppendedSource());
        Assert.Equal(2, doc.Length);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, ReadAll(doc));
        Assert.False(doc.IsModified);
    }

    private static byte[] ReadAll(Document doc)
    {
        byte[] data = new byte[doc.Length];
        doc.Current.Read(0, data);
        return data;
    }

    [Fact]
    public void Recording_stops_at_the_limit_and_discarding_keeps_the_total_position()
    {
        using UdpCapture capture = Start(limit: UdpCaptureSettings.MinimumLimit);
        int limitEvents = 0;
        capture.LimitReached += (_, _) => Interlocked.Increment(ref limitEvents);

        // 1,000 バイトのデータグラムを 1,100 個 (通し番号の下位 1 バイトで埋める) 記録する。
        for (int i = 0; i < 1100; i++)
        {
            capture.Record(Enumerable.Repeat((byte)i, 1000).ToArray(), new IPEndPoint(IPAddress.Loopback, 1));
        }

        Assert.Equal(UdpCaptureState.LimitReached, capture.State);
        Assert.Equal(1, limitEvents);
        Assert.True(capture.Store.Length <= UdpCaptureSettings.MinimumLimit);
        Assert.Equal(1048, capture.Packets.Count);

        // 「古いデータを捨てて続ける」の後、さらに 100 個。
        capture.DiscardOldData();
        for (int i = 1100; i < 1200; i++)
        {
            capture.Record(Enumerable.Repeat((byte)i, 1000).ToArray(), new IPEndPoint(IPAddress.Loopback, 1));
        }

        using var source = new UdpByteSource(capture, "UDP");
        long total = (1048 + 100) * 1000L;
        Assert.Equal(total, capture.Store.End);
        Assert.Equal(total - source.Length, source.BaseAddress);
        Assert.True(source.Length <= UdpCaptureSettings.MinimumLimit);
        byte[] last = new byte[1];
        source.Read(source.Length - 1, last);
        Assert.Equal(1199 & 0xFF, last[0]);
        Assert.Equal(total - 1, source.BaseAddress + source.Length - 1);
        Assert.True(capture.Packets[0].End > source.BaseAddress);
    }

    [Fact]
    public void Pause_resume_and_clear()
    {
        using UdpCapture capture = Start();
        var from = new IPEndPoint(IPAddress.Loopback, 1);
        capture.Record([1], from);
        capture.Pause();
        capture.Record([2], from);
        Assert.Equal(1, capture.Store.End);
        capture.Resume();
        capture.Record([3], from);
        Assert.Equal(2, capture.Store.End);
        capture.Clear();
        Assert.Equal((0L, 0L, 0), (capture.Store.Start, capture.Store.End, capture.Packets.Count));
    }

    [Fact]
    public void A_port_in_use_is_reported()
    {
        using var other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)other.Client.LocalEndPoint!).Port;
        UdpPortInUseException ex = Assert.Throws<UdpPortInUseException>(() =>
            UdpCapture.Start(new UdpCaptureSettings { Port = port, TempDirectory = _dir }));
        Assert.Equal(port, ex.Port);
    }

    [Fact]
    public void Data_over_the_memory_limit_goes_to_a_temporary_file()
    {
        using var store = new AppendStore(AppendStore.ChunkSize, _dir);
        byte[] block = new byte[AppendStore.ChunkSize];
        for (int i = 0; i < 4; i++)
        {
            Array.Fill(block, (byte)(i + 1));
            store.Append(block);
        }

        Assert.True(store.SpilledBytes >= 2L * AppendStore.ChunkSize);
        byte[] read = new byte[16];
        store.Read(AppendStore.ChunkSize - 8, read, out _);
        Assert.Equal(Enumerable.Repeat((byte)1, 8).Concat(Enumerable.Repeat((byte)2, 8)), read);

        // 捨てた塊の場所は使い回す (一時ファイルが伸び続けない)。
        store.DiscardFront(2L * AppendStore.ChunkSize);
        store.Append(block);
        store.Append(block);
        Assert.True(store.SpilledBytes <= 3L * AppendStore.ChunkSize);
        Assert.Equal(6L * AppendStore.ChunkSize, store.End);
    }

    [Fact]
    public void Packet_list_exports_to_csv_and_json()
    {
        var packets = new[]
        {
            new UdpPacket(1, 0, 3, new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), new IPEndPoint(IPAddress.Loopback, 5000)),
            new UdpPacket(2, 3, 4, new DateTimeOffset(2026, 10, 18, 0, 0, 1, TimeSpan.Zero), new IPEndPoint(IPAddress.Loopback, 5000)),
        };
        string csv = UdpPacketExport.ToCsv(packets);
        Assert.Equal("number,position,length,received,source", csv.Split("\r\n")[0]);
        Assert.Equal("2,0x3,4,2026-10-18T00:00:01.0000000Z,127.0.0.1:5000", csv.Split("\r\n")[2]);
        using var json = System.Text.Json.JsonDocument.Parse(UdpPacketExport.ToJson(packets));
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal(3, json.RootElement[1].GetProperty("position").GetInt64());
    }

    [Theory]
    [InlineData(0, UdpCaptureSettings.DefaultLimit, "port")]
    [InlineData(65536, UdpCaptureSettings.DefaultLimit, "port")]
    [InlineData(9000, 1000, "limit")]
    [InlineData(9000, UdpCaptureSettings.DefaultLimit, null)]
    public void Settings_are_validated(int port, long limit, string? expected) =>
        Assert.Equal(expected, UdpCaptureSettings.Validate(port, limit, EngineMemory.DefaultLimit));
}
