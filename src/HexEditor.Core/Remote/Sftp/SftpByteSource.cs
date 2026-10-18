using HexEditor.Core.Sources;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>
/// SFTP のリモートのファイルのデータソース (ENG-41 の仕様 4)。SFTP の位置指定の読み込みで必要な範囲だけを読み、ブロックキャッシュ (ENG-06) を
/// 通す。ファイル全体はダウンロードしない。長さはリモートのファイルサイズ。ドキュメントは長さを変えられる (保存は <see cref="SftpSaver"/>)。
/// 接続が切れたら自動で 1 回だけ再接続を試み、失敗したら「切断」(<see cref="SourceChangeKind.Disconnected"/>) にする (仕様 7)。
/// </summary>
public sealed class SftpByteSource : ByteSourceBase
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISftpFile? _file;
    private int _fileGeneration;
    private bool _autoReconnectUsed;
    private bool _disconnectNotified;
    private long _bytesRead;

    private SftpByteSource(SftpConnection connection, SftpFileInfo info, ISftpFile file)
    {
        Connection = connection;
        Info = info;
        _file = file;
        _fileGeneration = connection.Generation;
        Uri = new SftpUri(connection.Endpoint, info.Path);
        connection.AddUser();
    }

    /// <summary>リモートのファイルを開く (開く処理はファイルサイズに依存しない)。</summary>
    public static async Task<SftpByteSource> OpenAsync(SftpConnection connection, string path, CancellationToken cancellationToken = default)
    {
        ISftpSession session = connection.Session;
        SftpFileInfo info = await session.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsDirectory)
        {
            throw new SftpException(SftpFailure.Other, $"{path} is a directory.");
        }

        ISftpFile file = await session.OpenAsync(info.Path, SftpOpenMode.Read, cancellationToken).ConfigureAwait(false);
        return new SftpByteSource(connection, info, file);
    }

    public SftpConnection Connection { get; }

    /// <summary>開いたとき (または保存したとき) のファイルの値 (サイズと更新日時。外部変更の検知に使う。仕様 6)。</summary>
    public SftpFileInfo Info { get; private set; }

    /// <summary><c>sftp://ユーザー@ホスト:ポート/パス</c>。</summary>
    public SftpUri Uri { get; }

    public string RemotePath => Info.Path;

    public override string DisplayName => System.IO.Path.GetFileName(Info.Path.TrimEnd('/')) is { Length: > 0 } name ? name : Info.Path;

    public override string Identity => $"sftp://{Connection.Endpoint.Key}{Info.Path}";

    public override long Length => Info.Length;

    public override SourceCapabilities Capabilities =>
        Connection.State == RemoteConnectionState.Connected ? SourceCapabilities.CanWrite | SourceCapabilities.CanResize : SourceCapabilities.CanResize;

    /// <summary>読み込んだバイト数の合計 (テスト・診断用)。</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        byte[] temp = new byte[buffer.Length];
        ReadResult result = ReadAsync(offset, temp).AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, result.BytesReturned).CopyTo(buffer);
        return result;
    }

    public override async ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        Memory<byte> target = buffer[..length];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    ISftpFile file = await FileAsync(cancellationToken).ConfigureAwait(false);
                    int done = 0;
                    while (done < length)
                    {
                        int n = await file.ReadAsync(offset + done, target[done..], cancellationToken).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            break;
                        }

                        done += n;
                    }

                    Interlocked.Add(ref _bytesRead, done);
                    if (done < length)
                    {
                        // リモートのファイルが開いた後に短くなった: 残りは読めない。
                        target.Span[done..].Clear();
                        return new ReadResult(length, [new UnreadableRange(offset + done, length - done, UnreadableReason.IoError)]);
                    }

                    return new ReadResult(length);
                }
                catch (SftpException ex) when (ex.Failure == SftpFailure.Disconnected)
                {
                    // 接続が切れた: 自動で 1 回だけ再接続を試みる (仕様 7)。
                    if (attempt == 0 && await TryAutoReconnectAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    target.Span.Clear();
                    return new ReadResult(length, [new UnreadableRange(offset, length, UnreadableReason.Disconnected)]);
                }
                catch (SftpException ex)
                {
                    target.Span.Clear();
                    UnreadableReason reason = ex.Failure == SftpFailure.Permission ? UnreadableReason.AccessDenied : UnreadableReason.IoError;
                    return new ReadResult(length, [new UnreadableRange(offset, length, reason)]);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>開いているハンドル (再接続の後は開き直す)。</summary>
    private async Task<ISftpFile> FileAsync(CancellationToken cancellationToken)
    {
        int generation = Connection.Generation;
        if (_file is null || _fileGeneration != generation)
        {
            if (_file is not null)
            {
                await SafeCloseAsync(_file).ConfigureAwait(false);
            }

            _file = null;
            _file = await Connection.Session.OpenAsync(Info.Path, SftpOpenMode.Read, cancellationToken).ConfigureAwait(false);
            _fileGeneration = generation;
        }

        return _file;
    }

    private static async Task SafeCloseAsync(ISftpFile file)
    {
        try
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task<bool> TryAutoReconnectAsync(CancellationToken cancellationToken)
    {
        if (Connection.State == RemoteConnectionState.Connected)
        {
            Connection.MarkDisconnected();
        }

        if (!_autoReconnectUsed)
        {
            _autoReconnectUsed = true;
            try
            {
                await Connection.ReconnectAsync(cancellationToken).ConfigureAwait(false);

                // 再接続できた: 次に切れたときも 1 回だけ自動で試みる。
                _autoReconnectUsed = false;
                _disconnectNotified = false;
                return true;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
            {
            }
        }

        if (!_disconnectNotified)
        {
            _disconnectNotified = true;
            OnChanged(SourceChangeKind.Disconnected);
        }

        return false;
    }

    /// <summary>「再接続」ボタン (ENG-41 の仕様 7)。成功したら自動の再接続も再び使えるようにする。</summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await Connection.ReconnectAsync(cancellationToken).ConfigureAwait(false);
        _autoReconnectUsed = false;
        _disconnectNotified = false;
    }

    /// <summary>
    /// 外部変更の確認 (ENG-41 の仕様 6。ウィンドウのアクティブ化時と 30 秒ごと)。サイズか更新日時が開いたとき (保存したとき) と違えば true。
    /// 確認できない (切断) 場合は false。
    /// </summary>
    public async Task<bool> CheckExternalChangeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SftpFileInfo now = await Connection.Session.StatAsync(Info.Path, cancellationToken).ConfigureAwait(false);
            return now.Length != Info.Length || now.LastWriteUtc != Info.LastWriteUtc;
        }
        catch (SftpException)
        {
            return false;
        }
    }

    /// <summary>保存した後の値を記録する (自分の保存を外部変更と見なさないため)。</summary>
    internal void UpdateInfo(SftpFileInfo info) => Info = info;

    /// <summary>同じ接続で、同じパスを開き直したデータソース (長さが変わる保存の後の新しい元データ)。</summary>
    internal static async Task<SftpByteSource> ReopenAsync(SftpConnection connection, string path, CancellationToken cancellationToken) =>
        await OpenAsync(connection, path, cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_file is { } file)
        {
            _file = null;
            _ = SafeCloseAsync(file);
        }

        // 接続を使うデータソースがなくなったら切断する (保存前の版が読む元のデータソースも閉じるまで接続を保つ)。
        Connection.RemoveUser();
    }

    private int _disposed;
}
