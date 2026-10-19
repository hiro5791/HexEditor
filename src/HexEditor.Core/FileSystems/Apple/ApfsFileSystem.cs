using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Apple;

/// <summary>APFS のボリューム (FOR-13 の仕様 2)。<see cref="Encrypted"/> は APFS_FS_UNENCRYPTED が立っていないもの (FileVault を含む。FOR-29)。</summary>
public sealed record ApfsVolume(int Index, string Name, ushort Role, bool Encrypted, ulong FsFlags, Guid Uuid, long SuperblockBlock, long OmapTreeBlock,
    ulong RootTreeOid, long SnapshotTreeBlock, ulong IncompatibleFeatures, long FileCount, long DirectoryCount, long SnapshotCount, bool Corrupt)
{
    /// <summary>役割の識別子 (訳は <c>Apfs_Role_&lt;値&gt;</c>)。</summary>
    public string RoleName => Role switch
    {
        0 => "None", 1 => "System", 2 => "User", 4 => "Recovery", 8 => "VM", 0x10 => "Preboot", 0x20 => "Installer", 0x40 => "Data",
        0x80 => "Baseband", 0xC0 => "Update", 0x100 => "Xart", 0x140 => "Hardware", 0x180 => "Backup", 0x1C0 => "Sidecar", 0x200 => "Reserved",
        0x240 => "Enterprise", 0x280 => "Prelogin", _ => $"0x{Role:X}",
    };

    /// <summary>ディレクトリエントリのキーがハッシュ付きか (大文字小文字・正規化を区別しないボリューム)。</summary>
    public bool HashedNames => (IncompatibleFeatures & 0x9) != 0;
}

/// <summary>スナップショット (一覧だけ。中身の閲覧は未決定)。</summary>
public sealed record ApfsSnapshot(ulong Xid, string Name, FsTimestamp Created, long SuperblockBlock);

/// <summary>項目の実装用の情報。</summary>
internal sealed record ApfsTag(int Volume, ulong InodeId, ulong StreamId, long Size, bool IsVolume, bool IsSnapshotFolder, bool IsSnapshot);

/// <summary>
/// APFS の基本解析 (FOR-13 の仕様 2)。コンテナのスーパーブロック (NXSB) のうちチェックサム (Fletcher-64) の合う最新のチェックポイントを選び、
/// オブジェクトマップをたどってボリュームのスーパーブロック (APSB) を列挙する。暗号化されていないボリュームのファイルシステムツリーからファイル一覧と
/// 断片を読む。暗号化されたボリュームは検出だけを行い、一覧は出さない。スナップショットは一覧だけ。圧縮ファイル・クローンの共有・Fusion Drive は
/// 区別しない (圧縮は「圧縮されています」と示す)。空き領域は空き領域マネージャで判定し、読めなければ強調を無効にする (null)。
/// </summary>
public sealed class ApfsFileSystem : FileSystemBase
{
    public const ushort ObjectTypeNxSuperblock = 0x1;
    public const ushort ObjectTypeBtree = 0x2;
    public const ushort ObjectTypeBtreeNode = 0x3;
    public const ushort ObjectTypeSpaceman = 0x5;
    public const ushort ObjectTypeOmap = 0xB;
    public const ushort ObjectTypeCheckpointMap = 0xC;
    public const ushort ObjectTypeFs = 0xD;

    public const ulong RootDirectoryId = 2;
    public const int MaxDepth = 64;
    private const int MaxTreeDepth = 16;
    private const int MaxNodes = 1_000_000;

    private readonly CachedVolumeReader _meta;
    private readonly int _blockSize;
    private readonly long _blockCount;
    private readonly byte[] _nxsb;
    private readonly ulong _xid;
    private readonly long _containerOmapTree;
    private readonly List<ApfsVolume> _volumes = [];
    private readonly List<(uint Type, ulong Oid, long Paddr)> _checkpointMap = [];
    private readonly FsEntry _root;
    private readonly object _gate = new();
    private readonly HashSet<long> _badBlocks = [];
    private IAllocationMap? _allocation;
    private bool _allocationRead;

    public ApfsFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _ = options;
        _meta = volume as CachedVolumeReader ?? new CachedVolumeReader(volume, 64 * 1024, 64);
        byte[] first = _meta.ReadBytes(0, 4096);
        if (!first.AsSpan(32, 4).SequenceEqual("NXSB"u8))
        {
            throw new FileSystemOpenException("The APFS container superblock (NXSB) was not found.", FileSystemKind.Apfs);
        }

        _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(36));
        if (_blockSize is < 4096 or > 65536 || (_blockSize & (_blockSize - 1)) != 0)
        {
            throw new FileSystemOpenException("The APFS block size is invalid.", FileSystemKind.Apfs);
        }

        byte[] block0 = _meta.ReadBytes(0, _blockSize);
        (_nxsb, _xid) = SelectCheckpoint(block0);
        _blockCount = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(40)), (ulong)(Volume.Length / _blockSize + 1));
        long omap = (long)BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(160));
        _containerOmapTree = OmapTree(omap) ?? -1;
        ReadVolumes();
        _root = new FsEntry
        {
            Id = "apfs:container",
            Name = string.Empty,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            Tag = new ApfsTag(-1, 0, 0, 0, false, false, false),
        };
    }

    public override FileSystemKind Kind => FileSystemKind.Apfs;
    public override long ClusterSize => _blockSize;
    public override long ClusterCount => _blockCount;
    public override bool UsesBlocks => true;
    public override int SectorSize => 512;
    public override long FileSystemLength => _blockCount * _blockSize;
    protected override long ClusterAreaOffset => 0;
    public override FsEntry Root => _root;

    public IReadOnlyList<ApfsVolume> Volumes => _volumes;

    /// <summary>選んだチェックポイントのトランザクション番号。</summary>
    public ulong Xid => _xid;

    private static ushort TypeOf(ReadOnlySpan<byte> obj) => (ushort)(BinaryPrimitives.ReadUInt32LittleEndian(obj[24..]) & 0xFFFF);

    private static ulong Xid0(ReadOnlySpan<byte> obj) => BinaryPrimitives.ReadUInt64LittleEndian(obj[16..]);

    /// <summary>ブロックを読み、チェックサムを確かめる (合わなければ警告して null)。</summary>
    private byte[]? ReadObject(long block, ushort? expectedType = null)
    {
        if (block <= 0 || block >= _blockCount)
        {
            return null;
        }

        byte[] b = _meta.ReadBytes(block * _blockSize, _blockSize, out bool complete);
        if (!complete || !FsChecksums.ApfsObjectValid(b))
        {
            lock (_gate)
            {
                if (_badBlocks.Add(block) && IssueList.Count < 64)
                {
                    IssueList.Add(new FsIssue(FsIssueKind.ApfsChecksum, block.ToString(CultureInfo.InvariantCulture)));
                }
            }

            return null;
        }

        return expectedType is ushort t && TypeOf(b) != t ? null : b;
    }

    // ---- チェックポイント ----

    private (byte[] Superblock, ulong Xid) SelectCheckpoint(byte[] block0)
    {
        uint descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(block0.AsSpan(104));
        long descBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(block0.AsSpan(112));
        byte[]? best = null;
        ulong bestXid = 0, newestSeen = 0;
        var maps = new List<(ulong Xid, byte[] Block)>();
        if ((descBlocks & 0x80000000) == 0 && descBlocks is > 0 and <= 65536 && descBase > 0)
        {
            for (long i = 0; i < descBlocks; i++)
            {
                byte[] b = _meta.ReadBytes((descBase + i) * _blockSize, _blockSize, out bool complete);
                if (!complete)
                {
                    continue;
                }

                bool looksSuper = b.AsSpan(32, 4).SequenceEqual("NXSB"u8);
                if (looksSuper)
                {
                    newestSeen = Math.Max(newestSeen, Xid0(b));
                }

                if (!FsChecksums.ApfsObjectValid(b))
                {
                    continue;
                }

                if (looksSuper && TypeOf(b) == ObjectTypeNxSuperblock && Xid0(b) >= bestXid)
                {
                    (best, bestXid) = (b, Xid0(b));
                }
                else if (TypeOf(b) == ObjectTypeCheckpointMap)
                {
                    maps.Add((Xid0(b), b));
                }
            }
        }

        if (best is null)
        {
            if (!FsChecksums.ApfsObjectValid(block0))
            {
                throw new FileSystemOpenException("No valid APFS checkpoint was found.", FileSystemKind.Apfs) { MessageKey = "Fs_ApfsNoCheckpoint" };
            }

            (best, bestXid) = (block0, Xid0(block0));
        }

        if (newestSeen > bestXid)
        {
            IssueList.Add(new FsIssue(FsIssueKind.ApfsOlderCheckpoint, $"{bestXid} < {newestSeen}"));
        }

        foreach ((ulong xid, byte[] map) in maps.Where(m => m.Xid == bestXid))
        {
            uint count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(36)), (uint)((_blockSize - 40) / 40));
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> m = map.AsSpan(40 + i * 40, 40);
                _checkpointMap.Add((BinaryPrimitives.ReadUInt32LittleEndian(m) & 0xFFFF, BinaryPrimitives.ReadUInt64LittleEndian(m[24..]),
                    (long)BinaryPrimitives.ReadUInt64LittleEndian(m[32..])));
            }

            _ = xid;
        }

        return (best, bestXid);
    }

    // ---- B 木 ----

    private sealed record BNode(long Block, ushort Flags, int Level, int Count, byte[] Data, int TableStart, int KeyStart, int ValueEnd)
    {
        public bool IsLeaf => (Flags & 2) != 0;
        public bool Fixed => (Flags & 4) != 0;
    }

    private BNode? ReadNode(long block)
    {
        byte[]? b = ReadObject(block);
        if (b is null)
        {
            return null;
        }

        ushort type = TypeOf(b);
        if (type is not (ObjectTypeBtree or ObjectTypeBtreeNode))
        {
            return null;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(32));
        int level = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34));
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36)), (uint)_blockSize);
        int tableOff = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(40));
        int tableLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(42));
        int tableStart = 56 + tableOff;
        int keyStart = tableStart + tableLen;
        int valueEnd = _blockSize - ((flags & 1) != 0 ? 40 : 0);
        int entry = (flags & 4) != 0 ? 4 : 8;
        if (keyStart > valueEnd || tableStart + count * entry > keyStart || level > MaxTreeDepth)
        {
            return null;
        }

        return new BNode(block, flags, level, count, b, tableStart, keyStart, valueEnd);
    }

    /// <summary>ノードの i 番目のキーと値 (範囲外なら null)。固定長は omap (キー 16、値 16、索引の値 8)。</summary>
    private static (byte[] Key, byte[] Value)? Entry(BNode n, int i)
    {
        int k, kl, v, vl;
        if (n.Fixed)
        {
            k = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 4));
            v = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 4 + 2));
            kl = 16;
            vl = n.IsLeaf ? 16 : 8;
        }
        else
        {
            k = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 8));
            kl = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 8 + 2));
            v = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 8 + 4));
            vl = BinaryPrimitives.ReadUInt16LittleEndian(n.Data.AsSpan(n.TableStart + i * 8 + 6));
            if (!n.IsLeaf)
            {
                vl = 8;
            }
        }

        int keyAt = n.KeyStart + k;
        int valueAt = n.ValueEnd - v;
        if (keyAt < n.KeyStart || keyAt + kl > n.ValueEnd || valueAt < n.KeyStart || valueAt + vl > n.ValueEnd || v == 0xFFFF)
        {
            return null;
        }

        return (n.Data.AsSpan(keyAt, kl).ToArray(), n.Data.AsSpan(valueAt, vl).ToArray());
    }

    /// <summary>
    /// <paramref name="compare"/> (キーと目的の範囲の比較。範囲内は 0) が 0 の葉のレコードを順に返す。<paramref name="resolve"/> は索引の値
    /// (子の oid) をブロック番号にする (物理の木はそのまま、仮想の木はオブジェクトマップ)。
    /// </summary>
    private IEnumerable<(byte[] Key, byte[] Value)> Range(long rootBlock, Func<byte[], int> compare, Func<ulong, long?> resolve, List<long>? bad = null)
    {
        var stack = new Stack<(long Block, int Depth)>();
        stack.Push((rootBlock, 0));
        int visited = 0;
        var seen = new HashSet<long>();
        while (stack.Count > 0 && visited++ < MaxNodes)
        {
            (long block, int depth) = stack.Pop();
            if (!seen.Add(block))
            {
                // 同じノードを 2 回たどらない (壊れた木の循環・共有への備え)。
                continue;
            }

            if (depth > MaxTreeDepth || ReadNode(block) is not { } node)
            {
                bad?.Add(block);
                continue;
            }

            if (node.IsLeaf)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    if (Entry(node, i) is { } e && compare(e.Key) == 0)
                    {
                        yield return e;
                    }
                }

                continue;
            }

            var children = new List<long>();
            for (int i = 0; i < node.Count; i++)
            {
                if (Entry(node, i) is not { } e)
                {
                    continue;
                }

                if (compare(e.Key) > 0)
                {
                    break;
                }

                if (i + 1 < node.Count && Entry(node, i + 1) is { } next && compare(next.Key) < 0)
                {
                    continue;
                }

                if (resolve(BinaryPrimitives.ReadUInt64LittleEndian(e.Value)) is long child)
                {
                    children.Add(child);
                }
            }

            // 左から順に読むため、逆順に積む。
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1));
            }
        }

    }

    private static long? Physical(ulong oid) => (long)oid;

    private long? OmapTree(long omapBlock)
    {
        byte[]? omap = ReadObject(omapBlock, ObjectTypeOmap);
        return omap is null ? null : (long)BinaryPrimitives.ReadUInt64LittleEndian(omap.AsSpan(48));
    }

    /// <summary>オブジェクトマップで仮想の oid をブロック番号にする (xid 以下で最新のもの)。</summary>
    private long? Lookup(long omapTree, ulong oid)
    {
        if (omapTree <= 0)
        {
            return null;
        }

        long? best = null;
        ulong bestXid = 0;
        foreach ((byte[] key, byte[] value) in Range(omapTree, k => BinaryPrimitives.ReadUInt64LittleEndian(k).CompareTo(oid), Physical))
        {
            ulong xid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(value);
            if (xid <= _xid && xid >= bestXid && (flags & 1) == 0)
            {
                bestXid = xid;
                best = (long)BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
            }
        }

        return best;
    }

    // ---- ボリューム ----

    private void ReadVolumes()
    {
        uint max = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_nxsb.AsSpan(180)), 100);
        for (int i = 0; i < max; i++)
        {
            ulong oid = BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(184 + i * 8));
            if (oid == 0)
            {
                continue;
            }

            long? block = Lookup(_containerOmapTree, oid);
            byte[]? apsb = block is long b ? ReadObject(b, ObjectTypeFs) : null;
            if (apsb is null || !apsb.AsSpan(32, 4).SequenceEqual("APSB"u8))
            {
                _volumes.Add(new ApfsVolume(i, $"#{i}", 0, false, 0, Guid.Empty, block ?? -1, -1, 0, -1, 0, 0, 0, 0, Corrupt: true));
                continue;
            }

            ulong fsFlags = BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(264));
            string name = Encoding.UTF8.GetString(apsb, 704, 256);
            int nul = name.IndexOf('\0');
            _volumes.Add(new ApfsVolume(i, nul >= 0 ? name[..nul] : name, BinaryPrimitives.ReadUInt16LittleEndian(apsb.AsSpan(964)), (fsFlags & 1) == 0,
                fsFlags, new Guid(apsb.AsSpan(240, 16)), block!.Value, OmapTree((long)BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(128))) ?? -1,
                BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(136)), (long)BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(152)),
                BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(56)), (long)BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(184)),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(192)), (long)BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(216)), false));
            if ((fsFlags & 1) == 0)
            {
                IssueList.Add(new FsIssue(FsIssueKind.Encrypted, _volumes[^1].Name));
            }
        }
    }

    /// <summary>ボリュームのスナップショットの一覧 (スナップショットのメタデータの木。FOR-13 の仕様 2)。</summary>
    public IReadOnlyList<ApfsSnapshot> Snapshots(ApfsVolume volume)
    {
        var list = new List<ApfsSnapshot>();
        if (volume.SnapshotTreeBlock <= 0 || volume.Encrypted)
        {
            return list;
        }

        byte[]? tree = ReadObject(volume.SnapshotTreeBlock);
        long root = tree is not null && TypeOf(tree) == ObjectTypeBtree ? volume.SnapshotTreeBlock : -1;
        if (root < 0)
        {
            return list;
        }

        // 木の全体を読み、SNAP_METADATA (種類 1) だけを使う (SNAP_NAME などのキーと並びが混ざるため)。
        foreach ((byte[] key, byte[] value) in Range(root, _ => 0, Physical))
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(key) >> 60 != 1 || value.Length < 50 || list.Count >= 10_000)
            {
                continue;
            }

            int nameLength = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(48)), value.Length - 50);
            string name = Encoding.UTF8.GetString(value, 50, Math.Max(0, nameLength)).TrimEnd('\0');
            list.Add(new ApfsSnapshot(BinaryPrimitives.ReadUInt64LittleEndian(key) & 0x0FFFFFFFFFFFFFFF, name,
                NanoTime(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(16))), (long)BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8))));
        }

        return list;
    }

    private static FsTimestamp NanoTime(ulong ns) => ns == 0 ? default : ns > (ulong)(DateTime.MaxValue - DateTime.UnixEpoch).Ticks * 100
        ? new FsTimestamp(null, Invalid: true, Raw: (long)ns)
        : new FsTimestamp(DateTime.UnixEpoch.AddTicks((long)(ns / 100)), Raw: (long)ns);

    // ---- ファイルシステムツリー ----

    private const int TypeInode = 3, TypeXattr = 4, TypeFileExtent = 8, TypeDirRecord = 9;

    private static int CompareFsKey(byte[] key, ulong objId, int type)
    {
        ulong head = BinaryPrimitives.ReadUInt64LittleEndian(key);
        int c = (head & 0x0FFFFFFFFFFFFFFF).CompareTo(objId);
        return c != 0 ? c : ((int)(head >> 60)).CompareTo(type);
    }

    private IEnumerable<(byte[] Key, byte[] Value)> FsRecords(ApfsVolume v, ulong objId, int type, List<long>? bad = null)
    {
        long? root = Lookup(v.OmapTreeBlock, v.RootTreeOid);
        if (root is not long r)
        {
            bad?.Add(-1);
            return [];
        }

        return Range(r, k => CompareFsKey(k, objId, type), oid => Lookup(v.OmapTreeBlock, oid), bad);
    }

    private sealed record Inode(ulong Id, ulong Parent, ulong PrivateId, FsTimestamp Created, FsTimestamp Modified, FsTimestamp Changed,
        FsTimestamp Accessed, uint BsdFlags, uint Owner, uint Group, ushort Mode, long Size, int Links, ulong InternalFlags);

    private Inode? ReadInode(ApfsVolume v, ulong id)
    {
        foreach ((_, byte[] value) in FsRecords(v, id, TypeInode))
        {
            if (value.Length < 92)
            {
                return null;
            }

            long size = 0;
            ReadOnlySpan<byte> x = value.AsSpan(92);
            if (x.Length >= 4)
            {
                int n = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(x), 64);
                int dataAt = 4 + n * 4;
                for (int i = 0; i < n && 4 + i * 4 + 4 <= x.Length; i++)
                {
                    byte xtype = x[4 + i * 4];
                    int xsize = BinaryPrimitives.ReadUInt16LittleEndian(x[(4 + i * 4 + 2)..]);
                    if (xtype == 8 && dataAt + 8 <= x.Length)
                    {
                        size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(x[dataAt..]), long.MaxValue);
                    }

                    dataAt += (xsize + 7) & ~7;
                }
            }

            return new Inode(id, BinaryPrimitives.ReadUInt64LittleEndian(value), BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8)),
                NanoTime(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(16))), NanoTime(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(24))),
                NanoTime(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(32))), NanoTime(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(40))),
                BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(68)), BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(72)),
                BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(76)), BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(80)), size,
                BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(56)), BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(48)));
        }

        return null;
    }

    private bool HasDecmpfs(ApfsVolume v, ulong id) =>
        FsRecords(v, id, TypeXattr).Any(r => r.Key.Length >= 10 && Encoding.UTF8.GetString(r.Key, 10, Math.Max(0, Math.Min(
            BinaryPrimitives.ReadUInt16LittleEndian(r.Key.AsSpan(8)), r.Key.Length - 10))).TrimEnd('\0') == "com.apple.decmpfs");

    private IReadOnlyList<FsExtent> StreamExtents(ApfsVolume v, ulong streamId, long size)
    {
        var list = new List<FsExtent>();
        foreach ((byte[] key, byte[] value) in FsRecords(v, streamId, TypeFileExtent))
        {
            if (key.Length < 16 || value.Length < 16 || list.Count >= 1_000_000)
            {
                continue;
            }

            long logical = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8)), long.MaxValue / 2);
            long length = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0x00FFFFFFFFFFFFFF);
            long phys = (long)BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
            if (logical >= size || length <= 0)
            {
                continue;
            }

            long len = Math.Min(length, size - logical);
            long blocks = (length + _blockSize - 1) / _blockSize;
            if (phys == 0)
            {
                list.Add(new FsExtent(logical, len, null, -1, 0));
            }
            else if (phys + blocks <= _blockCount)
            {
                list.Add(new FsExtent(logical, len, phys * _blockSize, phys, blocks));
            }
        }

        list.Sort((a, b) => a.LogicalOffset.CompareTo(b.LogicalOffset));
        return list;
    }

    private FsEntry VolumeEntry(ApfsVolume v) => new()
    {
        Id = $"apfs:vol{v.Index}",
        Name = v.Name,
        Path = "\\" + v.Name,
        Flags = FsEntryFlags.Directory | (v.Encrypted ? FsEntryFlags.Encrypted : 0) | (v.Corrupt ? FsEntryFlags.Corrupt : 0),
        Attributes = v.RoleName,
        FileIdText = v.Index.ToString(CultureInfo.InvariantCulture),
        MetadataOffset = v.SuperblockBlock > 0 ? v.SuperblockBlock * _blockSize : null,
        MetadataLength = _blockSize,
        ParentId = _root.Id,
        Corruption = v.Corrupt ? FsCorruptionKind.ChecksumMismatch : FsCorruptionKind.None,
        Tag = new ApfsTag(v.Index, RootDirectoryId, 0, 0, true, false, false),
    };

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not ApfsTag tag)
        {
            yield break;
        }

        if (tag.Volume < 0)
        {
            foreach (ApfsVolume v in _volumes)
            {
                yield return VolumeEntry(v);
            }

            yield break;
        }

        ApfsVolume vol = _volumes.First(v => v.Index == tag.Volume);
        if (vol.Encrypted || vol.Corrupt)
        {
            // 暗号化されたボリュームは検出だけ (FOR-13 の仕様 2)。
            yield break;
        }

        if (tag.IsSnapshotFolder)
        {
            foreach (ApfsSnapshot s in Snapshots(vol))
            {
                yield return new FsEntry
                {
                    Id = $"apfs:{vol.Index}:snap:{s.Xid}",
                    Name = s.Name,
                    Path = FsText.Combine(directory.Path, s.Name),
                    Flags = FsEntryFlags.Virtual,
                    Created = s.Created,
                    FileIdText = s.Xid.ToString(CultureInfo.InvariantCulture),
                    ParentId = directory.Id,
                    Tag = new ApfsTag(vol.Index, 0, 0, 0, false, false, true),
                };
            }

            yield break;
        }

        if (tag.IsVolume && Snapshots(vol).Count > 0)
        {
            yield return new FsEntry
            {
                Id = $"apfs:{vol.Index}:snapshots",
                Name = "$Snapshots",
                Path = FsText.Combine(directory.Path, "$Snapshots"),
                Flags = FsEntryFlags.Directory | FsEntryFlags.Virtual,
                ParentId = directory.Id,
                Tag = new ApfsTag(vol.Index, 0, 0, 0, false, true, false),
            };
        }

        var bad = new List<long>();
        foreach ((byte[] key, byte[] value) in FsRecords(vol, tag.InodeId, TypeDirRecord, bad))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Length < 18)
            {
                continue;
            }

            string name;
            if (vol.HashedNames)
            {
                if (key.Length < 12)
                {
                    continue;
                }

                int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(8)) & 0x3FF);
                name = Encoding.UTF8.GetString(key, 12, Math.Max(0, Math.Min(len, key.Length - 12))).TrimEnd('\0');
            }
            else
            {
                if (key.Length < 10)
                {
                    continue;
                }

                int len = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(8));
                name = Encoding.UTF8.GetString(key, 10, Math.Max(0, Math.Min(len, key.Length - 10))).TrimEnd('\0');
            }

            ulong fileId = BinaryPrimitives.ReadUInt64LittleEndian(value);
            int dtype = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(16)) & 0xF;
            yield return MakeEntry(vol, fileId, name, dtype == 4, directory);
        }

        if (bad.Count > 0)
        {
            // チェックサムの合わない・読めないノード: このフォルダに破損の印を付けて続ける (FOR-13 の「エラー」)。
            directory.Flags |= FsEntryFlags.Corrupt;
            directory.Corruption = FsCorruptionKind.ChecksumMismatch;
            directory.CorruptionDetail = "node " + string.Join(", ", bad.Distinct());
        }
    }

    private FsEntry MakeEntry(ApfsVolume vol, ulong id, string name, bool directory, FsEntry parent)
    {
        Inode? inode = ReadInode(vol, id);
        string path = FsText.Combine(parent.Path, name);
        if (inode is null)
        {
            return new FsEntry
            {
                Id = $"apfs:{vol.Index}:{id}",
                Name = name,
                Path = path,
                Flags = (directory ? FsEntryFlags.Directory : 0) | FsEntryFlags.Corrupt,
                Corruption = FsCorruptionKind.BadAttribute,
                CorruptionDetail = $"inode {id}",
                FileIdText = id.ToString(CultureInfo.InvariantCulture),
                ParentId = parent.Id,
                Tag = new ApfsTag(vol.Index, id, 0, 0, false, false, false),
            };
        }

        bool dir = directory || (inode.Mode >> 12) == 4;
        bool compressed = (inode.BsdFlags & 0x20) != 0 || !dir && HasDecmpfs(vol, id);
        IReadOnlyList<FsExtent> extents = dir ? [] : StreamExtents(vol, inode.PrivateId, inode.Size);
        return new FsEntry
        {
            Id = $"apfs:{vol.Index}:{id}",
            Name = name,
            Path = path,
            Flags = (dir ? FsEntryFlags.Directory : 0) | (compressed ? FsEntryFlags.Compressed : 0),
            Size = dir ? 0 : inode.Size,
            AllocatedSize = extents.Sum(e => e.ClusterCount) * _blockSize,
            Created = inode.Created,
            Modified = inode.Modified,
            Changed = inode.Changed,
            Accessed = inode.Accessed,
            Attributes = FsText.PosixMode(inode.Mode),
            FileIdText = id.ToString(CultureInfo.InvariantCulture),
            FirstCluster = extents.FirstOrDefault(e => e.VolumeOffset is not null)?.StartCluster,
            FragmentCount = extents.Count(e => e.VolumeOffset is not null),
            ParentId = parent.Id,
            Tag = new ApfsTag(vol.Index, id, inode.PrivateId, inode.Size, false, false, false),
        };
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry) => entry.Tag switch
    {
        ApfsTag { IsVolume: false, IsSnapshot: false, IsSnapshotFolder: false, StreamId: > 0 } t when !entry.IsDirectory && t.Volume >= 0 =>
            StreamExtents(_volumes.First(v => v.Index == t.Volume), t.StreamId, t.Size),
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
        if (entry.Tag is not ApfsTag t || t.Volume < 0)
        {
            return d;
        }

        ApfsVolume v = _volumes.First(x => x.Index == t.Volume);
        if (t.IsVolume)
        {
            d.Add(new FsDetail("VolumeName", v.Name, "Volume"));
            d.Add(new FsDetail("VolumeRole", v.RoleName, "Volume"));
            d.Add(new FsDetail("Encrypted", v.Encrypted ? "yes" : "no", "Volume"));
            d.Add(new FsDetail("FsFlags", $"0x{v.FsFlags:X}", "Volume"));
            d.Add(new FsDetail("VolumeUuid", v.Uuid.ToString("D"), "Volume"));
            d.Add(new FsDetail("FileCount", v.FileCount.ToString(CultureInfo.InvariantCulture), "Volume"));
            d.Add(new FsDetail("DirectoryCount", v.DirectoryCount.ToString(CultureInfo.InvariantCulture), "Volume"));
            d.Add(new FsDetail("SnapshotCount", v.SnapshotCount.ToString(CultureInfo.InvariantCulture), "Volume"));
            d.Add(new FsDetail("SuperblockBlock", v.SuperblockBlock.ToString(CultureInfo.InvariantCulture), "Volume"));
            return d;
        }

        if (t.IsSnapshot)
        {
            d.Add(new FsDetail("SnapshotXid", entry.FileIdText, "Snapshot"));
            return d;
        }

        if (t.InodeId > 0 && ReadInode(v, t.InodeId) is { } inode)
        {
            d.Add(new FsDetail("InodeId", inode.Id.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("ParentId", inode.Parent.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("PrivateId", inode.PrivateId.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("Owner", inode.Owner.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("Group", inode.Group.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("Mode", $"{FsText.PosixMode(inode.Mode)} (0{Convert.ToString(inode.Mode & 0xFFF, 8)})"));
            d.Add(new FsDetail("BsdFlags", $"0x{inode.BsdFlags:X8}"));
            d.Add(new FsDetail("InternalFlags", $"0x{inode.InternalFlags:X}"));
            d.Add(new FsDetail("Links", inode.Links.ToString(CultureInfo.InvariantCulture)));
            if (entry.Flags.HasFlag(FsEntryFlags.Compressed))
            {
                d.Add(new FsDetail("Compression", "decmpfs"));
            }
        }

        return d;
    }

    // ---- メタデータ ----

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        yield return Fixed("$ContainerSuperblock", 0, _blockSize);
        long descBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(112));
        uint descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(_nxsb.AsSpan(104)) & 0x7FFFFFFF;
        long dataBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(120));
        uint dataBlocks = BinaryPrimitives.ReadUInt32LittleEndian(_nxsb.AsSpan(108)) & 0x7FFFFFFF;
        if (descBase > 0 && descBase + descBlocks <= _blockCount)
        {
            yield return Fixed("$CheckpointDescriptors", descBase * _blockSize, (long)descBlocks * _blockSize);
        }

        if (dataBase > 0 && dataBase + dataBlocks <= _blockCount)
        {
            yield return Fixed("$CheckpointData", dataBase * _blockSize, (long)dataBlocks * _blockSize);
        }

        foreach (ApfsVolume v in _volumes.Where(v => v.SuperblockBlock > 0))
        {
            yield return Fixed($"$VolumeSuperblock ({v.Name})", v.SuperblockBlock * _blockSize, _blockSize);
        }
    }

    private FsEntry Fixed(string name, long offset, long length) => new()
    {
        Id = "apfs:meta:" + name,
        Name = name,
        Path = "\\" + name,
        Flags = FsEntryFlags.Metadata,
        Size = length,
        AllocatedSize = length,
        MetadataOffset = offset,
        MetadataLength = (int)Math.Min(length, _blockSize),
        FirstCluster = offset / _blockSize,
        FragmentCount = 1,
        Tag = (IReadOnlyList<FsExtent>)[new FsExtent(0, length, offset, offset / _blockSize, (length + _blockSize - 1) / _blockSize)],
    };

    public override IReadOnlyList<FsRegion> MetadataRegions => [.. EnumerateMetadata().Select(m => new FsRegion(m.MetadataOffset!.Value, m.Size, FsRegionKind.Metadata))];

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
        var seen = new HashSet<string>();
        long done = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in ListDirectory(dir, cancellationToken))
            {
                if (e.Flags.HasFlag(FsEntryFlags.Virtual))
                {
                    continue;
                }

                yield return e;
                if (e.IsDirectory && depth + 1 < MaxDepth && seen.Add(e.Id))
                {
                    queue.Enqueue((e, depth + 1));
                }
            }

            progress?.Report(Math.Min(0.99, ++done / (double)(done + queue.Count + 1)));
        }

        progress?.Report(1);
    }

    public override FsEntry? FindById(string id)
    {
        if (id == _root.Id)
        {
            return _root;
        }

        foreach (ApfsVolume v in _volumes)
        {
            if (id == $"apfs:vol{v.Index}")
            {
                return VolumeEntry(v);
            }
        }

        return null;
    }

    // ---- 空き領域 ----

    public override IAllocationMap? Allocation
    {
        get
        {
            if (!_allocationRead)
            {
                _allocationRead = true;
                _allocation = ReadSpaceManager();
            }

            return _allocation;
        }
    }

    /// <summary>空き領域マネージャ (spaceman) のチャンク情報とビットマップ。読めなければ null。</summary>
    private IAllocationMap? ReadSpaceManager()
    {
        ulong oid = BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(152));
        long? block = _checkpointMap.Where(m => m.Oid == oid && m.Type == ObjectTypeSpaceman).Select(m => (long?)m.Paddr).FirstOrDefault();
        if (block is not long b || ReadObject(b, ObjectTypeSpaceman) is not { } sm)
        {
            return null;
        }

        uint blocksPerChunk = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(36));
        uint chunksPerCib = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(40));
        long blockCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(sm.AsSpan(48));
        uint cibCount = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(64));
        uint cabCount = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(68));
        uint addrOffset = BinaryPrimitives.ReadUInt32LittleEndian(sm.AsSpan(80));
        if (blocksPerChunk == 0 || blocksPerChunk > 8u * (uint)_blockSize || chunksPerCib == 0 || cabCount != 0 || cibCount == 0 || cibCount > 4096
            || addrOffset + cibCount * 8L > _blockSize || blockCount <= 0)
        {
            return null;
        }

        var chunks = new List<(long Addr, long Count, long Bitmap)>();
        for (int i = 0; i < cibCount; i++)
        {
            long cib = (long)BinaryPrimitives.ReadUInt64LittleEndian(sm.AsSpan((int)addrOffset + i * 8));
            if (ReadObject(cib, 7) is not { } c)
            {
                return null;
            }

            uint count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(36)), (uint)((_blockSize - 40) / 32));
            for (int k = 0; k < count; k++)
            {
                ReadOnlySpan<byte> ci = c.AsSpan(40 + k * 32, 32);
                chunks.Add(((long)BinaryPrimitives.ReadUInt64LittleEndian(ci[8..]), BinaryPrimitives.ReadUInt32LittleEndian(ci[16..]),
                    (long)BinaryPrimitives.ReadUInt64LittleEndian(ci[24..])));
            }
        }

        chunks.Sort((x, y) => x.Addr.CompareTo(y.Addr));
        return new ApfsAllocationMap(this, chunks, Math.Min(blockCount, _blockCount));
    }

    /// <summary>チャンクのビットマップ (1 が使用中)。ビットマップのないチャンクは空き。</summary>
    private sealed class ApfsAllocationMap(ApfsFileSystem fs, List<(long Addr, long Count, long Bitmap)> chunks, long blocks) : IAllocationMap
    {
        private readonly CachedVolumeReader _bits = new(fs.Volume, fs._blockSize, 32);

        public long ClusterCount => blocks;

        public bool IsAllocated(long cluster)
        {
            if (cluster < 0 || cluster >= blocks)
            {
                return false;
            }

            int lo = 0, hi = chunks.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (chunks[mid].Addr <= cluster)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found < 0 || cluster >= chunks[found].Addr + chunks[found].Count)
            {
                return true;
            }

            (long addr, _, long bitmap) = chunks[found];
            if (bitmap <= 0 || bitmap >= blocks)
            {
                return false;
            }

            long bit = cluster - addr;
            Span<byte> b = stackalloc byte[1];
            _bits.Read(bitmap * fs._blockSize + (bit >> 3), b);
            return (b[0] & (1 << (int)(bit & 7))) != 0;
        }

        public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default) =>
            AllocationScan.FreeRuns(Math.Max(0, first), Math.Min(first + count, blocks), IsAllocated, c =>
            {
                int found = Find(c);
                if (found < 0 || c + 8 > chunks[found].Addr + chunks[found].Count || (c - chunks[found].Addr & 7) != 0)
                {
                    return -1;
                }

                (long addr, _, long bitmap) = chunks[found];
                if (bitmap <= 0 || bitmap >= blocks)
                {
                    return 0;
                }

                Span<byte> b = stackalloc byte[1];
                _bits.Read(bitmap * fs._blockSize + ((c - addr) >> 3), b);
                return b[0] switch { 0xFF => 1, 0 => 0, _ => -1 };
            }, cancellationToken);

        private int Find(long cluster)
        {
            int lo = 0, hi = chunks.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (chunks[mid].Addr <= cluster)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return found;
        }

        public long CountFree(CancellationToken cancellationToken = default) => FreeRuns(0, blocks, cancellationToken).Sum(r => r.Count);
    }

    public override FileSystemSummary Summary => new()
    {
        Kind = FileSystemKind.Apfs,
        TypeName = "APFS",
        Label = string.Join(", ", _volumes.Select(v => v.Name)),
        Serial = new Guid(_nxsb.AsSpan(72, 16)).ToString("D"),
        ClusterSize = _blockSize,
        TotalClusters = _blockCount,
        Details =
        [
            new("BlockSize", _blockSize.ToString(CultureInfo.InvariantCulture), "Container"),
            new("BlockCount", _blockCount.ToString(CultureInfo.InvariantCulture), "Container"),
            new("CheckpointXid", _xid.ToString(CultureInfo.InvariantCulture), "Container"),
            new("Features", $"0x{BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(48)):X}", "Container"),
            new("IncompatibleFeatures", $"0x{BinaryPrimitives.ReadUInt64LittleEndian(_nxsb.AsSpan(64)):X}", "Container"),
            new("VolumeCount", _volumes.Count.ToString(CultureInfo.InvariantCulture), "Container"),
            .. _volumes.Select(v => new FsDetail($"Volume{v.Index}", $"{v.Name} ({v.RoleName}{(v.Encrypted ? ", encrypted" : string.Empty)})", "Volumes")),
        ],
    };
}
