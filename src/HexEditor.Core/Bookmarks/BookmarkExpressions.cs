using System.Diagnostics;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Bookmarks;

/// <summary>位置の式のエラーの種類 (INSP-29 の「エラー」)。</summary>
public enum BookmarkExpressionErrorKind
{
    /// <summary>入力式の誤り (構文、知らない名前、範囲外の読み込みなど。<see cref="BookmarkExpressionError.Detail"/> に詳細)。</summary>
    Expression,

    /// <summary>循環する参照 (仕様 7)。</summary>
    Cycle,

    /// <summary><c>bm.名前</c> の参照が 32 段を超えた (仕様 7)。</summary>
    TooDeep,

    /// <summary>評価した位置がドキュメントの範囲外。</summary>
    OutOfRange,
}

/// <summary>位置の式のエラー。位置は最後に評価できた値のままにする。</summary>
public sealed record BookmarkExpressionError(BookmarkExpressionErrorKind Kind, ExpressionException? Detail = null, long Value = 0);

/// <summary>
/// ブックマークの位置の式 (INSP-29 の仕様 5〜7) を評価し直して位置を更新する。式の中の <c>bm.名前</c> が位置の式を持つブックマークなら、
/// そのブックマークの式を先に評価する (32 段まで。循環はエラー)。<c>align(x, n)</c> を使える。UI スレッドで呼ぶ。
/// </summary>
public static class BookmarkExpressions
{
    /// <summary><c>bm.名前</c> の参照の深さの上限 (仕様 7)。</summary>
    public const int MaxDepth = 32;

    /// <summary>1 回の再評価の時間の目安 (「巨大ファイル・長時間処理」: 超えたら残りを次の再評価に回す)。</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    /// <summary>位置の式を持つブックマークがあるか。</summary>
    public static bool HasExpressions(BookmarkCollection bookmarks) => bookmarks.ExpressionCount > 0;

    /// <summary>
    /// 位置の式を持つすべてのブックマークを評価し直す。<paramref name="context"/> はドキュメントの値を読む関数と名前 (<c>cur</c> など) を
    /// 与える (<c>bm.名前</c> はここで解決する)。時間の目安を超えたら残りを評価せずに true を返す (次の再評価に回す)。
    /// </summary>
    public static bool Reevaluate(BookmarkCollection bookmarks, IExpressionContext context, long documentLength, TimeSpan? budget = null)
    {
        if (bookmarks.ExpressionCount == 0)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        TimeSpan limit = budget ?? Budget;
        var session = new Session(bookmarks, context, documentLength);
        var changed = new List<Bookmark>();
        bool remaining = false;
        foreach (Bookmark b in bookmarks.WithExpressions().OrderBy(b => b.Id))
        {
            if (stopwatch.Elapsed > limit)
            {
                remaining = true;
                break;
            }

            if (session.Apply(b))
            {
                changed.Add(b);
            }
        }

        bookmarks.RaiseExpressionResults(changed);
        return remaining;
    }

    /// <summary>1 件の式を評価する (その場の確認。編集のフライアウトの「解釈結果」)。エラーなら null と理由。</summary>
    public static long? Evaluate(BookmarkCollection bookmarks, Bookmark bookmark, string expression, IExpressionContext context, long documentLength,
        out BookmarkExpressionError? error)
    {
        var session = new Session(bookmarks, context, documentLength);
        return session.TryEvaluate(bookmark, expression, out error);
    }

    /// <summary>1 回の再評価の状態 (評価中のブックマークの並びと、評価済みの値)。</summary>
    private sealed class Session(BookmarkCollection bookmarks, IExpressionContext context, long documentLength)
    {
        private readonly List<Bookmark> _stack = [];
        /// <summary>評価済みの位置と、参照の段数 (位置の式のないブックマークは 0、式は参照先の最大 + 1)。</summary>
        private readonly Dictionary<Bookmark, (long Value, int Depth)> _done = [];
        private readonly Dictionary<Bookmark, BookmarkExpressionError> _failed = [];

        /// <summary>評価して位置とエラーを更新する。位置かエラーが変わったら true。</summary>
        public bool Apply(Bookmark b)
        {
            BookmarkExpressionError? before = b.ExpressionError;
            long start = b.Start;
            long? value;
            BookmarkExpressionError? error;
            try
            {
                value = Resolve(b, out error, out _);
            }
            catch (CycleException)
            {
                // 輪に入っているか、輪に入ったブックマークを参照している。
                error = _failed.GetValueOrDefault(b) ?? new BookmarkExpressionError(BookmarkExpressionErrorKind.Cycle);
                _failed[b] = error;
                value = null;
            }

            if (value is { } v && error is null)
            {
                b.ExpressionError = null;
                if (v != start)
                {
                    bookmarks.MoveForExpression(b, v, Math.Min(b.Length, documentLength - v));
                }

                return v != start || before is not null;
            }

            b.ExpressionError = error;
            return before?.Kind != error?.Kind || before?.Detail?.Error != error?.Detail?.Error;
        }

        public long? TryEvaluate(Bookmark b, string expression, out BookmarkExpressionError? error)
        {
            _stack.Add(b);
            try
            {
                long? value = Compute(expression, out error, out int depth);
                if (value is not null && depth > MaxDepth)
                {
                    error = new BookmarkExpressionError(BookmarkExpressionErrorKind.TooDeep);
                    return null;
                }

                return value;
            }
            catch (CycleException)
            {
                error = new BookmarkExpressionError(BookmarkExpressionErrorKind.Cycle);
                return null;
            }
            finally
            {
                _stack.Clear();
            }
        }

        /// <summary>ブックマークの位置 (位置の式があれば評価した値)。エラーなら null。循環は <see cref="CycleException"/>。</summary>
        private long? Resolve(Bookmark b, out BookmarkExpressionError? error, out int depth)
        {
            error = null;
            depth = 0;
            if (b.PositionExpression is not { } expression)
            {
                return b.Start;
            }

            if (_done.TryGetValue(b, out (long Value, int Depth) done))
            {
                depth = done.Depth;
                return done.Value;
            }

            if (_failed.TryGetValue(b, out BookmarkExpressionError? failed))
            {
                error = failed;
                return null;
            }

            int at = _stack.IndexOf(b);
            if (at >= 0)
            {
                // 循環する参照: 輪に入っているすべてのブックマークをエラーにする (仕様 7、TC-INSP-29-04)。
                var cycle = new BookmarkExpressionError(BookmarkExpressionErrorKind.Cycle);
                foreach (Bookmark member in _stack.Skip(at))
                {
                    _failed[member] = cycle;
                }

                throw new CycleException();
            }

            if (_stack.Count > MaxDepth)
            {
                error = new BookmarkExpressionError(BookmarkExpressionErrorKind.TooDeep);
                _failed[b] = error;
                return null;
            }

            _stack.Add(b);
            try
            {
                long? value = Compute(expression, out error, out depth);
                if (value is not null && depth > MaxDepth)
                {
                    // bm.名前 の参照は 32 段まで (仕様 7)。
                    error = new BookmarkExpressionError(BookmarkExpressionErrorKind.TooDeep);
                    value = null;
                }

                if (value is { } v)
                {
                    _done[b] = (v, depth);
                }
                else
                {
                    _failed[b] = error!;
                }

                return value;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        private long? Compute(string expression, out BookmarkExpressionError? error, out int depth)
        {
            var ctx = new Context(this, context);
            depth = 1;
            try
            {
                return ComputeCore(ctx, expression, out error);
            }
            finally
            {
                depth = ctx.ReferenceDepth + 1;
            }
        }

        private long? ComputeCore(Context ctx, string expression, out BookmarkExpressionError? error)
        {
            if (!ExpressionEvaluator.TryEvaluate(expression, ctx, out long value, out ExpressionException? ex))
            {
                if (ctx.Error is { } inner)
                {
                    error = inner;
                    return null;
                }

                error = new BookmarkExpressionError(BookmarkExpressionErrorKind.Expression, ex);
                return null;
            }

            if (ctx.Error is { } nested)
            {
                error = nested;
                return null;
            }

            if (value < 0 || value > documentLength)
            {
                error = new BookmarkExpressionError(BookmarkExpressionErrorKind.OutOfRange, null, value);
                return null;
            }

            error = null;
            return value;
        }

        /// <summary><c>bm.名前</c> の解決 (入力式の評価器から呼ばれる)。参照先のエラーはそのまま伝える。</summary>
        public long? Bookmark(string name, Context ctx)
        {
            if (bookmarks.FindByName(name) is not { } target)
            {
                return null;
            }

            long? value = Resolve(target, out BookmarkExpressionError? error, out int depth);
            ctx.ReferenceDepth = Math.Max(ctx.ReferenceDepth, depth);
            if (error is not null)
            {
                ctx.Error ??= error;
                return 0;
            }

            return value;
        }
    }

    /// <summary>循環を見つけた (評価をさかのぼって中断する)。</summary>
    private sealed class CycleException() : Exception("循環する参照");

    /// <summary>入力式の評価器に渡す名前と関数 (読み取りは元のもの、<c>bm.名前</c> は位置の式を評価する)。</summary>
    private sealed class Context(Session session, IExpressionContext inner) : IExpressionContext
    {
        /// <summary>参照先のブックマークのエラー (式自体は評価できても、この式もエラーにする)。</summary>
        public BookmarkExpressionError? Error { get; set; }

        /// <summary>参照したブックマークの段数の最大。</summary>
        public int ReferenceDepth { get; set; }

        public long Cursor => inner.Cursor;

        public long Length => inner.Length;

        public long SelectionStart => inner.SelectionStart;

        public long SelectionLength => inner.SelectionLength;

        public int SectorSize => inner.SectorSize;

        public long? ClusterSize => inner.ClusterSize;

        public long? RecordLength => inner.RecordLength;

        public long RecordStart => inner.RecordStart;

        public bool SupportsAlign => true;

        public long? Bookmark(string name) => session.Bookmark(name, this);

        public bool TryRead(long offset, Span<byte> destination) => inner.TryRead(offset, destination);
    }
}
