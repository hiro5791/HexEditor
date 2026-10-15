using System.Text.Json;
using HexEditor.Sdk.Plugins;

namespace Plugin.Generic;

/// <summary>
/// テスト用のプラグイン。フォルダの <c>test.json</c> の内容で振る舞いを決める:
/// <code>
/// { "initDelayMs": 200,
///   "commands": [ { "id": "dup1.cmd", "title": "dup1: cmd", "output": "from dup1", "throw": false } ],
///   "inspectorType": { "id": "throw.type", "name": "throw-type", "throw": true, "delayMs": 0 } }
/// </code>
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
            context.Register(new InspectorType(type.GetProperty("id").GetString()!, type.GetProperty("name").GetString()!,
                type.TryGetProperty("throw", out JsonElement t) && t.GetBoolean(),
                type.TryGetProperty("delayMs", out JsonElement d) ? d.GetInt32() : 0));
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

    private sealed class InspectorType(string id, string name, bool fail, int delayMs) : IInspectorType
    {
        public string Id => id;

        public string DisplayName => name;

        public int Size => 1;

        public string Decode(ReadOnlySpan<byte> data, bool bigEndian)
        {
            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }

            if (fail)
            {
                throw new InvalidOperationException("decode failed on purpose");
            }

            return data[0].ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
