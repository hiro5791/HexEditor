using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Fat;

/// <summary>
/// exFAT の解析 (FOR-11 の仕様 7)。メインとバックアップのブート領域のチェックサム、割り当てビットマップ・大文字変換表・ボリュームラベルの
/// エントリ、ファイルのエントリセット (File・Stream Extension・File Name) のセットのチェックサム、NoFatChain、10 ミリ秒と UTC オフセットの日時、
/// 使用中ビットが 0 の削除されたエントリセットを扱う。
/// </summary>
public sealed class ExFatFileSystem : FileSystemBase
{
    public const int MaxDirectoryBytes = 256 * 1024 * 1024;
    public const int MaxDepth = 64;
    private const int CacheLimit = 200_000;

    private readonly byte[] _boot;
    private readonly int _bps;
    private readonly long _clusterSize;
    private readonly long _fatOffset;
    private readonly long _fatLength;
    private readonly long _heapOffset;
    private readonly long _clusterCount;
    private readonly long _rootCluster;
    private readonly long _volumeLength;
    private readonly FatTable _fat;
    private readonly FileSystemOpenOptions _options;
    private readonly FsEntry _root;
    private readonly Dictionary<string, FsEntry> _byId = [];
    private readonly object _gate = new();
    private IAllocationMap? _allocation;
    private (long Cluster, long Length)? _bitmapFile;
    private (long Cluster, long Length)? _upcaseFile;
    private string _label = string.Empty;
    private bool _rootScanned;

    public ExFatFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _boot = volume.ReadBytes(0, 512);
        if (!_boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8))
        {
            throw new FileSystemOpenException("Not an exFAT volume.", FileSystemKind.ExFat);
        }

        int bpsShift = _boot[108], spcShift = _boot[109];
        if (bpsShift is < 9 or > 12 || spcShift > 25 - bpsShift)
        {
            throw new FileSystemOpenException("The boot sector is inconsistent (sector or cluster size).", FileSystemKind.ExFat);
        }

        _bps = 1 << bpsShift;
        _clusterSize = (long)_bps << spcShift;
        _volumeLength = BinaryPrimitives.ReadInt64LittleEndian(_boot.AsSpan(72)) * _bps;
        _fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(80)) * (long)_bps;
        _fatLength = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(84)) * (long)_bps;
        _heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(88)) * (long)_bps;
        _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(92));
        _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(96));
        if (_fatLength < (_clusterCount + 2) * 4 || _heapOffset <= 0 || _rootCluster < 2 || _rootCluster >= _clusterCount + 2)
        {
            throw new FileSystemOpenException("The boot sector is inconsistent (FAT area or cluster heap).", FileSystemKind.ExFat);
        }

        _fat = new FatTable(volume, _fatOffset, _fatLength, FatWidth.Fat32, _clusterCount);

        // ブート領域のチェックサム (メインとバックアップ。12 セクタずつ)。
        byte[] main = volume.ReadBytes(0, 12 * _bps);
        byte[] backup = volume.ReadBytes(12L * _bps, 12 * _bps);
        (bool mainOk, _) = FileSystemDetector.ExFatBootChecksum(main, _bps);
        (bool backupOk, _) = FileSystemDetector.ExFatBootChecksum(backup, _bps);
        MainChecksumOk = mainOk;
        BackupChecksumOk = backupOk;
        if (!mainOk || !backupOk)
        {
            IssueList.Add(new FsIssue(FsIssueKind.ExFatBootChecksum, mainOk ? "backup" : "main"));
        }
        else if (!main.AsSpan().SequenceEqual(backup))
        {
            IssueList.Add(new FsIssue(FsIssueKind.BootSectorBackupMismatch, "sector 12"));
        }

        _root = new FsEntry
        {
            Id = "exfat:root",
            Name = string.Empty,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            FirstCluster = _rootCluster,
            Tag = new FatEntryTag(-1, _rootCluster, -1, false, 0),
        };
        _byId[_root.Id] = _root;
    }

    public bool MainChecksumOk { get; }

    public bool BackupChecksumOk { get; }

    public override FileSystemKind Kind => FileSystemKind.ExFat;

    public override long ClusterSize => _clusterSize;

    public override long ClusterCount => _clusterCount;

    public override long FirstClusterNumber => 2;

    public override int SectorSize => _bps;

    public override long FileSystemLength => _volumeLength;

    protected override long ClusterAreaOffset => _heapOffset;

    public override FsEntry Root => _root;

    public override IAllocationMap? Allocation
    {
        get
        {
            ScanRoot();
            if (_allocation is null && _bitmapFile is { } bitmap && ClusterOffset(bitmap.Cluster) is long offset)
            {
                // 割り当てビットマップは連続したクラスタ (FAT のチェーンをたどる)。
                IReadOnlyList<FsExtent> extents = ExtentsFromRuns(Coalesce(_fat.Walk(bitmap.Cluster).Clusters), bitmap.Length);
                IVolumeReader content = extents.Count == 1 ? Volume.Slice(offset, bitmap.Length) : new ExtentContentReader(Volume, extents, bitmap.Length);
                _allocation = new BitmapAllocationMap(content, _clusterCount, 2);
            }

            return _allocation;
        }
    }

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            ScanRoot();
            var list = new List<FsRegion>
            {
                new(0, 24L * _bps, FsRegionKind.Metadata),
                new(_fatOffset, _fatLength, FsRegionKind.Metadata),
            };
            foreach ((long cluster, long length) in new[] { _bitmapFile, _upcaseFile }.Where(f => f is not null).Select(f => f!.Value))
            {
                foreach (FsExtent e in ExtentsFromRuns(Coalesce(_fat.Walk(cluster).Clusters), length))
                {
                    list.Add(new FsRegion(e.VolumeOffset!.Value, e.ClusterCount * _clusterSize, FsRegionKind.Metadata));
                }
            }

            return list;
        }
    }

    public override FileSystemSummary Summary
    {
        get
        {
            ScanRoot();
            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(_boot.AsSpan(100));
            ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(_boot.AsSpan(104));
            var d = new List<FsDetail>
            {
                new("PartitionOffset", BinaryPrimitives.ReadUInt64LittleEndian(_boot.AsSpan(64)).ToString(CultureInfo.InvariantCulture), "Boot"),
                new("VolumeLength", BinaryPrimitives.ReadUInt64LittleEndian(_boot.AsSpan(72)).ToString(CultureInfo.InvariantCulture), "Boot"),
                new("FatOffset", (_fatOffset / _bps).ToString(CultureInfo.InvariantCulture), "Boot"),
                new("FatLength", (_fatLength / _bps).ToString(CultureInfo.InvariantCulture), "Boot"),
                new("ClusterHeapOffset", (_heapOffset / _bps).ToString(CultureInfo.InvariantCulture), "Boot"),
                new("ClusterCount", _clusterCount.ToString(CultureInfo.InvariantCulture), "Boot"),
                new("FirstClusterOfRootDirectory", _rootCluster.ToString(CultureInfo.InvariantCulture), "Boot"),
                new("FileSystemRevision", $"{revision >> 8}.{revision & 0xFF:D2}", "Boot"),
                new("VolumeFlags", $"0x{BinaryPrimitives.ReadUInt16LittleEndian(_boot.AsSpan(106)):X4}", "Boot"),
                new("BytesPerSectorShift", _boot[108].ToString(CultureInfo.InvariantCulture), "Boot"),
                new("SectorsPerClusterShift", _boot[109].ToString(CultureInfo.InvariantCulture), "Boot"),
                new("NumberOfFats", _boot[110].ToString(CultureInfo.InvariantCulture), "Boot"),
                new("PercentInUse", _boot[112].ToString(CultureInfo.InvariantCulture), "Boot"),
                new("MainBootChecksum", MainChecksumOk ? "OK" : "NG", "Boot"),
                new("BackupBootChecksum", BackupChecksumOk ? "OK" : "NG", "Boot"),
            };
            if (_bitmapFile is { } bm)
            {
                d.Add(new FsDetail("AllocationBitmap", $"cluster {bm.Cluster}, {bm.Length} bytes", "Root"));
            }

            if (_upcaseFile is { } up)
            {
                d.Add(new FsDetail("UpcaseTable", $"cluster {up.Cluster}, {up.Length} bytes", "Root"));
            }

            d.Add(new FsDetail("VolumeLabel", _label, "Root"));
            return new FileSystemSummary
            {
                Kind = FileSystemKind.ExFat,
                TypeName = "exFAT",
                Label = _label,
                Serial = $"{serial >> 16:X4}-{serial & 0xFFFF:X4}",
                ClusterSize = _clusterSize,
                TotalClusters = _clusterCount,
                Details = d,
            };
        }
    }

    /// <summary>ルートの特別なエントリ (ビットマップ・大文字変換表・ラベル) を読む。</summary>
    private void ScanRoot()
    {
        if (_rootScanned)
        {
            return;
        }

        _rootScanned = true;
        IVolumeReader content = DirectoryContent((FatEntryTag)_root.Tag!);
        byte[] buf = new byte[32];
        for (long at = 0; at + 32 <= content.Length && at < 32 * 4096; at += 32)
        {
            content.Read(at, buf);
            switch (buf[0])
            {
                case 0x00:
                    return;
                case 0x81:
                    _bitmapFile ??= (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20)), (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(24)), long.MaxValue));
                    break;
                case 0x82:
                    _upcaseFile ??= (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(20)), (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(24)), long.MaxValue));
                    break;
                case 0x83:
                    int n = Math.Min((int)buf[1], 11);
                    _label = Encoding.Unicode.GetString(buf, 2, n * 2);
                    break;
            }
        }
    }

    private IVolumeReader DirectoryContent(FatEntryTag tag)
    {
        if (tag.FirstCluster < 2 || tag.FirstCluster >= _fat.ClusterLimit)
        {
            return new ArrayVolumeReader([]);
        }

        IReadOnlyList<FsExtent> extents;
        long length;
        if (tag.NoFatChain && tag.Size > 0)
        {
            length = Math.Min(tag.Size, MaxDirectoryBytes);
            extents = ExtentsFromRuns([(tag.FirstCluster, (length + _clusterSize - 1) / _clusterSize)], length);
        }
        else if (tag.Deleted)
        {
            length = Math.Min(Math.Max(tag.Size, _clusterSize), MaxDirectoryBytes);
            extents = ExtentsFromRuns([(tag.FirstCluster, (length + _clusterSize - 1) / _clusterSize)], length);
        }
        else
        {
            FatTable.Chain chain = _fat.Walk(tag.FirstCluster, MaxDirectoryBytes / _clusterSize);
            length = chain.Clusters.Count * _clusterSize;
            extents = ExtentsFromRuns(Coalesce(chain.Clusters), length);
        }

        return new ExtentContentReader(Volume, extents, length);
    }

    private sealed record EntrySet(bool Deleted, string Name, long EntryOffset, int Count, ushort Attributes, FsTimestamp Created, FsTimestamp Modified,
        FsTimestamp Accessed, byte StreamFlags, long ValidDataLength, long FirstCluster, long DataLength, bool ChecksumOk, ushort Checksum,
        ushort StoredChecksum);

    private IEnumerable<EntrySet> ReadSets(FatEntryTag directory, CancellationToken token)
    {
        IVolumeReader content = DirectoryContent(directory);
        long total = content.Length / 32;
        byte[] chunk = new byte[32 * 256];
        long chunkStart = -1;
        int chunkCount = 0;

        ReadOnlySpan<byte> EntryAt(long index)
        {
            if (index < chunkStart || index >= chunkStart + chunkCount)
            {
                chunkStart = index;
                chunkCount = (int)Math.Min(256, total - index);
                content.Read(index * 32, chunk.AsSpan(0, chunkCount * 32));
            }

            return chunk.AsSpan((int)(index - chunkStart) * 32, 32);
        }

        for (long i = 0; i < total; i++)
        {
            if ((i & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            byte type = EntryAt(i)[0];
            if (type == 0x00)
            {
                yield break;
            }

            if ((type & 0x7F) != 0x05)
            {
                continue;
            }

            bool deleted = (type & 0x80) == 0;
            byte[] file = EntryAt(i).ToArray();
            int secondary = file[1];
            if (secondary is < 2 or > 18 || i + secondary >= total)
            {
                continue;
            }

            var entries = new List<byte[]> { file };
            for (int k = 1; k <= secondary; k++)
            {
                entries.Add(EntryAt(i + k).ToArray());
            }

            byte streamType = entries[1][0];
            if ((streamType & 0x7F) != 0x40 || (streamType & 0x80) == 0 != deleted)
            {
                continue;
            }

            // セットのチェックサム (先頭のエントリの 2〜3 バイト目を除く)。
            ushort sum = 0;
            for (int k = 0; k < entries.Count; k++)
            {
                for (int b = 0; b < 32; b++)
                {
                    if (k == 0 && b is 2 or 3)
                    {
                        continue;
                    }

                    sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + entries[k][b]);
                }
            }

            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
            byte[] stream = entries[1];
            int nameLength = stream[3];
            var name = new StringBuilder();
            for (int k = 2; k < entries.Count && name.Length < nameLength; k++)
            {
                if ((entries[k][0] & 0x7F) != 0x41)
                {
                    break;
                }

                for (int c = 0; c < 15 && name.Length < nameLength; c++)
                {
                    name.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(entries[k].AsSpan(2 + c * 2)));
                }
            }

            TimeSpan zone = _options.LocalOffset;
            FsTimestamp created = FatTime.DecodeExFat(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)), file[20], file[22], zone);
            FsTimestamp modified = FatTime.DecodeExFat(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)), file[21], file[23], zone);
            FsTimestamp accessed = FatTime.DecodeExFat(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)), 0, file[24], zone);
            long entryOffset = content is ExtentContentReader ext ? ext.VolumeOffsetOf(i * 32) ?? -1 : -1;
            yield return new EntrySet(deleted, name.ToString(), entryOffset, entries.Count, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)),
                created, modified, accessed, stream[1], (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(8)), long.MaxValue),
                BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(20)), (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(24)), long.MaxValue),
                sum == stored, sum, stored);
            i += secondary;
        }
    }

    private static string AttributeText(ushort attr)
    {
        var s = new StringBuilder();
        if ((attr & 0x01) != 0) s.Append('R');
        if ((attr & 0x02) != 0) s.Append('H');
        if ((attr & 0x04) != 0) s.Append('S');
        if ((attr & 0x10) != 0) s.Append('D');
        if ((attr & 0x20) != 0) s.Append('A');
        return s.ToString();
    }

    private FsEntry MakeEntry(EntrySet set, FsEntry parent, bool deletedTree)
    {
        bool deleted = set.Deleted || deletedTree;
        bool dir = (set.Attributes & 0x10) != 0;
        bool noFatChain = (set.StreamFlags & 0x02) != 0;
        string path = (parent.Path.EndsWith('\\') ? parent.Path : parent.Path + "\\") + set.Name;
        var tag = new FatEntryTag(set.EntryOffset, set.FirstCluster, set.DataLength, deleted, set.Count, noFatChain, set.ValidDataLength);
        var entry = new FsEntry
        {
            Id = $"exfat:{set.EntryOffset:X}",
            Name = set.Name,
            Path = deleted ? string.Empty : path,
            OriginalPath = deleted ? path : string.Empty,
            Flags = (dir ? FsEntryFlags.Directory : 0) | (noFatChain ? FsEntryFlags.NoFatChain : 0),
            Size = dir ? 0 : set.DataLength,
            Created = set.Created,
            Modified = set.Modified,
            Accessed = set.Accessed,
            Attributes = AttributeText(set.Attributes),
            FileIdText = $"0x{set.EntryOffset:X}",
            FirstCluster = set.FirstCluster >= 2 ? set.FirstCluster : null,
            State = deleted ? FsEntryState.Deleted : FsEntryState.Normal,
            DeletedSource = deleted ? FsDeletedSource.DirectoryEntry : FsDeletedSource.None,
            MetadataOffset = set.EntryOffset >= 0 ? set.EntryOffset : null,
            MetadataLength = set.Count * 32,
            ParentId = parent.Id,
            Tag = tag,
        };
        if (!set.ChecksumOk)
        {
            entry.Flags |= FsEntryFlags.Corrupt;
            entry.Corruption = FsCorruptionKind.BadEntrySet;
            entry.CorruptionDetail = $"checksum 0x{set.StoredChecksum:X4} / 0x{set.Checksum:X4}";
        }

        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        entry.FragmentCount = extents.Count;
        entry.AllocatedSize = extents.Sum(e => e.ClusterCount) * _clusterSize;
        if (deleted)
        {
            entry.Recovery = RecoveryOf(extents);
        }

        lock (_gate)
        {
            if (_byId.Count > CacheLimit)
            {
                _byId.Clear();
                _byId[_root.Id] = _root;
            }

            _byId[entry.Id] = entry;
        }

        return entry;
    }

    public override FsEntry? FindById(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (directory.Tag is not FatEntryTag tag || !directory.IsDirectory)
        {
            yield break;
        }

        bool deletedTree = tag.Deleted;
        foreach (EntrySet set in ReadSets(tag, cancellationToken))
        {
            if (!set.Deleted || deletedTree)
            {
                yield return MakeEntry(set, directory, deletedTree);
            }
        }
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry)
    {
        if (entry.Tag is not FatEntryTag tag)
        {
            return entry.Tag is IReadOnlyList<FsExtent> fixedExtents ? fixedExtents : [];
        }

        long size = entry.IsDirectory ? Math.Max(tag.Size, 0) : tag.Size;
        if (tag.FirstCluster < 2 || size <= 0 && !entry.IsDirectory)
        {
            return [];
        }

        if (tag.FirstCluster >= _fat.ClusterLimit)
        {
            Mark(entry, FsCorruptionKind.ClusterOutOfRange, $"cluster {tag.FirstCluster}");
            return [];
        }

        long clusters = (size + _clusterSize - 1) / _clusterSize;
        if (tag.NoFatChain)
        {
            // 連続したクラスタとして扱い、FAT をたどらない (仕様 7)。
            clusters = Math.Min(clusters, _fat.ClusterLimit - tag.FirstCluster);
            return ExtentsFromRuns([(tag.FirstCluster, clusters)], size);
        }

        FatTable.Chain chain = _fat.Walk(tag.FirstCluster, entry.IsDirectory ? null : clusters);
        if (tag.Deleted && (chain.Problem != FsCorruptionKind.None || chain.Clusters.Count < clusters))
        {
            // チェーンが消えているので連続したクラスタと仮定する (FOR-17 の仕様 4)。
            entry.Flags |= FsEntryFlags.AssumedContiguous;
            clusters = Math.Min(Math.Max(1, clusters), _fat.ClusterLimit - tag.FirstCluster);
            return ExtentsFromRuns([(tag.FirstCluster, clusters)], entry.IsDirectory ? clusters * _clusterSize : size);
        }

        if (chain.Problem != FsCorruptionKind.None)
        {
            Mark(entry, chain.Problem, $"cluster {chain.ProblemCluster}");
        }
        else if (!entry.IsDirectory && chain.Clusters.Count < clusters)
        {
            Mark(entry, FsCorruptionKind.ChainLengthMismatch, $"{chain.Clusters.Count} / {clusters}");
        }

        long length = entry.IsDirectory ? chain.Clusters.Count * _clusterSize : Math.Min(size, chain.Clusters.Count * _clusterSize);
        return ExtentsFromRuns(Coalesce(chain.Clusters), length);
    }

    private static void Mark(FsEntry entry, FsCorruptionKind kind, string detail)
    {
        entry.Flags |= FsEntryFlags.Corrupt;
        entry.Corruption = kind;
        entry.CorruptionDetail = detail;
    }

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        long length = entry.IsDirectory ? extents.Sum(e => e.Length) : Math.Min(entry.Size, extents.Sum(e => e.Length));
        return new ExtentContentReader(Volume, extents, length);
    }

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is FatEntryTag tag)
        {
            d.Add(new FsDetail("EntryOffset", $"0x{tag.EntryOffset:X}"));
            d.Add(new FsDetail("SecondaryCount", (tag.EntryCount - 1).ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("FirstCluster", tag.FirstCluster.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("DataLength", tag.Size.ToString(CultureInfo.InvariantCulture), "StreamExtension"));
            d.Add(new FsDetail("ValidDataLength", tag.ValidDataLength.ToString(CultureInfo.InvariantCulture), "StreamExtension"));
            d.Add(new FsDetail("NoFatChain", tag.NoFatChain ? "1" : "0", "StreamExtension"));
        }

        return d;
    }

    /// <summary>クラスタのチェーン (「クラスタチェーンを表示」)。NoFatChain なら連続したクラスタ。</summary>
    public IReadOnlyList<long> ChainOf(FsEntry entry) => GetExtents(entry).SelectMany(e => Enumerable.Range(0, (int)Math.Min(e.ClusterCount, 1 << 20))
        .Select(i => e.StartCluster + i)).ToList();

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        ScanRoot();
        yield return Meta("$BootRegion", [new FsExtent(0, 12L * _bps, 0, -1, 0)], 12L * _bps);
        yield return Meta("$BackupBootRegion", [new FsExtent(0, 12L * _bps, 12L * _bps, -1, 0)], 12L * _bps);
        yield return Meta("$FAT", [new FsExtent(0, _fatLength, _fatOffset, -1, 0)], _fatLength);
        if (_bitmapFile is { } bm)
        {
            yield return Meta("$AllocationBitmap", ExtentsFromRuns(Coalesce(_fat.Walk(bm.Cluster).Clusters), bm.Length), bm.Length);
        }

        if (_upcaseFile is { } up)
        {
            yield return Meta("$UpcaseTable", ExtentsFromRuns(Coalesce(_fat.Walk(up.Cluster).Clusters), up.Length), up.Length);
        }
    }

    private FsEntry Meta(string name, IReadOnlyList<FsExtent> extents, long size) => new()
    {
        Id = "exfat:meta:" + name,
        Name = name,
        Path = "\\" + name,
        Flags = FsEntryFlags.Metadata,
        Size = size,
        AllocatedSize = size,
        MetadataOffset = extents.Count > 0 ? extents[0].VolumeOffset : null,
        Tag = extents,
        FragmentCount = extents.Count,
    };

    private IEnumerable<FsEntry> Walk(bool includeDeleted, IProgress<double>? progress, CancellationToken token)
    {
        var queue = new Queue<(FsEntry Dir, int Depth)>();
        queue.Enqueue((_root, 0));
        var seen = new HashSet<long> { _rootCluster };
        long done = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            var tag = (FatEntryTag)dir.Tag!;
            foreach (EntrySet set in ReadSets(tag, token))
            {
                if (set.Deleted && !includeDeleted)
                {
                    continue;
                }

                FsEntry entry = MakeEntry(set, dir, tag.Deleted);
                yield return entry;
                if (entry.IsDirectory && depth + 1 < MaxDepth && set.FirstCluster >= 2 && seen.Add(set.FirstCluster))
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
        Walk(true, progress, cancellationToken).Where(e => e.State == FsEntryState.Deleted);

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        EnumerateMetadata(cancellationToken).Concat(Walk(false, progress, cancellationToken));
}
