using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Fat;

/// <summary>FAT 系と exFAT を開く処理 (FOR-11)。</summary>
public sealed class FatFileSystemProvider : IFileSystemProvider
{
    public IReadOnlyCollection<FileSystemKind> Kinds { get; } = [FileSystemKind.Fat12, FileSystemKind.Fat16, FileSystemKind.Fat32, FileSystemKind.ExFat];

    public IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options) =>
        options.Kind == FileSystemKind.ExFat ? new ExFatFileSystem(volume, options) : new FatFileSystem(volume, options);
}

/// <summary>ディレクトリエントリの実装用の情報。</summary>
internal sealed record FatEntryTag(long EntryOffset, long FirstCluster, long Size, bool Deleted, int EntryCount, bool NoFatChain = false,
    long ValidDataLength = -1);

/// <summary>
/// FAT12 / FAT16 / FAT32 の解析 (FOR-11)。種類はクラスタ数で判定する (BPB の文字列は使わない)。FAT は 64 KB 単位でキャッシュして読む。
/// LFN は順序番号とチェックサムで検証して組み立てる。削除されたエントリ (E5) は先頭の 1 文字を <c>_</c> にし、LFN が残っていればそれを使う。
/// </summary>
public sealed class FatFileSystem : FileSystemBase
{
    /// <summary>1 つのディレクトリのエントリの上限 (FAT の仕様の 65,536 個)。</summary>
    public const int MaxDirectoryEntries = 65536;

    /// <summary>ディレクトリの階層の上限 (循環するディレクトリへの備え)。</summary>
    public const int MaxDepth = 64;

    private readonly FatGeometry _g;
    private readonly FatTable[] _fats;
    private readonly FileSystemOpenOptions _options;
    private readonly byte[] _boot;
    private readonly FsEntry _root;
    private readonly Dictionary<string, FsEntry> _byId = [];
    private readonly object _gate = new();
    private int _fatIndex;

    public FatFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _boot = volume.ReadBytes(0, 512);
        if (!FatGeometry.TryParse(_boot, out _g, options.OverrideSectorsPerCluster))
        {
            throw new FileSystemOpenException("The BPB is inconsistent (cluster size or FAT area).", FileSystemKind.Fat32);
        }

        FatWidth width = _g.Kind switch { FileSystemKind.Fat12 => FatWidth.Fat12, FileSystemKind.Fat16 => FatWidth.Fat16, _ => FatWidth.Fat32 };
        _fats = new FatTable[_g.FatCount];
        for (int i = 0; i < _g.FatCount; i++)
        {
            long offset = ((long)_g.ReservedSectors + i * _g.SectorsPerFat) * _g.BytesPerSector;
            _fats[i] = new FatTable(volume, offset, _g.SectorsPerFat * _g.BytesPerSector, width, _g.ClusterCount);
        }

        _fatIndex = Math.Clamp(options.FatIndex, 0, _fats.Length - 1);
        if ((long)_g.TotalSectors * _g.BytesPerSector > volume.Length)
        {
            IssueList.Add(new FsIssue(FsIssueKind.BpbInconsistent, $"total sectors {_g.TotalSectors} > volume"));
        }

        if (_g.Kind == FileSystemKind.Fat32 && _g.BackupBootSector is > 0 and < 64)
        {
            byte[] backup = volume.ReadBytes((long)_g.BackupBootSector * _g.BytesPerSector, 512);
            if (!backup.AsSpan().SequenceEqual(_boot))
            {
                IssueList.Add(new FsIssue(FsIssueKind.BootSectorBackupMismatch, $"sector {_g.BackupBootSector}"));
            }
        }

        _root = new FsEntry
        {
            Id = "fat:root",
            Name = string.Empty,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            FirstCluster = _g.Kind == FileSystemKind.Fat32 ? _g.RootCluster : null,
            Tag = new FatEntryTag(-1, _g.Kind == FileSystemKind.Fat32 ? _g.RootCluster : 0, -1, false, 0),
        };
        _byId[_root.Id] = _root;
    }

    public FatGeometry Geometry => _g;

    public override FileSystemKind Kind => _g.Kind;

    public override long ClusterSize => _g.ClusterSize;

    public override long ClusterCount => _g.ClusterCount;

    public override long FirstClusterNumber => 2;

    public override int SectorSize => _g.BytesPerSector;

    public override long FileSystemLength => _g.TotalSectors * _g.BytesPerSector;

    protected override long ClusterAreaOffset => _g.FirstDataSector * _g.BytesPerSector;

    /// <summary>FAT の数 (通常 2)。</summary>
    public int FatCount => _fats.Length;

    /// <summary>使っている FAT の番号 (0 始まり。仕様 3: 既定は 1 つ目、切り替えられる)。</summary>
    public int FatIndex => _fatIndex;

    public FatTable Fat => _fats[_fatIndex];

    public FatTable FatAt(int index) => _fats[index];

    /// <summary>使う FAT を切り替える。キャッシュしたチェーンを捨てる。</summary>
    public void SelectFat(int index)
    {
        lock (_gate)
        {
            _fatIndex = Math.Clamp(index, 0, _fats.Length - 1);
            _extents.Clear();
        }
    }

    /// <summary>1 つ目と 2 つ目の FAT の違いのあるエントリの数 (長時間処理。仕様 3)。FAT が 1 つなら 0。</summary>
    public long CountFatDifferences(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        _fats.Length < 2 ? 0 : FatTable.CountDifferences(_fats[0], _fats[1], progress, cancellationToken);

    public override IAllocationMap? Allocation => new FatAllocationMap(Fat);

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            long bps = _g.BytesPerSector;
            var list = new List<FsRegion> { new(0, _g.ReservedSectors * bps, FsRegionKind.Metadata) };
            list.Add(new FsRegion(_g.ReservedSectors * bps, _g.FatCount * _g.SectorsPerFat * bps, FsRegionKind.Metadata));
            if (_g.RootDirSectors > 0)
            {
                list.Add(new FsRegion((_g.ReservedSectors + _g.FatCount * _g.SectorsPerFat) * bps, _g.RootDirSectors * bps, FsRegionKind.Metadata));
            }

            return list;
        }
    }

    public override FileSystemSummary Summary
    {
        get
        {
            var d = new List<FsDetail>
            {
                new("OemName", Encoding.ASCII.GetString(_boot, 3, 8).TrimEnd(), "BPB"),
                new("BytesPerSector", _g.BytesPerSector.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("SectorsPerCluster", _g.SectorsPerCluster.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("ReservedSectors", _g.ReservedSectors.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("NumberOfFats", _g.FatCount.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("RootEntries", _g.RootEntries.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("TotalSectors", _g.TotalSectors.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("MediaDescriptor", $"0x{_boot[21]:X2}", "BPB"),
                new("SectorsPerFat", _g.SectorsPerFat.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("SectorsPerTrack", FatTime.ReadU16(_boot, 24).ToString(CultureInfo.InvariantCulture), "BPB"),
                new("NumberOfHeads", FatTime.ReadU16(_boot, 26).ToString(CultureInfo.InvariantCulture), "BPB"),
                new("HiddenSectors", BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(28)).ToString(CultureInfo.InvariantCulture), "BPB"),
                new("FileSystemTypeString", Encoding.ASCII.GetString(_boot, _g.Kind == FileSystemKind.Fat32 ? 82 : 54, 8).TrimEnd(), "BPB"),
                new("FirstDataSector", _g.FirstDataSector.ToString(CultureInfo.InvariantCulture)),
                new("ClusterCount", _g.ClusterCount.ToString(CultureInfo.InvariantCulture)),
                new("ActiveFat", (_fatIndex + 1).ToString(CultureInfo.InvariantCulture)),
            };
            if (_g.Kind == FileSystemKind.Fat32)
            {
                d.Add(new FsDetail("RootCluster", _g.RootCluster.ToString(CultureInfo.InvariantCulture), "BPB"));
                d.Add(new FsDetail("FsInfoSector", _g.FsInfoSector.ToString(CultureInfo.InvariantCulture), "BPB"));
                d.Add(new FsDetail("BackupBootSector", _g.BackupBootSector.ToString(CultureInfo.InvariantCulture), "BPB"));
                byte[] fsInfo = Volume.ReadBytes((long)_g.FsInfoSector * _g.BytesPerSector, 512);
                if (BinaryPrimitives.ReadUInt32LittleEndian(fsInfo) == 0x41615252)
                {
                    d.Add(new FsDetail("FsInfoFreeClusters", BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(488)).ToString(CultureInfo.InvariantCulture), "FSInfo"));
                    d.Add(new FsDetail("FsInfoNextFree", BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(492)).ToString(CultureInfo.InvariantCulture), "FSInfo"));
                }
            }

            return new FileSystemSummary
            {
                Kind = _g.Kind,
                TypeName = FileSystemDetector.DisplayName(_g.Kind),
                Label = VolumeLabel ?? _g.Label,
                Serial = $"{_g.VolumeId >> 16:X4}-{_g.VolumeId & 0xFFFF:X4}",
                ClusterSize = ClusterSize,
                TotalClusters = ClusterCount,
                Details = d,
            };
        }
    }

    private string? _label;
    private bool _labelRead;

    /// <summary>ルートディレクトリのボリュームラベルのエントリ。</summary>
    private string? VolumeLabel
    {
        get
        {
            if (!_labelRead)
            {
                _labelRead = true;
                foreach (RawEntry raw in ReadRawEntries(_root, CancellationToken.None))
                {
                    if (raw.Kind == RawKind.Label)
                    {
                        _label = raw.Name;
                        break;
                    }
                }
            }

            return _label;
        }
    }

    public override FsEntry Root => _root;

    /// <summary>キャッシュする項目の数の上限 (ファイル数の多いボリュームでメモリを使い過ぎない)。</summary>
    private const int CacheLimit = 200_000;

    private void Remember(FsEntry entry)
    {
        lock (_gate)
        {
            if (_byId.Count > CacheLimit)
            {
                _byId.Clear();
                _byId[_root.Id] = _root;
            }

            _byId[entry.Id] = entry;
        }
    }

    public override FsEntry? FindById(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    // ---- ディレクトリ ----

    private enum RawKind
    {
        File,
        Label,
        Deleted,
    }

    private sealed record RawEntry(RawKind Kind, string Name, string ShortName, long EntryOffset, byte Attributes, long FirstCluster, long Size,
        FsTimestamp Created, FsTimestamp Modified, FsTimestamp Accessed, bool LfnMismatch, int EntryCount);

    /// <summary>ディレクトリの内容の位置 (FAT12/16 のルートは固定の領域、それ以外はクラスタのチェーン)。</summary>
    private IVolumeReader DirectoryContent(FsEntry directory, out FsCorruptionKind problem)
    {
        problem = FsCorruptionKind.None;
        var tag = (FatEntryTag)directory.Tag!;
        if (ReferenceEquals(directory, _root) && _g.Kind != FileSystemKind.Fat32)
        {
            long offset = (_g.ReservedSectors + _g.FatCount * _g.SectorsPerFat) * _g.BytesPerSector;
            return Volume.Slice(offset, _g.RootEntries * 32L);
        }

        IReadOnlyList<FsExtent> extents = DirectoryExtents(tag, out problem);
        long length = extents.Count == 0 ? 0 : extents[^1].LogicalOffset + extents[^1].Length;
        return new ExtentContentReader(Volume, extents, Math.Min(length, MaxDirectoryEntries * 32L));
    }

    private IReadOnlyList<FsExtent> DirectoryExtents(FatEntryTag tag, out FsCorruptionKind problem)
    {
        problem = FsCorruptionKind.None;
        if (tag.FirstCluster < 2)
        {
            return [];
        }

        if (tag.Deleted)
        {
            // 削除されたディレクトリ: チェーンが消えているので先頭の 1 クラスタだけ読む。
            return ExtentsFromRuns([(tag.FirstCluster, 1)], ClusterSize);
        }

        long limit = (MaxDirectoryEntries * 32L + ClusterSize - 1) / ClusterSize;
        FatTable.Chain chain = Fat.Walk(tag.FirstCluster, limit);
        problem = chain.Problem;
        return ExtentsFromRuns(Coalesce(chain.Clusters), chain.Clusters.Count * ClusterSize);
    }

    private IEnumerable<RawEntry> ReadRawEntries(FsEntry directory, CancellationToken token)
    {
        IVolumeReader content = DirectoryContent(directory, out _);
        long count = Math.Min(content.Length / 32, MaxDirectoryEntries);
        byte[] block = new byte[32 * 128];
        var lfn = new SortedDictionary<int, string>();
        int lfnChecksum = -1;
        int lfnEntries = 0;
        var deletedLfn = new List<(byte Checksum, string Part)>();
        for (long i = 0; i < count; i += 128)
        {
            token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(128, count - i);
            content.Read(i * 32, block.AsSpan(0, n * 32));
            for (int k = 0; k < n; k++)
            {
                ReadOnlySpan<byte> e = block.AsSpan(k * 32, 32);
                long entryOffset = EntryVolumeOffset(content, (i + k) * 32);
                byte first = e[0];
                byte attr = e[11];
                if (first == 0x00)
                {
                    // 終わり。これより後ろは未使用 (削除されたエントリも探さない)。
                    yield break;
                }

                bool deleted = first == 0xE5;
                if (attr == 0x0F)
                {
                    string part = LfnPart(e);
                    if (deleted)
                    {
                        deletedLfn.Add((e[13], part));
                        continue;
                    }

                    int ordinal = first & 0x1F;
                    if ((first & 0x40) != 0)
                    {
                        lfn.Clear();
                        lfnChecksum = e[13];
                        lfnEntries = 0;
                    }

                    if (e[13] != lfnChecksum)
                    {
                        lfn.Clear();
                        lfnChecksum = -1;
                        continue;
                    }

                    lfn[ordinal] = part;
                    lfnEntries++;
                    continue;
                }

                if (deleted && first == 0xE5 && attr == 0 && e.Slice(1, 10).IndexOfAnyExcept((byte)0) < 0)
                {
                    lfn.Clear();
                    deletedLfn.Clear();
                    continue;
                }

                string shortName = ShortName(e, deleted);
                byte checksum = FatTime.LfnChecksum(e[..11]);
                string? longName = null;
                bool mismatch = false;
                if (!deleted && lfn.Count > 0)
                {
                    if (lfnChecksum == checksum && lfn.Keys.SequenceEqual(Enumerable.Range(1, lfn.Count)))
                    {
                        longName = string.Concat(lfn.Values);
                    }
                    else
                    {
                        mismatch = true;
                    }
                }
                else if (deleted && deletedLfn.Count > 0)
                {
                    // 削除された LFN: 順序番号は E5 で消えているので、並び (後ろから順) とチェックサムで組み立てる。短い名前の先頭の 1 文字も
                    // E5 で消えているので、チェックサムが合う先頭の文字を探す。
                    byte[] probe = e[..11].ToArray();
                    byte expected = deletedLfn[0].Checksum;
                    bool matched = false;
                    for (int c = 0x20; c < 0x100 && !matched; c++)
                    {
                        probe[0] = (byte)c;
                        matched = FatTime.LfnChecksum(probe) == expected;
                    }

                    if (matched && deletedLfn.All(p => p.Checksum == expected))
                    {
                        longName = string.Concat(Enumerable.Reverse(deletedLfn).Select(p => p.Part));
                    }
                }

                int entryCount = 1 + (deleted ? deletedLfn.Count : lfnEntries);
                lfn.Clear();
                lfnChecksum = -1;
                lfnEntries = 0;
                deletedLfn.Clear();

                if ((attr & 0x08) != 0 && (attr & 0x10) == 0)
                {
                    if (!deleted)
                    {
                        yield return new RawEntry(RawKind.Label, Encoding.ASCII.GetString(e[..11]).TrimEnd(), string.Empty, entryOffset, attr, 0, 0,
                            default, default, default, false, 1);
                    }

                    continue;
                }

                if (!deleted && e[0] == (byte)'.' && (e[1] == (byte)' ' || e[1] == (byte)'.' && e[2] == (byte)' '))
                {
                    continue;
                }

                long cluster = FatTime.ReadU16(e, 26) | (_g.Kind == FileSystemKind.Fat32 ? (long)FatTime.ReadU16(e, 20) << 16 : 0);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(e[28..]);
                TimeSpan zone = _options.LocalOffset;
                FsTimestamp created = FatTime.Decode(FatTime.ReadU16(e, 16), FatTime.ReadU16(e, 14), e[13], zone);
                FsTimestamp modified = FatTime.Decode(FatTime.ReadU16(e, 24), FatTime.ReadU16(e, 22), 0, zone);
                FsTimestamp accessed = FatTime.Decode(FatTime.ReadU16(e, 18), 0, 0, zone, hasTime: false);
                yield return new RawEntry(deleted ? RawKind.Deleted : RawKind.File, longName ?? shortName, shortName, entryOffset, attr, cluster, size,
                    created, modified, accessed, mismatch, entryCount);
            }
        }
    }

    /// <summary>ディレクトリの内容の位置に対応するボリューム上の位置。</summary>
    private static long EntryVolumeOffset(IVolumeReader content, long logical) => content switch
    {
        SubVolumeReader sub => sub.Offset + logical,
        ExtentContentReader ext => ext.VolumeOffsetOf(logical) ?? -1,
        _ => -1,
    };

    private static string LfnPart(ReadOnlySpan<byte> e)
    {
        Span<char> chars = stackalloc char[13];
        int n = 0;
        foreach ((int at, int count) in new[] { (1, 5), (14, 6), (28, 2) })
        {
            for (int i = 0; i < count; i++)
            {
                chars[n++] = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[(at + i * 2)..]);
            }
        }

        int end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    private static string ShortName(ReadOnlySpan<byte> e, bool deleted)
    {
        byte[] b = e[..11].ToArray();
        if (b[0] == 0x05)
        {
            b[0] = 0xE5;
        }

        if (deleted)
        {
            // 削除で先頭の 1 文字は分からない (FOR-11 の仕様 6)。
            b[0] = (byte)'_';
        }

        bool lowerBase = (e[12] & 0x08) != 0, lowerExt = (e[12] & 0x10) != 0;
        string name = Latin(b.AsSpan(0, 8)).TrimEnd();
        string ext = Latin(b.AsSpan(8, 3)).TrimEnd();
        if (lowerBase)
        {
            name = name.ToLowerInvariant();
        }

        if (lowerExt)
        {
            ext = ext.ToLowerInvariant();
        }

        return ext.Length > 0 ? $"{name}.{ext}" : name;
    }

    private static string Latin(ReadOnlySpan<byte> b)
    {
        var s = new StringBuilder(b.Length);
        foreach (byte c in b)
        {
            s.Append(c is >= 0x20 and < 0x7F ? (char)c : '?');
        }

        return s.ToString();
    }

    private static string AttributeText(byte attr)
    {
        var s = new StringBuilder();
        if ((attr & 0x01) != 0) s.Append('R');
        if ((attr & 0x02) != 0) s.Append('H');
        if ((attr & 0x04) != 0) s.Append('S');
        if ((attr & 0x10) != 0) s.Append('D');
        if ((attr & 0x20) != 0) s.Append('A');
        return s.ToString();
    }

    private FsEntry MakeEntry(RawEntry raw, FsEntry parent, bool deletedTree)
    {
        bool deleted = raw.Kind == RawKind.Deleted || deletedTree;
        bool dir = (raw.Attributes & 0x10) != 0;
        string path = (parent.Path.EndsWith('\\') ? parent.Path : parent.Path + "\\") + raw.Name;
        var tag = new FatEntryTag(raw.EntryOffset, raw.FirstCluster, dir ? -1 : raw.Size, deleted, raw.EntryCount);
        var entry = new FsEntry
        {
            Id = $"fat:{raw.EntryOffset:X}",
            Name = raw.Name,
            Path = deleted ? string.Empty : path,
            OriginalPath = deleted ? path : string.Empty,
            Flags = (dir ? FsEntryFlags.Directory : 0) | (raw.LfnMismatch ? FsEntryFlags.LfnMismatch : 0)
                | (deleted && !dir ? FsEntryFlags.AssumedContiguous : 0),
            Size = dir ? 0 : raw.Size,
            Created = raw.Created,
            Modified = raw.Modified,
            Accessed = raw.Accessed,
            Attributes = AttributeText(raw.Attributes),
            FileIdText = $"0x{raw.EntryOffset:X}",
            FirstCluster = raw.FirstCluster >= 2 ? raw.FirstCluster : null,
            State = deleted ? FsEntryState.Deleted : FsEntryState.Normal,
            DeletedSource = deleted ? FsDeletedSource.DirectoryEntry : FsDeletedSource.None,
            MetadataOffset = raw.EntryOffset >= 0 ? raw.EntryOffset - (raw.EntryCount - 1) * 32L : null,
            MetadataLength = raw.EntryCount * 32,
            ParentId = parent.Id,
            Tag = tag,
        };
        if (raw.LfnMismatch)
        {
            entry.CorruptionDetail = "LFN checksum";
        }

        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        entry.FragmentCount = extents.Count;
        entry.AllocatedSize = extents.Sum(x => x.ClusterCount) * ClusterSize;
        if (deleted)
        {
            entry.Recovery = RecoveryOf(extents);
        }

        Remember(entry);
        return entry;
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not FatEntryTag tag)
        {
            yield break;
        }

        bool deletedTree = tag.Deleted;
        foreach (RawEntry raw in ReadRawEntries(directory, cancellationToken))
        {
            if (raw.Kind == RawKind.File || deletedTree && raw.Kind == RawKind.Deleted)
            {
                yield return MakeEntry(raw, directory, deletedTree);
            }
        }
    }

    private readonly Dictionary<string, (IReadOnlyList<FsExtent> Extents, FsCorruptionKind Problem, string Detail)> _extents = [];

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry)
    {
        if (entry.Tag is not FatEntryTag tag)
        {
            return entry.Tag is IReadOnlyList<FsExtent> fixedExtents ? fixedExtents : [];
        }

        lock (_gate)
        {
            if (_extents.TryGetValue(entry.Id, out var cached))
            {
                Apply(entry, cached.Problem, cached.Detail);
                return cached.Extents;
            }
        }

        IReadOnlyList<FsExtent> extents;
        FsCorruptionKind problem = FsCorruptionKind.None;
        string detail = string.Empty;
        if (tag.FirstCluster < 2)
        {
            extents = [];
            if (tag.FirstCluster == 1 || tag.FirstCluster >= Fat.ClusterLimit)
            {
                problem = FsCorruptionKind.ClusterOutOfRange;
            }
        }
        else if (tag.FirstCluster >= Fat.ClusterLimit)
        {
            extents = [];
            problem = FsCorruptionKind.ClusterOutOfRange;
            detail = $"cluster {tag.FirstCluster}";
        }
        else if (tag.Deleted)
        {
            // チェーンが消えているので、先頭からファイルサイズ分の連続したクラスタと仮定する (FOR-17 の仕様 4)。
            long clusters = entry.IsDirectory ? 1 : Math.Max(1, (tag.Size + ClusterSize - 1) / ClusterSize);
            clusters = Math.Min(clusters, Fat.ClusterLimit - tag.FirstCluster);
            extents = ExtentsFromRuns([(tag.FirstCluster, clusters)], entry.IsDirectory ? clusters * ClusterSize : tag.Size);
        }
        else
        {
            long needed = entry.IsDirectory ? long.MaxValue : (tag.Size + ClusterSize - 1) / ClusterSize;
            FatTable.Chain chain = Fat.Walk(tag.FirstCluster);
            problem = chain.Problem;
            if (problem != FsCorruptionKind.None)
            {
                detail = $"cluster {chain.ProblemCluster}";
            }
            else if (!entry.IsDirectory && chain.Clusters.Count != needed)
            {
                problem = FsCorruptionKind.ChainLengthMismatch;
                detail = $"{chain.Clusters.Count} / {needed}";
            }

            long size = entry.IsDirectory ? chain.Clusters.Count * ClusterSize : Math.Min(tag.Size, chain.Clusters.Count * ClusterSize);
            extents = ExtentsFromRuns(Coalesce(chain.Clusters), size);
        }

        lock (_gate)
        {
            if (_extents.Count > CacheLimit)
            {
                _extents.Clear();
            }

            _extents[entry.Id] = (extents, problem, detail);
        }

        Apply(entry, problem, detail);
        return extents;
    }

    private static void Apply(FsEntry entry, FsCorruptionKind problem, string detail)
    {
        if (problem == FsCorruptionKind.None)
        {
            return;
        }

        entry.Corruption = problem;
        entry.CorruptionDetail = detail;
        entry.Flags |= FsEntryFlags.Corrupt;
    }

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        long length = entry.IsDirectory ? extents.Sum(e => e.Length) : extents.Count == 0 ? 0 : Math.Min(entry.Size, extents[^1].LogicalOffset + extents[^1].Length);
        if (entry.Tag is FatEntryTag { Deleted: false } && !entry.IsDirectory && entry.Corruption == FsCorruptionKind.ChainLengthMismatch)
        {
            length = Math.Min(entry.Size, extents.Sum(e => e.Length));
        }

        return new ExtentContentReader(Volume, extents, length);
    }

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is FatEntryTag tag)
        {
            d.Add(new FsDetail("EntryOffset", $"0x{tag.EntryOffset:X}"));
            d.Add(new FsDetail("DirectoryEntries", tag.EntryCount.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("FirstCluster", tag.FirstCluster.ToString(CultureInfo.InvariantCulture)));
            byte[] raw = tag.EntryOffset >= 0 ? Volume.ReadBytes(tag.EntryOffset, 32) : [];
            if (raw.Length == 32)
            {
                d.Add(new FsDetail("ShortName", ShortName(raw, tag.Deleted)));
                d.Add(new FsDetail("AttributesRaw", $"0x{raw[11]:X2}"));
                d.Add(new FsDetail("CreatedTenMs", raw[13].ToString(CultureInfo.InvariantCulture)));
            }

            if (!entry.IsDirectory || !tag.Deleted)
            {
                IReadOnlyList<FsExtent> extents = GetExtents(entry);
                long chainClusters = extents.Sum(e => e.ClusterCount);
                d.Add(new FsDetail("ChainClusters", chainClusters.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return d;
    }

    /// <summary>クラスタのチェーン (「クラスタチェーンを表示」。FOR-11)。</summary>
    public FatTable.Chain ChainOf(FsEntry entry) => entry.Tag is FatEntryTag tag ? Fat.Walk(tag.FirstCluster) : new FatTable.Chain([], FsCorruptionKind.None, 0);

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        long bps = _g.BytesPerSector;
        yield return MetadataEntry("$BootSector", 0, _g.ReservedSectors * bps);
        if (_g.Kind == FileSystemKind.Fat32)
        {
            yield return MetadataEntry("$FSInfo", _g.FsInfoSector * bps, bps);
        }

        for (int i = 0; i < _fats.Length; i++)
        {
            yield return MetadataEntry($"$FAT{i + 1}", _fats[i].Offset, _fats[i].Length);
        }

        if (_g.RootDirSectors > 0)
        {
            yield return MetadataEntry("$RootDirectory", (_g.ReservedSectors + _g.FatCount * _g.SectorsPerFat) * bps, _g.RootDirSectors * bps);
        }
    }

    private FsEntry MetadataEntry(string name, long offset, long length)
    {
        var extent = new FsExtent(0, length, offset, -1, 0);
        var entry = new FsEntry
        {
            Id = "fat:meta:" + name,
            Name = name,
            Path = "\\" + name,
            Flags = FsEntryFlags.Metadata,
            Size = length,
            AllocatedSize = length,
            MetadataOffset = offset,
            Tag = (IReadOnlyList<FsExtent>)[extent],
            FragmentCount = 1,
        };
        Remember(entry);
        return entry;
    }

    /// <summary>ディレクトリの木をたどる (階層と件数に上限)。<paramref name="includeDeleted"/> なら削除されたディレクトリの中も読む。</summary>
    private IEnumerable<FsEntry> Walk(bool includeDeleted, IProgress<double>? progress, CancellationToken token)
    {
        var queue = new Queue<(FsEntry Dir, int Depth)>();
        queue.Enqueue((_root, 0));
        var seen = new HashSet<long> { -1 };
        long done = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            bool deletedTree = dir.State == FsEntryState.Deleted;
            foreach (RawEntry raw in ReadRawEntries(dir, token))
            {
                if (raw.Kind == RawKind.Label || raw.Kind == RawKind.Deleted && !includeDeleted)
                {
                    continue;
                }

                FsEntry entry = MakeEntry(raw, dir, deletedTree);
                yield return entry;
                if (entry.IsDirectory && depth + 1 < MaxDepth && raw.FirstCluster >= 2 && seen.Add(raw.FirstCluster)
                    && (raw.Kind == RawKind.File || includeDeleted))
                {
                    queue.Enqueue((entry, depth + 1));
                }
            }

            done++;
            progress?.Report(Math.Min(0.99, done / (double)(done + queue.Count + 1)));
        }

        progress?.Report(1);
    }

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Walk(includeDeleted: true, progress, cancellationToken).Where(e => e.State == FsEntryState.Deleted);

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        EnumerateMetadata(cancellationToken).Concat(Walk(includeDeleted: false, progress, cancellationToken));
}
