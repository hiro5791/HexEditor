using System.Text.Json;

namespace HexEditor.Core.Commands;

/// <summary>
/// ショートカットのプリセット (UI-19)。各プリセットは <c>default</c> との差分で、アプリに同梱する JSON (<c>Presets/&lt;ID&gt;.json</c>。
/// Core のアセンブリに埋め込む) で定める。
/// </summary>
public static class KeyPresets
{
    public const string Default = "default";

    /// <summary>プリセットの ID (表示の順)。表示名はリソース <c>KeyPreset_&lt;ID&gt;</c>。</summary>
    public static readonly string[] Ids = [Default, "hxd", "010editor", "vscode"];

    /// <summary>
    /// プリセットの差分を読む。読めない (知らない ID・壊れたファイル) 場合は空の差分 (= <c>default</c>) を返し、
    /// <paramref name="error"/> に理由を入れる (UI-19 の「エラー」)。
    /// </summary>
    public static IReadOnlyList<KeyBindingEntry> Load(string id, out string? error)
    {
        error = null;
        if (id == Default)
        {
            return [];
        }

        using Stream? stream = typeof(KeyPresets).Assembly.GetManifestResourceStream($"HexEditor.Presets.{id}.json");
        if (stream is null)
        {
            error = $"プリセット {id} がありません。";
            return [];
        }

        try
        {
            using var reader = new StreamReader(stream);
            KeyBindingsDocument doc = KeyBindingsDocument.Parse(reader.ReadToEnd());
            if (doc.Errors.Count > 0)
            {
                error = $"プリセット {id} の {doc.Errors[0].Line} 行目: {doc.Errors[0].Message}";
                return [];
            }

            return doc.Bindings;
        }
        catch (JsonException ex)
        {
            error = $"プリセット {id}: {ex.Message}";
            return [];
        }
    }

    public static bool Exists(string id) => Ids.Contains(id, StringComparer.Ordinal);
}
