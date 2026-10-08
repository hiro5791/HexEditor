using System.Text.Json.Nodes;
using HexEditor.App.Commands;

namespace HexEditor.App.Services;

/// <summary>
/// 設定ではないアプリの状態 (前回の形式・前回の入力など。UI-23 の state.json) の文字列の値。書くのは
/// <see cref="CommandService.State"/> (state.json を書く唯一のもの)。
/// </summary>
public static class AppState
{
    public static string GetString(string key, string fallback) =>
        CommandService.State?.Get(key) is JsonValue v && v.TryGetValue(out string? s) ? s : fallback;

    public static void SetString(string key, string value) => CommandService.State?.Set(key, JsonValue.Create(value));
}
