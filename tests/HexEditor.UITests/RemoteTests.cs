using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// リモートのデータソース (ENG-40〜ENG-42) の UI の結合テスト。GDB はテストのプロセスの偽の GDB サーバー (127.0.0.1)、SFTP はアプリの中の偽の
/// SFTP サーバー、UDP はテストのプロセスから 127.0.0.1 に送るデータグラムを使う。外部のホストには接続しない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class RemoteTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static byte[] QemuRam()
    {
        byte[] data = new byte[64 * 1024];
        TestDataCatalog.Random(0x40, 0, data);
        return data;
    }

    // ---- ENG-40 GDB サーバー ----

    [Fact]
    [Trait("TC", "TC-ENG-40-02")]
    public Task Stopping_the_GDB_server_shows_disconnected_and_reconnect_resumes_reading() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] ram = QemuRam();
        using var server = new FakeGdbServer();
        server.Write(0x100000, ram);
        server.Start();
        AppSession app = await ctx.StartAsync();

        // TC-ENG-40-01 の手順 1 と同じ方法で接続し (64 bit、読み取り専用で開くをオフ)、0x100000 を表示する。
        JsonObject opened = await app.SendAsync("gdbConnect", new JsonObject { ["port"] = server.Port, ["readOnly"] = false });
        Assert.True(opened["opened"]!.GetValue<bool>(), opened.ToJsonString());
        await app.GoToAsync(0x100000);
        Assert.Equal(ram[..16], await app.BytesAsync(0x100000, 16));
        Assert.Contains("connected", (await app.SendAsync("remoteState"))["status"]!.GetValue<string>(), StringComparison.Ordinal);

        // 1-2. 接続先を止めると「切断」になり、「再接続」のボタンが出る。
        server.Stop();
        await app.WaitUntilAsync(async () => (await app.SendAsync("remoteState"))["state"]?.GetValue<string>() == "Disconnected",
            UiTest.Scaled(Wait), "the disconnected state");
        JsonObject state = await app.StateAsync();
        Assert.Contains(state["notifications"]!.AsArray(), n => n!["actions"]!.AsArray().Any(a => a!.GetValue<string>() == "Reconnect"));

        // 3-4. 同じポートで起動し直し、「再接続」で読み込みを再開する (キャッシュを捨てて読み直すので、0x100040 も読み直す)。
        server.Start();
        await app.SendAsync("notificationAction", new JsonObject { ["label"] = "Reconnect" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("remoteState"))["state"]?.GetValue<string>() == "Connected",
            UiTest.Scaled(Wait), "the reconnection");
        Assert.Equal(ram[0x40..0x50], await app.BytesAsync(0x100040, 16));
    });

    [Fact]
    public Task A_GDB_write_is_confirmed_and_reaches_the_target() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] ram = QemuRam();
        using var server = new FakeGdbServer();
        server.Write(0x100000, ram);
        server.Start();
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("gdbConnect", new JsonObject { ["port"] = server.Port, ["readOnly"] = false });
        await app.GoToAsync(0x100010);
        await app.TypeAsync("CAFEBABE");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitForAsync("GdbWriteConfirmDialog");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(() => Task.FromResult(server.Read(0x100010, 4).SequenceEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE })),
            UiTest.Scaled(Wait), "the written value");
        await app.WaitUntilAsync(async () => (await app.SendAsync("remoteState"))["modified"]?.GetValue<bool>() == false, UiTest.Scaled(Wait), "saved");
    });

    // ---- ENG-41 SFTP ----

    private static JsonObject FakeSftp(string fingerprint) => new()
    {
        ["spec"] = new JsonObject
        {
            ["hostKeyAlgorithm"] = "ssh-ed25519",
            ["hostKeyFingerprint"] = fingerprint,
            ["passwords"] = new JsonObject { ["tester"] = "Hx-Test-Pa55!" },
            ["files"] = new JsonArray(new JsonObject { ["path"] = "/home/test/a.bin", ["length"] = 4096, ["seed"] = 3 }),
        },
    };

    private static Task WaitForSftpAsync(AppSession app) => app.WaitUntilAsync(
        async () => (await app.SendAsync("sftpResult"))["done"]!.GetValue<bool>(), UiTest.Scaled(Wait), "the SFTP connection");

    [Fact]
    [Trait("TC", "TC-ENG-41-03")]
    public Task A_changed_host_key_shows_a_warning_and_the_default_button_does_not_connect() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("fakeSftp", FakeSftp("SHA256:firstKey000000000000000000000000000000000"));
        var login = new JsonObject { ["host"] = "localhost", ["port"] = 22, ["user"] = "tester", ["password"] = "Hx-Test-Pa55!", ["path"] = "/home/test/a.bin" };

        // 前提: 一度接続してホスト鍵を信頼してから切断する。
        await app.SendAsync("sftpConnect", login.DeepClone().AsObject());
        await app.WaitForAsync("SftpHostKeyDialog");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await WaitForSftpAsync(app);
        Assert.Equal("a.bin", (await app.SendAsync("sftpResult"))["result"]!.GetValue<string>());
        await app.KeyAsync("W", ctrl: true);
        Assert.Equal(1, (await app.SendAsync("fakeSftpState"))["sessions"]!.GetValue<int>());

        // サーバーのホスト鍵を作り直す。
        await app.SendAsync("fakeSftpHostKey", new JsonObject { ["algorithm"] = "ecdsa-sha2-nistp256", ["fingerprint"] = "SHA256:regenerated11111111111111111111111111111" });

        // 1-2. 同じホストに接続すると、新しい鍵の種類と SHA-256 のフィンガープリントを含む警告が出る。既定のボタンは「キャンセル」。
        await app.SendAsync("sftpConnect", login.DeepClone().AsObject());
        await app.WaitForAsync("SftpHostKeyChangedDialog");
        JsonObject dialog = await app.SendAsync("hostKeyDialog");
        string message = dialog["message"]!.GetValue<string>();
        Assert.Contains("different", message, StringComparison.Ordinal);
        Assert.Contains("ecdsa-sha2-nistp256", message, StringComparison.Ordinal);
        Assert.Contains("SHA256:regenerated11111111111111111111111111111", message, StringComparison.Ordinal);
        Assert.Equal("Close", dialog["defaultButton"]!.GetValue<string>());

        // 3. 既定のボタンを押すと接続しない (SFTP のセッションが作られない)。
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
        await WaitForSftpAsync(app);
        Assert.Equal("cancelled", (await app.SendAsync("sftpResult"))["result"]!.GetValue<string>());
        Assert.Equal(1, (await app.SendAsync("fakeSftpState"))["sessions"]!.GetValue<int>());
    });

    [Fact]
    public Task An_SFTP_file_is_recorded_in_recent_files_without_the_password_and_shows_its_remote_path() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.SendAsync("fakeSftp", FakeSftp("SHA256:firstKey000000000000000000000000000000000"));
        await app.SendAsync("sftpConnect", new JsonObject
        {
            ["host"] = "localhost", ["user"] = "tester", ["password"] = "Hx-Test-Pa55!", ["path"] = "/home/test/a.bin",
        });
        await app.WaitForAsync("SftpHostKeyDialog");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await WaitForSftpAsync(app);
        JsonObject remote = await app.SendAsync("remoteState");
        Assert.Equal("sftp://tester@localhost:22/home/test/a.bin", remote["location"]!.GetValue<string>());
        Assert.StartsWith("sftp://tester@localhost:22/home/test/a.bin", remote["toolTip"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty((await app.SendAsync("fakeSftpState"))["savedCredentials"]!.AsArray());
        await app.KeyAsync("W", ctrl: true);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(UiTest.Scaled(Wait));

        string recent = await File.ReadAllTextAsync(Path.Combine(profile, "recent.json"));
        Assert.Contains("sftp://tester@localhost:22/home/test/a.bin", recent, StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
        {
            byte[] content = await File.ReadAllBytesAsync(file);
            Assert.True(content.AsSpan().IndexOf("Hx-Test-Pa55!"u8) < 0, file);
            Assert.True(content.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("Hx-Test-Pa55!")) < 0, file);
        }
    });

    // ---- ENG-42 UDP ----

    /// <summary>テストのポートで待ち受ける (使用中なら空いているポートにする。テストの手順は同じ)。</summary>
    private static async Task<int> StartUdpAsync(AppSession app, int port, long? limit = null)
    {
        var request = new JsonObject { ["port"] = port };
        if (limit is { } l)
        {
            request["limit"] = l;
        }

        JsonObject started = await app.SendAsync("udpStart", request);
        if (!started["opened"]!.GetValue<bool>())
        {
            request["port"] = 0;
            started = await app.SendAsync("udpStart", request);
        }

        Assert.True(started["opened"]!.GetValue<bool>(), started.ToJsonString());
        return IPEndPoint.Parse(started["endpoint"]!.GetValue<string>()).Port;
    }

    private static async Task<JsonObject> UdpStateAsync(AppSession app) => await app.SendAsync("udpState", new JsonObject { ["flush"] = true });

    [Fact]
    [Trait("TC", "TC-ENG-42-01")]
    public Task Three_datagrams_are_appended_with_three_packet_rows() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1. 127.0.0.1、ポート 9000 で開始する。
        int port = await StartUdpAsync(app, 9000);

        // 2. テストのプロセスから 3 つのデータグラムを順に送る。
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        foreach (byte[] d in new byte[][] { [1, 2, 3], [4, 5, 6, 7], [8] })
        {
            await sender.SendAsync(d, target);
        }

        // 3. 長さ 8、内容 01..08、パケットの一覧が 3 行 (開始位置 0・3・7、長さ 3・4・1)、読み取り専用。
        await app.WaitUntilAsync(async () => (await UdpStateAsync(app))["length"]!.GetValue<long>() == 8, UiTest.Scaled(Wait), "8 bytes");
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await app.BytesAsync(0, 8));
        JsonObject state = await UdpStateAsync(app);
        JsonArray packets = state["packets"]!.AsArray();
        Assert.Equal([(0L, 3), (3L, 4), (7L, 1)], packets.Select(p => (p!["position"]!.GetValue<long>(), p["length"]!.GetValue<int>())));
        Assert.All(packets, p => Assert.StartsWith("127.0.0.1:", p!["source"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.True(state["readOnly"]!.GetValue<bool>());
        Assert.True(state["panelVisible"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await UdpStateAsync(app))["panelRows"]?.GetValue<int>() == 3, UiTest.Scaled(Wait), "3 rows");
        Assert.Contains("3 packets", state["status"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait("TC", "TC-ENG-42-02")]
    public Task Recording_stops_at_the_limit_and_discarding_keeps_total_addresses() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        const long limit = 1024 * 1024;
        int port = await StartUdpAsync(app, 9001, limit);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);

        // 50 個ずつ送り、受け取ったのを確かめてから次を送る (取りこぼさない)。done (この段階で送った数, 状態) が真になるまで待つ。
        async Task SendAsync(int from, int count, Func<int, JsonObject, bool> done)
        {
            for (int batch = 0; batch < count; batch += 50)
            {
                int n = Math.Min(50, count - batch);
                for (int i = 0; i < n; i++)
                {
                    await sender.SendAsync(Enumerable.Repeat((byte)(from + batch + i), 1000).ToArray(), target);
                }

                int sent = batch + n;
                await app.WaitUntilAsync(async () => done(sent, await UdpStateAsync(app)), UiTest.Scaled(Wait), $"{sent} datagrams");
            }
        }

        // 1. 各 1,000 バイトのデータグラム (内容は通し番号の下位 1 バイト) を 1,100 個送る。上限 (1 MiB) を超えた分は記録しない。
        await SendAsync(0, 1100, (sent, s) => s["received"]!.GetValue<long>() >= Math.Min(sent, 1048) || s["state"]!.GetValue<string>() == "LimitReached");
        await app.WaitUntilAsync(async () => (await UdpStateAsync(app))["state"]!.GetValue<string>() == "LimitReached", UiTest.Scaled(Wait), "the limit");

        // 2. 長さが 1 MiB を超えず、InfoBar が出る。
        JsonObject state = await UdpStateAsync(app);
        Assert.True(state["length"]!.GetValue<long>() <= limit);
        Assert.Equal(1048 * 1000L, state["length"]!.GetValue<long>());
        string notice = state["limitNotice"]!.GetValue<string>();
        Assert.Contains("1 MiB", notice, StringComparison.Ordinal);
        Assert.Contains("stopped", notice, StringComparison.Ordinal);

        // 3. 「古いデータを捨てて続ける」を選び、さらに 100 個送る。
        Assert.True((await app.SendAsync("notificationAction", new JsonObject { ["label"] = "Discard old data and continue" }))["invoked"]!.GetValue<bool>());
        long before = state["received"]!.GetValue<long>();
        await SendAsync(1100, 100, (sent, s) => s["received"]!.GetValue<long>() >= before + sent);
        await app.WaitUntilAsync(async () => (await UdpStateAsync(app))["received"]!.GetValue<long>() == before + 100, UiTest.Scaled(Wait), "100 more");

        // 4. ベースアドレス = 捨てたバイト数、末尾のバイトのアドレス = 記録したバイトの総数 − 1 (捨てた分を含む)、値は最後の通し番号。
        state = await UdpStateAsync(app);
        long total = state["total"]!.GetValue<long>();
        long length = state["length"]!.GetValue<long>();
        long baseAddress = state["baseAddress"]!.GetValue<long>();
        Assert.Equal(1148 * 1000L, total);
        Assert.Equal(total - length, baseAddress);
        Assert.True(length <= limit);
        Assert.Equal(total - 1, baseAddress + length - 1);
        Assert.Equal((byte)(1199 & 0xFF), (await app.BytesAsync(length - 1, 1))[0]);
    });
}
