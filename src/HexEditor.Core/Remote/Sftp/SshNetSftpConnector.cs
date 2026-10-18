using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using RemoteFileHandle = HexEditor.Core.Remote.Sftp.ISftpFile;
using SshFile = Renci.SshNet.Sftp.ISftpFile;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>
/// SSH.NET (MIT) で SFTP に接続する (ENG-41)。ホスト鍵は <see cref="KnownHosts"/> と照合し、信頼した鍵でなければ認証の前に接続をやめて
/// <see cref="HostKeyRejectedException"/> にする (黙って受け入れない。仕様 2)。認証はパスワード・鍵ファイル・SSH エージェント (仕様 1)。
/// 資格情報はログに書かない。
/// </summary>
public sealed class SshNetSftpConnector(ISshAgent? agent = null) : ISftpConnector
{
    public async Task<ISftpSession> ConnectAsync(SftpEndpoint endpoint, SftpCredentials credentials, KnownHosts hostKeys, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        AuthenticationMethod method = credentials.Method switch
        {
            SftpAuthMethod.Password => new PasswordAuthenticationMethod(endpoint.User, credentials.Password ?? string.Empty),
            SftpAuthMethod.KeyFile => new PrivateKeyAuthenticationMethod(endpoint.User, LoadKey(credentials)),
            _ => new PrivateKeyAuthenticationMethod(endpoint.User, [.. AgentKeys()]),
        };
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.User, method) { Timeout = timeout };
        var client = new SftpClient(info) { OperationTimeout = timeout };
        HostKeyInfo? seen = null;
        HostKeyStatus status = HostKeyStatus.Unknown;
        HostKeyInfo? previous = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = new HostKeyInfo(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
            status = hostKeys.Check(endpoint.Host, endpoint.Port, seen, out previous);
            e.CanTrust = status == HostKeyStatus.Trusted;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new Session(client);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (seen is not null && status != HostKeyStatus.Trusted)
            {
                throw new HostKeyRejectedException(endpoint, seen, status, previous);
            }

            throw ex switch
            {
                SshAuthenticationException => new SftpException(SftpFailure.Authentication, ex.Message, ex),
                SshPassPhraseNullOrEmptyException => new SftpException(SftpFailure.Authentication, ex.Message, ex),
                SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException
                    => new SftpException(SftpFailure.Connect, ex.Message, ex),
                OperationCanceledException => ex,
                _ => new SftpException(SftpFailure.Other, ex.Message, ex),
            };
        }
    }

    private static PrivateKeyFile LoadKey(SftpCredentials credentials)
    {
        try
        {
            return string.IsNullOrEmpty(credentials.Passphrase)
                ? new PrivateKeyFile(credentials.KeyFile ?? string.Empty)
                : new PrivateKeyFile(credentials.KeyFile ?? string.Empty, credentials.Passphrase);
        }
        catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            throw new SftpException(SftpFailure.Authentication, ex.Message, ex);
        }
    }

    private IEnumerable<IPrivateKeySource> AgentKeys()
    {
        if (agent is null)
        {
            throw new SftpException(SftpFailure.Authentication, "No SSH agent is available.");
        }

        IReadOnlyList<SshAgentIdentity> identities;
        try
        {
            identities = agent.ListIdentities();
        }
        catch (IOException ex)
        {
            throw new SftpException(SftpFailure.Authentication, ex.Message, ex);
        }

        if (identities.Count == 0)
        {
            throw new SftpException(SftpFailure.Authentication, "The SSH agent has no keys.");
        }

        return identities.Select(i => new AgentKeySource(agent, i));
    }

    /// <summary>SSH.NET の SftpClient を包んだセッション。</summary>
    private sealed class Session(SftpClient client) : ISftpSession
    {
        public bool IsConnected => client.IsConnected;

        public string HomeDirectory => client.WorkingDirectory;

        public Task<SftpFileInfo> StatAsync(string path, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            SftpFileAttributes a = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return new SftpFileInfo(path, a.Size, a.LastWriteTimeUtc, a.IsDirectory);
        });

        public Task<IReadOnlyList<SftpFileInfo>> ListAsync(string path, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            var list = new List<SftpFileInfo>();
            await foreach (SshFile f in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (f.Name is not "." and not "..")
                {
                    list.Add(new SftpFileInfo(f.FullName, f.Length, f.LastWriteTimeUtc, f.IsDirectory));
                }
            }

            return (IReadOnlyList<SftpFileInfo>)list;
        });

        public Task<RemoteFileHandle> OpenAsync(string path, SftpOpenMode mode, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            (FileMode fileMode, FileAccess access) = mode switch
            {
                SftpOpenMode.ReadWrite => (FileMode.Open, FileAccess.ReadWrite),
                SftpOpenMode.CreateNew => (FileMode.CreateNew, FileAccess.ReadWrite),
                _ => (FileMode.Open, FileAccess.Read),
            };
            SftpFileStream stream = await client.OpenAsync(path, fileMode, access, cancellationToken).ConfigureAwait(false);
            return (RemoteFileHandle)new RemoteFile(stream);
        });

        public Task RenameAsync(string from, string to, bool overwrite, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            if (overwrite)
            {
                // posix-rename@openssh.com (サーバーが対応していなければ NotSupportedException)。
                await Task.Run(() => client.RenameFile(from, to, isPosix: true), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.RenameFileAsync(from, to, cancellationToken).ConfigureAwait(false);
            }

            return true;
        });

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            Wrap(async () =>
            {
                await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
                return true;
            });

        public void Dispose()
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }

            client.Dispose();
        }
    }

    /// <summary>SSH.NET の例外を <see cref="SftpException"/> にする。</summary>
    private static async Task<T> Wrap<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new SftpException(SftpFailure.Permission, ex.Message, ex);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new SftpException(SftpFailure.NotFound, ex.Message, ex);
        }
        catch (Exception ex) when (ex is SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException
            || (ex is SshException && ex is not Renci.SshNet.Common.SftpException))
        {
            throw new SftpException(SftpFailure.Disconnected, ex.Message, ex);
        }
        catch (Renci.SshNet.Common.SftpException ex)
        {
            throw new SftpException(SftpFailure.Other, ex.Message, ex);
        }
    }

    private static async Task Wrap(Func<Task<bool>> action) => await Wrap<bool>(action).ConfigureAwait(false);

    /// <summary>SftpFileStream を位置指定の読み書きにする。</summary>
    private sealed class RemoteFile(SftpFileStream stream) : RemoteFileHandle
    {
        public Task<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            stream.Position = offset;
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        });

        public Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Wrap(async () =>
        {
            stream.Position = offset;
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

        public Task SetLengthAsync(long length, CancellationToken cancellationToken = default) => Wrap(() =>
        {
            stream.SetLength(length);
            return Task.FromResult(true);
        });

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
