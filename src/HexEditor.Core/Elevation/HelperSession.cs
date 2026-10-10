using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Elevation;

/// <summary>利用者が UAC の確認で「いいえ」を選んだ、または管理者の資格情報がない (ENG-28 の仕様 8)。</summary>
public sealed class HelperElevationDeclinedException() : OperationCanceledException("Administrator rights were not granted.");

/// <summary>補助プロセスのファイルが見つからない (ENG-28 の仕様 12)。</summary>
public sealed class HelperNotFoundException(string path) : FileNotFoundException("The elevated helper was not found.", path);

/// <summary>補助プロセスのファイルのハッシュが本体に埋め込んだ値と違う (PKG-14 の仕様 2)。起動しない。</summary>
public sealed class HelperTamperedException(string path) : IOException($"The elevated helper may have been tampered with: {path}");

/// <summary>起動した補助プロセス。</summary>
public interface IHelperProcess : IDisposable
{
    int Pid { get; }

    bool HasExited { get; }

    /// <summary>終了したら完了する。</summary>
    Task Exited { get; }
}

/// <summary>補助プロセスの起動。本物は UAC の確認を出す (<c>runas</c>)。テストは昇格せずに起動する。</summary>
public interface IHelperLauncher
{
    /// <exception cref="HelperElevationDeclinedException">UAC の確認で拒否された。</exception>
    IHelperProcess Launch(string path, IReadOnlyList<string> arguments);

    /// <summary>起動した回数 (= 昇格の要求の回数。TC-ENG-28-02)。</summary>
    int LaunchCount { get; }
}

/// <summary>補助プロセスとの接続の設定。</summary>
public sealed record HelperSessionOptions
{
    /// <summary><c>HexEditor.Elevated.exe</c> のパス (本体と同じフォルダ。PKG-14 の仕様 1)。</summary>
    public required string HelperPath { get; init; }

    public required IHelperLauncher Launcher { get; init; }

    public string AppVersion { get; init; } = string.Empty;

    /// <summary>本体に埋め込んだ補助プロセスの SHA-256 (16 進)。null なら確かめない (開発中のビルド)。</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>1 つの要求の待ち時間の上限 (ENG-28 の仕様 5)。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>補助プロセスが接続してくるまでの上限 (UAC の確認の時間を含む)。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>開いているハンドルが 0 になってから補助プロセスが終了するまで (分)。0 なら「アプリの終了まで残す」。</summary>
    public int IdleMinutes { get; init; } = 10;

    /// <summary>補助プロセスのログの置き場所。</summary>
    public string? LogPath { get; init; }

    /// <summary>補助プロセスに追加で渡す引数 (テスト用の偽のデバイスなど)。</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>このアプリのログ。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// パイプ名 (テスト用。null なら接続のたびにランダムに作る)。同じ名前のパイプが先にあると作れないこと (ENG-28 の仕様 2 の 1。
    /// TC-ENG-28-07 の手順 3) を確かめるために使う。
    /// </summary>
    public string? PipeName { get; init; }

    /// <summary>このプロセスの ID (テストで差し替えない)。</summary>
    public int CurrentPid { get; init; } = Environment.ProcessId;
}

/// <summary>
/// UI のプロセスから見た補助プロセス (ENG-28)。管理者権限が要る操作で初めて必要になったときに起動し (UAC の確認は 1 回)、以後は同じ
/// 補助プロセスを使い続ける。補助プロセスが異常終了したら <see cref="Disconnected"/> を通知する (仕様 7。再接続は
/// <see cref="ConnectAsync"/> をもう一度呼ぶ。UAC の確認が出る)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperSession : IAsyncDisposable
{
    private readonly HelperSessionOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private HelperClient? _client;
    private IHelperProcess? _process;
    private bool _disposed;
    private bool _writeBlock;

    public HelperSession(HelperSessionOptions options)
    {
        _options = options;
    }

    /// <summary>補助プロセスが動いていて接続している (ステータスバーの盾のアイコン)。</summary>
    public bool IsConnected => _client is { IsConnected: true };

    public HelperClient? Client => _client;

    /// <summary>今の補助プロセスの ID。</summary>
    public int? HelperPid => _process?.Pid;

    /// <summary>今の接続のパイプ名 (テスト用)。</summary>
    public string? PipeName { get; private set; }

    /// <summary>接続した・切れた (UI スレッドとは限らない)。</summary>
    public event EventHandler? StateChanged;

    /// <summary>補助プロセスが終了して接続が切れた (仕様 7)。利用者の操作で終わらせたときは通知しない。</summary>
    public event EventHandler? Disconnected;

    /// <summary>補助プロセスのファイルがあるか (仕様 12 の判定)。</summary>
    public bool HelperExists => File.Exists(_options.HelperPath);

    /// <summary>
    /// 接続している補助プロセスを返す。なければ起動して接続する (UAC の確認が出る)。
    /// </summary>
    /// <exception cref="HelperNotFoundException">補助プロセスのファイルがない。</exception>
    /// <exception cref="HelperTamperedException">ハッシュが違う。</exception>
    /// <exception cref="HelperElevationDeclinedException">UAC の確認で拒否された。</exception>
    public async Task<HelperClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is { IsConnected: true } existing)
            {
                return existing;
            }

            await CleanupAsync().ConfigureAwait(false);
            string path = _options.HelperPath;
            if (!File.Exists(path))
            {
                throw new HelperNotFoundException(path);
            }

            VerifyIntegrity(path);
            string name = _options.PipeName ?? HelperPipes.NewPipeName();
            byte[] secret = HelperPipes.NewSecret();

            // 同じ名前のパイプを他のプロセスが先に作っていたら、ここで失敗する (補助プロセスは起動しない。TC-ENG-28-07)。
            NamedPipeServerStream pipe = HelperPipes.CreateServer(name);
            IHelperProcess? process = null;
            try
            {
                var args = new List<string>
                {
                    "--pipe", name,
                    "--parent", _options.CurrentPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--secret", Convert.ToBase64String(secret),
                    "--idle-minutes", _options.IdleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (_options.LogPath is { } log)
                {
                    args.AddRange(["--log", log]);
                }

                args.AddRange(_options.ExtraArguments);
                Log($"Launching the elevated helper ({name})");
                process = _options.Launcher.Launch(path, args);
                int pid = process.Pid;
                using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task accept = HelperPipes.AcceptAsync(pipe, () => pid, secret, _options.ConnectTimeout,
                    reason => Log("Helper pipe: rejected a client: " + reason), stopWaiting.Token);
                if (await Task.WhenAny(accept, process.Exited).ConfigureAwait(false) != accept)
                {
                    await stopWaiting.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await accept.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    throw new IOException("The elevated helper exited before connecting.");
                }

                await accept.ConfigureAwait(false);
                var client = new HelperClient(pipe) { Timeout = _options.RequestTimeout };
                (uint status, byte[] body) = await client.SendAsync(HelperCommand.Hello, 0,
                    new BodyWriter().U32(HelperProtocol.Version).Text(_options.AppVersion).ToArray(), cancellationToken).ConfigureAwait(false);
                var reader = new BodyReader(body);
                uint version = status == 0 ? reader.U32() : 0;
                string appVersion = status == 0 ? reader.Text() : string.Empty;
                if (version != HelperProtocol.Version || appVersion != _options.AppVersion)
                {
                    // 版が違う補助プロセス (仕様 4 の Hello): 終了させる (次の要求で起動し直す)。
                    Log($"Helper version mismatch ({version} {appVersion})");
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new IOException("The elevated helper is a different version.");
                }

                if (_writeBlock)
                {
                    await client.SendAsync(HelperCommand.SetWriteBlock, 0, new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                }

                client.Disconnected += OnClientDisconnected;
                _client = client;
                _process = process;
                PipeName = name;
                process = null;
                Log("Connected to the elevated helper");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return client;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                process?.Dispose();
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>書き込み保護モード (FOR-01) の状態を補助プロセスに通知する (ENG-28 の仕様 4)。</summary>
    public async Task SetWriteBlockAsync(bool enabled)
    {
        _writeBlock = enabled;
        if (_client is { IsConnected: true } client)
        {
            await client.SendAsync(HelperCommand.SetWriteBlock, 0, new[] { enabled ? (byte)1 : (byte)0 }).ConfigureAwait(false);
        }
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        Log("The elevated helper disconnected");
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (!_disposed)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void VerifyIntegrity(string path)
    {
        if (_options.ExpectedSha256 is not { Length: > 0 } expected)
        {
            return;
        }

        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Helper hash mismatch: {actual}");
            throw new HelperTamperedException(path);
        }
    }

    private async Task CleanupAsync()
    {
        if (_client is { } client)
        {
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _process?.Dispose();
        _process = null;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>アプリの終了: 接続を閉じる (補助プロセスはパイプが切れたことで終了する。ENG-17 の仕様 9)。</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CleanupAsync().ConfigureAwait(false);
    }
}

/// <summary>UAC の確認を出して補助プロセスを起動する (<c>ShellExecuteEx</c> の <c>runas</c>。PKG-14 の仕様 2)。</summary>
[SupportedOSPlatform("windows")]
public sealed class RunAsHelperLauncher : IHelperLauncher
{
    private int _count;

    public int LaunchCount => _count;

    public IHelperProcess Launch(string path, IReadOnlyList<string> arguments)
    {
        Interlocked.Increment(ref _count);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            Process process = Process.Start(info) ?? throw new IOException("The elevated helper did not start.");
            return new StartedProcess(process);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32Errors.Cancelled)
        {
            throw new HelperElevationDeclinedException();
        }
    }
}

/// <summary>昇格せずに補助プロセスを起動する (テスト用。補助プロセスの通信をテストで確かめる)。</summary>
public sealed class DirectHelperLauncher : IHelperLauncher
{
    private int _count;

    public int LaunchCount => _count;

    public IHelperProcess Launch(string path, IReadOnlyList<string> arguments)
    {
        Interlocked.Increment(ref _count);
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        Process process = Process.Start(info) ?? throw new IOException("The helper did not start.");
        return new StartedProcess(process);
    }
}

internal sealed class StartedProcess : IHelperProcess
{
    private readonly Process _process;

    public StartedProcess(Process process)
    {
        _process = process;
        Pid = process.Id;
        Exited = process.WaitForExitAsync();
    }

    public int Pid { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public Task Exited { get; }

    public void Dispose() => _process.Dispose();
}
