using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Forensics.WriteBlock;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>SFTP の保存の方法 (ENG-41 の仕様 5)。</summary>
public enum SftpSaveMethod
{
    /// <summary>長さが変わらず、データがずれていない: 変更範囲だけを書き込む (ジャーナルは使わない)。</summary>
    ChangedRanges,

    /// <summary>それ以外: 同じフォルダの一時ファイルに全体を書き出し、名前の変更で置き換える。</summary>
    Replace,
}

/// <summary>保存の計画。</summary>
public sealed record SftpSavePlan(SftpSaveMethod Method, IReadOnlyList<(long Offset, long Length)> Ranges, long Length)
{
    /// <summary>書き込むバイト数。</summary>
    public long WriteBytes => Method == SftpSaveMethod.ChangedRanges ? Ranges.Sum(r => r.Length) : Length;
}

/// <summary>
/// SFTP のリモートのファイルへの保存 (ENG-41 の仕様 5)。変更範囲だけの書き込みは、旧内容を追加バッファに退避してから書く (保存前の Undo 履歴が
/// 旧内容を読めるように)。置き換えは同じフォルダの一時ファイルに全体を書き、サーバーが上書きの名前変更 (<c>posix-rename@openssh.com</c>) に
/// 対応していればそれで、なければ「退避 → 置き換え → 削除」の手順で置き換える。キャンセル・失敗では一時ファイルを消し、元のファイルは変えない。
/// </summary>
public static class SftpSaver
{
    private const int Chunk = 256 * 1024;

    /// <summary>保存の方法を決める。</summary>
    public static SftpSavePlan Plan(Document document, SftpByteSource source)
    {
        DocumentSnapshot snapshot = document.DataSnapshot;
        bool aligned = ReferenceEquals(snapshot.Storage.Source, source) && snapshot.Length == source.Length
            && snapshot.Tree.EnumerateAll().All(p => p.Piece.Kind != PieceKind.Original || p.Piece.Offset == p.DocumentOffset);
        return aligned
            ? new SftpSavePlan(SftpSaveMethod.ChangedRanges, MergeRanges(snapshot.EnumerateModifiedRanges()), snapshot.Length)
            : new SftpSavePlan(SftpSaveMethod.Replace, [], snapshot.Length);
    }

    /// <summary>
    /// 変更範囲だけを書き込む。成功したら UI のスレッドで <see cref="Document.CompleteTargetWrite"/> を呼ぶ (呼び出し側)。書けた範囲を返す。
    /// 途中で接続が切れた場合は、書けた範囲までを返して <see cref="SftpException"/> (リモートのファイルは一部だけ書き換わっている)。
    /// </summary>
    public static async Task<IReadOnlyList<SavedRange>> WriteChangedRangesAsync(Document document, SftpByteSource source, SftpSavePlan plan,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        WriteBlockGate.Current.EnsureRemoteWrite(source.Identity);
        DocumentSnapshot snapshot = document.DataSnapshot;
        AddBuffer addBuffer = document.AddBuffer;
        var written = new List<SavedRange>();
        operation?.SetTotal(plan.WriteBytes);
        long done = 0;
        byte[] buffer = new byte[Chunk];
        ISftpFile file = await source.Connection.Session.OpenAsync(source.RemotePath, SftpOpenMode.ReadWrite, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            foreach ((long offset, long length) in plan.Ranges)
            {
                // 旧内容を退避する (保存前の版が読む。CompleteTargetWrite の重ね合わせ)。
                long firstAt = -1;
                for (long pos = 0; pos < length; pos += Chunk)
                {
                    int n = (int)Math.Min(Chunk, length - pos);
                    ReadResult old = await source.ReadAsync(offset + pos, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    if (!old.IsComplete)
                    {
                        throw new SftpException(SftpFailure.Disconnected, "The original content could not be read before writing.");
                    }

                    long at = addBuffer.Append(buffer.AsSpan(0, n));
                    firstAt = firstAt < 0 ? at : firstAt;
                }

                for (long pos = 0; pos < length; pos += Chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(Chunk, length - pos);
                    ReadResult edited = snapshot.Read(offset + pos, buffer.AsSpan(0, n));
                    if (!edited.IsComplete)
                    {
                        throw new UnreadableDataException(edited.Unreadable);
                    }

                    await file.WriteAsync(offset + pos, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    done += n;
                    operation?.Report(done);
                }

                written.Add(new SavedRange(offset, length, firstAt));
            }
        }

        source.UpdateInfo(await source.Connection.Session.StatAsync(source.RemotePath, cancellationToken).ConfigureAwait(false));
        return written;
    }

    /// <summary>
    /// 同じフォルダの一時ファイルに全体を書き、名前の変更で置き換える。置き換えたファイルを開き直したデータソースを返す (呼び出し側は UI の
    /// スレッドで <see cref="Document.CompleteSave"/> を呼ぶ)。
    /// </summary>
    public static async Task<SftpByteSource> ReplaceAsync(Document document, SftpByteSource source, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        WriteBlockGate.Current.EnsureRemoteWrite(source.Identity);
        DocumentSnapshot snapshot = document.DataSnapshot;
        ISftpSession session = source.Connection.Session;
        string path = source.RemotePath;
        string folder = path.LastIndexOf('/') is var slash and >= 0 ? path[..(slash + 1)] : string.Empty;
        string name = path[(folder.Length)..];
        string temp = $"{folder}.{name}.hexeditor-{Guid.NewGuid().ToString("N")[..8]}.tmp";
        operation?.SetTotal(snapshot.Length);
        bool created = false;
        try
        {
            ISftpFile file = await session.OpenAsync(temp, SftpOpenMode.CreateNew, cancellationToken).ConfigureAwait(false);
            created = true;
            await using (file.ConfigureAwait(false))
            {
                byte[] buffer = new byte[Chunk];
                for (long pos = 0; pos < snapshot.Length; pos += Chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(Chunk, snapshot.Length - pos);
                    ReadResult edited = snapshot.Read(pos, buffer.AsSpan(0, n));
                    if (!edited.IsComplete)
                    {
                        throw new UnreadableDataException(edited.Unreadable);
                    }

                    await file.WriteAsync(pos, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    operation?.Report(pos + n);
                }
            }

            await ReplaceFileAsync(session, temp, path, cancellationToken).ConfigureAwait(false);
            created = false;
        }
        catch
        {
            if (created)
            {
                await TryDeleteAsync(session, temp).ConfigureAwait(false);
            }

            throw;
        }

        return await SftpByteSource.ReopenAsync(source.Connection, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一時ファイルで置き換える: 上書きの名前変更、なければ退避 → 置き換え → 削除 (途中で失敗したら退避を戻す)。</summary>
    private static async Task ReplaceFileAsync(ISftpSession session, string temp, string path, CancellationToken cancellationToken)
    {
        try
        {
            await session.RenameAsync(temp, path, overwrite: true, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (NotSupportedException)
        {
        }

        string backup = $"{path}.hexeditor-old-{Guid.NewGuid().ToString("N")[..8]}";
        await session.RenameAsync(path, backup, overwrite: false, cancellationToken).ConfigureAwait(false);
        try
        {
            await session.RenameAsync(temp, path, overwrite: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await session.RenameAsync(backup, path, overwrite: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await TryDeleteAsync(session, backup).ConfigureAwait(false);
    }

    private static async Task TryDeleteAsync(ISftpSession session, string path)
    {
        try
        {
            await session.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private static List<(long Offset, long Length)> MergeRanges(IEnumerable<(long Offset, long Length)> ranges)
    {
        var result = new List<(long Offset, long Length)>();
        foreach ((long offset, long length) in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
        {
            if (result.Count > 0 && offset <= result[^1].Offset + result[^1].Length)
            {
                result[^1] = (result[^1].Offset, Math.Max(result[^1].Offset + result[^1].Length, offset + length) - result[^1].Offset);
            }
            else
            {
                result.Add((offset, length));
            }
        }

        return result;
    }
}
