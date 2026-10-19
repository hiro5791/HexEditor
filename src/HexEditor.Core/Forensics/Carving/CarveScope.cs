using HexEditor.Core.Search;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>カービングの対象範囲 (12 の FOR-19 の仕様 1)。</summary>
public enum CarveScopeKind
{
    /// <summary>ドキュメント全体 (既定)。</summary>
    WholeDocument,

    /// <summary>選択範囲。</summary>
    Selection,

    /// <summary>未割り当て領域のみ (ファイルシステムを解析している場合)。</summary>
    Unallocated,

    /// <summary>スラックのみ (ファイルシステムを解析している場合)。</summary>
    Slack,
}

/// <summary>
/// ファイルシステムの解析 (FOR-15、FOR-16) が提供する、ドキュメント上の割り当ての状況。ファイルシステムを解析していないドキュメントでは提供されない
/// (カービングのダイアログは「未割り当て領域のみ」「スラックのみ」を無効にする)。各範囲は開始の順で、重ならない。
/// </summary>
public interface IAllocationMap
{
    /// <summary>どのファイルにも使われていない範囲 (空きクラスタ)。</summary>
    IReadOnlyList<SearchRange> Unallocated { get; }

    /// <summary>ファイルの終わりとクラスタの終わりの間の範囲 (スラック)。</summary>
    IReadOnlyList<SearchRange> Slack { get; }
}

/// <summary>対象範囲を、走査する範囲の一覧にする。</summary>
public static class CarveScope
{
    public static IReadOnlyList<SearchRange> Resolve(CarveScopeKind kind, long documentLength, IReadOnlyList<SearchRange> selection, IAllocationMap? map)
    {
        switch (kind)
        {
            case CarveScopeKind.WholeDocument:
                return documentLength > 0 ? [new SearchRange(0, documentLength)] : [];
            case CarveScopeKind.Selection:
                return SearchScope.Of(selection).Resolve(documentLength);
            case CarveScopeKind.Unallocated:
                return Clip(map?.Unallocated ?? throw new InvalidOperationException("No allocation information is available."), documentLength);
            default:
                return Clip(map?.Slack ?? throw new InvalidOperationException("No allocation information is available."), documentLength);
        }
    }

    private static IReadOnlyList<SearchRange> Clip(IReadOnlyList<SearchRange> ranges, long length) => SearchScope.Of(ranges).Resolve(length);
}
