using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Processes;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Devices;

/// <summary>
/// 補助プロセス (ENG-28) の通信を、昇格も別プロセスもなしで確かめる。<see cref="HelperServer"/> を偽のデバイスに対して走らせ、
/// <see cref="HelperClient"/> とつないだ双方向ストリームで要求を送る。
/// </summary>
public sealed class HelperProtocolTests
{
    private static FakeDeviceSpec Spec() => new()
    {
        Elevated = true,
        Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1 * 1024 * 1024, Seed = 1 }],
    };

    private static async Task<(HelperClient Client, Task Server, FakeDeviceAccess Devices)> StartAsync(
        FakeDeviceSpec? spec = null, FakeProcessListSpec? processes = null, Action<string>? log = null)
    {
        (Stream a, Stream b) = DuplexStreams.CreatePair();
        var devices = new FakeDeviceAccess(spec ?? Spec());
        var access = new FakeProcessAccess(processes ?? new FakeProcessListSpec { Elevated = true });
        var operations = new PrivilegedOperations(devices, access, log, "1.0.0");
        var server = new HelperServer(b, operations, log) { IdleTimeout = null };
        Task serverTask = server.RunAsync();
        var client = new HelperClient(a) { Timeout = TimeSpan.FromSeconds(5) };
        (uint status, _) = await client.SendAsync(HelperCommand.Hello, 0,
            new BodyWriter().U32(HelperProtocol.Version).Text("1.0.0").ToArray());
        Assert.Equal(0u, status);
        return (client, serverTask, devices);
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-06")]
    public async Task Commands_not_on_the_allow_list_are_rejected_and_the_server_stops()
    {
        // 1. 未定義のコマンド番号 0xFFFF。
        await RejectAsync(c => c.SendAsync((HelperCommand)0xFFFF, 0, ReadOnlyMemory<byte>.Empty));

        // 2. OpenDevice にファイルのパス。
        await RejectAsync(c => c.SendAsync(HelperCommand.OpenDevice, 0, new BodyWriter().Text(@"C:\Windows\win.ini").ToArray()));

        // 3. OpenDevice に .. を含むパス。
        await RejectAsync(c => c.SendAsync(HelperCommand.OpenDevice, 0, new BodyWriter().Text(@"\\.\PhysicalDrive0\..\..\Windows").ToArray()));

        // 4. 本体が上限 (1 MiB + 64) を超える WriteSectors。
        await RejectAsync(c => c.SendAsync(HelperCommand.WriteSectors, 0, new byte[HelperProtocol.MaxTransfer + 65]));

        // 5. QueryDeviceInfo に許可されていない種類 (SET MAX ADDRESS 相当)。
        await RejectAsync(c => c.SendAsync(HelperCommand.QueryDeviceInfo, 0, new BodyWriter().U32(1).U16(0xFFFF).ToArray()));

        // 6. SetUsbWriteProtect に値 2。
        await RejectAsync(c => c.SendAsync(HelperCommand.SetUsbWriteProtect, 0, new BodyWriter().U32(2).ToArray()));
    }

    private static async Task RejectAsync(Func<HelperClient, Task> send)
    {
        (HelperClient client, Task server, _) = await StartAsync();
        await using (client)
        {
            // 要求は実行されず、補助プロセスはパイプを切断して終了する (応答が来ない・接続が切れる)。
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await send(client);
            });

            HelperServerExit exit = await ((Task<HelperServerExit>)server).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HelperServerExit.Rejected, exit);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-06")]
    public async Task A_file_path_open_does_not_open_any_file()
    {
        var opened = new List<string>();
        var devices = new TrackingDevices(Spec(), opened);
        (Stream a, Stream b) = DuplexStreams.CreatePair();
        var operations = new PrivilegedOperations(devices, new FakeProcessAccess(new FakeProcessListSpec()), null, "1.0.0");
        Task server = new HelperServer(b, operations) { IdleTimeout = null }.RunAsync();
        var client = new HelperClient(a);
        await client.SendAsync(HelperCommand.Hello, 0, new BodyWriter().U32(HelperProtocol.Version).Text("1.0.0").ToArray());
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.SendAsync(HelperCommand.OpenDevice, 0, new BodyWriter().Text(@"C:\Windows\win.ini").ToArray()));
        await ((Task<HelperServerExit>)server).WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        Assert.Empty(opened);
    }

    [Fact]
    public async Task Read_through_the_helper_matches_the_device_contents()
    {
        (HelperClient client, Task server, FakeDeviceAccess devices) = await StartAsync();
        await using (client)
        {
            var proxy = new HelperDeviceAccess(client);
            using IDeviceHandle handle = proxy.Open(DevicePath.PhysicalDrive(0), writable: false);
            byte[] viaHelper = new byte[4096];
            Assert.Equal(0, handle.ReadSectors(0, viaHelper));

            byte[] direct = new byte[4096];
            devices.Disk(0).Read(0, direct);
            Assert.Equal(direct, viaHelper);
        }

        await ((Task<HelperServerExit>)server).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Closing_the_client_closes_the_helper_handles_and_unlocks()
    {
        var spec = new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1024 * 1024 }],
            Volumes = [new FakeVolumeSpec { Path = @"\\.\X:", DriveLetter = "X:", Disk = 0, Offset = 0, Size = 1024 * 1024, RequiresAdmin = true }],
        };
        (Stream a, Stream b) = DuplexStreams.CreatePair();
        var devices = new FakeDeviceAccess(spec);
        var operations = new PrivilegedOperations(devices, new FakeProcessAccess(new FakeProcessListSpec()), null, "1.0.0");
        Task server = new HelperServer(b, operations) { IdleTimeout = null }.RunAsync();
        var client = new HelperClient(a);
        await client.SendAsync(HelperCommand.Hello, 0, new BodyWriter().U32(HelperProtocol.Version).Text("1.0.0").ToArray());
        var proxy = new HelperDeviceAccess(client);
        IDeviceHandle handle = proxy.Open(@"\\.\X:", writable: true);
        Assert.Equal(0, handle.LockVolume());
        Assert.Contains(@"\\.\X:", devices.LockedVolumes);

        // クライアントを閉じると (UI のプロセスの終了・切断に当たる) 補助プロセスはハンドルを閉じ、ロックを解除する。
        await client.DisposeAsync();
        await ((Task<HelperServerExit>)server).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(@"\\.\X:", devices.LockedVolumes);
    }

    private sealed class TrackingDevices(FakeDeviceSpec spec, List<string> opened) : IDeviceAccess
    {
        private readonly FakeDeviceAccess _inner = new(spec);

        public DeviceCatalog Enumerate() => _inner.Enumerate();

        public IDeviceHandle Open(string path, bool writable)
        {
            opened.Add(path);
            return _inner.Open(path, writable);
        }
    }

    /// <summary>
    /// 補助プロセスが Hello で返す版は、本体が送る版 (SemVer。ビルドのメタデータ「+コミット」なし) と同じ形にする
    /// (InformationalVersion をそのまま返すと、同じビルドでも「版が違う」と判定されて起動できなかった)。
    /// </summary>
    [Theory]
    [InlineData("0.0.0-local+1a2b3c4", "0.0.0-local")]
    [InlineData("1.2.3-preview.4+abcdef0", "1.2.3-preview.4")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(null, "")]
    public void HelperVersionIsTheSemVerPart(string? informational, string expected) =>
        Assert.Equal(expected, HelperVersion.SemVerPart(informational));
}
