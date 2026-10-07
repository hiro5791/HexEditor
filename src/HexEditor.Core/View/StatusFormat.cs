using System.Globalization;

namespace HexEditor.Core.View;

/// <summary>
/// ステータスバーに表示する値の書式 (VIEW-40)。16 進の表記は地域設定の影響を受けず、10 進の数値とサイズは地域設定で書式化する。
/// </summary>
public static class StatusFormat
{
    public const int MinHexDigits = 8;

    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>
    /// オフセットの 16 進の桁数 (VIEW-19 の仕様 3): 表示しうる最大のアドレスを表せる桁数。最低 8 桁。
    /// </summary>
    public static int HexDigits(long maxAddress)
    {
        int digits = 1;
        for (ulong v = (ulong)Math.Max(0, maxAddress); v > 0xF; v >>= 4)
        {
            digits++;
        }

        return Math.Max(MinHexDigits, digits);
    }

    /// <summary>オフセット: <c>0x00001F00</c>。</summary>
    public static string Offset(long offset, int digits) => "0x" + offset.ToString("X" + digits, CultureInfo.InvariantCulture);

    /// <summary>16 進の値 (0 埋めなし): <c>0x100</c>。</summary>
    public static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>カーソルの値: <c>4F</c> と 10 進 <c>79</c>。</summary>
    public static (string Hex, string Decimal) ByteValue(byte value, CultureInfo culture) =>
        (value.ToString("X2", CultureInfo.InvariantCulture), value.ToString(culture));

    /// <summary>10 進の数値 (地域設定の桁区切り付き)。</summary>
    public static string Number(long value, CultureInfo culture) => value.ToString("N0", culture);

    /// <summary>
    /// ファイルサイズの短い表記 (VIEW-40 の仕様 2): 1,024 の累乗で KB / MB / GB / TB、小数 2 桁。1,024 未満は null
    /// (バイト数だけを表示する)。
    /// </summary>
    public static string? ShortSize(long bytes, CultureInfo culture)
    {
        if (bytes < 1024)
        {
            return null;
        }

        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 丸めで 1024.00 になる場合は次の単位にする。
        if (Math.Round(value, 2) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("N2", culture) + " " + Units[unit];
    }
}
