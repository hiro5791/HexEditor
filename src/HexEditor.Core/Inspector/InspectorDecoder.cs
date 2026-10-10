using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Inspector;

/// <summary>行の値の状態 (INSP-01 の仕様 2・3 と「エラー」)。</summary>
public enum InspectorStatus
{
    Ok,

    /// <summary>型のサイズより残りのバイト数が少ない (「—」)。</summary>
    NotEnoughData,

    /// <summary>読み込み中 (「…」)。</summary>
    Loading,

    /// <summary>読み込みエラー (「読み込めません」)。</summary>
    Unreadable,

    /// <summary>不正・範囲外の値 (理由を表示する)。</summary>
    Invalid,
}

/// <summary>展開した行の構成要素 (GUID のバージョン・タイムスタンプなど。INSP-11 の仕様 3)。範囲は起点からの位置。</summary>
public sealed record InspectorComponent(string Id, string Name, string Value, int Offset, int Length);

/// <summary>
/// 1 行の解釈の結果。<see cref="ByteCount"/> はその型が使うバイト数 (可変長の型は実際に使った数。強調の範囲 INSP-18)。
/// <see cref="FullText"/> は表示を省いた値の全体 (文字列の行の「値をコピー」とツールチップ。INSP-10 の仕様 2)。
/// <see cref="Rgba"/> は色の行の色 (0xRRGGBBAA。色見本に使う。INSP-12 の仕様 2)。
/// </summary>
public sealed record InspectorValue(InspectorStatus Status, string Text, int ByteCount, string? ToolTip = null,
    IReadOnlyList<InspectorComponent>? Components = null, string? FullText = null, uint? Rgba = null);

/// <summary>
/// バイト列を型で解釈して表示の文字列にする (INSP-03、INSP-05、INSP-08、INSP-09、INSP-11、INSP-13)。
/// <c>data</c> は起点から末尾まで (最大 128 バイト) のバイト、<c>states</c> は各バイトの読み込みの状態 (空ならすべて読めている)。
/// </summary>
public static partial class InspectorDecoder
{
    /// <summary>1 回の更新で読むバイト数 (標準の型だけの場合。INSP-01 の仕様 4)。</summary>
    public const int ReadLength = 128;

    /// <summary>文字列 (INSP-10) の行を表示しているときに読むバイト数 (INSP-01 の仕様 4)。</summary>
    public const int MaxReadLength = 4096;

    private static readonly string[] ControlNames =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    public static InspectorValue Decode(string typeId, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Endianness endian,
        InspectorOptions options) =>
        Decode(InspectorTypes.Get(typeId), data, states, endian, options);

    public static InspectorValue Decode(InspectorType type, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Endianness endian,
        InspectorOptions o)
    {
        if (type.FixedEndian)
        {
            endian = Endianness.Little;
        }

        switch (type.Id)
        {
            case InspectorTypes.Ansi:
                return DecodeAnsi(data, states, o);
            case InspectorTypes.Utf8:
                return DecodeUtf8(data, states, o);
            case InspectorTypes.Utf16:
                return DecodeUtf16(data, states, endian, o);
        }

        // 可変長の型 (INSP-07 可変長整数、INSP-10 文字列、INSP-14 の 10 進の数字の日時) は、使った長さを自分で決める。
        if (DecodeVariable(type, data, states, endian, o) is { } variable)
        {
            return variable;
        }

        if (Check(type.Size, data, states, o) is { } problem)
        {
            return problem;
        }

        ReadOnlySpan<byte> bytes = data[..type.Size];
        if (DecodeExtended(type, bytes, endian, o) is { } extended)
        {
            return extended;
        }

        return type.Id switch
        {
            InspectorTypes.Int8 or InspectorTypes.Int16 or InspectorTypes.Int32 or InspectorTypes.Int64 => Integer(bytes, endian, signed: true, o),
            InspectorTypes.UInt8 or InspectorTypes.UInt16 or InspectorTypes.UInt32 or InspectorTypes.UInt64 => Integer(bytes, endian, signed: false, o),
            InspectorTypes.Float32 => Float(bytes, endian, isFloat: true, o),
            InspectorTypes.Float64 => Float(bytes, endian, isFloat: false, o),
            InspectorTypes.Binary8 or InspectorTypes.Binary16 or InspectorTypes.Binary32 or InspectorTypes.Binary64 =>
                new InspectorValue(InspectorStatus.Ok, BinaryText(ReadBits(bytes, endian), bytes.Length * 8), bytes.Length),
            InspectorTypes.Guid => Guid(bytes, windows: true, o),
            InspectorTypes.Uuid => Guid(bytes, windows: false, o),
            InspectorTypes.Unix32 => UnixSeconds((int)(uint)ReadBits(bytes, endian), 4, o),
            InspectorTypes.Unix32U => UnixSeconds((uint)ReadBits(bytes, endian), 4, o),
            InspectorTypes.Unix64 => UnixSeconds((long)ReadBits(bytes, endian), 8, o),
            InspectorTypes.FileTime => FileTime(ReadBits(bytes, endian), o),
            InspectorTypes.DosDate => DosDateTime((ushort)ReadBits(bytes, endian), null, o),
            InspectorTypes.DosTime => DosDateTime(null, (ushort)ReadBits(bytes, endian), o),
            InspectorTypes.DosDateTime => DosDateTime((ushort)ReadBits(bytes[2..], endian), (ushort)ReadBits(bytes[..2], endian), o),
            _ => throw new ArgumentException($"未知の型です: {type.Id}"),
        };
    }

    /// <summary>
    /// 書き換えの入力欄に最初に入れる、現在の値 (INSP-17 の仕様 1)。入力の書式で読み直せる形 (桁区切りなし、日時は ISO 8601、
    /// 文字は文字そのもの)。値がなければ空。
    /// </summary>
    public static string EditText(string typeId, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Endianness endian, InspectorOptions o)
    {
        InspectorType type = InspectorTypes.Get(typeId);
        InspectorOptions plain = o with
        {
            Culture = CultureInfo.InvariantCulture,
            DigitGrouping = false,
            IntegerBase = o.IntegerBase,
            FloatFormat = o.FloatFormat == FloatFormat.HexFloat ? FloatFormat.HexFloat : FloatFormat.Shortest,
            DateTimeStyle = DateTimeStyle.Iso8601,
            Text = o.Text with { NoTimeZone = string.Empty, Denormal = "{0}", KindLocal = string.Empty, KindUnspecified = string.Empty },
        };
        InspectorValue value = Decode(type, data, states, endian, plain);
        if (value.Status != InspectorStatus.Ok)
        {
            return string.Empty;
        }

        // フェーズ 2 の型: 値の後ろの注記 (バイト数・形式) を除く。
        if (InspectorTypes.IsString(type.Id))
        {
            return QuoteString(value.FullText ?? string.Empty);
        }

        if (type.Group == InspectorGroup.VarInt || type.Id == InspectorTypes.DigitsDateTime)
        {
            int note = value.Text.LastIndexOf(" (", StringComparison.Ordinal);
            return (note > 0 ? value.Text[..note] : value.Text).Trim();
        }

        if (InspectorTypes.IsColor(type.Id))
        {
            return value.Text.Split(' ', 2)[0];
        }

        switch (type.Group)
        {
            case InspectorGroup.Text:
            {
                // 「あ  U+3042  (3 バイト)」の先頭の文字。制御文字 (名前で表示するもの) は空にする。
                int cut = value.Text.IndexOf("  U+", StringComparison.Ordinal);
                string ch = cut > 0 ? value.Text[..cut] : string.Empty;
                bool controlName = ch.Length >= 2 && ch.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
                return controlName ? string.Empty : ch;
            }

            case InspectorGroup.Float:
                return value.Text switch
                {
                    "+0" => "0",
                    "∞" => "inf",
                    "-∞" => "-inf",
                    _ when value.Text.StartsWith("NaN", StringComparison.Ordinal) => "nan",
                    _ => value.Text,
                };
            case InspectorGroup.DateTime:
                return value.Text.Trim();
            default:
                return value.Text;
        }
    }

    /// <summary>
    /// 必要なバイトがそろっているか。足りなければ「—」(残りのバイト数をツールチップで示す)、読み込み中なら「…」、
    /// 読めなければ「読み込めません」。そろっていれば null。
    /// </summary>
    private static InspectorValue? Check(int size, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorOptions o)
    {
        int count = Math.Min(size, data.Length);
        if (!states.IsEmpty)
        {
            ReadOnlySpan<ByteState> needed = states[..Math.Min(count, states.Length)];
            if (needed.Contains(ByteState.Unreadable))
            {
                return new InspectorValue(InspectorStatus.Unreadable, o.Text.Unreadable, size);
            }

            if (needed.Contains(ByteState.Loading))
            {
                return new InspectorValue(InspectorStatus.Loading, o.Text.Loading, size);
            }
        }

        if (size > data.Length)
        {
            return new InspectorValue(InspectorStatus.NotEnoughData, o.Text.NotEnoughValue, size,
                string.Format(o.Culture, o.Text.NotEnoughTip, size, data.Length));
        }

        return null;
    }

    /// <summary>エンディアンに従って最大 8 バイトの値を組み立てる。</summary>
    public static ulong ReadBits(ReadOnlySpan<byte> bytes, Endianness endian)
    {
        ulong value = 0;
        if (endian == Endianness.Little)
        {
            for (int i = bytes.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | bytes[i];
            }
        }
        else
        {
            foreach (byte b in bytes)
            {
                value = (value << 8) | b;
            }
        }

        return value;
    }

    /// <summary>値の下位 <paramref name="size"/> バイトを、エンディアンに従ったバイト列にする。</summary>
    public static byte[] WriteBits(ulong value, int size, Endianness endian)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            byte b = (byte)(value >> (8 * i));
            bytes[endian == Endianness.Little ? i : size - 1 - i] = b;
        }

        return bytes;
    }

    // ---- 整数 (INSP-03) ----

    private static InspectorValue Integer(ReadOnlySpan<byte> bytes, Endianness endian, bool signed, InspectorOptions o)
    {
        int size = bytes.Length;
        ulong bits = ReadBits(bytes, endian);
        BigInteger value = signed ? SignExtend(bits, size) : bits;
        return new InspectorValue(InspectorStatus.Ok, NumberFormat.Integer(value, bits, size, o.IntegerBase, o.DigitGrouping, o.Culture), size);
    }

    public static long SignExtend(ulong bits, int size)
    {
        int shift = 64 - size * 8;
        return shift == 0 ? (long)bits : ((long)(bits << shift)) >> shift;
    }

    // ---- 浮動小数点 (INSP-05) ----

    private static InspectorValue Float(ReadOnlySpan<byte> bytes, Endianness endian, bool isFloat, InspectorOptions o)
    {
        ulong bits = ReadBits(bytes, endian);
        double value = isFloat ? BitConverter.Int32BitsToSingle((int)(uint)bits) : BitConverter.Int64BitsToDouble((long)bits);
        string text = NumberFormat.Float(value, bits, isFloat, o.FloatFormat, o.Culture);
        bool subnormal = isFloat ? float.IsSubnormal((float)value) : double.IsSubnormal(value);
        if (subnormal)
        {
            text = string.Format(o.Culture, o.Text.Denormal, text);
        }

        return new InspectorValue(InspectorStatus.Ok, text, bytes.Length);
    }

    // ---- 2 進 (INSP-08) ----

    /// <summary>最上位ビットを左にして 4 bit ごとに空白で区切る (<c>0100 0001</c>)。</summary>
    public static string BinaryText(ulong value, int bits)
    {
        var text = new StringBuilder(bits + bits / 4);
        for (int i = bits - 1; i >= 0; i--)
        {
            text.Append(((value >> i) & 1) != 0 ? '1' : '0');
            if (i > 0 && i % 4 == 0)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    // ---- 文字 (INSP-09) ----

    private static InspectorValue DecodeAnsi(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorOptions o)
    {
        if (Check(1, data, states, o) is { } problem)
        {
            return problem;
        }

        Encoding encoding = o.AnsiEncoding ?? DefaultAnsi;
        Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (TryDecode(strict, data[..1], out string? one))
        {
            return CharValue(one, 1, o);
        }

        // 2 バイト文字コードの先行バイト (Shift_JIS など) なら 2 バイトで解釈する。
        if (!encoding.IsSingleByte)
        {
            if (Check(2, data, states, o) is { Status: not InspectorStatus.NotEnoughData } wait)
            {
                return wait;
            }

            if (data.Length >= 2 && TryDecode(strict, data[..2], out string? two) && two.Length == 1)
            {
                return CharValue(two, 2, o);
            }
        }

        return Invalid(o.Text.ReasonUndefined, 1, o);
    }

    /// <summary>システムの ANSI コードページ。</summary>
    public static Encoding DefaultAnsi
    {
        get
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1;
            }
        }
    }

    private static bool TryDecode(Encoding strict, ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = strict.GetString(bytes);
            return text.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static InspectorValue DecodeUtf8(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorOptions o)
    {
        if (Check(1, data, states, o) is { } problem)
        {
            return problem;
        }

        byte lead = data[0];
        (int length, int initial) = lead switch
        {
            < 0x80 => (1, lead),
            < 0xC0 => (-1, 0),
            < 0xC2 => (-2, 0),
            < 0xE0 => (2, lead & 0x1F),
            < 0xF0 => (3, lead & 0x0F),
            < 0xF5 => (4, lead & 0x07),
            _ => (-3, 0),
        };
        if (length == -1)
        {
            return Invalid(o.Text.ReasonContinuation, 1, o);
        }

        if (length == -2)
        {
            return Invalid(o.Text.ReasonOverlong, 1, o);
        }

        if (length < 0)
        {
            return Invalid(o.Text.ReasonBadLead, 1, o);
        }

        int cp = initial;
        for (int i = 1; i < length; i++)
        {
            if (i >= data.Length)
            {
                return Invalid(o.Text.ReasonIncomplete, i, o);
            }

            if (!states.IsEmpty && i < states.Length && states[i] != ByteState.Valid)
            {
                return states[i] == ByteState.Loading
                    ? new InspectorValue(InspectorStatus.Loading, o.Text.Loading, length)
                    : new InspectorValue(InspectorStatus.Unreadable, o.Text.Unreadable, length);
            }

            if ((data[i] & 0xC0) != 0x80)
            {
                return Invalid(o.Text.ReasonMissingContinuation, i, o);
            }

            cp = (cp << 6) | (data[i] & 0x3F);
        }

        if (length == 3 && cp < 0x800 || length == 4 && cp < 0x10000)
        {
            return Invalid(o.Text.ReasonOverlong, length, o);
        }

        if (cp is >= 0xD800 and <= 0xDFFF)
        {
            return Invalid(o.Text.ReasonSurrogate, length, o);
        }

        if (cp > 0x10FFFF)
        {
            return Invalid(o.Text.ReasonTooLarge, length, o);
        }

        return CharValue(char.ConvertFromUtf32(cp), length, o);
    }

    private static InspectorValue DecodeUtf16(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Endianness endian, InspectorOptions o)
    {
        if (Check(2, data, states, o) is { } problem)
        {
            return problem;
        }

        char first = (char)ReadBits(data[..2], endian);
        if (char.IsLowSurrogate(first))
        {
            return Invalid(o.Text.ReasonUnpairedLow, 2, o);
        }

        if (!char.IsHighSurrogate(first))
        {
            return CharValue(first.ToString(), 2, o);
        }

        if (Check(4, data, states, o) is { } wait)
        {
            return wait.Status == InspectorStatus.NotEnoughData ? Invalid(o.Text.ReasonUnpairedHigh, 2, o) : wait;
        }

        char second = (char)ReadBits(data[2..4], endian);
        if (!char.IsLowSurrogate(second))
        {
            return Invalid(o.Text.ReasonUnpairedHigh, 2, o);
        }

        return CharValue(new string([first, second]), 4, o);
    }

    private static InspectorValue Invalid(string reason, int bytes, InspectorOptions o) =>
        new(InspectorStatus.Invalid, string.Format(o.Culture, o.Text.Invalid, reason), bytes);

    /// <summary>文字の表示: 文字 (制御文字は名前)、コードポイント、バイト数 (<c>あ  U+3042  (3 バイト)</c>)。</summary>
    private static InspectorValue CharValue(string text, int bytes, InspectorOptions o)
    {
        int cp = char.ConvertToUtf32(text, 0);
        string shown = cp switch
        {
            < 0x20 => ControlNames[cp],
            0x7F => "DEL",
            >= 0x80 and <= 0x9F => string.Empty,

            // 代替フォントでも表示できない文字は、コードポイントだけを表示する (INSP-09 の仕様 3)。
            _ when o.CanDisplay is { } canDisplay && !canDisplay(cp) => string.Empty,
            _ => text,
        };
        string count = bytes == 1 ? o.Text.OneByte : string.Format(o.Culture, o.Text.Bytes, bytes);
        string code = "U+" + cp.ToString(cp > 0xFFFF ? "X5" : "X4", CultureInfo.InvariantCulture);
        string value = shown.Length > 0 ? $"{shown}  {code}  {count}" : $"{code}  {count}";
        return new InspectorValue(InspectorStatus.Ok, value, bytes);
    }

    // ---- GUID / UUID (INSP-11) ----

    private static InspectorValue Guid(ReadOnlySpan<byte> bytes, bool windows, InspectorOptions o)
    {
        // RFC の並び (ビッグエンディアン) に直したバイト列で構成要素を求める。Windows 形式では先頭 3 フィールドが逆順。
        byte[] raw = bytes.ToArray();
        int[] map = windows ? [3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15] : [.. Enumerable.Range(0, 16)];
        byte[] rfc = [.. map.Select(i => raw[i])];

        string hex = Convert.ToHexString(rfc);
        string text = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        text = windows ? "{" + text + "}" : text.ToLowerInvariant();

        var components = new List<InspectorComponent>();
        int version = rfc[6] >> 4;
        components.Add(new("version", o.Text.ComponentVersion,
            version is >= 1 and <= 8 ? version.ToString(CultureInfo.InvariantCulture) : o.Text.VersionUnknown, map[6], 1));
        int v = rfc[8];
        string variant = (v & 0x80) == 0 ? o.Text.VariantNcs : (v & 0xC0) == 0x80 ? o.Text.VariantRfc : (v & 0xE0) == 0xC0 ? o.Text.VariantMicrosoft : o.Text.VariantReserved;
        components.Add(new("variant", o.Text.ComponentVariant, variant, map[8], 1));
        if (version is 1 or 6)
        {
            ulong timeLow = (ulong)rfc[0] << 24 | (ulong)rfc[1] << 16 | (ulong)rfc[2] << 8 | rfc[3];
            ulong timeMid = (ulong)rfc[4] << 8 | rfc[5];
            ulong timeHigh = ((ulong)rfc[6] << 8 | rfc[7]) & 0x0FFF;
            ulong timestamp = version == 1
                ? timeHigh << 48 | timeMid << 32 | timeLow
                : timeLow << 28 | timeMid << 12 | timeHigh;
            components.Add(new("timestamp", o.Text.ComponentTimestamp, Timestamp100ns(timestamp, o), windows ? 0 : 0, 8));
            int clock = (rfc[8] << 8 | rfc[9]) & 0x3FFF;
            components.Add(new("clockSequence", o.Text.ComponentClockSequence, "0x" + clock.ToString("X4", CultureInfo.InvariantCulture), 8, 2));
            string node = string.Join("-", rfc[10..].Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            if ((rfc[10] & 1) != 0)
            {
                node = string.Format(o.Culture, o.Text.RandomNode, node);
            }

            components.Add(new("node", o.Text.ComponentNode, node, 10, 6));
        }
        else if (version == 7)
        {
            long ms = 0;
            for (int i = 0; i < 6; i++)
            {
                ms = (ms << 8) | rfc[i];
            }

            string time = InspectorDateTime.FormatInstant(InspectorDateTime.UnixEpoch.AddMilliseconds(ms), 3, o)
                ?? string.Format(o.Culture, o.Text.OutOfRange, ms);
            components.Add(new("unixTime", o.Text.ComponentUnixTime, time, 0, 6));
        }

        return new InspectorValue(InspectorStatus.Ok, text, 16, null, components);
    }

    private static string Timestamp100ns(ulong ticks, InspectorOptions o)
    {
        ulong max = (ulong)(DateTime.MaxValue.Ticks - InspectorDateTime.GregorianEpoch.Ticks);
        if (ticks > max)
        {
            return string.Format(o.Culture, o.Text.OutOfRange, ticks);
        }

        DateTime utc = new(InspectorDateTime.GregorianEpoch.Ticks + (long)ticks, DateTimeKind.Utc);
        return InspectorDateTime.FormatInstant(utc, 7, o) ?? string.Format(o.Culture, o.Text.OutOfRange, ticks);
    }

    // ---- 日時 (INSP-13) ----

    /// <summary>Unix 時刻 (秒) の表示できる範囲 (0001-01-01〜9999-12-31)。</summary>
    private static readonly long MinUnixSeconds = (DateTime.MinValue.Ticks - InspectorDateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
    private static readonly long MaxUnixSeconds = (DateTime.MaxValue.Ticks - InspectorDateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

    private static InspectorValue UnixSeconds(long seconds, int size, InspectorOptions o)
    {
        if (seconds < MinUnixSeconds || seconds > MaxUnixSeconds)
        {
            return OutOfRange(seconds.ToString(CultureInfo.InvariantCulture), size, o);
        }

        DateTime utc = InspectorDateTime.UnixEpoch.AddSeconds(seconds);
        return InspectorDateTime.FormatInstant(utc, 0, o) is { } text
            ? new InspectorValue(InspectorStatus.Ok, text, size)
            : OutOfRange(seconds.ToString(CultureInfo.InvariantCulture), size, o);
    }

    private static InspectorValue FileTime(ulong value, InspectorOptions o)
    {
        ulong max = (ulong)(DateTime.MaxValue.Ticks - InspectorDateTime.FileTimeEpoch.Ticks);
        if (value > max)
        {
            return OutOfRange(value.ToString(CultureInfo.InvariantCulture), 8, o);
        }

        DateTime utc = new(InspectorDateTime.FileTimeEpoch.Ticks + (long)value, DateTimeKind.Utc);
        return InspectorDateTime.FormatInstant(utc, 7, o) is { } text
            ? new InspectorValue(InspectorStatus.Ok, text, 8)
            : OutOfRange(value.ToString(CultureInfo.InvariantCulture), 8, o);
    }

    private static InspectorValue OutOfRange(string raw, int size, InspectorOptions o) =>
        new(InspectorStatus.Invalid, string.Format(o.Culture, o.Text.OutOfRange, raw), size);

    /// <summary>DOS 日付・時刻 (INSP-13 の仕様 1・4)。どちらかが null なら片方だけの形式。</summary>
    private static InspectorValue DosDateTime(ushort? date, ushort? time, InspectorOptions o)
    {
        int size = (date is null ? 0 : 2) + (time is null ? 0 : 2);
        int year = 1980, month = 1, day = 1, hour = 0, minute = 0, second = 0;
        if (date is ushort d)
        {
            year = 1980 + (d >> 9);
            month = (d >> 5) & 0x0F;
            day = d & 0x1F;
            if (month is 0 or > 12)
            {
                return InvalidField(o.Text.FieldMonth, month, size, o);
            }

            if (day == 0 || day > DateTime.DaysInMonth(year, month))
            {
                return InvalidField(o.Text.FieldDay, day, size, o);
            }
        }

        if (time is ushort t)
        {
            hour = t >> 11;
            minute = (t >> 5) & 0x3F;
            second = (t & 0x1F) * 2;
            if (hour >= 24)
            {
                return InvalidField(o.Text.FieldHour, hour, size, o);
            }

            if (minute >= 60)
            {
                return InvalidField(o.Text.FieldMinute, minute, size, o);
            }

            if (second >= 60)
            {
                return InvalidField(o.Text.FieldSecond, second, size, o);
            }
        }

        var value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        InspectorDateTime.Parts parts = date is null ? InspectorDateTime.Parts.Time : time is null ? InspectorDateTime.Parts.Date : InspectorDateTime.Parts.DateAndTime;
        return new InspectorValue(InspectorStatus.Ok, InspectorDateTime.FormatNoZone(value, 0, o, parts), size);
    }

    private static InspectorValue InvalidField(string field, int value, int size, InspectorOptions o) =>
        Invalid(string.Format(o.Culture, field, value), size, o);
}
