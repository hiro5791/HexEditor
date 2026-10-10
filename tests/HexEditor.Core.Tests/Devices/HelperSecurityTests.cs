using System.IO.Pipes;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;

namespace HexEditor.Core.Tests.Devices;

/// <summary>
/// 補助プロセスのパイプの接続の確認 (ENG-28 の仕様 2、PKG-14 の仕様 2・3)。昇格はせず、このテストのプロセスの中でパイプのサーバーと
/// クライアントを作って確かめる (実際のディスク・補助プロセスには触れない)。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HelperSecurityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    [Trait("TC", "TC-ENG-28-07")]
    [Trait("TC", "TC-PKG-14-05")]
    public async Task A_client_that_is_not_the_launched_helper_is_disconnected()
    {
        string name = HelperPipes.NewPipeName();
        byte[] secret = HelperPipes.NewSecret();
        await using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        var rejected = new List<string>();

        // 起動した補助プロセスの ID は、このプロセスとは別の値 (接続してくるテストのクライアントは「自分で起動していないプロセス」)。
        int helperPid = Environment.ProcessId + 1;
        using var stop = new CancellationTokenSource(Timeout);
        Task accept = HelperPipes.AcceptAsync(server, () => helperPid, secret, Timeout, r =>
        {
            lock (rejected)
            {
                rejected.Add(r);
            }
        }, stop.Token);

        // 手順 1: 合言葉を知らないクライアントが Hello を送る。手順 2: 正しい合言葉を送る (PID が違うため拒否される)。
        foreach (byte[] payload in new[] { HelperProtocol.Request(1, HelperCommand.Hello, 0, new BodyWriter().U32(1).Text("x").ToArray()), secret })
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(Timeout, stop.Token);
            try
            {
                await client.WriteAsync(payload, stop.Token);
                await client.FlushAsync(stop.Token);
            }
            catch (IOException)
            {
                // 既に切断されていれば書けない (拒否の結果として正しい)。
            }

            // サーバーは何も答えずに切断する (読むと 0 バイト、または切断の例外)。
            byte[] answer = new byte[16];
            int n;
            try
            {
                n = await client.ReadAsync(answer, stop.Token);
            }
            catch (IOException)
            {
                n = 0;
            }

            Assert.Equal(0, n);
        }

        Assert.False(accept.IsCompleted);
        lock (rejected)
        {
            Assert.True(rejected.Count >= 2, string.Join("; ", rejected));
            Assert.All(rejected, r => Assert.Contains("not the helper", r, StringComparison.Ordinal));
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-07")]
    public async Task A_wrong_secret_from_the_expected_process_is_disconnected_and_the_right_one_is_accepted()
    {
        string name = HelperPipes.NewPipeName();
        byte[] secret = HelperPipes.NewSecret();
        await using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        int rejectedCount = 0;
        using var stop = new CancellationTokenSource(Timeout);
        Task accept = HelperPipes.AcceptAsync(server, () => Environment.ProcessId, secret, Timeout,
            _ => Interlocked.Increment(ref rejectedCount), stop.Token);

        byte[] wrong = (byte[])secret.Clone();
        wrong[0] ^= 0xFF;
        await using (var bad = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await bad.ConnectAsync(Timeout, stop.Token);
            await bad.WriteAsync(wrong, stop.Token);
            await bad.FlushAsync(stop.Token);
            byte[] probe = new byte[1];
            int n;
            try
            {
                n = await bad.ReadAsync(probe, stop.Token);
            }
            catch (IOException)
            {
                n = 0;
            }

            Assert.Equal(0, n);
        }

        Assert.False(accept.IsCompleted);

        // 正しい合言葉なら受け付ける。
        NamedPipeClientStream good = await HelperPipes.ConnectAsync(name, Environment.ProcessId, secret, Timeout, stop.Token);
        await using (good)
        {
            await accept.WaitAsync(Timeout);
            Assert.Equal(1, Volatile.Read(ref rejectedCount));
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-07")]
    public async Task A_pipe_created_first_by_another_process_makes_the_app_fail_and_the_helper_sends_no_secret()
    {
        string name = HelperPipes.NewPipeName();
        byte[] secret = HelperPipes.NewSecret();

        // 手順 3: 同じ名前のパイプを先に作っておく (なりすまし)。アプリのパイプの作成は失敗する (最初のインスタンスとして作るため)。
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.ThrowsAny<Exception>(() => HelperPipes.CreateServer(name).Dispose());

        // 補助プロセスの側: パイプのサーバーが引数の親のプロセスでなければ、合言葉を送らずに失敗する。
        using var stop = new CancellationTokenSource(Timeout);
        Task waiting = squatter.WaitForConnectionAsync(stop.Token);
        int parentPid = Environment.ProcessId + 1;
        await Assert.ThrowsAsync<HelperHandshakeException>(() => HelperPipes.ConnectAsync(name, parentPid, secret, Timeout, stop.Token));
        await waiting;
        byte[] received = new byte[HelperProtocol.SecretSize];
        int n;
        try
        {
            n = await squatter.ReadAsync(received, stop.Token);
        }
        catch (IOException)
        {
            n = 0;
        }

        Assert.Equal(0, n);
    }

    [Fact]
    [Trait("TC", "TC-PKG-14-06")]
    public async Task A_replaced_helper_is_not_launched()
    {
        string dir = Directory.CreateTempSubdirectory("hexeditor-tamper").FullName;
        try
        {
            // 本体に埋め込んだハッシュと違うファイル (差し替えた補助プロセス)。
            string exe = Path.Combine(dir, "HexEditor.Elevated.exe");
            await File.WriteAllBytesAsync(exe, "MZ not the helper"u8.ToArray());
            var launcher = new CountingLauncher();
            await using var session = new HelperSession(new HelperSessionOptions
            {
                HelperPath = exe,
                Launcher = launcher,
                ExpectedSha256 = new string('0', 64),
            });

            await Assert.ThrowsAsync<HelperTamperedException>(() => session.ConnectAsync());

            // UAC の確認の前に止まる (起動の要求をしない)。
            Assert.Equal(0, launcher.LaunchCount);
            Assert.False(session.IsConnected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-28-06")]
    public void Malformed_or_unknown_requests_are_rejected_and_unexpected_failures_are_answered()
    {
        using var access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1024 * 1024 }],
        });
        var operations = new PrivilegedOperations(access, new HexEditor.Core.Processes.FakeProcessAccess(new HexEditor.Core.Processes.FakeProcessListSpec(), elevated: true));

        // 許可リストにないコマンド、ファイルのパスを指定した OpenDevice、本体が足りないメッセージは拒否 (切断の対象)。
        Assert.Throws<HelperProtocolException>(() => operations.Execute(0x7777, 0, []));
        Assert.Throws<HelperProtocolException>(() => operations.Execute((ushort)HelperCommand.OpenDevice, 0, new BodyWriter().Text(@"C:\Windows\win.ini").ToArray()));
        Assert.Throws<HelperProtocolException>(() => operations.Execute((ushort)HelperCommand.ReadSectors, 0, [1, 2]));

        // セクタ境界に揃っていない読み込みは、OS に渡さずに ERROR_INVALID_PARAMETER で答える。
        (uint status, byte[] body) = operations.Execute((ushort)HelperCommand.OpenDevice, 0, new BodyWriter().Text(DevicePath.PhysicalDrive(0)).ToArray());
        Assert.Equal(0u, status);
        uint id = new BodyReader(body).U32();
        (status, _) = operations.Execute((ushort)HelperCommand.ReadSectors, 0, new BodyWriter().U32(id).I64(100).U32(512).ToArray());
        Assert.Equal((uint)Win32Errors.InvalidParameter, status);

        // 読み取りのみで開いたハンドルへの書き込みはアクセス拒否。
        (status, _) = operations.Execute((ushort)HelperCommand.WriteSectors, 0, new BodyWriter().U32(id).I64(0).Bytes(new byte[512]).ToArray());
        Assert.Equal((uint)Win32Errors.AccessDenied, status);
        operations.Dispose();
    }

    [Fact]
    public void Exceptions_never_map_to_success()
    {
        // BitLocker でロックされたボリュームの読み込みの例外 (HRESULT 0x80310000) を 0 (成功) にしない。
        Assert.Equal(Win32Errors.FveLocked, Win32DeviceAccess.ErrorOf(new IOException("locked", Win32Errors.FveLocked)));
        Assert.Equal(Win32Errors.SectorNotFound, Win32DeviceAccess.ErrorOf(new IOException("crc", unchecked((int)0x8007001B))));
        Assert.Equal(Win32Errors.GenFailure, Win32DeviceAccess.ErrorOf(new IOException("zero", unchecked((int)0x80070000))));
        Assert.Equal(Win32Errors.InvalidHandle, Win32DeviceAccess.ErrorOf(new ObjectDisposedException("handle")));
        Assert.Equal(Win32Errors.AccessDenied, Win32DeviceAccess.ErrorOf(new UnauthorizedAccessException()));
    }

    private sealed class CountingLauncher : IHelperLauncher
    {
        public int LaunchCount { get; private set; }

        public IHelperProcess Launch(string path, IReadOnlyList<string> arguments)
        {
            LaunchCount++;
            throw new InvalidOperationException("Must not be launched.");
        }
    }
}
