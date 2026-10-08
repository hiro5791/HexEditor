using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Commands;

/// <summary>
/// keybindings.json の 1 行 (UI-18 の仕様 9)。<see cref="Remove"/> が true なら、そのキーの割り当てを外す
/// (ファイルでは <c>command</c> の先頭の <c>-</c>)。
/// </summary>
public sealed record KeyBindingEntry(string Command, KeyChord Chord, KeyScope Scope, bool Remove = false)
{
    public KeyBinding Binding => new(Chord, Scope);
}

/// <summary>keybindings.json の行の誤りの種類 (表示の文は App がリソースから作る)。</summary>
public enum KeyBindingsErrorKind
{
    /// <summary><c>command</c> がない。</summary>
    MissingCommand,

    /// <summary><c>key</c> の表記が正しくない (<see cref="KeyBindingsError.Value"/> に値)。</summary>
    InvalidKey,

    /// <summary><c>when</c> の値が正しくない (<see cref="KeyBindingsError.Value"/> に値)。</summary>
    InvalidScope,
}

/// <summary>keybindings.json の読み込みで飛ばした行 (行番号は 1 始まり。0 は不明)。</summary>
public sealed record KeyBindingsError(int Line, KeyBindingsErrorKind Kind, string? Value = null);

/// <summary>
/// キー割り当ての設定ファイル (keybindings.json。UI-18 の仕様 9、UI-19 の仕様 5、UI-21)。プリセットとの差分を持つ。
/// エクスポートのファイルとプリセットのファイル (<c>Presets/&lt;ID&gt;.json</c>) も同じ形式。
/// </summary>
public sealed class KeyBindingsDocument
{
    public const string FileName = "keybindings.json";
    public const string ExportFileName = "hexeditor-keybindings.json";
    public const int CurrentSchemaVersion = 1;

    /// <summary>インポートできるファイルの大きさの上限 (UI-21 の「巨大ファイル」)。</summary>
    public const long MaxImportBytes = 1024 * 1024;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Preset { get; init; } = KeyPresets.Default;

    public IReadOnlyList<KeyBindingEntry> Bindings { get; init; } = [];

    /// <summary>読めなかった行 (読める部分だけを使う。UI-18 の「エラー」)。</summary>
    public IReadOnlyList<KeyBindingsError> Errors { get; init; } = [];

    public bool IsTooNew => SchemaVersion > CurrentSchemaVersion;

    /// <summary>
    /// 読む。JSON として読めない場合は <see cref="JsonException"/> (行番号つき)。個々の行の誤り (キーの表記・有効範囲) は
    /// その行を飛ばして <see cref="Errors"/> に入れる。
    /// </summary>
    public static KeyBindingsDocument Parse(string json)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        JsonObject root = JsonNode.Parse(json, documentOptions: options) as JsonObject
            ?? throw new JsonException("The root of keybindings.json is not an object.", null, 0, 0);
        IReadOnlyList<int> lines = BindingLines(json);

        var entries = new List<KeyBindingEntry>();
        var errors = new List<KeyBindingsError>();
        if (root["bindings"] is JsonArray bindings)
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                int line = i < lines.Count ? lines[i] : 0;
                if (bindings[i] is not JsonObject item
                    || item["command"] is not JsonValue c || !c.TryGetValue(out string? command) || string.IsNullOrWhiteSpace(command))
                {
                    errors.Add(new KeyBindingsError(line, KeyBindingsErrorKind.MissingCommand));
                    continue;
                }

                string? key = item["key"] is JsonValue k && k.TryGetValue(out string? ks) ? ks : null;
                if (!KeyChord.TryParse(key, out KeyChord chord))
                {
                    errors.Add(new KeyBindingsError(line, KeyBindingsErrorKind.InvalidKey, key));
                    continue;
                }

                string? when = item["when"] is JsonValue w && w.TryGetValue(out string? ws) ? ws : null;
                if (!KeyScopes.TryParse(when, out KeyScope scope))
                {
                    errors.Add(new KeyBindingsError(line, KeyBindingsErrorKind.InvalidScope, when));
                    continue;
                }

                bool remove = command.StartsWith('-');
                entries.Add(new KeyBindingEntry(remove ? command[1..] : command, chord, scope, remove));
            }
        }

        int version = ReadVersion(root["$schemaVersion"]);
        string preset = root["preset"] is JsonValue p && p.TryGetValue(out string? ps) && !string.IsNullOrWhiteSpace(ps) ? ps : KeyPresets.Default;
        return new KeyBindingsDocument { SchemaVersion = version, Preset = preset, Bindings = entries, Errors = errors };
    }

    /// <summary>
    /// <c>$schemaVersion</c> を読む (設定ファイル・エクスポートのファイルでも使う)。整数でない値 (小数・数字の文字列) も読める範囲で
    /// 読み、読めなければ <paramref name="fallback"/> とみなす。
    /// </summary>
    public static int ReadVersion(JsonNode? node, int fallback = CurrentSchemaVersion)
    {
        if (node is not JsonValue v)
        {
            return fallback;
        }

        if (v.TryGetValue(out int n))
        {
            return n;
        }

        if (v.TryGetValue(out double d) && double.IsFinite(d))
        {
            // 小数は切り上げる (1.5 は 1 より新しい版とみなす)。
            return d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)Math.Ceiling(d);
        }

        return v.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed : fallback;
    }

    /// <summary>ファイルの形式で書く。<c>when</c> はグローバルなら省略する。</summary>
    public string ToJson()
    {
        var bindings = new JsonArray();
        foreach (KeyBindingEntry e in Bindings)
        {
            var item = new JsonObject
            {
                ["command"] = (e.Remove ? "-" : string.Empty) + e.Command,
                ["key"] = e.Chord.ToString(),
            };
            if (e.Scope != KeyScope.Global)
            {
                item["when"] = KeyScopes.Name(e.Scope);
            }

            bindings.Add(item);
        }

        var root = new JsonObject
        {
            ["$schemaVersion"] = SchemaVersion,
            ["preset"] = Preset,
            ["bindings"] = bindings,
        };

        // 利用者が編集するファイルなので、キーの表記の「+」などを \u のエスケープにしない (読みやすくする)。
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>bindings の各要素の行番号 (1 始まり)。誤りの表示に使う。</summary>
    private static List<int> BindingLines(string json)
    {
        var lines = new List<int>();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        bool inBindings = false;
        int bindingsDepth = -1;
        try
        {
            while (reader.Read())
            {
                if (!inBindings && reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("bindings"))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                    {
                        inBindings = true;
                        bindingsDepth = reader.CurrentDepth;
                    }

                    continue;
                }

                if (inBindings)
                {
                    if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == bindingsDepth)
                    {
                        break;
                    }

                    if (reader.CurrentDepth == bindingsDepth + 1 && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.String
                        or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null or JsonTokenType.StartArray)
                    {
                        lines.Add(LineAt(bytes, (int)reader.TokenStartIndex));
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return lines;
    }

    private static int LineAt(byte[] bytes, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
            }
        }

        return line;
    }
}
