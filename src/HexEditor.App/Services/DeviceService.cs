using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Processes;
using HexEditor.Platform;

namespace HexEditor.App.Services;

/// <summary>管理者権限が必要な操作を開く経路 (ENG-28、ENG-29 の仕様 3、ENG-32 の仕様 4)。</summary>
public enum OpenRoute
{
    /// <summary>UI のプロセスで直接開ける (USB ストレージのボリューム、同じユーザーの昇格していないプロセス)。</summary>
    Direct,

    /// <summary>管理者権限が必要。補助プロセスで開く。</summary>
    Helper,

    /// <summary>アプリ全体を管理者として実行中。同じプロセスで開く (ENG-28 の仕様 10)。</summary>
    SameProcess,

    /// <summary>補助プロセスも管理者権限も使えない。案内ダイアログを出す (ENG-28 の仕様 12)。</summary>
    GuidanceNeeded,

    /// <summary>管理者権限があっても開けない (保護されたプロセス)。</summary>
    Impossible,
}

/// <summary>
/// ディスク・プロセスを開く経路を決め、必要なら補助プロセス (ENG-28) を起動する。偽のデバイス (テスト) を注入でき、
/// 本物のデバイスに触れずに UI を確かめられる。実機のディスクへの書き込み・昇格はこのクラスを通してのみ行う。
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    private readonly IAppEnvironment _env;
    private readonly IDeviceAccess _directDevices;
    private readonly IProcessAccess _directProcesses;
    private readonly IDeviceAccess? _fakeElevatedDevices;
    private readonly IProcessAccess? _fakeElevatedProcesses;
    private readonly string _helperPath;
    private HelperSession? _session;
    private bool _fakeHelperActive;

    public DeviceService(IAppEnvironment env)
    {
        _env = env;
        _helperPath = Path.Combine(AppContext.BaseDirectory, "HexEditor.Elevated.exe");

        IDeviceAccess? directDevices = null;
        IProcessAccess? directProcesses = null;
#if HEX_TEST_HOOKS
        // テスト: 偽のデバイス。直接の経路は「管理者権限なし」を再現し (RequiresAdmin のものは拒否)、
        // 補助プロセス・同じプロセスの経路は昇格した偽のアクセスを使う (実際には昇格しない)。
        if (TestHooks.Active && TestHooks.FakeDevices is { } fakeDeviceAccess)
        {
            directDevices = fakeDeviceAccess.Direct;
            _fakeElevatedDevices = fakeDeviceAccess.Elevated;
        }

        if (TestHooks.Active && TestHooks.FakeProcesses is { } fakeProcessAccess)
        {
            directProcesses = fakeProcessAccess.Direct;
            _fakeElevatedProcesses = fakeProcessAccess.Elevated;
        }
#endif
        _directDevices = directDevices ?? Win32DeviceAccess.Instance;
        _directProcesses = directProcesses ?? Win32ProcessAccess.Instance;

        if (IsElevated)
        {
            Win32ProcessAccess.EnableDebugPrivilege();
        }
    }

    /// <summary>アプリ全体が管理者として動いている (どの配布形態でも。ENG-28 の仕様 10)。</summary>
    public bool IsElevated => TestHooks.SimulatesElevation || (OperatingSystem.IsWindows() && Win32ProcessAccess.IsCurrentProcessElevated);

    /// <summary>本体に埋め込んだ補助プロセスの SHA-256 (PKG-14 の仕様 2)。開発中のビルドでは空 (照合しない)。</summary>
    private static string? HelperSha256 =>
        typeof(DeviceService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HelperSha256")?.Value is { Length: > 0 } hash ? hash : null;

    /// <summary>補助プロセスを起動できる配布形態 (インストーラ・ポータブル)。</summary>
    public bool HelperSupported => _env.Distribution is Distribution.Installer or Distribution.Portable or Distribution.Development;

    /// <summary>補助プロセスのファイルがある、または偽のデバイスがある。</summary>
    public bool HelperAvailable => HelperSupported && (_fakeElevatedDevices is not null || _fakeElevatedProcesses is not null || File.Exists(_helperPath));

    /// <summary>補助プロセスが動いている (ステータスバーの盾のアイコン。ENG-28 の「画面」)。</summary>
    public bool IsHelperRunning => _fakeHelperActive || _session is { IsConnected: true };

    /// <summary>補助プロセスの状態が変わった。</summary>
    public event EventHandler? HelperStateChanged;

    /// <summary>補助プロセスが異常終了した (ENG-28 の仕様 7)。</summary>
    public event EventHandler? HelperDisconnected;

    /// <summary>ディスク・ボリュームの一覧 (管理者権限なしで取得。ENG-29 の仕様 1)。</summary>
    public DeviceCatalog EnumerateDevices() => (_fakeElevatedDevices ?? _directDevices).Enumerate();

    /// <summary>プロセスの一覧 (ENG-32 の仕様 1)。</summary>
    public IReadOnlyList<ProcessEntry> EnumerateProcesses() => (_fakeElevatedProcesses ?? _directProcesses).Enumerate();

    /// <summary>ディスク・ボリュームを開く経路を決める (ENG-29 の仕様 3)。</summary>
    public OpenRoute RouteForDisk(bool isRemovableUsbVolume)
    {
        if (isRemovableUsbVolume)
        {
            return OpenRoute.Direct;
        }

        if (IsElevated)
        {
            return OpenRoute.SameProcess;
        }

        return HelperAvailable ? OpenRoute.Helper : OpenRoute.GuidanceNeeded;
    }

    /// <summary>プロセスを開く経路を決める (ENG-32 の仕様 4)。</summary>
    public OpenRoute RouteForProcess(ProcessAccessLevel access) => access switch
    {
        ProcessAccessLevel.Protected => OpenRoute.Impossible,
        ProcessAccessLevel.Direct => OpenRoute.Direct,
        _ when IsElevated => OpenRoute.SameProcess,
        _ => HelperAvailable ? OpenRoute.Helper : OpenRoute.GuidanceNeeded,
    };

    /// <summary>
    /// デバイスを開く。<paramref name="route"/> が <see cref="OpenRoute.Helper"/> のときは補助プロセスを起動する (UAC の確認が出る)。
    /// </summary>
    public async Task<DeviceByteSource> OpenDeviceAsync(DeviceOpenInfo info, bool writable, OpenRoute route, CancellationToken cancellationToken = default)
    {
        IDeviceAccess access = await DeviceAccessForAsync(route, cancellationToken).ConfigureAwait(true);
        IDeviceHandle handle = access.Open(info.Path, writable);
        try
        {
            return new DeviceByteSource(handle, access, info with { Route = ToDeviceRoute(route) });
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>プロセスメモリを開く。</summary>
    public async Task<ProcessMemoryByteSource> OpenProcessAsync(int pid, ProcessOpenInfo info, bool writable, OpenRoute route, CancellationToken cancellationToken = default)
    {
        IProcessAccess access = await ProcessAccessForAsync(route, cancellationToken).ConfigureAwait(true);
        IProcessMemory memory = access.Open(pid, writable);
        try
        {
            return new ProcessMemoryByteSource(memory, access, info with { Route = ToDeviceRoute(route) });
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    /// <summary>与えられた経路のデバイスのアクセス手段 (補助プロセスなら接続する)。</summary>
    public async Task<IDeviceAccess> DeviceAccessForAsync(OpenRoute route, CancellationToken cancellationToken = default)
    {
        switch (route)
        {
            case OpenRoute.Direct:
                return _directDevices;
            case OpenRoute.SameProcess:
                return _fakeElevatedDevices ?? Win32DeviceAccess.Instance;
            case OpenRoute.Helper when _fakeElevatedDevices is not null:
                MarkFakeHelperRunning();
                return _fakeElevatedDevices;
            case OpenRoute.Helper:
                HelperClient client = await ConnectHelperAsync(cancellationToken).ConfigureAwait(true);
                return new HelperDeviceAccess(client);
            default:
                throw new InvalidOperationException($"Cannot open a device by the route {route}.");
        }
    }

    public async Task<IProcessAccess> ProcessAccessForAsync(OpenRoute route, CancellationToken cancellationToken = default)
    {
        switch (route)
        {
            case OpenRoute.Direct:
                return _directProcesses;
            case OpenRoute.SameProcess:
                return _fakeElevatedProcesses ?? Win32ProcessAccess.Instance;
            case OpenRoute.Helper when _fakeElevatedProcesses is not null:
                MarkFakeHelperRunning();
                return _fakeElevatedProcesses;
            case OpenRoute.Helper:
                HelperClient client = await ConnectHelperAsync(cancellationToken).ConfigureAwait(true);
                return new HelperProcessAccess(client, _directProcesses, WatchProcessExit);
            default:
                throw new InvalidOperationException($"Cannot open a process by the route {route}.");
        }
    }

    private void MarkFakeHelperRunning()
    {
        if (!_fakeHelperActive)
        {
            _fakeHelperActive = true;
            HelperStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<HelperClient> ConnectHelperAsync(CancellationToken cancellationToken)
    {
        _session ??= CreateSession();
        HelperClient client = await _session.ConnectAsync(cancellationToken).ConfigureAwait(true);
        HelperStateChanged?.Invoke(this, EventArgs.Empty);
        return client;
    }

    private HelperSession CreateSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var session = new HelperSession(new HelperSessionOptions
        {
            HelperPath = _helperPath,
            Launcher = new RunAsHelperLauncher(),
            AppVersion = _env.AppVersion,
            ExpectedSha256 = HelperSha256,
            IdleMinutes = 10,
            Log = AppLog.Info,
        });
        session.StateChanged += (_, _) => HelperStateChanged?.Invoke(this, EventArgs.Empty);
        session.Disconnected += (_, _) => HelperDisconnected?.Invoke(this, EventArgs.Empty);
        return session;
    }

    private static DeviceRoute ToDeviceRoute(OpenRoute route) => route switch
    {
        OpenRoute.Helper => DeviceRoute.Helper,
        OpenRoute.SameProcess => DeviceRoute.Elevated,
        _ => DeviceRoute.Direct,
    };

    /// <summary>対象のプロセスの終了を見張る (補助プロセス経由で開いた場合)。</summary>
    private IDisposable? WatchProcessExit(int pid, Action onExit)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => onExit();
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>補助プロセスに書き込み保護モード (FOR-01) の状態を通知する。</summary>
    public async Task SetWriteBlockAsync(bool enabled)
    {
        if (_session is { } session)
        {
            await session.SetWriteBlockAsync(enabled).ConfigureAwait(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
