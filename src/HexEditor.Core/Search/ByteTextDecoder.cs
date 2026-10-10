using System.Text;

namespace HexEditor.Core.Search;

/// <summary>
/// バイト列を、文字とその位置 (バイトのオフセット) の対応を保ったまま復号する (正規表現によるテキスト検索 (FIND-18 の仕様 3・4) と
/// 文字列の抽出 (FIND-32))。復号できないバイトは U+FFFD 1 文字にする。UTF-8・UTF-16・UTF-32・1 バイトの文字コード・2 バイトの
/// 文字コード (Shift_JIS、EUC-JP、GBK、Big5、EUC-KR など) と GB18030 の 4 バイトの文字を扱う。
/// </summary>
internal sealed class ByteTextDecoder
{
    private static readonly Dictionary<int, ByteTextDecoder> Cache = [];

    private readonly Kind _kind;
    private readonly bool _bigEndian;
    private readonly char[]? _single;
    private readonly int[]? _singleCodePoint;
    private readonly bool[]? _lead;
    private readonly int[]? _pairs;
    private readonly Encoding? _encoding;

    private enum Kind
    {
        SingleByte,
        Utf8,
        Utf16,
        Utf32,
        DoubleByte,
    }

    private ByteTextDecoder(Encoding encoding)
    {
        int cp = encoding.CodePage;
        switch (cp)
        {
            case 65001:
                _kind = Kind.Utf8;
                break;
            case 1200 or 1201:
                _kind = Kind.Utf16;
                _bigEndian = cp == 1201;
                break;
            case 12000 or 12001:
                _kind = Kind.Utf32;
                _bigEndian = cp == 12001;
                break;
            default:
                Encoding decoding = Encoding.GetEncoding(cp, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));
                _encoding = decoding;
                if (encoding.IsSingleByte)
                {
                    _kind = Kind.SingleByte;
                    _single = new char[256];
                    _singleCodePoint = new int[256];
                    char[] chars = new char[4];
                    for (int b = 0; b < 256; b++)
                    {
                        int n = decoding.GetChars([(byte)b], 0, 1, chars, 0);
                        char c = n == 1 ? chars[0] : '�';
                        _single[b] = c;
                        _singleCodePoint[b] = c == '�' || char.IsSurrogate(c) ? -1 : c;
                    }
                }
                else
                {
                    _kind = Kind.DoubleByte;
                    _singleCodePoint = new int[256];
                    _lead = new bool[256];
                    _pairs = new int[65536];
                    char[] chars = new char[8];
                    for (int b = 0; b < 256; b++)
                    {
                        _singleCodePoint[b] = DecodeOne(decoding, [(byte)b], chars);
                    }

                    for (int lead = 0x80; lead < 256; lead++)
                    {
                        for (int trail = 0; trail < 256; trail++)
                        {
                            int value = DecodeOne(decoding, [(byte)lead, (byte)trail], chars);
                            _pairs[(lead << 8) | trail] = value;
                            if (value >= 0)
                            {
                                _lead[lead] = true;
                            }
                        }
                    }
                }

                break;
        }
    }

    /// <summary>文字の境界の単位 (UTF-16 は 2、UTF-32 は 4、ほかは 1)。単位が 2 以上なら、位相ごとに復号し直す。</summary>
    public int Unit => _kind switch
    {
        Kind.Utf16 => 2,
        Kind.Utf32 => 4,
        _ => 1,
    };

    /// <summary>文字コードの復号器 (コードページごとに 1 回だけ作る)。</summary>
    public static ByteTextDecoder For(Encoding encoding)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(encoding.CodePage, out ByteTextDecoder? decoder))
            {
                decoder = new ByteTextDecoder(encoding);
                Cache[encoding.CodePage] = decoder;
            }

            return decoder;
        }
    }

    /// <summary>
    /// <paramref name="bytes"/> の <paramref name="at"/> から 1 文字を読む。戻り値は使ったバイト数 (1 以上)。<paramref name="codePoint"/> は
    /// 符号位置で、復号できなければ −1 (U+FFFD 1 文字として扱う)。
    /// </summary>
    public int Next(ReadOnlySpan<byte> bytes, int at, out int codePoint)
    {
        int left = bytes.Length - at;
        switch (_kind)
        {
            case Kind.SingleByte:
                codePoint = _singleCodePoint![bytes[at]];
                return 1;

            case Kind.Utf8:
                return NextUtf8(bytes, at, out codePoint);

            case Kind.Utf16:
            {
                if (left < 2)
                {
                    codePoint = -1;
                    return left;
                }

                int unit = _bigEndian ? (bytes[at] << 8) | bytes[at + 1] : bytes[at] | (bytes[at + 1] << 8);
                if (unit is >= 0xD800 and <= 0xDBFF && left >= 4)
                {
                    int low = _bigEndian ? (bytes[at + 2] << 8) | bytes[at + 3] : bytes[at + 2] | (bytes[at + 3] << 8);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                    {
                        codePoint = char.ConvertToUtf32((char)unit, (char)low);
                        return 4;
                    }
                }

                codePoint = unit is >= 0xD800 and <= 0xDFFF ? -1 : unit;
                return 2;
            }

            case Kind.Utf32:
            {
                if (left < 4)
                {
                    codePoint = -1;
                    return left;
                }

                long value = _bigEndian
                    ? ((long)bytes[at] << 24) | ((long)bytes[at + 1] << 16) | ((long)bytes[at + 2] << 8) | bytes[at + 3]
                    : bytes[at] | ((long)bytes[at + 1] << 8) | ((long)bytes[at + 2] << 16) | ((long)bytes[at + 3] << 24);
                codePoint = value is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? -1 : (int)value;
                return 4;
            }

            default:
            {
                byte b = bytes[at];
                if (_encoding!.CodePage == 54936 && b is >= 0x81 and <= 0xFE && left >= 4 && bytes[at + 1] is >= 0x30 and <= 0x39
                    && bytes[at + 2] is >= 0x81 and <= 0xFE && bytes[at + 3] is >= 0x30 and <= 0x39)
                {
                    codePoint = DecodeOne(_encoding, bytes.Slice(at, 4).ToArray(), new char[8]);
                    return 4;
                }

                if (_lead![b] && left >= 2 && _pairs![(b << 8) | bytes[at + 1]] is int pair and >= 0)
                {
                    codePoint = pair;
                    return 2;
                }

                codePoint = _singleCodePoint![b];
                return 1;
            }
        }
    }

    /// <summary>
    /// 全体を復号する。<paramref name="chars"/> に UTF-16 の文字を、<paramref name="offsets"/> に各文字の始まりのバイトの位置を書き、
    /// <c>offsets[文字数]</c> にはバイト数を書く。補助平面の文字 (サロゲートペア) は 2 文字とも同じ位置にする。戻り値は文字数。
    /// 配列は <paramref name="bytes"/> の長さ + 1 以上にする。
    /// </summary>
    public int Decode(ReadOnlySpan<byte> bytes, char[] chars, int[] offsets)
    {
        int n = 0;
        if (_kind == Kind.SingleByte)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i] = _single![bytes[i]];
                offsets[i] = i;
            }

            offsets[bytes.Length] = bytes.Length;
            return bytes.Length;
        }

        int at = 0;
        while (at < bytes.Length)
        {
            int used = Next(bytes, at, out int cp);
            if (cp >= 0x10000)
            {
                string pair = char.ConvertFromUtf32(cp);
                chars[n] = pair[0];
                offsets[n++] = at;
                chars[n] = pair[1];
                offsets[n++] = at;
            }
            else
            {
                chars[n] = cp < 0 ? '�' : (char)cp;
                offsets[n++] = at;
            }

            at += Math.Max(1, used);
        }

        offsets[n] = bytes.Length;
        return n;
    }

    /// <summary>UTF-8 の 1 文字 (Unicode の表 3-7 の正しい並びだけ。不正な並びは最大の部分を 1 文字にする)。</summary>
    private static int NextUtf8(ReadOnlySpan<byte> bytes, int at, out int codePoint)
    {
        byte b0 = bytes[at];
        if (b0 < 0x80)
        {
            codePoint = b0;
            return 1;
        }

        (int length, int min2, int max2) = b0 switch
        {
            >= 0xC2 and <= 0xDF => (2, 0x80, 0xBF),
            0xE0 => (3, 0xA0, 0xBF),
            >= 0xE1 and <= 0xEC => (3, 0x80, 0xBF),
            0xED => (3, 0x80, 0x9F),
            >= 0xEE and <= 0xEF => (3, 0x80, 0xBF),
            0xF0 => (4, 0x90, 0xBF),
            >= 0xF1 and <= 0xF3 => (4, 0x80, 0xBF),
            0xF4 => (4, 0x80, 0x8F),
            _ => (0, 0, 0),
        };
        codePoint = -1;
        if (length == 0 || at + 1 >= bytes.Length || bytes[at + 1] < min2 || bytes[at + 1] > max2)
        {
            return 1;
        }

        int cp = bytes[at + 1] & 0x3F;
        for (int i = 2; i < length; i++)
        {
            if (at + i >= bytes.Length || (bytes[at + i] & 0xC0) != 0x80)
            {
                return i;
            }

            cp = (cp << 6) | (bytes[at + i] & 0x3F);
        }

        int leadBits = length switch { 2 => 0x1F, 3 => 0x0F, _ => 0x07 };
        codePoint = ((b0 & leadBits) << (6 * (length - 1))) | cp;
        return length;
    }

    /// <summary>バイト列がちょうど 1 文字になればその符号位置、ならなければ −1。</summary>
    private static int DecodeOne(Encoding encoding, byte[] bytes, char[] chars)
    {
        int n = encoding.GetChars(bytes, 0, bytes.Length, chars, 0);
        if (n == 1 && chars[0] != '�' && !char.IsSurrogate(chars[0]))
        {
            return chars[0];
        }

        if (n == 2 && char.IsSurrogatePair(chars[0], chars[1]))
        {
            return char.ConvertToUtf32(chars[0], chars[1]);
        }

        return -1;
    }
}
