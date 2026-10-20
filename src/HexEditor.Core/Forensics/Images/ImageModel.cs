using System.Text.Json.Nodes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images;

/// <summary>ディスクイメージの形式 (FOR-20、FOR-21)。</summary>
public enum ImageFormat
{
    /// <summary>1 つのファイルの raw (dd)。</summary>
    Raw,

    /// <summary>分割 raw (<c>名前.001</c>、<c>名前.002</c> …)。</summary>
    SplitRaw,

    /// <summary>EnCase 6 互換の Expert Witness 形式。</summary>
    E01,

    /// <summary>Virtual PC / Hyper-V の VHD (固定・可変・差分)。</summary>
    Vhd,

    /// <summary>Hyper-V の VHDX (固定・可変・差分)。</summary>
    Vhdx,

    /// <summary>VMware の VMDK (記述ファイル、flat、hosted sparse、streamOptimized)。</summary>
    Vmdk,
}

/// <summary>E01 の圧縮 (FOR-20 の仕様 4)。</summary>
public enum EwfCompression
{
    None,

    /// <summary>速度優先 (zlib レベル 1)。</summary>
    Fast,

    /// <summary>容量優先 (zlib レベル 9)。</summary>
    Best,
}

/// <summary>E01 のケース情報 (FOR-20 の仕様 4)。</summary>
public sealed record EwfCaseInfo
{
    public string CaseNumber { get; init; } = string.Empty;

    public string EvidenceNumber { get; init; } = string.Empty;

    public string Examiner { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

/// <summary>セクタの範囲 (開始 LBA とセクタ数)。</summary>
public readonly record struct SectorRange(long StartSector, long SectorCount)
{
    public long EndSector => StartSector + SectorCount;
}

/// <summary>
/// 開いたイメージの情報 (FOR-21 の仕様 5「イメージの情報」)。形式にない項目は null。
/// </summary>
public sealed record ImageInfo
{
    public required ImageFormat Format { get; init; }

    /// <summary>イメージを構成するファイル (分割 raw・E01 のセグメント、差分ディスクの親を含む)。</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public long MediaSize { get; init; }

    public int BytesPerSector { get; init; } = 512;

    public EwfCaseInfo? Case { get; init; }

    /// <summary>取得日時 (UTC)。</summary>
    public DateTimeOffset? AcquiredAt { get; init; }

    /// <summary>取得したシステムの日時 (UTC)。</summary>
    public DateTimeOffset? SystemDate { get; init; }

    /// <summary>取得ツール (E01 のヘッダーの av / ツール名、VHD の作成アプリなど)。</summary>
    public string? AcquisitionTool { get; init; }

    public string? AcquisitionOs { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public long? ChunkCount { get; init; }

    public int? SectorsPerChunk { get; init; }

    public bool? Compressed { get; init; }

    public string? CompressionLevel { get; init; }

    /// <summary>格納されたハッシュ (アルゴリズム名 MD5 / SHA-1 → 値)。</summary>
    public IReadOnlyDictionary<string, byte[]> StoredHashes { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>取得時に読めなかったセクタ (E01 の error2)。</summary>
    public IReadOnlyList<SectorRange> AcquisitionErrors { get; init; } = [];

    /// <summary>VHD / VHDX / VMDK の種類 (固定・可変・差分、createType)。</summary>
    public string? Subtype { get; init; }

    /// <summary>差分ディスクの親のパス。</summary>
    public string? ParentPath { get; init; }

    /// <summary>足りないセグメント (「ある分だけ開く」で開いた場合)。</summary>
    public IReadOnlyList<string> MissingSegments { get; init; } = [];
}

/// <summary>
/// ディスクイメージのデータソース (FOR-21)。セクタサイズを持ち、ディスクと同じく区切り線「セクタ」を既定にする。
/// 書き込みはできない (E01・VHD などは常に読み取り専用。raw の書き込みは ENG-31 の <see cref="Devices.DiskImageByteSource"/>)。
/// </summary>
public interface IImageSource : IByteSource
{
    ImageInfo Info { get; }

    /// <summary>読み込み時に検査値が合わなかった範囲 (E01 のチャンクの Adler-32。FOR-21 の仕様 6)。見つかるたびに増える。</summary>
    IReadOnlyList<(long Offset, long Length)> CorruptRanges { get; }

    /// <summary>検査値が合わない範囲が新しく見つかった (UI はステータスバーと InfoBar で知らせる)。</summary>
    event EventHandler<(long Offset, long Length)>? CorruptionDetected;
}

/// <summary>イメージのデータソースの共通の実装。</summary>
public abstract class ImageSourceBase : ByteSourceBase, IImageSource, View.IViewDefaultsSource
{
    private readonly List<(long Offset, long Length)> _corrupt = [];
    private readonly object _corruptLock = new();

    protected ImageSourceBase(string path, ImageInfo info)
    {
        PrimaryPath = Path.GetFullPath(path);
        Info = info;
    }

    /// <summary>開いたファイル (<c>.E01</c>、<c>.001</c> など)。</summary>
    public string PrimaryPath { get; }

    public ImageInfo Info { get; protected set; }

    public JsonObject? ViewDefaults => new() { ["separator"] = "sector" };

    public override string DisplayName => Path.GetFileName(PrimaryPath);

    public override string Identity => "image:" + Info.Format + ":" + PrimaryPath.ToUpperInvariant();

    public override long Length => Info.MediaSize;

    public override int LogicalSectorSize => Info.BytesPerSector;

    public override SourceCapabilities Capabilities => SourceCapabilities.HasGaps;

    public IReadOnlyList<(long Offset, long Length)> CorruptRanges
    {
        get
        {
            lock (_corruptLock)
            {
                return [.. _corrupt];
            }
        }
    }

    public event EventHandler<(long Offset, long Length)>? CorruptionDetected;

    /// <summary>検査値の不一致を記録する (同じ範囲は 1 回だけ知らせる)。</summary>
    protected void ReportCorruption(long offset, long length)
    {
        lock (_corruptLock)
        {
            if (_corrupt.Contains((offset, length)))
            {
                return;
            }

            _corrupt.Add((offset, length));
        }

        CorruptionDetected?.Invoke(this, (offset, length));
    }
}

/// <summary>イメージの形式が壊れている (どこで失敗したかを <see cref="Part"/> で示す。FOR-21 の「エラー」)。</summary>
public sealed class ImageFormatException(ImageFormat format, string part, string message) : IOException(message)
{
    public ImageFormat Format { get; } = format;

    /// <summary>失敗した部分 (例: "header"、"table"、"footer")。</summary>
    public string Part { get; } = part;
}

/// <summary>分割 raw・E01 のセグメントが足りない (FOR-21 の「エラー」)。<see cref="ImageOpenOptions.AllowMissingSegments"/> で開き直せる。</summary>
public sealed class MissingSegmentsException(ImageFormat format, IReadOnlyList<string> missing)
    : IOException("Image segments are missing: " + string.Join(", ", missing.Select(Path.GetFileName)))
{
    public ImageFormat Format { get; } = format;

    public IReadOnlyList<string> Missing { get; } = missing;
}

/// <summary>差分ディスクの親が見つからない。</summary>
public sealed class ParentImageNotFoundException(string child, string parentHint)
    : FileNotFoundException($"The parent disk of {Path.GetFileName(child)} was not found: {parentHint}", parentHint)
{
    public string ParentHint { get; } = parentHint;
}
