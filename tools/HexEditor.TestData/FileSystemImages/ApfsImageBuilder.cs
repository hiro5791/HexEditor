using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// APFS のコンテナのイメージを直接書く生成ツール (TD-FOR-APFS・TD-FOR-APFS-ENC)。ブロック 4 KiB。チェックポイントは 2 つ (xid 1 と 2) で、
/// 新しいほうのボリュームのスーパーブロックとファイルシステムツリーを使うと全部のファイルが見え、古いほう (スナップショットの元) では readme.txt だけが
/// 見える。コンテナのオブジェクトマップ (固定長のキーの B 木)、ボリュームのオブジェクトマップ、ファイルシステムツリー (仮想の oid の 2 段の B 木:
/// INODE・DSTREAM_ID・FILE_EXTENT・ハッシュ付きの DIR_REC)、スナップショットのメタデータの木、空き領域マネージャ (チャンク情報とビットマップ) を書く。
/// すべてのオブジェクトに Fletcher-64 を付ける。<c>encrypted</c> のボリュームは APFS_FS_UNENCRYPTED を立てず、ツリーのブロックは乱数にする。
/// アプリの解析の実装は使わない。
/// </summary>
public sealed class ApfsImageBuilder
{
    public const int Bs = 4096;
    public const ulong VolumeOid = 1026, FsRootOid = 1028, SpacemanOid = 0x400;
    private const uint Physical = 0x40000000, Ephemeral = 0x80000000;
    private const ulong FirstFileId = 16;

    public sealed record File(string Name, byte[] Content, DateTime Modified, int Fragments = 1)
    {
        public ulong Id { get; set; }
        public List<(long Logical, long Block, long Count)> Extents { get; } = [];
    }

    private readonly long _blocks;
    private readonly string _volumeName;
    private readonly bool _encrypted;
    private readonly DateTime _created;
    private readonly bool[] _used;
    private long _next = 100;
    private SparseImage? _image;

    public ApfsImageBuilder(long size, string volumeName, DateTime created, bool encrypted = false)
    {
        _blocks = size / Bs;
        _volumeName = volumeName;
        _created = created;
        _encrypted = encrypted;
        _used = new bool[_blocks];
    }

    public List<File> Files { get; } = [];

    /// <summary>古いチェックポイント (xid 1) にも入れるファイルの名前。</summary>
    public string OldFile { get; init; } = "readme.txt";

    public string SnapshotName { get; init; } = "snap-2026-01-15";

    /// <summary>xid 3 の壊れた (チェックサムが合わない) スーパーブロックを記述子の領域に書く (古いチェックポイントを選ぶテスト)。</summary>
    public bool TornNewestCheckpoint { get; init; }

    public File AddFile(string name, byte[] content, DateTime modified, int fragments = 1)
    {
        var f = new File(name, content, modified, fragments);
        Files.Add(f);
        return f;
    }

    private static ulong Ns(DateTime utc) => (ulong)(utc - DateTime.UnixEpoch).Ticks * 100;

    private void Use(long block) => _used[block] = true;

    private byte[] Obj(ulong oid, ulong xid, uint type, uint subtype)
    {
        byte[] b = new byte[Bs];
        U64(b, 8, (long)oid);
        U64(b, 16, (long)xid);
        U32(b, 24, type);
        U32(b, 28, subtype);
        return b;
    }

    private void Put(long block, byte[] obj, bool checksum = true)
    {
        if (checksum)
        {
            SetFletcher64(obj);
        }

        _image!.Write(block * Bs, obj);
        Use(block);
    }

    // ---- B 木のノード ----

    /// <summary>
    /// B 木のノード 1 つを作る。<paramref name="fixedKv"/> ならキー 16・値 16 (索引は 8) の固定長 (kvoff)、そうでなければ可変長 (kvloc)。
    /// 根なら末尾に btree_info を置く。
    /// </summary>
    private byte[] Node(ulong oid, ulong xid, uint type, uint subtype, bool root, bool leaf, int level, bool fixedKv, IReadOnlyList<(byte[] Key, byte[] Value)> entries,
        long keyCount = 0, long nodeCount = 1)
    {
        byte[] b = Obj(oid, xid, type, subtype);
        ushort flags = (ushort)((root ? 1 : 0) | (leaf ? 2 : 0) | (fixedKv ? 4 : 0));
        U16(b, 32, flags);
        U16(b, 34, level);
        U32(b, 36, entries.Count);
        int tocEntry = fixedKv ? 4 : 8;
        int tocLength = Math.Max(entries.Count, 8) * tocEntry;
        U16(b, 40, 0);
        U16(b, 42, tocLength);
        int keyStart = 56 + tocLength;
        int valueEnd = Bs - (root ? 40 : 0);
        int k = 0, v = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            (byte[] key, byte[] value) = entries[i];
            key.CopyTo(b, keyStart + k);
            v += value.Length;
            value.CopyTo(b, valueEnd - v);
            if (fixedKv)
            {
                U16(b, 56 + i * 4, k);
                U16(b, 56 + i * 4 + 2, v);
            }
            else
            {
                U16(b, 56 + i * 8, k);
                U16(b, 56 + i * 8 + 2, key.Length);
                U16(b, 56 + i * 8 + 4, v);
                U16(b, 56 + i * 8 + 6, value.Length);
            }

            k += (key.Length + 7) & ~7;
        }

        if (keyStart + k > valueEnd - v)
        {
            throw new InvalidOperationException("The APFS test node is full.");
        }

        U16(b, 44, keyStart + k - 56 - tocLength);
        U16(b, 46, valueEnd - v - (keyStart + k));
        U16(b, 48, 0xFFFF);
        U16(b, 52, 0xFFFF);
        if (root)
        {
            int info = Bs - 40;
            U32(b, info, fixedKv ? 0x10 : 0);
            U32(b, info + 4, Bs);
            U32(b, info + 8, fixedKv ? 16 : 0);
            U32(b, info + 12, fixedKv ? 16 : 0);
            U32(b, info + 16, entries.Count == 0 ? 0 : entries.Max(e => e.Key.Length));
            U32(b, info + 20, entries.Count == 0 ? 0 : entries.Max(e => e.Value.Length));
            U64(b, info + 24, keyCount == 0 ? entries.Count : keyCount);
            U64(b, info + 32, nodeCount);
        }

        return b;
    }

    private static byte[] OmapKey(ulong oid, ulong xid)
    {
        byte[] k = new byte[16];
        U64(k, 0, (long)oid);
        U64(k, 8, (long)xid);
        return k;
    }

    private static byte[] OmapValue(long paddr)
    {
        byte[] v = new byte[16];
        U32(v, 4, Bs);
        U64(v, 8, paddr);
        return v;
    }

    private void Omap(long block, long treeBlock, ulong xid, IReadOnlyList<(ulong Oid, ulong Xid, long Paddr)> map)
    {
        byte[] om = Obj((ulong)block, xid, Physical | 0xB, 0);
        U32(om, 40, Physical | 0x2);
        U32(om, 44, Physical | 0x2);
        U64(om, 48, treeBlock);
        Put(block, om);
        var entries = map.OrderBy(m => m.Oid).ThenBy(m => m.Xid).Select(m => (OmapKey(m.Oid, m.Xid), OmapValue(m.Paddr))).ToList();
        Put(treeBlock, Node((ulong)treeBlock, xid, Physical | 0x2, 0xB, root: true, leaf: true, 0, fixedKv: true, entries));
    }

    // ---- ファイルシステムツリーのレコード ----

    private static byte[] FsKey(ulong objId, int type, int extra)
    {
        byte[] k = new byte[8 + extra];
        U64(k, 0, (long)(objId | (ulong)type << 60));
        return k;
    }

    private static byte[] XFields(IReadOnlyList<(byte Type, byte[] Data)> fields)
    {
        int head = 4 + fields.Count * 4;
        int data = fields.Sum(f => (f.Data.Length + 7) & ~7);
        byte[] x = new byte[head + data];
        U16(x, 0, fields.Count);
        U16(x, 2, data);
        int at = head;
        for (int i = 0; i < fields.Count; i++)
        {
            x[4 + i * 4] = fields[i].Type;
            x[4 + i * 4 + 1] = 0x20;
            U16(x, 4 + i * 4 + 2, fields[i].Data.Length);
            fields[i].Data.CopyTo(x, at);
            at += (fields[i].Data.Length + 7) & ~7;
        }

        return x;
    }

    private (byte[], byte[]) InodeRecord(ulong id, ulong parent, DateTime time, ushort mode, int children, long size)
    {
        byte[] v = new byte[92];
        U64(v, 0, (long)parent);
        U64(v, 8, (long)id);
        U64(v, 16, (long)Ns(time));
        U64(v, 24, (long)Ns(time));
        U64(v, 32, (long)Ns(time));
        U64(v, 40, (long)Ns(time));
        U32(v, 56, children);
        U32(v, 72, 99);
        U32(v, 76, 99);
        U16(v, 80, mode);
        byte[]? x = null;
        if ((mode >> 12) == 8)
        {
            byte[] dstream = new byte[40];
            U64(dstream, 0, size);
            U64(dstream, 8, (size + Bs - 1) / Bs * Bs);
            x = XFields([(8, dstream)]);
        }

        return (FsKey(id, 3, 0), x is null ? v : [.. v, .. x]);
    }

    private static (byte[], byte[]) DirRecord(ulong parent, string name, ulong fileId, DateTime added, bool directory)
    {
        byte[] utf8 = [.. Encoding.UTF8.GetBytes(name), 0];
        uint hash = Crc32c(0xFFFFFFFF, Encoding.UTF32.GetBytes(name.Normalize(NormalizationForm.FormD))) & 0x3FFFFF;
        byte[] k = FsKey(parent, 9, 4 + utf8.Length);
        U32(k, 8, (hash << 10) | (uint)utf8.Length);
        utf8.CopyTo(k, 12);
        byte[] v = new byte[18];
        U64(v, 0, (long)fileId);
        U64(v, 8, (long)Ns(added));
        U16(v, 16, directory ? 4 : 8);
        return (k, v);
    }

    private static (byte[], byte[]) ExtentRecord(ulong stream, long logical, long length, long block)
    {
        byte[] k = FsKey(stream, 8, 8);
        U64(k, 8, logical);
        byte[] v = new byte[24];
        U64(v, 0, length);
        U64(v, 8, block);
        return (k, v);
    }

    private static int CompareFs((byte[] Key, byte[] Value) a, (byte[] Key, byte[] Value) b)
    {
        ulong ha = BitConverter.ToUInt64(a.Key, 0), hb = BitConverter.ToUInt64(b.Key, 0);
        int c = (ha & 0x0FFFFFFFFFFFFFFF).CompareTo(hb & 0x0FFFFFFFFFFFFFFF);
        if (c != 0)
        {
            return c;
        }

        c = (ha >> 60).CompareTo(hb >> 60);
        if (c != 0)
        {
            return c;
        }

        switch (ha >> 60)
        {
            case 9:
                // DIR_REC: ハッシュ、名前の順。
                c = (BitConverter.ToUInt32(a.Key, 8) >> 10).CompareTo(BitConverter.ToUInt32(b.Key, 8) >> 10);
                return c != 0 ? c : a.Key.AsSpan(12).SequenceCompareTo(b.Key.AsSpan(12));
            case 8:
                // FILE_EXTENT: 論理アドレスの順。
                return BitConverter.ToUInt64(a.Key, 8).CompareTo(BitConverter.ToUInt64(b.Key, 8));
            default:
                c = a.Key.AsSpan(8).SequenceCompareTo(b.Key.AsSpan(8));
                if (c != 0)
                {
                    return c;
                }

                break;
        }

        return a.Key.Length.CompareTo(b.Key.Length);
    }

    private List<(byte[], byte[])> FsRecords(IReadOnlyList<File> files)
    {
        var records = new List<(byte[], byte[])>
        {
            InodeRecord(2, 1, _created, 0x41ED, files.Count, 0),
        };
        foreach (File f in files)
        {
            records.Add(DirRecord(2, f.Name, f.Id, f.Modified, false));
            records.Add(InodeRecord(f.Id, 2, f.Modified, 0x81A4, 1, f.Content.Length));
            byte[] refcnt = new byte[4];
            U32(refcnt, 0, 1);
            records.Add((FsKey(f.Id, 6, 0), refcnt));
            foreach ((long logical, long block, long count) in f.Extents)
            {
                long length = Math.Min(count * Bs, f.Content.Length - logical);
                records.Add(ExtentRecord(f.Id, logical, (length + Bs - 1) / Bs * Bs, block));
            }
        }

        records.Sort(CompareFs);
        return records;
    }

    // ---- 書き込み ----

    public SparseImage Build()
    {
        _image = new SparseImage(_blocks * Bs);
        ulong id = FirstFileId;
        foreach (File f in Files)
        {
            f.Id = id++;
            long blocks = (f.Content.Length + Bs - 1) / Bs;
            int pieces = Math.Max(1, Math.Min(f.Fragments, (int)Math.Max(1, blocks)));
            long per = blocks == 0 ? 0 : (blocks + pieces - 1) / pieces;
            for (long logical = 0; logical < blocks; logical += per)
            {
                long n = Math.Min(per, blocks - logical);
                f.Extents.Add((logical * Bs, _next, n));
                _image.Write(_next * Bs, f.Content.AsSpan((int)(logical * Bs), (int)Math.Min(n * Bs, f.Content.Length - logical * Bs)));
                for (long k = 0; k < n; k++)
                {
                    Use(_next + k);
                }

                _next += n + (pieces > 1 ? 4 : 0);
            }
        }

        // 新しい (xid 2) ボリューム: スーパーブロック 40、オブジェクトマップ 41/42、ツリーの根 43 と葉 44〜。
        List<(byte[] Key, byte[] Value)> records = FsRecords(Files);
        var leaves = new List<List<(byte[], byte[])>>();
        const int perLeaf = 12;
        for (int i = 0; i < records.Count; i += perLeaf)
        {
            leaves.Add(records.GetRange(i, Math.Min(perLeaf, records.Count - i)));
        }

        var volumeMap = new List<(ulong, ulong, long)> { (FsRootOid, 2, 43) };
        var rootEntries = new List<(byte[], byte[])>();
        for (int i = 0; i < leaves.Count; i++)
        {
            ulong oid = FsRootOid + 1 + (ulong)i;
            long block = 44 + i;
            volumeMap.Add((oid, 2, block));
            byte[] child = new byte[8];
            U64(child, 0, (long)oid);
            rootEntries.Add((leaves[i][0].Item1, child));
            byte[] leaf = _encrypted ? RandomBlock(block) : Node(oid, 2, 0x3, 0xE, root: false, leaf: true, 0, fixedKv: false, leaves[i]);
            Put(block, leaf, checksum: !_encrypted);
        }

        byte[] fsRoot = _encrypted ? RandomBlock(43) : Node(FsRootOid, 2, 0x2, 0xE, root: true, leaf: false, 1, fixedKv: false, rootEntries, records.Count, 1 + leaves.Count);
        Put(43, fsRoot, checksum: !_encrypted);
        Omap(41, 42, 2, volumeMap);
        Put(40, Apsb(2, 41, Files.Count, snapshotTree: 60));

        // 古い (xid 1) ボリューム: スーパーブロック 50、オブジェクトマップ 51/52、ツリー 53 (readme.txt だけ)。
        List<File> oldFiles = [.. Files.Where(f => f.Name == OldFile)];
        List<(byte[], byte[])> oldRecords = FsRecords(oldFiles);
        Put(53, _encrypted ? RandomBlock(53) : Node(FsRootOid, 1, 0x2, 0xE, root: true, leaf: true, 0, fixedKv: false, oldRecords), checksum: !_encrypted);
        Omap(51, 52, 1, [(FsRootOid, 1, 53)]);
        Put(50, Apsb(1, 51, oldFiles.Count, snapshotTree: 0));

        // スナップショットのメタデータの木 (物理)。
        byte[] snapKey = FsKey(1, 1, 0);
        byte[] nameBytes = [.. Encoding.UTF8.GetBytes(SnapshotName), 0];
        byte[] snapValue = new byte[50 + nameBytes.Length];
        U64(snapValue, 8, 50);
        U64(snapValue, 16, (long)Ns(_created.AddHours(1)));
        U64(snapValue, 24, (long)Ns(_created.AddHours(1)));
        U16(snapValue, 48, nameBytes.Length);
        nameBytes.CopyTo(snapValue, 50);
        Put(60, Node(60, 2, Physical | 0x2, 0x10, root: true, leaf: true, 0, fixedKv: false, [(snapKey, snapValue)]));

        // コンテナのオブジェクトマップ (古いボリュームと新しいボリューム)。
        Omap(30, 31, 2, [(VolumeOid, 1, 50), (VolumeOid, 2, 40)]);

        // 空き領域マネージャ: 9 (エフェメラル)、チャンク情報 25、ビットマップ 26。
        for (long b = 0; b <= 26; b++)
        {
            Use(b);
        }

        byte[] bitmap = new byte[Bs];
        for (long b = 0; b < _blocks; b++)
        {
            if (_used[b])
            {
                bitmap[b >> 3] |= (byte)(1 << (int)(b & 7));
            }
        }

        long free = _used.Count(u => !u);
        _image.Write(26 * Bs, bitmap);
        byte[] cib = Obj(25, 2, Physical | 0x7, 0);
        U32(cib, 32, 0);
        U32(cib, 36, 1);
        U64(cib, 40, 2);
        U64(cib, 48, 0);
        U32(cib, 56, _blocks);
        U32(cib, 60, free);
        U64(cib, 64, 26);
        Put(25, cib);
        byte[] sm = Obj(SpacemanOid, 2, Ephemeral | 0x5, 0);
        U32(sm, 32, Bs);
        U32(sm, 36, Bs * 8);
        U32(sm, 40, (Bs - 40) / 32);
        U32(sm, 44, (Bs - 40) / 8);
        U64(sm, 48, _blocks);
        U64(sm, 56, 1);
        U32(sm, 64, 1);
        U32(sm, 68, 0);
        U64(sm, 72, free);
        U32(sm, 80, 0x180);
        U64(sm, 0x180, 25);
        Put(9, sm);

        // チェックポイント: 1 = マップ (xid 1)、2 = NXSB (xid 1)、3 = マップ (xid 2)、4 = NXSB (xid 2)。
        Put(1, CheckpointMap(1, 1));
        Put(2, Nxsb(1, 1));
        Put(3, CheckpointMap(3, 2));
        byte[] newest = Nxsb(2, 3);
        Put(4, newest);
        if (TornNewestCheckpoint)
        {
            byte[] torn = Nxsb(3, 5);
            SetFletcher64(torn);
            torn[200] ^= 0xFF;
            Put(5, torn, checksum: false);
        }

        _image.Write(0, Nxsb(2, 3));
        return _image;
    }

    private byte[] RandomBlock(long block)
    {
        byte[] b = new byte[Bs];
        new Random((int)block * 7919).NextBytes(b);
        return b;
    }

    private byte[] CheckpointMap(long block, ulong xid)
    {
        byte[] m = Obj((ulong)block, xid, Physical | 0xC, 0);
        U32(m, 32, 1);
        U32(m, 36, 1);
        U32(m, 40, Ephemeral | 0x5);
        U32(m, 48, Bs);
        U64(m, 64, (long)SpacemanOid);
        U64(m, 72, 9);
        return m;
    }

    private byte[] Nxsb(ulong xid, long descNext)
    {
        byte[] n = Obj(1, xid, Ephemeral | 0x1, 0);
        Ascii(n, 32, "NXSB");
        U32(n, 36, Bs);
        U64(n, 40, _blocks);
        U64(n, 64, 2);
        new Guid("00000000-0000-0000-0000-0000000000A0").ToByteArray().CopyTo(n, 72);
        U64(n, 88, 2000);
        U64(n, 96, (long)xid + 1);
        U32(n, 104, 8);
        U32(n, 108, 16);
        U64(n, 112, 1);
        U64(n, 120, 9);
        U32(n, 128, descNext);
        U32(n, 132, 1);
        U32(n, 136, (uint)(descNext - 2));
        U32(n, 140, 2);
        U32(n, 144, 0);
        U32(n, 148, 1);
        U64(n, 152, (long)SpacemanOid);
        U64(n, 160, 30);
        U64(n, 168, 0x401);
        U32(n, 180, 100);
        U64(n, 184, (long)VolumeOid);
        return n;
    }

    private byte[] Apsb(ulong xid, long omap, int files, long snapshotTree)
    {
        byte[] a = Obj(VolumeOid, xid, 0xD, 0);
        Ascii(a, 32, "APSB");
        U32(a, 36, 0);
        U64(a, 56, 0x8);
        U64(a, 64, (long)Ns(_created));
        U32(a, 116, 0x2);
        U32(a, 120, Physical | 0x2);
        U32(a, 124, Physical | 0x2);
        U64(a, 128, omap);
        U64(a, 136, (long)FsRootOid);
        U64(a, 152, snapshotTree);
        U64(a, 176, 2000);
        U64(a, 184, files);
        U64(a, 192, 1);
        U64(a, 216, snapshotTree > 0 ? 1 : 0);
        new Guid("00000000-0000-0000-0000-0000000000A1").ToByteArray().CopyTo(a, 240);
        U64(a, 256, (long)Ns(_created));
        U64(a, 264, _encrypted ? 0x8 : 0x1);
        Ascii(a, 272, "hexeditor-testdata");
        Encoding.UTF8.GetBytes(_volumeName).CopyTo(a, 704);
        U16(a, 964, 0x40);
        return a;
    }
}
