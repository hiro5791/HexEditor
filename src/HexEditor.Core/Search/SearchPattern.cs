using System.Text;

namespace HexEditor.Core.Search;

/// <summary>検索語の誤り (FIND-04 の仕様 6)。UI はこれを見て説明文を出す。</summary>
public enum PatternError
{
    Empty,
    OddDigits,
    InvalidCharacter,
    TooLong,
    NotEncodable,
}

public sealed class PatternException(PatternError error, string detail = "") : Exception($"{error}: {detail}")
{
    public PatternError Error { get; } = error;

    /// <summary>表せない文字など、説明文に入れる語。</summary>
    public string Detail { get; } = detail;
}

/// <summary>
/// 検索するバイト列。ワイルドカードはマスクで表す (マスクのビットが 1 の部分だけを比べる)。マスクがなければリテラル。
/// </summary>
public sealed class SearchPattern
{
    /// <summary>検索語の最大の長さ (FIND-05 の仕様 3)。</summary>
    public const int MaxLength = 1024 * 1024;

    private SearchPattern(byte[] bytes, byte[]? mask)
    {
        Bytes = bytes;
        Mask = mask;
        (AnchorOffset, AnchorLength) = FindAnchor(mask, bytes.Length);
    }

    public byte[] Bytes { get; }

    /// <summary>比べるビット。null ならすべて比べる (リテラル)。</summary>
    public byte[]? Mask { get; }

    public int Length => Bytes.Length;

    public bool IsLiteral => Mask is null;

    /// <summary>ワイルドカードを含まない最も長い区間 (候補の絞り込みに使う)。</summary>
    internal int AnchorOffset { get; }

    internal int AnchorLength { get; }

    public static SearchPattern Literal(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (bytes.Length > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        return new SearchPattern(bytes, null);
    }

    /// <summary>
    /// Hex 文字列から作る (00-overview 6.3、FIND-05、FIND-06)。空白・カンマ・改行・`0x`・`\x` は区切り。
    /// `??` は任意の 1 バイト、`?` は任意の 1 ニブル。
    /// </summary>
    public static SearchPattern FromHex(string text)
    {
        var bytes = new List<byte>();
        var mask = new List<byte>();
        var digits = new List<char>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' && (digits.Count % 2 == 0))
            {
                i += 2;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
            {
                i += 2;
                continue;
            }

            if (!char.IsAsciiHexDigit(c) && c != '?')
            {
                throw new PatternException(PatternError.InvalidCharacter, c.ToString());
            }

            digits.Add(c);
            i++;
        }

        if (digits.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (digits.Count % 2 != 0)
        {
            throw new PatternException(PatternError.OddDigits);
        }

        bool anyWildcard = false;
        for (int d = 0; d < digits.Count; d += 2)
        {
            (int hi, int hiMask) = Nibble(digits[d]);
            (int lo, int loMask) = Nibble(digits[d + 1]);
            bytes.Add((byte)((hi << 4) | lo));
            mask.Add((byte)((hiMask << 4) | loMask));
            anyWildcard |= hiMask == 0 || loMask == 0;
        }

        if (bytes.Count > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        return new SearchPattern([.. bytes], anyWildcard ? [.. mask] : null);
    }

    /// <summary>文字列を文字コードで符号化して作る (FIND-07)。BOM は付けない。表せない文字はエラーにする。</summary>
    public static SearchPattern FromText(string text, Encoding encoding)
    {
        if (text.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        try
        {
            return Literal(strict.GetBytes(text));
        }
        catch (EncoderFallbackException ex)
        {
            throw new PatternException(PatternError.NotEncodable, ex.CharUnknown != '\0' ? ex.CharUnknown.ToString() : ex.CharUnknownHigh.ToString() + ex.CharUnknownLow);
        }
    }

    /// <summary><paramref name="data"/> の先頭がこのパターンに一致するか。</summary>
    public bool MatchesAt(ReadOnlySpan<byte> data)
    {
        if (Mask is null)
        {
            return data.StartsWith(Bytes);
        }

        for (int i = 0; i < Bytes.Length; i++)
        {
            if ((data[i] & Mask[i]) != (Bytes[i] & Mask[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static (int Value, int Mask) Nibble(char c) => c == '?' ? (0, 0) : (Convert.ToInt32(c.ToString(), 16), 0xF);

    private static (int Offset, int Length) FindAnchor(byte[]? mask, int length)
    {
        if (mask is null)
        {
            return (0, length);
        }

        int bestStart = 0;
        int bestLength = 0;
        int runStart = 0;
        for (int i = 0; i <= length; i++)
        {
            if (i == length || mask[i] != 0xFF)
            {
                if (i - runStart > bestLength)
                {
                    bestStart = runStart;
                    bestLength = i - runStart;
                }

                runStart = i + 1;
            }
        }

        return (bestStart, bestLength);
    }
}
