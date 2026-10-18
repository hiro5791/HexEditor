using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Rpc;

/// <summary>JSON-RPC のエラーコード (AUTO-45 の「エラー」。AUTO-04 の 9 の例外の種類に対応させる)。</summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>その他のスクリプトのエラー。</summary>
    public const int ScriptError = -32000;
    public const int PermissionError = -32001;
    public const int ArgumentError = -32002;
    public const int RangeError = -32003;
    public const int ReadOnlyError = -32004;
    public const int IoError = -32005;
    public const int CanceledError = -32006;
    public const int LimitError = -32007;

    /// <summary>利用者が接続を許可しなかった (接続を切る直前に送る)。</summary>
    public const int ConnectionRefused = -32010;

    /// <summary>例外の種類とコードの一覧 (仕様書に載せる)。</summary>
    public static IReadOnlyList<(string Type, int Code)> ByType { get; } =
    [
        (nameof(Sdk.PermissionError), PermissionError),
        (nameof(Sdk.ArgumentError), ArgumentError),
        (nameof(Sdk.RangeError), RangeError),
        (nameof(Sdk.ReadOnlyError), ReadOnlyError),
        (nameof(Sdk.IoError), IoError),
        (nameof(Sdk.CanceledError), CanceledError),
        (nameof(Sdk.LimitError), LimitError),
    ];

    public static int Of(ScriptError error) => error switch
    {
        Sdk.PermissionError => PermissionError,
        Sdk.ArgumentError => ArgumentError,
        Sdk.RangeError => RangeError,
        Sdk.ReadOnlyError => ReadOnlyError,
        Sdk.IoError => IoError,
        Sdk.CanceledError => CanceledError,
        Sdk.LimitError => LimitError,
        _ => ScriptError,
    };
}

/// <summary>
/// 外部からの自動操作の 1 つの接続 (AUTO-45)。改行で区切った JSON-RPC 2.0 のメッセージ (UTF-8、1 行 1 メッセージ、64 MB まで。仕様 6) を
/// 読み、API を呼んで応答を書く。要求は届いた順に 1 つずつ実行し (スクリプトと同じく API は 1 つのスレッドから使う)、
/// <c>$/cancel</c> 通知 (<c>{ "id": … }</c>) で実行中の要求をキャンセルできる。一括処理の進捗は <c>$/progress</c> 通知で送る。
/// 購読したイベント (<c>app.on</c>) は <c>event</c> 通知で送る。
/// 書き込みの排他は、要求が途切れて <see cref="IdleRelease"/> たったら手放す (Undo の単位はそこで区切れ、利用者は再び編集できる。仕様 7)。
/// </summary>
public sealed class JsonRpcSession
{
    /// <summary>1 つの要求・応答の上限 (仕様 6)。</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly ScriptContext _ctx;
    private readonly Stream _stream;
    private readonly JsonRpcConvert _convert;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly BlockingCollection<JsonNode> _queue = new();
    private long _lastActivity;
    private int _pending;

    public JsonRpcSession(ScriptContext context, Stream stream)
    {
        _ctx = context;
        _stream = stream;
        _convert = new JsonRpcConvert(context.App);
    }

    /// <summary>要求が途切れてから書き込みの排他を手放すまでの時間。</summary>
    public TimeSpan IdleRelease { get; init; } = TimeSpan.FromSeconds(2);

    public ScriptContext Context => _ctx;

    /// <summary>処理した要求の数 (テスト用)。</summary>
    public int Handled { get; private set; }

    /// <summary>接続が切れるまで読み続ける。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ctx.Token);
        Task worker = Task.Factory.StartNew(() => WorkLoop(linked.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task idle = IdleLoopAsync(linked.Token);
        try
        {
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 接続が切れた。
        }
        finally
        {
            _queue.CompleteAdding();
            foreach (CancellationTokenSource cts in _running.Values)
            {
                TryCancel(cts);
            }

            await worker.ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await idle.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ───────────── 読み込み ─────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        bool tooLong = false;
        while (true)
        {
            int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                if (!tooLong)
                {
                    line.Write(buffer, start, i - start);
                    await OnMessageAsync(line.GetBuffer().AsMemory(0, (int)line.Length)).ConfigureAwait(false);
                }
                else
                {
                    await SendErrorAsync(null, JsonRpcErrorCodes.LimitError, "The message is larger than 64 MB.").ConfigureAwait(false);
                }

                line.SetLength(0);
                tooLong = false;
                start = i + 1;
            }

            if (!tooLong)
            {
                line.Write(buffer, start, n - start);
                if (line.Length > MaxMessageBytes)
                {
                    tooLong = true;
                    line.SetLength(0);
                }
            }
        }
    }

    private async Task OnMessageAsync(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span.TrimEnd((byte)'\r');
        if (span.IsEmpty || span.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
        {
            return;
        }

        JsonNode? message;
        try
        {
            message = JsonNode.Parse(span, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            await SendErrorAsync(null, JsonRpcErrorCodes.ParseError, "Parse error: " + ex.Message).ConfigureAwait(false);
            return;
        }

        IEnumerable<JsonNode?> items = message is JsonArray batch ? batch : [message];
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject o || o["jsonrpc"]?.GetValueKind() != JsonValueKind.String || (string?)o["jsonrpc"] != "2.0"
                || o["method"] is not JsonValue mv || !mv.TryGetValue(out string? method))
            {
                await SendErrorAsync(item is JsonObject bad ? bad["id"]?.DeepClone() : null, JsonRpcErrorCodes.InvalidRequest, "Invalid request.").ConfigureAwait(false);
                continue;
            }

            if (method == "$/cancel")
            {
                // 実行中・待機中の要求のキャンセル (通知。応答はキャンセルされた要求の応答で返る)。
                if (o["params"] is JsonObject p && p["id"] is { } id && _running.TryGetValue(IdKey(id), out CancellationTokenSource? cts))
                {
                    TryCancel(cts);
                }

                continue;
            }

            if (o["id"] is { } requestId)
            {
                _running[IdKey(requestId)] = new CancellationTokenSource();
            }

            Interlocked.Increment(ref _pending);
            _queue.Add(o);
        }
    }

    private static string IdKey(JsonNode id) => id.ToJsonString();

    // ───────────── 実行 ─────────────

    private void WorkLoop(CancellationToken ct)
    {
        foreach (JsonNode message in _queue.GetConsumingEnumerable())
        {
            var o = (JsonObject)message;
            JsonNode? id = o["id"]?.DeepClone();
            string method = (string)o["method"]!;
            CancellationTokenSource? cts = id is null ? null : _running.GetValueOrDefault(IdKey(id));
            try
            {
                JsonNode? result = Invoke(method, o["params"], id, cts?.Token ?? CancellationToken.None);
                if (id is not null)
                {
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
                }
            }
            catch (Exception ex)
            {
                (int code, string text, JsonObject? data) = ErrorOf(ex);
                if (id is not null)
                {
                    SendError(id, code, text, data);
                }
            }
            finally
            {
                if (id is not null && _running.TryRemove(IdKey(id), out CancellationTokenSource? done))
                {
                    done.Dispose();
                }

                Handled++;
                Interlocked.Decrement(ref _pending);
                Volatile.Write(ref _lastActivity, Environment.TickCount64);
            }
        }
    }

    private static (int Code, string Message, JsonObject? Data) ErrorOf(Exception ex)
    {
        ex = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        ex = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex;
        return ex switch
        {
            JsonRpcMethodException m => (m.Code, m.Message, null),
            JsonRpcParamsException p => (JsonRpcErrorCodes.InvalidParams, p.Message, null),
            Sdk.PermissionError pe => (JsonRpcErrorCodes.PermissionError, pe.Message, new JsonObject { ["type"] = nameof(Sdk.PermissionError), ["permission"] = pe.Permission }),
            ScriptError se => (JsonRpcErrorCodes.Of(se), se.Message, new JsonObject { ["type"] = se.GetType().Name }),
            OperationCanceledException => (JsonRpcErrorCodes.CanceledError, Msg.T("キャンセルされました。", "Canceled."), new JsonObject { ["type"] = nameof(Sdk.CanceledError) }),
            ScriptTerminatedException => (JsonRpcErrorCodes.MethodNotFound, "Not available over JSON-RPC.", null),
            _ => Mapped(ex),
        };
    }

    /// <summary>アプリの例外を API の例外の種類にそろえる (分類できないもの (不具合) は内部エラー)。</summary>
    private static (int Code, string Message, JsonObject? Data) Mapped(Exception ex)
    {
        ScriptError mapped = ScriptErrorMap.ToScriptError(ex);
        return mapped is Sdk.IoError && ex is not (IOException or UnauthorizedAccessException)
            ? (JsonRpcErrorCodes.InternalError, ex.Message, null)
            : (JsonRpcErrorCodes.Of(mapped), mapped.Message, new JsonObject { ["type"] = mapped.GetType().Name });
    }

    private sealed class JsonRpcMethodException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>1 つの要求を実行する (要求の実行のスレッド)。</summary>
    internal JsonNode? Invoke(string method, JsonNode? parameters, JsonNode? id, CancellationToken cancellationToken)
    {
        _ctx.ThrowIfStopped();
        if (!JsonRpcApi.Members.TryGetValue(method, out JsonRpcMember? member))
        {
            throw new JsonRpcMethodException(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + method);
        }

        if (!member.IsAvailable)
        {
            throw new JsonRpcMethodException(JsonRpcErrorCodes.MethodNotFound, $"{method} is not available over JSON-RPC: {member.UnavailableReason}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var args = new ArgumentReader(parameters);
        object target = ResolveTarget(member.Owner, args);
        if (member.Property is { } property)
        {
            if (args.TryTake("value", out JsonNode? newValue))
            {
                if (!property.CanWrite)
                {
                    throw new JsonRpcParamsException($"{method} cannot be set.");
                }

                property.SetValue(target, _convert.FromJson(newValue, property.PropertyType));
                return null;
            }

            return _convert.ToJson(property.GetValue(target));
        }

        MethodInfo m = member.Method!;
        ParameterInfo[] ps = m.GetParameters();
        object?[] values = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            ParameterInfo p = ps[i];
            if (member.Owner == typeof(IApp) && m.Name == nameof(IApp.On) && typeof(Delegate).IsAssignableFrom(p.ParameterType))
            {
                values[i] = EventHandlerFor(args.Peek(0) ?? args.Named("eventName"));
                continue;
            }

            if (args.Next(p.Name!, out JsonNode? node))
            {
                values[i] = _convert.FromJson(node, p.ParameterType);
            }
            else if (p.HasDefaultValue)
            {
                values[i] = p.DefaultValue;
            }
            else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null)
            {
                values[i] = null;
            }
            else
            {
                throw new JsonRpcParamsException($"The parameter \"{p.Name}\" is required.");
            }

            if (typeof(AsyncOptions).IsAssignableFrom(p.ParameterType))
            {
                values[i] = WithSignal((AsyncOptions?)values[i], p.ParameterType, id, cancellationToken);
            }
        }

        object? result;
        try
        {
            result = m.Invoke(target, values);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }

        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
            result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
        }

        return _convert.ToJson(result);
    }

    /// <summary>
    /// 呼び出しの対象: <c>app</c> と app の部品 (hash、ui など) はそのもの。ドキュメントとその部品 (selection、bookmarks、highlights) は
    /// 引数 <c>document</c> (位置引数なら先頭) の id (省略するとアクティブなドキュメント)。そのほかの API のオブジェクトは引数 <c>handle</c>。
    /// </summary>
    private object ResolveTarget(Type owner, ArgumentReader args)
    {
        IApp app = _ctx.App;
        if (owner == typeof(IDocument) || JsonRpcApi.DocumentPart(owner) is not null)
        {
            IDocument doc = args.TakeTarget("document") is { } d ? (IDocument)_convert.FromJson(d, typeof(IDocument))!
                : app.ActiveDocument ?? throw new Sdk.ArgumentError(Msg.T("ドキュメントが開いていません。", "No document is open."));
            return owner == typeof(IDocument) ? doc : JsonRpcApi.DocumentPart(owner)!.GetValue(doc)!;
        }

        if (JsonRpcApi.ServiceOf(app, owner) is { } service)
        {
            return service;
        }

        JsonNode handle = args.TakeTarget("handle") ?? throw new JsonRpcParamsException($"The parameter \"handle\" is required for {JsonRpcApi.ObjectName(owner)}.");
        return _convert.FromJson(handle, owner)!;
    }

    /// <summary>非同期の処理のキャンセル (<c>$/cancel</c>) と進捗 (<c>$/progress</c>) をつなぐ。</summary>
    private AsyncOptions WithSignal(AsyncOptions? options, Type type, JsonNode? id, CancellationToken cancellationToken)
    {
        options ??= (AsyncOptions)Activator.CreateInstance(type)!;
        CancellationToken? existing = options.Signal;
        CancellationToken token = existing is { CanBeCanceled: true } e ? CancellationTokenSource.CreateLinkedTokenSource(e, cancellationToken).Token : cancellationToken;
        typeof(AsyncOptions).GetProperty(nameof(AsyncOptions.Signal))!.SetValue(options, token);
        if (id is not null)
        {
            JsonNode requestId = id.DeepClone();
            long last = 0;
            Action<double, string?> progress = (fraction, text) =>
            {
                // 100 ms ごとにまとめる。
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref last) < 100 && fraction < 1)
                {
                    return;
                }

                Interlocked.Exchange(ref last, now);
                Notify("$/progress", new JsonObject { ["id"] = requestId.DeepClone(), ["value"] = fraction, ["message"] = text });
            };
            typeof(AsyncOptions).GetProperty(nameof(AsyncOptions.Progress))!.SetValue(options, progress);
        }

        return options;
    }

    /// <summary><c>app.on</c>: スクリプトの関数の代わりに、イベントを <c>event</c> 通知で送る。</summary>
    private Action<ScriptEventArgs> EventHandlerFor(JsonNode? eventName)
    {
        string name = eventName is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
        return e => Notify("event", new JsonObject
        {
            ["event"] = name,
            ["name"] = e.Name,
            ["document"] = e.Document is { } d ? _convert.Describe(d) : null,
        });
    }

    // ───────────── 書き込み ─────────────

    /// <summary>通知を送る (どのスレッドからでもよい)。</summary>
    public void Notify(string method, JsonNode? parameters) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private void SendError(JsonNode? id, int code, string message, JsonObject? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data;
        }

        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error });
    }

    private Task SendErrorAsync(JsonNode? id, int code, string message)
    {
        SendError(id, code, message);
        return Task.CompletedTask;
    }

    private void Send(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonRpcConvert.Serialize(message) + "\n");
        if (bytes.Length > MaxMessageBytes && message["result"] is not null)
        {
            // 応答が上限を超える (仕様 6: 大きなデータはファイルを介して扱う)。
            SendError(message["id"]?.DeepClone(), JsonRpcErrorCodes.LimitError, "The response is larger than 64 MB. Use document.exportRange.");
            return;
        }

        _writeLock.Wait();
        try
        {
            _stream.Write(bytes);
            _stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 接続が切れた (読み込みのほうで終わる)。
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>接続を拒否したことを送る (利用者が許可しなかった。AUTO-45 の仕様 4)。この後に接続を切る。</summary>
    public static void SendRefusal(Stream stream, string message)
    {
        var o = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = JsonRpcErrorCodes.ConnectionRefused, ["message"] = message },
        };
        try
        {
            stream.Write(Encoding.UTF8.GetBytes(JsonRpcConvert.Serialize(o) + "\n"));
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // ───────────── 書き込みの排他の解放 ─────────────

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, IdleRelease.TotalMilliseconds / 4)), ct).ConfigureAwait(false);
            long last = Volatile.Read(ref _lastActivity);
            if (last != 0 && Volatile.Read(ref _pending) == 0 && Environment.TickCount64 - last >= IdleRelease.TotalMilliseconds)
            {
                Volatile.Write(ref _lastActivity, 0);
                try
                {
                    await Task.Run(_ctx.ReleaseIdleWrites, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        }
    }

    // ───────────── 引数 ─────────────

    /// <summary>位置引数 (配列) と名前付きの引数 (オブジェクト) の読み取り。</summary>
    private sealed class ArgumentReader(JsonNode? parameters)
    {
        private readonly JsonArray? _array = parameters as JsonArray;
        private readonly JsonObject? _object = parameters as JsonObject;
        private int _index;

        public JsonNode? Peek(int offset) => _array is not null && _index + offset < _array.Count ? _array[_index + offset] : null;

        public JsonNode? Named(string name) => _object?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>対象 (document / handle)。名前付きならその名前、位置引数なら先頭。</summary>
        public JsonNode? TakeTarget(string name)
        {
            if (_object is not null)
            {
                return Named(name) ?? Named("id");
            }

            if (_array is not null && _index < _array.Count)
            {
                return _array[_index++];
            }

            return null;
        }

        public bool TryTake(string name, out JsonNode? value)
        {
            if (_object is not null && _object.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = Named(name);
                return true;
            }

            if (_array is not null && _index < _array.Count)
            {
                value = _array[_index++];
                return true;
            }

            value = null;
            return false;
        }

        public bool Next(string name, out JsonNode? value)
        {
            if (_object is not null)
            {
                bool present = _object.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
                value = present ? Named(name) : null;
                return present;
            }

            if (_array is not null && _index < _array.Count)
            {
                value = _array[_index++];
                return true;
            }

            value = null;
            return false;
        }
    }
}
