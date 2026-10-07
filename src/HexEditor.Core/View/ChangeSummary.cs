using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>
/// 未保存の変更の量 (ENG-17 の仕様 2、UI-13 の仕様 1 の「N か所、M バイト」)。元データ以外から来た範囲 (入力・貼り付け・
/// 生成) を「変更」として数え、隣り合う範囲は 1 か所にまとめる。削除した範囲は、元データの続きが途切れた位置を 1 か所と数える。
/// ピースの数に比例する時間で求まる (ファイルサイズに依存しない)。
/// </summary>
public readonly record struct ChangeSummary(long Places, long Bytes)
{
    public static ChangeSummary Of(DocumentSnapshot snapshot)
    {
        long places = 0;
        long bytes = 0;
        bool inChange = false;
        long expectedOriginal = 0;
        foreach ((long _, Piece piece) in snapshot.Tree.EnumerateAll())
        {
            if (piece.Kind == PieceKind.Original)
            {
                // 元データが飛んでいれば、その位置で削除 (または並べ替え) があった。
                if (piece.Offset != expectedOriginal && !inChange)
                {
                    places++;
                }

                expectedOriginal = piece.Offset + piece.Length;
                inChange = false;
            }
            else
            {
                if (!inChange)
                {
                    places++;
                }

                bytes += piece.Length;
                inChange = true;
            }
        }

        // 末尾が削られた場合。
        if (!inChange && expectedOriginal != snapshot.Storage.Source.Length)
        {
            places++;
        }

        return new ChangeSummary(places, bytes);
    }
}
