namespace HexEditor.Core.Expressions;

/// <summary>
/// コマンドラインの範囲の指定 (AUTO-36 の仕様 4): <c>開始:長さ</c> または <c>開始..終了</c> (終了は含まない)。<c>開始</c> だけなら末尾まで。
/// それぞれ入力式 (00 の 6 章) で、<c>end</c> はデータの長さ。<c>hexed</c> のオプション (<c>--range</c>、<c>--select</c> など) と、
/// <c>HexEditor.exe</c> が受け取る同じオプションが共通で使う。
/// </summary>
public static class RangeExpression
{
    /// <summary>
    /// 範囲を評価する。式の誤りは <paramref name="error"/> に入れて false。範囲の検査 (ファイルの外か) は呼び出し側で行う
    /// (<see cref="IsInside"/>)。
    /// </summary>
    public static bool TryEvaluate(string text, IExpressionContext context, DefaultRadix radix, out long start, out long length, out ExpressionException? error)
    {
        start = length = 0;
        int dots = text.IndexOf("..", StringComparison.Ordinal);
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string first;
        string? second = null;
        bool isEnd = false;
        if (dots >= 0)
        {
            first = text[..dots];
            second = text[(dots + 2)..];
            isEnd = true;
        }
        else if (colon >= 0)
        {
            first = text[..colon];
            second = text[(colon + 1)..];
        }
        else
        {
            first = text;
        }

        if (!ExpressionEvaluator.TryEvaluate(first, context, out long a, out error, radix))
        {
            return false;
        }

        long b;
        if (second is null)
        {
            b = context.Length;
            isEnd = true;
        }
        else if (!ExpressionEvaluator.TryEvaluate(second, context, out b, out error, radix))
        {
            return false;
        }

        start = a;
        length = isEnd ? b - a : b;
        return true;
    }

    /// <summary>範囲がデータの中に収まっているか。</summary>
    public static bool IsInside(long start, long length, long dataLength) =>
        start >= 0 && length >= 0 && start <= dataLength && length <= dataLength - start;
}
