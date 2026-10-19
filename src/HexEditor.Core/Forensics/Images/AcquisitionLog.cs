using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Forensics.Images;

/// <summary>取得ログ・クローンのログの見出し (UI の表示言語の文言を渡す。既定は英語)。</summary>
public sealed record AcquisitionLogText
{
    public string Title { get; init; } = "Disk image acquisition log";

    public string CloneTitle { get; init; } = "Disk clone log";

    public string Status { get; init; } = "Status";

    public string StatusCompleted { get; init; } = "Completed";

    public string StatusCancelled { get; init; } = "Interrupted";

    public string StatusSourceLost { get; init; } = "The source was lost";

    public string StatusFailed { get; init; } = "Failed";

    public string Source { get; init; } = "Source";

    public string Path { get; init; } = "Path";

    public string Model { get; init; } = "Model";

    public string Serial { get; init; } = "Serial number";

    public string Capacity { get; init; } = "Capacity";

    public string SectorSize { get; init; } = "Sector size";

    public string Range { get; init; } = "Range";

    public string Output { get; init; } = "Output";

    public string Format { get; init; } = "Format";

    public string Files { get; init; } = "Files";

    public string Target { get; init; } = "Target";

    public string Started { get; init; } = "Started";

    public string Ended { get; init; } = "Ended";

    public string SectorsRead { get; init; } = "Sectors read";

    public string UnreadableSectors { get; init; } = "Unreadable sectors";

    public string None { get; init; } = "none";

    public string Hashes { get; init; } = "Hashes";

    public string IncludedHashes { get; init; } = "included ranges";

    public string Verification { get; init; } = "Verification";

    public string VerificationMatch { get; init; } = "match";

    public string VerificationMismatch { get; init; } = "mismatch";

    public string VerificationNoReference { get; init; } = "no reference value";

    public string VerificationReadError { get; init; } = "cannot verify because of read errors";

    public string VerificationSkipped { get; init; } = "not verified";

    public string AppVersion { get; init; } = "Application version";

    public string Case { get; init; } = "Case";

    public string CaseNumber { get; init; } = "Case number";

    public string EvidenceNumber { get; init; } = "Evidence number";

    public string Examiner { get; init; } = "Examiner";

    public string Description { get; init; } = "Description";

    public string Notes { get; init; } = "Notes";

    public string Scope { get; init; } = "Acquired ranges";

    public string ScopeFull { get; init; } = "whole";

    public string ScopeSparse { get; init; } = "sparse image (unallocated areas are not included)";

    public string ScopeSkeleton { get; init; } = "skeleton image (partition tables and file system metadata only)";

    public string ScopeNote { get; init; } = "The excluded areas are stored as zeros, so the hashes of this image do not match the hashes of the whole source disk.";

    public string RangesFile { get; init; } = "Included ranges";

    public string Message { get; init; } = "Message";
}

/// <summary>取得の範囲 (FOR-23 の仕様 1)。</summary>
public enum AcquisitionScope
{
    Full,
    Sparse,
    Skeleton,
}

/// <summary>取得・クローンの終わり方。</summary>
public enum AcquisitionStatus
{
    Completed,
    Cancelled,
    SourceLost,
    Failed,
}

/// <summary>入力元の説明 (取得ログ・監査ログに書く。FOR-20 の仕様 5)。</summary>
public sealed record AcquisitionSourceInfo
{
    public required string Path { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? Identity { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public long Capacity { get; init; }

    public int SectorSize { get; init; } = 512;

    /// <summary>入力元が物理ディスク。</summary>
    public bool Physical { get; init; }

    /// <summary>入力元のある物理ディスクの番号 (出力先の検査)。分からなければ null。</summary>
    public int? DiskNumber { get; init; }
}

/// <summary>取得ログの内容。</summary>
public sealed record AcquisitionLogData
{
    public bool IsClone { get; init; }

    public AcquisitionStatus Status { get; init; }

    public required AcquisitionSourceInfo Source { get; init; }

    public long RangeStart { get; init; }

    public long RangeLength { get; init; }

    public string FormatName { get; init; } = string.Empty;

    public IReadOnlyList<string> Files { get; init; } = [];

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Ended { get; init; }

    public long SectorsRead { get; init; }

    public IReadOnlyList<SectorRange> Unreadable { get; init; } = [];

    public IReadOnlyDictionary<string, byte[]> Hashes { get; init; } = new Dictionary<string, byte[]>();

    public IReadOnlyDictionary<string, byte[]>? IncludedHashes { get; init; }

    public VerificationVerdict? Verification { get; init; }

    public string AppVersion { get; init; } = string.Empty;

    public EwfCaseInfo? Case { get; init; }

    public ImageCaseInfo? CaseFolder { get; init; }

    public AcquisitionScope Scope { get; init; }

    public string? RangesFile { get; init; }

    public string? Message { get; init; }

    public TimeZoneInfo? TimeZone { get; init; }
}

/// <summary>取得ログ (<c>&lt;名前&gt;.txt</c>) の書き込みと、ハッシュの読み込み (FOR-20 の仕様 5、FOR-22 の仕様 2)。</summary>
public static partial class AcquisitionLog
{
    /// <summary>出力のパスに対する取得ログのパス (<c>m.dd</c> → <c>m.txt</c>、<c>m.E01</c> → <c>m.txt</c>、分割 raw の <c>m</c> → <c>m.txt</c>)。</summary>
    public static string PathFor(string output, ImageFormat format) => format switch
    {
        ImageFormat.SplitRaw => SplitRawNaming.TryParse(output, out string b, out _) ? b + ".txt" : output + ".txt",
        _ => System.IO.Path.ChangeExtension(output, ".txt"),
    };

    /// <summary>イメージの最初のファイルの隣にある取得ログ (なければ null)。</summary>
    public static string? FindFor(string imageFile, ImageFormat format)
    {
        string path = PathFor(imageFile, format);
        return File.Exists(path) ? path : null;
    }

    /// <summary>日時の表記 (FOR-05 の仕様 2: UTC は Z、ローカルはオフセット付き)。</summary>
    public static string FormatTime(DateTimeOffset t, TimeZoneInfo? zone = null)
    {
        DateTimeOffset utc = t.ToUniversalTime();
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + " ("
            + local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + ")";
    }

    public static string Hex(byte[] value) => Convert.ToHexStringLower(value);

    /// <summary>取得ログの本文を作る。</summary>
    public static string Build(AcquisitionLogData d, AcquisitionLogText t)
    {
        var s = new StringBuilder();
        void Line(string label, string value) => s.Append(label).Append(": ").Append(value).Append("\r\n");
        s.Append(d.IsClone ? t.CloneTitle : t.Title).Append("\r\n").Append("\r\n");
        Line(t.Status, d.Status switch
        {
            AcquisitionStatus.Completed => t.StatusCompleted,
            AcquisitionStatus.Cancelled => t.StatusCancelled,
            AcquisitionStatus.SourceLost => t.StatusSourceLost,
            _ => t.StatusFailed,
        });
        if (d.Message is { Length: > 0 })
        {
            Line(t.Message, d.Message);
        }

        s.Append("\r\n[").Append(t.Source).Append("]\r\n");
        Line(t.Path, d.Source.Path);
        Line(t.Model, d.Source.Model ?? "-");
        Line(t.Serial, d.Source.SerialNumber ?? "-");
        Line(t.Capacity, string.Create(CultureInfo.InvariantCulture, $"{d.Source.Capacity} bytes ({d.Source.Capacity / Math.Max(1, d.Source.SectorSize)} sectors)"));
        Line(t.SectorSize, d.Source.SectorSize.ToString(CultureInfo.InvariantCulture));
        int sector = Math.Max(1, d.Source.SectorSize);
        Line(t.Range, string.Create(CultureInfo.InvariantCulture, $"LBA {d.RangeStart / sector} + {d.RangeLength / sector} sectors ({d.RangeLength} bytes)"));

        s.Append("\r\n[").Append(d.IsClone ? t.Target : t.Output).Append("]\r\n");
        Line(t.Format, d.FormatName);
        Line(t.Files, d.Files.Count == 0 ? t.None : string.Join("; ", d.Files));
        Line(t.Scope, d.Scope switch { AcquisitionScope.Sparse => t.ScopeSparse, AcquisitionScope.Skeleton => t.ScopeSkeleton, _ => t.ScopeFull });
        if (d.Scope != AcquisitionScope.Full)
        {
            s.Append(t.ScopeNote).Append("\r\n");
            if (d.RangesFile is not null)
            {
                Line(t.RangesFile, d.RangesFile);
            }
        }

        s.Append("\r\n");
        TimeZoneInfo? zone = d.TimeZone ?? d.CaseFolder?.TimeZone;
        Line(t.Started, FormatTime(d.Started, zone));
        Line(t.Ended, FormatTime(d.Ended, zone));
        Line(t.SectorsRead, d.SectorsRead.ToString(CultureInfo.InvariantCulture));
        long bad = d.Unreadable.Sum(r => r.SectorCount);
        Line(t.UnreadableSectors, bad == 0 ? "0" : bad.ToString(CultureInfo.InvariantCulture) + " (" + string.Join(", ",
            d.Unreadable.Select(r => string.Create(CultureInfo.InvariantCulture, $"LBA {r.StartSector} + {r.SectorCount}"))) + ")");

        s.Append("\r\n[").Append(t.Hashes).Append("]\r\n");
        foreach ((string algorithm, byte[] value) in d.Hashes)
        {
            Line(algorithm, Hex(value));
        }

        if (d.IncludedHashes is not null)
        {
            foreach ((string algorithm, byte[] value) in d.IncludedHashes)
            {
                Line(algorithm + " (" + t.IncludedHashes + ")", Hex(value));
            }
        }

        Line(t.Verification, d.Verification switch
        {
            VerificationVerdict.Match => t.VerificationMatch,
            VerificationVerdict.Mismatch => t.VerificationMismatch,
            VerificationVerdict.NoReference => t.VerificationNoReference,
            VerificationVerdict.ReadError => t.VerificationReadError,
            _ => t.VerificationSkipped,
        });

        s.Append("\r\n");
        Line(t.AppVersion, d.AppVersion);
        if (d.Case is { } c && (c.CaseNumber.Length > 0 || c.EvidenceNumber.Length > 0 || c.Examiner.Length > 0))
        {
            s.Append("\r\n[").Append(t.Case).Append("]\r\n");
            Line(t.CaseNumber, c.CaseNumber);
            Line(t.EvidenceNumber, c.EvidenceNumber);
            Line(t.Examiner, c.Examiner);
            Line(t.Description, c.Description);
            Line(t.Notes, c.Notes);
        }

        return s.ToString();
    }

    /// <summary>取得ログを書く (UTF-8、BOM 付き。メモ帳でも文字化けしないように)。一時ファイルに書いてから置き換える。</summary>
    public static void Write(string path, AcquisitionLogData data, AcquisitionLogText text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Build(data, text), new UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 取得ログからイメージ全体のハッシュを読む (「MD5: …」の形の行。含めた範囲だけのハッシュ「MD5 (…): …」は除く)。
    /// 読めない・壊れている行は無視する。
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadHashes(string path)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            return result;
        }

        foreach (string line in File.ReadLines(path))
        {
            Match m = HashLine().Match(line);
            if (m.Success && ImageHashes.TryNormalize(m.Groups[1].Value, out string algorithm))
            {
                string hex = m.Groups[2].Value;
                int expected = algorithm == ImageHashes.Md5 ? 32 : algorithm == ImageHashes.Sha1 ? 40 : 64;
                if (hex.Length == expected)
                {
                    result.TryAdd(algorithm, Convert.FromHexString(hex));
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\s*(MD5|SHA-1|SHA-256|SHA1|SHA256)\s*:\s*([0-9A-Fa-f]+)\s*$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HashLine();
}
