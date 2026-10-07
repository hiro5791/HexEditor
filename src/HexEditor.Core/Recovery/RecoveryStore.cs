using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Recovery;

/// <summary>起動時に見つかった復旧用データ 1 件。</summary>
public sealed record RecoveryEntry(string Folder, RecoveryRecord Record);

/// <summary>復旧した結果。<see cref="SourceChanged"/> なら元のファイルが記録と違う (読み取り専用で開く。ENG-27 の仕様 6)。</summary>
public sealed record RestoredDocument(Document Document, DocumentRecovery Recovery, RecoveryRecord Record, bool SourceChanged);

/// <summary>復旧用データの一覧・復旧・破棄 (ENG-27 の仕様 6、PKG-30 の仕様 2)。</summary>
public static class RecoveryStore
{
    /// <summary>
    /// <paramref name="root"/> の復旧用データを一覧にする。他のインスタンスが使用中のもの (ロックファイルが開かれている) は除く。
    /// state.json のない使われていないフォルダ (正常に閉じられなかった一時ファイルの残り) は消す。
    /// </summary>
    public static IReadOnlyList<RecoveryEntry> Scan(string root)
    {
        var entries = new List<RecoveryEntry>();
        if (!Directory.Exists(root))
        {
            return entries;
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) || IsInUse(folder))
            {
                continue;
            }

            string state = Path.Combine(folder, DocumentRecovery.StateFileName);
            RecoveryRecord? record = null;
            try
            {
                if (File.Exists(state))
                {
                    using FileStream stream = File.OpenRead(state);
                    record = JsonSerializer.Deserialize<RecoveryRecord>(stream, DocumentRecovery.Json);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                record = null;
            }

            if (record is { Format: RecoveryRecord.CurrentFormat })
            {
                entries.Add(new RecoveryEntry(folder, record));
            }
            else if (!File.Exists(state))
            {
                TryDeleteFolder(folder);
            }
        }

        return entries.OrderByDescending(e => e.Record.SavedAtUtc).ToList();
    }

    /// <summary>
    /// 復旧する。元のファイルを開き直し、記録したピースの一覧を復元したドキュメントを「変更あり」の状態で返す。
    /// 復旧用データは新しいドキュメントの復旧用データとして引き継ぐ (同じフォルダ)。失敗したら例外で、データは残す。
    /// </summary>
    public static RestoredDocument Restore(RecoveryEntry entry, DocumentOptions options)
    {
        RecoveryRecord record = entry.Record;
        bool sourceChanged = false;
        IByteSource source;
        if (record.Path is null)
        {
            source = MemoryByteSource.CreateEmpty(record.DisplayName);
        }
        else
        {
            var file = FileByteSource.Open(record.Path);
            source = file;
            sourceChanged = record.SourceStamp is null || file.Stamp != record.SourceStamp;
        }

        AddBuffer? addBuffer = null;
        DocumentRecovery? recovery = null;
        try
        {
            // ロックを先に取り、他のインスタンスが同時に同じデータを復旧しないようにする。
            recovery = new DocumentRecovery(Path.GetDirectoryName(entry.Folder)!, record.DocumentId);
            string spill = Path.Combine(entry.Folder, "add.bin");
            addBuffer = record.AddBufferLength > 0 || File.Exists(spill)
                ? AddBuffer.OpenExisting(spill, record.AddBufferLength, options.AddBufferMemoryLimit)
                : new AddBuffer(spill, options.AddBufferMemoryLimit);
            IEnumerable<Piece> pieces = record.Pieces.Select(p => p.ToPiece());
            if (sourceChanged && record.Pieces.Any(p => p.Kind == PieceKind.Original && p.Offset + p.Length > source.Length))
            {
                throw new InvalidDataException("元のファイルが短くなっているため、復旧できません。");
            }

            Document document = Document.Restore(record.DocumentId, source, addBuffer, pieces, options);
            return new RestoredDocument(document, recovery, record, sourceChanged);
        }
        catch
        {
            addBuffer?.DisposeKeepingFile();
            recovery?.Release();
            source.Dispose();
            throw;
        }
    }

    /// <summary>復旧用データを消す (「破棄」)。</summary>
    public static void Discard(RecoveryEntry entry) => TryDeleteFolder(entry.Folder);

    private static bool IsInUse(string folder)
    {
        string lockPath = Path.Combine(folder, DocumentRecovery.LockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 削除待ち (DeleteOnClose) のファイルは開けない。使用中として扱う。
            return true;
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
