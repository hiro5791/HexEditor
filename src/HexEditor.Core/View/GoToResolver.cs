using HexEditor.Core.Expressions;

namespace HexEditor.Core.View;

/// <summary>移動バーの基準 (VIEW-29 の仕様 3)。</summary>
public enum GoToBase
{
    /// <summary>先頭が + または - ならカーソルから、それ以外は先頭から。</summary>
    Auto,
    FromStart,
    FromCursor,
    FromEnd,
}

/// <summary>移動バーの単位 (VIEW-29 の仕様 4)。</summary>
public enum GoToUnit
{
    Bytes,
    Rows,
    Sectors,
}

/// <summary>移動先を求めた結果。<see cref="Error"/> と <see cref="OutOfRange"/> のどちらも無ければ移動できる。</summary>
public readonly record struct GoToResult(long Offset, ExpressionException? Error, bool OutOfRange)
{
    public bool IsValid => Error is null && !OutOfRange;
}

/// <summary>移動バーの入力から移動先のオフセットを求める (VIEW-29)。</summary>
public static class GoToResolver
{
    /// <param name="byAddress">
    /// 「アドレスで指定」(VIEW-20 の仕様 4、VIEW-29 の仕様 5): 先頭からの値をアドレスとして、ベースアドレスを引いてオフセットにする。
    /// カーソルから・末尾からの相対の移動では使わない。
    /// </param>
    public static GoToResult Resolve(string text, GoToBase goToBase, GoToUnit unit, EditorState editor,
        DefaultRadix radix = DefaultRadix.Hexadecimal, bool byAddress = false)
    {
        var context = new EditorExpressionContext(editor);
        string trimmed = text.Trim();
        bool relative = goToBase == GoToBase.FromCursor
            || (goToBase == GoToBase.Auto && trimmed.Length > 0 && trimmed[0] is '+' or '-');

        if (!ExpressionEvaluator.TryEvaluate(trimmed, context, out long value, out ExpressionException? error, radix))
        {
            return new GoToResult(0, error, false);
        }

        long scale = unit switch
        {
            GoToUnit.Rows => editor.BytesPerRow,
            GoToUnit.Sectors => context.SectorSize,
            _ => 1,
        };

        try
        {
            long amount = checked(value * scale);
            ulong baseAddress = editor.View.BaseAddress;
            if (byAddress && baseAddress != 0 && goToBase != GoToBase.FromEnd && !relative)
            {
                // アドレスがベースアドレスより前なら、ファイルの先頭より前 (範囲外)。
                ulong address = unchecked((ulong)amount);
                return address < baseAddress
                    ? new GoToResult(-1, null, true)
                    : new GoToResult((long)Math.Min(address - baseAddress, long.MaxValue), null,
                        address - baseAddress > (ulong)editor.Layout.MaxCursor);
            }

            long offset = goToBase == GoToBase.FromEnd ? checked(editor.Document.Length - amount)
                : relative ? checked(editor.Cursor + amount)
                : amount;
            bool outOfRange = offset < 0 || offset > editor.Layout.MaxCursor;
            return new GoToResult(offset, null, outOfRange);
        }
        catch (OverflowException)
        {
            return new GoToResult(0, new ExpressionException(ExpressionError.Overflow, 0), false);
        }
    }
}

/// <summary>ビューの状態を入力式の名前に結び付ける。</summary>
public sealed class EditorExpressionContext(EditorState editor) : IExpressionContext
{
    public long Cursor => editor.Cursor;

    public long Length => editor.Document.Length;

    public long SelectionStart => editor.SelectionStart;

    public long SelectionLength => editor.SelectionLength;

    /// <summary>ディスクは論理セクタサイズ、ファイルは表示設定の値 (既定 512)。</summary>
    public int SectorSize => editor.SectorSize;

    public long? ClusterSize => null;

    /// <summary>レコード長 (VIEW-18 の仕様 8。レコード表示がオフでも最後に設定した値を使う)。</summary>
    public long? RecordLength => editor.View.RecordLength;

    public long RecordStart => editor.View.RecordStart;

    /// <summary>名前付きブックマーク (<c>bm.名前</c>。INSP-24 の仕様 2) の開始位置。</summary>
    public long? Bookmark(string name) => Bookmarks.BookmarkCollection.For(editor.Document)?.FindByName(name)?.Start;

    public bool TryRead(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > editor.Document.Length - destination.Length)
        {
            return false;
        }

        return editor.Document.Current.Read(offset, destination).IsComplete;
    }
}
