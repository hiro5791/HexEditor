using System.Globalization;

namespace HexEditor.Core.Hashing;

/// <summary>値の表示形式 (ANA-18 の仕様 5)。</summary>
public enum HashValueFormat
{
    HexUpper,
    HexLower,
    Base64,

    /// <summary>10 進 (64 bit 以下のアルゴリズムのみ。それ以外は Hex 大文字で表示する)。</summary>
    Decimal,
}

/// <summary>値の表示 (ANA-18 の仕様 5・6)。16 進の値は地域設定に依存しない (0.4)。</summary>
public static class HashValueFormatter
{
    /// <summary>
    /// 値を表示する。<paramref name="littleEndian"/> が真なら、64 bit 以下の数値の値をリトルエンディアンのバイト列で表記する
    /// (ANA-18 の仕様 6)。10 進は数値としての値なのでバイト順に依存しない。
    /// </summary>
    public static string Format(ReadOnlySpan<byte> value, bool numeric, HashValueFormat format, bool littleEndian = false)
    {
        byte[] bytes = Arrange(value, numeric, littleEndian);
        return format switch
        {
            HashValueFormat.HexLower => Convert.ToHexStringLower(bytes),
            HashValueFormat.Base64 => Convert.ToBase64String(bytes),
            HashValueFormat.Decimal when numeric && value.Length <= 8 => HashBytes.ToNumber(value).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToHexString(bytes),
        };
    }

    /// <summary>
    /// 数値の値 (ビッグエンディアンのバイト列、8 バイト以下) を、値のビット数の符号付き整数とみなした 10 進の文字列にする
    /// (ANA-19 の仕様 1 の「符号ありの場合、結果は符号付き 10 進でも表示する」)。例: <c>FF FE</c> は <c>-2</c>。
    /// </summary>
    public static string FormatSignedDecimal(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > 8)
        {
            throw new ArgumentException("8 バイト以下の値を指定してください。", nameof(value));
        }

        return ToSigned(HashBytes.ToNumber(value), value.Length * 8).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>下位 <paramref name="bits"/> ビットを符号付き整数とみなす (2 の補数)。</summary>
    public static long ToSigned(ulong value, int bits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 64);
        int shift = 64 - bits;
        return unchecked((long)(value << shift) >> shift);
    }

    /// <summary>表示・書き込みに使うバイト列 (数値の値で LE を選んだときは逆順)。</summary>
    public static byte[] Arrange(ReadOnlySpan<byte> value, bool numeric, bool littleEndian)
    {
        byte[] bytes = value.ToArray();
        if (numeric && littleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }
}
