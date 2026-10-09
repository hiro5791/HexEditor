using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Text;

namespace HexEditor.Core.Inspector;

/// <summary>
/// フェーズ 2 の型の解釈: 整数 24 / 48 / 128 bit (INSP-04)、拡張の浮動小数点と固定小数点 (INSP-06)、可変長整数 (INSP-07)、文字列
/// (INSP-10)、色 (INSP-12)、日時の拡張の形式 (INSP-14)。
/// </summary>
public static partial class InspectorDecoder
{
    /// <summary>文字列の表示の最大文字数 (INSP-10 の仕様 2)。超える分は「…」で省く。</summary>
    public const int MaxStringDisplay = 256;

    public static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime CocoaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime SqlEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>SQL Server datetime の 1 日の時刻の単位数 (1/300 秒)。</summary>
    public const uint SqlTicksPerDay = 24u * 60 * 60 * 300;

    /// <summary>書式 (ICU MessageFormat の複数形を使える)。数値は地域設定で書式化する。</summary>
    private static string Fmt(InspectorOptions o, string pattern, params object?[] args) =>
        MessageFormat.Format(pattern, o.Culture, o.Language, args);

    private static string ByteCountText(int count, InspectorOptions o) =>
        count == 1 ? o.Text.OneByte : string.Format(o.Culture, o.Text.Bytes, count);

    // ---- 固定長の型 ----

    private static InspectorValue? DecodeExtended(InspectorType type, ReadOnlySpan<byte> bytes, Endianness endian, InspectorOptions o)
    {
        if (type.Fixed is { } format)
        {
            return FixedPoint(bytes, endian, format, o);
        }

        return type.Id switch
        {
            InspectorTypes.Int24 or InspectorTypes.Int48 => Integer(bytes, endian, signed: true, o),
            InspectorTypes.UInt24 or InspectorTypes.UInt48 => Integer(bytes, endian, signed: false, o),
            InspectorTypes.Int128 => Integer128(bytes, endian, signed: true, o),
            InspectorTypes.UInt128 => Integer128(bytes, endian, signed: false, o),
            InspectorTypes.Half => HalfValue((ushort)ReadBits(bytes, endian), o),
            InspectorTypes.BFloat16 => BFloat16Value((ushort)ReadBits(bytes, endian), o),
            InspectorTypes.Float80 => Float80Value(Float80.FromBytes(bytes, endian), o),
            InspectorTypes.Real48 => Real48Value(bytes, o),
            InspectorTypes.Rgba8 => ColorValue(bytes[0], bytes[1], bytes[2], bytes[3], 4, o),
            InspectorTypes.Bgra8 => ColorValue(bytes[2], bytes[1], bytes[0], bytes[3], 4, o),
            InspectorTypes.Rgb8 => ColorValue(bytes[0], bytes[1], bytes[2], 255, 3, o),
            InspectorTypes.Rgb565 => Rgb565Value((ushort)ReadBits(bytes, endian), o),
            InspectorTypes.UnixMs => UnixMilliseconds((long)ReadBits(bytes, endian), o),
            InspectorTypes.OleDate => OleDate(BitConverter.Int64BitsToDouble((long)ReadBits(bytes, endian)), o),
            InspectorTypes.HfsPlus => SecondsFrom(HfsEpoch, (uint)ReadBits(bytes, endian), 4, o),
            InspectorTypes.Apfs => Apfs(ReadBits(bytes, endian), o),
            InspectorTypes.Cocoa => Cocoa(BitConverter.Int64BitsToDouble((long)ReadBits(bytes, endian)), o),
            InspectorTypes.SqlDateTime => SqlDateTime((int)(uint)ReadBits(bytes[..4], endian), (uint)ReadBits(bytes[4..8], endian), o),
            InspectorTypes.SqlSmallDateTime => SqlSmallDateTime((ushort)ReadBits(bytes[..2], endian), (ushort)ReadBits(bytes[2..4], endian), o),
            InspectorTypes.DotNetDateTime => DotNetDateTime(ReadBits(bytes, endian), o),
            _ => null,
        };
    }

    // ---- 整数 128 bit (INSP-04) ----

    /// <summary>16 バイトをエンディアンに従って組み立てる。</summary>
    public static UInt128 Read128(ReadOnlySpan<byte> bytes, Endianness endian)
    {
        UInt128 value = UInt128.Zero;
        for (int i = 0; i < 16; i++)
        {
            byte b = endian == Endianness.Little ? bytes[15 - i] : bytes[i];
            value = (value << 8) | b;
        }

        return value;
    }

    public static byte[] Write128(UInt128 value, Endianness endian)
    {
        byte[] bytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            byte b = (byte)(value >> (8 * i));
            bytes[endian == Endianness.Little ? i : 15 - i] = b;
        }

        return bytes;
    }

    private static InspectorValue Integer128(ReadOnlySpan<byte> bytes, Endianness endian, bool signed, InspectorOptions o)
    {
        UInt128 bits = Read128(bytes, endian);
        BigInteger value = signed ? (BigInteger)(Int128)bits : (BigInteger)bits;
        return new InspectorValue(InspectorStatus.Ok, NumberFormat.Integer128(value, bits, o.IntegerBase, o.DigitGrouping, o.Culture), 16);
    }

    // ---- 浮動小数点 (INSP-06) ----

    /// <summary>特殊な値 (0、無限大、NaN) の表示。それ以外は null。</summary>
    private static string? Special(double value, ulong raw, int hexDigits)
    {
        if (double.IsNaN(value))
        {
            return "NaN (0x" + raw.ToString("X" + hexDigits, CultureInfo.InvariantCulture) + ")";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "∞" : "-∞";
        }

        return value == 0 ? (double.IsNegative(value) ? "-0" : "+0") : null;
    }

    /// <summary>double で表せる形式の表示 (最短で正確・指数表記・16 進浮動小数点)。</summary>
    private static string ShortFloatText(double value, Func<double, bool> roundTrips, Func<string> hexFloat, InspectorOptions o)
    {
        if (o.FloatFormat == FloatFormat.HexFloat)
        {
            return hexFloat();
        }

        (double shortest, int digits) = SmallFloats.Shortest(value, roundTrips);
        return o.FloatFormat == FloatFormat.Exponent
            ? shortest.ToString("E" + Math.Max(0, digits - 1), o.Culture)
            : shortest.ToString("R", o.Culture);
    }

    private static InspectorValue HalfValue(ushort raw, InspectorOptions o)
    {
        Half h = BitConverter.UInt16BitsToHalf(raw);
        double value = (double)h;
        string text = Special(value, raw, 4)
            ?? ShortFloatText(value, d => BitConverter.HalfToUInt16Bits((Half)d) == raw, () => NumberFormat.HexFloatBits(raw, 10, 5), o);
        if (Half.IsSubnormal(h))
        {
            text = string.Format(o.Culture, o.Text.Denormal, text);
        }

        return new InspectorValue(InspectorStatus.Ok, text, 2);
    }

    private static InspectorValue BFloat16Value(ushort raw, InspectorOptions o)
    {
        float f = SmallFloats.FromBFloat16(raw);
        string text = Special(f, raw, 4)
            ?? ShortFloatText(f, d => SmallFloats.ToBFloat16((float)d) == raw, () => NumberFormat.HexFloatBits(raw, 7, 8), o);
        if (float.IsSubnormal(f))
        {
            text = string.Format(o.Culture, o.Text.Denormal, text);
        }

        return new InspectorValue(InspectorStatus.Ok, text, 2);
    }

    private static InspectorValue Real48Value(ReadOnlySpan<byte> bytes, InspectorOptions o)
    {
        // 指数 0 は値 0 (仮数と符号は見ない)。
        if (bytes[0] == 0)
        {
            return new InspectorValue(InspectorStatus.Ok, "+0", 6);
        }

        byte[] raw = bytes[..6].ToArray();
        double value = SmallFloats.FromReal48(raw);
        string text = ShortFloatText(value, d => SmallFloats.ToReal48(d) is { } back && back.AsSpan().SequenceEqual(raw),
            () => NumberFormat.HexFloat((ulong)BitConverter.DoubleToInt64Bits(value), isFloat: false), o);
        return new InspectorValue(InspectorStatus.Ok, text, 6);
    }

    /// <summary>
    /// 80 bit の拡張倍精度 (INSP-06 の仕様 2): 最も近い 80 bit の値に丸めたときに同じ値に戻る 10 進の表記のうち、桁数が最も少ないもの
    /// (有効数字は最大 21 桁)。非正規の表現は「非正規の表現」。
    /// </summary>
    private static InspectorValue Float80Value(Float80 v, InspectorOptions o)
    {
        switch (v.Kind)
        {
            case Float80Kind.Zero:
                return new InspectorValue(InspectorStatus.Ok, v.Negative ? "-0" : "+0", 10);
            case Float80Kind.Infinity:
                return new InspectorValue(InspectorStatus.Ok, v.Negative ? "-∞" : "∞", 10);
            case Float80Kind.NaN:
                return new InspectorValue(InspectorStatus.Ok,
                    "NaN (0x" + ((v.Negative ? 0x8000 : 0) | v.Exponent).ToString("X4", CultureInfo.InvariantCulture) + v.Mantissa.ToString("X16", CultureInfo.InvariantCulture) + ")", 10);
            case Float80Kind.Unnormal:
                return new InspectorValue(InspectorStatus.Invalid, o.Text.Unnormal, 10);
        }

        string text = Float80Text(v, o);
        if (v.Kind == Float80Kind.Denormal)
        {
            text = string.Format(o.Culture, o.Text.Denormal, text);
        }

        return new InspectorValue(InspectorStatus.Ok, text, 10);
    }

    /// <summary>80 bit の有限の値の表示 (INSP-02 の仕様 6 の表示形式に従う)。</summary>
    public static string Float80Text(Float80 v, InspectorOptions o)
    {
        if (o.FloatFormat == FloatFormat.HexFloat)
        {
            // 整数ビットを明示した形 (0x1.921fb54442d18469p+1)。
            ulong fraction = v.Mantissa << 1;
            string digits = fraction.ToString("x16", CultureInfo.InvariantCulture).TrimEnd('0');
            int e = (v.Exponent == 0 ? 1 : v.Exponent) - Float80.Bias;
            return (v.Negative ? "-" : string.Empty) + "0x" + (v.Mantissa >> 63) + (digits.Length > 0 ? "." + digits : string.Empty)
                + "p" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }

        (BigInteger num, BigInteger den) = v.Rational();
        Float80 target = v with { Negative = false };
        (BigInteger q, int exp10) = DecimalText.Shortest(num, den, 21, (n, d) => Float80.Round(n, d, false) == target);
        return o.FloatFormat == FloatFormat.Exponent
            ? DecimalText.FormatExponent(v.Negative, q, exp10, o.Culture)
            : DecimalText.Format(v.Negative, q, exp10, o.Culture);
    }

    /// <summary>
    /// 固定小数点 (INSP-06 の仕様 4): 小数部のビット数から正確に表せる桁数まで表示する (16.16 なら小数点以下 16 桁まで。末尾の 0 は省く)。
    /// </summary>
    private static InspectorValue FixedPoint(ReadOnlySpan<byte> bytes, Endianness endian, FixedPointFormat format, InspectorOptions o)
    {
        int size = format.TotalBits / 8;
        ulong bits = ReadBits(bytes[..size], endian);
        BigInteger raw = format.Signed ? SignExtend(bits, size) : bits;
        return new InspectorValue(InspectorStatus.Ok, FixedText(raw, format.FractionBits, o), size);
    }

    /// <summary>raw / 2^fraction の正確な 10 進表記。</summary>
    public static string FixedText(BigInteger raw, int fraction, InspectorOptions o)
    {
        bool negative = raw.Sign < 0;
        BigInteger a = BigInteger.Abs(raw);
        BigInteger whole = a >> fraction;
        BigInteger rest = a - (whole << fraction);
        string text = o.DigitGrouping ? whole.ToString("N0", o.Culture) : whole.ToString(o.Culture);
        if (!rest.IsZero)
        {
            // rest / 2^f = rest × 5^f / 10^f。f 桁の小数になる。
            string digits = (rest * BigInteger.Pow(5, fraction)).ToString(CultureInfo.InvariantCulture).PadLeft(fraction, '0').TrimEnd('0');
            text += o.Culture.NumberFormat.NumberDecimalSeparator + digits;
        }

        return negative ? o.Culture.NumberFormat.NegativeSign + text : text;
    }

    // ---- 色 (INSP-12) ----

    /// <summary>RGB565 の成分を 8 bit に広げる (上位のビットを下位に繰り返す。INSP-12 の仕様 1)。</summary>
    public static (byte R, byte G, byte B) ExpandRgb565(ushort v)
    {
        int r5 = v >> 11;
        int g6 = (v >> 5) & 0x3F;
        int b5 = v & 0x1F;
        return ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g6 << 2) | (g6 >> 4)), (byte)((b5 << 3) | (b5 >> 2)));
    }

    private static InspectorValue Rgb565Value(ushort v, InspectorOptions o)
    {
        (byte r, byte g, byte b) = ExpandRgb565(v);
        return ColorValue(r, g, b, 255, 2, o);
    }

    /// <summary>色の表示: <c>#RRGGBBAA</c> と 10 進の成分 (<c>R 255, G 128, B 0, A 255</c>)。色見本は UI が <see cref="InspectorValue.Rgba"/> で描く。</summary>
    private static InspectorValue ColorValue(byte r, byte g, byte b, byte a, int size, InspectorOptions o) =>
        new(InspectorStatus.Ok, ColorText(r, g, b, a, o), size, Rgba: (uint)(r << 24 | g << 16 | b << 8 | a));

    public static string ColorText(byte r, byte g, byte b, byte a, InspectorOptions o) =>
        $"#{r:X2}{g:X2}{b:X2}{a:X2}  " + string.Format(CultureInfo.InvariantCulture, o.Text.ColorComponents, r, g, b, a);

    // ---- 日時 (INSP-14) ----

    private static InspectorValue Instant(DateTime? utc, int fractionDigits, string raw, int size, InspectorOptions o) =>
        utc is { } value && InspectorDateTime.FormatInstant(value, fractionDigits, o) is { } text
            ? new InspectorValue(InspectorStatus.Ok, text, size)
            : OutOfRange(raw, size, o);

    private static DateTime? AddTicks(DateTime epoch, BigInteger ticks)
    {
        BigInteger result = epoch.Ticks + ticks;
        return result < DateTime.MinValue.Ticks || result > DateTime.MaxValue.Ticks ? null : new DateTime((long)result, epoch.Kind);
    }

    private static InspectorValue UnixMilliseconds(long ms, InspectorOptions o) =>
        Instant(AddTicks(InspectorDateTime.UnixEpoch, (BigInteger)ms * TimeSpan.TicksPerMillisecond), 3, ms.ToString(CultureInfo.InvariantCulture), 8, o);

    private static InspectorValue SecondsFrom(DateTime epoch, long seconds, int size, InspectorOptions o) =>
        Instant(AddTicks(epoch, (BigInteger)seconds * TimeSpan.TicksPerSecond), 0, seconds.ToString(CultureInfo.InvariantCulture), size, o);

    private static InspectorValue Apfs(ulong nanoseconds, InspectorOptions o) =>
        Instant(AddTicks(InspectorDateTime.UnixEpoch, nanoseconds / 100), 7, nanoseconds.ToString(CultureInfo.InvariantCulture), 8, o);

    private static InspectorValue Cocoa(double seconds, InspectorOptions o)
    {
        DateTime? value = double.IsFinite(seconds) && Math.Abs(seconds) < 4e11
            ? AddTicks(CocoaEpoch, new BigInteger(Math.Round(seconds * TimeSpan.TicksPerSecond)))
            : null;
        return Instant(value, 3, seconds.ToString("R", CultureInfo.InvariantCulture), 8, o);
    }

    /// <summary>OLE オートメーション日付 (1899-12-30 からの日数。タイムゾーンなし)。</summary>
    private static InspectorValue OleDate(double days, InspectorOptions o)
    {
        // DateTime.FromOADate の受け付ける範囲 (0100-01-01〜9999-12-31)。
        if (!double.IsFinite(days) || days <= -657435.0 || days >= 2958466.0)
        {
            return OutOfRange(days.ToString("R", CultureInfo.InvariantCulture), 8, o);
        }

        return new InspectorValue(InspectorStatus.Ok, InspectorDateTime.FormatNoZone(DateTime.FromOADate(days), 3, o), 8);
    }

    /// <summary>SQL Server datetime: 1900-01-01 からの日数 (符号あり) と 1/300 秒単位の時刻。</summary>
    private static InspectorValue SqlDateTime(int days, uint time, InspectorOptions o)
    {
        string raw = $"{days}, {time}";
        if (time >= SqlTicksPerDay || AddTicks(SqlEpoch, (BigInteger)days * TimeSpan.TicksPerDay + (BigInteger)time * TimeSpan.TicksPerSecond / 300) is not { } value)
        {
            return OutOfRange(raw, 8, o);
        }

        return new InspectorValue(InspectorStatus.Ok, InspectorDateTime.FormatNoZone(value, 3, o), 8);
    }

    private static InspectorValue SqlSmallDateTime(ushort days, ushort minutes, InspectorOptions o)
    {
        if (minutes >= 24 * 60)
        {
            return OutOfRange($"{days}, {minutes}", 4, o);
        }

        DateTime value = SqlEpoch.AddDays(days).AddMinutes(minutes);
        return new InspectorValue(InspectorStatus.Ok, InspectorDateTime.FormatNoZone(value, 0, o), 4);
    }

    /// <summary>.NET の DateTime (下位 62 bit が 0001-01-01 からの 100 ナノ秒単位、上位 2 bit が種類)。</summary>
    private static InspectorValue DotNetDateTime(ulong raw, InspectorOptions o)
    {
        long ticks = (long)(raw & 0x3FFF_FFFF_FFFF_FFFFUL);
        int kind = (int)(raw >> 62);
        if (ticks > DateTime.MaxValue.Ticks)
        {
            return OutOfRange(ticks.ToString(CultureInfo.InvariantCulture), 8, o);
        }

        if (kind == 1)
        {
            return Instant(new DateTime(ticks, DateTimeKind.Utc), 7, ticks.ToString(CultureInfo.InvariantCulture), 8, o);
        }

        // ローカル (2、3) と指定なし (0) は値のとおりに表示し、種類を示す。
        InspectorOptions shown = o with { Text = o.Text with { NoTimeZone = kind == 0 ? o.Text.KindUnspecified : o.Text.KindLocal } };
        return new InspectorValue(InspectorStatus.Ok, InspectorDateTime.FormatNoZone(new DateTime(ticks), 7, shown), 8);
    }

    // ---- 可変長の型 ----

    /// <summary>可変長の型の解釈。可変長の型でなければ null。</summary>
    private static InspectorValue? DecodeVariable(InspectorType type, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Endianness endian,
        InspectorOptions o) => type.Id switch
        {
            InspectorTypes.ULeb128 => Leb128(data, states, signed: false, o),
            InspectorTypes.SLeb128 => Leb128(data, states, signed: true, o),
            InspectorTypes.SqliteVarint => SqliteVarint(data, states, o),
            InspectorTypes.CStringAnsi => CString(data, states, o.AnsiEncoding ?? DefaultAnsi, 1, o),
            InspectorTypes.CStringUtf8 => CString(data, states, new UTF8Encoding(false), 1, o),
            InspectorTypes.CStringUtf16 => CString(data, states, new UnicodeEncoding(endian == Endianness.Big, false), 2, o),
            InspectorTypes.PString8Ansi => PString(data, states, 1, o.AnsiEncoding ?? DefaultAnsi, endian, o),
            InspectorTypes.PString8Utf8 => PString(data, states, 1, new UTF8Encoding(false), endian, o),
            InspectorTypes.PString16Ansi => PString(data, states, 2, o.AnsiEncoding ?? DefaultAnsi, endian, o),
            InspectorTypes.PString16Utf8 => PString(data, states, 2, new UTF8Encoding(false), endian, o),
            InspectorTypes.PString32Ansi => PString(data, states, 4, o.AnsiEncoding ?? DefaultAnsi, endian, o),
            InspectorTypes.PString32Utf8 => PString(data, states, 4, new UTF8Encoding(false), endian, o),
            InspectorTypes.DigitsDateTime => DigitsDateTime(data, states, o),
            _ => null,
        };

    /// <summary>i バイト目の読み込みの状態の問題 (読み込み中・読めない)。問題がなければ null。</summary>
    private static InspectorValue? StateAt(ReadOnlySpan<ByteState> states, int i, int size, InspectorOptions o)
    {
        if (states.IsEmpty || i >= states.Length || states[i] == ByteState.Valid)
        {
            return null;
        }

        return states[i] == ByteState.Loading
            ? new InspectorValue(InspectorStatus.Loading, o.Text.Loading, size)
            : new InspectorValue(InspectorStatus.Unreadable, o.Text.Unreadable, size);
    }

    /// <summary>データの末尾で終わっていない (「—」とツールチップ)。</summary>
    private static InspectorValue NotEnough(int needed, int available, InspectorOptions o) =>
        new(InspectorStatus.NotEnoughData, o.Text.NotEnoughValue, Math.Max(1, available),
            string.Format(o.Culture, o.Text.NotEnoughTip, needed, available));

    /// <summary>LEB128 の最大のバイト数 (64 bit。INSP-07 の仕様 2)。</summary>
    public const int MaxLeb128Bytes = 10;

    /// <summary>LEB128 を読む (INSP-07 の仕様 2)。結果の値とバイト数。10 バイト以内で終わらない・64 bit を超える場合は Overflow。</summary>
    public static (ulong Value, int Length, bool Overflow, bool Incomplete) ReadLeb128(ReadOnlySpan<byte> data, bool signed)
    {
        ulong value = 0;
        int shift = 0;
        for (int i = 0; i < MaxLeb128Bytes; i++)
        {
            if (i >= data.Length)
            {
                return (value, i, false, true);
            }

            byte b = data[i];
            ulong part = (ulong)(b & 0x7F);
            if (i == MaxLeb128Bytes - 1)
            {
                // 10 バイト目に入るのは 64 bit の最上位の 1 bit だけ (符号ありでは符号を広げた値)。
                bool fits = signed ? part is 0 or 0x7F : part <= 1;
                if (!fits || (b & 0x80) != 0)
                {
                    return (value, MaxLeb128Bytes, true, false);
                }
            }

            value |= part << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (signed && shift < 64 && (b & 0x40) != 0)
                {
                    value |= ulong.MaxValue << shift;
                }

                return (value, i + 1, false, false);
            }
        }

        return (value, MaxLeb128Bytes, true, false);
    }

    private static InspectorValue Leb128(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, bool signed, InspectorOptions o)
    {
        (ulong value, int length, bool overflow, bool incomplete) = ReadLeb128(data, signed);
        for (int i = 0; i < Math.Min(length, data.Length); i++)
        {
            if (StateAt(states, i, length, o) is { } problem)
            {
                return problem;
            }
        }

        if (incomplete)
        {
            return NotEnough(length + 1, data.Length, o);
        }

        if (overflow)
        {
            return new InspectorValue(InspectorStatus.Invalid, o.Text.TooLarge64, length);
        }

        BigInteger shown = signed ? (long)value : value;
        string text = NumberFormat.Integer(shown, value, 8, o.IntegerBase, o.DigitGrouping, o.Culture) + " " + ByteCountText(length, o);
        return new InspectorValue(InspectorStatus.Ok, text, length);
    }

    /// <summary>SQLite の可変長整数 (1〜9 バイト、ビッグエンディアン。9 バイト目は 8 bit すべてを使う。INSP-07 の仕様 3)。</summary>
    public static (ulong Value, int Length, bool Incomplete) ReadSqliteVarint(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (i >= data.Length)
            {
                return (value, i, true);
            }

            byte b = data[i];
            if (i == 8)
            {
                return ((value << 8) | b, 9, false);
            }

            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return (value, i + 1, false);
            }
        }

        return (value, 9, false);
    }

    private static InspectorValue SqliteVarint(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorOptions o)
    {
        (ulong value, int length, bool incomplete) = ReadSqliteVarint(data);
        for (int i = 0; i < Math.Min(length, data.Length); i++)
        {
            if (StateAt(states, i, length, o) is { } problem)
            {
                return problem;
            }
        }

        if (incomplete)
        {
            return NotEnough(length + 1, data.Length, o);
        }

        // SQLite の整数は 64 bit の 2 の補数。
        string text = NumberFormat.Integer((long)value, value, 8, o.IntegerBase, o.DigitGrouping, o.Culture) + " " + ByteCountText(length, o);
        return new InspectorValue(InspectorStatus.Ok, text, length);
    }

    // ---- 文字列 (INSP-10) ----

    /// <summary>NUL 終端の文字列 (その文字コードでの 0 まで読む。読むのは最大 4 KB)。</summary>
    private static InspectorValue CString(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Encoding encoding, int unit, InspectorOptions o)
    {
        int end = -1;
        for (int i = 0; i + unit <= data.Length; i += unit)
        {
            for (int k = 0; k < unit; k++)
            {
                if (StateAt(states, i + k, i + unit, o) is { } problem)
                {
                    return problem;
                }
            }

            if (data[i] == 0 && (unit == 1 || data[i + 1] == 0))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            if (data.Length < unit)
            {
                return NotEnough(unit, data.Length, o);
            }

            return new InspectorValue(InspectorStatus.Invalid, data.Length >= MaxReadLength ? o.Text.NotTerminated : o.Text.NotTerminatedAtEnd,
                data.Length);
        }

        string text = encoding.GetString(data[..end]);
        return StringValue(text, end + unit, false, o);
    }

    /// <summary>長さ付きの文字列 (Pascal 形式。先頭 1 / 2 / 4 バイトが長さ (バイト数))。</summary>
    private static InspectorValue PString(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, int prefix, Encoding encoding, Endianness endian,
        InspectorOptions o)
    {
        if (Check(prefix, data, states, o) is { } problem)
        {
            return problem;
        }

        ulong declared = ReadBits(data[..prefix], endian);
        long total = prefix + (long)declared;
        if (total > data.Length && data.Length < MaxReadLength)
        {
            return NotEnough((int)Math.Min(total, int.MaxValue), data.Length, o);
        }

        // 読む長さの上限 (4 KB) を超える文字列は、読んだ分だけを表示する (末尾に「…」)。
        int available = (int)Math.Min(total, data.Length);
        for (int i = prefix; i < available; i++)
        {
            if (StateAt(states, i, available, o) is { } wait)
            {
                return wait;
            }
        }

        string text = encoding.GetString(data[prefix..available]);
        return StringValue(text, (int)Math.Min(total, int.MaxValue), available < total, o);
    }

    /// <summary>文字列の表示 (<c>"Hello" (5 文字, 6 バイト)</c>)。先頭 256 文字まで表示し、全体は <see cref="InspectorValue.FullText"/>。</summary>
    private static InspectorValue StringValue(string text, int byteCount, bool truncated, InspectorOptions o)
    {
        int chars = 0;
        var shown = new StringBuilder("\"");
        bool cut = truncated;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (chars == MaxStringDisplay)
            {
                cut = true;
                chars++;
                continue;
            }

            if (chars < MaxStringDisplay)
            {
                AppendEscaped(shown, rune);
            }

            chars++;
        }

        if (cut)
        {
            shown.Append('…');
        }

        shown.Append('"');
        int count = text.EnumerateRunes().Count();
        string value = shown + " " + Fmt(o, o.Text.StringCounts, count, byteCount);
        return new InspectorValue(InspectorStatus.Ok, value, byteCount, null, null, text);
    }

    /// <summary>表示用のエスケープ (制御文字・引用符・\ を C の書き方にする)。</summary>
    private static void AppendEscaped(StringBuilder text, Rune rune)
    {
        switch (rune.Value)
        {
            case 0:
                text.Append("\\0");
                break;
            case '\n':
                text.Append("\\n");
                break;
            case '\r':
                text.Append("\\r");
                break;
            case '\t':
                text.Append("\\t");
                break;
            case '"':
                text.Append("\\\"");
                break;
            case '\\':
                text.Append("\\\\");
                break;
            case < 0x20 or 0x7F:
                text.Append("\\x").Append(rune.Value.ToString("X2", CultureInfo.InvariantCulture));
                break;
            default:
                text.Append(rune.ToString());
                break;
        }
    }

    /// <summary>書き換えの入力欄に入れる文字列 (引用符で囲み、表示と同じエスケープを使う)。</summary>
    public static string QuoteString(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (Rune rune in text.EnumerateRunes())
        {
            AppendEscaped(builder, rune);
        }

        return builder.Append('"').ToString();
    }

    // ---- 10 進の数字の日時 (INSP-14 の仕様 1・2) ----

    /// <summary>10 進の数字の日時の形式 (長い形式から順に試す)。</summary>
    public enum DigitsFormat
    {
        YyyyMmDdHhMmSs = 14,
        UnixMilliseconds = 13,
        YyMmDdHhMmSs = 12,
        UnixSeconds = 10,
        YyyyMmDd = 8,
    }

    public static readonly DigitsFormat[] DigitsFormats =
        [DigitsFormat.YyyyMmDdHhMmSs, DigitsFormat.UnixMilliseconds, DigitsFormat.YyMmDdHhMmSs, DigitsFormat.UnixSeconds, DigitsFormat.YyyyMmDd];

    /// <summary>形式の表示名 (言語によらない)。</summary>
    public static string DigitsFormatName(DigitsFormat format, InspectorOptions o) => format switch
    {
        DigitsFormat.YyyyMmDdHhMmSs => "YYYYMMDDhhmmss",
        DigitsFormat.YyMmDdHhMmSs => "YYMMDDhhmmss",
        DigitsFormat.YyyyMmDd => "YYYYMMDD",
        DigitsFormat.UnixSeconds => o.Text.DigitsUnixSeconds,
        _ => o.Text.DigitsUnixMilliseconds,
    };

    /// <summary>
    /// 数字の並びを形式で解釈する。日付として不正なら null。<paramref name="utc"/> は Unix 秒・ミリ秒 (UTC の時刻) のとき true。
    /// </summary>
    public static DateTime? ParseDigits(ReadOnlySpan<byte> digits, DigitsFormat format, out bool utc)
    {
        utc = format is DigitsFormat.UnixSeconds or DigitsFormat.UnixMilliseconds;
        string text = Encoding.ASCII.GetString(digits[..(int)format]);
        long Number(int from, int length) => long.Parse(text.AsSpan(from, length), NumberStyles.None, CultureInfo.InvariantCulture);

        try
        {
            switch (format)
            {
                case DigitsFormat.UnixSeconds:
                    return InspectorDateTime.UnixEpoch.AddSeconds(Number(0, 10));
                case DigitsFormat.UnixMilliseconds:
                    return InspectorDateTime.UnixEpoch.AddMilliseconds(Number(0, 13));
                case DigitsFormat.YyyyMmDd:
                    return MakeDate((int)Number(0, 4), (int)Number(4, 2), (int)Number(6, 2), 0, 0, 0);
                case DigitsFormat.YyMmDdHhMmSs:
                {
                    int yy = (int)Number(0, 2);
                    return MakeDate(yy >= 50 ? 1900 + yy : 2000 + yy, (int)Number(2, 2), (int)Number(4, 2), (int)Number(6, 2), (int)Number(8, 2), (int)Number(10, 2));
                }

                default:
                    return MakeDate((int)Number(0, 4), (int)Number(4, 2), (int)Number(6, 2), (int)Number(8, 2), (int)Number(10, 2), (int)Number(12, 2));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        static DateTime? MakeDate(int year, int month, int day, int hour, int minute, int second) =>
            year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59
                ? null
                : new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>起点から続く ASCII の数字の桁数 (最大 14)。</summary>
    public static int CountDigits(ReadOnlySpan<byte> data)
    {
        int n = 0;
        while (n < data.Length && n < 14 && data[n] is >= (byte)'0' and <= (byte)'9')
        {
            n++;
        }

        return n;
    }

    /// <summary>数字の桁数で使える最初の (最も長い) 形式と、その値。どれも不正なら null。</summary>
    public static (DigitsFormat Format, DateTime Value, bool Utc)? DetectDigits(ReadOnlySpan<byte> data)
    {
        int n = CountDigits(data);
        foreach (DigitsFormat format in DigitsFormats)
        {
            if ((int)format <= n && ParseDigits(data, format, out bool utc) is { } value)
            {
                return (format, value, utc);
            }
        }

        return null;
    }

    private static InspectorValue DigitsDateTime(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorOptions o)
    {
        for (int i = 0; i < Math.Min(14, data.Length); i++)
        {
            if (StateAt(states, i, 8, o) is { } problem)
            {
                return problem;
            }
        }

        int n = CountDigits(data);
        if (n < 8)
        {
            return new InspectorValue(InspectorStatus.NotEnoughData, o.Text.NotEnoughValue, Math.Max(1, n), string.Format(o.Culture, o.Text.DigitsNeeded, n));
        }

        if (DetectDigits(data) is not { } found)
        {
            return new InspectorValue(InspectorStatus.Invalid, o.Text.InvalidDigits, n);
        }

        string text = found.Format switch
        {
            DigitsFormat.YyyyMmDd => InspectorDateTime.FormatNoZone(found.Value, 0, o, InspectorDateTime.Parts.Date),
            DigitsFormat.UnixMilliseconds => InspectorDateTime.FormatInstant(found.Value, 3, o),
            DigitsFormat.UnixSeconds => InspectorDateTime.FormatInstant(found.Value, 0, o),
            _ => InspectorDateTime.FormatNoZone(found.Value, 0, o),
        } ?? o.Text.InvalidDigits;
        return new InspectorValue(InspectorStatus.Ok, text + " (" + DigitsFormatName(found.Format, o) + ")", (int)found.Format);
    }
}
