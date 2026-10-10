using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>演算の結果の件数 (EDIT-32 の仕様 3・4、EDIT-36 の仕様 3)。</summary>
public sealed class DataOperationStats
{
    /// <summary>あふれた (ラップまたは飽和した) 要素の数。</summary>
    public long Overflows { get; internal set; }

    /// <summary>結果が NaN になった要素の数 (浮動小数点)。</summary>
    public long NaNs { get; internal set; }

    /// <summary>結果が ±∞ になった要素の数 (浮動小数点)。</summary>
    public long Infinities { get; internal set; }

    /// <summary>逆除算で 0 のため変えなかった要素の数。</summary>
    public long ZeroSkipped { get; internal set; }

    /// <summary>値が変わった要素の数。</summary>
    public long ChangedElements { get; internal set; }

    /// <summary>要素に満たないため変えなかった末尾のバイト数 (範囲ごとの合計。EDIT-31 の仕様 8)。</summary>
    public long TrailingBytes { get; internal set; }

    /// <summary>データが 1 バイトでも変わったか (変わらなければ反映しない。EDIT-31 の「巨大ファイル」5)。</summary>
    public bool Changed { get; internal set; }
}

/// <summary>演算の結果。<see cref="Replacements"/> は <see cref="TransformApplier.Apply"/> で反映するか、捨てるなら Dispose する。</summary>
public sealed record DataOperationResult(IReadOnlyList<RangeReplacement> Replacements, DataOperationStats Stats);

/// <summary>読めないバイトがあるため演算できない (EDIT-31 の「エラー」。ドキュメントは変えない)。</summary>
public sealed class DataReadException(long offset) : IOException($"オフセット 0x{offset:X} を読み込めません。")
{
    public long Offset { get; } = offset;
}

/// <summary>プレビュー (EDIT-31 の仕様 10): 対象範囲の先頭 64 バイトの変更前と変更後、要素の値の 10 進表記。</summary>
public sealed record DataOperationPreview(byte[] Before, byte[] After, IReadOnlyList<string> BeforeValues, IReadOnlyList<string> AfterValues,
    long TrailingBytes)
{
    /// <summary>位置 <paramref name="i"/> のバイトが変わるか (印を付ける)。</summary>
    public bool IsChanged(int i) => i < Before.Length && i < After.Length && Before[i] != After[i];
}

/// <summary>
/// データ演算 (EDIT-31〜EDIT-36) を行う。対象範囲を 1 MiB ずつ読み、要素ごとに計算して追加データ (1 MiB 以下はメモリ、それを超えるものは
/// 一時ファイル) に書く。結果は作り終えてから <see cref="TransformApplier"/> で 1 つの編集グループとして反映する。キャンセル・失敗したら
/// 作りかけの一時ファイルを消し、ドキュメントは変えない。ビットの挿入・削除 (EDIT-37) は <see cref="BitShifter"/>。
/// </summary>
public sealed class DataOperationRunner
{
    /// <summary>これを超える範囲は長時間処理 (進捗・キャンセル) として実行する (EDIT-31 の「巨大ファイル」1)。</summary>
    public const long InPlaceLimit = 4L * 1024 * 1024;

    /// <summary>これ以下の結果は一時ファイルを作らずメモリ (追加バッファ) に置く。</summary>
    public const int InMemoryLimit = 1024 * 1024;

    /// <summary>1 回の読み込みのバイト数 (8 の倍数)。</summary>
    public const int ChunkSize = 1024 * 1024;

    private readonly string _tempDirectory;
    private readonly IVolumeInfoProvider? _volumes;

    public DataOperationRunner(string tempDirectory, IVolumeInfoProvider? volumes = null)
    {
        _tempDirectory = tempDirectory;
        _volumes = volumes;
    }

    /// <summary>ドキュメントの一時フォルダを使う。</summary>
    public static DataOperationRunner For(Document document, IVolumeInfoProvider? volumes = null) =>
        new(Path.Combine(document.Options.TempDirectory, document.Id.ToString("N")), volumes);

    /// <summary>対象範囲の合計の長さ (進捗の全体量)。</summary>
    public static long TotalBytes(IReadOnlyList<TargetRange> ranges) => ranges.Sum(r => r.Length);

    /// <summary>長時間処理として実行するか。</summary>
    public static bool IsLongRunning(IReadOnlyList<TargetRange> ranges) => TotalBytes(ranges) > InPlaceLimit;

    /// <summary>要素に満たないため変えない末尾のバイト数の合計 (EDIT-31 の仕様 8)。</summary>
    public static long TrailingBytes(IReadOnlyList<TargetRange> ranges, DataOperationSpec spec) =>
        spec.IsElementWise ? ranges.Sum(r => r.Length % spec.EffectiveSize) : 0;

    /// <summary>処理する要素の数 (増分で除数が 0 になるかの判定に使う。範囲ごとにやり直す場合は範囲ごとの最大)。</summary>
    public static long ProcessedElements(IReadOnlyList<TargetRange> ranges, DataOperationSpec spec)
    {
        long PerRange(TargetRange r)
        {
            long elements = r.Length / spec.EffectiveSize;
            long cycle = (long)spec.ProcessCount + spec.SkipCount;
            return elements / cycle * spec.ProcessCount + Math.Min(elements % cycle, spec.ProcessCount);
        }

        return ranges.Count == 0 ? 0
            : spec.IncrementScope == IncrementScope.Continuous ? ranges.Sum(PerRange) : ranges.Max(PerRange);
    }

    /// <summary>設定の誤り (対象範囲に応じた判定を含む)。</summary>
    public static DataOperationError Validate(IReadOnlyList<TargetRange> ranges, DataOperationSpec spec) =>
        spec.Validate(ProcessedElements(ranges, spec));

    /// <summary>
    /// 演算する。<paramref name="operation"/> があれば進捗を報告し、キャンセルされれば <see cref="OperationCanceledException"/> を投げる。
    /// </summary>
    /// <exception cref="TempSpaceException">一時領域の空きが足りない (実行前に調べる)。</exception>
    /// <exception cref="DataReadException">読めないバイトがある。</exception>
    public DataOperationResult Run(DocumentSnapshot snapshot, IReadOnlyList<TargetRange> ranges, DataOperationSpec spec,
        LongRunningOperation? operation = null)
    {
        if (spec.Category == DataOperationCategory.BitInsertDelete)
        {
            throw new ArgumentException("ビットの挿入・削除は BitShifter で行います。", nameof(spec));
        }

        DataOperationError error = Validate(ranges, spec);
        if (error != DataOperationError.None)
        {
            throw new ArgumentException($"演算の設定が正しくありません: {error}", nameof(spec));
        }

        ranges = SelectionRanges.Normalize(ranges);
        foreach (TargetRange r in ranges)
        {
            if (r.Offset < 0 || r.End > snapshot.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges), "対象範囲がドキュメントの範囲外です。");
            }
        }

        long total = TotalBytes(ranges);
        operation?.SetTotal(total);
        CheckSpace(ranges.Where(r => r.Length > InMemoryLimit).Sum(r => r.Length));
        var stats = new DataOperationStats();
        var processor = new ElementProcessor(spec, stats);
        var results = new List<RangeReplacement>();
        long done = 0;
        try
        {
            foreach (TargetRange range in ranges)
            {
                if (spec.IncrementScope == IncrementScope.PerRange)
                {
                    processor.ResetCounter();
                }

                processor.StartRange();
                EditContent content = spec.Kind == DataOperationKind.ReverseRange
                    ? Generate(range.Length, (dest, position) => ReverseRead(snapshot, range, position, dest, stats), operation, ref done)
                    : Generate(range.Length, (dest, position) =>
                    {
                        Read(snapshot, range.Offset + position, dest);
                        processor.Process(dest, range.Offset + position);
                    }, operation, ref done);
                results.Add(new RangeReplacement(range, content));
                stats.TrailingBytes += spec.IsElementWise ? range.Length % spec.EffectiveSize : 0;
            }
        }
        catch
        {
            TransformApplier.DisposeAll(results);
            throw;
        }

        return new DataOperationResult(results, stats);
    }

    /// <summary>プレビュー (先頭の範囲の先頭 64 バイト)。</summary>
    public static DataOperationPreview Preview(DocumentSnapshot snapshot, IReadOnlyList<TargetRange> ranges, DataOperationSpec spec)
    {
        ranges = SelectionRanges.Normalize(ranges);
        if (ranges.Count == 0 || spec.Category == DataOperationCategory.BitInsertDelete)
        {
            return new DataOperationPreview([], [], [], [], 0);
        }

        TargetRange range = ranges[0];
        int n = (int)Math.Min(64, range.Length);
        byte[] before = new byte[n];
        Read(snapshot, range.Offset, before);
        byte[] after = new byte[n];
        var stats = new DataOperationStats();
        if (spec.Kind == DataOperationKind.ReverseRange)
        {
            ReverseRead(snapshot, range, 0, after, stats);
        }
        else if (spec.Validate(ProcessedElements(ranges, spec)) == DataOperationError.None)
        {
            before.CopyTo(after, 0);
            var processor = new ElementProcessor(spec, stats);
            processor.StartRange();
            processor.Process(after, range.Offset);
        }
        else
        {
            before.CopyTo(after, 0);
        }

        int size = spec.IsElementWise ? spec.EffectiveSize : 1;
        var beforeValues = new List<string>();
        var afterValues = new List<string>();
        for (int i = 0; i + size <= n; i += size)
        {
            beforeValues.Add(FormatElement(before.AsSpan(i, size), spec));
            afterValues.Add(FormatElement(after.AsSpan(i, size), spec));
        }

        return new DataOperationPreview(before, after, beforeValues, afterValues, TrailingBytes(ranges, spec));
    }

    /// <summary>要素の値の 10 進表記 (プレビュー用。地域設定に依存しない)。</summary>
    public static string FormatElement(ReadOnlySpan<byte> bytes, DataOperationSpec spec)
    {
        ulong raw = ElementProcessor.ReadRaw(bytes, spec.BigEndian && spec.IsElementWise);
        int bits = bytes.Length * 8;
        if (spec.IsFloat)
        {
            return bits == 32
                ? BitConverter.Int32BitsToSingle((int)raw).ToString("R", CultureInfo.InvariantCulture)
                : BitConverter.Int64BitsToDouble((long)raw).ToString("R", CultureInfo.InvariantCulture);
        }

        if (spec.Type == ElementType.Signed && spec.Category is DataOperationCategory.Arithmetic or DataOperationCategory.Clamp
            or DataOperationCategory.ShiftRotate)
        {
            return ElementProcessor.SignExtend(raw, bits).ToString(CultureInfo.InvariantCulture);
        }

        return raw.ToString(CultureInfo.InvariantCulture);
    }

    // ---- 内部 ----

    /// <summary>範囲の長さの内容を、1 MiB ずつ <paramref name="fill"/> で作る (位置は範囲の先頭からのバイト数)。</summary>
    private EditContent Generate(long length, Action<Span<byte>, long> fill, LongRunningOperation? operation, ref long done)
    {
        if (length <= InMemoryLimit)
        {
            operation?.CancellationToken.ThrowIfCancellationRequested();
            byte[] data = new byte[length];
            fill(data, 0);
            done += length;
            operation?.Report(done);
            return EditContent.Bytes(data);
        }

        var writer = new TempContentWriter(_tempDirectory, length, "dataop");
        try
        {
            byte[] buffer = new byte[ChunkSize];
            long position = 0;
            while (position < length)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize, length - position);
                Span<byte> chunk = buffer.AsSpan(0, n);
                fill(chunk, position);
                writer.Write(chunk);
                position += n;
                done += n;
                operation?.Report(done);
            }

            return writer.Complete();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>範囲全体の反転: 結果の位置 <paramref name="position"/> からは、範囲の末尾から読んで逆順にしたもの (EDIT-35 の「巨大ファイル」)。</summary>
    private static void ReverseRead(DocumentSnapshot snapshot, TargetRange range, long position, Span<byte> destination, DataOperationStats stats)
    {
        long sourceEnd = range.End - position;
        Read(snapshot, sourceEnd - destination.Length, destination);
        destination.Reverse();
        stats.Changed |= range.Length >= 2;
    }

    private static void Read(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        ReadResult result = snapshot.Read(offset, destination);
        if (result.Unreadable.Count > 0)
        {
            throw new DataReadException(result.Unreadable[0].Offset);
        }

        if (result.BytesReturned < destination.Length)
        {
            throw new DataReadException(offset + result.BytesReturned);
        }
    }

    private void CheckSpace(long required)
    {
        if (_volumes is null || required <= 0)
        {
            return;
        }

        Directory.CreateDirectory(_tempDirectory);
        if (_volumes.GetVolume(_tempDirectory)?.AvailableFreeSpace is long available && available < required)
        {
            throw new TempSpaceException(required, available);
        }
    }
}

/// <summary>
/// 要素ごとの計算 (EDIT-31〜EDIT-36)。範囲の先頭から要素を数え、処理と飛ばし・オペランドの増分・鍵の位置を範囲をまたいで引き継ぐ。
/// 同じ値・増分なし・飛ばしなしのビット演算と鍵の XOR などはベクトル化した経路で処理する。
/// </summary>
internal sealed class ElementProcessor
{
    private readonly DataOperationSpec _spec;
    private readonly DataOperationStats _stats;
    private readonly int _size;
    private readonly int _bits;
    private readonly ulong _mask;
    private readonly bool _bigEndian;
    private readonly long _cycle;
    private readonly byte[]? _pattern;

    /// <summary>範囲の中の要素の番号 (処理と飛ばしの判定)。</summary>
    private long _elementIndex;

    /// <summary>処理した要素の数 (オペランドの増分、鍵の位置)。</summary>
    private long _processed;

    public ElementProcessor(DataOperationSpec spec, DataOperationStats stats)
    {
        _spec = spec;
        _stats = stats;
        _size = spec.EffectiveSize;
        _bits = _size * 8;
        _mask = _bits == 64 ? ulong.MaxValue : (1UL << _bits) - 1;
        _bigEndian = spec.BigEndian && spec.Category is not DataOperationCategory.Reorder;
        _cycle = (long)spec.ProcessCount + spec.SkipCount;
        _pattern = BuildPattern();
    }

    public void ResetCounter() => _processed = 0;

    public void StartRange() => _elementIndex = 0;

    /// <summary>
    /// <paramref name="data"/> (範囲の中の、要素の境界から始まる部分) を計算した値で書き換える。末尾の要素に満たないバイトは変えない。
    /// <paramref name="offset"/> はドキュメント上の位置 (「オフセット 0 から数える」鍵に使う)。
    /// </summary>
    public void Process(Span<byte> data, long offset)
    {
        int whole = data.Length / _size * _size;
        if (_pattern is not null)
        {
            ProcessPattern(data[..whole], offset);
            return;
        }

        for (int i = 0; i < whole; i += _size, _elementIndex++)
        {
            if (_elementIndex % _cycle >= _spec.ProcessCount)
            {
                continue;
            }

            Span<byte> element = data.Slice(i, _size);
            if (_spec.UsesKey)
            {
                long position = _spec.KeyOrigin == PatternOrigin.OffsetZero ? offset + i : _processed;
                byte key = KeyByte(position);
                byte before = element[0];
                element[0] = (byte)Bitwise(before, key, 0xFF);
                Count(before != element[0]);
                _processed++;
                continue;
            }

            switch (_spec.Category)
            {
                case DataOperationCategory.Reorder:
                    Reorder(element);
                    break;
                default:
                    ulong raw = ReadRaw(element, _bigEndian);
                    ulong result = _spec.IsFloat ? FloatElement(raw) : IntegerElement(raw);
                    if (result != raw)
                    {
                        WriteRaw(element, result, _bigEndian);
                    }

                    Count(result != raw);
                    break;
            }

            _processed++;
        }
    }

    private void Count(bool changed)
    {
        if (changed)
        {
            _stats.ChangedElements++;
            _stats.Changed = true;
        }
    }

    // ---- ベクトル化した経路: すべての要素を同じパターンとのビット演算で処理する ----

    /// <summary>
    /// 増分なし・飛ばしなしのビット演算 (数値のオペランド、または増分なしの鍵) は、バイト列のパターンとのバイトごとの演算にできる。
    /// </summary>
    private byte[]? BuildPattern()
    {
        if (_spec.Category != DataOperationCategory.Bitwise || _spec.SkipCount != 0)
        {
            return null;
        }

        if (_spec.UsesKey)
        {
            return _spec.KeyIncrement % 256 == 0 ? _spec.Key : null;
        }

        if (_spec.Increment != 0 && _spec.UsesOperand)
        {
            return null;
        }

        byte[] element = new byte[_size];
        WriteRaw(element, (ulong)((UInt128)_spec.Operand & _mask), _bigEndian);
        return element;
    }

    private void ProcessPattern(Span<byte> data, long offset)
    {
        byte[] pattern = _pattern!;
        long phase = _spec.UsesKey
            ? (_spec.KeyOrigin == PatternOrigin.OffsetZero ? offset : _processed) % pattern.Length
            : 0;
        long count = data.Length / _size;
        int done = 0;
        while (done < data.Length)
        {
            int n = (int)Math.Min(data.Length - done, pattern.Length - phase);
            Span<byte> slice = data.Slice(done, n);
            ReadOnlySpan<byte> key = pattern.AsSpan((int)phase, n);
            if (BitwiseSpan(slice, key))
            {
                _stats.Changed = true;
            }

            done += n;
            phase = 0;
        }

        _processed += count;
        _elementIndex += count;
    }

    /// <summary>バイトごとのビット演算。変わったバイトがあれば true。</summary>
    private bool BitwiseSpan(Span<byte> data, ReadOnlySpan<byte> key)
    {
        bool changed = false;
        int i = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            int width = Vector<byte>.Count;
            for (; i <= data.Length - width; i += width)
            {
                var x = new Vector<byte>(data.Slice(i, width));
                var k = new Vector<byte>(key.Slice(i, width));
                Vector<byte> r = _spec.Kind switch
                {
                    DataOperationKind.And => x & k,
                    DataOperationKind.Or => x | k,
                    DataOperationKind.Xor => x ^ k,
                    DataOperationKind.Not => ~x,
                    DataOperationKind.Nand => ~(x & k),
                    DataOperationKind.Nor => ~(x | k),
                    _ => ~(x ^ k),
                };
                if (r != x)
                {
                    changed = true;
                    _stats.ChangedElements += _size == 1 ? CountDiff(x, r) : 0;
                }

                r.CopyTo(data.Slice(i, width));
            }
        }

        for (; i < data.Length; i++)
        {
            byte before = data[i];
            data[i] = (byte)Bitwise(before, key[i], 0xFF);
            if (data[i] != before)
            {
                changed = true;
                _stats.ChangedElements += _size == 1 ? 1 : 0;
            }
        }

        return changed;
    }

    private static int CountDiff(Vector<byte> a, Vector<byte> b)
    {
        Vector<byte> eq = Vector.Equals(a, b);
        int same = 0;
        for (int j = 0; j < Vector<byte>.Count; j++)
        {
            same += eq[j] != 0 ? 1 : 0;
        }

        return Vector<byte>.Count - same;
    }

    // ---- 整数の要素 ----

    private ulong IntegerElement(ulong x)
    {
        return _spec.Category switch
        {
            DataOperationCategory.Arithmetic => Arithmetic(x),
            DataOperationCategory.Bitwise => Bitwise(x, OperandAt(_processed), _mask),
            DataOperationCategory.ShiftRotate => ShiftRotate(x),
            DataOperationCategory.Clamp => ClampInteger(x),
            _ => x,
        };
    }

    /// <summary>i 番目に処理する要素のオペランド (要素の大きさでラップする。EDIT-31 の仕様 6)。</summary>
    private ulong OperandAt(long i)
    {
        UInt128 value = (UInt128)_spec.Operand + (UInt128)_spec.Increment * (UInt128)(ulong)i;
        return (ulong)(value & _mask);
    }

    private ulong Bitwise(ulong x, ulong op, ulong mask) => _spec.Kind switch
    {
        DataOperationKind.And => x & op,
        DataOperationKind.Or => x | op,
        DataOperationKind.Xor => x ^ op,
        DataOperationKind.Not => ~x & mask,
        DataOperationKind.Nand => ~(x & op) & mask,
        DataOperationKind.Nor => ~(x | op) & mask,
        DataOperationKind.Xnor => ~(x ^ op) & mask,
        _ => x,
    };

    /// <summary>鍵の位置 <paramref name="position"/> のバイト (1 周するごとに増分を足す。EDIT-33 の仕様 4)。</summary>
    private byte KeyByte(long position)
    {
        byte[] key = _spec.Key!;
        long round = position / key.Length;
        return (byte)(key[position % key.Length] + round * _spec.KeyIncrement);
    }

    private ulong Arithmetic(ulong raw)
    {
        bool signed = _spec.Type == ElementType.Signed;
        Int128 x = signed ? (Int128)SignExtend(raw, _bits) : (Int128)raw;
        ulong opRaw = OperandAt(_processed);
        Int128 op = signed ? (Int128)SignExtend(opRaw, _bits) : (Int128)opRaw;
        Int128 min = signed ? -((Int128)1 << (_bits - 1)) : 0;
        Int128 max = signed ? ((Int128)1 << (_bits - 1)) - 1 : (Int128)_mask;
        Int128 r;
        switch (_spec.Kind)
        {
            case DataOperationKind.Add:
                r = x + op;
                break;
            case DataOperationKind.Subtract:
                r = x - op;
                break;
            case DataOperationKind.ReverseSubtract:
                r = op - x;
                break;
            case DataOperationKind.Multiply:
                if (!signed)
                {
                    // 符号なし 64 bit 同士の積は Int128 に収まらないため UInt128 で求める。
                    UInt128 product = (UInt128)raw * opRaw;
                    return product > _mask ? Overflowed((ulong)(product & _mask), max) : (ulong)product;
                }

                r = x * op;
                break;
            case DataOperationKind.Divide:
                r = x / op;
                break;
            case DataOperationKind.ReverseDivide:
                if (x == 0)
                {
                    _stats.ZeroSkipped++;
                    return raw;
                }

                r = op / x;
                break;
            case DataOperationKind.Modulo:
                r = x % op;
                break;
            case DataOperationKind.Negate:
                r = -x;
                break;
            case DataOperationKind.Abs:
                r = Int128.Abs(x);
                break;
            default:
                return raw;
        }

        if (r < min || r > max)
        {
            return Overflowed((ulong)(UInt128)(r & (Int128)_mask), r < min ? min : max);
        }

        return (ulong)(UInt128)(r & (Int128)_mask);
    }

    /// <summary>あふれた: ラップならラップした値、飽和なら型の最小値・最大値 (EDIT-32 の仕様 2)。</summary>
    private ulong Overflowed(ulong wrapped, Int128 limit)
    {
        _stats.Overflows++;
        return _spec.Overflow == OverflowMode.Wrap ? wrapped : (ulong)(UInt128)(limit & (Int128)_mask);
    }

    private ulong ShiftRotate(ulong x)
    {
        int bits = _bits;
        switch (_spec.Kind)
        {
            case DataOperationKind.ShiftLeft:
                return _spec.ShiftBits >= bits ? 0 : (x << _spec.ShiftBits) & _mask;
            case DataOperationKind.ShiftRightLogical:
                return _spec.ShiftBits >= bits ? 0 : x >> _spec.ShiftBits;
            case DataOperationKind.ShiftRightArithmetic:
            {
                long signed = SignExtend(x, bits);
                int n = Math.Min(_spec.ShiftBits, 63);
                return (ulong)(signed >> n) & _mask;
            }

            default:
            {
                // 段階的なローテート: i 番目の要素の回転量は (ビット数 + i × 増分) mod 要素のビット数 (EDIT-34 の仕様 3)。
                Int128 amount = ((Int128)_spec.ShiftBits + (Int128)_processed * _spec.RotateIncrement) % bits;
                if (amount < 0)
                {
                    amount += bits;
                }

                int n = (int)amount;
                if (n == 0)
                {
                    return x;
                }

                if (_spec.Kind == DataOperationKind.RotateRight)
                {
                    n = bits - n;
                }

                return ((x << n) | (x >> (bits - n))) & _mask;
            }
        }
    }

    private ulong ClampInteger(ulong raw)
    {
        bool signed = _spec.Type == ElementType.Signed;
        Int128 x = signed ? (Int128)SignExtend(raw, _bits) : (Int128)raw;
        if (_spec.Min is { } min && x < min)
        {
            return (ulong)(UInt128)(min & (Int128)_mask);
        }

        if (_spec.Max is { } max && x > max)
        {
            return (ulong)(UInt128)(max & (Int128)_mask);
        }

        return raw;
    }

    // ---- 浮動小数点の要素 ----

    private ulong FloatElement(ulong raw)
    {
        bool single = _size == 4;
        double x = single ? BitConverter.Int32BitsToSingle((int)raw) : BitConverter.Int64BitsToDouble((long)raw);
        if (_spec.Kind == DataOperationKind.Clamp)
        {
            // NaN は変えない (EDIT-36 の仕様 2)。
            if (double.IsNaN(x))
            {
                return raw;
            }

            double clamped = _spec.FloatMin is double fmin && x < fmin ? fmin : _spec.FloatMax is double fmax && x > fmax ? fmax : x;
            return clamped == x ? raw : ToRaw(clamped, single);
        }

        double op = _spec.FloatOperand + _spec.FloatIncrement * _processed;
        if (single)
        {
            op = (float)op;
        }

        double r = _spec.Kind switch
        {
            DataOperationKind.Add => x + op,
            DataOperationKind.Subtract => x - op,
            DataOperationKind.ReverseSubtract => op - x,
            DataOperationKind.Multiply => x * op,
            DataOperationKind.Divide => x / op,
            DataOperationKind.ReverseDivide => op / x,
            DataOperationKind.Negate => -x,
            DataOperationKind.Abs => Math.Abs(x),
            _ => x,
        };
        ulong result = ToRaw(r, single);
        double stored = single ? BitConverter.Int32BitsToSingle((int)result) : BitConverter.Int64BitsToDouble((long)result);
        if (double.IsNaN(stored))
        {
            _stats.NaNs++;
        }
        else if (double.IsInfinity(stored))
        {
            _stats.Infinities++;
        }

        return result;
    }

    private static ulong ToRaw(double value, bool single) =>
        single ? (uint)BitConverter.SingleToInt32Bits((float)value) : (ulong)BitConverter.DoubleToInt64Bits(value);

    // ---- 並べ替え (EDIT-35) ----

    private void Reorder(Span<byte> element)
    {
        byte[] before = element.ToArray();
        switch (_spec.Kind)
        {
            case DataOperationKind.ByteSwap16 or DataOperationKind.ByteSwap32 or DataOperationKind.ByteSwap64:
                element.Reverse();
                break;
            case DataOperationKind.WordSwap32:
                (element[0], element[1], element[2], element[3]) = (element[2], element[3], element[0], element[1]);
                break;
            case DataOperationKind.ReverseBits:
                element[0] = ReverseBits(element[0]);
                break;
            case DataOperationKind.SwapNibbles:
                element[0] = (byte)((element[0] << 4) | (element[0] >> 4));
                break;
        }

        Count(!element.SequenceEqual(before));
    }

    public static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        return (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
    }

    // ---- 要素の読み書き ----

    public static ulong ReadRaw(ReadOnlySpan<byte> element, bool bigEndian)
    {
        ulong value = 0;
        if (bigEndian)
        {
            foreach (byte b in element)
            {
                value = (value << 8) | b;
            }
        }
        else
        {
            for (int i = element.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | element[i];
            }
        }

        return value;
    }

    public static void WriteRaw(Span<byte> element, ulong value, bool bigEndian)
    {
        int n = element.Length;
        for (int i = 0; i < n; i++)
        {
            element[bigEndian ? n - 1 - i : i] = (byte)(value >> (8 * i));
        }
    }

    public static long SignExtend(ulong raw, int bits) => bits >= 64 ? (long)raw : (long)(raw << (64 - bits)) >> (64 - bits);
}
