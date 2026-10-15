using System.Text.Json;
using HexEditor.Sdk.Plugins;

namespace Plugin.Generic;

/// <summary>
/// テスト用のプラグイン。フォルダの <c>test.json</c> の内容で振る舞いを決める:
/// <code>
/// { "initDelayMs": 200,
///   "commands": [ { "id": "dup1.cmd", "title": "dup1: cmd", "output": "from dup1", "throw": false } ],
///   "inspectorType": { "id": "throw.type", "name": "throw-type", "throw": true, "delayMs": 0 },
///   "inspectorTypes": [ { "id": "x.loop", "name": "Loop", "size": 4, "hang": true },
///                       { "id": "x.throw", "name": "Throw", "size": 4, "throw": true, "message": "test failure" },
///                       { "id": "x.echo", "name": "Echo", "size": 4, "echo": true } ] }
/// </code>
/// 型の値は既定で先頭の 1 バイトの Hex、<c>echo</c> ならすべてのバイトを空白区切りの Hex にする。<c>hang</c> は戻らない (無限ループの代わり)。
/// </summary>
public sealed class GenericPlugin : IPlugin
{
    public void Initialize(IPluginContext context)
    {
        string path = Path.Combine(context.Folder, "test.json");
        using JsonDocument doc = JsonDocument.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("initDelayMs", out JsonElement delay))
        {
            Thread.Sleep(delay.GetInt32());
        }

        if (root.TryGetProperty("initThrow", out JsonElement initThrow) && initThrow.GetBoolean())
        {
            throw new InvalidOperationException("init failed on purpose");
        }

        if (root.TryGetProperty("commands", out JsonElement commands))
        {
            foreach (JsonElement c in commands.EnumerateArray())
            {
                context.Register(new Command(c.GetProperty("id").GetString()!, c.GetProperty("title").GetString()!,
                    c.TryGetProperty("output", out JsonElement o) ? o.GetString()! : "",
                    c.TryGetProperty("throw", out JsonElement t) && t.GetBoolean()));
            }
        }

        if (root.TryGetProperty("inspectorType", out JsonElement type))
        {
            context.Register(TypeOf(type));
        }

        if (root.TryGetProperty("inspectorTypes", out JsonElement types))
        {
            foreach (JsonElement t in types.EnumerateArray())
            {
                context.Register(TypeOf(t));
            }
        }

        context.Log.Info("initialized " + context.Id);
    }

    private sealed class Command(string id, string title, string output, bool fail) : ICommand
    {
        public string Id => id;

        public string DisplayName => title;

        public void Execute(ICommandContext context)
        {
            if (fail)
            {
                throw new InvalidOperationException("command failed on purpose");
            }

            context.Log.Info(output);
        }
    }

    private static InspectorType TypeOf(JsonElement type) => new(
        type.GetProperty("id").GetString()!,
        type.GetProperty("name").GetString()!,
        type.TryGetProperty("throw", out JsonElement t) && t.GetBoolean(),
        type.TryGetProperty("delayMs", out JsonElement d) ? d.GetInt32() : 0,
        type.TryGetProperty("size", out JsonElement s) ? s.GetInt32() : 1,
        type.TryGetProperty("hang", out JsonElement h) && h.GetBoolean(),
        type.TryGetProperty("echo", out JsonElement e) && e.GetBoolean(),
        type.TryGetProperty("message", out JsonElement m) ? m.GetString()! : "decode failed on purpose");

    private sealed class InspectorType(string id, string name, bool fail, int delayMs, int size, bool hang, bool echo, string message) : IInspectorType
    {
        public string Id => id;

        public string DisplayName => name;

        public int Size => size;

        public string Decode(ReadOnlySpan<byte> data, bool bigEndian)
        {
            if (hang)
            {
                // 戻らない (無限ループする型の代わり。CPU を使い続けない)
                Thread.Sleep(Timeout.Infinite);
            }

            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }

            if (fail)
            {
                throw new InvalidOperationException(message);
            }

            return echo
                ? string.Join(' ', data[..Math.Min(size, data.Length)].ToArray().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))
                : data[0].ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
