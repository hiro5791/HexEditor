using HexEditor.Core.Search;

namespace HexEditor.Core.Clipboard;

/// <summary>Hex 文字列とバイト列の変換 (EDIT-22 の Hex 文字列、EDIT-23 の Hex 列への貼り付け)。</summary>
public static class HexText
{
    /// <summary>既定の書式 `DE AD BE EF` (大文字、空白区切り、改行なし)。</summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var chars = new char[bytes.Length * 3 - 1];
        for (int i = 0; i < bytes.Length; i++)
        {
            string hex = bytes[i].ToString("X2");
            chars[i * 3] = hex[0];
            chars[i * 3 + 1] = hex[1];
            if (i < bytes.Length - 1)
            {
                chars[i * 3 + 2] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Hex バイト列の入力 (00-overview 6.3) として読めればバイト列を返す。ワイルドカード・奇数桁・Hex 以外の文字を含む場合は null。
    /// </summary>
    public static byte[]? TryParse(string text)
    {
        try
        {
            SearchPattern pattern = SearchPattern.FromHex(text);
            return pattern.IsLiteral ? pattern.Bytes : null;
        }
        catch (PatternException)
        {
            return null;
        }
    }
}
