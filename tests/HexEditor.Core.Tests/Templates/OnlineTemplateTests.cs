using System.Collections.Concurrent;
using System.Globalization;
using HexEditor.Core.Security;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Online;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// オンラインのテンプレートのリポジトリ (07 の TPL-36) の索引・署名・ダウンロード・インストール。通信は偽の配信 (フォルダの
/// TD-TPL-REPO を返し、受けた要求を記録する) で行い、ネットワークには接続しない。
/// </summary>
public sealed class OnlineTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HexEditorCoreTests", "online-" + Guid.NewGuid().ToString("N"));

    public OnlineTemplateTests()
    {
        Directory.CreateDirectory(_root);
        TestDataCatalog.TplRepo.WriteAll(Path.Combine(_root, "repo"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>フォルダの内容を <c>https://repo.test/</c> の下に配信する偽の通信。要求を記録する。</summary>
    private sealed class FolderFeed(string folder) : ITemplateFeedTransport
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        /// <summary>URL のパス → 返すファイル (フォルダからの相対パス) の差し替え。</summary>
        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

        public bool Offline { get; set; }

        public Task<byte[]> GetAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Enqueue(uri);
            if (Offline)
            {
                throw new TemplateFeedUnavailableException("offline");
            }

            string relative = Map.TryGetValue(uri.AbsolutePath, out string? mapped) ? mapped : uri.AbsolutePath.TrimStart('/');
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new TemplateFeedUnavailableException("404 " + uri.AbsolutePath);
            }

            byte[] data = File.ReadAllBytes(path);
            return data.LongLength > maxBytes ? throw new TemplateFeedTooLargeException(maxBytes) : Task.FromResult(data);
        }
    }

    private static readonly Uri IndexUrl = new("https://repo.test/index.json");

    private static TemplateRepository Official() => new(IndexUrl, "official", [TestDataCatalog.TplRepo.PublicKey]);

    private static TemplateRepository UserAdded() => new(IndexUrl, "user", []);

    private FolderFeed Feed() => new(Path.Combine(_root, "repo"));

    private string Installed => Path.Combine(_root, "templates-online");

    // ───────────── Ed25519 (RFC 8032 の 7.1 のテストベクタ) ─────────────

    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    [Trait(TC, "TC-TPL-36-03")]
    public void Ed25519_matches_the_rfc_8032_test_vectors(string seed, string publicKey, string message, string signature)
    {
        byte[] s = Convert.FromHexString(seed);
        byte[] pk = Convert.FromHexString(publicKey);
        byte[] m = Convert.FromHexString(message);
        byte[] sig = Convert.FromHexString(signature);
        Assert.Equal(pk, Ed25519.PublicKeyFromSeed(s));
        Assert.Equal(sig, Ed25519.Sign(s, m));
        Assert.True(Ed25519.Verify(pk, m, sig));

        byte[] bad = [.. sig];
        bad[5] ^= 0x10;
        Assert.False(Ed25519.Verify(pk, m, bad));
        Assert.False(Ed25519.Verify(pk, [.. m, 0x00], sig));
        Assert.False(Ed25519.Verify(pk[..31], m, sig));
    }

    /// <summary>
    /// 検証は BouncyCastle の Ed25519 (フェーズ 4 の監査で自前の移植から置き換えた)。壊れた署名・鍵は例外にせず false: S が L 以上
    /// (署名の可鍛性)、復元できない点の公開鍵、長さの違い。
    /// </summary>
    [Fact]
    public void Ed25519_rejects_malformed_signatures_and_keys_without_throwing()
    {
        byte[] seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        byte[] pk = Ed25519.PublicKeyFromSeed(seed);
        byte[] m = "message"u8.ToArray();
        byte[] sig = Ed25519.Sign(seed, m);
        Assert.True(Ed25519.Verify(pk, m, sig));

        // S に L を足す (同じ点を表すが、RFC 8032 は S < L を求める)。
        byte[] malleable = [.. sig];
        var l = System.Numerics.BigInteger.Parse("7237005577332262213973186563042994240857116359379907606001950938285454250989", System.Globalization.CultureInfo.InvariantCulture);
        var s = new System.Numerics.BigInteger(sig.AsSpan(32), isUnsigned: true) + l;
        byte[] sBytes = s.ToByteArray(isUnsigned: true);
        Array.Clear(malleable, 32, 32);
        sBytes.CopyTo(malleable, 32);
        Assert.False(Ed25519.Verify(pk, m, malleable));

        byte[] notAPoint = [.. Enumerable.Repeat((byte)0xFF, 32)];
        Assert.False(Ed25519.Verify(notAPoint, m, sig));
        Assert.False(Ed25519.Verify(new byte[32], m, sig));
        Assert.False(Ed25519.Verify(pk, m, sig[..63]));
        Assert.False(Ed25519.Verify(pk, m, []));
        Assert.False(Ed25519.Verify([], m, sig));
        var random = new Random(3603);
        for (int i = 0; i < 2000; i++)
        {
            byte[] k = new byte[32], g = new byte[64];
            random.NextBytes(k);
            random.NextBytes(g);
            Assert.False(Ed25519.Verify(k, m, g));
        }
    }

    // ───────────── 索引と署名 ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-36-01")]
    public async Task Official_index_with_a_valid_signature_lists_the_templates()
    {
        FolderFeed feed = Feed();
        TemplateRepositoryListing listing = await new TemplateRepositoryClient(feed).LoadAsync(Official());
        Assert.True(listing.Ok, listing.Message);
        Assert.True(listing.Signed);
        OnlineTemplateEntry entry = Assert.Single(listing.Templates);
        Assert.Equal("hxtest", entry.Id);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("MIT", entry.License);
        Assert.Equal(TestDataCatalog.TplRepo.DisplayName, entry.DisplayName(CultureInfo.GetCultureInfo("en-US")));
        Assert.True(entry.Matches("hxtest"));
        Assert.True(entry.Matches("HXTEST FORMAT"));
        Assert.False(entry.Matches("png"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-03")]
    public async Task Tampered_index_or_signature_from_another_key_is_not_used()
    {
        // 手順 1: 索引の 1 バイトを変えた (署名は元のまま)。
        FolderFeed feed = Feed();
        feed.Map["/index.json"] = "tampered/index-byte.json";
        feed.Map["/index.json.sig"] = "tampered/index-byte.json.sig";
        TemplateRepositoryListing listing = await new TemplateRepositoryClient(feed).LoadAsync(Official());
        Assert.Equal(TemplateRepositoryError.Tampered, listing.Error);
        Assert.Empty(listing.Templates);

        // 手順 2: 署名を別の鍵で作り直した。
        feed.Map["/index.json"] = "tampered/index-otherkey.json";
        feed.Map["/index.json.sig"] = "tampered/index-otherkey.json.sig";
        listing = await new TemplateRepositoryClient(feed).LoadAsync(Official());
        Assert.Equal(TemplateRepositoryError.Tampered, listing.Error);
        Assert.Empty(listing.Templates);

        // 署名のファイルがない公式の索引も使わない。
        feed.Map.Clear();
        feed.Map["/index.json.sig"] = "missing.sig";
        listing = await new TemplateRepositoryClient(feed).LoadAsync(Official());
        Assert.Equal(TemplateRepositoryError.Tampered, listing.Error);
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-03")]
    public async Task Tampered_template_file_is_not_installed_and_leaves_no_file()
    {
        // 手順 3: 正しい索引で、テンプレートのファイルだけを 1 バイト変えて配信する。
        FolderFeed feed = Feed();
        feed.Map["/hxtest-1.0.0.hxt"] = "tampered/hxtest-1.0.0.hxt";
        var client = new TemplateRepositoryClient(feed);
        TemplateRepositoryListing listing = await client.LoadAsync(Official());
        Assert.True(listing.Ok);
        await Assert.ThrowsAsync<TemplateTamperedException>(() => client.DownloadAsync(listing.Repository, listing.Templates[0]));

        // ストアに直接渡しても書かない (二重の確認)。
        var store = new OnlineTemplateStore(Installed);
        byte[] tampered = File.ReadAllBytes(Path.Combine(_root, "repo", "tampered", "hxtest-1.0.0.hxt"));
        Assert.Throws<TemplateTamperedException>(() => store.Install(listing, listing.Templates[0], tampered));
        Assert.Empty(store.Installed);
        Assert.False(Directory.Exists(Installed) && Directory.EnumerateFiles(Installed).Any(), "templates-online/ にファイルが残っています。");
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-03")]
    public async Task User_added_repository_without_a_signature_is_listed_as_unsigned()
    {
        // 手順 4: 署名のない索引 (利用者が追加したリポジトリ。署名のファイルは読まない)。
        FolderFeed feed = Feed();
        TemplateRepositoryListing listing = await new TemplateRepositoryClient(feed).LoadAsync(UserAdded());
        Assert.True(listing.Ok);
        Assert.False(listing.Signed);
        Assert.Single(listing.Templates);
        Assert.DoesNotContain(feed.Requests, u => u.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal));

        byte[] data = await new TemplateRepositoryClient(feed).DownloadAsync(listing.Repository, listing.Templates[0]);
        InstalledOnlineTemplate installed = new OnlineTemplateStore(Installed).Install(listing, listing.Templates[0], data);
        Assert.True(installed.Unsigned);
    }

    // ───────────── インストール・一覧・更新 ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-36-01")]
    public async Task Installed_template_is_in_the_catalog_as_online_and_auto_applies()
    {
        FolderFeed feed = Feed();
        var client = new TemplateRepositoryClient(feed);
        TemplateRepositoryListing listing = await client.LoadAsync(Official());
        var store = new OnlineTemplateStore(Installed);
        store.Install(listing, listing.Templates[0], await client.DownloadAsync(listing.Repository, listing.Templates[0]));
        Assert.True(File.Exists(Path.Combine(Installed, "hxtest.hxt")));

        var catalog = new TemplateCatalog([Path.Combine(_root, "user")], includeStandard: false, onlineFolder: Installed);
        TemplateCatalogEntry entry = Assert.Single(catalog.Entries);
        Assert.Equal(TemplateOrigin.Online, entry.Origin);
        Assert.Equal(TestDataCatalog.TplRepo.DisplayName, entry.Metadata.Name);
        Assert.Equal("1.0.0", entry.Metadata.Version);

        // 自動適用の候補になる (HXTS で始まるデータ。TD-TPL-REPO-DATA)。
        byte[] data = [.. "HXTS"u8, .. new byte[12]];
        using var doc = new Core.Engine.Document(new Core.Sources.MemoryByteSource(data), new Core.Engine.DocumentOptions());
        TemplateCandidate candidate = Assert.Single(catalog.Detect(doc.Current, "sample.hxts"));
        Assert.Equal(entry.Path, candidate.Entry.Path);

        // 削除すると一覧から消える。
        Assert.True(store.Remove("hxtest"));
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-02")]
    public async Task Newer_version_is_found_and_updating_replaces_the_file()
    {
        FolderFeed feed = Feed();
        var client = new TemplateRepositoryClient(feed);
        var store = new OnlineTemplateStore(Installed);
        TemplateRepositoryListing v10 = await client.LoadAsync(Official());
        store.Install(v10, v10.Templates[0], await client.DownloadAsync(v10.Repository, v10.Templates[0]));
        Assert.Empty(store.FindUpdates([v10]));

        // 索引を 1.1.0 を含むものに差し替える。
        feed.Map["/index.json"] = "index-v1.1.json";
        feed.Map["/index.json.sig"] = "index-v1.1.json.sig";
        TemplateRepositoryListing v11 = await client.LoadAsync(Official());
        OnlineTemplateUpdate update = Assert.Single(store.FindUpdates([v11]));
        Assert.Equal("1.0.0", update.Installed.Version);
        Assert.Equal("1.1.0", update.Latest.Version);

        store.Install(v11, update.Latest, await client.DownloadAsync(v11.Repository, update.Latest));
        Assert.Equal("1.1.0", store.Find("hxtest")!.Version);
        Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(Installed, "hxtest.hxt")));
        Assert.Empty(store.FindUpdates([v11]));
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-02")]
    public void Update_check_runs_once_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        Assert.True(OnlineTemplateUpdateSchedule.IsDue(null, now));
        Assert.False(OnlineTemplateUpdateSchedule.IsDue(now.AddHours(-23), now));
        Assert.True(OnlineTemplateUpdateSchedule.IsDue(now.AddHours(-25), now));
        Assert.True(OnlineTemplateUpdateSchedule.IsDue(now.AddHours(2), now)); // 時計が戻った
        Assert.True(TemplateVersion.Compare("1.10.0", "1.9.0") > 0);
        Assert.True(TemplateVersion.Compare("1.1.0-beta", "1.1.0") < 0);
        Assert.Equal(0, TemplateVersion.Compare("v1.0", "1.0.0"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-04")]
    public async Task Unavailable_repository_keeps_installed_templates_usable()
    {
        FolderFeed feed = Feed();
        var client = new TemplateRepositoryClient(feed);
        var store = new OnlineTemplateStore(Installed);
        TemplateRepositoryListing listing = await client.LoadAsync(Official());
        store.Install(listing, listing.Templates[0], await client.DownloadAsync(listing.Repository, listing.Templates[0]));

        feed.Offline = true;
        TemplateRepositoryListing failed = await client.LoadAsync(Official());
        Assert.Equal(TemplateRepositoryError.Unavailable, failed.Error);
        Assert.Empty(store.FindUpdates([failed]));
        var catalog = new TemplateCatalog([], includeStandard: false, onlineFolder: Installed);
        Assert.Single(catalog.Entries);
    }

    [Fact]
    [Trait(TC, "TC-TPL-36-05")]
    public async Task Requests_contain_only_repository_paths()
    {
        // 要求は索引・署名・テンプレートの URL だけで、利用者のファイルの名前・内容を渡す引数を持たない (通信の中身は UI テストで検査する)。
        FolderFeed feed = Feed();
        var client = new TemplateRepositoryClient(feed);
        TemplateRepositoryListing listing = await client.LoadAsync(Official());
        await client.DownloadAsync(listing.Repository, listing.Templates[0]);
        Assert.Equal(["/index.json", "/index.json.sig", "/hxtest-1.0.0.hxt"], feed.Requests.Select(u => u.AbsolutePath));
        Assert.All(feed.Requests, u => Assert.True(string.IsNullOrEmpty(u.Query)));
    }

    [Fact]
    public void Index_rejects_unsafe_ids_and_bad_hashes()
    {
        Assert.Throws<TemplateIndexFormatException>(() => TemplateRepositoryIndex.Parse("""{"templates":[{"id":"../evil","version":"1","sha256":"00","file":"x"}]}"""u8));
        Assert.Throws<TemplateIndexFormatException>(() => TemplateRepositoryIndex.Parse("""{"templates":[{"id":"ok","version":"1","sha256":"zz","file":"x"}]}"""u8));
        Assert.Throws<TemplateIndexFormatException>(() => TemplateRepositoryIndex.Parse("[]"u8));
        Assert.False(TemplateRepository.IsAllowedUrl(new Uri("http://example.com/index.json"), allowLoopbackHttp: true));
        Assert.True(TemplateRepository.IsAllowedUrl(new Uri("http://127.0.0.1:5000/index.json"), allowLoopbackHttp: true));
        Assert.False(TemplateRepository.IsAllowedUrl(new Uri("http://127.0.0.1:5000/index.json"), allowLoopbackHttp: false));
        Assert.True(TemplateRepository.IsAllowedUrl(new Uri("https://example.com/index.json"), allowLoopbackHttp: false));
    }

    [Fact]
    public async Task Template_file_on_another_host_is_not_downloaded()
    {
        FolderFeed feed = Feed();
        var listing = new TemplateRepositoryListing(UserAdded(), [], false, TemplateRepositoryError.None, null);
        var entry = new OnlineTemplateEntry
        {
            Id = "x",
            Names = new Dictionary<string, string> { ["en"] = "x" },
            Version = "1",
            Sha256 = new string('0', 64),
            File = "https://elsewhere.test/x.hxt",
        };
        await Assert.ThrowsAsync<TemplateTamperedException>(() => new TemplateRepositoryClient(feed).DownloadAsync(listing.Repository, entry));
        Assert.Empty(feed.Requests);
    }
}
