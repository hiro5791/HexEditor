using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Platform.Automation;

/// <summary>名前付きパイプに接続してきたクライアント (08 の AUTO-45 の仕様 4)。</summary>
/// <param name="Stream">接続 (閉じるとクライアントとの接続が切れる)。</param>
/// <param name="ProcessId">クライアントのプロセス ID (<c>GetNamedPipeClientProcessId</c>)。</param>
/// <param name="ExecutablePath">クライアントの実行ファイル (調べられなければ null)。</param>
public sealed record PipeClientConnection(NamedPipeServerStream Stream, int ProcessId, string? ExecutablePath);

/// <summary>
/// 外部からの自動操作の名前付きパイプ (08 の AUTO-45 の仕様 2)。パイプ名は <c>\\.\pipe\HexEditor.&lt;利用者の SID&gt;.rpc</c>。
/// ACL で現在の利用者だけに接続を許し (ネットワークのログオンは拒否)、<c>PIPE_REJECT_REMOTE_CLIENTS</c> (.NET の
/// <see cref="NamedPipeServerStream"/> が常に付ける) でネットワーク経由の接続を拒否する。最初のインスタンスは
/// <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> で作り、ほかのプロセスが先に同じ名前のパイプを作っていたら開始しない (なりすましの防止)。
/// <see cref="DisposeAsync"/> の後はパイプが存在しない (設定がオフの間はパイプがない。受け入れ基準 1)。
/// </summary>
public sealed class NamedPipeRpcListener : IAsyncDisposable
{
    /// <summary>同時に接続できる数。</summary>
    public const int MaxConnections = 16;

    private readonly Func<PipeClientConnection, Task> _onConnection;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _lock = new();
    private NamedPipeServerStream? _waiting;
    private Task? _loop;

    public NamedPipeRpcListener(string pipeName, Func<PipeClientConnection, Task> onConnection)
    {
        PipeName = pipeName;
        _onConnection = onConnection;
    }

    public string PipeName { get; }

    /// <summary>現在の利用者のパイプ名。</summary>
    public static string CurrentUserPipeName => NameFor(WindowsIdentity.GetCurrent().User!);

    public static string NameFor(SecurityIdentifier sid) => $"HexEditor.{sid.Value}.rpc";

    /// <summary>パイプの完全な名前 (<c>\\.\pipe\…</c>)。</summary>
    public static string FullName(string pipeName) => @"\\.\pipe\" + pipeName;

    /// <summary>パイプがあるか (<c>\\.\pipe\</c> の一覧で調べる。接続はしない)。</summary>
    public static bool Exists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, FullName(pipeName), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>パイプの ACL: 現在の利用者に読み書きとインスタンスの作成を許し、ネットワークのログオンを拒否する。</summary>
    public static PipeSecurity CreateSecurity()
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.SetOwner(user);
        return security;
    }

    /// <summary>
    /// 待ち受けを始める (最初のインスタンスを作ってから戻るので、戻った時点でパイプがある)。同じ名前のパイプが既にあれば
    /// <see cref="IOException"/> (UnauthorizedAccessException のこともある)。
    /// </summary>
    public void Start()
    {
        _waiting = Create(first: true);
        _loop = Task.Run(AcceptLoopAsync);
    }

    private NamedPipeServerStream Create(bool first) =>
        NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | (first ? PipeOptions.FirstPipeInstance : 0), 0, 0, CreateSecurity());

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server = _waiting ?? Create(first: false);
            _waiting = server;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                _waiting = null;
                continue;
            }

            _waiting = null;
            int pid = ClientProcessId(server);
            var connection = new PipeClientConnection(server, pid, pid > 0 ? ExecutableOf(pid) : null);
            Task handler = Task.Run(async () =>
            {
                try
                {
                    await _onConnection(connection).ConfigureAwait(false);
                }
                finally
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            });
            lock (_lock)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(handler);
            }

            // 同時に接続できる数を超えたら、どれかが終わるまで次のインスタンスを作らない。
            while (!_stop.IsCancellationRequested)
            {
                Task[] running;
                lock (_lock)
                {
                    _connections.RemoveAll(t => t.IsCompleted);
                    running = [.. _connections];
                }

                if (running.Length < MaxConnections)
                {
                    break;
                }

                await Task.WhenAny(running).ConfigureAwait(false);
            }
        }
    }

    /// <summary>止める。待ち受けのインスタンスを閉じ、接続中のものも閉じる (パイプがなくなる)。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_waiting is { } w)
        {
            await w.DisposeAsync().ConfigureAwait(false);
        }

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }

        Task[] running;
        lock (_lock)
        {
            running = [.. _connections];
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    // ───────────── クライアントの情報 ─────────────

    private static int ClientProcessId(NamedPipeServerStream server)
    {
        try
        {
            return GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) ? (int)pid : 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <summary>プロセスの実行ファイルのフルパス (調べられなければ null)。</summary>
    public static string? ExecutableOf(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        using SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[32768];
        int size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
}

/// <summary>パイプを作れなかった (同じ名前のパイプがほかのプロセスにある、など)。</summary>
public sealed class PipeUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static PipeUnavailableException From(Exception ex) =>
        new(ex is Win32Exception w ? w.Message : ex.Message, ex);
}
