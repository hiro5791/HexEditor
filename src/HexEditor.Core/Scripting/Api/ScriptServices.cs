using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>settings</c> (AUTO-17 の仕様 1・2)。</summary>
internal sealed class ScriptSettings(ScriptContext ctx) : ISettings
{
    public object? Get(string key) => ScriptErrorMap.Guard(() =>
    {
        if (!ctx.Environment.Settings.Exists(key))
        {
            throw new Sdk.ArgumentError(Msg.T($"設定のキーが不明です: {key}", $"Unknown setting key: {key}"));
        }

        return ScriptJson.ToPlain(ctx.Dispatcher.Invoke(() => ctx.Environment.Settings.Get(key)));
    });

    public void Set(string key, object? value) => ScriptErrorMap.Guard(() =>
    {
        if (!ctx.Environment.Settings.Exists(key))
        {
            throw new Sdk.ArgumentError(Msg.T($"設定のキーが不明です: {key}", $"Unknown setting key: {key}"));
        }

        ctx.RequirePermission(ScriptPermissions.Settings, key);
        JsonNode? node = ScriptJson.ToNode(value);
        ctx.Dispatcher.Invoke(() => ctx.Environment.Settings.Set(key, node));
    });
}

/// <summary><c>script</c> (AUTO-17 の仕様 4)。</summary>
internal sealed class ScriptInfo(ScriptContext ctx) : IScript
{
    public string Name => ctx.Name;

    public string? Path => ctx.Path;

    public IReadOnlyDictionary<string, object?> Args => ctx.Args;

    public CancellationToken Signal => ctx.Token;

    public IScriptStorage Storage { get; } = new ScriptStorage(ctx);
}

/// <summary><c>script.storage</c> (AUTO-17 の仕様 3)。スクリプトはファイルのパスで区別し、上限は 1 MB。</summary>
internal sealed class ScriptStorage(ScriptContext ctx) : IScriptStorage
{
    private string Key => ctx.Path is not null ? System.IO.Path.GetFullPath(ctx.Path) : "unsaved:" + ctx.Name;

    public object? Get(string key) => ScriptErrorMap.Guard(() => ScriptJson.ToPlain(ctx.Host.Storage.Load(Key)[key]?.DeepClone()));

    public void Set(string key, object? value) => ScriptErrorMap.Guard(() =>
    {
        JsonObject all = ctx.Host.Storage.Load(Key);
        all[key] = ScriptJson.ToNode(value);
        ctx.Host.Storage.Save(Key, all);
    });

    public void Remove(string key) => ScriptErrorMap.Guard(() =>
    {
        JsonObject all = ctx.Host.Storage.Load(Key);
        if (all.Remove(key))
        {
            ctx.Host.Storage.Save(Key, all);
        }
    });
}

/// <summary>スクリプトの値と JSON の変換 (設定・保存領域)。</summary>
internal static class ScriptJson
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d when double.IsFinite(d) => Math.Floor(d) == d && Math.Abs(d) < 9e15 ? JsonValue.Create((long)d) : JsonValue.Create(d),
        double => throw new Sdk.ArgumentError(Msg.T("NaN と無限大は保存できません。", "NaN and infinity cannot be stored.")),
        BigInteger bi => bi >= long.MinValue && bi <= long.MaxValue ? JsonValue.Create((long)bi) : JsonValue.Create(bi.ToString(CultureInfo.InvariantCulture)),
        byte[] bytes => new JsonArray([.. bytes.Select(b => (JsonNode)JsonValue.Create(b))]),
        Sdk.ByteRange r => new JsonObject { ["start"] = r.Start, ["length"] = r.Length },
        IDictionary dict => DictToNode(dict),
        IEnumerable list => new JsonArray([.. list.Cast<object?>().Select(ToNode)]),
        _ => throw new Sdk.ArgumentError(Msg.T($"JSON にできない値です: {value.GetType().Name}", $"This value cannot be converted to JSON: {value.GetType().Name}")),
    };

    private static JsonObject DictToNode(IDictionary dict)
    {
        var o = new JsonObject();
        foreach (DictionaryEntry e in dict)
        {
            o[Convert.ToString(e.Key, CultureInfo.InvariantCulture)!] = ToNode(e.Value);
        }

        return o;
    }

    public static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(p => p.Key, p => ToPlain(p.Value), StringComparer.Ordinal),
        JsonArray a => a.Select(ToPlain).ToList(),
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out long l) => l,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => node.ToJsonString(),
    };
}

/// <summary>
/// <c>fs</c> (AUTO-18 の仕様 1)。許可 <c>file-read</c> / <c>file-write</c> が必要。利用者が選んだパスと、スクリプトのフォルダ以下の
/// 読み取りは許可なしで使える。
/// </summary>
internal sealed class ScriptFileSystem(ScriptContext ctx) : IFileSystem
{
    private static string Full(string path) => System.IO.Path.GetFullPath(path);

    private static Encoding Enc(EncodingArg? encoding) => encoding is EncodingArg e ? ScriptText.Resolve(e) : new UTF8Encoding(false);

    public byte[] ReadBytes(string path, long offset = 0, long length = 16 * 1024 * 1024) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileRead(full);
        if (length > ScriptDocument.SyncLimit)
        {
            throw new LimitError(Msg.T("fs.readBytes は 1 回 16 MB までです。大きなファイルは app.open で開いて一括処理 API を使ってください。", "fs.readBytes reads at most 16 MB at a time. Open large files with app.open and use the bulk APIs.")) { Limit = "size" };
        }

        if (offset < 0 || length < 0)
        {
            throw new Sdk.RangeError(Msg.T("offset と length は 0 以上にしてください。", "offset and length must be 0 or greater."));
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length)
        {
            throw new Sdk.RangeError(Msg.T($"オフセットがファイルの外です: {offset}", $"The offset is outside the file: {offset}"));
        }

        stream.Position = offset;
        byte[] buffer = new byte[(int)Math.Min(length, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    });

    public string ReadText(string path, EncodingArg? encoding = null) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileRead(full);
        if (new FileInfo(full).Length > ScriptDocument.SyncLimit)
        {
            throw new LimitError(Msg.T("fs.readText は 16 MB までのファイルです。", "fs.readText handles files up to 16 MB.")) { Limit = "size" };
        }

        return encoding is null ? File.ReadAllText(full) : File.ReadAllText(full, Enc(encoding));
    });

    public void WriteBytes(string path, BytePattern data) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileWrite(full);
        File.WriteAllBytes(full, ScriptDocument.PatternBytes(data, allowRandom: false));
    });

    public void WriteText(string path, string text, EncodingArg? encoding = null) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileWrite(full);
        File.WriteAllText(full, text, Enc(encoding));
    });

    public IReadOnlyList<string> List(string folder, string? pattern = null) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(folder);
        ctx.RequireFileRead(System.IO.Path.Combine(full, "."));
        return (IReadOnlyList<string>)[.. Directory.EnumerateFileSystemEntries(full, pattern ?? "*").Order(StringComparer.OrdinalIgnoreCase)];
    });

    public bool Exists(string path) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileRead(full);
        return File.Exists(full) || Directory.Exists(full);
    });

    public FileStat Stat(string path) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileRead(full);
        if (Directory.Exists(full))
        {
            return new FileStat(0, Directory.GetLastWriteTimeUtc(full).ToString("o", CultureInfo.InvariantCulture), true);
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new IoError(Msg.T($"ファイルが見つかりません: {full}", $"File not found: {full}"));
        }

        return new FileStat(info.Length, info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture), false);
    });

    public void Delete(string path) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileWrite(full);
        File.Delete(full);
    });

    public void CreateFolder(string path) => ScriptErrorMap.Guard(() =>
    {
        string full = Full(path);
        ctx.RequireFileWrite(full);
        Directory.CreateDirectory(full);
    });
}

/// <summary>
/// <c>net</c> (AUTO-18 の仕様 2)。許可 <c>network</c> を与えたスクリプトだけが通信する。アプリが自分から行う通信ではないため
/// <c>network.offline</c> の対象外。1 回の応答の上限は 100 MB。
/// </summary>
internal sealed class ScriptNet(ScriptContext ctx) : INet
{
    public const long MaxResponseBytes = 100L * 1024 * 1024;

    public Task<FetchResponse> FetchAsync(string url, FetchOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new Sdk.ArgumentError(Msg.T($"HTTP か HTTPS の URL を指定してください: {url}", $"Specify an HTTP or HTTPS URL: {url}"));
        }

        ctx.RequirePermission(ScriptPermissions.Network, uri.Host);
        string method = (options?.Method ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST"))
        {
            throw new Sdk.ArgumentError(Msg.T($"method は GET か POST です: {method}", $"method must be GET or POST: {method}"));
        }

        byte[]? body = options?.Body is BytePattern b ? ScriptDocument.PatternBytes(b, allowRandom: false)
            : options?.BodyText is string t ? Encoding.UTF8.GetBytes(t) : null;
        using CancellationTokenSource cts = ctx.LinkedToken(options?.Signal);
        (int status, IReadOnlyDictionary<string, string> headers, byte[] response) = await ctx.Environment.Http
            .SendAsync(method, uri, options?.Headers ?? new Dictionary<string, string>(), body, MaxResponseBytes, cts.Token).ConfigureAwait(false);
        object result = (options?.ResponseType ?? "text") switch
        {
            "bytes" => response,
            "text" => Encoding.UTF8.GetString(response),
            string other => throw new Sdk.ArgumentError(Msg.T($"responseType は text か bytes です: {other}", $"responseType must be text or bytes: {other}")),
        };
        return new FetchResponse(status, headers, result);
    });
}

/// <summary><c>process</c> (AUTO-18 の仕様 3)。シェルを経由せず、引数は配列のまま渡す。</summary>
internal sealed class ScriptProcessRunner(ScriptContext ctx) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> args, ProcessOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        ctx.RequirePermission(ScriptPermissions.Process, path);
        double timeout = options?.Timeout ?? 60;
        if (timeout <= 0)
        {
            throw new Sdk.ArgumentError(Msg.T("timeout は 0 より大きくしてください。", "timeout must be greater than 0."));
        }

        using CancellationTokenSource cts = ctx.LinkedToken(options?.Signal);
        using (ctx.Clock.Pause())
        {
            return await ctx.Environment.Processes.RunAsync(path, args, options?.WorkingDirectory, options?.Input, TimeSpan.FromSeconds(timeout), cts.Token)
                .ConfigureAwait(false);
        }
    });
}

/// <summary><c>app.commands</c> (AUTO-06 の仕様 1〜3)。</summary>
internal sealed class ScriptCommands(ScriptContext ctx) : ICommands
{
    public Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?>? args = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        IScriptCommandService commands = ctx.Environment.Commands;
        if (!commands.Exists(id))
        {
            throw new Sdk.ArgumentError(Msg.T($"コマンドが見つかりません: {id} (app.commands.list() で一覧できます)", $"Command not found: {id} (see app.commands.list() for the list)"));
        }

        using (ctx.Clock.Pause())
        {
            return await ctx.Dispatcher.InvokeAsync(() => commands.ExecuteAsync(id, args ?? new Dictionary<string, object?>(), ctx.Environment.ActiveDocument,
                ctx.Headless)).ConfigureAwait(false);
        }
    });

    public IReadOnlyList<CommandInfo> List() => ctx.Dispatcher.Invoke(ctx.Environment.Commands.List);
}
