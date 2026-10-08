using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Settings;

namespace HexEditor.Core.View;

/// <summary>
/// 表示設定の保存 (VIEW-42 の仕様 2〜5)。全体の既定値は settings.json の <c>view.defaults</c>、ドキュメントごとの設定は設定フォルダの
/// <c>documents/</c> (00-overview 10 章の付随データ) に、既定値と違う項目だけを書く。
/// </summary>
public sealed class ViewSettingsStore(SettingsStore settings)
{
    /// <summary>全体の既定値の設定キー。</summary>
    public const string DefaultsKey = "view.defaults";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string DocumentsFolder => Path.Combine(settings.Folder, "documents");

    /// <summary>全体の既定値 (組み込みの既定値に「既定として保存」した項目を重ねたもの)。</summary>
    public ViewSettings Defaults => ViewSettings.FromJson(settings.GetNode(DefaultsKey) as JsonObject);

    /// <summary>「既定として保存」(VIEW-42 の仕様 4): ドキュメント固有の項目を除いて全体の既定値にする。</summary>
    public void SaveDefaults(ViewSettings view) =>
        settings.SetNode(DefaultsKey, Diff(view.ToJson(includeDocumentSpecific: false), ViewSettings.Default.ToJson(includeDocumentSpecific: false)));

    /// <summary>ドキュメントの表示設定 (全体の既定値にドキュメントごとの設定を重ねたもの) と基準点。保存していなければ既定値。</summary>
    public (ViewSettings View, long? ReferencePoint) Load(string documentPath)
    {
        ViewSettings defaults = Defaults;
        string file = PathFor(documentPath);
        if (!File.Exists(file))
        {
            return (defaults, null);
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root)
            {
                return (defaults, null);
            }

            long? reference = root["referencePoint"] is JsonValue v && v.TryGetValue(out long p) ? p : null;
            return (ViewSettings.FromJson(root["view"] as JsonObject, defaults), reference);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (defaults, null);
        }
    }

    /// <summary>ドキュメントごとの設定を保存する (VIEW-42 の仕様 3)。既定値と同じで基準点もなければ消す。</summary>
    public void Save(string documentPath, ViewSettings view, long? referencePoint)
    {
        JsonObject diff = Diff(view.ToJson(), Defaults.ToJson());
        string file = PathFor(documentPath);
        try
        {
            if (diff.Count == 0 && referencePoint is null)
            {
                File.Delete(file);
                return;
            }

            var root = new JsonObject
            {
                ["path"] = Path.GetFullPath(documentPath),
                ["view"] = diff,
            };
            if (referencePoint is { } p)
            {
                root["referencePoint"] = p;
            }

            Directory.CreateDirectory(DocumentsFolder);
            string temp = file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
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
            File.Delete(PathFor(documentPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>ファイルの絶対パス (大文字・小文字を区別しない) から付随データのファイル名を作る。</summary>
    public string PathFor(string documentPath)
    {
        string key = Path.GetFullPath(documentPath).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(DocumentsFolder, hash + ".view.json");
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
