using System.Diagnostics;
using System.IO.Pipes;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Tests.Engine;

namespace HexEditor.Core.Tests.Devices;

/// <summary>
/// 補助プロセスのパイプへの、自分で起動していないプロセスからの接続の拒否 (ENG-28 の仕様 2、TC-ENG-28-07)。
/// <c>HexEditor.Elevated.exe</c> (テスト用のビルド) を昇格せずに偽のデバイスで起動し、UI のプロセスと同じ <see cref="HelperSession"/> で
/// 接続する。別のテスト用のプロセス (Windows PowerShell) がパイプに接続して要求・合言葉を送る。昇格・実機のディスク・インストールは
/// 使わない。補助プロセスのビルドがなければスキップする。
/// </summary>
[Collection("ElevatedHelper")]
[Trait("Category", "Nightly")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HelperPipeSecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-pipe").FullName;
    private readonly List<Process> _rogues = [];

    private static string? FindHelperExe()
    {
        string csproj = SourceTests.FindRepoFile("src/HexEditor.Elevated/HexEditor.Elevated.csproj");
        string bin = Path.Combine(Path.GetDirectoryName(csproj)!, "bin", "Debug");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "HexEditor.Elevated.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    private HelperSession CreateSession(string exe, IHelperLauncher launcher, List<string> log, string? pipeName = null)
    {
        var spec = new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 4 * 1024 * 1024, Seed = 7, Serial = "PIPE0" }],
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
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PipeName = pipeName,
            Log = m =>
            {
                lock (log)
                {
                    log.Add(m);
                }
            },
        });
    }

    /// <summary>
    /// 別のテスト用のプロセス: パイプに接続し、<paramref name="hex"/> を送って応答を待つ。標準出力の 1 行目は "CONNECTED" か
    /// "NOT-CONNECTED"、2 行目は "EOF" (切断された)、"DATA n" (応答を受け取った)、"NO-RESPONSE" (応答がない)、"CLOSED" (書き込み・読み込みの失敗)。
    /// </summary>
    private Process StartRogue(string pipe, string hex)
    {
        string script = "$ErrorActionPreference = 'Stop'; " +
            $"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{pipe}', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous); " +
            "try { $p.Connect(8000) } catch { [Console]::Out.WriteLine('NOT-CONNECTED'); exit 0 }; " +
            "[Console]::Out.WriteLine('CONNECTED'); [Console]::Out.Flush(); " +
            $"$hex = '{hex}'; $b = New-Object byte[] ($hex.Length / 2); for ($i = 0; $i -lt $b.Length; $i++) {{ $b[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16) }}; " +
            "try { $p.Write($b, 0, $b.Length); $p.Flush(); $buf = New-Object byte[] 4096; $t = $p.ReadAsync($buf, 0, 4096); " +
            "if ($t.Wait(5000)) { if ($t.Result -eq 0) { 'EOF' } else { 'DATA ' + $t.Result } } else { 'NO-RESPONSE' } } catch { 'CLOSED' }";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        Process process = Process.Start(info) ?? throw new InvalidOperationException("powershell.exe did not start.");
        _rogues.Add(process);
        return process;
    }

    /// <summary>補助プロセスを起動する前に、別のテスト用のプロセスをパイプに接続させる起動 (手順 1・2)。</summary>
    private sealed class RogueFirstLauncher(HelperPipeSecurityTests owner) : IHelperLauncher
    {
        private readonly DirectHelperLauncher _inner = new();

        public int LaunchCount => _inner.LaunchCount;

        public List<Process> Rogues { get; } = [];

        public IHelperProcess Launch(string path, IReadOnlyList<string> arguments)
        {
            string pipe = arguments[arguments.ToList().IndexOf("--pipe") + 1];
            byte[] secret = Convert.FromBase64String(arguments[arguments.ToList().IndexOf("--secret") + 1]);

            // 1. Hello (要求の形。合言葉なし) を送るプロセス。接続できるまで待つ (UI のプロセスはまだ相手を確かめていない)。
            byte[] hello = new BodyWriter().U32(HelperProtocol.Version).Text("test").ToArray();
            string helloFrame = Convert.ToHexString(HelperProtocol.Request(1, HelperCommand.Hello, 0, hello));
            Process first = owner.StartRogue(pipe, helloFrame);
            Rogues.Add(first);
            string? line = first.StandardOutput.ReadLine();
            Assert.Equal("CONNECTED", line);

            // 2. 正しい合言葉を知っているが、アプリが起動したものではないプロセス (合言葉の後に Hello)。
            Rogues.Add(owner.StartRogue(pipe, Convert.ToHexString(secret) + helloFrame));

            return _inner.Launch(path, arguments);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-07")]
    public async Task Connections_from_processes_the_app_did_not_start_are_rejected()
    {
        string? exe = FindHelperExe();
        if (exe is null)
        {
            return; // ビルドがなければスキップ (CI は Elevated をビルドしてから実行する)。
        }

        var log = new List<string>();
        var launcher = new RogueFirstLauncher(this);
        await using HelperSession session = CreateSession(exe, launcher, log);

        // 手順 1・2 の接続はアプリ (HelperSession) に切断され、どの要求にも応答しない。本物の補助プロセスだけが接続できる。
        HelperClient client = await session.ConnectAsync();
        foreach (Process rogue in launcher.Rogues)
        {
            Assert.True(rogue.WaitForExit(30_000), "the test process did not finish");
            string output = (await rogue.StandardOutput.ReadToEndAsync()).Trim();
            Assert.DoesNotContain("DATA", output, StringComparison.Ordinal);
        }

        lock (log)
        {
            Assert.Contains(log, l => l.Contains("rejected a client", StringComparison.Ordinal));
        }

        // アプリのディスクの読み込みは影響を受けない。
        var devices = new HelperDeviceAccess(client);
        using (IDeviceHandle handle = devices.Open(DevicePath.PhysicalDrive(0), writable: false))
        {
            Assert.Equal(0, handle.ReadSectors(0, new byte[512]));
        }

        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-07")]
    public async Task A_pipe_created_first_by_someone_else_stops_the_connection()
    {
        string? exe = FindHelperExe();
        if (exe is null)
        {
            return;
        }

        // 3. 同じ名前のパイプを先に作っておく (別の利用者のプロセスの代わりに、このテストが作って持っておく)。
        string name = HelperPipes.NewPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task squatterConnected = squatter.WaitForConnectionAsync();

        var log = new List<string>();
        var launcher = new DirectHelperLauncher();
        await using HelperSession session = CreateSession(exe, launcher, log, name);

        // アプリのパイプの作成が失敗し、補助プロセスを起動しない (他のプロセスのパイプに合言葉が送られることはない)。
        await Assert.ThrowsAnyAsync<Exception>(() => session.ConnectAsync());
        Assert.Equal(0, launcher.LaunchCount);
        Assert.False(session.IsConnected);
        await Task.WhenAny(squatterConnected, Task.Delay(1000));
        Assert.False(squatterConnected.IsCompleted, "something connected to the squatting pipe");
    }

    public void Dispose()
    {
        foreach (Process p in _rogues)
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

            p.Dispose();
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
