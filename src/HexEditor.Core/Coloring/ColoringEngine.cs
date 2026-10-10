using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Coloring;

/// <summary>
/// 1 バイトの色付けの結果 (INSP-34 の仕様 1): 文字色・背景色・枠線を指定している最も上のルールの番号 (<see cref="ColoringRuleSet.Rules"/> の添字。
/// なければ −1)。
/// </summary>
public readonly record struct ColoringCell(short Foreground, short Background, short Border)
{
    public static readonly ColoringCell None = new(-1, -1, -1);

    public bool IsEmpty => Foreground < 0 && Background < 0 && Border < 0;
}

/// <summary>
/// 適用するルールの並び (ドキュメントのルール、全体のルールの順。それぞれ一覧の上ほど優先。INSP-34 の仕様 1)。有効なルールだけを解釈して持つ。
/// 解釈できないルールは <see cref="Errors"/> に入れ、適用しない (INSP-33 の「エラー」)。
/// </summary>
public sealed class ColoringRuleSet
{
    private static int _nextVersion;

    private ColoringRuleSet(IReadOnlyList<CompiledColoringRule> rules, IReadOnlyDictionary<string, ColoringRuleException> errors)
    {
        Rules = rules;
        Errors = errors;
        Version = Interlocked.Increment(ref _nextVersion);
        MaxMatchLength = rules.Count == 0 ? 1 : rules.Max(r => r.MaxMatchLength);
    }

    public static ColoringRuleSet Empty { get; } = new([], new Dictionary<string, ColoringRuleException>());

    public IReadOnlyList<CompiledColoringRule> Rules { get; }

    /// <summary>ルールの ID ごとの条件の誤り。</summary>
    public IReadOnlyDictionary<string, ColoringRuleException> Errors { get; }

    /// <summary>作るたびに変わる番号 (結果のキャッシュのキー)。</summary>
    public int Version { get; }

    public int MaxMatchLength { get; }

    public bool IsEmpty => Rules.Count == 0;

    /// <summary>ドキュメントのルールを全体のルールより優先して並べ、有効なものを解釈する。</summary>
    public static ColoringRuleSet Compile(IEnumerable<ColoringRule> documentRules, IEnumerable<ColoringRule> globalRules, IExpressionContext? context)
    {
        var compiled = new List<CompiledColoringRule>();
        var errors = new Dictionary<string, ColoringRuleException>();
        foreach (ColoringRule rule in documentRules.Concat(globalRules))
        {
            try
            {
                CompiledColoringRule c = CompiledColoringRule.Compile(rule, context);
                if (rule.Enabled && (rule.Foreground is not null || rule.Background is not null || rule.Border != ColoringBorder.None))
                {
                    compiled.Add(c);
                }
            }
            catch (ColoringRuleException ex)
            {
                errors[rule.Id] = ex;
            }
        }

        return compiled.Count == 0 && errors.Count == 0 ? Empty : new ColoringRuleSet(compiled, errors);
    }

    /// <summary>
    /// [start, start + hex.Length) の各バイトの結果を求める (データは <paramref name="data"/>、ドキュメントの <paramref name="dataOffset"/> から。
    /// パターンが範囲の境界をまたいでも色が付くよう、呼び出し側は前後に <see cref="MaxMatchLength"/> − 1 バイトを足して渡す)。
    /// <paramref name="hex"/> は Hex 列、<paramref name="text"/> はテキスト列の結果。
    /// </summary>
    public void Evaluate(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long dataOffset, long start, Span<ColoringCell> hex, Span<ColoringCell> text)
    {
        hex.Fill(ColoringCell.None);
        text.Fill(ColoringCell.None);
        int count = hex.Length;
        long end = start + count;
        var matches = new List<(long Start, int Length)>();
        for (int index = 0; index < Rules.Count; index++)
        {
            CompiledColoringRule rule = Rules[index];
            matches.Clear();
            rule.Collect(data, states, dataOffset, start - rule.MaxMatchLength + 1, end, matches);
            ColoringRule r = rule.Rule;
            bool toHex = r.Target != ColoringTarget.Text;
            bool toText = r.Target != ColoringTarget.Hex;
            short id = (short)index;
            foreach ((long s, int len) in matches)
            {
                int from = (int)Math.Max(0, s - start);
                int to = (int)Math.Min(count, s + len - start);
                for (int i = from; i < to; i++)
                {
                    if (toHex)
                    {
                        hex[i] = Merge(hex[i], r, id);
                    }

                    if (toText)
                    {
                        text[i] = Merge(text[i], r, id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// ドキュメントの [start, start + hex.Length) の結果をその場で求める (エクスポート・コピー用。表示はキャッシュのある <see cref="ColoringEngine"/>)。
    /// パターンが範囲の境界をまたいでも同じ色になるよう、前後に一致の最大長の分を足して読む。
    /// </summary>
    public void EvaluateRange(DocumentSnapshot snapshot, long start, Span<ColoringCell> hex, Span<ColoringCell> text)
    {
        hex.Fill(ColoringCell.None);
        text.Fill(ColoringCell.None);
        long end = Math.Min(start + hex.Length, snapshot.Length);
        if (IsEmpty || end <= start)
        {
            return;
        }

        int margin = MaxMatchLength - 1;
        long dataStart = Math.Max(0, start - margin);
        long dataEnd = Math.Min(snapshot.Length, end + margin);
        byte[] data = new byte[dataEnd - dataStart];
        ReadResult read = snapshot.Read(dataStart, data);
        int count = (int)(end - start);
        Evaluate(data, ColoringEngine.StatesOf(read, dataStart, data.Length), dataStart, start, hex[..count], text[..count]);
    }

    /// <summary>
    /// コピー (HTML・RTF の「色を含める」) に使う色付け (INSP-33 の仕様 8)。文字色・背景色を指定しているルールの色を返す。ルールがなければ null。
    /// </summary>
    public Clipboard.CopyColoring? ForCopy(DocumentSnapshot snapshot)
    {
        if (IsEmpty)
        {
            return null;
        }

        List<uint> palette = [.. Rules.SelectMany(r => new[] { r.Rule.Foreground, r.Rule.Background }).OfType<uint>().Select(c => c & 0xFFFFFF).Distinct()];
        return new Clipboard.CopyColoring(palette, (offset, count) =>
        {
            var hex = new ColoringCell[count];
            var text = new ColoringCell[count];
            EvaluateRange(snapshot, offset, hex, text);
            return (Convert(hex), Convert(text));
        });

        Clipboard.CopyCellColor[] Convert(ColoringCell[] cells)
        {
            var result = new Clipboard.CopyCellColor[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                ColoringCell c = cells[i];
                result[i] = new Clipboard.CopyCellColor(
                    c.Foreground >= 0 ? Rules[c.Foreground].Rule.Foreground & 0xFFFFFF : null,
                    c.Background >= 0 ? Rules[c.Background].Rule.Background & 0xFFFFFF : null);
            }

            return result;
        }
    }

    /// <summary>まだ決まっていない種類 (文字色・背景色・枠線) だけ、このルールのものにする (上のルールほど優先)。</summary>
    private static ColoringCell Merge(ColoringCell cell, ColoringRule rule, short id) => new(
        cell.Foreground < 0 && rule.Foreground is not null ? id : cell.Foreground,
        cell.Background < 0 && rule.Background is not null ? id : cell.Background,
        cell.Border < 0 && rule.Border != ColoringBorder.None ? id : cell.Border);
}

/// <summary>
/// 色付けルールの評価 (INSP-33 の「巨大ファイル・長時間処理」): 表示範囲を 4 KB のチャンクに分けて別のスレッドで評価し、結果を
/// チャンクとスナップショットの版ごとにキャッシュする。結果が出るまでは色なしで描画する (<see cref="TryGetCells"/> が false)。
/// ファイル全体は走査しない。
/// </summary>
public sealed class ColoringEngine
{
    public const int ChunkSize = 4096;

    /// <summary>キャッシュするチャンクの数。</summary>
    private const int CacheLimit = 128;

    private readonly object _lock = new();
    private readonly Dictionary<(long Chunk, int Version), Entry> _cache = [];
    private readonly LinkedList<(long Chunk, int Version)> _lru = new();
    private readonly HashSet<(long Chunk, int Version)> _pending = [];
    private DocumentSnapshot? _snapshot;
    private ColoringRuleSet _rules = ColoringRuleSet.Empty;

    /// <summary>チャンクの評価が終わった (別のスレッドから呼ぶ。描き直す)。</summary>
    public event EventHandler? Updated;

    /// <summary>評価を走らせる関数 (テストで同期にする)。</summary>
    public Action<Action> Schedule { get; set; } = work => Task.Run(work);

    public ColoringRuleSet Rules
    {
        get => _rules;
        set
        {
            lock (_lock)
            {
                _rules = value;
                Clear();
            }
        }
    }

    /// <summary>1 画面分の評価にかかった時間 (ms。性能のテストで読む)。</summary>
    public double LastEvaluationMilliseconds { get; private set; }

    private readonly Queue<double> _evaluationTimes = new();

    /// <summary>これまでのチャンク (4 KiB。1 画面分以上) ごとの評価時間 (ms。新しい 4,096 件まで) を取り出して空にする (INSP-33 の仕様 5 の性能テスト用)。</summary>
    public IReadOnlyList<double> TakeEvaluationTimes()
    {
        lock (_evaluationTimes)
        {
            double[] times = [.. _evaluationTimes];
            _evaluationTimes.Clear();
            return times;
        }
    }

    /// <summary>最後の評価の失敗 (診断用)。</summary>
    public Exception? LastError { get; private set; }

    private sealed record Entry(ColoringCell[] Hex, ColoringCell[] Text);

    private void Clear()
    {
        _cache.Clear();
        _lru.Clear();
        _pending.Clear();
        lock (_work)
        {
            _work.Clear();
        }
    }

    /// <summary>まだ評価していないチャンクの待ち行列 (新しい順)。速いスクロールで古くなった分は捨てる。</summary>
    private readonly LinkedList<((long Chunk, int Version) Key, Action Work)> _work = new();
    private bool _working;

    /// <summary>同時に待たせるチャンクの数 (超えたら古いものを捨てる。捨てたものはまた見えたときに評価する)。</summary>
    private const int MaxQueued = 32;

    /// <summary>
    /// チャンクの評価を待ち行列に入れる (<see cref="_lock"/> の中から呼ぶ)。評価は 1 つの作業で新しい順に進める (チャンクごとに
    /// スレッドを使うと、速いスクロールで UI のスレッドと CPU を取り合う。INSP-33 の仕様 5)。
    /// </summary>
    private void Enqueue((long Chunk, int Version) key, Action work)
    {
        lock (_work)
        {
            _work.AddFirst((key, work));
            while (_work.Count > MaxQueued)
            {
                _pending.Remove(_work.Last!.Value.Key);
                _work.RemoveLast();
            }

            if (_working)
            {
                return;
            }

            _working = true;
        }

        Schedule(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            Action work;
            lock (_work)
            {
                if (_work.Count == 0)
                {
                    _working = false;
                    return;
                }

                work = _work.First!.Value.Work;
                _work.RemoveFirst();
            }

            work();
        }
    }

    /// <summary>
    /// [start, start + hex.Length) の結果を返す。まだ評価していないチャンクがあれば、評価を始めて false を返す (そのチャンクの部分は
    /// <see cref="ColoringCell.None"/>)。UI スレッドから呼ぶ。ブロックしない。
    /// </summary>
    public bool TryGetCells(DocumentSnapshot snapshot, long start, Span<ColoringCell> hex, Span<ColoringCell> text)
    {
        hex.Fill(ColoringCell.None);
        text.Fill(ColoringCell.None);
        ColoringRuleSet rules = _rules;
        if (rules.IsEmpty || hex.Length == 0)
        {
            return true;
        }

        bool complete = true;
        lock (_lock)
        {
            if (!ReferenceEquals(snapshot, _snapshot))
            {
                // 編集で版が変わった: 古い版の結果は使わない。
                _snapshot = snapshot;
                Clear();
            }

            long end = Math.Min(start + hex.Length, snapshot.Length);
            for (long chunk = start / ChunkSize; chunk * ChunkSize < end; chunk++)
            {
                var key = (chunk, rules.Version);
                if (_cache.TryGetValue(key, out Entry? entry))
                {
                    long chunkStart = chunk * ChunkSize;
                    int from = (int)Math.Max(0, start - chunkStart);
                    int to = (int)Math.Min(entry.Hex.Length, end - chunkStart);
                    int at = (int)(chunkStart + from - start);
                    if (to > from)
                    {
                        entry.Hex.AsSpan(from, to - from).CopyTo(hex[at..]);
                        entry.Text.AsSpan(from, to - from).CopyTo(text[at..]);
                    }

                    _lru.Remove(key);
                    _lru.AddFirst(key);
                }
                else
                {
                    complete = false;
                    if (_pending.Add(key))
                    {
                        DocumentSnapshot current = snapshot;
                        long target = chunk;
                        Enqueue(key, () => EvaluateChunk(current, rules, target));
                    }
                }
            }
        }

        return complete;
    }

    /// <summary>チャンク 1 つを評価してキャッシュに入れる (別のスレッド)。</summary>
    private void EvaluateChunk(DocumentSnapshot snapshot, ColoringRuleSet rules, long chunk)
    {
        long chunkStart = chunk * ChunkSize;
        int length = (int)Math.Min(ChunkSize, snapshot.Length - chunkStart);
        var hex = new ColoringCell[Math.Max(0, length)];
        var text = new ColoringCell[Math.Max(0, length)];
        if (length > 0)
        {
            long started = 0;
            int margin = rules.MaxMatchLength - 1;
            long dataStart = Math.Max(0, chunkStart - margin);
            long dataEnd = Math.Min(snapshot.Length, chunkStart + length + margin);
            byte[] data = new byte[dataEnd - dataStart];
            try
            {
                ReadResult read = snapshot.Read(dataStart, data);
                ByteState[] states = StatesOf(read, dataStart, data.Length);
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                rules.Evaluate(data, states, dataStart, chunkStart, hex, text);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
            {
                // ドキュメントを閉じた後など: このチャンクは色を付けない (次の問い合わせで評価し直す)。
                LastError = ex;
                lock (_lock)
                {
                    _pending.Remove((chunk, rules.Version));
                }

                return;
            }

            LastEvaluationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_evaluationTimes)
            {
                _evaluationTimes.Enqueue(LastEvaluationMilliseconds);
                if (_evaluationTimes.Count > 4096)
                {
                    _evaluationTimes.Dequeue();
                }
            }
        }

        lock (_lock)
        {
            var key = (chunk, rules.Version);
            _pending.Remove(key);
            if (!ReferenceEquals(snapshot, _snapshot) || rules != _rules)
            {
                return;
            }

            _cache[key] = new Entry(hex, text);
            _lru.AddFirst(key);
            while (_lru.Count > CacheLimit)
            {
                _cache.Remove(_lru.Last!.Value);
                _lru.RemoveLast();
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>読み込みの結果の読めない範囲を、バイトごとの状態にする (読めるならすべて有効の空の配列)。</summary>
    internal static ByteState[] StatesOf(ReadResult read, long offset, int length)
    {
        if (read.IsComplete)
        {
            return [];
        }

        var states = new ByteState[length];
        foreach (UnreadableRange u in read.Unreadable)
        {
            long from = Math.Max(0, u.Offset - offset);
            long to = Math.Min(length, u.End - offset);
            for (long i = from; i < to; i++)
            {
                states[i] = ByteState.Unreadable;
            }
        }

        return states;
    }

    // ---- 凡例 (INSP-34 の仕様 3) ----

    /// <summary>ルールの一致の数 (バイト値の条件はバイト数、それ以外は一致の数)。開始が [start, end) のものを数える。</summary>
    private static long CountIn(CompiledColoringRule rule, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long dataOffset, long start, long end)
    {
        var matches = new List<(long Start, int Length)>();
        rule.Collect(data, states, dataOffset, start, end, matches);
        long count = 0;
        foreach ((long s, int len) in matches)
        {
            count += rule.CountsBytes ? Math.Min(len, end - s) : 1;
        }

        return count;
    }

    /// <summary>表示範囲 [start, end) での一致の件数 (表示用の読み込みを使い、ブロックしない。読み込み中のバイトは数えない)。</summary>
    public static long CountVisible(DocumentSnapshot snapshot, CompiledColoringRule rule, long start, long end)
    {
        end = Math.Min(end, snapshot.Length);
        if (end <= start)
        {
            return 0;
        }

        long dataEnd = Math.Min(snapshot.Length, end + rule.MaxMatchLength - 1);
        byte[] data = new byte[dataEnd - start];
        var states = new ByteState[data.Length];
        int n = snapshot.ReadForDisplay(start, data, states);
        return CountIn(rule, data.AsSpan(0, n), states.AsSpan(0, n), start, start, end);
    }

    /// <summary>「全体の件数を数える」(INSP-34 の仕様 3。長時間処理): ドキュメント全体の一致の件数。進捗は 0〜1。</summary>
    public static long CountAll(DocumentSnapshot snapshot, CompiledColoringRule rule, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        const int Block = 1 << 20;
        long total = 0;
        int margin = rule.MaxMatchLength - 1;
        long length = snapshot.Length;
        for (long at = 0; at < length; at += Block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long end = Math.Min(length, at + Block);
            long dataEnd = Math.Min(length, end + margin);
            byte[] data = new byte[dataEnd - at];
            ReadResult read = snapshot.Read(at, data);
            total += CountIn(rule, data, StatesOf(read, at, data.Length), at, at, end);
            progress?.Report(length == 0 ? 1 : (double)end / length);
        }

        return total;
    }

    /// <summary>
    /// 凡例の「次へ」「前へ」(INSP-34 の仕様 3): <paramref name="from"/> より後ろ (前) で開始する最初の一致の位置。なければ null。
    /// Hex パターン・テキストの条件は検索エンジン (FIND-01) で探す。それ以外は前から (後ろから) 順に評価する。
    /// </summary>
    public static long? FindNext(DocumentSnapshot snapshot, CompiledColoringRule rule, long from, bool forward, CancellationToken cancellationToken = default)
    {
        if (rule.SearchPattern is { } pattern && rule.Scope is null)
        {
            SearchHit? hit = SearchEngine.Find(snapshot, pattern, forward ? from + 1 : from, forward, wrap: false);
            return hit?.Offset;
        }

        const int Block = 1 << 16;
        int margin = rule.MaxMatchLength - 1;
        long length = snapshot.Length;
        var matches = new List<(long Start, int Length)>();
        if (forward)
        {
            for (long at = Math.Max(0, from + 1); at < length; at += Block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long end = Math.Min(length, at + Block);
                long dataEnd = Math.Min(length, end + margin);
                byte[] data = new byte[dataEnd - at];
                ReadResult read = snapshot.Read(at, data);
                matches.Clear();
                rule.Collect(data, StatesOf(read, at, data.Length), at, at, end, matches);
                if (matches.Count > 0)
                {
                    return matches.Min(m => m.Start);
                }
            }
        }
        else
        {
            for (long end = Math.Min(from, length); end > 0; end -= Block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long at = Math.Max(0, end - Block);
                long dataEnd = Math.Min(length, end + margin);
                byte[] data = new byte[dataEnd - at];
                ReadResult read = snapshot.Read(at, data);
                matches.Clear();
                rule.Collect(data, StatesOf(read, at, data.Length), at, at, end, matches);
                if (matches.Count > 0)
                {
                    // バイト値の区間は、区間の先頭を一致の位置とする。
                    return matches.Max(m => m.Start);
                }
            }
        }

        return null;
    }
}
