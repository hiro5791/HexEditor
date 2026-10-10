using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Hashing;

/// <summary>利用者が名前を付けて保存したアルゴリズムのセット (ANA-18 の仕様 3)。</summary>
public sealed record UserHashSet(string Name, IReadOnlyList<HashAlgorithmChoice> Algorithms);

/// <summary>
/// 選んだアルゴリズム (パラメータ付き) とセットの設定ファイルへの保存形式。アルゴリズムは
/// <c>{"id": "xxh64", "seed": 4660, "complement": "twos"}</c> の配列 (既定のパラメータは省く)。
/// </summary>
public static class HashSelection
{
    public static string Serialize(IEnumerable<HashAlgorithmChoice> choices) => ToArray(choices).ToJsonString();

    public static IReadOnlyList<HashAlgorithmChoice> Deserialize(string? json)
    {
        try
        {
            return json is { Length: > 0 } && JsonNode.Parse(json) is JsonArray array ? FromArray(array) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeSets(IEnumerable<UserHashSet> sets) =>
        new JsonArray([.. sets.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["algorithms"] = ToArray(s.Algorithms) })]).ToJsonString();

    public static IReadOnlyList<UserHashSet> DeserializeSets(string? json)
    {
        try
        {
            if (json is not { Length: > 0 } || JsonNode.Parse(json) is not JsonArray array)
            {
                return [];
            }

            return [.. array.OfType<JsonObject>()
                .Where(o => o["name"] is JsonValue)
                .Select(o => new UserHashSet(o["name"]!.GetValue<string>(), o["algorithms"] is JsonArray a ? FromArray(a) : []))];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private static JsonArray ToArray(IEnumerable<HashAlgorithmChoice> choices) =>
        new([.. choices.Select(c =>
        {
            var o = new JsonObject { ["id"] = c.Algorithm.Id };
            if (c.Parameters.Seed != 0)
            {
                o["seed"] = c.Parameters.Seed;
            }

            if (c.Parameters.Complement != HashComplement.None)
            {
                o["complement"] = c.Parameters.Complement.ToString().ToLowerInvariant();
            }

            if (c.Parameters.BigEndian is bool be)
            {
                o["bigEndian"] = be;
            }

            if (c.Parameters.Signed)
            {
                o["signed"] = true;
            }

            if (c.Parameters.KeyHex is { Length: > 0 } key)
            {
                o["key"] = key;
            }

            if (c.Parameters.OutputBits != 0)
            {
                o["outputBits"] = c.Parameters.OutputBits;
            }

            if (c.Parameters.Ed2k != Ed2kMode.Blue)
            {
                o["ed2k"] = c.Parameters.Ed2k.ToString().ToLowerInvariant();
            }

            return (JsonNode?)o;
        })]);

    private static List<HashAlgorithmChoice> FromArray(JsonArray array)
    {
        var result = new List<HashAlgorithmChoice>();
        foreach (JsonObject o in array.OfType<JsonObject>())
        {
            if (o["id"] is not JsonValue idValue || !idValue.TryGetValue(out string? id) || HashCatalog.Find(id) is not { } algorithm)
            {
                continue;
            }

            ulong seed = o["seed"] is JsonValue s && s.TryGetValue(out ulong v) ? v : 0;
            HashComplement complement = o["complement"] is JsonValue c && c.TryGetValue(out string? cs)
                && Enum.TryParse(cs, ignoreCase: true, out HashComplement parsed) ? parsed : HashComplement.None;
            bool? bigEndian = o["bigEndian"] is JsonValue b && b.TryGetValue(out bool bv) ? bv : null;
            bool signed = o["signed"] is JsonValue sg && sg.TryGetValue(out bool sv) && sv;
            string? key = o["key"] is JsonValue k && k.TryGetValue(out string? ks) && IsHex(ks) ? ks.ToUpperInvariant() : null;
            int outputBits = o["outputBits"] is JsonValue ob && ob.TryGetValue(out int obv) ? obv : 0;
            Ed2kMode ed2k = o["ed2k"] is JsonValue e && e.TryGetValue(out string? es)
                && Enum.TryParse(es, ignoreCase: true, out Ed2kMode em) ? em : Ed2kMode.Blue;
            result.Add(new HashAlgorithmChoice(algorithm, new HashParameters
            {
                Seed = seed,
                Complement = complement,
                BigEndian = bigEndian,
                Signed = signed,
                KeyHex = key,
                OutputBits = outputBits,
                Ed2k = ed2k,
            }));
        }

        return result;
    }

    private static bool IsHex(string? text) =>
        text is { Length: > 0 } && text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit);

    /// <summary>シードの入力 (10 進、または <c>0x</c> 付きの 16 進)。</summary>
    public static bool TryParseSeed(string text, out ulong seed)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out seed)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
    }
}
