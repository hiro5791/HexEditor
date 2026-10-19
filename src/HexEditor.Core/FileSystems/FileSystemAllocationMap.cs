using HexEditor.Core.Search;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ファイルシステムの割り当ての状況を、カービング (FOR-18) の対象範囲「未割り当て領域のみ」「スラックのみ」(<see cref="Forensics.Carving.IAllocationMap"/>) にする。
/// 範囲はドキュメント上の位置 (FOR-15・FOR-16 の <see cref="FileSystemExtraction"/> と同じ区間)。初めて読んだときに作る。
/// </summary>
public sealed class FileSystemAllocationMap : Forensics.Carving.IAllocationMap
{
    private readonly IFileSystem _fs;
    private readonly long _volumeOffset;
    private readonly Func<FileTailTable> _tails;
    private IReadOnlyList<SearchRange>? _unallocated;
    private IReadOnlyList<SearchRange>? _slack;

    public FileSystemAllocationMap(IFileSystem fs, long volumeOffset, Func<FileTailTable>? tails = null)
    {
        _fs = fs;
        _volumeOffset = volumeOffset;
        _tails = tails ?? (() => FileTailTable.Build(fs));
    }

    /// <summary>ドキュメントに付いているファイルシステム (FileSystemSession) の割り当ての状況。割り当ての情報がなければ null。</summary>
    public static FileSystemAllocationMap? For(Engine.Document document) =>
        FileSystemSession.For(document) is { FileSystem.Allocation: not null } s
            ? new FileSystemAllocationMap(s.FileSystem, s.VolumeOffset, () => s.Classifier.Tails ??= FileTailTable.Build(s.FileSystem))
            : null;

    public IReadOnlyList<SearchRange> Unallocated =>
        _unallocated ??= [.. FileSystemExtraction.UnallocatedRuns(_fs, _volumeOffset).Select(r => new SearchRange(r.DiskOffset, r.Length))];

    public IReadOnlyList<SearchRange> Slack =>
        _slack ??= [.. FileSystemExtraction.SlackRuns(_fs, _volumeOffset, _tails()).Where(r => r.Length > 0).Select(r => new SearchRange(r.DiskOffset, r.Length))];
}
