using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Inspector;

/// <summary>
/// 書き換えの結果 (INSP-17)。<see cref="Bytes"/> は書き込むバイト列 (誤りなら null)。<see cref="StoredText"/> は、丸め・切り捨てで
/// 入力と違う値が格納される場合のその値 (パネルの下部の「格納される値」。INSP-05 の仕様 4、INSP-15 の仕様 5)。
/// </summary>
public sealed record InspectorEncodeResult(byte[]? Bytes, string? Error, string? StoredText = null)
{
    public bool IsValid => Bytes is not null;

    public static InspectorEncodeResult Fail(string error) => new(null, error);
}

/// <summary>入力した値を型の表現のバイト列にする (INSP-03、INSP-05、INSP-08、INSP-09、INSP-11、INSP-13、INSP-15、INSP-17)。</summary>
public static class InspectorEncoder
{
    /// <summary>
    /// <paramref name="available"/> は起点から末尾までのバイト数 (末尾を越える書き込みは誤り。INSP-17 の仕様 4)。
    /// <paramref name="expressions"/> は整数の入力式の名前 (<c>cur</c> など) に使う。
    /// </summary>
    public static InspectorEncodeResult Encode(string typeId, string input, Endianness endian, InspectorOptions o, long available,
        IExpressionContext? expressions = null)
    {
        InspectorType type = InspectorTypes.Get(typeId);
        if (type.FixedEndian)
        {
            endian = Endianness.Little;
        }

        InspectorEncodeResult result = type.Id switch
        {
            InspectorTypes.Int8 or InspectorTypes.Int16 or InspectorTypes.Int32 or InspectorTypes.Int64 => Integer(type, input, endian, signed: true, o, expressions),
            InspectorTypes.UInt8 or InspectorTypes.UInt16 or InspectorTypes.UInt32 or InspectorTypes.UInt64 => Integer(type, input, endian, signed: false, o, expressions),
            InspectorTypes.Float32 => Float(input, endian, isFloat: true, o),
            InspectorTypes.Float64 => Float(input, endian, isFloat: false, o),
            InspectorTypes.Binary8 or InspectorTypes.Binary16 or InspectorTypes.Binary32 or InspectorTypes.Binary64 => Binary(type, input, endian, o),
            InspectorTypes.Ansi => Character(input, o.AnsiEncoding ?? InspectorDecoder.DefaultAnsi, o),
            InspectorTypes.Utf8 => Character(input, new UTF8Encoding(false), o),
            InspectorTypes.Utf16 => Character(input, new UnicodeEncoding(endian == Endianness.Big, false), o),
            InspectorTypes.Guid => Guid(input, windows: true, o),
            InspectorTypes.Uuid => Guid(input, windows: false, o),
            _ => DateTimeValue(type, input, endian, o),
        };
        if (result.Bytes is { } bytes && bytes.Length > available)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorPastEnd, bytes.Length, Math.Max(0, available)));
        }

        return result;
    }

    // ---- 整数 (INSP-03 の仕様 4) ----

    private static InspectorEncodeResult Integer(InspectorType type, string input, Endianness endian, bool signed, InspectorOptions o,
        IExpressionContext? expressions)
    {
        int size = type.Size;
        BigInteger min = signed ? -(BigInteger.One << (size * 8 - 1)) : BigInteger.Zero;
        BigInteger max = signed ? (BigInteger.One << (size * 8 - 1)) - 1 : (BigInteger.One << (size * 8)) - 1;
        BigInteger? value = ParseLiteral(input);
        if (value is null)
        {
            if (!ExpressionEvaluator.TryEvaluate(input, expressions ?? EmptyContext.Instance, out long evaluated, out _, DefaultRadix.Decimal))
            {
                return InspectorEncodeResult.Fail(o.Text.ErrorInteger);
            }

            value = evaluated;
        }

        if (value < min || value > max)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorIntegerRange, type.Id, min.ToString("N0", o.Culture), max.ToString("N0", o.Culture)));
        }

        ulong bits = (ulong)(value.Value & ulong.MaxValue);
        return new InspectorEncodeResult(InspectorDecoder.WriteBits(bits, size, endian), null);
    }

    /// <summary>
    /// 64 bit を超える数も書けるよう、数だけの入力 (符号、10 進・<c>0x</c>・<c>0b</c>・<c>0o</c>、<c>_</c> の桁区切り) は先に
    /// 多倍長で読む。式なら null。
    /// </summary>
    private static BigInteger? ParseLiteral(string input)
    {
        string text = input.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        if (text.Length == 0)
        {
            return null;
        }

        BigInteger value;
        string lower = text.ToLowerInvariant();
        if (lower.StartsWith("0x", StringComparison.Ordinal) && lower.Length > 2 && lower[2..].All(char.IsAsciiHexDigit))
        {
            value = BigInteger.Parse("0" + lower[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (lower.StartsWith("0b", StringComparison.Ordinal) && lower.Length > 2 && lower[2..].All(c => c is '0' or '1'))
        {
            value = lower[2..].Aggregate(BigInteger.Zero, (acc, c) => acc * 2 + (c - '0'));
        }
        else if (lower.StartsWith("0o", StringComparison.Ordinal) && lower.Length > 2 && lower[2..].All(c => c is >= '0' and <= '7'))
        {
            value = lower[2..].Aggregate(BigInteger.Zero, (acc, c) => acc * 8 + (c - '0'));
        }
        else if (lower.All(char.IsAsciiDigit))
        {
            value = BigInteger.Parse(lower, CultureInfo.InvariantCulture);
        }
        else
        {
            return null;
        }

        return negative ? -value : value;
    }

    // ---- 浮動小数点 (INSP-05 の仕様 4) ----

    private static InspectorEncodeResult Float(string input, Endianness endian, bool isFloat, InspectorOptions o)
    {
        double? parsed = NumberFormat.ParseFloat(input, isFloat, out bool overflow);
        if (parsed is not double value)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorFloat);
        }

        if (overflow)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, isFloat ? InspectorTypes.Float32 : InspectorTypes.Float64));
        }

        // nan は正の静かな NaN (0x7FC00000 / 0x7FF8000000000000) にする (.NET の double.NaN は符号ビットが立っている)。
        ulong bits = double.IsNaN(value) ? (isFloat ? 0x7FC00000UL : 0x7FF8000000000000UL)
            : isFloat ? (uint)BitConverter.SingleToInt32Bits((float)value) : (ulong)BitConverter.DoubleToInt64Bits(value);
        string? stored = NumberFormat.RoundingChanged(input, value, isFloat) ? NumberFormat.StoredValue(value, isFloat, o.Culture) : null;
        return new InspectorEncodeResult(InspectorDecoder.WriteBits(bits, isFloat ? 4 : 8, endian), null, stored);
    }

    // ---- 2 進 (INSP-08 の仕様 4) ----

    private static InspectorEncodeResult Binary(InspectorType type, string input, Endianness endian, InspectorOptions o)
    {
        int bits = type.Size * 8;
        string text = string.Concat(input.Where(c => !char.IsWhiteSpace(c) && c != '_'));
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length == 0 || text.Length > bits || text.Any(c => c is not ('0' or '1')))
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorBinary, bits));
        }

        ulong value = text.Aggregate(0UL, (acc, c) => (acc << 1) | (c == '1' ? 1UL : 0UL));
        return new InspectorEncodeResult(InspectorDecoder.WriteBits(value, type.Size, endian), null);
    }

    /// <summary>ビット <paramref name="bit"/> (0 が最下位) を反転したバイト列 (INSP-08 の仕様 3)。</summary>
    public static byte[] FlipBit(ReadOnlySpan<byte> current, int bit, Endianness endian)
    {
        byte[] bytes = current.ToArray();
        int index = bit / 8;
        if (endian == Endianness.Big)
        {
            index = bytes.Length - 1 - index;
        }

        bytes[index] ^= (byte)(1 << (bit % 8));
        return bytes;
    }

    // ---- 文字 (INSP-09 の仕様 5) ----

    private static InspectorEncodeResult Character(string input, Encoding encoding, InspectorOptions o)
    {
        if (input.Length == 0 || input.Length > 2 || input.Length == 2 && !char.IsSurrogatePair(input[0], input[1])
            || input.Length == 1 && char.IsSurrogate(input[0]))
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorChar);
        }

        Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            byte[] bytes = encoding is UnicodeEncoding or UTF8Encoding ? encoding.GetBytes(input) : strict.GetBytes(input);
            return new InspectorEncodeResult(bytes, null);
        }
        catch (EncoderFallbackException)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorNotEncodable, encoding.WebName));
        }
    }

    // ---- GUID (INSP-11 の仕様 4) ----

    private static InspectorEncodeResult Guid(string input, bool windows, InspectorOptions o)
    {
        string hex = string.Concat(input.Where(c => c is not ('{' or '}' or '-') && !char.IsWhiteSpace(c)));
        if (hex.Length != 32 || !hex.All(char.IsAsciiHexDigit))
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorGuid);
        }

        byte[] rfc = Convert.FromHexString(hex);
        if (!windows)
        {
            return new InspectorEncodeResult(rfc, null);
        }

        int[] map = [3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15];
        return new InspectorEncodeResult([.. map.Select(i => rfc[i])], null);
    }

    // ---- 日時 (INSP-13 の仕様 6、INSP-15 の仕様 4・5) ----

    private static InspectorEncodeResult DateTimeValue(InspectorType type, string input, Endianness endian, InspectorOptions o)
    {
        bool hasZone = type.Id is InspectorTypes.Unix32 or InspectorTypes.Unix32U or InspectorTypes.Unix64 or InspectorTypes.FileTime;
        DateTimeInput? parsed = InspectorDateTime.Parse(input, hasZone, o, allowTimeOnly: type.Id == InspectorTypes.DosTime);
        if (parsed is not { } p)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorDate);
        }

        DateTime value = p.Value;
        switch (type.Id)
        {
            case InspectorTypes.Unix32:
            case InspectorTypes.Unix32U:
            case InspectorTypes.Unix64:
            {
                // 秒より細かい部分は切り捨てる (負の値は小さい方へ)。
                long ticks = value.Ticks - InspectorDateTime.UnixEpoch.Ticks;
                long seconds = ticks >= 0 ? ticks / TimeSpan.TicksPerSecond : (ticks - TimeSpan.TicksPerSecond + 1) / TimeSpan.TicksPerSecond;

                (long min, long max) = type.Id switch
                {
                    InspectorTypes.Unix32 => ((long)int.MinValue, (long)int.MaxValue),
                    InspectorTypes.Unix32U => (0L, (long)uint.MaxValue),
                    _ => (long.MinValue, long.MaxValue),
                };
                if (seconds < min || seconds > max)
                {
                    return RangeError(type, InspectorDateTime.UnixEpoch.AddSeconds(Math.Max(min, -62135596800)),
                        InspectorDateTime.UnixEpoch.AddSeconds(Math.Min(max, 253402300799)), o);
                }

                DateTime kept = InspectorDateTime.UnixEpoch.AddSeconds(seconds);
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)seconds, type.Size, endian), null,
                    kept.Ticks != value.Ticks ? InspectorDateTime.FormatInstant(kept, 0, o) : null);
            }

            case InspectorTypes.FileTime:
            {
                if (value < InspectorDateTime.FileTimeEpoch)
                {
                    return RangeError(type, InspectorDateTime.FileTimeEpoch, DateTime.MaxValue, o);
                }

                ulong fileTime = (ulong)(value.Ticks - InspectorDateTime.FileTimeEpoch.Ticks);
                string? stored = p.FractionDigits > 7 ? InspectorDateTime.FormatInstant(value, 7, o) : null;
                return new InspectorEncodeResult(InspectorDecoder.WriteBits(fileTime, 8, endian), null, stored);
            }

            default:
                return Dos(type, value, p.FractionDigits, endian, o);
        }
    }

    private static InspectorEncodeResult Dos(InspectorType type, DateTime value, int fractionDigits, Endianness endian, InspectorOptions o)
    {
        bool hasDate = type.Id != InspectorTypes.DosTime;
        bool hasTime = type.Id != InspectorTypes.DosDate;
        if (hasDate && (value.Year < 1980 || value.Year > 2107))
        {
            return RangeError(type, new DateTime(1980, 1, 1), new DateTime(2107, 12, 31, 23, 59, 58), o);
        }

        ushort date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
        ushort time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        var kept = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second / 2 * 2);
        bool truncated = hasTime && (kept.Ticks != value.Ticks || fractionDigits > 0 && value.Ticks % TimeSpan.TicksPerSecond != 0);
        InspectorDateTime.Parts parts = !hasDate ? InspectorDateTime.Parts.Time : !hasTime ? InspectorDateTime.Parts.Date : InspectorDateTime.Parts.DateAndTime;
        string? stored = truncated ? InspectorDateTime.FormatNoZone(kept, 0, o, parts) : null;
        byte[] bytes = type.Id switch
        {
            InspectorTypes.DosDate => InspectorDecoder.WriteBits(date, 2, endian),
            InspectorTypes.DosTime => InspectorDecoder.WriteBits(time, 2, endian),
            _ => [.. InspectorDecoder.WriteBits(time, 2, endian), .. InspectorDecoder.WriteBits(date, 2, endian)],
        };
        return new InspectorEncodeResult(bytes, null, stored);
    }

    private static InspectorEncodeResult RangeError(InspectorType type, DateTime min, DateTime max, InspectorOptions o) =>
        InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorDateRange, type.Id,
            min.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), max.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

    /// <summary>名前のない入力式の文脈 (ドキュメントのない場面のテスト用)。</summary>
    private sealed class EmptyContext : IExpressionContext
    {
        public static readonly EmptyContext Instance = new();

        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
