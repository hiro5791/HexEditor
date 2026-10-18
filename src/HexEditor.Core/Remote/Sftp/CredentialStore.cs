using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>
/// パスワード・パスフレーズの保存先 (ENG-41 の仕様 3)。利用者が「保存する」を選んだ場合だけ書く。設定ファイルには保存しない。
/// 実装: Windows の資格情報マネージャー (<see cref="WindowsCredentialStore"/>)、テスト用のメモリの中の表。
/// </summary>
public interface ICredentialStore
{
    /// <summary>保存した秘密 (なければ null)。</summary>
    string? Read(string key);

    void Write(string key, string secret);

    void Delete(string key);
}

/// <summary>メモリの中だけの保存先 (テスト用。保存しない設定の確認にも使う)。</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_secrets)
            {
                return [.. _secrets.Keys];
            }
        }
    }

    public string? Read(string key)
    {
        lock (_secrets)
        {
            return _secrets.GetValueOrDefault(key);
        }
    }

    public void Write(string key, string secret)
    {
        lock (_secrets)
        {
            _secrets[key] = secret;
        }
    }

    public void Delete(string key)
    {
        lock (_secrets)
        {
            _secrets.Remove(key);
        }
    }
}

/// <summary>
/// Windows の資格情報マネージャー (汎用の資格情報、<c>CredWriteW</c> / <c>CredReadW</c> / <c>CredDeleteW</c>) への保存 (ENG-41 の仕様 3)。
/// 名前は <c>HexEditor:sftp:ユーザー@ホスト:ポート</c>。保存の範囲はログオンのセッションをまたぐ (<c>CRED_PERSIST_LOCAL_MACHINE</c>)。
/// テストでは使わない (この PC に資格情報を書かない)。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const string Prefix = "HexEditor:sftp:";

    public string? Read(string key)
    {
        if (!CredReadW(Prefix + key, CredTypeGeneric, 0, out IntPtr credential))
        {
            return null;
        }

        try
        {
            var c = (Credential*)credential;
            return c->CredentialBlobSize == 0 ? string.Empty : Encoding.Unicode.GetString(c->CredentialBlob, c->CredentialBlobSize);
        }
        finally
        {
            CredFree(credential);
        }
    }

    public void Write(string key, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* target = Prefix + key)
        fixed (char* user = key)
        fixed (byte* data = blob)
        {
            var c = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(&c, 0))
            {
                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
        }

        Array.Clear(blob);
    }

    public void Delete(string key) => CredDeleteW(Prefix + key, CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public byte* CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(Credential* credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
