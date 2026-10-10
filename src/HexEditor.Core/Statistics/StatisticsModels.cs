using System.Buffers.Binary;
using HexEditor.Core.Hashing;

namespace HexEditor.Core.Statistics;

/// <summary>統計の要素の型 (ANA-10 の仕様 2)。</summary>
public enum ElementType
{
    U8,
    S8,
    U16,
    S16,
    U32,
    S32,
    U64,
    S64,
    F32,
    F64,
}

/// <summary>要素の型の性質。</summary>
public static class ElementTypes
{
    public static readonly IReadOnlyList<ElementType> All = Enum.GetValues<ElementType>();

    public static int Size(ElementType type) => type switch
    {
        ElementType.U8 or ElementType.S8 => 1,
        ElementType.U16 or ElementType.S16 => 2,
        ElementType.U32 or ElementType.S32 or ElementType.F32 => 4,
        _ => 8,
    };

    public static bool IsFloat(ElementType type) => type is ElementType.F32 or ElementType.F64;

    public static bool IsSigned(ElementType type) => type is ElementType.S8 or ElementType.S16 or ElementType.S32 or ElementType.S64;

    /// <summary>ヒストグラム (値ごとの件数) から中央値・四分位数・最頻値を正確に求める型 (ANA-11 の仕様 3)。</summary>
    public static bool IsSmall(ElementType type) => Size(type) <= 2;

    /// <summary>表示名 (<c>u8</c>、<c>f64</c> など。訳さない)。</summary>
    public static string Name(ElementType type) => type.ToString().ToLowerInvariant();

    public static bool TryParse(string? text, out ElementType type) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type);

    /// <summary>整数型の最小値 (<see cref="IsSmall"/> の型の値ごとのビンの先頭)。</summary>
    public static long MinValue(ElementType type) => type switch
    {
        ElementType.S8 => sbyte.MinValue,
        ElementType.S16 => short.MinValue,
        _ => 0,
    };

    /// <summary>整数の要素を読む (u64 は Int128 で負にならない)。</summary>
    public static Int128 ReadInteger(ElementType type, ReadOnlySpan<byte> s, bool bigEndian) => type switch
    {
        ElementType.U8 => s[0],
        ElementType.S8 => (sbyte)s[0],
        ElementType.U16 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
        ElementType.S16 => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
        ElementType.U32 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
        ElementType.S32 => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
        ElementType.U64 => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
        ElementType.S64 => bigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>要素を double として読む (浮動小数点はそのまま)。</summary>
    public static double ReadDouble(ElementType type, ReadOnlySpan<byte> s, bool bigEndian) => type switch
    {
        ElementType.F32 => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
        ElementType.F64 => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
        _ => (double)ReadInteger(type, s, bigEndian),
    };

    /// <summary>値をドキュメント上のバイト列に戻す (検索に使う。整数型のみ)。</summary>
    public static byte[] Encode(ElementType type, Int128 value, bool bigEndian)
    {
        byte[] bytes = new byte[Size(type)];
        ulong raw = (ulong)(value & ulong.MaxValue);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[bigEndian ? bytes.Length - 1 - i : i] = (byte)(raw >> (8 * i));
        }

        return bytes;
    }
}

/// <summary>要素の読み方 (型・エンディアン・ストライド。ANA-10 の仕様 2)。</summary>
public sealed record ElementSpec(ElementType Type = ElementType.U8, bool BigEndian = false, int Stride = 0)
{
    public const int MaxStride = 65_536;

    public static readonly ElementSpec Bytes = new();

    public int Size => ElementTypes.Size(Type);

    /// <summary>要素の間隔。1 バイトの型と未指定は型のサイズ。</summary>
    public int EffectiveStride => Size == 1 || Stride <= 0 ? Size : Math.Clamp(Stride, 1, MaxStride);

    /// <summary>長さ <paramref name="length"/> の対象に含まれる要素の数。</summary>
    public long ElementCount(long length) => length < Size ? 0 : ((length - Size) / EffectiveStride) + 1;

    /// <summary>末尾の要素に満たないバイト数 (端数。ANA-10 の仕様 2)。</summary>
    public long Remainder(long length) => Math.Max(0, length - (ElementCount(length) * EffectiveStride));
}

/// <summary>ビンの決め方 (ANA-10 の仕様 3)。</summary>
public sealed record BinSpec
{
    public const int DefaultCount = 256;
    public const int MaxCount = 65_536;

    /// <summary>ビンの数 (2〜65,536)。u8 / s8 と値ごとのビンでは使わない。</summary>
    public int Count { get; init; } = DefaultCount;

    /// <summary>手入力の範囲 (両方あるときだけ使う)。null なら「自動」(最小値〜最大値)。</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>u16 / s16 の「値ごと (65,536 個)」。</summary>
    public bool PerValue { get; init; }

    public bool IsManual => Min is double min && Max is double max && max >= min;
}

/// <summary>対象範囲を連結した論理的な並び (06 の 0.1: マルチ選択はオフセットの小さい順に連結する)。</summary>
public sealed class LogicalRanges
{
    private readonly long[] _starts;

    public LogicalRanges(IReadOnlyList<HashRange> ranges)
    {
        Ranges = ranges;
        _starts = new long[ranges.Count];
        long at = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            _starts[i] = at;
            at += ranges[i].Length;
        }

        Length = at;
    }

    /// <summary>正規化した範囲 (オフセット順で重ならない)。</summary>
    public IReadOnlyList<HashRange> Ranges { get; }

    public long Length { get; }

    /// <summary>範囲の先頭 (表示用)。</summary>
    public long Start => Ranges.Count == 0 ? 0 : Ranges[0].Offset;

    /// <summary>範囲の最後のバイトの次 (表示用)。</summary>
    public long End => Ranges.Count == 0 ? 0 : Ranges[^1].End;

    /// <summary>ドキュメント全体 (オフセット 0 から 1 つの範囲) か。</summary>
    public bool IsWhole(long documentLength) => Ranges.Count == 1 && Ranges[0].Offset == 0 && Ranges[0].Length == documentLength;

    /// <summary>論理位置をドキュメント上のオフセットにする (末尾は最後の範囲の終わり)。</summary>
    public long ToDocument(long logical)
    {
        if (Ranges.Count == 0)
        {
            return 0;
        }

        if (logical >= Length)
        {
            return Ranges[^1].End;
        }

        int i = Array.BinarySearch(_starts, Math.Max(0, logical));
        if (i < 0)
        {
            i = ~i - 1;
        }

        return Ranges[i].Offset + (logical - _starts[i]);
    }

    /// <summary>ドキュメント上のオフセットを論理位置にする。範囲外なら null。</summary>
    public long? ToLogical(long offset)
    {
        for (int i = 0; i < Ranges.Count; i++)
        {
            if (offset >= Ranges[i].Offset && offset < Ranges[i].End)
            {
                return _starts[i] + (offset - Ranges[i].Offset);
            }
        }

        return null;
    }

    /// <summary>論理範囲 [from, to) に対応するドキュメント上の範囲。</summary>
    public IEnumerable<HashRange> ToDocumentRanges(long from, long to)
    {
        for (int i = 0; i < Ranges.Count && from < to; i++)
        {
            long s = _starts[i];
            long e = s + Ranges[i].Length;
            long a = Math.Max(from, s);
            long b = Math.Min(to, e);
            if (a < b)
            {
                yield return new HashRange(Ranges[i].Offset + (a - s), b - a);
            }
        }
    }

    /// <summary>連結の境目にある論理位置 (範囲の 2 つ目以降の先頭)。</summary>
    public IEnumerable<long> Joins => _starts.Skip(1);
}

/// <summary>読めなかった範囲の集計 (ANA-10 の「エラー」: 読み込めなかった範囲: 2 か所、8,192 バイト)。</summary>
public sealed class UnreadableSummary
{
    private readonly List<HashRange> _ranges = [];

    public IReadOnlyList<HashRange> Ranges => _ranges;

    public int Count => _ranges.Count;

    public long Bytes => _ranges.Sum(r => r.Length);

    /// <summary>ドキュメント上の範囲を加える (隣り合う範囲はまとめる)。</summary>
    internal void Add(long offset, long length)
    {
        if (length <= 0)
        {
            return;
        }

        if (_ranges.Count > 0 && _ranges[^1].End == offset)
        {
            _ranges[^1] = _ranges[^1] with { Length = _ranges[^1].Length + length };
            return;
        }

        _ranges.Add(new HashRange(offset, length));
    }

    internal UnreadableSummary Clone()
    {
        var copy = new UnreadableSummary();
        copy._ranges.AddRange(_ranges);
        return copy;
    }
}
