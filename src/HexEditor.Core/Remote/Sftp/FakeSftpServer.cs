#if HEX_TEST_HOOKS
using System.Text.Json;

namespace HexEditor.Core.Remote.Sftp;

// テスト用のビルドだけに入れる、偽の SFTP サーバー (テスト方針 7.2)。ネットワークにも実際のサーバーにも触れずに、ホスト鍵・認証・位置指定の
// 読み書き・名前の変更・切断の振る舞いを再現する。製品版には含めない。

/// <summary>偽のファイルの定義 (テスト用の設定ファイルの形)。</summary>
public sealed record FakeSftpFileSpec
{
    public required string Path { get; init; }

    public long Length { get; init; }

    /// <summary>初期内容 (16 進文字列)。</summary>
    public string? Data { get; init; }

    /// <summary>この種の乱数で埋める (テストデータと同じ SplitMix64)。null なら 0。</summary>
    public ulong? Seed { get; init; }
}

/// <summary>偽のサーバーの定義。</summary>
public sealed record FakeSftpSpec
{
    public string HostKeyAlgorithm { get; init; } = "ssh-ed25519";

    public string HostKeyFingerprint { get; init; } = "SHA256:fakeHostKey0000000000000000000000000000000";

    /// <summary>ユーザーとパスワード。</summary>
    public Dictionary<string, string> Passwords { get; init; } = [];

    /// <summary>鍵ファイル・エージェントで認証できるユーザー。</summary>
    public List<string> KeyUsers { get; init; } = [];

    public string Home { get; init; } = "/home/test";

    public bool PosixRename { get; init; } = true;

    public List<FakeSftpFileSpec> Files { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static FakeSftpSpec Parse(string json) => JsonSerializer.Deserialize<FakeSftpSpec>(json, Options) ?? new FakeSftpSpec();
}

/// <summary>
/// 偽の SFTP サーバー (<see cref="ISftpConnector"/>)。ファイルはページ単位の疎な表 (未設定のページは乱数の種または 0) で、10 GiB のような
/// 大きなファイルもメモリを使わずに置ける。操作の記録 (<see cref="Log"/>) で、読んだ量・書いた位置・作ったファイルを確かめられる。
/// </summary>
public sealed class FakeSftpServer : ISftpConnector
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];
    private readonly List<Session> _sessions = [];

    public FakeSftpServer(FakeSftpSpec spec)
    {
        Spec = spec;
        HostKey = new HostKeyInfo(spec.HostKeyAlgorithm, spec.HostKeyFingerprint);
        foreach (FakeSftpFileSpec f in spec.Files)
        {
            var file = new FakeFile(f.Length, f.Seed);
            if (f.Data is not null)
            {
                file.Write(0, Convert.FromHexString(f.Data));
            }

            _files[f.Path] = file;
        }
    }

    public FakeSftpSpec Spec { get; }

    /// <summary>今のホスト鍵 (テストが鍵を作り直す)。</summary>
    public HostKeyInfo HostKey { get; set; }

    /// <summary>上書きの名前変更に対応する。</summary>
    public bool PosixRename { get; set; } = true;

    /// <summary>接続を拒む (サーバーの停止)。</summary>
    public bool Down { get; set; }

    /// <summary>認証まで済んで作られたセッションの数。</summary>
    public int SessionsCreated { get; private set; }

    /// <summary>ホスト鍵を送った回数 (接続の試み)。</summary>
    public int HandshakeCount { get; private set; }

    /// <summary>読み込みで返したバイト数の合計。</summary>
    public long BytesRead { get; private set; }

    /// <summary>受け取った認証のパスワード (テストで、保存していないことを確かめるため。記録はメモリの中だけ)。</summary>
    public int PasswordAttempts { get; private set; }

    /// <summary>操作の記録 (<c>open path mode</c>、<c>write path offset length</c>、<c>rename a b</c>、<c>delete p</c>)。</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    public static FakeSftpServer FromJson(string json) => new(FakeSftpSpec.Parse(json)) { PosixRename = FakeSftpSpec.Parse(json).PosixRename };

    public void AddFile(string path, long length, ulong? seed = null)
    {
        lock (_lock)
        {
            _files[path] = new FakeFile(length, seed);
        }
    }

    public bool Exists(string path)
    {
        lock (_lock)
        {
            return _files.ContainsKey(path);
        }
    }

    public IReadOnlyCollection<string> Paths
    {
        get
        {
            lock (_lock)
            {
                return [.. _files.Keys];
            }
        }
    }

    public byte[] ReadFile(string path, long offset, int length)
    {
        lock (_lock)
        {
            byte[] data = new byte[length];
            _files[path].Read(offset, data);
            return data;
        }
    }

    public long LengthOf(string path)
    {
        lock (_lock)
        {
            return _files[path].Length;
        }
    }

    /// <summary>外部での変更 (他の人が書き換えた)。</summary>
    public void TouchExternally(string path, ReadOnlySpan<byte> data, long offset)
    {
        lock (_lock)
        {
            _files[path].Write(offset, data);
            _files[path].LastWrite = _files[path].LastWrite.AddSeconds(5);
        }
    }

    /// <summary>すべての接続を切る。</summary>
    public void DropConnections()
    {
        lock (_lock)
        {
            foreach (Session s in _sessions)
            {
                s.Connected = false;
            }
        }
    }

    private void Record(string text)
    {
        lock (_lock)
        {
            _log.Add(text);
        }
    }

    public Task<ISftpSession> ConnectAsync(SftpEndpoint endpoint, SftpCredentials credentials, KnownHosts hostKeys, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (Down)
        {
            throw new SftpException(SftpFailure.Connect, "Connection refused.");
        }

        // ホスト鍵の確認は認証の前 (信頼していなければ資格情報を送らない)。
        HandshakeCount++;
        HostKeyStatus status = hostKeys.Check(endpoint.Host, endpoint.Port, HostKey, out HostKeyInfo? previous);
        if (status != HostKeyStatus.Trusted)
        {
            throw new HostKeyRejectedException(endpoint, HostKey, status, previous);
        }

        bool ok = credentials.Method switch
        {
            SftpAuthMethod.Password => Spec.Passwords.TryGetValue(endpoint.User, out string? pw) && pw == credentials.Password,
            _ => Spec.KeyUsers.Contains(endpoint.User),
        };
        if (credentials.Method == SftpAuthMethod.Password)
        {
            PasswordAttempts++;
        }

        if (!ok)
        {
            throw new SftpException(SftpFailure.Authentication, "Permission denied (authentication failed).");
        }

        var session = new Session(this);
        lock (_lock)
        {
            _sessions.Add(session);
            SessionsCreated++;
        }

        return Task.FromResult<ISftpSession>(session);
    }

    private sealed class FakeFile(long length, ulong? seed)
    {
        private readonly Dictionary<long, byte[]> _pages = [];

        public long Length { get; set; } = length;

        public DateTime LastWrite { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Read(long offset, Span<byte> destination)
        {
            for (int i = 0; i < destination.Length;)
            {
                long a = offset + i;
                int inPage = (int)(a & 0xFFF);
                int n = Math.Min(4096 - inPage, destination.Length - i);
                if (_pages.TryGetValue(a >> 12, out byte[]? page))
                {
                    page.AsSpan(inPage, n).CopyTo(destination.Slice(i, n));
                }
                else if (seed is ulong s)
                {
                    SplitMix(s, a, destination.Slice(i, n));
                }
                else
                {
                    destination.Slice(i, n).Clear();
                }

                i += n;
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length;)
            {
                long a = offset + i;
                long index = a >> 12;
                if (!_pages.TryGetValue(index, out byte[]? page))
                {
                    page = new byte[4096];
                    Read(index << 12, page);
                    _pages[index] = page;
                }

                int inPage = (int)(a & 0xFFF);
                int n = Math.Min(4096 - inPage, data.Length - i);
                data.Slice(i, n).CopyTo(page.AsSpan(inPage));
                i += n;
            }

            Length = Math.Max(Length, offset + data.Length);
            LastWrite = LastWrite.AddSeconds(1);
        }

        private static void SplitMix(ulong seed, long offset, Span<byte> destination)
        {
            long word = -1;
            ulong z = 0;
            for (int i = 0; i < destination.Length; i++)
            {
                long p = offset + i;
                if (p >> 3 != word)
                {
                    word = p >> 3;
                    z = unchecked(seed + ((ulong)word * 0x9E3779B97F4A7C15UL) + 0x9E3779B97F4A7C15UL);
                    z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
                    z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
                    z ^= z >> 31;
                }

                destination[i] = (byte)(z >> (int)((p & 7) * 8));
            }
        }
    }

    private sealed class Session(FakeSftpServer owner) : ISftpSession
    {
        public FakeSftpServer Server { get; } = owner;

        public bool Connected { get; set; } = true;

        public bool IsConnected => Connected;

        public string HomeDirectory => Server.Spec.Home;

        private void Check()
        {
            if (!Connected)
            {
                throw new SftpException(SftpFailure.Disconnected, "Connection lost.");
            }
        }

        private FakeFile File(string path)
        {
            Check();
            lock (Server._lock)
            {
                return Server._files.TryGetValue(path, out FakeFile? f) ? f : throw new SftpException(SftpFailure.NotFound, $"No such file: {path}");
            }
        }

        public Task<SftpFileInfo> StatAsync(string path, CancellationToken cancellationToken = default)
        {
            Check();
            lock (Server._lock)
            {
                if (Server._files.TryGetValue(path, out FakeFile? f))
                {
                    return Task.FromResult(new SftpFileInfo(path, f.Length, f.LastWrite, false));
                }

                string prefix = path.TrimEnd('/') + "/";
                if (path == "/" || Server._files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return Task.FromResult(new SftpFileInfo(path, 0, default, true));
                }
            }

            throw new SftpException(SftpFailure.NotFound, $"No such file: {path}");
        }

        public Task<IReadOnlyList<SftpFileInfo>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            Check();
            string prefix = path.TrimEnd('/') + "/";
            var result = new Dictionary<string, SftpFileInfo>();
            lock (Server._lock)
            {
                foreach ((string key, FakeFile f) in Server._files)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string rest = key[prefix.Length..];
                    int slash = rest.IndexOf('/');
                    string full = prefix + (slash < 0 ? rest : rest[..slash]);
                    result[full] = slash < 0 ? new SftpFileInfo(full, f.Length, f.LastWrite, false) : new SftpFileInfo(full, 0, default, true);
                }
            }

            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([.. result.Values.OrderBy(v => v.Path, StringComparer.Ordinal)]);
        }

        public Task<ISftpFile> OpenAsync(string path, SftpOpenMode mode, CancellationToken cancellationToken = default)
        {
            Check();
            Server.Record($"open {path} {mode}");
            if (mode == SftpOpenMode.CreateNew)
            {
                lock (Server._lock)
                {
                    if (Server._files.ContainsKey(path))
                    {
                        throw new SftpException(SftpFailure.Other, "File exists.");
                    }

                    Server._files[path] = new FakeFile(0, null);
                }
            }

            return Task.FromResult<ISftpFile>(new Handle(this, path, File(path)));
        }

        public Task RenameAsync(string from, string to, bool overwrite, CancellationToken cancellationToken = default)
        {
            Check();
            if (overwrite && !Server.PosixRename)
            {
                throw new NotSupportedException("posix-rename@openssh.com is not supported.");
            }

            lock (Server._lock)
            {
                if (!Server._files.Remove(from, out FakeFile? f))
                {
                    throw new SftpException(SftpFailure.NotFound, $"No such file: {from}");
                }

                if (!overwrite && Server._files.ContainsKey(to))
                {
                    Server._files[from] = f;
                    throw new SftpException(SftpFailure.Other, "File exists.");
                }

                Server._files[to] = f;
            }

            Server.Record($"rename {from} {to}");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            Check();
            lock (Server._lock)
            {
                Server._files.Remove(path);
            }

            Server.Record($"delete {path}");
            return Task.CompletedTask;
        }

        public void Dispose() => Connected = false;

        private sealed class Handle(Session session, string path, FakeFile file) : ISftpFile
        {
            public Task<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                session.Check();
                lock (session.Server._lock)
                {
                    int n = (int)Math.Clamp(file.Length - offset, 0, buffer.Length);
                    file.Read(offset, buffer.Span[..n]);
                    session.Server.BytesRead += n;
                    return Task.FromResult(n);
                }
            }

            public Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            {
                session.Check();
                lock (session.Server._lock)
                {
                    file.Write(offset, data.Span);
                }

                session.Server.Record($"write {path} {offset} {data.Length}");
                return Task.CompletedTask;
            }

            public Task SetLengthAsync(long length, CancellationToken cancellationToken = default)
            {
                session.Check();
                lock (session.Server._lock)
                {
                    file.Length = length;
                }

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
#endif
