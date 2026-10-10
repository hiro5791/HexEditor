using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.View;

/// <summary>
/// 表示プリセット 1 つ (VIEW-42 の仕様 6): 名前、自動適用の条件 (拡張子の一覧。<c>.nes</c> の形)、表示設定 (<see cref="ViewSettings.ToJson"/> の形。
/// ドキュメント固有の項目は含めない)。
/// </summary>
public sealed record ViewPreset(string Name, IReadOnlyList<string> Extensions, JsonObject View)
{
    /// <summary>この表示設定を <paramref name="baseSettings"/> に重ねたもの (知らない項目は無視する)。</summary>
    public ViewSettings ApplyTo(ViewSettings baseSettings) => ViewSettings.FromJson(View, baseSettings);

    /// <summary>ファイルのパスが自動適用の条件に合うか (拡張子を大文字・小文字を区別せずに比べる)。</summary>
    public bool Matches(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Length > 0 && Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>プリセットの JSON が不正 (VIEW-42 の「エラー」)。<see cref="Line"/> は 1 始まりの行 (分かれば)。</summary>
public sealed class ViewPresetFormatException(string message, int? line = null) : Exception(message)
{
    public int? Line { get; } = line;
}

/// <summary>
/// 表示プリセットの一覧の読み書き (VIEW-42 の仕様 6・7)。settings.json の <c>view.presets</c> に配列で持ち、最大 <see cref="MaxPresets"/> 個。
/// エクスポートの形式: <c>{"version":1,"format":"hexeditor-view-presets","presets":[{"name":…,"extensions":[".nes"],"view":{…}}]}</c>。
/// 知らない項目は無視して読む (新しいバージョンで作ったプリセットとの互換性のため)。
/// </summary>
public static class ViewPresets
{
    public const string SettingsKey = "view.presets";
    public const int MaxPresets = 100;
    public const string Kind = "hexeditor-view-presets";

    /// <summary>プリセットに含めない項目 (ドキュメント固有の項目は <see cref="ViewSettings.ToJson"/> が除く)。</summary>
    private static readonly string[] Excluded = ["baseAddress", "rowShift"];

    /// <summary>今の表示設定からプリセットを作る (ドキュメント固有の項目を除く)。<paramref name="extensions"/> は「.nes;.gba」の形。</summary>
    public static ViewPreset FromView(string name, string extensions, ViewSettings view)
    {
        JsonObject json = view.ToJson(includeDocumentSpecific: false);
        foreach (string key in Excluded)
        {
            json.Remove(key);
        }

        return new ViewPreset(name.Trim(), ParseExtensions(extensions), json);
    }

    /// <summary>「.nes; gba ,.GB」を「.nes」「.gba」「.GB」にする (点がなければ付ける。空は除く)。</summary>
    public static IReadOnlyList<string> ParseExtensions(string text) =>
        [.. text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).Where(e => e.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>設定の値 (配列) から読む。壊れた項目は飛ばす。</summary>
    public static IReadOnlyList<ViewPreset> FromNode(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var list = new List<ViewPreset>();
        foreach (JsonNode? item in array)
        {
            if (Parse(item) is { } preset)
            {
                list.Add(preset);
            }
        }

        return Normalize(list);
    }

    /// <summary>設定に書く値 (配列)。</summary>
    public static JsonArray ToNode(IReadOnlyList<ViewPreset> presets) => [.. presets.Select(p => (JsonNode?)ToJson(p))];

    /// <summary>同じ名前は後のものを残し、上限の数までにする。</summary>
    public static IReadOnlyList<ViewPreset> Normalize(IEnumerable<ViewPreset> presets)
    {
        var byName = new List<ViewPreset>();
        foreach (ViewPreset p in presets)
        {
            int existing = byName.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                byName[existing] = p;
            }
            else
            {
                byName.Add(p);
            }
        }

        return [.. byName.Take(MaxPresets)];
    }

    /// <summary>自動適用するプリセット (条件に合う最初のもの)。なければ null。</summary>
    public static ViewPreset? ForPath(IReadOnlyList<ViewPreset> presets, string path) => presets.FirstOrDefault(p => p.Matches(path));

    /// <summary>エクスポートする (VIEW-42 の仕様 7)。</summary>
    public static string Export(IReadOnlyList<ViewPreset> presets)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["format"] = Kind,
            ["presets"] = ToNode(presets),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// インポートする JSON を読む (エクスポートの形、またはプリセット 1 つ・プリセットの配列)。不正なら <see cref="ViewPresetFormatException"/>
    /// (行番号付き)。知らない項目は無視する。
    /// </summary>
    public static IReadOnlyList<ViewPreset> Import(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new ViewPresetFormatException(ex.Message, ex.LineNumber is { } line ? (int)line + 1 : null);
        }

        JsonNode? items = root switch
        {
            JsonObject o when o["presets"] is JsonArray a => a,
            JsonObject o when o["name"] is not null => new JsonArray(o.DeepClone()),
            JsonArray a => a,
            _ => null,
        };
        if (items is not JsonArray array)
        {
            throw new ViewPresetFormatException("No presets were found in the file.");
        }

        var list = new List<ViewPreset>();
        foreach (JsonNode? item in array)
        {
            list.Add(Parse(item) ?? throw new ViewPresetFormatException("A preset has no name."));
        }

        return list;
    }

    private static ViewPreset? Parse(JsonNode? item)
    {
        if (item is not JsonObject o || o["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        IReadOnlyList<string> extensions = o["extensions"] switch
        {
            JsonArray a => ParseExtensions(string.Join(";", a.Select(e => e is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty))),
            JsonValue v when v.TryGetValue(out string? s) => ParseExtensions(s),
            _ => [],
        };
        JsonObject view = o["view"] is JsonObject viewObject ? (JsonObject)viewObject.DeepClone() : [];

        // 表示設定に書かれず、プリセットの直下にある設定 (1 行のバイト数など) も表示設定として読む (簡単な形の JSON のため)。
        foreach ((string key, JsonNode? value) in o)
        {
            if (key is not ("name" or "extensions" or "view") && !view.ContainsKey(key))
            {
                view[key] = value?.DeepClone();
            }
        }

        return new ViewPreset(name.Trim(), extensions, view);
    }

    private static JsonObject ToJson(ViewPreset p) => new()
    {
        ["name"] = p.Name,
        ["extensions"] = new JsonArray([.. p.Extensions.Select(e => (JsonNode?)e)]),
        ["view"] = p.View.DeepClone(),
    };
}
