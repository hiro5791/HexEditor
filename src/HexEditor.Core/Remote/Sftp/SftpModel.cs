using System.Globalization;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>SFTP の接続先 (ENG-41 の仕様 1)。パスワードは含めない。</summary>
public sealed record SftpEndpoint
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string User { get; init; }

    /// <summary>資格情報マネージャーの名前・既知のホストの表のキー (<c>user@host:port</c>)。</summary>
    public string Key => $"{User}@{Host.ToLowerInvariant()}:{Port}";

    /// <summary>表示用の「ユーザー@ホスト:ポート」。</summary>
    public string Display => $"{User}@{SftpUri.HostText(Host)}:{Port}";
}

/// <summary>認証方式 (ENG-41 の仕様 1)。</summary>
public enum SftpAuthMethod
{
    Password,
    KeyFile,

    /// <summary>SSH エージェント (Windows の OpenSSH エージェント、Pageant)。</summary>
    Agent,
}

/// <summary>
/// 認証に使う資格情報。パスワードとパスフレーズはメモリの中だけに持ち、ログ・設定ファイルには書かない (ENG-41 の仕様 3)。
/// <see cref="ToString"/> は秘密を含めない。
/// </summary>
public sealed class SftpCredentials
{
    public SftpAuthMethod Method { get; init; }

    public string? Password { get; init; }

    public string? KeyFile { get; init; }

    public string? Passphrase { get; init; }

    public override string ToString() => $"{Method}{(KeyFile is null ? string.Empty : " " + KeyFile)}";
}

/// <summary>ホスト鍵 (種類と SHA-256 のフィンガープリント。ENG-41 の仕様 2)。</summary>
public sealed record HostKeyInfo(string Algorithm, string Sha256)
{
    /// <summary>OpenSSH と同じ表記 (<c>SHA256:…</c>)。</summary>
    public string Fingerprint => Sha256.StartsWith("SHA256:", StringComparison.Ordinal) ? Sha256 : "SHA256:" + Sha256;
}

/// <summary>既知のホストの表との照合の結果。</summary>
public enum HostKeyStatus
{
    Trusted,

    /// <summary>初めて接続するホスト (確認して「信頼して接続」を選ばせる)。</summary>
    Unknown,

    /// <summary>以前と違う鍵 (警告し、既定では接続しない)。</summary>
    Changed,
}

/// <summary>
/// ホスト鍵を信頼していないため接続を止めた (認証の前。資格情報は送っていない)。UI は鍵の確認・警告のダイアログを出し、信頼したら
/// 既知のホストの表に加えて接続し直す。
/// </summary>
public sealed class HostKeyRejectedException(SftpEndpoint endpoint, HostKeyInfo key, HostKeyStatus status, HostKeyInfo? previous)
    : IOException($"The host key of {endpoint.Host}:{endpoint.Port} is {(status == HostKeyStatus.Changed ? "different from the trusted key" : "not trusted")}.")
{
    public SftpEndpoint Endpoint { get; } = endpoint;

    public HostKeyInfo Key { get; } = key;

    public HostKeyStatus Status { get; } = status;

    /// <summary>以前に信頼した鍵 (<see cref="HostKeyStatus.Changed"/> のとき)。</summary>
    public HostKeyInfo? Previous { get; } = previous;
}

/// <summary>SFTP の失敗の種類 (ENG-41 の「エラー」: 認証の失敗、ホスト鍵の不一致、権限の不足、接続の切断を区別する)。</summary>
public enum SftpFailure
{
    Connect,
    Authentication,
    Permission,
    NotFound,
    Disconnected,
    Other,
}

/// <summary>SFTP の失敗。</summary>
public sealed class SftpException(SftpFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    public SftpFailure Failure { get; } = failure;
}

/// <summary>リモートのファイルの情報。</summary>
public sealed record SftpFileInfo(string Path, long Length, DateTime LastWriteUtc, bool IsDirectory);

/// <summary>開き方。</summary>
public enum SftpOpenMode
{
    Read,
    ReadWrite,

    /// <summary>新しく作る (あれば失敗)。</summary>
    CreateNew,
}

/// <summary>
/// SFTP のセッション (1 つの SSH 接続の上の SFTP)。実装: SSH.NET (<see cref="SshNetSftpConnector"/>)、テスト用の偽のサーバー。
/// すべての操作は非同期で、切断は <see cref="SftpException"/> (<see cref="SftpFailure.Disconnected"/>)。
/// </summary>
public interface ISftpSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>ログインしたときのフォルダ。</summary>
    string HomeDirectory { get; }

    Task<SftpFileInfo> StatAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpFileInfo>> ListAsync(string path, CancellationToken cancellationToken = default);

    Task<ISftpFile> OpenAsync(string path, SftpOpenMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 名前を変える。<paramref name="overwrite"/> なら上書きの名前変更 (<c>posix-rename@openssh.com</c>)。サーバーが対応していなければ
    /// <see cref="NotSupportedException"/>。
    /// </summary>
    Task RenameAsync(string from, string to, bool overwrite, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>開いたリモートのファイル。位置を指定して読み書きする (同時に呼ばない。呼び出し側が 1 つずつにする)。</summary>
public interface ISftpFile : IAsyncDisposable
{
    /// <summary>位置を指定して読む。末尾では短くなる。</summary>
    Task<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>長さを変える。</summary>
    Task SetLengthAsync(long length, CancellationToken cancellationToken = default);
}

/// <summary>
/// SFTP のセッションを作る。<paramref name="hostKeys"/> と照合し、信頼していない鍵なら認証の前に <see cref="HostKeyRejectedException"/>。
/// 認証の失敗は <see cref="SftpException"/> (<see cref="SftpFailure.Authentication"/>)。
/// </summary>
public interface ISftpConnector
{
    Task<ISftpSession> ConnectAsync(SftpEndpoint endpoint, SftpCredentials credentials, KnownHosts hostKeys, TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary><c>sftp://ユーザー@ホスト:ポート/パス</c> (最近使ったファイル。ENG-41 の仕様 8。パスワードは含めない)。</summary>
public sealed record SftpUri(SftpEndpoint Endpoint, string Path)
{
    public const string Scheme = "sftp://";

    public override string ToString() =>
        $"{Scheme}{Uri.EscapeDataString(Endpoint.User)}@{HostText(Endpoint.Host)}:{Endpoint.Port.ToString(CultureInfo.InvariantCulture)}{(Path.StartsWith('/') ? Path : "/" + Path)}";

    public static string HostText(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    public static bool IsSftp(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>解析する。形が違えば null。パスワード (<c>user:pass@</c>) を含む URI は受け付けない。</summary>
    public static SftpUri? TryParse(string? text)
    {
        if (!IsSftp(text) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(':'))
        {
            return null;
        }

        string host = uri.Host.Trim('[', ']');
        int port = uri.IsDefaultPort || uri.Port < 0 ? 22 : uri.Port;
        return new SftpUri(new SftpEndpoint { Host = host, Port = port, User = Uri.UnescapeDataString(uri.UserInfo) },
            Uri.UnescapeDataString(uri.AbsolutePath));
    }
}
