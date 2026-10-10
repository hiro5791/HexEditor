using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Statistics;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>統計パネルのタブ: パターン統計 (ANA-15) と分類 (ANA-16)。どちらも「計算」ボタンで始める長時間処理。</summary>
public sealed partial class StatisticsViewModel
{
    public const string ThresholdPrefix = "analysis.classify.";

    /// <summary>分類のブロックの大きさの選択肢 (512 バイト〜1 MB の 2 の累乗。既定 4 KB。ANA-16 の仕様 1)。</summary>
    public static readonly IReadOnlyList<int> ClassBlockSizes = [.. Enumerable.Range(9, 12).Select(i => 1 << i)];

    // ---- パターン統計 (ANA-15) ----

    [ObservableProperty]
    public partial double NGramLength { get; set; } = 4;

    [ObservableProperty]
    public partial double TopCount { get; set; } = 100;

    [ObservableProperty]
    public partial double MinCount { get; set; } = 2;

    /// <summary>位置の条件「オフセット mod a = b」(空なら条件なし)。</summary>
    [ObservableProperty]
    public partial string AlignModulus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AlignRemainder { get; set; } = "0";

    [ObservableProperty]
    public partial bool ExcludeUniform { get; set; } = true;

    /// <summary>0 = よく現れるバイト列、1 = 周期。</summary>
    [ObservableProperty]
    public partial int PatternView { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatternIdle))]
    public partial bool IsPatternComputing { get; set; }

    public bool IsPatternIdle => !IsPatternComputing;

    [ObservableProperty]
    public partial string PatternStatus { get; set; } = string.Empty;

    /// <summary>計算後にドキュメントが編集された (06 の 0.2 の 3:「結果は編集前の内容のものです」。「計算」で計算し直す)。</summary>
    [ObservableProperty]
    public partial bool PatternStale { get; set; }

    private DocumentSnapshot? _patternSnapshot;

    public ObservableCollection<NGramRowViewModel> NGramRows { get; } = [];

    public ObservableCollection<PeriodRowViewModel> PeriodRows { get; } = [];

    /// <summary>計算中の作業用メモリ (テスト用の状態表示。TC-ANA-15-02)。</summary>
    public long PatternWorkingBytes => PatternStatistics.CurrentWorkingBytes;

    private LongRunningOperation? _patternOperation;

    private void ClearPatterns()
    {
        _patternOperation?.Cancel();
        NGramRows.Clear();
        PeriodRows.Clear();
        PatternStatus = string.Empty;
        PatternStale = false;
        _patternSnapshot = null;
    }

    /// <summary>よく現れるバイト列と周期を求める。キャンセルした場合は結果を破棄する (ANA-15)。</summary>
    public async Task ComputePatternsAsync()
    {
        if (_target is not { } doc || ResolveRanges() is not { } ranges)
        {
            PatternStatus = Loc.Get("Stats_RangeInvalid");
            return;
        }

        long modulus = 0;
        long remainder = 0;
        var context = new EditorExpressionContext(doc.Editor);
        if (AlignModulus.Trim().Length > 0)
        {
            if (!ExpressionEvaluator.TryEvaluate(AlignModulus, context, out modulus, out _) || modulus < 1
                || !ExpressionEvaluator.TryEvaluate(AlignRemainder, context, out remainder, out _) || remainder < 0 || remainder >= modulus)
            {
                PatternStatus = Loc.Get("Stats_AlignInvalid");
                return;
            }
        }

        _patternOperation?.Cancel();
        var request = new PatternRequest
        {
            Ranges = ranges,
            Length = (int)Math.Clamp(Value(NGramLength, 4), PatternRequest.MinLength, PatternRequest.MaxLength),
            Top = (int)Math.Clamp(Value(TopCount, 100), PatternRequest.MinTop, PatternRequest.MaxTop),
            MinCount = (long)Math.Max(2, Value(MinCount, 2)),
            AlignModulus = modulus,
            AlignRemainder = remainder,
            ExcludeUniform = ExcludeUniform,
        };
        DocumentSnapshot snapshot = doc.Document.Current;
        NGramRows.Clear();
        PeriodRows.Clear();
        PatternStatus = string.Empty;
        IsPatternComputing = true;
        try
        {
            (PatternResult result, IReadOnlyList<PeriodCandidate> periods) = await _operations.RunAsync(Loc.Get("Operation_Patterns"),
                OperationKind.ReadOnly, doc.Document, null, op =>
                {
                    _patternOperation = op;
                    PatternResult r = PatternStatistics.FindFrequent(snapshot, request, op);
                    IReadOnlyList<PeriodCandidate> p = PatternStatistics.EstimatePeriods(snapshot, ranges, operation: op);
                    return Task.FromResult((r, p));
                });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            for (int i = 0; i < result.Rows.Count; i++)
            {
                NGramRows.Add(new NGramRowViewModel(i + 1, result.Rows[i], result.Positions));
            }

            for (int i = 0; i < periods.Count; i++)
            {
                PeriodRows.Add(new PeriodRowViewModel(i + 1, periods[i]));
            }

            _patternSnapshot = snapshot;
            PatternStale = !ReferenceEquals(doc.Document.Current, snapshot);

            PatternStatus = result.Rows.Count == 0 ? Loc.Get("Stats_NoPatterns") : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 部分的な上位一覧は誤解を招くため破棄する (ANA-15 の「巨大ファイル・長時間処理」)。
            PatternStatus = Loc.Get("Stats_PatternsCancelled");
        }
        finally
        {
            IsPatternComputing = false;
        }
    }

    public void CancelPatterns() => _patternOperation?.Cancel();

    private static double Value(double v, double fallback) => double.IsNaN(v) ? fallback : v;

    /// <summary>行のダブルクリック: 最初の出現位置へ (ANA-15 の仕様 2)。</summary>
    public void GoToNGram(NGramRowViewModel row) => GoTo?.Invoke(row.Row.FirstOffset);

    public Task FindAllNGramAsync(NGramRowViewModel row) => FindAll?.Invoke(row.Row.Bytes) ?? Task.CompletedTask;

    /// <summary>周期の行から「この長さでレコード表示」(ANA-15 の仕様 3)。</summary>
    public bool CanShowRecords => ShowRecordView is not null;

    public void ShowRecords(PeriodRowViewModel row) => ShowRecordView?.Invoke(row.Candidate.Period);

    // ---- 暗号化・圧縮データの検出 (ANA-16) ----

    [ObservableProperty]
    public partial int ClassBlockSizeIndex { get; set; } = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClassifyIdle))]
    public partial bool IsClassifying { get; set; }

    public bool IsClassifyIdle => !IsClassifying;

    [ObservableProperty]
    public partial ClassificationResult? Classification { get; set; }

    [ObservableProperty]
    public partial bool ClassificationStale { get; set; }

    [ObservableProperty]
    public partial string ClassifyStatus { get; set; } = string.Empty;

    /// <summary>分類に使ったファイル形式 (ANA-17 の仕様 7)。使わなかったら空。</summary>
    [ObservableProperty]
    public partial string ClassifyFileTypeNote { get; set; } = string.Empty;

    /// <summary>ドキュメントのファイル形式の判定の結果 (ANA-17。ウィンドウが設定する)。分類に渡す (ANA-17 の仕様 7)。</summary>
    public Func<Document, HexEditor.Core.FileTypes.FileTypeCandidate?>? FileTypeOf { get; init; }

    public ObservableCollection<string> ClassBlockSizeNames { get; } = [];

    public ObservableCollection<ClassRowViewModel> ClassRows { get; } = [];

    public ObservableCollection<LegendItemViewModel> Legend { get; } = [];

    private LongRunningOperation? _classifyOperation;
    private DocumentSnapshot? _classifiedSnapshot;

    /// <summary>分類ごとの模様のリソース名 (色が見えなくても区別できる。ANA-16 の受け入れ基準 3)。</summary>
    public static string PatternOf(DataClass c) => c switch
    {
        DataClass.Zero => "Pattern_Empty",
        DataClass.Constant => "Pattern_Horizontal",
        DataClass.Text => "Pattern_Dots",
        DataClass.Encrypted => "Pattern_Cross",
        DataClass.Compressed => "Pattern_Diagonal",
        DataClass.Binary => "Pattern_Vertical",
        DataClass.Unreadable => "Pattern_Checker",
        _ => "Pattern_None",
    };

    public static string ClassName(DataClass c) => Loc.Get("Stats_Class_" + c);

    private void InitializeClassify()
    {
        foreach (int size in ClassBlockSizes)
        {
            ClassBlockSizeNames.Add(StatisticsFormat.Size(size, CultureInfo.CurrentCulture));
        }
    }

    private void ClearClassification()
    {
        _classifyOperation?.Cancel();
        Classification = null;
        ClassificationStale = false;
        ClassRows.Clear();
        Legend.Clear();
        ClassifyStatus = string.Empty;
        ClassifyFileTypeNote = string.Empty;
    }

    /// <summary>詳細設定のしきい値 (ANA-16 の仕様 6)。</summary>
    private ClassThresholds Thresholds()
    {
        var t = new ClassThresholds();
        if (_settings is null)
        {
            return t;
        }

        return new ClassThresholds
        {
            ConstantShare = _settings.GetDouble(ThresholdPrefix + "constantShare", t.ConstantShare),
            TextShare = _settings.GetDouble(ThresholdPrefix + "textShare", t.TextShare),
            EncryptedEntropy = _settings.GetDouble(ThresholdPrefix + "encryptedEntropy", t.EncryptedEntropy),
            PValueLow = _settings.GetDouble(ThresholdPrefix + "pValueLow", t.PValueLow),
            PValueHigh = _settings.GetDouble(ThresholdPrefix + "pValueHigh", t.PValueHigh),
            CompressedEntropy = _settings.GetDouble(ThresholdPrefix + "compressedEntropy", t.CompressedEntropy),
        };
    }

    /// <summary>分類する (解析 > 暗号化・圧縮データの検出)。キャンセルした場合は処理済みの範囲の結果を残す (ANA-16)。</summary>
    public async Task ClassifyAsync()
    {
        if (_target is not { } doc || ResolveRanges() is not { } ranges)
        {
            ClassifyStatus = Loc.Get("Stats_RangeInvalid");
            return;
        }

        _classifyOperation?.Cancel();
        var request = new ClassifyRequest
        {
            Ranges = ranges,
            BlockSize = ClassBlockSizes[Math.Clamp(ClassBlockSizeIndex, 0, ClassBlockSizes.Count - 1)],
            Thresholds = Thresholds(),
            FileType = FileTypeOf?.Invoke(doc.Document),
        };
        DocumentSnapshot snapshot = doc.Document.Current;
        IsClassifying = true;
        ClassifyStatus = string.Empty;
        try
        {
            ClassificationResult result = await _operations.RunAsync(Loc.Get("Operation_Classify"), OperationKind.ReadOnly, doc.Document, null, op =>
            {
                _classifyOperation = op;
                return Task.FromResult(DataClassifier.Classify(snapshot, request, op));
            });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            _classifiedSnapshot = snapshot;
            ClassificationStale = !ReferenceEquals(doc.Document.Current, snapshot);
            ApplyClassification(result);
            if (result.Ranges.IsWhole(snapshot.Length))
            {
                // ミニマップの「分類」レイヤ (ANA-16 の仕様 7、VIEW-35) が読む。
                DataClassifier.Remember(doc.Document, result);
            }
        }
        catch (OperationCanceledException)
        {
            ClassifyStatus = Loc.Get("Stats_Cancelled");
        }
        finally
        {
            IsClassifying = false;
        }
    }

    public void CancelClassify() => _classifyOperation?.Cancel();

    private void ApplyClassification(ClassificationResult result)
    {
        Classification = result;
        ClassRows.Clear();
        Legend.Clear();
        CultureInfo culture = CultureInfo.CurrentCulture;
        foreach (ClassRegion region in result.Regions)
        {
            ClassRows.Add(new ClassRowViewModel(region.Offset, region.Length, ClassName(region.Class), region.MeanEntropy.ToString("N2", culture),
                Loc.Get("Stats_Confidence_" + region.Confidence), dataClass: region.Class));
        }

        foreach (SignatureHit hit in result.Signatures)
        {
            ClassRows.Add(new ClassRowViewModel(hit.Offset, hit.StreamLength ?? -1, Loc.Format("Stats_SignatureKind", hit.Format), "—",
                Loc.Get("Stats_Confidence_" + hit.Confidence), hit));
        }

        long total = Math.Max(1, result.Ranges.Length);
        foreach (DataClass c in Enum.GetValues<DataClass>().Where(c => c != DataClass.None))
        {
            long bytes = result.BytesOf(c);
            Legend.Add(new LegendItemViewModel(c, ClassName(c), PatternOf(c), (bytes * 100.0 / total).ToString("N1", culture) + "%"));
        }

        ClassifyStatus = result.Completed ? string.Empty
            : Loc.Format("Stats_Aborted", (result.Fraction * 100).ToString("N0", culture));
        ClassifyFileTypeNote = result.FileTypeName is { } type ? Loc.Format("Stats_ClassifyFileType", type) : string.Empty;
        Annotations?.SetAnnotations(_target!.Document, AnalysisAnnotationSources.Classes,
            [.. result.Regions.Where(r => r.Class is not DataClass.Zero).Select(r => new AnalysisAnnotation(r.Offset, r.Length, ClassName(r.Class),
                "class." + r.Class.ToString().ToLowerInvariant()))]);
    }

    public void GoToClassRow(ClassRowViewModel row) => GoTo?.Invoke(row.OffsetValue);

    /// <summary>圧縮形式のシグネチャの行の「ここから展開...」(ANA-16 の仕様 5。03 の展開機能 F4-04 はフェーズ 4)。</summary>
    public bool CanDecompress(ClassRowViewModel row) => row.Signature is { IsCompression: true } && OpenDecompress is not null;

    public Task DecompressAsync(ClassRowViewModel row) =>
        row.Signature is { } s && OpenDecompress is not null ? OpenDecompress(s.Offset, s.Format) : Task.CompletedTask;
}
