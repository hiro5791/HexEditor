using System.Globalization;
using System.Text;

namespace HexEditor.Core.View;

/// <summary>
/// オフセットの書式 (VIEW-19、VIEW-20、VIEW-12)。オフセット列・列見出し・ステータスバー・移動バーの解釈結果で共通に使う。
/// 16 進の表記は地域設定の影響を受けない。接頭辞 <c>0x</c> の <c>x</c> は常に小文字。
/// </summary>
public sealed class OffsetFormat
{
    private int _digits;

    public OffsetFormat(ViewSettings settings, long maxCursor, int sectorSize, long? referencePoint = null)
    {
        Settings = settings;
        SectorSize = sectorSize > 0 ? sectorSize : 512;
        ReferencePoint = referencePoint;
        Grow(maxCursor);
    }

    public ViewSettings Settings { get; }

    public OffsetRadix Radix => Settings.Radix;

    public bool Lowercase => Settings.LowercaseHex;

    public ulong BaseAddress => Settings.BaseAddress;

    public int SectorSize { get; }

    /// <summary>基準点 p (VIEW-20 の仕様 6)。あればオフセット列とステータスバーに相対オフセットを出す。</summary>
    public long? ReferencePoint { get; }

    public bool UsesBaseAddress => BaseAddress != 0 && ReferencePoint is null;

    /// <summary>オフセット列の数字の桁数 (VIEW-19 の仕様 3)。</summary>
    public int Digits => _digits;

    /// <summary>オフセット列の文字数 (符号・区切りを含む)。</summary>
    public int ColumnWidth
    {
        get
        {
            if (Radix == OffsetRadix.Sector)
            {
                return _digits;
            }

            int width = _digits;
            if (Radix == OffsetRadix.Hex && Settings.HexDigitSeparator)
            {
                width += (_digits - 1) / 4;
            }

            return ReferencePoint is null ? width : width + 1;
        }
    }

    /// <summary>
    /// 表示しうる最大のアドレスに合わせて桁数を増やす (減らさない。表示中に幅が揺れないようにする。VIEW-19 の仕様 3)。桁数が変わったら true。
    /// </summary>
    public bool Grow(long maxCursor)
    {
        int digits = DigitsFor(maxCursor);
        if (digits <= _digits)
        {
            return false;
        }

        _digits = digits;
        return true;
    }

    private int DigitsFor(long maxCursor)
    {
        // 基準点があるときは、基準点からの距離の最大 (前後のどちらか遠い方)。
        ulong max = ReferencePoint is { } p
            ? (ulong)Math.Max(Math.Max(0, maxCursor) - Math.Min(p, maxCursor), Math.Max(0, p))
            : unchecked(BaseAddress + (ulong)Math.Max(0, maxCursor));
        switch (Radix)
        {
            case OffsetRadix.Decimal:
                return Math.Max(1, max.ToString(CultureInfo.InvariantCulture).Length);
            case OffsetRadix.Octal:
                return Math.Max(8, ToOctal(max).Length);
            case OffsetRadix.Sector:
                ulong sectors = max / (ulong)SectorSize;
                return sectors.ToString(CultureInfo.InvariantCulture).Length + 1 + HexLength((ulong)SectorSize - 1);
            default:
                return Math.Max(StatusFormat.MinHexDigits, HexLength(max));
        }
    }

    private static int HexLength(ulong value)
    {
        int digits = 1;
        for (ulong v = value; v > 0xF; v >>= 4)
        {
            digits++;
        }

        return digits;
    }

    /// <summary>表示上のアドレス (オフセット + ベースアドレス。2^64 を法とする)。</summary>
    public ulong AddressOf(long offset) => unchecked(BaseAddress + (ulong)offset);

    /// <summary>
    /// オフセット列の文字列 (VIEW-19 の仕様 2・4・5、VIEW-20 の仕様 3・6)。桁数で埋め、10 進は右寄せ。基準点があれば符号付きの相対オフセット。
    /// </summary>
    public string Column(long offset)
    {
        if (ReferencePoint is { } p)
        {
            long relative = offset - p;
            string sign = relative < 0 ? "-" : "+";
            ulong magnitude = relative < 0 ? (ulong)(-(relative + 1)) + 1 : (ulong)relative;
            return sign + FormatDigits(magnitude, pad: true).PadLeft(_digits);
        }

        string text = Radix == OffsetRadix.Sector ? Sector(AddressOf(offset)) : FormatDigits(AddressOf(offset), pad: true);
        return text.PadLeft(ColumnWidth);
    }

    /// <summary>
    /// ステータスバーのカーソル位置の値 (VIEW-40 の仕様 1)。16 進は <c>0x</c> 付き、ベースアドレスを使うときは <c>@</c> 付き、
    /// 10 進は地域設定の桁区切り付き。基準点があるときは符号付きの相対オフセット (「相対」の語は呼び出し側で付ける)。
    /// </summary>
    public string Status(long offset, CultureInfo culture)
    {
        if (ReferencePoint is not null)
        {
            return Column(offset).Trim();
        }

        ulong address = AddressOf(offset);
        string prefix = UsesBaseAddress ? "@" : string.Empty;
        return Radix switch
        {
            OffsetRadix.Decimal => prefix + address.ToString("N0", culture),
            OffsetRadix.Octal => prefix + (UsesBaseAddress ? string.Empty : "0o") + FormatDigits(address, pad: true),
            OffsetRadix.Sector => prefix + Sector(address),
            _ => prefix + (UsesBaseAddress ? string.Empty : "0x") + FormatDigits(address, pad: true),
        };
    }

    /// <summary>長さなど 0 埋めしない値 (ステータスバーの選択範囲の長さなど)。16 進は <c>0x</c> 付き。</summary>
    public string Value(long value, CultureInfo culture) => Radix switch
    {
        OffsetRadix.Decimal => value.ToString("N0", culture),
        OffsetRadix.Octal => "0o" + ToOctal((ulong)Math.Max(0, value)),
        _ => Hex(value, Lowercase),
    };

    /// <summary>0 埋めしない 16 進 (<c>0x1f00</c>。x は常に小文字)。</summary>
    public static string Hex(long value, bool lowercase) =>
        "0x" + value.ToString(lowercase ? "x" : "X", CultureInfo.InvariantCulture);

    /// <summary>セクタ形式 (VIEW-19 の仕様 2): <c>セクタ番号 (10 進):セクタ内の位置 (16 進)</c>。</summary>
    public string Sector(ulong address) => Sector(address, SectorSize, Lowercase);

    public static string Sector(ulong address, int sectorSize, bool lowercase = false)
    {
        ulong size = (ulong)Math.Max(1, sectorSize);
        return (address / size).ToString(CultureInfo.InvariantCulture) + ":"
            + (address % size).ToString(lowercase ? "x" : "X", CultureInfo.InvariantCulture);
    }

    private string FormatDigits(ulong value, bool pad)
    {
        int width = pad ? _digits : 0;
        string text;
        switch (Radix)
        {
            case OffsetRadix.Decimal:
                return value.ToString(CultureInfo.InvariantCulture);
            case OffsetRadix.Octal:
                text = ToOctal(value);
                return text.PadLeft(width, '0');
            default:
                text = value.ToString((Lowercase ? "x" : "X") + Math.Max(1, width), CultureInfo.InvariantCulture);
                return Settings.HexDigitSeparator && Radix == OffsetRadix.Hex ? InsertSeparators(text) : text;
        }
    }

    private static string ToOctal(ulong value)
    {
        if (value == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, (char)('0' + (int)(value & 7)));
            value >>= 3;
        }

        return sb.ToString();
    }

    /// <summary>4 桁ごとに <c>:</c> を入れる (VIEW-19 の仕様 5。例: <c>0000:1F00</c>)。</summary>
    private static string InsertSeparators(string digits)
    {
        var sb = new StringBuilder(digits.Length + digits.Length / 4);
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 4 == 0)
            {
                sb.Append(':');
            }

            sb.Append(digits[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 列見出しの数字 (VIEW-05 の仕様 2〜4)。行の中の位置 <paramref name="value"/> を、オフセットの基数 (セクタ表示では 16 進) で
    /// <paramref name="width"/> 文字に収まる下位の桁だけ表す。
    /// </summary>
    public static string RulerLabel(long value, OffsetRadix radix, bool lowercase, int width)
    {
        int b = radix switch
        {
            OffsetRadix.Decimal => 10,
            OffsetRadix.Octal => 8,
            _ => 16,
        };
        var sb = new StringBuilder();
        long v = value;
        for (int i = 0; i < width; i++)
        {
            int digit = (int)(v % b);
            char c = digit < 10 ? (char)('0' + digit) : (char)((lowercase ? 'a' : 'A') + digit - 10);
            sb.Insert(0, c);
            v /= b;
        }

        return sb.ToString();
    }
}
