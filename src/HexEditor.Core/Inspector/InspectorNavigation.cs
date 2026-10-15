namespace HexEditor.Core.Inspector;

/// <summary>「ポインタとして移動」の解釈の方法 (INSP-20 の仕様 2)。</summary>
public enum PointerMode
{
    /// <summary>値をドキュメントのオフセットとする。</summary>
    AbsoluteOffset,

    /// <summary>値を表示上のアドレスとし、ベースアドレスを引いてオフセットにする。</summary>
    AbsoluteAddress,

    /// <summary>起点 + 値。</summary>
    RelativeToCursor,

    /// <summary>起点 + 型のサイズ + 値 (x86 の相対分岐・RIP 相対など)。</summary>
    RelativeToNext,

    /// <summary>入力式で指定した基準 + 値。</summary>
    RelativeToBase,
}

/// <summary>ポインタとしての移動先の計算の結果。<see cref="Target"/> はドキュメントの範囲外でも計算した値 (InfoBar に出す)。</summary>
public readonly record struct PointerTarget(Int128 Target, bool InRange);

/// <summary>
/// インスペクタの整数の行を位置として使う移動 (INSP-20 のポインタ、INSP-21 の配列モード) の計算。UI に依存しない。
/// </summary>
public static class InspectorNavigation
{
    /// <summary>「ポインタとして移動」の対象になる行か (8〜64 bit の整数と可変長整数。INSP-20 の仕様 1)。</summary>
    public static bool IsPointerType(string typeId) => typeId is InspectorTypes.Int8 or InspectorTypes.UInt8 or InspectorTypes.Int16
        or InspectorTypes.UInt16 or InspectorTypes.Int24 or InspectorTypes.UInt24 or InspectorTypes.Int32 or InspectorTypes.UInt32
        or InspectorTypes.Int48 or InspectorTypes.UInt48 or InspectorTypes.Int64 or InspectorTypes.UInt64
        or InspectorTypes.ULeb128 or InspectorTypes.SLeb128 or InspectorTypes.SqliteVarint;

    /// <summary>
    /// 行の値を整数として読む (符号ありの型は負の値になる。INSP-20 の仕様 3)。読めない (データが足りない・可変長整数が不完全) なら null。
    /// <paramref name="size"/> は値が使ったバイト数 (可変長整数は実際の長さ)。
    /// </summary>
    public static Int128? ReadInteger(string typeId, ReadOnlySpan<byte> data, Endianness endian, out int size)
    {
        size = 0;
        InspectorType? type = InspectorTypes.Find(typeId);
        if (type is null || !IsPointerType(typeId))
        {
            return null;
        }

        switch (typeId)
        {
            case InspectorTypes.ULeb128:
            case InspectorTypes.SLeb128:
            {
                (ulong value, int length, bool overflow, bool incomplete) = InspectorDecoder.ReadLeb128(data, typeId == InspectorTypes.SLeb128);
                if (incomplete || overflow)
                {
                    return null;
                }

                size = length;
                return typeId == InspectorTypes.SLeb128 ? (long)value : value;
            }

            case InspectorTypes.SqliteVarint:
            {
                (ulong value, int length, bool incomplete) = InspectorDecoder.ReadSqliteVarint(data);
                if (incomplete)
                {
                    return null;
                }

                size = length;
                return (long)value;
            }
        }

        if (data.Length < type.Size)
        {
            return null;
        }

        size = type.Size;
        ulong raw = InspectorDecoder.ReadBits(data[..type.Size], endian);
        bool signed = typeId.StartsWith("int", StringComparison.Ordinal);
        return signed ? InspectorDecoder.SignExtend(raw, type.Size) : raw;
    }

    /// <summary>
    /// 移動先を求める (INSP-20 の仕様 2)。<paramref name="origin"/> は起点、<paramref name="size"/> は値のバイト数、
    /// <paramref name="baseAddress"/> は表示のベースアドレス、<paramref name="relativeBase"/> は「基準を指定して相対」の基準。
    /// 範囲 (0〜<paramref name="maxCursor"/>) の外なら <see cref="PointerTarget.InRange"/> が false (仕様 5)。
    /// </summary>
    public static PointerTarget Resolve(PointerMode mode, Int128 value, long origin, int size, ulong baseAddress, long relativeBase, long maxCursor)
    {
        Int128 target = mode switch
        {
            PointerMode.AbsoluteOffset => value,
            PointerMode.AbsoluteAddress => value - baseAddress,
            PointerMode.RelativeToCursor => origin + value,
            PointerMode.RelativeToNext => origin + size + value,
            _ => relativeBase + value,
        };
        return new PointerTarget(target, target >= 0 && target <= maxCursor);
    }
}

/// <summary>
/// 配列モード (INSP-21): 入ったときの起点を要素 0 とし、間隔 (既定は型のサイズ) で前後の要素へ移る。
/// </summary>
public sealed class InspectorArrayMode
{
    public InspectorArrayMode(string typeId, long origin, long stride)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        TypeId = typeId;
        Origin = origin;
        Stride = stride;
    }

    /// <summary>選んだ型 (強調する行)。</summary>
    public string TypeId { get; }

    /// <summary>要素 0 の位置。</summary>
    public long Origin { get; }

    /// <summary>要素の間隔 (バイト)。1 以上。</summary>
    public long Stride { get; set; }

    /// <summary>
    /// カーソルの位置の要素番号 (仕様 3)。境界にない (割り切れない) なら null。要素 0 より前は負の番号。
    /// </summary>
    public long? IndexOf(long cursor)
    {
        long delta = cursor - Origin;
        return delta % Stride == 0 ? delta / Stride : null;
    }

    /// <summary>要素の位置。桁あふれ・負の位置なら null。</summary>
    public long? OffsetOf(long index)
    {
        Int128 offset = Origin + (Int128)index * Stride;
        return offset >= 0 && offset <= long.MaxValue ? (long)offset : null;
    }

    /// <summary>
    /// 前 / 次の要素の位置 (カーソルが境界にない場合は、その方向で最も近い境界)。範囲 (0〜<paramref name="maxCursor"/>) の外なら null
    /// (ボタンを無効にする。INSP-21 の「エラー」)。
    /// </summary>
    public long? Step(long cursor, int direction, long maxCursor)
    {
        long delta = cursor - Origin;
        long index = delta >= 0 ? delta / Stride : -((-delta + Stride - 1) / Stride);
        long current = Origin + index * Stride;
        long nextIndex = direction > 0 ? index + 1 : current == cursor ? index - 1 : index;
        return OffsetOf(nextIndex) is { } target && target <= maxCursor ? target : null;
    }

    /// <summary>Hex ビューに引く要素の境界 (表示範囲 [start, end) の中だけ。仕様 5)。</summary>
    public IEnumerable<long> Boundaries(long start, long end)
    {
        long delta = start - Origin;
        long index = delta >= 0 ? (delta + Stride - 1) / Stride : -(-delta / Stride);
        for (long? at = OffsetOf(index); at is { } a && a < end; at = OffsetOf(++index))
        {
            if (a >= start)
            {
                yield return a;
            }
        }
    }
}
