using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Encryption;

namespace HexEditor.Core.FileSystems.Partitions;

/// <summary>
/// パーティション解析 (FOR-06) の各パーティションに、暗号化の検出 (FOR-29 の <see cref="EncryptionDetector.Detect"/>) の結果を付ける。
/// 読むのはパーティションごとにメタデータの数セクタと先頭 64 KiB だけ (パーティションの数は <see cref="EncryptionDetector.MaxPartitions"/> まで)。
/// </summary>
public static class PartitionEncryption
{
    public static PartitionAnalysis Annotate(PartitionAnalysis analysis, ICarveData data, CancellationToken cancellationToken = default)
    {
        int checkedCount = 0;
        var rows = new List<PartitionRow>(analysis.Rows.Count);
        foreach (PartitionRow row in analysis.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.IsUnallocated || row.IsContainer || row.Size <= 0 || checkedCount >= EncryptionDetector.MaxPartitions)
            {
                rows.Add(row);
                continue;
            }

            checkedCount++;
            rows.Add(EncryptionDetector.Detect(data, row.StartOffset, row.Size, row.TypeGuid) is { } info ? row with { Encryption = info } : row);
        }

        return analysis with { Rows = rows };
    }
}
