namespace HexEditor.Core.View;

/// <summary>セクタ単位の移動 (VIEW-32)。</summary>
public sealed partial class EditorState
{
    /// <summary>セクタ番号 (0 始まり、切り捨て)。末尾の次の位置は最後のセクタに属する (仕様 2)。</summary>
    public static long SectorOf(long offset, long length, int sectorSize)
    {
        long count = SectorCount(length, sectorSize);
        return count == 0 ? 0 : Math.Min(offset / sectorSize, count - 1);
    }

    /// <summary>セクタ数 ⌈長さ ÷ セクタサイズ⌉ (末尾の端数も 1 つと数える)。</summary>
    public static long SectorCount(long length, int sectorSize) => length <= 0 ? 0 : ((length - 1) / sectorSize) + 1;

    /// <summary>カーソルのあるセクタ番号 (ステータスバー。仕様 6)。</summary>
    public long CursorSector => SectorOf(Cursor, Document.Length, SectorSize);

    /// <summary>「次のセクタ」の移動先 (仕様 2)。動かない場合は null。</summary>
    public static long? NextSectorTarget(long cursor, long length, int sectorSize)
    {
        long count = SectorCount(length, sectorSize);
        long sector = SectorOf(cursor, length, sectorSize);
        return count == 0 || sector >= count - 1 ? null : (sector + 1) * sectorSize;
    }

    /// <summary>「前のセクタ」の移動先 (仕様 3)。動かない場合は null。</summary>
    public static long? PreviousSectorTarget(long cursor, long length, int sectorSize)
    {
        if (length <= 0)
        {
            return null;
        }

        long sector = SectorOf(cursor, length, sectorSize);
        long start = sector * sectorSize;
        if (cursor > start)
        {
            return start;
        }

        return sector > 0 ? (sector - 1) * sectorSize : null;
    }

    /// <summary>次のセクタの先頭へ移動する。移動したら true。</summary>
    public bool MoveNextSector() => MoveToSector(NextSectorTarget(Cursor, Document.Length, SectorSize));

    /// <summary>前のセクタの先頭へ移動する。移動したら true。</summary>
    public bool MovePreviousSector() => MoveToSector(PreviousSectorTarget(Cursor, Document.Length, SectorSize));

    /// <summary>セクタ番号を指定して移動する (「セクタへ移動」)。</summary>
    public bool GoToSector(long sector)
    {
        long count = SectorCount(Document.Length, SectorSize);
        return sector >= 0 && sector < count && MoveToSector(sector * SectorSize);
    }

    private bool MoveToSector(long? target)
    {
        if (target is not { } offset)
        {
            return false;
        }

        // ジャンプ履歴に記録し (仕様 7)、セクタの先頭行を画面の一番上にする (仕様 5)。
        RecordJump();
        ClearSelectionAnchor();
        MoveTo(offset, extend: false, scroll: false);
        SetTopRow(Layout.RowOf(offset));
        RaiseChanged();
        return true;
    }
}
