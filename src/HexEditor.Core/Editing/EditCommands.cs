using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.Core.Editing;

/// <summary>バイトの挿入・塗りつぶし・ファイルサイズの変更の入力の誤り。</summary>
public enum RangeEditError
{
    /// <summary>バイト数が 0 以下。</summary>
    CountTooSmall,

    /// <summary>挿入するとドキュメントの長さが 2^63 − 1 を超える (EDIT-14 の仕様 1)。</summary>
    CountTooLarge,

    /// <summary>位置がドキュメントの範囲外。</summary>
    PositionOutOfRange,

    /// <summary>長さを変えられないドキュメントで長さを変えようとした。</summary>
    FixedLength,

    /// <summary>新しい長さが負。</summary>
    NegativeLength,

    /// <summary>読み取り専用。</summary>
    ReadOnly,
}

/// <summary>
/// 内容を作る編集のコマンド (EDIT-14 バイトの挿入、EDIT-15 ファイルサイズの変更と切り詰め、EDIT-29 塗りつぶし、EDIT-30 ファイルの内容の挿入)。
/// 内容 (<see cref="EditContent"/>) は <see cref="ContentBuilder"/> で作ってから渡す。どのコマンドも 1 つの編集グループになる。
/// </summary>
public static class EditCommands
{
    /// <summary>挿入できる最大のバイト数 (2^63 − 1 − 現在の長さ。EDIT-14 の仕様 1)。</summary>
    public static long MaxInsertCount(Document document) => long.MaxValue - document.Length;

    /// <summary>バイトの挿入の入力を確かめる (EDIT-14)。正しければ null。</summary>
    public static RangeEditError? ValidateInsert(Document document, long position, long count)
    {
        if (document.IsReadOnly)
        {
            return RangeEditError.ReadOnly;
        }

        if (!document.CanResize)
        {
            return RangeEditError.FixedLength;
        }

        if (position < 0 || position > document.Length)
        {
            return RangeEditError.PositionOutOfRange;
        }

        if (count <= 0)
        {
            return RangeEditError.CountTooSmall;
        }

        return count > MaxInsertCount(document) ? RangeEditError.CountTooLarge : null;
    }

    /// <summary>
    /// <paramref name="position"/> に内容を挿入する (EDIT-14・EDIT-30)。<paramref name="selectInserted"/> なら挿入した範囲を選択する
    /// (EDIT-14 の仕様 3、EDIT-30 の仕様 3)。そうでなければカーソルを挿入した範囲の直後に置く。
    /// </summary>
    public static void Insert(EditorState editor, long position, EditContent content, bool selectInserted = true, string description = "挿入")
    {
        Document document = editor.Document;
        if (ValidateInsert(document, position, content.Length) is { } error)
        {
            content.Dispose();
            throw new RangeEditException(error);
        }

        long length = content.Length;
        document.InsertContent(position, content, description);
        Place(editor, position, length, selectInserted);
    }

    /// <summary>
    /// [<paramref name="start"/>, + 内容の長さ) を内容で上書きする (EDIT-29・EDIT-30 の上書き)。長さは変えない。ただし範囲が末尾を
    /// 越える場合、可変長のドキュメントでは末尾を延ばし、固定長のドキュメントでは <see cref="RangeEditError.FixedLength"/> (EDIT-29 の仕様 1)。
    /// 上書きした範囲を選択する。
    /// </summary>
    public static void Overwrite(EditorState editor, long start, EditContent content, string description = "塗りつぶし")
    {
        Document document = editor.Document;
        RangeEditError? error = document.IsReadOnly ? RangeEditError.ReadOnly
            : start < 0 || start > document.Length ? RangeEditError.PositionOutOfRange
            : !document.CanResize && content.Length > document.Length - start ? RangeEditError.FixedLength
            : null;
        if (error is not null)
        {
            content.Dispose();
            throw new RangeEditException(error.Value);
        }

        long length = content.Length;
        document.OverwriteContent(start, content, description);
        Place(editor, start, length, select: true);
    }

    /// <summary>ファイルサイズの変更の入力を確かめる (EDIT-15)。正しければ null。</summary>
    public static RangeEditError? ValidateResize(Document document, long newLength)
    {
        if (document.IsReadOnly)
        {
            return RangeEditError.ReadOnly;
        }

        if (!document.CanResize)
        {
            return RangeEditError.FixedLength;
        }

        return newLength < 0 ? RangeEditError.NegativeLength : null;
    }

    /// <summary>
    /// ドキュメントの長さを <paramref name="newLength"/> にする (EDIT-15)。短くする場合は末尾を切り捨て、長くする場合は末尾に
    /// <paramref name="extension"/> (長さ = 差分) を追加する。同じ長さなら何もしない (編集履歴に残さない。仕様 5)。
    /// </summary>
    /// <returns>切り捨てたバイト数 (長くした・変わらない場合は 0)。</returns>
    public static long Resize(EditorState editor, long newLength, EditContent? extension = null)
    {
        Document document = editor.Document;
        if (ValidateResize(document, newLength) is { } error)
        {
            extension?.Dispose();
            throw new RangeEditException(error);
        }

        long current = document.Length;
        if (newLength == current)
        {
            extension?.Dispose();
            return 0;
        }

        if (newLength < current)
        {
            extension?.Dispose();
            document.Delete(newLength, current - newLength, "ファイルサイズの変更");
            return current - newLength;
        }

        EditContent content = extension ?? EditContent.Fill(0, newLength - current);
        if (content.Length != newLength - current)
        {
            content.Dispose();
            throw new ArgumentException("追加する内容の長さが差分と違います。", nameof(extension));
        }

        document.InsertContent(current, content, "ファイルサイズの変更");
        return 0;
    }

    /// <summary>「カーソル位置で切り詰める」ができるか (カーソルが末尾位置にある場合はできない。EDIT-15 の仕様 3)。</summary>
    public static bool CanTruncateAtCursor(EditorState editor) =>
        !editor.Document.IsReadOnly && editor.Document.CanResize && editor.Cursor < editor.Document.Length;

    /// <summary>カーソル位置から末尾までを削除する (EDIT-15 の仕様 3)。切り捨てたバイト数を返す。</summary>
    public static long TruncateAtCursor(EditorState editor)
    {
        if (!CanTruncateAtCursor(editor))
        {
            throw new RangeEditException(editor.Document.IsReadOnly ? RangeEditError.ReadOnly
                : !editor.Document.CanResize ? RangeEditError.FixedLength : RangeEditError.PositionOutOfRange);
        }

        long removed = editor.Document.Length - editor.Cursor;
        editor.Document.Delete(editor.Cursor, removed, "カーソル位置で切り詰める");
        return removed;
    }

    private static void Place(EditorState editor, long start, long length, bool select)
    {
        if (select)
        {
            editor.Select(start, length);
        }
        else
        {
            editor.Select(Math.Min(start + length, editor.Document.Length), 0);
        }
    }
}

/// <summary>内容を作る編集の入力の誤り。</summary>
public sealed class RangeEditException(RangeEditError error) : InvalidOperationException($"編集できません: {error}")
{
    public RangeEditError Error { get; } = error;
}
