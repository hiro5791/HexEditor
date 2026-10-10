using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>複数ファイル検索の対象の指定 (FIND-30 の仕様 2)。名前を付けて保存できる (仕様 9)。</summary>
public sealed record MultiFileTargets
{
    /// <summary>検索するフォルダ (複数)。</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>サブフォルダを含める (既定オン)。</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>サブフォルダの深さの上限 (null は上限なし。0 は指定したフォルダだけ)。</summary>
    public int? MaxDepth { get; init; }

    /// <summary>含めるマスク (`*.bin;*.dat`。既定は `*`)。</summary>
    public string IncludeMasks { get; init; } = "*";

    /// <summary>除外するマスク (ファイルとフォルダ。`.git;node_modules`。既定は空)。</summary>
    public string ExcludeMasks { get; init; } = string.Empty;

    /// <summary>ファイルサイズの最小 (null は制限なし)。</summary>
    public long? MinSize { get; init; }

    /// <summary>ファイルサイズの最大 (null は制限なし)。</summary>
    public long? MaxSize { get; init; }

    /// <summary>隠しファイル・システムファイルを含める (既定オフ)。</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>シンボリックリンク・ジャンクションをたどる (既定オフ。オンでもたどったフォルダは 1 回だけ訪れる)。</summary>
    public bool FollowLinks { get; init; }

    /// <summary>NTFS 代替データストリームを含める (既定オフ。F4-09 の実装後に有効。今は使わない)。</summary>
    public bool IncludeAlternateStreams { get; init; }

    /// <summary>開いているドキュメントを含める (既定オン。開いているファイルは編集中の未保存の状態を検索する)。</summary>
    public bool IncludeOpenDocuments { get; init; } = true;

    public JsonObject ToJson() => new()
    {
        ["folders"] = new JsonArray([.. Folders.Select(f => (JsonNode?)f)]),
        ["subfolders"] = IncludeSubfolders,
        ["maxDepth"] = MaxDepth,
        ["include"] = IncludeMasks,
        ["exclude"] = ExcludeMasks,
        ["minSize"] = MinSize,
        ["maxSize"] = MaxSize,
        ["hidden"] = IncludeHidden,
        ["followLinks"] = FollowLinks,
        ["streams"] = IncludeAlternateStreams,
        ["openDocuments"] = IncludeOpenDocuments,
    };

    public static MultiFileTargets FromJson(JsonObject o) => new()
    {
        Folders = o["folders"] is JsonArray a ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>()] : [],
        IncludeSubfolders = Bool(o["subfolders"], true),
        MaxDepth = o["maxDepth"] is JsonValue d && d.TryGetValue(out int depth) ? depth : null,
        IncludeMasks = Text(o["include"]) ?? "*",
        ExcludeMasks = Text(o["exclude"]) ?? string.Empty,
        MinSize = o["minSize"] is JsonValue mi && mi.TryGetValue(out long min) ? min : null,
        MaxSize = o["maxSize"] is JsonValue ma && ma.TryGetValue(out long max) ? max : null,
        IncludeHidden = Bool(o["hidden"], false),
        FollowLinks = Bool(o["followLinks"], false),
        IncludeAlternateStreams = Bool(o["streams"], false),
        IncludeOpenDocuments = Bool(o["openDocuments"], true),
    };

    private static bool Bool(JsonNode? node, bool fallback) => node is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>読めなかった・飛ばしたファイルの理由 (FIND-30 の仕様 7、FIND-31 の仕様 7・8)。</summary>
public enum FileSkipReason
{
    AccessDenied,
    InUse,
    PathTooLong,
    NotFound,
    IoError,

    /// <summary>読み込みエラー (不良セクタなど) のある範囲があった (読めた範囲は検索した)。</summary>
    Unreadable,

    /// <summary>読み取り専用属性のため置換しなかった (FIND-31 の仕様 7)。</summary>
    ReadOnly,

    /// <summary>検索の後にサイズか更新日時が変わった (FIND-31 の仕様 8)。</summary>
    ChangedSinceSearch,

    /// <summary>置換できない一致があった (埋めて長さを保つで長すぎるなど)。</summary>
    CannotReplace,
}

/// <summary>飛ばしたファイル (理由と、OS のメッセージ)。</summary>
public sealed record SkippedFile(string Path, FileSkipReason Reason, string Message);

/// <summary>1 つのファイルの検索の結果 (FIND-30 の仕様 5)。</summary>
public sealed class FileSearchResult
{
    public FileSearchResult(string path, long size, DateTime lastWriteUtc, IReadOnlyList<SearchMatch> matches, bool limitReached, bool fromOpenDocument)
    {
        Path = path;
        Size = size;
        LastWriteUtc = lastWriteUtc;
        Matches = matches;
        LimitReached = limitReached;
        FromOpenDocument = fromOpenDocument;
    }

    public string Path { get; }

    public long Size { get; }

    /// <summary>検索したときの更新日時 (UTC)。置換の前の確認に使う (FIND-31 の仕様 8)。</summary>
    public DateTime LastWriteUtc { get; }

    /// <summary>一致 (開始の昇順)。1 ファイルあたりの上限 (既定 10,000 件) まで。</summary>
    public IReadOnlyList<SearchMatch> Matches { get; }

    /// <summary>1 ファイルあたりの上限で止めた。</summary>
    public bool LimitReached { get; }

    /// <summary>開いているドキュメントの (未保存の状態を含む) 内容を検索した。</summary>
    public bool FromOpenDocument { get; }

    /// <summary>一致のデータ (Hex・テキストの列) を読むためのスナップショット (検索したもの)。</summary>
    public DocumentSnapshot? Snapshot { get; init; }
}

/// <summary>複数ファイル検索の状態。</summary>
public enum MultiFileSearchState
{
    Running,
    Completed,
    Cancelled,
    LimitReached,
}

/// <summary>
/// 複数ファイル検索の結果 (FIND-30)。検索のスレッドがファイルごとに加え、<see cref="Changed"/> を出す。読み取りはどのスレッドからでもできる。
/// </summary>
public sealed class MultiFileSearchResults
{
    private readonly object _lock = new();
    private readonly List<FileSearchResult> _files = [];
    private readonly List<SkippedFile> _skipped = [];
    private long _matchCount;
    private int _processed;
    private int _found;
    private int _withoutMatches;
    private long _bytes;
    private string? _current;

    public MultiFileSearchResults(SearchPattern pattern, MultiFileTargets targets)
    {
        Pattern = pattern;
        Targets = targets;
    }

    public SearchPattern Pattern { get; }

    public MultiFileTargets Targets { get; }

    /// <summary>開いたファイルを記録する (テスト用。除外したファイルを開いていないことの確認)。</summary>
    public bool RecordOpenedFiles { get; init; }

    private readonly ConcurrentQueue<string> _opened = new();

    /// <summary>開いたファイル (<see cref="RecordOpenedFiles"/> のときだけ)。</summary>
    public IReadOnlyCollection<string> OpenedFiles => _opened;

    internal void Opened(string path)
    {
        if (RecordOpenedFiles)
        {
            _opened.Enqueue(path);
        }
    }

    public MultiFileSearchState State { get; private set; } = MultiFileSearchState.Running;

    /// <summary>一致のあったファイル (見つかった順)。</summary>
    public IReadOnlyList<FileSearchResult> Files
    {
        get
        {
            lock (_lock)
            {
                return [.. _files];
            }
        }
    }

    /// <summary>読めなかったファイル (理由付き。FIND-30 の仕様 7)。</summary>
    public IReadOnlyList<SkippedFile> Skipped
    {
        get
        {
            lock (_lock)
            {
                return [.. _skipped];
            }
        }
    }

    /// <summary>一致の合計の件数。</summary>
    public long MatchCount => Interlocked.Read(ref _matchCount);

    /// <summary>処理したファイルの数。</summary>
    public int ProcessedFiles => Volatile.Read(ref _processed);

    /// <summary>見つかった (列挙した) ファイルの数。</summary>
    public int FoundFiles => Volatile.Read(ref _found);

    /// <summary>一致がなかったファイルの数。</summary>
    public int FilesWithoutMatches => Volatile.Read(ref _withoutMatches);

    /// <summary>処理したバイト数。</summary>
    public long ProcessedBytes => Interlocked.Read(ref _bytes);

    /// <summary>今検索しているファイル。</summary>
    public string? CurrentFile => Volatile.Read(ref _current);

    /// <summary>結果・進捗が変わった (検索のスレッドで呼ぶ)。</summary>
    public event EventHandler? Changed;

    internal void AddFile(FileSearchResult file)
    {
        lock (_lock)
        {
            _files.Add(file);
        }

        Interlocked.Add(ref _matchCount, file.Matches.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void AddSkipped(SkippedFile file)
    {
        lock (_lock)
        {
            _skipped.Add(file);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Found() => Interlocked.Increment(ref _found);

    internal void Processed(long bytes, bool matched)
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Add(ref _bytes, bytes);
        if (!matched)
        {
            Interlocked.Increment(ref _withoutMatches);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetCurrent(string? path) => Volatile.Write(ref _current, path);

    internal void SetState(MultiFileSearchState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>開いているドキュメント (複数ファイル検索で、ファイルの代わりに未保存の状態を検索する。FIND-30 の仕様 2)。</summary>
public sealed record OpenDocumentSnapshot(DocumentSnapshot Snapshot, long Length, DateTime LastWriteUtc);

/// <summary>
/// 複数ファイル検索 (FIND-30)。フォルダのファイルを列挙しながら、並列に (既定 4 ファイル) チャンク単位で検索する。ファイルは読み取りで、
/// 読み取り・書き込み・削除をすべて許す共有モードで開く (仕様 4)。読めないファイルは理由を記録して飛ばす (「エラー」)。
/// </summary>
public static class MultiFileSearch
{
    /// <summary>全体の件数の上限 (FIND-20 と同じ。FIND-30 の仕様 8)。</summary>
    public const long DefaultTotalLimit = 1_000_000;

    /// <summary>1 ファイルあたりの件数の上限の既定値 (FIND-30 の仕様 8)。</summary>
    public const int DefaultPerFileLimit = 10_000;

    /// <summary>並列に検索するファイルの数の既定値と範囲 (FIND-30 の仕様 3)。</summary>
    public const int DefaultParallelism = 4;

    public const int MaxParallelism = 16;

    /// <summary>並列に検索するファイルの数の設定のキー。</summary>
    public const string ParallelismKey = "search.multiFile.parallelism";

    /// <summary>
    /// 対象を確かめる (存在しないフォルダがあれば、その名前を返す。FIND-30 の「エラー」: 検索の前にエラーを表示する)。
    /// </summary>
    public static IReadOnlyList<string> MissingFolders(MultiFileTargets targets) =>
        [.. targets.Folders.Where(f => string.IsNullOrWhiteSpace(f) || !Directory.Exists(f))];

    /// <summary>
    /// 対象のファイルを列挙する (FIND-30 の仕様 2)。列挙できなかったフォルダは <paramref name="onSkipped"/> に知らせる。
    /// たどったフォルダは最終的なパスで覚え、同じフォルダは 1 回だけ訪れる (シンボリックリンクのループで終わらなくならない)。
    /// </summary>
    public static IEnumerable<string> Enumerate(MultiFileTargets targets, Action<SkippedFile>? onSkipped = null, CancellationToken cancellationToken = default)
    {
        string[] include = Masks(targets.IncludeMasks, "*");
        string[] exclude = Masks(targets.ExcludeMasks, null);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Path, int Depth)>();
        foreach (string folder in targets.Folders.Reverse())
        {
            stack.Push((Path.GetFullPath(folder), 0));
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Win32,
        };
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string dir, int depth) = stack.Pop();
            string key = FinalPath(dir);
            if (!visited.Add(key))
            {
                continue;
            }

            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                onSkipped?.Invoke(new SkippedFile(dir, Reason(ex), ex.Message));
                continue;
            }

            var subfolders = new List<string>();
            foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    onSkipped?.Invoke(new SkippedFile(entry.FullName, Reason(ex), ex.Message));
                    continue;
                }

                if (!targets.IncludeHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }

                if (exclude.Any(m => FileSystemName.MatchesSimpleExpression(m, entry.Name, ignoreCase: true)))
                {
                    continue;
                }

                bool link = (attributes & FileAttributes.ReparsePoint) != 0;
                if (entry is DirectoryInfo)
                {
                    if (link && !targets.FollowLinks)
                    {
                        continue;
                    }

                    if (targets.IncludeSubfolders && (targets.MaxDepth is null || depth < targets.MaxDepth))
                    {
                        subfolders.Add(entry.FullName);
                    }

                    continue;
                }

                if (link && !targets.FollowLinks)
                {
                    continue;
                }

                if (!include.Any(m => FileSystemName.MatchesSimpleExpression(m, entry.Name, ignoreCase: true)))
                {
                    continue;
                }

                if (targets.MinSize is not null || targets.MaxSize is not null)
                {
                    long size = ((FileInfo)entry).Length;
                    if (size < (targets.MinSize ?? 0) || size > (targets.MaxSize ?? long.MaxValue))
                    {
                        continue;
                    }
                }

                yield return entry.FullName;
            }

            for (int i = subfolders.Count - 1; i >= 0; i--)
            {
                stack.Push((subfolders[i], depth + 1));
            }
        }
    }

    /// <summary>
    /// 検索する (FIND-30)。ファイルの列挙と検索を並行して行い、ファイルごとの結果を <paramref name="results"/> に加える。
    /// <paramref name="openDocument"/> は、そのパスのファイルを開いているドキュメントがあればその今の状態を返す (仕様 2 の
    /// 「開いているドキュメントを含める」)。件数の上限 (全体・ファイルごと) に達したら止める。キャンセルされた場合は
    /// <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task RunAsync(MultiFileSearchResults results, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int parallelism = DefaultParallelism, int perFileLimit = DefaultPerFileLimit, long totalLimit = DefaultTotalLimit,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default, string? tempDirectory = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation?.CancellationToken ?? default);
        CancellationToken token = linked.Token;
        var queue = new BlockingCollection<string>(boundedCapacity: 1024);
        var remaining = new Remaining { Value = totalLimit };
        bool limited = false;
        int workers = Math.Clamp(parallelism, 1, MaxParallelism);

        Task producer = Task.Run(() =>
        {
            try
            {
                foreach (string path in Enumerate(results.Targets, results.AddSkipped, token))
                {
                    results.Found();
                    queue.Add(path, token);
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, token);

        async Task Worker()
        {
            await Task.Yield();
            foreach (string path in queue.GetConsumingEnumerable(token))
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref remaining.Value) <= 0)
                {
                    limited = true;
                    linked.Cancel();
                    break;
                }

                results.SetCurrent(path);
                SearchOneFile(results, path, options, openDocument, perFileLimit, remaining, operation, token, tempDirectory);
                operation?.Report(results.ProcessedBytes);
            }
        }

        Task[] tasks = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(Worker, token))];
        try
        {
            await Task.WhenAll([producer, .. tasks]).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limited || Interlocked.Read(ref remaining.Value) <= 0)
        {
            limited = true;
        }
        catch (OperationCanceledException)
        {
            results.SetCurrent(null);
            results.SetState(MultiFileSearchState.Cancelled);
            throw;
        }

        results.SetCurrent(null);
        results.SetState(limited || Interlocked.Read(ref remaining.Value) <= 0 ? MultiFileSearchState.LimitReached : MultiFileSearchState.Completed);
    }

    private static void SearchOneFile(MultiFileSearchResults results, string path, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int perFileLimit, Remaining remaining, LongRunningOperation? operation, CancellationToken token, string? tempDirectory)
    {
        long cap = Math.Min(perFileLimit, Interlocked.Read(ref remaining.Value));
        if (cap <= 0)
        {
            return;
        }

        SearchOptions fileOptions = options with
        {
            MaxDegreeOfParallelism = 1,
            MaxMatches = cap,
            IncludeOverlapping = false,
            OnUnreadable = null,
        };
        try
        {
            if (results.Targets.IncludeOpenDocuments && openDocument?.Invoke(path) is { } open)
            {
                using SearchResults found = SearchEngine.FindAll(open.Snapshot, results.Pattern, fileOptions, null, token);
                Record(results, path, open.Length, open.LastWriteUtc, found, true, open.Snapshot, remaining);
                return;
            }

            FileByteSource source = FileByteSource.Open(path); // ドキュメントが閉じる
            results.Opened(path);
            using var document = new Document(source, new DocumentOptions
            {
                LockPolicy = FileLockPolicy.None,
                CacheCapacity = 16L * 1024 * 1024,
                TempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "HexEditor", "multifile"),
            });
            DocumentSnapshot snapshot = document.Current;
            using SearchResults matches = SearchEngine.FindAll(snapshot, results.Pattern, fileOptions, null, token);
            Record(results, path, source.Length, source.Stamp.LastWriteTimeUtc, matches, false, null, remaining);
            if (matches.SkippedRanges.Count > 0)
            {
                results.AddSkipped(new SkippedFile(path, FileSkipReason.Unreadable, string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException
            or NotSupportedException)
        {
            results.AddSkipped(new SkippedFile(path, Reason(ex), ex.Message));
            results.Processed(0, false);
        }
    }

    private static void Record(MultiFileSearchResults results, string path, long size, DateTime lastWrite, SearchResults found, bool open,
        DocumentSnapshot? snapshot, Remaining remaining)
    {
        IReadOnlyList<SearchMatch> matches = found.Matches;
        if (matches.Count > 0)
        {
            Interlocked.Add(ref remaining.Value, -matches.Count);
            results.AddFile(new FileSearchResult(path, size, lastWrite, [.. matches], found.LimitReached, open) { Snapshot = snapshot });
        }

        results.Processed(size, matches.Count > 0);
    }

    /// <summary>全体の件数の上限までの残り。</summary>
    private sealed class Remaining
    {
        public long Value;
    }

    /// <summary>例外から飛ばした理由を決める。</summary>
    public static FileSkipReason Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => FileSkipReason.AccessDenied,
        PathTooLongException => FileSkipReason.PathTooLong,
        FileNotFoundException or DirectoryNotFoundException => FileSkipReason.NotFound,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => FileSkipReason.InUse, // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        _ => FileSkipReason.IoError,
    };

    /// <summary>セミコロン区切りのマスクを分ける。空なら <paramref name="whenEmpty"/> (null なら何もない)。</summary>
    private static string[] Masks(string text, string? whenEmpty)
    {
        string[] masks = [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return masks.Length > 0 ? masks : whenEmpty is null ? [] : [whenEmpty];
    }

    /// <summary>フォルダの最終的なパス (リンクをたどった先)。たどれなければそのまま。</summary>
    private static string FinalPath(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return Path.TrimEndingDirectorySeparator(target.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
    }

    // ---- 複数ファイル置換 (FIND-31) ----

    /// <summary>テスト用: ファイルを書き込む直前に呼ぶ (書き込みの遅延・途中のキャンセルの再現。TC-FIND-31-05)。</summary>
    internal static Action<string, CancellationToken>? BeforeFileWrite { get; set; }

    /// <summary>バックアップのファイル名 (`&lt;ファイル名&gt;.bak`、あれば `.bak1`、`.bak2`…。FIND-31 の仕様 5)。</summary>
    public static string BackupPath(string path)
    {
        string first = path + ".bak";
        if (!File.Exists(first))
        {
            return first;
        }

        for (int n = 1; ; n++)
        {
            string candidate = path + ".bak" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// ファイルを置換して保存する (FIND-31 の仕様 4〜8)。検索の後にサイズか更新日時が変わっていたら飛ばす。読み取り専用属性のファイルは
    /// <see cref="MultiFileReplaceOptions.ClearReadOnly"/> がなければ飛ばす。バックアップを作ってから、安全な保存 (長さが変わらなければ
    /// 変更箇所だけ書く) で書き込む。キャンセルされた場合、書き込み中のファイルは元のまま (<see cref="OperationCanceledException"/>)。
    /// </summary>
    public static FileReplaceOutcome ReplaceInFile(FileSearchResult file, IReadOnlyList<SearchMatch> matches, SearchPattern pattern,
        MultiFileReplaceOptions options, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        string path = file.Path;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.NotFound, string.Empty);
        }

        if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc)
        {
            return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.ChangedSinceSearch, string.Empty);
        }

        bool readOnly = info.IsReadOnly;
        if (readOnly && !options.ClearReadOnly)
        {
            return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.ReadOnly, string.Empty);
        }

        string? backup = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readOnly)
            {
                info.IsReadOnly = false;
            }

            if (options.Backup)
            {
                backup = BackupPath(path);
                File.Copy(path, backup, overwrite: false);
            }

            Directory.CreateDirectory(options.JournalDirectory);
            FileByteSource source = FileByteSource.Open(path);
            using var document = new Document(source, new DocumentOptions
            {
                LockPolicy = FileLockPolicy.None,
                CacheCapacity = 16L * 1024 * 1024,
                TempDirectory = Path.Combine(options.JournalDirectory, "replace"),
            });
            DocumentSnapshot current = document.Current;
            var edits = new List<ReplacementEdit>();
            long previousEnd = 0;
            foreach (SearchMatch m in matches.OrderBy(m => m.Offset))
            {
                if (m.Offset < previousEnd || !Replacer.TryVerify(current, pattern, m.Offset, out int length, out int variant))
                {
                    continue;
                }

                ReplaceIssue issue = Replacer.Plan(options.Template, m.Offset, length, variant, options.ReplaceOptions, current.Length,
                    document.CanResize, out ReplacementEdit? edit, current);
                if (issue != ReplaceIssue.None)
                {
                    return Restore(new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.CannotReplace, issue.ToString()));
                }

                previousEnd = edit!.Offset + edit.RemoveLength;
                edits.Add(edit);
            }

            if (edits.Count == 0)
            {
                return Restore(new FileReplaceOutcome(path, FileReplaceStatus.Replaced, 0, null, string.Empty));
            }

            document.ApplyReplacements(edits, "置換");
            var settings = new SaveSettings { JournalDirectory = options.JournalDirectory };
            SavePlan plan = SavePlanner.Plan(document, null, settings);
            plan = plan.Issue switch
            {
                SaveIssue.JournalTooLarge => SavePlanner.UseSafeSave(plan),
                SaveIssue.HardLinks => plan.CanKeepLinks ? SavePlanner.KeepLinks(plan) : SavePlanner.BreakLinks(plan),
                SaveIssue.BackupCopy => SavePlanner.WithoutBackup(plan),
                _ => plan,
            };
            if (!plan.CanExecute)
            {
                return Restore(new FileReplaceOutcome(path, FileReplaceStatus.Failed, 0, FileSkipReason.IoError, plan.Issue.ToString()));
            }

            // キャンセルは書き込みの前 (と、その場保存のジャーナルを書くまで) に効く。書き込みを始めたファイルは安全な保存で最後まで書くか、
            // 元のまま残る (FIND-31 の「巨大ファイル・長時間処理」)。
            BeforeFileWrite?.Invoke(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            LongRunningOperation fileOperation = operation ?? new LongRunningOperation("replace", OperationKind.WritesExternal, null, null, TimeProvider.System);
            using CancellationTokenRegistration registration = cancellationToken.Register(fileOperation.Cancel);
            try
            {
                SaveResult result = SavePlanner.Execute(plan, fileOperation);
                SavePlanner.Complete(plan, result);
                result.SavedFile?.Dispose();
            }
            catch
            {
                SavePlanner.Abort(plan);
                throw;
            }

            if (readOnly)
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }

            return new FileReplaceOutcome(path, FileReplaceStatus.Replaced, edits.Count, null, string.Empty) { BackupPath = backup };
        }
        catch (OperationCanceledException)
        {
            Restore(null);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Restore(new FileReplaceOutcome(path, FileReplaceStatus.Failed, 0, Reason(ex), ex.Message));
        }

        // 置換しなかったときは、外した読み取り専用属性を戻し、作ったバックアップを消す。
        FileReplaceOutcome Restore(FileReplaceOutcome? outcome)
        {
            try
            {
                if (readOnly && File.Exists(path))
                {
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }

                if (backup is not null && File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return outcome!;
        }
    }
}

/// <summary>複数ファイル置換の条件 (FIND-31)。</summary>
public sealed record MultiFileReplaceOptions
{
    public required ReplacementTemplate Template { get; init; }

    public ReplaceOptions ReplaceOptions { get; init; } = new();

    /// <summary>置換の前に元のファイルを `.bak` にコピーする (既定オン。仕様 5)。</summary>
    public bool Backup { get; init; } = true;

    /// <summary>「読み取り専用属性を外して置換する」(既定オフ。仕様 7)。</summary>
    public bool ClearReadOnly { get; init; }

    /// <summary>その場保存のジャーナルと一時データの置き場所。</summary>
    public required string JournalDirectory { get; init; }
}

/// <summary>1 つのファイルの置換の結果 (FIND-31 の仕様 9)。</summary>
public enum FileReplaceStatus
{
    /// <summary>ファイルを置換して保存した。</summary>
    Replaced,

    /// <summary>開いているドキュメントを、エディタ上で置換した (未保存。仕様 6)。</summary>
    ReplacedInEditor,

    /// <summary>飛ばした (読み取り専用、検索後に変更など)。</summary>
    Skipped,

    /// <summary>書き込みに失敗した (容量不足、アクセス拒否、使用中)。</summary>
    Failed,

    /// <summary>キャンセルしたため置換しなかった。</summary>
    NotProcessed,
}

/// <summary>1 つのファイルの置換の結果。</summary>
public sealed record FileReplaceOutcome(string Path, FileReplaceStatus Status, long Count, FileSkipReason? Reason, string Message)
{
    /// <summary>作ったバックアップのパス。</summary>
    public string? BackupPath { get; init; }
}
