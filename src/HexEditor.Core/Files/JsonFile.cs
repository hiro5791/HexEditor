using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.Core.Files;

/// <summary>
/// 設定フォルダの JSON ファイル (recent.json、session.json、state.json、documents/) の読み書き。書き込みは一時ファイルに書いてから
/// 置き換える (途中で落ちても前の内容が壊れない。UI-23 の仕様 5 と同じ方式)。
/// </summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>JSON の文字列にする。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>ファイルを読む。ファイルがなければ null。JSON として読めなければ <see cref="JsonException"/>。</summary>
    public static T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(text, Options) ?? throw new JsonException($"{Path.GetFileName(path)} が空です。");
    }

    /// <summary>一時ファイルに書いてから置き換える。</summary>
    public static void Write<T>(string path, T value) => WriteText(path, Serialize(value));

    /// <summary>文字列を一時ファイルに書いてから置き換える。</summary>
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>壊れたファイルを <c>&lt;名前&gt;.broken-&lt;日時&gt;</c> に名前を変えて残す (UI-31 の「エラー」)。残せなかったら null。</summary>
    public static string? KeepBroken(string path, DateTime now)
    {
        string broken = $"{path}.broken-{now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, broken, overwrite: true);
            return broken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
