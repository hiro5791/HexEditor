using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// UDF 2.50 のイメージを直接書く生成ツール (TD-FOR-UDF250)。ボリューム認識シーケンス (BEA01・NSR03・TEA01)、アンカー (セクタ 256 と末尾)、
/// 主と予備のボリューム記述子シーケンス (PVD・IUVD・PD・LVD・USD・TD)、論理ボリュームの完全性記述子、タイプ 1 のパーティションと
/// メタデータパーティション (メタデータファイルと予備)、空き領域のビットマップ、ファイルセット記述子、拡張ファイルエントリ (EFE)、
/// ファイル識別記述子を書く。タグのチェックサムと CRC-CCITT は正しい値にする。ディレクトリの中身は short_ad (メタデータパーティション)、
/// ファイルのデータは long_ad (物理パーティション) で指す。<c>embedded</c> のファイルはデータを EFE の中に置く。アプリの解析の実装は使わない。
/// </summary>
public sealed class UdfImageBuilder
{
    public const int Block = 2048;
    public const ushort Revision = 0x0250;

    public sealed class Node
    {
        public required string Name { get; init; }
        public Node? Parent { get; init; }
        public bool IsDirectory { get; init; }
        public byte[] Content { get; init; } = [];
        public DateTime Modified { get; init; }
        public bool Embedded { get; init; }
        public List<Node> Children { get; } = [];

        /// <summary>EFE のメタデータパーティション内のブロック番号。</summary>
        public long Icb { get; set; }

        /// <summary>データの位置 (ファイルは物理パーティション内、ディレクトリはメタデータパーティション内のブロック番号)。</summary>
        public long Data { get; set; }

        public long DataBlocks { get; set; }
    }

    private readonly long _sectors;
    private readonly string _label;
    private readonly DateTime _created;
    private const long PartitionStart = 512;
    private const long MetadataFileBlock = 2, MirrorFileBlock = 3, MetadataAreaStart = 10;
    private const int MetadataBlocks = 64;
    private long _nextData = 200;
    private long _nextMeta;
    private SparseImage? _image;

    public UdfImageBuilder(long size, string label, DateTime created)
    {
        _sectors = size / Block;
        _label = label;
        _created = created;
        Root = new Node { Name = string.Empty, IsDirectory = true, Modified = created };
    }

    public Node Root { get; }

    public long PartitionLength => _sectors - PartitionStart - 256;

    /// <summary>メタデータパーティションのブロックのボリューム上のセクタ。</summary>
    public long MetadataSector(long block) => PartitionStart + MetadataAreaStart + block;

    public long PartitionSector(long block) => PartitionStart + block;

    /// <summary>時差 (分。JST)。</summary>
    public short ZoneMinutes { get; init; } = 540;

    public Node AddFile(Node dir, string name, byte[] content, DateTime modified, bool embedded = false)
    {
        var n = new Node { Name = name, Parent = dir, Content = content, Modified = modified, Embedded = embedded };
        dir.Children.Add(n);
        return n;
    }

    public Node AddDirectory(Node dir, string name, DateTime modified)
    {
        var n = new Node { Name = name, Parent = dir, IsDirectory = true, Modified = modified };
        dir.Children.Add(n);
        return n;
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

    // ---- 記述子の共通部分 ----

    /// <summary>タグを書き、チェックサムと CRC を計算する。</summary>
    private static void Tag(byte[] d, int id, long location, int crcLength)
    {
        U16(d, 0, id);
        U16(d, 2, 3);
        U16(d, 6, 1);
        U16(d, 10, crcLength);
        U32(d, 12, location);
        U16(d, 8, CrcCcitt(d.AsSpan(16, crcLength)));
        int sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum += d[i];
            }
        }

        d[4] = (byte)sum;
    }

    private static void RegId(byte[] d, int at, string id, ushort? suffix = null)
    {
        Ascii(d, at + 1, id);
        if (suffix is ushort s)
        {
            U16(d, at + 24, s);
        }
    }

    private static void CharSpec(byte[] d, int at)
    {
        d[at] = 0;
        Ascii(d, at + 1, "OSTA Compressed Unicode");
    }

    private static byte[] Cs0(string s)
    {
        bool wide = s.Any(c => c > 0xFF);
        return wide ? [16, .. Encoding.BigEndianUnicode.GetBytes(s)] : [8, .. Encoding.Latin1.GetBytes(s)];
    }

    private static void DString(byte[] d, int at, int length, string s)
    {
        byte[] b = Cs0(s);
        b.CopyTo(d, at);
        d[at + length - 1] = (byte)b.Length;
    }

    private void Timestamp(byte[] d, int at, DateTime utc)
    {
        DateTime local = utc.AddMinutes(ZoneMinutes);
        U16(d, at, (1 << 12) | (ZoneMinutes & 0xFFF));
        U16(d, at + 2, local.Year);
        d[at + 4] = (byte)local.Month;
        d[at + 5] = (byte)local.Day;
        d[at + 6] = (byte)local.Hour;
        d[at + 7] = (byte)local.Minute;
        d[at + 8] = (byte)local.Second;
        long micro = local.Ticks % TimeSpan.TicksPerSecond / 10;
        d[at + 9] = (byte)(micro / 10000);
        d[at + 10] = (byte)(micro / 100 % 100);
        d[at + 11] = (byte)(micro % 100);
    }

    private void Write(long sector, byte[] data) => _image!.Write(sector * Block, data);

    // ---- 書き込み ----

    public SparseImage Build()
    {
        _image = new SparseImage(_sectors * Block);

        // ボリューム認識シーケンス。
        string[] vrs = ["BEA01", "NSR03", "TEA01"];
        for (int i = 0; i < 3; i++)
        {
            byte[] v = new byte[Block];
            Ascii(v, 1, vrs[i]);
            v[6] = 1;
            Write(16 + i, v);
        }

        // メタデータパーティションの配置: 0 = FSD、1 = 終端、2〜 = EFE とディレクトリの中身。
        _nextMeta = 2;
        var nodes = All(Root).ToList();
        foreach (Node n in nodes)
        {
            n.Icb = _nextMeta++;
            if (n.IsDirectory)
            {
                long size = FidBytes(n);
                n.DataBlocks = (size + Block - 1) / Block;
                n.Data = _nextMeta;
                _nextMeta += n.DataBlocks;
            }
            else if (!n.Embedded)
            {
                n.DataBlocks = (n.Content.Length + Block - 1) / Block;
                n.Data = n.DataBlocks == 0 ? 0 : _nextData;
                _nextData += n.DataBlocks;
            }
        }

        if (_nextMeta > MetadataBlocks)
        {
            throw new InvalidOperationException("Too many entries for the test metadata partition.");
        }

        byte[] metadata = new byte[MetadataBlocks * Block];
        Fsd().CopyTo(metadata, 0);
        byte[] td = new byte[Block];
        Tag(td, 8, 1, 496);
        td.CopyTo(metadata, Block);
        foreach (Node n in nodes)
        {
            Efe(n).CopyTo(metadata, (int)(n.Icb * Block));
            if (n.IsDirectory)
            {
                Fids(n).CopyTo(metadata, (int)(n.Data * Block));
            }
            else if (!n.Embedded && n.Content.Length > 0)
            {
                Write(PartitionSector(n.Data), n.Content);
            }
        }

        // メタデータの領域 (と予備の写し)、メタデータファイルと予備の EFE (物理パーティション内)。
        Write(PartitionSector(MetadataAreaStart), metadata);
        Write(PartitionSector(MetadataAreaStart + MetadataBlocks), metadata);
        Write(PartitionSector(MetadataFileBlock), MetadataFileEntry(250, MetadataFileBlock, MetadataAreaStart));
        Write(PartitionSector(MirrorFileBlock), MetadataFileEntry(251, MirrorFileBlock, MetadataAreaStart + MetadataBlocks));
        Write(PartitionSector(0), SpaceBitmap());

        // ボリューム記述子シーケンス (主と予備)、完全性記述子、アンカー。
        foreach (long start in new[] { 32L, 48L })
        {
            WriteVds(start);
        }

        Write(64, Lvid());
        byte[] lvidTerm = new byte[Block];
        Tag(lvidTerm, 8, 65, 496);
        Write(65, lvidTerm);
        foreach (long at in new[] { 256L, _sectors - 1 })
        {
            byte[] a = new byte[Block];
            U32(a, 16, 16 * Block);
            U32(a, 20, 32);
            U32(a, 24, 16 * Block);
            U32(a, 28, 48);
            Tag(a, 2, at, 496);
            Write(at, a);
        }

        return _image;
    }

    private void WriteVds(long start)
    {
        // PVD
        byte[] pvd = new byte[Block];
        U32(pvd, 16, 1);
        DString(pvd, 24, 32, _label);
        U16(pvd, 56, 1);
        U16(pvd, 58, 1);
        U16(pvd, 60, 2);
        U16(pvd, 62, 3);
        U32(pvd, 64, 1);
        U32(pvd, 68, 1);
        DString(pvd, 72, 128, "0123456789ABCDEF" + _label);
        CharSpec(pvd, 200);
        CharSpec(pvd, 264);
        Timestamp(pvd, 376, _created);
        RegId(pvd, 388, "*HexEditor TestData");
        Tag(pvd, 1, start, 496);
        Write(start, pvd);

        // IUVD
        byte[] iuvd = new byte[Block];
        U32(iuvd, 16, 2);
        RegId(iuvd, 20, "*UDF LV Info", Revision);
        CharSpec(iuvd, 52);
        DString(iuvd, 116, 128, _label);
        Tag(iuvd, 4, start + 1, 496);
        Write(start + 1, iuvd);

        // PD: パーティションヘッダー記述子に空き領域のビットマップ。
        byte[] pd = new byte[Block];
        U32(pd, 16, 3);
        U16(pd, 20, 1);
        U16(pd, 22, 0);
        RegId(pd, 24, "+NSR03");
        U32(pd, 56 + 8, 24 + (PartitionLength + 7) / 8);
        U32(pd, 56 + 12, 0);
        U32(pd, 184, 1);
        U32(pd, 188, PartitionStart);
        U32(pd, 192, PartitionLength);
        RegId(pd, 196, "*HexEditor TestData");
        Tag(pd, 5, start + 2, 496);
        Write(start + 2, pd);

        // LVD: タイプ 1 (参照 0) とメタデータパーティション (参照 1)。FSD はメタデータパーティションの 0。
        byte[] lvd = new byte[Block];
        U32(lvd, 16, 4);
        CharSpec(lvd, 20);
        DString(lvd, 84, 128, _label);
        U32(lvd, 212, Block);
        RegId(lvd, 216, "*OSTA UDF Compliant", Revision);
        U32(lvd, 248, Block);
        U32(lvd, 252, 0);
        U16(lvd, 256, 1);
        U32(lvd, 264, 6 + 64);
        U32(lvd, 268, 2);
        RegId(lvd, 272, "*HexEditor TestData");
        U32(lvd, 432, 2 * Block);
        U32(lvd, 436, 64);
        int p = 440;
        lvd[p] = 1;
        lvd[p + 1] = 6;
        U16(lvd, p + 2, 1);
        U16(lvd, p + 4, 0);
        p += 6;
        lvd[p] = 2;
        lvd[p + 1] = 64;
        RegId(lvd, p + 4, "*UDF Metadata Partition", Revision);
        U16(lvd, p + 36, 1);
        U16(lvd, p + 38, 0);
        U32(lvd, p + 40, MetadataFileBlock);
        U32(lvd, p + 44, MirrorFileBlock);
        U32(lvd, p + 48, 0xFFFFFFFF);
        U32(lvd, p + 52, 32);
        U16(lvd, p + 56, 32);
        Tag(lvd, 6, start + 3, 440 + 70 - 16);
        Write(start + 3, lvd);

        // USD、TD。
        byte[] usd = new byte[Block];
        U32(usd, 16, 5);
        Tag(usd, 7, start + 4, 8);
        Write(start + 4, usd);
        byte[] term = new byte[Block];
        Tag(term, 8, start + 5, 496);
        Write(start + 5, term);
    }

    private byte[] Lvid()
    {
        byte[] d = new byte[Block];
        Timestamp(d, 16, _created);
        U32(d, 28, 1);
        U64(d, 40, 16 + All(Root).Count());
        U32(d, 72, 2);
        U32(d, 76, 46);
        U32(d, 80, 0);
        U32(d, 84, 0xFFFFFFFF);
        U32(d, 88, PartitionLength);
        U32(d, 92, 0xFFFFFFFF);
        RegId(d, 96, "*HexEditor TestData");
        U32(d, 128, All(Root).Count(n => !n.IsDirectory));
        U32(d, 132, All(Root).Count(n => n.IsDirectory));
        U16(d, 136, Revision);
        U16(d, 138, Revision);
        U16(d, 140, Revision);
        Tag(d, 9, 64, 80 + 16 + 46 - 16);
        return d;
    }

    private byte[] Fsd()
    {
        byte[] d = new byte[Block];
        Timestamp(d, 16, _created);
        U16(d, 28, 3);
        U16(d, 30, 3);
        U32(d, 32, 1);
        U32(d, 36, 1);
        CharSpec(d, 48);
        DString(d, 112, 128, _label);
        CharSpec(d, 240);
        DString(d, 304, 32, _label);
        U32(d, 400, Block);
        U32(d, 404, Root.Icb);
        U16(d, 408, 1);
        RegId(d, 416, "*OSTA UDF Compliant", Revision);
        Tag(d, 256, 0, 496);
        return d;
    }

    private static int FidSize(byte[] name) => (38 + name.Length + 3) & ~3;

    private long FidBytes(Node dir) => FidSize([]) + dir.Children.Sum(c => (long)FidSize(Cs0(c.Name)));

    private byte[] Fids(Node dir)
    {
        byte[] data = new byte[dir.DataBlocks * Block];
        int pos = 0;
        void Fid(Node target, byte characteristics, byte[] name)
        {
            int size = FidSize(name);
            byte[] f = new byte[size];
            U16(f, 16, 1);
            f[18] = characteristics;
            f[19] = (byte)name.Length;
            U32(f, 20, Block);
            U32(f, 24, target.Icb);
            U16(f, 28, 1);
            U16(f, 36, 0);
            name.CopyTo(f, 38);
            Tag(f, 257, dir.Data + pos / Block, size - 16);
            f.CopyTo(data, pos);
            pos += size;
        }

        Fid(dir.Parent ?? dir, 0x0A, []);
        foreach (Node c in dir.Children)
        {
            Fid(c, (byte)(c.IsDirectory ? 0x02 : 0x00), Cs0(c.Name));
        }

        return data;
    }

    private byte[] Efe(Node n)
    {
        byte[] d = new byte[Block];
        int adType = n.IsDirectory ? 0 : n.Embedded ? 3 : 1;
        IcbTag(d, n.IsDirectory ? 4 : 5, adType);
        U32(d, 36, 0xFFFFFFFF);
        U32(d, 40, 0xFFFFFFFF);
        U32(d, 44, n.IsDirectory ? 0x1CA5 : 0x1884);
        U16(d, 48, n.IsDirectory ? 1 + n.Children.Count(c => c.IsDirectory) : 1);
        long info = n.IsDirectory ? FidBytes(n) : n.Content.Length;
        U64(d, 56, info);
        U64(d, 64, info);
        U64(d, 72, n.Embedded ? 0 : n.DataBlocks);
        Timestamp(d, 80, n.Modified);
        Timestamp(d, 92, n.Modified);
        Timestamp(d, 104, n.Modified);
        Timestamp(d, 116, n.Modified);
        U32(d, 128, 1);
        RegId(d, 168, "*HexEditor TestData");
        U64(d, 200, n == Root ? 0 : 16 + n.Icb);
        U32(d, 208, 0);
        int lad;
        if (n.IsDirectory)
        {
            U32(d, 216, info);
            U32(d, 220, n.Data);
            lad = 8;
        }
        else if (n.Embedded)
        {
            n.Content.CopyTo(d, 216);
            lad = n.Content.Length;
        }
        else if (n.Content.Length > 0)
        {
            U32(d, 216, n.Content.Length);
            U32(d, 220, n.Data);
            U16(d, 224, 0);
            lad = 16;
        }
        else
        {
            lad = 0;
        }

        U32(d, 212, lad);
        Tag(d, 266, n.Icb, 216 + lad - 16);
        return d;
    }

    private static void IcbTag(byte[] d, int fileType, int adType)
    {
        U16(d, 20, 4);
        U16(d, 24, 1);
        d[27] = (byte)fileType;
        U16(d, 34, adType);
    }

    private byte[] MetadataFileEntry(int fileType, long location, long areaStart)
    {
        byte[] d = new byte[Block];
        IcbTag(d, fileType, 0);
        U32(d, 36, 0xFFFFFFFF);
        U32(d, 40, 0xFFFFFFFF);
        U16(d, 48, 1);
        U64(d, 56, MetadataBlocks * Block);
        U64(d, 64, MetadataBlocks * Block);
        U64(d, 72, MetadataBlocks);
        Timestamp(d, 80, _created);
        Timestamp(d, 92, _created);
        Timestamp(d, 104, _created);
        Timestamp(d, 116, _created);
        U32(d, 128, 1);
        RegId(d, 168, "*HexEditor TestData");
        U32(d, 212, 8);
        U32(d, 216, MetadataBlocks * Block);
        U32(d, 220, areaStart);
        Tag(d, 266, location, 216 + 8 - 16);
        return d;
    }

    private byte[] SpaceBitmap()
    {
        long bits = PartitionLength;
        long bytes = (bits + 7) / 8;
        byte[] d = new byte[(24 + bytes + Block - 1) / Block * Block];
        U32(d, 16, bits);
        U32(d, 20, bytes);

        // 1 が空き。
        var used = new HashSet<long>();
        long bitmapBlocks = d.Length / Block;
        for (long b = 0; b < bitmapBlocks; b++)
        {
            used.Add(b);
        }

        used.Add(MetadataFileBlock);
        used.Add(MirrorFileBlock);
        for (long b = MetadataAreaStart; b < MetadataAreaStart + 2 * MetadataBlocks; b++)
        {
            used.Add(b);
        }

        foreach (Node n in All(Root).Where(n => !n.IsDirectory && !n.Embedded))
        {
            for (long b = 0; b < n.DataBlocks; b++)
            {
                used.Add(n.Data + b);
            }
        }

        for (long b = 0; b < bits; b++)
        {
            if (!used.Contains(b))
            {
                d[24 + (b >> 3)] |= (byte)(1 << (int)(b & 7));
            }
        }

        Tag(d, 264, 0, (int)Math.Min(24 + bytes - 16, 0xFFFF));
        return d;
    }

    /// <summary>空きのブロック (パーティション内の番号) か (テストの期待値)。</summary>
    public bool IsDataBlock(Node file, long block) => block >= file.Data && block < file.Data + file.DataBlocks;
}
