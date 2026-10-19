using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Apple;

/// <summary>HFS+ / HFSX と APFS を開く処理 (FOR-13)。</summary>
public sealed class AppleFileSystemProvider : IFileSystemProvider
{
    public IReadOnlyCollection<FileSystemKind> Kinds { get; } = [FileSystemKind.HfsPlus, FileSystemKind.Hfsx, FileSystemKind.Apfs];

    public IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options) =>
        options.Kind == FileSystemKind.Apfs ? new ApfsFileSystem(volume, options) : new HfsPlusFileSystem(volume, options);
}

/// <summary>HFS+ のフォーク (論理サイズ、ブロック数、最初の 8 個のエクステント)。</summary>
public sealed record HfsFork(long LogicalSize, uint TotalBlocks, IReadOnlyList<(uint Start, uint Count)> Extents)
{
    public static HfsFork Parse(ReadOnlySpan<byte> b)
    {
        // 短いレコード (壊れたエクステントのオーバーフローのレコードなど) は 0 で埋めた 80 バイトとして読む。
        if (b.Length < 80)
        {
            byte[] padded = new byte[80];
            b.CopyTo(padded);
            b = padded;
        }

        var extents = new List<(uint, uint)>(8);
        for (int i = 0; i < 8; i++)
        {
            uint start = BinaryPrimitives.ReadUInt32BigEndian(b[(16 + i * 8)..]);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(b[(20 + i * 8)..]);
            if (count == 0)
            {
                break;
            }

            extents.Add((start, count));
        }

        return new HfsFork(BinaryPrimitives.ReadInt64BigEndian(b), BinaryPrimitives.ReadUInt32BigEndian(b[12..]), extents);
    }
}

/// <summary>項目の実装用の情報。</summary>
internal sealed record HfsTag(uint Cnid, bool Folder, HfsFork? Data, HfsFork? Resource, bool IsResourceFork, long RecordOffset, byte[] Record, uint LinkTarget);

/// <summary>
/// HFS+ / HFSX の解析 (FOR-13 の仕様 1)。ボリュームヘッダー (1024 バイト目) と末尾の予備を比べ、カタログ B 木からフォルダとファイル (データフォークと
/// リソースフォーク) を読む。9 個目以降の断片はエクステントオーバーフロー B 木から読む。ハードリンク (<c>iNode</c> ファイルへの参照) を解決する。
/// decmpfs の圧縮は展開せず「圧縮されています」と示す。ジャーナルは表示だけ行い、再生しない。空き領域は割り当てファイルで判定する。
/// 削除された項目は扱わない (未決定)。B 木のノードは必要なものだけ読む。
/// </summary>
public sealed class HfsPlusFileSystem : FileSystemBase
{
    public const uint RootFolderId = 2;
    public const int MaxDepth = 64;
    public const int MaxNodes = 1_000_000;
    private const uint HardLinkType = 0x686C6E6B, HardLinkCreator = 0x6866732B; // 'hlnk' 'hfs+'
    private const uint DirLinkType = 0x66647270, DirLinkCreator = 0x4D414353; // 'fdrp' 'MACS'
    private const string PrivateDataName = "\0\0\0\0HFS+ Private Data";
    private const string PrivateDirName = ".HFS+ Private Directory Data\r";

    private static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly byte[] _vh;
    private readonly int _blockSize;
    private readonly uint _totalBlocks;
    private readonly FileSystemKind _kind;
    private readonly BTree _extentsTree;
    private readonly BTree _catalog;
    private readonly HfsFork _allocationFork, _extentsFork, _catalogFork, _attributesFork, _startupFork;
    private readonly FsEntry _root;
    private readonly object _gate = new();
    private uint? _privateData;
    private uint? _privateDirs;
    private bool _privateRead;

    public HfsPlusFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _ = options;
        _vh = volume.ReadBytes(1024, 512);
        ushort sig = BinaryPrimitives.ReadUInt16BigEndian(_vh);
        _kind = sig switch { 0x482B => FileSystemKind.HfsPlus, 0x4858 => FileSystemKind.Hfsx, _ => FileSystemKind.Unknown };
        _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(_vh.AsSpan(40));
        _totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(_vh.AsSpan(44));
        if (_kind == FileSystemKind.Unknown || _blockSize < 512 || _blockSize > 1 << 20 || (_blockSize & (_blockSize - 1)) != 0 || _totalBlocks == 0)
        {
            throw new FileSystemOpenException("The HFS+ volume header is invalid.", FileSystemKind.HfsPlus);
        }

        _allocationFork = HfsFork.Parse(_vh.AsSpan(112, 80));
        _extentsFork = HfsFork.Parse(_vh.AsSpan(192, 80));
        _catalogFork = HfsFork.Parse(_vh.AsSpan(272, 80));
        _attributesFork = HfsFork.Parse(_vh.AsSpan(352, 80));
        _startupFork = HfsFork.Parse(_vh.AsSpan(432, 80));
        _extentsTree = new BTree(this, ForkReader(_extentsFork, 3, 0, overflow: false));
        _catalog = new BTree(this, ForkReader(_catalogFork, 4, 0, overflow: true));
        if (!_catalog.Valid)
        {
            throw new FileSystemOpenException("The HFS+ catalog B-tree header is invalid.", _kind);
        }

        CompareAlternateHeader();
        string label = ThreadOf(RootFolderId)?.Name ?? string.Empty;
        _root = new FsEntry
        {
            Id = "hfs:2",
            Name = label,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            FileIdText = "2",
            Tag = new HfsTag(RootFolderId, true, null, null, false, -1, [], 0),
        };
    }

    public override FileSystemKind Kind => _kind;
    public override long ClusterSize => _blockSize;
    public override long ClusterCount => _totalBlocks;
    public override bool UsesBlocks => true;
    public override int SectorSize => 512;
    public override long FileSystemLength => (long)_totalBlocks * _blockSize;
    protected override long ClusterAreaOffset => 0;
    public override FsEntry Root => _root;

    public bool Journaled => (BinaryPrimitives.ReadUInt32BigEndian(_vh.AsSpan(4)) & 0x2000) != 0;

    private static FsTimestamp Time(uint seconds) => seconds == 0 ? default : new FsTimestamp(Epoch.AddSeconds(seconds), Raw: seconds);

    // ---- フォーク ----

    /// <summary>フォークの断片 (最初の 8 個と、エクステントオーバーフロー B 木の続き)。</summary>
    private List<FsExtent> ForkExtents(HfsFork fork, uint fileId, byte forkType, bool overflow, out FsCorruptionKind problem)
    {
        problem = FsCorruptionKind.None;
        var list = new List<FsExtent>();
        long logical = 0;
        uint blocks = 0;
        void Add(uint start, uint count)
        {
            if ((long)start + count > _totalBlocks)
            {
                return;
            }

            long length = Math.Min((long)count * _blockSize, Math.Max(0, fork.LogicalSize - logical));
            if (length > 0)
            {
                list.Add(new FsExtent(logical, length, (long)start * _blockSize, start, count));
            }

            logical += (long)count * _blockSize;
            blocks += count;
        }

        foreach ((uint start, uint count) in fork.Extents)
        {
            if ((long)start + count > _totalBlocks)
            {
                problem = FsCorruptionKind.ClusterOutOfRange;
            }

            Add(start, count);
        }

        int guard = 0;
        while (overflow && blocks < fork.TotalBlocks && fork.Extents.Count == 8 && guard++ < 10_000)
        {
            uint startBlock = blocks;
            byte[]? record = _extentsTree.Find(k => CompareExtentKey(k, forkType, fileId, startBlock), out _);
            if (record is null)
            {
                problem = FsCorruptionKind.BadRunList;
                break;
            }

            uint before = blocks;
            HfsFork more = HfsFork.Parse([.. new byte[16], .. record.AsSpan(0, Math.Min(64, record.Length))]);
            foreach ((uint start, uint count) in more.Extents)
            {
                Add(start, count);
            }

            if (blocks == before)
            {
                break;
            }
        }

        return list;
    }

    private static int CompareExtentKey(ReadOnlySpan<byte> key, byte forkType, uint fileId, uint startBlock)
    {
        if (key.Length < 12)
        {
            return -1;
        }

        int c = key[2].CompareTo(forkType);
        if (c != 0)
        {
            return c;
        }

        c = BinaryPrimitives.ReadUInt32BigEndian(key[4..]).CompareTo(fileId);
        return c != 0 ? c : BinaryPrimitives.ReadUInt32BigEndian(key[8..]).CompareTo(startBlock);
    }

    private IVolumeReader ForkReader(HfsFork fork, uint fileId, byte forkType, bool overflow) =>
        new CachedVolumeReader(new ExtentContentReader(Volume, overflow ? ForkExtents(fork, fileId, forkType, true, out _) : ForkExtents(fork, fileId, forkType, false, out _),
            fork.LogicalSize), 64 * 1024, 32);

    // ---- B 木 ----

    /// <summary>B 木 (カタログ・エクステントオーバーフロー)。ノードは必要なものだけ読む。</summary>
    private sealed class BTree
    {
        private readonly IVolumeReader _file;

        public BTree(HfsPlusFileSystem fs, IVolumeReader file)
        {
            _ = fs;
            _file = file;
            byte[] head = file.ReadBytes(0, 512);
            if (head[8] != 1)
            {
                return;
            }

            ReadOnlySpan<byte> h = head.AsSpan(14);
            Depth = BinaryPrimitives.ReadUInt16BigEndian(h);
            RootNode = BinaryPrimitives.ReadUInt32BigEndian(h[2..]);
            FirstLeaf = BinaryPrimitives.ReadUInt32BigEndian(h[10..]);
            NodeSize = BinaryPrimitives.ReadUInt16BigEndian(h[18..]);
            MaxKeyLength = BinaryPrimitives.ReadUInt16BigEndian(h[20..]);
            TotalNodes = BinaryPrimitives.ReadUInt32BigEndian(h[22..]);
            KeyCompareType = h[37];
            Attributes = BinaryPrimitives.ReadUInt32BigEndian(h[38..]);
            Valid = NodeSize is >= 512 and <= 32768 && (NodeSize & (NodeSize - 1)) == 0 && TotalNodes > 0
                && (long)TotalNodes * NodeSize <= file.Length + NodeSize;
        }

        public bool Valid { get; }
        public int Depth { get; }
        public uint RootNode { get; }
        public uint FirstLeaf { get; }
        public int NodeSize { get; }
        public int MaxKeyLength { get; }
        public uint TotalNodes { get; }
        public byte KeyCompareType { get; }
        public uint Attributes { get; }

        public sealed record Node(uint Number, uint FLink, sbyte Kind, int Height, IReadOnlyList<(int Offset, int Length)> Records, byte[] Data);

        public Node? Read(uint number)
        {
            if (!Valid || number >= TotalNodes)
            {
                return null;
            }

            byte[] d = _file.ReadBytes((long)number * NodeSize, NodeSize, out bool complete);
            if (!complete)
            {
                return null;
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
            if (14 + count * 2 + 2 > NodeSize)
            {
                return null;
            }

            var records = new List<(int, int)>(count);
            for (int i = 0; i < count; i++)
            {
                int start = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(NodeSize - 2 * (i + 1)));
                int end = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(NodeSize - 2 * (i + 2)));
                if (start < 14 || end <= start || end > NodeSize - 2 * (count + 1))
                {
                    return null;
                }

                records.Add((start, end - start));
            }

            return new Node(number, BinaryPrimitives.ReadUInt32BigEndian(d), (sbyte)d[8], d[9], records, d);
        }

        /// <summary>キーの長さ (キー長の欄を含む)。索引ノードで可変長のキーを使わない場合は最大長。</summary>
        public int KeyLength(Node node, ReadOnlySpan<byte> record)
        {
            bool variable = node.Kind == -1 || (Attributes & 4) != 0;
            int length = (Attributes & 2) != 0 ? BinaryPrimitives.ReadUInt16BigEndian(record) + 2 : record[0] + 1;
            if (!variable)
            {
                length = MaxKeyLength + 2;
            }

            return (length + 1) & ~1;
        }

        /// <summary>
        /// <paramref name="compare"/> (キーと目的の比較: 負ならキーが小さい) で、目的以上の最初の葉のレコードから順に返す。fLink を
        /// たどり、ノードの数に上限を設ける (循環への備え)。
        /// </summary>
        public IEnumerable<(Node Node, int Index, byte[] Key, byte[] Data)> Scan(Func<byte[], int> compare, List<uint>? bad = null)
        {
            Node? node = Read(RootNode);
            if (node is null)
            {
                bad?.Add(RootNode);
            }

            var visited = new HashSet<uint>();
            for (int level = 0; node is not null && node.Kind == 0 && level < 16; level++)
            {
                if (!visited.Add(node.Number))
                {
                    yield break;
                }

                uint child = uint.MaxValue;
                for (int i = 0; i < node.Records.Count; i++)
                {
                    (int off, int len) = node.Records[i];
                    ReadOnlySpan<byte> rec = node.Data.AsSpan(off, len);
                    int keyLength = KeyLength(node, rec);
                    if (keyLength + 4 > len)
                    {
                        break;
                    }

                    if (i > 0 && compare(rec[..keyLength].ToArray()) > 0)
                    {
                        break;
                    }

                    child = BinaryPrimitives.ReadUInt32BigEndian(rec[keyLength..]);
                }

                node = child == uint.MaxValue ? null : Read(child);
                if (node is null && child != uint.MaxValue)
                {
                    bad?.Add(child);
                }
            }

            int nodes = 0;
            bool started = false;
            while (node is not null && node.Kind == -1 && nodes++ < MaxNodes && visited.Add(node.Number))
            {
                for (int i = 0; i < node.Records.Count; i++)
                {
                    (int off, int len) = node.Records[i];
                    byte[] rec = node.Data.AsSpan(off, len).ToArray();
                    int keyLength = Math.Min(KeyLength(node, rec), len);
                    byte[] key = rec[..keyLength];
                    if (!started && compare(key) < 0)
                    {
                        continue;
                    }

                    started = true;
                    yield return (node, i, key, rec[keyLength..]);
                }

                uint next = node.FLink;
                node = next == 0 ? null : Read(next);
                if (node is null && next != 0)
                {
                    bad?.Add(next);
                }
            }
        }

        /// <summary>目的と等しいキーのレコード (なければ null)。</summary>
        public byte[]? Find(Func<byte[], int> compare, out long recordOffset)
        {
            recordOffset = -1;
            foreach ((Node node, int index, byte[] key, byte[] data) in Scan(compare))
            {
                if (compare(key) == 0)
                {
                    recordOffset = (long)node.Number * NodeSize + node.Records[index].Offset;
                    return data;
                }

                return null;
            }

            return null;
        }

        public IVolumeReader File => _file;
    }

    // ---- カタログ ----

    private static uint KeyParent(byte[] key) => key.Length >= 6 ? BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2)) : 0;

    private static string KeyName(byte[] key)
    {
        if (key.Length < 8)
        {
            return string.Empty;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6));
        int bytes = Math.Min(length * 2, key.Length - 8);
        return Encoding.BigEndianUnicode.GetString(key, 8, Math.Max(0, bytes));
    }

    private sealed record Thread(uint Parent, string Name);

    private Thread? ThreadOf(uint cnid)
    {
        byte[]? data = _catalog.Find(k => CompareParent(k, cnid, emptyName: true), out _);
        if (data is null || data.Length < 10)
        {
            return null;
        }

        short type = BinaryPrimitives.ReadInt16BigEndian(data);
        if (type is not (3 or 4))
        {
            return null;
        }

        uint parent = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        string name = Encoding.BigEndianUnicode.GetString(data, 10, Math.Min(length * 2, data.Length - 10));
        return new Thread(parent, name);
    }

    private static int CompareParent(byte[] key, uint parent, bool emptyName)
    {
        int c = KeyParent(key).CompareTo(parent);
        if (c != 0 || !emptyName)
        {
            return c;
        }

        return key.Length >= 8 && BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6)) > 0 ? 1 : 0;
    }

    private sealed record CatalogRecord(string Name, short Type, byte[] Data, long Offset);

    /// <summary>フォルダの中のレコード (スレッドを除く)。</summary>
    private IEnumerable<CatalogRecord> Children(uint folder, CancellationToken token, List<uint>? bad = null)
    {
        foreach ((BTree.Node node, int index, byte[] key, byte[] data) in _catalog.Scan(k => CompareParent(k, folder, emptyName: true), bad))
        {
            token.ThrowIfCancellationRequested();
            if (KeyParent(key) != folder)
            {
                yield break;
            }

            if (data.Length < 2)
            {
                continue;
            }

            short type = BinaryPrimitives.ReadInt16BigEndian(data);
            if (type is 1 or 2)
            {
                yield return new CatalogRecord(FsText.NormalizeC(KeyName(key)), type, data,
                    (long)node.Number * _catalog.NodeSize + node.Records[index].Offset);
            }
        }
    }

    private void ReadPrivateFolders()
    {
        if (_privateRead)
        {
            return;
        }

        _privateRead = true;
        foreach (CatalogRecord r in Children(RootFolderId, CancellationToken.None))
        {
            if (r.Type == 1 && r.Data.Length >= 12)
            {
                if (r.Name == PrivateDataName)
                {
                    _privateData = BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan(8));
                }
                else if (r.Name == PrivateDirName)
                {
                    _privateDirs = BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan(8));
                }
            }
        }
    }

    /// <summary>ハードリンクの実体 (<c>iNode&lt;番号&gt;</c>。ディレクトリのリンクは <c>dir_&lt;番号&gt;</c>)。</summary>
    private CatalogRecord? LinkTarget(uint number, bool directory)
    {
        ReadPrivateFolders();
        uint? folder = directory ? _privateDirs : _privateData;
        if (folder is not uint f)
        {
            return null;
        }

        string name = (directory ? "dir_" : "iNode") + number.ToString(CultureInfo.InvariantCulture);
        return Children(f, CancellationToken.None).FirstOrDefault(r => r.Name == name);
    }

    private FsEntry MakeEntry(CatalogRecord r, FsEntry parent, out FsEntry? resource)
    {
        resource = null;
        byte[] d = r.Data;
        bool folder = r.Type == 1;
        if (d.Length < (folder ? 88 : 248))
        {
            return new FsEntry
            {
                Id = $"hfs:bad:{r.Offset:X}",
                Name = r.Name,
                Path = FsText.Combine(parent.Path, r.Name),
                Flags = FsEntryFlags.Corrupt | (folder ? FsEntryFlags.Directory : 0),
                Corruption = FsCorruptionKind.BadAttribute,
                CorruptionDetail = $"catalog record 0x{r.Offset:X}",
                ParentId = parent.Id,
            };
        }

        uint cnid = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
        byte[] record = d;
        uint linkTarget = 0;
        bool linkedFolder = false;
        if (!folder)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(48));
            uint creator = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(52));
            uint special = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(44));
            if (type == HardLinkType && creator == HardLinkCreator && LinkTarget(special, directory: false) is { Type: 2 } target)
            {
                record = target.Data;
                linkTarget = special;
            }
            else if (type == DirLinkType && creator == DirLinkCreator && LinkTarget(special, directory: true) is { Type: 1 } dir)
            {
                record = dir.Data;
                linkTarget = special;
                linkedFolder = true;
            }
        }

        bool isFolder = folder || linkedFolder;
        uint recordCnid = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8));
        byte ownerFlags = record[41];
        int mode = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(42));
        bool compressed = (ownerFlags & 0x20) != 0;
        HfsFork? data = isFolder ? null : HfsFork.Parse(record.AsSpan(88, 80));
        HfsFork? rsrc = isFolder ? null : HfsFork.Parse(record.AsSpan(168, 80));
        string path = FsText.Combine(parent.Path, r.Name);
        IReadOnlyList<FsExtent> extents = data is null ? [] : ForkExtents(data, recordCnid, 0, true, out FsCorruptionKind problem);
        var entry = new FsEntry
        {
            Id = $"hfs:{cnid}",
            Name = r.Name,
            Path = path,
            Flags = (isFolder ? FsEntryFlags.Directory : 0) | (compressed ? FsEntryFlags.Compressed : 0),
            Size = data?.LogicalSize ?? 0,
            AllocatedSize = (long)(data?.TotalBlocks ?? 0) * _blockSize,
            Created = Time(BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(12))),
            Modified = Time(BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(16))),
            Changed = Time(BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(20))),
            Accessed = Time(BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(24))),
            Attributes = mode != 0 ? FsText.PosixMode(mode) : string.Empty,
            FileIdText = cnid.ToString(CultureInfo.InvariantCulture),
            FirstCluster = extents.FirstOrDefault()?.StartCluster,
            FragmentCount = extents.Count,
            MetadataOffset = CatalogVolumeOffset(r.Offset),
            MetadataLength = Math.Min(512, r.Data.Length),
            ParentId = parent.Id,
            Tag = new HfsTag(isFolder ? BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8)) : recordCnid, isFolder, data, rsrc, false, r.Offset, record,
                linkTarget),
        };
        if (!isFolder && rsrc is { LogicalSize: > 0 })
        {
            IReadOnlyList<FsExtent> rext = ForkExtents(rsrc, recordCnid, 0xFF, true, out _);
            string name = r.Name + "/..namedfork/rsrc";
            resource = new FsEntry
            {
                Id = $"hfs:{cnid}:rsrc",
                Name = name,
                Path = FsText.Combine(parent.Path, name),
                Flags = FsEntryFlags.Stream,
                Size = rsrc.LogicalSize,
                AllocatedSize = (long)rsrc.TotalBlocks * _blockSize,
                Created = entry.Created,
                Modified = entry.Modified,
                Accessed = entry.Accessed,
                Changed = entry.Changed,
                Attributes = entry.Attributes,
                FileIdText = entry.FileIdText,
                FirstCluster = rext.FirstOrDefault()?.StartCluster,
                FragmentCount = rext.Count,
                MetadataOffset = entry.MetadataOffset,
                MetadataLength = entry.MetadataLength,
                StreamName = "rsrc",
                ParentId = parent.Id,
                Tag = new HfsTag(recordCnid, false, data, rsrc, true, r.Offset, record, linkTarget),
            };
        }

        return entry;
    }

    /// <summary>カタログファイルの中の位置のボリューム上の位置。</summary>
    private long? CatalogVolumeOffset(long fileOffset)
    {
        long logical = 0;
        foreach ((uint start, uint count) in _catalogFork.Extents)
        {
            long bytes = (long)count * _blockSize;
            if (fileOffset < logical + bytes)
            {
                return (long)start * _blockSize + (fileOffset - logical);
            }

            logical += bytes;
        }

        return null;
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not HfsTag { Folder: true } tag)
        {
            yield break;
        }

        var bad = new List<uint>();
        foreach (CatalogRecord r in Children(tag.Cnid, cancellationToken, bad))
        {
            if (tag.Cnid == RootFolderId && r.Name is PrivateDataName or PrivateDirName)
            {
                continue;
            }

            FsEntry e = MakeEntry(r, directory, out FsEntry? resource);
            yield return e;
            if (resource is not null)
            {
                yield return resource;
            }
        }

        if (bad.Count > 0)
        {
            // 読めない B 木のノード: このフォルダ (の下) に破損の印を付けて続ける (FOR-13 の「エラー」)。
            directory.Flags |= FsEntryFlags.Corrupt;
            directory.Corruption = FsCorruptionKind.BadSignature;
            directory.CorruptionDetail = "catalog node " + string.Join(", ", bad.Distinct());
        }
    }

    public override IEnumerable<FsEntry> GetStreams(FsEntry entry)
    {
        if (entry.Tag is HfsTag { IsResourceFork: false, Resource.LogicalSize: > 0 } && entry.ParentId is not null && FindByParent(entry) is { } parent)
        {
            foreach (FsEntry e in ListDirectory(parent).Where(x => x.Id == entry.Id + ":rsrc"))
            {
                yield return e;
            }
        }
    }

    private FsEntry? FindByParent(FsEntry entry) =>
        entry.ParentId == _root.Id ? _root : entry.ParentId is { } id && id.StartsWith("hfs:", StringComparison.Ordinal)
            && uint.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out uint cnid) ? FolderEntry(cnid) : null;

    private FsEntry? FolderEntry(uint cnid)
    {
        if (cnid == RootFolderId)
        {
            return _root;
        }

        Thread? t = ThreadOf(cnid);
        if (t is null)
        {
            return null;
        }

        string path = BuildPath(cnid) ?? "\\" + t.Name;
        return new FsEntry
        {
            Id = $"hfs:{cnid}",
            Name = t.Name,
            Path = path,
            Flags = FsEntryFlags.Directory,
            FileIdText = cnid.ToString(CultureInfo.InvariantCulture),
            Tag = new HfsTag(cnid, true, null, null, false, -1, [], 0),
        };
    }

    private string? BuildPath(uint cnid)
    {
        var parts = new List<string>();
        uint current = cnid;
        for (int i = 0; i < MaxDepth && current != RootFolderId; i++)
        {
            Thread? t = ThreadOf(current);
            if (t is null)
            {
                return null;
            }

            parts.Add(FsText.NormalizeC(t.Name));
            current = t.Parent;
        }

        parts.Reverse();
        return "\\" + string.Join('\\', parts);
    }

    public override FsEntry? FindById(string id)
    {
        if (!id.StartsWith("hfs:", StringComparison.Ordinal) || !uint.TryParse(id.AsSpan(4).TrimEnd(":rsrc"), NumberStyles.None, CultureInfo.InvariantCulture,
            out uint cnid))
        {
            return null;
        }

        if (cnid == RootFolderId)
        {
            return _root;
        }

        Thread? t = ThreadOf(cnid);
        FsEntry? parent = t is null ? null : FolderEntry(t.Parent);
        return parent is null ? null : ListDirectory(parent).FirstOrDefault(e => e.Id == id);
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry) => entry.Tag switch
    {
        HfsTag { IsResourceFork: true, Resource: { } r } t => ForkExtents(r, t.Cnid, 0xFF, true, out _),
        HfsTag { Data: { } d } t => ForkExtents(d, t.Cnid, 0, true, out _),
        IReadOnlyList<FsExtent> fixedExtents => fixedExtents,
        _ => [],
    };

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        long size = entry.Flags.HasFlag(FsEntryFlags.Metadata) ? extents.Sum(e => e.Length) : entry.Size;
        return new ExtentContentReader(Volume, extents, size);
    }

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is not HfsTag { Record.Length: > 0 } t)
        {
            return d;
        }

        byte[] r = t.Record;
        d.Add(new FsDetail("CatalogNodeId", t.Cnid.ToString(CultureInfo.InvariantCulture)));
        d.Add(new FsDetail("CatalogRecordOffset", $"0x{t.RecordOffset:X}"));
        d.Add(new FsDetail("Owner", BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(32)).ToString(CultureInfo.InvariantCulture), "Bsd"));
        d.Add(new FsDetail("Group", BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(36)).ToString(CultureInfo.InvariantCulture), "Bsd"));
        d.Add(new FsDetail("AdminFlags", $"0x{r[40]:X2}", "Bsd"));
        d.Add(new FsDetail("OwnerFlags", $"0x{r[41]:X2}", "Bsd"));
        d.Add(new FsDetail("FileMode", $"0{Convert.ToString(BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(42)), 8)}", "Bsd"));
        d.Add(new FsDetail("BackupDate", Time(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(28))).ToString()));
        if (!t.Folder)
        {
            d.Add(new FsDetail("FileType", FourCc(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(48))), "FinderInfo"));
            d.Add(new FsDetail("FileCreator", FourCc(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(52))), "FinderInfo"));
            d.Add(new FsDetail("DataForkSize", t.Data?.LogicalSize.ToString(CultureInfo.InvariantCulture) ?? "0", "Forks"));
            d.Add(new FsDetail("ResourceForkSize", t.Resource?.LogicalSize.ToString(CultureInfo.InvariantCulture) ?? "0", "Forks"));
        }
        else
        {
            d.Add(new FsDetail("Valence", BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)).ToString(CultureInfo.InvariantCulture)));
        }

        if (t.LinkTarget != 0)
        {
            d.Add(new FsDetail("HardLinkTarget", (t.Folder ? "dir_" : "iNode") + t.LinkTarget.ToString(CultureInfo.InvariantCulture)));
        }

        if ((r[41] & 0x20) != 0)
        {
            d.Add(new FsDetail("Compression", "decmpfs"));
        }

        return d;
    }

    private static string FourCc(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b.IndexOfAnyExcept((byte)0) < 0 ? string.Empty : Encoding.Latin1.GetString(b);
    }

    // ---- メタデータ ----

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        yield return Fixed("$VolumeHeader", 1024, 512);
        yield return Fixed("$AlternateVolumeHeader", AlternateOffset, 512);
        foreach ((string name, HfsFork fork, uint id) in new[]
        {
            ("$ExtentsOverflowFile", _extentsFork, 3u), ("$CatalogFile", _catalogFork, 4u), ("$AllocationFile", _allocationFork, 6u),
            ("$StartupFile", _startupFork, 7u), ("$AttributesFile", _attributesFork, 8u),
        })
        {
            if (fork.LogicalSize > 0)
            {
                IReadOnlyList<FsExtent> extents = ForkExtents(fork, id, 0, id != 3, out _);
                yield return new FsEntry
                {
                    Id = "hfs:meta:" + name,
                    Name = name,
                    Path = "\\" + name,
                    Flags = FsEntryFlags.Metadata,
                    Size = fork.LogicalSize,
                    AllocatedSize = (long)fork.TotalBlocks * _blockSize,
                    FileIdText = id.ToString(CultureInfo.InvariantCulture),
                    FirstCluster = extents.FirstOrDefault()?.StartCluster,
                    FragmentCount = extents.Count,
                    MetadataOffset = extents.FirstOrDefault()?.VolumeOffset,
                    Tag = extents,
                };
            }
        }

        if (Journal() is { } j)
        {
            yield return Fixed("$JournalInfoBlock", j.InfoBlockOffset, _blockSize);
            if (j.Offset > 0 && j.Size > 0 && j.Offset + j.Size <= Volume.Length)
            {
                yield return Fixed("$Journal", j.Offset, j.Size);
            }
        }
    }

    private long AlternateOffset => (long)_totalBlocks * _blockSize - 1024;

    private FsEntry Fixed(string name, long offset, long length) => new()
    {
        Id = "hfs:meta:" + name,
        Name = name,
        Path = "\\" + name,
        Flags = FsEntryFlags.Metadata,
        Size = length,
        AllocatedSize = length,
        MetadataOffset = offset,
        MetadataLength = (int)Math.Min(length, 4096),
        FirstCluster = offset / _blockSize,
        FragmentCount = 1,
        Tag = (IReadOnlyList<FsExtent>)[new FsExtent(0, length, offset, offset / _blockSize, (length + _blockSize - 1) / _blockSize)],
    };

    /// <summary>ジャーナルの情報 (表示だけ。FOR-13 の仕様 1)。</summary>
    public sealed record HfsJournal(long InfoBlockOffset, uint Flags, long Offset, long Size, uint Magic, long Start, long End, uint HeaderSize);

    public HfsJournal? Journal()
    {
        uint jib = BinaryPrimitives.ReadUInt32BigEndian(_vh.AsSpan(12));
        if (!Journaled || jib == 0 || jib >= _totalBlocks)
        {
            return null;
        }

        long at = (long)jib * _blockSize;
        byte[] b = Volume.ReadBytes(at, 52);
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(b);
        long offset = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(36));
        long size = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(44));
        byte[] h = offset > 0 && offset < Volume.Length ? Volume.ReadBytes(offset, 44) : new byte[44];
        bool little = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) == 0x12345678;
        uint U32(int o) => little ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o));
        long U64(int o) => little ? BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(o)) : BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(o));
        return new HfsJournal(at, flags, offset, size, U32(0), U64(8), U64(16), U32(36));
    }

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            var list = new List<FsRegion> { new(0, Math.Max(_blockSize, 1536), FsRegionKind.Metadata), new(AlternateOffset, 1024, FsRegionKind.Metadata) };
            foreach (FsEntry m in EnumerateMetadata())
            {
                foreach (FsExtent e in GetExtents(m))
                {
                    if (e.VolumeOffset is long v)
                    {
                        list.Add(new FsRegion(v, e.ClusterCount * _blockSize, FsRegionKind.Metadata));
                    }
                }
            }

            return list;
        }
    }

    private void CompareAlternateHeader()
    {
        if (AlternateOffset + 512 > Volume.Length || AlternateOffset < 1536)
        {
            IssueList.Add(new FsIssue(FsIssueKind.HfsAlternateHeaderMismatch, "missing"));
            return;
        }

        byte[] alt = Volume.ReadBytes(AlternateOffset, 512);
        var differ = new List<string>();
        foreach ((string name, int at, int length) in new[]
        {
            ("signature", 0, 4), ("blockSize", 40, 4), ("totalBlocks", 44, 4), ("allocationFile", 112, 80), ("extentsFile", 192, 80),
            ("catalogFile", 272, 80), ("attributesFile", 352, 80), ("startupFile", 432, 80), ("createDate", 16, 4),
        })
        {
            if (!alt.AsSpan(at, length).SequenceEqual(_vh.AsSpan(at, length)))
            {
                differ.Add(name);
            }
        }

        if (differ.Count > 0)
        {
            IssueList.Add(new FsIssue(FsIssueKind.HfsAlternateHeaderMismatch, string.Join(", ", differ)));
        }
    }

    // ---- 走査 ----

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(1);
        return [];
    }

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (FsEntry m in EnumerateMetadata(cancellationToken))
        {
            yield return m;
        }

        var queue = new Queue<(FsEntry, int)>([(_root, 0)]);
        var seen = new HashSet<uint> { RootFolderId };
        long done = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in ListDirectory(dir, cancellationToken))
            {
                yield return e;
                if (e.IsDirectory && e.Tag is HfsTag t && depth + 1 < MaxDepth && seen.Add(t.Cnid))
                {
                    queue.Enqueue((e, depth + 1));
                }
            }

            progress?.Report(Math.Min(0.99, ++done / (double)(done + queue.Count + 1)));
        }

        progress?.Report(1);
    }

    // ---- 割り当て ----

    private IAllocationMap? _allocation;

    public override IAllocationMap? Allocation => _allocation ??= _allocationFork.LogicalSize > 0
        ? new HfsAllocationMap(new CachedVolumeReader(new ExtentContentReader(Volume, ForkExtents(_allocationFork, 6, 0, true, out _), _allocationFork.LogicalSize),
            4096, 32), _totalBlocks)
        : null;

    /// <summary>割り当てファイル (1 ビットが 1 ブロック、バイトの上位ビットから)。</summary>
    private sealed class HfsAllocationMap(IVolumeReader bits, long blocks) : IAllocationMap
    {
        public long ClusterCount => blocks;

        public bool IsAllocated(long cluster)
        {
            if (cluster < 0 || cluster >= blocks)
            {
                return false;
            }

            Span<byte> b = stackalloc byte[1];
            bits.Read(cluster >> 3, b);
            return (b[0] & (0x80 >> (int)(cluster & 7))) != 0;
        }

        public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default) =>
            AllocationScan.FreeRuns(Math.Max(0, first), Math.Min(first + count, blocks), IsAllocated, c =>
            {
                Span<byte> b = stackalloc byte[1];
                bits.Read(c >> 3, b);
                return b[0] switch { 0xFF => 1, 0 => 0, _ => -1 };
            }, cancellationToken);

        public long CountFree(CancellationToken cancellationToken = default) => FreeRuns(0, blocks, cancellationToken).Sum(r => r.Count);
    }

    public override FileSystemSummary Summary
    {
        get
        {
            byte[] v = _vh;
            uint U(int at) => BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(at));
            uint created = U(16);
            var d = new List<FsDetail>
            {
                new("Signature", Encoding.ASCII.GetString(v, 0, 2), "VolumeHeader"),
                new("Version", BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("VolumeAttributes", $"0x{U(4):X8}", "VolumeHeader"),
                new("LastMountedVersion", FourCc(U(8)), "VolumeHeader"),
                new("JournalInfoBlock", U(12).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("CreateDate", Epoch.AddSeconds(created).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + " (local)", "VolumeHeader"),
                new("ModifyDate", Time(U(20)).ToString(), "VolumeHeader"),
                new("CheckedDate", Time(U(28)).ToString(), "VolumeHeader"),
                new("FileCount", U(32).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("FolderCount", U(36).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("BlockSize", _blockSize.ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("TotalBlocks", _totalBlocks.ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("FreeBlocks", U(48).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("NextCatalogId", U(64).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("WriteCount", U(68).ToString(CultureInfo.InvariantCulture), "VolumeHeader"),
                new("CatalogNodeSize", _catalog.NodeSize.ToString(CultureInfo.InvariantCulture), "Catalog"),
                new("CatalogDepth", _catalog.Depth.ToString(CultureInfo.InvariantCulture), "Catalog"),
                new("KeyCompareType", $"0x{_catalog.KeyCompareType:X2}", "Catalog"),
                new("AlternateVolumeHeader", Issues.Any(i => i.Kind == FsIssueKind.HfsAlternateHeaderMismatch) ? "mismatch" : "OK"),
            };
            if (Journal() is { } j)
            {
                d.Add(new FsDetail("JournalOffset", j.Offset.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalSize", j.Size.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalMagic", $"0x{j.Magic:X8}", "Journal"));
                d.Add(new FsDetail("JournalStart", j.Start.ToString(CultureInfo.InvariantCulture), "Journal"));
                d.Add(new FsDetail("JournalEnd", j.End.ToString(CultureInfo.InvariantCulture), "Journal"));
            }

            return new FileSystemSummary
            {
                Kind = _kind,
                TypeName = _kind == FileSystemKind.Hfsx ? "HFSX" : "HFS+",
                Label = _root.Name,
                Serial = $"{U(80 + 24):X8}{U(80 + 28):X8}",
                ClusterSize = _blockSize,
                TotalClusters = _totalBlocks,
                FreeClusters = U(48),
                Created = created == 0 ? default : new FsTimestamp(Epoch.AddSeconds(created), ZoneUnknown: true) { Recorded = Epoch.AddSeconds(created) },
                Details = d,
            };
        }
    }
}
