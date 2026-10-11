using HexEditor.Core.Sources;

namespace HexEditor.Core.View;

/// <summary>メモリ領域の移動 (ENG-33 の仕様 5)。領域マップを持つデータソース (プロセスメモリ) で使う。</summary>
public sealed partial class EditorState
{
    /// <summary>領域マップ (<see cref="IRegionMapSource"/>) を持つデータソースか。</summary>
    public IRegionMapSource? RegionSource => Document.Source as IRegionMapSource;

    /// <summary>
    /// [offset, offset + length) に割り当てられていない範囲 (空き・予約) が含まれる (ENG-34 の仕様 7)。領域の一覧を持たないデータソースでは偽。
    /// </summary>
    public bool IsUnallocated(long offset, long length)
    {
        if (RegionSource is not { } source)
        {
            return false;
        }

        long end = Math.Min(offset + Math.Max(length, 1), Document.Length);
        for (long at = offset; at < end;)
        {
            Sources.SourceRegion? region = Sources.RegionMaps.At(source.Regions, at);
            if (region is null || region.Access == Sources.RegionAccess.Unallocated)
            {
                return true;
            }

            at = region.End;
        }

        return false;
    }

    /// <summary>次の読める領域の先頭へ移動する (読めない範囲を飛ばす。仕様 5)。移動したら true。</summary>
    public bool MoveNextRegion()
    {
        if (RegionSource is not { } source)
        {
            return false;
        }

        return MoveToRegion(RegionMaps.NextReadable(source.Regions, Cursor));
    }

    /// <summary>前の読める領域の先頭へ移動する。移動したら true。</summary>
    public bool MovePreviousRegion()
    {
        if (RegionSource is not { } source)
        {
            return false;
        }

        return MoveToRegion(RegionMaps.PreviousReadable(source.Regions, Cursor));
    }

    private bool MoveToRegion(long? target)
    {
        if (target is not { } offset)
        {
            return false;
        }

        RecordJump();
        ClearSelectionAnchor();
        MoveTo(offset, extend: false, scroll: false);
        SetTopRow(Layout.RowOf(offset));
        RaiseChanged();
        return true;
    }
}
