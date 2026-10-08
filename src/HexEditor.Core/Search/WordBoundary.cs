using System.Globalization;
using System.Text;

namespace HexEditor.Core.Search;

/// <summary>
/// 単語単位の検索の境界の判定 (FIND-10 の仕様 4・5)。一致の直前と直後の文字を、選んだ文字コードで前後のバイトを復号して求め、
/// どちらも単語を作る文字 (Unicode の文字 L、数字 Nd、結合文字 M、`_`) でなければ一致とする。検索範囲の端と、
/// 復号できないバイトは単語の境界とみなす。CJK の文字は文字 (L) なので単語を作る文字として扱う。
/// </summary>
public sealed class WordBoundary
{
    /// <summary>1 文字の最大のバイト数 (UTF-8・UTF-16 のサロゲート・UTF-32・GB18030 の 4 バイト)。</summary>
    public const int MaxCharBytes = 4;

    private readonly Encoding _decoder;
    private readonly int _unit;

    public WordBoundary(Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        _decoder = strict;
        _unit = SearchPattern.CharacterUnit(encoding);
    }

    /// <summary>単語を作る文字か。</summary>
    public static bool IsWordRune(Rune rune)
    {
        if (rune.Value == '_')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
            UnicodeCategory.DecimalDigitNumber => true,
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => true,
            _ => false,
        };
    }

    /// <summary>
    /// 一致を単語として受け入れるか。<paramref name="before"/> は一致の直前の最大 <see cref="MaxCharBytes"/> バイト
    /// (範囲の先頭で切ったもの)、<paramref name="after"/> は直後の最大 <see cref="MaxCharBytes"/> バイト。
    /// </summary>
    public bool Accept(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after) =>
        !(LastRune(before) is { } b && IsWordRune(b)) && !(FirstRune(after) is { } a && IsWordRune(a));

    /// <summary>バイト列の末尾の 1 文字。末尾から、ちょうど 1 文字に復号できる最も長い部分を選ぶ (2 バイト文字の後半を誤読しない)。</summary>
    private Rune? LastRune(ReadOnlySpan<byte> bytes)
    {
        Rune? result = null;
        for (int len = _unit; len <= bytes.Length; len += _unit)
        {
            if (Single(bytes[^len..]) is { } r)
            {
                result = r;
            }
        }

        return result;
    }

    /// <summary>バイト列の先頭の 1 文字。先頭から、ちょうど 1 文字に復号できる最も短い部分を選ぶ。</summary>
    private Rune? FirstRune(ReadOnlySpan<byte> bytes)
    {
        for (int len = _unit; len <= bytes.Length; len += _unit)
        {
            if (Single(bytes[..len]) is { } r)
            {
                return r;
            }
        }

        return null;
    }

    /// <summary>ちょうど 1 文字に復号できればその文字。</summary>
    private Rune? Single(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = _decoder.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (text.Length == 0 || Rune.DecodeFromUtf16(text, out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
        {
            return null;
        }

        return consumed == text.Length ? rune : null;
    }
}
