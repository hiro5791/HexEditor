using HexEditor.Core.Engine;

namespace HexEditor.Core.Compare;

/// <summary>差分の種類 (ANA-08 の仕様 4)。</summary>
public enum DiffKind
{
    /// <summary>上書きした範囲 (左右で同じ長さ)。</summary>
    Changed,

    /// <summary>挿入した範囲 (右のみ。左の長さは 0)。</summary>
    Inserted,

    /// <summary>削除した範囲 (左のみ。右の長さは 0)。</summary>
    Deleted,
}

/// <summary>差分 1 件。左は保存済みの内容 (ディスク上)、右は編集中のドキュメント。</summary>
public readonly record struct DiffRange(DiffKind Kind, long LeftOffset, long LeftLength, long RightOffset, long RightLength);

/// <summary>
/// 保存済みの内容との比較 (ANA-08 の仕様 2・4)。ディスク上のファイルが開いた (または最後に保存した) 後に変更されていない場合に使う。
/// ピースツリーだけから差分を求め、ファイルは読まない。時間はピースの数に比例し、ファイルサイズに依存しない。
/// 外部で変更されている場合 (ENG-19) は、呼び出し側が挿入・削除を考慮した比較 (ANA-03) を使う。
/// </summary>
public static class SavedContentDiff
{
    /// <summary>
    /// 差分を先頭から求める。元データを指すピースのうち、元データ上の位置が単調に増えるものを一致 (アンカー) とし、
    /// アンカーの間を差分にする: 左右の両方に長さがあれば短い方の長さを「変更」、残りを「挿入」または「削除」にする。
    /// 元データを指すピースでも、前のアンカーより前の位置を指すもの (移動した内容) は挿入として扱う。
    /// </summary>
    /// <param name="snapshot">編集中のドキュメントの内容。</param>
    /// <param name="cancellationToken">ピースの数が多い場合に中止するため。</param>
    public static IReadOnlyList<DiffRange> Compute(DocumentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        long originalLength = snapshot.Storage.Source.Length;
        var result = new List<DiffRange>();
        long expectedOriginal = 0;
        long gapStart = 0;
        long gapLength = 0;
        int count = 0;
        foreach ((long docOffset, Piece piece) in snapshot.Tree.EnumerateAll())
        {
            if ((++count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (piece.Kind == PieceKind.Original && piece.Offset >= expectedOriginal && piece.Offset + piece.Length <= originalLength)
            {
                AddGap(result, expectedOriginal, piece.Offset - expectedOriginal, gapStart, gapLength);
                expectedOriginal = piece.Offset + piece.Length;
                gapStart = docOffset + piece.Length;
                gapLength = 0;
            }
            else
            {
                if (gapLength == 0)
                {
                    gapStart = docOffset;
                }

                gapLength += piece.Length;
            }
        }

        if (gapLength == 0)
        {
            gapStart = snapshot.Length;
        }

        AddGap(result, expectedOriginal, Math.Max(0, originalLength - expectedOriginal), gapStart, gapLength);
        return result;
    }

    private static void AddGap(List<DiffRange> result, long left, long leftLength, long right, long rightLength)
    {
        long common = Math.Min(leftLength, rightLength);
        if (common > 0)
        {
            result.Add(new DiffRange(DiffKind.Changed, left, common, right, common));
        }

        if (rightLength > common)
        {
            result.Add(new DiffRange(DiffKind.Inserted, left + common, 0, right + common, rightLength - common));
        }
        else if (leftLength > common)
        {
            result.Add(new DiffRange(DiffKind.Deleted, left + common, leftLength - common, right + common, 0));
        }
    }
}
