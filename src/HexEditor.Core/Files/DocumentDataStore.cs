using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Files;

/// <summary>
/// 前回閉じたときの位置 (ENG-16 の仕様 5): カーソル・選択範囲・スクロール位置と、そのときのファイルのサイズ・更新日時。
/// </summary>
public sealed record DocumentPosition
{
    public required string Path { get; init; }

    public long Cursor { get; init; }

    public long SelectionStart { get; init; }

    public long SelectionLength { get; init; }

    /// <summary>表示の先頭の行。</summary>
    public long TopRow { get; init; }

    /// <summary>記録したときのファイルの長さ。</summary>
    public long Length { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>
    /// 長さ <paramref name="length"/> のファイルに当てはめる。前回の位置が長さを超える場合は末尾に置き、選択範囲は捨てる
    /// (ENG-16 の仕様 5)。
    /// </summary>
    public DocumentPosition ClampTo(long length)
    {
        bool selectionFits = SelectionLength > 0 && SelectionStart >= 0 && SelectionStart + SelectionLength <= length;
        return this with
        {
            Cursor = Math.Clamp(Cursor, 0, Math.Max(0, length)),
            SelectionStart = selectionFits ? SelectionStart : 0,
            SelectionLength = selectionFits ? SelectionLength : 0,
        };
    }
}

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
/// ドキュメントに付随するデータ (00-overview.md 10 章) の保存先 <c>documents/</c>。ファイル本体には何も書かない。
/// キーはファイルの絶対パス (大文字・小文字を区別しない) のハッシュ。データの種類 (前回の位置 <see cref="PositionKind"/>、
/// ブックマーク、インスペクタの設定など) ごとに 1 つのファイル (<c>&lt;キー&gt;.&lt;種類&gt;.json</c>) にし、大きなデータ
/// (ブックマーク 100 万件など) を他と分ける。書き込みは一時ファイルに書いてから置き換える。
/// </summary>
public sealed class DocumentDataStore(string folder)
{
    private static readonly object WriteLock = new();

    public string Folder { get; } = folder;

    /// <summary>前回の位置 (ENG-16) の種類。</summary>
    public const string PositionKind = "position";

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

    /// <summary>前回の位置。なければ (読めなければ) null。</summary>
    public DocumentPosition? GetPosition(string path)
    {
        try
        {
            DocumentPosition? position = JsonFile.Read<DocumentPosition>(PathFor(path, PositionKind));
            return position is not null && string.Equals(position.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                ? position
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>位置を記録する。書けなければ理由を返す (利用者には出さない)。</summary>
    public string? SetPosition(DocumentPosition position)
    {
        try
        {
            JsonFile.Write(PathFor(position.Path, PositionKind), position with { Path = System.IO.Path.GetFullPath(position.Path) });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
