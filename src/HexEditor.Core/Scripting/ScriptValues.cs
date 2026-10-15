using HexEditor.Core.Clipboard;
using HexEditor.Core.Expressions;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting;

/// <summary>
/// 入力式の文字列の解釈 (AUTO-04 の仕様 5・6、00 の 6 章)。数値は入力式、範囲は <c>sel</c>・<c>all</c>・<c>開始:長さ</c>・
/// <c>開始..終了</c> (終了は含まない)、Hex は 00 の 6.3 の書式。
/// </summary>
public static class ScriptValues
{
    /// <summary>2^53 (JavaScript の number で正確に表せる整数の上限。AUTO-02 の仕様 7)。</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>数値の入力式を読む。接頭辞のない数値は <paramref name="radix"/> (ビューのオフセットの基数。00 の 6.1 の※) で読む。</summary>
    public static long EvaluateNumber(string text, IExpressionContext context, DefaultRadix radix = DefaultRadix.Decimal)
    {
        try
        {
            return ExpressionEvaluator.Evaluate(text, context, radix);
        }
        catch (ExpressionException ex)
        {
            throw new ArgumentError(Msg.T($"入力式を読めません: {text} ({ex.Error} {ex.Detail})", $"Cannot parse the input expression: {text} ({ex.Error} {ex.Detail})").TrimEnd(), ex);
        }
    }

    /// <summary>範囲の入力式を読む。</summary>
    public static SdkRange EvaluateRange(string text, IExpressionContext context, DefaultRadix radix = DefaultRadix.Decimal)
    {
        string t = text.Trim();
        if (t.Equals("sel", StringComparison.OrdinalIgnoreCase))
        {
            return new SdkRange(context.SelectionStart, context.SelectionLength);
        }

        if (t.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new SdkRange(0, context.Length);
        }

        int dots = TopLevelIndexOf(t, "..");
        if (dots > 0)
        {
            long start = EvaluateNumber(t[..dots], context, radix);
            long end = EvaluateNumber(t[(dots + 2)..], context, radix);
            if (end < start)
            {
                throw new ArgumentError(Msg.T($"範囲の終了が開始より前です: {text}", $"The end of the range is before its start: {text}"));
            }

            return new SdkRange(start, end - start);
        }

        int colon = TopLevelIndexOf(t, ":");
        if (colon > 0)
        {
            long start = EvaluateNumber(t[..colon], context, radix);
            long length = EvaluateNumber(t[(colon + 1)..], context, radix);
            if (length < 0)
            {
                throw new ArgumentError(Msg.T($"範囲の長さが負です: {text}", $"The length of the range is negative: {text}"));
            }

            return new SdkRange(start, length);
        }

        throw new ArgumentError(Msg.T($"範囲を読めません: {text} (例: \"sel\"、\"0x100:0x20\"、\"0x100..0x120\")", $"Cannot parse the range: {text} (e.g. \"sel\", \"0x100:0x20\", \"0x100..0x120\")"));
    }

    private static int TopLevelIndexOf(string text, string token)
    {
        int depth = 0;
        for (int i = 0; i <= text.Length - token.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 0 && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Hex バイト列 (00 の 6.3)。読めなければ <see cref="ArgumentError"/>。</summary>
    public static byte[] ParseHex(string text) =>
        HexText.TryParse(text) ?? throw new ArgumentError(Msg.T($"Hex のバイト列を読めません: {text} (例: \"DE AD BE EF\"。奇数桁は使えません)", $"Cannot parse the hex byte string: {text} (e.g. \"DE AD BE EF\"; an odd number of digits is not allowed)"));

    /// <summary>JavaScript の number に収まるか確かめる (AUTO-02 の仕様 7)。</summary>
    public static long CheckSafe(long value) =>
        value is > MaxSafeInteger or < -MaxSafeInteger ? throw new Sdk.RangeError(Msg.T($"2^53 を超える値は number で扱えません: {value}", $"Values beyond 2^53 cannot be represented as a number: {value}")) : value;
}

/// <summary>入力式の名前の値 (ドキュメントがないときに使う)。</summary>
internal sealed class EmptyExpressionContext : IExpressionContext
{
    public static readonly EmptyExpressionContext Instance = new();

    public long Cursor => 0;

    public long Length => 0;

    public long SelectionStart => 0;

    public long SelectionLength => 0;

    public int SectorSize => 512;

    public long? ClusterSize => null;

    public long? RecordLength => null;

    public long RecordStart => 0;

    public long? Bookmark(string name) => null;

    public bool TryRead(long offset, Span<byte> destination) => false;
}
