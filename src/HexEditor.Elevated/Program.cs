using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Processes;

namespace HexEditor.Elevated;

/// <summary>
/// 管理者権限の補助プロセス (ENG-28、PKG-14)。引数: <c>--pipe 名前 --parent PID --secret 合言葉 (Base64) [--idle-minutes N] [--log パス]</c>。
/// 本体が作ったパイプに接続し (パイプのサーバー側が親のプロセスであることを確かめてから合言葉を送る)、決められた要求だけを処理する。
/// 親のプロセスが終了した・パイプが切れた・許可リストにない要求を受け取った・ハンドルがない状態が続いたときに終了する。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArguments = 2;
    private const int ExitHandshake = 3;
    private const int ExitRejected = 4;

    private static StreamWriter? _log;

    private static int Main(string[] args)
    {
        // Windows のエラー報告の画面を出さない (UI を持たないプロセス)。
        SetErrorMode(0x0001 | 0x0002);
        Dictionary<string, string>? options = Parse(args);
        if (options is null)
        {
            return ExitBadArguments;
        }

        if (options.TryGetValue("--log", out string? logPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
                _log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _log = null;
            }
        }

        try
        {
            return RunAsync(options).GetAwaiter().GetResult();
        }
        finally
        {
            _log?.Dispose();
        }
    }

    private static Dictionary<string, string>? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }

        return values.ContainsKey("--pipe") && values.ContainsKey("--parent") && values.ContainsKey("--secret") ? values : null;
    }

    private static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        string pipeName = options["--pipe"];
        if (!pipeName.StartsWith(HelperPipes.Prefix, StringComparison.Ordinal)
            || !int.TryParse(options["--parent"], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid))
        {
            Log("Bad arguments");
            return ExitBadArguments;
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(options["--secret"]);
        }
        catch (FormatException)
        {
            Log("Bad secret");
            return ExitBadArguments;
        }

        int idleMinutes = options.TryGetValue("--idle-minutes", out string? idle) && int.TryParse(idle, NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            ? m : 10;

        // 親のプロセスの終了を見張る (パイプが切れない場合も 5 秒以内に終了する。ENG-28 の仕様 6)。
        using var stop = new CancellationTokenSource();
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => stop.Cancel();
            if (parent.HasExited)
            {
                return ExitOk;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log("The parent process is not running");
            return ExitOk;
        }

        using (parent)
        {
            System.IO.Pipes.NamedPipeClientStream pipe;
            try
            {
                pipe = await HelperPipes.ConnectAsync(pipeName, parentPid, secret, TimeSpan.FromSeconds(30), stop.Token);
            }
            catch (Exception ex) when (ex is HelperHandshakeException or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                Log("Handshake failed: " + ex.Message);
                return ExitHandshake;
            }

            if (Backend(options) is not ({ } devices, { } processes))
            {
                await pipe.DisposeAsync();
                return ExitBadArguments;
            }

            Log($"Connected to {parentPid}");
            string version = options.TryGetValue("--app-version", out string? v) ? v : AppVersion();
            var operations = new PrivilegedOperations(devices, processes, Log, version);
            var server = new HelperServer(pipe, operations, Log) { IdleTimeout = idleMinutes <= 0 ? null : TimeSpan.FromMinutes(idleMinutes) };
            HelperServerExit exit = await server.RunAsync(stop.Token);
            (devices as IDisposable)?.Dispose();
            return exit == HelperServerExit.Rejected ? ExitRejected : ExitOk;
        }
    }

    private static (IDeviceAccess Devices, IProcessAccess Processes)? Backend(Dictionary<string, string> options)
    {
#if HEX_TEST_HOOKS
        // テスト用のビルドだけ: 偽のデバイス・プロセスで動かす (補助プロセスの通信を、実際のディスクに触れずに確かめる)。
        if (options.TryGetValue("--test-fake-devices", out string? deviceSpec))
        {
            FakeDeviceSpec spec = FakeDeviceSpec.Parse(File.ReadAllText(deviceSpec));
            FakeProcessListSpec processSpec = options.TryGetValue("--test-fake-processes", out string? p)
                ? FakeProcessListSpec.Parse(File.ReadAllText(p)) : new FakeProcessListSpec();
            Log("Using fake devices (test hooks)");
            return (new FakeDeviceAccess(spec, elevated: true), new FakeProcessAccess(processSpec, elevated: true));
        }
#else
        // 製品版のビルドは偽のデバイスを持たない。テスト用の引数を渡されたら、実際のディスク・プロセスに触れずに止める
        // (テスト用でないビルドがテストから起動されても、実機のデバイスを開かない)。
        if (options.ContainsKey("--test-fake-devices") || options.ContainsKey("--test-fake-processes"))
        {
            Log("Test arguments given to a build without test hooks; refusing to run");
            return null;
        }
#endif
        Win32ProcessAccess.EnableDebugPrivilege();
        return (Win32DeviceAccess.Instance, Win32ProcessAccess.Instance);
    }

    private static string AppVersion() =>
        typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? string.Empty;

    private static void Log(string message)
    {
        try
        {
            _log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Elevated {Environment.ProcessId}] {message}");
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
