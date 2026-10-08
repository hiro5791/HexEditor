using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Settings;

/// <summary>
/// settings.json の JSON スキーマ (UI-23 の仕様 8)。設定項目の登録から作り、アプリに <c>settings.schema.json</c> として同梱する。
/// 外部のエディタの補完・検査用で、アプリは読み込みに使わない。知らないキーも許す (新しい版の設定を壊さない)。
/// </summary>
public static class SettingsSchema
{
    public const string FileName = "settings.schema.json";

    /// <summary><paramref name="englishNames"/> は項目の英語の名前 (説明に使う)。</summary>
    public static string Generate(SettingsCatalog catalog, Func<SettingDefinition, string> englishNames)
    {
        var properties = new JsonObject
        {
            ["$schema"] = new JsonObject { ["type"] = "string" },
            ["$schemaVersion"] = new JsonObject { ["type"] = "integer", ["const"] = SettingsStore.SchemaVersion },
        };
        foreach (SettingDefinition s in catalog.All.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var p = new JsonObject { ["description"] = englishNames(s) };
            switch (s.Kind)
            {
                case SettingKind.Bool:
                    p["type"] = "boolean";
                    break;
                case SettingKind.Int:
                    p["type"] = "integer";
                    break;
                case SettingKind.Number:
                    p["type"] = "number";
                    break;
                case SettingKind.Choice:
                    p["type"] = "string";
                    p["enum"] = new JsonArray([.. s.Options.Select(o => (JsonNode?)o)]);
                    break;
                case SettingKind.List:
                    p["type"] = "array";
                    p["items"] = new JsonObject { ["type"] = "string" };
                    break;
                default:
                    p["type"] = "string";
                    break;
            }

            if (s.Min is { } min)
            {
                p["minimum"] = min;
            }

            if (s.Max is { } max)
            {
                p["maximum"] = max;
            }

            if (s.Default is not null)
            {
                p["default"] = s.Default.DeepClone();
            }

            properties[s.Key] = p;
        }

        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "HexEditor settings.json",
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("$schemaVersion"),
            ["additionalProperties"] = true,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }
}
