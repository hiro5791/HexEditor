using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Documents;

/// <summary>付随データのファイルに記録した、元のファイルの状態。</summary>
public sealed record DocumentDataHeader(string Path, long? Length, DateTime? LastWriteTimeUtc)
{
    /// <summary>
    /// 記録したときから、ファイルのサイズ・更新日時が変わっていないか (00-overview 10 章。変わっていたら適用する前に確認する)。
    /// 記録がない項目は比べない。
    /// </summary>
    public bool Matches(FileStamp? stamp) =>
        stamp is null
        || (Length is null || Length == stamp.Length)
        && (LastWriteTimeUtc is null || stamp.LastWriteTimeUtc == DateTime.MinValue || LastWriteTimeUtc == stamp.LastWriteTimeUtc);
}

/// <summary>
/// ドキュメントに付随するデータ (00-overview 10 章): 設定フォルダの <c>documents/</c> に、ファイル本体とは別に保存する。
/// キーはファイルの絶対パス (大文字・小文字を区別しない) のハッシュ。データの種類 (<paramref name="kind"/>) ごとに
/// 1 つのファイル (<c>&lt;キー&gt;.&lt;種類&gt;.json</c>) にし、大きなデータ (ブックマーク 100 万件など) を他と分ける。
/// 書き込みは一時ファイルに書いてから置き換える。
/// </summary>
public sealed class DocumentDataStore(string folder)
{
    private static readonly object WriteLock = new();

    public string Folder { get; } = folder;

    /// <summary>ファイルの絶対パスからキーを作る。</summary>
    public static string Key(string documentPath)
    {
        string full = Path.GetFullPath(documentPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
    }

    public string PathFor(string documentPath, string kind) => Path.Combine(Folder, $"{Key(documentPath)}.{kind}.json");

    /// <summary>
    /// 付随データを書く。<paramref name="write"/> は本体のオブジェクトの中身 (<c>header</c> 以外のプロパティ) を書く。
    /// 失敗したら例外 (呼び出し側で記録する)。
    /// </summary>
    public void Write(string documentPath, string kind, FileStamp? stamp, Action<Utf8JsonWriter> write)
    {
        string target = PathFor(documentPath, kind);
        Directory.CreateDirectory(Folder);
        string temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("header");
                writer.WriteString("path", Path.GetFullPath(documentPath));
                if (stamp is not null)
                {
                    writer.WriteNumber("length", stamp.Length);
                    if (stamp.LastWriteTimeUtc != DateTime.MinValue)
                    {
                        writer.WriteString("lastWriteTimeUtc", stamp.LastWriteTimeUtc);
                    }
                }

                writer.WriteEndObject();
                write(writer);
                writer.WriteEndObject();
            }

            lock (WriteLock)
            {
                File.Move(temp, target, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>付随データを消す (データがなくなったとき)。</summary>
    public void Delete(string documentPath, string kind)
    {
        string target = PathFor(documentPath, kind);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    /// <summary>付随データを読む。なければ null。読めなければ <see cref="JsonException"/> / <see cref="IOException"/>。</summary>
    public (DocumentDataHeader Header, JsonDocument Body)? Read(string documentPath, string kind)
    {
        string target = PathFor(documentPath, kind);
        if (!File.Exists(target))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(target);
        JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        DocumentDataHeader header = new(documentPath, null, null);
        if (root.TryGetProperty("header", out JsonElement h))
        {
            header = new DocumentDataHeader(
                h.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? documentPath : documentPath,
                h.TryGetProperty("length", out JsonElement l) ? l.GetInt64() : null,
                h.TryGetProperty("lastWriteTimeUtc", out JsonElement w) ? w.GetDateTime().ToUniversalTime() : null);
        }

        return (header, doc);
    }

    /// <summary>小さな付随データ (JSON のオブジェクト 1 つ) を書く。</summary>
    public void WriteObject(string documentPath, string kind, FileStamp? stamp, JsonObject value) =>
        Write(documentPath, kind, stamp, writer =>
        {
            writer.WritePropertyName("data");
            value.WriteTo(writer);
        });

    /// <summary>小さな付随データを読む。なければ null。</summary>
    public (DocumentDataHeader Header, JsonObject Value)? ReadObject(string documentPath, string kind)
    {
        if (Read(documentPath, kind) is not { } read)
        {
            return null;
        }

        using JsonDocument doc = read.Body;
        JsonObject value = doc.RootElement.TryGetProperty("data", out JsonElement data) && JsonNode.Parse(data.GetRawText()) is JsonObject o ? o : [];
        return (read.Header, value);
    }
}
