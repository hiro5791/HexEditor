using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Files;

namespace HexEditor.App.Services;

/// <summary>
/// ショートカットのプリセットの選択 (UI-19 の仕様 5: <c>keybindings.json</c> の <c>preset</c>)。スタートページの「はじめに」(UI-38) が
/// 使う最小限の読み書き。キー割り当ての他の内容は読まずにそのまま残す (キー割り当ての本体は UI-18 の担当が扱う)。
/// </summary>
public static class ShortcutPreset
{
    public const string FileName = "keybindings.json";
    public const string Default = "default";

    public static string Get(string folder)
    {
        try
        {
            string path = Path.Combine(folder, FileName);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                && root["preset"] is JsonValue v && v.TryGetValue(out string? preset)
                ? preset
                : Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Default;
        }
    }

    /// <summary>プリセットを選ぶ。書けなければ理由を返す。</summary>
    public static string? Set(string folder, string preset)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            JsonObject root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : [];
            if (preset == Default && !root.ContainsKey("preset"))
            {
                return null;
            }

            root["preset"] = preset;
            JsonFile.WriteText(path, root.ToJsonString(JsonFile.Options));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }
}
