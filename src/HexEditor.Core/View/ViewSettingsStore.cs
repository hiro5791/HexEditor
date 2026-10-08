using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Files;
using HexEditor.Core.Settings;

namespace HexEditor.Core.View;

/// <summary>
/// データソースの種類ごとの表示設定の既定値を示すデータソース (VIEW-42 の仕様 2 の 3。ディスク・ボリュームでは区切り線「セクタ」など)。
/// 値は <see cref="ViewSettings.ToJson"/> と同じ形の、変える項目だけの JSON。ファイルのデータソースは示さない。
/// </summary>
public interface IViewDefaultsSource
{
    JsonObject? ViewDefaults { get; }
}

/// <summary>
/// 表示設定の保存 (VIEW-42 の仕様 2〜5)。全体の既定値は settings.json の <c>view.defaults</c>、ドキュメントごとの設定は
/// ドキュメントに付随するデータ (00-overview 10 章。<see cref="DocumentDataStore"/> の種類 <c>view</c>) に、既定値と違う項目だけを書く。
/// </summary>
public sealed class ViewSettingsStore(SettingsStore settings, DocumentDataStore documents)
{
    /// <summary>全体の既定値の設定キー。</summary>
    public const string DefaultsKey = "view.defaults";

    /// <summary>付随データの種類。</summary>
    public const string Kind = "view";

    /// <summary>全体の既定値 (組み込みの既定値に「既定として保存」した項目を重ねたもの)。</summary>
    public ViewSettings Defaults => ViewSettings.FromJson(settings.GetNode(DefaultsKey) as JsonObject);

    /// <summary>
    /// データソースの種類ごとの既定値を重ねた既定値 (VIEW-42 の仕様 2 の 1〜3: 組み込み → 全体の既定値 → データソースの種類)。
    /// </summary>
    public ViewSettings DefaultsFor(JsonObject? sourceDefaults) =>
        sourceDefaults is null || sourceDefaults.Count == 0 ? Defaults : ViewSettings.FromJson(sourceDefaults, Defaults);

    /// <summary>「既定として保存」(VIEW-42 の仕様 4): ドキュメント固有の項目を除いて全体の既定値にする。</summary>
    public void SaveDefaults(ViewSettings view) =>
        settings.SetNode(DefaultsKey, Diff(view.ToJson(includeDocumentSpecific: false), ViewSettings.Default.ToJson(includeDocumentSpecific: false)));

    /// <summary>ドキュメントの表示設定 (全体の既定値にドキュメントごとの設定を重ねたもの) と基準点。保存していなければ既定値。</summary>
    public (ViewSettings View, long? ReferencePoint) Load(string documentPath, JsonObject? sourceDefaults = null)
    {
        ViewSettings defaults = DefaultsFor(sourceDefaults);
        try
        {
            if (documents.ReadObject(documentPath, Kind) is not { } read)
            {
                return (defaults, null);
            }

            JsonObject root = read.Value;
            long? reference = root["referencePoint"] is JsonValue v && v.TryGetValue(out long p) ? p : null;
            return (ViewSettings.FromJson(root["view"] as JsonObject, defaults), reference);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return (defaults, null);
        }
    }

    /// <summary>ドキュメントごとの設定を保存する (VIEW-42 の仕様 3)。既定値と同じで基準点もなければ消す。</summary>
    public void Save(string documentPath, ViewSettings view, long? referencePoint, JsonObject? sourceDefaults = null)
    {
        JsonObject diff = Diff(view.ToJson(), DefaultsFor(sourceDefaults).ToJson());
        try
        {
            if (diff.Count == 0 && referencePoint is null)
            {
                documents.Delete(documentPath, Kind);
                return;
            }

            var root = new JsonObject { ["view"] = diff };
            if (referencePoint is { } p)
            {
                root["referencePoint"] = p;
            }

            documents.WriteObject(documentPath, Kind, null, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 付随データを書けなくても編集は続けられる。
        }
    }

    /// <summary>「既定に戻す」(VIEW-42 の仕様 5): ドキュメントごとの設定を消す。</summary>
    public void Remove(string documentPath)
    {
        try
        {
            documents.Delete(documentPath, Kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><paramref name="value"/> のうち <paramref name="baseline"/> と違う項目。</summary>
    public static JsonObject Diff(JsonObject value, JsonObject baseline)
    {
        var diff = new JsonObject();
        foreach ((string key, JsonNode? node) in value)
        {
            if (node?.ToJsonString() != baseline[key]?.ToJsonString())
            {
                diff[key] = node?.DeepClone();
            }
        }

        return diff;
    }
}
