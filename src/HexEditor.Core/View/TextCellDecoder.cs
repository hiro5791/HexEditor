using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>テキスト列の 1 セルの種類 (VIEW-22)。</summary>
public enum TextCellKind : byte
{
    /// <summary>データがない (ドキュメントの外・読み込み中など)。</summary>
    Empty,

    /// <summary>文字の先頭のバイト。<see cref="TextCell.Text"/> に描く文字を入れる。</summary>
    Char,

    /// <summary>文字の範囲の 2 バイト目以降 (空白、または続きの記号で描く。仕様 2)。</summary>
    Continuation,

    /// <summary>不正なバイト列 (仕様 5)。不正の記号と「不正な文字」色で描く。</summary>
    Invalid,

    /// <summary>表示しない文字 (制御文字・ゼロ幅・双方向制御・異体字セレクタ。VIEW-21 の仕様 6、VIEW-22 の仕様 6・7)。</summary>
    NonPrintable,
}

/// <summary>
/// テキスト列の 1 セル。<see cref="Span"/> は文字のバイト数 (先頭のセルだけ)、<see cref="Wide"/> は全角 (東アジアの文字幅が W / F)。
/// <see cref="Offset"/> は文字の先頭のバイトのオフセット (続きのセルでも先頭を指す。VIEW-22 の仕様 9 の「文字の範囲」)。
/// </summary>
public readonly record struct TextCell(TextCellKind Kind, string Text, int Span, bool Wide, long Offset)
{
    public static readonly TextCell None = new(TextCellKind.Empty, string.Empty, 0, false, -1);
}

/// <summary>
/// テキスト列の解読 (VIEW-22)。1 バイト = 1 セルの並びを崩さずに、マルチバイトの文字を先頭のセルに描く。表示する位置に関係なく、
/// 同じバイトは常に同じ文字になるように、同期点 (解読を始める位置) を決める (仕様 4)。読み戻しは最大 4 KiB。
/// </summary>
public static class TextCellDecoder
{
    /// <summary>同期点を探すための読み戻しの最大 (仕様 4、「巨大ファイル」)。</summary>
    public const int MaxLookback = 4096;

    /// <summary>表示範囲の後ろに必要なバイト数 (末尾のセルから始まる文字を最後まで解読するため)。</summary>
    public const int Lookahead = 3;

    /// <summary>結合文字を付ける点線の円 (仕様 6)。</summary>
    public const char DottedCircle = '◌';

    /// <summary>この文字コードで、表示範囲の前に読む必要のあるバイト数。</summary>
    public static int LookbackFor(TextEncoding encoding) => encoding.Kind switch
    {
        TextEncodingKind.Utf8 => 3,
        TextEncodingKind.Utf16 => 3,
        TextEncodingKind.Utf32 => 7,
        TextEncodingKind.DoubleByte or TextEncodingKind.Gb18030 => MaxLookback,
        _ => 0,
    };

    /// <summary>
    /// [<paramref name="windowStart"/>, windowStart + cells.Length) のセルを解読する。<paramref name="data"/> は
    /// [<paramref name="dataStart"/>, dataStart + data.Length) のバイト (表示範囲の前の読み戻しと後ろの先読みを含む)。
    /// <paramref name="states"/> を渡すと、<see cref="ByteState.Valid"/> でないバイトは文字の一部にしない (セルは <see cref="TextCellKind.Empty"/>)。
    /// <paramref name="dataStart"/> が 0 なら、ドキュメントの先頭として扱う。
    /// </summary>
    public static void Decode(TextEncoding encoding, ReadOnlySpan<byte> data, long dataStart, long windowStart, Span<TextCell> cells,
        ReadOnlySpan<ByteState> states = default, int utf16Phase = 0, int utf32Phase = 0)
    {
        cells.Fill(TextCell.None);
        var ctx = new Context(encoding, data, dataStart, windowStart, cells, states);
        switch (encoding.Kind)
        {
            case TextEncodingKind.Utf8:
                DecodeUtf8(ref ctx);
                break;
            case TextEncodingKind.Utf16:
                DecodeUnits(ref ctx, 2, utf16Phase & 1);
                break;
            case TextEncodingKind.Utf32:
                DecodeUnits(ref ctx, 4, utf32Phase & 3);
                break;
            case TextEncodingKind.DoubleByte:
            case TextEncodingKind.Gb18030:
                DecodeDoubleByte(ref ctx);
                break;
            default:
                DecodeSingleByte(ref ctx);
                break;
        }
    }

    /// <summary>解読の途中の状態。</summary>
    private ref struct Context(TextEncoding encoding, ReadOnlySpan<byte> data, long dataStart, long windowStart, Span<TextCell> cells,
        ReadOnlySpan<ByteState> states)
    {
        public readonly TextEncoding Encoding = encoding;
        public readonly ReadOnlySpan<byte> Data = data;
        public readonly long DataStart = dataStart;
        public readonly long WindowStart = windowStart;
        public readonly Span<TextCell> Cells = cells;
        public readonly ReadOnlySpan<ByteState> States = states;

        public readonly long DataEnd => DataStart + Data.Length;

        public readonly long WindowEnd => WindowStart + Cells.Length;

        public readonly bool Has(long offset) => offset >= DataStart && offset < DataEnd
            && (States.IsEmpty || States[(int)(offset - DataStart)] == ByteState.Valid);

        public readonly byte At(long offset) => Data[(int)(offset - DataStart)];

        /// <summary>[offset, offset + length) のバイトがすべて読めているか。</summary>
        public readonly bool HasAll(long offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (!Has(offset + i))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>文字 1 つを、先頭のセルと続きのセルに書く (表示範囲の中の分だけ)。</summary>
        public readonly void Put(long offset, int span, TextCellKind kind, string text, bool wide)
        {
            for (int i = 0; i < span; i++)
            {
                long at = offset + i;
                if (at < WindowStart || at >= WindowEnd)
                {
                    continue;
                }

                Cells[(int)(at - WindowStart)] = i == 0
                    ? new TextCell(kind, text, span, wide, offset)
                    : new TextCell(TextCellKind.Continuation, string.Empty, span, wide, offset);
            }
        }

        public readonly void PutInvalid(long offset, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                if (Has(offset + i))
                {
                    Put(offset + i, 1, TextCellKind.Invalid, ".", false);
                }
            }
        }

        public readonly void PutCodePoint(long offset, int span, int codePoint)
        {
            (TextCellKind kind, string text, bool wide) = Classify(codePoint);
            Put(offset, span, kind, text, wide);
        }
    }

    // ---- 1 バイトの文字コード ----

    private static void DecodeSingleByte(ref Context ctx)
    {
        for (long at = ctx.WindowStart; at < ctx.WindowEnd; at++)
        {
            if (!ctx.Has(at))
            {
                continue;
            }

            byte b = ctx.At(at);
            char c = ctx.Encoding.DisplayChar(b);
            bool hidden = c == TextEncoding.NonPrintable && b != (byte)TextEncoding.NonPrintable;
            ctx.Put(at, 1, hidden ? TextCellKind.NonPrintable : TextCellKind.Char, hidden ? "." : CharString(c), false);
        }
    }

    // ---- UTF-8 (仕様 4 の 1 つ目) ----

    private static void DecodeUtf8(ref Context ctx)
    {
        // 表示範囲の先頭から最大 3 バイト戻り、後続バイト (80〜BF) でない最初のバイトから解読する。
        long start = ctx.WindowStart;
        for (long back = ctx.WindowStart; back >= ctx.WindowStart - 3; back--)
        {
            if (!ctx.Has(back))
            {
                break;
            }

            if ((ctx.At(back) & 0xC0) != 0x80)
            {
                start = back;
                break;
            }
        }

        long at = start;
        while (at < ctx.WindowEnd)
        {
            if (!ctx.Has(at))
            {
                at++;
                continue;
            }

            int length = Utf8Length(ref ctx, at, out int codePoint);
            if (length == 0)
            {
                // 不正なバイトは 1 セルずつ不正の記号にし、次のバイトから解読をやり直す (仕様 5)。
                ctx.PutInvalid(at);
                at++;
                continue;
            }

            ctx.PutCodePoint(at, length, codePoint);
            at += length;
        }
    }

    /// <summary>UTF-8 の 1 文字の長さ (Unicode の表 3-7 の正しい並びだけ)。不正なら 0。</summary>
    private static int Utf8Length(ref Context ctx, long at, out int codePoint)
    {
        byte b0 = ctx.At(at);
        codePoint = b0;
        if (b0 < 0x80)
        {
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
        if (length == 0 || !ctx.HasAll(at, length))
        {
            return 0;
        }

        byte b1 = ctx.At(at + 1);
        if (b1 < min2 || b1 > max2)
        {
            return 0;
        }

        int cp = b1 & 0x3F;
        for (int i = 2; i < length; i++)
        {
            byte bi = ctx.At(at + i);
            if ((bi & 0xC0) != 0x80)
            {
                return 0;
            }

            cp = (cp << 6) | (bi & 0x3F);
        }

        int leadBits = length switch { 2 => 0x1F, 3 => 0x0F, _ => 0x07 };
        codePoint = ((b0 & leadBits) << (6 * (length - 1))) | cp;
        return length;
    }

    // ---- UTF-16 / UTF-32 (仕様 4 の 2・3 つ目) ----

    private static void DecodeUnits(ref Context ctx, int unit, int phase)
    {
        bool big = ctx.Encoding.BigEndian;

        // 区切りの位置に揃える。UTF-16 は下位サロゲートの前の上位サロゲートを見るため、もう 1 単位戻る。
        long aligned = AlignDown(ctx.WindowStart, unit, phase) - (unit == 2 ? 2 : 0);
        long at = Math.Max(aligned, AlignUp(ctx.DataStart, unit, phase));

        // ドキュメントの先頭で、区切りより前の端数のバイトは不正 (1 単位に満たない)。
        if (ctx.DataStart == 0)
        {
            for (long p = 0; p < Math.Min(AlignUp(0, unit, phase), ctx.WindowEnd); p++)
            {
                ctx.PutInvalid(p);
            }
        }

        while (at < ctx.WindowEnd)
        {
            if (!ctx.HasAll(at, unit))
            {
                ctx.PutInvalid(at, unit);
                at += unit;
                continue;
            }

            int value = ReadUnit(ref ctx, at, unit, big);
            if (unit == 4)
            {
                if (value is < 0 or > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
                {
                    ctx.PutInvalid(at, 4);
                }
                else
                {
                    ctx.PutCodePoint(at, 4, value);
                }

                at += 4;
                continue;
            }

            if (value is >= 0xD800 and <= 0xDBFF)
            {
                // 上位サロゲートから始まる 4 バイトを 1 文字とする。
                if (ctx.HasAll(at + 2, 2) && ReadUnit(ref ctx, at + 2, 2, big) is int low and >= 0xDC00 and <= 0xDFFF)
                {
                    ctx.PutCodePoint(at, 4, char.ConvertToUtf32((char)value, (char)low));
                    at += 4;
                    continue;
                }

                ctx.PutInvalid(at, 2);
            }
            else if (value is >= 0xDC00 and <= 0xDFFF)
            {
                ctx.PutInvalid(at, 2);
            }
            else
            {
                ctx.PutCodePoint(at, 2, value);
            }

            at += 2;
        }
    }

    private static int ReadUnit(ref Context ctx, long at, int unit, bool big)
    {
        long value = 0;
        for (int i = 0; i < unit; i++)
        {
            int index = big ? i : unit - 1 - i;
            value = (value << 8) | ctx.At(at + index);
        }

        return value > int.MaxValue ? -1 : (int)value;
    }

    private static long AlignDown(long offset, int unit, int phase)
    {
        long r = ((offset - phase) % unit + unit) % unit;
        return offset - r;
    }

    private static long AlignUp(long offset, int unit, int phase)
    {
        long down = AlignDown(offset, unit, phase);
        return down == offset ? offset : down + unit;
    }

    // ---- 2 バイトの CJK 文字コードと GB18030 (仕様 4 の 4・5 つ目) ----

    private static void DecodeDoubleByte(ref Context ctx)
    {
        DbcsTable table = DbcsTable.For(ctx.Encoding);

        // 表示範囲の先頭から戻って、2 バイト目になりえないバイトの直後を同期点とする。4 KiB 戻っても見つからなければ、
        // オフセットが 4,096 の倍数の位置 (ドキュメントの先頭が近ければ先頭) を同期点とする。
        long sync = -1;
        long limit = Math.Max(ctx.DataStart, ctx.WindowStart - MaxLookback);
        for (long back = ctx.WindowStart - 1; back >= limit; back--)
        {
            if (!ctx.Has(back))
            {
                sync = back + 1;
                break;
            }

            byte b = ctx.At(back);
            if (!table.CanBeTrail[b])
            {
                // 2 バイト目になりえない先頭のバイト (Big5 の 81〜A0 など) は、それ自身が文字の始まり。
                sync = table.Lead[b] ? back : back + 1;
                break;
            }
        }

        if (sync < 0)
        {
            sync = ctx.DataStart == 0 && ctx.WindowStart - MaxLookback <= 0 ? 0 : ctx.WindowStart / MaxLookback * MaxLookback;
            sync = Math.Max(sync, ctx.DataStart);
        }

        long at = sync;
        bool gb18030 = ctx.Encoding.Kind == TextEncodingKind.Gb18030;
        while (at < ctx.WindowEnd)
        {
            if (!ctx.Has(at))
            {
                at++;
                continue;
            }

            byte b = ctx.At(at);
            if (gb18030 && b is >= 0x81 and <= 0xFE && ctx.HasAll(at, 4) && ctx.At(at + 1) is >= 0x30 and <= 0x39
                && ctx.At(at + 2) is >= 0x81 and <= 0xFE && ctx.At(at + 3) is >= 0x30 and <= 0x39)
            {
                byte[] four = [b, ctx.At(at + 1), ctx.At(at + 2), ctx.At(at + 3)];
                if (table.TryDecode(four, out int cp4))
                {
                    ctx.PutCodePoint(at, 4, cp4);
                }
                else
                {
                    ctx.PutInvalid(at, 4);
                }

                at += 4;
                continue;
            }

            if (table.Lead[b])
            {
                if (ctx.Has(at + 1) && table.PairCodePoint(b, ctx.At(at + 1)) is int cp2 and >= 0)
                {
                    ctx.PutCodePoint(at, 2, cp2);
                    at += 2;
                }
                else
                {
                    // 2 バイト目が未定義・ない: 先頭のバイトだけを不正にして、次のバイトからやり直す (仕様 5)。
                    ctx.PutInvalid(at);
                    at++;
                }

                continue;
            }

            if (table.SingleCodePoint(b) is int cp1 and >= 0)
            {
                ctx.PutCodePoint(at, 1, cp1);
            }
            else
            {
                ctx.PutInvalid(at);
            }

            at++;
        }
    }

    /// <summary>2 バイトの文字コードの表 (先頭のバイト、2 バイト目になりうるバイト、2 バイトの文字)。文字コードごとに 1 回だけ作る。</summary>
    private sealed class DbcsTable
    {
        private static readonly Dictionary<int, DbcsTable> Cache = [];

        private readonly Encoding _encoding;
        private readonly int[] _single = new int[256];
        private readonly int[] _pairs = new int[65536];

        private DbcsTable(TextEncoding encoding)
        {
            // 置き換えの文字で不正を知る (例外を使わない)。
            _encoding = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));
            char[] chars = new char[8];
            for (int b = 0; b < 256; b++)
            {
                _single[b] = DecodeOne([(byte)b], chars);
            }

            for (int lead = 0x80; lead < 256; lead++)
            {
                for (int trail = 0; trail < 256; trail++)
                {
                    int cp = DecodeOne([(byte)lead, (byte)trail], chars);
                    _pairs[(lead << 8) | trail] = cp;
                    if (cp >= 0)
                    {
                        Lead[lead] = true;
                        CanBeTrail[trail] = true;
                    }
                }
            }

            if (encoding.Kind == TextEncodingKind.Gb18030)
            {
                // 4 バイトの文字の 2〜4 バイト目。
                for (int b = 0x30; b <= 0x39; b++)
                {
                    CanBeTrail[b] = true;
                }

                for (int b = 0x81; b <= 0xFE; b++)
                {
                    CanBeTrail[b] = true;
                }
            }
        }

        public bool[] Lead { get; } = new bool[256];

        public bool[] CanBeTrail { get; } = new bool[256];

        public static DbcsTable For(TextEncoding encoding)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(encoding.CodePage, out DbcsTable? table))
                {
                    table = new DbcsTable(encoding);
                    Cache[encoding.CodePage] = table;
                }

                return table;
            }
        }

        public int SingleCodePoint(byte b) => _single[b];

        public int PairCodePoint(byte lead, byte trail) => _pairs[(lead << 8) | trail];

        public bool TryDecode(byte[] bytes, out int codePoint)
        {
            codePoint = DecodeOne(bytes, new char[8]);
            return codePoint >= 0;
        }

        /// <summary>バイト列がちょうど 1 文字になればその符号位置、ならなければ -1。</summary>
        private int DecodeOne(byte[] bytes, char[] chars)
        {
            int n = _encoding.GetChars(bytes, 0, bytes.Length, chars, 0);
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

    // ---- 文字の分類 (仕様 2・6・7) ----

    private static readonly string[] Ascii = [.. Enumerable.Range(0, 128).Select(i => ((char)i).ToString())];

    private static string CharString(char c) => c < 128 ? Ascii[c] : c.ToString();

    /// <summary>符号位置の描き方。</summary>
    public static (TextCellKind Kind, string Text, bool Wide) Classify(int codePoint)
    {
        if (codePoint < 0x20 || codePoint is >= 0x7F and <= 0x9F || IsInvisible(codePoint))
        {
            return (TextCellKind.NonPrintable, ".", false);
        }

        if (codePoint < 0x80)
        {
            return (TextCellKind.Char, Ascii[codePoint], false);
        }

        string text = char.ConvertFromUtf32(codePoint);
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
        {
            // 結合文字は前の文字と合成せず、点線の円に付けて単独で描く (仕様 6)。
            return (TextCellKind.Char, DottedCircle + text, false);
        }

        if (category is UnicodeCategory.Control or UnicodeCategory.Surrogate)
        {
            return (TextCellKind.NonPrintable, ".", false);
        }

        return (TextCellKind.Char, text, IsWide(codePoint));
    }

    /// <summary>ゼロ幅の文字・双方向制御文字・異体字セレクタ (仕様 6・7。表示しない文字の記号で描く)。</summary>
    public static bool IsInvisible(int cp) =>
        cp is >= 0x200B and <= 0x200F or >= 0x2060 and <= 0x2064 or 0xFEFF or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069
            or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;

    /// <summary>東アジアの文字幅が W (広い) または F (全角) か (Unicode の EastAsianWidth.txt の主な範囲)。</summary>
    public static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F
            or >= 0x231A and <= 0x231B
            or >= 0x2329 and <= 0x232A
            or >= 0x23E9 and <= 0x23EC
            or 0x23F0 or 0x23F3
            or >= 0x25FD and <= 0x25FE
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or 0x267F or 0x2693 or 0x26A1 or >= 0x26AA and <= 0x26AB or >= 0x26BD and <= 0x26BE or >= 0x26C4 and <= 0x26C5
            or 0x26CE or 0x26D4 or 0x26EA or >= 0x26F2 and <= 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705
            or >= 0x270A and <= 0x270B or 0x2728 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or 0x2757
            or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF or >= 0x2B1B and <= 0x2B1C or 0x2B50 or 0x2B55
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x16FE0 and <= 0x18CFF
            or >= 0x1B000 and <= 0x1B2FF
            or 0x1F004 or 0x1F0CF or 0x1F18E or >= 0x1F191 and <= 0x1F19A
            or >= 0x1F200 and <= 0x1F251
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F7E0 and <= 0x1F7EB
            or >= 0x1F90C and <= 0x1F9FF
            or >= 0x1FA70 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;
}
