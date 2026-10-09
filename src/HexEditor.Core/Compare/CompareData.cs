using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Compare;

/// <summary>
/// 比較対象のデータ (ANA-01 の仕様 1)。ドキュメントのスナップショット (未保存の編集を含む、開始時点の内容。06 の 0.2)、
/// データソース (ディスク上のファイル・ディスク・スナップショット)、メモリ上のバイト列を同じ形で読む。読み込みはスレッドセーフ。
/// </summary>
public interface ICompareData
{
    long Length { get; }

    /// <summary>位置を指定して読む。読めなかった範囲は結果で返す (位置はこのデータ上の絶対位置)。</summary>
    ReadResult Read(long offset, Span<byte> destination);
}

/// <summary>比較対象のデータを作る。</summary>
public static class CompareData
{
    public static ICompareData FromSnapshot(DocumentSnapshot snapshot) => new SnapshotData(snapshot);

    public static ICompareData FromSource(IByteSource source) => new SourceData(source);

    public static ICompareData FromBytes(byte[] data) => new SourceData(new MemoryByteSource(data, "bytes", SourceCapabilities.None));

    /// <summary>スナップショットのデータならそのスナップショット (マージで大きな範囲を参照として挿入するため)。</summary>
    public static DocumentSnapshot? SnapshotOf(ICompareData data) => (data as SnapshotData)?.Snapshot;

    private sealed class SnapshotData(DocumentSnapshot snapshot) : ICompareData
    {
        public DocumentSnapshot Snapshot => snapshot;

        public long Length => snapshot.Length;

        public ReadResult Read(long offset, Span<byte> destination) => snapshot.Read(offset, destination);
    }

    private sealed class SourceData(IByteSource source) : ICompareData
    {
        public long Length => source.Length;

        public ReadResult Read(long offset, Span<byte> destination) => source.Read(offset, destination);
    }
}

/// <summary>比較する範囲 (ANA-01 の仕様 2)。<see cref="Start"/> と <see cref="Length"/> はデータ上の位置。</summary>
public sealed record CompareRange(ICompareData Data, long Start, long Length)
{
    /// <summary>データ全体。</summary>
    public static CompareRange Whole(ICompareData data) => new(data, 0, data.Length);

    public long End => Start + Length;
}
