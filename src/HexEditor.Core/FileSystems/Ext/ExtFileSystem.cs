using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Ext;

/// <summary>ext2 / ext3 / ext4 を開く処理 (FOR-12)。</summary>
public sealed class ExtFileSystemProvider : IFileSystemProvider
{
    public IReadOnlyCollection<FileSystemKind> Kinds { get; } = [FileSystemKind.Ext];

    public IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options) => new ExtFileSystem(volume, options);
}

/// <summary>項目の実装用の情報。<see cref="EntryOffset"/> はディレクトリエントリのボリューム上の位置 (ルート・inode の走査で見つけたものは -1)。</summary>
internal sealed record ExtTag(long InodeNumber, ExtInode? Inode, long EntryOffset, bool Deleted, bool EncryptedName);

/// <summary>
/// ext2 / ext3 / ext4 の解析 (FOR-12)。スーパーブロック・グループ記述子 (32 / 64 バイト、flex_bg、meta_bg)・inode・エクステントツリーと
/// 直接・間接ブロック・インラインデータ・ディレクトリ (線形と htree の葉) を読む。削除された項目は、ディレクトリエントリの rec_len の余りに残る名前と、
/// dtime のある inode から探す。metadata_csum があればスーパーブロック・グループ記述子・inode のチェックサムを確かめて警告する。
/// ジャーナル (jbd2) は <see cref="ExtJournal"/> で一覧にするだけで、再生しない。読み込みだけを行い、ボリュームには書かない。
/// </summary>
public sealed class ExtFileSystem : FileSystemBase
{
    public const long RootInode = 2;

    /// <summary>ディレクトリの階層の上限 (循環するディレクトリへの備え)。</summary>
    public const int MaxDepth = 64;

    /// <summary>エクステントツリーの深さの上限 (ext4 の上限は 5)。</summary>
    public const int MaxExtentDepth = 5;

    /// <summary>1 つのディレクトリのエントリの上限。</summary>
    public const int MaxDirectoryEntries = 10_000_000;

    /// <summary>1 つのファイルの断片の数の上限 (壊れたエクステントツリー・間接ブロックへの備え)。</summary>
    public const int MaxExtents = 1_000_000;

    private const int CacheLimit = 200_000;

    private readonly ExtSuperblock _sb;
    private readonly CachedVolumeReader _meta;
    private readonly FileSystemOpenOptions _options;
    private readonly FsEntry _root;
    private readonly object _gate = new();
    private readonly Dictionary<long, ExtGroupDescriptor> _groups = [];
    private readonly Dictionary<string, (IReadOnlyList<FsExtent> Extents, FsCorruptionKind Problem, string Detail)> _extents = [];
    private readonly HashSet<long> _badInodeChecksums = [];
    private IReadOnlyList<FsRegion>? _metadataRegions;

    public ExtFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _meta = volume as CachedVolumeReader ?? new CachedVolumeReader(volume, 64 * 1024, 128);
        _sb = ExtSuperblock.Parse(_meta.ReadBytes(ExtSuperblock.Offset, ExtSuperblock.Size))
            ?? throw new FileSystemOpenException("The ext superblock is invalid.", FileSystemKind.Ext);
        if ((_sb.Incompat & ExtFeatures.IncompatJournalDev) != 0)
        {
            throw new FileSystemOpenException("This is an external ext journal device, not a file system.", FileSystemKind.Ext);
        }

        if (_sb.GroupCount <= 0 || _sb.GroupCount > uint.MaxValue)
        {
            throw new FileSystemOpenException("The ext group count is invalid.", FileSystemKind.Ext);
        }

        if (_sb.ChecksumValid == false)
        {
            IssueList.Add(new FsIssue(FsIssueKind.ExtSuperblockChecksum, $"0x{_sb.Checksum:X8}"));
        }

        if ((_sb.RoCompat & ExtFeatures.RoBigalloc) != 0)
        {
            IssueList.Add(new FsIssue(FsIssueKind.ExtUnsupportedFeature, "bigalloc"));
        }

        if ((long)_sb.BlocksCount * _sb.BlockSize > volume.Length)
        {
            IssueList.Add(new FsIssue(FsIssueKind.BpbInconsistent, $"blocks {_sb.BlocksCount} > volume"));
        }

        VerifyGroupDescriptors();
        _root = MakeEntry(RootInode, ReadInode(RootInode), string.Empty, "\\", null, -1, deleted: false, FsDeletedSource.None, encryptedName: false);
    }

    public ExtSuperblock Superblock => _sb;

    public override FileSystemKind Kind => FileSystemKind.Ext;

    public override long ClusterSize => _sb.BlockSize;

    public override long ClusterCount => _sb.BlocksCount;

    public override bool UsesBlocks => true;

    public override int SectorSize => _options.SectorSize is >= 512 and <= 4096 ? _options.SectorSize : 512;

    public override long FileSystemLength => _sb.BlocksCount * _sb.BlockSize;

    protected override long ClusterAreaOffset => 0;

    public override FsEntry Root => _root;

    public int InodeSize => _sb.InodeSize;

    // ---- グループ記述子 ----

    private long GroupFirstBlock(long group) => _sb.FirstDataBlock + group * _sb.BlocksPerGroup;

    /// <summary>グループにスーパーブロックの予備があるか (sparse_super は 0・1・3・5・7 の累乗、sparse_super2 は指定の 2 つ)。</summary>
    public bool GroupHasSuperblock(long group)
    {
        if (group == 0)
        {
            return true;
        }

        if ((_sb.Compat & ExtFeatures.CompatSparseSuper2) != 0)
        {
            uint b1 = ExtSuperblock.U32(_sb.Raw, 0x24C), b2 = ExtSuperblock.U32(_sb.Raw, 0x250);
            return group == b1 || group == b2;
        }

        if (!_sb.HasSparseSuper || group == 1)
        {
            return true;
        }

        return IsPower(group, 3) || IsPower(group, 5) || IsPower(group, 7);

        static bool IsPower(long n, long b)
        {
            while (n > 1 && n % b == 0)
            {
                n /= b;
            }

            return n == 1;
        }
    }

    /// <summary>グループ記述子のボリューム上の位置。</summary>
    public long GroupDescriptorOffset(long group)
    {
        long perBlock = _sb.BlockSize / _sb.DescriptorSize;
        long index = group / perBlock;
        long block = !_sb.HasMetaBg || index < _sb.FirstMetaBg
            ? _sb.FirstDataBlock + 1 + index
            : GroupFirstBlock(index * perBlock) + (GroupHasSuperblock(index * perBlock) ? 1 : 0);
        return block * _sb.BlockSize + group % perBlock * _sb.DescriptorSize;
    }

    public ExtGroupDescriptor Group(long group)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(group, out ExtGroupDescriptor g))
            {
                return g;
            }
        }

        byte[] raw = _meta.ReadBytes(GroupDescriptorOffset(group), _sb.DescriptorSize);
        ExtGroupDescriptor d = ExtGroupDescriptor.Parse(raw, _sb.Has64Bit);
        lock (_gate)
        {
            if (_groups.Count > CacheLimit)
            {
                _groups.Clear();
            }

            _groups[group] = d;
        }

        return d;
    }

    private void VerifyGroupDescriptors()
    {
        if (!_sb.HasMetadataCsum)
        {
            return;
        }

        uint seed = _sb.CsumSeed;
        long count = Math.Min(_sb.GroupCount, 65536);
        var bad = new List<long>();
        for (long g = 0; g < count; g++)
        {
            byte[] raw = _meta.ReadBytes(GroupDescriptorOffset(g), _sb.DescriptorSize);
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(30));
            if (ExtGroupDescriptor.ComputeChecksum(seed, (uint)g, raw) != stored)
            {
                bad.Add(g);
            }
        }

        if (bad.Count > 0)
        {
            IssueList.Add(new FsIssue(FsIssueKind.ExtGroupDescriptorChecksum,
                string.Join(", ", bad.Take(16).Select(g => g.ToString(CultureInfo.InvariantCulture))) + (bad.Count > 16 ? ", …" : string.Empty)));
        }
    }

    // ---- inode ----

    /// <summary>inode のボリューム上の位置 (範囲外なら null。「inode へ移動」)。</summary>
    public long? InodeOffset(long number)
    {
        if (number < 1 || number > _sb.InodesCount)
        {
            return null;
        }

        long group = (number - 1) / _sb.InodesPerGroup;
        long index = (number - 1) % _sb.InodesPerGroup;
        if (group >= _sb.GroupCount)
        {
            return null;
        }

        long table = Group(group).InodeTable;
        if (table <= 0 || table >= _sb.BlocksCount)
        {
            return null;
        }

        return table * _sb.BlockSize + index * _sb.InodeSize;
    }

    /// <summary>inode を読む (範囲外・読めないなら null)。</summary>
    public ExtInode? ReadInode(long number)
    {
        if (InodeOffset(number) is not long offset)
        {
            return null;
        }

        byte[] raw = _meta.ReadBytes(offset, _sb.InodeSize, out bool complete);
        if (!complete)
        {
            return null;
        }

        ExtInode inode = ExtInode.Parse(number, offset, raw);
        if (_sb.HasMetadataCsum && !inode.IsEmpty && !inode.ChecksumMatches(_sb.CsumSeed))
        {
            lock (_gate)
            {
                _badInodeChecksums.Add(number);
            }
        }

        return inode;
    }

    private bool ChecksumBad(long number)
    {
        lock (_gate)
        {
            return _badInodeChecksums.Contains(number);
        }
    }

    // ---- ブロックの対応 ----

    private readonly record struct Run(long Logical, long Physical, long Count, bool Uninitialized);

    private sealed class MapState
    {
        public FsCorruptionKind Problem;
        public string Detail = string.Empty;
        public int Runs;
        public int Nodes;
        public int Depth;
    }

    /// <summary>inode のデータのブロックの並び (エクステントまたは間接ブロック)。壊れた部分は飛ばして <paramref name="state"/> に理由を記録する。</summary>
    private List<Run> BlockRuns(ExtInode inode, MapState state)
    {
        var runs = new List<Run>();
        long blocksNeeded = (inode.Size + _sb.BlockSize - 1) / _sb.BlockSize;
        if (inode.UsesExtents)
        {
            WalkExtents(inode.Block.ToArray(), 0, runs, state);
        }
        else if (!inode.HasInlineData)
        {
            if (inode.IsSymlink && inode.Size < 60 && inode.BlocksRaw == 0)
            {
                return runs;
            }

            WalkIndirect(inode.Block, blocksNeeded, runs, state);
        }

        runs.Sort((a, b) => a.Logical.CompareTo(b.Logical));
        return runs;
    }

    private void WalkExtents(byte[] node, int depth, List<Run> runs, MapState state)
    {
        if (depth > MaxExtentDepth || state.Nodes++ > 100_000)
        {
            state.Problem = FsCorruptionKind.DepthLimit;
            return;
        }

        ReadOnlySpan<byte> n = node;
        if (n.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(n) != 0xF30A)
        {
            state.Problem = FsCorruptionKind.BadSignature;
            state.Detail = "extent header";
            return;
        }

        int entries = BinaryPrimitives.ReadUInt16LittleEndian(n[2..]);
        int max = BinaryPrimitives.ReadUInt16LittleEndian(n[4..]);
        int level = BinaryPrimitives.ReadUInt16LittleEndian(n[6..]);
        if (entries > max || 12 + entries * 12 > n.Length || level > MaxExtentDepth)
        {
            state.Problem = FsCorruptionKind.BadSignature;
            state.Detail = "extent header";
            entries = Math.Min(entries, (n.Length - 12) / 12);
        }

        state.Depth = Math.Max(state.Depth, level);
        for (int i = 0; i < entries; i++)
        {
            ReadOnlySpan<byte> e = n.Slice(12 + i * 12, 12);
            uint logical = BinaryPrimitives.ReadUInt32LittleEndian(e);
            if (level == 0)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                bool uninit = len > 32768;
                if (uninit)
                {
                    len -= 32768;
                }

                long start = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                if (len == 0)
                {
                    continue;
                }

                if (start + len > _sb.BlocksCount || start < _sb.FirstDataBlock)
                {
                    state.Problem = FsCorruptionKind.ClusterOutOfRange;
                    state.Detail = $"block {start}";
                    continue;
                }

                if (state.Runs++ >= MaxExtents)
                {
                    state.Problem = FsCorruptionKind.DepthLimit;
                    return;
                }

                runs.Add(new Run(logical, start, len, uninit));
            }
            else
            {
                long leaf = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                if (leaf >= _sb.BlocksCount || leaf < _sb.FirstDataBlock)
                {
                    state.Problem = FsCorruptionKind.ClusterOutOfRange;
                    state.Detail = $"block {leaf}";
                    continue;
                }

                byte[] child = _meta.ReadBytes(leaf * _sb.BlockSize, _sb.BlockSize, out bool complete);
                if (!complete)
                {
                    state.Problem = FsCorruptionKind.ReadError;
                    state.Detail = $"block {leaf}";
                    continue;
                }

                WalkExtents(child, depth + 1, runs, state);
            }
        }
    }

    private void WalkIndirect(ReadOnlySpan<byte> block, long blocksNeeded, List<Run> runs, MapState state)
    {
        long logical = 0;
        long perBlock = _sb.BlockSize / 4;
        for (int i = 0; i < 12 && logical < blocksNeeded; i++, logical++)
        {
            Add(BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]), logical);
        }

        for (int level = 1; level <= 3 && logical < blocksNeeded; level++)
        {
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(block[((11 + level) * 4)..]);
            long span = 1;
            for (int k = 0; k < level; k++)
            {
                span *= perBlock;
            }

            if (pointer == 0)
            {
                logical += span;
                continue;
            }

            Indirect(pointer, level, ref logical);
        }

        void Indirect(uint pointer, int level, ref long next)
        {
            long span = 1;
            for (int k = 1; k < level; k++)
            {
                span *= perBlock;
            }

            if (pointer >= _sb.BlocksCount || pointer < _sb.FirstDataBlock)
            {
                state.Problem = FsCorruptionKind.ClusterOutOfRange;
                state.Detail = $"block {pointer}";
                next += span * perBlock;
                return;
            }

            if (state.Nodes++ > 1_000_000)
            {
                state.Problem = FsCorruptionKind.DepthLimit;
                next = long.MaxValue / 2;
                return;
            }

            byte[] data = _meta.ReadBytes((long)pointer * _sb.BlockSize, _sb.BlockSize);
            for (int i = 0; i < perBlock && next < blocksNeeded; i++)
            {
                uint p = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
                if (level == 1)
                {
                    Add(p, next);
                    next++;
                }
                else if (p == 0)
                {
                    next += span;
                }
                else
                {
                    Indirect(p, level - 1, ref next);
                }
            }
        }

        void Add(uint physical, long at)
        {
            if (physical == 0)
            {
                return;
            }

            if (physical >= _sb.BlocksCount || physical < _sb.FirstDataBlock)
            {
                state.Problem = FsCorruptionKind.ClusterOutOfRange;
                state.Detail = $"block {physical}";
                return;
            }

            if (runs.Count > 0 && runs[^1] is var last && last.Logical + last.Count == at && last.Physical + last.Count == physical)
            {
                runs[^1] = last with { Count = last.Count + 1 };
                return;
            }

            if (state.Runs++ >= MaxExtents)
            {
                state.Problem = FsCorruptionKind.DepthLimit;
                return;
            }

            runs.Add(new Run(at, physical, 1, false));
        }
    }

    /// <summary>インラインデータ (i_block の 60 バイトと、拡張属性 system.data の値。FOR-12 の仕様 3)。</summary>
    public byte[] InlineData(ExtInode inode)
    {
        byte[] block = inode.Block.ToArray();
        byte[] extra = InodeXattrs(inode).FirstOrDefault(x => x.Name == "system.data").Value ?? [];
        return [.. block, .. extra];
    }

    private IReadOnlyList<FsExtent> ExtentsOf(ExtInode inode, long size, out FsCorruptionKind problem, out string detail, out int depth)
    {
        var state = new MapState();
        depth = 0;
        if (inode.HasInlineData || inode.IsSymlink && inode.Size < 60 && inode.BlocksRaw == 0 && !inode.UsesExtents)
        {
            problem = FsCorruptionKind.None;
            detail = string.Empty;
            return [new FsExtent(0, inode.HasInlineData ? size : Math.Min(size, inode.Size), inode.Offset + 0x28, -1, 0) { Resident = true }];
        }

        List<Run> runs = BlockRuns(inode, state);
        problem = state.Problem;
        detail = state.Detail;
        depth = state.Depth;
        var list = new List<FsExtent>(runs.Count);
        long bs = _sb.BlockSize;
        foreach (Run r in runs)
        {
            long logical = r.Logical * bs;
            if (logical >= size)
            {
                break;
            }

            long length = Math.Min(r.Count * bs, size - logical);
            list.Add(new FsExtent(logical, length, r.Uninitialized ? null : r.Physical * bs, r.Physical, r.Count));
        }

        return list;
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry)
    {
        if (entry.Tag is not ExtTag tag)
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

        IReadOnlyList<FsExtent> extents = [];
        FsCorruptionKind problem = FsCorruptionKind.None;
        string detail = string.Empty;
        if (tag.Inode is { } inode && (!tag.Deleted || inode.IsDeleted))
        {
            extents = ExtentsOf(inode, entry.IsDirectory ? Math.Max(inode.Size, 0) : entry.Size, out problem, out detail, out _);
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
        if (entry.Tag is ExtTag { Inode: { } inode } && extents.Count == 1 && extents[0].Resident)
        {
            byte[] data = inode.HasInlineData ? InlineData(inode) : inode.Block.ToArray();
            long length = Math.Min(entry.Size, data.Length);
            return new ExtentContentReader(Volume, extents, length, data);
        }

        long size = entry.IsDirectory ? extents.Sum(e => e.Length) : entry.Size;
        if (entry.Flags.HasFlag(FsEntryFlags.LocationUnknown))
        {
            size = 0;
        }

        return new ExtentContentReader(Volume, extents, size);
    }

    // ---- 項目 ----

    private readonly Dictionary<string, FsEntry> _byId = [];

    private void Remember(FsEntry entry)
    {
        lock (_gate)
        {
            if (_byId.Count > CacheLimit)
            {
                _byId.Clear();
            }

            _byId[entry.Id] = entry;
        }
    }

    private FsEntry MakeEntry(long number, ExtInode? inode, string name, string path, FsEntry? parent, long entryOffset, bool deleted,
        FsDeletedSource source, bool encryptedName, string? id = null)
    {
        bool dir = inode?.IsDirectory ?? false;

        // 削除された項目: inode が削除されたまま (再利用されていない) なら位置情報を使う。ext3/4 では位置情報が消えていることが多い。
        bool locationUnknown = deleted && (inode is null || !inode.IsDeleted);
        long size = dir ? 0 : inode?.Size ?? 0;
        IReadOnlyList<FsExtent> extents = [];
        FsCorruptionKind problem = FsCorruptionKind.None;
        string detail = string.Empty;
        if (!locationUnknown && inode is not null)
        {
            extents = ExtentsOf(inode, dir ? Math.Max(inode.Size, 0) : size, out problem, out detail, out _);
            locationUnknown = deleted && extents.Count == 0;
        }

        FsEntryFlags flags = (dir ? FsEntryFlags.Directory : 0)
            | (inode?.IsEncrypted == true || encryptedName ? FsEntryFlags.Encrypted : 0)
            | (inode?.HasInlineData == true ? FsEntryFlags.Resident : 0)
            | (locationUnknown ? FsEntryFlags.LocationUnknown : 0);
        FsExtent? first = extents.FirstOrDefault(e => !e.Resident);
        var entry = new FsEntry
        {
            Id = id ?? (entryOffset >= 0 ? $"ext:{number}:{entryOffset:X}" : $"ext:{number}"),
            Name = name,
            Path = deleted ? string.Empty : path,
            OriginalPath = deleted ? path : string.Empty,
            Flags = flags,
            Size = size,
            Created = inode?.Created ?? default,
            Modified = inode?.Modified ?? default,
            Accessed = inode?.Accessed ?? default,
            Changed = inode?.Changed ?? default,
            Attributes = inode is null ? string.Empty : ExtInode.ModeText(inode.Mode),
            FileIdText = number.ToString(CultureInfo.InvariantCulture),
            FirstCluster = first?.StartCluster,
            State = deleted ? FsEntryState.Deleted : FsEntryState.Normal,
            DeletedSource = source,
            MetadataOffset = inode?.Offset ?? (entryOffset >= 0 ? entryOffset : null),
            MetadataLength = inode is null ? 8 : _sb.InodeSize,
            ParentId = parent?.Id,
            Tag = new ExtTag(number, locationUnknown ? null : inode, entryOffset, deleted, encryptedName),
            FragmentCount = extents.Count,
            AllocatedSize = extents.Where(e => !e.Resident).Sum(e => e.ClusterCount) * ClusterSize,
        };
        lock (_gate)
        {
            if (_extents.Count > CacheLimit)
            {
                _extents.Clear();
            }

            _extents[entry.Id] = (extents, problem, detail);
        }

        Apply(entry, problem, detail);
        if (inode is not null && ChecksumBad(number) && !deleted)
        {
            Apply(entry, FsCorruptionKind.ChecksumMismatch, "inode");
        }

        if (deleted)
        {
            entry.Recovery = locationUnknown ? FsRecoveryStatus.Unknown : RecoveryOf(extents);
        }

        Remember(entry);
        return entry;
    }

    public override FsEntry? FindById(string id)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out FsEntry? e))
            {
                return e;
            }
        }

        // ext:<inode> (inode 番号から作る。「inode へ移動」)。
        string[] parts = id.Split(':');
        if (parts.Length >= 2 && parts[0] == "ext" && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long number)
            && ReadInode(number) is { IsEmpty: false } inode)
        {
            return MakeEntry(number, inode, $"<{number}>", string.Empty, null, -1, inode.IsDeleted, inode.IsDeleted ? FsDeletedSource.Inode : FsDeletedSource.None,
                encryptedName: false, id: $"ext:{number}");
        }

        return null;
    }

    /// <summary>inode 番号から項目を作る (「inode を表示」「inode へ移動」)。</summary>
    public FsEntry? EntryForInode(long number) => FindById($"ext:{number}");

    // ---- ディレクトリ ----

    private sealed record RawDirEntry(long Inode, string Name, int FileType, long Offset, bool Deleted);

    /// <summary>
    /// ディレクトリの内容をブロックごとに読み、エントリを返す。<paramref name="includeSlack"/> なら rec_len の余りに残った削除されたエントリも返す
    /// (htree の索引ブロックは余りを読まない)。
    /// </summary>
    private IEnumerable<RawDirEntry> ReadDirectory(FsEntry directory, bool includeSlack, CancellationToken token)
    {
        if (directory.Tag is not ExtTag { Inode: { } inode })
        {
            yield break;
        }

        bool htree = (inode.Flags & ExtInode.FlagIndex) != 0;
        int count = 0;
        if (inode.HasInlineData)
        {
            byte[] data = InlineData(inode);
            long baseOffset = inode.Offset + 0x28;

            // 先頭の 4 バイトは親の inode 番号。
            uint parentNumber = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (parentNumber != 0)
            {
                // ".." に相当する (一覧には出さない)。
            }

            foreach (RawDirEntry e in ParseBlock(data.AsSpan(4, 56).ToArray(), baseOffset + 4, includeSlack, skipSlack: false))
            {
                yield return e;
            }

            if (data.Length > 60)
            {
                foreach (RawDirEntry e in ParseBlock(data.AsSpan(60).ToArray(), -1, includeSlack, skipSlack: false))
                {
                    yield return e;
                }
            }

            yield break;
        }

        IReadOnlyList<FsExtent> extents = GetExtents(directory);
        long bs = _sb.BlockSize;
        long blockIndex = 0;
        foreach (FsExtent extent in extents)
        {
            if (extent.VolumeOffset is not long start)
            {
                blockIndex += (extent.Length + bs - 1) / bs;
                continue;
            }

            long blocks = (extent.Length + bs - 1) / bs;
            for (long b = 0; b < blocks; b++, blockIndex++)
            {
                token.ThrowIfCancellationRequested();
                long offset = start + b * bs;
                byte[] block = _meta.ReadBytes(offset, (int)bs);

                // htree: 0 番目のブロックの dx_root と、索引ノード (inode 0 で rec_len がブロック全体) の余りは名前ではない。
                bool indexBlock = htree && (blockIndex == 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(block) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4)) == bs);
                foreach (RawDirEntry e in ParseBlock(block, offset, includeSlack, skipSlack: indexBlock))
                {
                    if (++count > MaxDirectoryEntries)
                    {
                        yield break;
                    }

                    yield return e;
                }
            }
        }
    }

    private IEnumerable<RawDirEntry> ParseBlock(byte[] block, long volumeOffset, bool includeSlack, bool skipSlack)
    {
        var list = new List<RawDirEntry>();
        int pos = 0;
        while (pos + 8 <= block.Length)
        {
            uint ino = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(pos));
            int recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(pos + 4));
            int nameLen = block[pos + 6];
            int type = _sb.HasFiletype ? block[pos + 7] : 0;
            if (!_sb.HasFiletype)
            {
                nameLen |= block[pos + 7] << 8;
            }

            if (recLen == 0 && block.Length >= 65536)
            {
                recLen = 65536;
            }

            if (recLen < 8 || recLen % 4 != 0 || pos + recLen > block.Length || 8 + nameLen > recLen)
            {
                // 壊れたエントリ: このブロックの残りは読まない。
                break;
            }

            long at = volumeOffset >= 0 ? volumeOffset + pos : -1;
            if (nameLen > 0 && type != 0xDE)
            {
                string name = ExtNames.Decode(block.AsSpan(pos + 8, nameLen));
                bool dot = name is "." or "..";
                if (ino != 0 && !dot)
                {
                    list.Add(new RawDirEntry(ino, name, type, at, Deleted: false));
                }
                else if (ino == 0 && !dot && includeSlack && !skipSlack && ValidName(block.AsSpan(pos + 8, nameLen)))
                {
                    // ブロックの先頭のエントリの削除 (inode が 0 になり、名前は残る)。
                    list.Add(new RawDirEntry(0, name, type, at, Deleted: true));
                }
            }

            int actual = (8 + nameLen + 3) & ~3;
            if (includeSlack && !skipSlack && recLen - actual >= 12)
            {
                ScanSlack(block, pos + actual, pos + recLen, volumeOffset, list);
            }

            pos += recLen;
        }

        return list;
    }

    /// <summary>rec_len の余りに残った削除されたエントリ (FOR-12 の仕様 4)。</summary>
    private void ScanSlack(byte[] block, int from, int end, long volumeOffset, List<RawDirEntry> list)
    {
        int q = from;
        int guard = 0;
        while (q + 8 < end && guard++ < 1024)
        {
            uint ino = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(q));
            int recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(q + 4));
            int nameLen = block[q + 6];
            int type = block[q + 7];
            bool plausible = nameLen > 0 && q + 8 + nameLen <= end && ino <= _sb.InodesCount && (!_sb.HasFiletype || type <= 7)
                && recLen >= 8 + nameLen && recLen % 4 == 0 && ValidName(block.AsSpan(q + 8, nameLen));
            if (!plausible)
            {
                q += 4;
                continue;
            }

            string name = ExtNames.Decode(block.AsSpan(q + 8, nameLen));
            if (name is not "." and not "..")
            {
                list.Add(new RawDirEntry(ino, name, type, volumeOffset >= 0 ? volumeOffset + q : -1, Deleted: true));
            }

            int actual = (8 + nameLen + 3) & ~3;
            q += q + recLen <= end && recLen > actual ? actual : Math.Max(actual, 4);
        }
    }

    private static bool ValidName(ReadOnlySpan<byte> name) => name.IndexOfAny((byte)0, (byte)'/') < 0;

    private static string Combine(FsEntry parent, string name)
    {
        string basePath = parent.Path.Length > 0 ? parent.Path : parent.OriginalPath;
        return (basePath.EndsWith('\\') ? basePath : basePath + "\\") + name;
    }

    private FsEntry FromRaw(RawDirEntry raw, FsEntry parent)
    {
        bool encryptedName = parent.Flags.HasFlag(FsEntryFlags.Encrypted);
        string path = Combine(parent, raw.Name);
        if (!raw.Deleted)
        {
            ExtInode? inode = ReadInode(raw.Inode);
            FsEntry e = MakeEntry(raw.Inode, inode, raw.Name, path, parent, raw.Offset, deleted: false, FsDeletedSource.None, encryptedName);
            if (inode is null)
            {
                Apply(e, FsCorruptionKind.ClusterOutOfRange, $"inode {raw.Inode}");
            }

            return e;
        }

        ExtInode? old = raw.Inode != 0 ? ReadInode(raw.Inode) : null;
        bool usable = old is { IsDeleted: true };
        FsEntry deleted = MakeEntry(raw.Inode, usable ? old : null, raw.Name, path, parent, raw.Offset, deleted: true, FsDeletedSource.DirectorySlack,
            encryptedName, id: $"ext:slack:{raw.Offset:X}:{raw.Inode}");
        return deleted;
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not ExtTag tag)
        {
            yield break;
        }

        foreach (RawDirEntry raw in ReadDirectory(directory, includeSlack: tag.Deleted, cancellationToken))
        {
            yield return FromRaw(raw, directory);
        }
    }

    // ---- 走査 ----

    private IEnumerable<FsEntry> Walk(bool includeDeleted, HashSet<long>? seenInodes, IProgress<double>? progress, CancellationToken token)
    {
        var queue = new Queue<(FsEntry Dir, int Depth)>();
        queue.Enqueue((_root, 0));
        var visited = new HashSet<long> { RootInode };
        seenInodes?.Add(RootInode);
        long done = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (RawDirEntry raw in ReadDirectory(dir, includeDeleted, token))
            {
                if (raw.Deleted && !includeDeleted)
                {
                    continue;
                }

                FsEntry entry = FromRaw(raw, dir);
                if (!raw.Deleted)
                {
                    seenInodes?.Add(raw.Inode);
                }

                yield return entry;
                if (!raw.Deleted && entry.IsDirectory && depth + 1 < MaxDepth && visited.Add(raw.Inode))
                {
                    queue.Enqueue((entry, depth + 1));
                }
            }

            done++;
            progress?.Report(Math.Min(0.5, done / (double)(done + queue.Count + 1) / 2));
        }
    }

    /// <summary>inode テーブルの走査 (長時間処理)。<paramref name="select"/> が true を返した inode を返す。</summary>
    private IEnumerable<ExtInode> ScanInodes(Func<ExtInode, bool> select, IProgress<double>? progress, double progressBase, CancellationToken token)
    {
        long groups = _sb.GroupCount;
        int perChunk = Math.Max(1, 64 * 1024 / _sb.InodeSize);
        for (long g = 0; g < groups; g++)
        {
            token.ThrowIfCancellationRequested();
            ExtGroupDescriptor d = Group(g);
            if ((d.Flags & ExtGroupDescriptor.InodeUninit) != 0 || d.InodeTable <= 0 || d.InodeTable >= _sb.BlocksCount)
            {
                continue;
            }

            long used = _sb.InodesPerGroup - (_sb.HasMetadataCsum || (_sb.RoCompat & ExtFeatures.RoGdtCsum) != 0 ? Math.Min(d.ItableUnused, _sb.InodesPerGroup) : 0);
            for (long i = 0; i < used; i += perChunk)
            {
                int n = (int)Math.Min(perChunk, used - i);
                long offset = d.InodeTable * _sb.BlockSize + i * _sb.InodeSize;
                byte[] chunk = Volume.ReadBytes(offset, n * _sb.InodeSize);
                for (int k = 0; k < n; k++)
                {
                    long number = g * _sb.InodesPerGroup + i + k + 1;
                    if (number > _sb.InodesCount)
                    {
                        break;
                    }

                    byte[] raw = chunk.AsSpan(k * _sb.InodeSize, _sb.InodeSize).ToArray();
                    ExtInode inode = ExtInode.Parse(number, offset + (long)k * _sb.InodeSize, raw);
                    if (select(inode))
                    {
                        yield return inode;
                    }
                }
            }

            progress?.Report(progressBase + (1 - progressBase) * (g + 1) / groups);
        }
    }

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // 1. ディレクトリエントリの余り (名前が分かる)。
        var named = new HashSet<long>();
        foreach (FsEntry e in Walk(includeDeleted: true, null, progress, cancellationToken))
        {
            if (e.State == FsEntryState.Deleted)
            {
                if (e.Tag is ExtTag { InodeNumber: > 0 } t)
                {
                    named.Add(t.InodeNumber);
                }

                yield return e;
            }
        }

        // 2. dtime のある inode (名前は分からない)。
        foreach (ExtInode inode in ScanInodes(i => i.IsDeleted && !named.Contains(i.Number) && i.Number >= _sb.FirstInode, progress, 0.5, cancellationToken))
        {
            yield return MakeEntry(inode.Number, inode, $"<{inode.Number}>", $"\\<{inode.Number}>", null, -1, deleted: true, FsDeletedSource.Inode,
                encryptedName: false, id: $"ext:del:{inode.Number}");
        }

        progress?.Report(1);
    }

    public override IEnumerable<FsEntry> EnumerateOrphans(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<long>();
        foreach (FsEntry _ in Walk(includeDeleted: false, seen, progress, cancellationToken))
        {
        }

        foreach (ExtInode inode in ScanInodes(i => i.Links > 0 && i.DtimeRaw == 0 && i.Mode != 0 && i.Number >= _sb.FirstInode && !seen.Contains(i.Number),
            progress, 0.5, cancellationToken))
        {
            FsEntry e = MakeEntry(inode.Number, inode, $"<{inode.Number}>", $"\\<{inode.Number}>", null, -1, deleted: false, FsDeletedSource.None,
                encryptedName: false, id: $"ext:orphan:{inode.Number}");
            e.State = FsEntryState.Orphan;
            yield return e;
        }

        progress?.Report(1);
    }

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (FsEntry m in EnumerateMetadata(cancellationToken))
        {
            yield return m;
        }

        foreach (FsEntry e in Walk(includeDeleted: false, null, progress, cancellationToken))
        {
            yield return e;
        }

        progress?.Report(1);
    }

    // ---- メタデータ ----

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        yield return Fixed("$Superblock", ExtSuperblock.Offset, ExtSuperblock.Size);
        long gdtBytes = Math.Min(_sb.GroupCount * _sb.DescriptorSize, int.MaxValue);
        yield return Fixed("$GroupDescriptors", GroupDescriptorOffset(0), gdtBytes);
        if (_sb.HasJournal && _sb.JournalInode != 0 && ReadInode(_sb.JournalInode) is { IsEmpty: false } journal)
        {
            yield return Mark(MakeEntry(_sb.JournalInode, journal, "$Journal", "\\$Journal", null, -1, deleted: false, FsDeletedSource.None, false,
                id: "ext:meta:journal"));
        }

        foreach ((long number, string name) in new[] { (7L, "$ResizeInode"), (5L, "$BootLoader") })
        {
            if (ReadInode(number) is { IsEmpty: false, Size: > 0 } inode && number < _sb.FirstInode)
            {
                yield return Mark(MakeEntry(number, inode, name, "\\" + name, null, -1, false, FsDeletedSource.None, false, id: "ext:meta:" + number));
            }
        }

        static FsEntry Mark(FsEntry e)
        {
            e.Flags |= FsEntryFlags.Metadata;
            return e;
        }
    }

    private FsEntry Fixed(string name, long offset, long length)
    {
        var extent = new FsExtent(0, length, offset, offset / _sb.BlockSize, (length + _sb.BlockSize - 1) / _sb.BlockSize);
        var entry = new FsEntry
        {
            Id = "ext:meta:" + name,
            Name = name,
            Path = "\\" + name,
            Flags = FsEntryFlags.Metadata,
            Size = length,
            AllocatedSize = length,
            MetadataOffset = offset,
            MetadataLength = (int)Math.Min(length, 4096),
            Tag = (IReadOnlyList<FsExtent>)[extent],
            FragmentCount = 1,
        };
        Remember(entry);
        return entry;
    }

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            if (_metadataRegions is { } cached)
            {
                return cached;
            }

            long bs = _sb.BlockSize;
            var list = new List<FsRegion> { new(0, Math.Max(2048, (_sb.FirstDataBlock + 1) * bs), FsRegionKind.Metadata) };
            long gdtBlocks = (_sb.GroupCount * _sb.DescriptorSize + bs - 1) / bs;
            long groups = Math.Min(_sb.GroupCount, 1_000_000);
            long inodeTableBlocks = ((long)_sb.InodesPerGroup * _sb.InodeSize + bs - 1) / bs;
            for (long g = 0; g < groups; g++)
            {
                if (g > 0 && GroupHasSuperblock(g))
                {
                    long n = 1 + (_sb.HasMetaBg ? 0 : gdtBlocks + _sb.ReservedGdtBlocks);
                    list.Add(new FsRegion(GroupFirstBlock(g) * bs, n * bs, FsRegionKind.Metadata));
                }
                else if (g == 0 && !_sb.HasMetaBg)
                {
                    list.Add(new FsRegion((_sb.FirstDataBlock + 1) * bs, (gdtBlocks + _sb.ReservedGdtBlocks) * bs, FsRegionKind.Metadata));
                }

                ExtGroupDescriptor d = Group(g);
                foreach ((long block, long count) in new[] { (d.BlockBitmap, 1L), (d.InodeBitmap, 1L), (d.InodeTable, inodeTableBlocks) })
                {
                    if (block > 0 && block + count <= _sb.BlocksCount)
                    {
                        list.Add(new FsRegion(block * bs, count * bs, FsRegionKind.Metadata));
                    }
                }
            }

            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var merged = new List<FsRegion>(list.Count);
            foreach (FsRegion r in list)
            {
                if (merged.Count > 0 && merged[^1].End >= r.Offset)
                {
                    FsRegion last = merged[^1];
                    merged[^1] = last with { Length = Math.Max(last.End, r.End) - last.Offset };
                }
                else
                {
                    merged.Add(r);
                }
            }

            return _metadataRegions = merged;
        }
    }

    // ---- 割り当て ----

    private IAllocationMap? _allocation;

    public override IAllocationMap? Allocation => _allocation ??= new ExtAllocationMap(this);

    /// <summary>ブロックビットマップで割り当てを判定する (FOR-12 の仕様 8)。BLOCK_UNINIT のグループは、そのグループのメタデータだけを使用中とする。</summary>
    private sealed class ExtAllocationMap(ExtFileSystem fs) : IAllocationMap
    {
        private readonly CachedVolumeReader _bitmaps = new(fs.Volume, 4096, 64);

        public long ClusterCount => fs._sb.BlocksCount;

        public bool IsAllocated(long cluster)
        {
            ExtSuperblock sb = fs._sb;
            if (cluster < 0 || cluster >= sb.BlocksCount)
            {
                return false;
            }

            if (cluster < sb.FirstDataBlock)
            {
                return true;
            }

            long group = (cluster - sb.FirstDataBlock) / sb.BlocksPerGroup;
            long bit = (cluster - sb.FirstDataBlock) % sb.BlocksPerGroup;
            ExtGroupDescriptor d = fs.Group(group);
            if ((d.Flags & ExtGroupDescriptor.BlockUninit) != 0)
            {
                return UninitAllocated(group, cluster, d);
            }

            if (d.BlockBitmap <= 0 || d.BlockBitmap >= sb.BlocksCount)
            {
                return true;
            }

            Span<byte> b = stackalloc byte[1];
            _bitmaps.Read(d.BlockBitmap * sb.BlockSize + (bit >> 3), b);
            return (b[0] & (1 << (int)(bit & 7))) != 0;
        }

        private bool UninitAllocated(long group, long cluster, ExtGroupDescriptor d)
        {
            ExtSuperblock sb = fs._sb;
            long first = fs.GroupFirstBlock(group);
            long bs = sb.BlockSize;
            if (fs.GroupHasSuperblock(group))
            {
                long gdtBlocks = (sb.GroupCount * sb.DescriptorSize + bs - 1) / bs;
                if (cluster < first + 1 + gdtBlocks + sb.ReservedGdtBlocks)
                {
                    return true;
                }
            }

            long tableBlocks = ((long)sb.InodesPerGroup * sb.InodeSize + bs - 1) / bs;
            return cluster == d.BlockBitmap || cluster == d.InodeBitmap || cluster >= d.InodeTable && cluster < d.InodeTable + tableBlocks;
        }

        public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default)
        {
            long start = Math.Max(0, first);
            long end = Math.Min(first + count, ClusterCount);
            long runStart = -1;
            for (long c = start; c < end; c++)
            {
                if ((c & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                bool used = IsAllocated(c);
                if (!used && runStart < 0)
                {
                    runStart = c;
                }
                else if (used && runStart >= 0)
                {
                    yield return (runStart, c - runStart);
                    runStart = -1;
                }
            }

            if (runStart >= 0)
            {
                yield return (runStart, end - runStart);
            }
        }

        public long CountFree(CancellationToken cancellationToken = default)
        {
            long free = 0;
            foreach ((_, long n) in FreeRuns(0, ClusterCount, cancellationToken))
            {
                free += n;
            }

            return free;
        }
    }

    // ---- 拡張属性 ----

    /// <summary>拡張属性 (inode の中と、i_file_acl のブロック)。名前は接頭辞を付けたもの (<c>user.</c> など)。</summary>
    public IReadOnlyList<(string Name, byte[] Value)> InodeXattrs(ExtInode inode)
    {
        var list = new List<(string, byte[])>();
        int start = 128 + inode.ExtraIsize;
        if (inode.Raw.Length > start + 4 && BinaryPrimitives.ReadUInt32LittleEndian(inode.Raw.AsSpan(start)) == 0xEA020000)
        {
            ParseXattrs(inode.Raw.AsSpan(start + 4), inode.Raw.AsSpan(start + 4), list);
        }

        if (inode.FileAcl > 0 && inode.FileAcl < _sb.BlocksCount)
        {
            byte[] block = _meta.ReadBytes(inode.FileAcl * _sb.BlockSize, _sb.BlockSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(block) == 0xEA020000)
            {
                ParseXattrs(block.AsSpan(32), block, list);
            }
        }

        return list;
    }

    private static void ParseXattrs(ReadOnlySpan<byte> entries, ReadOnlySpan<byte> valueBase, List<(string, byte[])> list)
    {
        int pos = 0;
        for (int guard = 0; guard < 256 && pos + 16 <= entries.Length; guard++)
        {
            int nameLen = entries[pos];
            int index = entries[pos + 1];
            if (nameLen == 0 && index == 0 && BinaryPrimitives.ReadUInt32LittleEndian(entries[pos..]) == 0)
            {
                break;
            }

            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(entries[(pos + 2)..]);
            uint valueInode = BinaryPrimitives.ReadUInt32LittleEndian(entries[(pos + 4)..]);
            int valueSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entries[(pos + 8)..]), 65536);
            if (pos + 16 + nameLen > entries.Length)
            {
                break;
            }

            string prefix = index switch
            {
                1 => "user.", 2 => "system.posix_acl_access", 3 => "system.posix_acl_default", 4 => "trusted.", 6 => "security.", 7 => "system.",
                8 => "system.richacl", _ => string.Empty,
            };
            string name = prefix + Encoding.ASCII.GetString(entries.Slice(pos + 16, nameLen));
            byte[] value = valueInode == 0 && valueOffset + valueSize <= valueBase.Length ? valueBase.Slice(valueOffset, valueSize).ToArray() : [];
            list.Add((name, value));
            pos += (16 + nameLen + 3) & ~3;
        }
    }

    // ---- 詳細 ----

    public override FileSystemSummary Summary
    {
        get
        {
            var d = new List<FsDetail>
            {
                new("BlockSize", _sb.BlockSize.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("InodesCount", _sb.InodesCount.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("BlocksCount", _sb.BlocksCount.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("FreeBlocks", _sb.FreeBlocks.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("FreeInodes", _sb.FreeInodes.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("FirstDataBlock", _sb.FirstDataBlock.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("BlocksPerGroup", _sb.BlocksPerGroup.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("InodesPerGroup", _sb.InodesPerGroup.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("GroupCount", _sb.GroupCount.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("InodeSize", _sb.InodeSize.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("DescriptorSize", _sb.DescriptorSize.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("RevisionLevel", _sb.RevLevel.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("FeaturesCompat", ExtFeatures.Names(_sb.Compat, 0), "Superblock"),
                new("FeaturesIncompat", ExtFeatures.Names(_sb.Incompat, 1), "Superblock"),
                new("FeaturesRoCompat", ExtFeatures.Names(_sb.RoCompat, 2), "Superblock"),
                new("Uuid", _sb.Uuid.ToString("D"), "Superblock"),
                new("VolumeName", _sb.VolumeName, "Superblock"),
                new("LastMounted", _sb.LastMounted, "Superblock"),
                new("LastMountTime", ExtInode.Time(_sb.MountTime, null).ToString(), "Superblock"),
                new("LastWriteTime", ExtInode.Time(_sb.WriteTime, null).ToString(), "Superblock"),
                new("JournalInode", _sb.JournalInode.ToString(CultureInfo.InvariantCulture), "Superblock"),
                new("State", $"0x{_sb.State:X4}", "Superblock"),
                new("FlexGroupSize", _sb.HasFlexBg ? (1 << Math.Min(_sb.LogGroupsPerFlex, 31)).ToString(CultureInfo.InvariantCulture) : "-", "Superblock"),
            };
            if (_sb.ChecksumValid is bool ok)
            {
                d.Add(new FsDetail("SuperblockChecksum", ok ? "OK" : "mismatch", "Superblock"));
            }

            if (ExtJournal.Read(this) is { } journal)
            {
                d.Add(new FsDetail("JournalBlockSize", journal.BlockSize.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalLength", journal.MaxLength.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalSequence", journal.Sequence.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalStart", journal.Start.ToString(CultureInfo.InvariantCulture), "Journal"));
            }

            return new FileSystemSummary
            {
                Kind = FileSystemKind.Ext,
                TypeName = _sb.Variant,
                Label = _sb.VolumeName,
                Serial = _sb.Uuid.ToString("D"),
                ClusterSize = _sb.BlockSize,
                TotalClusters = _sb.BlocksCount,
                Created = ExtInode.Time(_sb.MkfsTime, null),
                Details = d,
            };
        }
    }

    /// <summary>機能の名前の一覧 (スーパーブロックの詳細。TC-FOR-12-01)。</summary>
    public IReadOnlyList<string> FeatureNames =>
        [.. new[] { ExtFeatures.Names(_sb.Compat, 0), ExtFeatures.Names(_sb.Incompat, 1), ExtFeatures.Names(_sb.RoCompat, 2) }
            .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))];

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is not ExtTag tag)
        {
            return d;
        }

        d.Add(new FsDetail("Inode", tag.InodeNumber.ToString(CultureInfo.InvariantCulture)));
        if (tag.EntryOffset >= 0)
        {
            d.Add(new FsDetail("EntryOffset", $"0x{tag.EntryOffset:X}"));
        }

        if (tag.Inode is not { } inode)
        {
            d.Add(new FsDetail("DataLocation", "unknown"));
            return d;
        }

        d.Add(new FsDetail("InodeOffset", $"0x{inode.Offset:X}"));
        d.Add(new FsDetail("Mode", $"{ExtInode.ModeText(inode.Mode)} (0{Convert.ToString(inode.Mode & 0xFFF, 8)})"));
        d.Add(new FsDetail("Uid", inode.Uid.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("Gid", inode.Gid.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("Size", inode.Size.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("Links", inode.Links.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("InodeFlags", $"0x{inode.Flags:X8}{FlagNames(inode.Flags)}"));
        d.Add(new FsDetail("Generation", inode.Generation.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("BlocksRaw", inode.BlocksRaw.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("Accessed", inode.Accessed.ToString(), "Times"));
        d.Add(new FsDetail("Changed", inode.Changed.ToString(), "Times"));
        d.Add(new FsDetail("Modified", inode.Modified.ToString(), "Times"));
        d.Add(new FsDetail("Created", inode.Created.ToString(), "Times"));
        d.Add(new FsDetail("Deleted", inode.Deleted.ToString(), "Times"));
        string mapping = inode.HasInlineData ? "inline" : inode.UsesExtents ? "extents" : "indirect";
        ExtentsOf(inode, inode.Size, out _, out _, out int depth);
        d.Add(new FsDetail("DataMapping", inode.UsesExtents ? $"{mapping} (depth {depth})" : mapping));
        if (_sb.HasMetadataCsum)
        {
            d.Add(new FsDetail("InodeChecksum", inode.ChecksumMatches(_sb.CsumSeed) ? "OK" : "mismatch"));
        }

        if (inode.IsEncrypted || tag.EncryptedName)
        {
            d.Add(new FsDetail("Encryption", "fscrypt"));
        }

        if (inode.IsSymlink)
        {
            byte[] target = inode.Size < 60 && inode.BlocksRaw == 0 ? inode.Block[..(int)inode.Size].ToArray() : FsDetailReader.Head(OpenContent(entry), 1024);
            d.Add(new FsDetail("SymlinkTarget", ExtNames.Decode(target)));
        }

        foreach ((string name, byte[] value) in InodeXattrs(inode).Take(64))
        {
            d.Add(new FsDetail("xattr:" + name, value.Length <= 64 && value.All(b => b is >= 0x20 and < 0x7F)
                ? Encoding.ASCII.GetString(value) : Convert.ToHexString(value.AsSpan(0, Math.Min(value.Length, 32))) + (value.Length > 32 ? "…" : string.Empty),
                "ExtendedAttributes"));
        }

        return d;
    }

    private static string FlagNames(uint flags)
    {
        var names = new List<string>();
        foreach ((uint bit, string name) in new[]
        {
            (0x10u, "immutable"), (0x20u, "append"), (0x40u, "nodump"), (0x80u, "noatime"), (ExtInode.FlagEncrypted, "encrypted"),
            (ExtInode.FlagIndex, "index"), (ExtInode.FlagHugeFile, "huge_file"), (ExtInode.FlagExtents, "extents"), (ExtInode.FlagEaInode, "ea_inode"),
            (ExtInode.FlagInlineData, "inline_data"), (0x20000000u, "project_inherit"), (0x40000000u, "casefold"),
        })
        {
            if ((flags & bit) != 0)
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? string.Empty : " (" + string.Join(", ", names) + ")";
    }
}

/// <summary>詳細欄のための小さな読み込み。</summary>
internal static class FsDetailReader
{
    public static byte[] Head(IVolumeReader reader, int max)
    {
        byte[] b = new byte[(int)Math.Min(reader.Length, max)];
        reader.Read(0, b);
        return b;
    }
}
