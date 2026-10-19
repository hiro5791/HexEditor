using System.Globalization;

namespace HexEditor.Core.FileSystems;

/// <summary>項目の状態 (FOR-09 の仕様 3 の「状態」)。</summary>
public enum FsEntryState
{
    Normal,
    Deleted,
    Orphan,
}

/// <summary>削除された項目の復元の見込み (FOR-17 の仕様 3)。</summary>
public enum FsRecoveryStatus
{
    None,
    Good,
    PartiallyOverwritten,
    Overwritten,
    Unknown,
    Resident,
}

/// <summary>削除された項目の見つかり方 (FOR-17 の仕様 1)。</summary>
public enum FsDeletedSource
{
    None,

    /// <summary>NTFS: 使用中フラグが 0 の MFT レコード。</summary>
    MftRecord,

    /// <summary>NTFS: INDX レコードの使用領域より後ろ (インデックススラック) のエントリ。</summary>
    IndexSlack,

    /// <summary>FAT: 先頭バイト E5 のエントリ。exFAT: 使用中ビットが 0 のエントリセット。</summary>
    DirectoryEntry,

    /// <summary>ext: dtime のある inode。</summary>
    Inode,

    /// <summary>ext: ディレクトリエントリの rec_len の余り。</summary>
    DirectorySlack,
}

/// <summary>項目の印 (詳細欄・一覧の属性に出す)。</summary>
[Flags]
public enum FsEntryFlags
{
    None = 0,
    Directory = 1 << 0,

    /// <summary>代替データストリーム・リソースフォークなど、ファイルの名前付きのストリーム。</summary>
    Stream = 1 << 1,

    Compressed = 1 << 2,
    Encrypted = 1 << 3,
    Sparse = 1 << 4,

    /// <summary>データがメタデータ内にある (NTFS の常駐、ext4 のインライン)。</summary>
    Resident = 1 << 5,

    /// <summary>NTFS: $STANDARD_INFORMATION と $FILE_NAME の日時の不一致 (FOR-10 の仕様 4)。</summary>
    TimeMismatch = 1 << 6,

    /// <summary>ファイルシステムのメタデータファイル ($MFT、FAT 領域など)。</summary>
    Metadata = 1 << 7,

    /// <summary>ブラウザの仮想フォルダ (「削除されたファイル」など)。</summary>
    Virtual = 1 << 8,

    /// <summary>メタデータが壊れている (<see cref="FsEntry.Corruption"/> に理由)。</summary>
    Corrupt = 1 << 9,

    /// <summary>FAT・exFAT の削除: チェーンが消えているため、先頭から連続したクラスタと仮定して読む (FOR-17 の仕様 4)。</summary>
    AssumedContiguous = 1 << 10,

    /// <summary>データの位置が分からない (ext3/4 の削除など)。</summary>
    LocationUnknown = 1 << 11,

    /// <summary>LFN のチェックサムが合わないため短い名前を使った (FOR-11 の仕様 4)。</summary>
    LfnMismatch = 1 << 12,

    /// <summary>exFAT: Stream Extension の NoFatChain (連続したクラスタ。FOR-11 の仕様 7)。</summary>
    NoFatChain = 1 << 13,
}

/// <summary>壊れたメタデータの理由 (FOR-09 の「エラー」、FOR-10・FOR-11 の「エラー」)。文は表示の言語で作る (<c>FsCorrupt_&lt;種類&gt;</c>)。</summary>
public enum FsCorruptionKind
{
    None,
    FixupMismatch,
    BadSignature,
    ClusterOutOfRange,
    ChainLoop,
    ChainFreeCluster,
    ChainLengthMismatch,
    BadRunList,
    BadAttribute,
    BadEntrySet,
    ReadError,
    DepthLimit,
}

/// <summary>日時 1 つ (FOR-05)。<see cref="Utc"/> が null で <see cref="Invalid"/> なら「不正な日時」。</summary>
public readonly record struct FsTimestamp(DateTime? Utc, bool Invalid = false, bool ZoneUnknown = false, long Raw = 0)
{
    /// <summary>
    /// タイムゾーンを持たない日時 (FAT) のときの、記録された値そのもの (ローカル時刻として書かれている)。<see cref="Utc"/> はケースの
    /// タイムゾーンで変換した値。
    /// </summary>
    public DateTime? Recorded { get; init; }

    /// <summary>NTFS の FILETIME (100 ns 単位、1601-01-01 から)。範囲外は不正な日時。</summary>
    public static FsTimestamp FromFileTime(long fileTime)
    {
        if (fileTime == 0)
        {
            return default;
        }

        if (fileTime < 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return new FsTimestamp(null, Invalid: true, Raw: fileTime);
        }

        return new FsTimestamp(DateTime.FromFileTimeUtc(fileTime), Raw: fileTime);
    }

    public bool IsEmpty => Utc is null && !Invalid;

    public override string ToString() => Invalid ? "invalid" : Utc?.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>
/// ファイルの実データの断片 (エクステント。FOR-09 の仕様 5、FOR-15)。<see cref="VolumeOffset"/> はボリューム上のバイト位置
/// (null はスパースの区間)。常駐データは <see cref="Resident"/> で、位置はメタデータの中の位置。
/// </summary>
public sealed record FsExtent(long LogicalOffset, long Length, long? VolumeOffset, long StartCluster, long ClusterCount)
{
    /// <summary>NTFS 圧縮の圧縮単位 (Hex ビュー上は圧縮されたままの位置。FOR-10 の仕様 7)。</summary>
    public bool Compressed { get; init; }

    /// <summary>メタデータの中のデータ (NTFS の常駐など)。</summary>
    public bool Resident { get; init; }

    /// <summary>ディスク上の長さ (圧縮された単位は論理の長さより短い)。</summary>
    public long StoredLength { get; init; } = -1;

    public long DiskLength => StoredLength >= 0 ? StoredLength : Length;

    public bool IsSparse => VolumeOffset is null;
}

/// <summary>詳細欄の 1 行 (<see cref="Key"/> は項目名。<c>FsField_&lt;キー&gt;</c> の訳があれば訳を、なければそのまま表示する)。</summary>
public sealed record FsDetail(string Key, string Value, string? Group = null);

/// <summary>
/// ファイルシステムの項目 (ファイル・ディレクトリ・ストリーム・メタデータファイル・仮想フォルダ)。各ファイルシステムの実装が作り、
/// <see cref="Tag"/> に実装用の情報 (MFT 番号・ディレクトリエントリの位置など) を持つ。一覧の列は FOR-09 の仕様 3。
/// </summary>
public sealed class FsEntry
{
    /// <summary>同じファイルシステムの中で一意で、開き直しても同じ値 (<c>mft:37</c>、<c>fat:0x5A00:3</c> など)。</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>ルートからのパス (区切りは <c>\</c>。ストリームは <c>\file.txt:secret</c>)。</summary>
    public string Path { get; set; } = string.Empty;

    public FsEntryFlags Flags { get; set; }

    public bool IsDirectory => Flags.HasFlag(FsEntryFlags.Directory);

    public long Size { get; init; }

    public long AllocatedSize { get; set; }

    public FsTimestamp Created { get; init; }
    public FsTimestamp Modified { get; init; }
    public FsTimestamp Accessed { get; init; }

    /// <summary>メタデータ変更日時 (形式にある場合)。</summary>
    public FsTimestamp Changed { get; init; }

    /// <summary>属性の文字列 (<c>RHSA</c> など)。</summary>
    public string Attributes { get; init; } = string.Empty;

    /// <summary>ファイル ID の表示 (MFT 番号・inode 番号・ディレクトリエントリの位置)。</summary>
    public string FileIdText { get; init; } = string.Empty;

    /// <summary>先頭クラスタ (分からなければ null)。</summary>
    public long? FirstCluster { get; init; }

    public FsEntryState State { get; set; }

    public FsDeletedSource DeletedSource { get; init; }

    public FsRecoveryStatus Recovery { get; set; }

    /// <summary>元のパス (削除された項目。親から求めたもの。FOR-17 の仕様 2)。</summary>
    public string OriginalPath { get; set; } = string.Empty;

    public FsCorruptionKind Corruption { get; set; }

    /// <summary>破損の詳細 (位置など。訳さない)。</summary>
    public string CorruptionDetail { get; set; } = string.Empty;

    /// <summary>メタデータ (MFT レコード・ディレクトリエントリ・inode) のボリューム上の位置 (「メタデータの位置へ移動」)。</summary>
    public long? MetadataOffset { get; init; }

    public int MetadataLength { get; init; }

    /// <summary>名前付きストリームの名前 (ADS)。</summary>
    public string? StreamName { get; init; }

    /// <summary>親のディレクトリの ID (分かれば)。</summary>
    public string? ParentId { get; init; }

    /// <summary>実装用の情報。</summary>
    public object? Tag { get; init; }

    /// <summary>断片の数 (分かっていれば。null は未計算)。</summary>
    public int? FragmentCount { get; set; }

    public string Extension
    {
        get
        {
            if (IsDirectory || Flags.HasFlag(FsEntryFlags.Virtual))
            {
                return string.Empty;
            }

            string name = StreamName is null ? Name : Name;
            int dot = name.LastIndexOf('.');
            int colon = name.IndexOf(':');
            if (colon >= 0 && dot > colon)
            {
                return string.Empty;
            }

            return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : string.Empty;
        }
    }

    public override string ToString() => $"{Path} ({Id})";
}

/// <summary>ファイルシステム全体の警告 (ブラウザの上部に出す。FOR-11 の「エラー」など)。</summary>
public enum FsIssueKind
{
    BootSectorBackupMismatch,
    MftMirrorMismatch,
    BpbInconsistent,
    FatCopiesDiffer,
    FsInfoMismatch,
    ExFatBootChecksum,
    LfnChecksum,
    AllocationUnreadable,
    Encrypted,
}

public sealed record FsIssue(FsIssueKind Kind, string Detail = "");

/// <summary>ファイルシステムの概要 (FOR-09 の仕様 9)。</summary>
public sealed record FileSystemSummary
{
    public required FileSystemKind Kind { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public long ClusterSize { get; init; }
    public long TotalClusters { get; init; }

    /// <summary>空きクラスタ数 (割り当て情報から求めたもの。読めなければ null)。</summary>
    public long? FreeClusters { get; init; }

    public FsTimestamp Created { get; init; }

    /// <summary>形式ごとの項目 (BPB の全項目など)。</summary>
    public IReadOnlyList<FsDetail> Details { get; init; } = [];
}

/// <summary>Hex ビューの強調の領域 (FOR-16 の仕様 1)。</summary>
public enum FsRegionKind
{
    None,
    Metadata,
    FileData,
    RamSlack,
    DriveSlack,
    Unallocated,
    VolumeSlack,
    PartitionGap,
}

/// <summary>ボリューム上の範囲と領域の種類。</summary>
public readonly record struct FsRegion(long Offset, long Length, FsRegionKind Kind)
{
    public long End => Offset + Length;
}

/// <summary>割り当ての情報 (FAT の空きエントリ、exFAT・NTFS のビットマップ。FOR-16 の未割り当て、FOR-17 の状態)。</summary>
public interface IAllocationMap
{
    /// <summary>クラスタの数 (番号は <see cref="IFileSystem.FirstClusterNumber"/> から)。</summary>
    long ClusterCount { get; }

    /// <summary>割り当て済みか。範囲外は false。</summary>
    bool IsAllocated(long cluster);

    /// <summary>[first, first + count) の空きの連続区間 (クラスタ番号、クラスタ数)。</summary>
    IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default);

    /// <summary>空きクラスタ数 (全体の走査をするので長時間処理から呼ぶ)。</summary>
    long CountFree(CancellationToken cancellationToken = default);
}

/// <summary>
/// ファイルシステムの共通の形 (FOR-09)。NTFS・FAT・exFAT (FOR-10・FOR-11) のほか、ext・HFS+・APFS・ISO・UDF も同じ形で実装する。
/// ブラウザ・クラスタの対応付け (FOR-15)・強調と書き出し (FOR-16)・削除項目 (FOR-17)・消去 (FOR-30〜32) はこの形だけを使う。
/// <para>
/// 規則: ボリューム全体を読み込まない。ディレクトリは要求されたときに読む (遅延読み込み)。列挙は <see cref="IEnumerable{T}"/> で
/// 1 件ずつ返し、取り消しを確かめる。壊れたメタデータは例外にせず、項目に <see cref="FsEntryFlags.Corrupt"/> を付けて続ける。
/// 再帰・ループ・確保量に上限を設ける。ファイルシステムには書き込まない。
/// </para>
/// </summary>
public interface IFileSystem
{
    FileSystemKind Kind { get; }

    /// <summary>ボリュームの読み込み (オフセット 0 がファイルシステムの先頭)。</summary>
    IVolumeReader Volume { get; }

    FileSystemSummary Summary { get; }

    IReadOnlyList<FsIssue> Issues { get; }

    /// <summary>割り当ての単位のバイト数 (クラスタ・ブロック)。</summary>
    long ClusterSize { get; }

    /// <summary>割り当ての単位の数。</summary>
    long ClusterCount { get; }

    /// <summary>最初のクラスタの番号 (FAT 系は 2、NTFS・exFAT のビットマップの番号は形式に従う)。</summary>
    long FirstClusterNumber { get; }

    /// <summary>単位の呼び方 (ext・HFS+・APFS は「ブロック」。FOR-15 の仕様 2)。</summary>
    bool UsesBlocks { get; }

    /// <summary>セクタサイズ (RAM スラックの境界。FOR-16)。</summary>
    int SectorSize { get; }

    /// <summary>ファイルシステムが使う範囲のバイト数 (この後ろがボリュームスラック)。</summary>
    long FileSystemLength { get; }

    /// <summary>クラスタの先頭のボリューム上のオフセット (範囲外なら null)。</summary>
    long? ClusterOffset(long cluster);

    /// <summary>オフセットを含むクラスタの番号 (クラスタの領域の外なら null)。</summary>
    long? ClusterAt(long offset);

    FsEntry Root { get; }

    /// <summary>ディレクトリの中身 (名前付きストリームも項目として含む)。1 件ずつ返す。</summary>
    IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default);

    /// <summary>実データの断片 (論理オフセットの順)。データがなければ空。</summary>
    IReadOnlyList<FsExtent> GetExtents(FsEntry entry);

    /// <summary>ファイルの論理的な内容 (断片を順につないだもの。スパースは 0、圧縮は展開したもの)。</summary>
    IVolumeReader OpenContent(FsEntry entry);

    /// <summary>詳細欄の項目 (形式ごとの全項目)。</summary>
    IReadOnlyList<FsDetail> Describe(FsEntry entry);

    /// <summary>メタデータファイル (「$メタデータ」フォルダ)。</summary>
    IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default);

    /// <summary>削除された項目 (長時間処理。<paramref name="progress"/> は走査した量 (0〜1))。状態 (<see cref="FsEntry.Recovery"/>) を求めて返す。</summary>
    IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>親ディレクトリが見つからない項目 (「孤立したファイル」)。</summary>
    IEnumerable<FsEntry> EnumerateOrphans(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 割り当てられているすべての項目 (ファイル・ディレクトリ・ストリーム・メタデータ。名前検索・逆引き表・スラックの計算に使う。長時間処理)。
    /// </summary>
    IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>割り当ての情報 (読めなければ null。FOR-16 の強調を無効にする)。</summary>
    IAllocationMap? Allocation { get; }

    /// <summary>ファイルシステムの構造の範囲 (FOR-16 の「メタデータ」。ブートセクタ・FAT・$MFT など)。</summary>
    IReadOnlyList<FsRegion> MetadataRegions { get; }

    /// <summary>ファイル ID から項目を探す (MFT 番号へ移動などに使う。見つからなければ null)。</summary>
    FsEntry? FindById(string id);

    /// <summary>ファイルの名前付きのストリーム (NTFS の代替データストリーム、HFS+ のリソースフォーク)。なければ空。</summary>
    IEnumerable<FsEntry> GetStreams(FsEntry entry);
}
