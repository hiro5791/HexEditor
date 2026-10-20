using System.Buffers.Binary;
using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>ext の種類 (TD-FOR-EXT2・EXT3・EXT4)。</summary>
public enum ExtVariant
{
    Ext2,
    Ext3,
    Ext4,
}

/// <summary>
/// ext2 / ext3 / ext4 のボリュームのイメージを直接書く生成ツール (TD-FOR-EXT*)。mke2fs と同じ配置の規則 (スーパーブロック 1024 バイト目、
/// sparse_super の予備、flex_bg ではビットマップと inode テーブルをグループ 0 にまとめる、予約 inode 1〜10、lost+found は inode 11) で書く。
/// ext4 はエクステント (5 個以上の断片は深さ 1 のツリー)、metadata_csum の CRC32C (スーパーブロック・グループ記述子・inode・ディレクトリの末尾・
/// エクステントのブロック・ビットマップ)、htree のディレクトリを書く。ext2/3 は直接・間接・二重間接ブロックを使う。ext3/4 はジャーナル (inode 8) を持つ。
/// 削除 (<see cref="Delete"/>) は rec_len を前のエントリに吸収させ、inode に dtime を書き、ビットマップを戻す (データは残す。ext3/4 は位置情報を消す)。
/// アプリの解析の実装は使わない。
/// </summary>
public sealed class ExtImageBuilder
{
    public const uint FakeTime = 1767225600;

    public sealed class Node
    {
        public int Inode { get; set; }
        public required string Name { get; init; }
        public Node? Parent { get; init; }
        public bool IsDirectory { get; init; }
        public string? SymlinkTarget { get; init; }
        public byte[] Content { get; init; } = [];
        public DateTime Modified { get; init; }
        public int Fragments { get; init; } = 1;
        public int GapBlocks { get; init; }
        public bool Inline { get; init; }
        public bool HTree { get; init; }
        public bool Deleted { get; set; }
        public List<Node> Children { get; } = [];

        /// <summary>データのブロックの並び (論理ブロック、物理ブロック、ブロック数)。</summary>
        public List<(long Logical, long Physical, long Count)> Runs { get; } = [];

        /// <summary>ディレクトリエントリのボリューム上の位置。</summary>
        public long EntryOffset { get; set; } = -1;

        /// <summary>間接ブロック・エクステントのブロックなど、データ以外のブロック。</summary>
        public List<long> MapBlocks { get; } = [];

        public string Path => Parent is null ? "\\" : (Parent.Parent is null ? "\\" : Parent.Path + "\\") + Name;
    }

    private readonly ExtVariant _variant;
    private readonly long _blockCount;
    private readonly int _bs;
    private readonly int _bpg;
    private readonly int _ipg;
    private readonly int _inodeSize = 256;
    private readonly int _groups;
    private readonly int _descSize;
    private readonly int _gdtBlocks;
    private readonly bool _csum;
    private readonly bool _flex;
    private readonly bool _inlineFeature;
    private readonly int _journalBlocks;
    private readonly Guid _uuid;
    private readonly string _label;
    private readonly List<Node> _all = [];
    private readonly bool[] _used;
    private readonly bool[] _inodeUsed;
    private long _cursor;
    private int _nextInode = 12;
    private SparseImage? _image;
    private readonly long[] _blockBitmap, _inodeBitmap, _inodeTable;
    private readonly int _itableBlocks;
    private readonly byte[] _hashSeed;

    public ExtImageBuilder(ExtVariant variant, long size, Guid uuid, string label, int blockSize = 4096, int blocksPerGroup = 4096, int inodesPerGroup = 512,
        bool inlineData = false, int journalBlocks = 1024)
    {
        _variant = variant;
        _bs = blockSize;
        _blockCount = size / blockSize;
        _bpg = blocksPerGroup;
        _ipg = inodesPerGroup;
        _uuid = uuid;
        _label = label;
        _csum = variant == ExtVariant.Ext4;
        _flex = variant == ExtVariant.Ext4;
        _inlineFeature = inlineData && variant == ExtVariant.Ext4;
        _journalBlocks = variant == ExtVariant.Ext2 ? 0 : journalBlocks;
        _descSize = variant == ExtVariant.Ext4 ? 64 : 32;
        long firstData = blockSize == 1024 ? 1 : 0;
        _groups = (int)((_blockCount - firstData + _bpg - 1) / _bpg);
        _gdtBlocks = (_groups * _descSize + _bs - 1) / _bs;
        _itableBlocks = (_ipg * _inodeSize + _bs - 1) / _bs;
        _used = new bool[_blockCount];
        _inodeUsed = new bool[_groups * _ipg + 1];
        _blockBitmap = new long[_groups];
        _inodeBitmap = new long[_groups];
        _inodeTable = new long[_groups];
        _hashSeed = uuid.ToByteArray();
        Root = new Node { Name = string.Empty, IsDirectory = true, Modified = DateTime.UnixEpoch.AddSeconds(FakeTime), Inode = 2 };
        LostFound = new Node { Name = "lost+found", Parent = Root, IsDirectory = true, Modified = Root.Modified, Inode = 11 };
        Root.Children.Add(LostFound);
        LayoutMetadata();
    }

    public Node Root { get; }

    public Node LostFound { get; }

    public int BlockSize => _bs;

    public int GroupCount => _groups;

    public int InodeSize => _inodeSize;

    public long FirstDataBlock => _bs == 1024 ? 1 : 0;

    public long InodeTable(int group) => _inodeTable[group];

    /// <summary>inode のボリューム上の位置。</summary>
    public long InodeOffset(int inode) => _inodeTable[(inode - 1) / _ipg] * _bs + (long)((inode - 1) % _ipg) * _inodeSize;

    public IReadOnlyList<Node> Nodes => _all;

    public Node AddFile(Node dir, string name, byte[] content, DateTime modified, int fragments = 1, int gapBlocks = 16, bool inline = false)
    {
        var node = new Node
        {
            Name = name, Parent = dir, Content = content, Modified = modified, Fragments = fragments, GapBlocks = gapBlocks, Inline = inline && _inlineFeature,
            Inode = _nextInode++,
        };
        dir.Children.Add(node);
        _all.Add(node);
        return node;
    }

    public Node AddDirectory(Node dir, string name, DateTime modified, bool htree = false)
    {
        var node = new Node { Name = name, Parent = dir, IsDirectory = true, Modified = modified, HTree = htree, Inode = _nextInode++ };
        dir.Children.Add(node);
        _all.Add(node);
        return node;
    }

    public Node AddSymlink(Node dir, string name, string target, DateTime modified)
    {
        var node = new Node
        {
            Name = name, Parent = dir, SymlinkTarget = target, Content = Encoding.UTF8.GetBytes(target), Modified = modified, Inode = _nextInode++,
        };
        dir.Children.Add(node);
        _all.Add(node);
        return node;
    }

    /// <summary>削除する (イメージの作成の最後に行う)。</summary>
    public void Delete(Node node) => node.Deleted = true;

    // ---- 配置 ----

    private bool HasSuper(int g)
    {
        if (g <= 1)
        {
            return true;
        }

        foreach (int b in new[] { 3, 5, 7 })
        {
            int n = g;
            while (n % b == 0)
            {
                n /= b;
            }

            if (n == 1)
            {
                return true;
            }
        }

        return false;
    }

    private long GroupStart(int g) => FirstDataBlock + (long)g * _bpg;

    private void LayoutMetadata()
    {
        for (long b = 0; b <= FirstDataBlock; b++)
        {
            _used[b] = true;
        }

        for (int g = 0; g < _groups; g++)
        {
            if (HasSuper(g))
            {
                for (long b = 0; b < 1 + _gdtBlocks; b++)
                {
                    _used[GroupStart(g) + b] = true;
                }
            }
        }

        long next = GroupStart(0) + 1 + _gdtBlocks;
        for (int g = 0; g < _groups; g++)
        {
            if (_flex)
            {
                _blockBitmap[g] = next + g;
                _inodeBitmap[g] = next + _groups + g;
                _inodeTable[g] = next + 2L * _groups + (long)g * _itableBlocks;
            }
            else
            {
                long b = GroupStart(g) + (HasSuper(g) ? 1 + _gdtBlocks : 0);
                _blockBitmap[g] = b;
                _inodeBitmap[g] = b + 1;
                _inodeTable[g] = b + 2;
            }

            _used[_blockBitmap[g]] = true;
            _used[_inodeBitmap[g]] = true;
            for (int i = 0; i < _itableBlocks; i++)
            {
                _used[_inodeTable[g] + i] = true;
            }
        }

        _cursor = _flex ? _inodeTable[_groups - 1] + _itableBlocks : _inodeTable[0] + _itableBlocks;
        for (int i = 1; i <= 11; i++)
        {
            _inodeUsed[i] = true;
        }
    }

    /// <summary>連続したブロックを確保する (カーソルから先の最初の空き)。</summary>
    private long Alloc(long count)
    {
        for (long start = _cursor; start + count <= _blockCount; start++)
        {
            bool free = true;
            for (long k = 0; k < count; k++)
            {
                if (_used[start + k])
                {
                    free = false;
                    start += k;
                    break;
                }
            }

            if (free)
            {
                for (long k = 0; k < count; k++)
                {
                    _used[start + k] = true;
                }

                _cursor = start + count;
                return start;
            }
        }

        throw new InvalidOperationException("The ext image is full.");
    }

    private void AllocData(Node node, long blocks)
    {
        if (blocks == 0)
        {
            return;
        }

        int pieces = Math.Max(1, node.Fragments);
        long per = (blocks + pieces - 1) / pieces;
        long logical = 0;
        for (int p = 0; p < pieces && logical < blocks; p++)
        {
            long n = Math.Min(per, blocks - logical);
            long start = Alloc(n);
            node.Runs.Add((logical, start, n));
            logical += n;
            if (p < pieces - 1 && node.GapBlocks > 0)
            {
                // 一時的な詰め物 (後で空ける) で断片の間に隙間を置く。
                _cursor += node.GapBlocks;
            }
        }
    }

    // ---- 書き込み ----

    private uint CsumSeed => (uint)Crc32c(0xFFFFFFFF, _uuid.ToByteArray(bigEndian: true));

    private uint InodeSeed(int inode, uint generation) => Crc32c(Crc32c(CsumSeed, (uint)inode), generation);

    public SparseImage Build()
    {
        _image = new SparseImage(_blockCount * _bs);
        var dirBlocks = new Dictionary<Node, List<byte[]>>();
        Node? journal = null;
        if (_journalBlocks > 0)
        {
            journal = new Node { Name = "<journal>", Inode = 8, Modified = Root.Modified, Content = JournalContent() };
            AllocData(journal, _journalBlocks);
        }

        // ディレクトリの内容を先に組み立てる (エントリの位置を決めるため、ブロックの確保もここで行う)。
        var order = new List<Node> { Root, LostFound };
        order.AddRange(_all);
        foreach (Node n in order)
        {
            if (n.IsDirectory)
            {
                dirBlocks[n] = BuildDirectory(n);
                AllocData(n, dirBlocks[n].Count);
            }
            else if (n.SymlinkTarget is not null && n.Content.Length < 60 || n.Inline)
            {
                // データは inode の中。
            }
            else
            {
                AllocData(n, (n.Content.Length + _bs - 1) / _bs);
            }
        }

        // 間接ブロック・エクステントのブロック。
        foreach (Node n in order.Append(journal).OfType<Node>())
        {
            AllocMap(n);
        }

        // 内容を書く。
        foreach (Node n in order.Append(journal).OfType<Node>())
        {
            if (n.IsDirectory)
            {
                WriteDirectory(n, dirBlocks[n]);
            }
            else
            {
                WriteRuns(n, n.Content);
            }
        }

        // 削除: エントリの rec_len を前に吸収させ、ビットマップを戻す (データは残す)。
        foreach (Node n in _all.Where(n => n.Deleted))
        {
            foreach ((_, long phys, long count) in n.Runs)
            {
                for (long k = 0; k < count; k++)
                {
                    _used[phys + k] = false;
                }
            }

            foreach (long b in n.MapBlocks)
            {
                _used[b] = false;
            }
        }

        foreach (Node n in order.Where(n => n.IsDirectory))
        {
            WriteDirectory(n, dirBlocks[n], applyDeletes: true);
        }

        foreach (Node n in order.Append(journal).OfType<Node>())
        {
            _inodeUsed[n.Inode] = !n.Deleted;
            WriteInode(n);
        }

        WriteGroupsAndSuperblocks();
        return _image;
    }

    private byte[] JournalContent()
    {
        byte[] j = new byte[_journalBlocks * _bs];
        Span<byte> sb = j.AsSpan(0, 1024);
        B32(sb, 0, 0xC03B3998);
        B32(sb, 4, 4);
        B32(sb, 8, 0);
        B32(sb, 12, _bs);
        B32(sb, 16, _journalBlocks);
        B32(sb, 20, 1);
        B32(sb, 24, 3);
        B32(sb, 28, 0);
        _uuid.ToByteArray(bigEndian: true).CopyTo(sb[48..]);
        B32(sb, 64, 1);
        _uuid.ToByteArray(bigEndian: true).CopyTo(sb[0x100..]);

        // 2 つの完了したトランザクション (1: 記述子・データ・コミット、2: 取り消し・コミット)。
        void Header(int block, int type, int sequence)
        {
            Span<byte> h = j.AsSpan(block * _bs, 12);
            B32(h, 0, 0xC03B3998);
            B32(h, 4, type);
            B32(h, 8, sequence);
        }

        Header(1, 1, 1);
        Encoding.ASCII.GetBytes("JOURNAL-DATA-BLOCK").CopyTo(j, 2 * _bs);
        Header(3, 2, 1);
        Header(4, 5, 2);
        Header(5, 2, 2);
        return j;
    }

    private static byte FileType(Node n) => n.IsDirectory ? (byte)2 : n.SymlinkTarget is not null ? (byte)7 : (byte)1;

    private sealed record DirSlot(Node? Child, string Name, int Inode, byte Type, int Block, int Offset, int RecLen);

    private readonly Dictionary<Node, List<DirSlot>> _slots = [];

    private int Tail => _csum ? 12 : 0;

    /// <summary>ディレクトリのブロックの中身を組み立てる (エントリの位置と rec_len を決める)。htree なら 0 番目のブロックを dx_root にする。</summary>
    private List<byte[]> BuildDirectory(Node dir)
    {
        var entries = new List<(Node? Child, string Name, int Inode, byte Type)>();
        int parent = dir.Parent?.Inode ?? 2;
        if (!dir.HTree)
        {
            entries.Add((null, ".", dir.Inode, 2));
            entries.Add((null, "..", parent, 2));
        }

        foreach (Node c in dir.Children)
        {
            entries.Add((c, c.Name, c.Inode, FileType(c)));
        }

        var blocks = new List<List<(Node? Child, string Name, int Inode, byte Type, int Offset)>>();
        var current = new List<(Node?, string, int, byte, int)>();
        int pos = 0;
        int limit = _bs - Tail;
        foreach (var e in entries)
        {
            int size = (8 + Encoding.UTF8.GetByteCount(e.Name) + 3) & ~3;
            if (pos + size > limit)
            {
                blocks.Add(current);
                current = [];
                pos = 0;
            }

            current.Add((e.Child, e.Name, e.Inode, e.Type, pos));
            pos += size;
        }

        blocks.Add(current);
        if (dir == LostFound)
        {
            // mke2fs と同じく lost+found は 4 ブロック (16 KiB) にする。
            while (blocks.Count < 4)
            {
                blocks.Add([]);
            }
        }

        var slots = new List<DirSlot>();
        int first = dir.HTree ? 1 : 0;
        for (int b = 0; b < blocks.Count; b++)
        {
            var list = blocks[b];
            for (int i = 0; i < list.Count; i++)
            {
                int end = i + 1 < list.Count ? list[i + 1].Item5 : limit;
                slots.Add(new DirSlot(list[i].Item1, list[i].Item2, list[i].Item3, list[i].Item4, b + first, list[i].Item5, end - list[i].Item5));
            }
        }

        _slots[dir] = slots;
        var result = new List<byte[]>();
        for (int b = 0; b < blocks.Count + first; b++)
        {
            result.Add(new byte[_bs]);
        }

        return result;
    }

    private void WriteDirectory(Node dir, List<byte[]> blocks, bool applyDeletes = false)
    {
        List<DirSlot> slots = _slots[dir];
        foreach (byte[] b in blocks)
        {
            Array.Clear(b);
        }

        int first = dir.HTree ? 1 : 0;
        for (int b = first; b < blocks.Count; b++)
        {
            if (!slots.Any(s => s.Block == b))
            {
                // 空のブロック (inode 0、rec_len はブロック全体)。
                U16(blocks[b], 4, _bs - Tail);
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            DirSlot s = slots[i];
            byte[] block = blocks[s.Block];
            byte[] name = Encoding.UTF8.GetBytes(s.Name);
            U32(block, s.Offset, s.Inode);
            U16(block, s.Offset + 4, s.RecLen);
            block[s.Offset + 6] = (byte)name.Length;
            block[s.Offset + 7] = s.Type;
            name.CopyTo(block, s.Offset + 8);
            if (s.Child is not null)
            {
                s.Child.EntryOffset = BlockOf(dir, s.Block) * _bs + s.Offset;
            }
        }

        if (applyDeletes)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                DirSlot s = slots[i];
                if (s.Child is not { Deleted: true })
                {
                    continue;
                }

                byte[] block = blocks[s.Block];
                DirSlot? prev = i > 0 && slots[i - 1].Block == s.Block ? slots[i - 1] : null;
                if (prev is not null)
                {
                    // 前のエントリの rec_len に吸収させる (名前と inode 番号は残る)。
                    int recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(prev.Offset + 4));
                    U16(block, prev.Offset + 4, recLen + BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(s.Offset + 4)));
                }
                else
                {
                    // ブロックの先頭のエントリは inode を 0 にする。
                    U32(block, s.Offset, 0);
                }
            }
        }

        if (dir.HTree)
        {
            WriteDxRoot(dir, blocks);
        }

        for (int b = first; b < blocks.Count; b++)
        {
            if (_csum)
            {
                byte[] block = blocks[b];
                int t = _bs - 12;
                U32(block, t, 0);
                U16(block, t + 4, 12);
                block[t + 6] = 0;
                block[t + 7] = 0xDE;
                U32(block, t + 8, Crc32c(InodeSeed(dir.Inode, (uint)dir.Inode), block.AsSpan(0, t)));
            }
        }

        WriteRuns(dir, [.. blocks.SelectMany(b => b)]);
    }

    private void WriteDxRoot(Node dir, List<byte[]> blocks)
    {
        byte[] root = blocks[0];
        U32(root, 0, dir.Inode);
        U16(root, 4, 12);
        root[6] = 1;
        root[7] = 2;
        root[8] = (byte)'.';
        U32(root, 12, dir.Parent!.Inode);
        U16(root, 16, _bs - 12);
        root[18] = 2;
        root[19] = 2;
        root[20] = (byte)'.';
        root[21] = (byte)'.';

        // dx_root_info: reserved、hash_version 1 (half_md4)、info_length 8、indirect_levels 0。
        root[0x1C] = 1;
        root[0x1D] = 8;
        int leaves = blocks.Count - 1;
        int limit = (_bs - 0x20 - (_csum ? 8 : 0)) / 8;
        U16(root, 0x20, limit);
        U16(root, 0x22, leaves);
        U32(root, 0x24, 1);
        for (int i = 1; i < leaves; i++)
        {
            U32(root, 0x20 + i * 8, (uint)(i * (0xF0000000L / leaves)) & ~1u);
            U32(root, 0x24 + i * 8, i + 1);
        }

        if (_csum)
        {
            int tail = 0x20 + limit * 8;
            uint c = Crc32c(InodeSeed(dir.Inode, (uint)dir.Inode), root.AsSpan(0, 0x20 + leaves * 8));
            c = Crc32c(c, root.AsSpan(tail, 4));
            U32(root, tail + 4, c);
        }
    }

    private long BlockOf(Node node, long logical)
    {
        foreach ((long l, long p, long c) in node.Runs)
        {
            if (logical >= l && logical < l + c)
            {
                return p + (logical - l);
            }
        }

        throw new InvalidOperationException("block not mapped");
    }

    private void WriteRuns(Node node, byte[] content)
    {
        foreach ((long logical, long physical, long count) in node.Runs)
        {
            long from = logical * _bs;
            int n = (int)Math.Min(count * _bs, content.Length - from);
            if (n > 0)
            {
                _image!.Write(physical * _bs, content.AsSpan((int)from, n));
            }
        }
    }

    // ---- 間接ブロックとエクステント ----

    private readonly Dictionary<Node, byte[]> _iblock = [];

    private bool UsesExtents(Node n) => _variant == ExtVariant.Ext4 && !n.Inline && !(n.SymlinkTarget is not null && n.Content.Length < 60);

    private void AllocMap(Node n)
    {
        byte[] iblock = new byte[60];
        _iblock[n] = iblock;
        if (n.Inline || n.SymlinkTarget is not null && n.Content.Length < 60)
        {
            n.Content.AsSpan(0, Math.Min(60, n.Content.Length)).CopyTo(iblock);
            return;
        }

        if (UsesExtents(n))
        {
            var extents = new List<(long Logical, long Physical, long Count)>();
            foreach ((long l, long p, long c) in n.Runs)
            {
                for (long k = 0; k < c; k += 32768)
                {
                    extents.Add((l + k, p + k, Math.Min(32768, c - k)));
                }
            }

            U16(iblock, 0, 0xF30A);
            U16(iblock, 4, 4);
            if (extents.Count <= 4)
            {
                U16(iblock, 2, extents.Count);
                U16(iblock, 6, 0);
                for (int i = 0; i < extents.Count; i++)
                {
                    WriteExtent(iblock.AsSpan(12 + i * 12), extents[i]);
                }

                return;
            }

            // 深さ 1: inode の中に索引 1 つ、葉のブロック 1 つ。
            long leaf = Alloc(1);
            n.MapBlocks.Add(leaf);
            U16(iblock, 2, 1);
            U16(iblock, 6, 1);
            U32(iblock, 12, 0);
            U32(iblock, 16, leaf);
            U16(iblock, 20, (int)(leaf >> 32));
            byte[] block = new byte[_bs];
            int max = (_bs - 12) / 12;
            U16(block, 0, 0xF30A);
            U16(block, 2, extents.Count);
            U16(block, 4, max);
            U16(block, 6, 0);
            for (int i = 0; i < extents.Count; i++)
            {
                WriteExtent(block.AsSpan(12 + i * 12), extents[i]);
            }

            if (_csum)
            {
                U32(block, 12 + max * 12, Crc32c(InodeSeed(n.Inode, (uint)n.Inode), block.AsSpan(0, 12 + max * 12)));
            }

            _pending.Add((leaf, block));
            return;
        }

        // 直接・間接・二重間接ブロック。
        var blocks = new List<long>();
        foreach ((long l, long p, long c) in n.Runs)
        {
            for (long k = 0; k < c; k++)
            {
                blocks.Add(p + k);
            }
        }

        int per = _bs / 4;
        for (int i = 0; i < Math.Min(12, blocks.Count); i++)
        {
            U32(iblock, i * 4, blocks[i]);
        }

        if (blocks.Count > 12)
        {
            long ind = Alloc(1);
            n.MapBlocks.Add(ind);
            U32(iblock, 48, ind);
            _pending.Add((ind, Pointers(blocks, 12, per)));
        }

        if (blocks.Count > 12 + per)
        {
            long dind = Alloc(1);
            n.MapBlocks.Add(dind);
            U32(iblock, 52, dind);
            byte[] top = new byte[_bs];
            int start = 12 + per;
            for (int i = 0; start + i * per < blocks.Count; i++)
            {
                if (i >= per)
                {
                    throw new NotSupportedException("triple indirect blocks are not needed by the test images");
                }

                long ind = Alloc(1);
                n.MapBlocks.Add(ind);
                U32(top, i * 4, ind);
                _pending.Add((ind, Pointers(blocks, start + i * per, per)));
            }

            _pending.Add((dind, top));
        }
    }

    private readonly List<(long Block, byte[] Data)> _pending = [];

    private byte[] Pointers(List<long> blocks, int from, int per)
    {
        byte[] b = new byte[_bs];
        for (int i = 0; i < per && from + i < blocks.Count; i++)
        {
            U32(b, i * 4, blocks[from + i]);
        }

        return b;
    }

    private static void WriteExtent(Span<byte> e, (long Logical, long Physical, long Count) x)
    {
        U32(e, 0, x.Logical);
        U16(e, 4, (int)x.Count);
        U16(e, 6, (int)(x.Physical >> 32));
        U32(e, 8, x.Physical);
    }

    // ---- inode ----

    private static uint Extra(DateTime t) => (uint)((t - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerSecond * 100) << 2;

    private void WriteInode(Node n)
    {
        byte[] raw = new byte[_inodeSize];
        bool fastLink = n.SymlinkTarget is not null && n.Content.Length < 60;
        int mode = n.IsDirectory ? 0x41ED : n.SymlinkTarget is not null ? 0xA1FF : n.Inode == 8 ? 0x8180 : 0x81A4;
        long size = n.IsDirectory ? n.Runs.Sum(r => r.Count) * _bs : n.Content.Length;
        int links = n.IsDirectory ? 2 + n.Children.Count(c => c.IsDirectory && !c.Deleted) : 1;
        long seconds = Unix(n.Modified);
        U16(raw, 0, mode);
        U32(raw, 4, size);
        U32(raw, 8, seconds);
        U32(raw, 0xC, seconds);
        U32(raw, 0x10, seconds);
        U16(raw, 0x1A, n.Deleted ? 0 : links);
        long sectors = (n.Runs.Sum(r => r.Count) + n.MapBlocks.Count) * (_bs / 512);
        U32(raw, 0x1C, sectors);
        uint flags = 0;
        if (UsesExtents(n))
        {
            flags |= 0x80000;
        }

        if (n.HTree)
        {
            flags |= 0x1000;
        }

        if (n.Inline)
        {
            flags |= 0x10000000;
        }

        U32(raw, 0x20, flags);
        _iblock[n].CopyTo(raw, 0x28);
        U32(raw, 0x64, n.Inode);
        U32(raw, 0x6C, size >> 32);
        U16(raw, 0x80, 32);
        U32(raw, 0x84, Extra(n.Modified));
        U32(raw, 0x88, Extra(n.Modified));
        U32(raw, 0x8C, Extra(n.Modified));
        U32(raw, 0x90, seconds);
        U32(raw, 0x94, Extra(n.Modified));
        if (n.Inline)
        {
            // system.data の拡張属性 (値は空)。
            int at = 128 + 32;
            U32(raw, at, 0xEA020000);
            raw[at + 4] = 4;
            raw[at + 5] = 7;
            Ascii(raw, at + 4 + 16, "data");
        }

        if (n.Deleted)
        {
            U32(raw, 0x14, seconds + 3600);
            if (_variant != ExtVariant.Ext2)
            {
                // ext3/4 の削除は位置情報を消す (ext4 はエクステントのヘッダーだけを残す)。
                Array.Clear(raw, 0x28, 60);
                U32(raw, 4, 0);
                U32(raw, 0x1C, 0);
                if (UsesExtents(n))
                {
                    U16(raw, 0x28, 0xF30A);
                    U16(raw, 0x2C, 4);
                }
            }
        }

        if (_csum)
        {
            uint seed = InodeSeed(n.Inode, (uint)n.Inode);
            uint c = Crc32c(seed, raw.AsSpan(0, 0x7C));
            c = Crc32c(c, [0, 0]);
            c = Crc32c(c, raw.AsSpan(0x7E, 128 - 0x7E));
            c = Crc32c(c, raw.AsSpan(128, 2));
            c = Crc32c(c, [0, 0]);
            c = Crc32c(c, raw.AsSpan(0x84));
            U16(raw, 0x7C, (int)(c & 0xFFFF));
            U16(raw, 0x82, (int)(c >> 16));
        }

        _image!.Write(InodeOffset(n.Inode), raw);
    }

    // ---- グループ記述子とスーパーブロック ----

    private void WriteGroupsAndSuperblocks()
    {
        foreach ((long block, byte[] data) in _pending)
        {
            _image!.Write(block * _bs, data);
        }

        byte[] gdt = new byte[_gdtBlocks * _bs];
        long freeBlocks = 0, freeInodes = 0;
        for (int g = 0; g < _groups; g++)
        {
            long start = GroupStart(g);
            long count = Math.Min(_bpg, _blockCount - start);
            byte[] bitmap = new byte[_bs];
            long free = 0;
            bool anyData = false;
            for (long b = 0; b < _bpg; b++)
            {
                bool used = b >= count || _used[start + b];
                if (used)
                {
                    bitmap[b >> 3] |= (byte)(1 << (int)(b & 7));
                }
                else
                {
                    free++;
                }
            }

            for (long b = _bpg; b < _bs * 8L; b++)
            {
                bitmap[b >> 3] |= (byte)(1 << (int)(b & 7));
            }

            for (long b = 0; b < count && !anyData; b++)
            {
                anyData = _used[start + b] && !IsGroupMetadata(start + b);
            }

            byte[] ibitmap = new byte[_bs];
            int maxUsed = 0;
            long ifree = 0;
            for (int i = 0; i < _ipg; i++)
            {
                int number = g * _ipg + i + 1;
                if (_inodeUsed[number])
                {
                    ibitmap[i >> 3] |= (byte)(1 << (i & 7));
                    maxUsed = i + 1;
                }
                else
                {
                    ifree++;
                }
            }

            for (int i = _ipg; i < _bs * 8; i++)
            {
                ibitmap[i >> 3] |= (byte)(1 << (i & 7));
            }

            ushort flags = 0;
            bool blockUninit = _csum && g > 0 && !anyData;
            bool inodeUninit = _csum && maxUsed == 0;
            if (_csum)
            {
                flags |= 0x4;
            }

            if (blockUninit)
            {
                flags |= 0x2;
            }
            else
            {
                _image!.Write(_blockBitmap[g] * _bs, bitmap);
            }

            if (inodeUninit)
            {
                flags |= 0x1;
            }
            else
            {
                _image!.Write(_inodeBitmap[g] * _bs, ibitmap);
            }

            freeBlocks += free;
            freeInodes += ifree;
            Span<byte> d = gdt.AsSpan(g * _descSize, _descSize);
            U32(d, 0, _blockBitmap[g]);
            U32(d, 4, _inodeBitmap[g]);
            U32(d, 8, _inodeTable[g]);
            U16(d, 12, (int)free);
            U16(d, 14, (int)ifree);
            U16(d, 16, CountDirs(g));
            U16(d, 18, flags);
            if (_csum)
            {
                uint bc = blockUninit ? 0 : Crc32c(CsumSeed, bitmap.AsSpan(0, _bpg / 8));
                uint ic = inodeUninit ? 0 : Crc32c(CsumSeed, ibitmap.AsSpan(0, _ipg / 8));
                U16(d, 24, (int)(bc & 0xFFFF));
                U16(d, 26, (int)(ic & 0xFFFF));
                U16(d, 28, _ipg - maxUsed);
                if (_descSize >= 64)
                {
                    U16(d, 0x38, (int)(bc >> 16));
                    U16(d, 0x3A, (int)(ic >> 16));
                }

                uint c = Crc32c(CsumSeed, (uint)g);
                c = Crc32c(c, d[..30]);
                c = Crc32c(c, [0, 0]);
                c = Crc32c(c, d[32..]);
                U16(d, 30, (int)(c & 0xFFFF));
            }
        }

        for (int g = 0; g < _groups; g++)
        {
            if (!HasSuper(g))
            {
                continue;
            }

            byte[] sb = Superblock(g, freeBlocks, freeInodes);
            long start = GroupStart(g) * _bs;
            _image!.Write(g == 0 ? 1024 : start, sb);
            _image.Write(start + _bs, gdt);
        }
    }

    private bool IsGroupMetadata(long block)
    {
        for (int g = 0; g < _groups; g++)
        {
            if (block == _blockBitmap[g] || block == _inodeBitmap[g] || block >= _inodeTable[g] && block < _inodeTable[g] + _itableBlocks)
            {
                return true;
            }

            if (HasSuper(g) && block >= GroupStart(g) && block < GroupStart(g) + 1 + _gdtBlocks)
            {
                return true;
            }
        }

        return block <= FirstDataBlock;
    }

    private int CountDirs(int g)
    {
        int n = 0;
        foreach (Node node in new[] { Root, LostFound }.Concat(_all))
        {
            if (node.IsDirectory && !node.Deleted && (node.Inode - 1) / _ipg == g)
            {
                n++;
            }
        }

        return n;
    }

    private byte[] Superblock(int group, long freeBlocks, long freeInodes)
    {
        byte[] sb = new byte[1024];
        U32(sb, 0, (long)_groups * _ipg);
        U32(sb, 4, _blockCount);
        U32(sb, 12, freeBlocks);
        U32(sb, 16, freeInodes);
        U32(sb, 20, FirstDataBlock);
        U32(sb, 24, _bs switch { 1024 => 0, 2048 => 1, 4096 => 2, _ => 6 });
        U32(sb, 28, _bs switch { 1024 => 0, 2048 => 1, 4096 => 2, _ => 6 });
        U32(sb, 32, _bpg);
        U32(sb, 36, _bpg);
        U32(sb, 40, _ipg);
        U32(sb, 48, FakeTime);
        U16(sb, 54, 0xFFFF);
        U16(sb, 56, 0xEF53);
        U16(sb, 58, 1);
        U16(sb, 60, 1);
        U32(sb, 64, FakeTime);
        U32(sb, 76, 1);
        U32(sb, 84, 11);
        U16(sb, 88, _inodeSize);
        U16(sb, 90, group);
        uint compat = 0x8 | 0x20 | (_journalBlocks > 0 ? 0x4u : 0);
        uint incompat = 0x2 | (_variant == ExtVariant.Ext4 ? 0x40u | 0x80u | 0x200u : 0) | (_inlineFeature ? 0x8000u : 0);
        uint ro = 0x1 | 0x2 | (_variant == ExtVariant.Ext4 ? 0x8u | 0x20u | 0x40u | 0x400u : 0);
        U32(sb, 92, compat);
        U32(sb, 96, incompat);
        U32(sb, 100, ro);
        _uuid.ToByteArray(bigEndian: true).CopyTo(sb, 104);
        Encoding.ASCII.GetBytes(_label).AsSpan(0, Math.Min(16, _label.Length)).CopyTo(sb.AsSpan(120));
        if (_journalBlocks > 0)
        {
            U32(sb, 224, 8);
        }

        _hashSeed.CopyTo(sb, 236);
        sb[252] = 1;
        U16(sb, 254, _descSize);
        U32(sb, 256, 0x0C);
        U32(sb, 264, FakeTime);
        if (_journalBlocks > 0)
        {
            // s_jnl_blocks: ジャーナルの inode の i_block の写しと大きさ。
            sb[253] = 1;
            byte[]? iblock = _iblock.FirstOrDefault(kv => kv.Key.Inode == 8).Value;
            iblock?.CopyTo(sb, 268);
            U32(sb, 268 + 15 * 4, 0);
            U32(sb, 268 + 16 * 4, (long)_journalBlocks * _bs);
        }

        U16(sb, 348, 32);
        U16(sb, 350, 32);
        U32(sb, 352, 1);
        if (_variant == ExtVariant.Ext4)
        {
            sb[372] = 4;
            sb[373] = 1;
        }

        if (_csum)
        {
            U32(sb, 0x3FC, Crc32c(0xFFFFFFFF, sb.AsSpan(0, 1020)));
        }

        return sb;
    }
}
