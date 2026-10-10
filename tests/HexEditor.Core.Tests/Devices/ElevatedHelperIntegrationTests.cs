using System.Diagnostics;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Tests.Engine;

namespace HexEditor.Core.Tests.Devices;

/// <summary>
/// 補助プロセス (ENG-28) を昇格せずに、偽のデバイスに対して実際に起動して通信を確かめる (テスト方針どおり実機のディスクには触れない)。
/// <c>HexEditor.Elevated.exe</c> のビルドがなければスキップする。
/// </summary>
[Collection("ElevatedHelper")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ElevatedHelperIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-helper").FullName;
    private readonly List<Process> _started = [];

    private static string? FindHelperExe()
    {
        string csproj = SourceTests.FindRepoFile("src/HexEditor.Elevated/HexEditor.Elevated.csproj");
        string baseDir = Path.GetDirectoryName(csproj)!;
        // テスト用の仕組み (偽のデバイス) を持つ Debug のビルドだけを使う (製品版の Release のビルドは実機のデバイスを使うため使わない)。
        foreach (string config in new[] { "Debug" })
        {
            string dir = Path.Combine(baseDir, "bin", config);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            string? exe = Directory.GetFiles(dir, "HexEditor.Elevated.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is not null)
            {
                return exe;
            }
        }

        return null;
    }

    private HelperSession CreateSession(string exe, IHelperLauncher launcher)
    {
        var spec = new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 4 * 1024 * 1024, Seed = 42, Serial = "HLP0" }],
        };
        string specPath = Path.Combine(_dir, "devices.json");
        File.WriteAllText(specPath, spec.ToJson());
        return new HelperSession(new HelperSessionOptions
        {
            HelperPath = exe,
            Launcher = launcher,
            AppVersion = "test",
            ExtraArguments = ["--test-fake-devices", specPath, "--app-version", "test"],
            IdleMinutes = 0,
            RequestTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        });
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-02")]
    [Trait("Category", "Nightly")]
    public async Task The_helper_is_launched_once_and_reads_match()
    {
        string? exe = FindHelperExe();
        if (exe is null)
        {
            return; // ビルドがなければスキップ (CI は Elevated をビルドしてから実行する)。
        }

        var launcher = new DirectHelperLauncher();
        await using HelperSession session = CreateSession(exe, launcher);
        TrackLauncher(launcher);

        HelperClient client = await session.ConnectAsync();
        var devices = new HelperDeviceAccess(client);
        DeviceCatalog catalog = devices.Enumerate();
        Assert.Single(catalog.Disks);

        // 2 つの読み込み。昇格の要求 (起動) は 1 回だけ。
        using (IDeviceHandle handle1 = devices.Open(DevicePath.PhysicalDrive(0), writable: false))
        using (IDeviceHandle handle2 = devices.Open(DevicePath.PhysicalDrive(0), writable: false))
        {
            byte[] a = new byte[512];
            byte[] b = new byte[512];
            Assert.Equal(0, handle1.ReadSectors(0, a));
            Assert.Equal(0, handle2.ReadSectors(512, b));
            Assert.NotEqual(a, b);
        }

        await session.ConnectAsync();
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-09")]
    [Trait("Category", "Nightly")]
    public async Task Killing_the_helper_disconnects_and_reconnect_launches_again()
    {
        string? exe = FindHelperExe();
        if (exe is null)
        {
            return;
        }

        var launcher = new TrackingLauncher();
        await using HelperSession session = CreateSession(exe, launcher);
        var disconnected = new TaskCompletionSource();
        session.Disconnected += (_, _) => disconnected.TrySetResult();

        await session.ConnectAsync();
        Assert.True(session.IsConnected);
        int pid = session.HelperPid!.Value;

        // 補助プロセスを強制終了する (自分で起動したプロセスの PID だけを終了する)。
        Process helper = Process.GetProcessById(pid);
        helper.Kill();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsConnected);

        // 再接続すると、もう一度起動する (UAC の確認に相当)。
        HelperClient client = await session.ConnectAsync();
        Assert.True(session.IsConnected);
        Assert.Equal(2, launcher.LaunchCount);

        // 再接続後に読める。
        var devices = new HelperDeviceAccess(client);
        using IDeviceHandle handle = devices.Open(DevicePath.PhysicalDrive(0), writable: false);
        Assert.Equal(0, handle.ReadSectors(0, new byte[512]));
    }

    private void TrackLauncher(DirectHelperLauncher launcher) => _ = launcher;

    private sealed class TrackingLauncher : IHelperLauncher
    {
        private readonly DirectHelperLauncher _inner = new();

        public int LaunchCount => _inner.LaunchCount;

        public IHelperProcess Launch(string path, IReadOnlyList<string> arguments) => _inner.Launch(path, arguments);
    }

    public void Dispose()
    {
        foreach (Process p in _started)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
