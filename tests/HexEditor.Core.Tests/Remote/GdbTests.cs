using System.Diagnostics;
using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Remote;

/// <summary>
/// ENG-40 GDB サーバー。偽の GDB サーバー (<see cref="FakeGdbServer"/>、127.0.0.1) で QEMU の手順を再現する。実際の QEMU を使うテストは
/// <see cref="QemuFactAttribute"/> (CI の QEMU を入れたランナーだけ)。
/// </summary>
public sealed class GdbTests : IDisposable
{
    private const long RamBase = 0x100000;

    private readonly FakeGdbServer _server;
    private readonly byte[] _ram = QemuRam();

    public GdbTests()
    {
        _server = new FakeGdbServer();
        _server.Write(RamBase, _ram);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    /// <summary>TD-ENG-QEMU-RAM (64 KiB、種 0x40 の乱数)。</summary>
    public static byte[] QemuRam()
    {
        byte[] data = new byte[64 * 1024];
        TestDataCatalog.Random(0x40, 0, data);
        return data;
    }

    private GdbEndpoint Endpoint(TimeSpan? timeout = null) =>
        new() { Host = "127.0.0.1", Port = _server.Port, AddressBits = 64, Timeout = timeout ?? GdbEndpoint.DefaultTimeout };

    private static byte[] Read(IByteSource source, long offset, int length)
    {
        byte[] data = new byte[length];
        ReadResult r = source.Read(offset, data);
        Assert.True(r.IsComplete, string.Join(", ", r.Unreadable));
        return data;
    }

    [Fact]
    [Trait("TC", "TC-ENG-40-01")]
    public async Task Memory_is_read_and_a_saved_write_reaches_the_target()
    {
        // 1. 64 bit、読み取り専用で開くをオフにして接続する。
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(), writable: true);
        Assert.Equal(long.MaxValue, source.Length);
        Assert.True(source.Capabilities.HasFlag(SourceCapabilities.IsVolatile));
        using var doc = new Document(source);

        // 2. 0x100000 の 64 バイトが TD-ENG-QEMU-RAM の先頭と一致する。
        byte[] head = new byte[64];
        Assert.True(doc.Current.Read(RamBase, head).IsComplete);
        Assert.Equal(_ram[..64], head);

        // 3. 0x100010 を CA FE BA BE に上書きして保存する。
        doc.Overwrite(RamBase + 0x10, [0xCA, 0xFE, 0xBA, 0xBE]);
        ProcessWriteResult result = await GdbWrite.ExecuteAsync(doc, source);
        Assert.True(result.AllWritten);
        doc.CompleteTargetWrite(source, result.Written, result.AllWritten);
        Assert.False(doc.IsModified);

        // 4. 対象の 0x100010 に同じ値がある (gdb の x/4xb の代わりにサーバーのメモリを見る)。
        Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, _server.Read(RamBase + 0x10, 4));
        Assert.Contains(_server.Log, p => p.StartsWith('X'));

        // Undo して保存すると、旧内容が書き戻される (ENG-34 と同じ)。
        doc.Undo();
        result = await GdbWrite.ExecuteAsync(doc, source);
        doc.CompleteTargetWrite(source, result.Written, result.AllWritten);
        Assert.Equal(_ram[0x10..0x14], _server.Read(RamBase + 0x10, 4));
    }

    [Fact]
    public async Task Ack_mode_servers_and_hex_writes_work()
    {
        using var server = new FakeGdbServer(packetSize: 0x100, noAck: false, binaryWrite: false);
        server.Write(0x2000, [1, 2, 3, 4]);
        server.Start();
        var endpoint = new GdbEndpoint { Host = "127.0.0.1", Port = server.Port, AddressBits = 32 };
        using GdbByteSource source = await GdbByteSource.ConnectAsync(endpoint, writable: true);
        Assert.Equal(1L << 32, source.Length);

        // パケットサイズ (0x100) に合わせて分けて読む (1 回の m は 0x7C バイト以下)。
        byte[] data = Read(source, 0x2000, 1024);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data[..4]);
        Assert.All(server.Log.Where(p => p.StartsWith('m')), p =>
            Assert.True(int.Parse(p.Split(',')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 2 <= 0x100, p));

        // X に対応していなければ M (16 進) で書く。
        await source.WriteAsync(0x2000, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9, 3, 4 }, server.Read(0x2000, 4));
        Assert.Contains(server.Log, p => p.StartsWith('M'));
    }

    [Fact]
    public async Task Ranges_the_target_rejects_are_shown_as_access_denied()
    {
        _server.Unreadable.Add((0x200000, 0x1000));
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(), writable: false);
        byte[] data = new byte[0x3000];
        ReadResult r = source.Read(0x1FF000, data);
        UnreadableRange bad = Assert.Single(r.Unreadable);
        Assert.Equal(UnreadableReason.AccessDenied, bad.Reason);
        Assert.True(bad.Offset <= 0x200000 && bad.End >= 0x201000 && bad.Length <= 0x3000, $"{bad.Offset:X} {bad.Length:X}");
        Assert.Equal(RemoteConnectionState.Connected, source.State);
        Assert.False(source.Capabilities.HasFlag(SourceCapabilities.CanWrite));
    }

    [Fact]
    [Trait("TC", "TC-ENG-40-02")]
    public async Task Stopping_the_server_disconnects_and_reconnect_resumes_reading()
    {
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(), writable: false);
        using var doc = new Document(source);
        int disconnects = 0;
        source.Changed += (_, e) => disconnects += e.Kind == SourceChangeKind.Disconnected ? 1 : 0;
        Assert.Equal(_ram[..16], Read(source, RamBase, 16));

        // 1-2. 接続先を止めると「切断」になる。
        _server.Stop();
        byte[] data = new byte[16];
        ReadResult r = source.Read(RamBase + 0x40, data);
        Assert.Contains(r.Unreadable, u => u.Reason == UnreadableReason.Disconnected);
        Assert.Equal(RemoteConnectionState.Disconnected, source.State);
        Assert.Equal(1, disconnects);

        // 3-4. 再起動して再接続すると、キャッシュにない範囲の読み込みが再開する。
        _server.Start();
        await source.ReconnectAsync();
        doc.RefreshFromSource();
        Assert.Equal(RemoteConnectionState.Connected, source.State);
        Assert.Equal(_ram[0x40..0x50], Read(source, RamBase + 0x40, 16));
    }

    [Fact]
    public async Task A_stopped_server_is_detected_without_a_request()
    {
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(), writable: false);
        Assert.False(source.CheckConnection());
        _server.Stop();
        var watch = Stopwatch.StartNew();
        while (!source.CheckConnection() && source.State != RemoteConnectionState.Disconnected)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "not detected");
            await Task.Delay(20);
        }

        Assert.Equal(RemoteConnectionState.Disconnected, source.State);
    }

    [Fact]
    public async Task A_request_over_the_timeout_disconnects()
    {
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(TimeSpan.FromMilliseconds(300)), writable: false);
        _server.ReplyDelay = TimeSpan.FromSeconds(2);
        var watch = Stopwatch.StartNew();
        ReadResult r = source.Read(RamBase, new byte[16]);
        Assert.Contains(r.Unreadable, u => u.Reason == UnreadableReason.Disconnected);
        Assert.IsType<GdbTimeoutException>(source.LastError);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task An_invalid_reply_disconnects_with_a_protocol_error()
    {
        using GdbByteSource source = await GdbByteSource.ConnectAsync(Endpoint(), writable: false);
        _server.Garbage = true;
        source.Read(RamBase, new byte[16]);
        Assert.Equal(RemoteConnectionState.Disconnected, source.State);
        Assert.IsType<GdbProtocolException>(source.LastError);
    }

    [Fact]
    public async Task Connecting_to_a_closed_port_fails()
    {
        int port = _server.Port;
        _server.Stop();
        var endpoint = new GdbEndpoint { Host = "127.0.0.1", Port = port, Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => GdbByteSource.ConnectAsync(endpoint, writable: false));
    }

    [Fact]
    public void Packets_are_framed_escaped_and_run_length_decoded()
    {
        Assert.Equal("$m0,4#fd", System.Text.Encoding.ASCII.GetString(GdbClient.Frame("m0,4"u8)));
        byte[] escaped = GdbClient.Frame([(byte)'X', (byte)'#', (byte)'}']);
        Assert.Equal("$X}\u0003}]#", System.Text.Encoding.Latin1.GetString(escaped)[..7]);
        Assert.Equal("0000", GdbClient.Decode("0* "u8.ToArray()));
        Assert.Equal("ab}", GdbClient.Decode("ab}]"u8.ToArray()));
    }

    [QemuFact]
    [Trait("TC", "TC-ENG-40-01")]
    [Trait("Category", "Integration")]
    public async Task Qemu_guest_memory_is_read_and_written()
    {
        GdbEndpoint endpoint = QemuFactAttribute.Endpoint();
        using GdbByteSource source = await GdbByteSource.ConnectAsync(endpoint, writable: true);
        using var doc = new Document(source);
        Assert.Equal(_ram[..64], Read(source, RamBase, 64));
        doc.Overwrite(RamBase + 0x10, [0xCA, 0xFE, 0xBA, 0xBE]);
        ProcessWriteResult result = await GdbWrite.ExecuteAsync(doc, source);
        Assert.True(result.AllWritten);
        doc.CompleteTargetWrite(source, result.Written, result.AllWritten);
        source.Disconnect();

        // gdb-multiarch で x/4xb を実行して確かめる (CI のランナーだけ)。
        string output = QemuFactAttribute.RunGdb(endpoint, $"x/4xb 0x{RamBase + 0x10:x}");
        Assert.Contains("0xca\t0xfe\t0xba\t0xbe", output.Replace(' ', '\t'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// QEMU の GDB サーバー (TD-ENG-QEMU-RAM を 0x100000 に読み込ませたもの) があるときだけ動く Fact。環境変数 HEXEDITOR_QEMU_GDB (ホスト:ポート) と
/// HEXEDITOR_GDB (gdb-multiarch の実行ファイル) を CI のランナーで設定する。開発者の PC には入れない。
/// </summary>
public sealed class QemuFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_QEMU_GDB";

    public QemuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 })
        {
            Skip = $"{Variable} がない (QEMU を入れた CI のランナーだけで動く)。";
        }
    }

    public static GdbEndpoint Endpoint()
    {
        string value = Environment.GetEnvironmentVariable(Variable)!;
        int colon = value.LastIndexOf(':');
        return new GdbEndpoint { Host = value[..colon], Port = int.Parse(value[(colon + 1)..], CultureInfo.InvariantCulture) };
    }

    public static string RunGdb(GdbEndpoint endpoint, string command)
    {
        string gdb = Environment.GetEnvironmentVariable("HEXEDITOR_GDB") ?? "gdb-multiarch";
        var info = new ProcessStartInfo(gdb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-batch", "-ex", $"target remote {endpoint.Display}", "-ex", command })
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return output;
    }
}
