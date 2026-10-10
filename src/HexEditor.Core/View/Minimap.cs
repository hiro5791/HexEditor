using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.View;

/// <summary>ミニマップの表示内容 (VIEW-35 の仕様 3)。</summary>
public enum MinimapContent
{
    /// <summary>シャノンエントロピー (0〜8 bit)。既定。</summary>
    Entropy,

    /// <summary>バイトの種類 (VIEW-17 の「種類別」の 6 分類) の割合。</summary>
    ByteKinds,

    /// <summary>バイト値の平均。</summary>
    ByteValue,

    /// <summary><c>00</c> 以外のバイトの割合。</summary>
    Zero,

    /// <summary>「周辺」のときだけ。各バイトを 1 ピクセルとしてバイトテーマの色で描く。</summary>
    ByteTheme,
}

/// <summary>ミニマップの範囲 (VIEW-35 の仕様 2)。</summary>
public enum MinimapRange
{
    /// <summary>ドキュメント全体 (既定)。</summary>
    Whole,

    /// <summary>1 ピクセル行を Hex ビューの 1 行に対応させ、表示位置を中心に表示する。</summary>
    Around,
}

/// <summary>ミニマップの 1 ピクセル行の値。<see cref="Kinds"/> は 6 分類の割合 (<see cref="ByteCategory"/> の順)。</summary>
public sealed record MinimapStats(double Entropy, double Mean, double NonZero, double[] Kinds, bool Unreadable)
{
    /// <summary>表示内容の値 (エントロピーは bit、平均は 0〜255、ゼロは割合)。</summary>
    public double ValueOf(MinimapContent content) => content switch
    {
        MinimapContent.ByteValue => Mean,
        MinimapContent.Zero => NonZero,
        _ => Entropy,
    };

    /// <summary>棒の長さ (0〜1。仕様 3 の表と仕様 4)。「バイトの種類」とバイトテーマは全幅。</summary>
    public double BarOf(MinimapContent content) => content switch
    {
        MinimapContent.Entropy => Entropy / 8,
        MinimapContent.ByteValue => Mean / 255,
        MinimapContent.Zero => NonZero,
        _ => 1,
    };

    /// <summary>標本から値を求める。<paramref name="states"/> が読めないバイトを含めば <see cref="Unreadable"/>。</summary>
    public static MinimapStats From(ReadOnlySpan<byte> data, bool unreadable)
    {
        Span<long> histogram = stackalloc long[256];
        foreach (byte b in data)
        {
            histogram[b]++;
        }

        return FromHistogram(histogram, data.Length, unreadable);
    }

    /// <summary>度数分布から値を求める (「正確に計算」で範囲全体を読むとき)。</summary>
    public static MinimapStats FromHistogram(ReadOnlySpan<long> histogram, long total, bool unreadable)
    {
        if (total <= 0)
        {
            return new MinimapStats(0, 0, 0, new double[6], unreadable);
        }

        double entropy = 0;
        double sum = 0;
        double[] kinds = new double[6];
        for (int v = 0; v < 256; v++)
        {
            long n = histogram[v];
            if (n == 0)
            {
                continue;
            }

            double p = (double)n / total;
            entropy -= p * Math.Log2(p);
            sum += (double)v * n;
            kinds[(int)ByteTheme.Classify((byte)v)] += p;
        }

        return new MinimapStats(Math.Max(0, entropy), sum / total, 1 - (double)histogram[0] / total, kinds, unreadable);
    }
}

/// <summary>
/// ミニマップの計算 (VIEW-35)。ピクセル行ごとの範囲を決め、値をバックグラウンドで上から順に求める (表示を止めない)。
/// 範囲が 4 KiB を超えるピクセル行は、範囲の中央の 4 KiB を標本にする概算 (仕様 5)。「正確に計算」は範囲全体を読む長時間処理で、
/// 処理センターに出してキャンセルできる。読み込みは各ピクセル行の標本だけで、メモリはピクセル行の数にしか比例しない。
/// </summary>
public sealed class MinimapComputer : IDisposable
{
    /// <summary>概算の標本の大きさ (仕様 5)。</summary>
    public const int SampleSize = 4096;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private MinimapStats?[] _rows = [];
    private long _first;
    private long _rowBytes = 1;
    private long _length;
    private int _computed;
    private DocumentSnapshot? _snapshot;

    // 「正確に計算」の結果。ドキュメントごとに持ち (仕様の「巨大ファイル」)、内容が変わらない限り、どのビュー・どのタブの切り替えでも再利用する。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, ExactResult> ExactCache = new();

    private sealed record ExactResult(DocumentSnapshot Snapshot, long First, long RowBytes, int Count, MinimapStats?[] Rows);

    // 実行中の「正確に計算」(同じドキュメントの古い内容の計算は、新しく始めるときに止める)。
    private (DocumentSnapshot Snapshot, long First, long RowBytes, int Count, CancellationTokenSource Cts)? _exactRunning;

    // 利用者がキャンセルした「正確に計算」の割り当て (同じ内容・割り当てでは自動で始め直さない)。
    private (DocumentSnapshot Snapshot, long First, long RowBytes, int Count)? _exactDeclined;

    // 表示内容「バイトテーマ」のときの、ピクセル行ごとのバイト (「周辺」で 1 ピクセル行 = 1 行。ピクセル行の数 × 1 行のバイト数だけ)。
    private byte[]? _bytes;
    private bool _keepBytes;

    /// <summary>計算が進んだ (スレッドプールから呼ぶ)。</summary>
    public event EventHandler? Progress;

    /// <summary>ピクセル行の数。</summary>
    public int RowCount => _rows.Length;

    /// <summary>計算の済んだピクセル行の数 (UI オートメーションで公開する「計算済みの行数」)。</summary>
    public int Computed => Volatile.Read(ref _computed);

    /// <summary>1 ピクセル行が表すバイト数。</summary>
    public long RowBytes => _rowBytes;

    /// <summary>最初のピクセル行の先頭のオフセット。</summary>
    public long First => _first;

    /// <summary>正確な値を表示しているか。</summary>
    public bool IsExact { get; private set; }

    /// <summary>ピクセル行の値 (計算前は null)。</summary>
    public MinimapStats? Row(int index) => index >= 0 && index < _rows.Length ? Volatile.Read(ref _rows[index]) : null;

    /// <summary>
    /// 各ピクセル行のバイトを残すか (表示内容「バイトテーマ」。仕様 3)。1 ピクセル行の範囲が標本の大きさ以下のときだけ残す
    /// (「周辺」では 1 行分)。変えたら <see cref="Start"/> で計算し直す。
    /// </summary>
    public bool KeepBytes
    {
        get => _keepBytes;
        set => _keepBytes = value;
    }

    /// <summary>
    /// ピクセル行のバイト (<see cref="KeepBytes"/> のときだけ。計算前・残していなければ空)。<see cref="Row"/> が null でないことを
    /// 確かめてから読む。
    /// </summary>
    public ReadOnlySpan<byte> RowBytesOf(int index)
    {
        byte[]? bytes = _bytes;
        if (bytes is null || index < 0 || index >= _rows.Length || Row(index) is null)
        {
            return [];
        }

        long start = index * _rowBytes;
        int length = (int)RangeOf(index).Length;
        return start + length <= bytes.Length ? bytes.AsSpan((int)start, length) : [];
    }

    /// <summary>
    /// 表示内容「バイトテーマ」の 1 バイトの色 (仕様 3): テーマの背景色、なければ文字色。テーマが「なし」か色の指定がない値は、
    /// 「種類別」の色。
    /// </summary>
    public static SchemeColor ByteColor(ByteTheme? theme, byte value, bool dark)
    {
        if (theme?.ColorOf(value, dark) is { } color && (color.Background ?? color.Text) is { } c)
        {
            return c;
        }

        return ByteTheme.CategoryColor(ByteTheme.Classify(value), dark);
    }

    /// <summary>
    /// 「正確に計算」が必要か (仕様 5): 設定がオンのとき、今の割り当てがまだ正確でなく、実行中でもないなら真。1 ピクセル行の範囲が標本の大きさ
    /// 以下なら概算が範囲全体を読んでいるので要らない。
    /// </summary>
    public bool NeedsExact
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is not null && _rows.Length > 0 && !IsExact && _rowBytes > SampleSize
                    && !(_exactRunning is { } r && ReferenceEquals(r.Snapshot, _snapshot) && r.First == _first && r.RowBytes == _rowBytes
                        && r.Count == _rows.Length)
                    && !(_exactDeclined is { } d && ReferenceEquals(d.Snapshot, _snapshot) && d.First == _first && d.RowBytes == _rowBytes
                        && d.Count == _rows.Length);
            }
        }
    }

    /// <summary>ピクセル行が表す範囲。</summary>
    public (long Start, long Length) RangeOf(int index)
    {
        long start = _first + index * _rowBytes;
        return (start, Math.Max(0, Math.Min(_rowBytes, _length - start)));
    }

    /// <summary>オフセットのピクセル行 (範囲の外は -1)。</summary>
    public int RowOf(long offset)
    {
        if (_rows.Length == 0 || offset < _first)
        {
            return -1;
        }

        long row = (offset - _first) / _rowBytes;
        return row < _rows.Length ? (int)row : offset <= _length ? _rows.Length - 1 : -1;
    }

    /// <summary>
    /// 「全体」の割り当て (仕様 2): 1 ピクセル行が表す範囲は L ÷ 高さ (切り上げ、最小 1 行分)。
    /// </summary>
    public static (long First, long RowBytes, int Count) Whole(long length, int pixelRows, int bytesPerRow)
    {
        pixelRows = Math.Max(1, pixelRows);
        long rowBytes = Math.Max(Math.Max(1, bytesPerRow), (length + pixelRows - 1) / pixelRows);
        int count = (int)Math.Clamp((length + rowBytes - 1) / rowBytes, 1, pixelRows);
        return (0, rowBytes, count);
    }

    /// <summary>「周辺」の割り当て (仕様 2): 1 ピクセル行 = Hex ビューの 1 行。表示位置を中心にする。</summary>
    public static (long First, long RowBytes, int Count) Around(long length, int pixelRows, HexLayout layout, long topRow, int visibleRows)
    {
        pixelRows = Math.Max(1, pixelRows);
        long center = topRow + visibleRows / 2;
        long firstRow = Math.Clamp(center - pixelRows / 2, 0, Math.Max(0, layout.TotalRows - pixelRows));
        long first = layout.RowStart(firstRow);
        int count = (int)Math.Clamp(layout.TotalRows - firstRow, 1, pixelRows);
        return (first, layout.BytesPerRow, count);
    }

    /// <summary>
    /// 割り当てを決めて計算を始める (前の計算は止める)。同じ割り当て・同じ内容なら何もしない。<paramref name="exact"/> で正確な値の
    /// キャッシュがあればそれを使う。
    /// </summary>
    public void Start(DocumentSnapshot snapshot, long first, long rowBytes, int count, bool force = false)
    {
        lock (_gate)
        {
            bool keepBytes = _keepBytes && Math.Max(1, rowBytes) <= SampleSize;
            if (!force && ReferenceEquals(snapshot, _snapshot) && first == _first && rowBytes == _rowBytes && count == _rows.Length
                && keepBytes == _bytes is not null)
            {
                return;
            }

            _cts?.Cancel();
            _snapshot = snapshot;
            _first = first;
            _rowBytes = Math.Max(1, rowBytes);
            _length = snapshot.Length;
            if (keepBytes)
            {
                // バイトを残すとき (バイトテーマ) は概算が範囲全体を読むので、正確な値のキャッシュは使わずに読む。
                // 止めた前の計算が書き込むことがあるので、ピクセル行の値の配列と同じく毎回作る (ピクセル行の数 × 1 行分で小さい)。
                _bytes = new byte[count * _rowBytes];
            }
            else
            {
                _bytes = null;
                if (ExactCache.TryGetValue(snapshot.Storage.Owner, out ExactResult? cached) && ReferenceEquals(cached.Snapshot, snapshot)
                    && cached.First == first && cached.RowBytes == _rowBytes && cached.Count == count)
                {
                    _rows = (MinimapStats?[])cached.Rows.Clone();
                    _computed = count;
                    IsExact = true;
                    Progress?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            IsExact = false;
            _rows = new MinimapStats?[count];
            _computed = 0;
            var cts = new CancellationTokenSource();
            _cts = cts;
            MinimapStats?[] rows = _rows;
            byte[]? bytes = _bytes;
            _ = Task.Run(() => Compute(snapshot, first, _rowBytes, rows, bytes, cts.Token, 0, count));
        }
    }

    /// <summary>
    /// 編集で変わった範囲に対応するピクセル行だけを計算し直す (仕様 8。500 ms 待ってまとめるのは呼び出し側)。
    /// </summary>
    public void Invalidate(DocumentSnapshot snapshot, long offset, long length)
    {
        lock (_gate)
        {
            if (_rows.Length == 0)
            {
                return;
            }

            _cts?.Cancel();
            _snapshot = snapshot;
            _length = snapshot.Length;
            IsExact = false;
            int from = Math.Max(0, (int)Math.Min(_rows.Length - 1, (Math.Max(offset, _first) - _first) / _rowBytes));
            int to = length < 0 || offset + length >= _length ? _rows.Length - 1
                : (int)Math.Min(_rows.Length - 1, (offset + length - _first) / _rowBytes);
            MinimapStats?[] rows = _rows;
            for (int i = from; i <= to; i++)
            {
                rows[i] = null;
            }

            _computed = rows.Count(r => r is not null);
            var cts = new CancellationTokenSource();
            _cts = cts;
            byte[]? bytes = _bytes;
            _ = Task.Run(() => Compute(snapshot, _first, _rowBytes, rows, bytes, cts.Token, 0, rows.Length));
        }
    }

    /// <summary>
    /// 「正確に計算」(仕様 5): 各ピクセル行の範囲全体を読む長時間処理。処理センターに出してキャンセルできる。結果はドキュメントごとに
    /// キャッシュし、内容が変わらない限り再利用する (仕様の「巨大ファイル」)。同じドキュメントの古い内容に対して実行中の計算は止める。
    /// </summary>
    public async Task ComputeExactAsync(OperationCenter operations, string name, object? target)
    {
        DocumentSnapshot? snapshot;
        long first;
        long rowBytes;
        int count;
        CancellationTokenSource cts;
        lock (_gate)
        {
            snapshot = _snapshot;
            first = _first;
            rowBytes = _rowBytes;
            count = _rows.Length;
            if (snapshot is null || count == 0)
            {
                return;
            }

            if (_exactRunning is { } running)
            {
                if (ReferenceEquals(running.Snapshot, snapshot) && running.First == first && running.RowBytes == rowBytes && running.Count == count)
                {
                    // 同じ割り当てで実行中。
                    return;
                }

                if (ReferenceEquals(running.Snapshot.Storage.Owner, snapshot.Storage.Owner))
                {
                    // 同じドキュメントの古い内容・古い割り当ての計算は要らない。別のドキュメントの計算は、そのドキュメントのキャッシュになるので続ける。
                    running.Cts.Cancel();
                }
            }

            cts = new CancellationTokenSource();
            _exactRunning = (snapshot, first, rowBytes, count, cts);
        }

        long total = Math.Max(0, Math.Min(snapshot.Length - first, rowBytes * count));
        MinimapStats?[] exact = new MinimapStats?[count];
        try
        {
            await operations.RunAsync(name, OperationKind.ReadOnly, target, total, op =>
            {
                using CancellationTokenRegistration registration = cts.Token.Register(op.Cancel);
                byte[] buffer = new byte[1 << 20];
                long[] histogram = new long[256];
                long done = 0;
                for (int i = 0; i < count; i++)
                {
                    op.CancellationToken.ThrowIfCancellationRequested();
                    long start = first + i * rowBytes;
                    long end = Math.Min(snapshot.Length, start + rowBytes);
                    Array.Clear(histogram);
                    bool unreadable = false;
                    for (long at = start; at < end;)
                    {
                        op.CancellationToken.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(buffer.Length, end - at);
                        ReadResult read = snapshot.Read(at, buffer.AsSpan(0, n));
                        unreadable |= !read.IsComplete;
                        for (int k = 0; k < n; k++)
                        {
                            histogram[buffer[k]]++;
                        }

                        at += n;
                        done += n;
                        op.Report(done);
                    }

                    exact[i] = MinimapStats.FromHistogram(histogram, Math.Max(0, end - start), unreadable);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // 利用者のキャンセル: 同じ内容・割り当てでは自動で始め直さない (処理センターでのキャンセルを尊重する)。
            lock (_gate)
            {
                _exactDeclined = (snapshot, first, rowBytes, count);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (_exactRunning is { } r && ReferenceEquals(r.Cts, cts))
                {
                    _exactRunning = null;
                }
            }

            cts.Dispose();
        }

        lock (_gate)
        {
            ExactCache.AddOrUpdate(snapshot.Storage.Owner, new ExactResult(snapshot, first, rowBytes, count, exact));
            if (ReferenceEquals(_snapshot, snapshot) && _first == first && _rowBytes == rowBytes && _rows.Length == count)
            {
                _cts?.Cancel();
                _rows = (MinimapStats?[])exact.Clone();
                _computed = count;
                IsExact = true;
            }
        }

        Progress?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>計算を止める。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    private void Compute(DocumentSnapshot snapshot, long first, long rowBytes, MinimapStats?[] rows, byte[]? bytes, CancellationToken token, int from, int to)
    {
        byte[] buffer = new byte[SampleSize];
        int reported = 0;
        long lastReport = Environment.TickCount64;
        for (int i = from; i < to; i++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (Volatile.Read(ref rows[i]) is not null)
            {
                continue;
            }

            long start = first + i * rowBytes;
            long length = Math.Max(0, Math.Min(rowBytes, snapshot.Length - start));

            // 範囲が 4 KiB を超えるときは中央の 4 KiB を標本にする (概算)。
            long sampleStart = length > SampleSize ? start + (length - SampleSize) / 2 : start;
            int sampleLength = (int)Math.Min(length, SampleSize);
            MinimapStats stats;
            try
            {
                ReadResult read = snapshot.Read(sampleStart, buffer.AsSpan(0, sampleLength));
                stats = MinimapStats.From(buffer.AsSpan(0, sampleLength), !read.IsComplete);

                // バイトテーマ (仕様 3): 範囲全体 (標本と同じ) のバイトを残す。値を書く前に写すので、値が見えればバイトも読める。
                if (bytes is not null && (long)(i + 1) * rowBytes <= bytes.Length)
                {
                    buffer.AsSpan(0, sampleLength).CopyTo(bytes.AsSpan((int)(i * rowBytes), sampleLength));
                }

                // 統計パネル (ANA-13) で範囲全体のエントロピーを計算済みなら、標本の概算の代わりにその値を使う (キャッシュの共有。ANA-13 の仕様 8)。
                if (Statistics.EntropyCache.Find(snapshot.Storage.Owner)?.CachedEntropy(snapshot, start, length) is { } exact)
                {
                    stats = stats with { Entropy = exact };
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
            {
                return;
            }

            Volatile.Write(ref rows[i], stats);
            if (ReferenceEquals(rows, _rows))
            {
                Interlocked.Increment(ref _computed);
            }

            // 描き直しの通知は 50 ms ごとにまとめる (上から順に埋まる様子を見せる)。
            reported++;
            if (Environment.TickCount64 - lastReport >= 50)
            {
                lastReport = Environment.TickCount64;
                Progress?.Invoke(this, EventArgs.Empty);
            }
        }

        if (reported > 0 && !token.IsCancellationRequested)
        {
            Progress?.Invoke(this, EventArgs.Empty);
        }
    }
}
