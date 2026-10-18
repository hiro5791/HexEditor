using System.Globalization;
using System.Text;

namespace HexEditor.Core.Calculator;

/// <summary>
/// 結果の表(TOOL-22 の仕様 5): 16 進、10 進 (符号付き・符号なし)、8 進、2 進 (4 桁ごと)、ファイルサイズ表記。
/// 表示用 (地域設定の桁区切り) と、コピーする値 (桁区切りなし) の両方を持つ。小数の結果は 10 進だけ。
/// </summary>
public sealed record CalculatorRows(
    string? Hex,
    string? DecimalSigned,
    string? DecimalUnsigned,
    string? Octal,
    string? Binary,
    string? FileSize,
    string? CopyHex,
    string? CopyDecimalSigned,
    string? CopyDecimalUnsigned,
    string? CopyOctal,
    string? CopyBinary,
    string? CopyFileSize,
    string? DecimalFloat,
    string? CopyDecimalFloat)
{
    public bool IsFloat => DecimalFloat is not null;
}

/// <summary>結果の表示用の文字列を作る (TOOL-22・TOOL-23)。</summary>
public static class CalculatorFormat
{
    /// <summary>
    /// 結果の各行。<paramref name="padBinary"/> なら 2 進をビット幅まで 0 で埋める (プログラマモード)。通常の電卓は最小の桁数
    /// (4 の倍数に揃える)。
    /// </summary>
    public static CalculatorRows Rows(CalcValue value, CalculatorOptions options, CultureInfo culture, bool padBinary = false)
    {
        if (value.IsFloat)
        {
            string shown = Float(value.Double, culture);
            return new CalculatorRows(null, null, null, null, null, null, null, null, null, null, null, null, shown, Float(value.Double, CultureInfo.InvariantCulture));
        }

        Int128 v = value.Integer;
        int width = options.BitWidth;
        ulong bits = Bits(v, width);
        string hex = bits.ToString("X", CultureInfo.InvariantCulture);
        string octal = ToBase(bits, 8);
        string binaryRaw = ToBase(bits, 2);
        if (padBinary)
        {
            binaryRaw = binaryRaw.PadLeft(width, '0');
        }

        string unsigned = bits.ToString(CultureInfo.InvariantCulture);
        string signed = SignedValue(bits, width).ToString(CultureInfo.InvariantCulture);
        string size = FileSize(v, culture);
        string sizeCopy = FileSize(v, CultureInfo.InvariantCulture);
        return new CalculatorRows(
            "0x" + hex,
            SignedValue(bits, width).ToString("N0", culture),
            bits.ToString("N0", culture),
            octal,
            Group4(binaryRaw),
            size,
            "0x" + hex,
            signed,
            unsigned,
            octal,
            binaryRaw,
            sizeCopy,
            null,
            null);
    }

    /// <summary>値のビット列 (ビット幅の分だけ)。</summary>
    public static ulong Bits(Int128 value, int width) => (ulong)(value & ((Int128.One << width) - 1));

    /// <summary>ビット列を符号付きの値として見る。</summary>
    public static long SignedValue(ulong bits, int width)
    {
        if (width == 64)
        {
            return unchecked((long)bits);
        }

        ulong signBit = 1UL << (width - 1);
        return (bits & signBit) != 0 ? unchecked((long)(bits | (ulong.MaxValue << width))) : (long)bits;
    }

    private static string ToBase(ulong value, int radix)
    {
        if (value == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value != 0)
        {
            sb.Insert(0, (char)('0' + (int)(value % (ulong)radix)));
            value /= (ulong)radix;
        }

        return sb.ToString();
    }

    /// <summary>4 桁ごとに空白で区切る (右から数える。先頭は 4 桁に満たなければ 0 で埋める)。</summary>
    public static string Group4(string binary)
    {
        int padded = (binary.Length + 3) / 4 * 4;
        binary = binary.PadLeft(padded, '0');
        var sb = new StringBuilder(padded + padded / 4);
        for (int i = 0; i < binary.Length; i += 4)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(binary, i, 4);
        }

        return sb.ToString();
    }

    /// <summary>倍精度の小数の 10 進表示 (往復で同じ値になる最短の桁数)。</summary>
    public static string Float(double value, CultureInfo culture) => value.ToString("R", culture);

    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    /// <summary>ファイルサイズ表記 (例: <c>1.5 GiB</c>)。1024 の累乗の単位で、小数 2 桁まで (末尾の 0 は省く)。</summary>
    public static string FileSize(Int128 value, CultureInfo culture)
    {
        bool negative = value < 0;
        double size = (double)Int128.Abs(value);
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        string number = unit == 0 ? ((long)size).ToString("N0", culture) : Math.Round(size, 2).ToString("0.##", culture);
        return (negative ? "-" : string.Empty) + number + " " + Units[unit];
    }

    /// <summary>履歴・コピー用の結果の表記 (整数は 16 進、小数は 10 進。桁区切りなし)。</summary>
    public static string Summary(CalcValue value, int width)
    {
        if (value.IsFloat)
        {
            return Float(value.Double, CultureInfo.InvariantCulture);
        }

        return "0x" + Bits(value.Integer, width).ToString("X", CultureInfo.InvariantCulture);
    }
}
