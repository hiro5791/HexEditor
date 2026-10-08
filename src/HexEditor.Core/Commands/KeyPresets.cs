using System.Text.Json;

namespace HexEditor.Core.Commands;

/// <summary>プリセットが読めなかった理由の種類 (表示の文は App がリソースから作る)。</summary>
public enum KeyPresetErrorKind
{
    /// <summary>その ID のプリセットが同梱されていない。</summary>
    NotFound,

    /// <summary>同梱のファイルが JSON として読めない、または不正な行がある (<see cref="KeyPresetError.Line"/>)。</summary>
    Invalid,
}

/// <summary>プリセットが読めなかった理由 (UI-19 の「エラー」)。<see cref="Line"/> は 1 始まり (0 は不明)。</summary>
public sealed record KeyPresetError(string Preset, KeyPresetErrorKind Kind, int Line = 0);

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
    public static IReadOnlyList<KeyBindingEntry> Load(string id, out KeyPresetError? error)
    {
        error = null;
        if (id == Default)
        {
            return [];
        }

        using Stream? stream = typeof(KeyPresets).Assembly.GetManifestResourceStream($"HexEditor.Presets.{id}.json");
        if (stream is null)
        {
            error = new KeyPresetError(id, KeyPresetErrorKind.NotFound);
            return [];
        }

        try
        {
            using var reader = new StreamReader(stream);
            KeyBindingsDocument doc = KeyBindingsDocument.Parse(reader.ReadToEnd());
            if (doc.Errors.Count > 0)
            {
                error = new KeyPresetError(id, KeyPresetErrorKind.Invalid, doc.Errors[0].Line);
                return [];
            }

            return doc.Bindings;
        }
        catch (JsonException ex)
        {
            error = new KeyPresetError(id, KeyPresetErrorKind.Invalid, (int)(ex.LineNumber ?? 0) + 1);
            return [];
        }
    }

    public static bool Exists(string id) => Ids.Contains(id, StringComparer.Ordinal);
}
