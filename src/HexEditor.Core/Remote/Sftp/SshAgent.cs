using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>SSH エージェントの鍵 1 つ (公開鍵の blob とコメント)。</summary>
public sealed record SshAgentIdentity(byte[] KeyBlob, string Comment)
{
    /// <summary>鍵の種類 (blob の先頭の文字列。<c>ssh-ed25519</c> など)。</summary>
    public string KeyType
    {
        get
        {
            if (KeyBlob.Length < 4)
            {
                return string.Empty;
            }

            int n = BinaryPrimitives.ReadInt32BigEndian(KeyBlob);
            return n > 0 && n <= KeyBlob.Length - 4 ? Encoding.ASCII.GetString(KeyBlob, 4, n) : string.Empty;
        }
    }
}

/// <summary>SSH エージェント (ENG-41 の仕様 1。鍵の一覧と署名だけを使う)。テストでは使わない (この PC のエージェントに触れない)。</summary>
public interface ISshAgent
{
    IReadOnlyList<SshAgentIdentity> ListIdentities();

    /// <summary>署名する。戻り値は SSH の署名の blob (形式名 + 署名)。<paramref name="flags"/> は RSA の SHA-2 の指定 (2 = SHA-256、4 = SHA-512)。</summary>
    byte[] Sign(byte[] keyBlob, byte[] data, uint flags);
}

/// <summary>
/// Windows の SSH エージェント: OpenSSH の ssh-agent (名前付きパイプ <c>\\.\pipe\openssh-ssh-agent</c>) を使い、なければ Pageant
/// (<c>WM_COPYDATA</c>) を使う。エージェントの通信の形 (draft-miller-ssh-agent) の鍵の一覧 (11 → 12) と署名 (13 → 14) だけを送る。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSshAgent : ISshAgent
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    private const int MaxMessage = 256 * 1024;

    public IReadOnlyList<SshAgentIdentity> ListIdentities()
    {
        byte[] reply = Exchange([RequestIdentities]);
        if (reply.Length < 5 || reply[0] != IdentitiesAnswer)
        {
            throw new IOException("The SSH agent refused to list the keys.");
        }

        var list = new List<SshAgentIdentity>();
        int count = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(1));
        int at = 5;
        for (int i = 0; i < count; i++)
        {
            byte[] blob = ReadString(reply, ref at);
            string comment = Encoding.UTF8.GetString(ReadString(reply, ref at));
            list.Add(new SshAgentIdentity(blob, comment));
        }

        return list;
    }

    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        var message = new MemoryStream();
        message.WriteByte(SignRequest);
        WriteString(message, keyBlob);
        WriteString(message, data);
        Span<byte> f = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(f, flags);
        message.Write(f);
        byte[] reply = Exchange(message.ToArray());
        if (reply.Length < 5 || reply[0] != SignResponse)
        {
            throw new IOException("The SSH agent refused to sign.");
        }

        int at = 1;
        return ReadString(reply, ref at);
    }

    private static byte[] ReadString(byte[] data, ref int at)
    {
        int n = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
        if (n < 0 || at + 4 + n > data.Length)
        {
            throw new IOException("The SSH agent reply is malformed.");
        }

        byte[] value = data[(at + 4)..(at + 4 + n)];
        at += 4 + n;
        return value;
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, value.Length);
        stream.Write(n);
        stream.Write(value);
    }

    /// <summary>要求を 1 つ送って応答を受け取る (OpenSSH のエージェント、なければ Pageant)。</summary>
    private static byte[] Exchange(byte[] message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "openssh-ssh-agent", PipeDirection.InOut);
            pipe.Connect(1000);
            byte[] frame = new byte[4 + message.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, message.Length);
            message.CopyTo(frame, 4);
            pipe.Write(frame);
            byte[] length = new byte[4];
            pipe.ReadExactly(length);
            int n = BinaryPrimitives.ReadInt32BigEndian(length);
            if (n <= 0 || n > MaxMessage)
            {
                throw new IOException("The SSH agent reply is malformed.");
            }

            byte[] reply = new byte[n];
            pipe.ReadExactly(reply);
            return reply;
        }
        catch (TimeoutException)
        {
            return Pageant.Exchange(message);
        }
        catch (IOException) when (Pageant.IsRunning)
        {
            return Pageant.Exchange(message);
        }
    }

    /// <summary>Pageant との WM_COPYDATA による通信。</summary>
    private static unsafe class Pageant
    {
        private const int MaxLength = 8192;
        private const uint CopyDataId = 0x804e50ba;

        public static bool IsRunning => FindWindowW("Pageant", "Pageant") != 0;

        public static byte[] Exchange(byte[] message)
        {
            nint window = FindWindowW("Pageant", "Pageant");
            if (window == 0)
            {
                throw new IOException("No SSH agent is running (OpenSSH ssh-agent or Pageant).");
            }

            if (message.Length + 4 > MaxLength)
            {
                throw new IOException("The SSH agent request is too long.");
            }

            string name = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Environment.ProcessId:x8}";
            nint mapping = CreateFileMappingW(-1, 0, 0x04 /* PAGE_READWRITE */, 0, MaxLength, name);
            if (mapping == 0)
            {
                throw new IOException("Could not create the shared memory for Pageant.");
            }

            try
            {
                byte* view = (byte*)MapViewOfFile(mapping, 0x0002 | 0x0004 /* FILE_MAP_WRITE | FILE_MAP_READ */, 0, 0, MaxLength);
                if (view == null)
                {
                    throw new IOException("Could not map the shared memory for Pageant.");
                }

                try
                {
                    BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(view, 4), message.Length);
                    message.CopyTo(new Span<byte>(view + 4, message.Length));
                    byte[] ansi = Encoding.ASCII.GetBytes(name + "\0");
                    fixed (byte* p = ansi)
                    {
                        var data = new CopyData { Data = unchecked((nint)CopyDataId), Size = ansi.Length, Pointer = (nint)p };
                        if (SendMessageW(window, 0x004A /* WM_COPYDATA */, 0, (nint)(&data)) == 0)
                        {
                            throw new IOException("Pageant refused the request.");
                        }
                    }

                    int n = BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(view, 4));
                    if (n <= 0 || n > MaxLength - 4)
                    {
                        throw new IOException("The Pageant reply is malformed.");
                    }

                    return new ReadOnlySpan<byte>(view + 4, n).ToArray();
                }
                finally
                {
                    UnmapViewOfFile((nint)view);
                }
            }
            finally
            {
                CloseHandle(mapping);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyData
        {
            public nint Data;
            public int Size;
            public nint Pointer;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint FindWindowW(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateFileMappingW(nint file, nint attributes, uint protect, uint maxHigh, uint maxLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(nint view);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint handle);
    }
}

/// <summary>エージェントの鍵を SSH.NET の公開鍵認証に渡す (署名はエージェントが行い、秘密鍵はこのアプリに来ない)。</summary>
internal sealed class AgentKeySource : IPrivateKeySource
{
    public AgentKeySource(ISshAgent agent, SshAgentIdentity identity)
    {
        HostKeyAlgorithms = identity.KeyType == "ssh-rsa"
            ? [new AgentHostAlgorithm("rsa-sha2-512", agent, identity, 4), new AgentHostAlgorithm("rsa-sha2-256", agent, identity, 2)]
            : [new AgentHostAlgorithm(identity.KeyType, agent, identity, 0)];
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    private sealed class AgentHostAlgorithm(string name, ISshAgent agent, SshAgentIdentity identity, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => identity.KeyBlob;

        public override byte[] Sign(byte[] data) => agent.Sign(identity.KeyBlob, data, flags);

        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }
}
