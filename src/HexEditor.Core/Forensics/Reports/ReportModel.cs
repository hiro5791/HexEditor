using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>レポートの形式 (12 の FOR-33 の仕様 2)。</summary>
public enum ReportFormat
{
    Html,
    Pdf,
}

/// <summary>用紙 (PDF)。縦向き。</summary>
public enum ReportPaper
{
    A4,
    Letter,
}

/// <summary>レポートに含める項目の ID (仕様 1)。該当するデータがない項目は出さない。</summary>
public static class ReportSections
{
    public const string Cover = "cover";
    public const string Evidence = "evidence";
    public const string Acquisition = "acquisition";
    public const string Device = "device";
    public const string Partitions = "partitions";
    public const string FileSystem = "filesystem";
    public const string Bookmarks = "bookmarks";
    public const string Carving = "carving";
    public const string Audit = "audit";

    public static IReadOnlyList<string> All { get; } = [Cover, Evidence, Acquisition, Device, Partitions, FileSystem, Bookmarks, Carving, Audit];
}

/// <summary>レポートの内容の部品。HTML と PDF の両方に同じ内容を出す。</summary>
public abstract record ReportBlock;

public sealed record ReportParagraph(string Text) : ReportBlock;

public sealed record ReportKeyValues(IReadOnlyList<KeyValuePair<string, string>> Items) : ReportBlock;

/// <summary>
/// 表。<see cref="Rows"/> は 1 回だけ順に読む (メモリに溜めない)。<see cref="MaxRows"/> を超える行は本文には入れず、
/// <see cref="AttachmentName"/> があれば全件を CSV の添付ファイルに書く (仕様 1 の「1,000 件を超える場合は先頭 1,000 件とし、全件は CSV を添付する」)。
/// </summary>
public sealed record ReportTable(IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<string>> Rows) : ReportBlock
{
    public const int DefaultMaxRows = 1000;

    public int MaxRows { get; init; } = int.MaxValue;

    public string? AttachmentName { get; init; }

    /// <summary>添付の CSV にそのままコピーする既存の CSV ファイル (マニフェストなど)。なければ行から作る。</summary>
    public string? AttachmentSource { get; init; }

    /// <summary>行数が分かっていれば入れる (「先頭 N 件 / 全 M 件」の表示)。</summary>
    public long? TotalRows { get; init; }
}

/// <summary>ブックマークの位置などの Hex ダンプ。</summary>
public sealed record ReportDump(string Title, long Offset, byte[] Data) : ReportBlock;

public sealed record DiagramSegment(string Label, long Start, long Length, int Color);

/// <summary>配置図 (パーティションの並びなど)。HTML には SVG で、PDF には四角で描く。</summary>
public sealed record ReportDiagram(string Title, long TotalLength, IReadOnlyList<DiagramSegment> Segments) : ReportBlock;

/// <summary>見出し付きの節。</summary>
public sealed record ReportSection(string Id, string Title, IEnumerable<ReportBlock> Blocks);

/// <summary>
/// 他の領域 (イメージ作成の取得ログ、デバイス情報、パーティション、ファイルシステム、削除ファイルなど) が節を提供するための口。
/// <see cref="ReportRegistry"/> に登録しておくと、<see cref="ReportBuilder"/> が節の ID に従って呼ぶ。提供するデータがなければ null を返す。
/// </summary>
public interface IReportContributor
{
    /// <summary><see cref="ReportSections"/> の ID。</summary>
    string SectionId { get; }

    ReportSection? Build(ReportContext context);
}

/// <summary>寄稿者の登録簿。</summary>
public sealed class ReportRegistry
{
    private readonly List<IReportContributor> _contributors = [];
    private readonly object _gate = new();

    public static ReportRegistry Shared { get; } = new();

    public void Register(IReportContributor contributor)
    {
        lock (_gate)
        {
            _contributors.Add(contributor);
        }
    }

    public IReadOnlyList<IReportContributor> For(string sectionId)
    {
        lock (_gate)
        {
            return [.. _contributors.Where(c => c.SectionId == sectionId)];
        }
    }
}

/// <summary>ブックマーク 1 つ (仕様 1: 名前、オフセット、LBA、コメント、その位置の Hex ダンプ)。</summary>
public sealed record ReportBookmark(string Document, string Name, long Offset, long? Lba, string Comment, byte[] Dump);

/// <summary>レポートの入力。</summary>
public sealed record ReportInput
{
    public required CaseInfo Case { get; init; }

    public required IReadOnlyList<EvidenceItem> Evidence { get; init; }

    public IReadOnlyList<ReportBookmark> Bookmarks { get; init; } = [];

    /// <summary>カービングのマニフェスト (<c>carving-manifest.csv</c>)。複数あれば全部。</summary>
    public IReadOnlyList<string> CarvingManifests { get; init; } = [];

    /// <summary>監査ログ (ケースの <c>audit.log</c>)。</summary>
    public string? AuditLogPath { get; init; }

    public required string AppName { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>レポートを作る日時 (UTC)。</summary>
    public DateTime NowUtc { get; init; }

    public ReportRegistry? Registry { get; init; }
}

/// <summary>レポートの設定 (ダイアログ)。</summary>
public sealed record ReportOptions
{
    /// <summary>含める項目 (<see cref="ReportSections"/>)。</summary>
    public ISet<string> Sections { get; init; } = new HashSet<string>(ReportSections.All);

    public ReportFormat Format { get; init; } = ReportFormat.Html;

    public ReportPaper Paper { get; init; } = ReportPaper.A4;

    /// <summary>保存先 (ケースの <c>reports/</c>)。</summary>
    public required string OutputFolder { get; init; }

    /// <summary>ファイル名 (拡張子なし)。null なら <c>report-&lt;ケース番号&gt;-&lt;日時&gt;</c>。</summary>
    public string? FileName { get; init; }

    /// <summary>監査ログの期間 (null なら全件)。</summary>
    public DateTime? AuditFromUtc { get; init; }

    public DateTime? AuditToUtc { get; init; }

    /// <summary>ブックマークの Hex ダンプの長さ (既定 64、最大 4096)。</summary>
    public int DumpBytes { get; init; } = 64;

    /// <summary>ロゴ画像 (設定。PNG / JPEG / SVG)。HTML には data URI で埋め込む。</summary>
    public string? LogoPath { get; init; }

    /// <summary>ヘッダーの文言 (設定)。</summary>
    public string? HeaderText { get; init; }

    /// <summary>ローカル時刻のタイムゾーン (ケースのタイムゾーン)。</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>文言 (UI の言語)。</summary>
    public Func<string, string> Text { get; init; } = ReportText.English;

    /// <summary>言語コード (HTML の lang)。</summary>
    public string Language { get; init; } = "en";

    /// <summary>PDF で 1 つの表に入れる行の上限 (超えたら先頭だけ。全件は HTML か CSV)。</summary>
    public int PdfMaxRows { get; init; } = 3000;

    public ForensicAudit? Audit { get; init; }

    public IProgress<long>? Progress { get; init; }
}

/// <summary>作ったレポート。</summary>
public sealed class ReportResult
{
    /// <summary>レポートのファイル。</summary>
    public required string Path { get; init; }

    public required ReportFormat Format { get; init; }

    /// <summary>レポートのファイルの SHA-256 (小文字の 16 進。完了の InfoBar と監査ログに出す)。</summary>
    public required string Sha256 { get; init; }

    public long Bytes { get; init; }

    /// <summary>添付した CSV。</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>PDF を作れなかった理由 (HTML だけを作ったとき。仕様のエラー欄)。</summary>
    public string? PdfError { get; init; }

    /// <summary>PDF には表の先頭だけを入れた (全件は HTML か CSV で出すことを提案する)。</summary>
    public bool PdfTruncated { get; init; }

    public int Pages { get; init; }
}

/// <summary>レポートの文言。UI の言語のリソースを渡す。渡さなければ英語。</summary>
public static class ReportText
{
    private static readonly Dictionary<string, string> Strings = new(StringComparer.Ordinal)
    {
        ["Title"] = "Forensic Report",
        ["CaseNumber"] = "Case number",
        ["CaseName"] = "Case name",
        ["Investigator"] = "Investigator",
        ["Organization"] = "Organization",
        ["Description"] = "Description",
        ["CreatedAt"] = "Case created",
        ["GeneratedAt"] = "Report created",
        ["TimeZone"] = "Time zone",
        ["GeneratedBy"] = "Created by",
        ["Page"] = "Page",
        ["Of"] = "of",
        ["Case"] = "Case",
        ["Section_cover"] = "Cover",
        ["Section_evidence"] = "Evidence",
        ["Section_acquisition"] = "Acquisition records",
        ["Section_device"] = "Device information",
        ["Section_partitions"] = "Partitions",
        ["Section_filesystem"] = "File systems",
        ["Section_bookmarks"] = "Bookmarks",
        ["Section_carving"] = "Carving results",
        ["Section_audit"] = "Audit log",
        ["Evidence_Id"] = "Evidence",
        ["Evidence_Description"] = "Description",
        ["Evidence_Type"] = "Type",
        ["Evidence_Path"] = "Path",
        ["Evidence_Model"] = "Model",
        ["Evidence_Serial"] = "Serial number",
        ["Evidence_Capacity"] = "Capacity (bytes)",
        ["Evidence_Sectors"] = "Sectors",
        ["Evidence_SectorSize"] = "Sector size",
        ["Evidence_Hashes"] = "Hashes",
        ["Evidence_Verification"] = "Verification",
        ["Evidence_Added"] = "Added (UTC)",
        ["Evidence_AddedLocal"] = "Added (local)",
        ["Evidence_AddedBy"] = "Added by",
        ["Verification_passed"] = "Passed",
        ["Verification_failed"] = "Failed",
        ["Verification_none"] = "Not verified",
        ["Hash_NotComputed"] = "Not computed",
        ["Kind_PhysicalDisk"] = "Disk",
        ["Kind_Volume"] = "Volume",
        ["Kind_ImageFile"] = "Image",
        ["Kind_File"] = "File",
        ["Kind_Memory"] = "Memory",
        ["Bookmark_Document"] = "Document",
        ["Bookmark_Name"] = "Name",
        ["Bookmark_Offset"] = "Offset",
        ["Bookmark_Lba"] = "LBA",
        ["Bookmark_Comment"] = "Comment",
        ["Bookmark_Dump"] = "Hex dump",
        ["Carving_Number"] = "No.",
        ["Carving_Type"] = "Type",
        ["Carving_Offset"] = "Offset",
        ["Carving_Lba"] = "LBA",
        ["Carving_Length"] = "Length",
        ["Carving_Method"] = "Method",
        ["Carving_Output"] = "Output",
        ["Carving_Total"] = "Files found",
        ["Audit_Seq"] = "No.",
        ["Audit_TimeUtc"] = "Time (UTC)",
        ["Audit_TimeLocal"] = "Time (local)",
        ["Audit_User"] = "User",
        ["Audit_Computer"] = "Computer",
        ["Audit_Operation"] = "Operation",
        ["Audit_Evidence"] = "Evidence",
        ["Audit_Result"] = "Result",
        ["Audit_Message"] = "Message",
        ["Result_success"] = "success",
        ["Result_failure"] = "failure",
        ["Result_cancelled"] = "cancelled",
        ["Truncated"] = "Showing the first {0} of {1} rows. The full list is in the attached CSV file or the HTML report.",
        ["TruncatedPdf"] = "Showing the first {0} rows. The full list is in the HTML report or the CSV file.",
        ["Attachment"] = "Full list",
        ["Diagram_Free"] = "Unallocated",
        ["NoData"] = "No data.",
        ["InvalidTime"] = "invalid time",
    };

    /// <summary>英語の文言 (キーがなければキー自体)。</summary>
    public static string English(string key) => Strings.GetValueOrDefault(key, key);

    /// <summary>使われている文言のキー (UI のリソースに用意する)。</summary>
    public static IReadOnlyCollection<string> Keys => Strings.Keys;
}
