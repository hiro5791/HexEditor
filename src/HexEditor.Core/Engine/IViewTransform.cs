namespace HexEditor.Core.Engine;

/// <summary>
/// 変換ビュー (EDIT-45) の変換。データを変えずに、読み込んだバイトを表示上の値に変える (<see cref="Forward"/>)。表示上で入力した値は
/// <see cref="Inverse"/> で元のデータに戻してから書き込む (仕様 6)。どちらも位置 (ドキュメント上のオフセット) で結果が決まり、
/// 何度呼んでも同じ結果になること。
/// </summary>
public interface IViewTransform
{
    /// <summary>表示に使う短い説明 (例: <c>XOR 5A</c>。バッジ・ステータスバー・検索バーの「変換ビュー: …」の部分。仕様 9)。</summary>
    string Label { get; }

    /// <summary>逆変換を持つか。持たない変換ビューは読み取り専用になる (仕様 7)。</summary>
    bool CanInverse { get; }

    /// <summary>
    /// 要素の大きさ (1 / 2 / 4 / 8)。読み込みはこの大きさの境界 (<see cref="AlignmentOrigin"/> から数える) にそろえて <see cref="Forward"/> に渡す。
    /// </summary>
    int Alignment { get; }

    /// <summary>要素の境界の起点 (適用範囲の先頭)。</summary>
    long AlignmentOrigin { get; }

    /// <summary><paramref name="data"/> (<paramref name="offset"/> からの元のデータ) を、その場で表示上の値にする。</summary>
    void Forward(long offset, Span<byte> data);

    /// <summary><paramref name="data"/> (<paramref name="offset"/> からの表示上の値) を、その場で元のデータにする。</summary>
    void Inverse(long offset, Span<byte> data);
}

/// <summary>変換ビューでの長さを変える編集 (EDIT-45 の仕様 8: 「変換ビューでは長さを変える編集はできません」)。</summary>
public sealed class TransformViewLengthException() : InvalidOperationException("変換ビューでは長さを変える編集はできません。");

/// <summary>逆変換のない変換ビューでの編集 (EDIT-45 の仕様 7)。</summary>
public sealed class TransformViewReadOnlyException() : InvalidOperationException("この変換ビューは逆変換がないため編集できません。");
