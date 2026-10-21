using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.Templates;

namespace HexEditor.Core.FileMap;

/// <summary>ファイルマップを作る指定 (ANA-33 の仕様 1)。</summary>
public sealed record FileMapRequest
{
    /// <summary>対象 (null はドキュメント全体)。対象範囲 (0.1) は 1 つの連続した範囲にする (マルチ選択は最初から最後までの範囲)。</summary>
    public HashRange? Range { get; init; }

    /// <summary>ANA-16 の判定のしきい値 (詳細設定。ANA-16 の仕様 6)。</summary>
    public ClassThresholds Thresholds { get; init; } = new();

    /// <summary>構造の解析に使うテンプレートの一覧 (null なら構造の解析でテンプレートを使わない)。</summary>
    public TemplateCatalog? Templates { get; init; }

    /// <summary>ドキュメントのファイル名 (テンプレートの自動適用の判定の拡張子と <c>std::sys::file_name()</c>)。</summary>
    public string? FileName { get; init; }

    /// <summary>シグネチャデータベース (ANA-17)。</summary>
    public FileTypeDatabase Database { get; init; } = FileTypeDatabase.BuiltIn;

    /// <summary>カービングの定義 (12 の FOR-18 の仕様 3。組み込みのもの)。</summary>
    public IReadOnlyList<CarvingSignature> CarvingSignatures { get; init; } = BuiltInSignatures.All;

    /// <summary>全体の走査の 1 回の読み込みの大きさ (途中の結果とキャンセルの単位)。</summary>
    public int ChunkSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// すでにある ANA-16 の結果 (仕様 5: 同じドキュメント・同じ範囲・同じブロックの大きさなら、走査の (a) を省く)。作った時点のデータと
    /// 同じスナップショットの結果だけを渡すこと。
    /// </summary>
    public ClassificationResult? Existing { get; init; }

    /// <summary>途中の結果を画面に出す間隔 (06 の 0.3 の 0.5 秒)。</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromSeconds(0.5);
}

/// <summary>作っている間の通知 (どれも作っているスレッドから呼ぶ)。</summary>
public sealed class FileMapCallbacks
{
    /// <summary>途中の結果 (各段階の結果をできたところから出す。仕様 2)。</summary>
    public Action<FileMapResult>? Partial { get; init; }

    /// <summary>段階を始めた (処理センターの段階の名前)。</summary>
    public Action<FileMapStage>? StageStarted { get; init; }
}

/// <summary>
/// ファイルマップの作成 (ANA-33)。概観 (標本) → 全体の走査 → 構造の解析の 3 つの段階で作り、1 つの長時間処理として進捗を出す。
/// メモリはブロックの分類 (1 ブロック 1 バイトと、根拠のためのエントロピー・p 値・確度) と、上限のある領域・候補だけで、対象の大きさに
/// 比例する部分は最大 2^24 ブロック分 (80 MiB) に収める。キャンセルしたときは、済んだ段階・済んだ範囲の結果を返す
/// (<see cref="FileMapResult.Stages"/> に「キャンセル」が入る)。
/// </summary>
public static partial class FileMapBuilder
{
    /// <summary>概観の区間の数 (仕様 2 の 1)。</summary>
    public const int SampleIntervals = 4096;

    /// <summary>概観で区間ごとに読む長さ。</summary>
    public const int SampleBytes = 4096;

    /// <summary>この長さを超える対象は、ブロックの数が 2^24 以下になるようにブロックを大きくする (仕様 2 の 2 の (a))。</summary>
    public const long LargeTarget = 64L * 1024 * 1024 * 1024;

    public const int MaxBlocks = 1 << 24;

    /// <summary>持つ領域の上限 (2^20 個。超えた分は件数だけを数える)。</summary>
    public const int MaxRegions = 1 << 20;

    /// <summary>持つ埋め込まれた形式の候補の上限。</summary>
    public const int MaxCandidates = 100_000;

    /// <summary>テンプレートで評価する埋め込まれた形式の数の上限。</summary>
    public const int MaxEvaluatedEmbedded = 256;

    /// <summary>ゼロ・定数の連続として境目を確定する最小の長さ (仕様 2 の 2 の (d))。</summary>
    public const int MinConstantRun = 512;

    /// <summary>概観の確度の上限 (仕様 2 の 1)。</summary>
    private const Confidence SampleConfidenceCap = Confidence.Medium;

    /// <summary>全体の走査のブロックの大きさ。</summary>
    public static int BlockSizeFor(long length) =>
        length <= LargeTarget ? 4096 : (int)Math.Min(ClassifyRequest.MaxBlockSize, StatMath.CeilPowerOfTwo((length + MaxBlocks - 1) / MaxBlocks, 4096));

    /// <summary>概観の区間の長さ (「対象の長さ / 4,096」以上で最小の 2 の累乗。最小 4 KiB)。</summary>
    public static long SampleIntervalFor(long length) => StatMath.CeilPowerOfTwo(Math.Max(1, (length + SampleIntervals - 1) / SampleIntervals), SampleBytes);

    /// <summary>ファイルマップを作る。</summary>
    public static FileMapResult Build(DocumentSnapshot snapshot, FileMapRequest request, LongRunningOperation? operation = null,
        FileMapCallbacks? callbacks = null, CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        var build = new Builder(snapshot, request, operation, callbacks, token);
        return build.Run();
    }

    /// <summary>1 回の作成の状態。</summary>
    private sealed partial class Builder
    {
        private readonly DocumentSnapshot _snapshot;
        private readonly FileMapRequest _request;
        private readonly LongRunningOperation? _operation;
        private readonly FileMapCallbacks? _callbacks;
        private readonly CancellationToken _token;
        private readonly long _start;
        private readonly long _length;
        private readonly int _blockSize;
        private readonly long _interval;
        private readonly Stopwatch _publishWatch = Stopwatch.StartNew();
        private FileMapStages _stages = FileMapStages.Initial;
        private long _sampleRead;
        private long _scanProcessed;

        // 概観
        private DataClass[] _sampleClasses = [];
        private float[] _sampleEntropy = [];
        private Confidence[] _sampleConfidence = [];
        private FileTypeCandidate? _fileType;

        // 全体の走査
        private ClassifyHooks? _hooks;
        private ClassificationResult? _classification;
        private readonly RunTracker _runs = new();
        private readonly EmbeddedScanner _embedded;

        public Builder(DocumentSnapshot snapshot, FileMapRequest request, LongRunningOperation? operation, FileMapCallbacks? callbacks, CancellationToken token)
        {
            _snapshot = snapshot;
            _request = request;
            _operation = operation;
            _callbacks = callbacks;
            _token = token;
            HashRange range = request.Range ?? new HashRange(0, snapshot.Length);
            _start = Math.Clamp(range.Offset, 0, snapshot.Length);
            _length = Math.Clamp(range.Length, 0, snapshot.Length - _start);
            _blockSize = BlockSizeFor(_length);
            _interval = SampleIntervalFor(_length);
            _embedded = new EmbeddedScanner(snapshot, request.Database, request.CarvingSignatures, _start, token);
        }

        private long End => _start + _length;

        public FileMapResult Run()
        {
            int samples = (int)((_length + _interval - 1) / _interval);
            _operation?.SetTotal((long)samples * Math.Min(SampleBytes, _interval) + _length);

            // ---- 1. 概観 (標本) ----
            Begin(FileMapStage.Sample);
            try
            {
                RunSample(samples);
                _stages = _stages with { Sample = FileMapStageState.Done };
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                _stages = _stages with { Sample = FileMapStageState.Cancelled };
                return Snapshot(scanned: 0, structure: null);
            }

            Publish(force: true, scanned: 0);

            // ---- 2. 全体の走査 ----
            Begin(FileMapStage.Scan);
            long scannedLogical = RunScan();
            if (_token.IsCancellationRequested || _classification is not { Completed: true })
            {
                _stages = _stages with { Scan = FileMapStageState.Cancelled, ScanFraction = _length == 0 ? 1 : (double)scannedLogical / _length };
                return Snapshot(scannedLogical, structure: null);
            }

            _stages = _stages with { Scan = FileMapStageState.Done, ScanFraction = 1 };
            Publish(force: true, scanned: _length);

            // ---- 3. 構造の解析 ----
            Begin(FileMapStage.Structure);
            var structure = new StructureStage(this);
            try
            {
                structure.Run();
                _stages = _stages with { Structure = FileMapStageState.Done };
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                _stages = _stages with { Structure = FileMapStageState.Cancelled };
            }

            return Snapshot(_length, structure);
        }

        private void Begin(FileMapStage stage)
        {
            _stages = stage switch
            {
                FileMapStage.Sample => _stages with { Sample = FileMapStageState.Running },
                FileMapStage.Scan => _stages with { Scan = FileMapStageState.Running },
                _ => _stages with { Structure = FileMapStageState.Running },
            };
            _callbacks?.StageStarted?.Invoke(stage);
        }

        private void Report() => _operation?.ReportQuietly(_sampleRead + _scanProcessed);

        // ---- 1. 概観 ----

        /// <summary>
        /// 対象を 4,096 個の区間に分け、各区間の中央の 4 KiB を ANA-16 の 2 の規則で分類する。あわせて対象の先頭でファイル形式を判定する
        /// (ANA-17 の 4。読むのは最大 128 KiB)。
        /// </summary>
        private void RunSample(int samples)
        {
            _sampleClasses = new DataClass[samples];
            _sampleEntropy = new float[samples];
            _sampleConfidence = new Confidence[samples];
            if (_length > 0)
            {
                _fileType = new FileTypeDetector(_request.Database).Detect(new HeadTailMagicData(_snapshot, _start), Path.GetExtension(_request.FileName ?? string.Empty)).Best;
            }

            var options = new ParallelOptions { CancellationToken = _token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) };
            Parallel.For(0, samples, options, () => new byte[SampleBytes], (i, _, buffer) =>
            {
                long s = _start + (i * _interval);
                long len = Math.Min(_interval, End - s);
                int n = (int)Math.Min(SampleBytes, len);
                long at = s + Math.Max(0, (len - n) / 2);
                ReadResult r = _snapshot.Read(at, buffer.AsSpan(0, n));
                DataClassifier.ClassifyBlock(buffer.AsSpan(0, n), r.IsComplete ? 0 : 1, _request.Thresholds, out DataClass c, out Half h, out Confidence conf);
                _sampleClasses[i] = c;
                _sampleEntropy[i] = c == DataClass.Unreadable ? float.NaN : (float)h;
                _sampleConfidence[i] = conf > SampleConfidenceCap ? SampleConfidenceCap : conf;
                Interlocked.Add(ref _sampleRead, n);
                if ((i & 63) == 0)
                {
                    Report();
                }

                return buffer;
            }, _ => { });

            // ANA-17 の仕様 7: 先頭の形式が圧縮されたデータの形式なら、高エントロピーの区間は「圧縮」にする。
            if (_start == 0 && _fileType is { } type && DataClassifier.IsCompressedFormat(type.Mime))
            {
                for (int i = 0; i < samples; i++)
                {
                    if (_sampleClasses[i] == DataClass.Encrypted)
                    {
                        _sampleClasses[i] = DataClass.Compressed;
                    }
                }
            }

            Report();
        }

        /// <summary>概観の領域 (<paramref name="from"/> より後ろの部分。全体の走査が済んだ範囲より後ろを「標本による推定」のまま残す)。</summary>
        private void AddSampleRegions(List<FileMapRegion> content, long from)
        {
            int i = 0;
            while (i < _sampleClasses.Length)
            {
                int j = i;
                double entropy = 0;
                int entropyCount = 0;
                int score = 0;
                while (j < _sampleClasses.Length && _sampleClasses[j] == _sampleClasses[i])
                {
                    if (!float.IsNaN(_sampleEntropy[j]))
                    {
                        entropy += _sampleEntropy[j];
                        entropyCount++;
                    }

                    score += (int)_sampleConfidence[j];
                    j++;
                }

                long s = _start + (i * _interval);
                long e = Math.Min(End, _start + (j * _interval));
                if (e > from && _sampleClasses[i] != DataClass.None)
                {
                    long os = Math.Max(s, from);
                    var conf = (Confidence)(int)Math.Round((double)score / (j - i));
                    content.Add(new FileMapRegion(os, e - os, KindOf(_sampleClasses[i]), conf, FileMapStage.Sample, -1, -1,
                        entropyCount > 0 ? (float)(entropy / entropyCount) : float.NaN, float.NaN, j - i));
                }

                i = j;
            }
        }

        // ---- 2. 全体の走査 ----

        /// <summary>
        /// 対象を先頭から 1 回だけ順に読み、ブロックの分類 (a)、埋め込まれた形式のシグネチャ (b)、圧縮形式のシグネチャと展開の確認 (c)、ゼロ・定数の
        /// 連続 (d) を同時に求める。走査した範囲の終わり (論理位置) を返す。
        /// </summary>
        private long RunScan()
        {
            var ranges = new[] { new HashRange(_start, _length) };
            void OnChunk(ScanChunk chunk)
            {
                _token.ThrowIfCancellationRequested();
                foreach ((int s, int l) in chunk.GoodParts())
                {
                    ReadOnlySpan<byte> part = chunk.Span.Slice(s, l);
                    long doc = chunk.Document + s;
                    _runs.Feed(doc, part);
                    _embedded.Feed(doc, part);
                }

                if (chunk.HasBad)
                {
                    _runs.Break();
                    _embedded.Break();
                }
            }

            ClassificationResult? existing = _request.Existing;
            if (existing is { Completed: true } && existing.BlockSize == _blockSize && existing.Ranges.Ranges.Count == 1
                && existing.Ranges.Start == _start && existing.Ranges.Length == _length)
            {
                // 仕様 5: 条件の合う ANA-16 の結果があれば、走査の (a) を省く (読み込みは (b)〜(d) のために行う)。
                _classification = existing;
                var scanner = new RangeScanner(_snapshot, new LogicalRanges(ranges), Math.Max(_request.ChunkSize, 4096), _token);
                try
                {
                    foreach (ScanChunk chunk in scanner.Read())
                    {
                        OnChunk(chunk);
                        _scanProcessed = chunk.Logical + chunk.Length;
                        Report();
                        Publish(force: false, scanned: _scanProcessed);
                    }
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested)
                {
                    return _scanProcessed;
                }

                return _length;
            }

            _hooks = new ClassifyHooks
            {
                ChunkSize = _request.ChunkSize,
                CollectPValues = true,
                OnChunk = OnChunk,
                Report = processed =>
                {
                    _scanProcessed = processed;
                    Report();
                    Publish(force: false, scanned: _hooks!.Classified);
                },
            };
            var request = new ClassifyRequest { Ranges = ranges, BlockSize = _blockSize, Thresholds = _request.Thresholds, FileType = _fileType };
            ClassificationResult result = DataClassifier.Classify(_snapshot, request, operation: null, _token, _hooks);
            _classification = result;
            return result.Completed ? _length : _hooks.Classified;
        }

        /// <summary>途中の結果を出す (間隔をあけて。<paramref name="force"/> ならすぐに)。</summary>
        private void Publish(bool force, long scanned)
        {
            if (_callbacks?.Partial is not { } partial || (!force && _publishWatch.Elapsed < _request.PublishInterval))
            {
                return;
            }

            _publishWatch.Restart();
            _stages = _stages with { ScanFraction = _length == 0 ? 1 : Math.Clamp((double)scanned / _length, 0, 1) };
            partial(Snapshot(scanned, structure: null));
        }

        /// <summary>
        /// 内容のレーンの領域: 走査が済んだ範囲 ([0, <paramref name="scanned"/>) の論理位置) はブロックの分類から、残りは概観から。
        /// </summary>
        private List<FileMapRegion> Content(long scanned)
        {
            var content = new List<FileMapRegion>();
            long scannedEnd = _start;
            if (scanned > 0 && _classification is { Completed: true } done && _hooks is null)
            {
                // 再利用した ANA-16 の結果 (ブロックごとのエントロピー・p 値は持っていない)。
                foreach (ClassRegion r in done.Regions)
                {
                    long end = Math.Min(r.Offset + r.Length, _start + scanned);
                    if (end > r.Offset)
                    {
                        content.Add(new FileMapRegion(r.Offset, end - r.Offset, KindOf(r.Class), r.Confidence, FileMapStage.Scan, -1, -1,
                            (float)r.MeanEntropy, float.NaN, -1));
                    }
                }

                scannedEnd = content.Count > 0 ? content[^1].End : _start;
            }
            else if (scanned > 0 && _hooks is { Classes: { } classes } hooks)
            {
                long blocks = Math.Min(classes.Length, (scanned + _blockSize - 1) / _blockSize);
                if (scanned < _length)
                {
                    // 途中のブロックは含めない。
                    blocks = Math.Min(blocks, scanned / _blockSize);
                }

                AddBlockRegions(content, hooks, (int)blocks);
                scannedEnd = content.Count > 0 ? content[^1].End : _start;
            }

            if (scannedEnd < End)
            {
                AddSampleRegions(content, scannedEnd);
            }

            return content;
        }

        /// <summary>ブロックの分類 [0, <paramref name="blocks"/>) を、同じ分類が続く部分ごとの領域にする。</summary>
        private void AddBlockRegions(List<FileMapRegion> content, ClassifyHooks hooks, int blocks)
        {
            DataClass[] classes = hooks.Classes!;
            Half[]? entropy = hooks.Entropy;
            byte[]? p = hooks.PValues;
            Confidence[]? conf = hooks.Confidence;
            int i = 0;
            while (i < blocks)
            {
                DataClass c = classes[i];
                int j = i;
                double sumH = 0;
                double sumP = 0;
                int countP = 0;
                int score = 0;
                while (j < blocks && classes[j] == c)
                {
                    sumH += entropy is null ? 0 : (double)entropy[j];
                    if (p is not null && !double.IsNaN(DataClassifier.LoadP(p[j])))
                    {
                        sumP += DataClassifier.LoadP(p[j]);
                        countP++;
                    }

                    score += conf is null ? 2 : (int)conf[j];
                    j++;
                }

                if (c != DataClass.None)
                {
                    long s = _start + ((long)i * _blockSize);
                    long e = Math.Min(End, _start + ((long)j * _blockSize));
                    int n = j - i;
                    content.Add(new FileMapRegion(s, e - s, KindOf(c), (Confidence)(int)Math.Round((double)score / n), FileMapStage.Scan, -1, -1,
                        c == DataClass.Unreadable || entropy is null ? float.NaN : (float)(sumH / n), countP > 0 ? (float)(sumP / countP) : float.NaN, -1));
                }

                i = j;
            }
        }

        /// <summary>今の状態の結果を作る。</summary>
        private FileMapResult Snapshot(long scanned, StructureStage? structure)
        {
            List<FileMapRegion> content = structure?.FinalContent ?? Content(scanned);
            List<FileMapRegion> structural = SortStructure(structure?.Regions ?? []);
            List<FileMapDetail> details = structure?.Details ?? [];
            var bytes = new long[Enum.GetValues<FileMapKind>().Length];
            foreach (FileMapRegion r in content)
            {
                bytes[(int)r.Kind] += r.Length;
            }

            foreach (FileMapRegion r in structural)
            {
                if (r.Parent < 0)
                {
                    bytes[(int)r.Kind] += r.Length;
                }
            }

            // 上限を超えた内容のレーンの領域は持たない (件数だけを数える)。
            long omitted = 0;
            int room = Math.Max(0, MaxRegions - structural.Count);
            if (content.Count > room)
            {
                omitted = content.Count - room;
                content.RemoveRange(room, content.Count - room);
            }

            FileMapRegion[] regions = Merge(content, structural);
            long scannedEnd = Math.Min(End, _start + scanned);
            return new FileMapResult(_snapshot, _start, _length, _blockSize, regions, details, _stages, bytes)
            {
                SampleInterval = _interval,
                ScannedEnd = scannedEnd,
                Classification = _classification is { Completed: true } c ? c : null,
                FileTypeName = _fileType?.Name,
                OmittedRegions = omitted,
                OmittedCandidates = _embedded.Omitted,
                EvaluatedEmbedded = structure?.EvaluatedEmbedded ?? 0,
            };
        }

        /// <summary>構造のレーンの領域をオフセットの順 (同じオフセットでは親が先) に並べ、親の番号を付け替える。</summary>
        private static List<FileMapRegion> SortStructure(List<FileMapRegion> regions)
        {
            int Depth(int i) => regions[i].Parent < 0 ? 0 : 1 + Depth(regions[i].Parent);
            int[] order = [.. Enumerable.Range(0, regions.Count).OrderBy(i => regions[i].Offset).ThenBy(Depth).ThenBy(i => i)];
            int[] position = new int[regions.Count];
            for (int k = 0; k < order.Length; k++)
            {
                position[order[k]] = k;
            }

            return [.. order.Select(i => regions[i] with { Parent = regions[i].Parent < 0 ? -1 : position[regions[i].Parent] })];
        }

        /// <summary>
        /// 内容のレーンと構造のレーンの領域を 1 つの並び (オフセットの順。同じオフセットでは構造のレーンが先、親が先) にする。
        /// 構造のレーンの親の番号を並びの番号に付け替える。
        /// </summary>
        private static FileMapRegion[] Merge(List<FileMapRegion> content, List<FileMapRegion> structural)
        {
            var result = new FileMapRegion[content.Count + structural.Count];
            int[] map = new int[structural.Count];
            int c = 0;
            int s = 0;
            int at = 0;
            while (c < content.Count || s < structural.Count)
            {
                if (s < structural.Count && (c >= content.Count || structural[s].Offset <= content[c].Offset))
                {
                    map[s] = at;
                    FileMapRegion r = structural[s];
                    result[at++] = r with { Parent = r.Parent < 0 ? -1 : map[r.Parent] };
                    s++;
                }
                else
                {
                    result[at++] = content[c++];
                }
            }

            return result;
        }

        internal static FileMapKind KindOf(DataClass c) => c switch
        {
            DataClass.Zero => FileMapKind.Zero,
            DataClass.Constant => FileMapKind.Constant,
            DataClass.Text => FileMapKind.Text,
            DataClass.Encrypted => FileMapKind.Encrypted,
            DataClass.Compressed => FileMapKind.Compressed,
            DataClass.Unreadable => FileMapKind.Unreadable,
            _ => FileMapKind.Binary,
        };
    }
}
