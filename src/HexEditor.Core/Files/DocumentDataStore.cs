using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

/// <summary>
/// ドキュメントに付随するデータ (00-overview.md 10 章) の保存先 <c>documents/</c>。キーはファイルの絶対パス (大文字・小文字を区別しない)
/// で、ファイル名はそのハッシュ。いまは前回の位置 (ENG-16 の仕様 6) だけを置く。ファイル本体には何も書かない。
/// </summary>
public sealed class DocumentDataStore(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>パスのデータのファイル。</summary>
    public string FileFor(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant()));
        return System.IO.Path.Combine(Folder, Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".json");
    }

    /// <summary>前回の位置。なければ (読めなければ) null。</summary>
    public DocumentPosition? GetPosition(string path)
    {
        try
        {
            DocumentPosition? position = JsonFile.Read<DocumentPosition>(FileFor(path));
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
            JsonFile.Write(FileFor(position.Path), position with { Path = System.IO.Path.GetFullPath(position.Path) });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
