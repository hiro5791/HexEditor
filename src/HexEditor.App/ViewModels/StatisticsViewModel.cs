using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Settings;
using HexEditor.Core.Statistics;
using HexEditor.Core.View;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.ViewModels;

/// <summary>統計パネルの対象範囲の選択欄 (06 の 0.1)。</summary>
public enum StatsTargetKind
{
    Selection,
    MultiSelection,
    WholeDocument,
    Custom,
}

/// <summary>統計パネルのタブ (ANA-10 の「画面」)。</summary>
public enum StatsTab
{
    Histogram,
    Descriptive,
    Entropy,
    Digram,
    Pattern,
    Classify,
}

/// <summary>
/// 統計パネル (ANA-10〜ANA-16)。ウィンドウごとに 1 つで、パネルの中身 (浮動パネルとの間を移るたびに作り直す) はこれを共有する。
/// 計算は Core の <see cref="StatisticsEngine"/> などで、処理センターに読み取りのみの処理として登録する (UI スレッドを止めない)。
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject
{
    public const string AutoKey = "statistics.autoRecompute";

    /// <summary>対象範囲が変わってから自動で計算するまでの時間 (ANA-10 の仕様 9)。</summary>
    public static readonly TimeSpan AutoDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>エントロピーグラフのブロックの大きさの選択肢 (0 は「自動」。256 バイト〜64 MB の 2 の累乗。ANA-13 の仕様 1)。</summary>
    public static readonly IReadOnlyList<long> BlockSizes = [0, .. Enumerable.Range(8, 19).Select(i => 1L << i)];

    private readonly OperationCenter _operations;
    private readonly SettingsStore? _settings;
    private readonly DispatcherQueue? _queue;
    private readonly DispatcherQueueTimer? _timer;
    private DocumentViewModel? _target;
    private LongRunningOperation? _operation;
    private LongRunningOperation? _compression;
    private DocumentSnapshot? _computedSnapshot;
    private DocumentSnapshot? _seenSnapshot;
    private IReadOnlyList<HashRange> _requestedRanges = [];
    private bool _targetChosen;
    private (int Bin, long Offset)? _lastBinJump;

    public StatisticsViewModel(OperationCenter operations, SettingsStore? settings)
    {
        _operations = operations;
        _settings = settings;
        _queue = DispatcherQueue.GetForCurrentThread();
        if (_queue is not null)
        {
            _timer = _queue.CreateTimer();
            _timer.Interval = AutoDelay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => OnAutoTimer();
        }

        AutoRecompute = _settings?.GetBool(AutoKey, true) ?? true;
        foreach (ElementType type in ElementTypes.All)
        {
            TypeNames.Add(ElementTypes.Name(type));
        }

        foreach (long size in BlockSizes)
        {
            BlockSizeNames.Add(size == 0 ? Loc.Get("Stats_BlockSize_Auto") : StatisticsFormat.Size(size, CultureInfo.CurrentCulture));
        }

        InitializeClassify();
    }

    // ---- 外とのつなぎ (MainWindow が設定する) ----

    /// <summary>テキストをクリップボードに入れる。</summary>
    public Action<string>? SetClipboardText { get; set; }

    /// <summary>保存先を選ぶ (提案するファイル名、拡張子。null はキャンセル)。</summary>
    public Func<string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>ドキュメント上の位置へ移動する (ジャンプとして履歴に残す)。</summary>
    public Action<long>? GoTo { get; set; }

    /// <summary>ドキュメント上の範囲を選択する。</summary>
    public Action<long, long>? Select { get; set; }

    /// <summary>Hex のバイト列を検索バー (04) に入れて次を検索する。</summary>
    public Func<byte[], Task>? SearchNext { get; set; }

    /// <summary>Hex のバイト列で 04 のすべて検索を実行する。</summary>
    public Func<byte[], Task>? FindAll { get; set; }

    /// <summary>その長さでレコード表示にする (02 の VIEW-18、F2-16)。null なら「この長さでレコード表示」を無効にする。</summary>
    public Action<long>? ShowRecordView { get; set; }

    /// <summary>03 の展開機能 (F4-04) を開始オフセットと形式を指定して開く。null なら「ここから展開...」を無効にする (フェーズ 4)。</summary>
    public Func<long, string, Task>? OpenDecompress { get; set; }

    /// <summary>ドキュメントのエンディアン (02 の VIEW-11)。null ならリトルエンディアンを既定にする。</summary>
    public Func<DocumentViewModel, bool>? DocumentBigEndian { get; set; }

    /// <summary>マルチ選択の範囲 (03 の EDIT-07)。2 つ以上あれば「マルチ選択」を既定にする。null ならマルチ選択はない。</summary>
    public Func<EditorState, IReadOnlyList<HashRange>>? MultiSelectionOf { get; set; }

    /// <summary>共通の注釈レイヤー (INSP-32) に分類の区間を渡す口。null なら渡さない。</summary>
    public IAnnotationSink? Annotations { get; set; }

    // ---- 対象範囲と要素の設定 ----

    public ObservableCollection<string> TypeNames { get; } = [];

    public ObservableCollection<string> BlockSizeNames { get; } = [];

    [ObservableProperty]
    public partial StatsTab Tab { get; set; }

    [ObservableProperty]
    public partial StatsTargetKind TargetKind { get; set; } = StatsTargetKind.WholeDocument;

    [ObservableProperty]
    public partial string CustomStart { get; set; } = "0";

    [ObservableProperty]
    public partial string CustomLength { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CustomUsesEnd { get; set; }

    /// <summary>実際の開始・終了 (このバイトを含む)・長さ (0.1)。</summary>
    [ObservableProperty]
    public partial string RangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRangeError { get; set; }

    /// <summary>要素の型 (<see cref="ElementType"/> の番号)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiByte), nameof(CanChooseBins), nameof(CanPerValue))]
    public partial int TypeIndex { get; set; }

    [ObservableProperty]
    public partial bool BigEndian { get; set; }

    /// <summary>要素の間隔 (空は型のサイズ。1〜65,536)。</summary>
    [ObservableProperty]
    public partial string Stride { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double BinCount { get; set; } = BinSpec.DefaultCount;

    /// <summary>範囲を「自動」(最小値〜最大値) にする。false なら <see cref="BinMin"/>〜<see cref="BinMax"/>。</summary>
    [ObservableProperty]
    public partial bool BinAuto { get; set; } = true;

    [ObservableProperty]
    public partial string BinMin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BinMax { get; set; } = string.Empty;

    /// <summary>u16 / s16 の「値ごと (65,536 個)」。</summary>
    [ObservableProperty]
    public partial bool PerValue { get; set; }

    [ObservableProperty]
    public partial bool AutoRecompute { get; set; } = true;

    public ElementType Type => (ElementType)Math.Clamp(TypeIndex, 0, ElementTypes.All.Count - 1);

    /// <summary>2 バイト以上の型 (エンディアンとストライドを指定する)。</summary>
    public bool IsMultiByte => ElementTypes.Size(Type) > 1;

    /// <summary>ビンの数と範囲を選べる型 (u8 / s8 は 256 個に固定)。</summary>
    public bool CanChooseBins => IsMultiByte;

    public bool CanPerValue => Type is ElementType.U16 or ElementType.S16;

    // ---- 計算の状態 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsComputing { get; set; }

    public bool IsIdle => !IsComputing;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>計算後にドキュメントが編集された (0.2 の 3:「結果は編集前の内容のものです」と「再計算」)。</summary>
    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>対象が 64 MB を超えるため自動で計算しなかった (「再計算」ボタンを強調表示する。ANA-10 の仕様 9)。</summary>
    [ObservableProperty]
    public partial bool ComputeHighlighted { get; set; }

    /// <summary>最新の結果 (途中経過を含む)。</summary>
    [ObservableProperty]
    public partial StatisticsResult? Result { get; set; }

    /// <summary>端数・読めなかった範囲・途中で中止の表示。</summary>
    [ObservableProperty]
    public partial string ResultNote { get; set; } = string.Empty;

    /// <summary>変換ビュー (EDIT-45) の表示 (0.1.1)。変換ビューができるまでは空。</summary>
    public string TransformNote => string.Empty;

    /// <summary>計算の対象のドキュメント (選んでいるタブ)。</summary>
    public DocumentViewModel? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value))
            {
                return;
            }

            if (_target is not null)
            {
                _target.Editor.Changed -= Editor_Changed;
                _target.Document.Changed -= Document_Changed;
            }

            CancelComputation();
            _target = value;
            _targetChosen = false;
            ClearResults();
            if (_target is not null)
            {
                _target.Editor.Changed += Editor_Changed;
                _target.Document.Changed += Document_Changed;
                if (DocumentBigEndian?.Invoke(_target) is bool big)
                {
                    BigEndian = big;
                }
            }

            OnPropertyChanged();
            UpdateTarget(scheduleAuto: true);
        }
    }

    /// <summary>計算した対象 (結果の論理位置をドキュメントの位置にする)。</summary>
    public LogicalRanges? Ranges => Result?.Ranges;

    /// <summary>対象範囲 (ドキュメントの範囲)。指定が正しくなければ null。</summary>
    public IReadOnlyList<HashRange>? ResolveRanges()
    {
        if (_target is not { } doc)
        {
            return null;
        }

        EditorState editor = doc.Editor;
        long length = doc.Document.Length;
        switch (TargetKind)
        {
            case StatsTargetKind.MultiSelection when MultiSelectionOf?.Invoke(editor) is { Count: > 0 } multi:
                return multi;
            case StatsTargetKind.Selection or StatsTargetKind.MultiSelection when editor.HasSelection:
                return [new HashRange(editor.SelectionStart, editor.SelectionLength)];
            case StatsTargetKind.Selection or StatsTargetKind.MultiSelection or StatsTargetKind.WholeDocument:
                return [new HashRange(0, length)];
            default:
                var context = new EditorExpressionContext(editor);
                if (!ExpressionEvaluator.TryEvaluate(CustomStart, context, out long start, out _) || start < 0 || start > length)
                {
                    return null;
                }

                long count = length - start;
                if (CustomLength.Trim().Length > 0)
                {
                    if (!ExpressionEvaluator.TryEvaluate(CustomLength, context, out long value, out _))
                    {
                        return null;
                    }

                    count = CustomUsesEnd ? value - start + 1 : value;
                    if (count < 0 || (CustomUsesEnd && value >= length) || start + count > length)
                    {
                        return null;
                    }
                }

                return [new HashRange(start, count)];
        }
    }

    /// <summary>ユーザーが対象範囲の選択欄を選んだ (以後、選択範囲の有無で既定を切り替えない)。</summary>
    public void ChooseTarget(StatsTargetKind kind)
    {
        _targetChosen = true;
        TargetKind = kind;
    }

    /// <summary>計算の指定 (現在の設定から)。指定が正しくなければ null と理由。</summary>
    public (StatisticsRequest? Request, string? Error) BuildRequest()
    {
        if (ResolveRanges() is not { } ranges)
        {
            return (null, Loc.Get("Stats_RangeInvalid"));
        }

        int stride = 0;
        if (IsMultiByte && Stride.Trim().Length > 0)
        {
            if (!ExpressionEvaluator.TryEvaluate(Stride, new EditorExpressionContext(_target!.Editor), out long s, out _) || s < 1 || s > ElementSpec.MaxStride)
            {
                return (null, Loc.Get("Stats_StrideInvalid"));
            }

            stride = (int)s;
        }

        var bins = new BinSpec { Count = (int)Math.Clamp(double.IsNaN(BinCount) ? BinSpec.DefaultCount : BinCount, 2, BinSpec.MaxCount), PerValue = PerValue && CanPerValue };
        if (!BinAuto && CanChooseBins && !bins.PerValue)
        {
            if (!double.TryParse(BinMin, NumberStyles.Float, CultureInfo.CurrentCulture, out double min)
                || !double.TryParse(BinMax, NumberStyles.Float, CultureInfo.CurrentCulture, out double max) || max < min)
            {
                return (null, Loc.Get("Stats_BinRangeInvalid"));
            }

            bins = bins with { Min = min, Max = max };
        }

        return (new StatisticsRequest
        {
            Ranges = ranges,
            Element = new ElementSpec(Type, BigEndian, stride),
            Bins = bins,
            BlockSize = BlockSizes[Math.Clamp(BlockSizeIndex, 0, BlockSizes.Count - 1)],
        }, null);
    }

    /// <summary>計算する (「再計算」ボタン)。試し圧縮 (ANA-12 の仕様 3) は別の長時間処理として並行して行う。</summary>
    [RelayCommand]
    public async Task ComputeAsync()
    {
        if (_target is not { } doc)
        {
            return;
        }

        _timer?.Stop();
        CancelComputation();
        (StatisticsRequest? request, string? error) = BuildRequest();
        if (request is null)
        {
            StatusText = error ?? string.Empty;
            return;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        _requestedRanges = request.Ranges;
        ComputeHighlighted = false;
        StatusText = string.Empty;
        IsStale = false;
        Progress = 0;
        IsComputing = true;
        ClearDetail();
        if (EstimateCompression)
        {
            _ = EstimateCompressionAsync(doc, snapshot, request.Ranges);
        }

        LongRunningOperation? mine = null;
        try
        {
            StatisticsResult result = await _operations.RunAsync(Loc.Get("Operation_Statistics"), OperationKind.ReadOnly, doc.Document,
                null, op =>
                {
                    mine = op;
                    _operation = op;
                    op.ProgressChanged += (_, _) => _queue?.TryEnqueue(() => ShowProgress(op));
                    return Task.FromResult(StatisticsEngine.Compute(snapshot, request, op,
                        partial => _queue?.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(_operation, op) && ReferenceEquals(_target, doc))
                            {
                                ApplyResult(partial);
                            }
                        })));
                });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            _computedSnapshot = snapshot;
            ApplyResult(result);
            IsStale = !ReferenceEquals(doc.Document.Current, snapshot);
            if (result.Blocks is { } blocks && result.Ranges.IsWhole(snapshot.Length))
            {
                // ミニマップ (VIEW-35) と共有する (ANA-13 の仕様 8)。
                EntropyCache.For(doc.Document).Publish(snapshot, blocks);
            }
        }
        catch (StatisticsCancelledException e)
        {
            // キャンセルした場合は、そこまでの結果を「途中で中止 (xx% まで)」と表示して残す (ANA-10)。
            if (ReferenceEquals(_target, doc) && (ReferenceEquals(_operation, mine) || mine is null))
            {
                ApplyResult(e.Partial, aborted: true);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Get("Stats_Cancelled");
        }
        finally
        {
            if (ReferenceEquals(_operation, mine))
            {
                _operation = null;
                IsComputing = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsComputing))]
    public void Cancel() => CancelComputation();

    private void CancelComputation()
    {
        _operation?.Cancel();
        _compression?.Cancel();
        CancelDetail();
    }

    private void ShowProgress(LongRunningOperation op)
    {
        if (ReferenceEquals(op, _operation))
        {
            Progress = op.Fraction ?? 0;
        }
    }

    /// <summary>結果を各タブの表示に反映する。</summary>
    private void ApplyResult(StatisticsResult result, bool aborted = false)
    {
        Result = result;
        var notes = new List<string>();
        if (aborted)
        {
            notes.Add(Loc.Format("Stats_Aborted", (result.Fraction * 100).ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (result.Remainder > 0)
        {
            notes.Add(Loc.Format("Stats_Remainder", result.Remainder.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (result.Unreadable.Count > 0)
        {
            notes.Add(Loc.Format("Stats_Unreadable", result.Unreadable.Count.ToString("N0", CultureInfo.CurrentCulture),
                result.Unreadable.Bytes.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (result.Completed && result.Elements == 0)
        {
            notes.Add(Loc.Get("Stats_NoElements"));
        }

        ResultNote = string.Join("  ", notes);
        RebuildHistogram();
        RebuildDescriptive();
        RebuildEntropy();
        RebuildDigram();
    }

    private void ClearResults()
    {
        Result = null;
        ResultNote = string.Empty;
        IsStale = false;
        StatusText = string.Empty;
        _computedSnapshot = null;
        _requestedRanges = [];
        _seenSnapshot = null;
        Compression = null;
        CompressionText = string.Empty;
        ClearDetail();
        RebuildHistogram();
        RebuildDescriptive();
        RebuildEntropy();
        RebuildDigram();
        ClearPatterns();
        ClearClassification();
    }

    partial void OnTargetKindChanged(StatsTargetKind value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomStartChanged(string value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomLengthChanged(string value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomUsesEndChanged(bool value) => UpdateTarget(scheduleAuto: true);

    partial void OnTypeIndexChanged(int value) => SettingsChanged();

    partial void OnBigEndianChanged(bool value) => SettingsChanged();

    partial void OnStrideChanged(string value) => SettingsChanged();

    partial void OnBinCountChanged(double value) => SettingsChanged();

    partial void OnBinAutoChanged(bool value) => SettingsChanged();

    partial void OnBinMinChanged(string value) => SettingsChanged();

    partial void OnBinMaxChanged(string value) => SettingsChanged();

    partial void OnPerValueChanged(bool value) => SettingsChanged();

    partial void OnAutoRecomputeChanged(bool value) => _settings?.SetBool(AutoKey, value, true);

    /// <summary>要素・ビンの設定が変わった: 結果があれば自動で計算し直す (64 MB 以下のとき)。</summary>
    private void SettingsChanged()
    {
        if (Result is null || !AutoRecompute || ResolveRanges() is not { } ranges)
        {
            return;
        }

        _requestedRanges = [];
        ScheduleAuto(ranges);
    }

    private void Editor_Changed(object? sender, EventArgs e)
    {
        UpdateTarget(scheduleAuto: true);
        OnPropertyChanged(nameof(CursorLogical));
    }

    private void Document_Changed(object? sender, DocumentChangedEventArgs e)
    {
        if (_computedSnapshot is not null && Result is not null && _target is { } doc)
        {
            IsStale = !ReferenceEquals(doc.Document.Current, _computedSnapshot);
        }

        if (_classifiedSnapshot is not null && Classification is not null && _target is { } d)
        {
            ClassificationStale = !ReferenceEquals(d.Document.Current, _classifiedSnapshot);
        }

        if (_patternSnapshot is not null && _target is { } p)
        {
            PatternStale = !ReferenceEquals(p.Document.Current, _patternSnapshot);
        }

        UpdateTarget(scheduleAuto: false);
    }

    /// <summary>カーソル位置 (計算した対象の論理位置。範囲外なら null)。エントロピーグラフの縦線 (ANA-13 の仕様 6)。</summary>
    public long? CursorLogical => _target is { } doc ? Ranges?.ToLogical(doc.Editor.Cursor) : null;

    /// <summary>対象範囲の表示を更新し、範囲が変わったら自動で計算する準備をする。</summary>
    private void UpdateTarget(bool scheduleAuto)
    {
        if (_target is not { } doc)
        {
            RangeText = string.Empty;
            return;
        }

        // 選択範囲の有無で既定を切り替える (0.1 の「既定になる条件」)。利用者が選んだ後は変えない。
        if (!_targetChosen && TargetKind != StatsTargetKind.Custom)
        {
            StatsTargetKind auto = MultiSelectionOf?.Invoke(doc.Editor) is { Count: >= 2 } ? StatsTargetKind.MultiSelection
                : doc.Editor.HasSelection ? StatsTargetKind.Selection : StatsTargetKind.WholeDocument;
            if (TargetKind != auto)
            {
                TargetKind = auto;
                return;
            }
        }

        IReadOnlyList<HashRange>? ranges = ResolveRanges();
        IsRangeError = ranges is null;
        if (ranges is null)
        {
            RangeText = Loc.Get("Stats_RangeInvalid");
            return;
        }

        IReadOnlyList<HashRange> normalized = HashEngine.Normalize(ranges, doc.Document.Length);
        long total = normalized.Sum(r => r.Length);
        RangeText = total == 0
            ? Loc.Format("Hash_RangeEmpty", Hex(normalized.Count > 0 ? normalized[0].Offset : 0))
            : Loc.Format("Hash_Range", Hex(normalized[0].Offset), Hex(normalized[^1].End - 1), total.ToString("N0", CultureInfo.CurrentCulture));
        bool edited = _seenSnapshot is not null && !ReferenceEquals(doc.Document.Current, _seenSnapshot);
        _seenSnapshot = doc.Document.Current;
        if (!scheduleAuto || edited || _requestedRanges.SequenceEqual(ranges) || !AutoRecompute)
        {
            return;
        }

        ScheduleAuto(ranges);
    }

    private void ScheduleAuto(IReadOnlyList<HashRange> ranges)
    {
        // 対象が 64 MB を超える場合は自動で計算せず、「再計算」ボタンを強調表示する (ANA-10 の仕様 9)。
        _timer?.Stop();
        if (ranges.Sum(x => x.Length) > StatisticsEngine.AutoComputeLimit)
        {
            _requestedRanges = ranges;
            ComputeHighlighted = true;
            return;
        }

        ComputeHighlighted = false;
        _timer?.Start();
    }

    private void OnAutoTimer()
    {
        if (AutoRecompute && ResolveRanges() is { } ranges && ranges.Sum(x => x.Length) <= StatisticsEngine.AutoComputeLimit)
        {
            _ = ComputeAsync();
        }
    }

    // ---- 書き出し・コピー (ANA-10 の仕様 10) ----

    public async Task ExportAsync(bool json)
    {
        if (Result is not { } r || PickSavePath is null)
        {
            return;
        }

        string extension = json ? ".json" : ".csv";
        if (await PickSavePath("histogram" + extension, extension) is not { } path)
        {
            return;
        }

        try
        {
            string content = json ? StatisticsExport.Json(r) : StatisticsExport.Csv(r);
            await File.WriteAllTextAsync(path, content);
            StatusText = Loc.Format("Stats_Saved", Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.Format("Stats_SaveFailed", e.Message);
        }
    }

    internal string FormatHex(long value) => Hex(value);

    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>テスト用の状態 (ビンの値の次の出現位置への移動の記録など)。</summary>
    internal (int Bin, long Offset)? LastBinJump => _lastBinJump;
}
