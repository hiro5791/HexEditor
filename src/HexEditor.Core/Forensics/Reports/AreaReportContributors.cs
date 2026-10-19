using System.Globalization;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>
/// ほかの領域の節の共通の部品: 項目名は <c>localize</c> (UI のリソース <c>Report_&lt;キー&gt;</c>) で翻訳し、なければ英語。
/// </summary>
public abstract class AreaReportContributor(IReadOnlyDictionary<string, string> english, Func<string, string?>? localize) : IReportContributor
{
    public abstract string SectionId { get; }

    public abstract ReportSection? Build(ReportContext context);

    protected string T(string key) => localize?.Invoke(key) ?? (english.TryGetValue(key, out string? v) ? v : key);

    protected static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>レポート (FOR-33) の「パーティション」の節: 解析したディスクごとのパーティションの一覧と配置図 (FOR-06)、暗号化の検出 (FOR-29)。</summary>
public sealed class PartitionReportContributor(Func<IEnumerable<(string Document, PartitionAnalysis Analysis)>> source, Func<string, string?>? localize = null)
    : AreaReportContributor(English, localize)
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Part_Document"] = "Disk: {0} (sector size {1} bytes)",
        ["Part_Number"] = "No.",
        ["Part_Scheme"] = "Scheme",
        ["Part_Type"] = "Type",
        ["Part_Name"] = "Name",
        ["Part_StartLba"] = "Start LBA",
        ["Part_Sectors"] = "Sectors",
        ["Part_Size"] = "Size (bytes)",
        ["Part_FileSystem"] = "File system",
        ["Part_Encryption"] = "Encryption",
        ["Part_Unallocated"] = "Unallocated",
        ["Part_Diagram"] = "Layout",
    };

    public override string SectionId => ReportSections.Partitions;

    public override ReportSection? Build(ReportContext context)
    {
        List<(string Document, PartitionAnalysis Analysis)> disks = [.. source()];
        if (disks.Count == 0)
        {
            return null;
        }

        var blocks = new List<ReportBlock>();
        foreach ((string document, PartitionAnalysis analysis) in disks)
        {
            blocks.Add(new ReportParagraph(string.Format(CultureInfo.InvariantCulture, T("Part_Document"), document, analysis.SectorSize)));
            PartitionRow[] rows = [.. analysis.Rows];
            blocks.Add(new ReportTable(
                [T("Part_Number"), T("Part_Scheme"), T("Part_Type"), T("Part_Name"), T("Part_StartLba"), T("Part_Sectors"), T("Part_Size"), T("Part_FileSystem"), T("Part_Encryption")],
                rows.Select(r => (IReadOnlyList<string>)
                [
                    r.IsUnallocated ? "-" : r.Number.ToString(CultureInfo.InvariantCulture),
                    r.IsUnallocated ? T("Part_Unallocated") : r.Scheme.ToString(),
                    r.TypeText ?? r.TypeKey ?? r.TypeCode,
                    r.Name,
                    N(r.StartLba),
                    N(r.SectorCount),
                    N(r.Size),
                    r.FileSystem == FileSystemKind.Unknown ? string.Empty : FileSystemDetector.DisplayName(r.FileSystem),
                    r.Encryption?.Kind.ToString() ?? string.Empty,
                ]))
            {
                MaxRows = ReportTable.DefaultMaxRows,
                TotalRows = rows.Length,
            });
            blocks.Add(new ReportDiagram(T("Part_Diagram"), analysis.DiskLength,
                [.. rows.Where(r => !r.IsContainer && r.Size > 0).Select((r, i) =>
                    new DiagramSegment(r.IsUnallocated ? T("Part_Unallocated") : "#" + r.Number.ToString(CultureInfo.InvariantCulture), r.StartOffset, r.Size, r.IsUnallocated ? -1 : i))]));
        }

        return new ReportSection(ReportSections.Partitions, context.T("Section_partitions"), blocks);
    }
}

/// <summary>レポート (FOR-33) の「ファイルシステム」の節: 開いたファイルシステムの概要 (FOR-09〜14) と論理ボリューム (FOR-07)。</summary>
public sealed class FileSystemReportContributor(
    Func<IEnumerable<(string Document, long VolumeOffset, IFileSystem FileSystem)>> fileSystems,
    Func<IEnumerable<(string Document, LogicalVolumeScan Scan)>> logicalVolumes,
    Func<string, string?>? localize = null) : AreaReportContributor(English, localize)
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Fs_Document"] = "Document",
        ["Fs_Offset"] = "Volume offset",
        ["Fs_Type"] = "Type",
        ["Fs_Label"] = "Label",
        ["Fs_Serial"] = "Serial number",
        ["Fs_ClusterSize"] = "Cluster size (bytes)",
        ["Fs_Clusters"] = "Clusters",
        ["Fs_FreeClusters"] = "Free clusters",
        ["Fs_Issues"] = "Issues",
        ["Lv_Title"] = "Logical volumes: {0}",
        ["Lv_Name"] = "Name",
        ["Lv_Group"] = "Group",
        ["Lv_Kind"] = "Type",
        ["Lv_Size"] = "Size (bytes)",
        ["Lv_Layout"] = "Layout",
        ["Lv_Members"] = "Members",
    };

    public override string SectionId => ReportSections.FileSystem;

    public override ReportSection? Build(ReportContext context)
    {
        var blocks = new List<ReportBlock>();
        foreach ((string document, long offset, IFileSystem fs) in fileSystems())
        {
            FileSystemSummary s = fs.Summary;
            blocks.Add(new ReportKeyValues(
            [
                new(T("Fs_Document"), document),
                new(T("Fs_Offset"), N(offset)),
                new(T("Fs_Type"), s.TypeName.Length > 0 ? s.TypeName : FileSystemDetector.DisplayName(s.Kind)),
                new(T("Fs_Label"), s.Label),
                new(T("Fs_Serial"), s.Serial),
                new(T("Fs_ClusterSize"), N(s.ClusterSize)),
                new(T("Fs_Clusters"), N(s.TotalClusters)),
                new(T("Fs_FreeClusters"), s.FreeClusters is long free ? N(free) : string.Empty),
                new(T("Fs_Issues"), fs.Issues.Count.ToString(CultureInfo.InvariantCulture)),
            ]));
        }

        foreach ((string document, LogicalVolumeScan scan) in logicalVolumes())
        {
            if (scan.Volumes.Count == 0)
            {
                continue;
            }

            blocks.Add(new ReportParagraph(string.Format(CultureInfo.InvariantCulture, T("Lv_Title"), document)));
            blocks.Add(new ReportTable([T("Lv_Name"), T("Lv_Group"), T("Lv_Kind"), T("Lv_Size"), T("Lv_Layout"), T("Lv_Members")],
                scan.Volumes.Select(v => (IReadOnlyList<string>)[v.Name, v.Group, v.Kind.ToString(), N(v.Size), v.Layout, string.Join(", ", v.Members.Distinct())]))
            {
                MaxRows = ReportTable.DefaultMaxRows,
                TotalRows = scan.Volumes.Count,
            });
        }

        return blocks.Count == 0 ? null : new ReportSection(ReportSections.FileSystem, context.T("Section_filesystem"), blocks);
    }
}

/// <summary>取得したイメージの記録 (FOR-20 の取得ログ)。</summary>
public sealed record AcquiredImageRecord(string ImagePath, string SourcePath, IReadOnlyDictionary<string, string> Hashes, DateTime AcquiredUtc, string? LogPath);

/// <summary>レポート (FOR-33) の「取得記録」の節: このセッションで作ったイメージ (FOR-20〜23) の出力先、入力元、ハッシュ、取得ログ。</summary>
public sealed class AcquisitionReportContributor(Func<string, string?>? localize = null) : AreaReportContributor(English, localize)
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Acq_Image"] = "Image",
        ["Acq_Source"] = "Source",
        ["Acq_Time"] = "Acquired",
        ["Acq_Hashes"] = "Hashes",
        ["Acq_Log"] = "Acquisition log",
    };

    private readonly object _gate = new();
    private readonly List<AcquiredImageRecord> _records = [];

    public override string SectionId => ReportSections.Acquisition;

    /// <summary>取得したイメージを加える (同じ出力先の前の記録は置き換える)。</summary>
    public void Add(AcquiredImageRecord record)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => string.Equals(r.ImagePath, record.ImagePath, StringComparison.OrdinalIgnoreCase));
            _records.Add(record);
        }
    }

    public IReadOnlyList<AcquiredImageRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    public override ReportSection? Build(ReportContext context)
    {
        IReadOnlyList<AcquiredImageRecord> records = Records;
        if (records.Count == 0)
        {
            return null;
        }

        var blocks = new List<ReportBlock>();
        foreach (AcquiredImageRecord r in records)
        {
            var items = new List<KeyValuePair<string, string>>
            {
                new(T("Acq_Image"), r.ImagePath),
                new(T("Acq_Source"), r.SourcePath),
                new(T("Acq_Time"), ReportTime.Both(r.AcquiredUtc, context.Options.Zone)),
            };
            items.AddRange(r.Hashes.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => new KeyValuePair<string, string>(h.Key, h.Value)));
            if (r.LogPath is { } log)
            {
                items.Add(new(T("Acq_Log"), log));
            }

            blocks.Add(new ReportKeyValues(items));
        }

        return new ReportSection(ReportSections.Acquisition, context.T("Section_acquisition"), blocks);
    }
}
