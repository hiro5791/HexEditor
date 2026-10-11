using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Statistics;

namespace HexEditor.App.ViewModels;

/// <summary>統計パネルのタブ: ヒストグラム (ANA-10)、記述統計 (ANA-11)、エントロピー (ANA-12、ANA-13)、ダイグラム (ANA-14)。</summary>
public sealed partial class StatisticsViewModel
{
    // ---- ヒストグラム (ANA-10) ----

    /// <summary>縦軸を対数にする (既定は線形。ANA-10 の仕様 5)。</summary>
    [ObservableProperty]
    public partial bool LogScale { get; set; }

    /// <summary>縦軸を割合 (%) にする。</summary>
    [ObservableProperty]
    public partial bool ShowPercent { get; set; }

    /// <summary>「表で表示」(06 の 0.4)。</summary>
    [ObservableProperty]
    public partial bool HistogramAsTable { get; set; }

    /// <summary>表の並べ替え: 0 = 値の順、1 = 件数の多い順。</summary>
    [ObservableProperty]
    public partial int HistogramSort { get; set; }

    public ObservableCollection<HistogramRowViewModel> HistogramRows { get; } = [];

    /// <summary>u8 の分類ごとの割合 (ANA-10 の仕様 7)。</summary>
    public ObservableCollection<NameValueRowViewModel> ShareRows { get; } = [];

    /// <summary>表に文字の列を出す (u8 のとき)。</summary>
    [ObservableProperty]
    public partial bool HasCharacterColumn { get; set; }

    /// <summary>グラフの要約 (スクリーンリーダー向け。06 の 0.4)。</summary>
    [ObservableProperty]
    public partial string HistogramSummary { get; set; } = string.Empty;

    /// <summary>範囲外・NaN・∞ の件数の表示 (ANA-10 の仕様 3、4)。</summary>
    [ObservableProperty]
    public partial string HistogramExtras { get; set; } = string.Empty;

    public Histogram? Histogram => Result?.Histogram;

    partial void OnHistogramAsTableChanged(bool value) => RebuildHistogramRows();

    partial void OnHistogramSortChanged(int value) => RebuildHistogramRows();

    private void RebuildHistogram()
    {
        OnPropertyChanged(nameof(Histogram));
        ShareRows.Clear();
        if (Result is not { Histogram: { } h } r)
        {
            HistogramSummary = string.Empty;
            HistogramExtras = string.Empty;
            HistogramRows.Clear();
            return;
        }

        HasCharacterColumn = r.Element.Type == ElementType.U8;
        int top = 0;
        for (int i = 1; i < h.BinCount; i++)
        {
            if (h.Counts[i] > h.Counts[top])
            {
                top = i;
            }
        }

        HistogramSummary = Loc.Format("Stats_HistogramSummary", h.BinCount.ToString("N0", CultureInfo.CurrentCulture), BinLabel(h, top),
            h.Counts.Length == 0 ? "0" : h.Counts[top].ToString("N0", CultureInfo.CurrentCulture));
        var extras = new List<string>();
        if (h.Below > 0 || h.Above > 0)
        {
            extras.Add(Loc.Format("Stats_OutOfRange", h.Below.ToString("N0", CultureInfo.CurrentCulture), h.Above.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (h.NaN > 0 || h.PositiveInfinity > 0 || h.NegativeInfinity > 0)
        {
            extras.Add(Loc.Format("Stats_Specials", h.NaN.ToString("N0", CultureInfo.CurrentCulture),
                h.PositiveInfinity.ToString("N0", CultureInfo.CurrentCulture), h.NegativeInfinity.ToString("N0", CultureInfo.CurrentCulture)));
        }

        HistogramExtras = string.Join("  ", extras);
        if (r.Classes is { } c)
        {
            void Share(string id, long count) => ShareRows.Add(new NameValueRowViewModel(id, Loc.Get("Stats_Share_" + id),
                ByteClassShares.Percent(count, c.Total).ToString("N2", CultureInfo.CurrentCulture) + "%"));
            Share("Zero", c.Zero);
            Share("Ff", c.Ff);
            Share("Printable", c.Printable);
            Share("Whitespace", c.Whitespace);
            Share("Control", c.OtherControl);
            Share("High", c.High);
        }

        RebuildHistogramRows();
    }

    private void RebuildHistogramRows()
    {
        HistogramRows.Clear();
        if (!HistogramAsTable || Result is not { Histogram: { } h } r)
        {
            return;
        }

        IEnumerable<HistogramRow> rows = StatisticsExport.Rows(h);
        if (HistogramSort == 1)
        {
            rows = rows.OrderByDescending(x => x.Count).ThenBy(x => x.Bin);
        }

        bool bytes = r.Element.Type == ElementType.U8;
        Core.View.TextEncoding? encoding = _target?.Editor.TextEncoding;
        foreach (HistogramRow row in rows)
        {
            string ch = bytes ? (encoding?.DisplayChar((byte)row.Bin) ?? '.').ToString() : string.Empty;
            HistogramRows.Add(new HistogramRowViewModel(row.Bin, BinLabel(h, row.Bin), ch, row.Count, row.Percent, row.Cumulative));
        }
    }

    /// <summary>ビンの表示 (値ごとのビンは値、範囲のビンは「下端〜上端」)。u8 は 16 進。</summary>
    public string BinLabel(Histogram h, int bin)
    {
        if (h.BinCount == 0)
        {
            return string.Empty;
        }

        if (h.ValueBins)
        {
            long value = h.FirstValue + bin;
            return h.Type == ElementType.U8 ? $"0x{value:X2}" : value.ToString("N0", CultureInfo.CurrentCulture);
        }

        return Loc.Format("Stats_BinRange", StatisticsFormat.Decimal(h.BinLow(bin), CultureInfo.CurrentCulture),
            StatisticsFormat.Decimal(h.BinHigh(bin), CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// 棒または表の行をダブルクリック: その値 (ビン) の次の出現位置へ移動する (ANA-10 の仕様 8)。最初はカーソル位置から、同じビンを
    /// 続けて選ぶと次の位置から探す。末尾まで見つからなければ先頭から探し直す。
    /// </summary>
    public async Task GoToBinAsync(int bin)
    {
        if (Result is not { Histogram: { } h } r || _target is not { } doc || bin < 0 || bin >= h.BinCount)
        {
            return;
        }

        long cursor = doc.Editor.Cursor;
        long after = _lastBinJump is { } last && last.Bin == bin && last.Offset == cursor ? cursor : cursor - 1;
        DocumentSnapshot snapshot = doc.Document.Current;
        long? found = await Task.Run(() => StatisticsNavigator.FindNext(snapshot, r.Ranges, r.Element, after, v => h.BinOf(v) == bin, wrap: true));
        if (found is long offset)
        {
            _lastBinJump = (bin, offset);
            GoTo?.Invoke(offset);
        }
        else
        {
            StatusText = Loc.Get("Stats_ValueNotFound");
        }
    }

    /// <summary>「この値をすべて検索」(ANA-10 の仕様 8): 値 1 つのビンだけ (範囲のビンは検索の値の範囲 (FIND-15) を使うため対象外)。</summary>
    public bool CanFindAll(int bin) => Result is { Histogram: { ValueBins: true } h } && bin >= 0 && bin < h.BinCount && FindAll is not null;

    public async Task FindAllBinAsync(int bin)
    {
        if (!CanFindAll(bin) || Result is not { Histogram: { } h } r)
        {
            return;
        }

        await FindAll!(ElementTypes.Encode(r.Element.Type, h.FirstValue + bin, r.Element.BigEndian));
    }

    /// <summary>表をタブ区切りでコピーする (ANA-10 の仕様 10)。</summary>
    public void CopyHistogramTable()
    {
        if (Result is not { Histogram: { } h } r)
        {
            return;
        }

        bool bytes = r.Element.Type == ElementType.U8;
        string[] header = bytes
            ? [Loc.Get("Stats_Col_Value"), Loc.Get("Stats_Col_Char"), Loc.Get("Stats_Col_Count"), Loc.Get("Stats_Col_Percent"), Loc.Get("Stats_Col_Cumulative")]
            : [Loc.Get("Stats_Col_Value"), Loc.Get("Stats_Col_Count"), Loc.Get("Stats_Col_Percent"), Loc.Get("Stats_Col_Cumulative")];
        Core.View.TextEncoding? encoding = _target?.Editor.TextEncoding;
        IEnumerable<HistogramRowViewModel> rows = StatisticsExport.Rows(h).Select(row => new HistogramRowViewModel(row.Bin, BinLabel(h, row.Bin),
            bytes ? (encoding?.DisplayChar((byte)row.Bin) ?? '.').ToString() : string.Empty, row.Count, row.Percent, row.Cumulative));
        SetClipboardText?.Invoke(StatisticsExport.Tsv(header, rows.Select(x => x.Cells(bytes))));
    }

    // ---- 記述統計 (ANA-11) ----

    public ObservableCollection<NameValueRowViewModel> DescriptiveRows { get; } = [];

    private void RebuildDescriptive()
    {
        DescriptiveRows.Clear();
        if (Result is not { Descriptive: { } d } r)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        bool integer = !ElementTypes.IsFloat(d.Type);
        void Add(StatisticItem item, Func<string> value)
        {
            StatisticsUnavailable why = StatisticsFormat.WhyUnavailable(d, item);
            if (why == StatisticsUnavailable.NotApplicable)
            {
                return;
            }

            bool missing = why != StatisticsUnavailable.None;
            string text = missing ? "—" : value();
            if (!missing && text == "—")
            {
                why = StatisticsUnavailable.TooFewElements;
            }

            DescriptiveRows.Add(new NameValueRowViewModel(item.ToString(), Loc.Get("Stats_Item_" + item), text,
                why == StatisticsUnavailable.None ? null : Loc.Get("Stats_Why_" + why), Loc.Get("Stats_ItemDesc_" + item)));
        }

        string IntegerAndHex(Int128? exact, double approx) =>
            integer && exact is Int128 v ? $"{StatisticsFormat.Integer(v, culture)} ({StatisticsFormat.Hex(v)})" : StatisticsFormat.Decimal(approx, culture);

        Add(StatisticItem.Count, () => d.Count.ToString("N0", culture));
        Add(StatisticItem.Sum, () => d.IntegerSum is Int128 s ? StatisticsFormat.Integer(s, culture) : StatisticsFormat.Decimal(d.Sum, culture));
        Add(StatisticItem.Minimum, () => IntegerAndHex(d.IntegerMin, d.Min));
        Add(StatisticItem.Maximum, () => IntegerAndHex(d.IntegerMax, d.Max));
        Add(StatisticItem.Range, () => d.IntegerMax is Int128 max && d.IntegerMin is Int128 min
            ? StatisticsFormat.Integer(max - min, culture) : StatisticsFormat.Decimal(d.Range, culture));
        Add(StatisticItem.Mean, () => StatisticsFormat.Mean(d, culture));
        Add(StatisticItem.PopulationVariance, () => StatisticsFormat.Decimal(d.PopulationVariance, culture));
        Add(StatisticItem.SampleVariance, () => StatisticsFormat.Decimal(d.SampleVariance, culture));
        Add(StatisticItem.PopulationStdDev, () => StatisticsFormat.Decimal(d.PopulationStdDev, culture));
        Add(StatisticItem.SampleStdDev, () => StatisticsFormat.Decimal(d.SampleStdDev, culture));
        Add(StatisticItem.Median, () => StatisticsFormat.Quantile(d, d.Median, culture));
        Add(StatisticItem.Q1, () => StatisticsFormat.Quantile(d, d.Q1, culture));
        Add(StatisticItem.Q3, () => StatisticsFormat.Quantile(d, d.Q3, culture));
        Add(StatisticItem.Mode, () =>
        {
            if (d.Modes.Count == 0)
            {
                return "—";
            }

            IEnumerable<string> values = integer && d.IntegerModes.Count > 0
                ? d.IntegerModes.Select(m => $"{StatisticsFormat.Integer(m, culture)} ({StatisticsFormat.Hex(m)})")
                : d.Modes.Select(m => StatisticsFormat.Decimal(m, culture));
            string text = string.Join(", ", values) + " — " + Loc.Format("Stats_ModeCount", d.ModeCount.ToString("N0", culture));
            return d.MoreModes > 0 ? text + " " + Loc.Format("Stats_MoreModes", d.MoreModes.ToString("N0", culture)) : text;
        });
        Add(StatisticItem.Skewness, () => StatisticsFormat.Decimal(d.Skewness, culture));
        Add(StatisticItem.Kurtosis, () => StatisticsFormat.Decimal(d.Kurtosis, culture));
        Add(StatisticItem.ChiSquare, () => d.ChiSquare is double chi
            ? Loc.Format("Stats_ChiSquareValue", StatisticsFormat.Decimal(chi, culture), StatisticsFormat.Decimal(d.ChiSquareP ?? double.NaN, culture))
            : "—");
        Add(StatisticItem.SerialCorrelation, () => StatisticsFormat.Decimal(d.SerialCorrelation, culture));
        Add(StatisticItem.Pi, () => d.Pi is double pi
            ? Loc.Format("Stats_PiValue", StatisticsFormat.Decimal(pi, culture), (d.PiErrorPercent ?? 0).ToString("N2", culture))
            : "—");
        if (d.NaN > 0 || d.PositiveInfinity > 0 || d.NegativeInfinity > 0)
        {
            DescriptiveRows.Add(new NameValueRowViewModel("Specials", Loc.Get("Stats_Item_Specials"), Loc.Format("Stats_Specials",
                d.NaN.ToString("N0", culture), d.PositiveInfinity.ToString("N0", culture), d.NegativeInfinity.ToString("N0", culture))));
        }
    }

    /// <summary>「値をコピー」(1 項目) と「すべてコピー」(全項目を 項目名&lt;TAB&gt;値 の形で。ANA-11 の仕様 5)。</summary>
    public void CopyDescriptive(NameValueRowViewModel? row)
    {
        IEnumerable<NameValueRowViewModel> rows = row is null ? DescriptiveRows : [row];
        SetClipboardText?.Invoke(row is null
            ? string.Join("\r\n", rows.Select(x => x.Name + "\t" + x.Value)) + "\r\n"
            : row.Value);
    }

    // ---- エントロピー (ANA-12) ----

    public ObservableCollection<NameValueRowViewModel> EntropyRows { get; } = [];

    /// <summary>判定の目安 (u8 のときだけ。ANA-12 の仕様 4)。</summary>
    [ObservableProperty]
    public partial string Verdict { get; set; } = string.Empty;

    /// <summary>「圧縮率を推定する」(既定でオン。ANA-12 の仕様 5)。</summary>
    [ObservableProperty]
    public partial bool EstimateCompression { get; set; } = true;

    [ObservableProperty]
    public partial CompressionEstimate? Compression { get; set; }

    [ObservableProperty]
    public partial string CompressionText { get; set; } = string.Empty;

    /// <summary>試し圧縮ができなかった理由 (圧縮率の推定は「—」)。</summary>
    [ObservableProperty]
    public partial string CompressionError { get; set; } = string.Empty;

    private void RebuildEntropy()
    {
        EntropyRows.Clear();
        Verdict = string.Empty;
        OnPropertyChanged(nameof(Blocks));
        RebuildBlockRows();
        if (Result is not { Entropy: { } e } r)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        EntropyRows.Add(new NameValueRowViewModel("Entropy", Loc.Get("Stats_Entropy"),
            Loc.Format("Stats_EntropyBits", e.Bits.ToString("N4", culture)), description: Loc.Get("Stats_EntropyDesc")));
        EntropyRows.Add(new NameValueRowViewModel("EntropyPercent", Loc.Get("Stats_EntropyPercent"), e.Percent.ToString("N2", culture) + "%"));
        EntropyRows.Add(new NameValueRowViewModel("Redundancy", Loc.Get("Stats_Redundancy"), e.Redundancy.ToString("N2", culture) + "%"));
        EntropyRows.Add(new NameValueRowViewModel("MinimumSize", Loc.Get("Stats_MinimumSize"),
            Loc.Format("Stats_Bytes", Math.Ceiling(e.MinimumBytes).ToString("N0", culture))));
        EntropyRows.Add(new NameValueRowViewModel("Compression", Loc.Get("Stats_CompressionRatio"),
            CompressionText.Length > 0 ? CompressionText : "—", CompressionError.Length > 0 ? CompressionError : null));
        if (e.Verdict(r.Element.Type) is EntropyVerdict v)
        {
            Verdict = Loc.Get("Stats_Verdict_" + v);
        }
    }

    private async Task EstimateCompressionAsync(DocumentViewModel doc, DocumentSnapshot snapshot, IReadOnlyList<HashRange> ranges)
    {
        Compression = null;
        CompressionText = string.Empty;
        CompressionError = string.Empty;
        try
        {
            CompressionEstimate estimate = await _operations.RunAsync(Loc.Get("Operation_CompressionEstimate"), OperationKind.ReadOnly, doc.Document,
                null, op =>
                {
                    _compression = op;
                    return Task.FromResult(CompressionEstimator.Estimate(snapshot, ranges, op));
                });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            Compression = estimate;
            CultureInfo culture = CultureInfo.CurrentCulture;
            CompressionText = estimate.Ratio.ToString("N2", culture) + "%  " + Loc.Format("Stats_Sample",
                StatisticsFormat.Size(estimate.SampleBytes, culture), StatisticsFormat.Size(estimate.TotalBytes, culture));
        }
        catch (OperationCanceledException)
        {
            CompressionError = Loc.Get("Stats_Cancelled");
        }
        catch (Exception e) when (e is OutOfMemoryException or IOException or InvalidDataException)
        {
            // 試し圧縮の失敗: 圧縮率の推定を「—」にし、ツールチップに理由を出す (ANA-12 の「エラー」)。
            CompressionError = Loc.Format("Stats_CompressionFailed", e.Message);
        }
        finally
        {
            RebuildEntropy();
        }
    }

    // ---- エントロピーグラフ (ANA-13) ----

    /// <summary>ブロックの大きさ (<see cref="BlockSizes"/> の番号。0 は「自動」)。</summary>
    [ObservableProperty]
    public partial int BlockSizeIndex { get; set; }

    /// <summary>補助の系列: ブロックごとの 0x00 の割合 (ANA-13 の仕様 3)。</summary>
    [ObservableProperty]
    public partial bool ShowZeroSeries { get; set; }

    /// <summary>補助の系列: 印字可能な ASCII の割合。</summary>
    [ObservableProperty]
    public partial bool ShowPrintableSeries { get; set; }

    /// <summary>しきい値 (既定 7.2 ビット、0〜8。ANA-13 の仕様 7)。</summary>
    [ObservableProperty]
    public partial double Threshold { get; set; } = 7.2;

    [ObservableProperty]
    public partial bool GraphAsTable { get; set; }

    /// <summary>キーボードで選んでいるブロック (グラフの ← / →。ANA-13 の「画面」)。</summary>
    [ObservableProperty]
    public partial int SelectedBlock { get; set; } = -1;

    public ObservableCollection<EntropyBlockRowViewModel> BlockRows { get; } = [];

    public EntropyBlocks? Blocks => Result?.Blocks;

    /// <summary>拡大したときの表示範囲の細かいブロック (ANA-13 の仕様 4)。なければ null。</summary>
    [ObservableProperty]
    public partial EntropyBlocks? DetailBlocks { get; set; }

    /// <summary>細かいブロックを計算した論理範囲。</summary>
    public (long From, long To) DetailRange { get; private set; }

    /// <summary>細かいブロックを計算した範囲の記録 (テスト用。TC-ANA-13-03)。</summary>
    public List<(long BlockSize, long From, long To)> DetailLog { get; } = [];

    [ObservableProperty]
    public partial string GraphSummary { get; set; } = string.Empty;

    private LongRunningOperation? _detailOperation;
    private (long From, long To, double BlockPixels)? _pendingZoom;
    private DispatcherQueueTimerWrapper? _zoomTimer;

    partial void OnBlockSizeIndexChanged(int value)
    {
        if (Result is not null)
        {
            _ = RecomputeBlocksAsync();
        }
    }

    partial void OnGraphAsTableChanged(bool value) => RebuildBlockRows();

    private void RebuildBlockRows()
    {
        BlockRows.Clear();
        GraphSummary = string.Empty;
        if (Blocks is not { } b || Ranges is not { } ranges)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        int max = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (b.State[i] == BlockState.Valid && b.Entropy[i] > b.Entropy[max])
            {
                max = i;
            }
        }

        GraphSummary = b.Count == 0 ? string.Empty : Loc.Format("Stats_GraphSummary", b.Count.ToString("N0", culture),
            b.Entropy[max].ToString("N2", culture), Hex(ranges.ToDocument(b.BlockStart(max))));
        if (!GraphAsTable)
        {
            return;
        }

        // 拡大中は、表示範囲を細かいブロックの行にする (表示範囲の行のブロックの大きさが分かる)。
        EntropyBlocks? detail = DetailBlocks;
        for (int i = 0; i < b.Count; i++)
        {
            long start = b.BlockStart(i);
            if (detail is not null && start + b.BlockLength(i) > DetailRange.From && start < DetailRange.To)
            {
                for (int k = detail.BlockOf(Math.Max(start, DetailRange.From)); k < detail.Count && detail.BlockStart(k) < Math.Min(start + b.BlockLength(i), DetailRange.To); k++)
                {
                    BlockRows.Add(BlockRow(detail, k, ranges, culture));
                }

                continue;
            }

            BlockRows.Add(BlockRow(b, i, ranges, culture));
        }
    }

    private EntropyBlockRowViewModel BlockRow(EntropyBlocks b, int i, LogicalRanges ranges, CultureInfo culture)
    {
        bool ok = b.State[i] == BlockState.Valid;
        string unreadable = Loc.Get("Stats_Unreadable_Cell");
        return new EntropyBlockRowViewModel(i, Hex(ranges.ToDocument(b.BlockStart(i))), StatisticsFormat.Size(b.BlockSize, culture),
            b.State[i] == BlockState.None ? "—" : ok ? b.Entropy[i].ToString("N3", culture) : unreadable,
            ok ? (b.Zero[i] * 100).ToString("N1", culture) + "%" : "—", ok ? (b.Printable[i] * 100).ToString("N1", culture) + "%" : "—");
    }

    /// <summary>ブロックの大きさを変えた: ブロックだけを計算し直す (ドキュメント全体ならキャッシュを使う)。</summary>
    private async Task RecomputeBlocksAsync()
    {
        if (_target is not { } doc || Result is not { } r)
        {
            return;
        }

        long size = EntropyBlocks.EffectiveBlockSize(BlockSizes[Math.Clamp(BlockSizeIndex, 0, BlockSizes.Count - 1)], r.Ranges.Length);
        DocumentSnapshot snapshot = doc.Document.Current;
        ClearDetail();
        try
        {
            EntropyBlocks blocks = await _operations.RunAsync(Loc.Get("Operation_EntropyGraph"), OperationKind.ReadOnly, doc.Document, r.Ranges.Length,
                op =>
                {
                    if (r.Ranges.IsWhole(snapshot.Length))
                    {
                        return Task.FromResult(EntropyCache.For(doc.Document).GetOrCompute(snapshot, size, operation: op));
                    }

                    var b = new EntropyBlocks(r.Ranges.Length, size);
                    EntropyGraph.ComputeBlocks(snapshot, r.Ranges, b, 0, r.Ranges.Length, op);
                    return Task.FromResult(b);
                });
            if (ReferenceEquals(_target, doc) && ReferenceEquals(Result, r))
            {
                Result = new StatisticsResult
                {
                    Ranges = r.Ranges,
                    Element = r.Element,
                    Histogram = r.Histogram,
                    Descriptive = r.Descriptive,
                    Entropy = r.Entropy,
                    Classes = r.Classes,
                    Blocks = blocks,
                    Digram = r.Digram,
                    Positions = r.Positions,
                    Elements = r.Elements,
                    Remainder = r.Remainder,
                    Unreadable = r.Unreadable,
                    Completed = r.Completed,
                    Fraction = r.Fraction,
                    BytesRead = r.BytesRead,
                };
                RebuildEntropy();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// グラフの表示範囲が変わった (拡大・縮小・スクロール)。1 ブロックが 4 ピクセル以上なら、表示範囲だけを細かいブロックで計算し直す
    /// (最小 256 バイト。ANA-13 の仕様 4)。300 ms 待ってまとめて行う。
    /// </summary>
    public void OnGraphViewChanged(long from, long to, double blockPixels)
    {
        if (Blocks is not { } b)
        {
            return;
        }

        if (blockPixels < 4 || b.BlockSize <= EntropyBlocks.MinBlockSize)
        {
            if (DetailBlocks is not null)
            {
                ClearDetail();
                RebuildBlockRows();
            }

            return;
        }

        _pendingZoom = (from, to, blockPixels);
        _zoomTimer ??= new DispatcherQueueTimerWrapper(_queue, AutoDelay, () => _ = ComputeDetailAsync());
        _zoomTimer.Restart();
    }

    /// <summary>待たずに細かいブロックを計算する (テスト用)。</summary>
    public Task FlushDetailAsync() => ComputeDetailAsync();

    private async Task ComputeDetailAsync()
    {
        if (_pendingZoom is not { } zoom || Blocks is not { } b || Result is not { } r || _target is not { } doc)
        {
            return;
        }

        _pendingZoom = null;
        long from = Math.Max(0, zoom.From / b.BlockSize * b.BlockSize);
        long to = Math.Min(r.Ranges.Length, (zoom.To + b.BlockSize - 1) / b.BlockSize * b.BlockSize);
        long detail = EntropyGraph.DetailBlockSize(to - from);
        if (detail >= b.BlockSize || to <= from)
        {
            return;
        }

        if (DetailBlocks is { } existing && existing.BlockSize == detail && DetailRange.From <= from && DetailRange.To >= to)
        {
            return;
        }

        CancelDetail();
        DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            EntropyBlocks blocks = await _operations.RunAsync(Loc.Get("Operation_EntropyGraph"), OperationKind.ReadOnly, doc.Document, to - from, op =>
            {
                _detailOperation = op;
                if (r.Ranges.IsWhole(snapshot.Length))
                {
                    return Task.FromResult(EntropyCache.For(doc.Document).GetOrCompute(snapshot, detail, from, to, op));
                }

                var d = new EntropyBlocks(r.Ranges.Length, detail);
                EntropyGraph.ComputeBlocks(snapshot, r.Ranges, d, from, to, op);
                return Task.FromResult(d);
            });
            if (ReferenceEquals(_target, doc) && ReferenceEquals(Result, r))
            {
                DetailLog.Add((detail, from, to));
                DetailRange = (from, to);
                DetailBlocks = blocks;
                RebuildBlockRows();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelDetail()
    {
        _detailOperation?.Cancel();
        _detailOperation = null;
    }

    private void ClearDetail()
    {
        CancelDetail();
        _pendingZoom = null;
        DetailBlocks = null;
        DetailRange = default;
    }

    /// <summary>ブロックの先頭へ移動する (グラフのクリック、Enter。ANA-13 の仕様 5)。</summary>
    public void JumpToBlock(EntropyBlocks blocks, int index)
    {
        if (Ranges is { } ranges && index >= 0 && index < blocks.Count)
        {
            GoTo?.Invoke(ranges.ToDocument(blocks.BlockStart(index)));
        }
    }

    /// <summary>ドラッグした範囲 (ブロックの境界にそろえる) を選択する (ANA-13 の仕様 5)。</summary>
    public void SelectBlocks(EntropyBlocks blocks, int first, int lastExclusive)
    {
        if (Ranges is not { } ranges || lastExclusive <= first)
        {
            return;
        }

        long start = blocks.BlockStart(Math.Max(0, first));
        long end = Math.Min(blocks.Length, blocks.BlockStart(Math.Min(blocks.Count, lastExclusive)));
        long docStart = ranges.ToDocument(start);
        Select?.Invoke(docStart, ranges.ToDocument(end - 1) + 1 - docStart);
    }

    /// <summary>グラフの値を CSV (ブロックの開始オフセット, エントロピー) にエクスポートする (ANA-13 の仕様 9)。</summary>
    public async Task ExportGraphAsync()
    {
        if (Blocks is not { } b || Ranges is not { } ranges || PickSavePath is null || await PickSavePath("entropy.csv", ".csv") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, b.ToCsv(ranges));
            StatusText = Loc.Format("Stats_Saved", Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.Format("Stats_SaveFailed", e.Message);
        }
    }

    // ---- ダイグラムと位置ごとのバイト分布 (ANA-14) ----

    /// <summary>0 = ダイグラム、1 = 位置ごとの分布。</summary>
    [ObservableProperty]
    public partial int DigramMode { get; set; }

    [ObservableProperty]
    public partial bool DigramAsTable { get; set; }

    public ObservableCollection<DigramRowViewModel> DigramRows { get; } = [];

    /// <summary>対象が 2 バイト未満 (ANA-14 の「エラー」)。</summary>
    [ObservableProperty]
    public partial string DigramMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DigramSummary { get; set; } = string.Empty;

    public Digram? Digram => Result?.Digram;

    public PositionDistribution? Positions => Result?.Positions;

    partial void OnDigramAsTableChanged(bool value) => RebuildDigramRows();

    private void RebuildDigram()
    {
        OnPropertyChanged(nameof(Digram));
        OnPropertyChanged(nameof(Positions));
        DigramMessage = Result is { Completed: true } r && r.Ranges.Length < 2 ? Loc.Get("Stats_DigramTooShort") : string.Empty;
        DigramSummary = string.Empty;
        if (Digram is { } d && d.Top(1) is [var top])
        {
            DigramSummary = Loc.Format("Stats_DigramSummary", d.Total.ToString("N0", CultureInfo.CurrentCulture),
                $"0x{top.First:X2} 0x{top.Second:X2}", top.Count.ToString("N0", CultureInfo.CurrentCulture));
        }

        RebuildDigramRows();
    }

    private void RebuildDigramRows()
    {
        DigramRows.Clear();
        if (!DigramAsTable || Digram is not { } d)
        {
            return;
        }

        long total = d.Total;
        foreach ((int a, int b, long count) in d.Top(1000))
        {
            DigramRows.Add(new DigramRowViewModel(a, b, count, total == 0 ? 0 : count * 100.0 / total));
        }
    }

    /// <summary>ダイグラムのセルの説明 (ツールチップ: 値、件数、割合。ANA-14 の仕様 3)。</summary>
    public string DigramCellText(int first, int second)
    {
        if (Digram is not { } d)
        {
            return string.Empty;
        }

        long count = d.Count(first, second);
        long total = d.Total;
        return Loc.Format("Stats_DigramCell", $"0x{first:X2} 0x{second:X2}", count.ToString("N0", CultureInfo.CurrentCulture),
            (total == 0 ? 0 : count * 100.0 / total).ToString("N3", CultureInfo.CurrentCulture));
    }

    /// <summary>位置ごとの分布のセルの説明 (区間のアドレス範囲、値、件数、割合)。</summary>
    public string PositionCellText(int section, int value)
    {
        if (Positions is not { } p || Ranges is not { } ranges)
        {
            return string.Empty;
        }

        long start = p.SectionStart(section);
        long end = section + 1 < PositionDistribution.Sections ? p.SectionStart(section + 1) : p.Length;
        long count = p.Count(section, value);
        long total = p.SectionTotal(section);
        return Loc.Format("Stats_PositionCell", Hex(ranges.ToDocument(start)), Hex(ranges.ToDocument(Math.Max(start, end - 1))), $"0x{value:X2}",
            count.ToString("N0", CultureInfo.CurrentCulture), (total == 0 ? 0 : count * 100.0 / total).ToString("N2", CultureInfo.CurrentCulture));
    }

    /// <summary>ダイグラムのセルをダブルクリック: その 2 バイト列を検索バーに入れて次を検索する (ANA-14 の仕様 4)。</summary>
    public Task SearchDigramAsync(int first, int second) => SearchNext?.Invoke([(byte)first, (byte)second]) ?? Task.CompletedTask;

    /// <summary>位置ごとの分布のセルをクリック: その区間の先頭へ移動する。</summary>
    public void GoToSection(int section)
    {
        if (Positions is { } p && Ranges is { } ranges)
        {
            GoTo?.Invoke(ranges.ToDocument(p.SectionStart(Math.Clamp(section, 0, PositionDistribution.Sections - 1))));
        }
    }
}

/// <summary>1 回だけ動くタイマー (DispatcherQueue がなければすぐに動かす)。</summary>
internal sealed class DispatcherQueueTimerWrapper
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private readonly Action _action;

    public DispatcherQueueTimerWrapper(Microsoft.UI.Dispatching.DispatcherQueue? queue, TimeSpan interval, Action action)
    {
        _action = action;
        if (queue is not null)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = interval;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => action();
        }
    }

    public void Restart()
    {
        if (_timer is null)
        {
            _action();
            return;
        }

        _timer.Stop();
        _timer.Start();
    }
}
