using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Elevation;

/// <summary>接続の確認で相手が違った (ENG-28 の仕様 2 の 3・4)。</summary>
public sealed class HelperHandshakeException(string message) : IOException(message);

/// <summary>
/// 補助プロセスとの名前付きパイプと接続の確認 (ENG-28 の仕様 2)。UI のプロセスがパイプのサーバー側、補助プロセスがクライアント側。
/// </summary>
[SupportedOSPlatform("windows")]
public static class HelperPipes
{
    /// <summary>パイプ名の接頭辞 (<c>\\.\pipe\HexEditor.Helper.&lt;GUID&gt;</c>)。</summary>
    public const string Prefix = "HexEditor.Helper.";

    public static string NewPipeName() => Prefix + Guid.NewGuid().ToString("D");

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(HelperProtocol.SecretSize);

    /// <summary>
    /// UI のプロセスのパイプを作る (仕様 2 の 1): 現在のユーザーと SYSTEM だけが接続でき、ネットワークからの接続を拒否し、
    /// 最初のインスタンスとして作る (同名のパイプが既にあれば <see cref="UnauthorizedAccessException"/> または <see cref="IOException"/>)。
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name)
    {
        var security = new PipeSecurity();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, HelperProtocol.MaxBody + HelperProtocol.HeaderSize,
            HelperProtocol.MaxBody + HelperProtocol.HeaderSize, security);
    }

    /// <summary>
    /// 補助プロセスの接続を待つ (仕様 2 の 4)。接続してきたクライアントのプロセス ID が <paramref name="expectedPid"/> で、合言葉が一致した
    /// ときだけ受け付ける。違えば切断して待ち続ける (<paramref name="rejected"/> で知らせる)。時間内に正しい接続がなければ例外。
    /// </summary>
    public static async Task AcceptAsync(NamedPipeServerStream pipe, Func<int> expectedPid, byte[] secret, TimeSpan timeout,
        Action<string>? rejected = null, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        while (true)
        {
            await pipe.WaitForConnectionAsync(limit.Token).ConfigureAwait(false);
            int pid = ClientProcessId(pipe);
            if (pid != expectedPid())
            {
                rejected?.Invoke($"client process {pid} is not the helper {expectedPid()}");
                pipe.Disconnect();
                continue;
            }

            byte[] received = new byte[HelperProtocol.SecretSize];
            try
            {
                using var secretLimit = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                secretLimit.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.ReadExactlyAsync(received, secretLimit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException && !limit.IsCancellationRequested)
            {
                rejected?.Invoke("the client did not send the secret");
                Disconnect(pipe);
                continue;
            }

            if (!CryptographicOperations.FixedTimeEquals(received, secret))
            {
                rejected?.Invoke("wrong secret");
                Disconnect(pipe);
                continue;
            }

            return;
        }
    }

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// 補助プロセス側の接続 (仕様 2 の 3): パイプのサーバー側のプロセス ID が <paramref name="parentPid"/> と一致することを確認してから、
    /// 合言葉を送る。一致しなければ合言葉を送らずに例外。
    /// </summary>
    public static async Task<NamedPipeClientStream> ConnectAsync(string name, int parentPid, byte[] secret, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            int server = ServerProcessId(pipe);
            if (server != parentPid)
            {
                throw new HelperHandshakeException($"The pipe server is process {server}, not {parentPid}.");
            }

            await pipe.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static int ClientProcessId(PipeStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : -1;

    public static int ServerProcessId(PipeStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
