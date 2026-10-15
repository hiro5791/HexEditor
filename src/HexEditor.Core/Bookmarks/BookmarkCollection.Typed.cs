using HexEditor.Core.Inspector;

namespace HexEditor.Core.Bookmarks;

/// <summary>型付きブックマークと位置の式 (INSP-29)。</summary>
public sealed partial class BookmarkCollection
{
    /// <summary>位置の式を持つブックマーク (削除したものも残ることがある。使うときに一覧にあるものだけを選ぶ)。</summary>
    private readonly HashSet<Bookmark> _expressions = [];

    /// <summary>位置の式を持つブックマークの数。</summary>
    public int ExpressionCount
    {
        get
        {
            _expressions.RemoveWhere(b => b.Owner != this || b.PositionExpression is null);
            return _expressions.Count;
        }
    }

    /// <summary>位置の式を持つブックマーク (一覧にあるもの)。</summary>
    public IReadOnlyList<Bookmark> WithExpressions() => [.. _expressions.Where(b => b.Owner == this && b.InTree && b.PositionExpression is not null)];

    /// <summary>
    /// 型を付ける・外す (INSP-29 の仕様 1・2)。<paramref name="length"/> は型のサイズ (可変長の型は解釈した長さ)。型を外したときは長さを変えない。
    /// <paramref name="endian"/> が null ならドキュメントのエンディアン。
    /// </summary>
    public void SetType(Bookmark b, string? typeId, Endianness? endian, long? length = null)
    {
        typeId = string.IsNullOrWhiteSpace(typeId) ? null : typeId;
        bool sameLength = length is null || length == b.Length;
        if (b.TypeId == typeId && b.TypeEndian == endian && sameLength)
        {
            return;
        }

        b.TypeId = typeId;
        b.TypeEndian = endian;
        if (!sameLength && b.Owner == this && b.InTree)
        {
            long start = BookmarkTree.StartOf(b);
            _tree.Remove(b);
            b.Length = Math.Max(0, length!.Value);
            _tree.Insert(b, start);
        }

        Touch(b, customized: true);
    }

    /// <summary>
    /// 位置の式を設定する (空なら外して固定のオフセットに戻す。INSP-29 の仕様 5)。評価は呼び出し側が <see cref="BookmarkExpressions.Reevaluate"/>
    /// で行う。
    /// </summary>
    public void SetPositionExpression(Bookmark b, string? expression)
    {
        expression = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
        if (b.PositionExpression == expression)
        {
            return;
        }

        b.PositionExpression = expression;
        b.ExpressionError = null;
        if (expression is not null)
        {
            _expressions.Add(b);
        }

        Touch(b, customized: true);
    }

    /// <summary>読み込んだ型・位置の式を戻す (付随データの読み込み)。</summary>
    internal void RestoreTyped(Bookmark b, string? typeId, Endianness? endian, string? expression)
    {
        b.TypeId = string.IsNullOrWhiteSpace(typeId) ? null : typeId;
        b.TypeEndian = endian;
        b.PositionExpression = string.IsNullOrWhiteSpace(expression) ? null : expression;
        if (b.PositionExpression is not null)
        {
            _expressions.Add(b);
        }
    }

    /// <summary>位置の式で求めた位置に移す (編集の履歴・利用者の変更として扱わない)。</summary>
    internal void MoveForExpression(Bookmark b, long start, long length)
    {
        if (b.Owner != this || !b.InTree)
        {
            return;
        }

        _tree.Remove(b);
        b.Length = Math.Max(0, length);
        b.RangeDeleted = false;
        _tree.Insert(b, start);
    }

    private List<(Bookmark, long)> CaptureExpressionStarts() =>
        [.. _expressions.Where(b => b.Owner == this && b.InTree && b.PositionExpression is not null).Select(b => (b, BookmarkTree.StartOf(b)))];

    private void RestoreExpressionStarts(List<(Bookmark Bookmark, long Start)> starts, long documentLength)
    {
        foreach ((Bookmark b, long start) in starts)
        {
            if (b.InTree && BookmarkTree.StartOf(b) != start)
            {
                long at = Math.Clamp(start, 0, documentLength);
                MoveForExpression(b, at, Math.Min(b.Length, Math.Max(0, documentLength - at)));
            }
        }
    }

    /// <summary>位置の式の再評価で位置かエラーが変わったブックマークを知らせる (一覧と強調を更新する)。</summary>
    internal void RaiseExpressionResults(IReadOnlyList<Bookmark> changed)
    {
        if (changed.Count > 0)
        {
            RaiseChanged(BookmarkChangeKind.Positions, changed);
        }
    }
}
