using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Remote;

/// <summary>
/// ENG-41 SSH / SFTP。偽の SFTP サーバー (<see cref="FakeSftpServer"/>。ネットワークに触れない) で OpenSSH Server の手順を再現する。
/// 実際のサーバーを使うテストは <see cref="SftpServerFactAttribute"/> (CI のランナーだけ)。
/// </summary>
public sealed class SftpTests : IDisposable
{
    private const string Password = "Hx-Test-Pa55!";
    private readonly string _settings = Directory.CreateTempSubdirectory("hexeditor-sftp").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly SftpEndpoint Endpoint = new() { Host = "localhost", Port = 22, User = "tester" };

    private static FakeSftpServer Server(bool posixRename = true) =>
        new(new FakeSftpSpec { Passwords = new() { ["tester"] = Password }, KeyUsers = ["tester"], PosixRename = posixRename }) { PosixRename = posixRename };

    private KnownHosts TrustedHosts(FakeSftpServer server)
    {
        var hosts = new KnownHosts(Path.Combine(_settings, KnownHosts.FileName));
        hosts.Trust(Endpoint.Host, Endpoint.Port, server.HostKey, DateTime.UtcNow);
        return hosts;
    }

    private static readonly SftpCredentials PasswordLogin = new() { Method = SftpAuthMethod.Password, Password = Password };

    private static readonly SftpCredentials KeyLogin = new() { Method = SftpAuthMethod.KeyFile, KeyFile = @"C:\keys\id_ed25519" };

    [Fact]
    [Trait("TC", "TC-ENG-41-01")]
    public async Task A_10_GB_remote_file_opens_without_reading_the_whole_file()
    {
        FakeSftpServer server = Server();
        server.AddFile("/data/sparse10g.bin", 10L * 1024 * 1024 * 1024);
        var watch = Stopwatch.StartNew();
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, KeyLogin, server, TrustedHosts(server));
        SftpByteSource source = await SftpByteSource.OpenAsync(connection, "/data/sparse10g.bin");
        using var doc = new Document(source);

        // 先頭の表示 (最初のブロック) を読む。
        byte[] screen = new byte[4096];
        Assert.True(doc.Cache.ReadThrough(0, screen).IsComplete);
        watch.Stop();
        Assert.Equal(10L * 1024 * 1024 * 1024, doc.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.True(server.BytesRead <= 1024 * 1024, server.BytesRead.ToString());
    }

    private static byte[] Random16M(long offset, int length)
    {
        byte[] data = new byte[length];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, offset, data);
        return data;
    }

    [Fact]
    [Trait("TC", "TC-ENG-41-02")]
    public async Task Saving_a_one_byte_change_writes_only_that_byte()
    {
        FakeSftpServer server = Server();
        const string path = "/data/random16m.bin";
        server.AddFile(path, 16 * 1024 * 1024, TestDataCatalog.RandomSeed);
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, KeyLogin, server, TrustedHosts(server));
        SftpByteSource source = await SftpByteSource.OpenAsync(connection, path);
        using var doc = new Document(source);

        // 1. 0x800000 の 1 バイトを元の値の XOR 0xFF に上書きする。
        byte original = Random16M(0x800000, 1)[0];
        doc.Overwrite(0x800000, [(byte)(original ^ 0xFF)]);

        // 2. 保存 (変更範囲だけ)。
        SftpSavePlan plan = SftpSaver.Plan(doc, source);
        Assert.Equal(SftpSaveMethod.ChangedRanges, plan.Method);
        IReadOnlyList<SavedRange> written = await SftpSaver.WriteChangedRangesAsync(doc, source, plan);
        doc.CompleteTargetWrite(source, written, allWritten: true);
        Assert.False(doc.IsModified);

        // 3. 長さが 16 MiB のまま、0x800000 だけが変わり、他は元の内容。
        Assert.Equal(16 * 1024 * 1024, server.LengthOf(path));
        Assert.Equal((byte)(original ^ 0xFF), server.ReadFile(path, 0x800000, 1)[0]);
        Assert.Equal(SHA256.HashData(Random16M(0, 0x800000)), SHA256.HashData(server.ReadFile(path, 0, 0x800000)));
        Assert.Equal(SHA256.HashData(Random16M(0x800001, 0x7FFFFF)), SHA256.HashData(server.ReadFile(path, 0x800001, 0x7FFFFF)));

        // 一時ファイルを作らず、書き込みは元のファイルへの 1 回だけ。
        Assert.Equal([$"write {path} {0x800000} 1"], server.Log.Where(l => l.StartsWith("write", StringComparison.Ordinal)));
        Assert.DoesNotContain(server.Log, l => l.Contains("CreateNew", StringComparison.Ordinal) || l.StartsWith("rename", StringComparison.Ordinal));

        // 保存前の版 (Undo) は旧内容を読む。
        doc.Undo();
        byte[] b = new byte[1];
        doc.Current.Read(0x800000, b);
        Assert.Equal(original, b[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_length_change_is_saved_through_a_temporary_file_and_rename(bool posixRename)
    {
        FakeSftpServer server = Server(posixRename);
        const string path = "/data/small.bin";
        server.AddFile(path, 4096, 7);
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, KeyLogin, server, TrustedHosts(server));
        SftpByteSource source = await SftpByteSource.OpenAsync(connection, path);
        using var doc = new Document(source);
        byte[] before = server.ReadFile(path, 0, 4096);
        doc.Insert(0, [1, 2, 3]);

        SftpSavePlan plan = SftpSaver.Plan(doc, source);
        Assert.Equal(SftpSaveMethod.Replace, plan.Method);
        SftpByteSource saved = await SftpSaver.ReplaceAsync(doc, source);
        doc.CompleteSave(saved);

        Assert.Equal(4099, server.LengthOf(path));
        Assert.Equal([1, 2, 3, .. before], server.ReadFile(path, 0, 4099));
        Assert.Contains(server.Log, l => l.StartsWith("open /data/.small.bin.hexeditor-", StringComparison.Ordinal) && l.EndsWith("CreateNew", StringComparison.Ordinal));
        Assert.Equal([path], server.Paths.Where(p => p.StartsWith("/data/", StringComparison.Ordinal)));
        if (!posixRename)
        {
            // 退避 → 置き換え → 退避の削除。
            Assert.Equal(2, server.Log.Count(l => l.StartsWith("rename", StringComparison.Ordinal)));
            Assert.Contains(server.Log, l => l.StartsWith("delete /data/small.bin.hexeditor-old-", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-41-03")]
    public async Task A_changed_host_key_is_rejected_before_authentication()
    {
        FakeSftpServer server = Server();
        KnownHosts hosts = TrustedHosts(server);
        HostKeyInfo trusted = server.HostKey;

        // サーバーのホスト鍵を作り直す。
        server.HostKey = new HostKeyInfo("ecdsa-sha2-nistp256", "SHA256:regeneratedKey1111111111111111111111111111");
        HostKeyRejectedException ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SftpConnection.ConnectAsync(Endpoint, PasswordLogin, server, hosts));
        Assert.Equal(HostKeyStatus.Changed, ex.Status);
        Assert.Equal(server.HostKey, ex.Key);
        Assert.Equal(trusted, ex.Previous);

        // SFTP のセッションは作られず、パスワードも送っていない。
        Assert.Equal(0, server.SessionsCreated);
        Assert.Equal(0, server.PasswordAttempts);

        // 表は変わっていない (黙って受け入れない)。
        Assert.Equal(HostKeyStatus.Changed, new KnownHosts(Path.Combine(_settings, KnownHosts.FileName)).Check("localhost", 22, server.HostKey, out _));
    }

    [Fact]
    public async Task An_unknown_host_is_trusted_only_after_the_user_accepts_it()
    {
        FakeSftpServer server = Server();
        var hosts = new KnownHosts(Path.Combine(_settings, KnownHosts.FileName));
        HostKeyRejectedException ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SftpConnection.ConnectAsync(Endpoint, PasswordLogin, server, hosts));
        Assert.Equal(HostKeyStatus.Unknown, ex.Status);
        Assert.StartsWith("SHA256:", ex.Key.Fingerprint, StringComparison.Ordinal);

        // 「信頼して接続」: 表に加えて接続し直す。
        hosts.Trust(Endpoint.Host, Endpoint.Port, ex.Key, DateTime.UtcNow);
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, PasswordLogin, server, hosts);
        Assert.Equal(1, server.SessionsCreated);
        Assert.True(File.Exists(Path.Combine(_settings, KnownHosts.FileName)));
    }

    [Fact]
    public async Task Authentication_failures_are_distinguished()
    {
        FakeSftpServer server = Server();
        SftpException ex = await Assert.ThrowsAsync<SftpException>(() => SftpConnection.ConnectAsync(Endpoint,
            new SftpCredentials { Method = SftpAuthMethod.Password, Password = "wrong" }, server, TrustedHosts(server)));
        Assert.Equal(SftpFailure.Authentication, ex.Failure);
        server.Down = true;
        ex = await Assert.ThrowsAsync<SftpException>(() => SftpConnection.ConnectAsync(Endpoint, PasswordLogin, server, TrustedHosts(server)));
        Assert.Equal(SftpFailure.Connect, ex.Failure);
    }

    [Fact]
    [Trait("TC", "TC-ENG-41-04")]
    public async Task A_password_that_is_not_saved_is_not_left_in_the_settings_folder()
    {
        FakeSftpServer server = Server();
        server.AddFile("/home/test/a.bin", 1024, 3);
        var credentials = new MemoryCredentialStore();
        var recent = new RecentFileList();
        var store = new RecentFileStore(_settings);

        // 1. パスワード認証で接続し、「保存する」はオフ。
        KnownHosts hosts = TrustedHosts(server);
        using (SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, PasswordLogin, server, hosts))
        {
            SftpLogin.RememberSecret(credentials, Endpoint, PasswordLogin, save: false);

            // 2. ファイルを開いて閉じる (最近使ったファイルに記録)。
            using SftpByteSource source = await SftpByteSource.OpenAsync(connection, "/home/test/a.bin");
            recent.Record(source.Uri.ToString(), source.DisplayName, DateTime.UtcNow, RecentItemKind.Sftp);
            Assert.Null(store.Save(recent));
        }

        // 3. 設定フォルダのすべてのファイルに、UTF-8・UTF-16 LE のパスワードがない。
        byte[][] needles = [Encoding.UTF8.GetBytes(Password), Encoding.Unicode.GetBytes(Password)];
        foreach (string file in Directory.EnumerateFiles(_settings, "*", SearchOption.AllDirectories))
        {
            byte[] content = File.ReadAllBytes(file);
            foreach (byte[] needle in needles)
            {
                Assert.True(content.AsSpan().IndexOf(needle) < 0, file);
            }
        }

        // 4. 資格情報の保存先にそのホストの資格情報がない。
        Assert.Empty(credentials.Keys);

        // 5. recent.json の項目は sftp://<ユーザー>@localhost:22/<パス> で、パスワードを含まない。
        var reloaded = new RecentFileList();
        store.Load(reloaded);
        RecentItem item = Assert.Single(reloaded.Items);
        Assert.Equal("sftp://tester@localhost:22/home/test/a.bin", item.Path);
        Assert.Equal(RecentItemKind.Sftp, item.Kind);
    }

    [Fact]
    public void A_password_is_saved_only_when_the_user_chooses_to()
    {
        var store = new MemoryCredentialStore();
        SftpLogin.RememberSecret(store, Endpoint, PasswordLogin, save: true);
        Assert.Equal(Password, store.Read(Endpoint.Key));
        Assert.Equal(Password, SftpLogin.SavedSecret(store, Endpoint));

        // 次に「保存する」をオフにして接続したら消す。
        SftpLogin.RememberSecret(store, Endpoint, PasswordLogin, save: false);
        Assert.Null(store.Read(Endpoint.Key));
        Assert.DoesNotContain(Password, PasswordLogin.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dropped_connection_reconnects_once_automatically_then_reports_disconnected()
    {
        FakeSftpServer server = Server();
        server.AddFile("/a.bin", 8192, 5);
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, KeyLogin, server, TrustedHosts(server));
        using SftpByteSource source = await SftpByteSource.OpenAsync(connection, "/a.bin");
        int disconnects = 0;
        source.Changed += (_, e) => disconnects += e.Kind == SourceChangeKind.Disconnected ? 1 : 0;

        // 切れたが、自動の再接続で読める。
        server.DropConnections();
        byte[] data = new byte[16];
        Assert.True(source.Read(0, data).IsComplete);
        Assert.Equal(2, server.SessionsCreated);

        // サーバーが止まっていると、1 回だけ試して「切断」にする。
        server.DropConnections();
        server.Down = true;
        ReadResult r = source.Read(0, data);
        Assert.Contains(r.Unreadable, u => u.Reason == UnreadableReason.Disconnected);
        Assert.Equal(RemoteConnectionState.Disconnected, connection.State);
        Assert.Equal(1, disconnects);
        Assert.False(source.Read(0, data).IsComplete);
        Assert.Equal(1, disconnects);

        // 「再接続」で再開する。
        server.Down = false;
        await source.ReconnectAsync();
        Assert.True(source.Read(4096, data).IsComplete);
    }

    [Fact]
    public async Task External_changes_are_detected_by_size_and_time()
    {
        FakeSftpServer server = Server();
        server.AddFile("/a.bin", 100);
        using SftpConnection connection = await SftpConnection.ConnectAsync(Endpoint, KeyLogin, server, TrustedHosts(server));
        using SftpByteSource source = await SftpByteSource.OpenAsync(connection, "/a.bin");
        Assert.False(await source.CheckExternalChangeAsync());
        server.TouchExternally("/a.bin", [1], 0);
        Assert.True(await source.CheckExternalChangeAsync());
    }

    [Fact]
    public void Sftp_uris_round_trip_without_passwords()
    {
        var uri = new SftpUri(new SftpEndpoint { Host = "example.test", Port = 2222, User = "a b" }, "/x/y z.bin");
        string text = uri.ToString();
        Assert.Equal("sftp://a%20b@example.test:2222/x/y z.bin", text);
        SftpUri parsed = SftpUri.TryParse(text)!;
        Assert.Equal(uri.Endpoint, parsed.Endpoint);
        Assert.Equal("/x/y z.bin", parsed.Path);
        Assert.Null(SftpUri.TryParse("sftp://user:secret@host/x"));
        Assert.Equal(22, SftpUri.TryParse("sftp://u@h/p")!.Endpoint.Port);
    }

    [Fact]
    public async Task The_SSH_NET_connector_reports_a_refused_connection()
    {
        // localhost の閉じたポート (外部のホストには接続しない)。
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var endpoint = new SftpEndpoint { Host = "127.0.0.1", Port = port, User = "nobody" };
        SftpException ex = await Assert.ThrowsAsync<SftpException>(() => SftpConnection.ConnectAsync(endpoint,
            new SftpCredentials { Method = SftpAuthMethod.Password, Password = "x" }, new SshNetSftpConnector(), new KnownHosts(null),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(SftpFailure.Connect, ex.Failure);
    }

    [SftpServerFact]
    [Trait("TC", "TC-ENG-41-02")]
    [Trait("Category", "Integration")]
    public async Task A_real_server_receives_a_one_byte_change()
    {
        (SftpEndpoint endpoint, SftpCredentials login, string path) = SftpServerFactAttribute.Settings();
        var hosts = new KnownHosts(null);
        var connector = new SshNetSftpConnector();
        HostKeyRejectedException first = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            SftpConnection.ConnectAsync(endpoint, login, connector, hosts));
        hosts.Trust(endpoint.Host, endpoint.Port, first.Key, DateTime.UtcNow);
        using SftpConnection connection = await SftpConnection.ConnectAsync(endpoint, login, connector, hosts);
        SftpByteSource source = await SftpByteSource.OpenAsync(connection, path);
        using var doc = new Document(source);
        byte[] one = new byte[1];
        doc.Current.Read(0x800000, one);
        doc.Overwrite(0x800000, [(byte)(one[0] ^ 0xFF)]);
        SftpSavePlan plan = SftpSaver.Plan(doc, source);
        Assert.Equal(SftpSaveMethod.ChangedRanges, plan.Method);
        doc.CompleteTargetWrite(source, await SftpSaver.WriteChangedRangesAsync(doc, source, plan), allWritten: true);
        using SftpByteSource again = await SftpByteSource.OpenAsync(connection, path);
        byte[] check = new byte[1];
        Assert.True(again.Read(0x800000, check).IsComplete);
        Assert.Equal((byte)(one[0] ^ 0xFF), check[0]);
    }
}

/// <summary>
/// 実際の SFTP サーバー (CI のランナーの OpenSSH Server) があるときだけ動く Fact。HEXEDITOR_SFTP_HOST・HEXEDITOR_SFTP_USER・
/// HEXEDITOR_SFTP_KEY (鍵ファイル)・HEXEDITOR_SFTP_FILE (TD-RANDOM-16M のパス) を CI で設定する。開発者の PC には入れない。
/// </summary>
public sealed class SftpServerFactAttribute : FactAttribute
{
    public SftpServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HEXEDITOR_SFTP_HOST") is not { Length: > 0 })
        {
            Skip = "HEXEDITOR_SFTP_HOST がない (OpenSSH Server を用意した CI のランナーだけで動く)。";
        }
    }

    public static (SftpEndpoint Endpoint, SftpCredentials Login, string Path) Settings() =>
    (
        new SftpEndpoint { Host = Environment.GetEnvironmentVariable("HEXEDITOR_SFTP_HOST")!, User = Environment.GetEnvironmentVariable("HEXEDITOR_SFTP_USER")! },
        new SftpCredentials { Method = SftpAuthMethod.KeyFile, KeyFile = Environment.GetEnvironmentVariable("HEXEDITOR_SFTP_KEY") },
        Environment.GetEnvironmentVariable("HEXEDITOR_SFTP_FILE")!
    );
}
