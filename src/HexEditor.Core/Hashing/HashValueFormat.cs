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
