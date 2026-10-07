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
        _ => throw new InvalidDataException($"不明なピースの種類です: {Kind}"),
    };
}

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

    public long Length { get; init; }

    public long Cursor { get; init; }

    public long SelectionStart { get; init; }

    public long SelectionLength { get; init; }

    public DateTime SavedAtUtc { get; init; }

    /// <summary>変更の量: 元データ以外から来たバイト数 (入力・貼り付け・生成)。</summary>
    public long ChangedBytes { get; init; }
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
    public static RecoveryCapture? Capture(Document document, long cursor, long selectionStart, long selectionLength)
    {
        if (!document.CurrentUsesLatestSource)
        {
            return null;
        }

        DocumentSnapshot snapshot = document.Current;
        string? path = (document.Source as FileByteSource)?.Path;
        FileStamp? stamp = (document.Source as FileByteSource)?.Stamp;
        return new RecoveryCapture(document, snapshot, document.Id, document.Source.DisplayName, path, stamp, document.AddBuffer.Length,
            cursor, selectionStart, selectionLength);
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
            long changed = 0;
            foreach ((long _, Piece piece) in capture.Snapshot.Tree.EnumerateAll())
            {
                pieces.Add(RecoveryPiece.From(piece));
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
                Length = capture.Snapshot.Length,
                Cursor = capture.Cursor,
                SelectionStart = capture.SelectionStart,
                SelectionLength = capture.SelectionLength,
                SavedAtUtc = DateTime.UtcNow,
                ChangedBytes = changed,
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
    long SelectionLength);
