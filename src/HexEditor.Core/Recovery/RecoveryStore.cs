using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Recovery;

/// <summary>起動時に見つかった復旧用データ 1 件。</summary>
public sealed record RecoveryEntry(string Folder, RecoveryRecord Record);

/// <summary>復旧した結果。<see cref="SourceChanged"/> なら元のファイルが記録と違う (読み取り専用で開く。ENG-27 の仕様 6)。</summary>
public sealed record RestoredDocument(Document Document, DocumentRecovery Recovery, RecoveryRecord Record, bool SourceChanged)
{
    /// <summary>デコードし直したドキュメント (ENG-38) の、元の形式で保存するための設定。それ以外は null。</summary>
    public Formats.EncodedFileSettings? Encoded { get; init; }

    /// <summary>デコードした内容の最小のアドレス (元の形式で保存するときに使う)。</summary>
    public long EncodedBaseAddress { get; init; }
}

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
    public static RestoredDocument Restore(RecoveryEntry entry, DocumentOptions options) => Restore(entry, options, null);

    /// <summary>
    /// 復旧する。ディスク・ボリュームの記録 (<see cref="RecoveryRecord.DevicePath"/>) は、呼び出し側が開き直したデバイスのデータソースを
    /// <paramref name="device"/> に渡す (管理者権限・シリアル番号の確認は呼び出し側。ENG-27 の仕様 6)。範囲外になった変更は除く。
    /// </summary>
    public static RestoredDocument Restore(RecoveryEntry entry, DocumentOptions options, IByteSource? device)
    {
        RecoveryRecord record = entry.Record;
        bool sourceChanged = false;
        IByteSource source;
        Formats.EncodedFileSettings? encodedSettings = null;
        long encodedBase = 0;
        if (record.EncodedPath is { } encodedPath && record.EncodedFormat is { } encodedFormat)
        {
            // デコードして開いたドキュメント: 元のファイルをデコードし直し、その内容に変更を戻す (デコードの結果の一時ファイルは
            // 異常終了で消えているため)。元のファイルが変わっていたら読み取り専用で開く。
            using Formats.ImportResult decoded = Formats.Importer.DecodeFile(encodedPath,
                Formats.EncodedFile.OpenOptions(encodedFormat, (byte)(record.EncodedGapFill ?? Formats.EncodedFile.DefaultGapFill)), options.TempDirectory);
            encodedSettings = decoded.Settings ?? new Formats.EncodedFileSettings { Format = encodedFormat };
            encodedBase = decoded.BaseAddress;
            Formats.SparseImage image = decoded.TakeImage();
            image.Resizable = encodedFormat == Formats.FormatIds.Base64;
            source = image;
            sourceChanged = record.EncodedStamp is null || FileStamp.FromPath(encodedPath) != record.EncodedStamp;
        }
        else if (record.DevicePath is not null)
        {
            source = device ?? throw new InvalidDataException("The device of this recovery data is not open.");
            // デバイスは更新日時を持たない。記録した元データの範囲より短くなっていたら「変わった」とし、範囲外の部分を除く。
            long recordedEnd = record.Pieces.Where(p => p.Kind == PieceKind.Original).Select(p => p.Offset + p.Length).DefaultIfEmpty(0).Max();
            sourceChanged = source.Length < recordedEnd;
        }
        else if (record.Path is null)
        {
            source = MemoryByteSource.CreateEmpty(record.DisplayName);
        }
        else
        {
            // 範囲を開いたドキュメントは同じ範囲を開き直す (元データのピースの位置は範囲の中の位置)。
            FileByteSource file = record.RangeStart is { } rangeStart
                ? FileByteSource.OpenRange(record.Path, rangeStart, record.RangeLength ?? 0, record.RangeResizable, allowEmpty: true)
                : FileByteSource.Open(record.Path);
            source = file;
            sourceChanged = record.SourceStamp is null || file.Stamp != record.SourceStamp;
        }

        AddBuffer? addBuffer = null;
        DocumentRecovery? recovery = null;
        var externals = new List<IByteSource>();
        try
        {
            // ロックを先に取り、他のインスタンスが同時に同じデータを復旧しないようにする。
            recovery = new DocumentRecovery(Path.GetDirectoryName(entry.Folder)!, record.DocumentId);
            string spill = Path.Combine(entry.Folder, "add.bin");
            addBuffer = record.AddBufferLength > 0 || File.Exists(spill)
                ? AddBuffer.OpenExisting(spill, record.AddBufferLength, options.AddBufferMemoryLimit)
                : new AddBuffer(spill, options.AddBufferMemoryLimit);
            IEnumerable<Piece> pieces = record.Pieces.Select(p => p.ToPiece());
            if (sourceChanged)
            {
                // 元のファイルが記録より短くなっていたら、範囲外になった元データのピースを新しい長さで切り詰める (なくなった部分は除く)。
                // 警告付きの読み取り専用で開くため (ENG-27 の仕様 6)、復旧できる部分だけを復旧する。
                pieces = ClampToSource(pieces, source.Length);
            }

            foreach (RecoveryExternal external in record.Externals)
            {
                if (external.FileName.IndexOfAny(['/', '\\']) >= 0)
                {
                    throw new InvalidDataException("復旧用データの外部参照のファイル名が正しくありません。");
                }

                externals.Add(FileByteSource.Open(Path.Combine(entry.Folder, external.FileName)));
            }

            Document document = Document.Restore(record.DocumentId, source, addBuffer, pieces, options, externals);
            return new RestoredDocument(document, recovery, record, sourceChanged) { Encoded = encodedSettings, EncodedBaseAddress = encodedBase };
        }
        catch
        {
            addBuffer?.DisposeKeepingFile();
            foreach (IByteSource external in externals)
            {
                external.Dispose();
            }

            recovery?.Release();
            source.Dispose();
            throw;
        }
    }

    /// <summary>元データのピースを <paramref name="sourceLength"/> の範囲に収める。範囲外の部分は除く。他の種類のピースはそのまま。</summary>
    internal static IEnumerable<Piece> ClampToSource(IEnumerable<Piece> pieces, long sourceLength)
    {
        foreach (Piece piece in pieces)
        {
            if (piece.Kind != PieceKind.Original || piece.Offset + piece.Length <= sourceLength)
            {
                yield return piece;
            }
            else if (piece.Offset < sourceLength)
            {
                yield return Piece.Original(piece.Offset, sourceLength - piece.Offset);
            }
        }
    }

    /// <summary>古い復旧用データの保存期間 (PKG-13 の仕様 5)。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// <paramref name="maxAge"/> より前に保存された復旧用データを消す (PKG-13 の仕様 5。起動時に呼ぶ)。
    /// 他のインスタンスが使用中のもの (ロックファイルが開かれている) は消さない。消した件数を返す。
    /// </summary>
    public static int DeleteExpired(string root, TimeSpan maxAge, DateTime nowUtc)
    {
        int deleted = 0;
        foreach (RecoveryEntry entry in Scan(root))
        {
            if (nowUtc - entry.Record.SavedAtUtc > maxAge && !IsInUse(entry.Folder))
            {
                TryDeleteFolder(entry.Folder);
                if (!Directory.Exists(entry.Folder))
                {
                    deleted++;
                }
            }
        }

        return deleted;
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
