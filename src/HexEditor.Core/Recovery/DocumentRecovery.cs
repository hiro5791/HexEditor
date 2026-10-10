using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Recovery;

/// <summary>復旧用データの 1 ピース (ENG-27 の仕様 2)。元データのピースは位置と長さだけ、生成ピースは規則だけを記録する。</summary>
public sealed record RecoveryPiece(PieceKind Kind, long Length, long Offset, long Phase, int PatternLength, ulong Seed)
{
    public static RecoveryPiece From(Piece p) => new(p.Kind, p.Length, p.Offset, p.Phase, p.PatternLength, p.Seed);

    public Piece ToPiece() => Kind switch
    {
        PieceKind.Original => Piece.Original(Offset, Length),
        PieceKind.Added => Piece.Added(Offset, Length),
        PieceKind.Pattern => Piece.Pattern(Offset, PatternLength, Length, Phase),
        PieceKind.Random => Piece.Random(Seed, Offset, Length),
        PieceKind.External => Piece.External((int)Seed, Offset, Length),
        _ => throw new InvalidDataException($"不明なピースの種類です: {Kind}"),
    };
}

/// <summary>
/// 外部参照のピース (別のドキュメントからの貼り付け。EDIT-24) が指すデータを書き出したファイル。参照元のドキュメントは
/// 異常終了の後には残っていないため、内容を復旧用フォルダにコピーしておく。
/// </summary>
public sealed record RecoveryExternal(string FileName, long Length);

/// <summary>復旧用データの内容 (<c>recovery/&lt;ドキュメント ID&gt;/state.json</c>)。</summary>
public sealed record RecoveryRecord
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public Guid DocumentId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>元のファイルのパス。無題のドキュメントは null。</summary>
    public string? Path { get; init; }

    /// <summary>開いたとき (または最後に保存したとき) の元のファイルの状態。</summary>
    public FileStamp? SourceStamp { get; init; }

    public long AddBufferLength { get; init; }

    public IReadOnlyList<RecoveryPiece> Pieces { get; init; } = [];

    /// <summary>外部参照のピースの <see cref="RecoveryPiece.Seed"/> が指す、書き出したデータの一覧。</summary>
    public IReadOnlyList<RecoveryExternal> Externals { get; init; } = [];

    public long Length { get; init; }

    public long Cursor { get; init; }

    public long SelectionStart { get; init; }

    public long SelectionLength { get; init; }

    public DateTime SavedAtUtc { get; init; }

    /// <summary>変更の量: 元データ以外から来たバイト数 (入力・貼り付け・生成)。</summary>
    public long ChangedBytes { get; init; }

    /// <summary>範囲を開いたドキュメント (ENG-13) の範囲の開始位置。ファイル全体なら null (ENG-27 の仕様 2 の「範囲」)。</summary>
    public long? RangeStart { get; init; }

    /// <summary>範囲の長さ (開いたときの元データの長さ)。</summary>
    public long? RangeLength { get; init; }

    /// <summary>範囲の長さを変えられるか。</summary>
    public bool RangeResizable { get; init; }

    /// <summary>ディスク・ボリュームのドキュメント (ENG-29) のデバイスのパス。ファイルなら null (ENG-27 の仕様 6: 変更範囲マップを復旧する)。</summary>
    public string? DevicePath { get; init; }

    /// <summary>デバイスのシリアル番号 (一致しなければ復旧しない)。</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>範囲を指定して開いたデバイスの開始位置。</summary>
    public long DeviceRangeStart { get; init; }

    /// <summary>デコードして開いたドキュメント (ENG-38) の元のファイル。デコードし直して変更を戻す。ファイルなら null。</summary>
    public string? EncodedPath { get; init; }

    /// <summary>デコードした形式 (<c>ihex</c> など)。</summary>
    public string? EncodedFormat { get; init; }

    /// <summary>デコードしたときの元のファイルの値 (変わっていたら読み取り専用で開く)。</summary>
    public FileStamp? EncodedStamp { get; init; }

    /// <summary>デコードしたときの隙間の塗りつぶしの値 (ENG-38 の仕様 3)。null は既定の FF。</summary>
    public int? EncodedGapFill { get; init; }

    /// <summary>
    /// インポートしたドキュメント (TOOL-05〜07) の付随データ (実行開始アドレス・S0 の文字列など。エクスポートの既定値)。それ以外は null。
    /// </summary>
    public Formats.EncodedFileSettings? ImportedSettings { get; init; }
}

/// <summary>
/// 開いているドキュメント 1 つの復旧用データ (ENG-27、PKG-30)。フォルダは追加バッファの一時ファイル (ENG-04) と同じ
/// <c>recovery/&lt;ドキュメント ID&gt;/</c>。使用中はロックファイルを排他で開いておき、他のインスタンスの対象から外す (仕様 9)。
/// </summary>
public sealed class DocumentRecovery : IDisposable
{
    public const string StateFileName = "state.json";
    public const string LockFileName = "lock";

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _writeLock = new();
    private readonly Dictionary<IByteSource, RecoveryExternal> _externals = new(ReferenceEqualityComparer.Instance);
    private SafeFileHandle? _lock;

    /// <param name="root">復旧用データのフォルダ。ドキュメントの <see cref="DocumentOptions.TempDirectory"/> と同じにする。</param>
    public DocumentRecovery(string root, Guid documentId)
    {
        Folder = Path.Combine(root, documentId.ToString("N"));
        Directory.CreateDirectory(Folder);

        // プロセスが落ちると OS がハンドルを閉じ、ファイルも消える。残っていれば使用中ではない。
        _lock = File.OpenHandle(
            Path.Combine(Folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
    }

    public string Folder { get; }

    public string StatePath => Path.Combine(Folder, StateFileName);

    /// <summary>
    /// UI スレッドで、現在の内容を書き出す準備をする。現在の内容が今の元データで表せない場合 (保存前の版に Undo した直後) は
    /// null (前回の復旧用データを残す)。
    /// </summary>
    public static RecoveryCapture? Capture(Document document, long cursor, long selectionStart, long selectionLength,
        (string Path, string Format, FileStamp? Stamp)? encoded = null, byte gapFill = Formats.EncodedFile.DefaultGapFill)
    {
        if (!document.CurrentUsesLatestSource)
        {
            return null;
        }

        DocumentSnapshot snapshot = document.Current;
        string? path = (document.Source as FileByteSource)?.Path;
        FileStamp? stamp = (document.Source as FileByteSource)?.Stamp;
        (long, long, bool)? range = document.Source is FileByteSource { IsRange: true } r ? (r.RangeStart, r.Length, r.RangeResizable) : null;
        (string, string?, long)? device = document.Source is Devices.DeviceByteSource d ? (d.Path, d.Info.SerialNumber, d.Info.RangeStart) : null;
        return new RecoveryCapture(document, snapshot, document.Id, document.Source.DisplayName, path, stamp, document.AddBuffer.Length,
            cursor, selectionStart, selectionLength, range, device, encoded) { EncodedGapFill = gapFill };
    }

    /// <summary>
    /// 書き出す (どのスレッドからでもよい)。追加バッファの未書き出しの分を一時ファイルに追記してから、新しい
    /// state.json を書いて入れ替える。途中で落ちても前回のデータは壊れない (仕様 3)。
    /// </summary>
    public void Write(RecoveryCapture capture)
    {
        lock (_writeLock)
        {
            capture.Document.AddBuffer.Persist();
            var pieces = new List<RecoveryPiece>();
            var externals = new List<RecoveryExternal>();
            var externalIndex = new Dictionary<int, int>();
            long changed = 0;
            foreach ((long _, Piece piece) in capture.Snapshot.Tree.EnumerateAll())
            {
                RecoveryPiece recorded = RecoveryPiece.From(piece);
                if (piece.Kind == PieceKind.External)
                {
                    if (!externalIndex.TryGetValue(piece.ExternalIndex, out int index))
                    {
                        externals.Add(PersistExternal(capture.Snapshot.ExternalSource(piece.ExternalIndex)));
                        index = externals.Count - 1;
                        externalIndex[piece.ExternalIndex] = index;
                    }

                    recorded = recorded with { Seed = (ulong)index };
                }

                pieces.Add(recorded);
                if (piece.Kind != PieceKind.Original)
                {
                    changed += piece.Length;
                }
            }

            var record = new RecoveryRecord
            {
                DocumentId = capture.DocumentId,
                DisplayName = capture.DisplayName,
                Path = capture.Path,
                SourceStamp = capture.Stamp,
                AddBufferLength = capture.AddBufferLength,
                Pieces = pieces,
                Externals = externals,
                Length = capture.Snapshot.Length,
                Cursor = capture.Cursor,
                SelectionStart = capture.SelectionStart,
                SelectionLength = capture.SelectionLength,
                SavedAtUtc = DateTime.UtcNow,
                ChangedBytes = changed,
                RangeStart = capture.Range?.Start,
                RangeLength = capture.Range?.Length,
                RangeResizable = capture.Range?.Resizable ?? false,
                DevicePath = capture.Device?.Path,
                DeviceSerial = capture.Device?.Serial,
                DeviceRangeStart = capture.Device?.RangeStart ?? 0,
                EncodedPath = capture.Encoded?.Path,
                EncodedFormat = capture.Encoded?.Format,
                EncodedStamp = capture.Encoded?.Stamp,
                EncodedGapFill = capture.Encoded is null || capture.EncodedGapFill == Formats.EncodedFile.DefaultGapFill ? null : capture.EncodedGapFill,
                ImportedSettings = capture.ImportedSettings,
            };

            string temp = StatePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, StatePath, overwrite: true);
        }
    }

    /// <summary>
    /// 外部参照のデータを復旧用フォルダのファイルにコピーする (同じデータは 1 回だけ)。復旧したドキュメントの外部参照は、
    /// このフォルダのファイルをそのまま使う。
    /// </summary>
    private RecoveryExternal PersistExternal(IByteSource source)
    {
        if (_externals.TryGetValue(source, out RecoveryExternal? known))
        {
            return known;
        }

        if (source is FileByteSource file && string.Equals(Path.GetDirectoryName(file.Path), Folder, StringComparison.OrdinalIgnoreCase))
        {
            known = new RecoveryExternal(Path.GetFileName(file.Path), file.Length);
            _externals[source] = known;
            return known;
        }

        string name = $"ext-{_externals.Count + 1}-{Guid.NewGuid():N}.bin";
        string path = Path.Combine(Folder, name);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[(int)Math.Min(Math.Max(source.Length, 1), 4 * 1024 * 1024)];
            for (long done = 0; done < source.Length; done += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, source.Length - done);
                source.Read(done, buffer.AsSpan(0, n));
                stream.Write(buffer, 0, n);
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path);
        known = new RecoveryExternal(name, source.Length);
        _externals[source] = known;
        return known;
    }

    /// <summary>復旧用データを消す (保存した、または変更がなくなった。仕様 5)。追加バッファの一時ファイルは残す。</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            TryDelete(StatePath);
            TryDelete(StatePath + ".tmp");
        }
    }

    /// <summary>ロックだけを放し、データは残す (復旧に失敗したとき)。</summary>
    public void Release()
    {
        lock (_writeLock)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    /// <summary>ドキュメントを閉じた後に呼ぶ。ロックを放し、フォルダごと消す。</summary>
    public void Dispose()
    {
        lock (_writeLock)
        {
            _lock?.Dispose();
            _lock = null;
            try
            {
                if (Directory.Exists(Folder))
                {
                    Directory.Delete(Folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せなかったものは次回起動時に片付ける (RecoveryStore.Scan)。
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>UI スレッドで取った、書き出す内容 (スナップショットは不変なので別スレッドで書いてよい)。</summary>
public sealed record RecoveryCapture(
    Document Document,
    DocumentSnapshot Snapshot,
    Guid DocumentId,
    string DisplayName,
    string? Path,
    FileStamp? Stamp,
    long AddBufferLength,
    long Cursor,
    long SelectionStart,
    long SelectionLength,
    (long Start, long Length, bool Resizable)? Range = null,
    (string Path, string? Serial, long RangeStart)? Device = null,
    (string Path, string Format, FileStamp? Stamp)? Encoded = null)
{
    /// <summary>デコードしたときの隙間の塗りつぶしの値 (ENG-38 の仕様 3)。</summary>
    public byte EncodedGapFill { get; init; } = Formats.EncodedFile.DefaultGapFill;

    /// <summary>インポートしたドキュメントの付随データ (<see cref="RecoveryRecord.ImportedSettings"/>)。</summary>
    public Formats.EncodedFileSettings? ImportedSettings { get; init; }
}
