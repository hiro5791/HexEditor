using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// HFS+ のボリュームのイメージを直接書く生成ツール (TD-FOR-HFSPLUS)。ボリュームヘッダー (1024 バイト目) と末尾の予備、割り当てファイル、
/// エクステントオーバーフロー B 木 (9 個目以降の断片)、カタログ B 木 (ノード 8 KiB、葉が複数になると索引ノードを作る)、ハードリンク
/// (<c>\0\0\0\0HFS+ Private Data</c> の <c>iNode&lt;番号&gt;</c>)、リソースフォーク、decmpfs の印 (UF_COMPRESSED)、ジャーナル
/// (.journal_info_block と .journal) を書く。日時は 1904 年からの秒。アプリの解析の実装は使わない。
/// </summary>
public sealed class HfsPlusImageBuilder
{
    public const int CatalogNodeSize = 8192;
    public const int ExtentsNodeSize = 4096;
    public const string PrivateDataName = "\0\0\0\0HFS+ Private Data";
    private static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public sealed class Node
    {
        public required string Name { get; init; }
        public Node? Parent { get; init; }
        public bool IsFolder { get; init; }
        public uint Cnid { get; set; }
        public byte[] Data { get; init; } = [];
        public byte[] Resource { get; init; } = [];
        public DateTime Modified { get; init; }
        public int Fragments { get; init; } = 1;
        public bool Compressed { get; init; }
        public uint LinkTo { get; set; }
        public List<Node> Children { get; } = [];
        public List<(uint Start, uint Count)> DataExtents { get; } = [];
        public List<(uint Start, uint Count)> ResourceExtents { get; } = [];
    }

    private readonly int _bs;
    private readonly uint _blocks;
    private readonly string _label;
    private readonly DateTime _created;
    private readonly bool[] _used;
    private uint _cursor;
    private uint _nextCnid = 16;
    private SparseImage? _image;
    private uint _privateDataCnid;

    public HfsPlusImageBuilder(long size, string label, DateTime created, int blockSize = 4096)
    {
        _bs = blockSize;
        _blocks = (uint)(size / blockSize);
        _label = label;
        _created = created;
        _used = new bool[_blocks];
        Root = new Node { Name = label, IsFolder = true, Cnid = 2, Modified = created };
    }

    public Node Root { get; }

    public int BlockSize => _bs;

    public uint CatalogStart { get; private set; }

    public Node AddFile(Node dir, string name, byte[] data, DateTime modified, byte[]? resource = null, int fragments = 1, bool compressed = false)
    {
        var n = new Node { Name = name, Parent = dir, Data = data, Resource = resource ?? [], Modified = modified, Fragments = fragments, Compressed = compressed, Cnid = _nextCnid++ };
        dir.Children.Add(n);
        return n;
    }

    public Node AddFolder(Node dir, string name, DateTime modified)
    {
        var n = new Node { Name = name, Parent = dir, IsFolder = true, Modified = modified, Cnid = _nextCnid++ };
        dir.Children.Add(n);
        return n;
    }

    /// <summary>ハードリンクを作る (実体を Private Data に移し、<paramref name="names"/> のリンクのファイルを <paramref name="dir"/> に置く)。</summary>
    public Node AddHardLink(Node dir, byte[] data, DateTime modified, params string[] names)
    {
        Node priv = Root.Children.FirstOrDefault(c => c.Name == PrivateDataName) ?? AddFolder(Root, PrivateDataName, _created);
        _privateDataCnid = priv.Cnid;
        uint number = _nextCnid;
        Node target = AddFile(priv, "iNode" + number, data, modified);
        foreach (string name in names)
        {
            Node link = AddFile(dir, name, [], modified);
            link.LinkTo = target.Cnid;
        }

        return target;
    }

    private IEnumerable<Node> All(Node n)
    {
        yield return n;
        foreach (Node c in n.Children)
        {
            foreach (Node d in All(c))
            {
                yield return d;
            }
        }
    }

    private uint Alloc(uint count)
    {
        for (uint start = _cursor; start + count <= _blocks; start++)
        {
            bool free = true;
            for (uint k = 0; k < count; k++)
            {
                if (_used[start + k])
                {
                    free = false;
                    break;
                }
            }

            if (free)
            {
                for (uint k = 0; k < count; k++)
                {
                    _used[start + k] = true;
                }

                _cursor = start + count;
                return start;
            }
        }

        throw new InvalidOperationException("The HFS+ image is full.");
    }

    private List<(uint, uint)> AllocFork(long length, int fragments)
    {
        var list = new List<(uint, uint)>();
        uint blocks = (uint)((length + _bs - 1) / _bs);
        if (blocks == 0)
        {
            return list;
        }

        int pieces = Math.Max(1, Math.Min(fragments, (int)blocks));
        uint per = (uint)((blocks + pieces - 1) / pieces);
        uint done = 0;
        while (done < blocks)
        {
            uint n = Math.Min(per, blocks - done);
            list.Add((Alloc(n), n));
            done += n;
            if (pieces > 1)
            {
                _cursor += 2;
            }
        }

        return list;
    }

    private static uint Seconds(DateTime utc) => (uint)(utc - Epoch).TotalSeconds;

    private static byte[] ForkData(long size, IReadOnlyList<(uint Start, uint Count)> extents, int bs)
    {
        byte[] f = new byte[80];
        B64(f, 0, size);
        B32(f, 8, bs * 16);
        B32(f, 12, extents.Sum(e => e.Count));
        for (int i = 0; i < Math.Min(8, extents.Count); i++)
        {
            B32(f, 16 + i * 8, extents[i].Start);
            B32(f, 20 + i * 8, extents[i].Count);
        }

        return f;
    }

    // ---- B 木 ----

    private sealed record Rec(byte[] Key, byte[] Data, uint Parent, string Name, int Order);

    private static int CompareCatalog(Rec a, Rec b)
    {
        int c = a.Parent.CompareTo(b.Parent);
        if (c != 0)
        {
            return c;
        }

        // HFS+ の大文字小文字を区別しない比較の近似 (ASCII の名前では同じ順になる)。
        c = string.CompareOrdinal(a.Name.ToUpperInvariant(), b.Name.ToUpperInvariant());
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    private static byte[] CatalogKey(uint parent, string name)
    {
        byte[] chars = Encoding.BigEndianUnicode.GetBytes(name);
        byte[] k = new byte[8 + chars.Length];
        B16(k, 0, 6 + chars.Length);
        B32(k, 2, parent);
        B16(k, 6, name.Length);
        chars.CopyTo(k, 8);
        return k;
    }

    /// <summary>B 木のノードを組み立てる (葉と索引、ヘッダーノード)。戻り値はノードの配列。</summary>
    private static List<byte[]> BuildTree(List<(byte[] Key, byte[] Data)> records, int nodeSize, int maxKeyLength, uint attributes, int totalNodes,
        out int depth, out uint root, out uint firstLeaf, out uint lastLeaf)
    {
        var nodes = new List<byte[]> { new byte[nodeSize] };
        depth = 0;
        root = 0;
        firstLeaf = 0;
        lastLeaf = 0;
        var level = new List<(byte[] FirstKey, uint Node)>();
        List<(byte[] Key, byte[] Data)> current = records;
        sbyte kind = -1;
        int height = 1;
        while (current.Count > 0)
        {
            level.Clear();
            int i = 0;
            var created = new List<uint>();
            while (i < current.Count)
            {
                byte[] node = new byte[nodeSize];
                int pos = 14;
                int count = 0;
                var offsets = new List<int>();
                byte[]? first = null;
                while (i < current.Count)
                {
                    byte[] rec = [.. current[i].Key, .. current[i].Data];
                    if (rec.Length % 2 != 0)
                    {
                        rec = [.. rec, 0];
                    }

                    if (pos + rec.Length + 2 * (count + 2) > nodeSize)
                    {
                        break;
                    }

                    first ??= current[i].Key;
                    offsets.Add(pos);
                    rec.CopyTo(node, pos);
                    pos += rec.Length;
                    count++;
                    i++;
                }

                if (count == 0)
                {
                    throw new InvalidOperationException("A B-tree record does not fit in a node.");
                }

                offsets.Add(pos);
                for (int k = 0; k < offsets.Count; k++)
                {
                    B16(node, nodeSize - 2 * (k + 1), offsets[k]);
                }

                node[8] = (byte)kind;
                node[9] = (byte)height;
                B16(node, 10, count);
                uint number = (uint)nodes.Count;
                nodes.Add(node);
                created.Add(number);
                level.Add((first!, number));
            }

            // 兄弟のリンク。
            for (int k = 0; k < created.Count; k++)
            {
                B32(nodes[(int)created[k]], 0, k + 1 < created.Count ? created[k + 1] : 0);
                B32(nodes[(int)created[k]], 4, k > 0 ? created[k - 1] : 0);
            }

            if (kind == -1)
            {
                firstLeaf = created[0];
                lastLeaf = created[^1];
            }

            depth++;
            if (created.Count == 1)
            {
                root = created[0];
                break;
            }

            // 1 つ上の索引ノードのレコード: キー (可変長なら最初のキー、固定なら最大長に詰める) と子のノード番号。
            current = [.. level.Select(l =>
            {
                byte[] key = l.FirstKey;
                if ((attributes & 4) == 0)
                {
                    byte[] fixedKey = new byte[maxKeyLength + 2];
                    key.AsSpan(0, Math.Min(key.Length, fixedKey.Length)).CopyTo(fixedKey);
                    B16(fixedKey, 0, maxKeyLength);
                    key = fixedKey;
                }

                byte[] pointer = new byte[4];
                B32(pointer, 0, l.Node);
                return (key, pointer);
            })];
            kind = 0;
            height++;
        }

        if (nodes.Count > totalNodes)
        {
            throw new InvalidOperationException("The B-tree needs more nodes than reserved.");
        }

        while (nodes.Count < totalNodes)
        {
            nodes.Add(new byte[nodeSize]);
        }

        return nodes;
    }

    private static void WriteHeaderNode(byte[] node, int nodeSize, int depth, uint root, int leafRecords, uint firstLeaf, uint lastLeaf, int maxKeyLength,
        int totalNodes, int usedNodes, uint attributes, byte compareType)
    {
        node[8] = 1;
        B16(node, 10, 3);
        int h = 14;
        B16(node, h, depth);
        B32(node, h + 2, root);
        B32(node, h + 6, leafRecords);
        B32(node, h + 10, firstLeaf);
        B32(node, h + 14, lastLeaf);
        B16(node, h + 18, nodeSize);
        B16(node, h + 20, maxKeyLength);
        B32(node, h + 22, totalNodes);
        B32(node, h + 26, totalNodes - usedNodes);
        B32(node, h + 32, nodeSize * 4);
        node[h + 36] = 0;
        node[h + 37] = compareType;
        B32(node, h + 38, attributes);
        int userAt = 14 + 106, mapAt = userAt + 128, end = nodeSize - 8;
        for (int i = 0; i < usedNodes; i++)
        {
            node[mapAt + i / 8] |= (byte)(0x80 >> (i % 8));
        }

        B16(node, nodeSize - 2, 14);
        B16(node, nodeSize - 4, userAt);
        B16(node, nodeSize - 6, mapAt);
        B16(node, nodeSize - 8, end);
    }

    // ---- 書き込み ----

    public SparseImage Build()
    {
        _image = new SparseImage((long)_blocks * _bs);
        _used[0] = true;
        _used[_blocks - 1] = true;
        _cursor = 1;
        uint allocBlocks = (uint)((_blocks / 8 + _bs - 1) / _bs);
        uint allocStart = Alloc(allocBlocks);
        const int extentsNodes = 4;
        uint extentsStart = Alloc((uint)(extentsNodes * ExtentsNodeSize / _bs));
        const int catalogNodes = 16;
        CatalogStart = Alloc((uint)(catalogNodes * CatalogNodeSize / _bs));

        // ジャーナル (.journal_info_block と .journal)。
        Node jib = AddFile(Root, ".journal_info_block", new byte[_bs], _created);
        Node journal = AddFile(Root, ".journal", new byte[256 * _bs], _created);

        List<Node> nodes = [.. All(Root)];
        foreach (Node n in nodes.Where(n => !n.IsFolder))
        {
            n.DataExtents.AddRange(AllocFork(n.Data.Length, n.Fragments));
            n.ResourceExtents.AddRange(AllocFork(n.Resource.Length, 1));
        }

        // ジャーナルの中身: 情報ブロックとジャーナルのヘッダー (空)。
        long journalOffset = (long)journal.DataExtents[0].Start * _bs;
        byte[] info = new byte[_bs];
        B32(info, 0, 1);
        B64(info, 36, journalOffset);
        B64(info, 44, journal.Data.Length);
        info.CopyTo(jib.Data, 0);
        byte[] jh = new byte[512];
        B32(jh, 0, 0x4A4E4C78);
        B32(jh, 4, 0x12345678);
        B64(jh, 8, 512);
        B64(jh, 16, 512);
        B64(jh, 24, journal.Data.Length);
        B32(jh, 32, 4096);
        B32(jh, 40, 512);
        jh.CopyTo(journal.Data, 0);

        foreach (Node n in nodes.Where(n => !n.IsFolder))
        {
            WriteFork(n.Data, n.DataExtents);
            WriteFork(n.Resource, n.ResourceExtents);
        }

        // エクステントオーバーフロー: 9 個目以降の断片。
        var overflow = new List<(byte[] Key, byte[] Data)>();
        foreach (Node n in nodes.Where(n => !n.IsFolder))
        {
            foreach ((List<(uint Start, uint Count)> list, byte forkType) in new[] { (n.DataExtents, (byte)0), (n.ResourceExtents, (byte)0xFF) })
            {
                for (int start = 8; start < list.Count; start += 8)
                {
                    byte[] key = new byte[12];
                    B16(key, 0, 10);
                    key[2] = forkType;
                    B32(key, 4, n.Cnid);
                    B32(key, 8, list.Take(start).Sum(e => e.Count));
                    byte[] data = new byte[64];
                    for (int k = 0; k < 8 && start + k < list.Count; k++)
                    {
                        B32(data, k * 8, list[start + k].Start);
                        B32(data, k * 8 + 4, list[start + k].Count);
                    }

                    overflow.Add((key, data));
                }
            }
        }

        List<byte[]> extentsTree = BuildTree(overflow, ExtentsNodeSize, 10, 2, extentsNodes, out int eDepth, out uint eRoot, out uint eFirst, out uint eLast);
        if (overflow.Count == 0)
        {
            (eDepth, eRoot, eFirst, eLast) = (0, 0, 0, 0);
            extentsTree = [.. Enumerable.Range(0, extentsNodes).Select(_ => new byte[ExtentsNodeSize])];
        }

        WriteHeaderNode(extentsTree[0], ExtentsNodeSize, eDepth, eRoot, overflow.Count, eFirst, eLast, 10, extentsNodes,
            overflow.Count == 0 ? 1 : extentsTree.Count(n => n.AsSpan().IndexOfAnyExcept((byte)0) >= 0) + 1, 2, 0);
        _image.Write((long)extentsStart * _bs, [.. extentsTree.SelectMany(n => n)]);

        // カタログ。
        var records = new List<Rec>();
        int order = 0;
        foreach (Node n in nodes)
        {
            uint parent = n.Parent?.Cnid ?? 1;
            records.Add(new Rec(CatalogKey(parent, n.Name), n.IsFolder ? FolderRecord(n) : FileRecord(n), parent, n.Name, order++));
            byte[] name = Encoding.BigEndianUnicode.GetBytes(n.Name);
            byte[] thread = new byte[10 + name.Length];
            B16(thread, 0, n.IsFolder ? 3 : 4);
            B32(thread, 4, parent);
            B16(thread, 8, n.Name.Length);
            name.CopyTo(thread, 10);
            records.Add(new Rec(CatalogKey(n.Cnid, string.Empty), thread, n.Cnid, string.Empty, order++));
        }

        records.Sort(CompareCatalog);
        List<byte[]> catalog = BuildTree([.. records.Select(r => (r.Key, r.Data))], CatalogNodeSize, 516, 6, catalogNodes, out int cDepth, out uint cRoot,
            out uint cFirst, out uint cLast);
        int usedCatalog = catalog.Count(n => n.AsSpan().IndexOfAnyExcept((byte)0) >= 0) + 1;
        WriteHeaderNode(catalog[0], CatalogNodeSize, cDepth, cRoot, records.Count, cFirst, cLast, 516, catalogNodes, usedCatalog, 6, 0);
        CatalogDepth = cDepth;
        _image.Write((long)CatalogStart * _bs, [.. catalog.SelectMany(n => n)]);

        // 割り当てファイル (上位ビットから)。
        byte[] bitmap = new byte[allocBlocks * _bs];
        for (uint b = 0; b < _blocks; b++)
        {
            if (_used[b])
            {
                bitmap[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
            }
        }

        _image.Write((long)allocStart * _bs, bitmap);
        uint free = (uint)_used.Count(u => !u);

        byte[] vh = new byte[512];
        B16(vh, 0, 0x482B);
        B16(vh, 2, 4);
        B32(vh, 4, 0x100 | 0x2000);
        B32(vh, 8, 0x4846534A);
        B32(vh, 12, jib.DataExtents[0].Start);
        B32(vh, 16, Seconds(_created));
        B32(vh, 20, Seconds(_created));
        B32(vh, 28, Seconds(_created));
        B32(vh, 32, nodes.Count(n => !n.IsFolder));
        B32(vh, 36, nodes.Count(n => n.IsFolder) - 1);
        B32(vh, 40, _bs);
        B32(vh, 44, _blocks);
        B32(vh, 48, free);
        B32(vh, 52, _cursor);
        B32(vh, 56, 65536);
        B32(vh, 60, 65536);
        B32(vh, 64, _nextCnid);
        B32(vh, 68, 1);
        B64(vh, 72, 1);
        B32(vh, 80 + 24, 0x48455844);
        B32(vh, 80 + 28, 0x54455354);
        ForkData((long)allocBlocks * _bs, [(allocStart, allocBlocks)], _bs).CopyTo(vh, 112);
        ForkData((long)extentsNodes * ExtentsNodeSize, [(extentsStart, (uint)(extentsNodes * ExtentsNodeSize / _bs))], _bs).CopyTo(vh, 192);
        ForkData((long)catalogNodes * CatalogNodeSize, [(CatalogStart, (uint)(catalogNodes * CatalogNodeSize / _bs))], _bs).CopyTo(vh, 272);
        _image.Write(1024, vh);
        _image.Write((long)_blocks * _bs - 1024, vh);
        return _image;
    }

    public int CatalogDepth { get; private set; }

    private void WriteFork(byte[] content, List<(uint Start, uint Count)> extents)
    {
        long done = 0;
        foreach ((uint start, uint count) in extents)
        {
            int n = (int)Math.Min((long)count * _bs, content.Length - done);
            if (n > 0)
            {
                _image!.Write((long)start * _bs, content.AsSpan((int)done, n));
            }

            done += (long)count * _bs;
        }
    }

    private byte[] FolderRecord(Node n)
    {
        byte[] r = new byte[88];
        B16(r, 0, 1);
        B32(r, 4, n.Children.Count(c => c.Name != PrivateDataName));
        B32(r, 8, n.Cnid);
        uint t = Seconds(n.Modified);
        B32(r, 12, t);
        B32(r, 16, t);
        B32(r, 20, t);
        B32(r, 24, t);
        B16(r, 42, 0x41ED);
        return r;
    }

    private byte[] FileRecord(Node n)
    {
        byte[] r = new byte[248];
        B16(r, 0, 2);
        B16(r, 2, 0x2);
        B32(r, 8, n.Cnid);
        uint t = Seconds(n.Modified);
        B32(r, 12, t);
        B32(r, 16, t);
        B32(r, 20, t);
        B32(r, 24, t);
        r[41] = (byte)(n.Compressed ? 0x20 : 0);
        B16(r, 42, 0x81A4);
        if (n.LinkTo != 0)
        {
            B32(r, 44, n.LinkTo);
            B32(r, 48, 0x686C6E6B);
            B32(r, 52, 0x6866732B);
        }
        else if (n.Parent?.Cnid == _privateDataCnid && _privateDataCnid != 0)
        {
            B32(r, 44, 2);
        }

        ForkData(n.Data.Length, n.DataExtents, _bs).CopyTo(r, 88);
        ForkData(n.Resource.Length, n.ResourceExtents, _bs).CopyTo(r, 168);
        return r;
    }
}
