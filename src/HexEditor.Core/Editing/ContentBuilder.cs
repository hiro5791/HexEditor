using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Editing;

/// <summary>一時領域の空きが足りない (EDIT-29 の「エラー」)。</summary>
public sealed class TempSpaceException(long required, long available)
    : IOException($"一時領域が {required:N0} バイト必要ですが、空きは {available:N0} バイトです。")
{
    public long Required { get; } = required;

    public long Available { get; } = available;
}

/// <summary>
/// 塗りつぶし・挿入の内容 (<see cref="FillSpec"/>) から <see cref="EditContent"/> を作る (EDIT-29 の「巨大ファイル・長時間処理」)。
/// <list type="number">
/// <item>バイト値・4 KiB 以下のパターン・テキスト・周期の短いカウンタ: 生成ピース 1 つ (長さに関係なく一定時間)。</item>
/// <item>0〜255 の乱数: 乱数の生成ピース (ENG-03 の SplitMix64)。同じシードなら同じ内容。</item>
/// <item>それ以外 (範囲付きの乱数、暗号論的乱数、カウンタ、ファイル、長いパターン): 実データを作る。1 MiB を超える場合は一時ファイルに
/// 書き出す長時間処理で、キャンセル・失敗したら一時ファイルを消す (ドキュメントは変わらない)。</item>
/// </list>
/// </summary>
public sealed class ContentBuilder
{
    /// <summary>これ以下の実データは一時ファイルを作らずメモリ (追加バッファ) に置く。</summary>
    public const int InMemoryLimit = 1024 * 1024;

    private readonly string _tempDirectory;
    private readonly IVolumeInfoProvider? _volumes;
    private readonly Func<string, IByteSource> _openFile;

    /// <param name="tempDirectory">一時ファイルの置き場所 (ドキュメントの一時フォルダ)。</param>
    /// <param name="volumes">空き容量を調べる。null なら調べない。</param>
    /// <param name="openFile">ファイルを開く (テストで遅いデータソースに差し替える)。</param>
    public ContentBuilder(string tempDirectory, IVolumeInfoProvider? volumes = null, Func<string, IByteSource>? openFile = null)
    {
        _tempDirectory = tempDirectory;
        _volumes = volumes;
        _openFile = openFile ?? (path => FileByteSource.Open(path));
    }

    /// <summary>ドキュメントの一時フォルダを使う。</summary>
    public static ContentBuilder For(Document document, IVolumeInfoProvider? volumes = null, Func<string, IByteSource>? openFile = null) =>
        new(Path.Combine(document.Options.TempDirectory, document.Id.ToString("N")), volumes, openFile);

    /// <summary>
    /// 実データを作る必要がある (長時間処理になりうる) か。偽なら UI スレッドで <see cref="Build"/> してよい (100 ms 以内)。
    /// ファイルは開いてみないと分からないため常に真 (同じファイルの参照も含む)。
    /// </summary>
    public static bool NeedsGeneration(FillSpec spec, long length) => spec.Kind switch
    {
        FillKind.Byte => false,
        FillKind.HexPattern or FillKind.Text or FillKind.Clipboard when spec.Pattern is { } p =>
            p.Length > GeneratedData.MaxPatternLength && spec.Repeat && length > InMemoryLimit,
        FillKind.Random => !(spec.RandomMin == 0 && spec.RandomMax == 255) && length > InMemoryLimit,
        FillKind.CryptoRandom => length > InMemoryLimit,
        FillKind.Counter => CounterPeriodBytes(spec) is null && length > InMemoryLimit,
        _ => true,
    };

    /// <summary>
    /// <paramref name="rangeStart"/> から <paramref name="length"/> バイトに書く内容を作る。<paramref name="document"/> は
    /// ファイルの内容が開いているファイルと同じかを調べるのに使う (同じなら元データを参照する。EDIT-30 の仕様 2)。
    /// </summary>
    /// <exception cref="TempSpaceException">一時領域の空きが足りない (実行前に調べる)。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた。作りかけの一時ファイルは消す。</exception>
    /// <exception cref="IOException">ファイルを読めない。</exception>
    public EditContent Build(FillSpec spec, long rangeStart, long length, Document? document = null, LongRunningOperation? operation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (spec.Validate() is { } error)
        {
            throw new ArgumentException($"内容の指定が正しくありません: {error}", nameof(spec));
        }

        operation?.SetTotal(length);
        switch (spec.Kind)
        {
            case FillKind.Byte:
                return EditContent.Fill(spec.Value, length);

            case FillKind.HexPattern or FillKind.Text:
                return FromPattern(spec.Pattern!, spec, rangeStart, length, operation);

            case FillKind.Clipboard when spec.Pattern is { } bytes:
                return FromPattern(bytes, spec, rangeStart, length, operation);

            case FillKind.Clipboard:
                return FromSource(spec.ClipboardSource!, 0, spec.ClipboardSource!.Length, spec, rangeStart, length, operation, null);

            case FillKind.Random when spec.RandomMin == 0 && spec.RandomMax == 255:
                // 全範囲の乱数は生成ピースで表す (ENG-03)。シードを指定しなければ毎回変わる。
                return EditContent.Random(spec.Seed ?? RandomSeed(), length);

            case FillKind.Random:
            {
                var rng = new Xoshiro256StarStar(spec.Seed ?? RandomSeed());
                int span = spec.RandomMax - spec.RandomMin + 1;
                return Generate(length, operation, dest => rng.FillBytes(dest, spec.RandomMin, span));
            }

            case FillKind.CryptoRandom:
                return Generate(length, operation, dest => RandomNumberGenerator.Fill(dest));

            case FillKind.Counter:
                if (CounterPeriodBytes(spec) is int period)
                {
                    byte[] cycle = new byte[period];
                    new CounterGenerator(spec).Fill(cycle);
                    return EditContent.Pattern(cycle, length);
                }

                var counter = new CounterGenerator(spec);
                return Generate(length, operation, counter.Fill);

            default:
                return FromFile(spec, rangeStart, length, document, operation);
        }
    }

    /// <summary>
    /// 周期が短く (4 KiB 以下)、生成ピースで表せるカウンタ (ラップ) の 1 周期のバイト数。表せなければ null。
    /// </summary>
    public static int? CounterPeriodBytes(FillSpec spec)
    {
        if (spec.CounterOverflow != CounterOverflow.Wrap || spec.CounterSize > 2)
        {
            return null;
        }

        int bits = spec.CounterSize * 8;
        ulong modulus = 1UL << bits;
        ulong step = (ulong)spec.CounterStep & (modulus - 1);
        ulong elements = step == 0 ? 1 : modulus / (ulong)BigInteger.GreatestCommonDivisor(step, modulus);
        ulong bytes = elements * (ulong)spec.CounterSize;
        return bytes <= GeneratedData.MaxPatternLength ? (int)bytes : null;
    }

    private EditContent FromPattern(byte[] pattern, FillSpec spec, long rangeStart, long length, LongRunningOperation? operation)
    {
        long phase = spec.Origin == PatternOrigin.OffsetZero ? rangeStart % pattern.Length : 0;
        if (!spec.Repeat)
        {
            // 1 回だけ書いて、残りは変えない (EDIT-29 の仕様 3)。
            int n = (int)Math.Min(pattern.Length, length);
            return EditContent.Bytes(pattern[..n]);
        }

        if (pattern.Length <= GeneratedData.MaxPatternLength)
        {
            return EditContent.Pattern(pattern, length, phase);
        }

        if (length <= pattern.Length - phase)
        {
            return EditContent.Bytes(pattern[(int)phase..(int)(phase + length)]);
        }

        // 長いパターンは実データを作る (EDIT-29 の「巨大ファイル」3)。
        long position = phase;
        return Generate(length, operation, dest =>
        {
            GeneratedData.FillPattern(pattern, position, dest);
            position = (position + dest.Length) % pattern.Length;
        });
    }

    private EditContent FromFile(FillSpec spec, long rangeStart, long length, Document? document, LongRunningOperation? operation)
    {
        string path = Path.GetFullPath(spec.FilePath!);
        bool same = document?.Source is FileByteSource open && string.Equals(open.Path, path, StringComparison.OrdinalIgnoreCase);
        IByteSource source = same ? document!.Source : _openFile(path);
        try
        {
            long available = Math.Max(0, source.Length - spec.FileOffset);
            long count = Math.Min(spec.FileLength ?? available, available);
            if (spec.FileOffset > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(spec), "開始位置がファイルの末尾を超えています。");
            }

            return FromSource(source, spec.FileOffset, count, spec, rangeStart, length, operation, same ? document : null);
        }
        finally
        {
            if (!same)
            {
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// データソースの [offset, offset + count) を内容にする。挿入した後にデータソースが変わっても内容が変わらないよう、実データを
    /// 一時ファイルにコピーする (EDIT-30 の「巨大ファイル」)。<paramref name="sameDocument"/> なら元データを参照する。
    /// </summary>
    private EditContent FromSource(IByteSource source, long offset, long count, FillSpec spec, long rangeStart, long length,
        LongRunningOperation? operation, Document? sameDocument)
    {
        if (count == 0)
        {
            return EditContent.Bytes([]);
        }

        if (!spec.Repeat || length <= count)
        {
            long n = Math.Min(count, length);
            if (sameDocument is not null)
            {
                return EditContent.Original(offset, n);
            }

            return Generate(n, operation, CopyFrom(source, offset));
        }

        if (count <= GeneratedData.MaxPatternLength)
        {
            byte[] pattern = new byte[count];
            ReadFully(source, offset, pattern);
            return FromPattern(pattern, spec, rangeStart, length, operation);
        }

        // 長い内容の繰り返しは実データを作る。
        long phase = spec.Origin == PatternOrigin.OffsetZero ? rangeStart % count : 0;
        long position = phase;
        return Generate(length, operation, dest =>
        {
            int done = 0;
            while (done < dest.Length)
            {
                int n = (int)Math.Min(dest.Length - done, count - position);
                ReadFully(source, offset + position, dest.Slice(done, n));
                done += n;
                position = (position + n) % count;
            }
        });
    }

    private static Action<Span<byte>> CopyFrom(IByteSource source, long offset)
    {
        long position = offset;
        return dest =>
        {
            ReadFully(source, position, dest);
            position += dest.Length;
        };
    }

    /// <summary>読み込み中の I/O エラー・読めない範囲は中止する (EDIT-30 の「エラー」)。</summary>
    private static void ReadFully(IByteSource source, long offset, Span<byte> destination)
    {
        ReadResult result = source.Read(offset, destination);
        if (result.BytesReturned < destination.Length || !result.IsComplete)
        {
            throw new IOException($"オフセット 0x{offset:X} からを読めません。");
        }
    }

    /// <summary>実データを作る。1 MiB 以下はメモリに、それを超えるものは一時ファイルに書く。</summary>
    private EditContent Generate(long length, LongRunningOperation? operation, Action<Span<byte>> generate)
    {
        if (length <= InMemoryLimit)
        {
            byte[] data = new byte[length];
            generate(data);
            operation?.Report(length);
            return EditContent.Bytes(data);
        }

        CheckSpace(length);
        var writer = new TempContentWriter(_tempDirectory, length, "fill");
        try
        {
            writer.WriteAll(generate, operation);
            return writer.Complete();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>一時領域の空きを確かめる (EDIT-29 の「巨大ファイル」3)。</summary>
    private void CheckSpace(long required)
    {
        if (_volumes is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDirectory);
        if (_volumes.GetVolume(_tempDirectory)?.AvailableFreeSpace is long available && available < required)
        {
            throw new TempSpaceException(required, available);
        }
    }

    private static ulong RandomSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}

/// <summary>擬似乱数 xoshiro256** (EDIT-29 の乱数。シードから SplitMix64 で状態を作る)。</summary>
public sealed class Xoshiro256StarStar
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256StarStar(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public ulong Next()
    {
        unchecked
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }
    }

    /// <summary>[min, min + span) の一様な値で埋める (偏りが出ないよう、余りの部分は捨てて引き直す)。</summary>
    public void FillBytes(Span<byte> destination, byte min, int span)
    {
        int limit = 256 - 256 % span;
        int i = 0;
        while (i < destination.Length)
        {
            ulong word = Next();
            for (int k = 0; k < 8 && i < destination.Length; k++, word >>= 8)
            {
                int b = (int)(word & 0xFF);
                if (b < limit)
                {
                    destination[i++] = (byte)(min + b % span);
                }
            }
        }
    }

    private static ulong SplitMix(ref ulong x)
    {
        unchecked
        {
            ulong z = x += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

/// <summary>カウンタの値を順に書く (EDIT-29 の仕様 2 のカウンタ)。要素の途中で範囲が終わる場合は、その要素の先頭側だけを書く。</summary>
internal sealed class CounterGenerator(FillSpec spec)
{
    private readonly int _size = spec.CounterSize;
    private long _index;
    private int _byteInElement;
    private readonly byte[] _element = new byte[8];

    public void Fill(Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            if (_byteInElement == 0)
            {
                WriteElement(Value(_index));
            }

            destination[i] = _element[_byteInElement];
            if (++_byteInElement == _size)
            {
                _byteInElement = 0;
                _index++;
            }
        }
    }

    private Int128 Value(long index)
    {
        Int128 value = spec.CounterStart + (Int128)index * spec.CounterStep;
        int bits = _size * 8;
        if (spec.CounterOverflow == CounterOverflow.Saturate)
        {
            Int128 min = spec.CounterSigned ? -((Int128)1 << (bits - 1)) : 0;
            Int128 max = spec.CounterSigned ? ((Int128)1 << (bits - 1)) - 1 : ((Int128)1 << bits) - 1;
            return Int128.Clamp(value, min, max);
        }

        return value;
    }

    private void WriteElement(Int128 value)
    {
        ulong raw = (ulong)(UInt128)(value & ulong.MaxValue);
        for (int k = 0; k < _size; k++)
        {
            int shift = spec.CounterBigEndian ? (_size - 1 - k) * 8 : k * 8;
            _element[k] = (byte)(raw >> shift);
        }
    }
}
