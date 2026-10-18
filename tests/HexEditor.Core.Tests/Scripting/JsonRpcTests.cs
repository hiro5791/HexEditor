using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Docs;
using HexEditor.Core.Scripting.Rpc;
using HexEditor.Sdk;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// 外部からの自動操作の JSON-RPC (08 の AUTO-45) のメッセージの処理: メソッドの名前、引数と結果の変換、エラーコード、キャンセル、通知、
/// Undo の単位。パイプの作成・ACL・接続の確認は Platform と UI のテスト。
/// </summary>
public sealed class JsonRpcTests : IDisposable
{
    private const string TC = "TC";
    private readonly ScriptTestHost _t = new(language: "en");

    public void Dispose() => _t.Dispose();

    /// <summary>セッションとクライアントの組 (同じプロセスの中の名前付きパイプでつなぐ)。</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        private readonly StreamReader _reader;
        private readonly Task _run;
        private int _id;

        public Pair(ScriptTestHost t, TimeSpan? idle = null, IReadOnlyCollection<string>? permissions = null)
        {
            string name = "HexEditor.test." + Guid.NewGuid().ToString("N");
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task wait = _server.WaitForConnectionAsync();
            _client.Connect(5000);
            wait.Wait();
            Context = t.Host.OpenExternalContext("rpc-test.exe", t.Env, permissions ?? JsonRpcTrust.GrantedPermissions);
            Session = new JsonRpcSession(Context, _server) { IdleRelease = idle ?? TimeSpan.FromSeconds(30) };
            Host = t;
            _run = Session.RunAsync();
            _reader = new StreamReader(_client, new UTF8Encoding(false));
        }

        public ScriptTestHost Host { get; }

        public ScriptContext Context { get; }

        public JsonRpcSession Session { get; }

        public List<JsonObject> Notifications { get; } = [];

        public async Task SendBytesAsync(byte[] bytes)
        {
            await _client.WriteAsync(bytes);
            await _client.FlushAsync();
        }

        public async Task SendRawAsync(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _client.WriteAsync(bytes);
            await _client.FlushAsync();
        }

        /// <summary>応答 (通知は <see cref="Notifications"/> にためる)。</summary>
        public async Task<JsonObject> ReadResponseAsync()
        {
            while (true)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                string? line = await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("closed");
                var o = (JsonObject)JsonNode.Parse(line)!;
                if (o.ContainsKey("id"))
                {
                    return o;
                }

                Notifications.Add(o);
            }
        }

        public async Task<JsonObject> CallAsync(string method, JsonNode? parameters = null)
        {
            int id = ++_id;
            await SendRawAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            JsonObject response = await ReadResponseAsync();
            Assert.Equal(id, (int)response["id"]!);
            return response;
        }

        public async Task<JsonNode?> ResultAsync(string method, JsonNode? parameters = null)
        {
            JsonObject r = await CallAsync(method, parameters);
            Assert.True(r.ContainsKey("result"), r.ToJsonString());
            return r["result"];
        }

        public async Task<int> ErrorAsync(string method, JsonNode? parameters = null)
        {
            JsonObject r = await CallAsync(method, parameters);
            Assert.True(r["error"] is JsonObject, r.ToJsonString());
            return (int)r["error"]!["code"]!;
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Run(() => Host.Host.CloseExternalContext(Context));
            await _server.DisposeAsync();
        }
    }

    private static byte[] Bytes256() => [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    [Fact]
    [Trait(TC, "TC-AUTO-45-02")]
    public async Task Open_and_read_bytes_return_base64()
    {
        string path = Path.Combine(_t.Root, "bytes-256.bin");
        File.WriteAllBytes(path, Bytes256());
        await using var p = new Pair(_t);

        JsonNode doc = (await p.ResultAsync("app.open", new JsonArray(path)))!;
        string id = (string)doc["id"]!;
        Assert.Equal(256, (long)doc["length"]!);
        Assert.Equal(path, (string)doc["path"]!);

        // 位置引数: 先頭がドキュメントの id。
        string base64 = (string)(await p.ResultAsync("document.readBytes", new JsonArray(id, 0x10, 16)))!;
        Assert.Equal("10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F", string.Join(" ", Convert.FromBase64String(base64).Select(b => b.ToString("X2"))));

        // 名前付きの引数と入力式 (AUTO-04 の仕様 6)。
        base64 = (string)(await p.ResultAsync("document.readBytes", new JsonObject { ["document"] = id, ["offset"] = "end-2", ["length"] = 2 }))!;
        Assert.Equal([0xFE, 0xFF], Convert.FromBase64String(base64));

        // プロパティもメソッドの名前で読める。
        JsonArray docs = (JsonArray)(await p.ResultAsync("app.documents"))!;
        Assert.Equal(id, (string)Assert.Single(docs)!["id"]!);
        Assert.Equal(256, (long)(await p.ResultAsync("document.length", new JsonArray(id)))!);
        Assert.Equal(ApiInfo.Version, (string)(await p.ResultAsync("app.apiVersion"))!);
    }

    [Fact]
    [Trait(TC, "TC-AUTO-45-05")]
    public async Task Documents_list_has_paths_and_lengths()
    {
        _t.Open(new byte[1024 * 1024], "seq.bin");
        _t.Open(new byte[1024 * 1024], "zero.bin");
        await using var p = new Pair(_t);
        JsonArray docs = (JsonArray)(await p.ResultAsync("app.documents"))!;
        Assert.Equal(2, docs.Count);
        Assert.All(docs, d => Assert.Equal(1_048_576, (long)d!["length"]!));
        Assert.Equal(["seq.bin", "zero.bin"], docs.Select(d => (string)d!["name"]!));
    }

    [Fact]
    [Trait(TC, "TC-AUTO-45-02")]
    public async Task Writes_are_undoable_as_one_unit_per_burst()
    {
        CoreDocumentHost host = _t.Open(new byte[16]);
        await using var p = new Pair(_t, idle: TimeSpan.FromMilliseconds(200));
        JsonNode doc = (await p.ResultAsync("app.activeDocument"))!;
        string id = (string)doc["id"]!;
        await p.ResultAsync("document.writeBytes", new JsonArray(id, 0, Convert.ToBase64String([1, 2])));
        await p.ResultAsync("document.writeBytes", new JsonArray(id, 4, new JsonObject { ["hex"] = "AA BB" }));
        Assert.Equal([1, 2, 0, 0, 0xAA, 0xBB], Read(host, 6));

        // 要求が途切れると書き込みの排他を手放し、利用者が Undo できる (2 つの書き込みが 1 つの単位)。
        await WaitUntilAsync(() => !_t.OnUi(() => host.Document.IsEditLocked));
        _t.OnUi(() => host.Document.Undo());
        Assert.Equal(new byte[6], Read(host, 6));
    }

    private byte[] Read(CoreDocumentHost host, int length) => _t.OnUi(() =>
    {
        byte[] b = new byte[length];
        host.Document.Current.Read(0, b);
        return b;
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "条件が満たされません。");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Errors_use_the_documented_codes()
    {
        CoreDocumentHost host = _t.Open(new byte[16]);
        _t.OnUi(() => host.Document.SetReadOnly(HexEditor.Core.Engine.ReadOnlyReason.OpenedReadOnly));
        await using var p = new Pair(_t);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, await p.ErrorAsync("document.nothing"));
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, await p.ErrorAsync("app.exit", new JsonArray(0)));
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, await p.ErrorAsync("document.chunks"));
        string id = (string)(await p.ResultAsync("app.activeDocument"))!["id"]!;
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, await p.ErrorAsync("document.readBytes", new JsonArray(id, true, 1)));
        Assert.Equal(JsonRpcErrorCodes.RangeError, await p.ErrorAsync("document.readBytes", new JsonArray(id, 100, 1)));
        Assert.Equal(JsonRpcErrorCodes.ReadOnlyError, await p.ErrorAsync("document.writeBytes", new JsonArray(id, 0, "AA==")));
        Assert.Equal(JsonRpcErrorCodes.ArgumentError, await p.ErrorAsync("document.readBytes", new JsonArray("no-such-id", 0, 1)));

        // 形式の誤り。
        await p.SendRawAsync("{ not json");
        Assert.Equal(JsonRpcErrorCodes.ParseError, (int)(await p.ReadResponseAsync())["error"]!["code"]!);
        await p.SendRawAsync("""{"jsonrpc":"1.0","id":9,"method":"app.version"}""");
        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, (int)(await p.ReadResponseAsync())["error"]!["code"]!);
    }

    [Fact]
    public async Task Permission_error_for_disk_without_a_user_decision()
    {
        // disk は使うときに確認する (偽の UI は拒否する)。
        _t.Ui.PermissionAnswer = false;
        await using var p = new Pair(_t);
        JsonObject r = await p.CallAsync("app.openDisk", new JsonArray("PhysicalDrive0"));
        Assert.Equal(JsonRpcErrorCodes.PermissionError, (int)r["error"]!["code"]!);
        Assert.Equal("disk", (string)r["error"]!["data"]!["permission"]!);
    }

    [Fact]
    public async Task Cancel_notification_stops_a_long_request()
    {
        // 1 GiB の仮想のドキュメントで、見つからないパターンを数える (時間がかかる) 要求をキャンセルする。
        _t.Open(new HexEditor.Core.Sources.VirtualByteSource(1L << 30, HexEditor.Core.Sources.VirtualContent.Zero));
        await using var p = new Pair(_t);
        string id = (string)(await p.ResultAsync("app.activeDocument"))!["id"]!;
        await p.SendRawAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 77, ["method"] = "document.count", ["params"] = new JsonArray(id, new JsonObject { ["hex"] = "DE AD BE EF" }) }.ToJsonString());
        await p.SendRawAsync("""{"jsonrpc":"2.0","method":"$/cancel","params":{"id":77}}""");
        JsonObject response = await p.ReadResponseAsync();
        Assert.Equal(77, (int)response["id"]!);
        Assert.Equal(JsonRpcErrorCodes.CanceledError, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task Subscribed_events_arrive_as_notifications()
    {
        await using var p = new Pair(_t);
        JsonNode sub = (await p.ResultAsync("app.on", new JsonArray("documentOpened")))!;
        Assert.Equal("EventSubscription", (string)sub["type"]!);
        _t.Open(new byte[4], "opened.bin");

        // 通知が届くまで、何もしない要求で読み進める。
        await WaitUntilAsync(() =>
        {
            p.ResultAsync("app.version").GetAwaiter().GetResult();
            return p.Notifications.Any(n => (string?)n["method"] == "event");
        });
        JsonObject e = p.Notifications.First(n => (string?)n["method"] == "event");
        Assert.Equal("documentOpened", (string)e["params"]!["name"]!);
        Assert.Equal("opened.bin", (string)e["params"]!["document"]!["name"]!);

        // 購読をやめる (handle)。
        await p.ResultAsync("eventSubscription.off", new JsonObject { ["handle"] = (string)sub["handle"]! });
    }

    /// <summary>
    /// 壊れたメッセージ (重複したキー、型の違う値、深い入れ子、壊れた UTF-8 など。変異させたものを含む) でも接続は切れず、
    /// 続く正しい要求に応答する (フェーズ 4 の監査)。許可のない接続で送る (変異でファイル・プロセスの API を呼ばないように)。
    /// </summary>
    [Fact]
    public async Task Malformed_messages_never_end_the_session()
    {
        _t.Open(new byte[64]);
        await using var p = new Pair(_t, permissions: []);
        string[] seeds =
        [
            """{"jsonrpc":"2.0","id":1,"method":"app.documents"}""",
            """{"jsonrpc":"2.0","id":"a","method":"document.readBytes","params":{"offset":0,"length":4}}""",
            """[{"jsonrpc":"2.0","id":2,"method":"app.activeDocument"},{"jsonrpc":"2.0","method":"$/cancel","params":{"id":2}}]""",
        ];
        var malformed = new List<string>
        {
            """{"jsonrpc":"2.0","jsonrpc":"2.0","id":1,"method":"app.documents"}""",
            """{"jsonrpc":"2.0","id":1,"id":2,"method":"app.documents"}""",
            """{"jsonrpc":"2.0","id":1,"method":"document.readBytes","params":{"offset":0,"offset":1}}""",
            "[" + string.Join(",", Enumerable.Repeat("""{"a":1,"a":2}""", 3)) + "]",
            new string('[', 200) + new string(']', 200),
            """{"jsonrpc":2,"method":[1]}""",
            """{"jsonrpc":"2.0","id":{"x":1},"method":"app.documents","params":"x"}""",
            "\"just a string\"",
            """{"jsonrpc":"2.0","id":1,"method":"bookmark.name","params":{"handle":{"handle":[]}}}""",
        };
        var random = new Random(4501);
        for (int i = 0; i < 300; i++)
        {
            malformed.Add(Security.Fuzz.MutateText(random, seeds[random.Next(seeds.Length)], "{}[],:\"0123456789.-eE\\u ").Replace('\n', ' '));
        }

        // 誤りの応答を読みながら送る (読まないとパイプが詰まる)。最後の正しい要求 (id 999999) の応答まで読む。
        Task<JsonObject> last = Task.Run(async () =>
        {
            while (true)
            {
                JsonObject o = await p.ReadResponseAsync();
                if (o["id"] is JsonValue v && v.TryGetValue(out int n) && n == 999999)
                {
                    return o;
                }
            }
        });
        foreach (string line in malformed)
        {
            await p.SendRawAsync(line);
        }

        // 壊れた UTF-8 も 1 行。
        byte[] broken = [0xFF, 0xFE, 0x7B, 0xC0, 0x80, 0x7D, (byte)'\n'];
        await p.SendBytesAsync(broken);
        await p.SendRawAsync("""{"jsonrpc":"2.0","id":999999,"method":"app.apiVersion"}""");
        JsonObject ok = await last.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(ok.ContainsKey("result"), ok.ToJsonString());
    }

    /// <summary>参照 (handle) の表は、ドキュメントを閉じたらそのドキュメントの分を、接続が切れたらすべてを捨てる (フェーズ 4 の監査)。</summary>
    [Fact]
    public async Task Handles_are_released_when_the_document_closes()
    {
        _t.Open(new byte[64]);
        var p = new Pair(_t);
        try
        {
            string id = (string)(await p.ResultAsync("app.activeDocument"))!["id"]!;
            JsonNode mark = (await p.ResultAsync("bookmarks.add", new JsonObject
            {
                ["document"] = id,
                ["bookmark"] = new JsonObject { ["start"] = 16, ["length"] = 4, ["name"] = "header" },
            }))!;
            string handle = (string)mark["handle"]!;
            Assert.Equal(1, p.Session.HandleCount);
            await p.ResultAsync("document.close", new JsonObject { ["document"] = id });
            Assert.Equal(0, p.Session.HandleCount);
            Assert.Equal(JsonRpcErrorCodes.InvalidParams, await p.ErrorAsync("bookmark.name", new JsonObject { ["handle"] = handle }));
            await p.ResultAsync("bookmarks.add", new JsonObject
            {
                ["document"] = (string)(await p.ResultAsync("app.open", new JsonArray(WriteTemp())))!["id"]!,
                ["bookmark"] = new JsonObject { ["start"] = 0, ["length"] = 1, ["name"] = "x" },
            });
            Assert.Equal(1, p.Session.HandleCount);
        }
        finally
        {
            await p.DisposeAsync();
        }

        // 接続が切れたらすべて捨てる。
        Assert.Equal(0, p.Session.HandleCount);
    }

    /// <summary>確認の答えを待たせる UI (答えるまで ConfirmTrustAsync が終わらない)。</summary>
    private sealed class GatedTrustUi(CoreScriptEnvironment env) : HeadlessScriptUi(env)
    {
        private readonly object _lock = new();

        public List<TaskCompletionSource<TrustAnswer>> Pending { get; } = [];

        public int Prompts
        {
            get
            {
                lock (_lock)
                {
                    return Pending.Count;
                }
            }
        }

        public override Task<TrustAnswer> ConfirmTrustAsync(TrustPrompt prompt)
        {
            var tcs = new TaskCompletionSource<TrustAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                Pending.Add(tcs);
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// 接続の確認は 1 つずつ出し (列は 1 つ)、待っている接続が上限を超えたら確認を出さずに拒否する。待っている間に同じ実行ファイルを
    /// 拒否・常に許可したら、待っていた接続は確認を出さずにその答えになる (フェーズ 4 の監査: 確認のダイアログで画面を埋められない)。
    /// </summary>
    [Theory]
    [InlineData(TrustAnswer.Deny, false)]
    [InlineData(TrustAnswer.AlwaysAllow, true)]
    public async Task Trust_prompts_are_queued_one_at_a_time(TrustAnswer answer, bool allowed)
    {
        string exe = Path.Combine(_t.Root, "client-" + answer + ".exe");
        File.WriteAllBytes(exe, Encoding.ASCII.GetBytes("MZ fake client " + answer));
        var ui = new GatedTrustUi(_t.Env);
        IScriptUiService previous = _t.Env.Ui;
        _t.Env.Ui = ui;
        try
        {
            var client = new JsonRpcClient(exe, 1234, null);
            Task<JsonRpcAdmission>[] checks = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() => JsonRpcTrust.CheckAsync(_t.Host, _t.Env, client)))];

            // 上限を超えた分 (8 - 4) はすぐに拒否される。確認は 1 つだけ出ている。
            await WaitUntilAsync(() => checks.Count(c => c.IsCompleted) == 8 - JsonRpcTrust.MaxWaitingPrompts && ui.Prompts == 1);
            Assert.All(checks.Where(c => c.IsCompleted), c => Assert.False(c.Result.Allowed));
            Assert.Equal(1, ui.Prompts);

            ui.Pending[0].SetResult(answer);
            JsonRpcAdmission[] results = await Task.WhenAll(checks).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, ui.Prompts);
            Assert.Equal(allowed ? JsonRpcTrust.MaxWaitingPrompts : 0, results.Count(r => r.Allowed));
        }
        finally
        {
            _t.Env.Ui = previous;
        }
    }

    private string WriteTemp()
    {
        string path = Path.Combine(_t.Root, "handles-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    [Fact]
    public async Task Api_objects_are_passed_by_handle()
    {
        _t.Open(new byte[64]);
        await using var p = new Pair(_t);
        string id = (string)(await p.ResultAsync("app.activeDocument"))!["id"]!;
        JsonNode mark = (await p.ResultAsync("bookmarks.add", new JsonObject
        {
            ["document"] = id,
            ["bookmark"] = new JsonObject { ["start"] = 16, ["length"] = 4, ["name"] = "header" },
        }))!;
        Assert.Equal("Bookmark", (string)mark["type"]!);
        Assert.Equal(16, (long)mark["start"]!);
        string handle = (string)mark["handle"]!;
        Assert.Equal("header", (string)(await p.ResultAsync("bookmark.name", new JsonObject { ["handle"] = handle }))!);

        // 探したものも handle で返る。知らない handle は引数の誤り。
        JsonNode found = (await p.ResultAsync("bookmarks.find", new JsonArray(id, "header")))!;
        Assert.Equal("header", (string)found["name"]!);
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, await p.ErrorAsync("bookmark.name", new JsonObject { ["handle"] = "h999" }));
    }

    [Fact]
    public void Method_list_matches_the_api_and_marks_unavailable_ones()
    {
        IReadOnlyDictionary<string, JsonRpcMember> members = JsonRpcApi.Members;
        Assert.True(members["app.open"].IsAvailable);
        Assert.True(members["document.readBytes"].IsAvailable);
        Assert.True(members["app.documents"].IsAvailable);
        Assert.False(members["document.transformChunks"].IsAvailable);
        Assert.False(members["app.stop"].IsAvailable);

        // 生成した一覧 (docs/api/jsonrpc-methods.json) にも同じ名前・使えるかどうか・エラーコードがある。
        var json = (JsonObject)JsonNode.Parse(new ApiDocGenerator().JsonRpcMethods())!;
        var listed = ((JsonArray)json["methods"]!).Select(m => (string)m!["method"]!).ToHashSet();
        Assert.Equal(members.Keys.ToHashSet(), listed);
        Assert.Contains(((JsonArray)json["errors"]!), e => (string)e!["type"]! == "PermissionError" && (int)e["code"]! == -32001);
    }
}
