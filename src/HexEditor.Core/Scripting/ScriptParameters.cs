using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// パラメータの入力 (AUTO-26 の仕様 3)。宣言があれば実行前に入力ダイアログを出し、前回の入力値をスクリプトごとに記憶して次回の既定値にする。
/// ヘッドレス実行は <c>--arg</c> の値を使う (宣言していない名前も <c>script.args</c> に入る。AUTO-41 の仕様 3)。
/// </summary>
internal sealed class ScriptParameters(ScriptHost host, IScriptEnvironment env)
{
    public async Task<IReadOnlyDictionary<string, object?>?> CollectAsync(ScriptRunRequest request, ScriptRun run, ScriptMetadata meta)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string k, object? v) in request.PresetArgs)
        {
            values[k] = v;
        }

        IExpressionContext context = await env.Dispatcher.InvokeAsync(() =>
            env.ActiveDocument is { } d ? (IExpressionContext)new EditorExpressionContext(d.Editor) : EmptyExpressionContext.Instance).ConfigureAwait(false);

        if (request.Headless)
        {
            foreach ((string name, string text) in request.CommandLineArgs)
            {
                ScriptParameter? p = meta.Parameters.FirstOrDefault(x => x.Name == name);
                values[name] = p is null ? text : await env.Dispatcher.InvokeAsync(() => Convert(p, text, context)).ConfigureAwait(false);
            }

            foreach (ScriptParameter p in meta.Parameters.Where(p => !values.ContainsKey(p.Name)))
            {
                values[p.Name] = DefaultValue(p, context);
            }

            return values;
        }

        if (meta.Parameters.Count == 0)
        {
            return values;
        }

        Dictionary<string, string> remembered = host.Storage.LoadParameters(request.Path ?? run.Name);
        var fields = meta.Parameters
            .Where(p => !values.ContainsKey(p.Name))
            .Select(p => new ScriptFormField(p.Name, p.Label, p.Type, remembered.TryGetValue(p.Name, out string? last) ? last : TextOf(p.Default), p.Choices))
            .ToList();
        if (fields.Count == 0)
        {
            return values;
        }

        if (request.SkipParameterDialog)
        {
            foreach (ScriptFormField f in fields)
            {
                ScriptParameter p = meta.Parameters.First(x => x.Name == f.Name);
                values[f.Name] = f.Default is string s ? await env.Dispatcher.InvokeAsync(() => Convert(p, s, context)).ConfigureAwait(false) : DefaultValue(p, context);
            }

            return values;
        }

        IReadOnlyDictionary<string, string>? input = await env.Dispatcher.InvokeAsync(() => env.Ui.FormAsync(run.Name, null, fields, entered =>
        {
            foreach (ScriptParameter p in meta.Parameters)
            {
                if (entered.TryGetValue(p.Name, out string? text))
                {
                    try
                    {
                        _ = Convert(p, text, context);
                    }
                    catch (ScriptError ex)
                    {
                        return (p.Name, ex.Message);
                    }
                }
            }

            return null;
        })).ConfigureAwait(false);
        if (input is null)
        {
            return null;
        }

        foreach (ScriptParameter p in meta.Parameters.Where(p => input.ContainsKey(p.Name)))
        {
            values[p.Name] = await env.Dispatcher.InvokeAsync(() => Convert(p, input[p.Name], context)).ConfigureAwait(false);
        }

        host.Storage.SaveParameters(request.Path ?? run.Name, input);
        return values;
    }

    private static string? TextOf(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object? DefaultValue(ScriptParameter p, IExpressionContext context) =>
        p.Default is string s && p.Type is not ("text" or "choice") ? TryConvert(p, s, context) : p.Default;

    private static object? TryConvert(ScriptParameter p, string text, IExpressionContext context)
    {
        try
        {
            return Convert(p, text, context);
        }
        catch (ScriptError)
        {
            return text;
        }
    }

    /// <summary>入力の文字列を型の値にする (<c>ui.prompt</c> と同じ解釈。AUTO-16)。</summary>
    public static object? Convert(ScriptParameter p, string text, IExpressionContext context) =>
        ConvertValue(p.Type, text, context, p.Choices);

    public static object? ConvertValue(string type, string text, IExpressionContext context, IReadOnlyList<string>? choices = null) => type switch
    {
        "number" or "offset" => (double)ScriptValues.EvaluateNumber(text, context),
        "range" => ScriptValues.EvaluateRange(text, context),
        "hex" => ScriptValues.ParseHex(text),
        "checkbox" => text.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" or "" => false,
            _ => throw new ArgumentError(Msg.T($"true か false を指定してください: {text}", $"Specify true or false: {text}")),
        },
        "choice" when choices is { Count: > 0 } && !choices.Contains(text) => throw new ArgumentError(Msg.T($"選択肢にない値です: {text}", $"Not one of the choices: {text}")),
        _ => text,
    };
}

/// <summary>
/// スクリプトごとの保存領域 (<c>script.storage</c>。AUTO-17 の仕様 3)。スクリプトはファイルのパスで区別し、上限は 1 スクリプトあたり 1 MB。
/// 保存先は <c>&lt;設定フォルダ&gt;/scripts/storage/</c>。パラメータの前回の値 (AUTO-26 の仕様 3) も同じ場所に別のファイルで持つ。
/// </summary>
public sealed class ScriptStorageStore(string folder)
{
    public const int LimitBytes = 1024 * 1024;

    private readonly Lock _lock = new();

    public string Folder { get; } = folder;

    private string FileFor(string scriptKey, string suffix) =>
        Path.Combine(Folder, TrustStore.HashOf(scriptKey.ToUpperInvariant())[..32] + suffix);

    public JsonObject Load(string scriptKey)
    {
        lock (_lock)
        {
            string path = FileFor(scriptKey, ".json");
            try
            {
                return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o ? o : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>保存する。1 MB を超えると <see cref="LimitError"/> (保存しない)。</summary>
    public void Save(string scriptKey, JsonObject values)
    {
        string text = values.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(text) > LimitBytes)
        {
            throw new LimitError(Msg.T("script.storage の上限 1 MB を超えます。", "This exceeds the script.storage limit of 1 MB.")) { Limit = "storage" };
        }

        lock (_lock)
        {
            Directory.CreateDirectory(Folder);
            string path = FileFor(scriptKey, ".json");
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    public Dictionary<string, string> LoadParameters(string scriptKey)
    {
        lock (_lock)
        {
            string path = FileFor(scriptKey, ".params.json");
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void SaveParameters(string scriptKey, IReadOnlyDictionary<string, string> values)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FileFor(scriptKey, ".params.json"), JsonSerializer.Serialize(values));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 前回の値の記憶に失敗しても実行は続ける。
            }
        }
    }
}
