using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

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

    /// <summary>正規表現の照合が時間の上限に達したため、飛ばした範囲がある (FIND-18 の「エラー」。範囲はメッセージ)。</summary>
    RegexTimedOut,
}

/// <summary>飛ばしたファイル (理由と、OS のメッセージ)。</summary>
public sealed record SkippedFile(string Path, FileSkipReason Reason, string Message);

/// <summary>
/// 1 つのファイルの検索の結果 (FIND-30 の仕様 5)。一致そのものは結果の <see cref="MatchStore"/> に置き (メモリ上は 1,000,000 件まで、
/// 超える分は一時ファイル)、ここには場所 (番号の区間) だけを持つ。全体の上限で途中まで記録したファイルは、「続ける」で続きの一致を加える。
/// </summary>
public sealed class FileSearchResult
{
    private readonly MultiFileSearchResults _owner;

    /// <summary>一致の区間 (結果の一致の列の番号と件数)。普通は 1 つ。「続ける」で増える。</summary>
    private readonly List<(long First, int Count)> _segments = [];
    private int _count;

    internal FileSearchResult(MultiFileSearchResults owner, string path, long size, DateTime lastWriteUtc, bool fromOpenDocument, DocumentSnapshot? snapshot)
    {
        _owner = owner;
        Path = path;
        Size = size;
        LastWriteUtc = lastWriteUtc;
        FromOpenDocument = fromOpenDocument;
        Snapshot = snapshot;
    }

    public string Path { get; }

    public long Size { get; }

    /// <summary>検索したときの更新日時 (UTC)。置換の前の確認に使う (FIND-31 の仕様 8)。</summary>
    public DateTime LastWriteUtc { get; }

    /// <summary>結果の中の番号 (<see cref="MultiFileSearchResults.FileAt"/>)。</summary>
    public int Index { get; internal set; }

    /// <summary>一致の件数 (1 ファイルあたりの上限 (既定 10,000 件) まで)。</summary>
    public int MatchCount => Volatile.Read(ref _count);

    /// <summary>1 ファイルあたりの上限で止めた (FIND-30 の仕様 8)。</summary>
    public bool LimitReached { get; internal set; }

    /// <summary>全体の上限で、このファイルの途中までを記録した (「続ける」で続きを探す)。</summary>
    public bool Incomplete { get; internal set; }

    /// <summary>続きを探す位置 (<see cref="Incomplete"/> のとき)。</summary>
    internal long ResumeFrom { get; set; }

    /// <summary>開いているドキュメントの (未保存の状態を含む) 内容を検索した。</summary>
    public bool FromOpenDocument { get; }

    /// <summary>一致のデータ (Hex・テキストの列) を読むためのスナップショット (開いているドキュメントを検索したとき)。</summary>
    public DocumentSnapshot? Snapshot { get; }

    /// <summary>一致 (開始の昇順) の写し。件数が多い場合は <see cref="GetMatches"/> を使う。</summary>
    public IReadOnlyList<SearchMatch> Matches => GetMatches(0, MatchCount);

    /// <summary><paramref name="index"/> 番目の一致。</summary>
    public SearchMatch MatchAt(int index)
    {
        long at = StoreIndex(index);
        return _owner.Store[at];
    }

    /// <summary>[start, start + count) 番目の一致の写し。</summary>
    public IReadOnlyList<SearchMatch> GetMatches(int start, int count)
    {
        var result = new List<SearchMatch>(Math.Max(0, Math.Min(count, MatchCount - start)));
        (long First, int Count)[] segments;
        lock (_segments)
        {
            segments = [.. _segments];
        }

        int skip = Math.Max(0, start);
        int left = count;
        foreach ((long first, int n) in segments)
        {
            if (left <= 0)
            {
                break;
            }

            if (skip >= n)
            {
                skip -= n;
                continue;
            }

            int take = Math.Min(n - skip, left);
            result.AddRange(_owner.Store.GetRange(first + skip, take));
            left -= take;
            skip = 0;
        }

        return result;
    }

    internal void AddSegment(long first, int count)
    {
        lock (_segments)
        {
            _segments.Add((first, count));
            Volatile.Write(ref _count, _count + count);
        }
    }

    private long StoreIndex(int index)
    {
        lock (_segments)
        {
            int i = index;
            foreach ((long first, int n) in _segments)
            {
                if (i < n)
                {
                    return first + i;
                }

                i -= n;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }
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
/// 一致は <see cref="MatchStore"/> に置く (メモリ上は <see cref="MemoryLimit"/> 件まで。FIND-20 の仕様 7)。件数の上限
/// (<see cref="Limit"/>) で止めた場合は、<see cref="MultiFileSearch.ContinueAsync"/> で上限を 2 倍にして続きから探せる (FIND-20 の仕様 6)。
/// 使い終わったら <see cref="Dispose"/> で一時ファイルを消す。
/// </summary>
public sealed class MultiFileSearchResults : IDisposable
{
    private readonly object _lock = new();
    private readonly List<FileSearchResult> _files = [];
    private readonly List<SkippedFile> _skipped = [];

    /// <summary>まだ探していないファイル (上限で止めたときの、取り出した後の残りと途中までのファイル)。</summary>
    private readonly LinkedList<MultiFileSearch.WorkItem> _leftover = new();
    private MatchStore? _store;
    private IEnumerator<string>? _enumerator;
    private bool _enumerated;
    private bool _started;
    private long _limit = MultiFileSearch.DefaultTotalLimit;
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

    /// <summary>メモリ上に置く一致の件数の上限 (FIND-20 の仕様 7。テストでは小さくできる)。</summary>
    public int MemoryLimit { get; init; } = MatchStore.DefaultMemoryLimit;

    /// <summary>上限を超えた一致を書き出す一時ファイルのフォルダ (null なら %TEMP%\HexEditor\search)。</summary>
    public string? SpillDirectory { get; init; }

    /// <summary>一致しない箇所の検索 (FIND-25) の「最小の繰り返し」。</summary>
    public int MismatchMinRepeat { get; init; } = MismatchSearch.DefaultMinRepeat;

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

    /// <summary>一致の置き場所。</summary>
    internal MatchStore Store
    {
        get
        {
            lock (_lock)
            {
                return _store ??= new MatchStore { MemoryLimit = MemoryLimit, SpillDirectory = SpillDirectory };
            }
        }
    }

    /// <summary>メモリ上に置いている一致の件数 (テスト用)。</summary>
    public int InMemoryMatches => Store.InMemoryCount;

    /// <summary>一時ファイルを作れなかった・書けなかったため、メモリ上の件数で止めた (FIND-20 の「エラー」)。</summary>
    public bool SpillFailed => _store?.SpillFailed ?? false;

    public string? SpillError => _store?.SpillError;

    public MultiFileSearchState State { get; private set; } = MultiFileSearchState.Running;

    /// <summary>全体の件数の今の上限 (「続ける」で 2 倍になる)。</summary>
    public long Limit
    {
        get
        {
            lock (_lock)
            {
                return _limit;
            }
        }
    }

    /// <summary>「続ける」で続きを探せるか (上限で止めた。一時ファイルの失敗で止めた場合は続けられない)。</summary>
    public bool CanContinue => State == MultiFileSearchState.LimitReached && !SpillFailed;

    /// <summary>一致のあったファイル (見つかった順) の写し。件数が多い場合は <see cref="FileAt"/> を使う。</summary>
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

    /// <summary>一致のあったファイルの数。</summary>
    public int FileCount
    {
        get
        {
            lock (_lock)
            {
                return _files.Count;
            }
        }
    }

    /// <summary><paramref name="index"/> 番目の一致のあったファイル。</summary>
    public FileSearchResult FileAt(int index)
    {
        lock (_lock)
        {
            return _files[index];
        }
    }

    /// <summary>読めなかったファイル (理由付き。FIND-30 の仕様 7) の写し。</summary>
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

    /// <summary>読めなかったファイルの数。</summary>
    public int SkippedCount
    {
        get
        {
            lock (_lock)
            {
                return _skipped.Count;
            }
        }
    }

    /// <summary>読めなかったファイルの、先頭から <paramref name="count"/> 件の写し。</summary>
    public IReadOnlyList<SkippedFile> SkippedHead(int count)
    {
        lock (_lock)
        {
            return [.. _skipped.Take(count)];
        }
    }

    /// <summary>一致の合計の件数。</summary>
    public long MatchCount => Interlocked.Read(ref _matchCount);

    /// <summary>処理したファイルの数 (読めなかったファイルを含む)。</summary>
    public int ProcessedFiles => Volatile.Read(ref _processed);

    /// <summary>見つかった (列挙した) ファイルの数。</summary>
    public int FoundFiles => Volatile.Read(ref _found);

    /// <summary>一致がなかったファイルの数 (読めなかったファイルは数えない)。</summary>
    public int FilesWithoutMatches => Volatile.Read(ref _withoutMatches);

    /// <summary>処理したバイト数。</summary>
    public long ProcessedBytes => Interlocked.Read(ref _bytes);

    /// <summary>今検索しているファイル。</summary>
    public string? CurrentFile => Volatile.Read(ref _current);

    /// <summary>結果・進捗が変わった (検索のスレッドで呼ぶ)。</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        lock (_lock)
        {
            _enumerator?.Dispose();
            _enumerator = null;
            _store?.Dispose();
        }
    }

    /// <summary>最初の検索を始める (上限を決め、ファイルの列挙を用意する)。</summary>
    internal void Start(long limit)
    {
        lock (_lock)
        {
            if (_started)
            {
                throw new InvalidOperationException("この結果の検索はもう始めました。続きは ContinueAsync で探します。");
            }

            _started = true;
            _limit = Math.Max(1, limit);
            _enumerator = MultiFileSearch.Enumerate(Targets, AddSkipped).GetEnumerator();
        }

        State = MultiFileSearchState.Running;
    }

    /// <summary>「続ける」: 上限を 2 倍にする (FIND-20 の仕様 6)。</summary>
    internal bool PrepareContinue()
    {
        lock (_lock)
        {
            if (!CanContinue)
            {
                return false;
            }

            _limit = _limit >= long.MaxValue / 2 ? long.MaxValue : _limit * 2;
        }

        State = MultiFileSearchState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>次に探すファイル (残りを先に、そのあと列挙の続き)。なければ false。</summary>
    internal bool TryTakeLeftover(out MultiFileSearch.WorkItem item)
    {
        lock (_lock)
        {
            if (_leftover.First is { } first)
            {
                item = first.Value;
                _leftover.RemoveFirst();
                return true;
            }
        }

        item = default;
        return false;
    }

    /// <summary>列挙の続き (列挙のスレッドだけが呼ぶ)。</summary>
    internal bool TryEnumerate(out string path)
    {
        IEnumerator<string>? e;
        lock (_lock)
        {
            e = _enumerated ? null : _enumerator;
        }

        if (e is not null && e.MoveNext())
        {
            path = e.Current;
            Interlocked.Increment(ref _found);
            return true;
        }

        lock (_lock)
        {
            _enumerated = true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>探さなかったファイルを残りに戻す (上限で止めたとき。「続ける」で探す)。</summary>
    internal void ReturnLeftover(MultiFileSearch.WorkItem item, bool first = false)
    {
        lock (_lock)
        {
            if (first)
            {
                _leftover.AddFirst(item);
            }
            else
            {
                _leftover.AddLast(item);
            }
        }
    }

    /// <summary>全体の上限に達したか。</summary>
    internal bool IsFull => Interlocked.Read(ref _matchCount) >= Limit || SpillFailed;

    /// <summary>
    /// 1 つのファイル (またはその続き) で見つかった一致を記録する。全体の上限の残りの分だけを加え (上限ちょうどで止める)、加えた件数を返す。
    /// </summary>
    internal int Record(MultiFileSearch.WorkItem item, long size, DateTime lastWrite, bool open, DocumentSnapshot? snapshot,
        IReadOnlyList<SearchMatch> matches, bool perFileLimitReached, out FileSearchResult? file)
    {
        int added = 0;
        bool newFile = false;
        lock (_lock)
        {
            file = item.Partial;
            long room = _limit - _matchCount;
            int take = (int)Math.Clamp(room, 0, matches.Count);
            if (take > 0)
            {
                added = Store.Append(matches, take, out long first);
                if (added > 0)
                {
                    if (file is null)
                    {
                        file = new FileSearchResult(this, item.Path, size, lastWrite, open, snapshot) { Index = _files.Count };
                        _files.Add(file);
                        newFile = true;
                    }

                    file.AddSegment(first, added);
                    _matchCount += added;
                }
            }

            if (file is not null)
            {
                bool cut = added < matches.Count;
                file.Incomplete = cut;
                file.LimitReached = !cut && perFileLimitReached;
                if (cut)
                {
                    file.ResumeFrom = added > 0 ? matches[added - 1].End : file.ResumeFrom;
                }
            }
        }

        if (newFile || added > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    internal void AddSkipped(SkippedFile file)
    {
        lock (_lock)
        {
            _skipped.Add(file);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ファイルを最後まで処理した。<paramref name="matched"/> が null なら読めなかった (一致がなかったファイルに数えない)。</summary>
    internal void Processed(long bytes, bool? matched)
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Add(ref _bytes, bytes);
        if (matched == false)
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

    /// <summary>全体の件数の上限の設定 (FIND-20 の仕様 6 と同じ設定。1,000〜100,000,000)。</summary>
    public const string TotalLimitKey = "search.findAll.limit";

    public const long MinTotalLimit = 1_000;

    public const long MaxTotalLimit = 100_000_000;

    /// <summary>1 ファイルあたりの件数の上限の既定値 (FIND-30 の仕様 8)。</summary>
    public const int DefaultPerFileLimit = 10_000;

    /// <summary>並列に検索するファイルの数の既定値と範囲 (FIND-30 の仕様 3)。</summary>
    public const int DefaultParallelism = 4;

    public const int MaxParallelism = 16;

    /// <summary>並列に検索するファイルの数の設定のキー。</summary>
    public const string ParallelismKey = "search.multiFile.parallelism";

    /// <summary>探すファイル (途中まで記録したファイルの続きなら <see cref="Partial"/>)。</summary>
    internal readonly record struct WorkItem(string Path, FileSearchResult? Partial);

    /// <summary>
    /// 対象を確かめる (存在しないフォルダがあれば、その名前を返す。FIND-30 の「エラー」: 検索の前にエラーを表示する)。
    /// </summary>
    public static IReadOnlyList<string> MissingFolders(MultiFileTargets targets) =>
        [.. targets.Folders.Where(f => string.IsNullOrWhiteSpace(f) || !Directory.Exists(f))];

    /// <summary>
    /// 対象のファイルを列挙する (FIND-30 の仕様 2)。列挙できなかったフォルダは <paramref name="onSkipped"/> に知らせる。
    /// たどったフォルダは最終的なパス (リンクを解決した先のパスに、その下の名前を付けたもの) で覚え、同じフォルダは 1 回だけ訪れる
    /// (シンボリックリンクのループで終わらなくならない。リンク先の下のフォルダも 2 回訪れない)。
    /// </summary>
    public static IEnumerable<string> Enumerate(MultiFileTargets targets, Action<SkippedFile>? onSkipped = null, CancellationToken cancellationToken = default)
    {
        string[] include = Masks(targets.IncludeMasks, "*");
        string[] exclude = Masks(targets.ExcludeMasks, null);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Path, string Key, int Depth)>();
        foreach (string folder in targets.Folders.Reverse())
        {
            string full = Path.GetFullPath(folder);
            stack.Push((full, FinalPath(full), 0));
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
            (string dir, string key, int depth) = stack.Pop();
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

            var subfolders = new List<(string Path, string Key)>();
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
                        // リンクはたどった先のパス、そうでなければ親の最終的なパスに名前を付けたもの。
                        subfolders.Add((entry.FullName, link ? FinalPath(entry.FullName) : Path.Combine(key, entry.Name)));
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
                stack.Push((subfolders[i].Path, subfolders[i].Key, depth + 1));
            }
        }
    }

    /// <summary>
    /// 検索する (FIND-30)。ファイルの列挙と検索を並行して行い、ファイルごとの結果を <paramref name="results"/> に加える。
    /// <paramref name="openDocument"/> は、そのパスのファイルを開いているドキュメントがあればその今の状態を返す (仕様 2 の
    /// 「開いているドキュメントを含める」)。件数の上限 (全体・ファイルごと) に達したら止める。全体の上限では、並列数に関係なく
    /// 上限の件数ちょうどで止め (<see cref="MultiFileSearchState.LimitReached"/>)、<see cref="ContinueAsync"/> で続きから探せる。
    /// キャンセルされた場合は <see cref="OperationCanceledException"/>。
    /// </summary>
    public static Task RunAsync(MultiFileSearchResults results, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int parallelism = DefaultParallelism, int perFileLimit = DefaultPerFileLimit, long totalLimit = DefaultTotalLimit,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default, string? tempDirectory = null)
    {
        results.Start(totalLimit);
        return RunCoreAsync(results, options, openDocument, parallelism, perFileLimit, operation, cancellationToken, tempDirectory);
    }

    /// <summary>
    /// 上限で止めた検索を続ける (「続ける」。FIND-20 の仕様 6、FIND-30 の仕様 8)。上限を 2 倍にし、途中まで記録したファイルの続きと、
    /// まだ探していないファイルを探す。重複も取りこぼしもない。続けられなければ何もしない。
    /// </summary>
    public static Task ContinueAsync(MultiFileSearchResults results, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int parallelism = DefaultParallelism, int perFileLimit = DefaultPerFileLimit,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default, string? tempDirectory = null) =>
        results.PrepareContinue()
            ? RunCoreAsync(results, options, openDocument, parallelism, perFileLimit, operation, cancellationToken, tempDirectory)
            : Task.CompletedTask;

    private static async Task RunCoreAsync(MultiFileSearchResults results, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int parallelism, int perFileLimit, LongRunningOperation? operation, CancellationToken cancellationToken, string? tempDirectory)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation?.CancellationToken ?? default);
        CancellationToken token = linked.Token;
        var queue = new BlockingCollection<WorkItem>(boundedCapacity: 1024);
        int limited = 0;
        int workers = Math.Clamp(parallelism, 1, MaxParallelism);

        void Limit()
        {
            Volatile.Write(ref limited, 1);
            linked.Cancel();
        }

        // 列挙: 前回の残り (途中までのファイルが先) のあとに、列挙の続き。止めたときに取り出していたファイルは残りに戻す。
        Task producer = Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!results.TryTakeLeftover(out WorkItem item))
                    {
                        if (!results.TryEnumerate(out string path))
                        {
                            break;
                        }

                        item = new WorkItem(path, null);
                    }

                    try
                    {
                        queue.Add(item, token);
                    }
                    catch (OperationCanceledException)
                    {
                        results.ReturnLeftover(item, item.Partial is not null);
                        throw;
                    }
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, CancellationToken.None);

        async Task Worker()
        {
            await Task.Yield();
            while (!token.IsCancellationRequested && queue.TryTake(out WorkItem item, Timeout.Infinite, token))
            {
                if (results.IsFull)
                {
                    results.ReturnLeftover(item, item.Partial is not null);
                    Limit();
                    break;
                }

                results.SetCurrent(item.Path);
                bool more;
                try
                {
                    more = SearchOneFile(results, item, options, openDocument, perFileLimit, token, tempDirectory);
                }
                catch (OperationCanceledException) when (Volatile.Read(ref limited) == 1 && !cancellationToken.IsCancellationRequested
                    && !(operation?.CancellationToken.IsCancellationRequested ?? false))
                {
                    // 上限で止めた: 探しかけのファイルは残りに戻す (「続ける」で最初から、または続きから探す)。
                    results.ReturnLeftover(item, item.Partial is not null);
                    break;
                }

                operation?.Report(results.ProcessedBytes);
                if (!more)
                {
                    Limit();
                    break;
                }
            }
        }

        Task[] tasks = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(Worker, CancellationToken.None))];
        bool userCancelled = false;
        try
        {
            await Task.WhenAll([producer, .. tasks]).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            userCancelled = Volatile.Read(ref limited) == 0 || cancellationToken.IsCancellationRequested || (operation?.CancellationToken.IsCancellationRequested ?? false);
        }

        // 取り出していないファイルを残りに戻す (列挙の順を保つ)。
        while (queue.TryTake(out WorkItem rest))
        {
            results.ReturnLeftover(rest);
        }

        results.SetCurrent(null);
        if (userCancelled)
        {
            results.SetState(MultiFileSearchState.Cancelled);
            throw new OperationCanceledException(cancellationToken);
        }

        results.SetState(Volatile.Read(ref limited) == 1 || results.SpillFailed ? MultiFileSearchState.LimitReached : MultiFileSearchState.Completed);
    }

    /// <summary>1 つのファイル (またはその続き) を検索する。全体の上限に達して記録しきれなかったら false (残りに戻してある)。</summary>
    private static bool SearchOneFile(MultiFileSearchResults results, WorkItem item, SearchOptions options, Func<string, OpenDocumentSnapshot?>? openDocument,
        int perFileLimit, CancellationToken token, string? tempDirectory)
    {
        string path = item.Path;
        int already = item.Partial?.MatchCount ?? 0;
        long cap = Math.Max(1, perFileLimit - already);
        long startFrom = item.Partial?.ResumeFrom ?? long.MinValue;
        SearchOptions fileOptions = options with
        {
            MaxDegreeOfParallelism = 1,
            MaxMatches = cap,
            IncludeOverlapping = false,
            OnUnreadable = null,
            OnTimeout = null,
        };
        try
        {
            if (results.Targets.IncludeOpenDocuments && openDocument?.Invoke(path) is { } open)
            {
                using SearchResults found = NewResults(results, open.Snapshot, fileOptions);
                SearchEngine.FindAllFrom(found, startFrom, null, token);
                return Record(results, item, open.Length, open.LastWriteUtc, found, true, open.Snapshot);
            }

            FileByteSource source = FileByteSource.Open(path); // ドキュメントが閉じる
            results.Opened(path);
            using var document = new Document(source, new DocumentOptions
            {
                LockPolicy = FileLockPolicy.None,
                CacheCapacity = 16L * 1024 * 1024,
                TempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "HexEditor", "multifile"),
            });
            using SearchResults matches = NewResults(results, document.Current, fileOptions);
            SearchEngine.FindAllFrom(matches, startFrom, null, token);
            return Record(results, item, source.Length, source.Stamp.LastWriteTimeUtc, matches, false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException
            or NotSupportedException)
        {
            if (item.Partial is { } partial)
            {
                partial.Incomplete = false;
            }

            results.AddSkipped(new SkippedFile(path, Reason(ex), ex.Message));
            results.Processed(0, null);
            return true;
        }
    }

    /// <summary>1 つのファイルの検索の結果 (一致しない箇所の検索は専用の処理で探す。FIND-25 の仕様 4)。</summary>
    private static SearchResults NewResults(MultiFileSearchResults results, DocumentSnapshot snapshot, SearchOptions options) =>
        results.Pattern.IsMismatch
            ? MismatchSearch.CreateResults(snapshot, results.Pattern, options, results.MismatchMinRepeat)
            : new SearchResults(snapshot, results.Pattern, options);

    private static bool Record(MultiFileSearchResults results, WorkItem item, long size, DateTime lastWrite, SearchResults found, bool open,
        DocumentSnapshot? snapshot)
    {
        IReadOnlyList<SearchMatch> matches = found.Matches;
        int added = results.Record(item, size, lastWrite, open, snapshot, matches, found.LimitReached, out FileSearchResult? file);
        bool cut = added < matches.Count;
        long until = cut ? (file?.ResumeFrom ?? long.MinValue) : long.MaxValue;

        // 読めなかった範囲と、正規表現の時間の上限で飛ばしたチャンクを記録する (記録した一致の範囲の分だけ。FIND-01、FIND-18 の「エラー」)。
        if (found.SkippedRanges.Any(r => r.Offset < until))
        {
            results.AddSkipped(new SkippedFile(item.Path, FileSkipReason.Unreadable, string.Empty));
        }

        SearchRange[] timedOut = [.. found.TimedOutRanges.Where(r => r.Offset < until)];
        if (timedOut.Length > 0)
        {
            results.AddSkipped(new SkippedFile(item.Path, FileSkipReason.RegexTimedOut,
                string.Join(", ", timedOut.Take(8).Select(r => $"0x{r.Offset:X}–0x{r.End - 1:X}")) + (timedOut.Length > 8 ? ", …" : string.Empty)));
        }

        if (cut)
        {
            // 全体の上限: 記録しきれなかった続きは「続ける」で探す。
            results.ReturnLeftover(new WorkItem(item.Path, file), first: true);
            return false;
        }

        results.Processed(size, (file?.MatchCount ?? 0) > 0);
        return true;
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
    /// 開いているドキュメントでの置換 (FIND-31 の仕様 6。UI のスレッドで呼ぶ。1 ファイルで 1 回の Undo)。置換できない一致 (長さを保てないなど)
    /// があれば、ファイルへの置換 (<see cref="ReplaceInFile"/>) と同じく、そのドキュメントは置換せずに理由を記録する (黙って捨てない)。
    /// </summary>
    public static FileReplaceOutcome ReplaceInEditor(EditorState editor, string path, IReadOnlyList<SearchMatch> matches, SearchPattern pattern,
        MultiFileReplaceOptions options, string historyName)
    {
        Document doc = editor.Document;
        if (doc.IsReadOnly || editor.ReadOnly)
        {
            return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.ReadOnly, string.Empty);
        }

        DocumentSnapshot current = doc.Current;
        var edits = new List<ReplacementEdit>();
        long previousEnd = 0;
        foreach (SearchMatch m in matches.OrderBy(m => m.Offset))
        {
            if (m.Offset < previousEnd || !Replacer.TryVerify(current, pattern, m.Offset, out int length, out int variant))
            {
                continue;
            }

            ReplaceIssue issue = Replacer.Plan(options.Template, m.Offset, length, variant, options.ReplaceOptions, current.Length, doc.CanResize,
                out ReplacementEdit? edit, current);
            if (issue != ReplaceIssue.None)
            {
                return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.CannotReplace, issue.ToString());
            }

            previousEnd = edit!.Offset + edit.RemoveLength;
            edits.Add(edit);
        }

        if (edits.Count > 0)
        {
            doc.ApplyReplacements(edits, historyName);
        }

        return new FileReplaceOutcome(path, FileReplaceStatus.ReplacedInEditor, edits.Count, null, string.Empty);
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
