using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Platform;

/// <summary>
/// <c>state.json</c> の区分 (最上位のキー 1 つ) の読み書き。アプリの中では state.json を書くのはアプリの状態の保存 (Core の
/// StateStore) だけにするため、アプリはそれを包んだものを渡す。<see cref="StateFile"/> はアプリの外 (テスト・インストールのフック) 用。
/// </summary>
public interface IStateSections
{
    JsonObject ReadSection(string section);

    bool WriteSection(string section, JsonObject value);
}

/// <summary>
/// データフォルダの <c>state.json</c> (09 の UI-23: 設定ではない状態) の区分 1 つを読み書きする。
/// 他の区分 (最近使ったコマンド、Explorer 連携の解除の記録など) はそのまま残す。書き込みは一時ファイルに書いてから置き換える。
/// 読めないファイルは空として扱う (壊れた状態で起動を止めない)。
/// </summary>
public sealed class StateFile(string folder) : IStateSections
{
    public const string FileName = "state.json";

    private static readonly object Lock = new();

    public string PathName => Path.Combine(folder, FileName);

    public JsonObject ReadSection(string section)
    {
        lock (Lock)
        {
            return ReadAll()[section] is JsonObject obj ? (JsonObject)obj.DeepClone() : [];
        }
    }

    /// <summary>区分を書き換える。書けなければ false (データフォルダに書き込めない場合など)。</summary>
    public bool WriteSection(string section, JsonObject value)
    {
        lock (Lock)
        {
            try
            {
                JsonObject all = ReadAll();
                all[section] = value.DeepClone();
                Directory.CreateDirectory(folder);
                string temp = PathName + ".tmp";
                File.WriteAllText(temp, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temp, PathName, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private JsonObject ReadAll()
    {
        try
        {
            return File.Exists(PathName) && JsonNode.Parse(File.ReadAllText(PathName)) is JsonObject obj ? obj : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
